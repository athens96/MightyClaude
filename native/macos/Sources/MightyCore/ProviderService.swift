import Foundation

public struct ProviderCommand: Sendable, Equatable {
    public var provider: String
    public var executable: URL
    public var version: String
    public init(provider: String, executable: URL, version: String) { self.provider = provider; self.executable = executable; self.version = version }
}

public actor ProviderService {
    private let binaryOverrides: [String: URL]
    private let fixedEnvironment: [String: String]?
    private let environmentResolver: CLIEnvironmentResolver
    /// How long a CLI's `--version` may take before that CLI counts as not found.
    private let versionProbeTimeout: TimeInterval
    private struct CommandEntry {
        var id: UUID
        var created: Date
        var environment: [String: String]
        var task: Task<ProviderCommand?, Never>
    }
    private var commands: [CatalogKey: CommandEntry] = [:]
    private struct CatalogKey: Hashable { var provider: String; var workspacePath: String? }
    private struct CatalogEntry {
        var id: UUID
        var created: Date
        var task: Task<ModelCatalog, Never>
        var inFlight: Bool
        var forced: Bool
        var environment: [String: String]
    }
    private var catalogs: [CatalogKey: CatalogEntry] = [:]
    private var cacheGeneration = 0
    private var closing = false
    /// Where the app keeps what it learns from the CLIs: the last list each gave (`KeptModelLists`) and Hermes's
    /// picker. nil (tests, helpers) keeps the lists in memory only and has no Hermes picker.
    private let storageDirectory: URL?
    /// The picker the last Hermes session offered (`HermesAgent.saveModels`), in the storage folder.
    public nonisolated let hermesModelCacheURL: URL?
    /// Loaded from the storage folder on first use.
    private var keptLists: KeptModelLists?
    private var keptWrittenAt: Date?
    public init(binaryOverrides: [String: URL] = [:], environment: [String: String]? = nil, environmentResolver: CLIEnvironmentResolver = .shared, versionProbeTimeout: TimeInterval = 4, storageDirectory: URL? = nil) {
        self.binaryOverrides = binaryOverrides; self.fixedEnvironment = environment; self.environmentResolver = environmentResolver; self.versionProbeTimeout = versionProbeTimeout
        self.storageDirectory = storageDirectory
        self.hermesModelCacheURL = storageDirectory?.appendingPathComponent(HermesAgent.modelCacheFileName)
    }

    public nonisolated static func runtimeEnvironment() -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let entries = (env["PATH"] ?? "").split(separator: ":").map(String.init) + [home + "/.local/bin", home + "/.npm-global/bin", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"]
        var seen = Set<String>(); env["PATH"] = entries.filter { !$0.isEmpty && seen.insert($0).inserted }.joined(separator: ":")
        return env
    }
    public nonisolated static func supportsMods(_ version: String) -> Bool {
        guard let range = version.range(of: "[0-9]+\\.[0-9]+\\.[0-9]+", options: .regularExpression) else { return false }
        let end = range.upperBound
        if end < version.endIndex, version[end] == "-" { return false }
        let numbers = version[range].split(separator: ".").compactMap { Int($0) }
        return !numbers.lexicographicallyPrecedes([2, 1, 271])
    }

    /// Approval RPCs are checked against the stable 0.153.4 app-server schema.
    /// Unknown and prerelease CLIs keep the existing noninteractive modes.
    public nonisolated static func supportsCodexApprovals(_ version: String) -> Bool {
        guard let range = version.range(of: "[0-9]+\\.[0-9]+\\.[0-9]+", options: .regularExpression) else { return false }
        if range.upperBound < version.endIndex, version[range.upperBound] == "-" { return false }
        let numbers = version[range].split(separator: ".").compactMap { Int($0) }
        return !numbers.lexicographicallyPrecedes([0, 153, 4])
    }

    /// The environment of a headless Claude CLI run that must stay quiet: the model-list probe and the work map's summary.
    /// `DISABLE_TELEMETRY` is left out on purpose: with it the CLI skips fetching the account's current model
    /// configuration and answers with its built-in list, so a model released after the CLI was built (Fable 5.2 on
    /// 2.1.322) never reaches the model menu, and the `fable` alias reads as the older model.
    static let quietClaudeEnvironment: [String: String] = ["CLAUDE_CODE_SAFE_MODE": "1", "CLAUDE_CODE_DISABLE_TERMINAL_TITLE": "1", "CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL": "1", "CLAUDE_CODE_DISABLE_BACKGROUND_TASKS": "1", "DISABLE_AUTOUPDATER": "1", "DISABLE_ERROR_REPORTING": "1", "CLAUDE_CODE_SKIP_PROMPT_HISTORY": "1"]

    public nonisolated static func capabilities(provider: String, version: String?) -> ProviderCapabilities {
        var value = ProviderOptions.fallbackRuntime(provider).capabilities
        // Keep the app's existing Mods minimum. This advertises CLI syntax
        // support, not account/model/admin eligibility for the classifier.
        if provider == "claude", let version, supportsMods(version) {
            value.permissionModes = ProviderOptions.permissionModes(provider: provider)
        }
        if provider == "codex", let version, supportsCodexApprovals(version) {
            value.permissionModes = ProviderOptions.permissionModes(provider: provider)
        }
        return value
    }

    public func executionEnvironment(workspacePath: String? = nil, forceRefresh: Bool = false) async -> CLIEnvironmentSnapshot {
        if let fixedEnvironment { return CLIEnvironmentSnapshot(values: fixedEnvironment, source: .provided) }
        return await environmentResolver.resolve(workspacePath: workspacePath, forceRefresh: forceRefresh)
    }

    public func command(provider: String, workspacePath: String? = nil) async -> ProviderCommand? {
        guard !closing else { return nil }
        let snapshot = await executionEnvironment(workspacePath: workspacePath)
        return await command(provider: provider, workspacePath: workspacePath, snapshot: snapshot)
    }

    func command(provider: String, workspacePath: String?, snapshot: CLIEnvironmentSnapshot) async -> ProviderCommand? {
        guard !closing, ProviderOptions.ids.contains(provider) else { return nil }
        let key = CatalogKey(provider: provider, workspacePath: workspacePath.map { URL(fileURLWithPath: $0).standardizedFileURL.path })
        let entry: CommandEntry
        if let cached = commands[key], cached.environment == snapshot.values, Date().timeIntervalSince(cached.created) < 60 {
            entry = cached
        } else {
            commands[key]?.task.cancel()
            if commands[key] == nil, commands.count >= 64, let oldest = commands.min(by: { $0.value.created < $1.value.created }) {
                commands.removeValue(forKey: oldest.key)?.task.cancel()
            }
            let override = binaryOverrides[provider], env = snapshot.values, probeTimeout = versionProbeTimeout
            let task = Task<ProviderCommand?, Never> {
                let paths = override.map { [$0] } ?? (env["PATH"] ?? "").split(separator: ":").prefix(64).map { URL(fileURLWithPath: String($0)).appendingPathComponent(ProviderOptions.executable(provider)) }
                for path in paths {
                    if Task.isCancelled { return nil }
                    guard FileManager.default.isExecutableFile(atPath: path.path) else { continue }
                    if let result = try? await ProcessCapture.run(executable: path, arguments: ["--version"], environment: env, timeout: probeTimeout, maximumBytes: 16_384), result.exitCode == 0 {
                        var version = String(String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines).prefix(160))
                        if provider == "hermes" { version = HermesAgent.versionLine(version) }
                        if !version.isEmpty { return ProviderCommand(provider: provider, executable: path, version: version) }
                    }
                }
                return nil
            }
            entry = CommandEntry(id: UUID(), created: Date(), environment: snapshot.values, task: task)
            commands[key] = entry
        }
        let value = await entry.task.value
        return !closing && commands[key]?.id == entry.id ? value : nil
    }

    public func modelCatalog(provider: String, workspacePath: String? = nil, forceRefresh: Bool = false) async -> ModelCatalog {
        guard !closing else { return ProviderOptions.fallbackCatalog(provider) }
        let snapshot = await executionEnvironment(workspacePath: workspacePath, forceRefresh: forceRefresh)
        var catalog = await modelCatalog(provider: provider, workspacePath: workspacePath, forceRefresh: forceRefresh, snapshot: snapshot)
        if let detail = snapshot.fallbackDetail { catalog.detail += " " + detail }
        return catalog
    }

    func modelCatalog(provider: String, workspacePath: String?, forceRefresh: Bool = false, snapshot: CLIEnvironmentSnapshot) async -> ModelCatalog {
        guard !closing, ProviderOptions.ids.contains(provider) else { return ProviderOptions.fallbackCatalog(provider) }
        let cwd = workspacePath.map { URL(fileURLWithPath: $0, isDirectory: true).standardizedFileURL }
        let key = CatalogKey(provider: provider, workspacePath: cwd?.path)
        let entry: CatalogEntry
        if let cached = catalogs[key], cached.environment == snapshot.values,
           (forceRefresh ? cached.inFlight && cached.forced : cached.inFlight || Date().timeIntervalSince(cached.created) < 60) {
            entry = cached
        } else {
            // A refresh supersedes discovery begun before credentials/settings
            // changed, while concurrent refreshes for this context share work.
            catalogs[key]?.task.cancel()
            if catalogs[key] == nil, catalogs.count >= 64,
               let oldest = catalogs.min(by: { $0.value.created < $1.value.created }) {
                oldest.value.task.cancel(); catalogs.removeValue(forKey: oldest.key)
            }
            let generation = cacheGeneration, id = UUID()
            let task = Task<ModelCatalog, Never> {
                let read = await self.readModelList(provider: provider, workspacePath: workspacePath, snapshot: snapshot, workspace: cwd, generation: generation)
                // A read that a newer one superseded, or an invalidation dropped, answers nothing and keeps nothing.
                guard !Task.isCancelled, !self.closing, generation == self.cacheGeneration, self.catalogs[key]?.id == id else { return ProviderOptions.fallbackCatalog(provider) }
                return self.settle(read, provider: provider, workspace: cwd?.path)
            }
            entry = CatalogEntry(id: id, created: Date(), task: task, inFlight: true, forced: forceRefresh, environment: snapshot.values)
            catalogs[key] = entry
        }
        let value = await entry.task.value
        guard !closing, catalogs[key]?.id == entry.id else { return ProviderOptions.fallbackCatalog(provider) }
        catalogs[key]?.inFlight = false
        return value
    }

    /// One read of the provider's model list: its CLI's answer, or why there is none.
    private func readModelList(provider: String, workspacePath: String?, snapshot: CLIEnvironmentSnapshot, workspace: URL?, generation: Int) async -> ModelListRead {
        guard let command = await command(provider: provider, workspacePath: workspacePath, snapshot: snapshot) else {
            return .failed(L("run.error.cliExecutableMissing", ["name": ProviderOptions.label(provider)]))
        }
        guard !Task.isCancelled, !closing, generation == cacheGeneration else { return .failed(CancellationError().localizedDescription) }
        switch provider {
        // Hermes lists models only inside a session: the picker its last session offered is kept and read here.
        case "hermes": return .listed(HermesAgent.cachedCatalog(url: hermesModelCacheURL))
        case "claude", "codex": return await CLIModelProbe.read(command: command, environment: snapshot.values, workspace: workspace)
        default: return await Self.readListCommand(provider: provider, command: command, environment: snapshot.values, workspace: workspace)
        }
    }

    /// A list the CLI gave is kept as its newest; a read that failed answers with the list kept for this workspace
    /// (else the provider's newest anywhere), or the built-in list, either marked with why the read failed.
    private func settle(_ read: ModelListRead, provider: String, workspace: String?) -> ModelCatalog {
        switch read {
        case .listed(let catalog):
            if catalog.isFresh, provider != "hermes" { keep(catalog, provider: provider, workspace: workspace) }
            return catalog
        case .failed(let reason):
            if provider != "hermes", let entry = loadedKeptLists().list(provider: provider, workspace: workspace) { return KeptModelLists.kept(entry, reason: reason) }
            var fallback = ProviderOptions.fallbackCatalog(provider)
            fallback.readFailure = reason
            return fallback
        }
    }

    private func keep(_ catalog: ModelCatalog, provider: String, workspace: String?) {
        var lists = loadedKeptLists()
        let now = Date()
        let write = lists.remember(catalog, provider: provider, workspace: workspace, at: now, writtenAt: keptWrittenAt)
        keptLists = lists
        guard write, let url = KeptModelLists.url(in: storageDirectory), let data = lists.encoded() else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard (try? data.write(to: url, options: .atomic)) != nil else { return }
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        keptWrittenAt = now
    }

    private func loadedKeptLists() -> KeptModelLists {
        if let keptLists { return keptLists }
        let loaded = KeptModelLists.load(from: KeptModelLists.url(in: storageDirectory))
        keptLists = loaded
        return loaded
    }

    /// The arguments (after the executable) of each CLI's model-list read. Each was run against the CLI named here,
    /// and ModelListProbeTests pins them: a flag one CLI takes is not another's (agy 1.5.5 refuses `models --standalone`).
    /// - claude 2.1.322: a headless stream-json session that is sent only `initialize` (its answer lists the models).
    /// - codex 0.167.0: `app-server` over stdio, `initialize` then `model/list`.
    /// - agy 1.5.5: `models`. pi 1.5.2: `--list-models`. opencode 2.2.7: `models --standalone`, without starting
    ///   OpenCode 2's background service.
    /// - Hermes 0.23.1 has no list command (nil): the picker its last session offered is read instead.
    public nonisolated static func modelListArguments(_ provider: String) -> [String]? {
        switch provider {
        case "claude": return ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--safe-mode"]
        case "codex": return ["app-server", "--listen", "stdio://"]
        case "gemini": return ["models"]
        case "pi": return ["--list-models"]
        case "opencode": return ["models", "--standalone"]
        default: return nil
        }
    }

    /// The command a model-list read runs, as its failure names it.
    nonisolated static func modelListCommand(_ provider: String) -> String {
        let executable = ProviderOptions.executable(provider)
        guard provider != "claude", let arguments = modelListArguments(provider) else { return executable }
        return ([executable] + arguments).joined(separator: " ")
    }

    /// The one line the model menu's help shows for a read that failed: the command, what went wrong, and the line
    /// of the CLI's output that says why. The home folder reads `~`.
    public nonisolated static func readFailure(_ provider: String, problem: String, output: String = "") -> String {
        var text = modelListCommand(provider) + ": " + problem
        if let line = tellingLine(output) { text += " · " + line }
        return ActivitySupport.clean(DiagnosticsSummary.redact(text), maximumBytes: 400, singleLine: true)
    }

    /// The line of a CLI's output that says why it failed: the last that names an error, else the first that says anything.
    nonisolated static func tellingLine(_ output: String) -> String? {
        let lines = output.split(whereSeparator: \.isNewline).suffix(200).map { ActivitySupport.clean(String($0), maximumBytes: 240, singleLine: true) }.filter { !$0.isEmpty }
        return lines.last { $0.range(of: "error", options: .caseInsensitive) != nil } ?? lines.first
    }

    /// `agy models`, `pi --list-models` and `opencode models --standalone`: one command whose standard output is the list.
    /// A non-zero exit, a timeout, too much output or no models in it is a failed read.
    static func readListCommand(provider: String, command: ProviderCommand, environment: [String: String], workspace: URL?) async -> ModelListRead {
        guard let arguments = modelListArguments(provider) else { return .failed(readFailure(provider, problem: L("provider.catalog.failure.noModels"))) }
        let timeout: TimeInterval = provider == "opencode" ? 20 : 15
        let limit = provider == "opencode" ? 2_097_152 : provider == "pi" ? 1_048_576 : 262_144
        let result: ProcessResult
        do {
            result = try await ProcessCapture.run(executable: command.executable, arguments: arguments, environment: environment, cwd: workspace ?? FileManager.default.temporaryDirectory, timeout: timeout, maximumBytes: limit)
        } catch { return .failed(readFailure(provider, problem: error.localizedDescription)) }
        let stdout = String(decoding: result.stdout, as: UTF8.self), stderr = String(decoding: result.stderr, as: UTF8.self)
        guard result.exitCode == 0 else {
            return .failed(readFailure(provider, problem: L("provider.catalog.failure.exit", ["code": "\(result.exitCode)"]), output: stderr.isEmpty ? stdout : stderr))
        }
        let catalog = provider == "gemini" ? normalizeAntigravityCatalog(stdout) : provider == "pi" ? normalizePiCatalog(stdout) : normalizeOpencodeCatalog(stdout)
        return catalog.isFresh ? .listed(catalog) : .failed(readFailure(provider, problem: L("provider.catalog.failure.noModels"), output: stdout + "\n" + stderr))
    }

    public func providerRuntime(provider: String, workspacePath: String? = nil, forceRefresh: Bool = false) async -> ProviderRuntime {
        var runtime = ProviderOptions.fallbackRuntime(provider)
        guard !closing else { return runtime }
        let snapshot = await executionEnvironment(workspacePath: workspacePath, forceRefresh: forceRefresh)
        guard !Task.isCancelled else { return runtime }
        if let command = await command(provider: provider, workspacePath: workspacePath, snapshot: snapshot) {
            runtime.version = command.version
            runtime.capabilities = Self.capabilities(provider: provider, version: command.version)
            runtime.available = provider != "claude" || Self.supportsMods(command.version)
            runtime.detail = runtime.available ? L("provider.available") : L("run.error.modsVersionUnsupported", ["version": command.version])
        }
        guard !Task.isCancelled else { return runtime }
        runtime.modelCatalog = await modelCatalog(provider: provider, workspacePath: workspacePath, forceRefresh: forceRefresh, snapshot: snapshot)
        if let detail = snapshot.fallbackDetail { runtime.detail += " " + detail; runtime.modelCatalog.detail += " " + detail }
        return runtime
    }

    public func runtimeInfo(appVersion: String = "0.1.0", workspacePath: String? = nil, forceRefresh: Bool = false) async -> RuntimeInfo {
        var providers: [ProviderRuntime] = []
        for id in ProviderOptions.ids {
            providers.append(await providerRuntime(provider: id, workspacePath: workspacePath, forceRefresh: forceRefresh))
        }
        let claude = providers.first { $0.id == "claude" }
        let installed = claude?.version != nil
        return RuntimeInfo(appVersion: appVersion, claudeAvailable: installed, claudeVersion: claude?.version, modelCatalog: claude?.modelCatalog, providers: providers, mods: ModsRuntime(status: !installed ? "unavailable" : claude?.available == true ? "available" : "unsupported", detail: claude?.detail ?? ""))
    }
    public func invalidateCaches() async {
        cacheGeneration &+= 1
        let running = commands.values.map { $0.task }; let metadata = catalogs.values.map { $0.task }
        commands.removeAll(); catalogs.removeAll()
        running.forEach { $0.cancel() }; metadata.forEach { $0.cancel() }
        for task in running { _ = await task.value }; for task in metadata { _ = await task.value }
    }
    /// Authentication changes must not join metadata started before the change.
    /// Invalidate only this context; the next refresh starts a new generation.
    public func invalidateModelCatalog(provider: String, workspacePath: String? = nil) async {
        let key = CatalogKey(provider: provider, workspacePath: workspacePath.map { URL(fileURLWithPath: $0, isDirectory: true).standardizedFileURL.path })
        catalogs.removeValue(forKey: key)?.task.cancel()
        commands.removeValue(forKey: key)?.task.cancel()
        if fixedEnvironment == nil { await environmentResolver.invalidate(workspacePath: workspacePath) }
    }
    public func shutdown() async { closing = true; await invalidateCaches() }

    public func discardModelCatalogs(provider: String) async {
        let keys = Set(catalogs.keys.filter { $0.provider == provider }).union(commands.keys.filter { $0.provider == provider })
        for key in keys { await invalidateModelCatalog(provider: provider, workspacePath: key.workspacePath) }
    }

    /// `phaseModels` nil takes the request's own, which is how the app launches.
    /// `codexHome` is where a Codex run's user config lives; nil resolves it
    /// from this process's environment.
    public nonisolated static func arguments(_ request: StartRunRequest, pluginDirectory: URL, allowPermissionPrompts: Bool = false, phaseModels: PhaseModelConfig? = nil, paneMCPBinding: PaneMCPBinding? = nil, codexHome: URL? = nil) throws -> [String] {
        try CoreValidation.validate(request)
        let s = request.settings
        let phaseModels = phaseModels ?? request.phaseModels
        switch request.provider {
        case "claude":
            let launched = request.permissionModeOverride ?? s.permissionMode
            let mode = launched == "fullAccess" ? "bypassPermissions" : launched
            var args = ["--print", "--verbose", "--output-format", "stream-json", "--permission-prompts", allowPermissionPrompts ? "host" : "none", "--plugin-dir", pluginDirectory.path, "--permission-mode", mode]
            if allowPermissionPrompts { args += ["--input-format", "stream-json", "--permission-prompt-tool", "stdio"] }
            // Session-level model selection wins over phase-level config.
            // An omitted model restores the resumed conversation's saved ID.
            // Explicit default clears that override using the current CLI setup.
            let effectiveModel = request.model != "default" ? request.model : phaseModels.claudeMain
            if effectiveModel != "default" || request.resumeId != nil { args += ["--model", effectiveModel] }
            // Build --settings env JSON: effort + phase model alias pins.
            var envVars: [String: String] = [:]
            if s.effort != "default" { envVars["CLAUDE_CODE_EFFORT_LEVEL"] = s.effort }
            // The advisor, as the pane chose it: a model is named beside the main one, off is the CLI's own switch for
            // this run alone, and a pane that follows Claude's settings passes nothing. The CLI checks the model itself
            // (an advisor weaker than the main model is refused in its own words).
            if let advisor = s.advisor {
                if advisor == RunSettings.advisorOff { envVars[ClaudeAdvisorSettings.disableVariable] = "1" }
                else { args += ["--advisor", advisor] }
            }
            if phaseModels.claudeOpusAlias != "default" { envVars["ANTHROPIC_DEFAULT_OPUS_MODEL"] = phaseModels.claudeOpusAlias }
            if phaseModels.claudeSonnetAlias != "default" { envVars["ANTHROPIC_DEFAULT_SONNET_MODEL"] = phaseModels.claudeSonnetAlias }
            if phaseModels.claudeHaikuAlias != "default" { envVars["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = phaseModels.claudeHaikuAlias }
            if phaseModels.claudeSubagentDefault != "default" { envVars["CLAUDE_CODE_SUBAGENT_MODEL"] = phaseModels.claudeSubagentDefault }
            if !envVars.isEmpty {
                if s.effort != "default" { args += ["--effort", s.effort] }
                let data = try JSONSerialization.data(withJSONObject: ["env": envVars], options: [.sortedKeys])
                args += ["--settings", String(decoding: data, as: UTF8.self)]
            }
            if let turns = s.maxTurns { args += ["--max-turns", String(turns)] }
            if let budget = s.maxBudgetUsd { args += ["--max-budget-usd", String(budget)] }
            if let resume = request.resumeId { args += ["--resume", resume] }
            // Adds our server beside the user's own MCP servers; the token rides in the environment.
            if let binding = paneMCPBinding { args += ["--mcp-config", try binding.claudeMCPConfigJSON()] }
            return args
        case "codex":
            let asks = s.permissionMode == "onRequest"
            guard !asks || allowPermissionPrompts else { throw MightyError(L("provider.error.codexOnRequestNeedsApp")) }
            let sandbox = s.permissionMode == "fullAccess" ? "danger-full-access" : ["acceptEdits", "onRequest"].contains(s.permissionMode) ? "workspace-write" : "read-only"
            var args = ["-c", "approval_policy=\"\(asks ? "on-request" : "never")\"", "-c", "sandbox_mode=\"\(sandbox)\"", "-c", "sandbox_workspace_write.network_access=\(s.networkAccess)", "-c", "features.fast_mode=\(s.fastMode)", "-c", "service_tier=\"\(s.fastMode ? "fast" : "default")\""]
            if s.webSearch != "default" { args += ["-c", "web_search=\"\(s.webSearch)\""] }
            if sandbox == "workspace-write", let root = request.delegationWorktreeRoot {
                args += ["-c", "sandbox_workspace_write.writable_roots=\(PaneMCPBinding.tomlLiteral([root]))"]
            }
            // Phase model knobs: review_model, subagent model and effort, plan_mode_reasoning_effort.
            if phaseModels.codexReviewModel != "default" { args += ["-c", "review_model=\"\(phaseModels.codexReviewModel)\""] }
            if phaseModels.codexSubagentDefault != "default" { args += ["-c", "agents.default_subagent_model=\"\(phaseModels.codexSubagentDefault)\""] }
            if phaseModels.codexSubagentEffort != "default" { args += ["-c", "agents.default_subagent_reasoning_effort=\"\(phaseModels.codexSubagentEffort)\""] }
            if phaseModels.codexPlanModeReasoningEffort != "default" { args += ["-c", "plan_mode_reasoning_effort=\"\(phaseModels.codexPlanModeReasoningEffort)\""] }
            if let binding = paneMCPBinding {
                let home = codexHome ?? CLIAccountSupport.codexHome(home: FileManager.default.homeDirectoryForCurrentUser, environment: ProcessInfo.processInfo.environment)
                let user = CodexUserInstructions.effective(codexHome: home, workingDirectory: URL(fileURLWithPath: binding.workspacePath, isDirectory: true))
                args += binding.codexMCPArgs(userInstructions: user, mode: s.permissionMode)
            }
            if asks {
                // Never inherit delegated automatic approval from a user profile.
                args += ["-c", "approvals_reviewer=\"user\""]
                if request.model != "default" { args += ["-c", "model=\"\(request.model)\""] }
                if s.effort != "default" { args += ["-c", "model_reasoning_effort=\"\(s.effort)\""] }
                return args + ["app-server", "--listen", "stdio://"]
            }
            args += ["exec"]
            if let resume = request.resumeId { args += ["resume", resume] }
            args += ["--json", "--skip-git-repo-check"]
            if request.model != "default" { args += ["--model", request.model] }
            if s.effort != "default" { args += ["-c", "model_reasoning_effort=\"\(s.effort)\""] }
            return args + ["-"]
        case "gemini":
            // Antigravity CLI: `--print=` with no text takes the prompt from the
            // stream-json line on stdin (`ProviderInput`), one turn per line;
            // closing stdin ends the session. `--print-timeout 0` waits for the turn.
            // Manual keeps agy's own review mode: headless, what it would ask
            // about (shell commands, file edits) is declined unless its settings allow it.
            var args = ["--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--print-timeout", "0"]
            switch s.permissionMode {
            case "plan": args += ["--mode", "plan"]
            case "acceptEdits": args += ["--mode", "accept-edits"]
            case "fullAccess": args += ["--dangerously-skip-permissions"]
            default: break
            }
            if request.model != "default" { args += ["--model", request.model] }
            if let resume = request.resumeId { args += ["--conversation", resume] }
            if let root = request.delegationWorktreeRoot { args += ["--add-dir", root] }
            return args
        case "pi":
            // One RPC-mode process per request (`pi --mode rpc`): the prompt, a steer and the
            // stop travel as JSON lines on stdin (`ProviderInput.piPrompt`). pi has no approval
            // window, so manual keeps only the read tools and full access leaves every tool on.
            var args = ["--mode", "rpc"]
            if request.model != "default" { args += ["--model", request.model] }
            if s.effort != "default" { args += ["--thinking", s.effort] }
            // The app's question tool (`ask_user`) rides along as an extension of this run only;
            // the read-only allowlist names it, or `--tools` would leave it off.
            let questions = PiAgent.questionExtension()
            if s.permissionMode != "fullAccess" { args += ["--tools", ProviderOptions.piReadOnlyTools + (questions == nil ? "" : "," + PiAgent.questionTool)] }
            if let questions { args += ["--extension", questions.path] }
            if let resume = request.resumeId { args += ["--session", resume] }
            return args
        case "opencode":
            // One ACP server per request (`opencode acp`, OpenCode 2): the session, the settings and
            // the prompt travel as JSON-RPC on stdio (`ACPChannel`), the permission mode as the
            // run's `OPENCODE_CONFIG_CONTENT` (`OpencodeAgent.permissionRules`).
            return ["acp"]
        case "hermes":
            // One ACP server per request (`hermes acp`), a viewer of Hermes's gateway daemon, which it
            // starts when needed. The session, the settings and the prompt travel as JSON-RPC
            // (`ACPChannel`); full access is the run's `HERMES_YOLO_MODE` (`HermesAgent.apply`).
            return ["acp"]
        default: throw MightyError(L("common.unsupportedCli"))
        }
    }

    public nonisolated static func normalizeCodexCatalog(_ rows: [[String: Any]]) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("codex")
        var models = [fallback.models[0]]; var seen = Set(["default"])
        for row in rows.prefix(ProviderOptions.listedModelLimit("codex")) {
            guard let value = row["model"] as? String, CoreValidation.model(value), row["hidden"] as? Bool != true, seen.insert(value).inserted else { continue }
            // The efforts the CLI names for the model, `ultra` among them (`ProviderOptions.codexEfforts`).
            let levels = (row["supportedReasoningEfforts"] as? [[String: Any]])?.compactMap { $0["reasoningEffort"] as? String }.filter { ProviderOptions.codexEfforts.contains($0) }
            models.append(ModelOption(value: value, displayName: String((row["displayName"] as? String ?? value).prefix(160)), description: String((row["description"] as? String ?? "").prefix(2400)), supportsEffort: levels.map { !$0.isEmpty }, supportedEffortLevels: levels))
            if row["isDefault"] as? Bool == true {
                models[0].resolvedModel = value
                models[0].supportsEffort = levels.map { !$0.isEmpty }
                models[0].supportedEffortLevels = levels
            }
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: L("provider.catalog.codexSource")) : fallback
    }
    /// `agy models`: one `slug<TAB>Display Name` line per model on standard output (agy 1.5.5 writes its
    /// "Fetching available models..." line to standard error; an older agy wrote it first on standard output).
    /// It needs the network and a sign-in; anything else keeps the fallback list.
    public nonisolated static func normalizeAntigravityCatalog(_ text: String) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("gemini")
        var models = [fallback.models[0]]; var seen = Set(["default"])
        for line in text.split(whereSeparator: \.isNewline).prefix(256) {
            let fields = line.split(separator: "\t", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard fields.count == 2, CoreValidation.model(fields[0]), !fields[1].isEmpty, seen.insert(fields[0]).inserted, models.count <= ProviderOptions.listedModelLimit("gemini") else { continue }
            models.append(ModelOption(value: fields[0], displayName: ActivitySupport.clean(fields[1], maximumBytes: 160, singleLine: true)))
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: L("provider.catalog.antigravitySource")) : fallback
    }
    /// `pi --list-models`: a header row (`provider  model  context  max-out  thinking  images`)
    /// and one row per model, columns padded with spaces. Without a sign-in it prints "No models
    /// available…", which keeps the fallback (just pi's own default). A model is `provider/id`,
    /// the form `--model` takes; `thinking: no` means it has no thinking levels.
    public nonisolated static func normalizePiCatalog(_ text: String) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("pi")
        var models = [fallback.models[0]]; var seen = Set(["default"])
        for line in text.split(whereSeparator: \.isNewline).prefix(1_024) {
            let fields = line.split(whereSeparator: \.isWhitespace).map(String.init)
            guard fields.count >= 6, fields[0] != "provider", fields[4] == "yes" || fields[4] == "no" else { continue }
            let value = fields[0] + "/" + fields[1]
            guard CoreValidation.model(value), seen.insert(value).inserted, models.count <= ProviderOptions.listedModelLimit("pi") else { continue }
            models.append(ModelOption(value: value, displayName: value, description: L("provider.catalog.piModelRow", ["context": fields[2], "output": fields[3]]), supportsEffort: fields[4] == "yes"))
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: L("provider.catalog.piSource")) : fallback
    }
    /// `opencode models --standalone` (without OpenCode 2's background service): one `provider/model` per line.
    /// Without a sign-in it lists Zen's free models, which still run. Anything that is not a
    /// `provider/model` (a notice, a refresh line) is left out; an empty list keeps the fallback.
    public nonisolated static func normalizeOpencodeCatalog(_ text: String) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("opencode")
        var models = [fallback.models[0]]; var seen = Set(["default"])
        for line in text.split(whereSeparator: \.isNewline).prefix(4_096) {
            let value = line.trimmingCharacters(in: .whitespaces)
            guard let slash = value.firstIndex(of: "/"), slash != value.startIndex, value.index(after: slash) != value.endIndex,
                  !value.contains(" "), CoreValidation.model(value), seen.insert(value).inserted, models.count <= ProviderOptions.listedModelLimit("opencode") else { continue }
            let provider = String(value[..<slash])
            models.append(ModelOption(value: value, displayName: value, description: L("provider.catalog.opencodeModelRow", ["provider": provider])))
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: L("provider.catalog.opencodeSource")) : fallback
    }
    public nonisolated static func normalizeClaudeCatalog(_ rows: [[String: Any]]) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("claude")
        var models = [fallback.models[0]]; var seen = Set<String>()
        for row in rows.prefix(ProviderOptions.listedModelLimit("claude")) {
            guard let value = row["value"] as? String, CoreValidation.model(value), seen.insert(value).inserted else { continue }
            let supports = row["supportsEffort"] as? Bool
            let levels = (row["supportedEffortLevels"] as? [String])?.filter { ProviderOptions.efforts.contains($0) }
            // What the CLI says about a model's effort wins: Claude Code 2.1.322 gives Haiku 5.5 low to max. A Haiku model it
            // says nothing about (Haiku 4.5 there) keeps the old rule, no effort (`ProviderOptions.silentHaiku`).
            let silent = ProviderOptions.silentHaiku(value, supportsEffort: supports, levels: levels)
            let option = ModelOption(value: value, displayName: String((row["displayName"] as? String ?? value).prefix(160)), description: String((row["description"] as? String ?? "").prefix(2400)), resolvedModel: (row["resolvedModel"] as? String).flatMap { CoreValidation.model($0) ? $0 : nil }, supportsEffort: silent ? false : supports, supportedEffortLevels: silent ? [] : levels)
            if value == "default" {
                models[0].resolvedModel = option.resolvedModel
                models[0].supportsEffort = option.supportsEffort
                models[0].supportedEffortLevels = option.supportedEffortLevels
            } else { models.append(option) }
        }
        return !seen.isEmpty ? ModelCatalog(source: "cli", models: models, detail: L("provider.catalog.claudeSource")) : fallback
    }
}

