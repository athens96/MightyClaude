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

    /// Whether a saved target is gone and the pane falls back to Claude (with its
    /// one notice). Only judged once the PATH was read conclusively (`probed`) and
    /// the pane's slash catalogue scanned, so nothing unknown yet counts as gone.
    public static func fellBack(saved: String?, available: [AskTarget], probed: Bool, scanned: Bool) -> Bool {
        guard saved != nil, probed, scanned else { return false }
        return effective(saved: saved, available: available) == nil
    }

    /// The installed set after a PATH read, and whether the read is conclusive
    /// enough to judge a target gone. A PATH that is not the login shell's only
    /// adds what it found; a read finding nothing after one that found some is a
    /// hiccup, not every CLI uninstalled at once.
    public static func probed(previous: Set<String>, found: Set<String>, loginShell: Bool) -> (installed: Set<String>, conclusive: Bool) {
        if !loginShell { return (previous.union(found), false) }
        if found.isEmpty && !previous.isEmpty { return (previous, false) }
        return (found, true)
    }

    /// The question inside a wrapped send (`/oh-my-claudecode:ask codex <text>` → `<text>`),
    /// so a pane's automatic title names the question; any other text unchanged.
    public static func question(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        for target in all {
            let prefix = "/\(skill) \(target.id) "
            if trimmed.hasPrefix(prefix) { return String(trimmed.dropFirst(prefix.count)) }
        }
        return text
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
