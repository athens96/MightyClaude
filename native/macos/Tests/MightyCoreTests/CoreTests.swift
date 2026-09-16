import Testing
import Foundation
import Darwin
@testable import MightyCore

private final class EventRecorder: @unchecked Sendable {
    private let lock = NSLock(); private var events: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); events.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return events }
    func hasStatus(_ status: String, id: String) -> Bool { values().contains { $0.sessionId == id && $0.status == status } }
}

@Suite(.serialized)
final class CoreTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }
    private func temporary() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-native-core-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); directories.append(url); return url
    }
    private func script(_ source: String, name: String = "fixture") throws -> URL {
        let file = try temporary().appendingPathComponent(name)
        try Data(("#!/bin/sh\n" + source).utf8).write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)
        return file
    }
    private func waitUntil(timeout: TimeInterval = 5, _ condition: () -> Bool) async throws {
        let start = Date()
        while !condition() {
            if Date().timeIntervalSince(start) > timeout { Issue.record("Timed out waiting for native event"); return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }
    private func jsonLines(_ objects: [[String: Any]]) throws -> Data {
        var data = Data()
        for object in objects { data.append(try JSONSerialization.data(withJSONObject: object)); data.append(10) }
        return data
    }

    @Test func testArgumentBoundariesAndProviderSettings() throws {
        let plugin = URL(fileURLWithPath: "/Applications/Mighty Claude.app/Contents/Resources/mods/mighty-bridge")
        var request = StartRunRequest(sessionId: "run", workspaceId: "workspace", input: "--yolo $(echo secret)", model: "sonnet", settings: RunSettings(effort: "high", permissionMode: "plan", maxTurns: 5, maxBudgetUsd: 1.25), resumeId: "resume-id")
        let claude = try ProviderService.arguments(request, pluginDirectory: plugin)
        #expect(claude.contains("--plugin-dir")); #expect(claude.contains("--permission-prompts")); #expect(claude.contains("none")); #expect(!(claude.contains(request.input)))
        let settingsIndex = try #require(claude.firstIndex(of: "--settings"))
        let settings = try JSONSerialization.jsonObject(with: Data(claude[settingsIndex + 1].utf8)) as? [String: [String: String]]
        #expect((settings?["env"]?["CLAUDE_CODE_EFFORT_LEVEL"]) == ("high"))
        request.provider = "codex"; request.model = "company/model@2026"; request.settings = RunSettings(effort: "high", permissionMode: "acceptEdits")
        let codex = try ProviderService.arguments(request, pluginDirectory: plugin)
        #expect(codex.contains("sandbox_mode=\"workspace-write\"")); #expect(codex.contains("approval_policy=\"never\"")); #expect(codex.contains("resume")); #expect((codex.last) == ("-"))
        request.provider = "gemini"; request.settings = RunSettings(permissionMode: "plan")
        let gemini = try ProviderService.arguments(request, pluginDirectory: plugin)
        #expect((gemini) == (["--output-format", "stream-json", "--approval-mode", "plan", "--model", "company/model@2026", "--resume", "resume-id"]))
        for args in [claude, codex, gemini] { #expect(!(args.contains(request.input))); #expect(!(args.contains("--yolo"))); #expect(!(args.contains("--dangerously-skip-permissions"))) }
        request.model = "--model injected"; #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
        request.model = "default"; request.settings.effort = "high"; #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
        request.provider = "codex"; request.settings = RunSettings(permissionMode: "plan"); #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
        #expect(!(ProviderService.supportsMods("2.1.263 (Claude Code)"))); #expect(ProviderService.supportsMods("2.1.271 (Claude Code)")); #expect(!(ProviderService.supportsMods("2.1.271-preview")))
    }

    @Test func testCatalogAndEffortCapabilities() throws {
        let wire = try JSONSerialization.jsonObject(with: JSONEncoder().encode(RunSettings())) as? [String: Any]
        #expect(wire?["maxTurns"] is NSNull); #expect(wire?["maxBudgetUsd"] is NSNull)
        let catalog = ProviderService.normalizeCodexCatalog([["model": "company/model", "displayName": "Company", "supportedReasoningEfforts": [["reasoningEffort": "low"], ["reasoningEffort": "high"]]], ["model": "--injected"], ["model": "hidden", "hidden": true]])
        #expect((catalog.models.map(\.value)) == (["default", "company/model"]))
        #expect((ProviderOptions.effortLevels(provider: "codex", model: "default", catalog: catalog)) == ([]))
        #expect((ProviderOptions.effortLevels(provider: "codex", model: "company/model", catalog: catalog)) == (["low", "high"]))
        let valid = StartRunRequest(sessionId: "run", workspaceId: "workspace", input: "Hello", model: "company/model", provider: "codex", settings: RunSettings(effort: "high"))
        try CoreValidation.validateSelection(valid, catalog: catalog)
        var invalid = valid; invalid.settings.effort = "max"; #expect(throws: (any Error).self) { try CoreValidation.validateSelection(invalid, catalog: catalog) }
        let claude = ProviderService.normalizeClaudeCatalog([["value": "default", "displayName": "Account default", "supportedEffortLevels": ["high"]], ["value": "company/claude", "supportsEffort": true, "supportedEffortLevels": ["low", "max"]]])
        #expect((claude.models.first?.displayName) == ("Claude 설정 따름")); #expect((claude.models.first?.supportedEffortLevels) == nil)
    }

    @Test func testExecutionSettingsLegacyCodingAndPersistence() async throws {
        let legacy = Data(#"{"effort":"high","permissionMode":"acceptEdits","maxTurns":null,"maxBudgetUsd":null}"#.utf8)
        let decoded = try JSONDecoder().decode(RunSettings.self, from: legacy)
        #expect(decoded == RunSettings(effort: "high", permissionMode: "acceptEdits"))
        let defaultWire = try JSONSerialization.jsonObject(with: JSONEncoder().encode(decoded)) as? [String: Any]
        #expect(defaultWire?["fastMode"] == nil); #expect(defaultWire?["webSearch"] == nil); #expect(defaultWire?["networkAccess"] == nil)
        let selected = RunSettings(effort: "high", permissionMode: "acceptEdits", fastMode: true, webSearch: "live", networkAccess: true)
        #expect(try JSONDecoder().decode(RunSettings.self, from: JSONEncoder().encode(selected)) == selected)
        let directory = try temporary(); let repository = StateRepository(directory: directory, legacyStateURL: nil)
        _ = try await repository.load()
        let workspace = try await repository.approveWorkspace(Workspace(name: "Settings", path: directory.path))
        let session = RunSession(workspaceId: workspace.id, title: "Codex", provider: "codex", settings: selected)
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [session]))
        let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
        #expect(restored.sessions.first?.settings == selected)
    }

    @Test func testExplicitPermissionFastSearchAndNetworkArguments() throws {
        let plugin = URL(fileURLWithPath: "/tmp/plugin")
        var request = StartRunRequest(sessionId: "run", workspaceId: "workspace", input: "argument verification only", provider: "codex")
        let defaults = try ProviderService.arguments(request, pluginDirectory: plugin)
        #expect(defaults.contains("sandbox_mode=\"read-only\""))
        #expect(defaults.contains("features.fast_mode=false")); #expect(defaults.contains("service_tier=\"default\""))
        #expect(defaults.contains("sandbox_workspace_write.network_access=false"))
        #expect(!defaults.contains(where: { $0.hasPrefix("web_search=") }))
        request.settings = RunSettings(permissionMode: "acceptEdits", fastMode: true, webSearch: "live", networkAccess: true)
        for resume in [nil, "resume-id"] as [String?] {
            request.resumeId = resume
            let args = try ProviderService.arguments(request, pluginDirectory: plugin)
            #expect(args.contains("sandbox_mode=\"workspace-write\"")); #expect(args.contains("approval_policy=\"never\""))
            #expect(args.contains("features.fast_mode=true")); #expect(args.contains("service_tier=\"fast\""))
            #expect(args.contains("web_search=\"live\"")); #expect(args.contains("sandbox_workspace_write.network_access=true"))
        }
        for mode in ["disabled", "cached", "live"] {
            request.settings.webSearch = mode
            #expect(try ProviderService.arguments(request, pluginDirectory: plugin).contains("web_search=\"\(mode)\""))
        }
        request.settings = RunSettings(permissionMode: "fullAccess")
        #expect(try ProviderService.arguments(request, pluginDirectory: plugin).contains("sandbox_mode=\"danger-full-access\""))
        request.provider = "claude"
        #expect(try ProviderService.arguments(request, pluginDirectory: plugin).contains("bypassPermissions"))
        request.provider = "gemini"
        #expect(try ProviderService.arguments(request, pluginDirectory: plugin).contains("yolo"))
    }

    @Test func testUnsupportedOptionsAndLegacyRemoteCapabilitiesFailClosed() throws {
        let legacyJSON = Data(#"{"effort":true,"permissionModes":["manual","acceptEdits"],"maxTurns":false,"maxBudgetUsd":false,"resume":true}"#.utf8)
        let legacy = try JSONDecoder().decode(ProviderCapabilities.self, from: legacyJSON)
        #expect(!legacy.fastMode && !legacy.webSearch && !legacy.networkAccess)
        var request = StartRunRequest(sessionId: "run", workspaceId: "workspace", input: "validation only", provider: "codex")
        try CoreValidation.validateCapabilities(request, capabilities: legacy)
        let newSelections = [RunSettings(fastMode: true), RunSettings(webSearch: "disabled"), RunSettings(permissionMode: "acceptEdits", networkAccess: true), RunSettings(permissionMode: "fullAccess")]
        for setting in newSelections {
            request.settings = setting
            try CoreValidation.validate(request)
            #expect(throws: (any Error).self) { try CoreValidation.validateCapabilities(request, capabilities: legacy) }
            try CoreValidation.validateCapabilities(request, capabilities: ProviderOptions.fallbackRuntime("codex").capabilities)
        }
        for provider in ["claude", "gemini"] {
            request.provider = provider
            for setting in newSelections.prefix(3) {
                request.settings = setting
                #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
            }
        }
        request.provider = "codex"
        for permission in ["manual", "fullAccess"] {
            request.settings = RunSettings(permissionMode: permission, networkAccess: true)
            #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
            #expect(!ProviderOptions.normalizedSettings(provider: "codex", settings: request.settings).networkAccess)
        }
        request.settings = RunSettings(webSearch: "live\" --inject")
        #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
        request.kind = "shell"; request.settings = RunSettings(fastMode: true)
        #expect(throws: (any Error).self) { try CoreValidation.validate(request) }
    }

    @Test func testLegacyImportIsAtomicBoundedAndLeavesOriginalUntouched() async throws {
        let root = try temporary(); let legacy = root.appendingPathComponent("electron-state.json"); let native = root.appendingPathComponent("native")
        let fixture: [String: Any] = ["version": 1, "workspaces": [["id": "workspace", "name": "Legacy", "path": root.path, "createdAt": mightyTimestamp()]], "sessions": [["id": "legacy-pane", "workspaceId": "workspace", "title": "Legacy", "kind": "claude", "model": "sonnet", "status": "running", "logs": [], "createdAt": mightyTimestamp()]], "layout": "grid", "theme": "dark", "sidebarWidth": 252]
        let original = try JSONSerialization.data(withJSONObject: fixture); try original.write(to: legacy)
        let repository = StateRepository(directory: native, legacyStateURL: legacy)
        var state = try await repository.load()
        #expect((state.sessions.first?.provider) == ("claude")); #expect((state.sessions.first?.settings) == (RunSettings())); #expect((state.sessions.first?.status) == ("stopped"))
        #expect((try Data(contentsOf: legacy)) == (original))
        state.sessions[0].title = "Native saved"; try await repository.save(state)
        let fresh = StateRepository(directory: native, legacyStateURL: legacy); let restored = try await fresh.load()
        #expect((restored.sessions.first?.title) == ("Native saved")); #expect((try Data(contentsOf: legacy)) == (original))
        state.workspaces[0].path = "/unapproved"
        do { try await repository.save(state); Issue.record("Unapproved path accepted") } catch { }
        let permissions = try FileManager.default.attributesOfItem(atPath: native.appendingPathComponent("workspace-state.json").path)[.posixPermissions] as? NSNumber
        #expect((permissions?.intValue) == (0o600))
    }

    @Test func testRemoteWindowsPathsAndLogAttributionSurviveStateNormalization() {
        let reference = RemoteWorkspaceReference(connectionId: "connection", workspaceId: "peer-workspace", hostName: "Windows")
        let remote = Workspace(id: "remote", name: "Remote", path: "C:\\Work\\app", remote: reference)
        let invalid = Workspace(id: "local-invalid", name: "Invalid", path: "C:\\Work\\app")
        let session = RunSession(workspaceId: remote.id, title: "Gemini", provider: "gemini", settings: RunSettings(effort: "high", maxTurns: 3), status: "running", logs: [LogEntry(kind: "assistant", text: "Old Codex answer", provider: "codex")])
        let state = StateRepository.normalize(AppSnapshot(workspaces: [remote, invalid], sessions: [session]), restoring: true)
        #expect((state.workspaces) == ([remote])); #expect((state.sessions[0].status) == ("stopped")); #expect((state.sessions[0].settings) == (RunSettings())); #expect((state.sessions[0].logs.first?.provider) == ("codex"))
    }

    @Test func testDamagedNativeStateIsNeverOverwrittenByAnEmptySave() async throws {
        let directory = try temporary(); let file = directory.appendingPathComponent("workspace-state.json")
        let damaged = Data("{broken JSON".utf8); try damaged.write(to: file)
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        do { _ = try await repository.load(); Issue.record("Damaged state was accepted") } catch { }
        do { try await repository.save(AppSnapshot()); Issue.record("Damaged state was overwritten") } catch { }
        #expect(try Data(contentsOf: file) == damaged)
    }

    @Test func testClaudeAndCodexParsersPreserveSplitUTF8AndSharedMessageBlocks() throws {
        var logs: [(String, String)] = []; var resumes: [String] = []
        let claude = CLIStreamParser(provider: "claude", log: { logs.append(($0, $1)) }, resume: { resumes.append($0) })
        let records = try jsonLines([
            ["type": "system", "session_id": "claude-resume"],
            ["type": "assistant", "message": ["id": "a", "content": [["type": "text", "text": "계획"]]]],
            ["type": "assistant", "message": ["id": "b", "content": [["type": "thinking", "thinking": "private"]]]],
            ["type": "assistant", "uuid": "final", "message": ["id": "b", "content": [["type": "text", "text": "한글 응답"]]]],
            ["type": "assistant", "uuid": "final", "message": ["id": "b", "content": [["type": "text", "text": "한글 응답"]]]],
        ])
        for byte in records { claude.push(Data([byte])) }; claude.flush()
        #expect((logs.map { $0.1 }) == (["계획", "한글 응답"])); #expect((resumes) == (["claude-resume"]))
        logs = []; resumes = []
        let codex = CLIStreamParser(provider: "codex", log: { logs.append(($0, $1)) }, resume: { resumes.append($0) })
        codex.push(try jsonLines([["type": "thread.started", "thread_id": "codex-resume"], ["type": "item.completed", "item": ["id": "a", "type": "reasoning", "text": "private"]], ["type": "item.completed", "item": ["id": "b", "type": "agent_message", "text": "완료"]], ["type": "turn.failed", "error": ["message": "Login required"]]])); codex.flush()
        #expect((logs.map { $0.1 }) == (["완료", "Login required"])); #expect(codex.failed); #expect((resumes) == (["codex-resume"]))
    }

    @Test func testGeminiParserGroupsDeltasAndBoundsMalformedOutput() throws {
        var logs: [String] = []
        let parser = CLIStreamParser(provider: "gemini", log: { logs.append($1) }, resume: { _ in })
        parser.push(try jsonLines([["type": "message", "role": "user", "content": "secret prompt"], ["type": "message", "role": "assistant", "delta": true, "content": "안녕"], ["type": "message", "role": "assistant", "delta": true, "content": "하세요"], ["type": "error", "severity": "warning", "message": "Retrying"], ["type": "result", "status": "success"]])); parser.flush()
        #expect((logs) == (["안녕하세요", "Retrying"])); #expect(!(parser.failed))
        parser.push(String(repeating: "x", count: 1024 * 1024 + 1)); parser.push("\nplain warning\n{\"type\":\"result\",\"status\":\"error\",\"error\":{\"message\":\"Quota\"}}\n"); parser.flush()
        #expect(parser.failed); #expect(logs.contains("너무 긴 출력 한 줄을 생략했습니다.")); #expect((logs.last) == ("Quota"))
    }

    @Test func testProcessCaptureHandlesStdinTimeoutAndOutputLimits() async throws {
        let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/bin/cat"), arguments: [], input: Data("한국어 stdin\n".utf8))
        #expect((result.exitCode) == (0)); #expect((String(decoding: result.stdout, as: UTF8.self)) == ("한국어 stdin\n"))
        let start = Date()
        do { _ = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", "sleep 30"], timeout: 0.15); Issue.record("Timeout not enforced") } catch { }
        #expect((Date().timeIntervalSince(start)) < (3))
        do { _ = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/yes"), arguments: [], timeout: 3, maximumBytes: 1024); Issue.record("Output cap not enforced") } catch { }
    }

    @Test func testProcessGroupCleansBackgroundChildWhenParentExits() async throws {
        let directory = try temporary(); let pidFile = directory.appendingPathComponent("child.pid")
        let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", "sleep 30 & echo $! > child.pid; printf PARENT_DONE"], cwd: directory, timeout: 3)
        #expect((String(decoding: result.stdout, as: UTF8.self)) == ("PARENT_DONE"))
        let pid = try #require(Int32(String(contentsOf: pidFile).trimmingCharacters(in: .whitespacesAndNewlines)))
        try await waitUntil { Darwin.kill(pid, 0) != 0 }
    }

    @Test func testShellRunnerRunsStopsAndKeepsEventsIsolated() async throws {
        let directory = try temporary(); let events = EventRecorder(); let service = ProviderService()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        try await runner.start(request: StartRunRequest(sessionId: "first", workspaceId: workspace.id, kind: "shell", input: "printf '첫 번째'"), workspace: workspace)
        try await runner.start(request: StartRunRequest(sessionId: "second", workspaceId: workspace.id, kind: "shell", input: "printf READY; sleep 30"), workspace: workspace)
        try await waitUntil { events.hasStatus("completed", id: "first") && events.values().contains { $0.sessionId == "second" && $0.entry?.text == "READY" } }
        await runner.stop(id: "second")
        #expect(events.hasStatus("stopped", id: "second")); #expect(events.values().contains { $0.sessionId == "first" && $0.entry?.text == "첫 번째" })
        await runner.shutdown(); await service.shutdown()
    }

    private func fakeCLI(_ provider: String) throws -> URL {
        let version = provider == "claude" ? "2.1.271 (Claude Code)" : "1.0.0"
        return try script("""
        if [ "$1" = "--version" ]; then printf '%s\\n' '\(version)'; exit 0; fi
        folder="$(/usr/bin/dirname "$0")"
        case " $* " in
          *' --input-format '* )
            IFS= read -r request
            printf '%s\\n' "$request" > "$folder/metadata"
            id="$(printf '%s' "$request" | /usr/bin/sed -E 's/.*"request_id"[ ]*:[ ]*"([^"]+)".*/\\1/')"
            printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[{"value":"sonnet","displayName":"Sonnet","supportsEffort":true,"supportedEffortLevels":["low","high"]}]}}}\\n' "$id"
            /bin/cat > /dev/null; exit 0;;
          *' app-server '* )
            IFS= read -r request; printf '%s\\n' "$request" > "$folder/metadata"
            printf '{"id":1,"result":{}}\\n'
            IFS= read -r notification; IFS= read -r request
            printf '%s\\n%s\\n' "$notification" "$request" >> "$folder/metadata"
            printf '{"id":2,"result":{"data":[{"model":"company/model","displayName":"Company","supportedReasoningEfforts":[{"reasoningEffort":"high"}]}],"nextCursor":null}}\\n'
            /bin/cat > /dev/null; exit 0;;
        esac
        printf '%s\\n' "$@" > "$folder/args"
        /bin/cat > "$folder/input"
        case '\(provider)' in
          claude) printf '{"type":"assistant","session_id":"claude-session","message":{"id":"m","content":[{"type":"text","text":"Claude answer"}]}}\\n';;
          codex) printf '{"type":"thread.started","thread_id":"codex-session"}\\n{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"Codex answer"}}\\n';;
          gemini) printf '{"type":"init","session_id":"gemini-session"}\\n{"type":"message","role":"assistant","delta":true,"content":"Gemini answer"}\\n{"type":"result","status":"success"}\\n';;
        esac
        """, name: provider)
    }

    @Test func testThreeProviderFixturesUseOnlyMetadataBeforeExplicitPromptAndResumeFromOutput() async throws {
        let directory = try temporary(); let events = EventRecorder()
        var binaries: [String: URL] = [:]
        for provider in ProviderOptions.ids { binaries[provider] = try fakeCLI(provider) }
        let service = ProviderService(binaryOverrides: binaries)
        let info = await service.runtimeInfo()
        #expect(info.providers?.allSatisfy(\.available) == true)
        #expect((info.modelCatalog?.source) == ("cli")); #expect((info.providers?.first { $0.id == "codex" }?.modelCatalog.source) == ("cli"))
        for provider in ["claude", "codex"] {
            let metadata = try String(contentsOf: binaries[provider]!.deletingLastPathComponent().appendingPathComponent("metadata"))
            #expect(!(metadata.contains("\"type\":\"user\""))); #expect(!(metadata.contains("turn/start"))); #expect(!(metadata.contains("thread/start")))
            #expect(!(FileManager.default.fileExists(atPath: binaries[provider]!.deletingLastPathComponent().appendingPathComponent("input").path)))
        }
        let plugin = directory.appendingPathComponent("plugin/.claude-plugin")
        try FileManager.default.createDirectory(at: plugin, withIntermediateDirectories: true); try Data("{}".utf8).write(to: plugin.appendingPathComponent("plugin.json"))
        let runner = ProcessRunner(providerService: service, pluginDirectory: plugin.deletingLastPathComponent(), onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        for provider in ProviderOptions.ids {
            try await runner.start(request: StartRunRequest(sessionId: provider, workspaceId: workspace.id, input: "한글 prompt", provider: provider), workspace: workspace)
        }
        try await waitUntil(timeout: 8) { ProviderOptions.ids.allSatisfy { events.hasStatus("completed", id: $0) } }
        for provider in ProviderOptions.ids {
            #expect(events.values().contains { $0.sessionId == provider && $0.resumeId == provider + "-session" })
            #expect(events.values().contains { $0.sessionId == provider && $0.entry?.kind == "assistant" && $0.entry?.provider == provider })
            let input = try String(contentsOf: binaries[provider]!.deletingLastPathComponent().appendingPathComponent("input")); #expect((input) == ("한글 prompt"))
            let args = try String(contentsOf: binaries[provider]!.deletingLastPathComponent().appendingPathComponent("args")); #expect(!(args.contains("한글 prompt"))); #expect(!(args.contains("--yolo")))
        }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func testCancelPendingDiscoveryDoesNotLaunchLaterOrOverwriteReplacement() async throws {
        let binary = try script("if [ \"$1\" = \"--version\" ]; then sleep 0.4; printf '1.0.0\\n'; exit 0; fi\nprintf BAD_LAUNCH\n", name: "gemini")
        let directory = try temporary(); let events = EventRecorder(); let service = ProviderService(binaryOverrides: ["gemini": binary])
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        let request = StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "Hello", provider: "gemini")
        let pending = Task { try await runner.start(request: request, workspace: workspace) }
        try await Task.sleep(nanoseconds: 50_000_000); await runner.stop(id: "pane")
        try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, kind: "shell", input: "printf REPLACEMENT"), workspace: workspace)
        try await pending.value; try await waitUntil { events.hasStatus("completed", id: "pane") }
        #expect(!(events.values().contains { $0.entry?.text.contains("BAD_LAUNCH") == true }))
        #expect((events.values().filter { $0.status == "stopped" }.count) == (1))
        await runner.shutdown(); await service.shutdown()
    }

    @Test func testConcurrentClaudeStopAndShutdownFinishBeforeReturning() async throws {
        let directory = try temporary(); let binary = try fakeCLI("claude")
        let service = ProviderService(binaryOverrides: ["claude": binary])
        _ = await service.modelCatalog(provider: "claude")
        let plugin = directory.appendingPathComponent("plugin/.claude-plugin")
        try FileManager.default.createDirectory(at: plugin, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent("plugin.json"))
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        for index in 0..<8 {
            let id = "pane-\(index)"; let events = EventRecorder()
            let runner = ProcessRunner(providerService: service, pluginDirectory: plugin.deletingLastPathComponent(), onEvent: { events.append($0) })
            try await runner.start(request: StartRunRequest(sessionId: id, workspaceId: workspace.id, input: "Fixture prompt"), workspace: workspace)
            let stopping = Task { await runner.stop(id: id) }
            await runner.shutdown()
            #expect(events.values().filter { $0.type == "status" && ["completed", "stopped", "error"].contains($0.status ?? "") }.count == 1)
            let finalEvents = events.values(); await stopping.value
            try await Task.sleep(nanoseconds: 20_000_000)
            #expect(events.values() == finalEvents)
        }
        await service.shutdown()
    }

    @Test func testModBridgeRejectsUnauthenticatedBrowserAndSecretPayloads() async throws {
        let bridge = try ModBridge(onEvent: { _ in })
        let env = try await bridge.start(); let url = try #require(URL(string: env["MIGHTY_CLAUDE_BRIDGE_URL"]!))
        func post(token: String?, origin: String? = nil, secret: Bool = false) async throws -> Int {
            var request = URLRequest(url: url); request.httpMethod = "POST"; request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            if let token { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }; if let origin { request.setValue(origin, forHTTPHeaderField: "Origin") }
            var body: [String: Any] = ["version": 1, "runId": env["MIGHTY_CLAUDE_RUN_ID"]!, "claudeSessionId": "claude-id", "event": "turn.start", "turnId": "turn-id"]
            if secret { body["prompt"] = "must not be transported" }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (_, response) = try await URLSession.shared.data(for: request); return (response as! HTTPURLResponse).statusCode
        }
        let unauthorized = try await post(token: nil); #expect((unauthorized) == (401))
        let origin = try await post(token: env["MIGHTY_CLAUDE_BRIDGE_TOKEN"], origin: "https://example.com"); #expect((origin) == (403))
        let secret = try await post(token: env["MIGHTY_CLAUDE_BRIDGE_TOKEN"], secret: true); #expect((secret) == (400))
        let valid = try await post(token: env["MIGHTY_CLAUDE_BRIDGE_TOKEN"]); #expect((valid) == (200))
        let count = await bridge.receivedCount; #expect((count) == (1)); await bridge.stop()
    }
}
