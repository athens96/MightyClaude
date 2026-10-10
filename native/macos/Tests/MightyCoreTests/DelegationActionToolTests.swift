import Foundation
import Testing
@testable import MightyCore

private let appExecutable = URL(fileURLWithPath: "/Applications/MightyClaude.app/Contents/MacOS/MightyClaude")

private func json(_ line: String?) -> [String: Any]? {
    line.flatMap { (try? JSONSerialization.jsonObject(with: Data($0.utf8))) as? [String: Any] }
}

private func toolCall(_ name: String, _ arguments: [String: String]) -> String {
    let data = try! JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "id": 11, "method": "tools/call", "params": ["name": name, "arguments": arguments]])
    return String(decoding: data, as: UTF8.self)
}

private func text(of result: [String: Any]) -> String? { (result["content"] as? [[String: Any]])?.first?["text"] as? String }

/// The app side as the action tools see it: every pane is open and running in
/// the fixture's repository until a test closes it. Records every call.
private final class ActionHost: DelegationHost, @unchecked Sendable {
    let folder: String
    private let lock = NSLock()
    private var recorded: [String] = []
    private var closed: Set<String> = []

    init(folder: String) { self.folder = folder }

    var calls: [String] { lock.withLock { recorded } }
    func close(_ pane: String) { lock.withLock { _ = closed.insert(pane) } }
    private func note(_ call: String) { lock.withLock { recorded.append(call) } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { note("createPane \(pane.sessionId)"); return false }
    func startRun(sessionId: String, input: String) async -> String? { note("startRun \(sessionId)"); return nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { note("deliver \(sessionId)"); return nil }
    func paneState(sessionId: String) async -> DelegationPaneState? {
        note("paneState \(sessionId)")
        guard !lock.withLock({ closed.contains(sessionId) }) else { return nil }
        return DelegationPaneState(sessionId: sessionId, permissionMode: "acceptEdits", folder: folder, runId: "run-\(sessionId)", activity: .running)
    }
    func stopRun(sessionId: String) async { note("stopRun \(sessionId)") }
}

/// No terminal calls are made here.
private struct NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

/// A child record that only needs to exist: no worktree behind it.
private func record(_ id: String, parent: String = "parent", state: ChildState, followUps: Int = 0) -> ChildRecord {
    ChildRecord(id: id, parentSessionId: parent, worktreePath: "/nonexistent/worktrees/\(id)", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                startingMode: "plan", requestKey: "key-\(id)", followUpCount: followUps, state: state)
}

/// merge and follow_up with a fake host, merging on real git repositories in
/// a temp folder: what each tool records, and that every refusal names its
/// one reason code and changes nothing.
@Suite(.serialized, .enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationActionToolTests {
    private struct Fixture {
        let place: MergePlace
        let host: ActionHost
        let store: DelegationFileStore
        let coordinator: DelegationCoordinator

        /// `children` saved in the profile's delegation file before the coordinator loads it.
        init(_ place: MergePlace, children: [ChildRecord]) throws {
            self.place = place
            host = ActionHost(folder: place.repo.path)
            store = DelegationFileStore(directory: place.base.appendingPathComponent("profile", isDirectory: true))
            try store.save(DelegationFile(children: children))
            coordinator = try Self.makeCoordinator(store: store, host: host, place: place)
        }

        /// A coordinator on `store`, as the app makes one at launch.
        static func makeCoordinator(store: DelegationFileStore, host: ActionHost, place: MergePlace) throws -> DelegationCoordinator {
            try DelegationCoordinator(store: store, host: host, worktrees: ChildWorktreeMaker(root: place.root, freeBytes: { _ in 50_000_000_000 }), isSwitchOn: { true })
        }

        func binding(_ pane: String) -> PaneMCPBinding {
            PaneMCPBinding(agentPaneId: pane, token: "token-\(pane)", server: PaneMCPServerLocation(socketPath: "/tmp/unused-io.sock", executable: appExecutable),
                           workspaceId: "ws-1", workspacePath: place.repo.path, provider: "claude", delegation: true)
        }

        func call(_ tool: String, _ arguments: [String: String], from pane: String = "parent") async -> DelegationResponse {
            await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: binding(pane))
        }

        /// The same calls all at once; the answers in the calls' order.
        func callAtOnce(_ tool: String, _ calls: [[String: String]]) async -> [DelegationResponse] {
            let coordinator = self.coordinator, parent = binding("parent")
            let answers = await withTaskGroup(of: (Int, DelegationResponse).self) { group in
                for (index, arguments) in calls.enumerated() {
                    group.addTask { (index, await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: parent)) }
                }
                var all: [Int: DelegationResponse] = [:]
                for await (index, answer) in group { all[index] = answer }
                return all
            }
            return calls.indices.compactMap { answers[$0] }
        }

        func fileBytes() throws -> Data { try Data(contentsOf: store.fileURL) }
    }

    /// A child of "parent" made the app's way, with one commit per file and
    /// then reported at its head. Its worktree's REPORT.md is ignored by git.
    private static func reportedChild(_ place: MergePlace, _ id: String, files: [String]) async throws -> (ChildRecord, String) {
        var child = try await place.child(id)
        var head = child.baseCommit
        for name in files { head = try await place.commit(in: child.worktreePath, name, "\(id) \(name)\n") }
        try Data("# Report\nDone.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        let reported = child.recordReport(head: head); #expect(reported)
        return (child, head)
    }

    @Test func mergeFastForwardsAReportedChildAndWritesItsMergeRecord() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        let (made, head) = try await Self.reportedChild(place, "c1", files: ["a.txt", "b.txt"])
        var child = made
        child.parentCheckout = place.repo.path
        let fixture = try Fixture(place, children: [child])

        let answer = await fixture.call("merge", ["child": "c1", "expected_head": head])
        let record = MergeRecord(childId: "c1", kind: .toolFastForward, parentBranch: "main", preMergeCommit: base, mergedCommit: head, childHead: head)
        #expect(answer == DelegationResponse(merged: record))
        // One fast-forward: main is checked out at the child's head, with no merge commit and clean tracked files.
        #expect(try await place.git(["symbolic-ref", "HEAD"]) == "refs/heads/main")
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(try await place.git(["rev-list", "--count", "\(base)..HEAD"]) == "2")
        #expect(try await place.git(["rev-list", "--merges", "HEAD"]).isEmpty)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=no"]).isEmpty)
        #expect(try String(contentsOf: place.repo.appendingPathComponent("b.txt"), encoding: .utf8) == "c1 b.txt\n")
        // The child's branch and checkout are only read.
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c1"]) == head)
        #expect(try await place.git(["symbolic-ref", "HEAD"], in: child.worktreePath) == "refs/heads/mighty/c1")

        // The MergeRecord is saved in the delegation file with the child merged, and a relaunch reads both back.
        let file = await fixture.coordinator.file
        #expect(file.merges == [record])
        #expect(file.children.map(\.state) == [.merged])
        #expect(try fixture.store.load() == file)
        let relaunched = try Fixture.makeCoordinator(store: fixture.store, host: fixture.host, place: place)
        #expect(await relaunched.file.merges == [record])

        // Through the delegation MCP server the merge is tool data.
        let result = DelegationMCPServer.result(answer)
        #expect(result["isError"] == nil)
        let data = try #require((result["structuredContent"] as? [String: Any])?["merged"] as? [String: Any])
        #expect(data["child"] as? String == "c1")
        #expect(data["kind"] as? String == "tool_fast_forward")
        #expect(data["parentBranch"] as? String == "main")
        #expect(data["preMergeCommit"] as? String == base)
        #expect(data["mergedCommit"] as? String == head)
        #expect(data["childHead"] as? String == head)
        #expect(text(of: result) == "Merged child c1: main fast-forwarded from \(base) to \(head). The merge is recorded; only a human can undo it, from the child's card.")

        // A merged child is no longer reported: a second merge is refused and changes nothing.
        let bytes = try fixture.fileBytes()
        let before = try await place.snapshot(child)
        let again = await fixture.call("merge", ["child": "c1", "expected_head": head])
        #expect(again == .refusal(.notReported))
        #expect(try fixture.fileBytes() == bytes)
        #expect(try await place.snapshot(child) == before)
        #expect(await fixture.coordinator.file == file)
    }

    @Test func eachMergeRefusalIsAnsweredWithItsReasonCodeAndChangesNothing() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        let running = try await place.child("running")
        let (moved, movedHead) = try await Self.reportedChild(place, "moved", files: ["m.txt"])
        let (dirty, dirtyHead) = try await Self.reportedChild(place, "dirty", files: ["d.txt"])
        let (gone, goneHead) = try await Self.reportedChild(place, "gone", files: ["g.txt"])
        let (first, firstHead) = try await Self.reportedChild(place, "first", files: ["f.txt"])
        let (late, lateHead) = try await Self.reportedChild(place, "late", files: ["l.txt"])
        let fixture = try Fixture(place, children: [running, moved, dirty, gone, first, late])
        // A tracked change in one child, and another child's worktree gone.
        try Data("edited\n".utf8).write(to: URL(fileURLWithPath: dirty.worktreePath).appendingPathComponent("d.txt"))
        try FileManager.default.removeItem(atPath: gone.worktreePath)

        /// Asks for the merge and expects `reason`, with the delegation file,
        /// every ref and both checkouts (the parent's and `look`'s) unchanged.
        func expectRefusal(_ id: String, _ expectedHead: String, _ reason: DelegationReasonCode, look: ChildRecord, _ label: String) async throws {
            let bytes = try fixture.fileBytes()
            let file = await fixture.coordinator.file
            let before = try await place.snapshot(look)
            let answer = await fixture.call("merge", ["child": id, "expected_head": expectedHead])
            #expect(answer == .refusal(reason), "\(label)")
            #expect(try fixture.fileBytes() == bytes, "\(label)")
            #expect(await fixture.coordinator.file == file, "\(label)")
            #expect(try await place.snapshot(look) == before, "\(label)")
            // Through the delegation MCP server: exactly that one reason code.
            let result = DelegationMCPServer.result(answer)
            #expect(result["isError"] as? Bool == true, "\(label)")
            #expect(result["structuredContent"] as? [String: String] == ["refused": reason.rawValue], "\(label)")
            #expect(text(of: result) == "Refused: \(reason.rawValue). Nothing was changed.", "\(label)")
        }

        // The child's side.
        try await expectRefusal("running", running.baseCommit, .notReported, look: running, "running, no report")
        try await expectRefusal("moved", running.baseCommit, .headMoved, look: moved, "expected head is not the reported one")
        try await expectRefusal("dirty", dirtyHead, .trackedChanges, look: running, "tracked change in the child")
        try await expectRefusal("gone", goneHead, .worktreeMissing, look: running, "worktree gone")
        let later = try await place.commit(in: moved.worktreePath, "m2.txt", "later\n")
        try await expectRefusal("moved", movedHead, .headMoved, look: moved, "child branch moved past its report")
        try await expectRefusal("moved", later, .headMoved, look: moved, "a head that was never reported")
        try await place.git(["reset", "-q", "--hard", movedHead], in: moved.worktreePath)

        // The parent's side: a tracked change, then another branch checked out.
        try Data("parent edit\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        try await expectRefusal("moved", movedHead, .trackedChanges, look: moved, "tracked change in the parent")
        try await place.git(["checkout", "-q", "--", "file-0.txt"])
        try await place.git(["checkout", "-q", "-b", "elsewhere"])
        try await expectRefusal("moved", movedHead, .branchNotCheckedOut, look: moved, "parent branch not checked out")
        try await place.git(["checkout", "-q", "main"])

        // Once one child is merged, a sibling made from the same base has diverged.
        let merged = await fixture.call("merge", ["child": "first", "expected_head": firstHead])
        #expect(merged.merged?.mergedCommit == firstHead)
        try await expectRefusal("late", lateHead, .diverged, look: late, "diverged")
        #expect(try await place.git(["rev-parse", "HEAD"]) == firstHead)
        #expect(await fixture.coordinator.file.merges.map(\.childId) == ["first"])

        // A child that is not the caller's is an error, and changes nothing either.
        let bytes = try fixture.fileBytes()
        for (id, caller) in [("late", "other"), ("no-such-child", "parent")] {
            #expect(await fixture.call("merge", ["child": id, "expected_head": lateHead], from: caller) == .failure(DelegationCoordinator.notYourChildMessage(id)), "\(caller) \(id)")
        }
        #expect(try fixture.fileBytes() == bytes)
    }

    @Test func mergesAtOnceTakeTurnsSoExactlyOneFastForwards() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        let (one, oneHead) = try await Self.reportedChild(place, "one", files: ["1.txt"])
        let (two, twoHead) = try await Self.reportedChild(place, "two", files: ["2.txt"])
        let fixture = try Fixture(place, children: [one, two])

        // The same child three times and a sibling from the same base, all at once.
        let answers = await fixture.callAtOnce("merge", [["child": "one", "expected_head": oneHead], ["child": "two", "expected_head": twoHead],
                                                         ["child": "one", "expected_head": oneHead], ["child": "one", "expected_head": oneHead]])
        #expect(answers.count == 4)
        // One fast-forward; every other call is refused, never failed by git running twice at once.
        let records = answers.compactMap(\.merged)
        #expect(records.count == 1)
        let record = try #require(records.first)
        #expect(record.preMergeCommit == base)
        #expect(answers.allSatisfy { $0.error == nil })
        for answer in answers where answer.merged == nil {
            #expect(answer.refused == .notReported || answer.refused == .diverged, "\(answer)")
        }
        #expect(try await place.git(["rev-parse", "HEAD"]) == record.mergedCommit)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=no"]).isEmpty)
        let file = await fixture.coordinator.file
        #expect(file.merges == [record])
        #expect(file.children.filter { $0.state == .merged }.map(\.id) == [record.childId])
        #expect(try fixture.store.load() == file)
    }

