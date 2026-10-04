using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// Only a spawned child of the current root thread, with entries changed since the last poll.
public sealed record CodexSessionAgent(string Thread, string ParentThread, string? Title, string? Input,
    IReadOnlyList<LogEntry> Entries, IReadOnlySet<string> Answers, string? Output, string State, int Turns,
    IReadOnlyList<GraphResponseRecord> Usage);

internal sealed class CodexSessionThread
{
    internal sealed record Meta(string Thread, string Parent, string? Nickname, string? Path, long? StartOrdinal, bool Forked);
    internal static string? ThreadID(string? value) => Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : null;
    internal static Meta? Metadata(JsonElement record)
    {
        if (record.Text("type") != "session_meta") return null;
        var p = MetadataJson.Property(record, "payload");
        var spawn = MetadataJson.Property(MetadataJson.Property(MetadataJson.Property(p, "source"), "subagent"), "thread_spawn");
        if (spawn.ValueKind != JsonValueKind.Object || ThreadID(p.Text("id")) is not { } thread || ThreadID(p.Text("parent_thread_id") ?? spawn.Text("parent_thread_id")) is not { } parent || parent == thread) return null;
        return new(thread, parent, CodexCollaborationItem.Key(p.Text("agent_nickname") ?? spawn.Text("agent_nickname")),
            CodexCollaborationItem.Key(p.Text("agent_path") ?? spawn.Text("agent_path")), MetadataJson.Integer(p, "subagent_history_start_ordinal"),
            MetadataJson.Property(p, "forked_from_id").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null));
    }
    internal readonly Meta MetadataValue;
    private readonly string ns;
    private readonly DateTimeOffset since;
    private readonly bool stateOnly;
    private bool own, active, dirty = true, failed;
    private string? input, output, model, lastAnswer;
    private string state = "running";
    private int turns, errors;
    private readonly string? title;
    private readonly List<LogEntry> entries = [];
    private readonly HashSet<string> changed = [], answers = [], responses = [];
    private readonly List<GraphResponseRecord> usage = [];
    internal CodexSessionThread(Meta metadata, string ns, DateTimeOffset started)
    {
        MetadataValue = metadata; this.ns = ns; since = started.AddSeconds(-1);
        stateOnly = metadata.Forked && metadata.StartOrdinal is null; own = !stateOnly;
        title = Clean(string.Join(" · ", new[] { metadata.Nickname, metadata.Path?.Split('/').LastOrDefault() }.Where(v => !string.IsNullOrEmpty(v))), 160);
    }
    private static string? Clean(string? text, int maximum) => ActivitySupport.Clean(text, maximum) is { Length: > 0 } value ? value : null;
    private string ID(string key) => ExecutionGraphSupport.Identifier(ns, "codex-session:" + MetadataValue.Thread + ":" + key);
    private static string Stamp(JsonElement r) => r.Text("timestamp") is { Length: <= 80 } value && AgentRunTiming.Parse(value) is not null ? value : Wire.Now();
    private static string? Text(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return null;
        var text = string.Join('\n', content.EnumerateArray().Take(32).Where(p => p.Text("type") is "input_text" or "output_text" or "text" or "Text").Select(p => p.Text("text")).OfType<string>());
        return text.Length == 0 || text.StartsWith("gAAAA", StringComparison.Ordinal) ? null : text;
    }
    private static bool Envelope(string text)
    {
        var lines = text.TrimEnd('\n').Split('\n');
        if (lines.Length is < 2 or > 12 || !lines[^1].Contains(':') || lines[^1].Split(':', 2)[1].Trim().Length != 0) return false;
        return lines.All(line => line.IndexOf(':') is > 0 and <= 32 && char.IsUpper(line[0]) && line[..line.IndexOf(':')].All(c => char.IsLetter(c) || c == ' '));
    }
    private void Upsert(LogEntry entry)
    {
        var i = entries.FindIndex(e => e.Id == entry.Id);
        if (i >= 0) { entry = entry with { Timestamp = entries[i].Timestamp }; if (entries[i] == entry) return; entries[i] = entry; }
        else entries.Add(entry);
        while (entries.Count > ExecutionGraphSupport.MaximumEntries) { changed.Remove(entries[0].Id); answers.Remove(entries[0].Id); entries.RemoveAt(0); }
        changed.Add(entry.Id); dirty = true;
    }
    internal void Consume(JsonElement record)
    {
        var type = record.Text("type"); var p = MetadataJson.Property(record, "payload"); var kind = p.Text("type");
        if (type is null || p.ValueKind != JsonValueKind.Object) return;
        if (MetadataValue.StartOrdinal is { } start) { if (MetadataJson.Integer(record, "ordinal") is not { } ordinal || ordinal < start) return; }
        else if (!own)
        {
            if (!(type == "event_msg" && kind == "thread_settings_applied") && !(type == "response_item" && kind == "agent_message" && MetadataValue.Path is not null && p.Text("recipient") == MetadataValue.Path)) return;
            own = true;
        }
        if (!active)
        {
            if (type != "event_msg" || kind != "task_started" || AgentRunTiming.Parse(record.Text("timestamp")) is not { } date || date < since) return;
            active = true;
        }
        if (stateOnly && (type != "event_msg" || kind is not ("task_started" or "task_complete" or "turn_aborted"))) return;
        switch (type, kind)
        {
            case ("turn_context", _): model = Clean(CodexCollaborationItem.Key(p.Text("model")), 160); break;
            case ("token_usage_record", _):
                if (CodexCollaborationItem.Key(p.Text("response_id")) is { } response && responses.Count < 4096 && !responses.Contains(response) && ExecutionGraphTracker.CodexUsage(MetadataJson.Property(p, "usage")) is { } tokens)
                { responses.Add(response); usage.Add(new(response, model, tokens, [])); dirty = true; }
                break;
            case ("event_msg", "task_started"):
                if (!string.IsNullOrEmpty(output)) { var id = ID("answer:" + turns); Upsert(new(id, "assistant", output, Stamp(record), "codex")); answers.Add(id); }
                turns++; state = "running"; output = lastAnswer = null; failed = false; dirty = true; break;
            case ("event_msg", "task_complete"):
                failed = false; if (!stateOnly) output = Clean(p.Text("last_agent_message"), ExecutionGraphSupport.MaximumOutputBytes) ?? lastAnswer;
                state = "completed"; dirty = true; break;
            case ("event_msg", "turn_aborted"): failed = false; state = "stopped"; dirty = true; break;
            case ("event_msg", "error"):
                if (Clean(p.Text("message"), ExecutionGraphSupport.MaximumOutputBytes) is { } error) { failed = true; errors++; Upsert(new(ID("error:" + (MetadataJson.Integer(record, "ordinal") ?? errors)), "error", error, Stamp(record), "codex")); }
                break;
            case ("event_msg", "item_completed"): Item(MetadataJson.Property(p, "item"), p, record); break;
            case ("response_item", "agent_message"):
                if (MetadataValue.Path is not null && p.Text("recipient") == MetadataValue.Path && Text(MetadataJson.Property(p, "content")) is { } text && !Envelope(text))
                {
                    var clean = Clean(text, ExecutionGraphSupport.MaximumInputBytes);
                    if (input is null) { input = clean; dirty = true; }
                    else if (CodexCollaborationItem.Key(p.Text("id")) is { } id && clean is not null) Upsert(new(ID("input:" + id), "user", clean, Stamp(record), "codex"));
                }
                break;
            case ("response_item", "message"):
                var kinds = MetadataJson.Property(MetadataJson.Property(p, "internal_chat_message_metadata_passthrough"), "content_item_kinds");
                if (input is null && p.Text("role") == "user" && !MetadataJson.Flag(MetadataJson.Property(record, "metadata"), "inherited_user_message") &&
                    (kinds.ValueKind == JsonValueKind.Undefined || kinds.ValueKind == JsonValueKind.Array && kinds.GetArrayLength() > 0 && kinds.EnumerateArray().All(k => k.ValueKind == JsonValueKind.String && k.GetString() == "user.text")) &&
                    Text(MetadataJson.Property(p, "content")) is { } human && !human.StartsWith('<')) { input = Clean(human, ExecutionGraphSupport.MaximumInputBytes); dirty = true; }
                break;
        }
    }
    private void Item(JsonElement item, JsonElement payload, JsonElement record)
    {
        if (CodexCollaborationItem.Key(item.Text("id")) is not { } id || item.Text("type") is not { } type) return;
        if (type == "AgentMessage")
        {
            if (Clean(Text(MetadataJson.Property(item, "content")), ExecutionGraphSupport.MaximumOutputBytes) is not { } text) return;
            if (item.Text("phase") == "final_answer") { output = lastAnswer = text; dirty = true; }
            else Upsert(new(ID("message:" + id), "assistant", text, Stamp(record), "codex"));
            return;
        }
        var mapped = HistoryReplay.CodexItem(item) ?? item;
        string tool; JsonElement args = mapped; string? result = null;
        var bad = item.Text("status") is "failed" or "declined";
        switch (type)
        {
            case "CommandExecution": tool = "command_execution"; result = mapped.Text("aggregated_output"); break;
            case "FileChange": tool = "file_change"; break;
            case "McpToolCall":
                tool = string.Join('.', new[] { item.Text("server"), item.Text("tool") }.OfType<string>()); if (tool.Length == 0) tool = "MCP";
                args = MetadataJson.Property(item, "arguments");
                var err = MetadataJson.Property(item, "error"); var value = MetadataJson.Property(item, "result");
                if (err.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) { bad = true; result = ActivitySupport.Output(err); }
                else result = ActivitySupport.Output(value);
                bad |= MetadataJson.Flag(value, "isError"); break;
            case "Extension": tool = item.Text("kind") == "web.search" ? "web_search" : Clean(item.Text("kind"), 80) ?? "Extension"; break;
            case "ImageView": tool = "view_image"; break;
            case "CollabAgentToolCall": if (CodexCollaborationItem.Parse(mapped) is not { } call) return; tool = call.Tool; break;
            default: return;
        }
        double? duration = MetadataJson.Integer(payload, "started_at_ms") is { } started && MetadataJson.Integer(payload, "completed_at_ms") is { } ended && ended >= started ? (double)ended - started : null;
        var activity = ActivitySupport.Normalize(new(ActivitySupport.Id(ns, "codex-session:" + MetadataValue.Thread + ":" + id), "codex", ActivitySupport.Kind(tool), item.Text("status") == "in_progress" ? "running" : bad ? "error" : "completed", ActivitySupport.Summary(tool, args), tool, result, duration));
        if (activity is not null) Upsert(new(activity.Id, "system", activity.Summary, Stamp(record), "codex", activity));
    }
    internal void Close() { if (active && failed && state == "running") { state = "error"; dirty = true; } }
    internal CodexSessionAgent? Take()
    {
        if (!active || !dirty) return null;
        var value = new CodexSessionAgent(MetadataValue.Thread, MetadataValue.Parent, title, input, entries.Where(e => changed.Contains(e.Id)).ToArray(), answers.Intersect(changed).ToHashSet(), output, state, turns, usage.ToArray());
        dirty = false; changed.Clear(); usage.Clear(); return value;
    }
}

