import AppKit
import Foundation
import MightyCore

/// App self-update driven by a signed JSON manifest on Cloudflare. The
/// manifest address and the Ed25519 public key come from the build
/// (`MightyUpdateManifestURL`, `MightyUpdatePublicKey` in Info.plist); the
/// address can be overridden in Settings. The package is verified, unpacked,
/// and swapped in by a helper only after the app has quit.
///
/// With the "automatic" switch on (the default) the app checks at launch and
/// every `AppUpdateAuto.tick` whether a check is due, downloads and stages a
/// newer version without asking, and installs it when the app quits
/// (`launchAppUpdateOnQuit`) or, once the app is idle, after a 60-second
/// countdown banner (`offerAppUpdateRestart`). The rules live in `AppUpdateAuto`.
extension AppStore {
    enum AppUpdatePhase: Equatable {
        case idle, checking, upToDate, available, downloading(Double), staging, ready, installing
        case failed(String)
    }
    struct AppUpdateState: Equatable {
        var phase: AppUpdatePhase = .idle
        var availability: AppUpdateAvailability?
        var package: URL?
        var stagedApp: URL?
        var checkedAt: Date?
        /// The last failure, kept beside `.ready` so a failed background check
        /// never takes a staged update away; cleared by the next success.
        var lastError: String?
    }
    static let appUpdateURLDefaultsKey = "appUpdate.manifestURL"
    static let appUpdateLastCheckKey = "appUpdate.lastCheck"
    static let appUpdateAutomaticKey = "appUpdate.automatic"
    static let appBundleIdentifier = "dev.mightyclaude.native"

    var appVersion: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.2.0" }
    /// The address baked into the build, for the placeholder and the reset.
    var builtInUpdateManifestURL: String? {
        (Bundle.main.infoDictionary?["MightyUpdateManifestURL"] as? String).flatMap { $0.isEmpty ? nil : $0 }
    }
    /// Raw 32-byte Ed25519 public key baked into the build, base64 in Info.plist.
    var appUpdatePublicKey: Data? {
        (Bundle.main.infoDictionary?["MightyUpdatePublicKey"] as? String).flatMap { Data(base64Encoded: $0) }.flatMap { $0.count == 32 ? $0 : nil }
    }
    var appUpdateManifestURLOverride: String {
        get { UserDefaults.standard.string(forKey: Self.appUpdateURLDefaultsKey) ?? "" }
        set { objectWillChange.send(); UserDefaults.standard.set(newValue, forKey: Self.appUpdateURLDefaultsKey) }
    }
    var appUpdateManifestURL: URL? {
        // Rule 3: the stamped address wins; user override is not consulted when the build carries a URL.
        if let builtIn = builtInUpdateManifestURL, !builtIn.isEmpty {
            return URL(string: builtIn).flatMap { $0.scheme?.lowercased() == "https" ? $0 : nil }
        }
        let override = appUpdateManifestURLOverride.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !override.isEmpty else { return nil }
        return URL(string: override).flatMap { $0.scheme?.lowercased() == "https" ? $0 : nil }
    }
    var appUpdateAutomatic: Bool {
        get { UserDefaults.standard.object(forKey: Self.appUpdateAutomaticKey) as? Bool ?? true }
        set {
            objectWillChange.send(); UserDefaults.standard.set(newValue, forKey: Self.appUpdateAutomaticKey)
            appUpdateAutomaticChanged(newValue)
        }
    }
    /// The build can check and install on its own: it verifies manifests, knows
    /// where they are, and is not a smoke test or the help capture.
    var appUpdateCanRunAutomatically: Bool {
        !smokeTesting && appUpdatePublicKey != nil && appUpdateManifestURL != nil
    }

    private var appUpdateDirectory: URL { dataDirectory.appendingPathComponent("updates", isDirectory: true) }

