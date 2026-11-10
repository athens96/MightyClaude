import AppKit
import MightyCore

/// The setup screen's request: it opens as a sheet, scrolled to `focus` when set.
struct SetupSheetRequest: Identifiable, Equatable {
    let id = UUID()
    var focus: String?
}

/// The install the setup screen started: one script in one terminal window.
struct SetupRun: Equatable {
    var items: [String]
    var terminalId: String
    var startedMarker: URL
    var doneMarker: URL
    var finished: Bool
    /// The script has not said it started 20 s after the window opened.
    var notStarted = false
}

/// The confirmation's content: the lines that run first, then every step
/// exactly as the script runs it.
struct SetupConfirmation: Identifiable, Equatable {
    let id = UUID()
    var preamble: [String]
    var steps: [SetupScriptStep]
}

/// 필요한 도구: what the app needs on this Mac, read from the bundled catalog
/// (`SetupCatalog`), probed fresh, and installed in one visible terminal window.
/// The app writes the script from the catalog alone, never runs sudo and never
/// sees a password: whatever a step asks, the user answers in the terminal.
extension AppStore {
    static let setupNeverShowKey = "setup.neverShow"
    /// While an install runs, the screen waits this long between looks (the run's
    /// watcher probes its items); otherwise everything is probed this slowly.
    static let setupRunPollInterval: Duration = .seconds(5)
    static let setupIdlePollInterval: Duration = .seconds(30)
    static let setupWatchInterval: Duration = .seconds(2)
    static let setupStartDeadline: TimeInterval = 20

    var setupItems: [SetupItem] { setupCatalog?.items.filter { $0.platforms[.macos] != nil } ?? [] }
    var setupRunning: Bool { setupRun?.finished == false }
    var setupFolder: URL { dataDirectory.appendingPathComponent("setup", isDirectory: true) }

    /// The catalog, read once. A broken bundle is reported, not hidden.
    @discardableResult
    func loadSetupCatalog() -> SetupCatalog? {
        if let setupCatalog { return setupCatalog }
        do { setupCatalog = try SetupCatalog.bundled() }
        catch { setupMessage = L("setup.error.catalog", ["error": error.localizedDescription]) }
        return setupCatalog
    }

    /// Probes the items again (only `only`, when given). `forceEnvironment` reads
    /// the login shell anew; a forced refresh asked for while a probe runs is
    /// queued and runs right after it. When an agent CLI changed, the app's runtime
    /// is read again too, so a pane blocked for a missing CLI is not blocked any longer.
    func refreshSetup(forceEnvironment: Bool = false, only: Set<String>? = nil) async {
        guard !ending, let catalog = loadSetupCatalog() else { return }
        if setupProbing {
            if forceEnvironment { setupForceQueued = true }
            return
        }
        setupProbing = true
        let results: [String: SetupProbeResult]
        if let fixed = setupFixedResults { results = fixed }
        else {
            let environment = await providers.executionEnvironment(forceRefresh: forceEnvironment).values
            results = await SetupProber(environment: environment).probeAll(catalog, platform: .macos, only: only)
        }
        setupProbing = false
        guard !ending else { return }
        setupResults.merge(results) { _, new in new }
        setupSelection = setupSelection.filter { setupResults[$0]?.isInstalled != true }
        let agents = catalog.items.filter { $0.provider != nil }.map { setupResults[$0.id]?.version ?? "" }
        if setupFixedResults == nil, let cached = runtime?.providers, SetupRuntimeSync.isStale(runtime: cached, catalog: catalog, results: setupResults), agents != setupRuntimeSyncedAgents {
            setupRuntimeSyncedAgents = agents
            await refreshRuntimeAfterCLIUpdate()
            refreshCLIAccounts()
        }
        if setupForceQueued {
            setupForceQueued = false
            await refreshSetup(forceEnvironment: true)
        }
    }

    /// At launch: open the screen when no agent CLI can run or the command line
    /// tools are not ready, unless the user asked never to see it again. Never in
    /// a smoke run or the help capture, which have their own screens to show.
    func presentSetupIfNeeded() async {
        guard !smokeTesting, !ending else { return }
        await refreshSetup()
        guard let catalog = setupCatalog, !hasModal,
              SetupFirstRun.shouldPresent(catalog: catalog, results: setupResults, platform: .macos, neverShow: UserDefaults.standard.bool(forKey: Self.setupNeverShowKey)) else { return }
        openSetup()
    }

    /// The welcome screen's button shows while this holds.
    var setupNeedsAttention: Bool {
        guard let catalog = setupCatalog else { return false }
        return SetupFirstRun.needsAttention(catalog: catalog, results: setupResults, platform: .macos)
    }

