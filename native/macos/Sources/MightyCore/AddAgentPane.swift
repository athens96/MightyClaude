import Foundation

/// What picking an agent in the "창 추가" menu does. An agent whose earlier sessions
/// the app can continue (`ResumableSessions.providers`) first asks "새로 시작" or
/// "이어가기", unless this workspace folder has none of its sessions to continue;
/// any other agent adds a new pane at once.
public enum AddAgentPane {
    public enum Step: Sendable, Equatable {
        /// Add a new pane now.
        case startNew
        /// Ask whether to start a new session or continue one of this agent's.
        case askResumeOrNew
    }

    public static func offersResume(_ provider: String) -> Bool {
        ResumableSessions.providers.contains(provider)
    }

    /// The look-up run before asking, with the picker's default rules (automated runs
    /// hidden, sessions open panes use left out): one listed session is enough, and
    /// only the head of a record is read.
    public static func probe(_ query: ResumableSessionQuery) -> ResumableSessionQuery {
        var probe = query
        probe.includeAutomated = false
        probe.maximumSessions = 1
        probe.headOnly = true
        return probe
    }

    /// How long the look-up runs before "창 추가" shows its sheet in a checking state,
    /// so a slow folder never leaves the click without an answer.
    public static let quietLookUp: Duration = .milliseconds(300)

    /// `sessions`: what the look-up listed. `inUse`: session ids open panes continue,
    /// checked again because a pane may have taken one while the look-up ran.
    public static func step(provider: String, sessions: [ResumableSession], inUse: Set<String>) -> Step {
        guard offersResume(provider) else { return .startNew }
        let used = Set(inUse.map { $0.lowercased() })
        return sessions.contains { $0.provider == provider && !used.contains($0.sessionID.lowercased()) } ? .askResumeOrNew : .startNew
    }
}
