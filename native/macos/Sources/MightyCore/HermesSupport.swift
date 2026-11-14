import Foundation
import SQLite3

/// Hermes Agent (Nous Research, `hermes`): where it keeps its files and what the app reads of
/// them. A run is `hermes acp` through `ACPChannel`; the turn itself runs in Hermes's gateway
/// daemon, which `hermes acp` starts when needed. The app reads the sign-in from the ACP
/// server's `initialize` answer (a provider's name, never a credential), the session list
/// from `state.db`, and the model picker a session offered. It never writes into `~/.hermes`
/// itself; the one setting it changes (`HermesQuestionTool`) goes through `hermes config set`.
public enum HermesAgent {
    /// The ACP method id Hermes always advertises: its terminal setup, not a sign-in.
    static let setupMethod = "hermes-setup"
    /// The session's settings as Hermes's ACP config options name them.
    static let modelOption = "model", effortOption = "reasoning_effort", editPolicyOption = "edit_approval_policy"

    /// `$HERMES_HOME` when absolute, else `~/.hermes`.
    public static func home(home: URL, environment: [String: String]) -> URL {
        environment["HERMES_HOME"].flatMap { $0.hasPrefix("/") ? URL(fileURLWithPath: $0, isDirectory: true) : nil } ?? home.appendingPathComponent(".hermes", isDirectory: true)
    }
    /// Sessions, messages and each session's frozen launch policy.
    public static func database(home: URL, environment: [String: String]) -> URL {
        self.home(home: home, environment: environment).appendingPathComponent("state.db")
    }

    /// `hermes --version` prints its version first ("Hermes Agent v0.23.1 (2026.11.10)"), then
    /// its install folder, Python and update status: the first line is the version.
    public static func versionLine(_ output: String) -> String {
        output.split(whereSeparator: \.isNewline).first.map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
    }

    // MARK: Permissions

    /// Hermes's edit approval policy for the pane's mode (ACP modes Default / Accept Edits /
    /// Don't Ask): ask before every edit, allow edits inside the workspace, or allow them all.
    public static func editPolicy(mode: String) -> String {
        switch mode { case "acceptEdits": return "workspace_session"; case "fullAccess": return "session"; default: return "ask" }
    }
    /// The run's environment: full access is Hermes's `--yolo` for the session, given as its
    /// documented `HERMES_YOLO_MODE`, which `hermes acp` freezes into a session it creates. Any
    /// other mode removes a `HERMES_YOLO_MODE` the user's shell exported, so the pane's choice holds.
    public static func apply(mode: String, to environment: inout [String: String]) {
        if mode == "fullAccess" { environment["HERMES_YOLO_MODE"] = "1" } else { environment.removeValue(forKey: "HERMES_YOLO_MODE") }
    }
    /// The settings a run sends as `session/set_config_option` before its prompt: the model
    /// (Hermes's picker id, `provider:model`), the reasoning effort, and the edit policy, which
    /// is always sent so a resumed session follows the pane.
    public static func configValues(_ request: StartRunRequest) -> [(id: String, value: String)] {
        var values: [(id: String, value: String)] = []
        if request.model != "default" { values.append((modelOption, request.model)) }
        if request.settings.effort != "default" { values.append((effortOption, request.settings.effort)) }
        values.append((editPolicyOption, editPolicy(mode: request.settings.permissionMode)))
        return values
    }

    /// Whether Hermes created the session with `--yolo` (dangerous commands not asked about):
    /// the `yolo` of the creation request its gateway keeps in `state_meta`
    /// (`gateway.local_policy.v1:<id>`). The flag is fixed for the session's life, so a resume
    /// cannot change it. nil when the record is missing or unreadable.
    public static func startedWithFullAccess(sessionID: String, database: URL) -> Bool? {
        guard CoreValidation.sessionIdentifier(sessionID) else { return nil }
        return HermesSessions.read(database) { db in
            guard let raw = HermesSessions.text(db, "SELECT value FROM state_meta WHERE key = ?", ["gateway.local_policy.v1:" + sessionID]),
                  let receipt = HermesSessions.object(raw) else { return nil }
            let policy = receipt["policy"] as? [String: Any] ?? receipt
            guard let request = (policy["request_json"] as? String).flatMap(HermesSessions.object) ?? policy["request"] as? [String: Any] else { return nil }
            return request["yolo"] as? Bool == true
        }
    }

