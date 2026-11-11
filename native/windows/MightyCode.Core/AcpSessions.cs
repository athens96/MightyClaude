using System.Globalization;
using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// Where OpenCode 2 (<c>@opencode/cli</c>) keeps its database, and what "이전 세션 이어가기" reads of it: the root sessions of a folder
/// (M/MightyCore/OpencodeSupport.swift OpencodeAgent.database and OpencodeSessions). Read-only, through the system's SQLite; the app never
/// writes it. OpenCode 1's tables, which 2 migrates into <c>session_v2</c> in the background, are not read.
/// </summary>
public static class OpencodeSessions
{
    /// <summary>
    /// OpenCode 2's data folder: <c>$XDG_DATA_HOME/opencode</c> when that is a full path, else <c>&lt;home&gt;/.local/share/opencode</c>. On Windows
    /// too: 2.2.7 has no Windows branch, so there it is <c>%USERPROFILE%\.local\share\opencode</c>.
    /// </summary>
    public static string DataDirectory(IReadOnlyDictionary<string, string> environment, string home) =>
        environment.GetValueOrDefault("XDG_DATA_HOME") is { Length: > 0 } data && Absolute(data) ? Path.Combine(data, "opencode") : Path.Combine(home, ".local", "share", "opencode");

    /// <summary>
    /// The database (sessions and sign-ins): <c>$OPENCODE_DB</c> when set, a relative value resolved against the data folder as opencode's own
    /// <c>path.resolve</c> does; else <c>opencode.db</c> in the data folder (the release channels' name). Null for <c>:memory:</c>, which keeps nothing on disk.
    /// </summary>
    public static string? Database(IReadOnlyDictionary<string, string> environment, string home)
    {
        var data = DataDirectory(environment, home);
        if (environment.GetValueOrDefault("OPENCODE_DB") is not { Length: > 0 } given) return Path.Combine(data, "opencode.db");
        if (given == ":memory:") return null;
        try { return Path.GetFullPath(Path.Combine(data, given)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A root session: opencode's title ("" while it is the placeholder), its last update, its user messages (null when not counted), the first one's text, its model.</summary>
    internal sealed record Row(string Id, string Title, DateTimeOffset Updated, int? Requests, string? Prompt, string? Model);

    /// <summary>
    /// <c>session_v2</c> rows without a parent (a sub-agent's session has one), not archived, in one of <paramref name="directories"/> (any letter
    /// case and either slash on Windows), updated since <paramref name="since"/>, newest first. With <paramref name="counts"/> also the session's
    /// user messages (<c>session_message.type = user</c>): how many, and the first one's <c>data.text</c>. Null when the database is missing or unreadable.
    /// </summary>
    internal static List<Row>? Rows(string database, IReadOnlyList<string> directories, DateTimeOffset since, int limit, bool counts)
    {
        var folders = OperatingSystem.IsWindows() ? directories.Concat(directories.Select(d => d.Replace('\\', '/'))).Distinct(StringComparer.Ordinal).ToList() : directories.ToList();
        if (folders.Count == 0) return null;
        var collate = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : "";
        // A JSON value is read only up to 1 MiB, as the Mac's reader takes it.
        var details = counts
            ? ", (SELECT COUNT(*) FROM session_message m WHERE m.session_id = s.id AND m.type = 'user'), "
              + "(SELECT CASE WHEN length(CAST(m.data AS BLOB)) <= 1048576 THEN m.data END FROM session_message m WHERE m.session_id = s.id AND m.type = 'user' ORDER BY m.seq LIMIT 1)"
            : "";
        var sql = "SELECT s.id, s.title, s.time_updated, CASE WHEN length(CAST(s.model AS BLOB)) <= 1048576 THEN s.model END" + details
            + $" FROM session_v2 s WHERE s.parent_id IS NULL AND s.time_archived IS NULL AND s.directory{collate} IN ({string.Join(",", folders.Select(_ => "?"))})"
            + " AND s.time_updated >= CAST(? AS INTEGER) ORDER BY s.time_updated DESC LIMIT " + Math.Max(0, limit).ToString(CultureInfo.InvariantCulture);
        if (SystemSqlite.ReadOnly(database, sql, counts ? 6 : 4, [.. folders, since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)]) is not { } rows) return null;
        var result = new List<Row>();
        foreach (var row in rows)
        {
            if (row[0] is not { } id || Milliseconds(row[2]) is not { } updated) continue;
            var title = row[1] ?? "";
            int? requests = counts ? int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? Math.Min(count, 20_000) : 0 : null;
            result.Add(new Row(id, PlaceholderTitle(title) ? "" : ActivitySupport.Clean(title, 512, singleLine: true), updated, requests, counts ? Prompt(row[5]) : null, Model(row[3])));
        }
        return result;
    }

    /// <summary>opencode names a new session "New session - &lt;time&gt;" (a sub-agent's "Child session …") until it writes a real title.</summary>
    internal static bool PlaceholderTitle(string title) =>
        title.Length == 0 || title.StartsWith("new session", StringComparison.OrdinalIgnoreCase) || title.StartsWith("child session", StringComparison.OrdinalIgnoreCase);

    /// <summary>The session's <c>{providerID, id}</c> as <c>provider/model</c>.</summary>
    internal static string? Model(string? json) => Object(json, value => value.Text("providerID") is { } provider && value.Text("id") is { } id && Wire.Model(provider + "/" + id) ? provider + "/" + id : null);

    /// <summary>A user message's <c>data.text</c>, trimmed; null when it has none.</summary>
    private static string? Prompt(string? json) => Object(json, value => value.Text("text")?.Trim() is { Length: > 0 } text ? text : null);

    private static string? Object(string? json, Func<JsonElement, string?> read)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : null;
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? Milliseconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms is >= 0 and < 253_402_300_800_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    private static bool Absolute(string path) => path.StartsWith('/') || Path.IsPathFullyQualified(path);
}

/// <summary>
/// Hermes Agent's <c>state.db</c>: where it is, the conversations "이전 세션 이어가기" lists, and the two facts a Hermes run reads before it
/// continues a session (M/MightyCore/HermesSupport.swift HermesAgent.home, database, messageCount, startedWithFullAccess and HermesSessions).
/// Read-only, through the system's SQLite; the app never writes into Hermes's home.
/// </summary>
public static class HermesSessions
{
    /// <summary>The <c>state_meta</c> key of a session's creation receipt (hermes_state_local.py <c>POLICY_PREFIX</c>).</summary>
    private const string PolicyPrefix = "gateway.local_policy.v1:";
    /// <summary>What the app writes after the user's request while Hermes's question tool is on (the run side's prompt note): not the user's words.</summary>
    private const string NoteStart = "[Note from the Mighty Code app, not from the user:";

    /// <summary>
    /// Hermes's home: <c>$HERMES_HOME</c> when that is a full path, else the platform default (hermes_constants.py
    /// <c>_platform_default_hermes_home</c>): <c>%LOCALAPPDATA%\hermes</c> on Windows, <c>~/.hermes</c> elsewhere, with
    /// <c>$HERMES_DATA_DIR_SUFFIX</c> after the name. Without LOCALAPPDATA in <paramref name="environment"/>, this user's local application data folder.
    /// </summary>
    public static string Home(IReadOnlyDictionary<string, string> environment) => Home(environment, null);

    /// <param name="home">The user folder the default is found under when <paramref name="environment"/> does not name one (a session list's own
    /// home, as Hermes uses <c>&lt;home&gt;\AppData\Local</c>); null: this user's.</param>
    internal static string Home(IReadOnlyDictionary<string, string> environment, string? home)
    {
        if (environment.GetValueOrDefault("HERMES_HOME")?.Trim() is { Length: > 0 } given && Absolute(given)) return given;
        var name = (OperatingSystem.IsWindows() ? "hermes" : ".hermes") + environment.GetValueOrDefault("HERMES_DATA_DIR_SUFFIX", "");
        if (OperatingSystem.IsWindows())
        {
            var local = environment.GetValueOrDefault("LOCALAPPDATA")?.Trim() is { Length: > 0 } value ? value
                : home is not null ? Path.Combine(home, "AppData", "Local") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, name);
        }
        return Path.Combine(home ?? (environment.GetValueOrDefault("HOME") is { Length: > 0 } user && Absolute(user) ? user : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), name);
    }

    /// <summary>Sessions, messages and each session's frozen launch policy: <c>state.db</c> in <see cref="Home(IReadOnlyDictionary{string, string})"/>.</summary>
    public static string StateDatabase(IReadOnlyDictionary<string, string> environment) => StateDatabase(environment, null);
    internal static string StateDatabase(IReadOnlyDictionary<string, string> environment, string? home) => Path.Combine(Home(environment, home), "state.db");

    /// <summary>
    /// The messages the session holds (<c>sessions.message_count</c>): 0 for one whose turns all failed or never ran, which a run does not continue
    /// (it keeps the provider it was created with). Null when the database or the session is not there, or cannot be read.
    /// </summary>
    public static int? MessageCount(string database, string sessionId)
    {
        if (!Wire.SessionIdentifier(sessionId)) return null;
        return SystemSqlite.ReadOnly(database, "SELECT COALESCE(message_count, 0) FROM sessions WHERE id = ?", 1, sessionId) is [[var count, ..], ..]
            && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>
    /// Whether Hermes created the session with <c>--yolo</c> (dangerous commands not asked about): the <c>yolo</c> of the creation request its
    /// gateway keeps in <c>state_meta</c> (<c>gateway.local_policy.v1:&lt;id&gt;</c> → <c>policy.request_json</c>). <c>hermes acp</c> freezes it when
    /// it creates the session, so a resume cannot change it. Null when the database or the record is missing or cannot be read.
    /// </summary>
    public static bool? StartedWithFullAccess(string database, string sessionId)
    {
        if (!Wire.SessionIdentifier(sessionId)) return null;
        if (SystemSqlite.ReadOnly(database, "SELECT CASE WHEN length(CAST(value AS BLOB)) <= 1048576 THEN value END FROM state_meta WHERE key = ?", 1, PolicyPrefix + sessionId)
            is not [[{ } raw, ..], ..]) return null;
        try
        {
            using var receipt = JsonDocument.Parse(raw);
            if (receipt.RootElement.ValueKind != JsonValueKind.Object) return null;
            var policy = MetadataJson.Property(receipt.RootElement, "policy") is { ValueKind: JsonValueKind.Object } inner ? inner : receipt.RootElement;
            if (policy.Text("request_json") is { } text)
            {
                try
                {
                    using var request = JsonDocument.Parse(text);
                    if (request.RootElement.ValueKind == JsonValueKind.Object) return MetadataJson.Flag(request.RootElement, "yolo");
                }
                catch (JsonException) { }
            }
            return MetadataJson.Property(policy, "request") is { ValueKind: JsonValueKind.Object } given ? MetadataJson.Flag(given, "yolo") : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A stored request without the app's note after it, as the session list shows it: cut where the note starts, then trimmed.</summary>
    internal static string WithoutNote(string text) => text.IndexOf(NoteStart, StringComparison.Ordinal) is >= 0 and var start ? text[..start].Trim() : text;

    /// <summary>A conversation: its first row's id and title, its latest time, its user messages (null when not counted), the first one's text, its model.</summary>
    internal sealed record Row(string Id, string Title, DateTimeOffset Updated, int? Requests, string? Prompt, string? Model);

    /// <summary>One <c>sessions</c> row: a whole conversation, or one part of a compressed one.</summary>
    private sealed record Segment(string? Parent, string Title, double Updated, long Messages, string? Model, string? Cwd);

    /// <summary>A chain of segments under its first one, whose title, model and folder it keeps.</summary>
    private sealed class Conversation(string title, double updated, string? model, string? cwd)
    {
        public string Title = title;
        public double Updated = updated;
        public long Messages;
        public readonly string? Model = model, Cwd = cwd;
        public readonly List<string> Members = [];
    }

    /// <summary>
    /// The sessions an ACP client created (<c>source</c> or <c>created_source</c> is <c>acp</c>; Hermes's ACP <c>session/list</c> shows the same),
    /// not archived or hidden, as conversations: a compressed one is several rows chained by <c>parent_session_id</c>, listed once under its first
    /// row's id (the id ACP resumes) with the latest time of the chain. Kept are conversations with messages, active since <paramref name="since"/>,
    /// whose folder (<c>cwd</c>, else <c>model_config.cwd</c>) <paramref name="here"/> takes; newest first. Columns a Hermes version lacks are left
    /// out of the query (<c>PRAGMA table_info</c>). With <paramref name="counts"/> also each conversation's user messages: how many, the first one's
    /// text (the app's note taken off), and the newest message's time. Null when the database is missing or unreadable.
    /// </summary>
    internal static List<Row>? Rows(string database, Func<string, bool> here, DateTimeOffset since, int limit, bool counts, CancellationToken token = default)
    {
        if (SystemSqlite.ReadOnly(database, "PRAGMA table_info(sessions)", 2) is not { } info) return null;
        var have = info.Select(column => column[1]).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!have.IsSupersetOf(["id", "source", "started_at"])) return null;
        string Column(string name, string fallback = "NULL") => have.Contains(name) ? name : fallback;
        var filters = new List<string> { have.Contains("created_source") ? "(source = 'acp' OR created_source = 'acp')" : "source = 'acp'" };
        foreach (var flag in new[] { "archived", "hidden" }) if (have.Contains(flag)) filters.Add($"COALESCE({flag}, 0) = 0");
        // Older rows keep the folder in `model_config` alone: read only where `cwd` is empty, and only up to 1 MiB.
        var (cwd, config) = (Column("cwd"), Column("model_config"));
        var sql = $"SELECT id, {Column("parent_session_id")}, {Column("title")}, started_at, {Column("last_activity_at")}, {Column("message_count", "1")}, {Column("model")}, {cwd}, "
            + $"CASE WHEN {cwd} IS NULL AND length(CAST({config} AS BLOB)) <= 1048576 THEN {config} END FROM sessions WHERE {string.Join(" AND ", filters)} "
            + "ORDER BY started_at DESC LIMIT " + Math.Max(0, limit).ToString(CultureInfo.InvariantCulture);
        if (SystemSqlite.ReadOnly(database, sql, 9) is not { } rows) return null;
        var segments = new Dictionary<string, Segment>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var row in rows)
        {
            if (row[0] is not { } id || segments.ContainsKey(id)) continue;
            segments[id] = new Segment(row[1], ActivitySupport.Clean(row[2], 512, singleLine: true), Math.Max(Number(row[3]) ?? 0, Number(row[4]) ?? 0),
                (long)Math.Clamp(Number(row[5]) ?? 0, 0, int.MaxValue), row[6], row[7] ?? ConfigFolder(row[8]));
            order.Add(id);
        }
        // A conversation is its chain's first row; later rows (compression) only add to it.
        string Root(string id)
        {
            var current = id;
            for (var hops = 0; hops < 64 && segments[current].Parent is { } parent && segments.ContainsKey(parent); hops++) current = parent;
            return current;
        }
        var conversations = new Dictionary<string, Conversation>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var id in order)
        {
            var segment = segments[id];
            var key = Root(id);
            if (!conversations.TryGetValue(key, out var conversation))
            {
                var first = segments[key];
                conversations[key] = conversation = new Conversation(first.Title, segment.Updated, first.Model is { } model && Wire.Model(model) ? model : null, first.Cwd);
                roots.Add(key);
            }
            conversation.Members.Add(id);
            conversation.Messages += segment.Messages;
            conversation.Updated = Math.Max(conversation.Updated, segment.Updated);
            if (conversation.Title.Length == 0) conversation.Title = segment.Title;
        }
        var result = new List<Row>();
        foreach (var key in roots)
        {
            if (token.IsCancellationRequested) break;
            var conversation = conversations[key];
            if (conversation.Messages <= 0 || Time(conversation.Updated) is not { } updated || updated < since || conversation.Cwd is not { Length: > 0 } folder || !here(folder)) continue;
            var row = new Row(key, conversation.Title, updated, null, null, conversation.Model);
            result.Add(counts ? Details(database, conversation.Members, row) : row);
        }
        return result.OrderByDescending(row => row.Updated).ToList();
    }

    /// <summary>
    /// The conversation's user messages, as many as the Mac counts, and the first with text (a message with attachments is a JSON list of parts:
    /// its text parts are the prompt), read with the newest message's time, which <c>last_activity_at</c> is not (it stays near the start).
    /// </summary>
    private static Row Details(string database, IReadOnlyList<string> members, Row row)
    {
        var ids = members.Take(64).ToArray();
        var marks = string.Join(",", ids.Select(_ => "?"));
        // One read: the count and the newest time (kind 0), then the first few user messages up to 1 MiB each (kind 1), with their ids for their order.
        var sql = $"SELECT 0, (SELECT COUNT(*) FROM (SELECT 1 FROM messages WHERE session_id IN ({marks}) AND role = 'user' LIMIT 20000)), (SELECT MAX(timestamp) FROM messages WHERE session_id IN ({marks})) "
            + $"UNION ALL SELECT * FROM (SELECT 1, id, content FROM messages WHERE session_id IN ({marks}) AND role = 'user' AND length(CAST(content AS BLOB)) <= 1048576 ORDER BY id LIMIT 16)";
        if (SystemSqlite.ReadOnly(database, sql, 3, [.. ids, .. ids, .. ids]) is not { } rows || rows.FirstOrDefault(r => r[0] == "0") is not { } totals) return row;
        var updated = Number(totals[2]) is { } newest && Time(newest) is { } time && time > row.Updated ? time : row.Updated;
        var requests = int.TryParse(totals[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
        string? prompt = null;
        foreach (var message in rows.Where(r => r[0] == "1").OrderBy(r => long.TryParse(r[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : long.MaxValue))
        {
            if (message[2] is not { } content) continue;
            var text = content.StartsWith('[') && Parts(content) is { } parts ? parts : content;
            if (WithoutNote(text).Trim() is not { Length: > 0 } words) continue;
            prompt = words.Length > 2_000 ? words[..(char.IsHighSurrogate(words[1_999]) ? 1_999 : 2_000)] : words;
            break;
        }
        return row with { Updated = updated, Requests = requests, Prompt = prompt };
    }

    /// <summary>The text parts of a message stored as a JSON list of parts, joined; null when it is not such a list.</summary>
    private static string? Parts(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.EnumerateArray().Any(part => part.ValueKind != JsonValueKind.Object)) return null;
            return string.Join(" ", document.RootElement.EnumerateArray().Select(part => part.Text("text")).OfType<string>());
        }
        catch (JsonException) { return null; }
    }

    private static string? ConfigFolder(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Text("cwd");
        }
        catch (JsonException) { return null; }
    }

    private static double? Number(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    private static DateTimeOffset? Time(double seconds) => seconds is >= 0 and < 253_402_300_800 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1_000)) : null;
    private static bool Absolute(string path) => path.StartsWith('/') || Path.IsPathFullyQualified(path);
}
