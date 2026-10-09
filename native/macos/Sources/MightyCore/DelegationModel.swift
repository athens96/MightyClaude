import CryptoKit
import Foundation

/// What can happen to a delegated child (macOS only). ``ChildState/after(_:)``
/// says which state each event leads to, or refuses it.
public enum ChildStateEvent: String, Codable, Sendable, CaseIterable {
    /// A run starts in the child's pane: its first run, or any new one (a
    /// follow-up or the human's own send).
    case startRun
    /// The child could not be started.
    case failStart
    /// The child's run asks for a permission that only a human can answer.
    case askPermission
    /// That permission request was answered.
    case answerPermission
    /// The run ended with a changed REPORT.md.
    case endWithReport
    /// The run finished, stopped or errored with no new report.
    case endWithoutReport
    /// Quitting the app killed the run.
    case quitApp
    /// A merge took the child's reported head.
    case merge
    /// A human undid that merge from the card.
    case undoMerge
    /// The child's pane closed.
    case closePane
    /// A human discarded the child from its card.
    case discard
}

/// Where a delegated child is. The events allow exactly these moves:
///
/// - creating → running (its first run starts) | failed (the start failed)
/// - running ⇄ waiting (a permission request only a human can answer)
/// - running → reported (the run ended with a changed REPORT.md) | ended (no
///   new report) | interrupted (quitting the app killed it; it comes back stopped)
/// - reported, ended, interrupted, merged → running: any new run in the open,
///   idle pane, which clears reported
/// - reported → merged; merged → reported (undo while the pane is open). Undo
///   on a closed child leaves it closed.
/// - every open state → closed (the pane closed); every state but discarded →
///   discarded (human discard), so failed → discarded too
///
/// Every other event is refused and changes nothing. Discarded is final.
public enum ChildState: String, Codable, Sendable, CaseIterable {
    case creating, running, waiting, reported, ended, interrupted, merged, failed, closed, discarded

    /// Open children count toward the cap: every state except failed, closed
    /// and discarded.
    public var isOpen: Bool { ![.failed, .closed, .discarded].contains(self) }

    /// The state `event` leads to, or nil when it is refused here.
    public func after(_ event: ChildStateEvent) -> ChildState? {
        switch (self, event) {
        case (.creating, .startRun), (.reported, .startRun), (.ended, .startRun), (.interrupted, .startRun), (.merged, .startRun): return .running
        case (.creating, .failStart): return .failed
        case (.running, .askPermission): return .waiting
        case (.waiting, .answerPermission): return .running
        case (.running, .endWithReport): return .reported
        case (.running, .endWithoutReport): return .ended
        case (.running, .quitApp): return .interrupted
        case (.reported, .merge): return .merged
        case (.merged, .undoMerge): return .reported
        case (.closed, .undoMerge): return .closed
        case (_, .closePane) where isOpen: return .closed
        case (_, .discard) where self != .discarded: return .discarded
        default: return nil
        }
    }

    /// Whether some event moves this state to `next`, a different state.
    public func canMove(to next: ChildState) -> Bool { next != self && ChildStateEvent.allCases.contains { after($0) == next } }
}

/// One delegated child pane, as the delegation file records it.
public struct ChildRecord: Codable, Sendable, Equatable, Identifiable {
    /// The child pane's session id.
    public var id: String
    public var parentSessionId: String
    public var worktreePath: String
    /// Always `mighty/<session id>`, flat.
    public var branch: String
    /// The parent's branch and head when the child was made.
    public var parentBranch: String
    public var baseCommit: String
    /// The permission mode the parent asked for in delegate, written once.
    public let startingMode: String
    public var requestKey: String
    public var followUpCount: Int
    /// 0 until the first report; each report revision is one higher.
    public var reportRevision: Int
    public var reportHead: String?
    public var state: ChildState
    /// The top folder of the parent's checkout when the child was made: the
    /// parent's side, where the child's branch lives and where its cleanup
    /// and discard run git, even after the parent pane closed. nil in a
    /// record kept before it was stored.
    public var parentCheckout: String?
    /// The head of the child's branch when a human closed its merged pane,
    /// stored with its REPORT and TASK copies before any cleanup.
    public var closedHead: String?

