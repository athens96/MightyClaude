import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// A sub-agent as the flow's compact card (`FlowCard`): when it is one, what its four lines say, and the figures
/// the tracker keeps for it. Every figure is one that was observed; nothing on a card is a percentage.
struct FlowCardTests {
    private func agent(_ id: String = "a", title: String = "Map the repo", status: String = "running", background: Bool = false, kind: String? = nil,
                       parent: String? = nil, entries: [LogEntry] = [], input: String = "", source: String? = nil,
                       identity: GraphAgentIdentity? = nil, figures: GraphAgentFigures? = nil, usage: GraphTokenUsage? = nil,
                       records: [GraphResponseRecord]? = nil) -> MightyGraphAgent {
        MightyGraphAgent(id: id, parentID: parent, title: title, input: input, status: status, entries: entries, kind: kind, usage: usage, responseRecords: records,
                         background: background ? true : nil, sourceBlockId: source, identity: identity, figures: figures)
    }
    private func tool(_ id: String, _ summary: String, name: String = "Read", state: String = "completed", kind: String = "tool") -> LogEntry {
        LogEntry(id: id, kind: "system", text: summary, provider: "claude",
                 activity: AgentActivity(id: id, provider: "claude", kind: kind, state: state, toolName: name, summary: summary))
    }
    private func said(_ id: String, _ text: String) -> LogEntry { LogEntry(id: id, kind: "assistant", text: text, provider: "claude") }

    // MARK: Card or block

    @Test func aRunningSubagentIsACardUntilTheUserOpensIt() {
        #expect(FlowCard.isCompact(agent(status: "running"), opened: false))
        #expect(!FlowCard.isCompact(agent(status: "running"), opened: true))
        #expect(FlowCard.isCompact(agent(status: "waiting"), opened: false))
    }

    @Test func aForegroundSubagentThatEndedIsItsFullBlockAsBefore() {
        for status in ["completed", "error", "stopped", "failed", "cancelled", "interrupted"] {
            #expect(!FlowCard.isCompact(agent(status: status), opened: false), "\(status)")
            #expect(!FlowCard.foldsToCard(agent(status: status)), "\(status)")
        }
    }

    @Test func aBackgroundSubagentOfTheRequestStaysACardWhenItEnded() {
        for status in ["running", "completed", "error", "stopped"] {
            #expect(FlowCard.isCompact(agent(status: status, background: true), opened: false), "\(status)")
            #expect(FlowCard.foldsToCard(agent(status: status, background: true)), "\(status)")
            // Opened in place, it is the block until it is folded back.
            #expect(!FlowCard.isCompact(agent(status: status, background: true), opened: true), "\(status)")
        }
    }

    @Test func aNoticeRunsCopyAndABlockThatIsNoSubagentAreNeverCards() {
        #expect(!FlowCard.isCompact(agent(status: "completed", background: true, source: "original"), opened: false))
        #expect(!FlowCard.isCompact(agent(status: "running", background: true, source: "original"), opened: false))
        // A background command is the one other kind with a card: the request's own, running or ended, until opened.
        for kind in MightyGraphSupport.childKinds where kind != "task" {
            #expect(!FlowCard.isCompact(agent(status: "running", background: true, kind: kind), opened: false), "\(kind)")
            #expect(!FlowCard.foldsToCard(agent(status: "running", kind: kind)), "\(kind)")
        }
        for status in ["running", "completed", "error"] {
            #expect(FlowCard.isCompact(agent(status: status, background: true, kind: "task"), opened: false), "\(status)")
            #expect(!FlowCard.isCompact(agent(status: status, background: true, kind: "task"), opened: true), "\(status)")
        }
        #expect(!FlowCard.isCompact(agent(status: "completed", background: true, kind: "task", source: "original"), opened: false))
    }

    // MARK: What the card says

