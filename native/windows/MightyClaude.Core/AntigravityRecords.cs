using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// A sub-agent an Antigravity run started: the stream's <c>subagent_info</c> (<c>invoke_subagent</c>) or a nested one its
/// parent's own transcript names (M/MightyCore/AntigravityRecords.swift AntigravitySubagent). <paramref name="Parent"/> is the
/// conversation that started it: the run's own, or another sub-agent's.
/// </summary>
public sealed record AntigravitySubagent(string Conversation, string Parent, string? Title, string? Prompt);

/// <summary>
/// What Antigravity CLI (<c>agy</c>) keeps on disk, read-only (M/MightyCore/AntigravityRecords.swift). Each conversation has
/// <c>%USERPROFILE%\.gemini\antigravity-cli\brain\&lt;conversation id&gt;\.system_generated\logs\transcript_full.jsonl</c>: one line
/// per step (<c>step_index</c>, <c>source</c>, <c>type</c> USER_INPUT / PLANNER_RESPONSE / GENERIC / SYSTEM_MESSAGE, <c>status</c>,
/// <c>created_at</c>, <c>content</c>, <c>tool_calls</c>, token counts, <c>error</c>). A PLANNER_RESPONSE's tool calls are answered
/// by the GENERIC steps that follow it, in order. A sub-agent is a conversation of its own whose instructions arrive as
/// SYSTEM_MESSAGE steps from its parent. <c>conversation_summaries.db</c> lists the conversations with their workspaces and,
/// for sub-agents, their parent.
/// </summary>
public static partial class AntigravityRecords
{
    public static string Folder(string home) => Path.Combine(home, ".gemini", "antigravity-cli");
    public static string Brain(string home) => Path.Combine(Folder(home), "brain");
    public static string Database(string home) => Path.Combine(Folder(home), "conversation_summaries.db");
    [GeneratedRegex(@"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z")]
    private static partial Regex Uuid();
    [GeneratedRegex(@"""conversationId"":\s*""(?<id>[0-9A-Fa-f-]{36})""")]
    private static partial Regex Spawned();
    public static string? ConversationID(string? value) => value is not null && Uuid().IsMatch(value) ? value : null;
    /// <summary>Null for anything that is not a conversation id, so no path leaves the brain folder.</summary>
    public static string? Transcript(string brain, string conversation) =>
        ConversationID(conversation) is null ? null : Path.Combine(brain, conversation, ".system_generated", "logs", "transcript_full.jsonl");
    /// <summary>The brain folder a transcript lies in.</summary>
    public static string BrainOf(string transcript)
    {
        var path = transcript;
        for (var i = 0; i < 4; i++) path = Path.GetDirectoryName(path) ?? path;
        return path;
    }

