import CryptoKit
import Foundation

/// Claude Code's status line: a command from `settings.json` (`statusLine`)
/// that the CLI runs on every render with a JSON description of the session
/// on stdin, showing whatever it prints under the prompt. Print mode never
/// renders it, so the app runs the same command with the same payload and
/// draws the result under its own composer. Any tool that installs a
/// `statusLine` (oh-my-claudecode's HUD, ccusage, a personal script) works.
public struct StatusLineConfig: Sendable, Equatable {
    public var command: String
    public var padding: Int
    /// Which settings file supplied it, for the tooltip and the trust prompt.
    public var source: String
    public var fromWorkspace: Bool
    /// `outputStyle` / `alwaysThinkingEnabled` from the same file, when set.
    public var outputStyle: String?
    public var thinkingEnabled: Bool?
    public init(command: String, padding: Int = 0, source: String, fromWorkspace: Bool = false, outputStyle: String? = nil, thinkingEnabled: Bool? = nil) {
        self.command = command; self.padding = padding; self.source = source; self.fromWorkspace = fromWorkspace; self.outputStyle = outputStyle; self.thinkingEnabled = thinkingEnabled
    }

    /// A stable identity for "the user allowed this exact command here".
    public var fingerprint: String { SHA256.hash(data: Data((source + "\n" + command).utf8)).map { String(format: "%02x", $0) }.joined() }

    /// Both levels Claude consults. `workspace` is the first of the workspace's
    /// `.claude/settings.local.json` / `.claude/settings.json` that has an
    /// entry; `user` is `~/.claude/settings.json` (or `$CLAUDE_CONFIG_DIR`).
    /// A non-command entry at a level disables that level (`command == nil`).
    public struct Discovery: Sendable, Equatable {
        public var workspace: StatusLineConfig?
        public var workspaceDisabled = false
        public var user: StatusLineConfig?
        /// What runs when workspace-level commands are trusted (Claude's precedence).
        public var preferred: StatusLineConfig? { workspaceDisabled ? nil : (workspace ?? user) }
    }

    public static func discover(workspacePath: String?, home: URL = FileManager.default.homeDirectoryForCurrentUser, environment: [String: String] = ProcessInfo.processInfo.environment) -> Discovery {
        var result = Discovery()
        if let workspacePath {
            let root = URL(fileURLWithPath: workspacePath, isDirectory: true)
            for (url, source) in [(root.appendingPathComponent(".claude/settings.local.json"), "프로젝트 로컬 설정"), (root.appendingPathComponent(".claude/settings.json"), "프로젝트 설정")] {
                guard let entry = read(url, source: source, fromWorkspace: true) else { continue }
                if let config = entry { result.workspace = config } else { result.workspaceDisabled = true }
                break
            }
        }
        let configDir = environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) } ?? home.appendingPathComponent(".claude", isDirectory: true)
        if let entry = read(configDir.appendingPathComponent("settings.json"), source: "사용자 설정", fromWorkspace: false) { result.user = entry }
        return result
    }

    /// Claude's own precedence, for callers that already trust the workspace.
    public static func load(workspacePath: String?, home: URL = FileManager.default.homeDirectoryForCurrentUser, environment: [String: String] = ProcessInfo.processInfo.environment) -> StatusLineConfig? {
        discover(workspacePath: workspacePath, home: home, environment: environment).preferred
    }

    /// nil: no `statusLine` key. `.some(nil)`: a key that is not a command.
    static func read(_ url: URL, source: String, fromWorkspace: Bool) -> StatusLineConfig?? {
        guard let data = try? Data(contentsOf: url), data.count <= 4 * 1024 * 1024,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let entry = object["statusLine"] as? [String: Any] else { return nil }
        guard entry["type"] as? String == "command", let command = entry["command"] as? String,
              !command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, command.utf8.count <= 4096 else { return .some(nil) }
        let padding = (entry["padding"] as? Int).map { max(0, min(8, $0)) } ?? 0
        let style = (object["outputStyle"] as? String).flatMap { $0.isEmpty || $0.utf8.count > 80 ? nil : $0 }
        let thinking = object["alwaysThinkingEnabled"].flatMap { value -> Bool? in
            guard let bytes = try? JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed]) else { return nil }
            return try? JSONDecoder().decode(Bool.self, from: bytes)
        }
        return .some(StatusLineConfig(command: command, padding: padding, source: source, fromWorkspace: fromWorkspace, outputStyle: style, thinkingEnabled: thinking))
    }
}