    @Test func theCardNamesTheAgentItsTypeAndTheModelThatAnswered() {
        LocaleOverride.$language.withValue(.ko) {
            let run = MightyGraphRun(id: "r")
            let identity = GraphAgentIdentity(type: "Explore", requestedModel: "haiku")
            // Asked for, nothing answered yet.
            let asked = FlowCard.content(agent(identity: identity), in: run)
            #expect(asked.title == "Map the repo")
            #expect(asked.identity == "Explore · Haiku · 요청")
            #expect(asked.status == "running" && asked.stateWord == "실행 중" && asked.running)
            // The CLI resolved it at launch.
            var resolved = identity; resolved.resolvedModel = "claude-haiku-5-5"
            #expect(FlowCard.content(agent(identity: resolved), in: run).identity == "Explore · Haiku 5.5")
            // Its own responses name the model that answered.
            let records = [GraphResponseRecord(responseId: "m1", model: "claude-sonnet-5-5", usage: GraphTokenUsage(inputTokens: 10, outputTokens: 5))]
            #expect(FlowCard.content(agent(identity: identity, records: records), in: run).identity == "Explore · Sonnet 5.5")
            // A plugin's agent by its own name; a fork by its mark alone; nothing known, no line.
            #expect(FlowCard.content(agent(identity: GraphAgentIdentity(type: "tools:executor")), in: run).identity == "executor")
            let fork = FlowCard.content(agent(background: true, identity: GraphAgentIdentity(type: "fork", resolvedModel: "claude-opus-5-5")), in: run)
            #expect(fork.identity == "Opus 5.5")
            #expect(fork.marks == [.background, .fork])
            #expect(FlowCard.content(agent(), in: run).identity == nil)
            #expect(FlowCard.content(agent(), in: run).ink == .agent)
            // A custom agent's colour is the card's.
            #expect(FlowCard.content(agent(identity: GraphAgentIdentity(type: "reviewer")), in: run, definition: AgentDefinition(name: "reviewer", color: "green")).ink == .done)
        }
    }

    @Test func aRunningCardSaysItsLatestToolCallThenItsLastWordsThenWhatItWasAsked() {
        let asked = "Look for call sites of the tracker.\nThen report."
        // Nothing live yet: the first line of its prompt.
        #expect(FlowCard.doing(agent(input: asked)) == "Look for call sites of the tracker.")
        // It wrote something: its last line.
        #expect(FlowCard.doing(agent(entries: [said("t1", "Starting.\n\nReading the tracker next.\n")], input: asked)) == "Reading the tracker next.")
        // A tool call outranks its words, the latest one first, whatever came after it.
        let entries = [tool("a", "Sources/A.swift 읽기"), said("t1", "Found it."), tool("b", "grep tracker", name: "Grep", state: "running"), said("t2", "Now the tests.")]
        #expect(FlowCard.doing(agent(entries: entries, input: asked)) == "grep tracker")
        // A call without a summary is its tool's name; the run's own lifecycle row is no tool call.
        #expect(FlowCard.doing(agent(entries: [tool("a", "", name: "WebFetch")])) == "WebFetch")
        #expect(FlowCard.doing(agent(entries: [said("t1", "Thinking."), tool("turn", "응답 생성", name: "", kind: "turn")])) == "Thinking.")
        #expect(FlowCard.doing(agent()) == nil)
        // One line, bounded.
        let long = FlowCard.doing(agent(entries: [tool("a", String(repeating: "가", count: 400) + "\nsecond")]))
        #expect((long?.utf8.count ?? 0) <= FlowCard.lineBytes)
        #expect(long?.contains("\n") == false)
    }

    @Test func anEndedCardSaysTheFirstLineOfItsAnswer() {
        let run = MightyGraphRun(id: "r")
        let done = agent(status: "completed", background: true, entries: [tool("a", "a.txt 읽기"), said("answer", "\nb.txt: 3\nc.txt: 5")])
        let content = FlowCard.content(done, in: run)
        #expect(content.activity == "b.txt: 3")
        #expect(!content.running)
        // No answer: the last thing it recorded that is not a tool call, else nothing.
        let stopped = agent(status: "stopped", background: true, entries: [tool("a", "a.txt 읽기"), LogEntry(id: "note", kind: "system", text: "응답 없이 끝났습니다.", provider: "claude")])
        #expect(FlowCard.outcome(stopped) == "응답 없이 끝났습니다.")
        #expect(FlowCard.outcome(agent(status: "completed", entries: [tool("a", "a.txt 읽기")])) == nil)
    }