    /// <summary>What the user typed: the <c>&lt;USER_REQUEST&gt;</c> part of a USER_INPUT step.</summary>
    public static string? Request(string? content)
    {
        if (content is null) return null;
        var text = content;
        var start = content.IndexOf("<USER_REQUEST>", StringComparison.Ordinal);
        if (start >= 0)
        {
            var from = start + "<USER_REQUEST>".Length;
            var end = content.IndexOf("</USER_REQUEST>", from, StringComparison.Ordinal);
            text = content[from..(end < 0 ? content.Length : end)];
        }
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
    /// <summary>
    /// The request a USER_INPUT line opens, with when it was written. Lines without the word are rejected on their raw bytes
    /// first; however agy spaces its JSON, a line that has it is parsed.
    /// </summary>
    public static (string Prompt, string? Timestamp, string? Key)? Opening(byte[] line)
    {
        if (!HistoryScan.Contains(line, "USER_INPUT") || HistoryScan.Parse(line) is not { } value || value.Text("type") != "USER_INPUT" || value.Text("source") == "SYSTEM"
            || Request(value.Text("content")) is not { } prompt) return null;
        return (prompt, value.Text("created_at"), MetadataJson.Integer(value, "step_index") is { } index ? "step" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }
    /// <summary>The text of a <c>[Message] … sender=&lt;id&gt; … content=…</c> system message from <paramref name="sender"/>.</summary>
    public static string? Message(string? content, string sender)
    {
        if (content is null) return null;
        var start = content.IndexOf("sender=" + sender + " ", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var body = content.IndexOf(" content=", start + 8 + sender.Length, StringComparison.Ordinal);
        if (body < 0) return null;
        var text = content[(body + " content=".Length)..];
        var end = text.LastIndexOf("</SYSTEM_MESSAGE>", StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
    /// <summary>A tool step's text without agy's "Created At:" / "Completed At:" header.</summary>
    public static string? ToolText(string? content)
    {
        if (content is null) return null;
        var lines = (content.Length > 65_536 ? content[..65_536] : content).Split('\n').ToList();
        while (lines.Count > 0 && (lines[0].StartsWith("Created At: ", StringComparison.Ordinal) || lines[0].StartsWith("Completed At: ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lines[0]))) lines.RemoveAt(0);
        var text = string.Join('\n', lines).Trim();
        return text.Length == 0 ? null : text;
    }
    /// <summary>A step's own status: DONE (or none) went through, ERROR failed, and anything else (CANCELED, INTERRUPTED, …) was cut short.</summary>
    public enum Outcome { Done, Failed, Stopped }
    public static Outcome OutcomeOf(string? status) => (status ?? "DONE").ToUpperInvariant() switch { "DONE" => Outcome.Done, "ERROR" => Outcome.Failed, _ => Outcome.Stopped };
    /// <summary>The stream state the live parser reads for that status.</summary>
    public static string StreamState(Outcome outcome) => outcome switch { Outcome.Done => "DONE", Outcome.Failed => "ERROR", _ => "CANCELED" };
    /// <summary>When a step was written, null when it does not say.</summary>
    public static DateTimeOffset? Date(JsonElement step) => AgentRunTiming.Parse(step.Text("created_at"));
    public static string? ErrorText(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.Text("message");
        return string.IsNullOrEmpty(text) ? null : text;
    }
    /// <summary>"File Reader (research)", as agy names a sub-agent's messages.</summary>
    public static string? Title(string? role, string? typeName)
    {
        var name = role?.Trim() ?? ""; var type = typeName?.Trim() ?? "";
        if (name.Length == 0) return type.Length == 0 ? null : type;
        return type.Length == 0 || type == name ? name : name + " (" + type + ")";
    }
    /// <summary>A recorded tool call's arguments as the live stream's parameters (no <c>toolAction</c> / <c>toolSummary</c>).</summary>
    public static JsonElement Parameters(JsonElement args)
    {
        var result = new JsonObject();
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var field in args.EnumerateObject())
                if (field.Name is not ("toolAction" or "toolSummary")) result[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        return JsonSerializer.SerializeToElement(result);
    }
    /// <summary>A recorded tool call's one-line summary: what the live stream's parameters say, else agy's own <c>toolSummary</c>.</summary>
    public static string Summary(string tool, JsonElement args)
    {
        var summary = ActivitySupport.Summary(tool, OutputParser.AntigravityParameters(Parameters(args)));
        if (summary == ActivitySupport.Clean(tool, ActivitySupport.MaximumSummaryBytes, true) && args.Text("toolSummary") is { Length: > 0 } described) return ActivitySupport.Clean(described, ActivitySupport.MaximumSummaryBytes, true);
        return summary;
    }
    /// <summary>The conversation ids an <c>invoke_subagent</c> result step names.</summary>
    public static List<string> SpawnedConversations(string? content) => content is null ? []
        : Spawned().Matches(content.Length > 65_536 ? content[..65_536] : content).Select(match => ConversationID(match.Groups["id"].Value)).OfType<string>().ToList();
    /// <summary>A step's token counts (<c>input_tokens</c> without <c>cache_read_tokens</c>, which is its own field) as a block's usage.</summary>
    public static GraphTokenUsage? Usage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var mapped = new JsonObject();
        foreach (var (source, target) in new[] { ("input_tokens", "input_tokens"), ("output_tokens", "output_tokens"), ("cache_read_tokens", "cache_read_input_tokens") })
            if (MetadataJson.Property(value, source) is { ValueKind: JsonValueKind.Number } number) mapped[target] = JsonNode.Parse(number.GetRawText());
        return GraphTokenUsage.Parse(JsonSerializer.SerializeToElement(mapped));
    }

    // ── replaying a recorded request ───────────────────────────────────────

    private static JsonElement Step(Dictionary<string, object?> fields) => JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["event"] = "step_update", ["step_update"] = fields });
    private static List<JsonElement> Calls(JsonElement step) =>
        MetadataJson.Property(step, "tool_calls") is { ValueKind: JsonValueKind.Array } calls ? calls.EnumerateArray().Take(64).ToList() : [];

    /// <summary>
    /// One recorded request's steps (after its USER_INPUT) as the stream-json events agy printed for them, so a replay goes
    /// through the live parser and graph. The request completed when its last step is an answer without tool calls, failed when
    /// that answer failed, and was cut short when it was cancelled or ended on a tool step (one that was declined, say: the
    /// failed tool shows as such).
    /// </summary>
    public static (List<JsonElement> Events, string Status) StreamEvents(IReadOnlyList<JsonElement> steps, string conversation)
    {
        var events = new List<JsonElement> { JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["event"] = "init", ["conversation_id"] = conversation, ["init"] = new Dictionary<string, object?>() }) };
        var calls = new List<(string Name, JsonElement Args)>();
        var unanswered = 0;
        JsonElement Open((string Name, JsonElement Args) call)
        {
            unanswered--;
            return Step(new() { ["step_index"] = unanswered, ["state"] = "ACTIVE", ["step_type"] = "tool", ["tool_name"] = call.Name, ["tool_info"] = new Dictionary<string, object?> { ["name"] = call.Name, ["parameters"] = Parameters(call.Args) } });
        }
        JsonElement? last = null;
        foreach (var step in steps)
        {
            if (step.Text("type") is not { } type || MetadataJson.Integer(step, "step_index") is not { } index) continue;
            var outcome = OutcomeOf(step.Text("status")); var state = StreamState(outcome);
            switch (type)
            {
                case "PLANNER_RESPONSE":
                {
                    // Calls the model moved past without a result step stay open.
                    events.AddRange(calls.Select(Open)); calls.Clear();
                    var fields = new Dictionary<string, object?> { ["step_index"] = index, ["state"] = state, ["step_type"] = "agent_response" };
                    if (step.Text("content") is { Length: > 0 } text) fields["text_delta"] = text;
                    var usage = new Dictionary<string, object?>();
                    foreach (var key in new[] { "input_tokens", "output_tokens", "cache_read_tokens" }) if (MetadataJson.Property(step, key) is { ValueKind: JsonValueKind.Number } number) usage[key] = number;
                    if (usage.Count > 0) fields["usage"] = usage;
                    events.Add(Step(fields));
                    foreach (var call in Calls(step))
                        if (call.Text("name") is { Length: > 0 } name) calls.Add((name, MetadataJson.Property(call, "args")));
                    break;
                }
                case "GENERIC":
                {
                    if (calls.Count == 0) continue;
                    var call = calls[0]; calls.RemoveAt(0);
                    if (call.Name == "invoke_subagent")
                    {
                        var specs = MetadataJson.Property(call.Args, "Subagents") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToList() : [];
                        var subagents = SpawnedConversations(step.Text("content")).Select((id, offset) =>
                        {
                            var spec = offset < specs.Count ? specs[offset] : default;
                            return new Dictionary<string, object?> { ["conversation_id"] = id, ["role"] = spec.Text("Role") ?? "", ["type_name"] = spec.Text("TypeName") ?? "", ["initial_prompt"] = spec.Text("Prompt") ?? "" };
                        }).ToList();
                        events.Add(Step(new() { ["step_index"] = index, ["state"] = state, ["step_type"] = "subagent", ["tool_name"] = call.Name, ["subagent_info"] = new Dictionary<string, object?> { ["subagents"] = subagents } }));
                    }
                    else
                    {
                        var info = new Dictionary<string, object?> { ["name"] = call.Name, ["parameters"] = Parameters(call.Args) };
                        if (outcome == Outcome.Failed) info["error"] = new Dictionary<string, object?> { ["message"] = ErrorText(MetadataJson.Property(step, "error")) ?? ToolText(step.Text("content")) ?? "" };
                        // Command output is shown as the live run shows it; other results (file contents) are not copied.
                        else if (outcome == Outcome.Done && ActivitySupport.Kind(call.Name) == "command" && ToolText(step.Text("content")) is { } output) info["output"] = output;
                        events.Add(Step(new() { ["step_index"] = index, ["state"] = state, ["step_type"] = "tool", ["tool_name"] = call.Name, ["tool_info"] = info }));
                    }
                    break;
                }
                default: continue;
            }
            last = step;
        }
        events.AddRange(calls.Select(Open));
        if (last is not { } final || final.Text("type") != "PLANNER_RESPONSE") return (events, "stopped");
        switch (OutcomeOf(final.Text("status")))
        {
            case Outcome.Stopped: return (events, "stopped");
            case Outcome.Failed:
                events.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["event"] = "result", ["result"] = new Dictionary<string, object?> { ["conversation_id"] = conversation, ["status"] = "ERROR",
                    ["error"] = new Dictionary<string, object?> { ["message"] = ErrorText(MetadataJson.Property(final, "error")) ?? Locale.Get("run.error.providerRunFailed", new Dictionary<string, string> { ["name"] = ProviderMark.Label("gemini") }) } } }));
                return (events, "error");
        }
        if (Calls(final).Count > 0) return (events, "stopped");
        events.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["event"] = "result", ["result"] = new Dictionary<string, object?> { ["conversation_id"] = conversation, ["status"] = "SUCCESS" } }));
        return (events, "completed");
    }

    // ── the conversation list ──────────────────────────────────────────────

    /// <param name="Paths">Folders the conversation works in (the workspace and any <c>--add-dir</c>).</param>
    /// <param name="Parent">Set for a sub-agent's conversation.</param>
    public sealed record ConversationSummary(string Conversation, string Title, IReadOnlyList<string> Paths, string Parent);

    /// <summary>
    /// Every conversation <c>conversation_summaries.db</c> lists, read-only, newest rows first. While agy runs the database is in
    /// WAL mode and is read through its shared memory; when agy is not running the file alone is read (<c>immutable</c>). Null when
    /// the database is missing or unreadable, or the system has no SQLite (Windows' own <c>winsqlite3.dll</c>).
    /// </summary>
    public static List<ConversationSummary>? Summaries(string database, int limit = 20_000)
    {
        if (!RecordFiles.Regular(database) || Sqlite.Load() is not { } sqlite) return null;
        foreach (var mode in new[] { "mode=ro", "immutable=1" })
        {
            var uri = new Uri(Path.GetFullPath(database)).AbsoluteUri + "?" + mode;
            if (sqlite.Query(uri, "SELECT conversation_id, title, workspace_uris, parent_conversation_id FROM conversation_summaries ORDER BY rowid DESC LIMIT " + Math.Max(0, limit).ToString(System.Globalization.CultureInfo.InvariantCulture), 4) is not { } rows) continue;
            var summaries = new List<ConversationSummary>();
            foreach (var row in rows)
            {
                if (ConversationID(row[0]) is not { } id) continue;
                var paths = new List<string>();
                try
                {
                    using var json = JsonDocument.Parse(row[2] ?? "[]");
                    if (json.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var item in json.RootElement.EnumerateArray().Take(32))
                            if (item.ValueKind == JsonValueKind.String && Uri.TryCreate(item.GetString(), UriKind.Absolute, out var folder) && folder.IsFile) paths.Add(folder.LocalPath);
                }
                catch (JsonException) { }
                summaries.Add(new(id, row[1] ?? "", paths, row[3] ?? ""));
            }
            return summaries;
        }
        return null;
    }

    /// <summary>
    /// The few SQLite calls the conversation list needs, bound at run time to the system's own library (Windows ships
    /// <c>winsqlite3.dll</c>; macOS and Linux have <c>libsqlite3</c>), so the app carries no database package. Read-only.
    /// </summary>
    private sealed class Sqlite
    {
        private const int Ok = 0, Row = 100, Done = 101, OpenReadOnly = 0x1, OpenUri = 0x40, OpenNoMutex = 0x8000;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int OpenV2(byte[] filename, out nint db, int flags, nint vfs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Close(nint db);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int BusyTimeout(nint db, int milliseconds);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PrepareV2(nint db, byte[] sql, int bytes, out nint statement, nint tail);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int StepStatement(nint statement);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint ColumnText(nint statement, int column);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ColumnBytes(nint statement, int column);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FinalizeStatement(nint statement);
        private readonly OpenV2 open; private readonly Close close; private readonly BusyTimeout busy; private readonly PrepareV2 prepare;
        private readonly StepStatement step; private readonly ColumnText text; private readonly ColumnBytes length; private readonly FinalizeStatement finalize;
        private static readonly Lazy<Sqlite?> Shared = new(Bind);
        private Sqlite(nint library)
        {
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            open = Get<OpenV2>("sqlite3_open_v2"); close = Get<Close>("sqlite3_close"); busy = Get<BusyTimeout>("sqlite3_busy_timeout");
            prepare = Get<PrepareV2>("sqlite3_prepare_v2"); step = Get<StepStatement>("sqlite3_step"); text = Get<ColumnText>("sqlite3_column_text");
            length = Get<ColumnBytes>("sqlite3_column_bytes"); finalize = Get<FinalizeStatement>("sqlite3_finalize");
        }
        internal static Sqlite? Load() => Shared.Value;
        private static Sqlite? Bind()
        {
            var names = OperatingSystem.IsWindows() ? new[] { "winsqlite3.dll" } : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib"] : ["libsqlite3.so.0", "libsqlite3.so"];
            foreach (var name in names)
            {
                if (!NativeLibrary.TryLoad(name, out var library)) continue;
                try { return new Sqlite(library); }
                catch (EntryPointNotFoundException) { NativeLibrary.Free(library); }
            }
            return null;
        }
        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
        /// <summary>Every row's first <paramref name="columns"/> columns as text, or null when the database could not be opened or read to the end.</summary>
        internal List<string?[]>? Query(string uri, string sql, int columns)
        {
            if (open(Utf8(uri), out var db, OpenReadOnly | OpenUri | OpenNoMutex, 0) != Ok || db == 0) { if (db != 0) close(db); return null; }
            try
            {
                busy(db, 250);
                if (prepare(db, Utf8(sql), -1, out var statement, 0) != Ok || statement == 0) { if (statement != 0) finalize(statement); return null; }
                try
                {
                    var rows = new List<string?[]>(); int result;
                    while ((result = step(statement)) == Row)
                    {
                        var row = new string?[columns];
                        for (var column = 0; column < columns; column++)
                        {
                            var pointer = text(statement, column);
                            row[column] = pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer, length(statement, column));
                        }
                        rows.Add(row);
                    }
                    return result == Done ? rows : null;
                }
                finally { finalize(statement); }
            }
            finally { close(db); }
        }
    }
}

