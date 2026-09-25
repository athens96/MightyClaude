using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

// native/contracts/graph-vectors.json is macOS truth for the execution graph:
// GraphParityVectorTests re-derives every committed expectation by running the
// Swift implementation, and these checks re-derive the same expectations by
// running the Windows port. Frames compare within 0.001; relative paths compare
// with forward slashes; every case runs with the Korean locale, so the committed
// display strings are the ko copy the port reads from locales/ko.json.
internal static class GraphVectorVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Document = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.GraphVectors.json";
        using var stream = typeof(GraphVectorVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    /// The minimum number of cases each group must carry (macOS GraphVectors.minimumCounts).
    private static readonly (string Group, int Minimum)[] MinimumCounts =
    [
        ("claudeStream", 6), ("codexStream", 4), ("mods", 3), ("bounds", 3),
        ("layout", 8), ("camera", 6), ("capsule", 8), ("resultFiles", 4),
    ];
    /// Entries fed in as input carry this fixed timestamp so nothing in an
    /// expectation depends on the clock.
    private const string FixedTimestamp = "2026-01-01T00:00:00Z";

    // ── the shared runner ────────────────────────────────────────────────────

    /// Runs every case of one group and reports the cases whose produced value
    /// differs from the committed macOS expectation.
    private static void RunGroup(string group, Func<JsonElement, object?> produce)
    {
        var minimum = MinimumCounts.Single(entry => entry.Group == group).Minimum;
        Check(Document.Value.TryGetProperty("version", out var version) && version.GetInt32() == 1, "graph-vectors.json version must be 1");
        Check(Document.Value.TryGetProperty(group, out var cases) && cases.ValueKind == JsonValueKind.Array, "missing group " + group);
        var names = new HashSet<string>();
        var count = 0;
        var mismatches = new List<string>();
        foreach (var value in cases.EnumerateArray())
        {
            var name = value.Text("name") ?? throw new InvalidOperationException("a " + group + " case has no name");
            Check(names.Add(name), "duplicate " + group + " case name " + name);
            Check(value.TryGetProperty("expected", out var expected), group + "/" + name + " has no expected value");
            count += 1;
            if (Difference(expected, produce(value), group + "/" + name) is { } reason) mismatches.Add(reason);
        }
        Check(count >= minimum, $"group {group} has {count} cases, needs {minimum}");
        Check(mismatches.Count == 0, "Windows does not reproduce macOS:\n" + string.Join("\n", mismatches));
    }

    /// Structural comparison against the committed expectation. Numbers compare
    /// within 0.001 so layout frames survive double arithmetic on either side.
    private static string? Difference(JsonElement expected, object? produced, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                return produced is null ? null : path + ": expected null, produced " + Show(produced);
            case JsonValueKind.True:
            case JsonValueKind.False:
                return produced is bool flag && flag == (expected.ValueKind == JsonValueKind.True)
                    ? null : path + ": expected " + expected.GetRawText() + ", produced " + Show(produced);
            case JsonValueKind.Number:
                if (produced is null) return path + ": expected " + expected.GetRawText() + ", produced null";
                if (!TryNumber(produced, out var number)) return path + ": expected a number, produced " + Show(produced);
                return Math.Abs(expected.GetDouble() - number) <= 0.001
                    ? null : path + ": expected " + expected.GetRawText() + ", produced " + Show(produced);
            case JsonValueKind.String:
                return produced is string text && text == expected.GetString()
                    ? null : path + ": expected " + JsonSerializer.Serialize(expected.GetString()) + ", produced " + Show(produced);
            case JsonValueKind.Array:
                if (produced is string || produced is not System.Collections.IEnumerable items) return path + ": expected an array, produced " + Show(produced);
                var list = items.Cast<object?>().ToList();
                var elements = expected.EnumerateArray().ToList();
                if (list.Count != elements.Count) return $"{path}: expected {elements.Count} items, produced {list.Count}";
                for (var index = 0; index < list.Count; index++)
                    if (Difference(elements[index], list[index], $"{path}[{index}]") is { } reason) return reason;
                return null;
            case JsonValueKind.Object:
                if (produced is not IReadOnlyDictionary<string, object?> map) return path + ": expected an object, produced " + Show(produced);
                var keys = expected.EnumerateObject().Select(p => p.Name).ToHashSet();
                if (!keys.SetEquals(map.Keys))
                    return $"{path}: expected keys [{string.Join(",", keys.Order())}], produced [{string.Join(",", map.Keys.Order())}]";
                foreach (var property in expected.EnumerateObject())
                    if (Difference(property.Value, map[property.Name], path + "." + property.Name) is { } reason) return reason;
                return null;
            default:
                return path + ": unsupported expectation";
        }
    }
    private static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case double d: number = d; return true;
            default: number = 0; return false;
        }
    }
    private static string Show(object? value) => value switch
    {
        null => "null",
        string text => JsonSerializer.Serialize(text),
        IReadOnlyDictionary<string, object?> map => "{" + string.Join(",", map.Keys.Order()) + "}",
        System.Collections.IEnumerable items => "[" + items.Cast<object?>().Count() + " items]",
        _ => value.ToString() ?? "",
    };

    // ── JSON shapes, one per model the vectors carry ─────────────────────────

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] fields)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in fields) result[key] = value;
        return result;
    }
    private static object? UsageJson(GraphTokenUsage? usage) => usage is null ? null : Map(
        ("inputTokens", usage.InputTokens), ("outputTokens", usage.OutputTokens),
        ("cacheReadTokens", usage.CacheReadTokens), ("cacheCreationTokens", usage.CacheCreationTokens));
    private static object RecordJson(GraphResponseRecord record) => Map(
        ("responseId", record.ResponseId), ("model", record.Model), ("usage", UsageJson(record.Usage)),
        ("activityIds", record.ActivityIds.Cast<object?>().ToList()), ("markedAsConfigured", record.MarkedAsConfigured));
    // Timestamps are wall-clock and never part of an expectation.
    private static object EntryJson(LogEntry entry) => Map(
        ("id", entry.Id), ("kind", entry.Kind), ("text", entry.Text), ("provider", entry.Provider));
    private static object NodeJson(ExecutionGraphNode node) => Map(
        ("id", node.Id), ("runId", node.RunId), ("parentId", node.ParentId), ("kind", node.Kind),
        ("state", node.State), ("title", node.Title), ("input", node.Input), ("output", node.Output),
        ("usage", UsageJson(node.Usage)), ("activityGeneration", node.ActivityGeneration),
        ("responseRecords", node.ResponseRecords?.Select(RecordJson).ToList()),
        ("entries", node.Entries.Select(EntryJson).ToList()));
    private static object AgentJson(MightyGraphAgent agent) => Map(
        ("id", agent.Id), ("parentID", agent.ParentID), ("title", agent.Title), ("input", agent.Input),
        ("status", agent.Status), ("kind", agent.Kind), ("usage", UsageJson(agent.Usage)),
        ("activityGeneration", agent.ActivityGeneration),
        ("responseRecords", agent.ResponseRecords?.Select(RecordJson).ToList()),
        ("entries", agent.Entries.Select(EntryJson).ToList()));
    private static object RunJson(MightyGraphRun run) => Map(
        ("id", run.Id), ("input", run.Input), ("status", run.Status), ("provider", run.Provider),
        ("finalOutput", run.FinalOutput), ("sourceRunID", run.SourceRunID), ("usage", UsageJson(run.Usage)),
        ("nodeModelLabel", run.NodeModelLabel), ("configuredModel", run.ConfiguredModel),
        ("responseRecords", run.ResponseRecords?.Select(RecordJson).ToList()),
        ("rootEntries", run.RootEntries.Select(EntryJson).ToList()),
        ("resultEntries", run.ResultEntries.Select(EntryJson).ToList()),
        ("agents", run.Agents.Select(AgentJson).ToList()));
    private static object RectJson(GraphRect rect) => Map(("x", rect.X), ("y", rect.Y), ("w", rect.W), ("h", rect.H));
    private static object AnchorJson(MightyGraphCamera.Anchor anchor) => anchor.Kind == "hold"
        ? Map(("kind", "hold")) : Map(("kind", "reaim"), ("nodeID", anchor.NodeID), ("alignTop", anchor.AlignTop));

    // ── reading a case's inputs ──────────────────────────────────────────────

    private static GraphTokenUsage? UsageIn(JsonElement value) => value.ValueKind != JsonValueKind.Object ? null
        : new(Long(value, "inputTokens"), Long(value, "outputTokens"), Long(value, "cacheReadTokens"), Long(value, "cacheCreationTokens"));
    private static long Long(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt64() : 0;
    private static double? Double(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
    private static bool Flag(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    private static JsonElement Field(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? item : default;
    private static List<string> Strings(JsonElement value) => value.ValueKind != JsonValueKind.Array ? []
        : value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
    private static List<JsonElement> Items(JsonElement value) => value.ValueKind != JsonValueKind.Array ? [] : [.. value.EnumerateArray()];

    private static GraphResponseRecord RecordIn(JsonElement value) => new(
        value.Text("responseId") ?? "", value.Text("model"), UsageIn(Field(value, "usage")) ?? new(),
        Strings(Field(value, "activityIds")), Flag(value, "markedAsConfigured"));
    private static LogEntry EntryIn(JsonElement value) => new(
        value.Text("id") ?? "", value.Text("kind") ?? "system", value.Text("text") ?? "",
        value.Text("timestamp") ?? FixedTimestamp, value.Text("provider"));
    private static MightyGraphAgent AgentIn(JsonElement value) => new()
    {
        Id = value.Text("id") ?? "", ParentID = value.Text("parentID"),
        Title = value.Text("title") ?? Locale.Get("graph.block.agent"), Input = value.Text("input") ?? "",
        Status = value.Text("status") ?? "running", Entries = Items(Field(value, "entries")).Select(EntryIn).ToList(),
        Kind = value.Text("kind"), Usage = UsageIn(Field(value, "usage")),
        ActivityGeneration = Field(value, "activityGeneration").ValueKind == JsonValueKind.Number ? Field(value, "activityGeneration").GetInt32() : null,
        ResponseRecords = Field(value, "responseRecords").ValueKind == JsonValueKind.Array ? Items(Field(value, "responseRecords")).Select(RecordIn).ToList() : null,
    };
    private static MightyGraphRun RunIn(JsonElement value) => new()
    {
        Id = value.Text("id") ?? "", Input = value.Text("input") ?? "", Status = value.Text("status") ?? "running",
        RootEntries = Items(Field(value, "rootEntries")).Select(EntryIn).ToList(),
        Agents = Items(Field(value, "agents")).Select(AgentIn).ToList(),
        SourceRunID = value.Text("sourceRunID"), FinalOutput = value.Text("finalOutput"),
        Usage = UsageIn(Field(value, "usage")),
        ResponseRecords = Field(value, "responseRecords").ValueKind == JsonValueKind.Array ? Items(Field(value, "responseRecords")).Select(RecordIn).ToList() : null,
        Provider = value.Text("provider"), NodeModelLabel = value.Text("nodeModelLabel"), ConfiguredModel = value.Text("configuredModel"),
    };
    private static AgentActivity ActivityIn(JsonElement value) => new(
        value.Text("id") ?? "", value.Text("provider") ?? "claude", value.Text("kind") ?? "tool",
        value.Text("state") ?? "completed", value.Text("summary") ?? "", value.Text("toolName"),
        value.Text("output"), Double(value, "durationMs"));
    private static ModMetadata ModIn(JsonElement value)
    {
        var raw = Field(value, "graph");
        ModGraphMetadata? graph = raw.ValueKind != JsonValueKind.Object ? null : new(
            raw.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1,
            raw.Text("phase") ?? "starting", raw.Text("agentId"), raw.Text("parentAgentId"), raw.Text("parentToolUseId"),
            raw.Text("name"), raw.Text("agentType"), raw.Text("model"), raw.Text("input"), raw.Text("output"));
        return new(value.Text("claudeSessionId") ?? "session", value.Text("event") ?? "agent.spawn",
            value.Text("tool"), value.Text("toolUseId"), value.Text("agentId"), graph);
    }
    private static List<ModelOption> CatalogIn(JsonElement value) => Items(value)
        .Select(o => new ModelOption(o.Text("value") ?? "", o.Text("displayName") ?? "", "", o.Text("resolvedModel"))).ToList();

    // ── group runners ───────────────────────────────────────────────────────

    /// claudeStream, codexStream and mods all drive ExecutionGraphTracker:
    /// a run identity plus an ordered list of steps.
    private static object RunTracker(JsonElement value)
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker(value.Text("runId") ?? "run-1", value.Text("input"),
            value.Text("provider") ?? "claude", value.Text("configuredModel"), emitted.Add);
        foreach (var step in Items(Field(value, "steps")))
            switch (step.Text("kind"))
            {
                case "frame": tracker.Consume(Field(step, "value")); break;
                case "mod": tracker.ReceiveMod(ModIn(Field(step, "value"))); break;
                case "steer": tracker.Steer(step.Text("id") ?? "", step.Text("text") ?? ""); break;
                case "activity": tracker.Activity(ActivityIn(Field(step, "value")), step.Text("toolId")); break;
                case "finish": tracker.Finish(step.Text("state") ?? "completed"); break;
            }
        // The latest snapshot of every node, in first-emission order.
        var order = new List<string>();
        var latest = new Dictionary<string, ExecutionGraphNode>();
        foreach (var node in emitted)
        {
            if (!latest.ContainsKey(node.Id)) order.Add(node.Id);
            latest[node.Id] = node;
        }
        return Map(("nodes", order.Select(id => NodeJson(latest[id])).ToList()));
    }

    private static object RunBounds(JsonElement value)
    {
        var runs = Items(Field(value, "runs")).Select(RunIn).ToList();
        if (value.Text("op") == "boundedLiveHistory")
            return Map(("runs", MightyGraphSupport.BoundedLiveHistory(runs).Select(RunJson).ToList()), ("budget", null));
        var budget = Field(value, "budget").ValueKind == JsonValueKind.Number ? Field(value, "budget").GetInt32() : MightyGraphSupport.LiveHistoryLimit;
        var result = MightyGraphSupport.Normalized(runs, Flag(value, "restoring"), ref budget, value.Text("provider"));
        return Map(("runs", result.Select(RunJson).ToList()), ("budget", budget));
    }

    private static object RunLayout(JsonElement value)
    {
        var viewport = Field(value, "viewport");
        var layout = MightyGraphLayout.Make(
            Items(Field(value, "runs")).Select(RunIn).ToList(), value.Text("draft") ?? "", Flag(value, "running"),
            Strings(Field(value, "expanded")).ToHashSet(), value.Text("resultFilesRunID"),
            viewport.ValueKind == JsonValueKind.Object ? (Double(viewport, "w") ?? 0, Double(viewport, "h") ?? 0) : null);
        return Map(
            ("nodes", layout.Nodes.Select(n => (object)Map(("id", n.Id), ("kind", n.Kind), ("frame", RectJson(n.Frame)))).ToList()),
            ("edges", layout.Edges.Select(e => (object)Map(("source", e.Source), ("target", e.Target), ("joins", e.Joins))).ToList()),
            ("size", Map(("w", layout.Size.W), ("h", layout.Size.H))),
            ("originX", layout.OriginX),
            ("fittedResultID", layout.FittedResultID));
    }

    private static object? RunCamera(JsonElement value)
    {
        var args = Field(value, "args");
        (double X, double Y)? Point(string key)
        {
            var raw = Field(args, key);
            return raw.ValueKind == JsonValueKind.Object ? (Double(raw, "x") ?? 0, Double(raw, "y") ?? 0) : null;
        }
        (double W, double H) SizeOf(string key)
        {
            var raw = Field(args, key);
            return raw.ValueKind == JsonValueKind.Object ? (Double(raw, "w") ?? 0, Double(raw, "h") ?? 0) : (0, 0);
        }
        GraphRect RectOf(JsonElement raw) => new(Double(raw, "x") ?? 0, Double(raw, "y") ?? 0, Double(raw, "w") ?? 0, Double(raw, "h") ?? 0);
        switch (value.Text("fn"))
        {
            case "trimAnchor":
                return AnchorJson(MightyGraphCamera.TrimAnchor(Strings(Field(args, "previousRunIDs")), Strings(Field(args, "runIDs")),
                    args.Text("selectedNodeID"), Strings(Field(args, "layoutNodeIDs")).ToHashSet()));
            case "reaimAnchor":
                return AnchorJson(MightyGraphCamera.ReaimAnchor(args.Text("newestRunID"), args.Text("selectedNodeID"),
                    Strings(Field(args, "layoutNodeIDs")).ToHashSet()));
            case "resizeAnchor":
                var frames = new Dictionary<string, GraphRect>();
                if (Field(args, "frames").ValueKind == JsonValueKind.Object)
                    foreach (var property in Field(args, "frames").EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.Object) frames[property.Name] = RectOf(property.Value);
                return AnchorJson(MightyGraphCamera.ResizeAnchor(args.Text("fittedResultID"), args.Text("targetID"), Flag(args, "targetAlignTop"), frames));
            case "cameraOffset":
                var offset = MightyGraphCamera.CameraOffset(RectOf(Field(args, "frame")), SizeOf("viewport"), Double(args, "zoom") ?? 1, Flag(args, "alignTop"));
                return Map(("x", offset.X), ("y", offset.Y));
            case "lostFrameIndex":
                var index = MightyGraphCamera.LostFrameIndex(Items(Field(args, "previousFrames")).Select(RectOf).ToList(),
                    Items(Field(args, "currentFrames")).Select(RectOf).ToList(), Point("camera") ?? (0, 0), SizeOf("viewport"), Double(args, "zoom") ?? 1);
                return Map(("index", index), ("stranded", index is not null));
            case "admittedCamera":
                var admitted = MightyGraphCamera.AdmittedCamera(args.Text("targetToken"), args.Text("consumedToken"),
                    Point("targetCamera"), Point("current") ?? (0, 0), Point("requested") ?? (0, 0));
                return admitted is null ? null : Map(("x", admitted.Value.X), ("y", admitted.Value.Y));
            case "trimToken":
                return MightyGraphCamera.TrimToken((int)Long(args, "sequence"), args.Text("nodeID") ?? "");
            case "originX":
                return Map(("originX", MightyGraphCamera.OriginX(Double(args, "leadingMinX") ?? 0)),
                    ("canvasWidth", MightyGraphCamera.CanvasWidth(Double(args, "leadingMinX") ?? 0, Double(args, "trailingMaxX") ?? 0)));
            default: return null;
        }
    }

    private static object? RunCapsule(JsonElement value)
    {
        var args = Field(value, "args");
        var catalog = CatalogIn(Field(args, "catalog"));
        var records = Items(Field(args, "records")).Select(RecordIn).ToList();
        switch (value.Text("fn"))
        {
            case "blockCapsule":
                return ModelUsageFormat.BlockCapsule(UsageIn(Field(args, "usage")), records, args.Text("nodeModelLabel"), catalog);
            case "blockCapsuleHelp":
                return ModelUsageFormat.BlockCapsuleHelp(records, catalog);
            case "activitySuffix":
                var raw = Field(args, "childBlock");
                var child = raw.ValueKind != JsonValueKind.Object ? null
                    : new GraphChildBlock(UsageIn(Field(raw, "usage")), Items(Field(raw, "records")).Select(RecordIn).ToList());
                return ModelUsageFormat.ActivitySuffix(args.Text("activityId") ?? "", records, child, catalog);
            case "callerAttribution":
                return ModelUsageFormat.CallerAttribution(args.Text("activityId") ?? "", records);
            case "blockModels":
                return ModelUsageFormat.BlockModels(records).Select(m => (object)Map(("model", m.Model), ("usage", UsageJson(m.Usage)))).ToList();
            case "shortName":
                return ModelUsageFormat.ShortName(args.Text("modelId") ?? "", catalog);
            case "nodeModelLabel":
                return GraphModelLabel.NodeModelLabel(args.Text("cliReportedModel"), args.Text("configuredModel") ?? "default");
            case "blockTitle":
                return MightyGraphSupport.BlockTitle(AgentIn(Field(args, "agent")));
            case "blockKind":
                return MightyGraphSupport.BlockKind(AgentIn(Field(args, "agent")));
            case "usageSummary":
                var usage = UsageIn(Field(args, "usage")) ?? new GraphTokenUsage();
                return Map(("summary", usage.Summary), ("detail", usage.Detail), ("compact", GraphTokenUsage.Compact(usage.Total)));
            case "childBlocks":
                var map = GraphChildBlocks.Map(
                    Field(args, "records").ValueKind == JsonValueKind.Array ? Items(Field(args, "records")).Select(RecordIn).ToList() : null,
                    Items(Field(args, "agents")).Select(AgentIn).ToList(), args.Text("runId") ?? "");
                return map.Keys.Order(StringComparer.Ordinal).Select(key => (object)Map(
                    ("activityId", key), ("usage", UsageJson(map[key].Usage)),
                    ("records", map[key].Records.Select(RecordJson).ToList()))).ToList();
            default: return null;
        }
    }

    /// Materializes the listed workspace tree in a temporary root, runs the
    /// result-file rule over the case's texts, and returns the relative paths.
    private static object RunResultFiles(JsonElement value)
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-vectors-" + Wire.Id());
        Directory.CreateDirectory(root);
        try
        {
            foreach (var relative in Strings(Field(value, "tree")))
            {
                var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "fixture\n");
            }
            var files = ResultFiles.In(Strings(Field(value, "texts")), root);
            return Map(("paths", files.Select(f => (object?)f.Path).ToList()), ("lines", files.Select(f => (object?)f.Line).ToList()));
        }
        finally { Directory.Delete(root, true); }
    }

    // ── the named checks ────────────────────────────────────────────────────

    internal static Task ClaudeStream() { RunGroup("claudeStream", RunTracker); return Task.CompletedTask; }
    internal static Task CodexStream() { RunGroup("codexStream", RunTracker); return Task.CompletedTask; }
    internal static Task Mods() { RunGroup("mods", RunTracker); return Task.CompletedTask; }
    internal static Task Bounds() { RunGroup("bounds", RunBounds); return Task.CompletedTask; }
    internal static Task Layout() { RunGroup("layout", RunLayout); return Task.CompletedTask; }
    internal static Task Camera() { RunGroup("camera", RunCamera); return Task.CompletedTask; }
    internal static Task Capsule() { RunGroup("capsule", RunCapsule); return Task.CompletedTask; }
    internal static Task Files() { RunGroup("resultFiles", RunResultFiles); return Task.CompletedTask; }

    /// The saved profile carries the graph under the macOS field names, loads a
    /// snapshot without them unchanged, and restores running blocks as stopped.
    /// Block resize is out of scope: graphBlockSizes/graphResultSize are absent.
    internal static async Task SessionFields()
    {
        var workspace = new Workspace { Path = Path.GetTempPath() };
        var run = new MightyGraphRun
        {
            Id = "request-one", Input = "Refactor", Status = "running", Provider = "claude",
            ConfiguredModel = "claude-opus-5", NodeModelLabel = "claude-opus-5 " + Locale.Get("graph.nodeModel.configuredSuffix"),
            SourceRunID = "process-one", Usage = new(100, 20), FinalOutput = "partial",
            ResponseRecords = [new("m1", "claude-opus-5", new(100, 20), ["tool-1"])],
            Agents =
            [
                new() { Id = "graph-child", Title = "Reader", Input = "Read", Status = "running", Usage = new(10, 2), ActivityGeneration = 1 },
                // Every optional field populated, so the save names them all.
                new()
                {
                    Id = "graph-grandchild", ParentID = "graph-child", Title = "Build", Input = "npm run build", Status = "running",
                    Kind = "task", Usage = new(5, 1), ActivityGeneration = 2,
                    ResponseRecords = [new("m2", "claude-opus-5", new(5, 1), ["tool-2"])],
                    Entries = [new("graph-grandchild-launch", "system", "started", "2026-01-01T00:00:00Z", "claude")],
                },
            ],
        };
        var session = new RunSession { WorkspaceId = workspace.Id, Provider = "claude", AgentViewMode = "mighty", GraphRuns = [run] };
        var encoded = JsonSerializer.Serialize(new AppSnapshot { Version = 1, Workspaces = [workspace], Sessions = [session] }, Wire.Json);
        using (var document = JsonDocument.Parse(encoded))
        {
            var saved = document.RootElement.GetProperty("sessions")[0];
            Check(document.RootElement.GetProperty("version").GetInt32() == 1, "AppSnapshot version must stay 1");
            Check(saved.Text("agentViewMode") == "mighty", "RunSession must serialize agentViewMode");
            Check(saved.TryGetProperty("graphRuns", out var runs) && runs.GetArrayLength() == 1, "RunSession must serialize graphRuns");
            foreach (var key in new[] { "graphBlockSizes", "graphResultSize" })
                Check(!saved.TryGetProperty(key, out _), key + " is block resize, which is out of scope");
            var saveNames = runs[0].EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var name in new[] { "id", "input", "status", "rootEntries", "agents", "resultEntries", "sourceRunID", "finalOutput", "usage", "responseRecords", "provider", "nodeModelLabel", "configuredModel" })
                Check(saveNames.Contains(name), "graphRuns must use the macOS MightyGraphRun name " + name);
            var agentNames = runs[0].GetProperty("agents")[1].EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var name in new[] { "id", "parentID", "title", "input", "status", "entries", "kind", "usage", "activityGeneration", "responseRecords" })
                Check(agentNames.Contains(name), "graphRuns agents must use the macOS MightyGraphAgent name " + name);
            var usageNames = runs[0].GetProperty("usage").EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var name in new[] { "inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens" })
                Check(usageNames.Contains(name), "usage must use the macOS GraphTokenUsage name " + name);
            var recordNames = runs[0].GetProperty("responseRecords")[0].EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var name in new[] { "responseId", "model", "usage", "activityIds", "markedAsConfigured" })
                Check(recordNames.Contains(name), "responseRecords must use the macOS GraphResponseRecord name " + name);
        }

        var directory = Verification.Temp();
        try
        {
            await StateStore.AtomicWriteAsync(Path.Combine(directory, "workspace-state.json"), System.Text.Encoding.UTF8.GetBytes(encoded));
            var loaded = await new StateStore(directory).LoadAsync();
            var restored = loaded.Sessions.Single();
            Check(restored.AgentViewMode == "mighty", "agentViewMode must survive a restart");
            var graph = restored.GraphRuns!.Single();
            Check(graph.Status == "stopped" && graph.Agents.All(a => a.Status == "stopped"), "restoring turns running graph blocks into stopped");
            Check(graph.Agents.Count == 2 && graph.Agents[1].ParentID == "graph-child" && graph.Agents[1].Kind == "task", "the child tree itself must survive");
            Check(graph.Id == "request-one" && graph.SourceRunID == "process-one" && graph.Usage!.Total == 120, "the saved graph itself must survive");
        }
        finally { Directory.Delete(directory, true); }

        // Anything other than "default"/"mighty" loads as no choice at all.
        foreach (var raw in new[] { "\"grid\"", "12", "null", "true" })
        {
            var node = JsonNode.Parse(encoded)!;
            node["sessions"]![0]!["agentViewMode"] = JsonNode.Parse(raw);
            var directory2 = Verification.Temp();
            try
            {
                await StateStore.AtomicWriteAsync(Path.Combine(directory2, "workspace-state.json"), System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));
                var loaded = await new StateStore(directory2).LoadAsync();
                Check(loaded.Sessions.Single().AgentViewMode is null, "agentViewMode " + raw + " must load as null");
            }
            finally { Directory.Delete(directory2, true); }
        }

        // A snapshot written before the two fields existed loads unchanged.
        var legacy = JsonNode.Parse(encoded)!;
        legacy["sessions"]![0]!.AsObject().Remove("agentViewMode");
        legacy["sessions"]![0]!.AsObject().Remove("graphRuns");
        var directory3 = Verification.Temp();
        try
        {
            await StateStore.AtomicWriteAsync(Path.Combine(directory3, "workspace-state.json"), System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString()));
            var loaded = await new StateStore(directory3).LoadAsync();
            var restored = loaded.Sessions.Single();
            Check(restored.AgentViewMode is null && restored.GraphRuns is null, "a snapshot without the fields loads unchanged");
        }
        finally { Directory.Delete(directory3, true); }
    }

    /// The run pipeline itself: a Claude and a Codex run each record their graph
    /// into the session; a Gemini run and a shell run record none.
    internal static async Task RecordedFromARun()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            foreach (var (provider, expected) in new[] { ("claude", true), ("codex", true), ("gemini", false) })
            {
                var record = Path.Combine(directory, provider + "-run");
                var events = new List<RunEvent>();
                await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", provider, record)));
                await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, Path.Combine(directory, "plugin"), ev => { lock (events) events.Add(ev); });
                var id = "pane-" + provider;
                var session = new RunSession { Id = id, WorkspaceId = workspace.Id, Provider = provider, Kind = "claude", Model = "default" };
                var snapshot = new AppSnapshot { Version = 1, Workspaces = [workspace], Sessions = [session] };
                // The composer records the request itself, exactly as the app does.
                snapshot = snapshot.Apply(RunEvent.Log(id, "user", "Say hello", provider));
                await manager.StartAsync(new(id, workspace.Id, "claude", "Say hello", [], Provider: provider));
                await Verification.Until(() => { lock (events) return events.Any(e => e.SessionId == id && e.Type == "status" && e.Status is "completed" or "error" or "stopped"); });
                RunEvent[] received; lock (events) received = [.. events];
                foreach (var ev in received) snapshot = snapshot.Apply(ev);
                var graphRuns = snapshot.Sessions.Single().GraphRuns;
                if (!expected) { Check(graphRuns is null, provider + " must record no graph"); continue; }
                Check(received.Any(e => e.Type == "graph_run"), provider + " run must hand its graph to the session");
                Check(graphRuns is { Count: 1 }, provider + " run must record exactly one graph run");
                var run = graphRuns!.Single();
                Check(run.Provider == provider, provider + " graph run must carry its provider");
                Check(MightyGraphSupport.Terminal(run.Status), provider + " graph run must be settled");
                Check(run.Input == "Say hello", provider + " graph run must keep the request it observed");
                if (provider == "codex") Check(run.FinalOutput == "FAKE_CLI_OK", "the codex graph run must keep the answer the CLI reported");
            }

            // A shell pane has no agent graph at all.
            var shellEvents = new List<RunEvent>();
            await using (var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)))
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, directory, ev => { lock (shellEvents) shellEvents.Add(ev); }))
            {
                var shell = new RunSession { Id = "pane-shell", WorkspaceId = workspace.Id, Kind = "shell" };
                var snapshot = new AppSnapshot { Version = 1, Workspaces = [workspace], Sessions = [shell] };
                await manager.StartAsync(new("pane-shell", workspace.Id, "shell", "echo hello", []));
                await Verification.Until(() => { lock (shellEvents) return shellEvents.Any(e => e.Type == "status" && e.Status is "completed" or "error" or "stopped"); });
                RunEvent[] received; lock (shellEvents) received = [.. shellEvents];
                foreach (var ev in received) snapshot = snapshot.Apply(ev);
                Check(!received.Any(e => e.Type == "graph_run"), "a shell run must not produce a graph");
                Check(snapshot.Sessions.Single().GraphRuns is null, "a shell pane must record no graph");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
