import Foundation
import Darwin

/// One Codex subagent as its own session record describes it. With multi-agent
/// v2, `codex exec --json` reports only a bare `wait` item, so the graph learns
/// child threads from `$CODEX_HOME/sessions` instead.
struct CodexSessionAgent: Equatable {
    let thread: String
    let parentThread: String
    var title: String?
    var input: String?
    /// Entries added or changed since the previous snapshot, oldest first.
    var entries: [LogEntry] = []
    /// IDs among `entries` that keep an earlier turn's final answer.
    var answers = Set<String>()
    var output: String?
    var state = "running"
    /// Turns the child started in this run; a later turn on a settled child is new work.
    var turns = 0
    /// Usage of responses not yet handed to the graph, oldest first.
    var usage: [GraphResponseRecord] = []
}

/// `sessions/YYYY/MM/DD/rollout-<local time>-<thread>.jsonl`, read without
/// following symlinks and only as regular files.
enum CodexSessionFiles {
    private static let name = #/^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<thread>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\.jsonl$/#

    static func thread(fileName: String) -> String? {
        (try? name.wholeMatch(in: fileName)).map { String($0.thread) }
    }
    private static func realDirectory(_ url: URL) -> Bool {
        var info = stat()
        return lstat(url.path, &info) == 0 && info.st_mode & S_IFMT == S_IFDIR
    }
    /// `$CODEX_HOME/sessions` with every link in its own path resolved once, so
    /// a linked sessions folder still works; nothing below it may be a link.
    static func sessions(codexHome: URL) -> URL? {
        guard let real = realpath(codexHome.appendingPathComponent("sessions", isDirectory: true).path, nil) else { return nil }
        defer { free(real) }
        let url = URL(fileURLWithPath: String(cString: real), isDirectory: true)
        return realDirectory(url) ? url : nil
    }
    /// Day folders from the day before `start` through `now`, newest first,
    /// at most `maximum`. Codex files a thread under the local date it was
    /// created, so this run's new children are in the start day or a later
    /// one (the day before covers a clock or zone change around midnight).
    /// A child created on an older day and reused by a resumed run is not
    /// looked for; long runs lose only folders whose children were already found.
    static func dayFolders(sessions: URL, start: Date, now: Date, maximum: Int = 3) -> [URL] {
        guard realDirectory(sessions) else { return [] }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = .current
        let last = calendar.startOfDay(for: max(start, now))
        guard var day = calendar.date(byAdding: .day, value: -1, to: calendar.startOfDay(for: start)) else { return [] }
        var days: [Date] = []
        while day <= last, days.count < 400 {
            days.append(day)
            guard let next = calendar.date(byAdding: .day, value: 1, to: day) else { break }
            day = next
        }
        return days.suffix(maximum).reversed().compactMap { day in
            let parts = calendar.dateComponents([.year, .month, .day], from: day)
            guard let year = parts.year, let month = parts.month, let date = parts.day else { return nil }
            var folder = sessions
            for component in [String(format: "%04d", year), String(format: "%02d", month), String(format: "%02d", date)] {
                folder.appendPathComponent(component, isDirectory: true)
                guard realDirectory(folder) else { return nil }
            }
            return folder
        }
    }
    /// Rollout files in one day folder that are regular files, not links.
    static func rollouts(in folder: URL) -> [(url: URL, thread: String, modified: Date)] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey]
        guard let urls = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        return urls.compactMap { url in
            guard let thread = thread(fileName: url.lastPathComponent), let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isSymbolicLink != true, values.isRegularFile == true, let modified = values.contentModificationDate else { return nil }
            return (url, thread, modified)
        }
    }
    /// Opens without following a final symlink and only a regular file.
    static func open(_ url: URL) -> Int32? {
        let fd = Darwin.open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { return nil }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { Darwin.close(fd); return nil }
        return fd
    }
    static func size(_ fd: Int32) -> Int? {
        var info = stat()
        return fstat(fd, &info) == 0 ? Int(info.st_size) : nil
    }
    enum FirstLine { case line(Data), incomplete, unusable }
    static func firstLine(_ url: URL, maximumBytes: Int) -> FirstLine {
        guard let fd = open(url) else { return .unusable }
        defer { Darwin.close(fd) }
        guard let size = size(fd) else { return .unusable }
        guard size > 0 else { return .incomplete }
        var bytes = [UInt8](repeating: 0, count: min(size, maximumBytes + 1))
        let count = bytes.withUnsafeMutableBytes { pread(fd, $0.baseAddress, $0.count, 0) }
        guard count > 0 else { return count == 0 ? .incomplete : .unusable }
        if let end = bytes[..<count].firstIndex(of: 10) { return .line(Data(bytes[..<end])) }
        return count > maximumBytes ? .unusable : .incomplete
    }
}