    /// At launch, before any check: a staged copy of the running version (its
    /// helper was cut off after the swap) or an older one is deleted, never offered.
    func discardStaleAppUpdates() {
        guard !smokeTesting, appUpdate.phase == .idle else { return }
        let folder = appUpdateDirectory
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        for name in AppUpdateAuto.staleStagedFolders(names, runningVersion: appVersion) {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent(name, isDirectory: true))
        }
    }

    private var appUpdateService: AppUpdateService {
        if let service = appUpdateServiceStorage { return service }
        let service = AppUpdateService(directory: appUpdateDirectory, publicKey: appUpdatePublicKey)
        appUpdateServiceStorage = service
        return service
    }

    /// Asks `AppUpdateAuto.checkDue` and checks when it says so; the attempt is
    /// remembered so a failed check waits for the next cycle.
    func checkForAppUpdateAutomatically(now: Date = Date()) {
        guard !ending, !appUpdateBusy, !appUpdateCheckInFlight else { return }
        let last = UserDefaults.standard.object(forKey: Self.appUpdateLastCheckKey) as? Date
        guard AppUpdateAuto.checkDue(enabled: appUpdateAutomatic, hasKey: appUpdateCanRunAutomatically, lastCheck: last, lastAttempt: appUpdateLastAttempt, now: now) else { return }
        appUpdateLastAttempt = now
        checkForAppUpdate()
    }

    /// Looks every `AppUpdateAuto.tick` whether a check is due. Started at
    /// launch and when the switch goes on; cancelled when it goes off and at quit.
    func startAppUpdateLoop() {
        guard appUpdateLoop == nil, appUpdateAutomatic, appUpdateCanRunAutomatically else { return }
        appUpdateLoop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(AppUpdateAuto.tick))
                guard !Task.isCancelled, let self, !self.ending else { return }
                await self.appUpdateTick()
            }
        }
    }

    /// Stops the loop and a running countdown (the banner goes away; a "later" stays).
    func stopAppUpdateLoop() {
        appUpdateLoop?.cancel()
        appUpdateLoop = nil
        cancelAppUpdateCountdown()
    }

    /// One pass of the loop: a check when one is due, then the idle restart offer.
    func appUpdateTick(now: Date = Date()) async {
        checkForAppUpdateAutomatically(now: now)
        await offerAppUpdateRestart()
    }

    // MARK: Idle restart

    /// What the app is doing right now, for `AppUpdateAuto.idleBlockers`: one
    /// source per row of the plan's idle table.
    func appIdleFacts(now: Date) async -> AppIdleFacts {
        let counts = await appIdleActorCounts()
        return appIdleFacts(now: now, phoneUploads: counts.phoneUploads, delegationPending: counts.delegationPending)
    }

    /// The two idle facts that live on actors: files the phone holds here, and
    /// delegation work under way.
    private func appIdleActorCounts() async -> (phoneUploads: Int, delegationPending: Int) {
        (await mobileRemote.openUploadCount(), await delegation?.pendingWork() ?? 0)
    }

    /// Everything else, read on the main actor without a suspension, so a
    /// caller can act on the answer in the same turn.
    private func appIdleFacts(now: Date, phoneUploads: Int, delegationPending: Int) -> AppIdleFacts {
        let sessions = snapshot.sessions
        let agentRuns = sessions.filter { $0.kind != "shell" && $0.status == "running" }.count + pendingRuns.count + heldForUpdate.count
            + (delegation?.endingRuns.count ?? 0) + delegationPending
        let pendingCards = toolPermissions.values.reduce(0) { $0 + $1.count } + antigravityBoard.cards.count + webOpenRequests.count
        let drafts = self.drafts.values.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.count
            + attachmentDrafts.values.filter { !$0.isEmpty }.count + importingAttachments.count
        // A shell at its prompt still holding a background or stopped job counts as running.
        let terminals = sessions.filter { $0.kind == "shell" && $0.status == "running" }.count
            + localTerminals.values.filter { $0.shellProcessId.map { ShellProcessProbe.hasChildren(pid: $0) } ?? false }.count
            + agentTerminals.values.reduce(0) { $0 + $1.pane.runningHandles().count }
            + (setupRun?.finished == false ? 1 : 0) + setupTerminals.count + cliLoginPending.count + backgroundLoginJobs.count
        let cliWork = isUpdatingCLIs || automaticUpdateRunning || isManagingPlugins || updatingPluginsFor != nil
            || claudeModelResetInProgress || toolkitRunning || componentAction != nil
        let modal = hasModal || setupConfirmation != nil || toolkitShowConfirmation
            || NSApp.modalWindow != nil || NSApp.windows.contains { $0.attachedSheet != nil }
        // Any keyboard or mouse input in the login session, in this app or another.
        let inputIdle = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
        return AppIdleFacts(agentRuns: agentRuns, pendingCards: pendingCards, queuedInputs: queuedInputs.values.filter { !$0.isEmpty }.count,
                            drafts: drafts, terminals: terminals, cliWork: cliWork,
                            screenSharing: screenShareMenuBar?.isActive ?? false, phoneUploads: phoneUploads, remoteHostRuns: remoteHostState.activeRuns,
                            sharingWorkspaces: remoteHostState.enabled,
                            blockingModal: modal, lastUserInput: now.addingTimeInterval(-inputIdle), lastAgentActivity: providerLastActive.values.max())
    }

    /// One step of the idle-restart offer (`AppUpdateAuto.restartOffer`): starts
    /// the countdown banner when the app is idle, cancels it quietly when it no
    /// longer is, and restarts to install when it runs out.
    func offerAppUpdateRestart() async {
        // Nothing staged and no banner or snooze: nothing to decide.
        guard !ending, appUpdate.phase == .ready || appUpdateRestart != .idle else { return }
        let facts = await appIdleFacts(now: Date())
        guard !ending else { return }
        let now = Date()
        let decision = AppUpdateAuto.restartOffer(appUpdateRestart, ready: appUpdate.phase == .ready,
                                                  automatic: appUpdateAutomatic && appUpdateCanRunAutomatically,
                                                  blockers: AppUpdateAuto.idleBlockers(facts, now: now), now: now)
        // Unchanged state is not set again: the store would redraw every second.
        if appUpdateRestart != decision.state { appUpdateRestart = decision.state }
        switch decision.action {
        case .none: break
        case .startCountdown: startAppUpdateCountdownTicks()
        case .cancel: stopAppUpdateCountdownTicks()
        case .restart:
            stopAppUpdateCountdownTicks()
            restartForAppUpdateAutomatically()
        }
    }

    /// The countdown ran out. The package is verified first (off the main
    /// actor); then, in one main-actor turn with no suspension, the app is
    /// asked again whether it is idle (input aside, as during the countdown),
    /// and only then is the helper started and the app quit. Something that
    /// started meanwhile (a run, a card, a dialog…) puts the update back to
    /// ready and the next idle tick offers a fresh countdown.
    private func restartForAppUpdateAutomatically() {
        guard !ending, let install = prepareAppUpdateInstall(relaunch: true) else { return }
        appUpdateVerifyingRestart = true
        let store = AppUpdateProgressSink(self)
        Task {
            defer { store.owner?.appUpdateVerifyingRestart = false }
            do { try await install.verify() } catch {
                store.owner?.appUpdate.phase = .failed(L("settings.appUpdate.errorInstall", ["error": error.localizedDescription])); return
            }
            guard let owner = store.owner else { return }
            let counts = await owner.appIdleActorCounts()
            // From here to the quit: no suspension, so nothing can start in between.
            let now = Date()
            let blockers = AppUpdateAuto.idleBlockers(owner.appIdleFacts(now: now, phoneUploads: counts.phoneUploads, delegationPending: counts.delegationPending), now: now)
            guard !owner.ending, owner.appUpdate.phase == .installing, owner.appUpdateAutomatic,
                  blockers.allSatisfy({ $0 == .userActive }) else {
                if owner.appUpdate.phase == .installing { owner.appUpdate.phase = .ready }
                return
            }
            do { try install.launch() } catch {
                owner.appUpdate.phase = .failed(L("settings.appUpdate.errorInstall", ["error": error.localizedDescription])); return
            }
            NSApp.terminate(nil)
        }
    }

    /// The banner's "restart now".
    func restartForAppUpdateNow() {
        stopAppUpdateCountdownTicks()
        appUpdateRestart = .idle
        installAppUpdateAndRelaunch()
    }

    /// The banner's "later": no banner for `AppUpdateAuto.snooze`; quitting still installs.
    func postponeAppUpdateRestart() {
        stopAppUpdateCountdownTicks()
        appUpdateRestart = AppUpdateAuto.later(now: Date())
    }

    /// Takes the banner away without a word; a "later" is kept.
    private func cancelAppUpdateCountdown() {
        stopAppUpdateCountdownTicks()
        if case .countingDown = appUpdateRestart { appUpdateRestart = .idle }
    }

    /// While the banner counts down, the offer is asked again every `AppUpdateAuto.countdownTick`.
    private func startAppUpdateCountdownTicks() {
        appUpdateCountdownTicks?.cancel()
        appUpdateCountdownTicks = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(AppUpdateAuto.countdownTick))
                guard !Task.isCancelled, let self, !self.ending, case .countingDown = self.appUpdateRestart else { return }
                await self.offerAppUpdateRestart()
            }
        }
    }

    private func stopAppUpdateCountdownTicks() {
        appUpdateCountdownTicks?.cancel()
        appUpdateCountdownTicks = nil
    }

    /// Off stops the loop, any download and the countdown; a staged update then
    /// installs only from "restart now". On starts the loop, checks when due and
    /// downloads what a check already found.
    private func appUpdateAutomaticChanged(_ on: Bool) {
        guard on else {
            stopAppUpdateLoop()
            cancelAppUpdateDownload()
            return
        }
        guard appUpdateCanRunAutomatically else { return }
        startAppUpdateLoop()
        if appUpdate.phase == .available { downloadAppUpdate() } else { checkForAppUpdateAutomatically() }
    }

    private var appUpdateBusy: Bool {
        switch appUpdate.phase { case .checking, .downloading, .staging, .installing: return true; default: return false }
    }

    func checkForAppUpdate() {
        guard !ending, !appUpdateBusy, !appUpdateCheckInFlight else { return }
        guard let url = appUpdateManifestURL else { appUpdate.phase = .failed(L("settings.appUpdate.errorNoManifestURL")); return }
        // A staged update stays ready (and installable) while the check runs.
        if appUpdate.phase != .ready { appUpdate.phase = .checking }
        appUpdateCheckInFlight = true
        let version = appVersion
        let service = appUpdateService
        Task { [weak self] in
            let outcome: Result<AppUpdateAvailability, Error>
            do { outcome = .success(try await service.check(manifestURL: url, currentVersion: version)) } catch { outcome = .failure(error) }
            await MainActor.run {
                guard let self else { return }
                self.appUpdateCheckInFlight = false
                // "Restart now" was pressed meanwhile: the install owns the state.
                guard self.appUpdate.phase == .ready || self.appUpdate.phase == .checking else { return }
                let staged = self.appUpdate.phase == .ready ? self.appUpdate.availability?.manifest.version : nil
                switch outcome {
                case .success(let availability):
                    UserDefaults.standard.set(Date(), forKey: Self.appUpdateLastCheckKey)
                    self.appUpdate.checkedAt = Date()
                    self.appUpdate.lastError = nil
                    let next = AppUpdateAuto.afterCheck(availability, stagedVersion: staged, automatic: self.appUpdateAutomatic && self.appUpdateCanRunAutomatically)
                    if next == .keepStaged { return }
                    self.appUpdate.availability = availability
                    self.appUpdate.package = nil; self.appUpdate.stagedApp = nil
                    switch next {
                    case .keepStaged: break
                    case .blocked(let reason): self.appUpdate.phase = .failed(reason)
                    case .upToDate: self.appUpdate.phase = .upToDate
                    case .available(let download):
                        self.appUpdate.phase = .available
                        if download { self.downloadAppUpdate() }
                    }
                case .failure(let error):
                    self.appUpdate.lastError = error.localizedDescription
                    if staged == nil { self.appUpdate.phase = .failed(error.localizedDescription) }
                }
            }
        }
    }

    func downloadAppUpdate() {
        guard !ending, !appUpdateBusy, let availability = appUpdate.availability, availability.isNewer, let asset = availability.manifest.macos else { return }
        appUpdate.phase = .downloading(0)
        let service = appUpdateService
        let version = availability.manifest.version
        let store = AppUpdateProgressSink(self)
        Task {
            do {
                let package = try await service.download(asset, version: version) { fraction in store.report(fraction) }
                await MainActor.run { store.owner?.appUpdate.phase = .staging }
                let staged = try await service.stage(package: package, expectedBundleIdentifier: Self.appBundleIdentifier)
                await MainActor.run {
                    guard let owner = store.owner else { return }
                    owner.appUpdate.package = package; owner.appUpdate.stagedApp = staged; owner.appUpdate.phase = .ready
                    owner.appUpdate.lastError = nil
                }
            } catch is CancellationError {
                await MainActor.run { store.owner?.appUpdate.phase = .available }
            } catch let error as URLError where error.code == .cancelled {
                await MainActor.run { store.owner?.appUpdate.phase = .available }
            } catch {
                await MainActor.run {
                    store.owner?.appUpdate.phase = .failed(error.localizedDescription)
                    store.owner?.appUpdate.lastError = error.localizedDescription
                }
            }
        }
    }

    func cancelAppUpdateDownload() {
        guard case .downloading = appUpdate.phase else { return }
        let service = appUpdateService
        Task { await service.cancelDownload() }
    }

    /// Re-validates the staged bundle and the package hash, starts the helper and quits.
    /// The helper waits for this process to end, swaps the bundle at the running
    /// app's own path, and relaunches it.
    func installAppUpdateAndRelaunch() {
        guard !ending, let install = prepareAppUpdateInstall(relaunch: true) else { return }
        let store = AppUpdateProgressSink(self)
        Task {
            do {
                try await install.verify()
                try install.launch()
                NSApp.terminate(nil)
            } catch {
                store.owner?.appUpdate.phase = .failed(L("settings.appUpdate.errorInstall", ["error": error.localizedDescription]))
            }
        }
    }

    /// Called first thing in `shutdown()`: a staged update is installed once
    /// the app has quit, without opening it again. Skipped with the switch off,
    /// while an install is already under way, when the quit's reason is a
    /// logout, restart or shutdown (`appUpdatePoweringOff`), and in smoke tests. The returned task is awaited at the end of `shutdown()` so
    /// the helper has started before the process ends.
    func launchAppUpdateOnQuit() -> Task<Void, Never>? {
        guard AppUpdateAuto.installsOnQuit(ready: appUpdate.phase == .ready, automatic: appUpdateAutomatic,
                                           hasKey: appUpdateCanRunAutomatically, poweringOff: appUpdatePoweringOff),
              let install = prepareAppUpdateInstall(relaunch: false) else { return nil }
        return Task {
            do { try await install.verify(); try install.launch() }
            catch { NSLog("MightyClaude update on quit not started: %@", error.localizedDescription) }
        }
    }

    /// The checks both installs share, run before anything changes: the staged
    /// bundle, its package and asset, and a writable destination. On success
    /// the phase is `.installing`; `verify` re-checks the package on disk off
    /// the main actor and `launch` then starts the helper (at once, on the caller's actor).
    private func prepareAppUpdateInstall(relaunch: Bool) -> (verify: () async throws -> Void, launch: () throws -> Void)? {
        guard case .ready = appUpdate.phase, let staged = appUpdate.stagedApp, let package = appUpdate.package else { return nil }
        guard let asset = appUpdate.availability?.manifest.macos else {
            appUpdate.phase = .failed(L("settings.appUpdate.errorNoPackage")); return nil
        }
        let destination = Bundle.main.bundleURL
        guard destination.pathExtension == "app", FileManager.default.isWritableFile(atPath: destination.deletingLastPathComponent().path) else {
            appUpdate.phase = .failed(L("settings.appUpdate.errorNotWritable", ["path": destination.path])); return nil
        }
        do { try AppUpdateService.validate(app: staged, within: package.deletingLastPathComponent(), expectedBundleIdentifier: Self.appBundleIdentifier) }
        catch { appUpdate.phase = .failed(L("settings.appUpdate.errorPreflight", ["error": error.localizedDescription])); appUpdate.stagedApp = nil; return nil }
        appUpdate.phase = .installing
        let script = AppUpdateService.installScript(stagedApp: staged, destination: destination, pid: ProcessInfo.processInfo.processIdentifier, relaunch: relaunch)
        let service = appUpdateService
        return (verify: {
            // Rule 4: re-verify the package on disk immediately before replacement,
            // off the main actor (hashing the package takes a while).
            try await Task.detached { try Self.verifyAppUpdatePackage(package, asset: asset) }.value
        }, launch: {
            try service.launchInstaller(script: script, near: package)
        })
    }

    private nonisolated static func verifyAppUpdatePackage(_ package: URL, asset: AppUpdateAsset) throws {
        if let size = asset.size {
            let actual = (try? FileManager.default.attributesOfItem(atPath: package.path)[.size] as? Int) ?? -1
            if actual != size { throw MightyError(L("settings.appUpdate.errorSizeMismatch")) }
        }
        if let sha256 = asset.sha256, try AppUpdateService.fileDigest(package) != sha256 {
            throw MightyError(L("settings.appUpdate.errorDigestMismatch"))
        }
    }
}

/// A weak handle the download task can carry across actors without
/// capturing the store itself.
final class AppUpdateProgressSink: @unchecked Sendable {
    weak var owner: AppStore?
    init(_ owner: AppStore) { self.owner = owner }
    func report(_ fraction: Double) {
        Task { @MainActor in
            guard let owner, case .downloading = owner.appUpdate.phase else { return }
            owner.appUpdate.phase = .downloading(fraction)
        }
    }
}