/// What the app knows about a pane, in the CLI's own field names.
public struct StatusLineContext: Sendable, Equatable {
    public var sessionId: String
    public var cwd: String
    public var projectDir: String
    public var modelId: String
    public var modelName: String
    public var version: String
    public var costUSD: Double?
    public var durationMs: Int
    public var apiDurationMs: Int
    public var inputTokens: Int?
    public var outputTokens: Int?
    public var cacheReadTokens: Int?
    public var cacheWriteTokens: Int?
    public var contextUsedTokens: Int?
    public var contextWindowTokens: Int?
    public var effort: String?
    public var fastMode: Bool
    public var rateLimits: [SessionRateLimit]
    public var outputStyle: String?
    public var thinkingEnabled: Bool?
    public init(sessionId: String, cwd: String, projectDir: String, modelId: String, modelName: String, version: String, costUSD: Double? = nil, durationMs: Int = 0, apiDurationMs: Int = 0,
                inputTokens: Int? = nil, outputTokens: Int? = nil, cacheReadTokens: Int? = nil, cacheWriteTokens: Int? = nil, contextUsedTokens: Int? = nil, contextWindowTokens: Int? = nil,
                effort: String? = nil, fastMode: Bool = false, rateLimits: [SessionRateLimit] = [], outputStyle: String? = nil, thinkingEnabled: Bool? = nil) {
        self.sessionId = sessionId; self.cwd = cwd; self.projectDir = projectDir; self.modelId = modelId; self.modelName = modelName; self.version = version
        self.costUSD = costUSD; self.durationMs = durationMs; self.apiDurationMs = apiDurationMs
        self.inputTokens = inputTokens; self.outputTokens = outputTokens; self.cacheReadTokens = cacheReadTokens; self.cacheWriteTokens = cacheWriteTokens
        self.contextUsedTokens = contextUsedTokens; self.contextWindowTokens = contextWindowTokens; self.effort = effort; self.fastMode = fastMode; self.rateLimits = rateLimits
        self.outputStyle = outputStyle; self.thinkingEnabled = thinkingEnabled
    }
}

public enum StatusLineSupport {
    public static let maximumLines = 6
    public static let maximumOutputBytes = 16 * 1024

