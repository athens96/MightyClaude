import Foundation

public struct ProviderCommand: Sendable, Equatable {
    public var provider: String
    public var executable: URL
    public var version: String
    public init(provider: String, executable: URL, version: String) { self.provider = provider; self.executable = executable; self.version = version }
}

public actor ProviderService {
    private let binaryOverrides: [String: URL]
    private let environment: [String: String]
    private var commands: [String: (Date, Task<ProviderCommand?, Never>)] = [:]
    private var catalogs: [String: (Date, Task<ModelCatalog, Never>)] = [:]
    private var cacheGeneration = 0
    private var closing = false
    public init(binaryOverrides: [String: URL] = [:], environment: [String: String]? = nil) { self.binaryOverrides = binaryOverrides; self.environment = environment ?? Self.runtimeEnvironment() }

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

    public nonisolated static func capabilities(provider: String, version: String?) -> ProviderCapabilities {
        var value = ProviderOptions.fallbackRuntime(provider).capabilities
        // Keep the app's existing Mods minimum. This advertises CLI syntax
        // support, not account/model/admin eligibility for the classifier.
        if provider == "claude", let version, supportsMods(version) {
            value.permissionModes = ProviderOptions.permissionModes(provider: provider)
        }
        return value
    }

    public func command(provider: String) async -> ProviderCommand? {
        guard !closing, ProviderOptions.ids.contains(provider) else { return nil }
        let generation = cacheGeneration
        if let cached = commands[provider], Date().timeIntervalSince(cached.0) < 60 {
            let value = await cached.1.value
            return !closing && generation == cacheGeneration ? value : nil
        }
        let override = binaryOverrides[provider]; let env = environment
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
        commands[provider] = (Date(), task)
        let value = await task.value
        return !closing && generation == cacheGeneration ? value : nil
    }

    public func modelCatalog(provider: String) async -> ModelCatalog {
        guard !closing else { return ProviderOptions.fallbackCatalog(provider) }
        guard provider == "codex" || provider == "claude" else { return ProviderOptions.fallbackCatalog(provider) }
        let generation = cacheGeneration
        if let cached = catalogs[provider], Date().timeIntervalSince(cached.0) < 60 {
            let value = await cached.1.value
            return !closing && generation == cacheGeneration ? value : ProviderOptions.fallbackCatalog(provider)
        }
        guard let command = await command(provider: provider), !closing, generation == cacheGeneration else { return ProviderOptions.fallbackCatalog(provider) }
        if let cached = catalogs[provider], Date().timeIntervalSince(cached.0) < 60 {
            let value = await cached.1.value
            return !closing && generation == cacheGeneration ? value : ProviderOptions.fallbackCatalog(provider)
        }
        let env = environment
        let task = Task<ModelCatalog, Never> { await CLIModelProbe.read(command: command, environment: env) }
        catalogs[provider] = (Date(), task)
        let value = await task.value
        return !closing && generation == cacheGeneration ? value : ProviderOptions.fallbackCatalog(provider)
    }

    public func runtimeInfo(appVersion: String = "0.1.0") async -> RuntimeInfo {
        var providers: [ProviderRuntime] = []
        var claudeInstalled = false; var claudeVersion: String?
        for id in ProviderOptions.ids {
            var runtime = ProviderOptions.fallbackRuntime(id)
            if let command = await command(provider: id) {
                runtime.version = command.version
                runtime.capabilities = Self.capabilities(provider: id, version: command.version)
                runtime.available = id != "claude" || Self.supportsMods(command.version)
                runtime.detail = runtime.available ? "설치된 CLI의 로그인·제공자 설정으로 실행합니다." : "이 앱의 Mods 연결은 2.1.271 공개 타입을 기준으로 합니다. 현재 \(command.version)에서는 Claude 실행을 지원하지 않습니다."
                if id == "claude" { claudeInstalled = true; claudeVersion = command.version }
            }
            runtime.modelCatalog = await modelCatalog(provider: id)
            providers.append(runtime)
        }
        return RuntimeInfo(appVersion: appVersion, claudeAvailable: claudeInstalled, claudeVersion: claudeVersion, modelCatalog: providers.first?.modelCatalog, providers: providers, mods: ModsRuntime(status: !claudeInstalled ? "unavailable" : providers.first?.available == true ? "available" : "unsupported", detail: providers.first?.detail ?? ""))
    }
    public func invalidateCaches() async {
        cacheGeneration &+= 1
        let running = commands.values.map { $0.1 }; let metadata = catalogs.values.map { $0.1 }
        commands.removeAll(); catalogs.removeAll()
        running.forEach { $0.cancel() }; metadata.forEach { $0.cancel() }
        for task in running { _ = await task.value }; for task in metadata { _ = await task.value }
    }
    public func shutdown() async { closing = true; await invalidateCaches() }

    public nonisolated static func arguments(_ request: StartRunRequest, pluginDirectory: URL, allowPermissionPrompts: Bool = false) throws -> [String] {
        try CoreValidation.validate(request)
        let s = request.settings
        switch request.provider {
        case "claude":
            let mode = s.permissionMode == "fullAccess" ? "bypassPermissions" : s.permissionMode
            var args = ["--print", "--verbose", "--output-format", "stream-json", "--permission-prompts", allowPermissionPrompts ? "host" : "none", "--plugin-dir", pluginDirectory.path, "--permission-mode", mode]
            if allowPermissionPrompts { args += ["--input-format", "stream-json", "--permission-prompt-tool", "stdio"] }
            if request.model != "default" { args += ["--model", request.model] }
            if s.effort != "default" {
                let data = try JSONSerialization.data(withJSONObject: ["env": ["CLAUDE_CODE_EFFORT_LEVEL": s.effort]], options: [.sortedKeys])
                args += ["--effort", s.effort, "--settings", String(decoding: data, as: UTF8.self)]
            }
            if let turns = s.maxTurns { args += ["--max-turns", String(turns)] }
            if let budget = s.maxBudgetUsd { args += ["--max-budget-usd", String(budget)] }
            if let resume = request.resumeId { args += ["--resume", resume] }
            return args
        case "codex":
            let sandbox = s.permissionMode == "fullAccess" ? "danger-full-access" : s.permissionMode == "acceptEdits" ? "workspace-write" : "read-only"
            var args = ["-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"\(sandbox)\"", "-c", "sandbox_workspace_write.network_access=\(s.networkAccess)", "-c", "features.fast_mode=\(s.fastMode)", "-c", "service_tier=\"\(s.fastMode ? "fast" : "default")\""]
            if s.webSearch != "default" { args += ["-c", "web_search=\"\(s.webSearch)\""] }
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
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: "설치된 Codex의 model/list 응답입니다. 실제 사용 가능 여부에는 계정·제공자 정책이 적용됩니다.") : fallback
    }
    public nonisolated static func normalizeClaudeCatalog(_ rows: [[String: Any]]) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("claude")
        var models = [fallback.models[0]]; var seen = Set(["default"])
        for row in rows.prefix(128) {
            guard let value = row["value"] as? String, CoreValidation.model(value), seen.insert(value).inserted else { continue }
            let haiku = value.lowercased().contains("haiku")
            let levels = (row["supportedEffortLevels"] as? [String])?.filter { ProviderOptions.efforts.contains($0) }
            models.append(ModelOption(value: value, displayName: String((row["displayName"] as? String ?? value).prefix(160)), description: String((row["description"] as? String ?? "").prefix(2400)), resolvedModel: (row["resolvedModel"] as? String).flatMap { CoreValidation.model($0) ? $0 : nil }, supportsEffort: haiku ? false : row["supportsEffort"] as? Bool, supportedEffortLevels: haiku ? [] : levels))
        }
        return models.count > 1 ? ModelCatalog(source: "cli", models: models, detail: "설치된 Claude Code의 초기화 응답입니다. 실제 사용 가능 여부에는 계정·제공자 정책이 적용됩니다.") : fallback
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
    static func read(command: ProviderCommand, environment: [String: String]) async -> ModelCatalog {
        let state = CLIModelProbe(provider: command.provider)
        return await withTaskCancellationHandler(operation: {
            do {
                try Task.checkCancellation()
                let claude = command.provider == "claude"
                let arguments = claude ? ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--safe-mode"] : ["app-server", "--listen", "stdio://"]
                var env = environment
                if claude { env.merge(["CLAUDE_CODE_SAFE_MODE": "1", "CLAUDE_CODE_DISABLE_TERMINAL_TITLE": "1", "CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL": "1", "CLAUDE_CODE_DISABLE_BACKGROUND_TASKS": "1", "DISABLE_AUTOUPDATER": "1", "DISABLE_TELEMETRY": "1", "DISABLE_ERROR_REPORTING": "1", "CLAUDE_CODE_SKIP_PROMPT_HISTORY": "1"]) { _, new in new } }
                let child = try NativeChildProcess(executable: command.executable, arguments: arguments, environment: env, cwd: FileManager.default.temporaryDirectory, stdout: { state.consume($0) }, stderr: { _ in }, exited: { _ in state.complete(nil) })
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
