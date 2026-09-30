import Foundation

/// The user's own Codex `developer_instructions`, read so a run can add ours
/// after them instead of replacing them: `-c developer_instructions=` replaces
/// the configured value and Codex has no additive key.
///
/// Reading is conservative. Anything this reader does not confidently parse is
/// ``Setting/unreadable`` and the run then passes no `developer_instructions`
/// at all, so the user's value is never clobbered.
public enum CodexUserInstructions {
    public enum Setting: Equatable, Sendable {
        case absent
        case value(String)
        case unreadable
    }

    static let key = "developer_instructions"

    /// The value Codex would use for a run in `workingDirectory`.
    ///
    /// Codex layers `.codex/config.toml` files from the project root (the git
    /// root, or the working directory itself outside git) down to the working
    /// directory over `$CODEX_HOME/config.toml`, but only for a project the
    /// user marked trusted. A project layer that sets the key is used only when
    /// the home config affirmatively trusts that root; otherwise the result is
    /// ``Setting/unreadable``, which neither clobbers a value Codex would use
    /// nor lifts an untrusted repository's text into developer instructions.
    public static func effective(codexHome: URL, workingDirectory: URL?) -> Setting {
        let homeFile = codexHome.appendingPathComponent("config.toml")
        let homeText = read(homeFile)
        let home: Setting = homeText.map { $0.map(parse) ?? .unreadable } ?? .absent
        guard let workingDirectory else { return home }
        let homePath = canonical(homeFile.path)
        var layer: Setting = .absent
        let chain = projectChain(workingDirectory.standardizedFileURL)
        for directory in chain.directories {
            let file = directory.appendingPathComponent(".codex/config.toml")
            guard canonical(file.path) != homePath, let text = read(file) else { continue }
            let setting = text.map(parse) ?? .unreadable
            if setting != .absent { layer = setting }
        }
        guard layer != .absent else { return home }
        guard let homeText = homeText ?? nil, trusts(project: chain.root, homeConfig: homeText) else { return .unreadable }
        return layer
    }

    /// The effective `developer_instructions` of one config.toml: a
    /// `[profiles.<profile>]` value when a top-level `profile` names one that
    /// sets it, else the top-level value.
    public static func parse(_ text: String) -> Setting {
        guard text.contains(key) else { return .absent }
        guard let entries = TOMLScanner.entries(text) else { return .unreadable }
        var top: Setting = .absent
        var profiles: [String: Setting] = [:]
        var profile: String?
        var profileUnreadable = false
        for entry in entries {
            let path = entry.path
            if path == ["profile"], !entry.inArrayTable {
                guard case .string(let name) = entry.value, profile == nil else { profileUnreadable = true; continue }
                profile = name
                continue
            }
            guard path.contains(key) || entry.value.mentions(key) else { continue }
            if entry.inArrayTable { return .unreadable }
            if path == [key] {
                guard top == .absent else { return .unreadable }
                top = entry.value.setting
            } else if path.count == 3, path[0] == "profiles", path[2] == key {
                guard profiles[path[1]] == nil else { return .unreadable }
                profiles[path[1]] = entry.value.setting
            } else if path.first == "profiles", path.count <= 2 {
                // `profiles = { ... }` or `[profiles] p = { ... }` spelling the key.
                return .unreadable
            }
        }
        if profileUnreadable { return .unreadable }
        if let profile, let chosen = profiles[profile] { return chosen }
        return top
    }

    /// Whether `homeConfig` has `[projects."<root>"] trust_level = "trusted"`.
    static func trusts(project root: URL, homeConfig: String) -> Bool {
        guard let entries = TOMLScanner.entries(homeConfig) else { return false }
        let names = Set([root.standardizedFileURL.path, canonical(root.path)])
        return entries.contains { entry in
            guard !entry.inArrayTable, entry.path.count == 3, entry.path[0] == "projects", entry.path[2] == "trust_level", names.contains(entry.path[1]),
                  case .string(let level) = entry.value else { return false }
            return level == "trusted"
        }
    }

    /// Directories whose `.codex/config.toml` Codex layers, outermost first,
    /// and the project root they belong to.
    static func projectChain(_ workingDirectory: URL) -> (root: URL, directories: [URL]) {
        var chain = [workingDirectory]
        var current = workingDirectory
        for _ in 0 ..< 64 {
            if FileManager.default.fileExists(atPath: current.appendingPathComponent(".git").path) { return (current, chain.reversed()) }
            let parent = current.deletingLastPathComponent()
            guard parent.path != current.path else { break }
            current = parent; chain.append(parent)
        }
        return (workingDirectory, [workingDirectory])
    }

