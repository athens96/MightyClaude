import Foundation
import Testing
@testable import MightyCore

private let appExecutable = URL(fileURLWithPath: "/Applications/MightyClaude.app/Contents/MacOS/MightyClaude")
private let reportedHead = String(repeating: "b", count: 40)

private func json(_ line: String?) -> [String: Any]? {
    line.flatMap { (try? JSONSerialization.jsonObject(with: Data($0.utf8))) as? [String: Any] }
}

private func toolCall(_ name: String, _ arguments: [String: String]) -> String {
    let data = try! JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "id": 9, "method": "tools/call", "params": ["name": name, "arguments": arguments]])
    return String(decoding: data, as: UTF8.self)
}

/// The app side as the status tools see it: every pane open and idle. Records
/// every call, so a test can show the tools reached no pane.
private final class StatusHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []
    var calls: [String] { lock.withLock { recorded } }
    private func note(_ call: String) { lock.withLock { recorded.append(call) } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { note("createPane \(pane.sessionId)"); return false }
    func startRun(sessionId: String, input: String) async -> String? { note("startRun \(sessionId)"); return nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { note("deliver \(sessionId)"); return nil }
    func paneState(sessionId: String) async -> DelegationPaneState? {
        note("paneState \(sessionId)")
        return DelegationPaneState(sessionId: sessionId, permissionMode: "auto", folder: "/tmp", runId: "run-\(sessionId)", activity: .finished)
    }
    func stopRun(sessionId: String) async { note("stopRun \(sessionId)") }
}

/// No terminal calls are made here.
private struct NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

/// A child record that only needs to exist: no worktree behind it.
private func record(_ id: String, parent: String = "parent", state: ChildState, revision: Int = 0, head: String? = nil) -> ChildRecord {
    ChildRecord(id: id, parentSessionId: parent, worktreePath: "/nonexistent/worktrees/\(id)", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                startingMode: "plan", requestKey: "key-\(id)", reportRevision: revision, reportHead: head, state: state)
}

private func report(_ id: String, revision: Int, _ text: String) -> DelegationCopy {
    DelegationCopy(childId: id, kind: .report, revision: revision, contents: Data(text.utf8))
}

/// list_children and child_status with a fake host: the parent reads its
/// children from the delegation file, and nothing changes.
@Suite(.serialized) struct DelegationStatusToolTests {
    /// "parent" has children in every kind of state; "other" has one of its own.
    static let children = [
        record("running", state: .running),
        record("reported", state: .reported, revision: 2, head: reportedHead),
        record("someone-elses", parent: "other", state: .reported, revision: 1, head: reportedHead),
        record("merged", state: .merged, revision: 1, head: String(repeating: "c", count: 40)),
        record("failed", state: .failed),
        record("closed", state: .closed, revision: 3, head: String(repeating: "d", count: 40)),
        record("discarded", state: .discarded),
    ]
    static let reportText = "Fixed the parser.\nLeft: the docs.\nCheck: `swift test` and \"quotes\".\n"

    private struct Fixture {
        let folder: URL
        let host = StatusHost()
        let store: DelegationFileStore
        let coordinator: DelegationCoordinator

        init(file: DelegationFile) throws {
            folder = try shortTemporaryDirectory()
            store = DelegationFileStore(directory: folder.appendingPathComponent("profile", isDirectory: true))
            try store.save(file)
            coordinator = try DelegationCoordinator(store: store, host: host, worktrees: ChildWorktreeMaker(root: folder.appendingPathComponent("worktrees", isDirectory: true)), isSwitchOn: { true })
        }

        func binding(_ pane: String) -> PaneMCPBinding {
            PaneMCPBinding(agentPaneId: pane, token: "token-\(pane)", server: PaneMCPServerLocation(socketPath: folder.appendingPathComponent("io.sock").path, executable: appExecutable),
                           workspaceId: "ws-1", workspacePath: folder.path, provider: "claude", delegation: true)
        }

        func call(_ tool: String, _ arguments: [String: String] = [:], from pane: String = "parent") async -> DelegationResponse {
            await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: binding(pane))
        }

        func fileBytes() throws -> Data { try Data(contentsOf: store.fileURL) }
        func tearDown() { try? FileManager.default.removeItem(at: folder) }
    }

    private static func standardFile() -> DelegationFile {
        DelegationFile(children: children, copies: [
            DelegationCopy(childId: "reported", kind: .task, revision: 0, contents: Data("Fix the parser.".utf8)),
            report("reported", revision: 2, reportText),
            report("someone-elses", revision: 1, "Not for parent."),
            report("merged", revision: 1, "Merged work."),
        ])
    }

    @Test func listChildrenGivesTheCallersChildrenWithIdStateAndCurrentRevision() async throws {
        let fixture = try Fixture(file: Self.standardFile())
        defer { fixture.tearDown() }
        let bytes = try fixture.fileBytes()
        let file = await fixture.coordinator.file

        let mine = await fixture.call("list_children")
        #expect(mine.refused == nil && mine.error == nil && mine.child == nil && mine.status == nil)
        let expected: [(String, ChildState, Int)] = [("running", .running, 0), ("reported", .reported, 2), ("merged", .merged, 1), ("failed", .failed, 0),
                                                     ("closed", .closed, 3), ("discarded", .discarded, 0)]
        let listed = try #require(mine.children)
        #expect(listed.map(\.id) == expected.map(\.0))
        #expect(listed.map(\.state) == expected.map(\.1))
        #expect(listed.map(\.revision) == expected.map(\.2))
        // Another parent sees only its own child; a pane without children gets an empty list.
        #expect(await fixture.call("list_children", from: "other").children == [DelegationChildSummary(Self.children[2])])
        #expect(await fixture.call("list_children", from: "lonely").children == [])

        // Through the delegation MCP server the list is tool data.
        let result = DelegationMCPServer.result(mine)
        #expect(result["isError"] == nil)
        let data = try #require((result["structuredContent"] as? [String: Any])?["children"] as? [[String: Any]])
        #expect(data.compactMap { $0["id"] as? String } == expected.map(\.0))
        #expect(data.compactMap { $0["state"] as? String } == expected.map(\.1.rawValue))
        #expect(data.compactMap { $0["revision"] as? Int } == expected.map(\.2))
        let text = try #require((result["content"] as? [[String: Any]])?.first?["text"] as? String)
        #expect(text.hasPrefix("Your children:\n- running: running, report revision 0\n- reported: reported, report revision 2\n"))
        #expect(!text.contains("someone-elses"))
        let none = DelegationMCPServer.result(await fixture.call("list_children", from: "lonely"))
        #expect((none["structuredContent"] as? [String: Any])?["children"] as? [[String: Any]] != nil)
        #expect(((none["structuredContent"] as? [String: Any])?["children"] as? [[String: Any]])?.isEmpty == true)
        #expect((none["content"] as? [[String: Any]])?.first?["text"] as? String == "You have no children.")

        // Reading changed nothing and reached no pane.
        #expect(try fixture.fileBytes() == bytes)
        #expect(await fixture.coordinator.file == file)
        #expect(fixture.host.calls.isEmpty)
    }

    @Test func childStatusGivesOneChildsStateHeadAndReportBodyAsToolData() async throws {
        let fixture = try Fixture(file: Self.standardFile())
        defer { fixture.tearDown() }
        let bytes = try fixture.fileBytes()

        let answer = await fixture.call("child_status", ["child": "reported"])
        #expect(answer.refused == nil && answer.error == nil && answer.child == nil && answer.children == nil)
        let status = try #require(answer.status)
        #expect(status == DelegationChildStatus(Self.children[1], report: report("reported", revision: 2, Self.reportText)))
        #expect(status.id == "reported" && status.state == .reported && status.revision == 2)
        #expect(status.head == reportedHead)
        #expect(status.report == Self.reportText)
        #expect(!status.reportTruncated)

        // Through the delegation MCP server the state, head and report body are tool data.
        let result = DelegationMCPServer.result(answer)
        #expect(result["isError"] == nil)
        let data = try #require((result["structuredContent"] as? [String: Any])?["status"] as? [String: Any])
        #expect(data["id"] as? String == "reported")
        #expect(data["state"] as? String == "reported")
        #expect(data["revision"] as? Int == 2)
        #expect(data["head"] as? String == reportedHead)
        #expect(data["report"] as? String == Self.reportText)
        #expect(data["reportTruncated"] as? Bool == false)
        let text = try #require((result["content"] as? [[String: Any]])?.first?["text"] as? String)
        #expect(text == "Child reported is reported. Its report revision 2 was made at head \(reportedHead).\n\nREPORT.md:\n\(Self.reportText)")

        // Before its first report a child has no head and no report body.
        let running = try #require(await fixture.call("child_status", ["child": "running"]).status)
        #expect(running.state == .running && running.revision == 0 && running.head == nil && running.report == nil && !running.reportTruncated)
        let runningData = try #require((DelegationMCPServer.result(.init(status: running))["structuredContent"] as? [String: Any])?["status"] as? [String: Any])
        #expect(runningData["head"] is NSNull && runningData["report"] is NSNull)
        #expect((DelegationMCPServer.result(.init(status: running))["content"] as? [[String: Any]])?.first?["text"] as? String == "Child running is running. It has not reported yet.")

        // A merged child keeps its head and report; a closed one whose copy was pruned keeps its head only.
        let merged = try #require(await fixture.call("child_status", ["child": " merged\n"]).status)
        #expect(merged.state == .merged && merged.head == String(repeating: "c", count: 40) && merged.report == "Merged work.")
        let closed = try #require(await fixture.call("child_status", ["child": "closed"]).status)
        #expect(closed.state == .closed && closed.revision == 3 && closed.head == String(repeating: "d", count: 40) && closed.report == nil)
        #expect((DelegationMCPServer.result(.init(status: closed))["content"] as? [[String: Any]])?.first?["text"] as? String
            == "Child closed is closed. Its report revision 3 was made at head \(String(repeating: "d", count: 40)). Its REPORT.md copy is no longer kept.")
        #expect(await fixture.call("child_status", ["child": "discarded"]).status?.state == .discarded)
        #expect(await fixture.call("child_status", ["child": "failed"]).status?.state == .failed)

        #expect(try fixture.fileBytes() == bytes)
        #expect(fixture.host.calls.isEmpty)
    }

    @Test func aLongReportIsTheMarkedCopyAndAStaleCopyIsNoReport() async throws {
        let long = String(repeating: "line of the report\n", count: 5_000)
        let file = DelegationFile(children: [record("long", state: .reported, revision: 1, head: reportedHead), record("stale", state: .reported, revision: 2, head: reportedHead)],
                                  copies: [report("long", revision: 1, long), report("stale", revision: 1, "The first report.")])
        let fixture = try Fixture(file: file)
        defer { fixture.tearDown() }

        let status = try #require(await fixture.call("child_status", ["child": "long"]).status)
        #expect(status.reportTruncated)
        let body = try #require(status.report)
        #expect(body.hasSuffix(DelegationCopy.truncationMarker))
        #expect(long.hasPrefix(String(body.dropLast(DelegationCopy.truncationMarker.count))))
        #expect(body.utf8.count <= DelegationFileStore.maximumCopyBytes)
        let data = try #require((DelegationMCPServer.result(.init(status: status))["structuredContent"] as? [String: Any])?["status"] as? [String: Any])
        #expect(data["reportTruncated"] as? Bool == true)
        #expect(data["report"] as? String == body)

        // A copy of an earlier revision is not the current report.
        let stale = try #require(await fixture.call("child_status", ["child": "stale"]).status)
        #expect(stale.revision == 2 && stale.head == reportedHead && stale.report == nil && !stale.reportTruncated)
    }

    @Test func childStatusOfAChildThatIsNotTheCallersIsAnErrorAndChangesNothing() async throws {
        let fixture = try Fixture(file: Self.standardFile())
        defer { fixture.tearDown() }
        let bytes = try fixture.fileBytes()
        let file = await fixture.coordinator.file

        for (id, caller) in [("someone-elses", "parent"), ("reported", "other"), ("no-such-child", "parent"), ("", "parent"), ("reported", "lonely")] {
            let answer = await fixture.call("child_status", ["child": id], from: caller)
            #expect(answer == .failure(DelegationCoordinator.notYourChildMessage(id)), "\(caller) \(id)")
            #expect(answer.refused == nil && answer.status == nil)
        }
        // Without exactly its one argument it is answered with an error too.
        #expect(await fixture.call("child_status").error == "child_status takes exactly these string arguments: child.")
        #expect(await fixture.call("list_children", ["child": "reported"]).error == "list_children takes no arguments.")

        #expect(try fixture.fileBytes() == bytes)
        #expect(await fixture.coordinator.file == file)
        #expect(fixture.host.calls.isEmpty)
    }

    @Test func overTheRealSocketTheDelegationServerHandsBackTheListAndTheReport() async throws {
        let fixture = try Fixture(file: Self.standardFile())
        defer { fixture.tearDown() }
        let socketPath = fixture.folder.appendingPathComponent("io.sock").path
        let bindings = PaneMCPBindingRegistry()
        let server = AgentIOSocketServer(socketPath: socketPath, bindings: bindings, handler: NoTerminalCalls(), delegation: fixture.coordinator)
        try server.start()
        defer { server.stop() }
        let location = PaneMCPServerLocation(socketPath: socketPath, executable: appExecutable)
        let parent = bindings.bind(agentPaneId: "parent", server: location, workspaceId: "ws-1", workspacePath: fixture.folder.path, provider: "claude", kind: SessionKind.claude, delegation: true)
        let mcp = DelegationMCPServer(environment: [PaneMCPBinding.tokenEnvironmentKey: parent.token, PaneMCPBinding.socketEnvironmentKey: socketPath])

        let list = try #require(json(mcp.handle(line: toolCall("list_children", [:])))?["result"] as? [String: Any])
        #expect(list["isError"] == nil)
        let children = try #require((list["structuredContent"] as? [String: Any])?["children"] as? [[String: Any]])
        #expect(children.compactMap { $0["id"] as? String } == ["running", "reported", "merged", "failed", "closed", "discarded"])

        let status = try #require(json(mcp.handle(line: toolCall("child_status", ["child": "reported"])))?["result"] as? [String: Any])
        #expect(status["isError"] == nil)
        let data = try #require((status["structuredContent"] as? [String: Any])?["status"] as? [String: Any])
        #expect(data["state"] as? String == "reported")
        #expect(data["head"] as? String == reportedHead)
        #expect(data["report"] as? String == Self.reportText)
        #expect(fixture.host.calls.isEmpty)
    }
}