/// What one model-list read gave: the CLI's list, or why there is none (one line, for the model menu's help).
enum ModelListRead: Sendable, Equatable {
    case listed(ModelCatalog)
    case failed(String)
}

/// Claude's and Codex's model lists come from a short session with the CLI (`ProviderService.modelListArguments`):
/// Claude answers `initialize` with its models; Codex's app-server answers `initialize`, then `model/list`.
private final class CLIModelProbe: @unchecked Sendable {
    /// How long the CLI may take to answer before the read counts as failed.
    static let answerTimeout: TimeInterval = 6
    /// How a read ended, worded only once it is back in the caller's task (`read`).
    private enum Outcome {
        case listed(ModelCatalog)
        case exited(Int32, String)
        case refused(String?)
        case noModels, tooMuch, timedOut, cancelled
    }
    private let provider: String
    private let requestId = UUID().uuidString
    private let lock = NSLock()
    private var child: NativeChildProcess?
    private var buffer = Data()
    private var errors = Data()
    private var bytes = 0
    private var expected = 1
    private var rows: [[String: Any]] = []
    private var result: Outcome?
    private var continuation: CheckedContinuation<Outcome, Never>?
    private init(provider: String) { self.provider = provider }

    private func attach(_ process: NativeChildProcess) { lock.lock(); child = process; let closed = result != nil; lock.unlock(); if closed { process.stop() } }
    private func send(_ value: [String: Any]) { guard let data = try? JSONSerialization.data(withJSONObject: value) else { return }; child?.write(data + Data([10])) }
    private func complete(_ outcome: Outcome) { lock.lock(); finishLocked(outcome); lock.unlock() }
    private func finishLocked(_ outcome: Outcome) {
        guard result == nil else { return }
        result = outcome; let pending = continuation; continuation = nil
        pending?.resume(returning: outcome)
    }
    /// The end of what the CLI wrote to standard error, for a failure's reason.
    private func consumeError(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        errors.append(data)
        if errors.count > 8_192 { errors = Data(errors.suffix(8_192)) }
    }
    /// The CLI ended before it answered (its output was read to the end before this is called).
    private func exited(_ code: Int32) {
        lock.lock(); defer { lock.unlock() }
        finishLocked(.exited(code, String(decoding: errors, as: UTF8.self)))
    }
    private func consume(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        guard result == nil else { return }
        bytes += data.count
        guard bytes <= 1024 * 1024 else { finishLocked(.tooMuch); return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 10) {
            let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
            guard let event = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
            if provider == "claude" {
                guard event["type"] as? String == "control_response", let response = event["response"] as? [String: Any], response["request_id"] as? String == requestId else { continue }
                guard response["subtype"] as? String == "success" else { finishLocked(.refused(response["error"] as? String)); return }
                guard let payload = response["response"] as? [String: Any], let models = payload["models"] as? [[String: Any]] else { finishLocked(.noModels); return }
                let catalog = ProviderService.normalizeClaudeCatalog(models)
                finishLocked(catalog.isFresh ? .listed(catalog) : .noModels); return
            }
            guard event["id"] as? Int == expected else { continue }
            if let error = event["error"] { finishLocked(.refused((error as? [String: Any])?["message"] as? String)); return }
            if expected == 1 {
                send(["method": "initialized"]); expected += 1
                send(["id": expected, "method": "model/list", "params": ["limit": 64, "includeHidden": false]])
            } else {
                guard let result = event["result"] as? [String: Any], let data = result["data"] as? [[String: Any]] else { finishLocked(.noModels); return }
                rows += data.prefix(max(0, 128 - rows.count))
                if let cursor = result["nextCursor"] as? String, !cursor.isEmpty, cursor.count < 4096, expected < 5, rows.count < 128 {
                    expected += 1; send(["id": expected, "method": "model/list", "params": ["limit": 64, "includeHidden": false, "cursor": cursor]])
                } else {
                    let catalog = ProviderService.normalizeCodexCatalog(rows)
                    finishLocked(catalog.isFresh ? .listed(catalog) : .noModels); return
                }
            }
        }
    }
    private func wait() async -> Outcome {
        await withCheckedContinuation { continuation in
            lock.lock()
            if let result { lock.unlock(); continuation.resume(returning: result) }
            else { self.continuation = continuation; lock.unlock() }
        }
    }
    /// The outcome in words, in the caller's task (where a test's language override applies).
    private func worded(_ outcome: Outcome) -> ModelListRead {
        let problem: String, output: String
        switch outcome {
        case .listed(let catalog): return .listed(catalog)
        case .exited(let code, let errors): problem = L("provider.catalog.failure.exit", ["code": "\(code)"]); output = errors
        case .refused(let message): problem = message.flatMap { $0.isEmpty ? nil : $0 } ?? L("provider.catalog.failure.noModels"); output = ""
        case .noModels: problem = L("provider.catalog.failure.noModels"); output = ""
        case .tooMuch: problem = L("process.error.outputLimit"); output = ""
        case .timedOut: problem = L("process.error.timeout"); output = ""
        case .cancelled: problem = CancellationError().localizedDescription; output = ""
        }
        return .failed(ProviderService.readFailure(provider, problem: problem, output: output))
    }
    static func read(command: ProviderCommand, environment: [String: String], workspace: URL?) async -> ModelListRead {
        let state = CLIModelProbe(provider: command.provider)
        return await withTaskCancellationHandler(operation: {
            do {
                try Task.checkCancellation()
                let claude = command.provider == "claude"
                guard let arguments = ProviderService.modelListArguments(command.provider) else { return state.worded(.noModels) }
                var env = environment
                if claude { env.merge(ProviderService.quietClaudeEnvironment) { _, new in new } }
                let child = try NativeChildProcess(executable: command.executable, arguments: arguments, environment: env, cwd: workspace ?? FileManager.default.temporaryDirectory,
                                                   stdout: { state.consume($0) }, stderr: { state.consumeError($0) }, exited: { state.exited($0) })
                state.attach(child)
                if claude { state.send(["type": "control_request", "request_id": state.requestId, "request": ["subtype": "initialize", "hooks": [:], "sdkMcpServers": []]]) }
                else { state.send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "mighty_claude_native", "title": "Mighty Code", "version": "0.1.0"]]]) }
                let timeout = Task { do { try await Task.sleep(nanoseconds: UInt64(answerTimeout * 1_000_000_000)); state.complete(.timedOut) } catch { } }
                let outcome = await state.wait(); timeout.cancel(); child.stop(); _ = await child.wait(timeout: 2)
                return state.worded(outcome)
            } catch { return .failed(ProviderService.readFailure(command.provider, problem: error.localizedDescription)) }
        }, onCancel: { state.complete(.cancelled) })
    }
}