/// <summary>
/// One Antigravity sub-agent's own conversation, read as it grows (M/MightyCore/AntigravityRecords.swift AntigravityChildRecord):
/// its parent's instructions, its messages and tool calls, what it sent back (<c>send_message</c>) and its usage. Snapshots carry
/// only the entries changed since the previous one, as Codex's session records do. Tool results are shown for commands and
/// failures only; file contents are never copied. A sub-agent that started helpers of its own is not done while any of them
/// has not reported back: their messages continue its work.
/// </summary>
public sealed class AntigravityChildRecord(AntigravitySubagent subagent, string ns)
{
    public AntigravitySubagent Subagent { get; } = subagent;
    private readonly List<LogEntry> entries = [];
    private readonly HashSet<string> changed = [];
    private readonly List<GraphResponseRecord> usage = [];
    private bool dirty;
    private string? input = subagent.Prompt, output;
    private string state = "running";
    private int turns;
    /// <summary>Tool calls waiting for their result step, oldest first.</summary>
    private readonly List<(string Entry, string Name, JsonElement Args)> calls = [];
    private string? answer, message;
    /// <summary>Sub-agents this one started that were not handed out yet; those it started; those that have not reported back.</summary>
    private readonly List<AntigravitySubagent> spawned = [];
    private readonly List<string> started = [];
    private readonly HashSet<string> waiting = new(StringComparer.OrdinalIgnoreCase);