/// Byte-offset tail of a JSONL file. Only complete lines are consumed, so a
/// line still being written is read again once its newline arrives.
struct CodexRolloutTail {
    private(set) var offset: Int
    private var skipping = false
    let maximumLineBytes: Int

    init(offset: Int = 0, maximumLineBytes: Int) { self.offset = offset; self.maximumLineBytes = maximumLineBytes }

    /// Reads at most `maximumBytes`; an oversized line is skipped whole.
    /// Returns the complete lines and the bytes read, or nil when the file
    /// became shorter than what was already consumed.
    mutating func read(_ fd: Int32, maximumBytes: Int) -> (lines: [Data], bytes: Int)? {
        guard let size = CodexSessionFiles.size(fd) else { return ([], 0) }
        guard size >= offset else { return nil }
        let capacity = min(maximumLineBytes + 1, size - offset, maximumBytes)
        guard capacity > 0 else { return ([], 0) }
        let buffer = UnsafeMutableRawBufferPointer.allocate(byteCount: capacity, alignment: 1)
        defer { buffer.deallocate() }
        guard let base = buffer.baseAddress else { return ([], 0) }
        var lines: [Data] = []; var budget = maximumBytes
        while budget > 0 {
            let wanted = min(budget, capacity)
            let count = pread(fd, base, wanted, off_t(offset))
            guard count > 0 else { break }
            budget -= count
            func newline(from index: Int) -> Int? {
                guard index < count, let found = memchr(base + index, 10, count - index) else { return nil }
                return base.distance(to: found)
            }
            var start = 0
            if skipping {
                guard let end = newline(from: 0) else { offset += count; continue }
                start = end + 1; skipping = false
            }
            while let end = newline(from: start) {
                if end > start { lines.append(Data(bytes: base + start, count: end - start)) }
                start = end + 1
            }
            if count - start >= maximumLineBytes { skipping = true; start = count }
            offset += start
            // A short read ended at the file's current end; the rest is partial.
            if count < wanted || start == 0 { break }
        }
        return (lines, maximumBytes - budget)
    }
}

/// Folds one child thread's session records into a graph snapshot. Only this
/// run's work counts: nothing is kept before the first `task_started` stamped
/// at or after the run start, so a child reused by a resumed run does not
/// replay earlier turns, answers or token usage.
final class CodexSessionThread {
    struct Meta: Equatable {
        let thread: String
        let parent: String
        let nickname: String?
        let path: String?
        let startOrdinal: Int?
        let forked: Bool
    }
    private(set) var agent: CodexSessionAgent
    let meta: Meta
    private let namespace: String
    /// Run start less a small clock allowance, as a date and in Codex's
    /// fixed-width UTC form for a cheap comparison before parsing.
    private let since: Date
    private let sinceStamp: String
    /// Past the history a fork copied from its parent.
    private var own: Bool
    /// A fork without ordinals has only a guessed history boundary; it shows
    /// the state from `task_*` records and nothing else.
    private let stateOnly: Bool
    private(set) var active = false
    private var model: String?
    private var lastAnswer: String?
    private var failedTurn = false
    private var errors = 0
    private var responses = Set<String>()
    private var changed = Set<String>()
    private var dirty = true

