import Foundation

/// Secrets in text about to be committed: well-known token shapes and
/// private-key blocks, plus files whose names look like secrets
/// (`ChildAutoCommit.looksSecret`). A finding names the file, the line and
/// the kind, never the value.
public enum SecretScan {
    public struct Finding: Sendable, Equatable, Identifiable {
        public var id: String { "\(path):\(line):\(kind)" }
        public let path: String
        /// 0 for a finding about the file's name.
        public let line: Int
        public let kind: String
    }

    static let patterns: [(kind: String, regex: String)] = [
        ("AWS access key", "\\b(AKIA|ASIA)[0-9A-Z]{16}\\b"),
        ("GitHub token", "\\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\\b|\\bgithub_pat_[A-Za-z0-9_]{60,}\\b"),
        ("GitLab token", "\\bglpat-[A-Za-z0-9_-]{20,}\\b"),
        ("Slack token", "\\bxox[abprs]-[A-Za-z0-9-]{10,}\\b"),
        ("Anthropic key", "\\bsk-ant-[A-Za-z0-9_-]{20,}\\b"),
        ("OpenAI key", "\\bsk-(proj-)?[A-Za-z0-9_-]{32,}\\b"),
        ("Google API key", "\\bAIza[0-9A-Za-z_-]{35}\\b"),
        ("Stripe key", "\\b(sk|rk)_live_[0-9A-Za-z]{20,}\\b"),
        ("Private key", "-----BEGIN ([A-Z]+ )?PRIVATE KEY-----"),
    ]
    private static let compiled: [(String, NSRegularExpression)] = patterns.compactMap { entry in
        (try? NSRegularExpression(pattern: entry.regex)).map { (entry.kind, $0) }
    }

    /// Findings in `text` (the lines being added to `path`), one per line and kind.
    public static func scan(_ text: String, path: String, firstLine: Int = 1) -> [Finding] {
        var findings: [Finding] = []
        for (offset, line) in text.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
            let value = String(line)
            let range = NSRange(value.startIndex..., in: value)
            for (kind, regex) in compiled where regex.firstMatch(in: value, range: range) != nil {
                findings.append(Finding(path: path, line: firstLine + offset, kind: kind))
            }
        }
        return findings
    }

    /// Findings in the lines a unified diff adds, per file (`+++ b/<path>` heads, `@@ … +n,m @@` hunks).
    public static func scanDiff(_ diff: String) -> [Finding] {
        var findings: [Finding] = []
        var path = "", line = 0
        for raw in diff.split(separator: "\n", omittingEmptySubsequences: false) {
            let text = String(raw)
            if text.hasPrefix("+++ ") {
                path = text.hasPrefix("+++ b/") ? String(text.dropFirst(6)) : String(text.dropFirst(4))
            } else if text.hasPrefix("@@"), let plus = text.range(of: "\\+(\\d+)", options: .regularExpression) {
                line = Int(text[plus].dropFirst()) ?? 0
            } else if text.hasPrefix("+") {
                findings += scan(String(text.dropFirst()), path: path, firstLine: line); line += 1
            } else if !text.hasPrefix("-") && !text.hasPrefix("\\") {
                line += 1
            }
        }
        return findings
    }

    /// Findings for files whose names look like secrets.
    public static func scanNames(_ paths: [String]) -> [Finding] {
        paths.filter(ChildAutoCommit.looksSecret).map { Finding(path: $0, line: 0, kind: "secret file") }
    }
}

/// 이 요청이 한 일: what one request's tools did, one row each — commands
/// run, files written, pages fetched, files read — from its graph's entries.
public enum RunAudit {
    public enum Kind: String, Sendable, CaseIterable { case command, write, web, read, other }

    public struct Row: Sendable, Equatable, Identifiable {
        public let id: String
        public let kind: Kind
        public let tool: String
        public let summary: String
        public let state: String
        public let agent: String?
    }

    public static func kind(tool: String) -> Kind {
        switch tool {
        case "Bash", "BashOutput", "KillShell", "shell", "exec_command", "local_shell", "run_shell_command", "run_in_terminal": return .command
        case "Edit", "Write", "MultiEdit", "NotebookEdit", "apply_patch", "write_file", "replace", "edit_file": return .write
        case "WebFetch", "WebSearch", "web_search", "web_fetch", "google_web_search", "open_url": return .web
        case "Read", "Glob", "Grep", "LS", "read_file", "list_directory", "glob", "search_file_content", "read_many_files": return .read
        default: return tool.lowercased().contains("bash") || tool.lowercased().contains("shell") ? .command : .other
        }
    }

    /// The rows of `run`: its own tool activities, then each sub-agent's, each activity once (its last state).
    public static func rows(_ run: MightyGraphRun) -> [Row] {
        var order: [String] = [], byID: [String: Row] = [:]
        func add(_ entries: [LogEntry], agent: String?) {
            for entry in entries {
                guard let activity = entry.activity, let tool = activity.toolName, !tool.isEmpty else { continue }
                if byID[activity.id] == nil { order.append(activity.id) }
                byID[activity.id] = Row(id: activity.id, kind: kind(tool: tool), tool: tool, summary: activity.summary, state: activity.state, agent: agent)
            }
        }
        add(run.rootEntries, agent: nil)
        for agent in run.agents { add(agent.entries, agent: agent.title) }
        add(run.resultEntries, agent: nil)
        return order.compactMap { byID[$0] }
    }

    /// The rows as plain text, one line each, grouped by kind.
    public static func text(_ rows: [Row], title: String, labels: [Kind: String]) -> String {
        var lines = [title]
        for kind in Kind.allCases {
            let group = rows.filter { $0.kind == kind }
            guard !group.isEmpty else { continue }
            lines.append("")
            lines.append("## " + (labels[kind] ?? kind.rawValue) + " (\(group.count))")
            lines += group.map { "- [\($0.tool)] \($0.summary)" + ($0.state == "error" || $0.state == "failed" ? " ✗" : "") + ($0.agent.map { " · \($0)" } ?? "") }
        }
        return lines.joined(separator: "\n")
    }
}

/// The summary a 진단 정보 export writes: versions and counts, no prompts,
/// no output and no paths (the home folder becomes `~`, workspace paths are not included).
public enum DiagnosticsSummary {
    public static func redact(_ text: String, home: String = FileManager.default.homeDirectoryForCurrentUser.path) -> String {
        guard !home.isEmpty, home != "/" else { return text }
        return text.replacingOccurrences(of: home, with: "~")
    }
}
