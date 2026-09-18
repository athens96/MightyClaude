import Foundation

/// A CLI summarizing its own context mid-turn. Claude Code reports it as a
/// `system` frame with `subtype: compact_boundary` and token counts; Codex as
/// a `context_compaction` item without counts. Gemini's headless output has
/// no such event, so Gemini panes never get this block.
public enum ContextCompaction {
    public static let title = "컨텍스트 정리"

    /// One line for the block and the log: "자동 정리 · 84,120 → 21,300 토큰 · 12초".
    public static func claudeSummary(_ metadata: Any?) -> String {
        let fields = metadata as? [String: Any] ?? [:]
        let trigger = fields["trigger"] as? String == "manual" ? "수동 정리" : "자동 정리"
        var parts = [trigger]
        let pre = tokens(fields["pre_tokens"]), post = tokens(fields["post_tokens"])
        switch (pre, post) {
        case let (pre?, post?): parts.append("\(format(pre)) → \(format(post)) 토큰")
        case let (pre?, nil): parts.append("\(format(pre)) 토큰에서 요약")
        default: break
        }
        if let summarized = tokens(fields["messages_summarized"]), summarized > 0 { parts.append("메시지 \(format(summarized))개 요약") }
        if let milliseconds = tokens(fields["duration_ms"]), milliseconds > 0 { parts.append(duration(milliseconds)) }
        return parts.joined(separator: " · ")
    }

    public static let codexSummary = "Codex가 대화 맥락을 요약했습니다."

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
        milliseconds < 1000 ? "\(milliseconds)ms" : String(format: "%.1f초", Double(milliseconds) / 1000).replacingOccurrences(of: ".0초", with: "초")
    }
}