    public List<AntigravitySubagent> TakeSpawned() { var value = spawned.ToList(); spawned.Clear(); return value; }
    public CodexSessionAgent? Take()
    {
        if (!dirty) return null;
        dirty = false;
        var value = new CodexSessionAgent(Subagent.Conversation, Subagent.Parent, Subagent.Title, string.IsNullOrEmpty(input) ? null : input,
            entries.Where(e => changed.Contains(e.Id)).ToArray(), new HashSet<string>(), output, state, turns, usage.ToArray());
        changed.Clear(); usage.Clear();
        return value;
    }

    private string Key(string suffix) => ActivitySupport.Id(ns, "agy:" + Subagent.Conversation + ":" + suffix);
    private void Upsert(LogEntry entry)
    {
        var index = entries.FindIndex(e => e.Id == entry.Id);
        if (index >= 0)
        {
            entry = entry with { Timestamp = entries[index].Timestamp };
            if (entries[index] == entry) return;
            entries[index] = entry;
        }
        else
        {
            entries.Add(entry);
            while (entries.Count > ExecutionGraphSupport.MaximumEntries) { changed.Remove(entries[0].Id); entries.RemoveAt(0); }
        }
        changed.Add(entry.Id); dirty = true;
    }
    private void Activity(string id, string tool, JsonElement args, string toolState, string stamp, string? toolOutput = null)
    {
        if (ActivitySupport.Normalize(new(id, "gemini", ActivitySupport.Kind(tool), toolState, AntigravityRecords.Summary(tool, args), tool, toolOutput)) is not { } value) return;
        Upsert(new(id, "system", value.Summary, stamp, "gemini", value));
    }