    /// Opens the screen, with `focus` picked and scrolled to when it is missing.
    func openSetup(focus: String? = nil) {
        guard loadSetupCatalog() != nil, !hasModal else { return }
        setupResultWaiting = false
        if let focus, let item = setupCatalog?.item(focus), SetupRowState.isSelectable(setupRowState(item), item: item, platform: .macos) { setupSelection.insert(focus) }
        setupSheet = SetupSheetRequest(focus: focus)
    }

    /// The first time the list shows with results: required items that are
    /// missing, and Claude Code when no agent CLI is there. Never again after the
    /// user changed the picks.
    func preselectSetupOnce() {
        guard !setupDidPreselect, let catalog = setupCatalog, !setupResults.isEmpty else { return }
        setupDidPreselect = true
        setupSelection.formUnion(SetupFirstRun.preselection(catalog: catalog, results: setupResults, platform: .macos))
    }

    func dismissSetup(neverShowAgain: Bool) {
        if neverShowAgain { UserDefaults.standard.set(true, forKey: Self.setupNeverShowKey) }
        setupSheet = nil
    }

    /// The help capture's answer, never this Mac's: the demo's agent CLIs as its
    /// runtime has them and the other tools installed, but for two optional ones,
    /// so the picture shows both kinds of row.
    func helpCaptureSetupResults() -> [String: SetupProbeResult] {
        var results: [String: SetupProbeResult] = [:]
        for item in setupItems {
            if let provider = item.provider {
                let version = runtime?.providers?.first { $0.id == provider }?.version
                results[item.id] = version.map { .installed(version: $0) } ?? .missing
            } else {
                results[item.id] = ["tailscale", "glab"].contains(item.id) ? .missing : .installed(version: nil)
            }
        }
        return results
    }

    // MARK: Rows

    func setupRowState(_ item: SetupItem) -> SetupRowState {
        SetupRowState.derive(setupResults[item.id], inRun: setupRun?.items.contains(item.id) == true, runFinished: setupRun?.finished ?? true)
    }

    func setupIsSatisfied(_ id: String) -> Bool { setupResults[id]?.isInstalled == true }

    /// The selection plus the missing prerequisites it needs, in install order.
    var setupPlanIds: [String] {
        guard let catalog = setupCatalog else { return [] }
        return SetupPlan.order(selected: setupSelection, catalog: catalog, platform: .macos, isSatisfied: setupIsSatisfied)
    }

    /// Prerequisites the plan adds that the user did not pick.
    var setupAddedIds: Set<String> { Set(setupPlanIds).subtracting(setupSelection) }

    func toggleSetupSelection(_ id: String) {
        guard !setupRunning else { return }
        setupDidPreselect = true
        if !setupSelection.insert(id).inserted { setupSelection.remove(id) }
    }

    // MARK: Installing

    /// Shows the one confirmation: the lines that run first, then every command, in order.
    func requestSetupInstall() {
        guard let catalog = setupCatalog, !setupRunning else { return }
        let steps = SetupScript.steps(for: setupPlanIds, catalog: catalog, platform: .macos, title: { $0.title })
        guard !steps.isEmpty else { return }
        setupConfirmation = SetupConfirmation(preamble: SetupScript.preamble(home: FileManager.default.homeDirectoryForCurrentUser), steps: steps)
    }

