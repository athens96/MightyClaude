import Foundation
import Testing
@testable import MightyCore

struct MightyGraphTests {
    private func node(_ run: String, agent: String? = nil, parent: String? = nil, state: String = "running", input: String? = nil, output: String? = nil, entries: [LogEntry] = []) -> ExecutionGraphNode {
        ExecutionGraphNode(id: agent.map { ExecutionGraphSupport.agentNodeID(runId: run, toolUseId: $0) } ?? ExecutionGraphSupport.mainNodeID(runId: run), runId: run,
                           parentId: parent.map { ExecutionGraphSupport.agentNodeID(runId: run, toolUseId: $0) } ?? (agent == nil ? nil : ExecutionGraphSupport.mainNodeID(runId: run)),
                           kind: agent == nil ? "main" : "agent", state: state, title: agent ?? "메인", input: input, output: output, entries: entries)
    }
    private func receive(_ node: ExecutionGraphNode, _ session: inout RunSession) {
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: node))
    }

    @Test func resultWaitsForEveryChildAndAppearsInMainAndResult() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "Build this", id: "request-one")
        receive(node("process-one"), &session)
        receive(node("process-one", agent: "a", input: "Inspect API"), &session)
        receive(node("process-one", agent: "b", parent: "a", input: "Check schema"), &session)
        receive(node("process-one", state: "completed", output: "# Final answer"), &session)
        #expect(session.mightyGraphRuns[0].resultEntries.isEmpty)
        receive(node("process-one", agent: "a", state: "completed", output: "API checked"), &session)
        #expect(session.mightyGraphRuns[0].resultEntries.isEmpty)
        receive(node("process-one", agent: "b", parent: "a", state: "completed", output: "Schema checked"), &session)
        let run = session.mightyGraphRuns[0]
        #expect(run.settled)
        #expect(run.resultEntries.map(\.text) == ["# Final answer"])
        #expect(run.rootEntries.filter { $0.text == "# Final answer" }.count == 1)
        #expect(run.agents[1].parentID == run.agents[0].id)
        #expect(run.agents[0].input == "Inspect API")
        #expect(run.agents[1].entries.last?.text == "Schema checked")
        receive(node("process-one", agent: "b", parent: "a", state: "completed", output: "Schema checked"), &session)
        #expect(session.mightyGraphRuns[0].agents.count == 2)
        #expect(session.mightyGraphRuns[0].rootEntries.count == 1)
    }

    @Test func sequentialRequestsAndLateEventsStayWithTheirOriginalRun() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "First", id: "request-one")
        receive(node("process-one", state: "completed", output: "First result"), &session)
        session.beginGraphRun(input: "Second", id: "request-two")
        receive(node("process-two"), &session)
        receive(node("process-one", agent: "late", state: "completed", output: "Old agent"), &session)
        #expect(session.mightyGraphRuns.count == 2)
        #expect(session.mightyGraphRuns[0].agents.count == 1)
        #expect(session.mightyGraphRuns[1].agents.isEmpty)
        #expect(session.mightyGraphRuns[1].status == "running")
        receive(node("process-unknown", output: "Must not bind"), &session)
        #expect(session.mightyGraphRuns[1].sourceRunID == "process-two")
        session.recordGraph(RunEvent(sessionId: session.id, type: "status", status: "stopped"))
        #expect(session.mightyGraphRuns[1].resultEntries.isEmpty)
        #expect(session.mightyGraphRuns[1].status == "stopped")
    }

    @Test func interruptionDoesNotInventFinalAnswerAndTerminalNodesDoNotReopen() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "Request")
        receive(node("process", agent: "child", input: "Child request"), &session)
        session.recordGraph(RunEvent(sessionId: session.id, type: "status", status: "error"))
        receive(node("process", agent: "child", state: "running"), &session)
        #expect(session.mightyGraphRuns[0].agents[0].status == "error")
        #expect(session.mightyGraphRuns[0].agents[0].input == "Child request")
        #expect(session.mightyGraphRuns[0].resultEntries.isEmpty)
        receive(node("process", state: "completed", output: "Late result"), &session)
        receive(node("process", agent: "child", state: "completed", output: "Late child"), &session)
        #expect(session.mightyGraphRuns[0].status == "error")
        #expect(session.mightyGraphRuns[0].agents[0].status == "error")
        #expect(session.mightyGraphRuns[0].resultEntries.isEmpty)
    }

    @Test func legacyHistoryGroupsActualRequestsWithoutInventingChildren() throws {
        let session = RunSession(workspaceId: "workspace", title: "Old", status: "completed", logs: [
            LogEntry(id: "input-one", kind: "user", text: "One"), LogEntry(kind: "assistant", text: "First answer"),
            LogEntry(id: "input-two", kind: "user", text: "Two"), LogEntry(kind: "assistant", text: "Second answer")
        ])
        #expect(session.mightyGraphRuns.map(\.input) == ["One", "Two"])
        #expect(session.mightyGraphRuns.allSatisfy { $0.agents.isEmpty })
        #expect(session.mightyGraphRuns[1].resultEntries.first?.text == "Second answer")
        var object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        object["graphRuns"] = ["damaged"]
        object["agentViewMode"] = 3
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(decoded.logs == session.logs)
        #expect(decoded.graphRuns == nil)
        #expect(decoded.mightyGraphRuns.count == 2)
    }

    @Test func graphRestoresModeAndInterruptedChildrenFromDisk() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-graph-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Graph", path: directory.path))
        var session = RunSession(workspaceId: workspace.id, title: "Graph", status: "running")
        session.agentViewMode = "mighty"
        session.beginGraphRun(input: "Request", id: "request")
        receive(node("process", agent: "child", input: "Nested work"), &session)
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [session]))
        let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
        let saved = try #require(restored.sessions.first)
        #expect(saved.agentViewMode == "mighty")
        #expect(saved.mightyGraphRuns[0].status == "stopped")
        #expect(saved.mightyGraphRuns[0].agents[0].status == "stopped")
        #expect(saved.mightyGraphRuns[0].agents[0].input == "Nested work")
        #expect(saved.mightyGraphRuns[0].resultEntries.isEmpty)
    }

    @Test func malformedCyclesAndOversizedHistoryAreBounded() {
        let run = MightyGraphRun(id: "run", input: String(repeating: "가", count: 50_000), agents: [
            MightyGraphAgent(id: "a", parentID: "b"), MightyGraphAgent(id: "b", parentID: "a"),
            MightyGraphAgent(id: "c", parentID: "missing")
        ])
        var budget = 65_536
        let normalized = MightyGraphSupport.normalized([run], restoring: false, budget: &budget)
        #expect(normalized[0].input.utf8.count <= 32_768)
        #expect(normalized[0].agents.allSatisfy { $0.parentID == nil })
        #expect(budget >= 0)
        let corrupt = RunEvent(sessionId: "pane", type: "graph")
        #expect(RemoteValidation.event(corrupt, sessionId: "pane"))
        #expect(!RemoteValidation.event(corrupt, sessionId: "other"))
    }

    @Test func remoteGraphUsesExplicitOptInAndPreservesParentAndEntries() async throws {
        let child = node("remote-run", agent: "child", parent: "parent", state: "completed", input: "Inspect source", output: "Checked", entries: [LogEntry(id: "child-message", kind: "assistant", text: "# Checked")])
        let poll = WirePoll(cursor: 1, lastCursor: 1, gap: false, done: false, events: [WireEvent(cursor: 1, event: RunEvent(sessionId: "remote-job", type: "graph", graph: child))])
        let body = try JSONEncoder().encode(poll)
        let token = String(repeating: "g", count: 43)
        let server = HTTPServer(address: "127.0.0.1", port: 0) { request in
            guard request.headers["x-mighty-graph"] == "1", request.headers["authorization"] == "Bearer \(token)" else { return .json(400, ["error": "missing graph opt-in"]) }
            return HTTPResponse(status: 200, body: body, headers: ["x-mighty-remote-version": "1"])
        }
        do {
            let port = try await server.start()
            let target = RemoteTarget(origin: "http://127.0.0.1:\(port)", host: "127.0.0.1", port: Int(port), ip: "127.0.0.1")
            let data = try await RemoteTransport.request(target, token: token, method: "GET", path: "/v1/runs/remote-job/events?cursor=0")
            let received = try JSONDecoder().decode(WirePoll.self, from: data)
            let event = try #require(received.events.first?.event)
            #expect(RemoteValidation.event(event, sessionId: "remote-job"))
            #expect(event.graph == child)
            #expect(ExecutionGraphSupport.normalized(child) != nil)
        } catch { await server.stop(); throw error }
        await server.stop()
    }

    @Test func liveBudgetRetainsUnfinishedChildIdentityWhenTextIsTrimmed() throws {
        let agents = (0..<128).map { index in
            MightyGraphAgent(id: "agent-\(index)", input: String(repeating: "p", count: 16_384), status: index == 127 ? "waiting" : "completed",
                             entries: [LogEntry(kind: "assistant", text: String(repeating: "x", count: 60_000))])
        }
        let run = MightyGraphRun(id: "large-run", input: "Request", status: "completed", agents: agents, finalOutput: "Final reported")
        let bounded = MightyGraphSupport.boundedLiveHistory([run])
        #expect(bounded.count == 1)
        #expect(bounded[0].agents.count == 128)
        #expect(bounded[0].agents.last?.status == "waiting")
        #expect(!bounded[0].settled)
        #expect(bounded[0].resultEntries.isEmpty)
        #expect(try JSONEncoder().encode(bounded).count < 2 * 1024 * 1024)
    }
}
