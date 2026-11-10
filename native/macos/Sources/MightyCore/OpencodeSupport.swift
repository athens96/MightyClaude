import Foundation
import SQLite3

/// Where OpenCode 2 (`@opencode/cli`) keeps its files, and what the app reads of them:
/// the sign-ins (`opencode auth list --format json`: integration names only, never a
/// credential) and the session list of `opencode.db` for "resume an earlier session".
/// The app never writes any of it.
public enum OpencodeAgent {
    /// The first OpenCode 2 release: an older `opencode` is OpenCode 1 (`opencode-ai`), whose
    /// files, permissions and credentials differ, and is replaced rather than run.
    public static let minimumMajor = 2
    /// The npm package OpenCode 1 came in; the setup screen removes it before installing 2,
    /// since both put an `opencode` command on the PATH (migrate-v1.mdx).
    public static let legacyPackage = "opencode-ai"

    /// Whether `opencode --version` (`opencode v2.2.7`; OpenCode 1 printed `1.19.5`) names OpenCode 2.
    public static func isSupported(version: String) -> Bool {
        guard let numbers = SetupProber.versionNumbers(version), let major = numbers.first else { return false }
        return major >= minimumMajor
    }

    /// `$XDG_DATA_HOME/opencode`, else `~/.local/share/opencode` (OpenCode 2 keeps the name).
    public static func dataDirectory(home: URL, environment: [String: String]) -> URL {
        environment["XDG_DATA_HOME"].flatMap { $0.hasPrefix("/") ? URL(fileURLWithPath: $0, isDirectory: true).appendingPathComponent("opencode", isDirectory: true) : nil }
            ?? home.appendingPathComponent(".local/share/opencode", isDirectory: true)
    }
    /// The database (sessions and credentials): `$OPENCODE_DB` when absolute, else
    /// `<data dir>/opencode.db` (the release channels' name; another channel is `opencode-<channel>.db`).
    public static func database(home: URL, environment: [String: String]) -> URL {
        if let override = environment["OPENCODE_DB"], override.hasPrefix("/") { return URL(fileURLWithPath: override) }
        let channel = environment["OPENCODE_CHANNEL"] ?? ""
        let shared = channel.isEmpty || ["latest", "dev", "beta", "next", "prod"].contains(channel) || ["1", "true"].contains(environment["OPENCODE_DISABLE_CHANNEL_DB"] ?? "")
        let name = shared || channel.range(of: "^[a-z0-9-]{1,32}$", options: .regularExpression) == nil ? "opencode.db" : "opencode-\(channel).db"
        return dataDirectory(home: home, environment: environment).appendingPathComponent(name)
    }

    /// What `opencode auth list --format json` lists: integrations with at least one
    /// connection (a stored credential or an environment key). A connection whose status is
    /// `needs_auth` does not count. Only ids, names and the kind of connection are read.
    public static func connections(listJSON: Data) -> (integrations: [String], subscription: Bool, environmentOnly: Bool)? {
        guard let rows = try? JSONSerialization.jsonObject(with: listJSON) as? [[String: Any]] else { return nil }
        var names: [String] = [], subscription = false, stored = false
        for row in rows.prefix(64) {
            let live = (row["connections"] as? [[String: Any]] ?? []).filter { ($0["status"] as? [String: Any])?["status"] as? String != "needs_auth" }
            guard !live.isEmpty, let id = (row["name"] ?? row["id"]) as? String else { continue }
            names.append(CLIAccountSupport.clean(id))
            if live.contains(where: { $0["type"] as? String == "credential" }) { stored = true }
            if live.contains(where: { $0["type"] as? String == "credential" && $0["method"] as? String == "oauth" }) { subscription = true }
        }
        return (names.sorted(), subscription, !names.isEmpty && !stored)
    }

    // MARK: Permissions