/// Discovers current-run descendants only. All reads are byte-bounded, confined
/// below the resolved sessions directory, and serialized with final disposal.
public sealed class CodexSessionWatcher : IDisposable
{
    public sealed record Limits(int BytesPerPoll = 4 * 1024 * 1024, int FinalBytes = 32 * 1024 * 1024,
        int MaximumLineBytes = 2 * 1024 * 1024, int MaximumFirstLineBytes = 1024 * 1024,
        int FirstLinesPerPoll = 64, int MaximumChildren = 128, int MaximumWaiting = 512);
    private sealed class Child(string path, WorkspaceOpenFile file, long offset, CodexSessionThread thread)
    {
        internal readonly string Path = path;
        internal readonly WorkspaceOpenFile File = file;
        internal readonly CodexSessionThread Thread = thread;
        internal long Offset = offset;
        internal bool Skipping, Closed;
    }
    private readonly object sync = new();
    private readonly string home, root, ns;
    private string? sessions;
    private readonly DateTimeOffset started;
    private readonly Func<DateTimeOffset> now;
    private readonly Limits limits;
    private readonly List<Child> children = [];
    private readonly HashSet<string> known = [], skipped = [];
    private readonly Dictionary<string, (string Path, string Parent)> waiting = [];
    private readonly Queue<string> waitingOrder = [];
    private int next;
    private bool stopped;
    public int FirstLineReads { get; private set; }
    public CodexSessionWatcher(string codexHome, string rootThread, DateTimeOffset startedAt, string graphNamespace, Func<DateTimeOffset>? clock = null, Limits? limits = null)
    {
        home = codexHome; root = CodexSessionThread.ThreadID(rootThread) ?? throw new ArgumentException("Invalid Codex root thread."); started = startedAt; ns = graphNamespace; now = clock ?? (() => DateTimeOffset.UtcNow); this.limits = limits ?? new();
        if (this.limits.BytesPerPoll < 1 || this.limits.FinalBytes < 1 || this.limits.MaximumLineBytes is < 1 or > 2 * 1024 * 1024 || this.limits.MaximumFirstLineBytes is < 1 or > 1024 * 1024 || this.limits.FirstLinesPerPoll is < 1 or > 64 || this.limits.MaximumChildren is < 1 or > 128 || this.limits.MaximumWaiting is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(limits));
    }
    public IReadOnlyList<CodexSessionAgent> Poll() { lock (sync) return stopped ? [] : Read(limits.BytesPerPoll, false); }
    public IReadOnlyList<CodexSessionAgent> Finish() { lock (sync) { if (stopped) return []; try { return Read(limits.FinalBytes, true); } finally { Dispose(); } } }
    public void Dispose() { lock (sync) { stopped = true; foreach (var child in children) child.File.Dispose(); } }
    private IEnumerable<string> DayFolders()
    {
        if (sessions is null) yield break;
        var first = started.LocalDateTime.Date.AddDays(-1); var last = (now() > started ? now() : started).LocalDateTime.Date;
        for (var day = last; day >= first && day >= last.AddDays(-2); day = day.AddDays(-1))
        {
            var path = sessions; var valid = true;
            foreach (var part in new[] { day.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), day.ToString("MM", System.Globalization.CultureInfo.InvariantCulture), day.ToString("dd", System.Globalization.CultureInfo.InvariantCulture) })
            { path = Path.Combine(path, part); if (!RecordFiles.RealDirectory(path)) { valid = false; break; } }
            if (valid) yield return path;
        }
    }
    private WorkspaceOpenFile? Open(string path)
    {
        try { return sessions is not null && RecordFiles.Regular(path) ? WorkspaceFiles.OpenFile(Path.GetRelativePath(sessions, path), sessions) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private (CodexSessionThread.Meta Meta, long Offset)? Header(WorkspaceOpenFile file, string thread, out bool incomplete)
    {
        FirstLineReads++; incomplete = false;
        var bytes = new byte[(int)Math.Min(file.Stream.Length, limits.MaximumFirstLineBytes + 1L)];
        var count = RecordFiles.Read(file.Stream.SafeFileHandle, bytes, 0); var end = bytes.AsSpan(0, count).IndexOf((byte)'\n');
        if (end < 0) { incomplete = count <= limits.MaximumFirstLineBytes; return null; }
        try { using var doc = JsonDocument.Parse(bytes.AsMemory(0, end)); return CodexSessionThread.Metadata(doc.RootElement) is { } meta && meta.Thread == thread ? (meta, end + 1) : null; }
        catch (JsonException) { return null; }
    }
    private void Skip(string name) { if (skipped.Count >= 8192) skipped.Clear(); skipped.Add(name); }
    private void Discover()
    {
        sessions ??= WorkspaceFiles.RealPath(Path.Combine(home, "sessions"));
        if (sessions is null || !RecordFiles.RealDirectory(sessions)) return;
        var reads = 0;
        void Candidate(string path, string thread, bool recheck = false)
        {
            var name = Path.GetFileName(path);
            if (reads >= limits.FirstLinesPerPoll || thread == root || known.Contains(thread) || skipped.Contains(name) || !recheck && waiting.ContainsKey(name)) return;
            WorkspaceOpenFile? file = null;
            try
            {
                file = Open(path); if (file is null || file.Modified < started) return;
                reads++;
                if (Header(file, thread, out var incomplete) is not { } header) { if (!incomplete) Skip(name); return; }
                if (header.Meta.Parent != root && !known.Contains(header.Meta.Parent))
                {
                    if (!waiting.ContainsKey(name))
                    {
                        while (waiting.Count >= limits.MaximumWaiting && waitingOrder.TryDequeue(out var old)) { waiting.Remove(old); Skip(old); }
                        waiting[name] = (path, header.Meta.Parent); waitingOrder.Enqueue(name);
                    }
                    return;
                }
                waiting.Remove(name);
                if (children.Count >= limits.MaximumChildren) { Skip(name); return; }
                known.Add(thread); children.Add(new(path, file, header.Offset, new(header.Meta, ns, started))); file = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { file?.Dispose(); }
        }
        foreach (var folder in DayFolders())
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder).Take(16384))
                {
                    if (reads >= limits.FirstLinesPerPoll) break;
                    if (RecordFiles.Thread(Path.GetFileName(path)) is { } id && CodexSessionThread.ThreadID(id) is { } thread) Candidate(path, thread);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var previous = -1;
        while (previous != known.Count && reads < limits.FirstLinesPerPoll)
        {
            previous = known.Count;
            foreach (var entry in waiting.Values.Where(e => known.Contains(e.Parent)).ToArray())
                if (RecordFiles.Thread(Path.GetFileName(entry.Path)) is { } id && CodexSessionThread.ThreadID(id) is { } thread) Candidate(entry.Path, thread, true);
        }
    }
    private IReadOnlyList<CodexSessionAgent> Read(int budget, bool closing)
    {
        Discover();
        var first = children.Count == 0 ? 0 : next++ % children.Count;
        for (var step = 0; step < children.Count && budget > 0; step++)
        {
            var child = children[(first + step) % children.Count]; if (child.Closed) continue;
            try
            {
                var stream = child.File.Stream;
                if (stream.Length < child.Offset) { child.Closed = true; continue; }
                while (budget > 0)
                {
                    var capacity = (int)Math.Min(Math.Min(limits.MaximumLineBytes + 1L, stream.Length - child.Offset), budget); if (capacity <= 0) break;
                    var bytes = new byte[capacity]; var count = RecordFiles.Read(stream.SafeFileHandle, bytes, child.Offset); if (count <= 0) break;
                    budget -= count; var start = 0;
                    if (child.Skipping) { var end = bytes.AsSpan(0, count).IndexOf((byte)'\n'); if (end < 0) { child.Offset += count; continue; } start = end + 1; child.Skipping = false; }
                    while (start < count)
                    {
                        var relative = bytes.AsSpan(start, count - start).IndexOf((byte)'\n'); if (relative < 0) break;
                        if (relative > 0 && relative <= limits.MaximumLineBytes)
                            try { using var doc = JsonDocument.Parse(bytes.AsMemory(start, relative)); child.Thread.Consume(doc.RootElement); } catch (JsonException) { }
                        start += relative + 1;
                    }
                    if (count - start >= limits.MaximumLineBytes) { child.Skipping = true; start = count; }
                    child.Offset += start;
                    if (start == 0 || count < capacity) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { child.Closed = true; }
        }
        if (closing) foreach (var child in children) child.Thread.Close();
        return children.Select(c => c.Thread.Take()).OfType<CodexSessionAgent>().ToArray();
    }
}
