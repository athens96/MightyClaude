import Foundation
import Testing
@testable import MightyCore

struct CodexGraphProjectionTests {
    private func session() -> RunSession {
        var session = RunSession(workspaceId: "workspace", title: "Codex", provider: "codex")
        session.beginGraphRun(input: "Review the change", id: "request", configuredModel: "default")
        return session
    }
    private func node(state: String = "running", generation: Int? = nil, input: String? = nil,
                      output: String? = nil, entries: [LogEntry] = []) -> ExecutionGraphNode {
        ExecutionGraphNode(id: "child", runId: "process", parentId: ExecutionGraphSupport.mainNodeID(runId: "process"),
                           kind: "agent", state: state, title: "Review", input: input, output: output,
                           entries: entries, activityGeneration: generation)
    }
    private func receive(_ node: ExecutionGraphNode, into session: inout RunSession) {
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: node))
    }

    @Test(arguments: ["completed", "error", "stopped"])
    func explicitNewActivityReopensTerminalChildAndRetainsHistory(_ terminal: String) throws {
        var session = session()
        receive(node(state: terminal, input: "Original task", output: "First response"), into: &session)
        // A duplicate start from the original activity cannot reopen it.
        receive(node(input: "Late original task"), into: &session)
        #expect(session.mightyGraphRuns[0].agents[0].status == terminal)
        receive(node(generation: 1, entries: [LogEntry(id: "followup", kind: "user", text: "Check one more file")]), into: &session)
        let reopened = try #require(session.mightyGraphRuns.first?.agents.first)
        #expect(reopened.status == "running")
        #expect(reopened.activityGeneration == 1)
        #expect(reopened.input == "Original task")
        #expect(reopened.entries.map(\.text) == ["First response", "Check one more file"])
        receive(node(state: "completed", generation: 1, output: "Second response"), into: &session)
        let finished = try #require(session.mightyGraphRuns.first?.agents.first)
        #expect(finished.status == "completed")
        #expect(finished.entries.map(\.text) == ["First response", "Check one more file", "Second response"])
        #expect(Set(finished.entries.map(\.id)).count == 3)
        #expect(finished.entries.allSatisfy { $0.provider == "codex" })
    }

    @Test func alreadyCompletedNewActivityCanReplacePriorFailure() throws {
        var session = session()
        receive(node(state: "error", input: "Original", output: "First attempt failed"), into: &session)
        receive(node(state: "completed", generation: 1, output: "Followup succeeded"), into: &session)
        #expect(session.mightyGraphRuns[0].agents[0].status == "completed")
        #expect(session.mightyGraphRuns[0].agents[0].activityGeneration == 1)
        #expect(session.mightyGraphRuns[0].agents[0].entries.map(\.text) == ["First attempt failed", "Followup succeeded"])
    }

    @Test func trackerArchivedResponseUsesSameIdentityAsProjectedResponse() throws {
        var session = session()
        receive(node(state: "completed", output: "Prior response"), into: &session)
        let archive = LogEntry(id: ExecutionGraphSupport.identifier("process", "child:answer:0"), kind: "assistant", text: "Prior response", provider: "codex")
        receive(node(generation: 1, entries: [archive, LogEntry(id: "followup", kind: "user", text: "Continue")]), into: &session)
        #expect(session.mightyGraphRuns[0].agents[0].entries.map(\.text) == ["Prior response", "Continue"])
    }

    @Test func identicalRepliesFromDifferentActivitiesRemainSeparate() throws {
        var session = session()
        receive(node(state: "completed", output: "Done"), into: &session)
        let archive = LogEntry(id: ExecutionGraphSupport.identifier("process", "child:answer:0"), kind: "assistant", text: "Done", provider: "codex")
        let followup = LogEntry(id: "followup", kind: "user", text: "Check again")
        receive(node(generation: 1, entries: [archive, followup]), into: &session)
        receive(node(state: "completed", generation: 1, output: "Done", entries: [archive, followup]), into: &session)
        #expect(session.mightyGraphRuns[0].agents[0].entries.map(\.text) == ["Done", "Check again", "Done"])
    }

    @Test func lowerGenerationCannotOverwriteStateInputOrEntries() throws {
        var session = session()
        receive(node(state: "completed", input: "First task", output: "First answer"), into: &session)
        receive(node(generation: 2, entries: [LogEntry(id: "current-input", kind: "user", text: "Current task")]), into: &session)
        let before = session.mightyGraphRuns
        receive(node(state: "error", generation: 1, input: "Stale task", output: "Stale failure"), into: &session)
        receive(node(state: "completed", output: "Legacy late completion"), into: &session)
        #expect(session.mightyGraphRuns == before)
    }

    @Test(arguments: ["completed", "error", "stopped"])
    func terminalRunCannotBeReopenedByHigherGeneration(_ terminal: String) throws {
        var session = session()
        receive(node(state: "completed", generation: 1, output: "Done"), into: &session)
        session.recordGraph(RunEvent(sessionId: session.id, type: "status", status: terminal))
        let before = session.mightyGraphRuns
        receive(node(generation: 2, entries: [LogEntry(kind: "user", text: "Late followup")]), into: &session)
        #expect(session.mightyGraphRuns == before)
        receive(node(state: "completed", generation: 2, output: "New generation late answer"), into: &session)
        #expect(session.mightyGraphRuns == before)
        session.beginGraphRun(input: "Another request", id: "request-two", configuredModel: "default")
        receive(node(generation: 3), into: &session)
        #expect(session.mightyGraphRuns[0] == before[0])
        #expect(session.mightyGraphRuns[1].agents.isEmpty)
    }

    @Test func generationRoundTripsAndInvalidValuesAreDiscarded() throws {
        let original = node(generation: 7)
        let decoded = try JSONDecoder().decode(ExecutionGraphNode.self, from: JSONEncoder().encode(original))
        #expect(decoded.activityGeneration == 7)
        #expect(ExecutionGraphSupport.normalized(decoded, restoring: true)?.activityGeneration == 7)
        #expect(ExecutionGraphSupport.normalized(decoded, restoring: true)?.state == "stopped")
        for invalid in [-1, ExecutionGraphSupport.maximumActivityGeneration + 1, Int.max] {
            #expect(ExecutionGraphSupport.normalized(node(generation: invalid))?.activityGeneration == nil)
            var budget = 1 << 20
            let run = MightyGraphRun(id: "request", agents: [MightyGraphAgent(id: "child", activityGeneration: invalid)])
            #expect(MightyGraphSupport.normalized([run], restoring: true, budget: &budget)[0].agents[0].activityGeneration == nil)
        }
        let oldNode = try JSONDecoder().decode(ExecutionGraphNode.self, from: JSONEncoder().encode(node()))
        let oldAgent = try JSONDecoder().decode(MightyGraphAgent.self, from: JSONEncoder().encode(MightyGraphAgent(id: "old")))
        #expect(oldNode.activityGeneration == nil)
        #expect(oldAgent.activityGeneration == nil)
    }

    @Test func restoredActiveChildRetainsGenerationAndCannotReopenStoppedHistory() throws {
        var session = session()
        receive(node(generation: 4, input: "Task", entries: [LogEntry(id: "progress", kind: "system", text: "Working")]), into: &session)
        let data = try JSONEncoder().encode(session)
        var restored = try JSONDecoder().decode(RunSession.self, from: data)
        var budget = 1 << 20
        restored.graphRuns = MightyGraphSupport.normalized(restored.graphRuns ?? [], restoring: true, budget: &budget, provider: restored.provider)
        let saved = restored.mightyGraphRuns
        #expect(saved[0].status == "stopped")
        #expect(saved[0].agents[0].status == "stopped")
        #expect(saved[0].agents[0].activityGeneration == 4)
        receive(node(generation: 5), into: &restored)
        #expect(restored.mightyGraphRuns == saved)
    }

    @Test func codexFinalAndChildOutputsKeepProviderThroughNormalization() throws {
        var session = session()
        receive(node(state: "completed", output: "Child answer"), into: &session)
        receive(ExecutionGraphNode(id: ExecutionGraphSupport.mainNodeID(runId: "process"), runId: "process", kind: "main", state: "completed", title: "Main", output: "Final answer"), into: &session)
        let run = try #require(session.mightyGraphRuns.first)
        #expect(run.provider == "codex")
        #expect(run.rootEntries.last?.provider == "codex")
        #expect(run.resultEntries.first?.provider == "codex")
        #expect(run.agents[0].entries.first?.provider == "codex")
        var budget = 1 << 20
        let restored = MightyGraphSupport.normalized([run], restoring: true, budget: &budget)
        #expect(restored[0].resultEntries.first?.provider == "codex")
        #expect(restored[0].agents[0].entries.first?.provider == "codex")
    }

    @Test func sessionProviderRepairsOldSavedGraphsAndLegacyTranscripts() throws {
        var session = RunSession(workspaceId: "workspace", title: "Legacy Codex", provider: "codex", status: "completed", logs: [
            LogEntry(id: "input", kind: "user", text: "Question"),
            LogEntry(id: "answer", kind: "assistant", text: "Answer", provider: "codex")
        ])
        #expect(session.mightyGraphRuns[0].resultEntries.first?.provider == "codex")
        session.graphRuns = [MightyGraphRun(id: "old", status: "completed", rootEntries: [LogEntry(id: "main-answer", kind: "assistant", text: "Answer", provider: "claude")], agents: [
            MightyGraphAgent(id: "child", status: "completed", entries: [LogEntry(id: "child-answer", kind: "assistant", text: "Child", provider: "claude")])
        ], finalOutput: "Answer")]
        #expect(session.mightyGraphRuns[0].rootEntries.first?.provider == "codex")
        #expect(session.mightyGraphRuns[0].agents[0].entries.first?.provider == "codex")
        let workspace = Workspace(id: "workspace", name: "Test", path: "/tmp")
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: true)
        let saved = try #require(restored.sessions.first?.graphRuns?.first)
        #expect(saved.provider == "codex")
        #expect(saved.rootEntries.first?.provider == "codex")
        #expect(saved.resultEntries.first?.provider == "codex")
        #expect(saved.agents[0].entries.first?.provider == "codex")
    }
}
