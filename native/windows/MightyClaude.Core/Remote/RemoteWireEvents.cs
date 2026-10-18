using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>What one wire event is to a Windows client: a pane event, a tool request (with the host's run id to answer with), ignored, or unusable.</summary>
/// <remarks>An unusable event is left out alone; the caller says so once per poll (remote.run.notice.skipped) and the run goes on.</remarks>
public sealed record RemoteIncoming(RunEvent? Event = null, ToolPermissionRequest? Permission = null, string? HostRunId = null, bool Ignored = false)
{
    [JsonIgnore] public bool Usable => Event is not null || Permission is not null || Ignored;
}

/// <summary>
/// Windows run events and the Mac's wire events (native/contracts/fixtures/remote-v2/poll.json). A host writes with From*, a client
/// reads with Read; the Mac is the reference for every field (M/Models.swift RunEvent and LogEntry, M/Remote/RemoteWorkspaceModels.swift
/// RemoteWorkspaceValidation.event, M/Remote/RemoteClientService.swift local).
/// </summary>
public static class RemoteWireEvents
{
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant);
    /// <summary>The two forms the Mac writes: ISO8601DateFormatter's whole seconds and its fractional-seconds form.</summary>
    private static readonly Regex MacForm = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z\z", RegexOptions.CultureInvariant);
    /// <summary>The members of a wire event that hold a time, and whether the Mac writes that one with milliseconds.</summary>
    private static readonly IReadOnlyDictionary<string, bool> TimestampMembers = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["timestamp"] = false, ["updatedAt"] = false, ["rateLimitsUpdatedAt"] = false, ["resetsAt"] = false,
        ["receivedAt"] = true, ["decidedAt"] = true, ["startedAt"] = true, ["endedAt"] = true,
    };

    /// <summary>A host's event as the wire carries it; null when it does not travel: graph_run to a client that did not ask, a tool request
    /// or a tracker node (they travel as <see cref="FromPermission"/> and <see cref="FromGraphNode"/>), any type the Mac does not read, and
    /// a value RemoteWire.Json cannot write (a null in a member that is not nullable). The caller leaves a null out of the run's events.</summary>
    public static JsonObject? FromRunEvent(RunEvent value, string jobId, bool graphRuns)
    {
        var travels = value.Type == "graph_run" ? graphRuns : value.Type is not ("permission" or "graph") && RemoteWorkspaceValidation.EventTypes.Contains(value.Type);
        if (!travels || Written(value with { SessionId = jobId, Permission = null, Reason = null }) is not { } node) return null;
        // A lost sign-in is the Mac's "auth"; no other reason is one the Mac reads.
        if (value.Type == "status" && value.Reason == "authentication") node["reason"] = "auth";
        if (node["entry"] is JsonObject entry) Pictures(entry, reading: false);
        if (node["graphRun"] is JsonObject run) GraphRunPictures(run, reading: false);
        Normalize(node);
        return Readable(node, jobId, graphRuns);
    }

    /// <summary>A tool request from the host's run (RunManager's permissionRequested), as the Mac's "permission" event; null when
    /// RemoteWire.Json cannot write it, which the caller leaves out.</summary>
    public static JsonObject? FromPermission(ToolPermissionRequest value, string jobId)
    {
        if (Written(value with { RunId = jobId }) is not { } permission) return null;
        Rename(permission, "inputJson", "inputJSON");
        var node = new JsonObject { ["sessionId"] = jobId, ["type"] = "permission", ["permission"] = permission };
        Normalize(node);
        return Readable(node, jobId, graphRuns: false);
    }

    /// <summary>One execution-graph node of the host's run, as the Mac's "graph" event (for a client that did not ask for graph_run); null
    /// when RemoteWire.Json cannot write it, which the caller leaves out.</summary>
    public static JsonObject? FromGraphNode(ExecutionGraphNode value, string jobId)
    {
        // The Mac's client normalizes every node it reads (ExecutionGraphSupport.normalized) and leaves out one that does not normalize.
        if (ExecutionGraphSupport.Normalized(value) is not { } clean || Written(clean) is not { } graph) return null;
        if (graph["entries"] is JsonArray entries) foreach (var entry in entries.OfType<JsonObject>()) Pictures(entry, reading: false);
        var node = new JsonObject { ["sessionId"] = jobId, ["type"] = "graph", ["graph"] = graph };
        Normalize(node);
        return node;
    }

    /// <summary>One event of the host's run, as this client's pane would have it, or unusable. Never throws, whatever a converter does,
    /// except for cancellation and running out of memory: one event never takes the poll down.</summary>
    public static RemoteIncoming Read(JsonObject wire, string jobId, string paneId, bool graphRuns)
    {
        try { return Incoming(wire.DeepClone().AsObject(), jobId, paneId, graphRuns); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { return new(); }
    }

    /// <summary>The value as RemoteWire.Json writes it; null for one it refuses to write.</summary>
    private static JsonObject? Written<T>(T value)
    {
        try { return JsonSerializer.SerializeToNode(value, RemoteWire.Json)?.AsObject(); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The written event when a client reads it (<see cref="Read"/>, the Mac's decoder and checks), else null. RemoteWire.Json leaves a null
    /// member out instead of refusing it, so an event missing what the Mac requires (a log entry's text, a request's input) is caught here.
    /// </summary>
    private static JsonObject? Readable(JsonObject node, string jobId, bool graphRuns) => Read(node, jobId, jobId, graphRuns).Usable ? node : null;

    private static RemoteIncoming Incoming(JsonObject copy, string jobId, string paneId, bool graphRuns)
    {
        // The Mac's RunEvent decoder: sessionId, type, entry, status, resumeId, activity and permission must decode, or the event is refused;
        // usage, graph, reason, plan, todos and background that do not decode are read as absent (try?), which refuses an event of that type.
        Lenient<SessionUsage>(copy, "usage", Usage);
        Lenient<string>(copy, "reason");
        Lenient<PlanRecord>(copy, "plan");
        Lenient<TodoProgress>(copy, "todos");
        // BackgroundWork.turnEnded and BackgroundTask.status are defaulted on Windows but required by the Mac's synthesized decoder.
        Lenient<BackgroundWork>(copy, "background", b => b["turnEnded"] is not null && b["tasks"] is JsonArray tasks && tasks.All(t => t is not JsonObject task || task["status"] is not null));
        Lenient<MightyGraphRun>(copy, "graphRun");
        if (copy["entry"] is JsonObject entry)
        {
            // The Mac reads a damaged activity or picture list of an entry as none (try?). A log whose entry activity is not valid is
            // refused (RemoteWorkspaceValidation.event checks it on log events alone); on any other event that activity is only left out.
            Pictures(entry, reading: true);
            if (entry["activity"] is { } inner && (Activity(inner) is not { } activity || !Valid(activity)))
            {
                if (Activity(inner) is not null && copy["type"] is JsonValue kind && kind.TryGetValue<string>(out var named) && named == "log") return new();
                entry.Remove("activity");
            }
        }
        if (copy["activity"] is { } outer && (Activity(outer) is not { } eventActivity || !Valid(eventActivity))) return new();
        if (copy["permission"] is JsonObject request)
        {
            // Windows names it InputJson; ToolPermissionRequest.state and canAllow are defaulted on Windows but required by the Mac's decoder.
            request.Remove("inputJson");
            Rename(request, "inputJSON", "inputJson");
            if (request["state"] is null || request["canAllow"] is null) return new();
        }
        if (copy["graphRun"] is JsonObject run) GraphRunPictures(run, reading: true);

        var value = copy.Deserialize<RunEvent>(RemoteWire.Json) ?? throw new JsonException();
        if (value.SessionId != jobId) return new();
        var type = value.Type;
        // A usage the Mac's decoder cannot read, or its client cannot normalize, is left out by RemoteClientService.local without a word.
        if (type == "usage" && value.Usage is null) return new(Ignored: true);
        // A Mac host's tracker node: a Windows pane draws graph_run instead, so it is left out without a word.
        if (type == "graph") return new(Ignored: true);
        if (!(RemoteWorkspaceValidation.EventTypes.Contains(type) || type == "graph_run" && graphRuns)) return new();
        switch (type)
        {
            case "status" when value.Reason is not (null or "auth"):
                return new();
            case "permission":
                if (value.Permission is not { } permission || !Wire.Identifier(permission.RunId) || permission.Id.Length == 0 || Characters(permission.Id) > 256 || Characters(permission.ToolName) > 256
                    || Encoding.UTF8.GetByteCount(permission.InputJson) > ClaudePermissionChannel.MaximumInputBytes || Encoding.UTF8.GetByteCount(permission.Summary) > 16_384
                    || permission.State.Length == 0 || Characters(permission.State) > 32) return new();
                return new(Permission: permission with { RunId = paneId }, HostRunId: permission.RunId);
            case "log" when value.Entry is not { } log || log.Provider is not null && !Wire.Providers.Contains(log.Provider) || Encoding.UTF8.GetByteCount(log.Text) > ActivitySupport.MaximumMessageBytes:
                return new();
        }
        value = value with { SessionId = paneId, Permission = null, Reason = type == "status" && value.Reason == "auth" ? "authentication" : null };
        if (value.Entry is { } text) value = value with { Entry = text with { Text = ActivitySupport.Clean(text.Text, text.Kind == "assistant" ? ActivitySupport.MaximumMessageBytes : 32768) } };
        return value.Valid() ? new(value) : new();
    }

    /// <summary>The time in the form the Mac writes it, in UTC: yyyy-MM-ddTHH:mm:ss.fffZ, or whole seconds; null when it is not an ISO 8601 time.
    /// Windows writes "O" (seven digits and an offset), which the Mac's ISO 8601 reader does not take.</summary>
    public static string? MacTimestamp(string value, bool milliseconds = true) =>
        Timestamp.IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString(milliseconds ? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'" : "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Every time member of a wire event (timestamp, updatedAt, rateLimitsUpdatedAt, resetsAt in whole seconds; receivedAt, decidedAt,
    /// startedAt, endedAt in milliseconds, as the Mac writes each) in a form the Mac reads. A time already in either of the Mac's forms
    /// is kept exactly, so a received event writes back unchanged; anything else that is a time is rewritten by <see cref="MacTimestamp"/>.
    /// Only those members are touched: a time inside text or inside a tool's input is never rewritten.
    /// </summary>
    public static void Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (TimestampMembers.TryGetValue(key, out var milliseconds) && o[key] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (!MacForm.IsMatch(s) && MacTimestamp(s, milliseconds) is { } t) o[key] = t;
                    }
                    else Normalize(o[key]);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Normalize(item);
                break;
        }
    }

    /// <summary>A member the Mac decodes with try?: one that does not decode (or fails <paramref name="shape"/>) is removed, as if never sent.</summary>
    private static void Lenient<T>(JsonObject value, string key, Func<JsonObject, bool>? shape = null)
    {
        if (value[key] is not { } member) return;
        bool decodes;
        try { decodes = member.Deserialize<T>(RemoteWire.Json) is not null && (shape is null || member is JsonObject o && shape(o)); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { decodes = false; }
        if (!decodes) value.Remove(key);
    }

    /// <summary>An activity as the Mac's AgentActivity decoder reads it (id, provider, kind, state and summary strings; toolName and output
    /// strings when present; durationMs try?, and none when it is out of range), before any cleaning; null when that decoder would throw.</summary>
    private static AgentActivity? Activity(JsonNode node)
    {
        if (node is not JsonObject o) return null;
        static bool Text(JsonNode? value, out string text)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var s)) { text = s; return true; }
            text = ""; return false;
        }
        static bool Optional(JsonNode? value, out string? text)
        {
            text = null;
            if (value is null) return true;
            if (!Text(value, out var s)) return false;
            text = s; return true;
        }
        if (!Text(o["id"], out var id) || !Text(o["provider"], out var provider) || !Text(o["kind"], out var kind) || !Text(o["state"], out var state) || !Text(o["summary"], out var summary)
            || !Optional(o["toolName"], out var tool) || !Optional(o["output"], out var output)) return null;
        double? duration = o["durationMs"] is JsonValue d && d.GetValueKind() == JsonValueKind.Number && d.GetValue<double>() is var ms && ActivitySupport.ValidDuration(ms) ? ms : null;
        return new(id, provider, kind, state, summary, tool, output, duration);
    }

    /// <summary>
    /// A usage as the Mac's synthesized SessionUsage decoder reads it: provider, source, tokenScope and updatedAt strings; model,
    /// selectedModel, providerSessionId, costScope and rateLimitsUpdatedAt strings when present; token counts whole numbers, costUSD a
    /// number; every rate-limit window an object with a kind string, a percentUsed number and a resetsAt string when present.
    /// SessionUsageConverter reads more loosely (a mistyped member as absent), so the Mac's rules are checked here first.
    /// </summary>
    private static bool Usage(JsonObject o)
    {
        static bool Text(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.String;
        static bool Number(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
        static bool Whole(JsonNode? value) => Number(value) && value!.GetValue<double>() is var n && Math.Floor(n) == n && Math.Abs(n) <= 9.2e18;
        if (!new[] { "provider", "source", "tokenScope", "updatedAt" }.All(k => Text(o[k]))) return false;
        if (!new[] { "model", "selectedModel", "providerSessionId", "costScope", "rateLimitsUpdatedAt" }.All(k => o[k] is null || Text(o[k]))) return false;
        if (!new[] { "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "reasoningTokens", "totalTokens", "contextUsedTokens", "contextWindowTokens" }.All(k => o[k] is null || Whole(o[k]))) return false;
        if (o["costUSD"] is { } cost && !Number(cost)) return false;
        return o["rateLimits"] switch
        {
            null => true,
            JsonArray windows => windows.All(w => w is JsonObject window && Text(window["kind"]) && (window["percentUsed"] is null || Number(window["percentUsed"])) && (window["resetsAt"] is null || Text(window["resetsAt"]))),
            _ => false,
        };
    }

    /// <summary>The Mac's ActivitySupport.valid: an activity is valid when normalizing leaves it as it is.</summary>
    private static bool Valid(AgentActivity activity) => ActivitySupport.Normalize(activity) == activity;

    /// <summary>A picture's cache key names a file on the host, so pictures never cross. A client reads a picture entry as its text alone,
    /// as the phone does (MobileMighty): a Windows image entry must carry its pictures.</summary>
    private static void Pictures(JsonObject entry, bool reading)
    {
        entry.Remove("images");
        if (reading && entry["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name == "image") entry["kind"] = "system";
    }

    private static void GraphRunPictures(JsonObject run, bool reading)
    {
        foreach (var list in new[] { run["rootEntries"], run["resultEntries"] }.Concat((run["agents"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(a => a["entries"])))
            if (list is JsonArray entries) foreach (var entry in entries.OfType<JsonObject>()) Pictures(entry, reading);
    }

    private static void Rename(JsonObject value, string from, string to)
    {
        if (!value.TryGetPropertyValue(from, out var member)) return;
        value.Remove(from);
        value[to] = member;
    }

    /// <summary>Characters as Swift's String.count counts them (grapheme clusters).</summary>
    private static int Characters(string value) => new StringInfo(value).LengthInTextElements;
}

/// <summary>
/// A host can send any number of tool requests; a remote pane keeps only the newest
/// <see cref="RemoteProtocol.MaximumPendingPermissions"/> waiting (M/RemotePendingPermissions). Older ones are withdrawn as
/// "cancelled" changes, so the pane's cards go too.
/// </summary>
public sealed class RemotePendingPermissions
{
    private readonly Dictionary<string, List<ToolPermissionRequest>> pending = new(StringComparer.Ordinal);

    /// <returns>The requests this one pushed out, each already marked cancelled.</returns>
    public IReadOnlyList<ToolPermissionRequest> Admit(ToolPermissionRequest request)
    {
        lock (pending)
        {
            if (!pending.TryGetValue(request.RunId, out var list)) pending[request.RunId] = list = [];
            list.RemoveAll(r => r.Id == request.Id);
            if (request.State != "pending") return [];
            list.Add(request);
            var evicted = new List<ToolPermissionRequest>();
            while (list.Count > RemoteProtocol.MaximumPendingPermissions) { evicted.Add(list[0] with { State = "cancelled" }); list.RemoveAt(0); }
            return evicted;
        }
    }
    public int Count(string paneId) { lock (pending) return pending.TryGetValue(paneId, out var list) ? list.Count : 0; }
    public void Forget(string paneId) { lock (pending) pending.Remove(paneId); }
}
