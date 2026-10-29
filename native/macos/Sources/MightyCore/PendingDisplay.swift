import Foundation

/// Where one user action is (natural submit A1): nothing asked yet, waiting for its answer,
/// answered, or failed with the message the block shows in place. `since` is when the
/// action began, `at` when it ended. A failure without `at` reads as failing now
/// (fixture `pending.json` `displayRule`). Windows Core, the Windows page and the phone
/// carry the same four states; `PendingDisplay` decides what each shows.
public enum PendingState: Equatable, Sendable {
    case idle
    case pending(since: Date)
    case done(since: Date, at: Date)
    case failed(_ message: String, since: Date, at: Date?)
}

/// What the pending area shows now: nothing, the shared loading, or the failure's message.
public enum PendingIndicator: Equatable, Sendable {
    case hidden
    case loading
    case failed(String)
}

/// The pending display rule, a pure function of the action's start, its end and now, shared
/// with Windows Core, the Windows page and the phone through
/// native/contracts/fixtures/pending.json (`display`; scripts/check-design-tokens.js holds
/// the same rule as `pendingVisible`). Loading shows only once the action has lasted
/// `pendingShowAfter`; an action over by then never shows it; once shown it stays while the
/// action is pending and until `pendingShowAfter + pendingMinVisible` (inclusive), then hides
/// at once. A failure follows that rule until it ends, then shows its message at once with no
/// hold and keeps it. Reduce Motion changes only the movement, never this timing.
public enum PendingDisplay {
    /// The rule in milliseconds on one clock. `endedAt` nil: still pending, or for a failure
    /// a failure now.
    public static func visible(startedAt: Double, endedAt: Double?, now: Double, failed: Bool = false,
                               showAfter: Int = DesignMetrics.Motion.pendingShowAfter,
                               minVisible: Int = DesignMetrics.Motion.pendingMinVisible) -> Bool {
        let shownAt = startedAt + Double(showAfter)
        if failed { return now >= (endedAt ?? now) || now >= shownAt }
        if now < shownAt { return false }
        guard let endedAt else { return true }
        if endedAt <= shownAt { return false }
        return now < endedAt || now <= shownAt + Double(minVisible)
    }

    /// Whether `state` shows its pending area at `now`.
    public static func visible(_ state: PendingState, now: Date,
                               showAfter: Int = DesignMetrics.Motion.pendingShowAfter,
                               minVisible: Int = DesignMetrics.Motion.pendingMinVisible) -> Bool {
        indicator(state, now: now, showAfter: showAfter, minVisible: minVisible) != .hidden
    }

    /// What `state` shows at `now`: a failure shows loading until it ends (if the rule shows
    /// loading by then) and its message from then on.
    public static func indicator(_ state: PendingState, now: Date,
                                 showAfter: Int = DesignMetrics.Motion.pendingShowAfter,
                                 minVisible: Int = DesignMetrics.Motion.pendingMinVisible) -> PendingIndicator {
        switch state {
        case .idle:
            return .hidden
        case .pending(let since):
            return visible(startedAt: 0, endedAt: nil, now: milliseconds(now, since: since), showAfter: showAfter, minVisible: minVisible) ? .loading : .hidden
        case .done(let since, let at):
            return visible(startedAt: 0, endedAt: milliseconds(at, since: since), now: milliseconds(now, since: since),
                           showAfter: showAfter, minVisible: minVisible) ? .loading : .hidden
        case .failed(let message, let since, let at):
            let elapsed = milliseconds(now, since: since)
            let ended = at.map { milliseconds($0, since: since) } ?? elapsed
            if elapsed >= ended { return .failed(message) }
            return visible(startedAt: 0, endedAt: ended, now: elapsed, failed: true, showAfter: showAfter, minVisible: minVisible) ? .loading : .hidden
        }
    }

    /// `date` after `since` in milliseconds, kept to whole microseconds so a boundary the
    /// fixture names in milliseconds is not lost to floating point.
    static func milliseconds(_ date: Date, since: Date) -> Double {
        (date.timeIntervalSince(since) * 1_000_000).rounded() / 1000
    }
}

/// A host's answer to a submit (natural submit B5): the Mac and Windows hosts'
/// `/m1/sessions/{id}/submit` and the Windows page's `send` bridge, old and new
/// (fixture `pending.json` `submitResponses`, rule `submitRule`).
public struct SubmitResponse: Equatable, Sendable {
    /// Whether the host took the request.
    public var accepted: Bool
    /// The request's user entry, so an optimistic block is matched to it; nil from an old
    /// host or for a queued request, which has no block yet.
    public var entryId: String?
    /// The graph run the request started or went into.
    public var runId: String?
    /// What happened: started, queued or steered (`dropped` is never an answer).
    public var outcome: SubmitOutcome?

