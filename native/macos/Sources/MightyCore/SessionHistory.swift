import Darwin
import Foundation

/// The session record a pane's CLI keeps on disk: Claude's
/// `<config>/projects/<escaped cwd>/<session>.jsonl` or Codex's
/// `sessions/YYYY/MM/DD/rollout-…-<thread>.jsonl`. The device and inode tell a
/// rotated or replaced file apart from the one a cursor points into.
public struct SessionHistoryFile: Sendable, Equatable {
    public let url: URL
    public let device: UInt64
    public let inode: UInt64
}

/// The retained request the loaded history continues above: its text and,
/// when known, when it was sent.
public struct SessionHistoryAnchor: Sendable, Equatable {
    public var text: String
    public var date: Date?
    public init(text: String, date: Date?) { self.text = text; self.date = date }

    /// What the user typed: the transcript adds one "첨부: " line per attachment.
    static func typed(_ text: String) -> String {
        let typed = text.hasPrefix("첨부: ") ? "" : text.components(separatedBy: "\n\n첨부: ")[0]
        return typed.trimmingCharacters(in: .whitespacesAndNewlines)
    }
    /// How well a record prompt stands for the retained request: 2 when the
    /// text is the same, 1 when it only contains it, nil otherwise. With both
    /// dates known the prompt must also be written around the same time (the
    /// CLI records it a moment after the app sent it).
    func quality(prompt: String, date: Date?) -> Int? {
        let typed = Self.typed(text), prompt = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !typed.isEmpty, prompt == typed || prompt.contains(typed) else { return nil }
        if let anchor = self.date, let date {
            guard date >= anchor.addingTimeInterval(-600) && date <= anchor.addingTimeInterval(120) else { return nil }
        }
        return prompt == typed ? 2 : 1
    }
    func matches(prompt: String, date: Date?) -> Bool { quality(prompt: prompt, date: date) != nil }
    /// How far a prompt was written from the send time; 0 when either is unknown.
    func distance(_ date: Date?) -> TimeInterval {
        guard let anchor = self.date, let date else { return 0 }
        return abs(date.timeIntervalSince(anchor))
    }
    /// Written after any prompt that could match: a newer, retained request.
    func newer(_ date: Date?) -> Bool {
        guard let anchor = self.date, let date else { return false }
        return date > anchor.addingTimeInterval(120)
    }
    /// Written before any prompt that could still match: the search is over.
    func below(_ date: Date?) -> Bool {
        guard let anchor = self.date, let date else { return false }
        return date < anchor.addingTimeInterval(-600)
    }
    /// A prompt written before the retained request was sent is older history
    /// even when its text could not be matched.
    func precedes(_ date: Date?) -> Bool {
        guard let anchor = self.date, let date else { return false }
        return date < anchor.addingTimeInterval(-1)
    }
}

public struct SessionHistoryRequest: Sendable {
    public var provider: String
    public var resumeID: String
    public var workspacePath: String
    public var environment: [String: String]
    public var home: URL
    /// The file an earlier chunk came from; nil locates it.
    public var file: SessionHistoryFile?
    /// Where the previous chunk began; nil reads from the end of the file.
    public var end: Int?
    /// Only for the first chunk: newer requests than this one are already shown.
    public var anchor: SessionHistoryAnchor?
    public var turns: Int
    /// Bytes read per chunk once at least one request was found.
    public var maximumBytes: Int
    public init(provider: String, resumeID: String, workspacePath: String, environment: [String: String] = [:],
                home: URL = FileManager.default.homeDirectoryForCurrentUser, file: SessionHistoryFile? = nil, end: Int? = nil,
                anchor: SessionHistoryAnchor? = nil, turns: Int = SessionHistory.turnsPerChunk, maximumBytes: Int = SessionHistory.maximumChunkBytes) {
        self.provider = provider; self.resumeID = resumeID; self.workspacePath = workspacePath; self.environment = environment
        self.home = home; self.file = file; self.end = end; self.anchor = anchor; self.turns = turns; self.maximumBytes = maximumBytes
    }
}

public struct SessionHistoryChunk: Sendable {
    /// Oldest first, each built by the live graph's own parser and tracker.
    public var runs: [MightyGraphRun]
    public var file: SessionHistoryFile
    /// Where the next older chunk ends.
    public var end: Int
    public var reachedStart: Bool
}

public enum SessionHistoryError: Error, Equatable {
    /// The pane's CLI keeps no record the graph can read (Gemini, a shell).
    case unsupported
    /// No record for this session exists, or it is not a readable regular file.
    case missing
    /// The record was replaced or shortened since the previous chunk.
    case changed
    /// Reading the record failed part way; trying again may work.
    case unreadable
}

/// Older requests of a pane, read back from the CLI's own session record and
/// turned into graph runs by replaying each request through the same
/// `CLIStreamParser` and `ExecutionGraphTracker` a live run uses. They are a
/// view of the record: never saved with the profile and never sent to the phone.
public enum SessionHistory {
    public static let providers = MightyGraphSupport.providers
    public static let turnsPerChunk = 10
    public static let maximumChunkBytes = 48 * 1_048_576
    /// A single record line larger than this (a pasted picture, a huge tool
    /// result) is skipped rather than held in memory.
    static let maximumLineBytes = 16 * 1_048_576
    /// One request's lines kept for replay; past it only the prompt survives.
    static let maximumTurnBytes = 24 * 1_048_576
    /// Text a replayed request may keep, as the saved profile bounds a run.
    static let runBudget = 1_048_576

