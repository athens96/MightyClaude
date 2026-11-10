import Foundation
import SQLite3

/// Where opencode (`opencode-ai`, OpenCode 1) keeps its files, and what the app reads of
/// them: whether any sign-in exists (`auth.json`, or a provider API key in the environment),
/// and the session list of `opencode.db` for "resume an earlier session". The app never
/// writes any of it and never shows a credential: only which providers have one.
public enum OpencodeAgent {
    /// The provider API key variables opencode reads through models.dev (`provider.env`),
    /// the common ones; `OPENCODE_API_KEY` is opencode Zen's own.
    public static let apiKeyVariables = PiAgent.apiKeyVariables.filter { $0 != "OPENCODE_API_KEY" } + ["OPENCODE_API_KEY", "GOOGLE_GENERATIVE_AI_API_KEY", "TOGETHER_AI_API_KEY", "GITHUB_TOKEN"]

    /// `$XDG_DATA_HOME/opencode`, else `~/.local/share/opencode`.
    public static func dataDirectory(home: URL, environment: [String: String]) -> URL {
        environment["XDG_DATA_HOME"].flatMap { $0.hasPrefix("/") ? URL(fileURLWithPath: $0, isDirectory: true).appendingPathComponent("opencode", isDirectory: true) : nil }
            ?? home.appendingPathComponent(".local/share/opencode", isDirectory: true)
    }
    public static func authFile(home: URL, environment: [String: String]) -> URL {
        dataDirectory(home: home, environment: environment).appendingPathComponent("auth.json")
    }
    /// What opencode reads as its sign-ins: `$OPENCODE_AUTH_CONTENT` when set, else `auth.json`.
    public static func authJSON(home: URL, environment: [String: String]) -> Data? {
        if let content = environment["OPENCODE_AUTH_CONTENT"], !content.isEmpty { return Data(content.prefix(1_048_576).utf8) }
        return CLIAccountSupport.boundedData(authFile(home: home, environment: environment))
    }
    /// The session database: `$OPENCODE_DB` when absolute, else `<data dir>/opencode.db`.
    public static func database(home: URL, environment: [String: String]) -> URL {
        if let override = environment["OPENCODE_DB"], override.hasPrefix("/") { return URL(fileURLWithPath: override) }
        return dataDirectory(home: home, environment: environment).appendingPathComponent("opencode.db")
    }

    /// What `auth.json` holds: the providers with a credential (`oauth`, `api` or `wellknown`),
    /// and whether one is a subscription (OAuth) sign-in. An empty or unreadable file holds none.
    public static func credentials(authJSON: Data?) -> (providers: [String], subscription: Bool) {
        guard let authJSON, let object = try? JSONSerialization.jsonObject(with: authJSON) as? [String: Any] else { return ([], false) }
        let entries = object.filter { ["oauth", "api", "wellknown"].contains(($0.value as? [String: Any])?["type"] as? String ?? "") }
        let providers = entries.keys.sorted().prefix(16).map { CLIAccountSupport.clean($0) }
        let subscription = entries.values.contains { ($0 as? [String: Any])?["type"] as? String == "oauth" }
        return (Array(providers), subscription)
    }
    public static func environmentKeys(_ environment: [String: String]) -> [String] {
        apiKeyVariables.filter { !(environment[$0] ?? "").isEmpty }
    }

    // MARK: Permissions

    /// The pane's permission mode as opencode's own rules (`OPENCODE_PERMISSION`, merged over
    /// the user's config for this run only). "ask" becomes the pane's approval card through ACP.
    /// Plan keeps opencode's plan agent (edits denied) and asks for commands and the web.
    public static func permissionRules(mode: String) -> [String: String] {
        switch mode {
        case "fullAccess":
            return ["*": "allow", "external_directory": "allow", "doom_loop": "allow", "question": "deny"]
        case "acceptEdits":
            return ["edit": "allow", "bash": "ask", "webfetch": "ask", "websearch": "ask", "task": "ask", "external_directory": "ask", "doom_loop": "ask", "question": "deny"]
        case "plan":
            return ["bash": "ask", "webfetch": "ask", "websearch": "ask", "external_directory": "ask", "doom_loop": "ask", "question": "deny"]
        default:
            return ["edit": "ask", "bash": "ask", "webfetch": "ask", "websearch": "ask", "task": "ask", "external_directory": "ask", "doom_loop": "ask", "question": "deny"]
        }
    }
    public static func permissionEnvironment(mode: String) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: permissionRules(mode: mode), options: [.sortedKeys])) ?? Data("{}".utf8)
        return String(decoding: data, as: UTF8.self)
    }
    /// The settings a run sends as `session/set_config_option` before its prompt.
    public static func configValues(_ request: StartRunRequest) -> [(id: String, value: String)] {
        var values: [(id: String, value: String)] = []
        if request.model != "default" { values.append(("model", request.model)) }
        if request.settings.effort != "default" { values.append(("effort", request.settings.effort)) }
        // opencode's two primary agents: build (the default, left as the session has it) and plan, which cannot edit.
        if request.settings.permissionMode == "plan" { values.append(("mode", "plan")) }
        return values
    }
}

