import Foundation
import Testing
@testable import MightyCore

/// A host whose panes are all closed: enough for a run start to be recorded.
private final class ClosedPanesHost: DelegationHost, @unchecked Sendable {
    func createPane(_ pane: DelegationChildPane) async -> Bool { false }
    func startRun(sessionId: String, input: String) async -> String? { nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { nil }
    func stopRun(sessionId: String) async {}
}

private func pane(_ id: String, in workspaceId: String, parent: String? = nil) -> RunSession {
    var session = RunSession(id: id, workspaceId: workspaceId, title: id)
    session.parentSessionId = parent
    return session
}

private func child(_ id: String, of parent: String, _ state: ChildState = .running, checkout: String? = nil) -> DelegationChildRow {
    DelegationChildRow(id: id, parentSessionId: parent, state: state, parentBranch: "main", parentCheckout: checkout)
}

private func record(_ id: String, of parent: String, _ state: ChildState) -> ChildRecord {
    ChildRecord(id: id, parentSessionId: parent, worktreePath: "/worktrees/\(id)", parentBranch: "main", baseCommit: "base", startingMode: "auto", requestKey: "key-\(id)", state: state, parentCheckout: "/repo")
}

@Suite struct DelegationSidebarTests {
    let workspaces = [Workspace(id: "w1", name: "repo", path: "/repo"), Workspace(id: "w2", name: "sub", path: "/repo/sub"), Workspace(id: "w3", name: "app", path: "/other/app")]

    @Test func childrenAreListedUnderTheirOpenParentInTheOrderTheyWereMade() {
        let sessions = [pane("p1", in: "w1"), pane("c1", in: "w1", parent: "p1"), pane("x", in: "w1"), pane("stray", in: "w1", parent: "p1"), pane("q", in: "w2")]
        let children = [child("c2", of: "p1", .failed), child("c1", of: "p1", .reported)]
        let tree = DelegationSidebar.tree(workspaceId: "w1", workspaces: workspaces, sessions: sessions, children: children)
        // A child pane is listed once, under its parent; a pane with no record stays at the top.
        #expect(tree.top.map(\.id) == ["p1", "x", "stray"])
        #expect(tree.children["p1"] == children)
        #expect(tree.parentClosed.isEmpty)
        let other = DelegationSidebar.tree(workspaceId: "w2", workspaces: workspaces, sessions: sessions, children: children)
        #expect(other.top.map(\.id) == ["q"])
        #expect(other.children.isEmpty && other.parentClosed.isEmpty)
    }

    @Test func childrenOfAClosedParentAreListedUnderAParentClosedNode() {
        let sessions = [pane("p1", in: "w1"), pane("c3", in: "w2", parent: "gone")]
        let children = [
            child("c3", of: "gone", .running, checkout: "/repo"),   // its own pane is open in w2
            child("c4", of: "gone", .closed, checkout: "/repo"),    // no pane: the workspace at the checkout itself
            child("c5", of: "gone2", .failed, checkout: "/repo/"),  // the same folder written another way
            child("c6", of: "gone3", .ended, checkout: "/other"),   // the first workspace inside the checkout
            child("c7", of: "gone4", .merged),                      // no pane and no checkout: not placed
            child("c8", of: "gone", .interrupted, checkout: "/repo"),
        ]
        let w1 = DelegationSidebar.tree(workspaceId: "w1", workspaces: workspaces, sessions: sessions, children: children)
        #expect(w1.top.map(\.id) == ["p1"])
        #expect(w1.children.isEmpty)
        #expect(w1.parentClosed.map(\.id) == ["gone", "gone2"])
        #expect(w1.parentClosed.first?.children.map(\.id) == ["c4", "c8"])
        #expect(w1.parentClosed.first?.parentBranch == "main")
        let w2 = DelegationSidebar.tree(workspaceId: "w2", workspaces: workspaces, sessions: sessions, children: children)
        #expect(w2.top.isEmpty)
        #expect(w2.parentClosed.map(\.id) == ["gone"])
        #expect(w2.parentClosed.first?.children.map(\.id) == ["c3"])
        let w3 = DelegationSidebar.tree(workspaceId: "w3", workspaces: workspaces, sessions: sessions, children: children)
        #expect(w3.parentClosed.map(\.id) == ["gone3"])
        let listed = [w1, w2, w3].flatMap { $0.parentClosed.flatMap(\.children) }.map(\.id).sorted()
        #expect(listed == ["c3", "c4", "c5", "c6", "c8"])
    }

    @Test func aRowShowsItsStateWithWaitingWhileARunningChildAsksAHuman() {
        #expect(DelegationSidebar.shownState(.running, asksHuman: true) == .waiting)
        #expect(DelegationSidebar.shownState(.running, asksHuman: false) == .running)
        #expect(DelegationSidebar.shownState(.waiting, asksHuman: false) == .waiting)
        #expect(DelegationSidebar.shownState(.reported, asksHuman: true) == .reported)
        let tones: [ChildState: DesignTone] = [.creating: .run, .running: .run, .waiting: .wait, .reported: .done, .merged: .done,
                                               .ended: .idle, .interrupted: .stop, .failed: .err, .closed: .idle]
        for (state, tone) in tones { #expect(DelegationSidebar.tone(state) == tone, "\(state)") }
    }

    @Test func rowsLeaveOutDiscardedChildrenAndCarryTheTaskInOneLine() {
        let file = DelegationFile(children: [record("a", of: "p", .reported), record("b", of: "p", .discarded), record("c", of: "p", .failed)],
                                  copies: [DelegationCopy(childId: "a", kind: .task, revision: 0, contents: Data("Fix the login\n  bug".utf8)),
                                           DelegationCopy(childId: "a", kind: .report, revision: 1, contents: Data("done".utf8))])
        let rows = DelegationSidebar.rows(file)
        #expect(rows.map(\.id) == ["a", "c"])
        #expect(rows.first == DelegationChildRow(id: "a", parentSessionId: "p", state: .reported, task: "Fix the login bug", parentBranch: "main", parentCheckout: "/repo"))
        #expect(rows.last?.task == nil)
    }

    @Test func theCoordinatorHandsOnTheChildListAtLoadAndAfterEachChange() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("delegation-sidebar-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let store = DelegationFileStore(directory: folder)
        try store.save(DelegationFile(children: [record("c1", of: "p1", .creating)]))
        let coordinator = try DelegationCoordinator(store: store, host: ClosedPanesHost(), isSwitchOn: { false })
        var rows = coordinator.childRows.makeAsyncIterator()
        #expect(await rows.next()?.map(\.state) == [.creating])
        await coordinator.childRunStarted("c1", runId: "run-1")
        #expect(await rows.next()?.map(\.state) == [.running])
    }
}