    public void Consume(JsonElement step)
    {
        if (step.Text("type") is not { } type || MetadataJson.Integer(step, "step_index") is not { } index) return;
        var stamp = step.Text("created_at") is { } written && AgentRunTiming.Parse(written) is not null ? written : Wire.Now();
        var outcome = AntigravityRecords.OutcomeOf(step.Text("status"));
        var before = (state, output, turns, input);
        var key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (type)
        {
            case "SYSTEM_MESSAGE" when AntigravityRecords.Message(step.Text("content"), Subagent.Parent) is null:
                // A helper it started reported back: the same assignment goes on.
                if (started.FirstOrDefault(helper => AntigravityRecords.Message(step.Text("content"), helper) is not null) is not { } reported) return;
                waiting.Remove(reported);
                if (state == "completed") state = "running";
                break;
            case "SYSTEM_MESSAGE" or "USER_INPUT":
            {
                // Each instruction from the parent (or the user) is a new assignment.
                if ((type == "USER_INPUT" ? AntigravityRecords.Request(step.Text("content")) : AntigravityRecords.Message(step.Text("content"), Subagent.Parent)) is not { } text) return;
                turns++;
                if (turns == 1 && string.IsNullOrEmpty(input)) input = text;
                else if (turns > 1) Upsert(new(Key(key), "user", text, stamp, "gemini"));
                answer = null; message = null; state = "running";
                break;
            }
            case "PLANNER_RESPONSE":
            {
                if (step.Text("content")?.Trim() is { Length: > 0 } text) { Upsert(new(Key(key), "assistant", text, stamp, "gemini")); answer = text; }
                var ids = new List<string>();
                var offset = 0;
                foreach (var call in MetadataJson.Property(step, "tool_calls") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Take(64) : [])
                {
                    var position = offset++;
                    if (call.Text("name") is not { Length: > 0 } name) continue;
                    var args = MetadataJson.Property(call, "args"); var id = Key(key + ":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Activity(id, name, args, "running", stamp);
                    calls.Add((id, name, args)); ids.Add(id);
                    // What it sends its parent is its answer to the assignment.
                    if (name == "send_message" && string.Equals(args.Text("Recipient"), Subagent.Parent, StringComparison.OrdinalIgnoreCase) && args.Text("Message")?.Trim() is { Length: > 0 } sent) message = sent;
                }
                if (AntigravityRecords.Usage(step) is { } tokens) { usage.Add(new("agy:" + Subagent.Conversation + ":" + key, null, tokens, ids)); dirty = true; }
                switch (outcome)
                {
                    case AntigravityRecords.Outcome.Failed:
                        state = "error";
                        if (AntigravityRecords.ErrorText(MetadataJson.Property(step, "error")) is { } error) Upsert(new(Key(key + ":error"), "error", error, stamp, "gemini"));
                        break;
                    case AntigravityRecords.Outcome.Stopped: state = "stopped"; break;
                    // An answer while its helpers still work ("waiting for …") is not the end.
                    default: state = ids.Count == 0 && waiting.Count == 0 ? "completed" : "running"; break;
                }
                break;
            }
            case "GENERIC":
            {
                if (calls.Count == 0) return;
                var call = calls[0]; calls.RemoveAt(0);
                var result = outcome == AntigravityRecords.Outcome.Failed ? AntigravityRecords.ErrorText(MetadataJson.Property(step, "error")) ?? AntigravityRecords.ToolText(step.Text("content"))
                    : outcome == AntigravityRecords.Outcome.Done && ActivitySupport.Kind(call.Name) == "command" ? AntigravityRecords.ToolText(step.Text("content")) : null;
                Activity(call.Entry, call.Name, call.Args, outcome switch { AntigravityRecords.Outcome.Done => "completed", AntigravityRecords.Outcome.Failed => "error", _ => "stopped" }, stamp, result);
                if (call.Name == "invoke_subagent" && outcome == AntigravityRecords.Outcome.Done)
                {
                    var specs = MetadataJson.Property(call.Args, "Subagents") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToList() : [];
                    var conversations = AntigravityRecords.SpawnedConversations(step.Text("content"));
                    for (var offset = 0; offset < conversations.Count; offset++)
                    {
                        var id = conversations[offset]; var spec = offset < specs.Count ? specs[offset] : default;
                        if (!started.Contains(id)) started.Add(id);
                        waiting.Add(id);
                        spawned.Add(new(id, Subagent.Conversation, AntigravityRecords.Title(spec.Text("Role"), spec.Text("TypeName")), spec.Text("Prompt")));
                    }
                }
                break;
            }
            default: return;
        }
        output = message ?? (state == "completed" ? answer : null);
        if (before != (state, output, turns, input)) dirty = true;
    }
}

/// <summary>
/// Follows an Antigravity run's sub-agents through their own transcripts (M/MightyCore/AntigravityRecords.swift
/// AntigravitySubagentWatcher). Every read is bounded; calls are serialized.
/// </summary>
public sealed class AntigravitySubagentWatcher
{
    public sealed record Limits(int BytesPerPoll = 4 * 1024 * 1024, int FinalBytes = 32 * 1024 * 1024, int MaximumLineBytes = 2 * 1024 * 1024, int MaximumChildren = ExecutionGraphSupport.MaximumNodes);
    private sealed class Child(string path, AntigravityChildRecord record)
    {
        internal readonly string Path = path;
        internal readonly AntigravityChildRecord Record = record;
        internal long Offset;
        internal bool Skipping;
        /// <summary>The file shrank; transcripts only grow, so it is not read again.</summary>
        internal bool Closed;
    }
    private readonly object sync = new();
    private readonly string brain, ns;
    private readonly Limits limits;
    private readonly List<Child> children = [];
    private readonly HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
    private int next;
    private bool stopped;
    /// <summary>Steps written after this are not read (a recorded request's end: later requests may give the same sub-agent more work).</summary>
    private readonly DateTimeOffset? until;
    public long BytesRead { get; private set; }

