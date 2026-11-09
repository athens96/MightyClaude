import Foundation

/// The fenced shell blocks of an assistant reply that the transcript offers to run
/// (docs/agent-terminal-and-web.md, "Running a shell block from a reply"). Only the
/// user's click runs one, and what runs is the block as shown.
public enum ShellBlocks {
    /// Languages whose whole block is the command. PowerShell blocks run only on Windows.
    public static let shellLanguages: Set<String> = ["sh", "bash", "zsh", "shell"]

    /// The command a fenced block with `language` (its info string) and `code` runs,
    /// or nil when the block gets no Run button. A `console` block runs only its lines
    /// that start with `$ ` or `% `, without that prompt; its other lines are sample output.
    public static func command(language: String?, code: String) -> String? {
        guard let word = language?.split(whereSeparator: \.isWhitespace).first?.lowercased() else { return nil }
        var body = code
        while body.last?.isNewline == true { body.removeLast() }
        let command: String
        if shellLanguages.contains(word) {
            command = body
        } else if word == "console" {
            command = body.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line -> String? in
                ["$ ", "% "].first { line.hasPrefix($0) }.map { String(line.dropFirst($0.count)) }
            }.joined(separator: "\n")
        } else { return nil }
        guard !command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !command.unicodeScalars.contains(where: hidesText) else { return nil }
        return command
    }

    /// Characters that make the text run differ from the text shown (trojan source): bidi
    /// embeddings, overrides and isolates, zero-width and other format characters, and C0
    /// controls other than tab and newline (ESC and CR included) or DEL. A block holding
    /// one gets no Run button, so what runs is always what the user reads.
    static func hidesText(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x09, 0x0A: false
        case 0x00...0x1F, 0x7F: true
        case 0x200B...0x200F, 0x202A...0x202E, 0x2060, 0x2066...0x2069, 0xFEFF: true
        default: false
        }
    }

    /// A runnable block's identity: its entry and its 0-based place among that entry's
    /// runnable blocks, so a reply that grows keeps the keys of the blocks it has.
    public static func key(entryId: String, index: Int) -> String { "\(entryId)#\(index)" }

    /// Whether `markdown` ends inside a fenced code block whose closing fence has not
    /// arrived yet: a reply still streaming that block. Such a block gets no button.
    public static func endsInOpenFence(_ markdown: String) -> Bool {
        var open: (mark: Character, count: Int)?
        for line in markdown.split(separator: "\n", omittingEmptySubsequences: false) {
            let text = line.drop { $0 == " " || $0 == "\t" || $0 == ">" }
            guard let mark = text.first, mark == "`" || mark == "~" else { continue }
            let count = text.prefix { $0 == mark }.count
            guard count >= 3 else { continue }
            if let current = open {
                if mark == current.mark, count >= current.count, text.dropFirst(count).allSatisfy(\.isWhitespace) { open = nil }
            } else if mark == "~" || !text.dropFirst(count).contains("`") {
                open = (mark, count)
            }
        }
        return open != nil
    }
}

/// One run of a reply's shell block, kept with its pane (`RunSession.shellRuns`) so the
/// result is still under the block after a relaunch. Running the block again replaces it.
public struct ShellBlockRun: Codable, Sendable, Equatable {
    public enum State: String, Codable, Sendable {
        /// Still going in the pane's terminal.
        case running
        /// Ended with exit code 0.
        case completed
        /// Ended with another exit code, by a signal, or could not start.
        case failed
        /// The user pressed Stop.
        case stopped
        /// It was still running when the app quit.
        case interrupted
    }

    /// The output kept under the block: its last 16 KB.
    public static let maximumOutputBytes = 16_384
    /// The runs a pane keeps, newest last.
    public static let maximumRuns = 64

    public var key: String
    public var command: String
    public var state: State
    public var exitCode: Int?
    /// The run's terminal output, cleaned like an agent's tool output.
    public var output: String
    public var startedAt: String
    public var endedAt: String?

    public init(key: String, command: String, startedAt: String = mightyTimestamp()) {
        self.key = key; self.command = command; self.state = .running; self.exitCode = nil
        self.output = ""; self.startedAt = startedAt; self.endedAt = nil
    }

    /// Add output the run printed, keeping only the tail.
    public mutating func append(_ text: String) {
        guard !text.isEmpty else { return }
        output = Self.tail(output + text)
    }

    /// The run ended. `stopped` is the user's Stop; otherwise exit code 0 is done and
    /// anything else (another code, a signal, no code at all) failed. A run ended by a
    /// signal shows the shell's 128 + signal. Only a running record changes.
    public mutating func finish(exitCode: Int32?, signal: Int32?, stopped: Bool, at date: String = mightyTimestamp()) {
        guard state == .running else { return }
        self.exitCode = exitCode.map(Int.init) ?? signal.map { 128 + Int($0) }
        state = stopped ? .stopped : signal == nil && exitCode == 0 ? .completed : .failed
        endedAt = date
    }

    /// The last `maximumBytes` bytes of `text`, cut at a character boundary.
    public static func tail(_ text: String, maximumBytes: Int = maximumOutputBytes) -> String {
        guard text.utf8.count > maximumBytes else { return text }
        guard maximumBytes > 0 else { return "" }
        let suffix = Data(text.utf8.suffix(maximumBytes))
        for dropped in 0...min(3, suffix.count) {
            if let value = String(data: suffix.dropFirst(dropped), encoding: .utf8) { return value }
        }
        return ""
    }

    /// The runs as a pane saves or restores them: well-formed ones only, one per key
    /// (the newest), at most `maximumRuns`, a restored running one `interrupted`, each
    /// output cut to its tail and counted against the pane's log `budget`.
    public static func normalized(_ runs: [ShellBlockRun]?, restoring: Bool, budget: inout Int) -> [ShellBlockRun]? {
        guard let runs else { return nil }
        var seen = Set<String>()
        var kept: [ShellBlockRun] = []
        for run in runs.reversed() where !run.key.isEmpty && run.key.utf8.count <= 256 && !run.command.isEmpty
            && run.command.utf8.count <= AgentIOWire.maxCommandBytes && seen.insert(run.key).inserted {
            guard kept.count < maximumRuns, budget > 0 else { break }
            var next = run
            if restoring && next.state == .running { next.state = .interrupted; next.endedAt = next.endedAt ?? mightyTimestamp() }
            next.output = tail(next.output, maximumBytes: min(maximumOutputBytes, max(0, budget)))
            budget -= next.output.utf8.count + next.command.utf8.count
            kept.append(next)
        }
        return kept.isEmpty ? nil : kept.reversed()
    }
}
