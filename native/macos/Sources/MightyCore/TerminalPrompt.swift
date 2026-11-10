import Foundation

/// A command in an agent pane's terminal that waits for the user to type
/// something: a yes/no question, a password, a line of text or a choice from a
/// list. The pane shows it as a card so the user answers there (or on the
/// phone) instead of in the terminal pane. Nothing is ever typed without the
/// user's answer.
public struct TerminalPrompt: Codable, Sendable, Equatable, Identifiable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        /// `(Y/n)`, `[y/N]`, `(yes/no)`.
        case yesNo
        /// A password, passphrase or token: the answer is never shown or kept.
        case secret
        /// A line of text.
        case text
        /// A list moved through with the arrow keys and taken with Enter.
        case choice
    }

    /// The command's handle and how much output it had shown when it stopped
    /// to wait: a new wait after more output is a new prompt.
    public var id: String
    public var handle: String
    /// The question as the terminal shows it, at most ``maximumTextLength`` characters.
    public var text: String
    /// The choices of a ``Kind/choice`` prompt, as their lines show them, the marked one included.
    public var options: [String]
    public var kind: Kind
    /// For ``Kind/yesNo``: true when Enter alone answers yes (`(Y/n)`), false when it answers no (`(y/N)`), nil when neither.
    public var defaultYes: Bool?

    public static let maximumTextLength = 300
    public static let maximumOptions = 12

    public init(id: String, handle: String, text: String, options: [String] = [], kind: Kind, defaultYes: Bool? = nil) {
        self.id = id; self.handle = handle; self.text = text; self.options = options; self.kind = kind; self.defaultYes = defaultYes
    }
}

/// What the user answered a ``TerminalPrompt`` with, and what that types into the terminal.
public enum TerminalPromptAnswer: Sendable, Equatable {
    case yes
    case no
    /// A line of text, or a secret: typed, then Enter.
    case text(String)
    /// One key, for a list or a prompt that waits for Enter alone.
    case key(Key)

    public enum Key: String, Sendable, CaseIterable { case up, down, enter, space }

    /// The bytes the terminal would get had the user typed the answer there.
    /// Enter is a CR, as a terminal sends it; a line holding a CR or LF is cut
    /// at it, so one answer is one line.
    public var input: String {
        switch self {
        case .yes: "y\r"
        case .no: "n\r"
        case .text(let text): String(text.prefix { $0 != "\r" && $0 != "\n" }) + "\r"
        case .key(.up): "\u{1B}[A"
        case .key(.down): "\u{1B}[B"
        case .key(.enter): "\r"
        case .key(.space): " "
        }
    }
}

/// Tells from the end of a command's cleaned output whether it waits for input.
/// It is only asked once the output has been quiet for a moment
/// (``TerminalPromptMonitor``), so a progress line still moving is never a prompt.
public enum TerminalPromptDetector {
    /// The prompt the output ends with, nil when it does not look like one.
    /// `tail` is cleaned text (``AgentTerminalOutputCleaner``); the last line is
    /// the one after the last newline, where the cursor waits.
    public static func detect(_ tail: String) -> (text: String, options: [String], kind: TerminalPrompt.Kind, defaultYes: Bool?)? {
        let lines = tail.components(separatedBy: "\n")
        let last = lines.last.map(trimmedEnd) ?? ""
        // A list prints its choices below the question, so the cursor rests on a choice line.
        if let list = choice(lines) { return list }
        if last.isEmpty { return nil }
        guard last.count <= 400, !last.hasSuffix("...") && !last.hasSuffix("…") else { return nil }
        let text = shown(last)
        if let defaultYes = yesNo(last) { return (text, [], .yesNo, defaultYes) }
        if isSecret(last) { return (text, [], .secret, nil) }
        if let end = last.last, "?:›>：？".contains(end) { return (text, [], .text, nil) }
        return nil
    }

