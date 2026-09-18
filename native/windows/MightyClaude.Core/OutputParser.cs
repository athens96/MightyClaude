using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed class OutputParser
{
    private readonly string provider, activityNamespace;
    private readonly Action<string, string> log;
    private readonly Action<string> resume;
    private readonly Action<AgentActivity>? activity;
    private readonly SessionUsageTracker usage;
    private readonly Func<double> clock;
    private readonly object sync = new();
    private readonly HashSet<string> seen = [];
    private readonly Queue<string> seenOrder = [];
    private readonly Dictionary<string, AgentActivity> activities = [];
    private readonly Dictionary<string, double> starts = [];
    private readonly Dictionary<string, long> sequences = [];
    private readonly Queue<string> activityOrder = [];
    private readonly StringBuilder pending = new();
    private string? lastResume;
    private AgentActivity? lastTurn;
    private bool assistantSeen, pendingTruncated, closed;
    public bool Failed { get; private set; }
    public OutputParser(string provider, Action<string, string> log, Action<string> resume, Action<AgentActivity>? activity = null, Action<SessionUsage>? usage = null, string? activityNamespace = null, Func<double>? activityClock = null)
    {
        this.provider = provider; this.log = log; this.resume = resume; this.activity = activity;
        this.usage = new(provider, usage); this.activityNamespace = activityNamespace ?? Wire.Id();
        var origin = Stopwatch.GetTimestamp(); clock = activityClock ?? (() => Stopwatch.GetElapsedTime(origin).TotalMilliseconds);
    }
    public static async IAsyncEnumerable<string> LinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken token = default)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); var dropping = false; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            for (var i = 0; i < count; i++)
                if (buffer[i] == '\n') { if (!dropping && line.Length > 0) yield return line.ToString(); line.Clear(); dropping = false; }
                else if (!dropping) { line.Append(buffer[i]); if (line.Length > 1024 * 1024) { dropping = true; line.Clear(); } }
        if (!dropping && line.Length > 0) yield return line.ToString();
    }
    private void Resume(string? value) { if (Wire.Identifier(value) && value != lastResume) { lastResume = value; resume(value!); } }
    private void Assistant(string text, string? key = null)
    {
        if (text.Length == 0) return;
        key = (key ?? "message") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (!seen.Add(key)) return; seenOrder.Enqueue(key); if (seenOrder.Count > 512) seen.Remove(seenOrder.Dequeue());
        assistantSeen = true; log("assistant", ActivitySupport.PrefixUtf8(text, ActivitySupport.MaximumMessageBytes));
    }
    private static string Error(JsonElement value, string fallback) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.Text("message") ?? fallback;
    private void Turn(string summary)
    {
        var value = new AgentActivity(activityNamespace, provider, "turn", "running", summary);
        if (value == lastTurn) return; lastTurn = value; activity?.Invoke(value);
    }
    private void Tool(string? rawId, string state, string? name = null, JsonElement input = default, string? output = null, string? summary = null)
    {
        if (string.IsNullOrEmpty(rawId) || Encoding.UTF8.GetByteCount(rawId) > 512) return;
        var id = ActivitySupport.Id(activityNamespace, rawId); activities.TryGetValue(id, out var previous);
        if (previous is not null && ActivitySupport.Terminal(previous.State) && !ActivitySupport.Terminal(state) || previous?.State == "error" && state == "completed") return;
        var tool = name ?? previous?.ToolName ?? "Tool";
        var selected = summary ?? (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? previous?.Summary : null) ?? ActivitySupport.Summary(tool, input);
        double? duration = null;
        if (ActivitySupport.Terminal(state))
        { duration = previous?.DurationMs; if (starts.Remove(id, out var start) && duration is null) { var ms = clock() - start; if (ActivitySupport.ValidDuration(ms)) duration = ms; } }
        else if (!starts.ContainsKey(id)) { var now = clock(); if (double.IsFinite(now) && now >= 0) starts[id] = now; }
        var value = ActivitySupport.Normalize(new(id, provider, ActivitySupport.Kind(tool), state, selected, tool, output ?? previous?.Output, duration));
        if (value is null || value == previous) return;
        if (previous is null) { activityOrder.Enqueue(id); if (activityOrder.Count > 512) { var expired = activityOrder.Dequeue(); activities.Remove(expired); starts.Remove(expired); sequences.Remove(expired); } }
        activities[id] = value; activity?.Invoke(value);
    }
    public void ReceiveMod(JsonElement root)
    {
        lock (sync)
        {
            if (provider != "claude" || closed) return;
            Resume(root.Text("claudeSessionId")); usage.ConsumeMod(root);
            var kind = root.Text("event");
            if (kind == "turn.start" && root.Text("agentId") is null) { Turn("Claude 응답 생성 중"); return; }
            if (kind == "turn.complete") { if (root.Text("agentId") is null) Turn("Claude 응답 마무리 중"); return; }
            if (kind is not ("tool.call" or "tool.waiting" or "tool.complete")) return;
            if (root.Text("toolUseId") is not { } rawId || root.Text("tool") is not { } name) return;
            var id = ActivitySupport.Id(activityNamespace, rawId);
            if (MetadataJson.Integer(root, "sequence") is { } sequence) { if (sequences.TryGetValue(id, out var old) && old >= sequence) return; sequences[id] = sequence; }
            if (kind == "tool.call") Tool(rawId, name == "AskUserQuestion" ? "waiting" : "running", name, summary: root.Text("summary"));
            if (kind == "tool.waiting") Tool(rawId, "waiting", name, summary: root.Text("summary"));
            if (kind == "tool.complete") Tool(rawId, MetadataJson.Flag(root, "isError") ? "error" : "completed", name, output: root.Text("output"), summary: root.Text("summary"));
        }
    }
    public void FinishActivities(bool stopped)
    {
        lock (sync)
        {
            closed = true;
            foreach (var id in activityOrder)
                if (activities.TryGetValue(id, out var previous) && previous.State is "running" or "waiting")
                {
                    double? duration = previous.DurationMs;
                    if (starts.Remove(id, out var start) && ActivitySupport.ValidDuration(clock() - start)) duration = clock() - start;
                    var value = previous with { State = stopped ? "stopped" : "error", Output = previous.Output ?? "도구 결과를 받기 전에 실행이 종료되었습니다.", DurationMs = duration };
                    activities[id] = value; activity?.Invoke(value);
                }
        }
    }
    public void Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (sync)
        try
        {
            if (closed) return;
            using var document = JsonDocument.Parse(line); var root = document.RootElement; var type = root.Text("type");
            usage.Consume(root);
            if (provider == "claude")
            {
                var child = MetadataJson.Property(root, "parent_tool_use_id").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
                if (!child) Resume(root.Text("session_id"));
                var message = MetadataJson.Property(root, "message"); var blocks = MetadataJson.Property(message, "content");
                if (type == "assistant" && blocks.ValueKind == JsonValueKind.Array)
                {
                    if (!child) Assistant(string.Join('\n', blocks.EnumerateArray().Where(b => b.Text("type") == "text").Select(b => b.Text("text") ?? "")), root.Text("uuid") ?? message.Text("id"));
                    foreach (var block in blocks.EnumerateArray().Where(b => b.Text("type") == "tool_use")) Tool(block.Text("id"), block.Text("name") == "AskUserQuestion" ? "waiting" : "running", block.Text("name"), MetadataJson.Property(block, "input"));
                }
                if (type == "user" && blocks.ValueKind == JsonValueKind.Array)
                    foreach (var block in blocks.EnumerateArray().Where(b => b.Text("type") == "tool_result")) Tool(block.Text("tool_use_id"), MetadataJson.Flag(block, "is_error") ? "error" : "completed", output: ActivitySupport.Output(MetadataJson.Property(block, "content")));
                if (type == "system" && root.Text("subtype") == "permission_denied") Tool(root.Text("tool_use_id"), "error", root.Text("tool_name"), output: Error(MetadataJson.Property(root, "message"), "선택한 권한 모드 또는 Claude 규칙에서 거부했습니다."));
                if (type == "result" && !child && !ClaudeStream.IsNotificationResult(root))
                {
                    if (MetadataJson.Flag(root, "is_error") || root.Text("subtype")?.StartsWith("error", StringComparison.Ordinal) == true)
                    { Failed = true; log("error", root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array ? string.Join('\n', errors.EnumerateArray().Select(e => Error(e, "실행 오류"))) : root.Text("result") ?? "Claude 실행 오류"); }
                    else if (!assistantSeen) Assistant(root.Text("result") ?? "");
                    Turn("Claude 응답 마무리 중");
                }
            }
            else if (provider == "codex")
            {
                if (type == "thread.started") Resume(root.Text("thread_id"));
                if (type is "turn.started" or "turn.completed") Turn(type == "turn.started" ? "Codex 응답 생성 중" : "Codex 응답 마무리 중");
                var item = MetadataJson.Property(root, "item");
                if (type is "item.started" or "item.updated" or "item.completed" && item.ValueKind == JsonValueKind.Object)
                {
                    var ended = type == "item.completed"; var state = item.Text("status") == "failed" ? "error" : ended ? "completed" : "running";
                    switch (item.Text("type"))
                    {
                        case "agent_message": if (ended) Assistant(item.Text("text") ?? "", Wire.Clean(item.Text("id"), 128)); break;
                        case "command_execution": Tool(item.Text("id"), state, "command_execution", item, ended ? item.Text("aggregated_output") : null); if (activity is null && ended && item.Text("aggregated_output") is { Length: > 0 } output) log("output", output); break;
                        case "file_change": case "web_search": Tool(item.Text("id"), state, item.Text("type"), item); break;
                        case "mcp_tool_call": var name = string.Join('.', new[] { item.Text("server"), item.Text("tool") }.Where(v => !string.IsNullOrEmpty(v))); Tool(item.Text("id"), state, name.Length > 0 ? name : "MCP", MetadataJson.Property(item, "arguments"), ActivitySupport.Output(MetadataJson.Property(item, "error").ValueKind != JsonValueKind.Undefined ? MetadataJson.Property(item, "error") : MetadataJson.Property(item, "result"))); break;
                        case "error": log("error", item.Text("message") ?? "Codex 작업 오류"); break;
                    }
                }
                if (type is "turn.failed" or "error") { Failed = true; log("error", root.TryGetProperty("error", out var error) ? Error(error, "Codex 실행 오류") : root.Text("message") ?? "Codex 실행 오류"); }
            }
            else
            {
                if (type == "init") { Resume(root.Text("session_id")); Turn("Gemini 응답 생성 중"); }
                if (type == "message" && root.Text("role") == "assistant")
                {
                    if (MetadataJson.Flag(root, "delta")) { var text = root.Text("content") ?? ""; var remaining = Math.Max(0, ActivitySupport.MaximumMessageBytes - Encoding.UTF8.GetByteCount(pending.ToString())); if (Encoding.UTF8.GetByteCount(text) > remaining) pendingTruncated = true; pending.Append(ActivitySupport.PrefixUtf8(text, remaining)); }
                    else { Flush(); if (root.Text("content") is { Length: > 0 } text) log("assistant", ActivitySupport.PrefixUtf8(text, ActivitySupport.MaximumMessageBytes)); }
                }
                if (type == "tool_use") { Flush(); Tool(root.Text("tool_id"), "running", root.Text("tool_name"), MetadataJson.Property(root, "parameters")); if (activity is null) log("system", $"도구 실행 · {root.Text("tool_name")}"); }
                if (type == "tool_result") { Flush(); var failed = root.Text("status") == "error"; var output = failed ? Error(MetadataJson.Property(root, "error"), "Gemini 도구 실행 오류") : root.Text("output"); Tool(root.Text("tool_id"), failed ? "error" : "completed", output: output); if (activity is null && output is not null) log("output", output); }
                if (type == "error" || type == "result" && root.Text("status") == "error") { Flush(); var fatal = root.Text("severity") != "warning"; Failed |= fatal; log(fatal ? "error" : "system", root.TryGetProperty("error", out var error) ? Error(error, "Gemini 실행 오류") : root.Text("message") ?? "Gemini 실행 오류"); }
                if (type == "result") { Flush(); Turn("Gemini 응답 마무리 중"); }
            }
        }
        catch (JsonException) { log("output", line); }
        catch (InvalidOperationException) { log("system", "지원하지 않는 CLI 출력 레코드를 생략했습니다."); }
    }
    public void Flush() { lock (sync) { if (pending.Length > 0) log("assistant", pending.ToString()); if (pendingTruncated) log("system", "응답 한 메시지가 128 KiB를 넘어 뒷부분을 생략했습니다."); pending.Clear(); pendingTruncated = false; } }
}

public static class ClaudeStream
{
    // A resumed session whose earlier process left a background task behind first reports that task as
    // stopped, and closes that report with a `result` of its own (`origin.kind == "task-notification"`)
    // before it even reads the new request. It is not the request's result: OutputParser and
    // SessionUsageTracker must not treat it as the turn ending.
    public static bool IsNotificationResult(JsonElement value) => value.Text("type") == "result" && MetadataJson.Property(value, "origin").Text("kind") == "task-notification";
}