    public init(accepted: Bool, entryId: String? = nil, runId: String? = nil, outcome: SubmitOutcome? = nil) {
        self.accepted = accepted; self.entryId = entryId; self.runId = runId; self.outcome = outcome
    }

    /// Reads a decoded JSON body (`JSONSerialization`), nil or not an object reading as not
    /// accepted. `outcome` is the body's `outcome` when it is started, queued or steered (any
    /// other word is ignored), else its `accepted` when that is one of those words (old
    /// hosts); accepted when there is an outcome or `accepted` is the boolean true. Ids count
    /// only as non-empty strings; everything else in the body is ignored. Kept for contract
    /// parity (pending.json `submitResponses`): no Mac code calls it, the Mac only answers.
    public static func parse(_ json: Any?) -> SubmitResponse {
        guard let body = json as? [String: Any] else { return SubmitResponse(accepted: false) }
        let outcome = word(body["outcome"]) ?? word(body["accepted"])
        let acceptedTrue = isBoolean(body["accepted"]) && body["accepted"] as? Bool == true
        return SubmitResponse(accepted: outcome != nil || acceptedTrue, entryId: id(body["entryId"]), runId: id(body["runId"]), outcome: outcome)
    }

    /// Reads a raw body; one that is not JSON reads as not accepted.
    public static func parse(data: Data) -> SubmitResponse {
        parse(try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]))
    }

    private static func word(_ value: Any?) -> SubmitOutcome? {
        guard let text = value as? String, let outcome = SubmitOutcome(rawValue: text), outcome != .dropped else { return nil }
        return outcome
    }

    private static func id(_ value: Any?) -> String? {
        guard let text = value as? String, !text.isEmpty else { return nil }
        return text
    }

    /// `JSONSerialization` hands numbers and booleans back as `NSNumber`, and `1 as? Bool` is
    /// true; only a real JSON `true` counts.
    private static func isBoolean(_ value: Any?) -> Bool {
        guard let number = value as? NSNumber else { return value is Bool }
        return CFGetTypeID(number) == CFBooleanGetTypeID()
    }
}

/// Where a view moves when a request block appears (natural submit B2; fixture
/// `pending.json` `scrollTargets`, rule `scrollRule`). The caller decides `nearBottom`
/// with its own threshold (the Mac's 36 pt from the bottom; in 흐름 the follow camera being
/// on; in 기록 and 파이프라인 being at the newest end).
public enum SubmitScroll {
    /// The four views of an agent pane: the default transcript, 흐름, 기록 and 파이프라인.
    public enum View: String, Sendable, CaseIterable {
        case basic, diagram, timeline, pipeline

        /// The view a pane shows now. `showsMighty` is whether it draws the Mighty view at all
        /// (the caller's own rule, e.g. `SessionPaneView.showsMightyGraph`: `agentViewMode`
        /// "mighty" on a provider that has it); false is the default transcript, whatever
        /// `mightyViewMode` says. `mightyViewMode` is `RunSession.mightyViewMode` (a saved nil
        /// `graphViewMode` already reads as the diagram there).
        public init(showsMighty: Bool, mightyViewMode: MightyGraphViewMode) {
            guard showsMighty else { self = .basic; return }
            switch mightyViewMode {
            case .diagram: self = .diagram
            case .timeline: self = .timeline
            case .structure: self = .pipeline
            }
        }
    }

    public enum Target: String, Sendable {
        /// Move to the new request block, whatever the position.
        case toRequest
        /// Today's follow: keep the newest in view.
        case follow
        /// Stay where the user is reading.
        case none
    }

    public struct Decision: Equatable, Sendable {
        public var scroll: Target
        /// The 파이프라인 goes back to the newest board first.
        public var resetBoard: Bool
        public init(scroll: Target, resetBoard: Bool) { self.scroll = scroll; self.resetBoard = resetBoard }
    }

    /// `own`: this composer's Enter, a queued request that now runs, 바로 실행 or a selected
    /// ⌘Enter child. An own request always moves to its block, back to the newest 파이프라인
    /// board when an older one is chosen. Anything else follows only near the bottom, and
    /// never on an older 파이프라인 board, which the user chose (its badge says there is more).
    /// `chosenBoardIsNewest` matters only in the 파이프라인.
    public static func target(view: View, own: Bool, chosenBoardIsNewest: Bool, nearBottom: Bool) -> Decision {
        let olderBoard = view == .pipeline && !chosenBoardIsNewest
        if own { return Decision(scroll: .toRequest, resetBoard: olderBoard) }
        if olderBoard { return Decision(scroll: .none, resetBoard: false) }
        return Decision(scroll: nearBottom ? .follow : .none, resetBoard: false)
    }
}