    /// `(Y/n)` → true, `(y/N)` → false, `(y/n)`/`(yes/no)` → nil wrapped, nothing → not yes/no.
    static func yesNo(_ line: String) -> Bool?? {
        guard let match = line.range(of: #"[\(\[]\s*(y(es)?)\s*/\s*(n(o)?)\s*[\)\]]"#, options: [.regularExpression, .caseInsensitive]) else { return nil }
        let inner = line[match].filter(\.isLetter)
        let yes = inner.prefix { $0 == "y" || $0 == "Y" || $0 == "e" || $0 == "s" || $0 == "E" || $0 == "S" }
        let no = inner.dropFirst(yes.count)
        if yes.first == "Y" && no.first == "n" { return .some(true) }
        if yes.first == "y" && no.first == "N" { return .some(false) }
        return .some(nil)
    }

    static func isSecret(_ line: String) -> Bool {
        line.range(of: #"(password|passphrase|passcode|\bpin\b|token|secret|비밀번호|암호|パスワード|密码)[^\n]*[:?：]\s*$"#, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// A list the arrow keys move through: a line marked with a pointer among
    /// the last lines, below the question (a line opening with `?`).
    static func choice(_ lines: [String]) -> (text: String, options: [String], kind: TerminalPrompt.Kind, defaultYes: Bool?)? {
        let recent = lines.suffix(TerminalPrompt.maximumOptions + 2).map(trimmedEnd)
        guard let questionIndex = recent.lastIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("?") }) else { return nil }
        let options = recent[(questionIndex + 1)...].filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        guard options.count >= 2, options.contains(where: isMarked) else { return nil }
        return (shown(recent[questionIndex]), options.prefix(TerminalPrompt.maximumOptions).map { $0.trimmingCharacters(in: .whitespaces) }, .choice, nil)
    }

    public static func isMarked(_ line: String) -> Bool {
        let start = line.trimmingCharacters(in: .whitespaces)
        return ["❯", "›", ">", "●", "◉", "◆", "→"].contains { start.hasPrefix($0) }
    }

    static func shown(_ line: String) -> String {
        String(line.trimmingCharacters(in: .whitespaces).prefix(TerminalPrompt.maximumTextLength))
    }

    static func trimmedEnd(_ line: String) -> String {
        var line = Substring(line)
        while let last = line.last, last.isWhitespace { line = line.dropLast() }
        return String(line)
    }
}

/// Follows the commands running in one agent pane's terminal and says which
/// of them waits for input. It reads each command's output with its own
/// cursor (the agent's reads are not touched), cleans it, keeps its last
/// lines, and asks ``TerminalPromptDetector`` once the output has not grown for
/// ``quietSeconds``. An answered prompt stays hidden until the output grows.
public final class TerminalPromptMonitor: @unchecked Sendable {
    public static let quietSeconds: TimeInterval = 0.8
    static let tailCharacters = 4_096

    private struct Follow {
        var offset = 0
        var cleaner = AgentTerminalOutputCleaner()
        var tail = ""
        var length = 0
        var grewAt: Date
        var answeredAtLength: Int?
    }

    private let quietSeconds: TimeInterval
    private let now: () -> Date
    private var follows: [String: Follow] = [:]
    private let lock = NSLock()

    public init(quietSeconds: TimeInterval = TerminalPromptMonitor.quietSeconds, now: @escaping () -> Date = Date.init) {
        self.quietSeconds = quietSeconds; self.now = now
    }

    /// Reads what the running commands printed since the last poll and returns
    /// the prompt the newest waiting one shows, nil when none waits.
    public func poll(_ pane: AgentTerminalPane) -> TerminalPrompt? {
        lock.lock(); defer { lock.unlock() }
        let running = pane.runningHandles()
        follows = follows.filter { running.contains($0.key) }
        var found: TerminalPrompt?
        for handle in running {
            var follow = follows[handle] ?? Follow(grewAt: now())
            var more = true
            while more {
                let read = pane.readOutput(handle: handle, fromOffset: follow.offset, maxBytes: AgentTerminalRunner.readChunkBytes)
                more = read.moreRemains && read.nextOffset > follow.offset
                follow.offset = read.nextOffset
                let cleaned = follow.cleaner.clean(read.output, final: false)
                if !cleaned.isEmpty {
                    follow.tail = String((follow.tail + cleaned).suffix(Self.tailCharacters))
                    follow.length += cleaned.count
                    follow.grewAt = now()
                }
            }
            follows[handle] = follow
            guard now().timeIntervalSince(follow.grewAt) >= quietSeconds, follow.answeredAtLength.map({ follow.length > $0 }) ?? true,
                  let detected = TerminalPromptDetector.detect(follow.tail) else { continue }
            found = TerminalPrompt(id: "\(handle):\(follow.length)", handle: handle, text: detected.text, options: detected.options, kind: detected.kind, defaultYes: detected.defaultYes)
        }
        return found
    }

    /// The user answered `prompt`: it is not shown again until its command prints more.
    public func answered(_ prompt: TerminalPrompt) {
        lock.lock(); defer { lock.unlock() }
        guard var follow = follows[prompt.handle] else { return }
        follow.answeredAtLength = follow.length
        follows[prompt.handle] = follow
    }
}
