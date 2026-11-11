using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// "이전 세션 이어가기" for opencode and Hermes, and the two facts a Hermes run reads before it continues a session, ported from the Mac's
/// OpencodeTests.rootSessionsOfTheWorkspaceFolderAreListedFromTheDatabase and HermesTests.acpConversationsOfTheWorkspaceFolderAreListedFromStateDB.
/// The databases are written here with the system's SQLite in the shapes OpenCode 2.2.7 (<c>session_v2</c>, <c>session_message</c>) and
/// Hermes 0.23.1 (<c>sessions</c>, <c>messages</c>, <c>state_meta</c>) keep them; every id, path and text is made up.
/// </summary>
internal static class AcpSessionsVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static Dictionary<string, string> Env(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
    private static string Text(string value) => "'" + value.Replace("'", "''") + "'";
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Ids(ResumableSessionListing listing) => string.Join("|", listing.Items.Select(i => i.SessionID)) + " hidden " + listing.Hidden;

    // ── opencode ──────────────────────────────────────────────────────────

    private const string Session = "ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw", Placeholder = "ses_8a0000000000Ab0000000000Cd", Child = "ses_7a0000000000Ab0000000000Ce",
        Elsewhere = "ses_6a0000000000Ab0000000000Cf", Archived = "ses_5a0000000000Ab0000000000Cg", Old = "ses_4a0000000000Ab0000000000Ch",
        Unused = "ses_3a0000000000Ab0000000000Ci", Automated = "ses_2a0000000000Ab0000000000Cj", Blank = "ses_1a0000000000Ab0000000000Ck",
        Spelled = "ses_0a0000000000Ab0000000000Cl";

    internal static Task OpencodeListsTheRootSessionsOfTheWorkspaceFolder()
    {
        var root = Verification.Temp();
        try
        {
            var workspace = Path.Combine(root, "work", "project"); Directory.CreateDirectory(workspace);
            var home = Path.Combine(root, "home");
            var database = OpencodeSessions.Database(Env(), home) ?? throw new InvalidOperationException("opencode.db has a place");
            var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string Row(string id, string? parent, string directory, string title, string? model, long updated, long? archived = null) =>
                $"INSERT INTO session_v2 VALUES ({Text(id)}, 'p', {(parent is null ? "NULL" : Text(parent))}, 's', {Text(directory)}, {Text(title)}, '2.2.7', {(model is null ? "NULL" : Text(model))}, {updated - 9_000}, {updated}, {(archived is { } at ? at.ToString(CultureInfo.InvariantCulture) : "NULL")})";
            var seq = 0;
            string Message(string session, string type, string data) => $"INSERT INTO session_message VALUES ('msg_{++seq}', {Text(session)}, {Text(type)}, {seq}, 1, 1, {Text(data)})";
            // A folder spelled with the other slash and letter case is the same folder on Windows alone.
            var spelled = (OperatingSystem.IsWindows() ? workspace.Replace('\\', '/') : workspace).ToUpperInvariant();
            Check(TestSqlite.Write(database,
                "CREATE TABLE session_v2 (id TEXT PRIMARY KEY, project_id TEXT, parent_id TEXT, slug TEXT, directory TEXT, title TEXT, version TEXT, model TEXT, time_created INTEGER, time_updated INTEGER, time_archived INTEGER)",
                "CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, seq INTEGER, time_created INTEGER, time_updated INTEGER, data TEXT)",
                Row(Session, null, workspace, "Fix the failing test", """{"id":"claude-sonnet-4-5","providerID":"anthropic"}""", ms - 1_000),
                Row(Placeholder, null, workspace, "New session - 2026-11-10T10:00:00.000Z", null, ms - 2_000),
                Row(Child, Session, workspace, "Child session - 2026-11-10T10:01:00.000Z", null, ms - 500),
                Row(Elsewhere, null, Path.Combine(root, "elsewhere"), "Other folder", null, ms - 500),
                Row(Archived, null, workspace, "Archived", null, ms - 500, archived: ms),
                Row(Old, null, workspace, "Two months ago", null, ms - (long)TimeSpan.FromDays(61).TotalMilliseconds),
                Row(Unused, null, workspace, "Planning", null, ms - 3_000),
                Row(Automated, null, workspace, "Nested run", null, ms - 4_000),
                Row(Blank, null, workspace, "Rename the module", "{}", ms - 5_000),
                Row(Spelled, null, spelled, "Spelled differently", null, ms - 6_000),
                Message(Session, "user", """{"text":"why does main return 1?","files":[]}"""),
                Message(Session, "assistant", """{"agent":"build","content":[]}"""),
                Message(Session, "user", """{"text":"and now?"}"""),
                Message(Placeholder, "user", """{"text":"  hello\n"}"""),
                Message(Child, "user", """{"text":"a sub-agent's task"}"""),
                Message(Elsewhere, "user", """{"text":"elsewhere"}"""),
                Message(Archived, "user", """{"text":"archived"}"""),
                Message(Old, "user", """{"text":"long ago"}"""),
                Message(Automated, "user", """{"text":"User: earlier text\nAssistant: more"}"""),
                Message(Blank, "user", """{"text":"   "}"""),
                Message(Spelled, "user", """{"text":"spelled"}""")), "the system's SQLite wrote the fixture opencode.db");

            var query = new ResumableSessionQuery(workspace) { Home = home };
            var listing = ResumableSessions.Listing(query, "opencode");
            string[] expected = OperatingSystem.IsWindows() ? [Session, Placeholder, Blank, Spelled] : [Session, Placeholder, Blank];
            Check(listing.Items.Select(i => i.SessionID).SequenceEqual(expected) && listing.Hidden == 1,
                "this folder's root sessions with a request, newest first, without children, other folders, archived or two-month-old ones: " + Ids(listing));
            var first = listing.Items[0];
            Check(first.Provider == "opencode" && first.Title == "why does main return 1?" && first.Requests == 2 && first.Model == "anthropic/claude-sonnet-4-5"
                && first.Path == database && !first.Automated && Math.Abs((first.Modified - DateTimeOffset.FromUnixTimeMilliseconds(ms - 1_000)).TotalMilliseconds) < 1, "the first user message titles the row; its user messages are the requests");
            Check(listing.Items[1].Title == "hello" && listing.Items[1].Requests == 1 && listing.Items[1].Model is null, "a placeholder title gives way to the request");
            Check(listing.Items[2].Title == "Rename the module" && listing.Items[2].Requests == 1 && listing.Items[2].Model is null, "a blank request leaves opencode's own title, and a model without its two names is none");

            Check(ResumableSessions.Listing(query with { Excluding = new HashSet<string> { Session.ToLowerInvariant() } }, "opencode").Items.All(i => i.SessionID != Session), "an open pane's session is left out");
            var all = ResumableSessions.Listing(query with { IncludeAutomated = true }, "opencode");
            Check(all.Hidden == 0 && all.Items.Single(i => i.SessionID == Automated).Automated, "every session shown: the nested run too, marked");
            Check(ResumableSessions.Listing(query with { Known = new HashSet<string> { Automated.ToLowerInvariant() } }, "opencode").Items.Any(i => i.SessionID == Automated && !i.Automated), "a session the app's own pane ran is never hidden");
            var head = ResumableSessions.Listing(query with { HeadOnly = true }, "opencode");
            Check(head.Items.All(i => i.Requests is null) && head.Items.Select(i => i.SessionID).Take(5).SequenceEqual([Session, Placeholder, Unused, Automated, Blank])
                && head.Items[0].Title == "Fix the failing test" && head.Items[1].Title is null, "a yes/no look-up reads no messages: opencode's titles, no counts: " + Ids(head));
            Check(ResumableSessions.Listing(query with { MaximumSessions = 1 }, "opencode").Items.Single().SessionID == Session, "the list stops at its size");
            Check(ResumableSessions.Listing(query).Items.Select(i => i.SessionID).SequenceEqual(expected), "the whole list has opencode's sessions");
            Check(ResumableSessions.Providers.Contains("opencode") && ResumableSessions.OffersResume("claude", "opencode") && AddPaneMenu.MayAskResume("opencode", fromMenu: true), "창 추가 asks first for opencode");
            var pane = ResumableSessions.Apply(first, new RunSession { WorkspaceId = "space", Title = "opencode", Provider = "opencode" });
            Check(pane.ResumeId == Session && pane.Title == "why does main return 1?", "continuing one resumes that session");
            Check(Wire.SessionIdentifier(Session) && !Wire.SessionIdentifier("ses_../x") && KnownSessionIDs.Adding(Session, []) is [Session], "opencode's ids are session ids, remembered as the app's own");

            Check(ResumableSessions.Listing(query with { Home = root }, "opencode").Items.Count == 0, "no database (opencode never ran): nothing to list");
            Check(ResumableSessions.Listing(query with { Environment = Env(("OPENCODE_DB", ":memory:")) }, "opencode").Items.Count == 0, "an in-memory database keeps nothing to list");
            Check(ResumableSessions.Listing(query with { Home = root, Environment = Env(("OPENCODE_DB", database)) }, "opencode").Items.Count == expected.Length, "OPENCODE_DB names the database");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task OpencodeFindsItsDatabaseWhereOpenCodeKeepsIt()
    {
        var home = Path.Combine(Path.GetTempPath(), "mighty-opencode-home");
        var data = Path.Combine(home, ".local", "share", "opencode");
        Check(OpencodeSessions.DataDirectory(Env(), home) == data && OpencodeSessions.Database(Env(), home) == Path.Combine(data, "opencode.db"), "<home>/.local/share/opencode/opencode.db, on Windows too");
        var xdg = Path.Combine(home, "xdg");
        Check(OpencodeSessions.Database(Env(("XDG_DATA_HOME", xdg)), home) == Path.Combine(xdg, "opencode", "opencode.db"), "XDG_DATA_HOME moves the data folder");
        Check(OpencodeSessions.Database(Env(("XDG_DATA_HOME", "relative")), home) == Path.Combine(data, "opencode.db"), "a relative XDG_DATA_HOME names no folder the app can know");
        var elsewhere = Path.Combine(home, "db", "sessions.db");
        Check(OpencodeSessions.Database(Env(("OPENCODE_DB", elsewhere), ("XDG_DATA_HOME", xdg)), home) == elsewhere, "OPENCODE_DB names the file");
        Check(OpencodeSessions.Database(Env(("OPENCODE_DB", "work.db")), home) == Path.Combine(data, "work.db")
            && OpencodeSessions.Database(Env(("OPENCODE_DB", "work.db"), ("XDG_DATA_HOME", xdg)), home) == Path.Combine(xdg, "opencode", "work.db"), "a relative OPENCODE_DB is in the data folder");
        Check(OpencodeSessions.Database(Env(("OPENCODE_DB", ":memory:")), home) is null, ":memory: keeps nothing on disk");
        Check(OpencodeSessions.Database(Env(("OPENCODE_DB", "")), home) == Path.Combine(data, "opencode.db"), "an empty value is no value");
        Check(OpencodeSessions.PlaceholderTitle("New session - 2026-11-10T10:00:00.000Z") && OpencodeSessions.PlaceholderTitle("child session - x") && OpencodeSessions.PlaceholderTitle("")
            && !OpencodeSessions.PlaceholderTitle("Fix the build"), "opencode's placeholder titles");
        Check(OpencodeSessions.Model("""{"id":"gpt-5","providerID":"openai","variant":"high"}""") == "openai/gpt-5" && OpencodeSessions.Model("""{"id":"x"}""") is null
            && OpencodeSessions.Model("not json") is null, "the model is provider/model");
        Check(new[] { "XDG_DATA_HOME", "OPENCODE_DB", "HERMES_HOME", "LOCALAPPDATA" }.All(ResumableSessions.StoreVariables.Contains), "a session list carries the variables these databases are found by");
        return Task.CompletedTask;
    }

    // ── Hermes ────────────────────────────────────────────────────────────

    /// <summary>The sessions of the Mac's recordings (HermesTests.answerSession, resumedSession).</summary>
    private const string Main = "local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b", Earlier = "local-a8a2537a19ce1905c0a99559ca0f2e86197234e68b0931bf190fc6517e91386e";
    private static readonly string Continued = "local-" + new string('7', 64), Noted = "local-" + new string('c', 64), Pictured = "local-" + new string('d', 64),
        Nested = "local-" + new string('e', 64), Cased = "local-" + new string('f', 64), Unknown = "local-" + new string('0', 64);

    /// <summary>A creation receipt as Hermes 0.23.1 keeps it in <c>state_meta</c>; <c>yolo</c> is there only for a session created with it.</summary>
    private static string Receipt(string id, bool yolo)
    {
        var request = new Dictionary<string, object> { ["cwd"] = "/tmp/project", ["editor"] = new Dictionary<string, object>(), ["source"] = "acp" };
        if (yolo) request["yolo"] = true;
        var policy = new Dictionary<string, object> { ["cwd"] = "/tmp/project", ["edit_approval_policy"] = "ask", ["request_json"] = JsonSerializer.Serialize(request), ["source"] = "acp" };
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["policy"] = policy, ["session_id"] = id });
    }

    /// <summary>Writes the fixture <c>state.db</c> (WAL, as Hermes keeps it) for <paramref name="workspace"/>; <c>t</c> is now in seconds.</summary>
    private static void WriteState(string database, string workspace, string root, double t)
    {
        const string columns = "id, source, created_source, model, model_config, parent_session_id, started_at, message_count, cwd, title, last_activity_at, archived, hidden";
        string Row(string id, string source, string? created, string? model, string? config, string? parent, double started, int messages, string? cwd, string? title, double? active, int archived = 0, int hidden = 0)
        {
            static string Maybe(string? value) => value is null ? "NULL" : Text(value);
            return $"INSERT INTO sessions ({columns}) VALUES ({Text(id)}, {Text(source)}, {Maybe(created)}, {Maybe(model)}, {Maybe(config)}, {Maybe(parent)}, {Number(started)}, {messages}, {Maybe(cwd)}, {Maybe(title)}, {(active is { } a ? Number(a) : "NULL")}, {archived}, {hidden})";
        }
        string Message(string session, string role, string content, double timestamp) => $"INSERT INTO messages (session_id, role, content, timestamp) VALUES ({Text(session)}, {Text(role)}, {Text(content)}, {Number(timestamp)})";
        var old = t - TimeSpan.FromDays(61).TotalSeconds;
        Check(TestSqlite.Write(database,
            "PRAGMA journal_mode=WAL",
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, created_source TEXT, model TEXT, model_config TEXT, parent_session_id TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0, cwd TEXT, title TEXT, last_activity_at REAL, archived INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0)",
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL)",
            "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)",
            // The owner stores an editor's thread as `local`, created by `acp`.
            Row(Main, "local", "acp", "anthropic/claude-sonnet-4.5", null, null, t - 900, 4, workspace, "Clean the build", t - 800),
            // Its compressed continuation: the same conversation, resumed under the first id.
            Row(Continued, "local", "acp", "anthropic/claude-sonnet-4.5", null, Main, t - 300, 2, workspace, null, t - 100),
            // An older row keeps its folder in `model_config` alone.
            Row(Earlier, "acp", null, null, JsonSerializer.Serialize(new { cwd = workspace }), null, t - 5_000, 2, null, null, null),
            Row(Noted, "local", "acp", null, null, null, t - 2_000, 2, workspace, "Quiz me", t - 1_990),
            Row(Pictured, "local", "acp", null, null, null, t - 3_000, 2, workspace, "Screenshot question", t - 2_990),
            Row(Nested, "local", "acp", null, null, null, t - 4_000, 2, workspace, "Nested", t - 3_990),
            Row(Cased, "local", "acp", null, null, null, t - 6_000, 2, workspace.ToUpperInvariant(), "Cased", t - 5_990),
            Row("20261110_090000_0000cli1", "cli", "cli", null, null, null, t - 50, 2, workspace, "A terminal chat", t - 40),
            Row("20261110_090000_0000else", "acp", "acp", null, null, null, t - 50, 2, Path.Combine(root, "elsewhere"), "Other folder", t - 40),
            Row("20261110_090000_0000arch", "acp", "acp", null, null, null, t - 50, 2, workspace, "Archived", t - 40, archived: 1),
            Row("20261110_090000_0000hide", "acp", "acp", null, null, null, t - 50, 2, workspace, "Hidden", t - 40, hidden: 1),
            Row("20261110_090000_0000none", "acp", "acp", null, null, null, t - 50, 0, workspace, "Never used", t - 40),
            Row("20261110_090000_00000old", "acp", "acp", null, null, null, old, 2, workspace, "Two months ago", old + 100),
            Message(Main, "user", "clean the build folder", 1),
            Message(Main, "assistant", "done", 2),
            Message(Continued, "user", "and write notes", t - 20),
            Message(Earlier, "user", """[{"type":"text","text":"what is in this picture?"},{"type":"image_url"}]""", 4),
            Message(Noted, "user", "선택하는 문제를 내\n\n[Note from the Mighty Code app, not from the user: this pane shows your clarify tool as a card the user answers with a click.]", t - 1_900),
            Message(Pictured, "user", """[{"type":"image_url"}]""", t - 2_900),
            Message(Nested, "user", "User: earlier text\nAssistant: more", t - 3_900),
            Message(Cased, "user", "cased", t - 5_900),
            Message("20261110_090000_00000old", "user", "long ago", old + 50),
            $"INSERT INTO state_meta VALUES ({Text("gateway.local_policy.v1:" + Main)}, {Text(Receipt(Main, yolo: true))})",
            $"INSERT INTO state_meta VALUES ({Text("gateway.local_policy.v1:" + Earlier)}, {Text(Receipt(Earlier, yolo: false))})",
            "INSERT INTO state_meta VALUES ('gateway.local_policy.v1:local-" + new string('9', 64) + "', 'not json')"), "the system's SQLite wrote the fixture state.db");
    }

    internal static Task HermesListsTheAcpConversationsOfTheWorkspaceFolder()
    {
        var root = Verification.Temp();
        try
        {
            var workspace = Path.Combine(root, "work", "project"); Directory.CreateDirectory(workspace);
            var home = Path.Combine(root, "home");
            var database = HermesSessions.StateDatabase(Env(), home);
            var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1_000.0;
            WriteState(database, workspace, root, t);
            var query = new ResumableSessionQuery(workspace) { Home = home };
            var listing = ResumableSessions.Listing(query, "hermes");
            string[] expected = OperatingSystem.IsWindows() ? [Main, Noted, Pictured, Earlier, Cased] : [Main, Noted, Pictured, Earlier];
            Check(listing.Items.Select(i => i.SessionID).SequenceEqual(expected) && listing.Hidden == 1,
                "this folder's ACP conversations with messages, newest first, the compressed one once, without terminal, archived, hidden, empty or two-month-old ones: " + Ids(listing));
            var first = listing.Items[0];
            Check(first.Provider == "hermes" && first.Title == "clean the build folder" && first.Requests == 2 && first.Model == "anthropic/claude-sonnet-4.5" && first.Path == database,
                "the first user message titles the conversation; the user messages of its whole chain are the requests: " + first);
            // `last_activity_at` stays near the start: the newest message dates the conversation.
            Check(Math.Abs(first.Modified.ToUnixTimeMilliseconds() / 1_000.0 - (t - 20)) < 1, "the newest message dates the conversation");
            Check(listing.Items[1].Title == "선택하는 문제를 내" && listing.Items[1].Requests == 1, "the app's note after a request is not its title");
            Check(listing.Items[2].Title == "Screenshot question" && listing.Items[2].Requests == 1, "a request of pictures alone leaves Hermes's own title");
            Check(listing.Items[3].Title == "what is in this picture?" && listing.Items[3].Model is null, "a request with attachments is titled by its text");

            Check(ResumableSessions.Listing(query with { Excluding = new HashSet<string> { Main.ToLowerInvariant() } }, "hermes").Items.All(i => i.SessionID != Main), "an open pane's conversation is left out");
            var all = ResumableSessions.Listing(query with { IncludeAutomated = true }, "hermes");
            Check(all.Hidden == 0 && all.Items.Single(i => i.SessionID == Nested).Automated, "every session shown: the nested run too, marked");
            var head = ResumableSessions.Listing(query with { HeadOnly = true }, "hermes");
            Check(head.Items.All(i => i.Requests is null) && head.Items[0].SessionID == Main && head.Items[0].Title == "Clean the build"
                && Math.Abs(head.Items[0].Modified.ToUnixTimeMilliseconds() / 1_000.0 - (t - 100)) < 1, "a yes/no look-up reads no messages: Hermes's titles and times: " + Ids(head));
            Check(ResumableSessions.Listing(query).Items.Select(i => i.SessionID).SequenceEqual(expected), "the whole list has Hermes's conversations");
            Check(ResumableSessions.Providers.Contains("hermes") && ResumableSessions.OffersResume("claude", "hermes") && AddPaneMenu.MayAskResume("hermes", fromMenu: true), "창 추가 asks first for Hermes");
            Check(ResumableSessions.Listing(query with { Home = root }, "hermes").Items.Count == 0, "no state.db (Hermes never ran): nothing to list");
            Check(ResumableSessions.Listing(query with { Home = root, Environment = Env(("HERMES_HOME", Path.GetDirectoryName(database)!)) }, "hermes").Items.Count == expected.Length, "HERMES_HOME moves the store");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task HermesReadsYoloAndTheMessageCountOfASession()
    {
        var root = Verification.Temp();
        try
        {
            var workspace = Path.Combine(root, "work", "project"); Directory.CreateDirectory(workspace);
            var database = Path.Combine(root, "hermes", "state.db");
            WriteState(database, workspace, root, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1_000.0);
            // Whether a session was created with --yolo, which a resume cannot change.
            Check(HermesSessions.StartedWithFullAccess(database, Main) == true, "a session created with yolo");
            Check(HermesSessions.StartedWithFullAccess(database, Earlier) == false, "a session created without it");
            Check(HermesSessions.StartedWithFullAccess(database, Unknown) is null && HermesSessions.StartedWithFullAccess(database, "local-" + new string('9', 64)) is null
                && HermesSessions.StartedWithFullAccess(database, "local-../x") is null && HermesSessions.StartedWithFullAccess(Path.Combine(root, "missing.db"), Main) is null, "no record, an unreadable one, a bad id or no database: unknown");
            // A session whose turns all failed holds no messages and is not one to continue.
            Check(HermesSessions.MessageCount(database, Main) == 4 && HermesSessions.MessageCount(database, "20261110_090000_0000none") == 0, "the messages a session holds");
            Check(HermesSessions.MessageCount(database, Unknown) is null && HermesSessions.MessageCount(database, "local-../x") is null && HermesSessions.MessageCount(Path.Combine(root, "missing.db"), Main) is null,
                "no session, a bad id or no database: unknown");
            Check(HermesSessions.WithoutNote("선택하는 문제를 내\n\n[Note from the Mighty Code app, not from the user: call clarify.]") == "선택하는 문제를 내"
                && HermesSessions.WithoutNote(" plain ") == " plain ", "the app's note comes off a stored request; a request without it stays as it is");
            // Policy-only receipts (an older shape): the request object itself, or the receipt without its `policy` wrapper.
            var other = Path.Combine(root, "older", "state.db");
            Check(TestSqlite.Write(other, "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)",
                $"INSERT INTO state_meta VALUES ({Text("gateway.local_policy.v1:" + Main)}, {Text("""{"policy":{"request":{"yolo":true}}}""")})",
                $"INSERT INTO state_meta VALUES ({Text("gateway.local_policy.v1:" + Earlier)}, {Text("""{"request_json":"{\"yolo\":false}"}""")})"), "the system's SQLite wrote the older receipts");
            Check(HermesSessions.StartedWithFullAccess(other, Main) == true && HermesSessions.StartedWithFullAccess(other, Earlier) == false && HermesSessions.MessageCount(other, Main) is null,
                "a request object, a receipt without its wrapper; no sessions table: no count");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task HermesReadsOnlyTheColumnsItsVersionHas()
    {
        var root = Verification.Temp();
        try
        {
            var workspace = Path.Combine(root, "work", "project"); Directory.CreateDirectory(workspace);
            var home = Path.Combine(root, "home");
            var database = HermesSessions.StateDatabase(Env(), home);
            var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1_000.0;
            string older = "local-" + new string('1', 64), terminal = "local-" + new string('2', 64);
            // No created_source, cwd, title, last_activity_at, archived or hidden: the folder is in model_config, and only `acp` rows are editors'.
            Check(TestSqlite.Write(database,
                "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, model TEXT, model_config TEXT, parent_session_id TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0)",
                "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL)",
                $"INSERT INTO sessions VALUES ({Text(older)}, 'acp', 'openrouter/qwen-3', {Text(JsonSerializer.Serialize(new { cwd = workspace }))}, NULL, {Number(t - 60)}, 2)",
                $"INSERT INTO sessions VALUES ({Text(terminal)}, 'cli', NULL, {Text(JsonSerializer.Serialize(new { cwd = workspace }))}, NULL, {Number(t - 30)}, 2)",
                $"INSERT INTO messages (session_id, role, content, timestamp) VALUES ({Text(older)}, 'user', 'an older Hermes', {Number(t - 50)})",
                $"INSERT INTO messages (session_id, role, content, timestamp) VALUES ({Text(terminal)}, 'user', 'a terminal chat', {Number(t - 20)})"), "the system's SQLite wrote the older state.db");
            var query = new ResumableSessionQuery(workspace) { Home = home };
            var listing = ResumableSessions.Listing(query, "hermes");
            Check(listing.Items.Select(i => i.SessionID).SequenceEqual([older]) && listing.Items[0].Title == "an older Hermes" && listing.Items[0].Requests == 1 && listing.Items[0].Model == "openrouter/qwen-3",
                "an older state.db lists through the columns it has: " + Ids(listing));
            File.Delete(database);
            Check(TestSqlite.Write(database, "CREATE TABLE other (id TEXT)"), "the system's SQLite wrote a state.db without sessions");
            Check(ResumableSessions.Listing(query, "hermes").Items.Count == 0 && HermesSessions.MessageCount(database, older) is null && HermesSessions.StartedWithFullAccess(database, older) is null,
                "a state.db without Hermes's tables lists nothing and reads nothing");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task HermesFindsItsHomeWhereHermesKeepsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mighty-hermes-home");
        var local = Path.Combine(root, "Local"); var home = Path.Combine(root, "user"); var custom = Path.Combine(root, "custom hermes");
        var suffix = ("HERMES_DATA_DIR_SUFFIX", "-channel-build-beta");
        if (OperatingSystem.IsWindows())
        {
            Check(HermesSessions.Home(Env(("LOCALAPPDATA", local))) == Path.Combine(local, "hermes"), "%LOCALAPPDATA%\\hermes");
            Check(HermesSessions.Home(Env()) == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes"), "without LOCALAPPDATA, this user's local application data");
            Check(HermesSessions.StateDatabase(Env(), home) == Path.Combine(home, "AppData", "Local", "hermes", "state.db"), "a session list's own home: <home>\\AppData\\Local\\hermes, as Hermes falls back");
            Check(HermesSessions.Home(Env(("LOCALAPPDATA", local), suffix)) == Path.Combine(local, "hermes-channel-build-beta"), "HERMES_DATA_DIR_SUFFIX follows the name");
        }
        else
        {
            Check(HermesSessions.Home(Env(("HOME", home))) == Path.Combine(home, ".hermes"), "~/.hermes");
            Check(HermesSessions.StateDatabase(Env(), home) == Path.Combine(home, ".hermes", "state.db"), "a session list's own home");
            Check(HermesSessions.Home(Env(("HOME", home), suffix)) == Path.Combine(home, ".hermes-channel-build-beta"), "HERMES_DATA_DIR_SUFFIX follows the name");
        }
        Check(HermesSessions.Home(Env(("HERMES_HOME", custom), ("LOCALAPPDATA", local), ("HOME", home), suffix)) == custom
            && HermesSessions.Home(Env(("HERMES_HOME", " " + custom + " "))) == custom, "HERMES_HOME moves the home");
        Check(HermesSessions.StateDatabase(Env(("HERMES_HOME", custom))) == Path.Combine(custom, "state.db"), "state.db is in the home");
        Check(HermesSessions.Home(Env(("HERMES_HOME", "relative"), ("LOCALAPPDATA", local), ("HOME", home))) == HermesSessions.Home(Env(("LOCALAPPDATA", local), ("HOME", home))), "a relative HERMES_HOME names no folder the app can know");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes a fixture database with the system's own SQLite (<c>sqlite3_exec</c>, Windows' <c>winsqlite3.dll</c>, macOS's and Linux's
/// <c>libsqlite3</c>), so the checks need no database package. False when there is no SQLite or a statement fails.
/// </summary>
internal static class TestSqlite
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Open(byte[] filename, out nint db, int flags, nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Exec(nint db, byte[] sql, nint callback, nint argument, nint error);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Close(nint db);

    internal static bool Write(string path, params string[] statements)
    {
        var names = OperatingSystem.IsWindows() ? new[] { "winsqlite3.dll" } : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib"] : ["libsqlite3.so.0"];
        var library = names.Select(name => NativeLibrary.TryLoad(name, out var handle) ? handle : 0).FirstOrDefault(handle => handle != 0);
        if (library == 0) return false;
        var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "sqlite3_open_v2"));
        var exec = Marshal.GetDelegateForFunctionPointer<Exec>(NativeLibrary.GetExport(library, "sqlite3_exec"));
        var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "sqlite3_close"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
        // SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE
        if (open(Utf8(path), out var db, 0x2 | 0x4, 0) != 0) { if (db != 0) close(db); return false; }
        try { return statements.All(sql => exec(db, Utf8(sql), 0, 0, 0) == 0); }
        finally { close(db); }
    }
}
