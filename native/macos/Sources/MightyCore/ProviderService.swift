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
    public init(binaryOverrides: [String: URL] = [:], environment: [String: String]? = nil, environmentResolver: CLIEnvironmentResolver = .shared) { self.binaryOverrides = binaryOverrides; self.fixedEnvironment = environment; self.environmentResolver = environmentResolver }

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
            let override = binaryOverrides[provider], env = snapshot.values
            let task = Task<ProviderCommand?, Never> {
                let paths = override.map { [$0] } ?? (env["PATH"] ?? "").split(separator: ":").prefix(64).map { URL(fileURLWithPath: String($0)).appendingPathComponent(provider) }
                for path in paths {
                    if Task.isCancelled { return nil }
                    guard FileManager.default.isExecutableFile(atPath: path.path) else { continue }
                    if let result = try? await ProcessCapture.run(executable: path, arguments: ["--version"], environment: env, timeout: 4, maximumBytes: 16_384), result.exitCode == 0 {
                        let version = String(String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines).prefix(160))
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
        guard !closing, provider == "codex" || provider == "claude" else { return ProviderOptions.fallbackCatalog(provider) }
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
            let generation = cacheGeneration
            let task = Task<ModelCatalog, Never> {
                guard let command = await self.command(provider: provider, workspacePath: workspacePath, snapshot: snapshot), !Task.isCancelled,
                      !self.closing, generation == self.cacheGeneration else { return ProviderOptions.fallbackCatalog(provider) }
                return await CLIModelProbe.read(command: command, environment: snapshot.values, workspace: cwd)
            }
            entry = CatalogEntry(id: UUID(), created: Date(), task: task, inFlight: true, forced: forceRefresh, environment: snapshot.values)
            catalogs[key] = entry
        }
        let value = await entry.task.value
        guard !closing, catalogs[key]?.id == entry.id else { return ProviderOptions.fallbackCatalog(provider) }
        catalogs[key]?.inFlight = false
        return value
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
            runtime.detail = runtime.available ? "설치된 CLI의 로그인·제공자 설정으로 실행합니다." : "이 앱의 Mods 연결은 2.1.271 공개 타입을 기준으로 합니다. 현재 \(command.version)에서는 Claude 실행을 지원하지 않습니다."
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

    public nonisolated static func arguments(_ request: StartRunRequest, pluginDirectory: URL, allowPermissionPrompts: Bool = false, phaseModels: PhaseModelConfig = PhaseModelConfig()) throws -> [String] {
        try CoreValidation.validate(request)
        let s = request.settings
        switch request.provider {
        case "claude":
            let mode = s.permissionMode == "fullAccess" ? "bypassPermissions" : s.permissionMode
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
            return args
        case "codex":
            let asks = s.permissionMode == "onRequest"
            guard !asks || allowPermissionPrompts else { throw MightyError("Codex 승인 요청은 로컬 앱의 승인 연결이 필요합니다.") }
            let sandbox = s.permissionMode == "fullAccess" ? "danger-full-access" : ["acceptEdits", "onRequest"].contains(s.permissionMode) ? "workspace-write" : "read-only"
            var args = ["-c", "approval_policy=\"\(asks ? "on-request" : "never")\"", "-c", "sandbox_mode=\"\(sandbox)\"", "-c", "sandbox_workspace_write.network_access=\(s.networkAccess)", "-c", "features.fast_mode=\(s.fastMode)", "-c", "service_tier=\"\(s.fastMode ? "fast" : "default")\""]
            if s.webSearch != "default" { args += ["-c", "web_search=\"\(s.webSearch)\""] }
            // Phase model knobs: review_model, agents.default_subagent_model, plan_mode_reasoning_effort.
            if phaseModels.codexReviewModel != "default" { args += ["-c", "review_model=\"\(phaseModels.codexReviewModel)\""] }
            if phaseModels.codexSubagentDefault != "default" { args += ["-c", "agents.default_subagent_model=\"\(phaseModels.codexSubagentDefault)\""] }
            if phaseModels.codexPlanModeReasoningEffort != "default" { args += ["-c", "plan_mode_reasoning_effort=\"\(phaseModels.codexPlanModeReasoningEffort)\""] }
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
            let mode = ["manual": "default", "plan": "plan", "acceptEdits": "auto_edit", "fullAccess": "yolo"][s.permissionMode]!
            var args = ["--output-format", "stream-json", "--approval-mode", mode]
            if request.model != "default" { args += ["--model", request.model] }
            if let resume = request.resumeId { args += ["--resume", resume] }
            return args
        default: throw MightyError("지원하지 않는 CLI입니다.")
        }
    }

    public nonisolated static func normalizeCodexCatalog(_ rows: [[String: Any]]) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("codex")
        var models = [fallback.models[0]]; var seen = Set(["default"])
        for row in rows.prefix(128) {
            guard let value = row["model"] as? String, CoreValidation.model(value), row["hidden"] as? Bool != true, seen.insert(value).inserted else { continue }
            let levels = (row["supportedReasoningEfforts"] as? [[String: Any]])?.compactMap { $0["reasoningEffort"] as? String }.filter { ProviderOptions.efforts.contains($0) }
            models.append(ModelOption(value: value, displayName: String((row["displayName"] as? String ?? value).prefix(160)), description: String((row["description"] as? String ?? "").prefix(2400)), supportsEffort: levels.map { !$0.isEmpty }, supportedEffortLevels: levels))
            if row["isDefault"] as? Bool == true {
                models[0].resolvedModel = value
                models[0].supportsEffort = levels.map { !$0.isEmpty }
                models[0].supportedEffortLevels = levels
            }
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: "설치된 Codex의 model/list 응답입니다. 실제 사용 가능 여부에는 계정·제공자 정책이 적용됩니다.") : fallback
    }
    public nonisolated static func normalizeClaudeCatalog(_ rows: [[String: Any]]) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("claude")
        var models = [fallback.models[0]]; var seen = Set<String>()
        for row in rows.prefix(128) {
            guard let value = row["value"] as? String, CoreValidation.model(value), seen.insert(value).inserted else { continue }
            let haiku = value.lowercased().contains("haiku")
            let levels = (row["supportedEffortLevels"] as? [String])?.filter { ProviderOptions.efforts.contains($0) }
            let option = ModelOption(value: value, displayName: String((row["displayName"] as? String ?? value).prefix(160)), description: String((row["description"] as? String ?? "").prefix(2400)), resolvedModel: (row["resolvedModel"] as? String).flatMap { CoreValidation.model($0) ? $0 : nil }, supportsEffort: haiku ? false : row["supportsEffort"] as? Bool, supportedEffortLevels: haiku ? [] : levels)
            if value == "default" {
                models[0].resolvedModel = option.resolvedModel
                models[0].supportsEffort = option.supportsEffort
                models[0].supportedEffortLevels = option.supportedEffortLevels
            } else { models.append(option) }
        }
        return !seen.isEmpty ? ModelCatalog(source: "cli", models: models, detail: "설치된 Claude Code의 초기화 응답입니다. 실제 사용 가능 여부에는 계정·제공자 정책이 적용됩니다.") : fallback
    }
}

