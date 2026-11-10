import Foundation
import SQLite3
import Testing
@testable import MightyCore

private final class HermesRecorder<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func append(_ item: T) { lock.lock(); items.append(item); lock.unlock() }
    func values() -> [T] { lock.lock(); defer { lock.unlock() }; return items }
}

/// Hermes Agent (`hermes acp`) as the sixth agent, Mac only: the ACP frames in
/// native/contracts/fixtures/hermes-acp-*.jsonl (written by hand from Hermes's source,
/// acp_adapter/* at a2fa0a4b, not recorded), the settings as config options, the approval
/// cards for dangerous commands and edits, a question skipped, usage as the session's
/// counters, the sign-in read from `initialize`, the kept model picker, the Mac-only setup
/// item, the session list read from `state.db` and the cancel sent before a stop.
struct HermesTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static let sessionID = "20261111_101502_3fa9c1d2"
    static let earlier = "20261109_181244_a17c90be"
    static let model = "openrouter:anthropic/claude-sonnet-4.5"

    static func fixture(_ name: String) throws -> [Data] {
        let data = try Data(contentsOf: root.appendingPathComponent("native/contracts/fixtures/hermes-acp-\(name).jsonl"))
        return data.split(separator: 10).map { Data($0) }
    }

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
        var answers: [[String: Any]] { writes.filter { $0["result"] != nil } }
        func option(_ answer: [String: Any]) -> String? { ((answer["result"] as? [String: Any])?["outcome"] as? [String: Any])?["optionId"] as? String }
    }

    static func channel(_ request: StartRunRequest) throws -> Replay {
        let result = Replay()
        result.parser = CLIStreamParser(provider: "hermes", log: { result.logs.append(($0, $1)) }, resume: { result.resumes.append($0) }, activityNamespace: "run-hermes",
                                        activity: { result.activities.append($0) }, usage: { result.usage.append($0) }, graph: { result.graph.append($0) }, graphInput: request.input)
        result.channel = ACPChannel(runId: "run-hermes", request: request, workspacePath: "/tmp/project", attachments: try AttachmentPreparation([]),
                                    configValues: HermesAgent.configValues(request),
                                    write: { data in result.writes.append((try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]) },
                                    event: { result.parser.receive(object: $0) },
                                    emit: { result.permissions.append($0) }, activity: { result.parser.permissionActivity($0, state: $1) },
                                    warning: { result.warnings.append($0) }, fail: { result.failures.append($0) }, completed: { result.completed += 1 })
        return result
    }
    static func replay(_ name: String, request: StartRunRequest = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi there", provider: "hermes"),
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
    static func allowing(_ allow: Bool) -> (Replay, Int) throws -> Void {
        { run, _ in if let ask = run.permissions.last, ask.state == "pending" { try run.channel.respond(requestId: ask.id, allow: allow) } }
    }

    // MARK: The channel's requests

    @Test func aNewSessionIsInitializeThenSessionNewThenOnePromptAndTheDefaultEditPolicyIsNotSetAgain() throws {
        let run = try Self.replay("answer")
        #expect(run.methods == ["initialize", "session/new", "session/prompt"])
        let new = try #require(run.writes[1]["params"] as? [String: Any])
        #expect(new["cwd"] as? String == "/tmp/project" && (new["mcpServers"] as? [Any])?.isEmpty == true)
        let prompt = try #require(run.writes[2]["params"] as? [String: Any])
        #expect(prompt["sessionId"] as? String == Self.sessionID)
        #expect(((prompt["prompt"] as? [[String: Any]])?.first)?["text"] as? String == "hi there")
        #expect(run.channel.turnCompleted && !run.channel.failed && run.completed == 1 && run.failures.isEmpty && run.warnings.isEmpty)
        // The turn is over: nothing is left to cancel.
        #expect(run.channel.cancellation.take() == nil)
    }

    @Test func theModelTheEffortAndTheEditPolicyGoAsHermessConfigOptions() throws {
        let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "do it", model: "nous:hermes-4-405b", provider: "hermes",
                                      settings: RunSettings(effort: "high", permissionMode: "acceptEdits"))
        #expect(HermesAgent.configValues(request).map(\.id) == ["model", "reasoning_effort", "edit_approval_policy"])
        let run = try Self.channel(request)
        run.channel.start()
        let lines = try Self.fixture("answer")
        run.channel.receive(lines[0] + Data([10])); run.channel.receive(lines[1] + Data([10]))
        func sent(_ index: Int) -> (String?, String?) { let params = run.writes[index]["params"] as? [String: Any]; return (params?["configId"] as? String, params?["value"] as? String) }
        #expect(sent(2) == ("model", "nous:hermes-4-405b"))
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-3","result":{}}"#.utf8) + Data([10]))
        #expect(sent(3) == ("reasoning_effort", "high"))
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-4","result":{}}"#.utf8) + Data([10]))
        #expect(sent(4) == ("edit_approval_policy", "workspace_session"))
        // Hermes refuses a control while the session is busy: named, and the request still goes.
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-5","error":{"code":-32603,"message":"Mode changes apply when the session is idle; try again after this turn."}}"#.utf8) + Data([10]))
        #expect(run.methods.last == "session/prompt" && run.warnings.count == 1 && run.warnings[0].contains("idle"))
        // A model the session's picker does not list is named and left out.
        let unknown = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "do it", model: "openrouter:gone/model", provider: "hermes"))
        unknown.channel.start()
        unknown.channel.receive(lines[0] + Data([10])); unknown.channel.receive(lines[1] + Data([10]))
        #expect(unknown.methods == ["initialize", "session/new", "session/prompt"] && unknown.warnings.count == 1 && unknown.warnings[0].contains("openrouter:gone/model"))
    }

    @Test func aResumeIgnoresTheReplayedConversationAndFollowsThePanesEditPolicy() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "go on", provider: "hermes", settings: RunSettings(permissionMode: "acceptEdits"))
        request.resumeId = Self.earlier
        let run = try Self.replay("resume", request: request)
        #expect(run.methods == ["initialize", "session/resume", "session/set_config_option", "session/prompt"])
        #expect((run.writes[1]["params"] as? [String: Any])?["sessionId"] as? String == Self.earlier)
        #expect((run.writes[2]["params"] as? [String: Any])?["value"] as? String == "workspace_session")
        // What Hermes replays before it answers the resume is the earlier conversation, not this run's.
        #expect(run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Continuing from where we stopped."])
        #expect(run.resumes == [Self.earlier] && run.channel.turnCompleted && !run.parser.failed)
        #expect(run.usage.last?.providerSessionId == Self.earlier && run.usage.last?.totalTokens == 40195)
    }

    @Test func aMissingSessionToResumeStartsANewOneWithANotice() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "go on", provider: "hermes")
        request.resumeId = "20250101_000000_deadbeef"
        let run = try Self.replay("resume-missing", request: request)
        #expect(run.methods == ["initialize", "session/resume", "session/new", "session/prompt"])
        #expect(run.warnings == [L("acp.channel.newSession")])
        #expect(run.resumes == [Self.sessionID] && run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Starting fresh."])
    }

    // MARK: Updates to the log, the graph and usage

    @Test func streamedTextIsLoggedOnceAndThoughtsAreNotCopied() throws {
        let run = try Self.replay("answer")
        #expect(run.logs.map(\.kind) == ["assistant"] && run.logs.first?.text == "Hello! How can I help with this project?")
        #expect(run.resumes == [Self.sessionID] && !run.parser.failed)
        let main = try #require(run.graph.last { $0.kind == "main" })
        #expect(main.state == "completed" && main.output == "Hello! How can I help with this project?")
        // Hermes reports its session's running counts, not the request's: the block carries no tokens.
        #expect(main.usage == nil)
    }

    @Test func usageIsTheSessionsRunningTokensAndTheContextWithoutACost() throws {
        let run = try Self.replay("answer")
        let last = try #require(run.usage.last)
        #expect(last.provider == "hermes" && last.source == "hermes.acp" && last.providerSessionId == Self.sessionID && last.model == Self.model)
        #expect(last.costUSD == nil)
        #expect(last.contextUsedTokens == 9200 && last.contextWindowTokens == 200000)
        // `totalTokens` holds the cached input that `inputTokens` leaves out.
        #expect(last.tokenScope == "session" && last.inputTokens == 9200 && last.outputTokens == 40 && last.cacheReadTokens == 8000 && last.reasoningTokens == 12 && last.totalTokens == 9240)
        // The ledger counts what the session grew by.
        var ledger = UsageLedger()
        #expect(ledger.record(paneId: "pane", workspaceId: "w", provider: "hermes", model: "default", usage: last)?.tokens == 9240)
        var next = last; next.totalTokens = 12_000
        #expect(ledger.record(paneId: "pane", workspaceId: "w", provider: "hermes", model: "default", usage: next)?.tokens == 2760)
    }

    @Test func toolsAreRowsADangerousCommandAndAnEditAreCardsAndAQuestionIsSkipped() throws {
        let run = try Self.replay("tools", answer: Self.allowing(true))
        #expect(run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["I'll read the README, then clean the build.", "The build folder is gone and notes.txt is written."])
        // Hermes sends no rawInput for its own tools: the path, the shell line and the title's tool name stand in.
        let read = run.activities.filter { $0.toolName == "Read" }
        #expect(read.last?.state == "completed" && read.last?.summary == "README.md" && read.last?.output == "Read README.md (42 lines)")
        let bash = run.activities.filter { $0.toolName == "Bash" && $0.summary == "rm -rf build" }
        #expect(bash.last?.state == "completed" && bash.last?.kind == "command" && bash.last?.output == "exit 0")
        let edit = run.activities.filter { $0.toolName == "Edit" && $0.summary == "notes.txt" }
        #expect(edit.last?.state == "completed")
        // The plan is the checklist.
        #expect(run.logs.contains { $0.kind == "system" && $0.text == "[x] Clean the build folder\n[ ] Write notes.txt" })
        // Two cards: the command Hermes holds back, and the edit with its arguments unwrapped.
        let cards = run.permissions.filter { $0.state == "pending" }
        #expect(cards.map(\.toolUseId) == ["perm-check-1", "edit-approval-2"] && cards.allSatisfy(\.canAllow))
        #expect(cards[0].toolName == "Bash" && cards[0].summary == "recursive delete: rm -rf build" && cards[0].inputJSON.contains("\"command\" : \"rm -rf build\""))
        #expect(ToolPermissionPresentation.make(toolName: cards[0].toolName, inputJSON: cards[0].inputJSON).primaryCode?.value == "rm -rf build")
        #expect(cards[1].toolName == "Edit" && cards[1].inputJSON.contains("\"path\" : \"notes.txt\"") && !cards[1].inputJSON.contains("\"tool\""))
        #expect(run.permissions.map(\.state) == ["pending", "allowed", "pending", "allowed"])
        // Allowed once with Hermes's own option id, never its "for session" or "always".
        #expect(run.answers.map { $0["id"] as? Int } == [0, 1, 2])
        #expect(run.answers.map(run.option) == ["allow_once", "allow_once", "skip"])
        // The card's own bubble settles with the answer; the question leaves no row.
        #expect(run.activities.last { $0.summary == "recursive delete: rm -rf build" }?.state == "completed")
        #expect(!run.activities.contains { $0.summary.contains("Commit") })
        #expect(run.warnings == [L("acp.channel.questionSkipped", ["question": "Commit the change now?", "choices": "Yes / Not yet"])])
        #expect(!run.parser.failed && run.channel.turnCompleted)
    }

    @Test func aDeniedCardAnswersHermessDenyOnce() throws {
        let denied = try Self.replay("tools", answer: Self.allowing(false))
        #expect(denied.answers.map(denied.option) == ["deny", "deny", "skip"])
        #expect(denied.permissions.map(\.state) == ["pending", "denied", "pending", "denied"])
    }

    @Test func aTurnThatFailsForAMissingProviderIsALostSignIn() throws {
        let run = try Self.replay("auth")
        #expect(run.parser.failed && run.parser.authFailure && run.channel.failed && run.completed == 1)
        // The reply said why; the error that follows is only the gateway's word for a failed turn.
        #expect(run.logs.contains { $0.kind == "assistant" && $0.text.contains("not connected to any AI provider") })
        #expect(run.logs.last { $0.kind == "error" }?.text == L("run.error.hermesTurnFailed"))
        #expect(CLIAuthFailure.hermes(text: "No LLM provider configured. Run `hermes model` to select a provider"))
        #expect(CLIAuthFailure.hermes(text: "Run `hermes auth add openai-codex` to re-enable if needed."))
        #expect(CLIAuthFailure.hermes(text: "Error code: 401 - Unauthorized"))
        #expect(!CLIAuthFailure.hermes(text: "admitted_turn_failed"))
        #expect(!CLIAuthFailure.hermes(text: "407 Proxy Authentication Required"))
        // Another failure keeps Hermes's own words (the Python ACP SDK's `data.details`).
        let other = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes"))
        other.channel.start()
        other.channel.receive(try Self.fixture("answer")[0] + Data([10]))
        other.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-2","error":{"code":-32603,"message":"Internal error","data":{"details":"Gateway could not start: port in use"}}}"#.utf8) + Data([10]))
        #expect(other.parser.failed && !other.parser.authFailure && other.logs.last?.text == "Gateway could not start: port in use")
    }

    // MARK: Permissions, sign-in, models

    @Test func permissionModesAreTheEditPolicyAndYoloForANewSession() {
        #expect(ProviderOptions.permissionModes(provider: "hermes") == ["manual", "acceptEdits", "fullAccess"])
        #expect(HermesAgent.editPolicy(mode: "manual") == "ask" && HermesAgent.editPolicy(mode: "acceptEdits") == "workspace_session" && HermesAgent.editPolicy(mode: "fullAccess") == "session")
        var environment = ["HERMES_YOLO_MODE": "1", "PATH": "/usr/bin"]
        HermesAgent.apply(mode: "manual", to: &environment)
        #expect(environment == ["PATH": "/usr/bin"])
        HermesAgent.apply(mode: "fullAccess", to: &environment)
        #expect(environment["HERMES_YOLO_MODE"] == "1")
        // Every mode can still ask (an edit to a sensitive path, a resumed session): the cards stay on.
        #expect(["manual", "acceptEdits", "fullAccess"].allSatisfy { ProviderOptions.asksInPane(provider: "hermes", mode: $0) })
        // A plan mode Hermes does not have falls back to asking.
        #expect(ProviderOptions.normalizedSettings(provider: "hermes", settings: RunSettings(permissionMode: "plan")).permissionMode == "manual")
        #expect(ProviderOptions.effortLevels(provider: "hermes", model: "default", registeredModels: []) == ProviderOptions.efforts)
    }

    @Test func signInIsTheProviderInitializeNames() throws {
        let signedIn = try #require(HermesAgent.parseInitialize(Data("warning line\n".utf8) + Self.probeAnswer(provider: "openrouter")))
        #expect(signedIn.answered && signedIn.provider == "openrouter" && signedIn.version == "0.23.1")
        let status = CLIAccountSupport.hermesStatus(signedIn)
        #expect(status.loggedIn == true && status.account == "openrouter" && status.methodId == CLIAccountMethod.hermesProvider && !status.canSignOut)
        // Only the terminal setup: no provider is connected.
        let signedOut = try #require(HermesAgent.parseInitialize(Self.probeAnswer(provider: nil)))
        #expect(signedOut.answered && signedOut.provider == nil)
        #expect(CLIAccountSupport.hermesStatus(signedOut).loggedIn == false)
        // No answer is unknown, not signed out.
        #expect(HermesAgent.parseInitialize(Data("The ACP server can't start: its protocol packages are missing from this install.\n".utf8)) == nil)
        #expect(CLIAccountSupport.hermesStatus(HermesAgent.Probe(provider: nil, answered: false, timedOut: false, version: nil)).loggedIn == nil)
        #expect(CLIAccountSupport.hermesStatus(HermesAgent.Probe(provider: nil, answered: false, timedOut: true, version: nil)).detail == L("settings.cliAccounts.detailHermesTimeout"))
        #expect(CLIAccountSupport.loginCommand(provider: "hermes", option: .account) == "hermes model")
        #expect(CLITerminalSignIn.table["hermes"]?.command == "hermes model" && CLIAuthFailure.providers.contains("hermes"))
    }
    static func probeAnswer(provider: String?) -> Data {
        let methods = (provider.map { [["id": $0, "name": "\($0) runtime credentials"]] } ?? []) + [["id": "hermes-setup", "name": "Configure Hermes provider", "type": "terminal", "args": ["--setup"]]]
        let frame: [String: Any] = ["jsonrpc": "2.0", "id": "mighty-probe", "result": ["protocolVersion": 1, "agentInfo": ["name": "hermes-agent", "version": "0.23.1"], "authMethods": methods]]
        return (try? JSONSerialization.data(withJSONObject: frame)).map { $0 + Data([10]) } ?? Data()
    }

    @Test func theAccountServiceAsksHermessACPServerAndClosesIt() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let bin = directory.appendingPathComponent("bin"); try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let home = directory.appendingPathComponent("home"); try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let answer = directory.appendingPathComponent("answer.json")
        // Reads the one request, answers it, then waits for stdin to close as an ACP server does.
        let script = "#!/bin/sh\nprintf '%s\\n' \"$@\" > '\(directory.path)/argv.txt'\nIFS= read -r request\nprintf '%s\\n' \"$request\" > '\(directory.path)/stdin.txt'\ncat '\(answer.path)'\ncat > /dev/null\n"
        try Data(script.utf8).write(to: bin.appendingPathComponent("hermes"))
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bin.appendingPathComponent("hermes").path)
        let service = CLIAccountService(environment: ["PATH": bin.path + ":/usr/bin:/bin"], home: home)
        try Self.probeAnswer(provider: nil).write(to: answer)
        let signedOut = await service.status(provider: "hermes")
        #expect(signedOut.installed && signedOut.loggedIn == false, "\(signedOut)")
        try Self.probeAnswer(provider: "nous").write(to: answer)
        let status = await service.status(provider: "hermes")
        #expect(status.loggedIn == true && status.account == "nous")
        #expect(try String(contentsOf: directory.appendingPathComponent("argv.txt"), encoding: .utf8) == "acp\n")
        #expect(try String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8).contains("\"method\":\"initialize\""))
    }

    @Test func theModelMenuIsThePickerTheLastSessionOffered() throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let cache = directory.appendingPathComponent("nested/hermes-models.json")
        // Before any session: Hermes's own default alone, with the reason.
        let empty = HermesAgent.cachedCatalog(url: cache)
        #expect(empty.models.map(\.value) == ["default"] && empty.detail == L("provider.catalog.hermesEmpty"))
        let session = try #require((try JSONSerialization.jsonObject(with: try Self.fixture("answer")[1]) as? [String: Any])?["result"] as? [String: Any])
        let models = HermesAgent.models(configOptions: session["configOptions"])
        #expect(models.map(\.value) == [Self.model, "openrouter:openai/gpt-5.5", "nous:hermes-4-405b"] && models[0].displayName == "Claude Sonnet 4.5")
        #expect(models[2].description == L("provider.catalog.hermesModelRow", ["provider": "nous"]))
        HermesAgent.saveModels(configOptions: session["configOptions"], to: cache)
        let catalog = HermesAgent.cachedCatalog(url: cache)
        #expect(catalog.source == "cli" && catalog.models.map(\.value) == ["default"] + models.map(\.value))
        // A session without a picker changes nothing.
        HermesAgent.saveModels(configOptions: [[String: Any]](), to: cache)
        #expect(HermesAgent.cachedCatalog(url: cache).models.count == 4)
        #expect(CoreValidation.model(Self.model))
    }

    // MARK: Setup

    @Test func theMacOnlySetupItemRunsHermessInstallerWithoutItsWizardAfterGit() throws {
        let shared = try Data(contentsOf: Self.root.appendingPathComponent("native/contracts/fixtures/setup-catalog.json"))
        let macOnly = try Data(contentsOf: Self.root.appendingPathComponent("native/macos/Sources/MightyCore/Resources/Setup/setup-catalog-macos.json"))
        #expect(try SetupCatalogDecoder.decode(shared).item("hermes") == nil)
        let catalog = try SetupCatalogDecoder.decode(shared, macOnly: macOnly)
        let item = try #require(catalog.item("hermes"))
        #expect(item.group == .agent && !item.required && item.provider == "hermes" && item.platforms[.windows] == nil)
        let entry = try #require(item.platforms[.macos])
        let url = try #require(URL(string: "https://hermes-agent.nousresearch.com/install.sh"))
        #expect(entry.probe == .executable(name: "hermes", minimumVersion: "0.23.0") && entry.install == .officialScript(url: url, shell: "bash") && entry.prerequisites == ["git"])
        // The script is fetched, then run with the option that leaves the sign-in to the app.
        let body = try #require(SetupScript.body(entry))
        #expect(body.hasPrefix("mighty_script=\"$(\(SetupScript.curl) 'https://hermes-agent.nousresearch.com/install.sh')\"") && body.hasSuffix("/bin/bash -c \"$mighty_script\" mighty-install '--skip-setup'"))
        // Another official script gets no option.
        let claudeEntry = try #require(catalog.item("claude")?.platforms[.macos])
        let claude = try #require(SetupScript.body(claudeEntry))
        #expect(claude.hasSuffix("-c \"$mighty_script\""))
        #expect(catalog.items.filter { $0.group == .agent }.map(\.id).suffix(2) == ["opencode", "hermes"])
        #expect(SetupPlan.order(selected: ["hermes"], catalog: catalog, platform: .macos, isSatisfied: { _ in false }) == ["git", "hermes"])
        // An out-of-date Hermes updates itself (`hermes update`); it is not installed over.
        #expect(!SetupRowState.isSelectable(.outdated, item: item, platform: .macos))
        #expect(try SetupCatalog.bundled().item("hermes") != nil)
        #expect(ComponentCatalog.installCommand(provider: "hermes")?.contains("--skip-setup") == true)
        #expect(SetupProber.versionNumbers("Hermes Agent v0.23.1 (2026.11.10)\nInstall directory: /Users/x/.hermes/hermes-agent\nPython: 3.12.7") == [0, 23, 1])
    }

    // MARK: Session list

    @Test func acpConversationsOfTheWorkspaceFolderAreListedFromStateDB() throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let workspace = directory.appendingPathComponent("project"); try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let home = directory.appendingPathComponent("home")
        let data = home.appendingPathComponent(".hermes"); try FileManager.default.createDirectory(at: data, withIntermediateDirectories: true)
        let path = workspace.resolvingSymlinksInPath().path
        let now = Date(), t = now.timeIntervalSince1970
        let policy = #"{"session_id":"\#(Self.sessionID)","lineage":["\#(Self.sessionID)"],"policy":{"request_json":"{\"source\":\"acp\",\"yolo\":true}"}}"#
        let asking = #"{"session_id":"\#(Self.earlier)","policy":{"request_json":"{\"source\":\"acp\"}"}}"#
        try sqlite(data.appendingPathComponent("state.db"), [
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, created_source TEXT, model TEXT, model_config TEXT, parent_session_id TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0, cwd TEXT, title TEXT, last_activity_at REAL, archived INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0)",
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL)",
            "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)",
            // The owner stores an editor's thread as `local`, created by `acp`.
            "INSERT INTO sessions VALUES ('\(Self.sessionID)', 'local', 'acp', 'anthropic/claude-sonnet-4.5', NULL, NULL, \(t - 900), 4, '\(path)', 'Clean the build', \(t - 800), 0, 0)",
            // Its compressed continuation: the same conversation, resumed under the first id.
            "INSERT INTO sessions VALUES ('20261111_103000_77aa11bb', 'local', 'acp', 'anthropic/claude-sonnet-4.5', NULL, '\(Self.sessionID)', \(t - 300), 2, '\(path)', NULL, \(t - 100), 0, 0)",
            "INSERT INTO sessions VALUES ('\(Self.earlier)', 'acp', NULL, NULL, '{\"cwd\":\"\(path)\"}', NULL, \(t - 5000), 2, NULL, NULL, NULL, 0, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000cli1', 'cli', 'cli', NULL, NULL, NULL, \(t - 50), 2, '\(path)', 'A terminal chat', \(t - 40), 0, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000else', 'acp', 'acp', NULL, NULL, NULL, \(t - 50), 2, '/elsewhere', 'Other folder', \(t - 40), 0, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000arch', 'acp', 'acp', NULL, NULL, NULL, \(t - 50), 2, '\(path)', 'Archived', \(t - 40), 1, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000none', 'acp', 'acp', NULL, NULL, NULL, \(t - 50), 0, '\(path)', 'Never used', \(t - 40), 0, 0)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(Self.sessionID)', 'user', 'clean the build folder', 1)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(Self.sessionID)', 'assistant', 'done', 2)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('20261111_103000_77aa11bb', 'user', 'and write notes', 3)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(Self.earlier)', 'user', '[{\"type\":\"text\",\"text\":\"what is in this picture?\"},{\"type\":\"image_url\"}]', 4)",
            "INSERT INTO state_meta VALUES ('gateway.local_policy.v1:\(Self.sessionID)', '\(policy)')",
            "INSERT INTO state_meta VALUES ('gateway.local_policy.v1:\(Self.earlier)', '\(asking)')",
        ])
        let listing = ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, now: now), provider: "hermes")
        #expect(listing.items.map(\.sessionID) == [Self.sessionID, Self.earlier])
        let first = try #require(listing.items.first)
        #expect(first.title == "clean the build folder" && first.requests == 2 && first.model == "anthropic/claude-sonnet-4.5" && first.provider == "hermes")
        #expect(abs(first.modified.timeIntervalSince1970 - (t - 100)) < 1)
        #expect(listing.items.last?.title == "what is in this picture?")
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, excluding: [Self.sessionID], now: now), provider: "hermes").items.count == 1)
        // `HERMES_HOME` moves the store.
        #expect(HermesAgent.database(home: home, environment: ["HERMES_HOME": "/opt/hermes"]).path == "/opt/hermes/state.db")
        // Whether a session was created with --yolo, which a resume cannot change.
        let database = HermesAgent.database(home: home, environment: [:])
        #expect(HermesAgent.startedWithFullAccess(sessionID: Self.sessionID, database: database) == true)
        #expect(HermesAgent.startedWithFullAccess(sessionID: Self.earlier, database: database) == false)
        #expect(HermesAgent.startedWithFullAccess(sessionID: "20250101_000000_deadbeef", database: database) == nil)
        #expect(CoreValidation.sessionIdentifier(Self.sessionID) && !CoreValidation.sessionIdentifier("20261111_101502_../x"))
    }

    // MARK: A run

    private func temporary() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-hermes-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
    private func sqlite(_ url: URL, _ statements: [String]) throws {
        var handle: OpaquePointer?
        guard sqlite3_open(url.path, &handle) == SQLITE_OK, let db = handle else { throw MightyError("sqlite open") }
        defer { sqlite3_close(db) }
        for statement in statements { guard sqlite3_exec(db, statement, nil, nil, nil) == SQLITE_OK else { throw MightyError("sqlite: " + statement) } }
    }
    /// A stand-in `hermes`: answers each of the channel's requests with the fixture's next
    /// response. With `hold` it answers up to the prompt and then only records what it is sent,
    /// as a turn in flight does.
    private func fakeHermes(_ directory: URL, fixture: String, hold: Bool = false) throws -> URL {
        let binary = directory.appendingPathComponent("hermes")
        let fixtureFile = Self.root.appendingPathComponent("native/contracts/fixtures/hermes-acp-\(fixture).jsonl").path
        let source = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf 'Hermes Agent v0.23.1 (2026.11.10)\\nInstall directory: /tmp/hermes\\nPython: 3.12.7\\n'; exit 0; fi
        printf '%s\\n' "$@" > argv.txt
        printf '%s\\n' "${HERMES_YOLO_MODE:-unset}" > yolo.txt
        exec 3<&0
        while IFS= read -r frame; do
          case "$frame" in
            *'"id":"mighty-'*) IFS= read -r request <&3; printf '%s\\n' "$request" >> stdin.txt
              \(hold ? "case \"$request\" in *'\"session/prompt\"'*) exec cat <&3 >> stdin.txt ;; esac" : ":") ;;
          esac
          printf '%s\\n' "$frame"
        done < '\(fixtureFile)'
        cat <&3 > /dev/null
        """
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        return binary
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Timed out waiting for the Hermes fixture") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    @Test(arguments: ["manual", "fullAccess"]) func aRunSpeaksACPAndGivesFullAccessAsYolo(mode: String) async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        // Full access also lifts the edit policy, which Hermes answers before the prompt.
        let binary = try fakeHermes(directory, fixture: mode == "fullAccess" ? "full-access" : "answer")
        let service = ProviderService(binaryOverrides: ["hermes": binary], environment: ["PATH": "/usr/bin:/bin", "HOME": directory.path, "HERMES_YOLO_MODE": "1"])
        let events = HermesRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi there", provider: "hermes", settings: RunSettings(permissionMode: mode)), workspace: workspace)
            try await wait { events.values().contains { $0.status == "completed" || $0.status == "error" } }
            let all = events.values()
            #expect(all.last { $0.type == "status" }?.status == "completed")
            #expect(all.contains { $0.type == "log" && $0.entry?.kind == "assistant" && $0.entry?.text == "Hello! How can I help with this project?" })
            #expect(all.contains { $0.type == "resume" && $0.resumeId == Self.sessionID })
            #expect(all.contains { $0.type == "usage" && $0.usage?.provider == "hermes" && $0.usage?.totalTokens == 9240 })
            #expect(try String(contentsOf: directory.appendingPathComponent("argv.txt"), encoding: .utf8) == "acp\n")
            // The pane's mode decides, not a HERMES_YOLO_MODE the shell exported.
            #expect(try String(contentsOf: directory.appendingPathComponent("yolo.txt"), encoding: .utf8) == (mode == "fullAccess" ? "1\n" : "unset\n"))
            let stdin = try String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            // Manual leaves the session's "ask" as it is.
            #expect(stdin.map { $0.contains("\"session/set_config_option\"") } == (mode == "fullAccess" ? [false, false, true, false] : [false, false, false]))
            #expect(stdin.last?.contains("\"session/prompt\"") == true)
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func aStopTellsHermesToCancelTheTurnBeforeTheProcessEnds() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakeHermes(directory, fixture: "answer", hold: true)
        let service = ProviderService(binaryOverrides: ["hermes": binary], environment: ["PATH": "/usr/bin:/bin", "HOME": directory.path])
        let events = HermesRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        let sent = directory.appendingPathComponent("stdin.txt")
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi there", provider: "hermes"), workspace: workspace)
            try await wait { (try? String(contentsOf: sent, encoding: .utf8))?.contains("\"session/prompt\"") == true }
            // The turn runs in Hermes's gateway, which ending this process would not stop.
            runner.signalStop(id: "pane")
            await runner.stop(id: "pane")
            let frames = try String(contentsOf: sent, encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(frames.last?.contains("\"method\":\"session/cancel\"") == true && frames.last?.contains(Self.sessionID) == true)
            #expect(events.values().last { $0.type == "status" }?.status == "stopped")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func hermesIsABetaLocalAgentWithItsMarkAndNotADelegationAgent() throws {
        #expect(ProviderOptions.ids.last == "hermes" && ProviderOptions.label("hermes") == "Hermes" && ProviderOptions.isBeta("hermes") && ProviderOptions.isLocalOnly("hermes"))
        #expect(ProviderMark.markedProvider("hermes") == "hermes" && ProviderMark.colors(provider: "hermes").count == 1)
        #expect(!ProviderMark.commands(ProviderMark.outline(provider: "hermes")).isEmpty && ProviderMark.outline(provider: "hermes") != ProviderMark.outline(provider: "claude"))
        #expect(!DelegationProviders.isAgentPane(kind: SessionKind.claude, provider: "hermes"))
        #expect(SlashCommandCatalog.builtins(provider: "hermes").contains { $0.invocation == "model" })
        #expect(MightyGraphSupport.providers.contains("hermes") && ResumableSessions.providers.contains("hermes") && !SessionHistory.providers.contains("hermes"))
        #expect(try ProviderService.arguments(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes"), pluginDirectory: URL(fileURLWithPath: "/tmp")) == ["acp"])
        #expect(ProviderOptions.fallbackCatalog("hermes").models.map(\.value) == ["default"])
        #expect(HermesAgent.versionLine("Hermes Agent v0.23.1 (2026.11.10)\nInstall directory: /Users/x/.hermes/hermes-agent\nUp to date") == "Hermes Agent v0.23.1 (2026.11.10)")
    }
}