    /// nil: no file. `.some(nil)`: present but not a small UTF-8 file.
    private static func read(_ url: URL) -> String?? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        guard let data = CLIAccountSupport.boundedData(url), let text = String(data: data, encoding: .utf8) else { return .some(nil) }
        return .some(text)
    }

    private static func canonical(_ path: String) -> String {
        guard let resolved = realpath(path, nil) else { return URL(fileURLWithPath: path).standardizedFileURL.path }
        defer { free(resolved) }
        return String(cString: resolved)
    }
}

/// A small, strict reader for the parts of TOML a Codex config uses: table
/// headers, dotted keys, all four string forms, comments, and skipping of
/// scalars, arrays and inline tables. Returns nil for anything it does not
/// confidently understand.
enum TOMLScanner {
    enum Value: Equatable {
        case string(String)
        case scalar
        /// An array or inline table, kept as source text.
        case complex(String)

        var setting: CodexUserInstructions.Setting {
            if case .string(let value) = self { return .value(value) }
            return .unreadable
        }

        func mentions(_ word: String) -> Bool {
            if case .complex(let source) = self { return source.contains(word) }
            return false
        }
    }

    struct Entry: Equatable {
        var path: [String]
        var value: Value
        var inArrayTable: Bool
    }

    static func entries(_ text: String) -> [Entry]? {
        var scanner = Cursor(Array(text.replacingOccurrences(of: "\r\n", with: "\n").unicodeScalars))
        var table: [String] = []
        var inArrayTable = false
        var result: [Entry] = []
        while true {
            scanner.skipBlankLinesAndComments()
            guard let c = scanner.peek() else { return result }
            if c == "[" {
                scanner.advance()
                let array = scanner.peek() == "["
                if array { scanner.advance() }
                guard let path = scanner.keyPath() else { return nil }
                guard scanner.take("]"), !array || scanner.take("]") else { return nil }
                guard scanner.endOfLine() else { return nil }
                table = path; inArrayTable = array
            } else {
                guard let key = scanner.keyPath(), scanner.take("="), let value = scanner.value(), scanner.endOfLine() else { return nil }
                result.append(Entry(path: table + key, value: value, inArrayTable: inArrayTable))
            }
        }
    }

    struct Cursor {
        let s: [Unicode.Scalar]
        var i = 0
        init(_ s: [Unicode.Scalar]) { self.s = s }

        func peek(_ offset: Int = 0) -> Unicode.Scalar? { i + offset < s.count ? s[i + offset] : nil }
        mutating func advance(_ n: Int = 1) { i += n }
        func startsWith(_ text: String) -> Bool {
            for (k, u) in text.unicodeScalars.enumerated() where peek(k) != u { return false }
            return true
        }

        mutating func skipSpaces() { while let c = peek(), c == " " || c == "\t" { advance() } }
        mutating func skipComment() { if peek() == "#" { while let c = peek(), c != "\n" { advance() } } }
        mutating func skipBlankLinesAndComments() {
            while true {
                skipSpaces(); skipComment()
                guard peek() == "\n" else { return }
                advance()
            }
        }
        /// Spaces, an optional comment, then a newline or the end.
        mutating func endOfLine() -> Bool {
            skipSpaces(); skipComment()
            guard let c = peek() else { return true }
            guard c == "\n" else { return false }
            advance(); return true
        }
        mutating func take(_ c: Unicode.Scalar) -> Bool {
            skipSpaces()
            guard peek() == c else { return false }
            advance(); return true
        }

        mutating func keyPath() -> [String]? {
            var parts: [String] = []
            repeat {
                skipSpaces()
                guard let c = peek() else { return nil }
                if c == "\"" {
                    guard !startsWith("\"\"\""), let part = basicString() else { return nil }
                    parts.append(part)
                } else if c == "'" {
                    guard !startsWith("'''"), let part = literalString() else { return nil }
                    parts.append(part)
                } else {
                    var part = ""
                    while let c = peek(), Self.bare(c) { part.unicodeScalars.append(c); advance() }
                    guard !part.isEmpty else { return nil }
                    parts.append(part)
                }
                skipSpaces()
            } while take(".")
            return parts
        }

        static func bare(_ c: Unicode.Scalar) -> Bool {
            ("a" ... "z").contains(c) || ("A" ... "Z").contains(c) || ("0" ... "9").contains(c) || c == "_" || c == "-"
        }

