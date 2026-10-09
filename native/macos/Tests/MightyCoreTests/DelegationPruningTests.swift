import Foundation
import Testing
@testable import MightyCore

/// Pruning the delegation side file (macOS only) as it nears its 4 MiB cap,
/// and the store_full refusal of a new child when pruning leaves no room.
struct DelegationPruningTests {
    private let cap = DelegationFileStore.maximumFileBytes
    private let fullCopy = DelegationFileStore.maximumCopyBytes
    /// Parent p1 is open, p2 is closed; no merge can be undone.
    private let context = DelegationPruneContext(openPaneIds: ["p1"]) { _ in false }

    private func child(_ id: String, parent: String = "p1", state: ChildState) -> ChildRecord {
        ChildRecord(id: id, parentSessionId: parent, worktreePath: "/tmp/worktrees/\(id)", parentBranch: "main", baseCommit: "a1", startingMode: "acceptEdits", requestKey: "k-\(id)", state: state)
    }

    private func copy(_ id: String, _ kind: DelegationCopy.Kind = .report, length: Int = 1_000) -> DelegationCopy {
        DelegationCopy(childId: id, kind: kind, revision: kind == .report ? 1 : 0, contents: Data(String(repeating: "r", count: length).utf8))
    }

    private func notice(_ id: String, _ lane: DeliveryLane, kind: Notice.Kind = .reported) -> Notice {
        Notice(id: "n-\(id)", childId: id, reportRevision: 1, kind: kind, lane: lane)
    }

    private func merge(_ id: String) -> MergeRecord {
        MergeRecord(childId: id, kind: .toolFastForward, parentBranch: "main", preMergeCommit: "a1", mergedCommit: "m-\(id)", childHead: "m-\(id)")
    }

    private func size(_ file: DelegationFile) throws -> Int { try DelegationFileStore.encode(file).count }

    /// `file` pruned until at least `bytes` bytes are freed.
    private func freeing(_ bytes: Int, from file: DelegationFile, _ context: DelegationPruneContext) throws -> DelegationFile? {
        try file.pruned(leaving: cap - size(file) + bytes, context: context)
    }

    private func without(_ file: DelegationFile, reportOf ids: [String]) -> DelegationFile {
        var file = file
        file.copies.removeAll { ids.contains($0.childId) && $0.kind == .report }
        return file
    }

    private func without(_ file: DelegationFile, records ids: [String]) -> DelegationFile {
        var file = file
        file.children.removeAll { ids.contains($0.id) }; file.notices.removeAll { ids.contains($0.childId) }
        file.followUps.removeAll { ids.contains($0.childId) }; file.merges.removeAll { ids.contains($0.childId) }
        file.copies.removeAll { ids.contains($0.childId) }
        return file
    }

    /// `base` plus open filler children whose report copies leave exactly
    /// `room` bytes under the cap (a negative room passes it).
    private func filled(_ base: DelegationFile, leaving room: Int) throws -> DelegationFile {
        var file = base, index = 0
        func add(_ length: Int) {
            let id = String(format: "fill%03d", index); index += 1
            file.children.append(child(id, state: .running)); file.setCopy(copy(id, length: length))
        }
        // Whole 64 KiB fillers, measured once, until about three are left to fit.
        let whole = try (cap - room - size(file)) / (fullCopy + 1_000) - 3
        for _ in 0 ..< max(0, whole) { add(fullCopy) }
        while true {
            add(10_000)
            let length = try 10_000 + cap - room - size(file)
            try #require(length >= 10_000)
            if length <= fullCopy { file.setCopy(copy(file.children.last!.id, length: length)); return file }
            file.setCopy(copy(file.children.last!.id, length: min(fullCopy, length - 30_000)))
        }
    }

    @Test func aFileThatFitsComesBackUnchanged() throws {
        let file = DelegationFile(children: [child("c1", state: .closed)], notices: [notice("c1", .delivered)], copies: [copy("c1")])
        #expect(try file.pruned(context: context) == file)
        #expect(try freeing(0, from: file, context) == file)
    }