    @Test func followUpRecordsAtMostTwoPerChildAndRefusesTheThirdWithFollowUpLimit() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let fixture = try Fixture(place, children: [record("c1", state: .running), record("c2", state: .reported)])

        let first = await fixture.call("follow_up", ["child": "c1", "text": "Also add tests."])
        #expect(first.refused == nil && first.error == nil && first.merged == nil)
        let info = try #require(first.followUp)
        #expect(info.child == "c1" && info.lane == .pending && info.remaining == 1 && !info.id.isEmpty)
        var file = await fixture.coordinator.file
        #expect(file.followUps == [FollowUp(id: info.id, childId: "c1", text: "Also add tests.")])
        #expect(file.children.map(\.followUpCount) == [1, 0])
        // Written to disk before the answer.
        #expect(try fixture.store.load() == file)

        // Through the delegation MCP server the follow-up is tool data.
        let result = DelegationMCPServer.result(first)
        #expect(result["isError"] == nil)
        let data = try #require((result["structuredContent"] as? [String: Any])?["followUp"] as? [String: Any])
        #expect(data["id"] as? String == info.id)
        #expect(data["child"] as? String == "c1")
        #expect(data["lane"] as? String == "pending")
        #expect(data["remaining"] as? Int == 1)
        #expect(text(of: result) == "Follow-up \(info.id) for child c1 is recorded; Mighty Claude hands it to the child exactly once. This child may get 1 more.")