    /// Only `spawn_agent` children: guardian reviews and other internal
    /// subagents carry no `thread_spawn` source and are not shown.
    static func meta(_ record: [String: Any]) -> Meta? {
        guard record["type"] as? String == "session_meta", let payload = record["payload"] as? [String: Any],
              let thread = CodexCollaborationItem.key(payload["id"]),
              let spawn = ((payload["source"] as? [String: Any])?["subagent"] as? [String: Any])?["thread_spawn"] as? [String: Any],
              let parent = CodexCollaborationItem.key(payload["parent_thread_id"]) ?? CodexCollaborationItem.key(spawn["parent_thread_id"]) else { return nil }
        return Meta(thread: thread, parent: parent,
                    nickname: CodexCollaborationItem.key(payload["agent_nickname"]) ?? CodexCollaborationItem.key(spawn["agent_nickname"]),
                    path: CodexCollaborationItem.key(payload["agent_path"]) ?? CodexCollaborationItem.key(spawn["agent_path"]),
                    startOrdinal: payload["subagent_history_start_ordinal"] as? Int, forked: payload["forked_from_id"] != nil)
    }

    init(meta: Meta, namespace: String, since: Date) {
        self.meta = meta; self.namespace = namespace
        self.since = since.addingTimeInterval(-1)
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        sinceStamp = formatter.string(from: self.since)
        // A forked child first replays its parent's history; skip that copy.
        stateOnly = meta.forked && meta.startOrdinal == nil
        own = !stateOnly
        let name = meta.path.flatMap { $0.split(separator: "/").last.map(String.init) }
        let title = [meta.nickname, name].compactMap { $0 }.joined(separator: " · ")
        agent = CodexSessionAgent(thread: meta.thread, parentThread: meta.parent,
                                  title: title.isEmpty ? nil : ActivitySupport.clean(title, maximumBytes: 160, singleLine: true))
    }

    /// The snapshot if anything changed since the last one; a thread that did
    /// no work in this run has none. Entries are only the new or changed ones.
    func take() -> CodexSessionAgent? {
        guard active, dirty else { return nil }
        dirty = false
        var snapshot = agent
        snapshot.entries = agent.entries.filter { changed.contains($0.id) }
        snapshot.answers = agent.answers.intersection(changed)
        changed.removeAll(); agent.usage = []
        return snapshot
    }
    /// The run ended: a turn that reported an error and never completed failed.
    func close() {
        if active, failedTurn, agent.state == "running" { set(\.state, "error") }
    }

