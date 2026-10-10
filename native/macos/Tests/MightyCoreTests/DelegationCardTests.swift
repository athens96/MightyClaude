import Foundation
import Testing
@testable import MightyCore

/// The app side as a child's card sees it: the panes a test sets, each item
/// handed to a pane, which it takes as its next run, and each pane stopped.
private final class CardHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var handed: [String] = []
    private var stopped: [String] = []

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    func pane(_ sessionId: String) -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    var deliveries: [String] { lock.withLock { handed } }
    var stops: [String] { lock.withLock { stopped } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { false }
    func startRun(sessionId: String, input: String) async -> String? { nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? {
        lock.withLock { handed.append("\(sessionId) \(route.rawValue)"); return "run-delivered" }
    }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async { lock.withLock { stopped.append(sessionId); panes[sessionId]?.activity = .idle } }
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

extension DelegationCardTests {
    @Test func asksAgainWhenAWorktreeNestedInTheChildsSinceTheConfirmation() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, _, _, _) = try await reportedChild(place)
        let child = try #require(await coordinator.file.children.first)
        let confirmed = try #require(await coordinator.nestedWorktrees(of: "c1")); #expect(confirmed.isEmpty)

        // Nested after the confirmation named none: nothing is removed, and the answer names it.
        let nested = URL(fileURLWithPath: child.worktreePath).appendingPathComponent("inner", isDirectory: true)
        try await place.git(["worktree", "add", "-q", "-b", "inner", nested.path])
        #expect(await coordinator.discardFromCard("c1", confirmedNested: confirmed) == .nestedChanged([ChildCleanup.canonical(nested.path)]))
        #expect(FileManager.default.fileExists(atPath: child.worktreePath)); #expect(FileManager.default.fileExists(atPath: nested.path))
        #expect(try await place.git(["rev-parse", "--verify", "-q", "refs/heads/mighty/c1"]).count == 40)
        #expect(await coordinator.file.children.map(\.state) == [.reported])

        // Confirmed with it named: the child, the nested worktree and the child's branch go.
        #expect(await coordinator.discardFromCard("c1", confirmedNested: [ChildCleanup.canonical(nested.path)]) == .discarded)
        #expect(!FileManager.default.fileExists(atPath: child.worktreePath)); #expect(!FileManager.default.fileExists(atPath: nested.path))
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]).isEmpty)
        #expect(await coordinator.file.children.map(\.state) == [.discarded])
        #expect(await DelegationSidebar.rows(coordinator.file).isEmpty)
    }

    @Test func aDiscardThatCannotListTheNestedWorktreesLeavesTheChildsRunAlone() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, host, _, _) = try await reportedChild(place)
        let child = try #require(await coordinator.file.children.first)
        host.set(DelegationPaneState(sessionId: "c1", permissionMode: "acceptEdits", folder: child.worktreePath, parentSessionId: "parent", runId: "c1-run", activity: .running))
        // The parent's folder is gone, so the worktrees nested in the child's cannot be listed.
        let moved = place.base.appendingPathComponent("repo-moved", isDirectory: true)
        try FileManager.default.moveItem(at: place.repo, to: moved)
        #expect(await coordinator.nestedWorktrees(of: "c1") == nil)

        #expect(await coordinator.discardFromCard("c1", confirmedNested: nil) == .failed(L("delegation.card.error.nestedUnlisted")))
        // Its run is never stopped, and nothing is removed.
        #expect(host.stops.isEmpty)
        #expect(host.pane("c1")?.activity == .running)
        #expect(FileManager.default.fileExists(atPath: child.worktreePath))
        #expect(await coordinator.file.children.map(\.state) == [.reported])

        // Listed again with the folder back, the discard goes on: its run is stopped, then it goes.
        try FileManager.default.moveItem(at: moved, to: place.repo)
        #expect(await coordinator.discardFromCard("c1", confirmedNested: []) == .discarded)
        #expect(host.stops.first == "c1" && host.pane("c1")?.activity == .idle)
        #expect(!FileManager.default.fileExists(atPath: child.worktreePath))
        #expect(await coordinator.file.children.map(\.state) == [.discarded])
    }

    @Test func aCardsFailureReadsInTheAppsLanguage() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, _, _, _) = try await reportedChild(place)
        var merges: [AppLanguage: String] = [:]
        for language in [AppLanguage.ko, .en, .zh, .ja] {
            merges[language] = await LocaleOverride.$language.withValue(language) { () async -> String? in
                #expect(await coordinator.undoMergeFromCard("c1") == .failed(L("delegation.card.error.noMergeToUndo")))
                #expect(await coordinator.discardFromCard("nobody", confirmedNested: []) == .failed(L("delegation.card.error.cannotDiscard")))
                guard case .failed(let message) = await coordinator.mergeFromCard("nobody") else { return nil }
                #expect(message == L("delegation.card.error.noChild"))
                return message
            }
        }
        // Each language's own words, never the one English text every language used to get.
        #expect(merges.count == 4 && Set(merges.values).count == 4)
    }

    @Test func deliveryStartsNoRunInAPaneWhoseCheckoutACardIsChanging() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let (coordinator, host, _, _) = try await reportedChild(place)
        await coordinator.addPending(Notice(id: "n1", childId: "c1", reportRevision: 1, kind: .reported))

        await coordinator.holdCardPanes(["parent"])
        await coordinator.deliverPending()
        #expect(host.deliveries.isEmpty)
        #expect(await coordinator.file.notices.map(\.lane) == [.pending])

        await coordinator.holdCardPanes([])
        await coordinator.deliverPending()
        #expect(host.deliveries == ["parent queue"])
        #expect(await coordinator.file.notices.map(\.lane) == [.delivered])
    }

    @Test func undoesOnlyTheMergeInEffect() {
        let first = MergeRecord(childId: "c1", kind: .cardFastForward, parentBranch: "main", preMergeCommit: "a", mergedCommit: "b", childHead: "b")
        let second = MergeRecord(childId: "c1", kind: .cardFastForward, parentBranch: "main", preMergeCommit: "b", mergedCommit: "c", childHead: "c")
        var child = ChildRecord(id: "c1", parentSessionId: "parent", worktreePath: "/tmp/w/c1", parentBranch: "main", baseCommit: "a", startingMode: "auto", requestKey: "k", state: .merged)
        var file = DelegationFile(children: [child], merges: [first, second])
        #expect(file.undoableMerge(of: "c1") == second)
        for state in [ChildState.reported, .running, .ended, .failed, .discarded] {
            child.state = state; file.children = [child]
            #expect(file.undoableMerge(of: "c1") == nil, "\(state)")
        }
        // Closed: only the merge whose head it was closed at.
        child.state = .closed; child.closedHead = "C"; file.children = [child]
        #expect(file.undoableMerge(of: "c1") == second)
        file.merges = [first]
        #expect(file.undoableMerge(of: "c1") == nil)
        child.closedHead = nil; file.children = [child]; file.merges = [first, second]
        #expect(file.undoableMerge(of: "c1") == nil)
        #expect(DelegationChildRow(child, in: file).canUndo == false)
    }
}

extension DelegationCoordinator {
    /// A pending item, as a run end or a follow-up leaves it after launch.
    fileprivate func addPending(_ notice: Notice) { file.notices.append(notice) }
    /// The panes a card's merge or undo would be changing.
    fileprivate func holdCardPanes(_ ids: Set<String>) { cardPanes = ids }
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