        // The second, kept exactly as given; the other child's count is its own.
        let second = await fixture.call("follow_up", ["child": " c1\n", "text": "그리고 문서도 고쳐 줘.\n"])
        let secondInfo = try #require(second.followUp)
        #expect(secondInfo.remaining == 0 && secondInfo.id != info.id)
        #expect(text(of: DelegationMCPServer.result(second))?.hasSuffix("It was this child's last follow-up.") == true)
        #expect(await fixture.call("follow_up", ["child": "c2", "text": "Rebase on main."]).followUp?.remaining == 1)
        file = await fixture.coordinator.file
        #expect(file.followUps.map(\.text) == ["Also add tests.", "그리고 문서도 고쳐 줘.\n", "Rebase on main."])
        #expect(file.followUps.map(\.childId) == ["c1", "c1", "c2"])
        #expect(file.followUps.allSatisfy { $0.lane == .pending && $0.receipt == nil })
        #expect(Set(file.followUps.map(\.id)).count == 3)
        #expect(file.children.map(\.followUpCount) == [2, 1])

        // The third to c1 is refused with follow_up_limit and changes nothing.
        let bytes = try fixture.fileBytes()
        let third = await fixture.call("follow_up", ["child": "c1", "text": "One more thing."])
        #expect(third == .refusal(.followUpLimit))
        #expect(try fixture.fileBytes() == bytes)
        #expect(await fixture.coordinator.file == file)
        let refusal = DelegationMCPServer.result(third)
        #expect(refusal["isError"] as? Bool == true)
        #expect(refusal["structuredContent"] as? [String: String] == ["refused": "follow_up_limit"])