    /// Whether a run continues the pane's Hermes session, and what the pane is told (Windows
    /// `HermesAgent.Resume`): Hermes fixes `--yolo` when a session starts, so one that started
    /// with it is not continued by a pane that asks (a new session, run.notice.hermesFullAccessSession),
    /// and one that started without it keeps asking in a full-access pane (continued,
    /// run.notice.hermesAskingSession); a session whose first request never got an answer
    /// (`message_count` 0) keeps the provider it was made with and is not continued. A session
    /// whose policy receipt cannot be read may have started with `--yolo`: a pane that asks
    /// starts a new one (run.notice.hermesUnknownSession), a full-access pane continues it.
    public static func resume(_ resumeID: String, mode: String, messageCount: Int?, startedWithFullAccess: Bool?) -> (resumeID: String?, noticeKey: String?) {
        let full = mode == "fullAccess"
        if messageCount == 0 { return (nil, nil) }
        if startedWithFullAccess == true, !full { return (nil, "run.notice.hermesFullAccessSession") }
        if startedWithFullAccess == nil, !full { return (nil, "run.notice.hermesUnknownSession") }
        return (resumeID, startedWithFullAccess == false && full ? "run.notice.hermesAskingSession" : nil)
    }

    /// The messages the session holds (`sessions.message_count`): 0 for one whose turns all failed
    /// or never ran. nil when the session or the database is not there.
    public static func messageCount(sessionID: String, database: URL) -> Int? {
        guard CoreValidation.sessionIdentifier(sessionID) else { return nil }
        return HermesSessions.read(database) { db in HermesSessions.text(db, "SELECT COALESCE(message_count, 0) FROM sessions WHERE id = ?", [sessionID]).flatMap { Int($0) } }
    }

    /// What Hermes writes to stderr while it serves ACP: its log (`2026-11-11 01:18:54 [INFO] …`)
    /// and a Python traceback for every request it answers with an error. Neither is for the
    /// pane, which shows the ACP error itself. Anything else (a start-up failure's own words) passes.
    final class StderrFilter {
        private var partial = "", inTraceback = false
        func push(_ text: String) -> String {
            var lines = (partial + text).split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
            partial = String(lines.removeLast().prefix(16_384))
            return lines.filter(keep).map { $0 + "\n" }.joined()
        }
        func finish() -> String { defer { partial = "" }; return partial.isEmpty || !keep(partial) ? "" : partial }
        private func keep(_ line: String) -> Bool {
            if line.range(of: "^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(,[0-9]+)? \\[[A-Z]+\\]", options: .regularExpression) != nil { inTraceback = false; return false }
            if line.hasPrefix("Traceback (most recent call last)") { inTraceback = true; return false }
            if line.hasPrefix("During handling of the above exception") || line.hasPrefix("The above exception was the direct cause") { return false }
            guard inTraceback else { return !line.trimmingCharacters(in: .whitespaces).isEmpty }
            // The frames are indented; the first line that is not is the exception itself, the traceback's last.
            if !(line.first?.isWhitespace ?? true) { inTraceback = false }
            return false
        }
    }

    // MARK: Sign-in

    public struct Probe: Sendable, Equatable {
        /// The provider whose credentials Hermes resolved, nil when it has none.
        public var provider: String?
        /// Hermes answered `initialize`; false when it exited or timed out first.
        public var answered: Bool
        public var timedOut: Bool
        public var version: String?
    }

