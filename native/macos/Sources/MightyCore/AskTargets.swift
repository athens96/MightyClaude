import Foundation

/// Composer "send to" targets: another CLI asked through oh-my-claudecode's ask skill
/// (native/contracts/fixtures/ask-targets.json).
public struct AskTarget: Equatable, Sendable {
    public let id: String
    public let label: String
    public let executable: String
}

public enum AskTargets {
    public static let skill = "oh-my-claudecode:ask"
    public static let all: [AskTarget] = [
        AskTarget(id: "codex", label: "Codex", executable: "codex"),
        AskTarget(id: "antigravity", label: "Antigravity", executable: "agy"),
        AskTarget(id: "gemini", label: "Gemini CLI", executable: "gemini"),
        AskTarget(id: "grok", label: "Grok", executable: "grok"),
        AskTarget(id: "cursor", label: "Cursor", executable: "cursor-agent"),
    ]

    /// Targets shown for a pane: empty unless Claude agent pane + skill present.
    public static func available(isClaudeAgent: Bool, slashInvocations: Set<String>, installed: Set<String>) -> [AskTarget] {
        guard isClaudeAgent, slashInvocations.contains(skill) else { return [] }
        return all.filter { installed.contains($0.executable) }
    }

    /// The target a send uses: nil (Claude) when the saved id is not in `available`.
    public static func effective(saved: String?, available: [AskTarget]) -> AskTarget? {
        guard let saved else { return nil }
        return available.first { $0.id == saved }
    }

    /// What is sent: nil when nothing should be sent.
    public static func wrap(_ text: String, target: AskTarget?) -> String? {
        guard let target else { return text }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return nil }
        if trimmed.hasPrefix("/") { return text }
        return "/\(skill) \(target.id) \(trimmed)"
    }
}
