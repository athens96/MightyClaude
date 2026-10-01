import Foundation

/// The figures under an agent pane's hero (concept D): how long the latest request has
/// taken, how full the context is, what the session has cost and how many tool calls
/// the latest request made. Read only from what the pane already holds; a figure the
/// app has no number for is left out rather than shown as zero. Mirrors the phone's
/// `SessionHeader` figures.
public enum PaneHero {
    public enum Figure: Equatable, Sendable {
        /// `RunSession.runTiming`, when valid. The view ticks it only while it runs.
        case elapsed(AgentRunTiming)
        /// `SessionUsage.contextPercent`, rounded, from a reading of the pane's own provider.
        case context(Int)
        /// `SessionUsage.costUSD`, from a reading of the pane's own provider.
        case cost(Double)
        /// Tool calls since the latest user request (`toolCount`).
        case tools(Int)
    }

    /// In order: elapsed, context, cost, tools — each only when the pane has it.
    public static func figures(_ session: RunSession) -> [Figure] {
        var figures: [Figure] = []
        if let timing = session.runTiming, timing.isValid { figures.append(.elapsed(timing)) }
        // A reading from another provider (the pane was switched) does not describe this one.
        let usage = session.sessionUsage?.provider == session.provider ? session.sessionUsage : nil
        if let percent = usage?.contextPercent, percent.isFinite { figures.append(.context(Int(min(100, max(0, percent)).rounded()))) }
        if let cost = usage?.costUSD, cost.isFinite, cost >= 0 { figures.append(.cost(cost)) }
        if let tools = toolCount(session.logs) { figures.append(.tools(tools)) }
        return figures
    }

    /// The tool rows after the latest user request: log entries that carry an activity
    /// other than the turn itself. A tool row is updated in place by its id, so each
    /// call counts once whatever state it reached. nil before the first request.
    public static func toolCount(_ logs: [LogEntry]) -> Int? {
        guard let start = logs.lastIndex(where: { $0.kind == "user" }) else { return nil }
        return logs[(start + 1)...].reduce(0) { count, entry in
            guard let activity = entry.activity, activity.kind != "turn" else { return count }
            return count + 1
        }
    }

    /// `$0.38`, as on the phone: two decimals, never a currency symbol from the locale.
    public static func cost(_ value: Double) -> String {
        "$" + String(format: "%.2f", value)
    }
}