    /// <param name="root">The run's own conversation, never followed as a sub-agent.</param>
    public AntigravitySubagentWatcher(string brain, string graphNamespace, string? root = null, DateTimeOffset? until = null, Limits? limits = null)
    {
        this.brain = brain; ns = graphNamespace; this.until = until; this.limits = limits ?? new();
        if (root is not null) known.Add(root);
    }
    /// <summary>Changed sub-agents while the run is active.</summary>
    public IReadOnlyList<CodexSessionAgent> Poll(IReadOnlyList<AntigravitySubagent> subagents) { lock (sync) return stopped ? [] : Read(subagents, limits.BytesPerPoll); }
    /// <summary>One last, larger read after the run ended; later polls read nothing.</summary>
    public IReadOnlyList<CodexSessionAgent> Finish(IReadOnlyList<AntigravitySubagent> subagents)
    {
        lock (sync)
        {
            if (stopped) return [];
            try { return Read(subagents, limits.FinalBytes); }
            finally { stopped = true; }
        }
    }
    private void Adopt(IEnumerable<AntigravitySubagent> subagents)
    {
        foreach (var subagent in subagents)
        {
            if (children.Count >= limits.MaximumChildren) break;
            if (AntigravityRecords.Transcript(brain, subagent.Conversation) is not { } path || !known.Add(subagent.Conversation)) continue;
            children.Add(new(path, new(subagent, ns)));
        }
    }
    private IReadOnlyList<CodexSessionAgent> Read(IReadOnlyList<AntigravitySubagent> subagents, long budget)
    {
        Adopt(subagents);
        // Start one child later each time so a busy child cannot starve the rest; children a child starts are read in the same pass.
        var existing = children.Count;
        var first = existing == 0 ? 0 : next % existing;
        next = first + 1;
        var order = Enumerable.Range(0, existing).Select(i => (first + i) % existing).ToList();
        for (var position = 0; position < order.Count && budget > 0; position++)
        {
            var child = children[order[position]];
            if (child.Closed) continue;
            using (var handle = RecordFiles.Open(child.Path))
            {
                if (handle is null) continue;
                long size;
                try { size = RandomAccess.GetLength(handle); } catch (IOException) { continue; }
                if (size < child.Offset) { child.Closed = true; continue; }
                while (budget > 0 && child.Offset < size)
                {
                    var capacity = (int)Math.Min(Math.Min(limits.MaximumLineBytes + 1L, size - child.Offset), budget);
                    var bytes = new byte[capacity]; var count = RecordFiles.Read(handle, bytes, child.Offset);
                    if (count <= 0) break;
                    budget -= count; BytesRead += count;
                    var start = 0;
                    if (child.Skipping)
                    {
                        var end = bytes.AsSpan(0, count).IndexOf((byte)'\n');
                        if (end < 0) { child.Offset += count; continue; }
                        start = end + 1; child.Skipping = false;
                    }
                    while (start < count && !child.Closed)
                    {
                        var relative = bytes.AsSpan(start, count - start).IndexOf((byte)'\n');
                        if (relative < 0) break;
                        if (relative > 0 && relative <= limits.MaximumLineBytes && HistoryScan.Parse(bytes[start..(start + relative)]) is { } step)
                        {
                            // Transcripts are written in order: past the end, nothing more belongs here.
                            if (until is { } limit && AntigravityRecords.Date(step) is { } date && date > limit) { child.Closed = true; break; }
                            child.Record.Consume(step);
                        }
                        start += relative + 1;
                    }
                    if (count - start >= limits.MaximumLineBytes) { child.Skipping = true; start = count; }
                    child.Offset += start;
                    if (child.Closed || start == 0 || count < capacity) break;
                }
            }
            var before = children.Count;
            Adopt(child.Record.TakeSpawned());
            for (var added = before; added < children.Count; added++) order.Add(added);
        }
        return children.Select(c => c.Record.Take()).OfType<CodexSessionAgent>().ToArray();
    }
}