    @Test func theCardCountsTheSubagentsUnderItAtAnyDepth() {
        let agents = [agent("root"), agent("child", parent: "root"), agent("grand", parent: "child"), agent("note", kind: "compact", parent: "child"),
                      agent("other"), agent("loop-a", parent: "loop-b"), agent("loop-b", parent: "loop-a")]
        let run = MightyGraphRun(id: "r", agents: agents)
        #expect(FlowCard.nested(under: agents[0], in: run) == 2)
        #expect(FlowCard.nested(under: agents[1], in: run) == 1)
        #expect(FlowCard.nested(under: agents[4], in: run) == 0)
        // A cycle in a damaged save ends.
        #expect(FlowCard.nested(under: agents[5], in: run) == 1)
        #expect(FlowCard.content(agents[0], in: run).nested == 2)
    }

    // MARK: Figures

    @Test func theFiguresAreTheStateAndWhatWasObserved() {
        LocaleOverride.$language.withValue(.ko) {
            let run = MightyGraphRun(id: "r")
            let start = Date(timeIntervalSince1970: 1_800_000_000)
            let stamp = ISO8601DateFormatter().string(from: start)
            let usage = GraphTokenUsage(inputTokens: 300, outputTokens: 45, cacheReadTokens: 12_000)
            let running = FlowCard.content(agent(figures: GraphAgentFigures(startedAt: stamp, toolCalls: 3), usage: usage), in: run)
            #expect(FlowCard.figures(running, now: start.addingTimeInterval(42)) == "실행 중 · 42초 · 도구 3 · 토큰 12K")
            #expect(FlowCard.figures(running, now: start.addingTimeInterval(125)) == "실행 중 · 2분 5초 · 도구 3 · 토큰 12K")
            // A clock that reads before the start is no negative time.
            #expect(FlowCard.figures(running, now: start.addingTimeInterval(-5)) == "실행 중 · 0초 · 도구 3 · 토큰 12K")
            // Nothing live: the state alone.
            #expect(FlowCard.figures(FlowCard.content(agent(), in: run), now: start) == "실행 중")
            // No tool call yet is no "도구 0".
            #expect(FlowCard.figures(FlowCard.content(agent(figures: GraphAgentFigures(startedAt: stamp, toolCalls: 0)), in: run), now: start.addingTimeInterval(1)) == "실행 중 · 1초")
            // Ended: how long it ran, whatever the clock says now.
            let ended = ISO8601DateFormatter().string(from: start.addingTimeInterval(65))
            let done = FlowCard.content(agent(status: "completed", background: true, figures: GraphAgentFigures(startedAt: stamp, endedAt: ended, toolCalls: 2), usage: usage), in: run)
            #expect(FlowCard.figures(done, now: start.addingTimeInterval(9_999)) == "완료 · 1분 5초 · 도구 2 · 토큰 12K")
            // An end the app did not see (a restored run) names no time.
            let restored = FlowCard.content(agent(status: "stopped", background: true, figures: GraphAgentFigures(startedAt: stamp, toolCalls: 2)), in: run)
            #expect(FlowCard.figures(restored, now: start.addingTimeInterval(9_999)) == "중단 · 도구 2")
            let failed = FlowCard.content(agent(status: "error", background: true), in: run)
            #expect(FlowCard.figures(failed, now: start) == "실패")
            for text in [FlowCard.figures(running, now: start.addingTimeInterval(42)), FlowCard.figures(done, now: start)] { #expect(!text.contains("%")) }
        }
    }

    @Test func aFigureThatCannotBeReadBackIsDropped() {
        #expect(GraphAgentFigures(startedAt: "yesterday", endedAt: String(repeating: "9", count: 90), toolCalls: -1).normalized == nil)
        #expect(GraphAgentFigures(startedAt: "2026-11-10T18:56:44.496Z", toolCalls: GraphAgentFigures.maximumToolCalls + 1).normalized
                == GraphAgentFigures(startedAt: "2026-11-10T18:56:44.496Z"))
        #expect(GraphAgentFigures(startedAt: "2026-11-10T18:56:44Z").started != nil)
        #expect(GraphAgentFigures().isEmpty)
        #expect(GraphAgentFigures(exitCode: 3).normalized == GraphAgentFigures(exitCode: 3))
        #expect(GraphAgentFigures(exitCode: Int(Int32.max) + 1).normalized == nil)
        #expect(!GraphAgentFigures(exitCode: 0).isEmpty)
        // A sub-agent block and a background command's keep figures, on the wire and in a save; no other block does.
        let task = ExecutionGraphNode(id: "n", runId: "r", kind: "task", state: "running", title: "t", figures: GraphAgentFigures(exitCode: 2))
        #expect(ExecutionGraphSupport.normalized(task)?.figures == GraphAgentFigures(exitCode: 2))
        let steer = ExecutionGraphNode(id: "s", runId: "r", kind: "steer", state: "running", title: "s", figures: GraphAgentFigures(toolCalls: 2))
        #expect(ExecutionGraphSupport.normalized(steer)?.figures == nil)
        var budget = 100_000
        let saved = MightyGraphSupport.normalized([MightyGraphRun(id: "r", status: "completed", agents: [
            agent("a", status: "completed", figures: GraphAgentFigures(startedAt: "2026-11-10T18:56:44Z", endedAt: "soon", toolCalls: 4)),
            agent("t", status: "completed", kind: "task", figures: GraphAgentFigures(startedAt: "2026-11-10T18:56:44Z", exitCode: 0)),
            agent("q", status: "completed", kind: "question", figures: GraphAgentFigures(toolCalls: 4))])], restoring: true, budget: &budget)
        #expect(saved[0].agents[0].figures == GraphAgentFigures(startedAt: "2026-11-10T18:56:44Z", toolCalls: 4))
        #expect(saved[0].agents[1].figures == GraphAgentFigures(startedAt: "2026-11-10T18:56:44Z", exitCode: 0))
        #expect(saved[0].agents[2].figures == nil)
    }