    /// The pane's permission mode as OpenCode 2's ordered rules (`permissions`, the last match
    /// wins), given to the run alone in `OPENCODE_CONFIG_CONTENT` after the user's own config.
    /// OpenCode 2 allows everything by default, the folders outside the project too; what
    /// these leave at "ask" comes to the pane as the approval card. Plan sets no edit rule, so
    /// the plan agent's own edit denial stays.
    public static func permissionRules(mode: String) -> [[String: String]] {
        func rule(_ action: String, _ effect: String) -> [String: String] { ["action": action, "resource": "*", "effect": effect] }
        let outside = ["shell", "webfetch", "websearch", "subagent", "external_directory"]
        switch mode {
        case "fullAccess": return [rule("*", "allow")]
        case "acceptEdits": return [rule("edit", "allow")] + outside.map { rule($0, "ask") }
        case "plan": return outside.map { rule($0, "ask") }
        default: return [rule("edit", "ask")] + outside.map { rule($0, "ask") }
        }
    }
    public static func configContent(mode: String) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: ["permissions": permissionRules(mode: mode)], options: [.sortedKeys])) ?? Data("{}".utf8)
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
    /// OpenCode 2's sign-ins are what `opencode auth list --format json` lists (credentials in
    /// its database, keys in the environment). With none it still runs opencode Zen's free
    /// models, which reads as signed out with a note: `opencode auth login` adds a provider.
    /// A failed or unreadable list stays unknown; OpenCode 1 asks to install OpenCode 2.
    public static func opencodeStatus(exitCode: Int32, output: Data, version: String? = nil) -> CLIAccountStatus {
        if let version, !OpencodeAgent.isSupported(version: version) {
            return CLIAccountStatus(provider: "opencode", detail: L("settings.cliAccounts.detailOpencodeV1"), canSignOut: false)
        }
        guard exitCode == 0, let listed = OpencodeAgent.connections(listJSON: output) else {
            return CLIAccountStatus(provider: "opencode", detail: exitCode == -1 ? L("settings.cliAccounts.detailOpencodeTimeout") : L("settings.cliAccounts.detailOpencodeUnknown"), canSignOut: false)
        }
        guard !listed.integrations.isEmpty else {
            return CLIAccountStatus(provider: "opencode", loggedIn: false, detail: L("settings.cliAccounts.detailOpencodeFreeOnly"), canSignOut: false)
        }
        let method = listed.environmentOnly ? L("cliAccounts.method.opencodeEnvironmentKey") : listed.subscription ? L("cliAccounts.method.opencodeSubscription") : L("windows.cli.method.apiKey")
        return CLIAccountStatus(provider: "opencode", loggedIn: true, method: method, methodId: listed.subscription ? CLIAccountMethod.opencodeSubscription : CLIAccountMethod.opencodeAPIKey,
                                account: listed.integrations.prefix(16).joined(separator: ", "), detail: L("settings.cliAccounts.detailOpencodeSignOut"), canSignOut: false)
    }
}

// MARK: Resumable sessions

extension ResumableSessions {
    /// The root sessions OpenCode 2's `opencode.db` keeps for the workspace folder
    /// (`session_v2.directory`), newest first; the first user message is the title when
    /// opencode's own is still its placeholder, and the request count is the session's user
    /// messages. Read-only. OpenCode 1's tables, which 2 migrates in the background, are not read.
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

    /// `session_v2` rows without a parent (sub-agent sessions have one), not archived, in one
    /// of `directories`, updated since `since`; `model` is the session's `{providerID, id}`.
    /// nil when the database is missing or unreadable.
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
            let query = "SELECT id, title, time_updated, model FROM session_v2 WHERE parent_id IS NULL AND time_archived IS NULL AND directory IN (\(marks)) AND time_updated >= ? ORDER BY time_updated DESC LIMIT \(max(0, limit))"
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
                rows.append(Row(id: id, title: placeholderTitle(title) ? "" : ActivitySupport.clean(title, maximumBytes: 512, singleLine: true), updated: updated, model: model(json(statement, 3))))
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

    static func model(_ value: [String: Any]?) -> String? {
        guard let provider = value?["providerID"] as? String, let id = value?["id"] as? String, CoreValidation.model(provider + "/" + id) else { return nil }
        return provider + "/" + id
    }
    private static func json(_ statement: OpaquePointer, _ column: Int32) -> [String: Any]? {
        guard let bytes = sqlite3_column_blob(statement, column) else { return nil }
        let count = Int(sqlite3_column_bytes(statement, column))
        guard count <= 1_048_576 else { return nil }
        return (try? JSONSerialization.jsonObject(with: Data(bytes: bytes, count: count))) as? [String: Any]
    }

    /// The session's user messages (`session_message.type = user`): their count and the first
    /// one's `data.text`.
    private static func details(_ db: OpaquePointer, row: inout Row) {
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT data FROM session_message WHERE session_id = ? AND type = 'user' ORDER BY seq LIMIT 20000", -1, &statement, nil) == SQLITE_OK, let statement else { sqlite3_finalize(statement); return }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_text(statement, 1, row.id, -1, transient)
        var count = 0
        while sqlite3_step(statement) == SQLITE_ROW {
            count += 1
            if count == 1, let text = json(statement, 0)?["text"] as? String {
                let prompt = text.trimmingCharacters(in: .whitespacesAndNewlines)
                row.prompt = prompt.isEmpty ? nil : prompt
            }
        }
        row.requests = count
    }
}
