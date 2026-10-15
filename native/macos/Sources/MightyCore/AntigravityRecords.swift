import Darwin
import Foundation
import SQLite3

/// A sub-agent an Antigravity run started: the stream's `subagent_info`
/// (`invoke_subagent`) or a nested one its parent's own transcript names.
struct AntigravitySubagent: Equatable {
    let conversation: String
    /// The conversation that started it: the run's own, or another sub-agent's.
    let parent: String
    var title: String?
    var prompt: String?
}

/// What Antigravity CLI (`agy`) keeps on disk, read-only. Each conversation
/// has `~/.gemini/antigravity-cli/brain/<conversation id>/.system_generated/logs/transcript_full.jsonl`:
/// one line per step (`step_index`, `source`, `type` USER_INPUT / PLANNER_RESPONSE /
/// GENERIC / SYSTEM_MESSAGE, `status`, `created_at`, `content`, `tool_calls`,
/// token counts, `error`). A PLANNER_RESPONSE's tool calls are answered by
/// the GENERIC steps that follow it, in order. A sub-agent is a conversation
/// of its own whose instructions arrive as SYSTEM_MESSAGE steps from its parent.
/// `conversation_summaries.db` lists the conversations with their workspaces
/// and, for sub-agents, their parent.
enum AntigravityRecords {
    static func folder(home: URL) -> URL { home.appendingPathComponent(".gemini/antigravity-cli", isDirectory: true) }
    static func brain(home: URL) -> URL { folder(home: home).appendingPathComponent("brain", isDirectory: true) }
    static func database(home: URL) -> URL { folder(home: home).appendingPathComponent("conversation_summaries.db") }
    private static let uuid = #/^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/#
    static func conversationID(_ value: Any?) -> String? {
        guard let value = value as? String, (try? uuid.wholeMatch(in: value)) != nil else { return nil }
        return value
    }
    /// nil for anything that is not a conversation id, so no path leaves the brain folder.
    static func transcript(brain: URL, conversation: String) -> URL? {
        guard conversationID(conversation) != nil else { return nil }
        return brain.appendingPathComponent(conversation, isDirectory: true).appendingPathComponent(".system_generated/logs/transcript_full.jsonl")
    }
    /// The brain folder a transcript lies in.
    static func brain(transcript: URL) -> URL {
        transcript.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    /// What the user typed: the `<USER_REQUEST>` part of a USER_INPUT step.
    static func request(_ content: Any?) -> String? {
        guard let content = content as? String else { return nil }
        var text = Substring(content)
        if let start = content.range(of: "<USER_REQUEST>") {
            let end = content.range(of: "</USER_REQUEST>", range: start.upperBound..<content.endIndex)?.lowerBound ?? content.endIndex
            text = content[start.upperBound..<end]
        }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
    /// The request a USER_INPUT line opens, with when it was written. Lines
    /// without the word are rejected on their raw bytes first; however agy
    /// spaces its JSON, a line that has it is parsed.
    static func opening(_ line: Data) -> (prompt: String, timestamp: String?, key: String?)? {
        guard line.range(of: Data("USER_INPUT".utf8)) != nil, let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              object["type"] as? String == "USER_INPUT", object["source"] as? String != "SYSTEM", let prompt = request(object["content"]) else { return nil }
        return (prompt, object["created_at"] as? String, (object["step_index"] as? Int).map { "step\($0)" })
    }
    /// The text of a `[Message] … sender=<id> … content=…` system message from `sender`.
    static func message(_ content: Any?, from sender: String) -> String? {
        guard let content = content as? String, let start = content.range(of: "sender=" + sender + " ", options: .caseInsensitive),
              let body = content.range(of: " content=", range: start.upperBound..<content.endIndex) else { return nil }
        var text = content[body.upperBound...]
        if let end = text.range(of: "</SYSTEM_MESSAGE>", options: .backwards) { text = text[..<end.lowerBound] }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
    /// A tool step's text without agy's "Created At:" / "Completed At:" header.
    static func toolText(_ content: Any?) -> String? {
        guard let content = content as? String else { return nil }
        var lines = content.prefix(65_536).split(separator: "\n", omittingEmptySubsequences: false)
        while let first = lines.first, first.hasPrefix("Created At: ") || first.hasPrefix("Completed At: ") || first.allSatisfy(\.isWhitespace) { lines.removeFirst() }
        let text = lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? nil : text
    }
    /// A step's own status: DONE (or none) went through, ERROR failed, and
    /// anything else (CANCELED, INTERRUPTED, …) was cut short.
    enum Outcome { case done, failed, stopped }
    static func outcome(_ value: Any?) -> Outcome {
        switch (value as? String)?.uppercased() ?? "DONE" {
        case "DONE": return .done
        case "ERROR": return .failed
        default: return .stopped
        }
    }
    /// The stream state the live parser reads for that status.
    static func streamState(_ outcome: Outcome) -> String {
        switch outcome { case .done: return "DONE"; case .failed: return "ERROR"; case .stopped: return "CANCELED" }
    }
    /// When a step was written, nil when it does not say.
    static func date(_ object: [String: Any]) -> Date? { (object["created_at"] as? String).flatMap(AgentRunTiming.parseTimestamp) }
    static func errorText(_ value: Any?) -> String? {
        let text = (value as? String) ?? ((value as? [String: Any])?["message"] as? String)
        return text.flatMap { $0.isEmpty ? nil : $0 }
    }
    /// "File Reader (research)", as agy names a sub-agent's messages.
    static func title(role: Any?, typeName: Any?) -> String? {
        let role = (role as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "", type = (typeName as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if role.isEmpty { return type.isEmpty ? nil : type }
        return type.isEmpty || type == role ? role : role + " (" + type + ")"
    }
    /// A recorded tool call's arguments as the live stream's parameters (no
    /// `toolAction` / `toolSummary`), and its one-line summary.
    static func parameters(_ args: [String: Any]) -> [String: Any] { args.filter { !["toolAction", "toolSummary"].contains($0.key) } }
    static func summary(tool: String, args: [String: Any]) -> String {
        let summary = ActivitySupport.summary(tool: tool, input: CLIStreamParser.antigravityParameters(parameters(args)))
        if summary == tool, let described = args["toolSummary"] as? String, !described.isEmpty { return ActivitySupport.clean(described, maximumBytes: ActivitySupport.maximumSummaryBytes, singleLine: true) }
        return summary
    }
    /// The conversation ids an `invoke_subagent` result step names.
    static func spawnedConversations(_ content: Any?) -> [String] {
        guard let content = content as? String else { return [] }
        return content.prefix(65_536).matches(of: #/"conversationId":\s*"(?<id>[0-9A-Fa-f-]{36})"/#).compactMap { conversationID(String($0.id)) }
    }
    static func usage(_ value: Any?, keys: (input: String, output: String, cache: String) = ("input_tokens", "output_tokens", "cache_read_tokens")) -> GraphTokenUsage? {
        guard let object = value as? [String: Any] else { return nil }
        var mapped: [String: Any] = [:]
        for (source, target) in [(keys.input, "input_tokens"), (keys.output, "output_tokens"), (keys.cache, "cache_read_input_tokens")] { if let number = object[source] { mapped[target] = number } }
        return GraphTokenUsage.parse(mapped)
    }

    // MARK: Replaying a recorded request

    /// One recorded request's steps (after its USER_INPUT) as the stream-json
    /// events agy printed for them, so a replay goes through the live parser.
    /// The request completed when its last step is an answer without tool
    /// calls, failed when that answer failed, and was cut short when it was
    /// cancelled or ended on a tool step (one that was declined, say: the
    /// failed tool shows as such).
    static func streamEvents(_ steps: [[String: Any]], conversation: String) -> (events: [[String: Any]], status: String) {
        func step(_ fields: [String: Any]) -> [String: Any] { ["event": "step_update", "step_update": fields] }
        var events: [[String: Any]] = [["event": "init", "conversation_id": conversation, "init": [String: Any]()]]
        var calls: [(name: String, args: [String: Any])] = []
        var unanswered = 0
        func open(_ name: String, _ args: [String: Any]) -> [String: Any] {
            unanswered -= 1
            return step(["step_index": unanswered, "state": "ACTIVE", "step_type": "tool", "tool_name": name, "tool_info": ["name": name, "parameters": parameters(args)]])
        }
        var last: [String: Any]?
        for object in steps {
            guard let type = object["type"] as? String, let index = object["step_index"] as? Int else { continue }
            let outcome = outcome(object["status"]), state = streamState(outcome)
            switch type {
            case "PLANNER_RESPONSE":
                // Calls the model moved past without a result step stay open.
                events += calls.map { open($0.name, $0.args) }; calls.removeAll()
                var fields: [String: Any] = ["step_index": index, "state": state, "step_type": "agent_response"]
                if let text = object["content"] as? String, !text.isEmpty { fields["text_delta"] = text }
                var usage: [String: Any] = [:]
                for key in ["input_tokens", "output_tokens", "cache_read_tokens"] { if let number = object[key] { usage[key] = number } }
                if !usage.isEmpty { fields["usage"] = usage }
                events.append(step(fields))
                for call in (object["tool_calls"] as? [[String: Any]] ?? []).prefix(64) {
                    guard let name = call["name"] as? String, !name.isEmpty else { continue }
                    calls.append((name, call["args"] as? [String: Any] ?? [:]))
                }
            case "GENERIC":
                guard !calls.isEmpty else { continue }
                let call = calls.removeFirst()
                if call.name == "invoke_subagent" {
                    let specs = call.args["Subagents"] as? [[String: Any]] ?? []
                    let subagents = spawnedConversations(object["content"]).enumerated().map { offset, id -> [String: Any] in
                        let spec = offset < specs.count ? specs[offset] : [:]
                        return ["conversation_id": id, "role": spec["Role"] ?? "", "type_name": spec["TypeName"] ?? "", "initial_prompt": spec["Prompt"] ?? ""]
                    }
                    events.append(step(["step_index": index, "state": state, "step_type": "subagent", "tool_name": call.name, "subagent_info": ["subagents": subagents]]))
                } else {
                    var info: [String: Any] = ["name": call.name, "parameters": parameters(call.args)]
                    if outcome == .failed { info["error"] = ["message": errorText(object["error"]) ?? toolText(object["content"]) ?? ""] }
                    // Command output is shown as the live run shows it; other results (file contents) are not copied.
                    else if outcome == .done, ActivitySupport.kind(tool: call.name) == "command", let text = toolText(object["content"]) { info["output"] = text }
                    events.append(step(["step_index": index, "state": state, "step_type": "tool", "tool_name": call.name, "tool_info": info]))
                }
            default: continue
            }
            last = object
        }
        events += calls.map { open($0.name, $0.args) }
        guard let last, last["type"] as? String == "PLANNER_RESPONSE" else { return (events, "stopped") }
        switch outcome(last["status"]) {
        case .stopped: return (events, "stopped")
        case .failed:
            events.append(["event": "result", "result": ["conversation_id": conversation, "status": "ERROR", "error": ["message": errorText(last["error"]) ?? L("run.error.providerRunFailed", ["name": ProviderOptions.label("gemini")])]]])
            return (events, "error")
        case .done: break
        }
        guard (last["tool_calls"] as? [Any] ?? []).isEmpty else { return (events, "stopped") }
        events.append(["event": "result", "result": ["conversation_id": conversation, "status": "SUCCESS"]])
        return (events, "completed")
    }

    // MARK: Conversation list

    struct Summary: Equatable {
        var conversation: String
        var title: String
        /// Folders the conversation works in (the workspace and any `--add-dir`).
        var paths: [String]
        /// Set for a sub-agent's conversation.
        var parent: String
    }

    /// Every conversation `conversation_summaries.db` lists, read-only. While
    /// agy runs the database is in WAL mode and is read through its shared
    /// memory; when agy is not running the file alone is read (`immutable`).
    /// nil when the database is missing or unreadable.
    static func summaries(database: URL, limit: Int = 20_000) -> [Summary]? {
        guard FileManager.default.isReadableFile(atPath: database.path) else { return nil }
        for mode in ["mode=ro", "immutable=1"] {
            var components = URLComponents(); components.scheme = "file"; components.path = database.path; components.query = mode
            guard let uri = components.string else { return nil }
            var handle: OpaquePointer?
            guard sqlite3_open_v2(uri, &handle, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK, let db = handle else { sqlite3_close(handle); continue }
            defer { sqlite3_close(db) }
            sqlite3_busy_timeout(db, 250)
            var statement: OpaquePointer?
            let query = "SELECT conversation_id, title, workspace_uris, parent_conversation_id FROM conversation_summaries ORDER BY rowid DESC LIMIT \(max(0, limit))"
            guard sqlite3_prepare_v2(db, query, -1, &statement, nil) == SQLITE_OK, let statement else { sqlite3_finalize(statement); continue }
            defer { sqlite3_finalize(statement) }
            func column(_ index: Int32) -> String { sqlite3_column_text(statement, index).map { String(cString: $0) } ?? "" }
            var rows: [Summary] = []
            var result = sqlite3_step(statement)
            while result == SQLITE_ROW {
                if let id = conversationID(column(0)) {
                    let uris = (try? JSONSerialization.jsonObject(with: Data(column(2).utf8))) as? [String] ?? []
                    rows.append(Summary(conversation: id, title: column(1), paths: uris.prefix(32).compactMap { URL(string: $0).flatMap { $0.isFileURL ? $0.path : nil } }, parent: column(3)))
                }
                result = sqlite3_step(statement)
            }
            if result == SQLITE_DONE { return rows }
        }
        return nil
    }
}

/// One Antigravity sub-agent's own conversation, read as it grows: its
/// parent's instructions, its messages and tool calls, what it sent back
/// (`send_message`) and its usage. Snapshots carry only the entries changed
/// since the previous one, as Codex's session records do. Tool results are
/// shown for commands and failures only; file contents are never copied. A
/// sub-agent that started helpers of its own is not done while any of them
/// has not reported back: their messages continue its work.
final class AntigravityChildRecord {
    let subagent: AntigravitySubagent
    private let namespace: String
    private var agent: CodexSessionAgent
    private var changed = Set<String>()
    private var dirty = false
    /// Tool calls waiting for their result step, oldest first.
    private var calls: [(entry: String, name: String, args: [String: Any])] = []
    private var answer: String?
    private var message: String?
    /// Sub-agents this one started that were not handed out yet.
    private var spawned: [AntigravitySubagent] = []
    /// Sub-agents this one started, and those that have not reported back.
    private var started: [String] = []
    private var waiting = Set<String>()

    init(_ subagent: AntigravitySubagent, namespace: String) {
        self.subagent = subagent; self.namespace = namespace
        agent = CodexSessionAgent(thread: subagent.conversation, parentThread: subagent.parent, title: subagent.title, input: subagent.prompt)
    }

    func takeSpawned() -> [AntigravitySubagent] { defer { spawned.removeAll() }; return spawned }
    func take() -> CodexSessionAgent? {
        guard dirty else { return nil }
        dirty = false
        var snapshot = agent
        snapshot.entries = agent.entries.filter { changed.contains($0.id) }
        changed.removeAll(); agent.usage = []
        return snapshot
    }

    private func key(_ suffix: String) -> String { ActivitySupport.id(namespace: namespace, key: "agy:" + subagent.conversation + ":" + suffix) }
    private func upsert(_ entry: LogEntry) {
        if let index = agent.entries.firstIndex(where: { $0.id == entry.id }) {
            var replacement = entry; replacement.timestamp = agent.entries[index].timestamp
            guard agent.entries[index] != replacement else { return }
            agent.entries[index] = replacement
        } else {
            agent.entries.append(entry)
            if agent.entries.count > ExecutionGraphSupport.maximumEntries {
                for removed in agent.entries.prefix(agent.entries.count - ExecutionGraphSupport.maximumEntries) { changed.remove(removed.id) }
                agent.entries.removeFirst(agent.entries.count - ExecutionGraphSupport.maximumEntries)
            }
        }
        changed.insert(entry.id); dirty = true
    }
    private func activity(_ id: String, tool: String, args: [String: Any], state: String, output: String? = nil, stamp: String) {
        guard let value = ActivitySupport.normalized(AgentActivity(id: id, provider: "gemini", kind: ActivitySupport.kind(tool: tool), state: state, toolName: tool,
                                                                    summary: AntigravityRecords.summary(tool: tool, args: args), output: output)) else { return }
        upsert(LogEntry(id: id, kind: "system", text: value.summary, timestamp: stamp, provider: "gemini", activity: value))
    }

    func consume(_ object: [String: Any]) {
        guard let type = object["type"] as? String, let index = object["step_index"] as? Int else { return }
        let stamp = (object["created_at"] as? String).flatMap { AgentRunTiming.parseTimestamp($0) != nil ? $0 : nil } ?? mightyTimestamp()
        let outcome = AntigravityRecords.outcome(object["status"])
        let previous = (agent.state, agent.output, agent.turns, agent.input)
        switch type {
        case "SYSTEM_MESSAGE" where AntigravityRecords.message(object["content"], from: subagent.parent) == nil:
            // A helper it started reported back: the same assignment goes on.
            guard let helper = started.first(where: { AntigravityRecords.message(object["content"], from: $0) != nil }) else { return }
            waiting.remove(helper.lowercased())
            if agent.state == "completed" { agent.state = "running" }
        case "SYSTEM_MESSAGE", "USER_INPUT":
            // Each instruction from the parent (or the user) is a new assignment.
            guard let text = type == "USER_INPUT" ? AntigravityRecords.request(object["content"]) : AntigravityRecords.message(object["content"], from: subagent.parent) else { return }
            agent.turns += 1
            if agent.turns == 1, agent.input == nil || agent.input?.isEmpty == true { agent.input = text }
            else if agent.turns > 1 { upsert(LogEntry(id: key("\(index)"), kind: "user", text: text, timestamp: stamp, provider: "gemini")) }
            answer = nil; message = nil; agent.state = "running"
        case "PLANNER_RESPONSE":
            if let text = (object["content"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
                upsert(LogEntry(id: key("\(index)"), kind: "assistant", text: text, timestamp: stamp, provider: "gemini")); answer = text
            }
            var ids: [String] = []
            for (offset, call) in (object["tool_calls"] as? [[String: Any]] ?? []).prefix(64).enumerated() {
                guard let name = call["name"] as? String, !name.isEmpty else { continue }
                let args = call["args"] as? [String: Any] ?? [:]
                let id = key("\(index):\(offset)")
                activity(id, tool: name, args: args, state: "running", stamp: stamp)
                calls.append((id, name, args)); ids.append(id)
                // What it sends its parent is its answer to the assignment.
                if name == "send_message", (args["Recipient"] as? String)?.lowercased() == subagent.parent.lowercased(),
                   let text = (args["Message"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty { message = text }
            }
            if let usage = AntigravityRecords.usage(object) {
                agent.usage.append(GraphResponseRecord(responseId: "agy:" + subagent.conversation + ":\(index)", usage: usage, activityIds: ids))
                dirty = true
            }
            switch outcome {
            case .failed:
                agent.state = "error"
                if let error = AntigravityRecords.errorText(object["error"]) { upsert(LogEntry(id: key("\(index):error"), kind: "error", text: error, timestamp: stamp, provider: "gemini")) }
            case .stopped: agent.state = "stopped"
            // An answer while its helpers still work ("waiting for …") is not the end.
            case .done: agent.state = ids.isEmpty && waiting.isEmpty ? "completed" : "running"
            }
        case "GENERIC":
            guard !calls.isEmpty else { return }
            let call = calls.removeFirst()
            let output = outcome == .failed ? AntigravityRecords.errorText(object["error"]) ?? AntigravityRecords.toolText(object["content"])
                : outcome == .done && ActivitySupport.kind(tool: call.name) == "command" ? AntigravityRecords.toolText(object["content"]) : nil
            activity(call.entry, tool: call.name, args: call.args, state: outcome == .done ? "completed" : outcome == .failed ? "error" : "stopped", output: output, stamp: stamp)
            if call.name == "invoke_subagent", outcome == .done {
                let specs = call.args["Subagents"] as? [[String: Any]] ?? []
                for (offset, id) in AntigravityRecords.spawnedConversations(object["content"]).enumerated() {
                    let spec = offset < specs.count ? specs[offset] : [:]
                    if !started.contains(id) { started.append(id) }
                    waiting.insert(id.lowercased())
                    spawned.append(AntigravitySubagent(conversation: id, parent: subagent.conversation,
                                                       title: AntigravityRecords.title(role: spec["Role"], typeName: spec["TypeName"]), prompt: spec["Prompt"] as? String))
                }
            }
        default: return
        }
        agent.output = message ?? (agent.state == "completed" ? answer : nil)
        if previous != (agent.state, agent.output, agent.turns, agent.input) { dirty = true }
    }
}

/// Follows an Antigravity run's sub-agents through their own transcripts.
/// The runner's poll task reads off its actor, one call at a time; `finish`
/// runs only after that task has ended. Every read is bounded.
final class AntigravitySubagentWatcher: @unchecked Sendable {
    struct Limits {
        var bytesPerPoll = 4 * 1024 * 1024
        var finalBytes = 32 * 1024 * 1024
        var maximumLineBytes = 2 * 1024 * 1024
        var maximumChildren = ExecutionGraphSupport.maximumNodes
    }
    private struct Child {
        let url: URL
        let record: AntigravityChildRecord
        var tail: CodexRolloutTail
        /// The file shrank; transcripts only grow, so it is not read again.
        var closed = false
    }
    private let brain: URL
    private let namespace: String
    private let limits: Limits
    private var children: [Child] = []
    private var known = Set<String>()
    private var next = 0
    private(set) var stopped = false
    /// Steps written after this are not read (a recorded request's end:
    /// later requests may give the same sub-agent more work).
    private let until: Date?
    private(set) var bytesRead = 0

    /// `root` is the run's own conversation, never followed as a sub-agent.
    init(brain: URL, namespace: String, root: String? = nil, until: Date? = nil, limits: Limits = Limits()) {
        self.brain = brain; self.namespace = namespace; self.until = until; self.limits = limits
        if let root { known.insert(root.lowercased()) }
    }

    /// Changed sub-agents while the run is active.
    func poll(_ subagents: [AntigravitySubagent]) -> [CodexSessionAgent] { stopped ? [] : read(subagents, budget: limits.bytesPerPoll) }
    /// One last, larger read after the run ended; later polls read nothing.
    func finish(_ subagents: [AntigravitySubagent]) -> [CodexSessionAgent] {
        guard !stopped else { return [] }
        defer { stopped = true }
        return read(subagents, budget: limits.finalBytes)
    }

    private func adopt(_ subagents: [AntigravitySubagent]) {
        for subagent in subagents where children.count < limits.maximumChildren {
            guard let url = AntigravityRecords.transcript(brain: brain, conversation: subagent.conversation),
                  known.insert(subagent.conversation.lowercased()).inserted else { continue }
            children.append(Child(url: url, record: AntigravityChildRecord(subagent, namespace: namespace), tail: CodexRolloutTail(maximumLineBytes: limits.maximumLineBytes)))
        }
    }

    private func read(_ subagents: [AntigravitySubagent], budget: Int) -> [CodexSessionAgent] {
        adopt(subagents)
        var remaining = budget
        // Start one child later each time so a busy child cannot starve the rest;
        // children a child starts are read in the same pass.
        let existing = children.count
        let first = existing == 0 ? 0 : next % existing
        next = first + 1
        var order = (0..<existing).map { (first + $0) % existing }
        var position = 0
        while position < order.count, remaining > 0, !Task.isCancelled {
            let index = order[position]; position += 1
            guard !children[index].closed, let fd = CodexSessionFiles.open(children[index].url) else { continue }
            let result = children[index].tail.read(fd, maximumBytes: remaining)
            Darwin.close(fd)
            guard let result else { children[index].closed = true; continue }
            remaining -= result.bytes; bytesRead += result.bytes
            for line in result.lines {
                guard let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { continue }
                // Transcripts are written in order: past the end, nothing more belongs here.
                if let until, let date = AntigravityRecords.date(object), date > until { children[index].closed = true; break }
                children[index].record.consume(object)
            }
            let before = children.count
            adopt(children[index].record.takeSpawned())
            order += before..<children.count
        }
        return children.compactMap { $0.record.take() }
    }
}
