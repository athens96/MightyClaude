import Foundation
import Testing
@testable import MightyCore

/// The app side as a child's card sees it: the panes a test sets.
private final class CardHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { false }
    func startRun(sessionId: String, input: String) async -> String? { nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async { lock.withLock { panes[sessionId]?.activity = .idle } }
}

/// A child's card through the coordinator, on real git repositories in a temp
/// folder: merge and undo write and take back their MergeRecord, a refusal
/// carries its one reason and changes nothing, the card offers only the
/// actions that apply, and the discard confirmation gets the nested worktrees.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"))
struct DelegationCardTests {
    @Test func mergesAndUndoesFromTheCardWithTheActionsThatApply() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, host, base, head) = try await reportedChild(place)
        #expect(await DelegationSidebar.rows(coordinator.file).map(\.cardActions) == [[.merge, .discard]])

        guard case .merged(let record) = await coordinator.mergeFromCard("c1") else { Issue.record("not merged"); return }
        #expect(record == MergeRecord(childId: "c1", kind: .cardFastForward, parentBranch: "main", preMergeCommit: base, mergedCommit: head, childHead: head))
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(await coordinator.file.children.map(\.state) == [.merged])
        #expect(await coordinator.file.merges == [record])
        #expect(try store(place).load().merges == [record])
        let merged = try #require(await DelegationSidebar.rows(coordinator.file).first)
        #expect(merged.canUndo); #expect(merged.cardActions == [.undo, .discard])

        // A running parent refuses the undo with parent_busy, and nothing changes.
        host.set(DelegationPaneState(sessionId: "parent", permissionMode: "auto", folder: place.repo.path, runId: "run-2", activity: .running))
        #expect(await coordinator.undoMergeFromCard("c1") == .refused(.parentBusy))
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(await coordinator.file.merges == [record]); #expect(await coordinator.file.children.map(\.state) == [.merged])

        // Idle again: the branch goes back, the record leaves the file and the child is reported again.
        host.set(DelegationPaneState(sessionId: "parent", permissionMode: "auto", folder: place.repo.path, runId: "run-2", activity: .finished))
        #expect(await coordinator.undoMergeFromCard("c1") == .undone(record, restoredBranch: false))
        #expect(try await place.git(["rev-parse", "HEAD"]) == base)
        #expect(await coordinator.file.merges.isEmpty); #expect(await coordinator.file.children.map(\.state) == [.reported])
        #expect(try store(place).load().merges.isEmpty)
        #expect(await DelegationSidebar.rows(coordinator.file).map(\.cardActions) == [[.merge, .discard]])
        guard case .failed = await coordinator.undoMergeFromCard("c1") else { Issue.record("undid a merge that is not there"); return }
    }

    @Test func refusesWithItsReasonAndChangesNothing() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, _, base, _) = try await reportedChild(place)
        let before = await coordinator.file

        // Tracked changes in the parent's folder.
        try Data("changed\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        #expect(await coordinator.mergeFromCard("c1") == .refused(.trackedChanges))
        try await place.git(["checkout", "--", "file-0.txt"])
        // Another branch checked out in the parent's folder.
        try await place.git(["checkout", "-q", "-b", "elsewhere"])
        #expect(await coordinator.mergeFromCard("c1") == .refused(.branchNotCheckedOut))
        try await place.git(["checkout", "-q", "main"])
        #expect(try await place.git(["rev-parse", "HEAD"]) == base)
        #expect(await coordinator.file == before)
        #expect(await DelegationSidebar.rows(coordinator.file).map(\.cardActions) == [[.merge, .discard]])
        guard case .failed = await coordinator.mergeFromCard("nobody") else { Issue.record("merged a child that is not there"); return }
    }

    @Test func listsTheWorktreesNestedInTheChildsForTheDiscardConfirmation() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, _, _, _) = try await reportedChild(place)
        let child = try #require(await coordinator.file.children.first)
        #expect(await coordinator.nestedWorktrees(of: "c1") == [])
        let nested = URL(fileURLWithPath: child.worktreePath).appendingPathComponent("inner", isDirectory: true)
        try await place.git(["worktree", "add", "-q", "-b", "inner", nested.path])
        #expect(await coordinator.nestedWorktrees(of: "c1") == [ChildCleanup.canonical(nested.path)])
        #expect(await coordinator.nestedWorktrees(of: "nobody") == nil)
    }
}

private func store(_ place: MergePlace) -> DelegationFileStore {
    DelegationFileStore(directory: place.base.appendingPathComponent("profile", isDirectory: true))
}

/// A repository on main, and a coordinator over a child `c1` made the app's
/// way that committed `a.txt` and reported it, with its parent's open pane
/// idle in the repository: the coordinator, its host, the base and the head.
private func reportedChild(_ place: MergePlace) async throws -> (DelegationCoordinator, CardHost, String, String) {
    let base = try await place.repository()
    for (key, value) in [("user.name", "Fixture"), ("user.email", "fixture@example.invalid"), ("commit.gpgsign", "false")] { try await place.git(["config", key, value]) }
    var child = try await place.child("c1")
    child.parentCheckout = place.repo.path
    let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
    let reported = child.recordReport(head: head); #expect(reported)
    try store(place).save(DelegationFile(children: [child]))
    let host = CardHost()
    host.set(DelegationPaneState(sessionId: "parent", permissionMode: "auto", folder: place.repo.path, runId: "run-1", activity: .finished))
    let coordinator = try DelegationCoordinator(store: store(place), host: host, worktrees: ChildWorktreeMaker(root: place.root, freeBytes: { _ in 50_000_000_000 }), isSwitchOn: { true })
    return (coordinator, host, base, head)
}