    @Test func olderSavesHaveNoFiguresAndABlockWithoutThemEncodesNone() throws {
        let agent = try JSONDecoder().decode(MightyGraphAgent.self, from: Data(#"{"id":"a","title":"Old","input":"","status":"completed","entries":[]}"#.utf8))
        #expect(agent.figures == nil)
        #expect(!String(decoding: try JSONEncoder().encode(agent), as: UTF8.self).contains("figures"))
        var timed = agent
        timed.figures = GraphAgentFigures(startedAt: "2026-11-10T18:56:44Z", toolCalls: 2)
        let text = String(decoding: try JSONEncoder().encode(timed), as: UTF8.self)
        #expect(text.contains(#""figures":{"#) && text.contains(#""toolCalls":2"#) && !text.contains("endedAt"))
        #expect(try JSONDecoder().decode(MightyGraphAgent.self, from: Data(text.utf8)).figures == timed.figures)
    }

    // MARK: Where a finished background agent's card leads

    @Test func theCardLeadsToTheNewestCopyInANoticeRun() {
        let original = agent("block", status: "completed", background: true)
        let request = MightyGraphRun(id: "r", status: "completed", agents: [original])
        let first = MightyGraphRun(id: "r:notice:1", status: "completed", agents: [agent("r:notice:1:block", status: "completed", background: true, source: "block")], noticeRun: true)
        let second = MightyGraphRun(id: "r:notice:2", status: "completed", agents: [agent("r:notice:2:other", status: "completed", background: true, source: "other"),
                                                                                    agent("r:notice:2:block", status: "completed", background: true, source: "block")], noticeRun: true)
        #expect(FlowCard.copyNodeID(of: original, in: [request]) == nil)
        #expect(FlowCard.copyNodeID(of: original, in: [request, first]) == MightyGraphLayout.nodeID(first, suffix: "agent:r:notice:1:block"))
        #expect(FlowCard.copyNodeID(of: original, in: [request, first, second]) == MightyGraphLayout.nodeID(second, suffix: "agent:r:notice:2:block"))
        // Only a notice run holds a copy.
        let impostor = MightyGraphRun(id: "x", status: "completed", agents: [agent("x-copy", status: "completed", source: "block")])
        #expect(FlowCard.copyNodeID(of: original, in: [request, impostor]) == nil)
    }
}
