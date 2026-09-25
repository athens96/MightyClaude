using System.Reflection;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// Reads native/contracts/graph-vectors.json and asserts the C# implementation
/// reproduces every committed expected value, proving parity with macOS Swift.
/// All capsule/display cases run with the Korean locale (the committed strings
/// are Korean so the Windows locale files must produce exact matches).
internal static class GraphParityVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // ── Path resolution ───────────────────────────────────────────────────────

    private static string VectorsPath()
    {
        // The test binary lives in …/MightyClaude.Core.Tests/bin/…/net*/
        // Walk up to the repo root (5–7 levels) until native/contracts/ exists.
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "contracts", "graph-vectors.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("graph-vectors.json not found (searched up from assembly dir)");
    }

    // ── Canonical JSON (sorted keys, no whitespace, wraps scalars) ────────────

    private static string CanonicalCore(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",",
            elem.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalCore(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", elem.EnumerateArray().Select(CanonicalCore)) + "]",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => JsonSerializer.Serialize(elem.GetString()),
        JsonValueKind.Number => elem.GetRawText(),
        _ => throw new InvalidOperationException("Unexpected JsonValueKind " + elem.ValueKind),
    };

    private static string Canonical(JsonElement elem)
    {
        if (elem.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return CanonicalCore(elem);
        return "{\"value\":" + CanonicalCore(elem) + "}";
    }

    private static JsonElement ToElement(object? value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    // ── JSON helpers (mirrors GraphVectorFixtures.swift) ──────────────────────

    private static JsonElement Opt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v : default;

    private static string? Str(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static double? Num(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        return null;
    }

    private static GraphTokenUsage? UsageIn(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        long L(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var u = new GraphTokenUsage(L("inputTokens"), L("outputTokens"), L("cacheReadTokens"), L("cacheCreationTokens"));
        return u.Total > 0 ? u : null;
    }

    private static object? UsageOut(GraphTokenUsage? u) => u is null ? null : new Dictionary<string, object?>
    {
        ["inputTokens"] = u.InputTokens,
        ["outputTokens"] = u.OutputTokens,
        ["cacheReadTokens"] = u.CacheReadTokens,
        ["cacheCreationTokens"] = u.CacheCreationTokens,
    };

    private static LogEntry EntryIn(JsonElement e) => new(
        Id: e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
        Kind: e.TryGetProperty("kind", out var k) ? k.GetString() ?? "system" : "system",
        Text: e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
        Timestamp: e.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "2026-01-01T00:00:00Z" : "2026-01-01T00:00:00Z",
        Provider: e.TryGetProperty("provider", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null);

    private static object EntryOut(LogEntry e) => new Dictionary<string, object?>
    { ["id"] = e.Id, ["kind"] = e.Kind, ["text"] = e.Text, ["provider"] = e.Provider };

    private static GraphResponseRecord RecordIn(JsonElement e)
    {
        var actIds = e.TryGetProperty("activityIds", out var ai) && ai.ValueKind == JsonValueKind.Array
            ? ai.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
            : new List<string>();
        return new GraphResponseRecord(
            ResponseId: e.TryGetProperty("responseId", out var rid) ? rid.GetString() ?? "" : "",
            Model: e.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
            Usage: e.TryGetProperty("usage", out var u) ? UsageIn(u) ?? new GraphTokenUsage() : new GraphTokenUsage(),
            ActivityIds: actIds,
            MarkedAsConfigured: e.TryGetProperty("markedAsConfigured", out var mc) && mc.ValueKind == JsonValueKind.True);
    }

    private static object RecordOut(GraphResponseRecord r) => new Dictionary<string, object?>
    {
        ["responseId"] = r.ResponseId,
        ["model"] = r.Model,
        ["usage"] = UsageOut(r.Usage),
        ["activityIds"] = r.ActivityIds,
        ["markedAsConfigured"] = r.MarkedAsConfigured,
    };

    private static object NodeOut(ExecutionGraphNode n) => new Dictionary<string, object?>
    {
        ["id"] = n.Id,
        ["runId"] = n.RunId,
        ["parentId"] = n.ParentId,
        ["kind"] = n.Kind,
        ["state"] = n.State,
        ["title"] = n.Title,
        ["input"] = n.Input,
        ["output"] = n.Output,
        ["usage"] = UsageOut(n.Usage),
        ["activityGeneration"] = n.ActivityGeneration,
        ["responseRecords"] = n.ResponseRecords?.Select(RecordOut).ToList(),
        ["entries"] = n.Entries.Select(EntryOut).ToList(),
    };

    private static MightyGraphAgent AgentIn(JsonElement e)
    {
        var agent = new MightyGraphAgent
        {
            Id = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            ParentID = e.TryGetProperty("parentID", out var par) && par.ValueKind == JsonValueKind.String ? par.GetString() : null,
            Title = e.TryGetProperty("title", out var t) ? t.GetString() ?? "하위 에이전트" : "하위 에이전트",
            Input = e.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "",
            Status = e.TryGetProperty("status", out var st) ? st.GetString() ?? "running" : "running",
            Entries = e.TryGetProperty("entries", out var en) && en.ValueKind == JsonValueKind.Array
                ? en.EnumerateArray().Select(EntryIn).ToList() : new(),
            Kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
            Usage = e.TryGetProperty("usage", out var u) ? UsageIn(u) : null,
            ActivityGeneration = e.TryGetProperty("activityGeneration", out var ag) && ag.ValueKind == JsonValueKind.Number ? ag.GetInt32() : null,
            ResponseRecords = e.TryGetProperty("responseRecords", out var rr) && rr.ValueKind == JsonValueKind.Array
                ? rr.EnumerateArray().Select(RecordIn).ToList() : null,
        };
        return agent;
    }

    private static object AgentOut(MightyGraphAgent a) => new Dictionary<string, object?>
    {
        ["id"] = a.Id,
        ["parentID"] = a.ParentID,
        ["title"] = a.Title,
        ["input"] = a.Input,
        ["status"] = a.Status,
        ["kind"] = a.Kind,
        ["usage"] = UsageOut(a.Usage),
        ["activityGeneration"] = a.ActivityGeneration,
        ["responseRecords"] = a.ResponseRecords?.Select(RecordOut).ToList(),
        ["entries"] = a.Entries.Select(EntryOut).ToList(),
    };

    private static MightyGraphRun RunIn(JsonElement e)
    {
        var run = new MightyGraphRun
        {
            Id = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Input = e.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "",
            Status = e.TryGetProperty("status", out var st) ? st.GetString() ?? "running" : "running",
            RootEntries = e.TryGetProperty("rootEntries", out var re) && re.ValueKind == JsonValueKind.Array
                ? re.EnumerateArray().Select(EntryIn).ToList() : new(),
            Agents = e.TryGetProperty("agents", out var ag) && ag.ValueKind == JsonValueKind.Array
                ? ag.EnumerateArray().Select(AgentIn).ToList() : new(),
            ResultEntries = e.TryGetProperty("resultEntries", out var rse) && rse.ValueKind == JsonValueKind.Array
                ? rse.EnumerateArray().Select(EntryIn).ToList() : new(),
            SourceRunID = e.TryGetProperty("sourceRunID", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null,
            FinalOutput = e.TryGetProperty("finalOutput", out var fo) && fo.ValueKind == JsonValueKind.String ? fo.GetString() : null,
            Usage = e.TryGetProperty("usage", out var u) ? UsageIn(u) : null,
            ResponseRecords = e.TryGetProperty("responseRecords", out var rr) && rr.ValueKind == JsonValueKind.Array
                ? rr.EnumerateArray().Select(RecordIn).ToList() : null,
            Provider = e.TryGetProperty("provider", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null,
            NodeModelLabel = e.TryGetProperty("nodeModelLabel", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null,
            ConfiguredModel = e.TryGetProperty("configuredModel", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() : null,
        };
        return run;
    }

    private static object RunOut(MightyGraphRun run) => new Dictionary<string, object?>
    {
        ["id"] = run.Id,
        ["input"] = run.Input,
        ["status"] = run.Status,
        ["provider"] = run.Provider,
        ["finalOutput"] = run.FinalOutput,
        ["sourceRunID"] = run.SourceRunID,
        ["usage"] = UsageOut(run.Usage),
        ["nodeModelLabel"] = run.NodeModelLabel,
        ["configuredModel"] = run.ConfiguredModel,
        ["responseRecords"] = run.ResponseRecords?.Select(RecordOut).ToList(),
        ["rootEntries"] = run.RootEntries.Select(EntryOut).ToList(),
        ["resultEntries"] = run.ResultEntries.Select(EntryOut).ToList(),
        ["agents"] = run.Agents.Select(AgentOut).ToList(),
    };

    private static ModMetadata ModIn(JsonElement e)
    {
        ModGraphMetadata? graph = null;
        if (e.TryGetProperty("graph", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            graph = new ModGraphMetadata(
                Version: g.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number ? ver.GetInt32() : 1,
                Phase: g.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "starting" : "starting",
                AgentId: g.TryGetProperty("agentId", out var aid) && aid.ValueKind == JsonValueKind.String ? aid.GetString() : null,
                ParentAgentId: g.TryGetProperty("parentAgentId", out var paid) && paid.ValueKind == JsonValueKind.String ? paid.GetString() : null,
                ParentToolUseId: g.TryGetProperty("parentToolUseId", out var ptui) && ptui.ValueKind == JsonValueKind.String ? ptui.GetString() : null,
                Name: g.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null,
                AgentType: g.TryGetProperty("agentType", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null,
                Model: g.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String ? mo.GetString() : null,
                Input: g.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String ? inp.GetString() : null,
                Output: g.TryGetProperty("output", out var outp) && outp.ValueKind == JsonValueKind.String ? outp.GetString() : null);
        }
        return new ModMetadata(
            ClaudeSessionId: e.TryGetProperty("claudeSessionId", out var cs) ? cs.GetString() ?? "session" : "session",
            Event: e.TryGetProperty("event", out var ev) ? ev.GetString() ?? "agent.spawn" : "agent.spawn",
            Tool: e.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String ? tool.GetString() : null,
            ToolUseId: e.TryGetProperty("toolUseId", out var tui) && tui.ValueKind == JsonValueKind.String ? tui.GetString() : null,
            AgentId: e.TryGetProperty("agentId", out var agid) && agid.ValueKind == JsonValueKind.String ? agid.GetString() : null,
            Graph: graph);
    }

    private static AgentActivity ActivityIn(JsonElement e) => new(
        Id: e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
        Provider: e.TryGetProperty("provider", out var pr) ? pr.GetString() ?? "claude" : "claude",
        Kind: e.TryGetProperty("kind", out var k) ? k.GetString() ?? "tool" : "tool",
        State: e.TryGetProperty("state", out var st) ? st.GetString() ?? "completed" : "completed",
        Summary: e.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
        ToolName: e.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : null,
        Output: e.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null,
        DurationMs: e.TryGetProperty("durationMs", out var dm) && dm.ValueKind == JsonValueKind.Number ? dm.GetDouble() : null);

    private static List<ModelOption> CatalogIn(JsonElement args)
    {
        if (!args.TryGetProperty("catalog", out var cat) || cat.ValueKind != JsonValueKind.Array)
            return [];
        return cat.EnumerateArray().Select(c => new ModelOption(
            Value: c.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
            DisplayName: c.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
            Description: "",
            ResolvedModel: c.TryGetProperty("resolvedModel", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString() : null)).ToList();
    }

    private static List<GraphResponseRecord> RecordsIn(JsonElement args)
    {
        if (!args.TryGetProperty("records", out var rr) || rr.ValueKind != JsonValueKind.Array) return [];
        return rr.EnumerateArray().Select(RecordIn).ToList();
    }

    // ── Group runners ─────────────────────────────────────────────────────────

    private static object RunTracker(JsonElement v)
    {
        var runId = v.TryGetProperty("runId", out var ri) ? ri.GetString() ?? "run-1" : "run-1";
        var input = v.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String ? inp.GetString() : null;
        var provider = v.TryGetProperty("provider", out var pr) ? pr.GetString() ?? "claude" : "claude";
        var configuredModel = v.TryGetProperty("configuredModel", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() : null;

        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker(runId, input, provider, configuredModel, n => emitted.Add(n));

        if (v.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                var kind = step.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                step.TryGetProperty("value", out var val);
                switch (kind)
                {
                    case "frame":
                        if (val.ValueKind == JsonValueKind.Object) tracker.Consume(val);
                        break;
                    case "mod":
                        if (val.ValueKind == JsonValueKind.Object) tracker.ReceiveMod(ModIn(val));
                        break;
                    case "steer":
                        var steerId = step.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "";
                        var steerText = step.TryGetProperty("text", out var stxt) ? stxt.GetString() ?? "" : "";
                        tracker.Steer(steerId, steerText);
                        break;
                    case "activity":
                        if (val.ValueKind == JsonValueKind.Object)
                        {
                            var toolId = step.TryGetProperty("toolId", out var tid) && tid.ValueKind == JsonValueKind.String ? tid.GetString() : null;
                            tracker.Activity(ActivityIn(val), toolId);
                        }
                        break;
                    case "finish":
                        var state = step.TryGetProperty("state", out var fs) ? fs.GetString() ?? "completed" : "completed";
                        tracker.Finish(state);
                        break;
                }
            }
        }

        // Latest snapshot per node, in first-emission order
        var order = new List<string>();
        var latest = new Dictionary<string, ExecutionGraphNode>();
        foreach (var node in emitted)
        {
            if (!latest.ContainsKey(node.Id)) order.Add(node.Id);
            latest[node.Id] = node;
        }
        return new Dictionary<string, object?> { ["nodes"] = order.Select(id => NodeOut(latest[id])).ToList() };
    }

    private static object RunBounds(JsonElement v)
    {
        var runs = v.TryGetProperty("runs", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(RunIn).ToList() : new List<MightyGraphRun>();
        var restoring = v.TryGetProperty("restoring", out var rest) && rest.ValueKind == JsonValueKind.True;
        var provider = v.TryGetProperty("provider", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;
        var budget = v.TryGetProperty("budget", out var b) && b.ValueKind == JsonValueKind.Number
            ? b.GetInt32() : MightyGraphSupport.LiveHistoryLimit;
        var result = MightyGraphSupport.Normalized(runs, restoring, ref budget, provider);
        return new Dictionary<string, object?> { ["runs"] = result.Select(RunOut).ToList(), ["budget"] = budget };
    }

    private static object RunLayout(JsonElement v)
    {
        var runs = v.TryGetProperty("runs", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(RunIn).ToList() : new List<MightyGraphRun>();
        var draft = v.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        var running = v.TryGetProperty("running", out var run) && run.ValueKind == JsonValueKind.True;
        var expanded = v.TryGetProperty("expanded", out var exp) && exp.ValueKind == JsonValueKind.Array
            ? exp.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet() : new HashSet<string>();
        var resultFilesRunID = v.TryGetProperty("resultFilesRunID", out var rf) && rf.ValueKind == JsonValueKind.String ? rf.GetString() : null;
        (double W, double H)? viewport = null;
        if (v.TryGetProperty("viewport", out var vp) && vp.ValueKind == JsonValueKind.Object
            && vp.TryGetProperty("w", out var vw) && vp.TryGetProperty("h", out var vh))
            viewport = (vw.GetDouble(), vh.GetDouble());

        var layout = MightyGraphLayout.Make(runs, draft, running, expanded, resultFilesRunID, viewport, false);
        return new Dictionary<string, object?>
        {
            ["nodes"] = layout.Nodes.Select(n => new Dictionary<string, object?>
            {
                ["id"] = n.Id,
                ["kind"] = n.Kind,
                ["frame"] = new Dictionary<string, object?> { ["x"] = n.Frame.X, ["y"] = n.Frame.Y, ["w"] = n.Frame.W, ["h"] = n.Frame.H },
            }).ToList(),
            ["edges"] = layout.Edges.Select(e => new Dictionary<string, object?>
                { ["source"] = e.Source, ["target"] = e.Target, ["joins"] = e.Joins }).ToList(),
            ["size"] = new Dictionary<string, object?> { ["w"] = layout.Size.W, ["h"] = layout.Size.H },
            ["originX"] = layout.OriginX,
            ["fittedResultID"] = layout.FittedResultID,
        };
    }

    private static object? RunCamera(JsonElement v)
    {
        var fn = v.TryGetProperty("fn", out var f) ? f.GetString() ?? "" : "";
        var args = v.TryGetProperty("args", out var a) ? a : default;

        IReadOnlySet<string> LayoutNodeIDs() =>
            args.TryGetProperty("layoutNodeIDs", out var ln) && ln.ValueKind == JsonValueKind.Array
                ? ln.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet() : new HashSet<string>();
        IReadOnlyList<string> AsList(string key) =>
            args.TryGetProperty(key, out var lst) && lst.ValueKind == JsonValueKind.Array
                ? lst.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : Array.Empty<string>();
        string? StrArg(string key) => args.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

        static object AnchorOut(MightyGraphCamera.Anchor a)
        {
            if (a.Kind == "hold") return new Dictionary<string, object?> { ["kind"] = "hold" };
            return new Dictionary<string, object?> { ["kind"] = "reaim", ["nodeID"] = a.NodeID, ["alignTop"] = a.AlignTop };
        }

        switch (fn)
        {
            case "trimAnchor":
            {
                var anchor = MightyGraphCamera.TrimAnchor(
                    previousRunIDs: AsList("previousRunIDs"),
                    runIDs: AsList("runIDs"),
                    selectedNodeID: StrArg("selectedNodeID"),
                    layoutNodeIDs: LayoutNodeIDs());
                return AnchorOut(anchor);
            }
            case "reaimAnchor":
            {
                var anchor = MightyGraphCamera.ReaimAnchor(
                    newestRunID: StrArg("newestRunID"),
                    selectedNodeID: StrArg("selectedNodeID"),
                    layoutNodeIDs: LayoutNodeIDs());
                return AnchorOut(anchor);
            }
            case "resizeAnchor":
            {
                var frames = new Dictionary<string, GraphRect>();
                if (args.TryGetProperty("frames", out var frm) && frm.ValueKind == JsonValueKind.Object)
                    foreach (var p in frm.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Object)
                            frames[p.Name] = new GraphRect(
                                p.Value.TryGetProperty("x", out var rx) ? rx.GetDouble() : 0,
                                p.Value.TryGetProperty("y", out var ry) ? ry.GetDouble() : 0,
                                p.Value.TryGetProperty("w", out var rw) ? rw.GetDouble() : 0,
                                p.Value.TryGetProperty("h", out var rh) ? rh.GetDouble() : 0);
                var anchor = MightyGraphCamera.ResizeAnchor(StrArg("fittedResultID"), StrArg("targetID"),
                    args.TryGetProperty("targetAlignTop", out var tat) && tat.ValueKind == JsonValueKind.True, frames);
                return AnchorOut(anchor);
            }
            case "admittedCamera":
            {
                (double X, double Y)? PointIn(string key)
                {
                    if (!args.TryGetProperty(key, out var pt) || pt.ValueKind != JsonValueKind.Object) return null;
                    return (pt.TryGetProperty("x", out var px) ? px.GetDouble() : 0,
                            pt.TryGetProperty("y", out var py) ? py.GetDouble() : 0);
                }
                var result = MightyGraphCamera.AdmittedCamera(
                    targetToken: StrArg("targetToken"),
                    consumedToken: StrArg("consumedToken"),
                    targetCamera: PointIn("targetCamera"),
                    current: PointIn("current") ?? (0, 0),
                    requested: PointIn("requested") ?? (0, 0));
                if (result is null) return null;
                return new Dictionary<string, object?> { ["x"] = result.Value.X, ["y"] = result.Value.Y };
            }
            case "cameraOffset":
            {
                if (!args.TryGetProperty("frame", out var frm) || frm.ValueKind != JsonValueKind.Object) return null;
                var frame = new GraphRect(
                    frm.TryGetProperty("x", out var fx) ? fx.GetDouble() : 0,
                    frm.TryGetProperty("y", out var fy) ? fy.GetDouble() : 0,
                    frm.TryGetProperty("w", out var fw) ? fw.GetDouble() : 0,
                    frm.TryGetProperty("h", out var fh) ? fh.GetDouble() : 0);
                (double W, double H) vp = (0, 0);
                if (args.TryGetProperty("viewport", out var vpe) && vpe.ValueKind == JsonValueKind.Object)
                    vp = (vpe.TryGetProperty("w", out var vw) ? vw.GetDouble() : 0,
                          vpe.TryGetProperty("h", out var vh) ? vh.GetDouble() : 0);
                var zoom = args.TryGetProperty("zoom", out var z) ? z.GetDouble() : 1.0;
                var alignTop = args.TryGetProperty("alignTop", out var at) && at.ValueKind == JsonValueKind.True;
                var pt = MightyGraphCamera.CameraOffset(frame, vp, zoom, alignTop);
                return new Dictionary<string, object?> { ["x"] = pt.X, ["y"] = pt.Y };
            }
            default:
                return null;
        }
    }

    private static object? RunCapsule(JsonElement v)
    {
        var fn = v.TryGetProperty("fn", out var f) ? f.GetString() ?? "" : "";
        var args = v.TryGetProperty("args", out var a) ? a : default;
        var catalog = CatalogIn(args);
        var records = RecordsIn(args);

        switch (fn)
        {
            case "blockCapsule":
                var usage = args.TryGetProperty("usage", out var u) ? UsageIn(u) : null;
                var label = args.TryGetProperty("nodeModelLabel", out var nml) && nml.ValueKind == JsonValueKind.String ? nml.GetString() : null;
                return ModelUsageFormat.BlockCapsule(usage, records, label, catalog);

            case "blockCapsuleHelp":
                return ModelUsageFormat.BlockCapsuleHelp(records, catalog);

            case "activitySuffix":
                GraphChildBlock? child = null;
                if (args.TryGetProperty("childBlock", out var cb) && cb.ValueKind == JsonValueKind.Object)
                {
                    var childUsage = cb.TryGetProperty("usage", out var cu) ? UsageIn(cu) : null;
                    var childRecords = cb.TryGetProperty("records", out var cr) && cr.ValueKind == JsonValueKind.Array
                        ? cr.EnumerateArray().Select(RecordIn).ToList() : new List<GraphResponseRecord>();
                    child = new GraphChildBlock(childUsage, childRecords);
                }
                var actId = args.TryGetProperty("activityId", out var ai) ? ai.GetString() ?? "" : "";
                return ModelUsageFormat.ActivitySuffix(actId, records, child, catalog);

            case "shortName":
                var modelId = args.TryGetProperty("modelId", out var mid) ? mid.GetString() ?? "" : "";
                return ModelUsageFormat.ShortName(modelId, catalog);

            case "nodeModelLabel":
                var cli = args.TryGetProperty("cliReportedModel", out var crm) && crm.ValueKind == JsonValueKind.String ? crm.GetString() : null;
                var configured = args.TryGetProperty("configuredModel", out var cmod) ? cmod.GetString() ?? "default" : "default";
                return GraphModelLabel.NodeModelLabel(cli, configured);

            default:
                return null;
        }
    }

    private static object RunResultFiles(JsonElement v)
    {
        var texts = v.TryGetProperty("texts", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : new List<string>();
        var tree = v.TryGetProperty("tree", out var tr) && tr.ValueKind == JsonValueKind.Array
            ? tr.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : new List<string>();
        var tempDir = Path.Combine(Path.GetTempPath(), "graph-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var relPath in tree)
            {
                var full = Path.Combine(tempDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "fixture\n");
            }
            var files = ResultFiles.In(texts, tempDir);
            return new Dictionary<string, object?>
            {
                ["paths"] = files.Select(f => f.Path).ToList(),
                ["lines"] = files.Select(f => (object?)f.Line).ToList(),
            };
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    private static (string group, object? produced) RunCase(string group, JsonElement caseElem)
    {
        object? produced = group switch
        {
            "claudeStream" or "codexStream" or "mods" => RunTracker(caseElem),
            "bounds" => RunBounds(caseElem),
            "layout" => RunLayout(caseElem),
            "camera" => RunCamera(caseElem),
            "capsule" => RunCapsule(caseElem),
            "resultFiles" => RunResultFiles(caseElem),
            _ => null,
        };
        return (group, produced);
    }

    // ── Main test entry point ─────────────────────────────────────────────────

    public static Task RunAsync()
    {
        var path = VectorsPath();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;

        var groups = new[] { "claudeStream", "codexStream", "mods", "bounds", "layout", "camera", "capsule", "resultFiles" };
        var minimums = new Dictionary<string, int>
        {
            ["claudeStream"] = 6, ["codexStream"] = 4, ["mods"] = 3, ["bounds"] = 3,
            ["layout"] = 8, ["camera"] = 6, ["capsule"] = 8, ["resultFiles"] = 4,
        };

        // Validate minimum counts and unique names
        foreach (var group in groups)
        {
            Check(root.TryGetProperty(group, out var arr) && arr.ValueKind == JsonValueKind.Array,
                $"group {group} missing");
            Check(arr.GetArrayLength() >= minimums[group],
                $"group {group} has {arr.GetArrayLength()} cases, needs {minimums[group]}");
            var names = new HashSet<string>();
            foreach (var c in arr.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                Check(name is not null, $"a {group} case has no name");
                Check(names.Add(name!), $"duplicate {group} case name {name}");
            }
        }

        // Run all capsule/display-text cases with Korean locale
        var previousLang = Locale.LanguagePreference;
        Locale.LanguagePreference = "ko";
        Locale.ResetCache();
        try
        {
            var mismatches = new List<string>();
            var checkedCount = 0;

            foreach (var group in groups)
            {
                root.TryGetProperty(group, out var cases);
                foreach (var caseElem in cases.EnumerateArray())
                {
                    var name = caseElem.TryGetProperty("name", out var n) ? n.GetString() ?? "#?" : "#?";
                    if (!caseElem.TryGetProperty("expected", out var expected)) continue;

                    checkedCount++;
                    var (_, produced) = RunCase(group, caseElem);
                    var producedElem = ToElement(produced);
                    var committedStr = Canonical(expected);
                    var freshStr = Canonical(producedElem);

                    if (committedStr != freshStr)
                        mismatches.Add($"{group}/{name}\n  committed: {committedStr[..Math.Min(600, committedStr.Length)]}\n  csharp:    {freshStr[..Math.Min(600, freshStr.Length)]}");
                }
            }

            Check(checkedCount >= 50, $"only {checkedCount} cases checked — too few");
            Check(mismatches.Count == 0,
                "vectors no longer match the C# implementation:\n" + string.Join("\n", mismatches));
        }
        finally
        {
            Locale.LanguagePreference = previousLang;
            Locale.ResetCache();
        }

        return Task.CompletedTask;
    }

    // ── AC3: persistence field names ──────────────────────────────────────────

    internal static Task SessionFieldNames()
    {
        // Use non-null values so WhenWritingNull doesn't suppress the fields.
        var session = new RunSession { AgentViewMode = "mighty", GraphRuns = [] };
        var json = JsonSerializer.SerializeToElement(session, Wire.Json);
        Check(json.TryGetProperty("agentViewMode", out _), "RunSession must have agentViewMode JSON field");
        Check(json.TryGetProperty("graphRuns", out _), "RunSession must have graphRuns JSON field");

        var run = new MightyGraphRun
        {
            Id = "r1", Input = "hi", Status = "completed", SourceRunID = "s1", FinalOutput = "done",
            Provider = "claude", NodeModelLabel = "n", ConfiguredModel = "c",
            Usage = new GraphTokenUsage { InputTokens = 1 }, ResponseRecords = [],
        };
        var runEl = JsonSerializer.SerializeToElement(run, Wire.Json);
        foreach (var key in new[] { "id", "input", "status", "rootEntries", "agents", "resultEntries", "sourceRunID", "finalOutput", "usage", "responseRecords", "provider", "nodeModelLabel", "configuredModel" })
            Check(runEl.TryGetProperty(key, out _), $"MightyGraphRun missing JSON field: {key}");

        var agent = new MightyGraphAgent
        {
            Id = "a1", ParentID = "p1", Title = "t", Input = "i", Status = "running", Kind = "task",
            Usage = new GraphTokenUsage { InputTokens = 1 }, ActivityGeneration = 1, ResponseRecords = [],
        };
        var agentEl = JsonSerializer.SerializeToElement(agent, Wire.Json);
        foreach (var key in new[] { "id", "parentID", "title", "input", "status", "entries", "kind", "usage", "activityGeneration", "responseRecords" })
            Check(agentEl.TryGetProperty(key, out _), $"MightyGraphAgent missing JSON field: {key}");

        return Task.CompletedTask;
    }

    // ── AC3: wiring — tracker → BuildRun → AppSnapshot.Apply → GraphRuns ──────

    internal static Task RunsRecorded()
    {
        const string runId = "test-recorded-run";
        const string input = "Print hello";
        var tracker = new ExecutionGraphTracker(runId, input, "claude", "claude-sonnet-4-5", _ => { });

        using var msgDoc = JsonDocument.Parse(
            """{"type":"assistant","message":{"id":"msg1","model":"claude-sonnet-4-5","content":[{"type":"text","text":"Hello!"}],"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""");
        tracker.Consume(msgDoc.RootElement);
        using var resultDoc = JsonDocument.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"Hello!"}""");
        tracker.Consume(resultDoc.RootElement);
        tracker.Finish("completed");

        var run = tracker.BuildRun();
        Check(run is not null, "BuildRun must return non-null after Finish");
        Check(run!.Id == runId, $"run.Id: {run.Id} vs {runId}");
        Check(run.Status == "completed", $"run.Status: {run.Status}");
        Check(run.Input == input, $"run.Input: {run.Input}");
        Check(run.Provider == "claude", $"run.Provider: {run.Provider}");

        var ev = new RunEvent("session-1", "graph_run", GraphRun: run);
        Check(ev.Valid(), "graph_run RunEvent must pass Valid()");

        // AppSnapshot.Apply (public) routes into RunSession internally.
        var snapshot = new AppSnapshot { Sessions = [new RunSession { Id = "session-1", WorkspaceId = "ws-1", Kind = "claude", Provider = "claude" }] };
        var updated = snapshot.Apply(ev);
        var updatedSession = updated.Sessions.First();
        Check(updatedSession.GraphRuns is { Count: 1 }, $"GraphRuns should have 1 run, got {updatedSession.GraphRuns?.Count ?? 0}");
        Check(updatedSession.GraphRuns![0].Id == runId, "stored run ID must match");
        Check(updatedSession.GraphRuns[0].Status == "completed", "stored run status must be completed");

        // Shell sessions must not accumulate graph runs.
        var shellSnapshot = new AppSnapshot { Sessions = [new RunSession { Id = "session-1", WorkspaceId = "ws-1", Kind = "shell", Provider = "claude" }] };
        var shellUpdated = shellSnapshot.Apply(ev);
        Check(shellUpdated.Sessions.First().GraphRuns is null, "shell session must not accept graph_run events");

        return Task.CompletedTask;
    }
}
