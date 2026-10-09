import Foundation
import Testing
@testable import MightyCore

/// The delegation model: child states and their only moves, the records the
/// delegation file keeps, the reason codes and the delegate request key.
struct DelegationModelTests {
    /// The ontology's moves, written out by hand. Every (state, event) pair not
    /// listed here must be refused.
    private static let expectedAfter: [ChildState: [ChildStateEvent: ChildState]] = [
        .creating: [.startRun: .running, .failStart: .failed, .closePane: .closed, .discard: .discarded],
        .running: [.askPermission: .waiting, .endWithReport: .reported, .endWithoutReport: .ended, .quitApp: .interrupted, .closePane: .closed, .discard: .discarded],
        .waiting: [.answerPermission: .running, .closePane: .closed, .discard: .discarded],
        .reported: [.startRun: .running, .merge: .merged, .closePane: .closed, .discard: .discarded],
        .ended: [.startRun: .running, .closePane: .closed, .discard: .discarded],
        .interrupted: [.startRun: .running, .closePane: .closed, .discard: .discarded],
        .merged: [.startRun: .running, .undoMerge: .reported, .closePane: .closed, .discard: .discarded],
        .failed: [.discard: .discarded],
        .closed: [.undoMerge: .closed, .discard: .discarded],
        .discarded: [:],
    ]

    /// The same moves as state pairs, self-moves excluded.
    private static let expectedMoves: [ChildState: Set<ChildState>] = [
        .creating: [.running, .failed, .closed, .discarded],
        .running: [.waiting, .reported, .ended, .interrupted, .closed, .discarded],
        .waiting: [.running, .closed, .discarded],
        .reported: [.running, .merged, .closed, .discarded],
        .ended: [.running, .closed, .discarded],
        .interrupted: [.running, .closed, .discarded],
        .merged: [.running, .reported, .closed, .discarded],
        .failed: [.discarded],
        .closed: [.discarded],
        .discarded: [],
    ]

    private func record(state: ChildState = .creating) -> ChildRecord {
        ChildRecord(id: "child-1", parentSessionId: "parent-1", worktreePath: "/tmp/worktrees/child-1", parentBranch: "main", baseCommit: "abc123",
                    startingMode: "acceptEdits", requestKey: "key", state: state)
    }

    @Test func everyStateAndEventPairIsAllowedOrRefusedExactlyAsTheOntologySays() {
        #expect(ChildState.allCases.map(\.rawValue) == ["creating", "running", "waiting", "reported", "ended", "interrupted", "merged", "failed", "closed", "discarded"])
        var allowed = 0, refused = 0
        for state in ChildState.allCases {
            for event in ChildStateEvent.allCases {
                let expected = Self.expectedAfter[state]?[event]
                #expect(state.after(event) == expected, "\(state.rawValue) + \(event.rawValue)")
                if expected == nil { refused += 1 } else { allowed += 1 }
            }
        }
        #expect(allowed == 30); #expect(refused == 80)
    }

    @Test func everyStatePairIsAMoveExactlyWhenTheOntologyListsIt() {
        var moves = 0
        for from in ChildState.allCases {
            for to in ChildState.allCases {
                let expected = Self.expectedMoves[from]?.contains(to) ?? false
                #expect(from.canMove(to: to) == expected, "\(from.rawValue) -> \(to.rawValue)")
                if expected { moves += 1 }
            }
        }
        #expect(moves == 29)
    }

    @Test func theNamedMovesHoldUndoWhileOpenAClosedChildStaysClosedAndFailedIsOnlyDiscarded() {
        // Undo of a merge: back to reported while the pane is open; a closed child stays closed.
        #expect(ChildState.merged.after(.undoMerge) == .reported)
        #expect(ChildState.closed.after(.undoMerge) == .closed)
        #expect(!ChildState.closed.canMove(to: .reported)); #expect(!ChildState.closed.canMove(to: .merged)); #expect(!ChildState.closed.canMove(to: .running))
        // A failed start is never closed, run or merged; only a human discard moves it.
        #expect(ChildState.failed.after(.discard) == .discarded)
        #expect(ChildState.failed.after(.closePane) == nil); #expect(ChildState.failed.after(.startRun) == nil)
        // Discarded is final.
        #expect(ChildStateEvent.allCases.allSatisfy { ChildState.discarded.after($0) == nil })
        // Only a reported child merges; undo is only for a merged (or closed) child.
        #expect(ChildState.allCases.filter { $0.after(.merge) != nil } == [.reported])
        #expect(ChildState.allCases.filter { $0.after(.undoMerge) != nil } == [.merged, .closed])
    }