    @Test func reportCopiesOfTheOldestClosedRecordsGoFirstThenTheOldestDeliveredRecords() throws {
        let file = DelegationFile(
            children: [child("c1", state: .closed), child("c2", state: .running), child("c3", state: .discarded), child("c4", state: .closed)],
            notices: [notice("c1", .delivered), notice("c3", .delivered), notice("c4", .delivered)],
            followUps: [FollowUp(id: "f1", childId: "c1", text: "테스트도 추가해 줘", lane: .delivered)],
            merges: [merge("c1")],
            copies: [copy("c1", .task), copy("c1"), copy("c2"), copy("c3"), copy("c4")])
        let reportBytes = try ["c1", "c3", "c4"].map { try DelegationFile.elementBytes(copy($0)) }

        // The oldest closed record's report copy goes first; its TASK copy and record stay.
        #expect(try freeing(1, from: file, context) == without(file, reportOf: ["c1"]))
        // Then the next closed record's (a discarded child is closed too); the open child's copy is kept.
        #expect(try freeing(reportBytes[0] + 1, from: file, context) == without(file, reportOf: ["c1", "c3"]))
        // Only when every closed report copy is gone does the oldest closed,
        // fully delivered record go, with its notice, follow-up, merge and TASK copy.
        let allReports = reportBytes.reduce(0, +)
        let oldestGone = try #require(try freeing(allReports + 1, from: file, context))
        #expect(oldestGone == without(without(file, reportOf: ["c1", "c3", "c4"]), records: ["c1"]))
        #expect(oldestGone.children.map(\.id) == ["c2", "c3", "c4"])
        #expect(oldestGone.copy(childId: "c2", kind: .report) == copy("c2"))
        // The next oldest record goes next.
        let firstRecordBytes = try DelegationFile.elementBytes(file.children[0]) + DelegationFile.elementBytes(file.notices[0])
            + DelegationFile.elementBytes(file.followUps[0]) + DelegationFile.elementBytes(file.merges[0]) + DelegationFile.elementBytes(file.copies[0])
        let nextGone = try #require(try freeing(allReports + firstRecordBytes + 1, from: file, context))
        #expect(nextGone.children.map(\.id) == ["c2", "c4"])
    }

    @Test func openChildrenFailedChildrenHeldItemsOfOpenPanesAndUndoableMergesAreNeverPruned() throws {
        let file = DelegationFile(
            children: [child("open", state: .running), child("waiting", state: .waiting), child("failed", state: .failed),
                       child("heldOpenParent", state: .closed), child("heldClosedParent", parent: "p2", state: .closed),
                       child("pendingNotice", state: .closed), child("heldFollowUp", state: .closed),
                       child("undoable", state: .closed), child("merged", state: .closed)],
            notices: [notice("open", .held), notice("failed", .delivered, kind: .failedToStart), notice("heldOpenParent", .held),
                      notice("heldClosedParent", .held), notice("pendingNotice", .pending), notice("undoable", .delivered), notice("merged", .delivered)],
            followUps: [FollowUp(id: "f-open", childId: "open", text: "하나 더", lane: .held),
                        FollowUp(id: "f-held", childId: "heldFollowUp", text: "마저 해 줘", lane: .held)],
            merges: [merge("undoable"), merge("merged")],
            copies: [copy("open", .task), copy("open"), copy("waiting"), copy("failed", .task), copy("heldOpenParent"), copy("heldClosedParent"),
                     copy("pendingNotice"), copy("heldFollowUp"), copy("undoable"), copy("merged")])
        let canUndo = DelegationPruneContext(openPaneIds: ["p1", "open", "waiting"]) { $0.mergedCommit == "m-undoable" }

        // Everything allowed is pruned: every closed report copy, and the closed
        // records that are fully delivered (a held notice of a closed parent
        // counts) and have no merge that can still be undone.
        let expected = without(without(file, reportOf: ["heldOpenParent", "heldClosedParent", "pendingNotice", "heldFollowUp", "undoable", "merged"]),
                               records: ["heldClosedParent", "merged"])
        #expect(try file.pruned(leaving: cap - size(expected), context: canUndo) == expected)
        #expect(expected.children.map(\.id) == ["open", "waiting", "failed", "heldOpenParent", "pendingNotice", "heldFollowUp", "undoable"])
        #expect(expected.merges == [merge("undoable")])
        #expect(expected.followUps.map(\.id) == ["f-open", "f-held"])
        #expect(expected.copy(childId: "open", kind: .report) != nil); #expect(expected.copy(childId: "waiting", kind: .report) != nil)
        #expect(expected.copy(childId: "failed", kind: .task) != nil)
        // Needing one byte more than that is not possible.
        #expect(try file.pruned(leaving: cap - size(expected) + 1, context: canUndo) == nil)

        // With p1 closed too, the notice held for its closed child counts as delivered.
        let parentClosed = DelegationPruneContext(openPaneIds: ["open", "waiting"]) { $0.mergedCommit == "m-undoable" }
        let more = without(expected, records: ["heldOpenParent"])
        #expect(try file.pruned(leaving: cap - size(more), context: parentClosed) == more)
    }

