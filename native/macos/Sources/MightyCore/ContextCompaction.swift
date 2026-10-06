import Foundation

/// A CLI summarizing its own context mid-turn. Claude Code reports it as a
/// `system` frame with `subtype: compact_boundary` and token counts; Codex as
/// a `context_compaction` item without counts. Gemini's headless output has
/// no such event, so Gemini panes never get this block.
public enum ContextCompaction {
    public static var title: String { L("graph.block.compact") }

    /// One line for the block and the log: "Auto summary · 84,120 → 21,300 tokens · 12s".
    public static func claudeSummary(_ metadata: Any?) -> String {
        let fields = metadata as? [String: Any] ?? [:]
        let trigger = fields["trigger"] as? String == "manual" ? L("graph.compaction.manual") : L("graph.compaction.auto")
        var parts = [trigger]
        let pre = tokens(fields["pre_tokens"]), post = tokens(fields["post_tokens"])
        switch (pre, post) {
        case let (pre?, post?): parts.append(L("graph.compaction.tokenRange", ["before": format(pre), "after": format(post)]))
        case let (pre?, nil): parts.append(L("graph.compaction.tokenFrom", ["before": format(pre)]))
        default: break
        }
        if let summarized = tokens(fields["messages_summarized"]), summarized > 0 { parts.append(L("graph.compaction.messageCount", ["count": format(summarized)])) }
        if let milliseconds = tokens(fields["duration_ms"]), milliseconds > 0 { parts.append(duration(milliseconds)) }
        return parts.joined(separator: " · ")
    }

    public static var codexSummary: String { L("graph.compaction.codex") }

    static func tokens(_ value: Any?) -> Int? {
        if let value = value as? Int { return value >= 0 ? value : nil }
        if let value = value as? Double, value.isFinite, value >= 0, value <= Double(Int32.max) { return Int(value) }
        return nil
    }
    static func format(_ value: Int) -> String {
        let formatter = NumberFormatter(); formatter.numberStyle = .decimal; formatter.locale = Locale(identifier: "en_US")
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }
    static func duration(_ milliseconds: Int) -> String {
        milliseconds < 1000 ? "\(milliseconds)ms" : L("graph.compaction.durationSeconds", ["n": String(format: "%.1f", Double(milliseconds) / 1000).replacingOccurrences(of: ".0", with: "")])
    }
}