    // MARK: Locating the record

    /// Claude names a project folder after its working directory with every
    /// character other than an ASCII letter or digit replaced by "-".
    public static func claudeProjectFolder(_ path: String) -> String {
        String(path.unicodeScalars.map { scalar -> Character in
            scalar.isASCII && (CharacterSet.alphanumerics.contains(scalar)) ? Character(scalar) : "-"
        })
    }

    public static func locate(provider: String, resumeID: String, workspacePath: String,
                              environment: [String: String], home: URL) -> URL? {
        guard CoreValidation.identifier(resumeID) else { return nil }
        switch provider {
        case "claude":
            let config = environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) }
                ?? home.appendingPathComponent(".claude", isDirectory: true)
            let projects = config.appendingPathComponent("projects", isDirectory: true)
            let name = resumeID + ".jsonl"
            var folders = [claudeProjectFolder(workspacePath)]
            if let real = realpath(workspacePath, nil) { folders.append(claudeProjectFolder(String(cString: real))); free(real) }
            for folder in folders {
                let url = projects.appendingPathComponent(folder, isDirectory: true).appendingPathComponent(name)
                if regularFile(url) { return url }
            }
            // A long path is shortened by the CLI; the session id alone still finds it.
            let entries = (try? FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil)) ?? []
            for entry in entries.prefix(8_192) {
                let url = entry.appendingPathComponent(name)
                if regularFile(url) { return url }
            }
            return nil
        case "codex":
            guard let sessions = CodexSessionFiles.sessions(codexHome: CLIAccountSupport.codexHome(home: home, environment: environment)) else { return nil }
            return codexRollout(sessions: sessions, thread: resumeID)
        default: return nil
        }
    }
    private static func regularFile(_ url: URL) -> Bool {
        var info = stat()
        return lstat(url.path, &info) == 0 && info.st_mode & S_IFMT == S_IFREG
    }
    /// Codex thread ids are UUIDv7: the first 48 bits are the creation time,
    /// which names the day folder. Other days are walked newest first.
    static func uuidV7Date(_ value: String) -> Date? {
        let hex = value.replacingOccurrences(of: "-", with: "")
        guard hex.count == 32, hex.dropFirst(12).first == "7", let milliseconds = UInt64(hex.prefix(12), radix: 16) else { return nil }
        return Date(timeIntervalSince1970: Double(milliseconds) / 1_000)
    }
    static func codexRollout(sessions: URL, thread: String) -> URL? {
        let wanted = thread.lowercased()
        func find(_ folder: URL) -> URL? { CodexSessionFiles.rollouts(in: folder).first { $0.thread.lowercased() == wanted }?.url }
        if let date = uuidV7Date(thread) {
            for folder in CodexSessionFiles.dayFolders(sessions: sessions, start: date, now: date.addingTimeInterval(86_400)) {
                if let url = find(folder) { return url }
            }
        }
        func folders(_ url: URL) -> [URL] {
            let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey]
            let items = (try? FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
            return items.filter { item in
                let values = try? item.resourceValues(forKeys: Set(keys))
                return values?.isDirectory == true && values?.isSymbolicLink != true && item.lastPathComponent.allSatisfy(\.isNumber)
            }.sorted { $0.lastPathComponent > $1.lastPathComponent }
        }
        var visited = 0
        for year in folders(sessions) {
            for month in folders(year) {
                for day in folders(month) {
                    visited += 1
                    guard visited <= 4_000 else { return nil }
                    if let url = find(day) { return url }
                }
            }
        }
        return nil
    }

    /// The record's identity, for a request that should read this file only.
    public static func identify(_ url: URL) -> SessionHistoryFile? {
        guard let fd = CodexSessionFiles.open(url) else { return nil }
        defer { Darwin.close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0 else { return nil }
        return SessionHistoryFile(url: url, device: UInt64(bitPattern: Int64(info.st_dev)), inode: UInt64(info.st_ino))
    }

    // MARK: Loading a chunk

    /// Reads the next older chunk. Synchronous and file-bound: call it off the
    /// main actor. Never reads the whole record into memory. A cancelled task
    /// stops between requests with `CancellationError`.
    public static func load(_ request: SessionHistoryRequest) throws -> SessionHistoryChunk {
        guard providers.contains(request.provider) else { throw SessionHistoryError.unsupported }
        let url: URL
        if let known = request.file { url = known.url }
        else {
            guard let located = locate(provider: request.provider, resumeID: request.resumeID, workspacePath: request.workspacePath,
                                       environment: request.environment, home: request.home) else { throw SessionHistoryError.missing }
            url = located
        }
        guard let fd = CodexSessionFiles.open(url) else { throw request.file == nil ? SessionHistoryError.missing : SessionHistoryError.changed }
        defer { Darwin.close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0 else { throw SessionHistoryError.missing }
        let file = SessionHistoryFile(url: url, device: UInt64(bitPattern: Int64(info.st_dev)), inode: UInt64(info.st_ino))
        if let known = request.file, known != file { throw SessionHistoryError.changed }
        let size = Int(info.st_size)
        let end = request.end ?? size
        guard end <= size, end >= 0 else { throw SessionHistoryError.changed }
        var reader = JSONLBackwardReader(fd: fd, end: end, maximumLineBytes: maximumLineBytes)
        let format: HistoryFormat = request.provider == "codex"
            ? .codex(thread: CodexSessionFiles.thread(fileName: url.lastPathComponent) ?? request.resumeID) : .claude
        let scan = try HistoryScan.run(&reader, format: format, anchor: request.end == nil ? request.anchor : nil,
                                       turns: max(1, request.turns), maximumBytes: request.maximumBytes)
        var runs: [MightyGraphRun] = []
        for turn in scan.turns {
            try Task.checkCancellation()
            if let run = HistoryReplay.run(turn, format: format) { runs.append(run) }
        }
        return SessionHistoryChunk(runs: runs, file: file, end: scan.end, reachedStart: scan.reachedStart)
    }
}

// MARK: - Reading lines backwards

/// Complete lines of a JSONL file, newest first, read in blocks from an end
/// offset towards the start. A line still being written past the last newline
/// is never returned, and a line longer than the limit is skipped whole.
struct JSONLBackwardReader {
    private let fd: Int32
    private let blockSize: Int
    private let maximumLineBytes: Int
    /// Bytes `[bufferStart, end)` of the file that are not returned yet.
    private var buffer: [UInt8] = []
    private var bufferStart: Int
    /// Every line not yet returned ends before this offset.
    private(set) var end: Int
    private var aligned = false
    /// A read failed: the reader stops as if the file began here.
    private(set) var failed = false

    init(fd: Int32, end: Int, blockSize: Int = 256 * 1_024, maximumLineBytes: Int) {
        self.fd = fd; self.end = end; bufferStart = end; self.blockSize = max(1, blockSize); self.maximumLineBytes = max(1, maximumLineBytes)
    }

    private mutating func fill() -> Bool {
        guard bufferStart > 0, !failed else { return false }
        let count = min(blockSize, bufferStart)
        var block = [UInt8](repeating: 0, count: count)
        let read = block.withUnsafeMutableBytes { pread(fd, $0.baseAddress, count, off_t(bufferStart - count)) }
        guard read == count else { failed = true; return false }
        buffer.insert(contentsOf: block, at: 0)
        bufferStart -= count
        return true
    }

    /// Drop whatever follows the last newline: it is a line still being written.
    private mutating func align() {
        aligned = true
        while true {
            if let index = buffer.lastIndex(of: 10) {
                buffer.removeSubrange((index + 1)...)
                end = bufferStart + index + 1
                return
            }
            buffer.removeAll(keepingCapacity: true)
            guard fill() else { end = 0; return }
        }
    }

    /// The previous complete line and its starting offset; nil at the start.
    mutating func previous() -> (offset: Int, line: Data)? {
        if !aligned { align() }
        while end > 0 {
            // The buffer ends with the newline that closes the wanted line.
            var discarding = false
            while true {
                let searchEnd = buffer.count - 1
                if searchEnd > 0, let index = buffer[..<searchEnd].lastIndex(of: 10) {
                    let start = index + 1
                    let line = discarding || searchEnd - start > maximumLineBytes ? Data() : Data(buffer[start..<searchEnd])
                    let offset = bufferStart + start
                    buffer.removeSubrange(start...)
                    end = offset
                    if discarding || line.isEmpty { break }
                    return (offset, line)
                }
                if bufferStart == 0 || failed {
                    // The first line of the file (or of what could be read).
                    let line = discarding || searchEnd <= 0 || searchEnd > maximumLineBytes ? Data() : Data(buffer[..<searchEnd])
                    let offset = bufferStart
                    buffer.removeAll(); end = 0
                    if failed { return nil }
                    return line.isEmpty ? nil : (offset, line)
                }
                if buffer.count > maximumLineBytes + 1 {
                    // Too long to keep: forget its bytes but remember the
                    // newline that ends it, then look for where it starts.
                    discarding = true
                    buffer = [10]
                }
                guard fill() else { end = 0; return nil }
            }
        }
        return nil
    }
}

// MARK: - Finding requests

enum HistoryFormat: Equatable {
    case claude
    case codex(thread: String)
}

struct HistoryTurn {
    /// Offset of the line that opens the request.
    var start: Int
    var prompt: String
    var timestamp: String?
    var date: Date?
    var key: String?
    /// The request's lines in file order, after its opening line.
    var lines: [Data]
}

enum HistoryScan {
    struct Result { var turns: [HistoryTurn]; var end: Int; var reachedStart: Bool }
    struct Opening { var prompt: String?; var timestamp: String?; var key: String? }

    static func contains(_ line: Data, _ pattern: String) -> Bool { line.range(of: Data(pattern.utf8)) != nil }

    /// The line that opens a request, cheaply rejected on raw bytes first: a
    /// key in raw JSON (`"toolUseResult":`) can never appear inside a string.
    static func opening(_ line: Data, format: HistoryFormat) -> Opening? {
        switch format {
        case .claude:
            guard contains(line, #""type":"user""#), !contains(line, #""toolUseResult":"#),
                  let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  let prompt = claudePrompt(object) else { return nil }
            return Opening(prompt: prompt, timestamp: object["timestamp"] as? String, key: object["uuid"] as? String)
        case .codex:
            guard contains(line, #""task_started""#), let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  object["type"] as? String == "event_msg", let payload = object["payload"] as? [String: Any],
                  payload["type"] as? String == "task_started" else { return nil }
            return Opening(prompt: nil, timestamp: object["timestamp"] as? String, key: payload["turn_id"] as? String)
        }
    }

    /// A request the user sent: not a tool result, an injected notification
    /// or peer message, a meta line, a sub-agent line, a compaction summary, an
    /// interruption or a local command's printed output. Interactive `claude`
    /// stamps what the user typed with a human origin; `-p` stream input has
    /// no origin at all.
    static func claudePrompt(_ object: [String: Any]) -> String? {
        guard object["type"] as? String == "user", object["isMeta"] as? Bool != true, object["isSidechain"] as? Bool != true,
              object["isCompactSummary"] as? Bool != true, humanOrigin(object["origin"]),
              !injectedTurnOrigins.contains(object["turnOrigin"] as? String ?? ""),
              let message = object["message"] as? [String: Any] else { return nil }
        let text: String
        if let value = message["content"] as? String { text = value }
        else if let blocks = message["content"] as? [[String: Any]] {
            guard !blocks.contains(where: { $0["type"] as? String == "tool_result" }) else { return nil }
            text = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined(separator: "\n")
        } else { return nil }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !trimmed.hasPrefix("[Request interrupted by user"), !trimmed.hasPrefix("<local-command-") else { return nil }
        // A slash command the user typed is recorded as tags only.
        if trimmed.hasPrefix("<command-") { return commandPrompt(trimmed) }
        return trimmed
    }
    /// No origin (stream input) or one the user typed; never a notification,
    /// a peer agent or any other injected kind.
    static func humanOrigin(_ origin: Any?) -> Bool {
        guard let origin, !(origin is NSNull) else { return true }
        return (origin as? [String: Any])?["kind"] as? String == "human"
    }
    static let injectedTurnOrigins: Set<String> = ["peer", "task_notification", "task-notification"]
    /// A slash command is recorded as tags; show it as it was typed.
    static func commandPrompt(_ text: String) -> String? {
        func tag(_ name: String) -> String? {
            guard let start = text.range(of: "<\(name)>"), let end = text.range(of: "</\(name)>", range: start.upperBound..<text.endIndex) else { return nil }
            return text[start.upperBound..<end.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
        }
        guard var name = tag("command-name"), !name.isEmpty else { return nil }
        if !name.hasPrefix("/") { name = "/" + name }
        let arguments = tag("command-args") ?? ""
        return arguments.isEmpty ? name : name + " " + arguments
    }
    /// What a local command (`/model`, `/login`) printed: shown as the
    /// command's result, never as a request of its own. nil for other text.
    static func localCommandOutput(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        for tag in ["local-command-stdout", "local-command-stderr"] where trimmed.hasPrefix("<\(tag)>") {
            var inner = String(trimmed.dropFirst(tag.count + 2))
            if inner.hasSuffix("</\(tag)>") { inner = String(inner.dropLast(tag.count + 3)) }
            return inner.replacingOccurrences(of: "\u{1B}\\[[0-9;?]*[A-Za-z]", with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return nil
    }

    /// The user's text of a Codex turn, one rule for the history and the
    /// session list: the `UserMessage` item (0.153 and later), else the
    /// `user_message` event (0.147 and earlier), else the first user message
    /// item the model saw that is not injected context (`<environment_context>`,
    /// `# AGENTS.md instructions …`).
    static func codexPrompt(_ lines: [Data]) -> String? {
        var event: String?, response: String?
        for line in lines {
            if contains(line, #""UserMessage""#), let text = codexUserItem(line) { return text }
            if event == nil, contains(line, #""user_message""#) { event = codexUserEvent(line) }
            if response == nil, contains(line, #""role":"user""#) { response = codexUserResponse(line) }
        }
        return event ?? response
    }
    /// Whether a line can carry a Codex turn's user text at all.
    static func codexUserLine(_ line: Data) -> Bool {
        contains(line, #""UserMessage""#) || contains(line, #""user_message""#) || contains(line, #""role":"user""#)
    }
    private static func codexPayload(_ line: Data, type: String) -> [String: Any]? {
        guard let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any], object["type"] as? String == type,
              let payload = object["payload"] as? [String: Any] else { return nil }
        return payload
    }
    private static func nonEmpty(_ text: String?) -> String? {
        guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return nil }
        return text
    }
    static func codexUserItem(_ line: Data) -> String? {
        guard let payload = codexPayload(line, type: "event_msg"), payload["type"] as? String == "item_completed",
              let item = payload["item"] as? [String: Any], item["type"] as? String == "UserMessage" else { return nil }
        return nonEmpty((item["content"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined(separator: "\n"))
    }
    static func codexUserEvent(_ line: Data) -> String? {
        guard let payload = codexPayload(line, type: "event_msg"), payload["type"] as? String == "user_message" else { return nil }
        return nonEmpty(payload["message"] as? String)
    }
    static func codexUserResponse(_ line: Data) -> String? {
        guard let payload = codexPayload(line, type: "response_item"), payload["type"] as? String == "message",
              payload["role"] as? String == "user",
              let text = nonEmpty((payload["content"] as? [[String: Any]] ?? [])
                .filter { $0["type"] as? String == "input_text" }.compactMap { $0["text"] as? String }.joined(separator: "\n")),
              !text.hasPrefix("<"), !text.hasPrefix("# AGENTS.md instructions") else { return nil }
        return text
    }

    /// The undecided requests (newest first) the retained one is among: the
    /// same text before only a containing one, then the closest send time.
    static func anchorIndex(_ turns: [HistoryTurn], anchor: SessionHistoryAnchor) -> Int? {
        var best: (index: Int, quality: Int, distance: TimeInterval)?
        for (index, turn) in turns.enumerated() {
            guard let quality = anchor.quality(prompt: turn.prompt, date: turn.date) else { continue }
            let distance = anchor.distance(turn.date)
            if let current = best, quality < current.quality || (quality == current.quality && distance >= current.distance) { continue }
            best = (index, quality, distance)
        }
        return best?.index
    }
    /// Requests looked at before the retained one must be placed.
    static let maximumUndecided = 16

    /// Walks back from the reader's end. With an anchor, requests at or after
    /// the retained one are passed over (they are already on screen) and the
    /// chunk starts right above it. Stops after `turns` requests, or once
    /// `maximumBytes` were read and at least one request was found. A failed
    /// read throws `unreadable`; a cancelled task stops between requests.
    static func run(_ reader: inout JSONLBackwardReader, format: HistoryFormat, anchor: SessionHistoryAnchor?,
                    turns wanted: Int, maximumBytes: Int) throws -> Result {
        var collected: [HistoryTurn] = []
        var pending: [Data] = [], pendingBytes = 0
        // Requests near the retained one, newest first, until it is placed.
        var undecided: [HistoryTurn] = []
        var past = anchor == nil
        var end = reader.end, read = 0
        func keep(_ line: Data) {
            // An oversized request keeps the lines nearest its end; a Codex
            // turn also keeps the lines that carry its user text.
            if pendingBytes + line.count > SessionHistory.maximumTurnBytes {
                guard case .codex = format, codexUserLine(line) else { return }
            }
            pending.append(line); pendingBytes += line.count
        }
        /// An older request for the chunk; true once the chunk is full.
        func take(_ turn: HistoryTurn) -> Bool {
            collected.append(turn); end = turn.start
            return collected.count >= wanted || read >= maximumBytes
        }
        /// Places the retained request among the undecided ones: what is older
        /// than it is history. Without a match, what was written before it was
        /// sent is. True once the chunk is full.
        func place() -> Bool {
            past = true
            guard let anchor else { return false }
            let start = anchorIndex(undecided, anchor: anchor).map { $0 + 1 } ?? undecided.firstIndex { anchor.precedes($0.date) } ?? undecided.count
            let older = undecided[start...]
            undecided.removeAll()
            for turn in older where take(turn) { return true }
            return false
        }
        func full() -> Result { Result(turns: collected.reversed(), end: end, reachedStart: false) }
        while let (offset, line) = reader.previous() {
            read += line.count + 1
            guard let opening = opening(line, format: format) else { keep(line); continue }
            try Task.checkCancellation()
            var lines = Array(pending.reversed())
            pending.removeAll(keepingCapacity: true); pendingBytes = 0
            if case .codex = format { lines.insert(line, at: 0) }
            let prompt = opening.prompt ?? codexPrompt(lines)
            let date = opening.timestamp.flatMap(AgentRunTiming.parseTimestamp)
            guard let prompt else { end = offset; continue }
            let turn = HistoryTurn(start: offset, prompt: prompt, timestamp: opening.timestamp, date: date, key: opening.key, lines: lines)
            if !past, let anchor {
                end = offset
                if anchor.below(date) {
                    if place() || take(turn) { return full() }
                    continue
                }
                let quality = anchor.quality(prompt: prompt, date: date)
                if anchor.date != nil, date != nil {
                    // Written after the retained request could have been: on screen.
                    if anchor.newer(date) { continue }
                    undecided.append(turn)
                } else {
                    // Without dates only the text tells; until a candidate
                    // shows up the requests are the newer, retained ones.
                    if quality == nil, undecided.isEmpty { continue }
                    undecided.append(turn)
                    if quality == 2 {
                        if place() { return full() }
                        continue
                    }
                }
                if undecided.count >= maximumUndecided, place() { return full() }
                continue
            }
            if take(turn) { return full() }
        }
        if reader.failed { throw SessionHistoryError.unreadable }
        if !past, place() { return full() }
        // Lines before the first request belong to no request.
        return Result(turns: collected.reversed(), end: 0, reachedStart: true)
    }
}

// MARK: - Replaying a request

/// Feeds one recorded request through a fresh `CLIStreamParser` and records
/// what it emits on a scratch session exactly as `AppStore.apply` does.
final class HistoryReplay {
    private var session: RunSession
    private let provider: String
    private let formatter = ISO8601DateFormatter()
    /// The record line being replayed, in the app's own timestamp form.
    private var stamp: String

    private init(provider: String, runID: String, prompt: String, stamp: String) {
        self.provider = provider; self.stamp = stamp
        session = RunSession(id: "history", workspaceId: "history", title: "", kind: "claude", provider: provider)
        session.graphRuns = []
        session.recordGraph(RunEvent(sessionId: session.id, type: "log", entry: LogEntry(id: runID, kind: "user", text: prompt, timestamp: stamp, provider: provider)))
    }
    private func apply(_ event: RunEvent) { session.recordGraph(event) }
    private func log(_ kind: String, _ text: String) {
        guard !text.isEmpty else { return }
        apply(RunEvent(sessionId: session.id, type: "log", entry: LogEntry(kind: kind, text: ActivitySupport.prefixUTF8(text, maximumBytes: 131_072), timestamp: stamp, provider: provider)))
    }
    private func activity(_ value: AgentActivity) {
        guard let value = ActivitySupport.normalized(value), value.kind != "turn" else { return }
        apply(RunEvent(sessionId: session.id, type: "log", entry: LogEntry(id: value.id, kind: "system", text: value.summary, timestamp: stamp, provider: value.provider, activity: value)))
    }
    private func at(_ raw: Any?) {
        if let raw = raw as? String, let date = AgentRunTiming.parseTimestamp(raw) { stamp = formatter.string(from: date) }
    }

    /// Ids of runs read from the record start with this, so they never meet a
    /// retained run's id (or a legacy "history-" group).
    static let runPrefix = "record-"
    static func runID(_ turn: HistoryTurn) -> String {
        let key = turn.key.map { runPrefix + $0 }
        return key.flatMap { CoreValidation.identifier($0) ? $0 : nil } ?? runPrefix + "o\(turn.start)"
    }

    static func run(_ turn: HistoryTurn, format: HistoryFormat) -> MightyGraphRun? {
        let provider: String
        let model: String?
        switch format {
        case .claude: provider = "claude"; model = nil
        case .codex: provider = "codex"; model = codexModel(turn.lines)
        }
        let formatter = ISO8601DateFormatter()
        let first = turn.date.map { formatter.string(from: $0) } ?? mightyTimestamp()
        let runID = runID(turn)
        let replay = HistoryReplay(provider: provider, runID: runID, prompt: turn.prompt, stamp: first)
        let parser = CLIStreamParser(provider: provider, log: { replay.log($0, $1) }, resume: { _ in },
                                     activityNamespace: runID, activity: { replay.activity($0) },
                                     usage: { replay.apply(RunEvent(sessionId: "history", type: "usage", usage: $0)) },
                                     graph: { replay.apply(RunEvent(sessionId: "history", type: "graph", graph: $0)) },
                                     graphInput: turn.prompt, configuredModel: model)
        let status: String
        switch format {
        case .claude: status = replay.claude(turn.lines, parser: parser)
        case .codex(let thread): status = replay.codex(turn.lines, thread: thread, parser: parser)
        }
        parser.flush()
        parser.finishActivities(stopped: true)
        parser.finishGraph(state: status)
        replay.apply(RunEvent(sessionId: "history", type: "status", status: status))
        guard var run = replay.session.graphRuns?.last, run.id == runID else { return nil }
        if run.nodeModelLabel == nil, let model { run.nodeModelLabel = GraphModelLabel.nodeModelLabel(cliReportedModel: model, configuredModel: "default") }
        // Blocks the tracker made carry the replay's own clock; the request
        // happened when its record says.
        if let latest = AgentRunTiming.parseTimestamp(replay.stamp) {
            let settle: ([LogEntry]) -> [LogEntry] = { entries in
                entries.map { entry in
                    guard let date = AgentRunTiming.parseTimestamp(entry.timestamp), date > latest.addingTimeInterval(1) else { return entry }
                    var entry = entry; entry.timestamp = replay.stamp; return entry
                }
            }
            run.rootEntries = settle(run.rootEntries); run.resultEntries = settle(run.resultEntries)
            for index in run.agents.indices { run.agents[index].entries = settle(run.agents[index].entries) }
        }
        var budget = SessionHistory.runBudget
        return MightyGraphSupport.normalized([run], restoring: true, budget: &budget, provider: provider).first
    }

    // MARK: Claude records

    /// A message the user typed while the request ran, recorded as a queued
    /// command: shown as the live graph's steer block. Injected kinds (peer
    /// messages, task notifications) are not.
    static func steer(_ object: [String: Any]) -> (id: String?, text: String)? {
        guard let attachment = object["attachment"] as? [String: Any], attachment["type"] as? String == "queued_command",
              attachment["commandMode"] as? String == "prompt", attachment["isMeta"] as? Bool != true,
              HistoryScan.humanOrigin(attachment["origin"]) else { return nil }
        let text = (attachment["prompt"] as? String)
            ?? (attachment["prompt"] as? [[String: Any]])?.compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }.joined(separator: "\n")
        guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty, !text.hasPrefix("<") else { return nil }
        return (object["uuid"] as? String, text)
    }

    /// A Claude record line is the stream-json event it was printed as, minus
    /// the final `result`, which is rebuilt from the last answer.
    private func claude(_ lines: [Data], parser: CLIStreamParser) -> String {
        var answer: String?
        var interrupted = false
        for (index, line) in lines.enumerated() {
            guard var object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  let type = object["type"] as? String, ["assistant", "user", "system", "attachment"].contains(type),
                  object["isSidechain"] as? Bool != true else { continue }
            at(object["timestamp"])
            switch type {
            case "attachment":
                if let steer = Self.steer(object) { parser.steer(id: steer.id ?? "line-\(index)", text: steer.text) }
                continue
            case "system":
                guard object["subtype"] as? String == "compact_boundary" else { continue }
                if object["compact_metadata"] == nil, let metadata = object["compactMetadata"] { object["compact_metadata"] = metadata }
            case "user":
                guard object["isMeta"] as? Bool != true, object["isCompactSummary"] as? Bool != true,
                      let message = object["message"] as? [String: Any] else { continue }
                let text = (message["content"] as? String)
                    ?? (message["content"] as? [[String: Any]])?.compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }.joined(separator: "\n")
                if text?.hasPrefix("[Request interrupted by user") == true { interrupted = true; continue }
                // A local command's printed output is that command's result.
                if let output = text.flatMap(HistoryScan.localCommandOutput) {
                    if !output.isEmpty { answer = output }
                    continue
                }
            default:
                if let blocks = (object["message"] as? [String: Any])?["content"] as? [[String: Any]] {
                    let text = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined(separator: "\n")
                    if !text.isEmpty { answer = text }
                }
            }
            parser.receive(object: object)
        }
        if let answer { parser.receive(object: ["type": "result", "subtype": "success", "is_error": false, "result": answer]) }
        if interrupted { return "stopped" }
        return parser.failed ? "error" : "completed"
    }

    // MARK: Codex records

    static func codexModel(_ lines: [Data]) -> String? {
        for line in lines where HistoryScan.contains(line, #""type":"turn_context""#) {
            guard let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  let payload = object["payload"] as? [String: Any], let model = payload["model"] as? String,
                  CoreValidation.model(model) else { continue }
            return model
        }
        return nil
    }

    /// A rollout records `codex exec --json` items under other names; each is
    /// mapped back to the exec shape the parser reads.
    static func codexItem(_ item: [String: Any]) -> [String: Any]? {
        guard let type = item["type"] as? String, let id = item["id"] as? String else { return nil }
        let raw = item["status"] as? String
        let status = raw == nil || raw == "completed" ? "completed" : raw == "in_progress" ? "in_progress" : "failed"
        switch type {
        case "AgentMessage":
            let text = (item["content"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined(separator: "\n")
            return text.isEmpty ? nil : ["id": id, "type": "agent_message", "text": text]
        case "CommandExecution":
            let command: String
            if let parts = item["command"] as? [String] {
                command = parts.count == 3 && ["-lc", "-c"].contains(parts[1]) ? parts[2] : parts.joined(separator: " ")
            } else { command = item["command"] as? String ?? "" }
            var mapped: [String: Any] = ["id": id, "type": "command_execution", "command": command, "status": status]
            if let output = (item["aggregated_output"] ?? item["stdout"]) as? String { mapped["aggregated_output"] = output }
            if let code = item["exit_code"] { mapped["exit_code"] = code }
            return mapped
        case "FileChange":
            let changes = (item["changes"] as? [String: Any] ?? [:]).sorted { $0.key < $1.key }.prefix(64).map { path, change -> [String: Any] in
                ["path": path, "kind": (change as? [String: Any])?["type"] as? String ?? "update"]
            }
            return ["id": id, "type": "file_change", "changes": changes, "status": status]
        case "McpToolCall":
            var mapped = item; mapped["type"] = "mcp_tool_call"; mapped["status"] = status
            return mapped
        case "Extension":
            guard item["kind"] as? String == "web.search" else { return nil }
            return ["id": id, "type": "web_search", "query": item["query"] as? String ?? "", "status": status]
        case "CollabAgentToolCall":
            var mapped = item; mapped["type"] = "collab_tool_call"
            return mapped
        case "ContextCompaction":
            return ["id": id, "type": "context_compaction"]
        default: return nil
        }
    }

    private func codex(_ lines: [Data], thread: String, parser: CLIStreamParser) -> String {
        var usage: [String: Int] = [:]
        var completed = false, aborted = false
        parser.receive(object: ["type": "thread.started", "thread_id": thread])
        for line in lines {
            guard let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  let type = object["type"] as? String, let payload = object["payload"] as? [String: Any] else { continue }
            at(object["timestamp"])
            switch (type, payload["type"] as? String) {
            case ("event_msg", "task_started"): parser.receive(object: ["type": "turn.started"])
            case ("event_msg", "item_completed"):
                guard let item = payload["item"] as? [String: Any], let mapped = Self.codexItem(item) else { continue }
                parser.receive(object: ["type": "item.completed", "item": mapped])
            case ("token_usage_record", _):
                for (key, value) in payload["usage"] as? [String: Any] ?? [:] {
                    if let number = value as? Int, number >= 0 { usage[key, default: 0] += number }
                }
            case ("event_msg", "error"):
                if let message = payload["message"] as? String, !message.isEmpty { parser.receive(object: ["type": "error", "message": message]) }
            case ("event_msg", "task_complete"): completed = true
            case ("event_msg", "turn_aborted"): aborted = true
            default: continue
            }
        }
        if completed || !usage.isEmpty { parser.receive(object: ["type": "turn.completed", "usage": usage]) }
        if aborted { return "stopped" }
        if completed { return "completed" }
        return parser.failed ? "error" : "stopped"
    }
}

// MARK: - A pane's loaded history

/// What a pane loaded from its session record, kept in memory only. The rules
/// live here so they can be tested; the app only runs the file work off the
/// main actor and hands the result back.
public struct SessionHistoryState: Equatable, Sendable {
    public enum Phase: Equatable, Sendable {
        /// More can be loaded.
        case idle
        case loading
        /// The record's first request is on screen.
        case start
        /// No record, or nothing in it before what is already shown.
        case unavailable
        case failed
        /// The pane holds as many older requests as it keeps in memory.
        case limit
    }
    /// Older requests kept per pane, and the bytes they may hold together
    /// (measured as the live history measures its own); past either the
    /// oldest go and the pane stops loading.
    public static let maximumRuns = 100
    public static let maximumBytes = 8 * 1_048_576
    public private(set) var runs: [MightyGraphRun] = []
    public private(set) var phase: Phase = .idle
    public private(set) var file: SessionHistoryFile?
    public private(set) var end: Int?
    /// The retained request and session the loaded runs attach above. A pane
    /// that loaded before it sent anything has no retained request yet: its
    /// first one becomes the anchor.
    public private(set) var anchorRunID: String?
    public private(set) var resumeID: String?
    /// The run the diagram keeps where it first stood: the retained request
    /// the history first attached above. Runs a trim moves in from the
    /// retained list stack below it, so nothing on screen moves.
    public private(set) var pinnedRunID: String?
    /// Bumped on every reset so a load that started before it is discarded.
    public private(set) var generation = 0
    public init() {}

    public var canLoad: Bool { [.idle, .failed].contains(phase) }
    private var touched: Bool { !runs.isEmpty || end != nil || phase != .idle }
    /// Whether what was loaded still attaches above this retained request.
    public func connects(anchorRunID: String?, resumeID: String?) -> Bool {
        !touched || (resumeID == self.resumeID && (anchorRunID == self.anchorRunID || self.anchorRunID == nil))
    }

    /// History attaches above one retained request of one session. Once the
    /// pane resumes another session, or the retained list starts at a request
    /// the history never saw, what was loaded no longer connects. A pane
    /// without a retained request adopts its first one.
    public mutating func reconcile(anchorRunID: String?, resumeID: String?) {
        guard touched else { return }
        guard connects(anchorRunID: anchorRunID, resumeID: resumeID) else { reset(); return }
        adopt(anchorRunID)
    }
    private mutating func adopt(_ id: String?) {
        guard anchorRunID == nil, let id else { return }
        anchorRunID = id
        if pinnedRunID == nil { pinnedRunID = id }
    }
    public mutating func reset() {
        let next = generation + 1
        self = SessionHistoryState(); generation = next
    }

    /// The pane's retained list changed from `previous` to `current`. A trim
    /// that dropped the anchor moves the dropped runs here, newest last, and
    /// anchors on the new first one instead of throwing the history away.
    public mutating func follow(previous: [MightyGraphRun], current: [MightyGraphRun], resumeID: String?, provider: String) {
        guard touched, previous.first?.id != current.first?.id else { return }
        guard resumeID == self.resumeID else { reset(); return }
        guard let anchor = anchorRunID else { adopt(current.first?.id); return }
        // Everything from the anchor up to the new first run was trimmed; a
        // trim that also took the run before a new request takes them all.
        guard let start = previous.firstIndex(where: { $0.id == anchor }), let first = current.first else { reset(); return }
        let stop = previous.firstIndex(where: { $0.id == first.id }) ?? previous.count
        guard stop > start else { reset(); return }
        let known = Set(runs.map(\.id))
        let moved = previous[start..<stop].filter { !known.contains($0.id) }.map { original -> MightyGraphRun in
            var run = original; run.applyProvider(provider); return run
        }
        runs += moved
        anchorRunID = first.id
        if pinnedRunID == nil { pinnedRunID = anchor }
        bound()
    }

    /// Drops the oldest runs past the count and byte limits; the pane then
    /// stops loading, since the record cursor lies above what was dropped.
    private mutating func bound() {
        var bytes = MightyGraphSupport.liveHistoryBytes(runs)
        var dropped = false
        while !runs.isEmpty, runs.count > Self.maximumRuns || bytes > Self.maximumBytes {
            bytes -= MightyGraphSupport.liveHistoryBytes([runs.removeFirst()])
            dropped = true
        }
        if dropped || runs.count >= Self.maximumRuns { phase = .limit }
    }

    /// The request for the next chunk, or nil when nothing more can be loaded.
    /// Marks the state as loading.
    public mutating func begin(anchorRunID: String?, resumeID: String?, anchor: SessionHistoryAnchor?, base: SessionHistoryRequest) -> SessionHistoryRequest? {
        reconcile(anchorRunID: anchorRunID, resumeID: resumeID)
        guard canLoad else { return nil }
        guard resumeID != nil else { self.anchorRunID = anchorRunID; phase = .unavailable; return nil }
        if !touched { self.anchorRunID = anchorRunID; pinnedRunID = anchorRunID }
        self.resumeID = resumeID
        phase = .loading
        var request = base
        request.file = file; request.end = end
        request.anchor = end == nil ? anchor : nil
        return request
    }

    /// Takes a finished load; a result from before a reset is ignored.
    /// Returns true when the load should start again from scratch (the
    /// record was replaced under the cursor).
    @discardableResult
    public mutating func finish(_ result: Result<SessionHistoryChunk, Error>, generation: Int) -> Bool {
        guard generation == self.generation, phase == .loading else { return false }
        switch result {
        case .success(let chunk):
            let known = Set(runs.map(\.id))
            runs = chunk.runs.filter { !known.contains($0.id) } + runs
            file = chunk.file; end = chunk.end
            if chunk.reachedStart { phase = runs.isEmpty ? .unavailable : .start }
            else { phase = .idle }
            bound()
            return false
        case .failure(let error):
            switch error as? SessionHistoryError {
            case .changed?:
                reset()
                return true
            case .missing?, .unsupported?: phase = runs.isEmpty ? .unavailable : .start
            case .unreadable?, nil: phase = .failed
            }
            return false
        }
    }
}