    @Test func aNewChildIsRefusedWithStoreFullWhenPruningLeavesNoRoom() throws {
        let newChild = child("new", state: .creating), task = copy("new", .task, length: 200)

        // Only open children: nothing can be pruned, so there is no room for a
        // record and a full report copy.
        let openOnly = try filled(DelegationFile(), leaving: 100)
        #expect(try size(openOnly) == cap - 100)
        #expect(try openOnly.admitting(newChild, task: task, context: context) == .refused(.storeFull))
        #expect(DelegationReasonCode.storeFull.rawValue == "store_full")

        // A closed record whose notice is still held for its open parent is protected too.
        let oldCopies = [copy("old", .task, length: fullCopy), copy("old", length: fullCopy)]
        let held = try filled(DelegationFile(children: [child("old", state: .closed)], notices: [notice("old", .held)], copies: oldCopies), leaving: 100)
        // Dropping its report copy alone is not room enough, and the record stays.
        #expect(try held.admitting(newChild, task: task, context: context) == .refused(.storeFull))
    }

    @Test func pruningMakesRoomForANewChildWhenItCan() throws {
        let newChild = child("new", state: .creating), task = copy("new", .task, length: 200)
        let oldCopies = [copy("old", .task, length: fullCopy), copy("old", length: fullCopy)]
        let roomy = try filled(DelegationFile(children: [child("old", state: .closed)], notices: [notice("old", .delivered)], copies: oldCopies), leaving: 100)

        guard case .admitted(let admitted) = try roomy.admitting(newChild, task: task, context: context) else {
            Issue.record("the closed, delivered record should have made room"); return
        }
        #expect(admitted.children.last == newChild); #expect(admitted.copy(childId: "new", kind: .task) == task)
        #expect(!admitted.children.contains { $0.id == "old" }); #expect(admitted.copies.allSatisfy { $0.childId != "old" })
        #expect(admitted.children.count == roomy.children.count)
        #expect(try size(admitted) + DelegationFile.reportCopyRoom(childId: "new") <= cap)
    }

    @Test func savingNearTheCapPrunesAndAFileThatCannotFitIsNotWritten() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("DelegationPruningTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = DelegationFileStore(directory: directory)

        // Less than one more child's room left: the closed record's report copy goes.
        let old = DelegationFile(children: [child("old", state: .closed)], notices: [notice("old", .delivered)], copies: [copy("old", .task), copy("old", length: fullCopy)])
        let near = try filled(old, leaving: 10_000)
        let saved = try store.save(near, pruning: context)
        #expect(saved == without(near, reportOf: ["old"]))
        #expect(try store.load() == saved)
        #expect(try size(saved) + DelegationFile.newChildRoom() <= cap)

        // A file that fits but has nothing to prune is saved as it is.
        let openOnly = try filled(DelegationFile(), leaving: 10)
        #expect(try store.save(openOnly, pruning: context) == openOnly)
        let before = try Data(contentsOf: store.fileURL)

        // A file over the cap with nothing to prune is refused and nothing is written.
        let over = try filled(DelegationFile(), leaving: -10)
        #expect(throws: DelegationFileError.full) { try store.save(over, pruning: context) }
        #expect(try Data(contentsOf: store.fileURL) == before)
    }
}