    /// Claude keeps transcripts under `<config>/projects/<cwd with every
    /// non-alphanumeric byte replaced by "-">/<session id>.jsonl`.
    public static func transcriptPath(cwd: String, sessionId: String, home: URL = FileManager.default.homeDirectoryForCurrentUser, environment: [String: String] = ProcessInfo.processInfo.environment) -> String {
        let configDir = environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) } ?? home.appendingPathComponent(".claude", isDirectory: true)
        let slug = String(cwd.unicodeScalars.map { scalar -> Character in
            switch scalar {
            case "a"..."z", "A"..."Z", "0"..."9": return Character(scalar)
            default: return "-"
            }
        }.prefix(200))
        return configDir.appendingPathComponent("projects/\(slug)/\(sessionId).jsonl").path
    }

    /// The stdin document, shaped like Claude Code 2.1.x's `Status` payload.
    /// Fields the app cannot measure (lines added/removed) are zero; optional
    /// sections are omitted rather than guessed.
    public static func payload(_ context: StatusLineContext, home: URL = FileManager.default.homeDirectoryForCurrentUser, environment: [String: String] = ProcessInfo.processInfo.environment) -> [String: Any] {
        var object: [String: Any] = [
            "hook_event_name": "Status",
            "session_id": context.sessionId,
            "transcript_path": transcriptPath(cwd: context.cwd, sessionId: context.sessionId, home: home, environment: environment),
            "cwd": context.cwd,
            "model": ["id": context.modelId, "display_name": context.modelName],
            "workspace": ["current_dir": context.cwd, "project_dir": context.projectDir],
            "version": context.version,
            "cost": ["total_cost_usd": context.costUSD ?? 0, "total_duration_ms": context.durationMs, "total_api_duration_ms": context.apiDurationMs, "total_lines_added": 0, "total_lines_removed": 0],
            "fast_mode": context.fastMode,
        ]
        if let style = context.outputStyle { object["output_style"] = ["name": style] }
        if let thinking = context.thinkingEnabled { object["thinking"] = ["enabled": thinking] }
        var window: [String: Any] = [
            "total_input_tokens": context.inputTokens ?? 0,
            "total_output_tokens": context.outputTokens ?? 0,
            "context_window_size": context.contextWindowTokens ?? 200_000,
        ]
        if let used = context.contextUsedTokens {
            let size = max(1, context.contextWindowTokens ?? 200_000)
            let percent = min(100, max(0, Double(used) / Double(size) * 100))
            // The CLI's current_usage describes the context itself (its parts sum
            // to the used tokens). The app's cache counters are cumulative, so
            // only the measured context size is reported, all as input.
            window["current_usage"] = ["input_tokens": used, "output_tokens": 0, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0]
            window["used_percentage"] = (percent * 10).rounded() / 10
            window["remaining_percentage"] = ((100 - percent) * 10).rounded() / 10
            object["exceeds_200k_tokens"] = used > 200_000
        } else {
            window["current_usage"] = NSNull(); window["used_percentage"] = NSNull(); window["remaining_percentage"] = NSNull()
            object["exceeds_200k_tokens"] = false
        }
        object["context_window"] = window
        if let effort = context.effort, ["low", "medium", "high", "xhigh", "max"].contains(effort) { object["effort"] = ["level": effort] }
        var limits: [String: Any] = [:]
        let formatter = ISO8601DateFormatter()
        let fractional = ISO8601DateFormatter(); fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        for limit in context.rateLimits {
            let key: String
            switch limit.kind {
            case "five_hour", "session", "5h", "primary": key = "five_hour"
            case "seven_day", "weekly", "7d", "secondary": key = "seven_day"
            default: continue
            }
            guard limits[key] == nil, let percent = limit.percentUsed, percent.isFinite else { continue }
            var entry: [String: Any] = ["used_percentage": min(100, max(0, percent))]
            if let resets = limit.resetsAt, let date = formatter.date(from: resets) ?? fractional.date(from: resets) {
                guard date > Date() else { continue }
                entry["resets_at"] = Int(date.timeIntervalSince1970)
            }
            limits[key] = entry
        }
        if !limits.isEmpty { object["rate_limits"] = limits }
        return object
    }

    /// Runs `config.command` through `/bin/sh -c` in `cwd` with the payload on
    /// stdin, the way the CLI does, on the app's `posix_spawn` path: its own
    /// process group (killed as a whole on timeout), SIGPIPE-safe stdin, and a
    /// bounded drain after exit so a backgrounded helper cannot hang the pane.
    /// Output is capped; a non-zero exit still returns whatever was printed.
    public static func run(_ config: StatusLineConfig, payload: [String: Any], cwd: String, environment: [String: String], timeout: TimeInterval = 8) async -> StatusLineResult {
        let data = (try? JSONSerialization.data(withJSONObject: payload)) ?? Data("{}".utf8)
        let output = OutputBuffer(limit: maximumOutputBytes)
        var env = environment
        env["CLAUDE_CODE_STATUSLINE_HOST"] = "mightyclaude"
        let child: NativeChildProcess
        do {
            child = try NativeChildProcess(executable: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", config.command], environment: env, cwd: URL(fileURLWithPath: cwd, isDirectory: true),
                                           stdout: { output.append($0) }, stderr: { output.appendError($0) }, exited: { _ in })
        } catch {
            return StatusLineResult(lines: [], error: "명령을 시작하지 못했습니다: \(error.localizedDescription)", status: -1, timedOut: false)
        }
        child.write(data, closeAfter: true)
        let code = await child.wait(timeout: timeout)
        return output.result(status: Int(code), timedOut: code == -1)
    }

    /// Lines the CLI would draw: the first `maximumLines`, trailing blank
    /// lines dropped, each parsed for SGR colour and weight.
    public static func lines(from text: String) -> [[ANSISegment]] {
        var rows = text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        while rows.last?.trimmingCharacters(in: .whitespaces).isEmpty == true { rows.removeLast() }
        return rows.prefix(maximumLines).map(ANSIText.parse)
    }

    /// One private class shared by the reader callbacks; the limit keeps a
    /// chatty command from filling memory.
    final class OutputBuffer: @unchecked Sendable {
        private let lock = NSLock()
        private var data = Data()
        private var errorData = Data()
        private let limit: Int
        init(limit: Int) { self.limit = limit }
        func append(_ chunk: Data) { lock.lock(); if data.count < limit { data.append(chunk.prefix(limit - data.count)) }; lock.unlock() }
        func appendError(_ chunk: Data) { lock.lock(); if errorData.count < 4096 { errorData.append(chunk.prefix(4096 - errorData.count)) }; lock.unlock() }
        func result(status: Int, timedOut: Bool) -> StatusLineResult {
            lock.lock(); defer { lock.unlock() }
            let text = String(decoding: data, as: UTF8.self)
            let errorText = String(decoding: errorData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            let lines = StatusLineSupport.lines(from: text)
            var error: String?
            if timedOut { error = "상태 줄 명령이 제한 시간 안에 끝나지 않았습니다." }
            else if lines.isEmpty, status != 0 { error = errorText.isEmpty ? "상태 줄 명령이 종료 코드 \(status)로 끝났습니다." : String(errorText.prefix(300)) }
            return StatusLineResult(lines: lines, error: error, status: status, timedOut: timedOut)
        }
    }
}