        mutating func value() -> Value? {
            skipSpaces()
            guard let c = peek() else { return nil }
            switch c {
            case "\"":
                if startsWith("\"\"\"") { return multilineBasic().map(Value.string) }
                return basicString().map(Value.string)
            case "'":
                if startsWith("'''") { return multilineLiteral().map(Value.string) }
                return literalString().map(Value.string)
            case "[", "{":
                let start = i
                guard skipCompound() else { return nil }
                return .complex(String(String.UnicodeScalarView(s[start ..< i])))
            default:
                var any = false
                while let c = peek(), c != "\n", c != "#", c != ",", c != "]", c != "}" { any = true; advance() }
                return any ? .scalar : nil
            }
        }

        /// Skip an array or inline table, including nested ones, strings and
        /// (in arrays) comments and newlines.
        mutating func skipCompound() -> Bool {
            var closers: [Unicode.Scalar] = []
            repeat {
                guard let c = peek() else { return false }
                switch c {
                case "[": closers.append("]"); advance()
                case "{": closers.append("}"); advance()
                case "]", "}":
                    guard closers.last == c else { return false }
                    closers.removeLast(); advance()
                case "\"":
                    guard (startsWith("\"\"\"") ? multilineBasic() : basicString()) != nil else { return false }
                case "'":
                    guard (startsWith("'''") ? multilineLiteral() : literalString()) != nil else { return false }
                case "#": skipComment()
                default: advance()
                }
            } while !closers.isEmpty
            return true
        }

        mutating func basicString() -> String? {
            advance()
            var out = ""
            while let c = peek() {
                if c == "\"" { advance(); return out }
                if c == "\n" { return nil }
                if c == "\\" { guard let e = escape() else { return nil }; out.unicodeScalars.append(e); continue }
                out.unicodeScalars.append(c); advance()
            }
            return nil
        }

        mutating func literalString() -> String? {
            advance()
            var out = ""
            while let c = peek() {
                if c == "'" { advance(); return out }
                if c == "\n" { return nil }
                out.unicodeScalars.append(c); advance()
            }
            return nil
        }

        mutating func multilineBasic() -> String? {
            advance(3)
            if peek() == "\n" { advance() }
            var out = ""
            while let c = peek() {
                if c == "\"", startsWith("\"\"\"") {
                    var run = 0
                    while peek(run) == "\"" { run += 1 }
                    guard run <= 5 else { return nil }
                    out += String(repeating: "\"", count: run - 3); advance(run); return out
                }
                if c == "\\" {
                    // A line-ending backslash trims the newline and following whitespace.
                    var k = 1
                    while let w = peek(k), w == " " || w == "\t" { k += 1 }
                    if peek(k) == "\n" {
                        advance(k)
                        while let w = peek(), w == " " || w == "\t" || w == "\n" { advance() }
                        continue
                    }
                    guard let e = escape() else { return nil }
                    out.unicodeScalars.append(e); continue
                }
                out.unicodeScalars.append(c); advance()
            }
            return nil
        }

        mutating func multilineLiteral() -> String? {
            advance(3)
            if peek() == "\n" { advance() }
            var out = ""
            while let c = peek() {
                if c == "'", startsWith("'''") {
                    var run = 0
                    while peek(run) == "'" { run += 1 }
                    guard run <= 5 else { return nil }
                    out += String(repeating: "'", count: run - 3); advance(run); return out
                }
                out.unicodeScalars.append(c); advance()
            }
            return nil
        }

        mutating func escape() -> Unicode.Scalar? {
            advance()
            guard let c = peek() else { return nil }
            advance()
            switch c {
            case "\"": return "\""
            case "\\": return "\\"
            case "n": return "\n"
            case "t": return "\t"
            case "r": return "\r"
            case "b": return "\u{08}"
            case "f": return "\u{0C}"
            case "e": return "\u{1B}"
            case "x": return hex(2)
            case "u": return hex(4)
            case "U": return hex(8)
            default: return nil
            }
        }

        mutating func hex(_ count: Int) -> Unicode.Scalar? {
            var digits = ""
            for _ in 0 ..< count {
                guard let c = peek(), c.properties.isASCIIHexDigit else { return nil }
                digits.unicodeScalars.append(c); advance()
            }
            return UInt32(digits, radix: 16).flatMap(Unicode.Scalar.init)
        }
    }
}
