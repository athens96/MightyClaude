import Foundation
import SQLite3
import Testing
@testable import MightyCore

private final class OpencodeRecorder<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func append(_ item: T) { lock.lock(); items.append(item); lock.unlock() }
    func values() -> [T] { lock.lock(); defer { lock.unlock() }; return items }
}

/// opencode (`opencode acp`) as the fifth agent, Mac only: the ACP frames in
/// native/contracts/fixtures/opencode-acp-*.jsonl (written by hand from opencode 1.19.5's
/// source, src/acp/*), the channel's requests and answers, the approval card, usage,
/// permissions as opencode's rules, the model list, the sign-in reading, the Mac-only setup
/// item and the session list read from `opencode.db`.
struct OpencodeTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static let sessionID = "ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw"

    static func fixture(_ name: String) throws -> [Data] {
        let data = try Data(contentsOf: root.appendingPathComponent("native/contracts/fixtures/opencode-acp-\(name).jsonl"))
        return data.split(separator: 10).map { Data($0) }
    }

    /// What a channel and its parser did with a fixture.
    final class Replay {
        var logs: [(kind: String, text: String)] = []
        var activities: [AgentActivity] = []
        var resumes: [String] = []
        var usage: [SessionUsage] = []
        var graph: [ExecutionGraphNode] = []
        var writes: [[String: Any]] = []
        var permissions: [ToolPermissionRequest] = []
        var warnings: [String] = []
        var failures: [String] = []
        var completed = 0
        var parser: CLIStreamParser!
        var channel: ACPChannel!
        var methods: [String] { writes.compactMap { $0["method"] as? String } }
    }

    static func channel(_ request: StartRunRequest) throws -> Replay {
        let result = Replay()
        result.parser = CLIStreamParser(provider: "opencode", log: { result.logs.append(($0, $1)) }, resume: { result.resumes.append($0) }, activityNamespace: "run-opencode",
                                        activity: { result.activities.append($0) }, usage: { result.usage.append($0) }, graph: { result.graph.append($0) }, graphInput: request.input)
        result.channel = ACPChannel(runId: "run-opencode", request: request, workspacePath: "/tmp/project", attachments: try AttachmentPreparation([]),
                                    configValues: OpencodeAgent.configValues(request),
                                    write: { data in result.writes.append((try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]) },
                                    event: { result.parser.receive(object: $0) },
                                    emit: { result.permissions.append($0) }, activity: { result.parser.permissionActivity($0, state: $1) },
                                    warning: { result.warnings.append($0) }, fail: { result.failures.append($0) }, completed: { result.completed += 1 })
        return result
    }
    static func replay(_ name: String, request: StartRunRequest = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi there", provider: "opencode"),
                       answer: ((Replay, Int) throws -> Void)? = nil) throws -> Replay {
        let run = try channel(request)
        run.channel.start()
        for (index, line) in try fixture(name).enumerated() {
            var data = line; data.append(10)
            run.channel.receive(data)
            try answer?(run, index)
        }
        run.parser.flush(); run.parser.finishActivities(stopped: false); run.parser.finishGraph(state: "completed")
        return run
    }

    // MARK: The channel's requests

    @Test func aNewSessionIsInitializeThenSessionNewThenOnePromptAndTheCurrentModeIsNotSetAgain() throws {
        let run = try Self.replay("answer")
        #expect(run.methods == ["initialize", "session/new", "session/prompt"])
        #expect(run.writes.allSatisfy { $0["jsonrpc"] as? String == "2.0" })
        let initialize = try #require(run.writes.first?["params"] as? [String: Any])
        #expect(initialize["protocolVersion"] as? Int == 1)
        #expect((initialize["clientCapabilities"] as? [String: Any])?["terminal"] as? Bool == false)
        let new = try #require(run.writes[1]["params"] as? [String: Any])
        #expect(new["cwd"] as? String == "/tmp/project" && (new["mcpServers"] as? [Any])?.isEmpty == true)
        let prompt = try #require(run.writes[2]["params"] as? [String: Any])
        #expect(prompt["sessionId"] as? String == Self.sessionID)
        #expect(((prompt["prompt"] as? [[String: Any]])?.first)?["text"] as? String == "hi there")
        #expect(run.channel.turnCompleted && !run.channel.failed && run.completed == 1 && run.failures.isEmpty)
    }

    @Test func settingsGoAsConfigOptionsAndOneTheAgentLacksIsNamedAndSkipped() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "plan it", model: "anthropic/claude-sonnet-4-5", provider: "opencode",
                                      settings: RunSettings(effort: "high", permissionMode: "plan"))
        request.attachments = []
        let run = try Self.channel(request)
        run.channel.start()
        let lines = try Self.fixture("answer")
        run.channel.receive(lines[0] + Data([10])); run.channel.receive(lines[1] + Data([10]))
        // model first; the fixture's session has no effort option; mode differs from build.
        #expect(run.methods == ["initialize", "session/new", "session/set_config_option"])
        #expect((run.writes[2]["params"] as? [String: Any])?["configId"] as? String == "model")
        #expect((run.writes[2]["params"] as? [String: Any])?["value"] as? String == "anthropic/claude-sonnet-4-5")
        #expect(run.warnings.isEmpty)
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-3","result":{}}"#.utf8) + Data([10]))
        #expect(run.warnings.count == 1 && run.warnings[0].contains("effort"))
        #expect((run.writes[3]["params"] as? [String: Any])?["configId"] as? String == "mode" && (run.writes[3]["params"] as? [String: Any])?["value"] as? String == "plan")
        // A setting the agent refuses is named; the request still goes.
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-4","error":{"code":-32602,"message":"Invalid value"}}"#.utf8) + Data([10]))
        #expect(run.methods.last == "session/prompt" && run.warnings.count == 2)
    }

    @Test func aMissingSessionToResumeStartsANewOneWithANotice() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "go on", provider: "opencode")
        request.resumeId = "ses_0000000000000000000000dead"
        let run = try Self.replay("resume-missing", request: request)
        #expect(run.methods == ["initialize", "session/resume", "session/new", "session/prompt"])
        #expect((run.writes[1]["params"] as? [String: Any])?["sessionId"] as? String == "ses_0000000000000000000000dead")
        #expect(run.warnings == [L("acp.channel.newSession")])
        #expect(run.resumes == [Self.sessionID] && run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Starting fresh."])
        #expect(run.channel.turnCompleted && !run.parser.failed)
    }

    // MARK: Updates to the log, the graph and usage

    @Test func streamedTextIsLoggedOnceAndThoughtsAreNotCopied() throws {
        let run = try Self.replay("answer")
        #expect(run.logs.map(\.kind) == ["assistant"])
        #expect(run.logs.first?.text == "Hello! How can I help with this project?")
        #expect(!run.logs.contains { $0.text.contains("greets") })
        #expect(run.resumes == [Self.sessionID] && !run.parser.failed)
        let main = try #require(run.graph.last { $0.kind == "main" })
        #expect(main.state == "completed" && main.output == "Hello! How can I help with this project?")
        #expect(main.usage == GraphTokenUsage(inputTokens: 1203, outputTokens: 18))
    }

    @Test func usageIsTheSessionsCostTheContextAndTheLastCallsTokens() throws {
        let run = try Self.replay("answer")
        let last = try #require(run.usage.last)
        #expect(last.provider == "opencode" && last.source == "opencode.acp" && last.providerSessionId == Self.sessionID)
        #expect(last.model == "opencode/big-pickle")
        #expect(last.costUSD == 0.0123 && last.costScope == "session")
        #expect(last.contextUsedTokens == 12034 && last.contextWindowTokens == 200000)
        #expect(last.inputTokens == 1203 && last.outputTokens == 18 && last.totalTokens == 1221 && last.tokenScope == "response")
        // The ledger counts the run once, and only what the session's cost grew next time.
        var ledger = UsageLedger()
        let recorded = ledger.record(paneId: "pane", workspaceId: "w", provider: "opencode", model: "default", usage: last)
        let first = try #require(recorded)
        #expect(first.costUSD == 0.0123 && first.tokens == 1221)
        var next = last; next.costUSD = 0.05
        #expect(abs((ledger.record(paneId: "pane", workspaceId: "w", provider: "opencode", model: "default", usage: next)?.costUSD ?? 0) - 0.0377) < 1e-9)
        // Cached input is inside input, as for the others.
        let tools = try Self.replay("tools", answer: { run, _ in if let ask = run.permissions.last, ask.state == "pending" { try run.channel.respond(requestId: ask.id, allow: true) } })
        #expect(tools.usage.last?.inputTokens == 5148 && tools.usage.last?.cacheReadTokens == 2048 && tools.usage.last?.costUSD == 0.0411)
    }

    @Test func toolsAreRowsAndAnAskIsTheApprovalCardAnsweredOnce() throws {
        let run = try Self.replay("tools", answer: { run, _ in
            if let ask = run.permissions.last, ask.state == "pending" { try run.channel.respond(requestId: ask.id, allow: true) }
        })
        #expect(run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Let me run the tests.", "One test fails because main returns 1."])
        let read = run.activities.filter { $0.toolName == "Read" }
        #expect(read.last?.state == "completed" && read.last?.kind == "read" && read.last?.summary == "src/main.ts")
        #expect(read.last?.output?.contains("export function main()") == true)
        let bash = run.activities.filter { $0.toolName == "Bash" }
        #expect(bash.map(\.state).contains("waiting") && bash.last?.state == "completed" && bash.last?.kind == "command" && bash.last?.output == "1 failing")
        let edit = run.activities.filter { $0.toolName == "Edit" }
        #expect(edit.last?.state == "error" && edit.last?.output?.contains("rejected") == true)
        // The card: the command, allowed once with opencode's own option id.
        let card = try #require(run.permissions.first)
        #expect(card.toolName == "Bash" && card.toolUseId == "toolu_02BashB" && card.summary == "npm test" && card.canAllow)
        #expect(card.inputJSON.contains("\"command\" : \"npm test\""))
        #expect(ToolPermissionPresentation.make(toolName: card.toolName, inputJSON: card.inputJSON).primaryCode?.value == "npm test")
        #expect(run.permissions.map(\.state) == ["pending", "allowed"])
        let answer = try #require(run.writes.first { $0["result"] != nil })
        #expect(answer["id"] as? Int == 0)
        #expect(((answer["result"] as? [String: Any])?["outcome"] as? [String: Any]).map { $0["outcome"] as? String == "selected" && $0["optionId"] as? String == "once" } == true)
        #expect(throws: (any Error).self) { try run.channel.respond(requestId: card.id, allow: true) }
        #expect(!run.parser.failed && run.channel.turnCompleted)
    }

    @Test func aDeniedAskAnswersRejectAndAnUnansweredOneIsCancelledWhenTheCallSettles() throws {
        let denied = try Self.replay("tools", answer: { run, _ in
            if let ask = run.permissions.last, ask.state == "pending" { try run.channel.respond(requestId: ask.id, allow: false) }
        })
        let answer = try #require(denied.writes.first { $0["result"] != nil })
        #expect(((answer["result"] as? [String: Any])?["outcome"] as? [String: Any])?["optionId"] as? String == "reject")
        let ignored = try Self.replay("tools")
        #expect(ignored.permissions.map(\.state) == ["pending", "cancelled"])
    }

    @Test func aSignedOutRunFailsAsALostSignIn() throws {
        let run = try Self.replay("auth")
        #expect(run.parser.failed && run.parser.authFailure && run.channel.failed && run.completed == 1)
        #expect(run.logs.contains { $0.kind == "error" && $0.text.contains("API key is missing") })
        #expect(CLIAuthFailure.opencode(text: "ProviderAuthError: Anthropic API key is missing"))
        #expect(CLIAuthFailure.opencode(text: "AI_APICallError: 401 Unauthorized"))
        #expect(CLIAuthFailure.opencode(text: "Run `opencode auth login` to add a provider"))
        #expect(!CLIAuthFailure.opencode(text: "Model not found: anthropic/claude-x"))
        #expect(!CLIAuthFailure.opencode(text: "407 Proxy Authentication Required"))
    }

    @Test func anAgentRequestThisClientDoesNotOfferIsRefused() throws {
        let run = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "opencode"))
        run.channel.start()
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"path":"/etc/hosts"}}"#.utf8) + Data([10]))
        #expect((run.writes.last?["error"] as? [String: Any])?["code"] as? Int == -32601)
        // EOF before the answer is a failure.
        run.channel.flush()
        #expect(run.channel.failed && run.failures == [L("acp.channel.closedEarly")])
    }

    // MARK: Permissions, models, sign-in

    @Test func permissionModesAreOpencodesRulesForTheRun() throws {
        #expect(ProviderOptions.permissionModes(provider: "opencode") == ["manual", "plan", "acceptEdits", "fullAccess"])
        #expect(OpencodeAgent.permissionRules(mode: "manual")["edit"] == "ask" && OpencodeAgent.permissionRules(mode: "manual")["bash"] == "ask")
        #expect(OpencodeAgent.permissionRules(mode: "acceptEdits")["edit"] == "allow" && OpencodeAgent.permissionRules(mode: "acceptEdits")["bash"] == "ask")
        // Plan keeps the plan agent's own edit denial: no edit rule over it.
        #expect(OpencodeAgent.permissionRules(mode: "plan")["edit"] == nil)
        #expect(OpencodeAgent.permissionRules(mode: "fullAccess")["*"] == "allow" && OpencodeAgent.permissionRules(mode: "fullAccess")["external_directory"] == "allow")
        // The question tool cannot be answered from a pane.
        for mode in ["manual", "plan", "acceptEdits", "fullAccess"] { #expect(OpencodeAgent.permissionRules(mode: mode)["question"] == "deny") }
        let json = try #require(try JSONSerialization.jsonObject(with: Data(OpencodeAgent.permissionEnvironment(mode: "manual").utf8)) as? [String: String])
        #expect(json == OpencodeAgent.permissionRules(mode: "manual"))
        #expect(OpencodeAgent.configValues(StartRunRequest(sessionId: "p", workspaceId: "w", input: "x", provider: "opencode")).isEmpty)
        #expect(OpencodeAgent.configValues(StartRunRequest(sessionId: "p", workspaceId: "w", input: "x", provider: "opencode", settings: RunSettings(permissionMode: "plan"))).map(\.id) == ["mode"])
        #expect(try ProviderService.arguments(StartRunRequest(sessionId: "p", workspaceId: "w", input: "x", provider: "opencode"), pluginDirectory: URL(fileURLWithPath: "/tmp")) == ["acp"])
        #expect(ProviderOptions.asksInPane(provider: "opencode", mode: "manual") && !ProviderOptions.asksInPane(provider: "pi", mode: "manual"))
        #expect(ProviderOptions.effortLevels(provider: "opencode", model: "default", registeredModels: []) == [])
        #expect(ProviderOptions.effortLevels(provider: "opencode", model: "anthropic/claude-sonnet-4-5", registeredModels: []) == ["low", "medium", "high", "max"])
    }

    @Test func theModelListIsProviderSlashModelRows() {
        let text = "Models cache refreshed\nopencode/big-pickle\nopencode/grok-code\nanthropic/claude-sonnet-4-5\nnot a model\n{\n  \"id\": \"x\"\n}\nopenrouter/qwen/qwen3-coder\n"
        let catalog = ProviderService.normalizeOpencodeCatalog(text)
        #expect(catalog.models.map(\.value) == ["default", "opencode/big-pickle", "opencode/grok-code", "anthropic/claude-sonnet-4-5", "openrouter/qwen/qwen3-coder"])
        #expect(catalog.source == "cli")
        #expect(ProviderService.normalizeOpencodeCatalog("").models.map(\.value) == ["default"])
    }

    @Test func signInIsAuthJsonOrAnEnvironmentKeyAndOtherwiseZensFreeModelsOnly() throws {
        let oauth = Data(#"{"anthropic":{"type":"oauth","refresh":"r","access":"a","expires":1},"openai":{"type":"api","key":"sk-secret"}}"#.utf8)
        let signedIn = CLIAccountSupport.opencodeStatus(exitCode: 0, authJSON: oauth, environment: [:])
        #expect(signedIn.loggedIn == true && signedIn.methodId == CLIAccountMethod.opencodeSubscription && signedIn.account == "anthropic, openai" && !signedIn.canSignOut)
        // A credential is never shown.
        #expect(![signedIn.account, signedIn.detail, signedIn.method].compactMap { $0 }.joined().contains("sk-secret"))
        #expect(CLIAccountSupport.opencodeStatus(exitCode: 0, authJSON: nil, environment: ["OPENROUTER_API_KEY": "x"]).loggedIn == true)
        let free = CLIAccountSupport.opencodeStatus(exitCode: 0, authJSON: Data("{}".utf8), environment: [:])
        #expect(free.loggedIn == false && free.detail == L("settings.cliAccounts.detailOpencodeFreeOnly"))
        #expect(CLIAccountSupport.opencodeStatus(exitCode: -1, authJSON: oauth, environment: [:]).loggedIn == nil)
        #expect(CLIAuthFailure.signInCanFix(free) && !CLIAuthFailure.signInCanFix(signedIn))
        let home = URL(fileURLWithPath: "/Users/someone")
        #expect(OpencodeAgent.authFile(home: home, environment: [:]).path == "/Users/someone/.local/share/opencode/auth.json")
        #expect(OpencodeAgent.authFile(home: home, environment: ["XDG_DATA_HOME": "/data"]).path == "/data/opencode/auth.json")
        #expect(OpencodeAgent.authJSON(home: home, environment: ["OPENCODE_AUTH_CONTENT": "{\"x\":{\"type\":\"api\",\"key\":\"k\"}}"]).map { OpencodeAgent.credentials(authJSON: $0).providers } == ["x"])
        #expect(CLIAccountSupport.loginCommand(provider: "opencode") == "opencode auth login")
        #expect(CLIAccountSupport.statusArguments(provider: "opencode") == ["opencode", "auth", "list"])
        #expect(CLITerminalSignIn.table["opencode"]?.command == "opencode auth login")
    }

    @Test func theAccountServiceReadsTheHomesAuthFile() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let bin = directory.appendingPathComponent("bin"); try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let home = directory.appendingPathComponent("home"); try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let script = bin.appendingPathComponent("opencode")
        try Data("#!/bin/sh\necho 'Credentials ~/.local/share/opencode/auth.json' >&2\nexit 0\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        let service = CLIAccountService(environment: ["PATH": bin.path + ":/usr/bin:/bin"], home: home)
        let signedOut = await service.status(provider: "opencode")
        #expect(signedOut.loggedIn == false, "\(signedOut)")
        let folder = home.appendingPathComponent(".local/share/opencode"); try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(#"{"github-copilot":{"type":"oauth","refresh":"r","access":"a","expires":1}}"#.utf8).write(to: folder.appendingPathComponent("auth.json"))
        let status = await service.status(provider: "opencode")
        #expect(status.installed && status.loggedIn == true && status.account == "github-copilot")
    }

    // MARK: Setup

    @Test func theMacOnlySetupItemInstallsOpencodeWithNpmAfterNodeAndLeavesTheContractAlone() throws {
        let shared = try Data(contentsOf: Self.root.appendingPathComponent("native/contracts/fixtures/setup-catalog.json"))
        let macOnly = try Data(contentsOf: Self.root.appendingPathComponent("native/macos/Sources/MightyCore/Resources/Setup/setup-catalog-macos.json"))
        // Windows embeds the contract: opencode is not in it yet.
        #expect(try SetupCatalogDecoder.decode(shared).item("opencode") == nil)
        let catalog = try SetupCatalogDecoder.decode(shared, macOnly: macOnly)
        let item = try #require(catalog.item("opencode"))
        #expect(item.group == .agent && !item.required && item.provider == "opencode" && item.platforms[.windows] == nil)
        let entry = try #require(item.platforms[.macos])
        #expect(entry.probe == .executable(name: "opencode", minimumVersion: nil) && entry.install == .npmGlobal("opencode-ai") && entry.prerequisites == ["node"])
        // After pi, among the agents.
        let agents = catalog.items.filter { $0.group == .agent }.map(\.id)
        #expect(agents.last == "opencode" && agents.contains("pi"))
        #expect(SetupPlan.order(selected: ["opencode"], catalog: catalog, platform: .macos, isSatisfied: { $0 == "git" }) == ["homebrew", "node", "opencode"])
        #expect(try SetupCatalog.bundled().item("opencode") != nil)
        #expect(ComponentCatalog.installCommand(provider: "opencode") == "npm install -g opencode-ai")
        // A Mac-only item names the Mac alone.
        let both = String(decoding: macOnly, as: UTF8.self).replacingOccurrences(of: "\"macos\": {", with: "\"windows\": { \"probe\": { \"kind\": \"executable\", \"name\": \"opencode\" }, \"install\": { \"kind\": \"npmGlobal\", \"package\": \"opencode-ai\" }, \"prerequisites\": [\"node\"], \"source\": \"https://github.com/anomalyco/opencode\" }, \"macos\": {")
        #expect(throws: (any Error).self) { try SetupCatalogDecoder.decode(shared, macOnly: Data(both.utf8)) }
    }

    // MARK: Session list

    @Test func rootSessionsOfTheWorkspaceFolderAreListedFromTheDatabase() throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let workspace = directory.appendingPathComponent("project"); try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let home = directory.appendingPathComponent("home")
        let data = home.appendingPathComponent(".local/share/opencode"); try FileManager.default.createDirectory(at: data, withIntermediateDirectories: true)
        let path = workspace.resolvingSymlinksInPath().path
        let now = Date()
        let ms = Int64(now.timeIntervalSince1970 * 1_000)
        try sqlite(data.appendingPathComponent("opencode.db"), [
            "CREATE TABLE session (id TEXT PRIMARY KEY, project_id TEXT, parent_id TEXT, slug TEXT, directory TEXT, title TEXT, version TEXT, time_created INTEGER, time_updated INTEGER, time_archived INTEGER)",
            "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT)",
            "CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT, session_id TEXT, data TEXT)",
            "INSERT INTO session VALUES ('\(Self.sessionID)', 'p', NULL, 's', '\(path)', 'Fix the failing test', '1.19.5', \(ms - 9000), \(ms - 1000), NULL)",
            "INSERT INTO session VALUES ('ses_8a0000000000Ab0000000000Cd', 'p', NULL, 's', '\(path)', 'New session - 2026-11-10T10:00:00.000Z', '1.19.5', \(ms - 8000), \(ms - 2000), NULL)",
            "INSERT INTO session VALUES ('ses_7a0000000000Ab0000000000Ce', 'p', '\(Self.sessionID)', 's', '\(path)', 'Child session', '1.19.5', \(ms - 8000), \(ms - 500), NULL)",
            "INSERT INTO session VALUES ('ses_6a0000000000Ab0000000000Cf', 'p', NULL, 's', '/elsewhere', 'Other folder', '1.19.5', \(ms - 8000), \(ms - 500), NULL)",
            "INSERT INTO session VALUES ('ses_5a0000000000Ab0000000000Cg', 'p', NULL, 's', '\(path)', 'Archived', '1.19.5', \(ms - 8000), \(ms - 500), \(ms))",
            "INSERT INTO message VALUES ('msg_1', '\(Self.sessionID)', 1, '{\"role\":\"user\"}')",
            "INSERT INTO message VALUES ('msg_2', '\(Self.sessionID)', 2, '{\"role\":\"assistant\",\"providerID\":\"anthropic\",\"modelID\":\"claude-sonnet-4-5\"}')",
            "INSERT INTO message VALUES ('msg_3', '\(Self.sessionID)', 3, '{\"role\":\"user\"}')",
            "INSERT INTO part VALUES ('prt_1', 'msg_1', '\(Self.sessionID)', '{\"type\":\"text\",\"text\":\"why does main return 1?\"}')",
            "INSERT INTO message VALUES ('msg_4', 'ses_8a0000000000Ab0000000000Cd', 1, '{\"role\":\"user\"}')",
            "INSERT INTO part VALUES ('prt_2', 'msg_4', 'ses_8a0000000000Ab0000000000Cd', '{\"type\":\"text\",\"text\":\"hello\"}')",
        ])
        let listing = ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, now: now), provider: "opencode")
        #expect(listing.items.map(\.sessionID) == [Self.sessionID, "ses_8a0000000000Ab0000000000Cd"])
        let first = try #require(listing.items.first)
        #expect(first.title == "why does main return 1?" && first.requests == 2 && first.model == "anthropic/claude-sonnet-4-5" && first.provider == "opencode")
        #expect(listing.items.last?.title == "hello")
        // An open pane's session is left out.
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, excluding: [Self.sessionID], now: now), provider: "opencode").items.count == 1)
        #expect(ResumableSessions.providers.contains("opencode"))
        #expect(CoreValidation.sessionIdentifier(Self.sessionID) && !CoreValidation.sessionIdentifier("ses_../x"))
    }

    // MARK: A run

    private func temporary() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-opencode-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
    private func sqlite(_ url: URL, _ statements: [String]) throws {
        var handle: OpaquePointer?
        guard sqlite3_open(url.path, &handle) == SQLITE_OK, let db = handle else { throw MightyError("sqlite open") }
        defer { sqlite3_close(db) }
        for statement in statements { guard sqlite3_exec(db, statement, nil, nil, nil) == SQLITE_OK else { throw MightyError("sqlite: " + statement) } }
    }
    /// A stand-in `opencode`: answers each of the channel's requests with the fixture's next
    /// response, and stays up after the prompt's answer when `linger` (as an ACP server may).
    private func fakeOpencode(_ directory: URL, fixture: String, linger: Bool) throws -> URL {
        let binary = directory.appendingPathComponent("opencode")
        let fixtureFile = Self.root.appendingPathComponent("native/contracts/fixtures/opencode-acp-\(fixture).jsonl").path
        let source = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '1.19.5\\n'; exit 0; fi
        if [ "$1" = "models" ]; then printf 'opencode/big-pickle\\n'; exit 0; fi
        printf '%s\\n' "$@" > argv.txt
        printf '%s\\n' "$OPENCODE_PERMISSION" > permission.txt
        exec 3<&0
        while IFS= read -r frame; do
          case "$frame" in
            *'"id":"mighty-'*) IFS= read -r request <&3; printf '%s\\n' "$request" >> stdin.txt ;;
          esac
          printf '%s\\n' "$frame"
        done < '\(fixtureFile)'
        \(linger ? "sleep 60" : "cat <&3 > /dev/null")
        """
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        return binary
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Timed out waiting for the opencode fixture") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    @Test(arguments: [false, true]) func aRunSpeaksACPAndCompletesWhetherTheServerExitsOrIsEnded(linger: Bool) async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakeOpencode(directory, fixture: "answer", linger: linger)
        let service = ProviderService(binaryOverrides: ["opencode": binary])
        let events = OpencodeRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi there", provider: "opencode"), workspace: workspace)
            try await wait { events.values().contains { $0.status == "completed" || $0.status == "error" } }
            let all = events.values()
            #expect(all.last { $0.type == "status" }?.status == "completed")
            #expect(all.contains { $0.type == "log" && $0.entry?.kind == "assistant" && $0.entry?.text == "Hello! How can I help with this project?" })
            #expect(all.contains { $0.type == "resume" && $0.resumeId == Self.sessionID })
            #expect(all.contains { $0.type == "usage" && $0.usage?.provider == "opencode" && $0.usage?.costUSD == 0.0123 })
            let argv = try String(contentsOf: directory.appendingPathComponent("argv.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(argv == ["acp"])
            let permission = try String(contentsOf: directory.appendingPathComponent("permission.txt"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
            #expect(permission == OpencodeAgent.permissionEnvironment(mode: "manual"))
            let stdin = try String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(stdin.count == 3 && stdin[0].contains("\"initialize\"") && stdin[1].contains("\"session/new\"") && stdin[2].contains("\"session/prompt\""))
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func aSignedOutRunEndsInErrorWithTheAuthReason() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakeOpencode(directory, fixture: "auth", linger: true)
        let service = ProviderService(binaryOverrides: ["opencode": binary])
        let events = OpencodeRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi", provider: "opencode"), workspace: workspace)
            try await wait { events.values().contains { $0.status == "completed" || $0.status == "error" } }
            let status = events.values().last { $0.type == "status" }
            #expect(status?.status == "error" && status?.reason == "auth")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    // MARK: Where it shows

    @Test func opencodeIsABetaLocalAgentWithItsMarkAndNotADelegationAgent() {
        #expect(ProviderOptions.ids.last == "opencode" && ProviderOptions.label("opencode") == "opencode" && ProviderOptions.isBeta("opencode"))
        #expect(ProviderOptions.isLocalOnly("opencode") && ProviderOptions.isLocalOnly("pi") && !ProviderOptions.isLocalOnly("codex"))
        #expect(ProviderMark.markedProvider("opencode") == "opencode" && ProviderMark.colors(provider: "opencode").count == 1)
        #expect(!ProviderMark.commands(ProviderMark.outline(provider: "opencode")).isEmpty && ProviderMark.outline(provider: "opencode") != ProviderMark.outline(provider: "claude"))
        #expect(!DelegationProviders.isAgentPane(kind: SessionKind.claude, provider: "opencode"))
        #expect(SlashCommandCatalog.builtins(provider: "opencode").contains { $0.invocation == "model" })
        #expect(MightyGraphSupport.providers.contains("opencode") && !SessionHistory.providers.contains("opencode"))
        #expect(CLIAuthFailure.providers.contains("opencode"))
    }
}