extension CLIAccountSupport {
    /// opencode's sign-in is `auth.json` (or `$OPENCODE_AUTH_CONTENT`) and the environment's
    /// provider keys. Without either it still runs opencode Zen's free models, which reads as
    /// signed out with a note: `opencode auth login` adds a provider. A failed `auth list`
    /// (the liveness check) stays unknown.
    public static func opencodeStatus(exitCode: Int32, authJSON: Data?, environment: [String: String]) -> CLIAccountStatus {
        guard exitCode == 0 else {
            return CLIAccountStatus(provider: "opencode", detail: exitCode == -1 ? L("settings.cliAccounts.detailOpencodeTimeout") : L("settings.cliAccounts.detailOpencodeUnknown"), canSignOut: false)
        }
        let stored = OpencodeAgent.credentials(authJSON: authJSON)
        if !stored.providers.isEmpty {
            return CLIAccountStatus(provider: "opencode", loggedIn: true, method: stored.subscription ? L("cliAccounts.method.opencodeSubscription") : L("windows.cli.method.apiKey"),
                                    methodId: stored.subscription ? CLIAccountMethod.opencodeSubscription : CLIAccountMethod.opencodeAPIKey,
                                    account: stored.providers.joined(separator: ", "), detail: L("settings.cliAccounts.detailOpencodeSignOut"), canSignOut: false)
        }
        if !OpencodeAgent.environmentKeys(environment).isEmpty {
            return CLIAccountStatus(provider: "opencode", loggedIn: true, method: L("cliAccounts.method.opencodeEnvironmentKey"), methodId: CLIAccountMethod.opencodeAPIKey, canSignOut: false)
        }
        return CLIAccountStatus(provider: "opencode", loggedIn: false, detail: L("settings.cliAccounts.detailOpencodeFreeOnly"), canSignOut: false)
    }
}

// MARK: Resumable sessions

extension ResumableSessions {
    /// The root sessions `opencode.db` keeps for the workspace folder (`session.directory`),
    /// newest first; the first user message is the title when opencode's own is still its
    /// placeholder, and the request count is the session's user messages. Read-only.
    static func opencode(_ query: ResumableSessionQuery, budget: inout Int) -> ResumableSessionListing {
        let database = OpencodeAgent.database(home: query.home, environment: query.environment)
        let paths = workspacePaths(query.workspacePath)
        guard let rows = OpencodeSessions.rows(database: database, directories: paths, since: query.now.addingTimeInterval(-query.maximumAge), limit: min(query.maximumScanned, 2_000), counts: !query.headOnly) else { return ResumableSessionListing() }
        var listing = ResumableSessionListing()
        for row in rows {
            guard listing.items.count < query.maximumSessions, !Task.isCancelled else { break }
            guard CoreValidation.sessionIdentifier(row.id), !query.excluding.contains(row.id.lowercased()) else { continue }
            if row.requests == 0 { continue }
            let title = row.prompt ?? (row.title.isEmpty ? nil : row.title)
            let automated = !query.known.contains(row.id.lowercased()) && automatedPrompt(row.prompt)
            if automated && !query.includeAutomated { listing.hidden += 1; continue }
            listing.items.append(ResumableSession(provider: "opencode", sessionID: row.id, title: title.map(oneLine), modified: row.updated, requests: row.requests,
                                                  model: row.model, url: database, automated: automated))
        }
        return listing
    }
}

enum OpencodeSessions {
    struct Row: Equatable { var id: String; var title: String; var updated: Date; var requests: Int?; var prompt: String?; var model: String? }