public struct StatusLineResult: Sendable, Equatable {
    public var lines: [[ANSISegment]]
    public var error: String?
    public var status: Int
    public var timedOut: Bool
    public init(lines: [[ANSISegment]], error: String? = nil, status: Int = 0, timedOut: Bool = false) { self.lines = lines; self.error = error; self.status = status; self.timedOut = timedOut }
    public var plainText: String { lines.map { $0.map(\.text).joined() }.joined(separator: "\n") }
}

/// A run of text with the SGR state that applied to it.
public struct ANSISegment: Sendable, Equatable {
    public enum Color: Sendable, Equatable {
        case standard(Int)      // 0–7 normal, 8–15 bright
        case palette(Int)       // 256-colour index
        case rgb(Int, Int, Int)
    }
    public var text: String
    public var bold = false
    public var dim = false
    public var italic = false
    public var underline = false
    public var foreground: Color?
    public var background: Color?
    public init(text: String, bold: Bool = false, dim: Bool = false, italic: Bool = false, underline: Bool = false, foreground: Color? = nil, background: Color? = nil) {
        self.text = text; self.bold = bold; self.dim = dim; self.italic = italic; self.underline = underline; self.foreground = foreground; self.background = background
    }
}

/// Minimal SGR parser: colours, weight, italics, underline. Every other
/// escape (cursor movement, OSC titles and hyperlinks) is removed so the
/// visible text is exactly what a terminal would show.
public enum ANSIText {
    public static func parse(_ line: String) -> [ANSISegment] {
        var segments: [ANSISegment] = []
        var state = ANSISegment(text: "")
        var buffer = ""
        let scalars = Array(line.unicodeScalars)
        var index = 0
        func flush() {
            guard !buffer.isEmpty else { return }
            var segment = state; segment.text = buffer
            segments.append(segment); buffer = ""
        }
        while index < scalars.count {
            let scalar = scalars[index]
            guard scalar == "\u{1B}" else {
                if scalar.value >= 0x20 || scalar == "\t" { buffer.unicodeScalars.append(scalar) }
                index += 1; continue
            }
            guard index + 1 < scalars.count else { break }
            let kind = scalars[index + 1]
            if kind == "[" {
                var end = index + 2
                var parameters = ""
                while end < scalars.count, !(0x40...0x7E).contains(scalars[end].value) { parameters.unicodeScalars.append(scalars[end]); end += 1 }
                if end < scalars.count, scalars[end] == "m" { flush(); apply(parameters, to: &state) }
                index = min(scalars.count, end + 1)
            } else if kind == "]" {
                // OSC … BEL or ESC \
                var end = index + 2
                while end < scalars.count {
                    if scalars[end] == "\u{07}" { end += 1; break }
                    if scalars[end] == "\u{1B}", end + 1 < scalars.count, scalars[end + 1] == "\\" { end += 2; break }
                    end += 1
                }
                index = end
            } else {
                index += 2
            }
        }
        flush()
        return segments
    }