    public init(id: String, parentSessionId: String, worktreePath: String, parentBranch: String, baseCommit: String, startingMode: String, requestKey: String, followUpCount: Int = 0, reportRevision: Int = 0, reportHead: String? = nil, state: ChildState = .creating, parentCheckout: String? = nil) {
        self.id = id; self.parentSessionId = parentSessionId; self.worktreePath = worktreePath; branch = Self.branchName(for: id)
        self.parentBranch = parentBranch; self.baseCommit = baseCommit; self.startingMode = startingMode; self.requestKey = requestKey
        self.followUpCount = followUpCount; self.reportRevision = reportRevision; self.reportHead = reportHead; self.state = state
        self.parentCheckout = parentCheckout
    }

    public static func branchName(for sessionId: String) -> String { "mighty/" + sessionId }

    /// Moves the child by `event`. A refused event changes nothing and returns false.
    @discardableResult public mutating func apply(_ event: ChildStateEvent) -> Bool {
        guard let next = state.after(event) else { return false }
        state = next; return true
    }

    /// The run ended with a changed REPORT.md at `head`: the child is reported
    /// with the next revision. Refused, changing nothing, unless it was running.
    @discardableResult public mutating func recordReport(head: String) -> Bool {
        guard apply(.endWithReport) else { return false }
        reportRevision += 1; reportHead = head; return true
    }
}

/// Where a notice or follow-up waits: pending (not yet sent), held (sent only
/// by run next or the human's next send) or delivered (exactly once).
public enum DeliveryLane: String, Codable, Sendable, CaseIterable { case pending, held, delivered }

/// How an item reached its pane: steered into a running Claude pane, or
/// queued as the next run.
public enum DeliveryRoute: String, Codable, Sendable, CaseIterable { case steer, queue }

public struct DeliveryReceipt: Codable, Sendable, Equatable {
    /// ISO 8601.
    public var time: String
    public var route: DeliveryRoute
    /// The run that received the item.
    public var runId: String

    public init(time: String, route: DeliveryRoute, runId: String) { self.time = time; self.route = route; self.runId = runId }
}

/// A pointer to one child event for the parent. The report body is read
/// through child_status, never carried here.
public struct Notice: Codable, Sendable, Equatable, Identifiable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        case reported
        case endedWithoutReport = "ended_without_report"
        case failedToStart = "failed_to_start"
    }

    public var id: String
    public var childId: String
    public var reportRevision: Int
    public var kind: Kind
    public var lane: DeliveryLane
    public var receipt: DeliveryReceipt?

    public init(id: String, childId: String, reportRevision: Int, kind: Kind, lane: DeliveryLane = .pending, receipt: DeliveryReceipt? = nil) {
        self.id = id; self.childId = childId; self.reportRevision = reportRevision; self.kind = kind; self.lane = lane; self.receipt = receipt
    }
}

/// A parent's instruction to one of its children.
public struct FollowUp: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var childId: String
    public var text: String
    public var lane: DeliveryLane
    public var receipt: DeliveryReceipt?

    public init(id: String, childId: String, text: String, lane: DeliveryLane = .pending, receipt: DeliveryReceipt? = nil) {
        self.id = id; self.childId = childId; self.text = text; self.lane = lane; self.receipt = receipt
    }
}

/// Written on every merge; undo reads it.
public struct MergeRecord: Codable, Sendable, Equatable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        case toolFastForward = "tool_fast_forward"
        case cardFastForward = "card_fast_forward"
        case cardMergeCommit = "card_merge_commit"
    }

    public var childId: String
    public var kind: Kind
    public var parentBranch: String
    public var preMergeCommit: String
    public var mergedCommit: String
    public var childHead: String

    public init(childId: String, kind: Kind, parentBranch: String, preMergeCommit: String, mergedCommit: String, childHead: String) {
        self.childId = childId; self.kind = kind; self.parentBranch = parentBranch; self.preMergeCommit = preMergeCommit; self.mergedCommit = mergedCommit; self.childHead = childHead
    }
}

/// The idempotency key of one delegate call. The server derives it from the
/// calling pane's session id, that pane's current run id, the task text and
/// the starting mode, with no extra argument, so a repeat within the same
/// parent run gets the same child. Each part is length-prefixed before
/// hashing, so no two different part lists share a key.
public enum DelegationRequestKey {
    public static func make(parentSessionId: String, parentRunId: String, task: String, startingMode: String) -> String {
        var bytes = Data()
        for part in [parentSessionId, parentRunId, task, startingMode] {
            let utf8 = Data(part.utf8)
            withUnsafeBytes(of: UInt64(utf8.count).bigEndian) { bytes.append(contentsOf: $0) }
            bytes.append(utf8)
        }
        return SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
    }
}