    /// `session` rows without a parent (sub-agent sessions have one), not archived, in one of
    /// `directories`, updated since `since`. nil when the database is missing or unreadable.
    static func rows(database: URL, directories: Set<String>, since: Date, limit: Int, counts: Bool) -> [Row]? {
        guard FileManager.default.isReadableFile(atPath: database.path), !directories.isEmpty else { return nil }
        for mode in ["mode=ro", "immutable=1"] {
            var components = URLComponents(); components.scheme = "file"; components.path = database.path; components.query = mode
            guard let uri = components.string else { return nil }
            var handle: OpaquePointer?
            guard sqlite3_open_v2(uri, &handle, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK, let db = handle else { sqlite3_close(handle); continue }
            defer { sqlite3_close(db) }
            sqlite3_busy_timeout(db, 250)
            let marks = Array(repeating: "?", count: directories.count).joined(separator: ",")
            let query = "SELECT id, title, time_updated FROM session WHERE parent_id IS NULL AND time_archived IS NULL AND directory IN (\(marks)) AND time_updated >= ? ORDER BY time_updated DESC LIMIT \(max(0, limit))"
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(db, query, -1, &statement, nil) == SQLITE_OK, let statement else { sqlite3_finalize(statement); continue }
            defer { sqlite3_finalize(statement) }
            let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
            for (index, directory) in directories.sorted().enumerated() { sqlite3_bind_text(statement, Int32(index + 1), directory, -1, transient) }
            sqlite3_bind_int64(statement, Int32(directories.count + 1), Int64(since.timeIntervalSince1970 * 1_000))
            var rows: [Row] = []
            var result = sqlite3_step(statement)
            while result == SQLITE_ROW {
                let id = sqlite3_column_text(statement, 0).map { String(cString: $0) } ?? ""
                let title = sqlite3_column_text(statement, 1).map { String(cString: $0) } ?? ""
                let updated = Date(timeIntervalSince1970: Double(sqlite3_column_int64(statement, 2)) / 1_000)
                rows.append(Row(id: id, title: placeholderTitle(title) ? "" : ActivitySupport.clean(title, maximumBytes: 512, singleLine: true), updated: updated))
                result = sqlite3_step(statement)
            }
            guard result == SQLITE_DONE else { continue }
            if counts { for index in rows.indices { details(db, row: &rows[index]) } }
            return rows
        }
        return nil
    }

    /// opencode names a new session "New session - <time>" until it writes a real title.
    static func placeholderTitle(_ title: String) -> Bool { title.isEmpty || title.lowercased().hasPrefix("new session") || title.lowercased().hasPrefix("child session") }

    /// The session's user messages (their count, the first one's text) and the model of its last
    /// assistant message. Message `data` is opencode's JSON (`role`, `modelID`, `providerID`);
    /// a part of `type: text` carries the words.
    private static func details(_ db: OpaquePointer, row: inout Row) {
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        func each(_ sql: String, _ body: (OpaquePointer) -> Bool) {
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let statement else { sqlite3_finalize(statement); return }
            defer { sqlite3_finalize(statement) }
            sqlite3_bind_text(statement, 1, row.id, -1, transient)
            while sqlite3_step(statement) == SQLITE_ROW { if !body(statement) { break } }
        }
        func json(_ statement: OpaquePointer, _ column: Int32) -> [String: Any]? {
            guard let bytes = sqlite3_column_blob(statement, column) else { return nil }
            let count = Int(sqlite3_column_bytes(statement, column))
            guard count <= 1_048_576 else { return nil }
            return (try? JSONSerialization.jsonObject(with: Data(bytes: bytes, count: count))) as? [String: Any]
        }
        var users: [String] = [], model: String?
        each("SELECT id, data FROM message WHERE session_id = ? ORDER BY id LIMIT 20000") { statement in
            guard let data = json(statement, 1) else { return true }
            if data["role"] as? String == "user", let id = sqlite3_column_text(statement, 0).map({ String(cString: $0) }) { users.append(id) }
            if data["role"] as? String == "assistant", let provider = data["providerID"] as? String, let id = data["modelID"] as? String, CoreValidation.model(provider + "/" + id) { model = provider + "/" + id }
            return true
        }
        row.requests = users.count; row.model = model
        guard let first = users.first else { return }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT data FROM part WHERE message_id = ? ORDER BY id LIMIT 64", -1, &statement, nil) == SQLITE_OK, let statement else { sqlite3_finalize(statement); return }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_text(statement, 1, first, -1, transient)
        var texts: [String] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            if let data = json(statement, 0), data["type"] as? String == "text", data["synthetic"] as? Bool != true, let text = data["text"] as? String { texts.append(text) }
        }
        let prompt = texts.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        row.prompt = prompt.isEmpty ? nil : prompt
    }
}