    @Test func openMeansEveryStateExceptFailedClosedAndDiscarded() {
        #expect(ChildState.allCases.filter(\.isOpen) == [.creating, .running, .waiting, .reported, .ended, .interrupted, .merged])
        // Exactly the open states can have their pane closed.
        #expect(ChildState.allCases.filter { $0.after(.closePane) == .closed } == ChildState.allCases.filter(\.isOpen))
    }

    @Test func aRecordMovesOnlyByAllowedEventsAndARefusedEventChangesNothing() {
        var child = record()
        #expect(child.branch == "mighty/child-1"); #expect(child.state == .creating); #expect(child.reportRevision == 0); #expect(child.reportHead == nil)
        // Each step: the event, whether it is allowed, and the state after it.
        func step(_ event: ChildStateEvent, _ allowed: Bool, _ state: ChildState, sourceLocation: SourceLocation = #_sourceLocation) {
            let before = child
            let moved = child.apply(event)
            #expect(moved == allowed, sourceLocation: sourceLocation); #expect(child.state == state, sourceLocation: sourceLocation)
            if !allowed { #expect(child == before, sourceLocation: sourceLocation) }
        }
        func report(_ head: String, _ allowed: Bool) -> Bool {
            let before = child
            let moved = child.recordReport(head: head)
            return moved == allowed && (allowed || child == before)
        }
        step(.merge, false, .creating)
        #expect(report("def456", false))
        step(.startRun, true, .running)
        #expect(report("def456", true))
        #expect(child.state == .reported); #expect(child.reportRevision == 1); #expect(child.reportHead == "def456")
        // A new run (a follow-up or the human's send) clears reported; the next report is revision 2.
        step(.startRun, true, .running)
        #expect(report("fed789", true)); #expect(child.reportRevision == 2); #expect(child.reportHead == "fed789")
        step(.merge, true, .merged); step(.undoMerge, true, .reported)
        step(.merge, true, .merged); step(.closePane, true, .closed)
        step(.undoMerge, true, .closed)
        step(.startRun, false, .closed); #expect(report("000", false)); #expect(child.reportRevision == 2)
        step(.discard, true, .discarded)
        step(.discard, false, .discarded)
    }

    @Test func theReasonCodesAreTheOntologyListInOrder() {
        #expect(DelegationReasonCode.allCases.map(\.rawValue) == [
            "claude_only", "child_cannot_delegate", "width_cap", "low_disk", "store_full", "not_git", "unborn_branch", "detached_head",
            "wider_mode", "discard_human_only", "not_reported", "tracked_changes", "head_moved", "diverged", "branch_not_checked_out",
            "child_closed", "parent_closed", "follow_up_limit", "worktree_missing", "merge_conflict", "undo_parent_moved", "parent_busy",
            "workspace_has_children", "held_not_removable",
        ])
    }

    @Test func recordsRoundTripWithTheOntologyNames() throws {
        #expect(Notice.Kind.allCases.map(\.rawValue) == ["reported", "ended_without_report", "failed_to_start"])
        #expect(DeliveryLane.allCases.map(\.rawValue) == ["pending", "held", "delivered"])
        #expect(DeliveryRoute.allCases.map(\.rawValue) == ["steer", "queue"])
        #expect(MergeRecord.Kind.allCases.map(\.rawValue) == ["tool_fast_forward", "card_fast_forward", "card_merge_commit"])

        let receipt = DeliveryReceipt(time: "2026-10-09T00:00:00Z", route: .steer, runId: "run-2")
        let notice = Notice(id: "n1", childId: "child-1", reportRevision: 1, kind: .endedWithoutReport, lane: .delivered, receipt: receipt)
        let followUp = FollowUp(id: "f1", childId: "child-1", text: "Also update the tests.", lane: .held)
        let merge = MergeRecord(childId: "child-1", kind: .cardMergeCommit, parentBranch: "main", preMergeCommit: "abc123", mergedCommit: "fed789", childHead: "def456")
        var child = record(state: .merged); child.followUpCount = 2; child.reportRevision = 3; child.reportHead = "def456"

        let noticeJSON = String(decoding: try JSONEncoder().encode(notice), as: UTF8.self)
        #expect(noticeJSON.contains("\"ended_without_report\"")); #expect(noticeJSON.contains("\"steer\""))
        #expect(String(decoding: try JSONEncoder().encode(merge), as: UTF8.self).contains("\"card_merge_commit\""))
        #expect(try JSONDecoder().decode(Notice.self, from: JSONEncoder().encode(notice)) == notice)
        #expect(try JSONDecoder().decode(FollowUp.self, from: JSONEncoder().encode(followUp)) == followUp)
        #expect(try JSONDecoder().decode(MergeRecord.self, from: JSONEncoder().encode(merge)) == merge)
        #expect(try JSONDecoder().decode(ChildRecord.self, from: JSONEncoder().encode(child)) == child)
        #expect(followUp.receipt == nil); #expect(notice.receipt?.runId == "run-2")
    }

    @Test func theRequestKeyComesFromTheParentSessionRunTaskAndModeAndNothingElse() {
        let key = DelegationRequestKey.make(parentSessionId: "parent-1", parentRunId: "run-1", task: "Fix the login bug", startingMode: "acceptEdits")
        // Golden values pin the encoding: each part as an 8-byte big-endian UTF-8 byte count and its bytes, then SHA-256.
        #expect(key == "0767d17eeeaaa20c3f5c0e3a11dc260bd005cc116a76ea02adb2c78fb3a48ef7")
        #expect(DelegationRequestKey.make(parentSessionId: "parent-1", parentRunId: "run-1", task: "Tidy the café menu → done", startingMode: "plan")
                == "7f4b512df24f7e3bc398f2a2ca4f9ac5ce328ce54ec824429a2c55bc3d49e976")
        // A repeat within the same parent run gets the same key.
        #expect(DelegationRequestKey.make(parentSessionId: "parent-1", parentRunId: "run-1", task: "Fix the login bug", startingMode: "acceptEdits") == key)
        // Each part changes it.
        let variants = [
            DelegationRequestKey.make(parentSessionId: "parent-2", parentRunId: "run-1", task: "Fix the login bug", startingMode: "acceptEdits"),
            DelegationRequestKey.make(parentSessionId: "parent-1", parentRunId: "run-2", task: "Fix the login bug", startingMode: "acceptEdits"),
            DelegationRequestKey.make(parentSessionId: "parent-1", parentRunId: "run-1", task: "Fix the login bug.", startingMode: "acceptEdits"),
            DelegationRequestKey.make(parentSessionId: "parent-1", parentRunId: "run-1", task: "Fix the login bug", startingMode: "plan"),
        ]
        #expect(Set(variants + [key]).count == 5)
        // Moving text between parts never collides.
        #expect(DelegationRequestKey.make(parentSessionId: "ab", parentRunId: "c", task: "t", startingMode: "plan")
                != DelegationRequestKey.make(parentSessionId: "a", parentRunId: "bc", task: "t", startingMode: "plan"))
        #expect(DelegationRequestKey.make(parentSessionId: "p", parentRunId: "r", task: "do it plan", startingMode: "")
                != DelegationRequestKey.make(parentSessionId: "p", parentRunId: "r", task: "do it", startingMode: " plan"))
        #expect(key.count == 64); #expect(key.allSatisfy { $0.isHexDigit && !$0.isUppercase })
    }
}
