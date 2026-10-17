import AppKit
import MightyCore

/// The setup screen's request: it opens as a sheet, scrolled to `focus` when set.
struct SetupSheetRequest: Identifiable, Equatable {
    let id = UUID()
    var focus: String?
}

/// The install the setup screen started: one terminal pane running one script.
struct SetupRun: Equatable {
    var items: [String]
    var sessionId: String
    var markerPath: String
    var finished: Bool
}

/// The confirmation's content: every step exactly as the script runs it.
struct SetupConfirmation: Identifiable, Equatable {
    let id = UUID()
    var steps: [SetupScriptStep]
}

/// 필요한 도구: what the app needs on this Mac, read from the bundled catalog
/// (`SetupCatalog`), probed fresh, and installed in one visible terminal pane.
/// The app writes the script from the catalog alone, never runs sudo and never
/// sees a password: whatever a step asks, the user answers in the terminal.
extension AppStore {
    static let setupNeverShowKey = "setup.neverShow"
    static let setupPollInterval: Duration = .seconds(5)
    static let setupRunPollInterval: Duration = .seconds(2)

    var setupItems: [SetupItem] { setupCatalog?.items.filter { $0.platforms[.macos] != nil } ?? [] }

    /// The catalog, read once. A broken bundle is reported, not hidden.
    @discardableResult
    func loadSetupCatalog() -> SetupCatalog? {
        if let setupCatalog { return setupCatalog }
        do { setupCatalog = try SetupCatalog.bundled() }
        catch { setupMessage = L("setup.error.catalog", ["error": error.localizedDescription]) }
        return setupCatalog
    }

    /// Probes every item again. `forceEnvironment` reads the login shell anew
    /// (after an install it may have changed); the timer uses the short cache.
    /// When an agent CLI changed, the app's runtime is read again too, so a pane
    /// that was blocked for a missing CLI is not blocked any longer.
    func refreshSetup(forceEnvironment: Bool = false) async {
        guard !ending, !setupProbing, let catalog = loadSetupCatalog() else { return }
        setupProbing = true
        defer { setupProbing = false }
        let results: [String: SetupProbeResult]
        if let fixed = setupFixedResults { results = fixed }
        else {
            let environment = await providers.executionEnvironment(forceRefresh: forceEnvironment).values
            results = await SetupProber(environment: environment).probeAll(catalog, platform: .macos)
        }
        guard !ending else { return }
        setupResults = results
        setupSelection = setupSelection.filter { results[$0]?.isInstalled != true }
        let agents = catalog.items.filter { $0.provider != nil }.map { results[$0.id]?.version ?? "" }
        if setupFixedResults == nil, let cached = runtime?.providers, SetupRuntimeSync.isStale(runtime: cached, catalog: catalog, results: results), agents != setupRuntimeSyncedAgents {
            setupRuntimeSyncedAgents = agents
            await refreshRuntimeAfterCLIUpdate()
            refreshCLIAccounts()
        }
    }

