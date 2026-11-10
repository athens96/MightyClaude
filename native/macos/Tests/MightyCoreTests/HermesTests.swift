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

/// Hermes Agent (`hermes acp`) as the sixth agent, Mac only. The ACP frames in
/// native/contracts/fixtures/hermes-acp-*.jsonl are recordings of Hermes 0.23.1 (ids, paths
/// and the model's name replaced, thought deltas joined): the settings as config options,
/// the approval cards for an edit and a dangerous command, usage as the session's counters,
/// a session that cannot answer, the sign-in read from `initialize`, the kept model picker,
/// the Mac-only setup item, the session list read from `state.db` and the cancel sent
/// before a stop.
struct HermesTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    /// The sessions of the recordings.
    static let answerSession = "local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b"
    static let resumedSession = "local-a8a2537a19ce1905c0a99559ca0f2e86197234e68b0931bf190fc6517e91386e"
    static let staleSession = "local-b3d62fb4c0780d2c11596ef324e3582b301cb89d9993a5daaa93c6431187c278"
    static let fullAccessSession = "local-dfe43adeddc0d387c5ccf74a66b860e934acba67a28d57f45eb25e0421ef9dc6"
    /// The recorded session's model as it opens, and as Hermes names it once the endpoint is settled.
    static let model = "custom:example-model-2.5"
    static let namedModel = "custom:llm.example.com:example-model-2.5"

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

    static func channel(_ request: StartRunRequest, admit: (([String: Any]) -> String?)? = nil) throws -> Replay {
        let result = Replay()
        result.parser = CLIStreamParser(provider: "hermes", log: { result.logs.append(($0, $1)) }, resume: { result.resumes.append($0) }, activityNamespace: "run-hermes",
                                        activity: { result.activities.append($0) }, usage: { result.usage.append($0) }, graph: { result.graph.append($0) }, graphInput: request.input)
        result.channel = ACPChannel(runId: "run-hermes", request: request, workspacePath: "/tmp/project", attachments: try AttachmentPreparation([]),
                                    configValues: HermesAgent.configValues(request), freeChoices: true, admit: admit,
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
        // Hermes's session ids are `local-` and 64 hex digits.
        #expect(prompt["sessionId"] as? String == Self.answerSession && Self.answerSession.hasPrefix("local-") && Self.answerSession.count == 70)
        #expect(((prompt["prompt"] as? [[String: Any]])?.first)?["text"] as? String == "hi there")
        #expect(run.channel.turnCompleted && !run.channel.failed && run.completed == 1 && run.failures.isEmpty && run.warnings.isEmpty)
        // The turn is over: nothing is left to cancel.
        #expect(run.channel.cancellation.take() == nil)
    }

    @Test func theModelTheEffortAndTheEditPolicyGoAsHermessConfigOptions() throws {
        // The recorded session runs `custom:example-model-2.5` at effort max, asking before edits.
        let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "do it", model: Self.namedModel, provider: "hermes",
                                      settings: RunSettings(effort: "high", permissionMode: "acceptEdits"))
        #expect(HermesAgent.configValues(request).map(\.id) == ["model", "reasoning_effort", "edit_approval_policy"])
        let run = try Self.channel(request)
        run.channel.start()
        let lines = try Self.fixture("answer")
        run.channel.receive(lines[0] + Data([10])); run.channel.receive(lines[1] + Data([10]))
        func sent(_ index: Int) -> [String?] { let params = run.writes[index]["params"] as? [String: Any]; return [params?["configId"] as? String, params?["value"] as? String] }
        #expect(sent(2) == ["model", Self.namedModel])
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-3","result":{}}"#.utf8) + Data([10]))
        #expect(sent(3) == ["reasoning_effort", "high"])
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-4","result":{}}"#.utf8) + Data([10]))
        #expect(sent(4) == ["edit_approval_policy", "workspace_session"])
        // Hermes refuses a control while the session is busy: named, and the request still goes.
        run.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-5","error":{"code":-32603,"message":"Mode changes apply when the session is idle; try again after this turn."}}"#.utf8) + Data([10]))
        #expect(run.methods.last == "session/prompt" && run.warnings.count == 1 && run.warnings[0].contains("idle"))
        // The session's own model and effort are not sent again; a model its picker does not list is named and left out.
        let same = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "do it", model: Self.model, provider: "hermes", settings: RunSettings(effort: "max")))
        same.channel.start()
        same.channel.receive(lines[0] + Data([10])); same.channel.receive(lines[1] + Data([10]))
        #expect(same.methods == ["initialize", "session/new", "session/prompt"] && same.warnings.isEmpty)
        let unknown = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "do it", model: "openrouter:gone/model", provider: "hermes"))
        unknown.channel.start()
        unknown.channel.receive(lines[0] + Data([10])); unknown.channel.receive(lines[1] + Data([10]))
        #expect(unknown.methods == ["initialize", "session/new", "session/prompt"] && unknown.warnings.count == 1 && unknown.warnings[0].contains("openrouter:gone/model"))
    }

    @Test func aResumeIgnoresTheReplayedConversationAndFollowsThePanesEditPolicy() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "go on", provider: "hermes", settings: RunSettings(permissionMode: "acceptEdits"))
        request.resumeId = Self.resumedSession
        let run = try Self.replay("resume", request: request)
        #expect(run.methods == ["initialize", "session/resume", "session/set_config_option", "session/prompt"])
        #expect((run.writes[1]["params"] as? [String: Any])?["sessionId"] as? String == Self.resumedSession)
        #expect((run.writes[2]["params"] as? [String: Any])?["value"] as? String == "workspace_session")
        // What Hermes replays before it answers the resume (two earlier requests, their tools) is not this run's.
        #expect(run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Bye."])
        #expect(run.activities.allSatisfy { $0.kind == "turn" })
        #expect(run.resumes == [Self.resumedSession] && run.channel.turnCompleted && !run.parser.failed)
        // The counters carry on from the session's earlier requests.
        #expect(run.usage.last?.providerSessionId == Self.resumedSession && run.usage.last?.totalTokens == 46130)
    }

    @Test func aMissingSessionToResumeStartsANewOneWithANotice() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "go on", provider: "hermes")
        request.resumeId = "local-" + String(repeating: "0", count: 64)
        let run = try Self.replay("resume-missing", request: request)
        #expect(run.methods == ["initialize", "session/resume", "session/new", "session/prompt"])
        #expect(run.warnings == [L("acp.channel.newSession")])
        #expect(run.resumes.count == 1 && run.resumes[0] != request.resumeId && run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Hello."])
    }

    @Test func withoutAProviderTheRunStopsAtInitializeAsAMissingSignIn() throws {
        // The recorded `initialize` of a Hermes with nothing configured offers its terminal setup alone.
        let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes")
        let run = try Self.channel(request, admit: { HermesAgent.provider(initialize: $0) == nil ? L("run.error.hermesNoProvider") : nil })
        run.channel.start()
        run.channel.receive(try Self.fixture("unconfigured")[0] + Data([10]))
        // No session is opened: one created now would keep a provider that cannot answer.
        #expect(run.methods == ["initialize"] && run.channel.failed && run.completed == 1 && run.resumes.isEmpty)
        #expect(run.parser.failed && run.parser.authFailure && run.logs.map(\.text) == [L("run.error.hermesNoProvider")])
        // A configured Hermes passes.
        let configured = try Self.channel(request, admit: { HermesAgent.provider(initialize: $0) == nil ? "no" : nil })
        configured.channel.start()
        configured.channel.receive(try Self.fixture("answer")[0] + Data([10]))
        #expect(configured.methods == ["initialize", "session/new"])
    }

    // MARK: Updates to the log, the graph and usage

    @Test func streamedTextIsLoggedOnceAndThoughtsAreNotCopied() throws {
        let run = try Self.replay("answer")
        #expect(run.logs.map(\.kind) == ["assistant"] && run.logs.first?.text == "Hi.")
        #expect(run.resumes == [Self.answerSession] && !run.parser.failed)
        let main = try #require(run.graph.last { $0.kind == "main" })
        #expect(main.state == "completed" && main.output == "Hi.")
        // Hermes reports its session's running counts, not the request's: the block carries no tokens.
        #expect(main.usage == nil)
    }

    @Test func usageIsTheSessionsRunningTokensAndTheContextWithoutACost() throws {
        let run = try Self.replay("answer")
        let last = try #require(run.usage.last)
        #expect(last.provider == "hermes" && last.source == "hermes.acp" && last.providerSessionId == Self.answerSession)
        // The session opens as `custom:<model>` and then names the endpoint it settled on.
        #expect(run.usage.first?.model == Self.model && last.model == Self.namedModel)
        #expect(last.costUSD == nil)
        #expect(last.contextUsedTokens == 11194 && last.contextWindowTokens == 256000)
        #expect(last.tokenScope == "session" && last.inputTokens == 11166 && last.outputTokens == 28 && last.totalTokens == 11194)
        // The ledger counts what the session grew by: the recorded resume of a 34,633-token session ends at 46,130.
        var ledger = UsageLedger()
        #expect(ledger.record(paneId: "pane", workspaceId: "w", provider: "hermes", model: "default", usage: last)?.tokens == 11194)
        var next = last; next.totalTokens = 12_000
        #expect(ledger.record(paneId: "pane", workspaceId: "w", provider: "hermes", model: "default", usage: next)?.tokens == 806)
        // A turn cancelled before its first model call reports zeros: they are not the session's counts.
        let cancelled = try Self.replay("cancel")
        #expect(cancelled.channel.turnCompleted && !cancelled.parser.failed && cancelled.usage.allSatisfy { $0.totalTokens == nil })
    }

    @Test func toolsAreRowsAndAnEditAndADangerousCommandAreCards() throws {
        // Recorded with both cards denied: the calls end failed.
        let run = try Self.replay("tools", answer: Self.allowing(false))
        // Hermes sends the reply it streamed once more when the turn settles: it is logged once, without the blank lines ahead of it.
        let replies = run.logs.filter { $0.kind == "assistant" }.map(\.text)
        #expect(replies.count == 2 && replies[0] == "[tool call]" && replies[1].hasPrefix("Done what could be done;"))
        #expect(replies[1].components(separatedBy: "Done what could be done;").count == 2)
        // Hermes sends no rawInput for its own tools: the path, the shell line and the title's tool name stand in.
        let read = run.activities.filter { $0.toolName == "Read" }
        #expect(read.last?.state == "completed" && read.last?.summary == "/etc/shells" && read.last?.output?.contains("/bin/zsh") == true)
        let edit = run.activities.filter { $0.toolName == "Edit" && $0.summary == "note.txt" }
        #expect(edit.first?.state == "running" && edit.last?.state == "error" && edit.last?.output?.contains("Edit approval denied") == true)
        let bash = run.activities.filter { $0.toolName == "Bash" && $0.summary == "rm -rf ./nothing-here" }
        #expect(bash.last?.state == "error" && bash.last?.kind == "command" && bash.last?.output?.contains("Command denied by user") == true)
        // A tool Hermes has no kind for is named by its title, and this one does carry its input.
        #expect(run.activities.last { $0.toolName == "tool_search" }?.state == "completed")
        // Two cards: the edit with its arguments unwrapped, and the command Hermes holds back.
        let cards = run.permissions.filter { $0.state == "pending" }
        #expect(cards.map(\.toolUseId) == ["edit-approval-1", "perm-check-1"] && cards.allSatisfy(\.canAllow))
        #expect(cards[0].toolName == "Edit" && cards[0].summary == "Approve edit: /private/tmp/project/note.txt")
        #expect(cards[0].inputJSON.contains("\"path\" : \"note.txt\"") && cards[0].inputJSON.contains("\"content\" : \"hi\"") && !cards[0].inputJSON.contains("\"tool\""))
        #expect(cards[1].toolName == "Bash" && cards[1].summary == "recursive delete: rm -rf ./nothing-here" && cards[1].inputJSON.contains("\"command\" : \"rm -rf ./nothing-here\""))
        #expect(ToolPermissionPresentation.make(toolName: cards[1].toolName, inputJSON: cards[1].inputJSON).primaryCode?.value == "rm -rf ./nothing-here")
        #expect(run.permissions.map(\.state) == ["pending", "denied", "pending", "denied"])
        // Denied with Hermes's own option id; its number ids are answered as numbers.
        #expect(run.answers.map { $0["id"] as? Int } == [0, 1] && run.answers.map(run.option) == ["deny", "deny"])
        // The card's own bubble settles with the answer.
        #expect(run.activities.last { $0.summary == "recursive delete: rm -rf ./nothing-here" }?.state == "error")
        #expect(!run.parser.failed && run.channel.turnCompleted && run.usage.last?.totalTokens == 24646)
        // Allowed once, never "for session" or "always", though the command's card offers them.
        let allowed = try Self.replay("tools", answer: Self.allowing(true))
        #expect(allowed.answers.map(allowed.option) == ["allow_once", "allow_once"])
    }

    /// The frames Hermes 0.23.1's own builders (`acp_adapter/elicitation.py`) make for a clarify prompt:
    /// one choice, several, a typed answer, and the approval a client without forms gets. The recorded
    /// exchange (hermes-acp-clarify.jsonl) shows only the single-choice form.
    static let builtClarify: [String] = [
        #"{"jsonrpc":"2.0","id":0,"method":"elicitation/create","params":{"sessionId":"local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b","mode":"form","message":"Commit the change now?","requestedSchema":{"type":"object","properties":{"answer":{"type":"string","enum":["Yes","Not yet"]}},"required":["answer"]}}}"#,
        #"{"jsonrpc":"2.0","id":1,"method":"elicitation/create","params":{"sessionId":"local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b","mode":"form","message":"Which files should be staged?","requestedSchema":{"type":"object","properties":{"answer":{"type":"array","items":{"type":"string","enum":["README.md","notes.txt","src/main.py"]}}},"required":["answer"]}}}"#,
        #"{"jsonrpc":"2.0","id":2,"method":"elicitation/create","params":{"sessionId":"local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b","mode":"form","message":"What should the commit message say?","requestedSchema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}}}"#,
        #"{"jsonrpc":"2.0","id":3,"method":"session/request_permission","params":{"sessionId":"local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b","toolCall":{"content":[{"content":{"text":"Commit the change now?","type":"text"},"type":"content"}],"kind":"other","rawInput":{"question":"Commit the change now?","choices":["Yes","Not yet"]},"status":"pending","title":"Commit the change now?","toolCallId":"clarify-7c1d","sessionUpdate":"tool_call_update"},"options":[{"kind":"allow_once","name":"Yes","optionId":"choice-0"},{"kind":"allow_once","name":"Not yet","optionId":"choice-1"},{"kind":"reject_once","name":"Skip","optionId":"skip"}]}}"#,
    ]
    /// A session opened, then one of `builtClarify`.
    static func clarify(_ index: Int) throws -> (Replay, ToolPermissionRequest?) {
        let run = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes"))
        run.channel.start()
        let lines = try Self.fixture("answer")
        run.channel.receive(lines[0] + Data([10])); run.channel.receive(lines[1] + Data([10]))
        run.channel.receive(Data(Self.builtClarify[index].utf8) + Data([10]))
        return (run, run.permissions.first)
    }
    /// Answers each question card as it appears with `answer`.
    static func answering(_ answer: @escaping (Replay, ToolPermissionRequest) throws -> Void) -> (Replay, Int) throws -> Void {
        { run, _ in
            guard let card = run.permissions.last, card.state == "pending", run.permissions.filter({ $0.id == card.id }).count == 1 else { return }
            try answer(run, card)
        }
    }

    @Test func aRecordedClarifyAsksItsQuestionsOneCardAtATimeAndTheTurnGoesOn() throws {
        // Recorded with Hermes 0.23.1: one clarify call of three questions, each its own `elicitation/create` (ids 0, 1, 2).
        var asked: [UserQuestionnaire.Question] = []
        let run = try Self.replay("clarify", answer: Self.answering { run, card in
            let question = try #require(card.questionnaire).questions[0]
            asked.append(question)
            // The form names no call: it is the question of the clarify call that is open, whose row waits.
            #expect(card.toolUseId == "call_clarify_1" && run.activities.last?.state == "waiting")
            try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, picked: [question.options[0].label]))
        })
        #expect(asked.count == 3 && asked.allSatisfy { !$0.multiSelect && $0.header == L("questionnaire.header.agent") })
        // Hermes marks its suggested choice in the choice's own text; the whole text is the answer it reads.
        #expect(asked[0].options.map(\.label) == ["작은 웹 앱/CLI 툴 (Recommended)", "라이브러리/패키지", "대충 뭐라도 — 아이디어를 던져보고 같이 다듬기", "아직 모르겠음"])
        let replies = run.writes.filter { ($0["result"] as? [String: Any])?["action"] != nil }
        // The request ids are numbers from 0: zero is an id like any other.
        #expect(replies.map { $0["id"] as? Int } == [0, 1, 2])
        #expect(((replies[0]["result"] as? [String: Any])?["content"] as? [String: String]) == ["answer": "작은 웹 앱/CLI 툴 (Recommended)"])
        #expect(run.permissions.map(\.state) == ["pending", "answered", "pending", "answered", "pending", "answered"])
        #expect(run.warnings.isEmpty && run.failures.isEmpty && run.channel.turnCompleted && !run.parser.failed)
        #expect(run.logs.contains { $0.kind == "assistant" && $0.text.contains("작은 웹 앱/CLI 툴") })
    }

    @Test func aDeclinedClarifyIsSkippedAndTheTurnGoesOn() throws {
        let run = try Self.replay("clarify-declined", answer: Self.answering { run, card in try run.channel.respond(requestId: card.id, allow: false) })
        let reply = try #require(run.writes.first { $0["id"] as? Int == 0 }?["result"] as? [String: Any])
        #expect(reply["action"] as? String == "decline" && reply.count == 1)
        #expect(run.permissions.map(\.state) == ["pending", "denied"] && run.channel.turnCompleted && !run.parser.failed)
        // Recorded: Hermes goes on and says the card did not reach the user.
        #expect(run.logs.contains { $0.kind == "assistant" })
    }

    @Test func aClarifyHermesStoppedWaitingForTakesItsCardAwayWithALine() throws {
        // Hermes waits `agent.clarify_timeout` (90 s by default), then settles the call without telling the client.
        let run = try Self.replay("clarify-declined")
        #expect(run.permissions.map(\.state) == ["pending", "cancelled"])
        #expect(run.warnings == [L("acp.channel.questionWithdrawn", ["question": try #require(run.permissions.first).summary])])
        #expect(!run.writes.contains { $0["id"] as? Int == 0 })
    }

    @Test func theNoteAboutTheCardFollowsTheRequestAndIsNotTheSessionsPrompt() throws {
        let result = Replay()
        let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "선택하는 문제를 내", provider: "hermes")
        result.parser = CLIStreamParser(provider: "hermes", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-hermes", graphInput: request.input)
        result.channel = ACPChannel(runId: "run-hermes", request: request, workspacePath: "/tmp/project", attachments: try AttachmentPreparation([]),
                                    configValues: HermesAgent.configValues(request), freeChoices: true, promptNote: HermesQuestionTool.promptNote,
                                    write: { data in result.writes.append((try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]) },
                                    event: { result.parser.receive(object: $0) }, emit: { _ in }, activity: { _, _ in }, warning: { _ in }, fail: { _ in }, completed: {})
        result.channel.start()
        let lines = try Self.fixture("answer")
        result.channel.receive(lines[0] + Data([10])); result.channel.receive(lines[1] + Data([10]))
        let prompt = try #require(result.writes.last { $0["method"] as? String == "session/prompt" }?["params"] as? [String: Any])
        let text = try #require((prompt["prompt"] as? [[String: Any]])?.first?["text"] as? String)
        // After the request: Hermes titles the session by the start of its first message.
        #expect(text == "선택하는 문제를 내\n\n" + HermesQuestionTool.promptNote)
        #expect(HermesQuestionTool.withoutNote(text) == "선택하는 문제를 내" && HermesQuestionTool.withoutNote("plain") == "plain")
        #expect(HermesQuestionTool.promptNote.contains("clarify"))
    }

    static func answers(_ card: ToolPermissionRequest, picked: [String] = [], typed: String? = nil) throws -> [String: UserQuestionAnswer] {
        [try #require(card.questionnaire).questions[0].question: UserQuestionAnswer(selectedOptions: picked, customText: typed)]
    }

    @Test func formElicitationIsOfferedAndAClarifyFormIsTheQuestionCard() throws {
        var (run, shown) = try Self.clarify(0)
        let capabilities = try #require((run.writes[0]["params"] as? [String: Any])?["clientCapabilities"] as? [String: Any])
        #expect(((capabilities["elicitation"] as? [String: Any])?["form"] as? [String: Any])?.isEmpty == true)
        // One property without a title: the form's message is the question, each `enum` value a choice.
        var card = try #require(shown), question = try #require(card.questionnaire).questions[0]
        #expect(card.canAnswerQuestions && question.question == "Commit the change now?" && question.header == L("questionnaire.header.agent"))
        #expect(question.options.map(\.label) == ["Yes", "Not yet"] && !question.multiSelect)
        // No tool call of its own: no activity row is made for it.
        #expect(run.activities.allSatisfy { $0.kind == "turn" })
        try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, picked: ["Not yet"]))
        var result = try #require(run.answers.last?["result"] as? [String: Any])
        #expect(run.answers.last?["id"] as? Int == 0 && result["action"] as? String == "accept" && (result["content"] as? [String: String]) == ["answer": "Not yet"])
        // Hermes reads any string as the answer: a typed one travels in the same property.
        (run, shown) = try Self.clarify(0); card = try #require(shown)
        try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, typed: "After the tests pass"))
        #expect(((run.answers.last?["result"] as? [String: Any])?["content"] as? [String: String]) == ["answer": "After the tests pass"])
        // Several picks are a list, a typed answer its last entry.
        (run, shown) = try Self.clarify(1); card = try #require(shown); question = try #require(card.questionnaire).questions[0]
        #expect(question.multiSelect && question.options.map(\.label) == ["README.md", "notes.txt", "src/main.py"])
        try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, picked: ["notes.txt", "README.md"], typed: "docs/"))
        #expect(((run.answers.last?["result"] as? [String: Any])?["content"] as? [String: [String]]) == ["answer": ["README.md", "notes.txt", "docs/"]])
        // No choices: a typed answer.
        (run, shown) = try Self.clarify(2); card = try #require(shown); question = try #require(card.questionnaire).questions[0]
        #expect(question.options.isEmpty && question.question == "What should the commit message say?")
        #expect(throws: (any Error).self) { try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card)) }
        try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, typed: "Fix the parser"))
        #expect(((run.answers.last?["result"] as? [String: Any])?["content"] as? [String: String]) == ["answer": "Fix the parser"])
        // Cancel declines the form, which Hermes reads as its Skip.
        (run, shown) = try Self.clarify(0); card = try #require(shown)
        try run.channel.respond(requestId: card.id, allow: false)
        result = try #require(run.answers.last?["result"] as? [String: Any])
        #expect(result["action"] as? String == "decline" && result.count == 1 && run.permissions.map(\.state) == ["pending", "denied"])
    }

    @Test func aClarifyApprovalIsTheQuestionCardAndThePickIsItsOption() throws {
        // What a Hermes that was not told about forms sends: one `allow_once` option per choice, plus Skip.
        var (run, shown) = try Self.clarify(3)
        var card = try #require(shown)
        let question = try #require(card.questionnaire).questions[0]
        #expect(card.canAnswerQuestions && !card.canAllow && card.toolUseId == "clarify-7c1d")
        #expect(question.question == "Commit the change now?" && question.options.map(\.label) == ["Yes", "Not yet"])
        // Nothing typed can travel as an option: refused, and the card stays.
        #expect(throws: (any Error).self) { try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, typed: "Later")) }
        #expect(run.answers.isEmpty)
        try run.channel.answerQuestions(requestId: card.id, answers: Self.answers(card, picked: ["Not yet"]))
        #expect(run.answers.map(run.option) == ["choice-1"] && run.answers.last?["id"] as? Int == 3 && run.permissions.map(\.state) == ["pending", "answered"])
        // Cancel is Hermes's Skip.
        (run, shown) = try Self.clarify(3); card = try #require(shown)
        try run.channel.respond(requestId: card.id, allow: false)
        #expect(run.answers.map(run.option) == ["skip"])
        // The call settled elsewhere: the card goes without an answer.
        (run, shown) = try Self.clarify(3)
        run.channel.receive(Data(#"{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"\#(Self.answerSession)","update":{"sessionUpdate":"tool_call_update","toolCallId":"clarify-7c1d","status":"failed"}}}"#.utf8) + Data([10]))
        #expect(run.permissions.map(\.state) == ["pending", "cancelled"] && run.answers.isEmpty)
    }

    @Test func aQuestionTheCardCannotShowIsSkippedAndNamed() throws {
        let run = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes"))
        run.channel.start()
        let lines = try Self.fixture("answer")
        run.channel.receive(lines[0] + Data([10])); run.channel.receive(lines[1] + Data([10]))
        // Two choices of one name: the card could not tell them apart.
        let ask: [String: Any] = ["jsonrpc": "2.0", "id": 2, "method": "session/request_permission", "params": ["sessionId": Self.answerSession,
            "toolCall": ["toolCallId": "clarify-7c1d", "title": "Commit the change now?", "kind": "other", "status": "pending", "rawInput": ["question": "Commit the change now?", "choices": ["Yes", "Yes"]]],
            "options": [["optionId": "choice-0", "name": "Yes", "kind": "allow_once"], ["optionId": "choice-1", "name": "Yes", "kind": "allow_once"], ["optionId": "skip", "name": "Skip", "kind": "reject_once"]]]]
        run.channel.receive(try JSONSerialization.data(withJSONObject: ask) + Data([10]))
        #expect(run.permissions.isEmpty && run.answers.map(run.option) == ["skip"] && run.activities.allSatisfy { $0.kind == "turn" })
        #expect(run.warnings == [L("acp.channel.questionSkipped", ["question": "Commit the change now?", "choices": "Yes / Yes"])])
    }

    @Test func aTurnThatFailsForAMissingProviderIsALostSignIn() throws {
        // Recorded: a session created before any provider was set up, resumed after one was.
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes")
        request.resumeId = Self.staleSession
        let run = try Self.replay("auth", request: request)
        #expect(run.parser.failed && run.parser.authFailure && run.channel.failed && run.completed == 1)
        // The reply said why; the error that follows is only the gateway's word for a failed turn.
        #expect(run.logs.first { $0.kind == "assistant" }?.text.hasPrefix("⚠️ provider 'auto' has no endpoint configured") == true)
        #expect(run.logs.last { $0.kind == "error" }?.text == L("run.error.hermesTurnFailed"))
        #expect(CLIAuthFailure.hermes(text: "⚠️ provider 'auto' has no endpoint configured, so the request is not sent anywhere"))
        #expect(CLIAuthFailure.hermes(text: "No LLM provider configured. Run `hermes model` to select a provider"))
        #expect(CLIAuthFailure.hermes(text: "Run `hermes auth add openai-codex` to re-enable if needed."))
        #expect(CLIAuthFailure.hermes(text: "Error code: 401 - Unauthorized"))
        #expect(!CLIAuthFailure.hermes(text: "admitted_turn_failed"))
        #expect(!CLIAuthFailure.hermes(text: "407 Proxy Authentication Required"))
        // Another failure keeps Hermes's own words (the Python ACP SDK's `data.details`, as recorded for a missing session).
        let other = try Self.channel(StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "hi", provider: "hermes"))
        other.channel.start()
        other.channel.receive(try Self.fixture("answer")[0] + Data([10]))
        other.channel.receive(Data(#"{"jsonrpc":"2.0","id":"mighty-2","error":{"code":-32603,"message":"Internal error","data":{"details":"Gateway could not start: port in use"}}}"#.utf8) + Data([10]))
        #expect(other.parser.failed && !other.parser.authFailure && other.logs.last?.text == "Gateway could not start: port in use")
    }

    @Test func hermessLogAndTracebacksOnStderrStayOutOfThePane() {
        // What `hermes acp` wrote to stderr for the failed request above.
        let filter = HermesAgent.StderrFilter()
        let recorded = """
        2026-11-11 01:18:54 [INFO] acp_adapter.entry: Starting hermes-agent ACP adapter
        2026-11-11 01:18:54 [INFO] hermes_cli.plugins: Plugin 'default' registered delegation
        2026-11-11 01:18:54 [ERROR] root: Background task failed
        Traceback (most recent call last):
          File "/x/venv/lib/python3.14/site-packages/acp/task/supervisor.py", line 51, in _on_done
            task.result()
            ~~~~~~~~~~~^^
          File "/x/venv/lib/python3.14/site-packages/acp/connection.py", line 208, in _run_request
            raise err from None
        acp.exceptions.RequestError: Internal error

        """
        // Arrives in pieces that do not end on lines.
        let half = recorded.index(recorded.startIndex, offsetBy: 150)
        #expect(filter.push(String(recorded[..<half])) + filter.push(String(recorded[half...])) + filter.finish() == "")
        // A start-up failure's own words pass.
        let missing = "The ACP server can't start: its protocol packages are missing from this install.\nFrom the Hermes environment, run: hermes pm install --extra acp\n"
        #expect(HermesAgent.StderrFilter().push(missing) == missing)
        let tail = HermesAgent.StderrFilter()
        #expect(tail.push("Then restart Hermes.") == "" && tail.finish() == "Then restart Hermes.")
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
        // The recorded answers: a configured Hermes names its provider beside the terminal setup, an unconfigured one the setup alone.
        func result(_ fixture: String) throws -> [String: Any] { try #require((try JSONSerialization.jsonObject(with: try Self.fixture(fixture)[0]) as? [String: Any])?["result"] as? [String: Any]) }
        #expect(HermesAgent.provider(initialize: try result("answer")) == "custom")
        #expect(HermesAgent.provider(initialize: try result("unconfigured")) == nil)
        let signedIn = try #require(HermesAgent.parseInitialize(Data("warning line\n".utf8) + Self.probeAnswer(provider: "openrouter")))
        #expect(signedIn.answered && signedIn.provider == "openrouter" && signedIn.version == "0.23.1")
        let status = CLIAccountSupport.hermesStatus(signedIn)
        #expect(status.loggedIn == true && status.account == "openrouter" && status.methodId == CLIAccountMethod.hermesProvider && !status.canSignOut)
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
        // The recorded picker: a custom endpoint's model under both of its ids, and the providers Hermes found credentials for.
        #expect(models.count == 20 && models.contains { $0.value == Self.model } && models.contains { $0.value == Self.namedModel } && models[0].value == "anthropic:claude-fable-5-2")
        #expect(models[0].displayName == "Anthropic · claude-fable-5-2" && models[0].description == L("provider.catalog.hermesModelRow", ["provider": "anthropic"]))
        #expect(models.allSatisfy { CoreValidation.model($0.value) })
        // A session that held an unusable model listed it first; the next session's picker replaces the whole list.
        var stale = try #require(session["configOptions"] as? [[String: Any]])
        let at = try #require(stale.firstIndex { $0["id"] as? String == "model" })
        stale[at]["options"] = [["name": "Auto · anthropic/claude-opus-4.6", "value": "auto:anthropic/claude-opus-4.6"]] + ((stale[at]["options"] as? [[String: Any]]) ?? [])
        HermesAgent.saveModels(configOptions: stale, to: cache)
        #expect(HermesAgent.cachedCatalog(url: cache).models.map(\.value).contains("auto:anthropic/claude-opus-4.6"))
        HermesAgent.saveModels(configOptions: session["configOptions"], to: cache)
        let catalog = HermesAgent.cachedCatalog(url: cache)
        #expect(catalog.source == "cli" && catalog.models.map(\.value) == ["default"] + models.map(\.value))
        // A session without a picker changes nothing; a Hermes without a provider drops the list.
        HermesAgent.saveModels(configOptions: [[String: Any]](), to: cache)
        #expect(HermesAgent.cachedCatalog(url: cache).models.count == 21)
        HermesAgent.forgetModels(at: cache)
        #expect(HermesAgent.cachedCatalog(url: cache).models.map(\.value) == ["default"])
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
        #expect(SetupProber.versionNumbers("Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\nInstall directory: /Users/x/.hermes/hermes-agent\nInstall method: git\nPython: 3.14.9") == [0, 23, 1])
    }

    // MARK: Session list

    static let sessionID = answerSession
    static let earlier = resumedSession
    @Test func acpConversationsOfTheWorkspaceFolderAreListedFromStateDB() throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let workspace = directory.appendingPathComponent("project"); try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let home = directory.appendingPathComponent("home")
        let data = home.appendingPathComponent(".hermes"); try FileManager.default.createDirectory(at: data, withIntermediateDirectories: true)
        let path = workspace.resolvingSymlinksInPath().path
        let now = Date(), t = now.timeIntervalSince1970
        let continued = "local-" + String(repeating: "7", count: 64)
        // The record's shape as Hermes 0.23.1 writes it; `yolo` is there only for a session created with it.
        let policy = #"{"policy":{"cwd":"/tmp/project","edit_approval_policy":"ask","request_json":"{\"cwd\":\"/tmp/project\",\"editor\":{},\"source\":\"acp\",\"yolo\":true}","source":"acp"},"session_id":"\#(Self.sessionID)"}"#
        let asking = #"{"policy":{"request_json":"{\"cwd\":\"/tmp/project\",\"editor\":{},\"source\":\"acp\"}","source":"acp"},"session_id":"\#(Self.earlier)"}"#
        try sqlite(data.appendingPathComponent("state.db"), [
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, created_source TEXT, model TEXT, model_config TEXT, parent_session_id TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0, cwd TEXT, title TEXT, last_activity_at REAL, archived INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0)",
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL)",
            "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)",
            // The owner stores an editor's thread as `local`, created by `acp`.
            "INSERT INTO sessions VALUES ('\(Self.sessionID)', 'local', 'acp', 'anthropic/claude-sonnet-4.5', NULL, NULL, \(t - 900), 4, '\(path)', 'Clean the build', \(t - 800), 0, 0)",
            // Its compressed continuation: the same conversation, resumed under the first id.
            "INSERT INTO sessions VALUES ('\(continued)', 'local', 'acp', 'anthropic/claude-sonnet-4.5', NULL, '\(Self.sessionID)', \(t - 300), 2, '\(path)', NULL, \(t - 100), 0, 0)",
            "INSERT INTO sessions VALUES ('\(Self.earlier)', 'acp', NULL, NULL, '{\"cwd\":\"\(path)\"}', NULL, \(t - 5000), 2, NULL, NULL, NULL, 0, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000cli1', 'cli', 'cli', NULL, NULL, NULL, \(t - 50), 2, '\(path)', 'A terminal chat', \(t - 40), 0, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000else', 'acp', 'acp', NULL, NULL, NULL, \(t - 50), 2, '/elsewhere', 'Other folder', \(t - 40), 0, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000arch', 'acp', 'acp', NULL, NULL, NULL, \(t - 50), 2, '\(path)', 'Archived', \(t - 40), 1, 0)",
            "INSERT INTO sessions VALUES ('20261110_090000_0000none', 'acp', 'acp', NULL, NULL, NULL, \(t - 50), 0, '\(path)', 'Never used', \(t - 40), 0, 0)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(Self.sessionID)', 'user', 'clean the build folder', 1)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(Self.sessionID)', 'assistant', 'done', 2)",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(continued)', 'user', 'and write notes', \(t - 20))",
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ('\(Self.earlier)', 'user', '[{\"type\":\"text\",\"text\":\"what is in this picture?\"},{\"type\":\"image_url\"}]', 4)",
            "INSERT INTO state_meta VALUES ('gateway.local_policy.v1:\(Self.sessionID)', '\(policy)')",
            "INSERT INTO state_meta VALUES ('gateway.local_policy.v1:\(Self.earlier)', '\(asking)')",
        ])
        let listing = ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, now: now), provider: "hermes")
        #expect(listing.items.map(\.sessionID) == [Self.sessionID, Self.earlier])
        let first = try #require(listing.items.first)
        #expect(first.title == "clean the build folder" && first.requests == 2 && first.model == "anthropic/claude-sonnet-4.5" && first.provider == "hermes")
        // `last_activity_at` stays near the start: the newest message dates the conversation.
        #expect(abs(first.modified.timeIntervalSince1970 - (t - 20)) < 1)
        #expect(listing.items.last?.title == "what is in this picture?")
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, excluding: [Self.sessionID], now: now), provider: "hermes").items.count == 1)
        // `HERMES_HOME` moves the store.
        #expect(HermesAgent.database(home: home, environment: ["HERMES_HOME": "/opt/hermes"]).path == "/opt/hermes/state.db")
        // Whether a session was created with --yolo, which a resume cannot change.
        let database = HermesAgent.database(home: home, environment: [:])
        #expect(HermesAgent.startedWithFullAccess(sessionID: Self.sessionID, database: database) == true)
        #expect(HermesAgent.startedWithFullAccess(sessionID: Self.earlier, database: database) == false)
        #expect(HermesAgent.startedWithFullAccess(sessionID: "local-" + String(repeating: "0", count: 64), database: database) == nil)
        // A session whose turns all failed holds no messages and is not one to continue.
        #expect(HermesAgent.messageCount(sessionID: Self.sessionID, database: database) == 4)
        #expect(HermesAgent.messageCount(sessionID: "20261110_090000_0000none", database: database) == 0)
        #expect(HermesAgent.messageCount(sessionID: "local-" + String(repeating: "0", count: 64), database: database) == nil)
        #expect(CoreValidation.sessionIdentifier(Self.sessionID) && !CoreValidation.sessionIdentifier("local-../x"))
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
    /// `acp`: what `hermes config get --json platform_toolsets.acp` answers (nil: not set); `config set` rewrites it.
    private func fakeHermes(_ directory: URL, fixture: String, hold: Bool = false, acp: String? = #"["hermes-acp", "clarify"]"#, toolsets: String = #"["hermes-cli"]"#) throws -> URL {
        if let acp { try Data(acp.utf8).write(to: directory.appendingPathComponent("acp.json")) }
        try Data(toolsets.utf8).write(to: directory.appendingPathComponent("toolsets.json"))
        let binary = directory.appendingPathComponent("hermes")
        let fixtureFile = Self.root.appendingPathComponent("native/contracts/fixtures/hermes-acp-\(fixture).jsonl").path
        let source = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf 'Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\\nInstall directory: /tmp/hermes\\nInstall method: git\\n'; exit 0; fi
        if [ "$1" = "config" ]; then
          cd '\(directory.path)'
          printf '%s\\n' "$*" >> config.txt
          case "$2 $3 $4" in
            "get --json platform_toolsets.acp") if [ -f acp.json ]; then cat acp.json; exit 0; fi; printf 'Config key not set: platform_toolsets.acp\\n' >&2; exit 1 ;;
            "get --json toolsets") cat toolsets.json; exit 0 ;;
            "set platform_toolsets.acp "*) printf '%s' "$4" > acp.json; printf 'Set\\n'; exit 0 ;;
          esac
          exit 2
        fi
        printf '2026-11-11 01:18:54 [INFO] acp_adapter.entry: Starting hermes-agent ACP adapter\\n2026-11-11 01:18:54 [ERROR] root: Background task failed\\nTraceback (most recent call last):\\n  File "x.py", line 1, in run\\nacp.exceptions.RequestError: Internal error\\n' >&2
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
    @Test func theClarifyToolIsTurnedOnOnlyWhenTheUserSavedNoAcpToolList() async throws {
        func look(acp: String?, toolsets: String = #"["hermes-cli"]"#) async throws -> (HermesQuestionTool.Outcome?, [String], String?) {
            let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
            let binary = try fakeHermes(directory, fixture: "answer", acp: acp, toolsets: toolsets)
            let outcome = await HermesQuestionTool.apply(executable: binary, environment: ["PATH": "/usr/bin:/bin"], cwd: directory)
            let commands = ((try? String(contentsOf: directory.appendingPathComponent("config.txt"), encoding: .utf8)) ?? "").split(separator: "\n").map(String.init)
            return (outcome, commands, try? String(contentsOf: directory.appendingPathComponent("acp.json"), encoding: .utf8))
        }
        // Not set: the default composite plus clarify, through `hermes config set`, then read back.
        var (outcome, commands, saved) = try await look(acp: nil)
        #expect(outcome == .enabled(["hermes-acp", "clarify", "x_search"]) && saved == #"["hermes-acp","clarify","x_search"]"#)
        #expect(commands == ["config get --json platform_toolsets.acp", "config get --json toolsets", #"config set platform_toolsets.acp ["hermes-acp","clarify","x_search"]"#, "config get --json platform_toolsets.acp"])
        // The legacy top-level kanban opt-in would stop applying once a list is saved: it is named.
        (outcome, commands, saved) = try await look(acp: nil, toolsets: #"["hermes-cli", "kanban"]"#)
        #expect(outcome == .enabled(["hermes-acp", "clarify", "x_search", "kanban"]))
        // The user's own list is left alone: with clarify nothing is said, without it the pane says how to add it.
        (outcome, commands, saved) = try await look(acp: #"["hermes-acp", "clarify"]"#)
        #expect(outcome == .ready && commands == ["config get --json platform_toolsets.acp"])
        (outcome, commands, saved) = try await look(acp: #"["terminal", "file"]"#)
        #expect(outcome == .missing && commands == ["config get --json platform_toolsets.acp"] && saved == #"["terminal", "file"]"#)
        // A list an older `config set` saved as text reads as the list it spells.
        (outcome, commands, saved) = try await look(acp: #""['terminal', 'clarify']""#)
        #expect(outcome == .ready && commands.count == 1)
        // An answer that is not a list changes nothing and is asked again later.
        (outcome, commands, saved) = try await look(acp: "not json")
        #expect(outcome == nil && commands.count == 1 && saved == "not json")
        #expect(HermesQuestionTool.value(legacyToolsets: []) == ["hermes-acp", "clarify", "x_search"])
    }

    @Test func aRunTurnsClarifyOnOnceAndSaysSoInThePane() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakeHermes(directory, fixture: "answer", acp: nil)
        let service = ProviderService(binaryOverrides: ["hermes": binary])
        let events = HermesRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        let notice = L("run.notice.hermesClarifyEnabled", ["key": "platform_toolsets.acp", "value": "hermes-acp, clarify, x_search"])
        do {
            for round in 1...2 {
                try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi there", provider: "hermes"), workspace: workspace)
                try await wait { events.values().filter { $0.type == "status" && ($0.status == "completed" || $0.status == "error") }.count == round }
            }
            // Said once, set once: the second run of this launch does not look again.
            #expect(events.values().filter { $0.type == "log" && $0.entry?.text == notice }.count == 1)
            let commands = try String(contentsOf: directory.appendingPathComponent("config.txt"), encoding: .utf8).split(separator: "\n")
            #expect(commands.filter { $0.hasPrefix("config set ") }.count == 1 && commands.count == 4)
            #expect(try String(contentsOf: directory.appendingPathComponent("acp.json"), encoding: .utf8) == #"["hermes-acp","clarify","x_search"]"#)
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
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
            #expect(all.contains { $0.type == "log" && $0.entry?.kind == "assistant" && $0.entry?.text == "Hi." })
            #expect(all.contains { $0.type == "resume" && $0.resumeId == (mode == "fullAccess" ? Self.fullAccessSession : Self.answerSession) })
            #expect(all.contains { $0.type == "usage" && $0.usage?.provider == "hermes" && $0.usage?.totalTokens == (mode == "fullAccess" ? 11192 : 11194) })
            // Hermes's log and tracebacks on stderr are not the pane's.
            #expect(!all.contains { $0.type == "log" && ($0.entry?.text.contains("[INFO]") == true || $0.entry?.text.contains("Traceback") == true || $0.entry?.text.contains("RequestError") == true) })
            // The first line of `hermes --version` is the version.
            #expect(await service.command(provider: "hermes")?.version == "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882")
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

    @Test func aSessionWithoutAnAnsweredTurnIsNotContinued() async throws {
        // The pane's first request failed before a provider was set up: its session holds no messages
        // and keeps the provider it was created with, so the next request starts a new one.
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let store = directory.appendingPathComponent(".hermes"); try FileManager.default.createDirectory(at: store, withIntermediateDirectories: true)
        try sqlite(store.appendingPathComponent("state.db"), [
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, created_source TEXT, model TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0, cwd TEXT)",
            "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)",
            "INSERT INTO sessions VALUES ('\(Self.staleSession)', 'local', 'acp', 'anthropic/claude-opus-4.6', 1, 0, '\(directory.path)')",
        ])
        let binary = try fakeHermes(directory, fixture: "answer")
        let service = ProviderService(binaryOverrides: ["hermes": binary], environment: ["PATH": "/usr/bin:/bin", "HOME": directory.path])
        let events = HermesRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            var request = StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi there", provider: "hermes")
            request.resumeId = Self.staleSession
            try await runner.start(request: request, workspace: workspace)
            try await wait { events.values().contains { $0.status == "completed" || $0.status == "error" } }
            #expect(events.values().last { $0.type == "status" }?.status == "completed")
            let stdin = try String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(stdin.count == 3 && stdin[1].contains("\"session/new\"") && !stdin.contains { $0.contains("session/resume") })
            #expect(events.values().contains { $0.type == "resume" && $0.resumeId == Self.answerSession })
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
        #expect(HermesAgent.versionLine("Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\nInstall directory: /Users/x/.hermes/hermes-agent\nUp to date") == "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882")
    }
}
