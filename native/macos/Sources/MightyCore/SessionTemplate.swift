import Foundation

/// A new AI pane starts from the most recently used pane of the same kind and
/// provider, so a preferred model, effort, permission mode and view mode do not
/// have to be re-selected each time. Conversation identity never carries over.
extension RunSession {
    /// When the pane was last used: its newest log entry, else its creation.
    public var lastUsedAt: Date {
        let newest = logs.reversed().lazy.compactMap { AgentRunTiming.parseTimestamp($0.timestamp) }.first
        return newest ?? AgentRunTiming.parseTimestamp(createdAt) ?? .distantPast
    }

    /// The pane whose settings a new `kind`/`provider` pane should inherit.
    /// Shell panes carry no model or run settings, so they never act as one.
    public static func template(kind: String, provider: String, in sessions: [RunSession], excluding excludedId: String? = nil) -> RunSession? {
        guard kind != "shell" else { return nil }
        let provider = ProviderOptions.normalizeProvider(provider)
        return sessions
            .filter { $0.id != excludedId && $0.kind == kind && ProviderOptions.normalizeProvider($0.provider) == provider }
            .max { lhs, rhs in
                let l = lhs.lastUsedAt, r = rhs.lastUsedAt
                return l == r ? lhs.createdAt < rhs.createdAt : l < r
            }
    }

    /// Copy the user's choices only: model, run settings and basic/Mighty view.
    /// Logs, resume identity, usage, timing, graph history and title stay fresh.
    public mutating func inheritSettings(from template: RunSession) {
        model = template.model
        settings = template.settings
        agentViewMode = template.agentViewMode
    }
}
