import Foundation
import Testing
@testable import MightyCore

/// Who each block is, as the tracker records it (`GraphAgentIdentity`): the Agent call's `subagent_type`, `model` and
/// `isolation`, the model the CLI resolved, the type it named, and the pane's effort on the run.
///
/// native/contracts/fixtures/claude-stream-subagents.jsonl and claude-stream-fork-worktree.jsonl are recordings of
/// Claude Code 2.1.322 (Haiku, `--print --verbose --output-format stream-json`). The first is a foreground and then a
/// background Explore sub-agent with the flags the app runs Claude with (`--input-format stream-json
/// --permission-prompts host --permission-prompt-tool stdio`, stdin left open): the request's result comes when its
/// turn ends and the background agent's notice opens a turn of its own. The second is a fork and a sub-agent in a
/// worktree, recorded with a prompt argument and `--forward-subagent-text`, which the app does not pass: it adds the
/// foreground agent's thinking and text frames, and both results come at the end. Hook and rate-limit frames are
/// dropped, the init frame is cut to what names the run, and ids and paths are replaced.
struct GraphAgentIdentityTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static func fixture(_ name: String) throws -> [Data] {
        let data = try Data(contentsOf: root.appendingPathComponent("native/contracts/fixtures/\(name).jsonl"))
        return data.split(separator: 10).map { Data($0) }
    }

    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    private func spawn(_ id: String, _ input: [String: Any]) -> [String: Any] {
        ["type": "assistant", "uuid": "call-" + id, "session_id": "main", "message": ["id": "call-" + id, "content": [["type": "tool_use", "id": id, "name": "Agent", "input": input]]]]
    }
    private func latest(_ nodes: [ExecutionGraphNode], tool: String, run: String = "run") throws -> ExecutionGraphNode {
        let id = ExecutionGraphSupport.agentNodeID(runId: run, toolUseId: tool)
        return try #require(nodes.last { $0.id == id })
    }
    private func parser(_ nodes: Box<[ExecutionGraphNode]>, run: String = "run") -> CLIStreamParser {
        CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: run, graph: { nodes.value.append($0) }, graphInput: "request")
    }
    final class Box<Value>: @unchecked Sendable { var value: Value; init(_ value: Value) { self.value = value } }

    /// A pane whose request runs through a parser that records its graph as the app does.
    private func pane(runID: String, effort: String? = nil) -> (CLIStreamParser, () -> RunSession) {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        if let effort { session.settings.effort = effort }
        let box = Box(session)
        box.value.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: runID,
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
        return (parser, { box.value })
    }

    @Test func theAgentCallNamesItsTypeItsModelAndItsIsolation() throws {
        let nodes = Box<[ExecutionGraphNode]>([])
        let parser = parser(nodes)
        try send(spawn("a", ["subagent_type": "Explore", "model": "haiku", "isolation": "worktree", "description": "Map the repo", "prompt": "Look around", "run_in_background": true]), to: parser)
        let block = try latest(nodes.value, tool: "a")
        #expect(block.identity == GraphAgentIdentity(type: "Explore", requestedModel: "haiku", isolation: "worktree"))
        #expect(block.background == true)
        // The title rule is as it was: the description names the block.
        #expect(block.title == "Map the repo")
        // A call that says nothing about itself records nothing.
        try send(spawn("b", ["description": "Plain", "prompt": "Go"]), to: parser)
        #expect(try latest(nodes.value, tool: "b").identity == nil)
        // A type alone still titles a block without a description, as before.
        try send(spawn("c", ["subagent_type": "fork", "prompt": "Go on"]), to: parser)
        let fork = try latest(nodes.value, tool: "c")
        #expect(fork.title == "fork")
        #expect(fork.identity?.isFork == true)
    }

    @Test func theLaunchResultNamesTheModelThatRunsAndAForegroundResultTheTypeAndTheWorktree() throws {
        let nodes = Box<[ExecutionGraphNode]>([])
        let parser = parser(nodes)
        try send(spawn("bg", ["description": "Survey", "prompt": "Go", "model": "opus"]), to: parser)
        // The live stream's key, then a transcript's.
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "bg", "content": "Async agent launched successfully.\nagentId: a1"]]],
                  "tool_use_result": ["isAsync": true, "status": "async_launched", "agentId": "a1", "resolvedModel": "claude-opus-5-5"]], to: parser)
        let background = try latest(nodes.value, tool: "bg")
        #expect(background.identity == GraphAgentIdentity(requestedModel: "opus", resolvedModel: "claude-opus-5-5"))
        #expect(background.background == true)
        #expect(background.state == "running")

        try send(spawn("fg", ["description": "Count", "prompt": "Go"]), to: parser)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "fg", "content": "3"]]],
                  "toolUseResult": ["status": "completed", "agentType": "general-purpose", "resolvedModel": "claude-haiku-5-5",
                                    "worktreePath": "/tmp/probe/.claude/worktrees/agent-1", "worktreeBranch": "worktree-agent-1"]], to: parser)
        let foreground = try latest(nodes.value, tool: "fg")
        #expect(foreground.identity == GraphAgentIdentity(type: "general-purpose", resolvedModel: "claude-haiku-5-5", isolation: "worktree"))
        #expect(foreground.state == "completed")
        #expect(foreground.output == "3")
    }

    @Test func theCLINamesTheTypeOfACallThatLeftItUnsaidAndNeverReplacesTheCallsOwn() throws {
        let nodes = Box<[ExecutionGraphNode]>([])
        let parser = parser(nodes)
        try send(spawn("plain", ["description": "Plain", "prompt": "Go"]), to: parser)
        try send(["type": "system", "subtype": "task_started", "task_id": "t1", "tool_use_id": "plain", "subagent_type": "general-purpose",
                  "is_backgrounded": false, "task_type": "local_agent"], to: parser)
        #expect(try latest(nodes.value, tool: "plain").identity == GraphAgentIdentity(type: "general-purpose"))
        try send(spawn("named", ["subagent_type": "Explore", "description": "Named", "prompt": "Go"]), to: parser)
        try send(["type": "system", "subtype": "task_started", "task_id": "t2", "tool_use_id": "named", "subagent_type": "other"], to: parser)
        #expect(try latest(nodes.value, tool: "named").identity?.type == "Explore")
        // A background command's start is no agent's.
        try send(["type": "assistant", "uuid": "sh", "message": ["id": "sh", "content": [["type": "tool_use", "id": "shell", "name": "Bash", "input": ["command": "sleep 5", "run_in_background": true]]]]], to: parser)
        try send(["type": "system", "subtype": "task_started", "task_id": "t3", "tool_use_id": "shell", "subagent_type": "ghost", "task_type": "local_bash"], to: parser)
        #expect(try latest(nodes.value, tool: "shell").identity == nil)
        #expect(try latest(nodes.value, tool: "shell").kind == "task")
    }

    @Test func modsNamesTheTypeTheRequestedModelWhileItStartsAndTheResolvedOneOnceItRuns() throws {
        let nodes = Box<[ExecutionGraphNode]>([])
        let parser = parser(nodes)
        func mod(_ phase: String, model: String?, event: String = "agent.spawn") -> ModMetadata {
            ModMetadata(claudeSessionId: "main", event: event,
                        graph: ModGraphMetadata(phase: phase, agentId: phase == "starting" ? nil : "agent-1", parentToolUseId: "m", name: "Review", agentType: "code-reviewer", model: model, input: "Review it"))
        }
        parser.receiveMod(mod("starting", model: "sonnet"))
        #expect(try latest(nodes.value, tool: "m").identity == GraphAgentIdentity(type: "code-reviewer", requestedModel: "sonnet"))
        parser.receiveMod(mod("running", model: "claude-sonnet-5-5"))
        #expect(try latest(nodes.value, tool: "m").identity == GraphAgentIdentity(type: "code-reviewer", requestedModel: "sonnet", resolvedModel: "claude-sonnet-5-5"))
        // A start that failed names no model that ran.
        parser.receiveMod(ModMetadata(claudeSessionId: "main", event: "agent.spawn",
                                      graph: ModGraphMetadata(phase: "error", parentToolUseId: "denied", name: "Denied", agentType: "Explore", model: "opus", output: "denied")))
        #expect(try latest(nodes.value, tool: "denied").identity == GraphAgentIdentity(type: "Explore"))
    }

    @Test func aNoticeRunsCopyIsWhoItsBlockIs() throws {
        let (parser, session) = pane(runID: "proc")
        try send(spawn("bg", ["subagent_type": "Explore", "model": "haiku", "description": "Survey", "prompt": "Go", "run_in_background": true]), to: parser)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "bg", "content": "Async agent launched successfully.\nagentId: a1"]]],
                  "tool_use_result": ["isAsync": true, "status": "async_launched", "agentId": "a1", "resolvedModel": "claude-haiku-5-5"]], to: parser)
        try send(["type": "result", "subtype": "success", "is_error": false, "result": "launched"], to: parser)
        try send(["type": "system", "subtype": "task_notification", "task_id": "a1", "tool_use_id": "bg", "status": "completed", "summary": "Done"], to: parser)
        try send(["type": "result", "subtype": "success", "is_error": false, "result": "done", "origin": ["kind": "task-notification"]], to: parser)
        parser.flush()
        let runs = session().mightyGraphRuns
        #expect(runs.count == 2)
        let expected = GraphAgentIdentity(type: "Explore", requestedModel: "haiku", resolvedModel: "claude-haiku-5-5")
        let original = try #require(runs[0].agents.first)
        #expect(original.identity == expected)
        let copy = try #require(runs[1].agents.first)
        #expect(copy.sourceBlockId == original.id)
        #expect(copy.identity == expected)
    }

    @Test func theIdentityIsBoundedAndOnlyASubagentBlockKeepsOne() throws {
        let long = String(repeating: "x", count: 400)
        let node = ExecutionGraphNode(id: "n", runId: "r", kind: "agent", state: "running", title: "t",
                                      identity: GraphAgentIdentity(type: " multi\nline " + long, requestedModel: "two words", resolvedModel: "claude-opus-5-5", isolation: "sandbox"))
        let kept = try #require(ExecutionGraphSupport.normalized(node)?.identity)
        #expect(kept.type?.contains("\n") == false)
        #expect((kept.type?.utf8.count ?? 0) <= 160)
        #expect(kept.requestedModel == nil)
        #expect(kept.resolvedModel == "claude-opus-5-5")
        #expect(kept.isolation == nil)
        // Nothing left is no identity, and a task block has none.
        let empty = ExecutionGraphNode(id: "n", runId: "r", kind: "agent", state: "running", title: "t", identity: GraphAgentIdentity(requestedModel: "a b"))
        #expect(ExecutionGraphSupport.normalized(empty)?.identity == nil)
        let task = ExecutionGraphNode(id: "n", runId: "r", kind: "task", state: "running", title: "t", identity: GraphAgentIdentity(type: "Explore"))
        #expect(ExecutionGraphSupport.normalized(task)?.identity == nil)
    }

    @Test func olderSavesAndEventsDecodeAndABlockWithoutAnIdentityEncodesNone() throws {
        let old = #"{"id":"n","runId":"r","kind":"agent","state":"completed","title":"Old","entries":[],"updatedAt":"2026-01-01T00:00:00Z"}"#
        let node = try JSONDecoder().decode(ExecutionGraphNode.self, from: Data(old.utf8))
        #expect(node.identity == nil)
        #expect(!String(decoding: try JSONEncoder().encode(node), as: UTF8.self).contains("identity"))
        let agent = try JSONDecoder().decode(MightyGraphAgent.self, from: Data(#"{"id":"a","title":"Old","input":"","status":"completed","entries":[]}"#.utf8))
        #expect(agent.identity == nil)
        #expect(!String(decoding: try JSONEncoder().encode(agent), as: UTF8.self).contains("identity"))
        let run = try JSONDecoder().decode(MightyGraphRun.self, from: Data(#"{"id":"r","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[]}"#.utf8))
        #expect(run.configuredEffort == nil)
        #expect(!String(decoding: try JSONEncoder().encode(run), as: UTF8.self).contains("configuredEffort"))
        // What is there travels, and only what is there.
        var named = agent
        named.identity = GraphAgentIdentity(type: "Explore", requestedModel: "haiku")
        let text = String(decoding: try JSONEncoder().encode(named), as: UTF8.self)
        #expect(text.contains(#""identity":{"#) && text.contains(#""type":"Explore""#) && !text.contains("resolvedModel") && !text.contains("isolation"))
        #expect(try JSONDecoder().decode(MightyGraphAgent.self, from: Data(text.utf8)).identity == named.identity)
    }

    @Test func aSavedGraphKeepsTheIdentityOfSubagentsAndTheRunsEffort() {
        var budget = 1_000_000
        let agents = [MightyGraphAgent(id: "a", title: "Agent", status: "completed", identity: GraphAgentIdentity(type: "Explore", requestedModel: "bad model", isolation: "worktree")),
                      MightyGraphAgent(id: "t", title: "Task", status: "completed", kind: "task", identity: GraphAgentIdentity(type: "Explore"))]
        let saved = MightyGraphSupport.normalized([MightyGraphRun(id: "r", status: "completed", agents: agents, configuredEffort: "high"),
                                                   MightyGraphRun(id: "s", status: "completed", configuredEffort: "extreme")], restoring: true, budget: &budget)
        #expect(saved[0].agents[0].identity == GraphAgentIdentity(type: "Explore", isolation: "worktree"))
        #expect(saved[0].agents[1].identity == nil)
        #expect(saved[0].configuredEffort == "high")
        #expect(saved[1].configuredEffort == nil)
    }

    @Test func theRunKeepsTheEffortItStartedWith() {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.beginGraphRun(input: "one", id: "one", configuredModel: "opus", configuredEffort: "high")
        #expect(session.graphRuns?.last?.configuredEffort == "high")
        // Settings › Models gives its effort to a pane left on Auto, once the run is about to start.
        session.beginGraphRun(input: "two", id: "two", configuredModel: "default", configuredEffort: "default")
        session.noteGraphRunEffort("medium", runID: "two")
        #expect(session.graphRuns?.last?.configuredEffort == "medium")
        #expect(session.graphRuns?.first?.configuredEffort == "high")
        session.noteGraphRunEffort("not-a-level", runID: "two")
        session.noteGraphRunEffort("low", runID: "missing")
        #expect(session.graphRuns?.map(\.configuredEffort) == ["high", "medium"])
        // A request another client sent takes the pane's own effort.
        var remote = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        remote.settings.effort = "xhigh"
        remote.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "sent", kind: "user", text: "from the phone", provider: "claude")))
        #expect(remote.graphRuns?.last?.configuredEffort == "xhigh")
    }

    // MARK: Recordings

    @Test func theRecordedForegroundAndBackgroundSubagentsAreWhoTheCLISaid() throws {
        let (parser, session) = pane(runID: "recorded-a", effort: "default")
        for line in try Self.fixture("claude-stream-subagents") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        let runs = session().mightyGraphRuns
        #expect(runs.first?.noticeRun != true)
        #expect(runs.first?.finalOutput == "launched.")
        let agents = runs[0].agents.filter(\.isSubagent)
        #expect(agents.map(\.title) == ["count a", "count b and c"])
        let expected = GraphAgentIdentity(type: "Explore", requestedModel: "haiku", resolvedModel: "claude-haiku-5-5")
        #expect(agents.map(\.identity) == [expected, expected])
        #expect(agents.map { $0.background == true } == [false, true])
        #expect(agents.allSatisfy { $0.status == "completed" })
        // Each block's own responses name the model that answered.
        for agent in agents { #expect(Set((agent.responseRecords ?? []).compactMap(\.model)) == ["claude-haiku-5-5"]) }
        // The background agent's end opened a notice turn, whose run holds its copy and Claude's answer.
        let notice = try #require(runs.last { run in run.noticeRun == true && run.agents.contains { $0.sourceBlockId == agents[1].id } })
        #expect(notice.finalOutput == "done.")
        #expect(notice.agents.first?.identity == expected)
        // The CLI reports a foreground agent's end as a task notification too, before its result names the model: its
        // copy is still who the block is.
        for run in runs.dropFirst() { for copy in run.agents where copy.sourceBlockId == agents[0].id { #expect(copy.identity == expected) } }
        #expect(FlowRoles.subagent(agents[1])?.marks == [.background])
        #expect(FlowRoles.subagent(agents[0])?.marks.isEmpty == true)
        #expect(runs[0].configuredEffort == "default")
    }

    @Test func theRecordedForkAndWorktreeSubagentsCarryTheirMarks() throws {
        let (parser, session) = pane(runID: "recorded-b")
        for line in try Self.fixture("claude-stream-fork-worktree") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        let runs = session().mightyGraphRuns
        let agents = (runs.first?.agents ?? []).filter(\.isSubagent)
        #expect(agents.map(\.title) == ["fork count a", "worktree count b"])
        // A fork names no model: it runs on its caller's, which the launch result names.
        #expect(agents[0].identity == GraphAgentIdentity(type: "fork", resolvedModel: "claude-haiku-5-5"))
        #expect(agents[0].background == true)
        #expect(FlowRoles.subagent(agents[0])?.marks == [.background, .fork])
        #expect(agents[1].identity == GraphAgentIdentity(type: "general-purpose", requestedModel: "haiku", resolvedModel: "claude-haiku-5-5", isolation: "worktree"))
        #expect(FlowRoles.subagent(agents[1])?.marks == [.worktree])
        #expect(agents[1].background != true)
    }
}
