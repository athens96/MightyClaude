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
/// (`launchAppUpdateOnQuit`). The rules live in `AppUpdateAuto`.
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
        observeAppUpdatePowerOff()
        appUpdateLoop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(AppUpdateAuto.tick))
                guard !Task.isCancelled, let self, !self.ending else { return }
                self.appUpdateTick()
            }
        }
    }

    func stopAppUpdateLoop() {
        appUpdateLoop?.cancel()
        appUpdateLoop = nil
    }

    /// One pass of the loop: a check when one is due. The idle restart offer
    /// joins this pass.
    func appUpdateTick(now: Date = Date()) {
        checkForAppUpdateAutomatically(now: now)
    }

    /// Off stops the loop and any download; a staged update then installs only
    /// from "restart now". On starts the loop, checks when due and downloads
    /// what a check already found.
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

    /// Logging out, restarting or shutting down: quitting then leaves the
    /// staged update for the next quit (`AppUpdateAuto.installsOnQuit`).
    private func observeAppUpdatePowerOff() {
        guard appUpdatePowerOffObserver == nil else { return }
        appUpdatePowerOffObserver = NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.willPowerOffNotification)
            .sink { [weak self] _ in self?.appUpdatePoweringOff = true }
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
                try await install()
                await MainActor.run { NSApp.terminate(nil) }
            } catch {
                await MainActor.run { store.owner?.appUpdate.phase = .failed(L("settings.appUpdate.errorInstall", ["error": error.localizedDescription])) }
            }
        }
    }

    /// Called first thing in `shutdown()`: a staged update is installed once
    /// the app has quit, without opening it again. Skipped with the switch off,
    /// while an install is already under way, at logout or shutdown, and in
    /// smoke tests. The returned task is awaited at the end of `shutdown()` so
    /// the helper has started before the process ends.
    func launchAppUpdateOnQuit() -> Task<Void, Never>? {
        guard AppUpdateAuto.installsOnQuit(ready: appUpdate.phase == .ready, automatic: appUpdateAutomatic,
                                           hasKey: appUpdateCanRunAutomatically, poweringOff: appUpdatePoweringOff),
              let install = prepareAppUpdateInstall(relaunch: false) else { return nil }
        return Task {
            do { try await install() }
            catch { NSLog("MightyClaude update on quit not started: %@", error.localizedDescription) }
        }
    }

    /// The checks both installs share, run before anything changes: the staged
    /// bundle, its package and asset, and a writable destination. On success
    /// the phase is `.installing` and the returned step re-verifies the package
    /// on disk and starts the helper.
    private func prepareAppUpdateInstall(relaunch: Bool) -> (() async throws -> Void)? {
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
        return {
            // Rule 4: re-verify the package on disk immediately before replacement.
            if let size = asset.size {
                let actual = (try? FileManager.default.attributesOfItem(atPath: package.path)[.size] as? Int) ?? -1
                if actual != size { throw MightyError(L("settings.appUpdate.errorSizeMismatch")) }
            }
            if let sha256 = asset.sha256, try AppUpdateService.fileDigest(package) != sha256 {
                throw MightyError(L("settings.appUpdate.errorDigestMismatch"))
            }
            try await service.launchInstaller(script: script, near: package)
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