    /// Codex writes `{"timestamp":"…","ordinal":N,` first. Inherited and
    /// pre-run lines are dropped on that prefix without parsing JSON.
    func wants(_ line: Data) -> Bool {
        let (stamp, ordinal) = Self.prefix(line)
        if let start = meta.startOrdinal, let ordinal, ordinal < start { return false }
        if !active, own, let stamp, stamp.utf8.count == sinceStamp.utf8.count, stamp.hasSuffix("Z"), stamp < sinceStamp { return false }
        return true
    }
    static func prefix(_ line: Data) -> (timestamp: String?, ordinal: Int?) {
        let head = [UInt8](line.prefix(96))
        let key = Array(#"{"timestamp":""#.utf8)
        guard head.starts(with: key), let quote = head[key.count...].firstIndex(of: 34) else { return (nil, nil) }
        let stamp = String(decoding: head[key.count..<quote], as: UTF8.self)
        let ordinalKey = Array(#","ordinal":"#.utf8)
        guard head[(quote + 1)...].starts(with: ordinalKey) else { return (stamp, nil) }
        let digits = head[(quote + 1 + ordinalKey.count)...].prefix { (48...57).contains($0) }
        guard (1...12).contains(digits.count), digits.endIndex < head.count else { return (stamp, nil) }
        return (stamp, digits.reduce(0) { $0 * 10 + Int($1 - 48) })
    }

    private static func text(_ content: Any?) -> String? {
        let parts = (content as? [[String: Any]] ?? []).prefix(32).compactMap { part -> String? in
            guard ["input_text", "output_text", "text", "Text"].contains(part["type"] as? String ?? "") else { return nil }
            return part["text"] as? String
        }
        let text = parts.joined(separator: "\n")
        // Collaboration payloads may be sealed; never show ciphertext.
        guard !text.isEmpty, !text.hasPrefix("gAAAA") else { return nil }
        return text
    }
    /// Multi-agent v2 seals a message's body and leaves only a short labelled
    /// header readable: "Label: value" lines whose last label has no value
    /// (the sealed body follows it). It carries no task text, so it is
    /// neither the block's request nor a follow-up.
    static func envelopeHeader(_ text: String) -> Bool {
        var lines = text.split(separator: "\n", omittingEmptySubsequences: false)
        if lines.last?.isEmpty == true { lines.removeLast() }
        guard (2...12).contains(lines.count), let last = lines.last?.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false),
              last.count == 2, last[1].allSatisfy(\.isWhitespace) else { return false }
        return lines.allSatisfy { line in
            guard let colon = line.firstIndex(of: ":") else { return false }
            let label = line[..<colon]
            return (1...32).contains(label.count) && label.first?.isUppercase == true && label.allSatisfy { $0.isLetter || $0 == " " }
        }
    }
    private static func timestamp(_ record: [String: Any]) -> String {
        if let value = record["timestamp"] as? String, value.utf8.count <= 80, AgentRunTiming.parseTimestamp(value) != nil { return value }
        return mightyTimestamp()
    }
    private func upsert(_ entry: LogEntry) {
        if let index = agent.entries.firstIndex(where: { $0.id == entry.id }) {
            var replacement = entry; replacement.timestamp = agent.entries[index].timestamp
            guard agent.entries[index] != replacement else { return }
            agent.entries[index] = replacement
        } else {
            agent.entries.append(entry)
            if agent.entries.count > ExecutionGraphSupport.maximumEntries {
                for removed in agent.entries.prefix(agent.entries.count - ExecutionGraphSupport.maximumEntries) { changed.remove(removed.id); agent.answers.remove(removed.id) }
                agent.entries.removeFirst(agent.entries.count - ExecutionGraphSupport.maximumEntries)
            }
        }
        changed.insert(entry.id); dirty = true
    }
    private func set<T: Equatable>(_ path: WritableKeyPath<CodexSessionAgent, T>, _ value: T) {
        guard agent[keyPath: path] != value else { return }
        agent[keyPath: path] = value; dirty = true
    }
    private func entryID(_ key: String) -> String { ExecutionGraphSupport.identifier(namespace, "codex-session:" + meta.thread + ":" + key) }

    func consume(_ record: [String: Any]) {
        guard let type = record["type"] as? String, let payload = record["payload"] as? [String: Any] else { return }
        let kind = payload["type"] as? String
        if let start = meta.startOrdinal {
            guard let ordinal = record["ordinal"] as? Int, ordinal >= start else { return }
        } else if !own {
            // Older forks carry no ordinals: the child's own settings open its history.
            let addressed = type == "response_item" && kind == "agent_message" && meta.path != nil && payload["recipient"] as? String == meta.path
            guard (type == "event_msg" && kind == "thread_settings_applied") || addressed else { return }
            own = true
        }
        if !active {
            guard type == "event_msg", kind == "task_started", let stamp = record["timestamp"] as? String,
                  let date = AgentRunTiming.parseTimestamp(stamp), date >= since else { return }
            active = true
        }
        if stateOnly, type != "event_msg" || !["task_started", "task_complete", "turn_aborted"].contains(kind ?? "") { return }
        switch (type, kind) {
        case ("turn_context", _):
            model = CodexCollaborationItem.key(payload["model"]).map { ActivitySupport.clean($0, maximumBytes: 160, singleLine: true) }
        case ("token_usage_record", _):
            guard let response = CodexCollaborationItem.key(payload["response_id"]), !responses.contains(response),
                  let usage = ExecutionGraphTracker.codexUsage(payload["usage"]), responses.count < 4_096 else { return }
            responses.insert(response)
            agent.usage.append(GraphResponseRecord(responseId: response, model: model, usage: usage)); dirty = true
        case ("event_msg", "task_started"):
            // The previous turn's answer stays in the log, even when both
            // turns arrive in one read.
            if let output = agent.output, !output.isEmpty {
                let id = entryID("answer:\(agent.turns)")
                upsert(LogEntry(id: id, kind: "assistant", text: output, timestamp: Self.timestamp(record), provider: "codex"))
                agent.answers.insert(id)
            }
            set(\.turns, agent.turns + 1); set(\.state, "running"); set(\.output, nil); lastAnswer = nil; failedTurn = false
        case ("event_msg", "task_complete"):
            // The turn finished; an error reported during it does not stick.
            failedTurn = false
            let last = (payload["last_agent_message"] as? String).map { ActivitySupport.clean($0, maximumBytes: ExecutionGraphSupport.maximumOutputBytes) }
            if !stateOnly { set(\.output, last.flatMap { $0.isEmpty ? nil : $0 } ?? lastAnswer) }
            set(\.state, "completed")
        case ("event_msg", "turn_aborted"):
            failedTurn = false
            set(\.state, "stopped")
        case ("event_msg", "error"):
            guard let message = payload["message"] as? String, !message.isEmpty else { return }
            failedTurn = true; errors += 1
            upsert(LogEntry(id: entryID("error:\(record["ordinal"] as? Int ?? errors)"), kind: "error", text: ActivitySupport.clean(message, maximumBytes: ExecutionGraphSupport.maximumOutputBytes), timestamp: Self.timestamp(record), provider: "codex"))
        case ("event_msg", "item_completed"):
            if let item = payload["item"] as? [String: Any] { consume(item: item, payload: payload, record: record) }
        case ("response_item", "agent_message"):
            // A task or follow-up addressed to this child by its path.
            guard meta.path != nil, payload["recipient"] as? String == meta.path, let text = Self.text(payload["content"]), !Self.envelopeHeader(text) else { return }
            let clean = ActivitySupport.clean(text, maximumBytes: ExecutionGraphSupport.maximumInputBytes)
            if agent.input == nil { set(\.input, clean) }
            else if let id = CodexCollaborationItem.key(payload["id"]) {
                upsert(LogEntry(id: entryID("input:" + id), kind: "user", text: clean, timestamp: Self.timestamp(record), provider: "codex"))
            }
        case ("response_item", "message"):
            // Older children receive the task as a plain user message.
            guard agent.input == nil, payload["role"] as? String == "user",
                  (record["metadata"] as? [String: Any])?["inherited_user_message"] as? Bool != true else { return }
            let kinds = (payload["internal_chat_message_metadata_passthrough"] as? [String: Any])?["content_item_kinds"] as? [String]
            guard kinds.map({ !$0.isEmpty && $0.allSatisfy { $0 == "user.text" } }) ?? true,
                  let text = Self.text(payload["content"]), !text.hasPrefix("<") else { return }
            set(\.input, ActivitySupport.clean(text, maximumBytes: ExecutionGraphSupport.maximumInputBytes))
        default: break
        }
    }
    private func consume(item: [String: Any], payload: [String: Any], record: [String: Any]) {
        guard let id = CodexCollaborationItem.key(item["id"]), let type = item["type"] as? String else { return }
        if type == "AgentMessage" {
            guard let text = Self.text(item["content"]) else { return }
            let clean = ActivitySupport.clean(text, maximumBytes: ExecutionGraphSupport.maximumOutputBytes)
            // The final answer is the block's output; the graph shows it once.
            if item["phase"] as? String == "final_answer" { lastAnswer = clean; set(\.output, clean); return }
            upsert(LogEntry(id: entryID("message:" + id), kind: "assistant", text: clean, timestamp: Self.timestamp(record), provider: "codex"))
            return
        }
        let status = item["status"] as? String
        var failed = ["failed", "declined"].contains(status ?? "")
        let tool: String; var input: Any? = nil; var output: String? = nil; var summary: String? = nil
        switch type {
        case "CommandExecution":
            tool = "command_execution"; input = ["command": item["command"] as Any]
            output = item["aggregated_output"] as? String
        case "FileChange":
            tool = "file_change"
            let changes = (item["changes"] as? [String: Any] ?? [:]).sorted { $0.key < $1.key }.prefix(12).map { path, change -> [String: Any] in
                ["path": path, "kind": (change as? [String: Any])?["type"] as? String ?? "update"]
            }
            input = ["changes": changes]
        case "McpToolCall":
            let name = [item["server"] as? String, item["tool"] as? String].compactMap { $0 }.joined(separator: ".")
            tool = name.isEmpty ? "MCP" : name; input = item["arguments"]
            let error = item["error"].flatMap { $0 is NSNull ? nil : $0 }
            output = ActivitySupport.output(error ?? item["result"])
            if (item["result"] as? [String: Any])?["isError"] as? Bool == true || error != nil { failed = true }
        case "Extension":
            let kind = item["kind"] as? String
            tool = kind == "web.search" ? "web_search" : kind.map { ActivitySupport.clean($0, maximumBytes: 80, singleLine: true) } ?? "Extension"
            input = item
        case "ImageView":
            tool = "view_image"; input = ["path": item["path"] as Any]
        case "CollabAgentToolCall":
            var copy = item; copy["type"] = "collab_tool_call"
            guard let call = CodexCollaborationItem(copy) else { return }
            tool = call.tool; summary = call.summary; output = call.output
        default: return
        }
        var duration: Double?
        if let started = payload["started_at_ms"] as? Int, let completed = payload["completed_at_ms"] as? Int, completed >= started { duration = Double(completed - started) }
        let state = status == "in_progress" ? "running" : failed ? "error" : "completed"
        guard let activity = ActivitySupport.normalized(AgentActivity(id: ActivitySupport.id(namespace: namespace, key: "codex-session:" + meta.thread + ":" + id),
                provider: "codex", kind: ActivitySupport.kind(tool: tool), state: state, toolName: tool,
                summary: summary ?? ActivitySupport.summary(tool: tool, input: input), output: output, durationMs: duration)) else { return }
        upsert(LogEntry(id: activity.id, kind: "system", text: activity.summary, timestamp: Self.timestamp(record), provider: "codex", activity: activity))
    }
}

/// Discovers and tails a Codex run's subagent session files. The runner's
/// poll task reads off its actor, one call at a time; `finish` runs only
/// after that task has ended. Every read is bounded so a poll each second
/// stays cheap.
final class CodexSessionWatcher: @unchecked Sendable {
    struct Limits {
        /// Bytes read per poll and for the final read, across all children.
        var bytesPerPoll = 4 * 1024 * 1024
        var finalBytes = 32 * 1024 * 1024
        var maximumLineBytes = 2 * 1024 * 1024
        var maximumFirstLineBytes = 1024 * 1024
        var firstLinesPerPoll = 64
        var maximumChildren = ExecutionGraphSupport.maximumNodes
        /// Files whose parent is not known yet, such as children of another
        /// session running at the same time; the oldest is dropped first.
        var maximumWaiting = 512
    }
    private struct Child {
        let url: URL
        var tail: CodexRolloutTail
        let thread: CodexSessionThread
        /// The file shrank; rollouts only grow, so it is not read again.
        var closed = false
    }
    private let codexHome: URL
    private var sessions: URL?
    private let root: String
    private let startedAt: Date
    private let namespace: String
    private let now: () -> Date
    private let limits: Limits
    private var children: [Child] = []
    private var next = 0
    private var known = Set<String>()
    private var waiting: [String: (url: URL, parent: String)] = [:]
    private var waitingOrder: [String] = []
    private var skipped = Set<String>()
    private(set) var firstLineReads = 0
    private(set) var stopped = false