        // A relaunch from the same profile keeps both follow-ups and the limit;
        // what was still pending at launch is held.
        let relaunched = try Fixture.makeCoordinator(store: fixture.store, host: fixture.host, place: place)
        var held = file
        for index in held.followUps.indices { held.followUps[index].lane = .held }
        #expect(await relaunched.file == held)
        #expect(try fixture.store.load() == held)
        let heldBytes = try fixture.fileBytes()
        #expect(await relaunched.handle(DelegationRequest(tool: "follow_up", arguments: ["child": "c1", "text": "Again."]), binding: fixture.binding("parent")) == .refusal(.followUpLimit))
        #expect(try fixture.fileBytes() == heldBytes)
    }

    @Test func followUpsAtOnceNeverPassTheLimit() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let fixture = try Fixture(place, children: [record("c1", state: .running)])
        let answers = await fixture.callAtOnce("follow_up", (1...5).map { ["child": "c1", "text": "Follow-up \($0)."] })
        #expect(answers.count == 5)
        #expect(answers.filter { $0.followUp != nil }.count == 2)
        #expect(answers.filter { $0 == .refusal(.followUpLimit) }.count == 3)
        let file = await fixture.coordinator.file
        #expect(file.followUps.count == 2)
        #expect(file.children.first?.followUpCount == 2)
        #expect(try fixture.store.load() == file)
    }

    @Test func followUpIsRefusedForAClosedChildOrAClosedParentAndChangesNothing() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let fixture = try Fixture(place, children: [record("closed", state: .closed), record("discarded", state: .discarded), record("failed", state: .failed),
                                                     record("open", state: .reported), record("full", state: .running, followUps: 2), record("theirs", parent: "other", state: .running)])
        let bytes = try fixture.fileBytes()
        let file = await fixture.coordinator.file

        // A child that is not open: its pane closed, a human discarded it, or its start failed.
        for id in ["closed", "discarded", "failed"] {
            let answer = await fixture.call("follow_up", ["child": id, "text": "Go on."])
            #expect(answer == .refusal(.childClosed), "\(id)")
            #expect(DelegationMCPServer.result(answer)["structuredContent"] as? [String: String] == ["refused": "child_closed"], "\(id)")
        }
        #expect(await fixture.call("follow_up", ["child": "full", "text": "Go on."]) == .refusal(.followUpLimit))
        // A child that is not the caller's, an empty text or one over 48 KiB is an error.
        #expect(await fixture.call("follow_up", ["child": "theirs", "text": "Go on."]) == .failure(DelegationCoordinator.notYourChildMessage("theirs")))
        #expect(await fixture.call("follow_up", ["child": "open", "text": "Go on."], from: "other") == .failure(DelegationCoordinator.notYourChildMessage("open")))
        #expect(await fixture.call("follow_up", ["child": "no-such-child", "text": "Go on."]) == .failure(DelegationCoordinator.notYourChildMessage("no-such-child")))
        #expect(await fixture.call("follow_up", ["child": "open", "text": " \n"]).error == "follow_up needs a text: the instruction for the child.")
        let long = String(repeating: "x", count: DelegationCoordinator.maximumFollowUpBytes + 1)
        #expect(await fixture.call("follow_up", ["child": "open", "text": long]).error == "A follow-up's text may be at most 48 KiB.")
        // Measured as it is stored: under 48 KiB in UTF-8, over it with its escapes.
        let quotes = String(repeating: "\"", count: DelegationCoordinator.maximumFollowUpBytes / 2 + 1)
        #expect(await fixture.call("follow_up", ["child": "open", "text": quotes]).error == "A follow-up's text may be at most 48 KiB.")
        // No run input may hold a NUL character, so a held one would block the child's pane.
        #expect(await fixture.call("follow_up", ["child": "open", "text": "Go on.\0Then stop."]).error == "A follow-up's text may not contain a NUL character.")
        #expect(await fixture.call("follow_up", ["child": "open"]).error == "follow_up takes exactly these string arguments: child, text.")

        // The parent's pane closed: its children keep their cards, and nothing more is sent to them.
        fixture.host.close("parent")
        for id in ["open", "full", "closed"] {
            let answer = await fixture.call("follow_up", ["child": id, "text": "Go on."])
            #expect(answer == .refusal(.parentClosed), "\(id)")
            #expect(text(of: DelegationMCPServer.result(answer)) == "Refused: parent_closed. Nothing was changed.", "\(id)")
        }

        #expect(try fixture.fileBytes() == bytes)
        #expect(await fixture.coordinator.file == file)
        #expect(!fixture.host.calls.contains { $0.hasPrefix("deliver") || $0.hasPrefix("startRun") || $0.hasPrefix("createPane") })
    }

    @Test func overTheRealSocketMergeAndFollowUpComeBackAsToolData() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        let (child, head) = try await Self.reportedChild(place, "c1", files: ["a.txt"])
        let fixture = try Fixture(place, children: [child])
        let short = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: short) }
        let socketPath = short.appendingPathComponent("io.sock").path
        let bindings = PaneMCPBindingRegistry()
        let server = AgentIOSocketServer(socketPath: socketPath, bindings: bindings, handler: NoTerminalCalls(), delegation: fixture.coordinator)
        try server.start()
        defer { server.stop() }
        let location = PaneMCPServerLocation(socketPath: socketPath, executable: appExecutable)
        let parent = bindings.bind(agentPaneId: "parent", server: location, workspaceId: "ws-1", workspacePath: place.repo.path, provider: "claude", kind: SessionKind.claude, delegation: true)
        let mcp = DelegationMCPServer(environment: [PaneMCPBinding.tokenEnvironmentKey: parent.token, PaneMCPBinding.socketEnvironmentKey: socketPath])

        let merge = try #require(json(mcp.handle(line: toolCall("merge", ["child": "c1", "expected_head": head])))?["result"] as? [String: Any])
        #expect(merge["isError"] == nil)
        let merged = try #require((merge["structuredContent"] as? [String: Any])?["merged"] as? [String: Any])
        #expect(merged["child"] as? String == "c1")
        #expect(merged["preMergeCommit"] as? String == base)
        #expect(merged["mergedCommit"] as? String == head)
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)

        let again = try #require(json(mcp.handle(line: toolCall("merge", ["child": "c1", "expected_head": head])))?["result"] as? [String: Any])
        #expect(again["isError"] as? Bool == true)
        #expect(again["structuredContent"] as? [String: String] == ["refused": "not_reported"])

        let followUp = try #require(json(mcp.handle(line: toolCall("follow_up", ["child": "c1", "text": "Check the docs too."])))?["result"] as? [String: Any])
        #expect(followUp["isError"] == nil)
        let recorded = try #require((followUp["structuredContent"] as? [String: Any])?["followUp"] as? [String: Any])
        #expect(recorded["child"] as? String == "c1")
        #expect(recorded["lane"] as? String == "pending")
        #expect(recorded["remaining"] as? Int == 1)
        let file = await fixture.coordinator.file
        #expect(file.followUps.map(\.id) == [recorded["id"] as? String].compactMap { $0 })
        #expect(file.merges.map(\.mergedCommit) == [head])
    }
}