    static func apply(_ parameters: String, to state: inout ANSISegment) {
        // Semicolons separate parameters; an empty one means 0 (`ESC[m` resets)
        // and anything non-numeric is skipped rather than read as reset. A
        // colon group is one extended colour (`38:2::r:g:b`, `38:5:n`).
        var codes: [Int] = []
        for token in parameters.split(separator: ";", omittingEmptySubsequences: false) {
            if token.contains(":") {
                let subs = token.split(separator: ":", omittingEmptySubsequences: false).map { $0.isEmpty ? nil : Int($0) }
                guard subs.count >= 3, let kind = subs[0], kind == 38 || kind == 48, let mode = subs[1] else { continue }
                let numbers = subs.dropFirst(2).compactMap { $0 }
                if mode == 2, numbers.count >= 3 { codes += [kind, 2] + numbers.suffix(3) }
                else if mode == 5, let index = numbers.first { codes += [kind, 5, index] }
            } else {
                codes.append(token.isEmpty ? 0 : (Int(token) ?? -1))
            }
        }
        var position = 0
        if codes.isEmpty { reset(&state); return }
        while position < codes.count {
            let code = codes[position]; position += 1
            switch code {
            case 0: reset(&state)
            case 1: state.bold = true
            case 2: state.dim = true
            case 3: state.italic = true
            case 4: state.underline = true
            case 22: state.bold = false; state.dim = false
            case 23: state.italic = false
            case 24: state.underline = false
            case 30...37: state.foreground = .standard(code - 30)
            case 90...97: state.foreground = .standard(code - 90 + 8)
            case 39: state.foreground = nil
            case 40...47: state.background = .standard(code - 40)
            case 100...107: state.background = .standard(code - 100 + 8)
            case 49: state.background = nil
            case 38, 48:
                var color: ANSISegment.Color?
                if position < codes.count, codes[position] == 5, position + 1 < codes.count {
                    color = .palette(min(255, max(0, codes[position + 1]))); position += 2
                } else if position < codes.count, codes[position] == 2, position + 3 < codes.count {
                    color = .rgb(clamp(codes[position + 1]), clamp(codes[position + 2]), clamp(codes[position + 3])); position += 4
                } else { position = codes.count }
                if code == 38 { state.foreground = color } else { state.background = color }
            default: break
            }
        }
    }
    private static func clamp(_ value: Int) -> Int { min(255, max(0, value)) }
    private static func reset(_ state: inout ANSISegment) { state = ANSISegment(text: "") }
}