private final class CLIModelProbe: @unchecked Sendable {
    private let provider: String
    private let requestId = UUID().uuidString
    private let lock = NSLock()
    private var child: NativeChildProcess?
    private var buffer = Data()
    private var bytes = 0
    private var expected = 1
    private var rows: [[String: Any]] = []
    private var result: ModelCatalog?
    private var finished = false
    private var continuation: CheckedContinuation<ModelCatalog?, Never>?
    private init(provider: String) { self.provider = provider }

    private func attach(_ process: NativeChildProcess) { lock.lock(); child = process; let closed = finished; lock.unlock(); if closed { process.stop() } }
    private func send(_ value: [String: Any]) { guard let data = try? JSONSerialization.data(withJSONObject: value) else { return }; child?.write(data + Data([10])) }
    private func complete(_ result: ModelCatalog?) { lock.lock(); finishLocked(result); lock.unlock() }
    private func finishLocked(_ value: ModelCatalog?) {
        guard !finished else { return }
        finished = true; result = value; let pending = continuation; continuation = nil
        pending?.resume(returning: value)
    }
    private func consume(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return }
        bytes += data.count
        guard bytes <= 1024 * 1024 else { finishLocked(nil); return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 10) {
            let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
            guard let event = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
            if provider == "claude" {
                guard event["type"] as? String == "control_response", let response = event["response"] as? [String: Any], response["request_id"] as? String == requestId else { continue }
                guard response["subtype"] as? String == "success", let payload = response["response"] as? [String: Any], let models = payload["models"] as? [[String: Any]] else { finishLocked(nil); return }
                finishLocked(ProviderService.normalizeClaudeCatalog(models)); return
            }
            guard event["id"] as? Int == expected else { continue }
            if event["error"] != nil { finishLocked(nil); return }
            if expected == 1 {
                send(["method": "initialized"]); expected += 1
                send(["id": expected, "method": "model/list", "params": ["limit": 64, "includeHidden": false]])
            } else {
                guard let result = event["result"] as? [String: Any], let data = result["data"] as? [[String: Any]] else { finishLocked(nil); return }
                rows += data.prefix(max(0, 128 - rows.count))
                if let cursor = result["nextCursor"] as? String, !cursor.isEmpty, cursor.count < 4096, expected < 5, rows.count < 128 {
                    expected += 1; send(["id": expected, "method": "model/list", "params": ["limit": 64, "includeHidden": false, "cursor": cursor]])
                } else { finishLocked(ProviderService.normalizeCodexCatalog(rows)); return }
            }
        }
    }
    private func wait() async -> ModelCatalog? {
        await withCheckedContinuation { continuation in
            lock.lock()
            if finished { let result = result; lock.unlock(); continuation.resume(returning: result) }
            else { self.continuation = continuation; lock.unlock() }
        }
    }
    static func read(command: ProviderCommand, environment: [String: String], workspace: URL?) async -> ModelCatalog {
        let state = CLIModelProbe(provider: command.provider)
        return await withTaskCancellationHandler(operation: {
            do {
                try Task.checkCancellation()
                let claude = command.provider == "claude"
                let arguments = claude ? ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--safe-mode"] : ["app-server", "--listen", "stdio://"]
                var env = environment
                if claude { env.merge(["CLAUDE_CODE_SAFE_MODE": "1", "CLAUDE_CODE_DISABLE_TERMINAL_TITLE": "1", "CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL": "1", "CLAUDE_CODE_DISABLE_BACKGROUND_TASKS": "1", "DISABLE_AUTOUPDATER": "1", "DISABLE_TELEMETRY": "1", "DISABLE_ERROR_REPORTING": "1", "CLAUDE_CODE_SKIP_PROMPT_HISTORY": "1"]) { _, new in new } }
                let child = try NativeChildProcess(executable: command.executable, arguments: arguments, environment: env, cwd: workspace ?? FileManager.default.temporaryDirectory, stdout: { state.consume($0) }, stderr: { _ in }, exited: { _ in state.complete(nil) })
                state.attach(child)
                if claude { state.send(["type": "control_request", "request_id": state.requestId, "request": ["subtype": "initialize", "hooks": [:], "sdkMcpServers": []]]) }
                else { state.send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "mighty_claude_native", "title": "MightyClaude", "version": "0.1.0"]]]) }
                let timeout = Task { do { try await Task.sleep(nanoseconds: 6_000_000_000); state.complete(nil) } catch { } }
                let result = await state.wait(); timeout.cancel(); child.stop(); _ = await child.wait(timeout: 2)
                return result ?? ProviderOptions.fallbackCatalog(command.provider)
            } catch { return ProviderOptions.fallbackCatalog(command.provider) }
        }, onCancel: { state.complete(nil) })
    }
}