    init(codexHome: URL, rootThread: String, startedAt: Date, namespace: String, now: @escaping () -> Date = Date.init, limits: Limits = Limits()) {
        self.codexHome = codexHome
        root = rootThread.lowercased(); self.startedAt = startedAt; self.namespace = namespace; self.now = now; self.limits = limits
    }

    /// Changed children while the run is active.
    func poll() -> [CodexSessionAgent] { stopped ? [] : read(budget: limits.bytesPerPoll, closing: false) }
    /// One last, larger read after the run ended; later polls read nothing.
    func finish() -> [CodexSessionAgent] {
        guard !stopped else { return [] }
        defer { stopped = true }
        return read(budget: limits.finalBytes, closing: true)
    }

    /// Polls after each `sleep` until `poll` reports the run is over.
    static func drive(sleep: () async throws -> Void, poll: () async -> Bool) async {
        while !Task.isCancelled {
            do { try await sleep() } catch { return }
            guard await poll() else { return }
        }
    }

    private func read(budget: Int, closing: Bool) -> [CodexSessionAgent] {
        discover()
        var remaining = budget
        // Start one child later each time so a busy child cannot starve the rest.
        let first = children.isEmpty ? 0 : next % children.count
        next = first + 1
        for step in children.indices where remaining > 0 {
            let index = (first + step) % children.count
            guard !children[index].closed, let fd = CodexSessionFiles.open(children[index].url) else { continue }
            let result = children[index].tail.read(fd, maximumBytes: remaining)
            Darwin.close(fd)
            guard let result else { children[index].closed = true; continue }
            remaining -= result.bytes
            for line in result.lines where children[index].thread.wants(line) {
                guard let record = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { continue }
                children[index].thread.consume(record)
            }
        }
        if closing { for child in children { child.thread.close() } }
        return children.compactMap { $0.thread.take() }
    }