    /// At launch: open the screen when no agent CLI can run or a required tool is
    /// missing, unless the user asked never to see it again. Never in a smoke run
    /// or the help capture, which have their own screens to show.
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
        preselectSetup(focus: focus)
        setupSheet = SetupSheetRequest(focus: focus)
    }

    /// Required items that are missing start checked; optional ones do not.
    func preselectSetup(focus: String? = nil) {
        for item in setupItems where item.required && setupResults[item.id] == .missing && item.platforms[.macos]?.install.runsInTerminal == true {
            setupSelection.insert(item.id)
        }
        if let focus, setupResults[focus] == .missing, setupCatalog?.item(focus)?.platforms[.macos]?.install.runsInTerminal == true { setupSelection.insert(focus) }
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
        let finishesOutside: Bool
        if case .argv(_, let outside)? = item.platforms[.macos]?.install { finishesOutside = outside } else { finishesOutside = false }
        let inRun = setupRun?.items.contains(item.id) == true
        return SetupRowState.derive(setupResults[item.id], inRun: inRun, runFinished: setupRun?.finished ?? true, finishesOutside: finishesOutside)
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
        guard setupRun?.finished != false else { return }
        if !setupSelection.insert(id).inserted { setupSelection.remove(id) }
    }

    // MARK: Installing

    /// Shows the one confirmation: every command the script will run, in order.
    func requestSetupInstall() {
        guard let catalog = setupCatalog, setupRun?.finished != false else { return }
        let steps = SetupScript.steps(for: setupPlanIds, catalog: catalog, platform: .macos, title: { $0.title })
        guard !steps.isEmpty else { return }
        setupConfirmation = SetupConfirmation(steps: steps)
    }

    /// Writes the script, opens a terminal pane in a local workspace and runs it
    /// there. The probe, not the script's exit code, decides what got installed.
    func startSetupInstall() async {
        guard let confirmation = setupConfirmation, setupRun?.finished != false, !ending else { return }
        setupConfirmation = nil
        let directory = dataDirectory.appendingPathComponent("setup", isDirectory: true)
        let stamp = String(Int(Date().timeIntervalSince1970))
        let scriptURL = directory.appendingPathComponent("install-\(stamp).sh"), marker = directory.appendingPathComponent("install-\(stamp).done")
        let labels = SetupScriptLabels(step: { "[\($0)/\($1)] \($2)" }, failed: L("setup.script.failed"), waiting: L("setup.script.waiting"), done: L("setup.script.done"))
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            // Earlier runs' scripts and markers are of no further use.
            for old in (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [] { try? FileManager.default.removeItem(at: old) }
            try Data(SetupScript.render(steps: confirmation.steps, labels: labels, markerPath: marker.path).utf8).write(to: scriptURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: scriptURL.path)
        } catch {
            setupMessage = L("setup.error.script", ["error": error.localizedDescription])
            return
        }
        // addSession refuses while a sheet is up: the screen closes and comes back when the script ends.
        setupSheet = nil
        if showSettings { showSettings = false }
        guard let workspace = await setupWorkspace(), let id = addSession(kind: "shell", workspaceId: workspace.id) else {
            setupMessage = L("setup.error.terminal")
            openSetup()
            return
        }
        updateSession(id) { $0.title = L("setup.terminal.title") }
        // The app wrote this command itself from its own catalog, after the user confirmed it.
        pendingTerminalInput[id] = TerminalInput(text: SetupScript.launchCommand(scriptPath: scriptURL.path), autoRun: true)
        setupMessage = nil
        setupRun = SetupRun(items: confirmation.steps.map(\.itemId), sessionId: id, markerPath: marker.path, finished: false)
        watchSetupRun(id: id, marker: marker)
    }

    /// The pane's script leaves its marker when it ends for any reason; a closed
    /// pane ends the run too. Then everything is probed again and the screen comes
    /// back with the outcome and any sign-in to offer.
    private func watchSetupRun(id: String, marker: URL) {
        setupWatchTask?.cancel()
        setupWatchTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.setupRunPollInterval)
                guard let self, !Task.isCancelled, !self.ending else { return }
                let paneOpen = self.snapshot.sessions.contains { $0.id == id }
                if FileManager.default.fileExists(atPath: marker.path) || !paneOpen { break }
            }
            guard let self, !Task.isCancelled, !self.ending, self.setupRun?.sessionId == id else { return }
            self.setupRun?.finished = true
            await self.refreshSetup(forceEnvironment: true)
            if !self.hasModal { self.setupSheet = SetupSheetRequest(focus: nil) }
        }
    }

    /// A local workspace for the install pane. A first run may have none: the
    /// home folder is added then, the folder the official installers work from.
    private func setupWorkspace() async -> Workspace? {
        if let active = activeWorkspace, active.remote == nil { selectWorkspace(active.id); return active }
        if let local = snapshot.workspaces.first(where: { $0.remote == nil }) { selectWorkspace(local.id); return local }
        let home = FileManager.default.homeDirectoryForCurrentUser
        do {
            let workspace = try await repository.approveWorkspace(Workspace(name: home.lastPathComponent, path: home.path))
            if !snapshot.workspaces.contains(where: { $0.id == workspace.id }) { snapshot.workspaces.append(workspace) }
            selectWorkspace(workspace.id)
            return workspace
        } catch {
            self.error = error.localizedDescription
            return nil
        }
    }

    // MARK: After installing

    /// The existing sign-in flow; the screen closes so its terminal pane can open.
    func signInFromSetup(_ provider: String) {
        setupSheet = nil
        if showSettings { showSettings = false }
        startCLILogin(provider)
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
}