    static let probeFrame = Data(#"{"jsonrpc":"2.0","id":"mighty-probe","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false},"clientInfo":{"name":"mighty-code","title":"Mighty Code","version":"1.0"}}}"#.utf8) + Data([10])

    /// The `initialize` answer among what `hermes acp` printed: `authMethods` holds Hermes's
    /// terminal setup always, and an agent method named after the provider when that provider's
    /// credentials resolve (`acp_adapter/auth.py`). Only ids are read.
    public static func parseInitialize(_ output: Data) -> Probe? {
        for line in output.split(separator: 10).prefix(64) {
            guard let object = (try? JSONSerialization.jsonObject(with: Data(line))) as? [String: Any], object["id"] as? String == "mighty-probe" else { continue }
            guard let result = object["result"] as? [String: Any] else { return Probe(provider: nil, answered: false, timedOut: false, version: nil) }
            let provider = provider(initialize: result)
            let version = (result["agentInfo"] as? [String: Any])?["version"] as? String
            return Probe(provider: provider.map { CLIAccountSupport.clean($0) }, answered: true, timedOut: false, version: version)
        }
        return nil
    }

    /// The provider an `initialize` result's `authMethods` name: the entry that is not the terminal setup.
    static func provider(initialize result: [String: Any]) -> String? {
        let methods = result["authMethods"] as? [[String: Any]] ?? []
        return methods.first { ($0["id"] as? String).map { !$0.isEmpty && $0 != setupMethod } == true && ($0["type"] as? String ?? "agent") == "agent" }?["id"] as? String
    }

    /// Starts `hermes acp`, sends `initialize` alone and reads its answer. No session is
    /// opened, so Hermes's gateway is not started. nil when the command cannot start.
    static func probe(arguments: [String], environment: [String: String], cwd: URL, timeout: TimeInterval) async -> Probe? {
        let stdout = AppUpdateService.OutputSink()
        var env = environment; env["NO_COLOR"] = "1"
        guard let child = try? NativeChildProcess(executable: URL(fileURLWithPath: "/usr/bin/env"), arguments: arguments, environment: env, cwd: cwd,
                                                  stdout: { stdout.append($0) }, stderr: { _ in }, exited: { _ in }) else { return nil }
        child.write(probeFrame)
        let began = Date()
        var parsed: Probe?
        while parsed == nil, !child.hasExited, Date().timeIntervalSince(began) < timeout {
            try? await Task.sleep(for: .milliseconds(50))
            parsed = parseInitialize(stdout.data)
        }
        let timedOut = parsed == nil && !child.hasExited
        child.closeInput()
        _ = await child.wait(timeout: 2)
        return parsed ?? parseInitialize(stdout.data) ?? Probe(provider: nil, answered: false, timedOut: timedOut, version: nil)
    }

    // MARK: Models

    /// The file in the app's folder (`ProviderService.hermesModelCacheURL`) that keeps the model picker of the last
    /// Hermes session: Hermes has no command that lists models without opening a session. A ProviderService without
    /// a storage folder (tests) has none, so a fake Hermes never writes over the user's picker.
    public static let modelCacheFileName = "hermes-models.json"
    /// The `model` option of a session's `configOptions` as picker rows: `provider:model` ids
    /// with Hermes's own names.
    public static func models(configOptions: Any?) -> [ModelOption] {
        guard let option = (configOptions as? [[String: Any]])?.first(where: { $0["id"] as? String == modelOption }) else { return [] }
        var seen = Set<String>(), models: [ModelOption] = []
        func add(_ entry: [String: Any]) {
            guard let value = entry["value"] as? String, value != "default", CoreValidation.model(value), seen.insert(value).inserted, models.count < ProviderOptions.listedModelLimit("hermes") else { return }
            let name = ActivitySupport.clean(entry["name"] as? String ?? value, maximumBytes: 160, singleLine: true)
            let provider = value.firstIndex(of: ":").map { String(value[..<$0]) } ?? ""
            models.append(ModelOption(value: value, displayName: name.isEmpty ? value : name, description: provider.isEmpty ? "" : L("provider.catalog.hermesModelRow", ["provider": provider])))
        }
        for entry in (option["options"] as? [[String: Any]] ?? []).prefix(4_096) {
            add(entry)
            for inner in (entry["options"] as? [[String: Any]] ?? []).prefix(4_096) { add(inner) }
        }
        return models
    }
    /// Keeps a session's picker for the model menu; an empty one changes nothing.
    public static func saveModels(configOptions: Any?, to url: URL?) {
        let models = models(configOptions: configOptions)
        guard let url, !models.isEmpty, let data = try? JSONEncoder().encode(models) else { return }
        if (try? Data(contentsOf: url)) == data { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }
    /// Without a provider the kept picker is a past sign-in's: it goes.
    public static func forgetModels(at url: URL?) { if let url { try? FileManager.default.removeItem(at: url) } }
    /// "Follow Hermes's settings" and, once a session ran, the models its picker offered.
    public static func cachedCatalog(url: URL?) -> ModelCatalog {
        let fallback = ProviderOptions.fallbackCatalog("hermes")
        guard let url, let values = try? url.resourceValues(forKeys: [.fileSizeKey]), (values.fileSize ?? .max) <= 2_097_152,
              let data = try? Data(contentsOf: url), let stored = try? JSONDecoder().decode([ModelOption].self, from: data) else {
            return ModelCatalog(models: [fallback.models[0]], detail: L("provider.catalog.hermesEmpty"))
        }
        var seen = Set(["default"])
        let models = stored.prefix(ProviderOptions.listedModelLimit("hermes")).filter { CoreValidation.model($0.value) && seen.insert($0.value).inserted }
        guard !models.isEmpty else { return ModelCatalog(models: [fallback.models[0]], detail: L("provider.catalog.hermesEmpty")) }
        return ModelCatalog(source: "cli", models: [fallback.models[0]] + models, detail: L("provider.catalog.hermesSource"))
    }
}

extension CLIAccountSupport {
    /// Hermes is signed in when its ACP server names a provider whose credentials resolve
    /// (`hermes model` sets one up); with none it only offers its terminal setup. No answer
    /// (an old Hermes, a missing ACP package, a timeout) stays unknown.
    public static func hermesStatus(_ probe: HermesAgent.Probe?) -> CLIAccountStatus {
        guard let probe else { return CLIAccountStatus(provider: "hermes", detail: L("settings.cliAccounts.detailRunFailed"), canSignOut: false) }
        guard probe.answered else {
            return CLIAccountStatus(provider: "hermes", detail: probe.timedOut ? L("settings.cliAccounts.detailHermesTimeout") : L("settings.cliAccounts.detailHermesUnknown"), canSignOut: false)
        }
        guard let provider = probe.provider, !provider.isEmpty else {
            return CLIAccountStatus(provider: "hermes", loggedIn: false, detail: L("settings.cliAccounts.detailHermesNoProvider"), canSignOut: false)
        }
        return CLIAccountStatus(provider: "hermes", loggedIn: true, method: L("cliAccounts.method.hermesProvider"), methodId: CLIAccountMethod.hermesProvider,
                                account: provider, detail: L("settings.cliAccounts.detailHermesSignOut"), canSignOut: false)
    }
}

// MARK: Resumable sessions

extension ResumableSessions {
    /// The conversations Hermes's ACP server lists for the workspace folder (`state.db`,
    /// sessions an ACP client created), newest first. A compressed conversation is several
    /// rows chained by `parent_session_id`; it is listed once, under its first row's id, which
    /// is the id ACP resumes. Read-only.
    static func hermes(_ query: ResumableSessionQuery, budget: inout Int) -> ResumableSessionListing {
        let database = HermesAgent.database(home: query.home, environment: query.environment)
        let paths = workspacePaths(query.workspacePath)
        guard let rows = HermesSessions.rows(database: database, directories: paths, since: query.now.addingTimeInterval(-query.maximumAge), limit: min(query.maximumScanned, 2_000), counts: !query.headOnly) else { return ResumableSessionListing() }
        var listing = ResumableSessionListing()
        for row in rows {
            guard listing.items.count < query.maximumSessions, !Task.isCancelled else { break }
            guard CoreValidation.sessionIdentifier(row.id), !query.excluding.contains(row.id.lowercased()) else { continue }
            if row.requests == 0 { continue }
            let title = row.prompt ?? (row.title.isEmpty ? nil : row.title)
            let automated = !query.known.contains(row.id.lowercased()) && automatedPrompt(row.prompt)
            if automated && !query.includeAutomated { listing.hidden += 1; continue }
            listing.items.append(ResumableSession(provider: "hermes", sessionID: row.id, title: title.map(oneLine), modified: row.updated, requests: row.requests,
                                                  model: row.model, url: database, automated: automated))
        }
        return listing
    }
}

enum HermesSessions {
    struct Row: Equatable { var id: String; var title: String; var updated: Date; var requests: Int?; var prompt: String?; var model: String? }

    /// Opens the database read-only (`mode=ro`, then `immutable=1` for one a writer holds) and runs `body`.
    static func read<T>(_ database: URL, _ body: (OpaquePointer) -> T?) -> T? {
        guard FileManager.default.isReadableFile(atPath: database.path) else { return nil }
        for mode in ["mode=ro", "immutable=1"] {
            var components = URLComponents(); components.scheme = "file"; components.path = database.path; components.query = mode
            guard let uri = components.string else { return nil }
            var handle: OpaquePointer?
            guard sqlite3_open_v2(uri, &handle, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK, let db = handle else { sqlite3_close(handle); continue }
            defer { sqlite3_close(db) }
            sqlite3_busy_timeout(db, 250)
            if let value = body(db) { return value }
        }
        return nil
    }
    static func object(_ text: String) -> [String: Any]? {
        guard text.utf8.count <= 1_048_576, let data = text.data(using: .utf8) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
    private static func prepare(_ db: OpaquePointer, _ sql: String, _ texts: [String]) -> OpaquePointer? {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let statement else { sqlite3_finalize(statement); return nil }
        for (index, text) in texts.enumerated() { sqlite3_bind_text(statement, Int32(index + 1), text, -1, transient) }
        return statement
    }
    /// The first column of the first row, as text.
    static func text(_ db: OpaquePointer, _ sql: String, _ texts: [String]) -> String? {
        guard let statement = prepare(db, sql, texts) else { return nil }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW, sqlite3_column_bytes(statement, 0) <= 1_048_576 else { return nil }
        return sqlite3_column_text(statement, 0).map { String(cString: $0) }
    }
    private static func columns(_ db: OpaquePointer, table: String) -> Set<String> {
        guard let statement = prepare(db, "PRAGMA table_info(\(table))", []) else { return [] }
        defer { sqlite3_finalize(statement) }
        var names = Set<String>()
        while sqlite3_step(statement) == SQLITE_ROW { if let name = sqlite3_column_text(statement, 1) { names.insert(String(cString: name)) } }
        return names
    }

    /// The ACP-created sessions (`source` or `created_source` is `acp`) with messages, in one
    /// of `directories`, active since `since`, grouped into conversations. Columns a Hermes
    /// version lacks are left out of the query. nil when the database is missing or unreadable.
    static func rows(database: URL, directories: Set<String>, since: Date, limit: Int, counts: Bool) -> [Row]? {
        guard !directories.isEmpty else { return nil }
        return read(database) { db -> [Row]? in
            let have = columns(db, table: "sessions")
            guard have.isSuperset(of: ["id", "source", "started_at"]) else { return nil }
            func column(_ name: String, _ fallback: String = "NULL") -> String { have.contains(name) ? name : fallback }
            var filters = [have.contains("created_source") ? "(source = 'acp' OR created_source = 'acp')" : "source = 'acp'"]
            for flag in ["archived", "hidden"] where have.contains(flag) { filters.append("COALESCE(\(flag), 0) = 0") }
            let sql = "SELECT id, \(column("parent_session_id")), \(column("title")), started_at, \(column("last_activity_at")), \(column("message_count", "1")), \(column("model")), \(column("cwd")), \(column("model_config")) FROM sessions WHERE \(filters.joined(separator: " AND ")) ORDER BY started_at DESC LIMIT \(max(0, limit))"
            guard let statement = prepare(db, sql, []) else { return nil }
            defer { sqlite3_finalize(statement) }
            struct Segment { var id: String; var parent: String?; var title: String; var updated: Double; var messages: Int; var model: String?; var cwd: String? }
            var segments: [String: Segment] = [:], order: [String] = []
            func string(_ index: Int32) -> String? { sqlite3_column_type(statement, index) == SQLITE_NULL ? nil : sqlite3_column_text(statement, index).map { String(cString: $0) } }
            var result = sqlite3_step(statement)
            while result == SQLITE_ROW {
                if let id = string(0) {
                    let started = sqlite3_column_double(statement, 3), active = sqlite3_column_double(statement, 4)
                    // Older rows keep the folder in `model_config` only.
                    let cwd = string(7) ?? string(8).flatMap(object)?["cwd"] as? String
                    segments[id] = Segment(id: id, parent: string(1), title: ActivitySupport.clean(string(2) ?? "", maximumBytes: 512, singleLine: true), updated: max(started, active),
                                           messages: Int(sqlite3_column_int64(statement, 5)), model: string(6), cwd: cwd)
                    order.append(id)
                }
                result = sqlite3_step(statement)
            }
            guard result == SQLITE_DONE else { return nil }
            // A conversation is its chain's first row; later rows (compression) only add to it.
            func root(_ id: String) -> String {
                var current = id, hops = 0
                while let parent = segments[current]?.parent, segments[parent] != nil, hops < 64 { current = parent; hops += 1 }
                return current
            }
            var grouped: [String: (row: Row, members: [String], messages: Int, cwd: String?)] = [:], roots: [String] = []
            for id in order {
                guard let segment = segments[id] else { continue }
                let key = root(id)
                if grouped[key] == nil {
                    let first = segments[key] ?? segment
                    grouped[key] = (Row(id: key, title: first.title, updated: Date(timeIntervalSince1970: segment.updated), model: first.model.flatMap { CoreValidation.model($0) ? $0 : nil }), [], 0, first.cwd)
                    roots.append(key)
                }
                grouped[key]!.members.append(id)
                grouped[key]!.messages += max(0, segment.messages)
                if segment.updated > grouped[key]!.row.updated.timeIntervalSince1970 { grouped[key]!.row.updated = Date(timeIntervalSince1970: segment.updated) }
                if grouped[key]!.row.title.isEmpty { grouped[key]!.row.title = segment.title }
            }
            var rows: [Row] = []
            for key in roots {
                guard var entry = grouped[key], entry.messages > 0, entry.row.updated >= since,
                      let cwd = entry.cwd, directories.contains(URL(fileURLWithPath: cwd).standardizedFileURL.path) || directories.contains(cwd) else { continue }
                if counts { details(db, members: entry.members, row: &entry.row) }
                rows.append(entry.row)
            }
            return rows.sorted { $0.updated > $1.updated }
        }
    }

    /// The conversation's user messages: their count and the first one's text.
    private static func details(_ db: OpaquePointer, members: [String], row: inout Row) {
        let ids = Array(members.prefix(64))
        let marks = Array(repeating: "?", count: ids.count).joined(separator: ",")
        guard !ids.isEmpty, let statement = prepare(db, "SELECT content FROM messages WHERE session_id IN (\(marks)) AND role = 'user' ORDER BY id LIMIT 20000", ids) else { return }
        defer { sqlite3_finalize(statement) }
        // `last_activity_at` stays near the session's start: the newest message says when it was last used.
        if let newest = text(db, "SELECT MAX(timestamp) FROM messages WHERE session_id IN (\(marks))", ids).flatMap({ Double($0) }), newest > row.updated.timeIntervalSince1970 {
            row.updated = Date(timeIntervalSince1970: newest)
        }
        var count = 0
        while sqlite3_step(statement) == SQLITE_ROW {
            count += 1
            guard row.prompt == nil, sqlite3_column_bytes(statement, 0) <= 1_048_576, let raw = sqlite3_column_text(statement, 0) else { continue }
            var text = String(cString: raw)
            // A message with attachments is stored as a JSON list of parts: its text parts are the prompt.
            if text.hasPrefix("["), let parts = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [[String: Any]] {
                text = parts.compactMap { $0["text"] as? String }.joined(separator: " ")
            }
            let prompt = HermesQuestionTool.withoutNote(text).trimmingCharacters(in: .whitespacesAndNewlines)
            row.prompt = prompt.isEmpty ? nil : String(prompt.prefix(2_000))
        }
        row.requests = count
    }
}

/// Hermes leaves its question tool (`clarify`) out of editor sessions: the `hermes-acp` toolset
/// is its coding tools without it, and only `platform_toolsets.acp` in Hermes's config changes
/// what an ACP session gets (no flag, environment variable or ACP parameter does). So before
/// the first Hermes run of an app launch the app reads that one key with `hermes config get`
/// and, only when it is unset, sets it with `hermes config set` to the default plus clarify.
/// A list the user saved is theirs and is left alone. Nothing else of Hermes's config is read
/// or written, and the file is never edited by the app itself.
public enum HermesQuestionTool {
    public static let key = "platform_toolsets.acp"
    public static let tool = "clarify"
    public enum Outcome: Equatable, Sendable {
        /// The key was unset and is now this list.
        case enabled([String])
        /// The user's own list has no clarify: the app says how to add it.
        case missing
        /// clarify is already on.
        case ready
    }
    /// What follows the user's request in a Hermes prompt while clarify is on. Hermes has no
    /// per-run system prompt (ACP's `session/new` takes a folder and MCP servers, its launch
    /// flags are yes/no switches), and without these words its model lists choices as text.
    /// After the request, not before it: Hermes titles a session by the start of its first message.
    public static let promptNote = "[Note from the Mighty Code app, not from the user: this pane shows your clarify tool as a card the user answers with a click. Whenever you present the user with options to choose from (a quiz or multiple-choice problem, a preference, a decision), call clarify with the question and the choices instead of listing them in your reply.]"
    /// A stored request without the note, as the session list shows it.
    public static func withoutNote(_ text: String) -> String {
        guard let range = text.range(of: "[Note from the Mighty Code app, not from the user:") else { return text }
        return String(text[..<range.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// What the unset key becomes. Saving a list makes Hermes stop adding two things it adds to
    /// the unsaved default, so they are named: `x_search` (Hermes adds it when xAI credentials
    /// exist; without them the tool's own check still leaves it out) and `kanban` when the
    /// config's legacy top-level `toolsets` opts into it. Every other tool and every enabled MCP
    /// server resolves as before (checked against Hermes 0.23.1's `_get_platform_tools`).
    static func value(legacyToolsets: [String]) -> [String] {
        ["hermes-acp", tool, "x_search"] + (legacyToolsets.contains("kanban") ? ["kanban"] : [])
    }
    /// A `config get --json` answer as toolset names: a list, or the list a string spells
    /// (older `config set` builds saved `'["a", "b"]'` as text).
    static func names(json: Data) -> [String]? {
        guard let object = try? JSONSerialization.jsonObject(with: json, options: [.fragmentsAllowed]) else { return nil }
        if let list = object as? [Any] { return list.map { "\($0)" } }
        if let text = object as? String, text.trimmingCharacters(in: .whitespaces).hasPrefix("["),
           let list = try? JSONSerialization.jsonObject(with: Data(text.replacingOccurrences(of: "'", with: "\"").utf8)) as? [Any] { return list.map { "\($0)" } }
        return nil
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var looked: [String: Outcome] = [:]
    /// Once per app launch for each Hermes (its executable and data folder). `first` is what
    /// this launch found the first time (the pane says it once); `asks` whether Hermes's model
    /// has the tool as far as the app knows.
    static func ensure(executable: URL, environment: [String: String], cwd: URL) async -> (first: Outcome?, asks: Bool) {
        let identity = executable.path + "\n" + (environment["HERMES_HOME"] ?? "")
        if let known = lock.withLock({ looked[identity] }) { return (nil, known != .missing) }
        // A Hermes that could not answer is asked again by the next run.
        guard let outcome = await apply(executable: executable, environment: environment, cwd: cwd) else { return (nil, false) }
        let first = lock.withLock { () -> Bool in if looked[identity] == nil { looked[identity] = outcome; return true }; return false }
        return (first ? outcome : nil, outcome != .missing)
    }
    /// nil when Hermes could not be asked (no answer, a managed or unreadable config).
    static func apply(executable: URL, environment: [String: String], cwd: URL) async -> Outcome? {
        guard let current = await run(executable, ["config", "get", "--json", key], environment, cwd) else { return nil }
        if current.status == 0 {
            guard let names = names(json: current.stdout) else { return nil }
            return names.contains(tool) ? .ready : .missing
        }
        // Only Hermes's own "not set" answer is an unset key; any other failure changes nothing.
        guard current.status == 1, current.stdout.allSatisfy({ $0 == 10 || $0 == 32 }), String(decoding: current.stderr, as: UTF8.self).contains("not set: " + key) else { return nil }
        let legacy = await run(executable, ["config", "get", "--json", "toolsets"], environment, cwd)
        let list = value(legacyToolsets: legacy.flatMap { $0.status == 0 ? names(json: $0.stdout) : nil } ?? [])
        guard let json = try? JSONSerialization.data(withJSONObject: list),
              let written = await run(executable, ["config", "set", key, String(decoding: json, as: UTF8.self)], environment, cwd), written.status == 0,
              let check = await run(executable, ["config", "get", "--json", key], environment, cwd), check.status == 0, names(json: check.stdout) == list else { return nil }
        return .enabled(list)
    }
    private static func run(_ executable: URL, _ arguments: [String], _ environment: [String: String], _ cwd: URL) async -> (status: Int32, stdout: Data, stderr: Data)? {
        let stdout = AppUpdateService.OutputSink(), stderr = AppUpdateService.OutputSink()
        var env = environment; env["NO_COLOR"] = "1"
        guard let child = try? NativeChildProcess(executable: executable, arguments: arguments, environment: env, cwd: cwd,
                                                  stdout: { stdout.append($0) }, stderr: { stderr.append($0) }, exited: { _ in }) else { return nil }
        child.closeInput()
        let status = await child.wait(timeout: 20)
        return status < 0 ? nil : (status, stdout.data, stderr.data)
    }
}