    private enum FirstRecord { case meta(CodexSessionThread.Meta, offset: Int), incomplete, unusable }
    private func firstRecord(_ url: URL, thread: String) -> FirstRecord {
        firstLineReads += 1
        switch CodexSessionFiles.firstLine(url, maximumBytes: limits.maximumFirstLineBytes) {
        case .incomplete: return .incomplete
        case .unusable: return .unusable
        case .line(let data):
            guard let record = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
                  let meta = CodexSessionThread.meta(record), meta.thread.lowercased() == thread else { return .unusable }
            return .meta(meta, offset: data.count + 1)
        }
    }

    private func discover() {
        if sessions == nil { sessions = CodexSessionFiles.sessions(codexHome: codexHome) }
        guard let sessions else { return }
        var reads = 0
        for folder in CodexSessionFiles.dayFolders(sessions: sessions, start: startedAt, now: now()) {
            for file in CodexSessionFiles.rollouts(in: folder) {
                let name = file.url.lastPathComponent
                let thread = file.thread.lowercased()
                // Only files written since the run started can hold its work.
                guard thread != root, !known.contains(thread), waiting[name] == nil, !skipped.contains(name), file.modified >= startedAt else { continue }
                guard reads < limits.firstLinesPerPoll else { return }
                reads += 1
                switch firstRecord(file.url, thread: thread) {
                case .incomplete: continue
                case .unusable: skip(name)
                case .meta(let meta, let offset):
                    let parent = meta.parent.lowercased()
                    if parent == root || known.contains(parent) { adopt(name, url: file.url, meta: meta, offset: offset) }
                    else { wait(name, url: file.url, parent: parent) }
                }
            }
        }
    }
    /// Adopts a child, then any waiting files whose parent it is.
    private func adopt(_ name: String, url: URL, meta: CodexSessionThread.Meta, offset: Int) {
        var queue = [(name, url, meta, offset)]
        while let (name, url, meta, offset) = queue.popLast() {
            let thread = meta.thread.lowercased()
            guard children.count < limits.maximumChildren else { skip(name); continue }
            guard known.insert(thread).inserted else { continue }
            children.append(Child(url: url, tail: CodexRolloutTail(offset: offset, maximumLineBytes: limits.maximumLineBytes),
                                  thread: CodexSessionThread(meta: meta, namespace: namespace, since: startedAt)))
            for (waitingName, entry) in waiting.filter({ $0.value.parent == thread }).sorted(by: { $0.key < $1.key }) {
                waiting.removeValue(forKey: waitingName); waitingOrder.removeAll { $0 == waitingName }
                guard case .meta(let childMeta, let childOffset) = firstRecord(entry.url, thread: CodexSessionFiles.thread(fileName: waitingName)?.lowercased() ?? ""),
                      childMeta.parent.lowercased() == thread else { skip(waitingName); continue }
                queue.append((waitingName, entry.url, childMeta, childOffset))
            }
        }
    }
    private func wait(_ name: String, url: URL, parent: String) {
        if waiting.count >= limits.maximumWaiting, !waitingOrder.isEmpty {
            let oldest = waitingOrder.removeFirst()
            waiting.removeValue(forKey: oldest); skip(oldest)
        }
        waiting[name] = (url, parent); waitingOrder.append(name)
    }
    private func skip(_ name: String) {
        if skipped.count >= 8_192 { skipped.removeAll() }
        skipped.insert(name)
    }
}
