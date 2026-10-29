import Foundation

/// What 바로 실행 on a queued row does (docs/superpowers/specs/2026-10-29-queue-run-now-design.md; Windows Core
/// `QueueRunNow`). Checked against native/contracts/fixtures/queue-run-now.json.
public enum QueueRunNow: String, Sendable, CaseIterable {
    /// A new child pane of the pane's own agent, in its own worktree beside it, runs the row now; a right-click picks another agent.
    case worktree
    /// The pane's own agent cannot have a child in its mode, another agent can: a click opens the agent menu.
    case agentMenu
    /// The row starts now as the pane's next run, the items held for the pane ahead of it.
    case here
    /// The row goes into the pane's run that only waits on background work, as a new turn (중간 요청).
    case steer
    /// The first held row: every item held for the pane goes to it now, together.
    case held
    /// Disabled: a turn runs and neither a new worktree nor a steer can take the row (or the held items, while a notice turn runs); it runs in its turn.
    case waitsForTurn
    /// Disabled: the row could go into the run that only waits on background work, but items held for the pane go first.
    case waitsForHeld
    /// Disabled: the pane is held for a CLI update.
    case waitsForUpdate
    /// No button: a held row after the first (the first one's button sends them all).
    case hidden

    /// The pane as 바로 실행 reads it.
    public struct Pane: Sendable, Equatable {
        /// Running, starting, held for a CLI update or plugin change, or a child whose last run's end is still being recorded.
        public var busy: Bool
        public var heldForUpdate: Bool
        /// `ComposerSecondSend.choose(…)`: `.worktree` where a new child of the pane's agent can be made now.
        public var secondSend: ComposerSecondSend
        /// `ComposerSecondSend.menuOnly(…)`.
        public var menuOnly: Bool
        /// A local Claude pane, not stopping, whose turn ended and whose background work still runs.
        public var waitsOnBackgroundOnly: Bool
        /// Items delegation holds for the pane.
        public var held: Int
        /// Claude is answering finished background work (`MightyGraphSupport.noticeTurnRunning`): nothing steers into that
        /// turn, neither a human row nor the held items.
        public var noticeTurnRunning: Bool
        public init(busy: Bool, heldForUpdate: Bool, secondSend: ComposerSecondSend, menuOnly: Bool, waitsOnBackgroundOnly: Bool, held: Int,
                    noticeTurnRunning: Bool = false) {
            self.busy = busy; self.heldForUpdate = heldForUpdate; self.secondSend = secondSend; self.menuOnly = menuOnly
            self.waitsOnBackgroundOnly = waitsOnBackgroundOnly; self.held = held; self.noticeTurnRunning = noticeTurnRunning
        }
    }

    /// Nothing a person sends joins a running turn, so a busy pane that can make no worktree and is not only waiting on
    /// background work can only say why the row waits. Held items go ahead of the pane's next request, so a human row is
    /// never steered past them. `heldIndex`: the row's place among the held rows, nil for a human row. `continuation`: the
    /// row continues an Antigravity permission card (``QueuedInput/isContinuation``); its allow rules and card belong to
    /// this pane's next run, so it never goes to a worktree child nor into a running turn: here when idle, else it waits.
    /// A busy pane takes its held items only as a steer, which a running notice turn refuses: then they wait for that turn.
    public static func decide(heldIndex: Int?, text: String, attachments: Int, continuation: Bool = false, pane: Pane) -> QueueRunNow {
        if let heldIndex {
            guard heldIndex == 0 else { return .hidden }
            return pane.busy && pane.noticeTurnRunning ? .waitsForTurn : .held
        }
        if !pane.busy, !pane.heldForUpdate { return .here }
        if pane.heldForUpdate { return .waitsForUpdate }
        if continuation { return .waitsForTurn }
        // A new worktree needs written words for its task; files alone wait.
        let words = !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if words, pane.secondSend == .worktree { return .worktree }
        if words, pane.menuOnly { return .agentMenu }
        if pane.waitsOnBackgroundOnly, !pane.noticeTurnRunning, attachments == 0 { return pane.held == 0 ? .steer : .waitsForHeld }
        return .waitsForTurn
    }

    /// Whether the button does something (the rest are drawn disabled, or not at all).
    public var runs: Bool { [.worktree, .agentMenu, .here, .steer, .held].contains(self) }

    /// The phone's `runNow` and `runNowReason` on a queued row. The reason is a word: the phone words it in its own
    /// language. A phone cannot pick an agent, so `agentMenu` is unavailable there. nil: no button.
    public var wire: (runNow: String, reason: String?)? {
        switch self {
        case .worktree: return ("worktree", nil)
        case .here, .steer, .held: return ("here", nil)
        case .waitsForTurn: return ("unavailable", "turn")
        case .waitsForHeld: return ("unavailable", "held")
        case .waitsForUpdate: return ("unavailable", "update")
        case .agentMenu: return ("unavailable", "agent")
        case .hidden: return nil
        }
    }
}

public extension QueuedInput {
    /// The follow-up of an allowed Antigravity card (`antigravityGrant`, `antigravityCard`): it must start as this pane's
    /// own next run, so 바로 실행 never sends it to a worktree child or steers it (``QueueRunNow/decide(heldIndex:text:attachments:continuation:pane:)``).
    var isContinuation: Bool { antigravityGrant != nil || antigravityCard != nil }
}