    /// Writes the script into the app's own setup folder and runs it in a terminal
    /// window of its own. The probe, not the script's exit code, decides what got
    /// installed. The screen stays open and follows along.
    func startSetupInstall() async {
        guard let confirmation = setupConfirmation, !setupRunning, !ending else { return }
        setupConfirmation = nil
        let folder = setupFolder
        let labels = SetupScriptLabels(step: { "[\($0)/\($1)] \($2)" }, failed: L("setup.script.failed"), waiting: L("setup.script.waiting"), done: L("setup.script.done"))
        let scriptURL: URL, markers: (started: URL, done: URL)
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try SetupScriptFiles.prepareDirectory(folder)
            // Earlier runs' scripts and markers are of no further use.
            SetupScriptFiles.removeLeftovers(in: folder)
            markers = SetupScriptFiles.markers(in: folder)
            let script = SetupScript.render(steps: confirmation.steps, labels: labels, home: FileManager.default.homeDirectoryForCurrentUser,
                                            startedMarker: markers.started.path, doneMarker: markers.done.path)
            scriptURL = try SetupScriptFiles.writeScript(script, in: folder)
        } catch {
            setupMessage = L("setup.error.script", ["error": error.localizedDescription])
            return
        }
        // The app wrote this command itself from its own catalog, after the user confirmed it.
        let id = "setup-" + UUID().uuidString
        openSetupTerminal(id: id, title: L("setup.terminal.title"), input: TerminalInput(text: SetupScript.launchCommand(scriptPath: scriptURL.path), autoRun: true))
        setupMessage = nil
        setupRun = SetupRun(items: confirmation.steps.map(\.itemId), terminalId: id, startedMarker: markers.started, doneMarker: markers.done, finished: false)
        watchSetupRun(id: id)
    }

    /// A terminal window that is not a pane (`SetupTerminalWindow`), in the setup folder.
    func openSetupTerminal(id: String, title: String, input: TerminalInput) {
        try? SetupScriptFiles.prepareDirectory(setupFolder)
        let window = SetupTerminalWindow(id: id, title: title, directory: setupFolder, input: input, controller: sharedTerminalController(),
                                         onClose: { [weak self] id in self?.setupTerminalClosed(id) },
                                         onInputProblem: { [weak self] message in self?.setupMessage = message })
        setupTerminals[id] = window
        window.show()
    }

    private func setupTerminalClosed(_ id: String) {
        setupTerminals.removeValue(forKey: id)
        cliLoginEnded(sessionID: id)
    }

    /// The script makes its start marker first thing and its end marker when it
    /// ends for any reason; a closed window ends the run too. Then everything is
    /// probed again. The screen comes back by itself only while the app is in
    /// front; otherwise the status bar says the install is over.
    private func watchSetupRun(id: String) {
        setupWatchTask?.cancel()
        let opened = Date()
        setupWatchTask = Task { [weak self] in
            var probed = Date()
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.setupWatchInterval)
                guard let self, !Task.isCancelled, !self.ending, let run = self.setupRun, run.terminalId == id, !run.finished else { return }
                if SetupScriptFiles.markerExists(run.doneMarker) || self.setupTerminals[id] == nil { break }
                let started = SetupScriptFiles.markerExists(run.startedMarker)
                let stalled = !started && Date().timeIntervalSince(opened) > Self.setupStartDeadline
                if self.setupRun?.notStarted != stalled { self.setupRun?.notStarted = stalled }
                // Only the run's own items, every few seconds.
                if Date().timeIntervalSince(probed) >= 5 {
                    probed = Date()
                    await self.refreshSetup(only: Set(run.items))
                }
            }
            guard let self, !Task.isCancelled else { return }
            await self.finishSetupRun(id: id)
        }
    }

    /// Ends the run: by the watcher, or by the user when the script never started.
    func finishSetupRun(id: String) async {
        guard !ending, setupRun?.terminalId == id, setupRun?.finished == false else { return }
        setupRun?.finished = true
        setupRun?.notStarted = false
        await refreshSetup(forceEnvironment: true)
        guard setupSheet == nil else { return }
        if NSApp.isActive, !hasModal { openSetup() } else { setupResultWaiting = true }
    }

    /// The hint's button: the script never started, so the run is closed by hand.
    func abandonSetupRun() {
        guard let run = setupRun, !run.finished else { return }
        setupWatchTask?.cancel()
        setupTerminals[run.terminalId]?.close()
        Task { await finishSetupRun(id: run.terminalId) }
    }

    // MARK: After installing

    /// The existing sign-in flow in a pane when there is a local workspace;
    /// otherwise in a terminal window of the setup screen's own, which is never
    /// saved, shown to the phone or selected.
    func signInFromSetup(_ provider: String) {
        if snapshot.workspaces.contains(where: { $0.remote == nil }) {
            setupSheet = nil
            if showSettings { showSettings = false }
            startCLILogin(provider)
            return
        }
        cliAccountMessages[provider] = nil
        if let reason = cliAccountBlockedReason(provider) { setupMessage = reason; return }
        guard !cliAccountBusy.contains(provider), let command = CLIAccountSupport.loginCommand(provider: provider, option: .account) else { return }
        endCLILogin(provider)
        let id = "setup-login-" + UUID().uuidString
        openSetupTerminal(id: id, title: L("loginRecovery.terminalTitle", ["provider": ProviderOptions.label(provider)]), input: TerminalInput(text: command, autoRun: true))
        watchCLILogin(provider, terminalId: id)
    }

    /// An agent CLI below the app's minimum gets the existing CLI update.
    func updateFromSetup(_ provider: String) {
        startCLIUpdates(providers: [provider])
    }

    /// A vendor page from the catalog, whose decoder only lets allow-listed https pages in.
    func openSetupVendorPage(_ item: SetupItem) {
        guard case .vendorPage(let url)? = item.platforms[.macos]?.install else { return }
        NSWorkspace.shared.open(url)
    }

    /// The setup item for an agent pane's provider when its CLI is missing on this
    /// Mac, so the blocked message can offer to install it.
    func setupItemForMissingCLI(_ session: RunSession) -> String? {
        guard session.kind != "shell", !smokeProvidersTreatedAvailable.contains(session.provider),
              snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote == nil,
              runtime != nil, providerRuntime(session.provider, workspaceId: session.workspaceId).version == nil else { return nil }
        return loadSetupCatalog()?.items.first { $0.provider == session.provider }?.id
    }

    func closeSetupTerminals() {
        for window in setupTerminals.values { window.close() }
    }
}
