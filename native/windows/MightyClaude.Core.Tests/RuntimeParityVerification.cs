using System.Text.Json;
using System.Text;
using MightyClaude.Core;

/// <summary>The parser and graph together: child ownership, pictures, consent and resume identity across stdio and Mods.</summary>
internal static class RuntimeParityVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static string Line(object value) => JsonSerializer.Serialize(value, Wire.Json);

    internal static Task ApprovalStateWinsDelayedMods()
    {
        foreach (var (literal, expected) in new (string, long)[] { ("1.0", 1), ("1e0", 1), ("-0.0", 0), ("9007199254740991", 9_007_199_254_740_991), ("-9007199254740991", -9_007_199_254_740_991) })
            Check(MetadataJson.SafeInteger(Json("{\"n\":" + literal + "}"), "n") == expected, "Mods accept safe integral JSON numbers: " + literal);
        foreach (var literal in new[] { "null", "true", "\"1\"", "0.5", "1e309", "9007199254740992", "-9007199254740992" })
            Check(MetadataJson.SafeInteger(Json("{\"n\":" + literal + "}"), "n") is null, "Mods reject non-integral, non-finite and unsafe integers: " + literal);
        Check(MetadataJson.Integer(Json("""{"n":1.0}"""), "n") is null, "The existing integer metadata decoder retains its strict syntax");
        var activities = new List<AgentActivity>();
        var parser = new OutputParser("claude", (_, _) => { }, _ => { }, activities.Add, activityNamespace: "consent-run");
        var request = new ToolPermissionRequest("approval", "consent-run", "call", "Bash", "{}", "git status");
        const string call = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"call","name":"Bash","input":{"command":"git status"}}]}}""";
        parser.Parse(call);
        parser.PermissionActivity(request, "waiting");
        var waitingCount = activities.Count;
        parser.ReceiveMod(Json("""{"event":"tool.call","claudeSessionId":"root","toolUseId":"call","tool":"Bash","sequence":1.0}"""));
        parser.Parse(call);
        Check(activities.Count == waitingCount && activities.Last().State == "waiting", "Late Mod and stdout starts cannot erase an outstanding UI approval");

        parser.PermissionActivity(request, "running");
        var allowedCount = activities.Count;
        parser.ReceiveMod(Json("""{"event":"tool.waiting","claudeSessionId":"root","toolUseId":"call","tool":"Bash","sequence":2e0}"""));
        Check(activities.Count == allowedCount && activities.Last().State == "running", "A delayed waiting Mod cannot undo the user's allow response");
        parser.ReceiveMod(Json("""{"event":"tool.complete","claudeSessionId":"root","toolUseId":"call","tool":"Bash","output":"clean","sequence":3.0}"""));
        Check(activities.Last() is { State: "completed", Output: "clean" }, "The actual result still completes an allowed tool");
        var completedCount = activities.Count;
        parser.ReceiveMod(Json("""{"event":"tool.complete","claudeSessionId":"root","toolUseId":"call","tool":"Bash","output":"duplicate","sequence":3e0}"""));
        parser.ReceiveMod(Json("""{"event":"tool.complete","claudeSessionId":"root","toolUseId":"call","tool":"Bash","output":"older","sequence":2.0,"isError":true}"""));
        Check(activities.Count == completedCount && activities.Last() is { State: "completed", Output: "clean" }, "Equivalent decimal/exponent sequences and older telemetry cannot replace a result");

        var denied = request with { Id = "denied-approval", ToolUseId = "denied" };
        parser.PermissionActivity(denied, "waiting");
        parser.PermissionActivity(denied, "error");
        var deniedCount = activities.Count;
        parser.ReceiveMod(Json("""{"event":"tool.waiting","claudeSessionId":"root","toolUseId":"denied","tool":"Bash","sequence":4}"""));
        parser.ReceiveMod(Json("""{"event":"tool.complete","claudeSessionId":"root","toolUseId":"denied","tool":"Bash","sequence":5}"""));
        Check(activities.Count == deniedCount && activities.Last().State == "error", "Late telemetry cannot resurrect or accept a denied call");

        // Consent ownership has the same bounded lifetime as the activity it describes.
        for (var i = 0; i < 512; i++) parser.ReceiveMod(Json(Line(new { @event = "tool.call", claudeSessionId = "root", toolUseId = "later-" + i, tool = "Read" })));
        parser.ReceiveMod(Json("""{"event":"tool.waiting","claudeSessionId":"root","toolUseId":"call","tool":"Bash","sequence":6}"""));
        Check(activities.Last().State == "waiting", "Evicted activities do not retain stale permission ownership");
        parser.FinishActivities(true);
        var finishedCount = activities.Count;
        parser.PermissionActivity(request, "running");
        Check(activities.Count == finishedCount, "A finalized parser ignores late consent callbacks");
        return Task.CompletedTask;
    }

    internal static Task ModsNeverReplaceRootResume()
    {
        var resumes = new List<string>();
        var parser = new OutputParser("claude", (_, _) => { }, resumes.Add);
        parser.ReceiveMod(Json("""{"event":"session.start","claudeSessionId":"mod-before-root"}"""));
        Check(resumes.Count == 0, "Mods do not choose a resumable CLI conversation");
        parser.Parse("""{"type":"system","subtype":"init","session_id":"root-session"}""");
        parser.ReceiveMod(Json("""{"event":"tool.call","claudeSessionId":"child-session","agentId":"child","toolUseId":"read","tool":"Read"}"""));
        parser.ReceiveMod(Json("""{"event":"session.start","claudeSessionId":"late-unscoped-session"}"""));
        parser.Parse("""{"type":"assistant","session_id":"child-session","parent_tool_use_id":"spawn","message":{"content":[]}}""");
        Check(resumes.SequenceEqual(["root-session"]), "Child and unscoped Mods must not overwrite the saved root resume ID");
        parser.Parse("""{"type":"system","subtype":"init","session_id":"next-root-session"}""");
        Check(resumes.SequenceEqual(["root-session", "next-root-session"]), "A new root CLI session can still update resume identity");
        return Task.CompletedTask;
    }

    private sealed class Pipeline
    {
        internal readonly string RunId = "runtime-" + Wire.Id();
        internal readonly ExecutionGraphTracker Graph;
        internal readonly OutputParser Parser;
        internal readonly List<AgentActivity> MainActivities = [];
        internal readonly List<LogEntry> MainImages = [];
        internal readonly List<(string Kind, string Text)> Logs = [];
        internal Pipeline(AgentImageCache? images = null)
        {
            Graph = new(RunId, "Review the screenshots", "claude", null, _ => { });
            Parser = new("claude", (kind, text) => Logs.Add((kind, text)), _ => { }, MainActivities.Add,
                activityNamespace: RunId, images: images, imageEntry: MainImages.Add,
                graphActivity: Graph.Activity, graphImages: Graph.Images);
        }
        internal void Frame(string frame) { Graph.Consume(Json(frame)); Parser.Parse(frame); }
        internal void Mod(string frame)
        {
            var value = Json(frame);
            var raw = value.TryGetProperty("graph", out var graph) ? graph : default;
            var metadata = raw.ValueKind != JsonValueKind.Object ? null : new ModGraphMetadata(1, raw.Text("phase") ?? "running", raw.Text("agentId"), raw.Text("parentAgentId"), raw.Text("parentToolUseId"), raw.Text("name"));
            Graph.ReceiveMod(new(value.Text("claudeSessionId") ?? "root", value.Text("event") ?? "", value.Text("tool"), value.Text("toolUseId"), value.Text("agentId"), metadata));
            Parser.ReceiveMod(value);
        }
        internal MightyGraphAgent Child(string tool) => Graph.BuildRun(includeRunning: true)!.Agents.Single(a => a.Id == ExecutionGraphSupport.AgentNodeID(RunId, tool));
    }

    internal static Task ChildActivitiesStayInTheirGraph()
    {
        var flow = new Pipeline();
        flow.Frame("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"spawn","name":"Agent","input":{"description":"Reviewer","prompt":"Review"}}]}}""");
        flow.Frame("""{"type":"assistant","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_use","id":"child-read","name":"Read","input":{"file_path":"README.md"}}]}}""");
        Check(flow.MainActivities.All(a => a.ToolName != "Read"), "A child tool is absent from main activity rows");
        Check(flow.Child("spawn").Entries.Single(e => e.Activity?.ToolName == "Read").Activity!.State == "running", "The parser routes the raw child tool ID to its graph owner");
        flow.Frame("""{"type":"user","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_result","tool_use_id":"child-read","content":"Reviewed"}]}}""");
        Check(flow.Child("spawn").Entries.Single(e => e.Activity?.ToolName == "Read").Activity is { State: "completed", Output: "Reviewed" }, "A child result replaces the same graph activity");

        // Mods can arrive before the stream names a spawn: pending ownership must survive that order.
        flow.Mod("""{"event":"tool.call","claudeSessionId":"child-mod-session","agentId":"mod-agent","toolUseId":"mod-read","tool":"Read","summary":"other.md","sequence":1}""");
        Check(flow.MainActivities.All(a => a.ToolName != "Read"), "A child whose spawn has not arrived never leaks a tool into main");
        flow.Mod("""{"event":"agent.spawn","claudeSessionId":"root","agentId":"mod-agent","graph":{"version":1,"phase":"starting","agentId":"mod-agent","parentToolUseId":"mod-spawn","name":"Second reviewer"}}""");
        Check(flow.Child("mod-spawn").Entries.Single(e => e.Activity?.ToolName == "Read").Activity!.Summary == "other.md", "A later spawn takes its pending tool activity");
        flow.Mod("""{"event":"tool.complete","claudeSessionId":"child-mod-session","agentId":"mod-agent","toolUseId":"mod-read","tool":"Read","output":"Finished","sequence":2}""");
        Check(flow.Child("mod-spawn").Entries.Single(e => e.Activity?.ToolName == "Read").Activity is { State: "completed", Output: "Finished" }, "Mod completion stays with the child owner");

        flow.Frame("""{"type":"assistant","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_use","id":"child-open","name":"Bash","input":{"command":"build"}}]}}""");
        flow.Parser.FinishActivities(true);
        Check(flow.Child("spawn").Entries.Single(e => e.Activity?.ToolName == "Bash").Activity!.State == "stopped", "Finishing a child activity retains its already known owner without a new raw ID");
        Check(flow.MainActivities.All(a => a.ToolName is not ("Read" or "Bash")), "Neither results nor finalization leak child rows into main");
        return Task.CompletedTask;
    }

    internal static Task ChildImagesReachTheirGraphAndGallery()
    {
        var directory = Verification.Temp();
        try
        {
            var cache = new AgentImageCache(Path.Combine(directory, "images"));
            var flow = new Pipeline(cache);
            var png = AgentImageVerification.Png(12, 8);
            object Picture(byte[] bytes) => new { type = "image", source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(bytes) } };
            flow.Frame("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"spawn","name":"Agent","input":{"description":"Visual reviewer","prompt":"Review images"}}]}}""");
            flow.Frame("""{"type":"assistant","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_use","id":"read-shot","name":"Read","input":{"file_path":"/work/screenshot.png"}}]}}""");
            var result = Line(new { type = "user", parent_tool_use_id = "spawn", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "read-shot", content = new[] { Picture(png) } } } } });
            flow.Frame(result); flow.Frame(result);
            var image = flow.Child("spawn").Entries.Single(e => e.Kind == "image");
            Check(image.Images is { Count: 1 } && image.Images[0].Hash == AgentImageSupport.Sha256(png), "The child's image is retained once by reference in its graph");
            Check(cache.Data(image.Images![0])!.SequenceEqual(png), "The child's image bytes are available to the viewer");
            Check(flow.MainImages.Count == 0, "A child's picture never appears in the main transcript");

            // Direct assistant pictures and results with unknown/evicted tool IDs use the spawning tool as their owner.
            flow.Frame(Line(new { type = "assistant", uuid = "child-message", parent_tool_use_id = "spawn", message = new { content = new[] { Picture(AgentImageVerification.Png(6, 4, 42)) } } }));
            flow.Frame(Line(new { type = "user", parent_tool_use_id = "spawn", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "unseen-tool", content = new[] { Picture(AgentImageVerification.Png(4, 3, 90)) } } } } }));
            Check(flow.Child("spawn").Entries.Count(e => e.Kind == "image") == 3 && flow.MainImages.Count == 0, "Message pictures and orphan tool results stay on the child graph");
            var run = flow.Graph.BuildRun(includeRunning: true)!;
            Check(MightyGraphImages.Galleries([run], directory).Single() == (run.Id, MightyGraphImages.AgentStep(flow.Child("spawn").Id), 3), "The existing gallery discovers all three child pictures");
            Check(!Line(run).Contains(Convert.ToBase64String(png), StringComparison.Ordinal), "Persisted graph entries carry no image base64");

            var oversizedSource = image.Images![0] with { Source = new string('x', 1000), Path = new string('x', WorkspaceFiles.MaximumPathBytes + 1) };
            var boundedPicture = image with { Images = Enumerable.Repeat(oversizedSource, 20).ToList() };
            var invalidPicture = image with { Id = "invalid-picture", Images = [oversizedSource with { Hash = "invalid" }] };
            var ordinary = image with { Id = "ordinary-entry", Kind = "system" };
            var node = new ExecutionGraphNode("normalized-child", flow.RunId, null, "agent", "running", "Child") { Entries = [boundedPicture, invalidPicture, ordinary] };
            var cleanNode = ExecutionGraphSupport.Normalized(node)!;
            Check(cleanNode.Entries[0].Images is { Count: AgentImageSupport.MaximumImagesPerEntry } cleanImages && cleanImages.All(value => value.Source.Length == 240 && value.Path is null), "Graph nodes retain only bounded, normalized image references");
            Check(cleanNode.Entries.Skip(1).All(entry => entry.Images is null), "Invalid references and pictures attached to non-image rows are discarded");
            var history = new MightyGraphRun { Id = "normalized-history", Input = "Review", Agents = [new() { Id = "normalized-child", Entries = [boundedPicture, invalidPicture, ordinary] }] };
            var withImagesBudget = 100_000;
            var cleanHistory = MightyGraphSupport.Normalized([history], false, ref withImagesBudget).Single();
            Check(cleanHistory.Agents[0].Entries[0].Images?.Count == AgentImageSupport.MaximumImagesPerEntry && cleanHistory.Agents[0].Entries.Skip(1).All(entry => entry.Images is null), "Saved graph history applies the same image normalization");
            var noImages = history.Copy();
            noImages.Agents[0].Entries = noImages.Agents[0].Entries.Select(entry => entry with { Images = null }).ToList();
            var plainBudget = 100_000;
            MightyGraphSupport.Normalized([noImages], false, ref plainBudget);
            Check(withImagesBudget < plainBudget, "Saved history charges the reference metadata to its byte budget");
            var longPath = "/" + new string('x', WorkspaceFiles.MaximumPathBytes - 1);
            var expensive = image with { Id = "too-many-reference-bytes", Images = Enumerable.Repeat(oversizedSource with { Path = longPath }, AgentImageSupport.MaximumImagesPerEntry).ToList() };
            // As on the Mac (ExecutionGraph.swift / MightyGraph.swift entries): the newest entry is kept even when its pictures are past
            // the budget, without its activity and with its text clipped, and the spent budget leaves no room for older entries.
            var capped = ExecutionGraphSupport.Normalized(node with { Entries = [ordinary, expensive] })!;
            Check(capped.Entries.Select(entry => entry.Id).SequenceEqual([expensive.Id]) && capped.Entries[0] is { Activity: null, Text.Length: 0, Images.Count: AgentImageSupport.MaximumImagesPerEntry },
                "Image metadata past the node byte budget keeps its entry with clipped text, leaving no space for older entries");
            history.Agents[0].Entries = [ordinary, expensive];
            var cappedBudget = 100_000;
            var cappedHistory = MightyGraphSupport.Normalized([history], false, ref cappedBudget).Single();
            Check(cappedBudget < 100_000 - 65_536 && cappedHistory.Agents[0].Entries.Select(entry => entry.Id).SequenceEqual([expensive.Id]) && cappedHistory.Agents[0].Entries[0].Text.Length == 0,
                "Saved graph pictures are charged after their entry is admitted: they clip its text and spend the budget older entries would use");

            var mainPng = AgentImageVerification.Png(2, 2, 110);
            flow.Frame(Line(new { type = "assistant", uuid = "main-message", message = new { content = new[] { Picture(mainPng) } } }));
            Check(flow.MainImages.Single().Images!.Single().Hash == AgentImageSupport.Sha256(mainPng), "The root's picture still reaches the main transcript");

            // A Mod knows a child ID before its spawn is bound: even a result without parent_tool_use_id cannot leak its picture.
            flow.Mod("""{"event":"tool.call","claudeSessionId":"root","agentId":"unbound-child","toolUseId":"unbound-read","tool":"Read"}""");
            flow.Frame(Line(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "unbound-read", content = new[] { Picture(png) } } } } }));
            Check(flow.MainImages.Count == 1, "An unbound child owner keeps its picture out of main even without a parent envelope");

            var before = flow.Logs.Count;
            flow.Frame(Line(new { type = "assistant", uuid = "bad-child-image", parent_tool_use_id = "spawn", message = new { content = new[] { Picture("not an image"u8.ToArray()) } } }));
            Check(flow.Logs.Count == before, "A child's rejected picture does not add an unrelated main error row");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    // Run through the actual runner wiring and authenticated local Mods endpoint. No installed CLI or account is used.
    internal static async Task RunnerPreservesChildOwnershipAndImagesAcrossRestore()
    {
        var directory = Verification.Temp();
        try
        {
            var store = new StateStore(Path.Combine(directory, "profile"));
            await store.LoadAsync();
            var workspace = store.ApproveLocal(directory);
            var plugin = Path.Combine(directory, "plugin");
            Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
            await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            var cache = new AgentImageCache(Path.Combine(directory, "images"));
            var events = new List<RunEvent>();
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--runtime-parity-cli")));
            await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, value => { lock (events) events.Add(value); }, images: cache);
            const string paneId = "runtime-parity-pane";
            await manager.StartReachedAsync(new(paneId, workspace.Id, "claude", "Review the screenshots", []) { GraphRunId = "runner-parity" });
            await Verification.Until(() => !manager.IsRunning(paneId), 20000);
            RunEvent[] received; lock (events) received = [.. events];
            Check(received.Last(e => e.Type == "status").Status == "completed", "The scripted run must complete: " + string.Join("; ", received.Where(e => e.Entry?.Kind == "error").Select(e => e.Entry!.Text)));
            Check(received.Where(e => e.Type == "resume").Select(e => e.ResumeId).SequenceEqual(["fixture-root"]), "Runner Mods and child frames never replace the root resume ID");
            Check(!received.Any(e => e.Activity?.ToolName == "Read" || e.Entry?.Activity?.ToolName == "Read" || e.Entry?.Kind == "image"), "Child activities and pictures never reach the runner's main log/activity sink");

            var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [new() { Id = paneId, WorkspaceId = workspace.Id }], ActiveWorkspaceId = workspace.Id, ActiveSessionId = paneId };
            snapshot = snapshot.Apply(RunEvent.Log(paneId, "user", "Review the screenshots", "claude"));
            foreach (var value in received) snapshot = snapshot.Apply(value);
            var run = snapshot.Sessions.Single().GraphRuns!.Single();
            var expectedHash = AgentImageSupport.Sha256(AgentImageVerification.Png(7, 5));
            void CheckChildren(MightyGraphRun graph)
            {
                foreach (var spawn in new[] { "stdio-spawn", "mod-spawn" })
                {
                    var child = graph.Agents.Single(a => a.Id == ExecutionGraphSupport.AgentNodeID("runner-parity", spawn));
                    Check(child.Entries.Any(e => e.Activity is { ToolName: "Read", State: "completed", Summary: "screenshot.png" }), spawn + " keeps its parsed tool activity and ignores duplicate/older sequence summaries");
                    Check(child.Entries.Single(e => e.Kind == "image").Images!.Single().Hash == expectedHash, spawn + " keeps its picture reference");
                    if (spawn == "mod-spawn") Check(child.Title == "Mod reviewer" && child.Entries.Any(e => e.Kind == "assistant" && e.Text == "Mod graph completed"), "Decimal spawn and exponent completion graph versions reach the real runner");
                }
                Check(MightyGraphImages.Galleries([graph], directory).Count == 2, "Both children expose their own image gallery");
            }
            CheckChildren(run);
            await store.SaveAsync(snapshot);
            var saved = await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath, "workspace-state.json"));
            Check(!saved.Contains(Convert.ToBase64String(AgentImageVerification.Png(7, 5)), StringComparison.Ordinal), "Saved graph history contains references, never picture bytes");
            var restored = await new StateStore(store.DirectoryPath).LoadAsync();
            CheckChildren(restored.Sessions.Single().GraphRuns!.Single());
            Check(restored.Sessions.Single().ResumeId == "fixture-root", "Saved sessions continue the root CLI conversation after restore");

            var withoutImages = run.Copy();
            foreach (var agent in withoutImages.Agents) agent.Entries = agent.Entries.Select(entry => entry with { Images = null }).ToList();
            Check(MightyGraphSupport.LiveHistoryBytes([run]) > MightyGraphSupport.LiveHistoryBytes([withoutImages]), "Image reference metadata participates in the live history byte budget");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task FakeCliAsync()
    {
        await Console.In.ReadToEndAsync();
        void Emit(object value) { Console.WriteLine(Line(value)); Console.Out.Flush(); }
        var url = Environment.GetEnvironmentVariable("MIGHTY_CLAUDE_BRIDGE_URL") ?? throw new InvalidOperationException("Missing fixture bridge URL");
        var runId = Environment.GetEnvironmentVariable("MIGHTY_CLAUDE_RUN_ID") ?? throw new InvalidOperationException("Missing fixture bridge run ID");
        var token = Environment.GetEnvironmentVariable("MIGHTY_CLAUDE_BRIDGE_TOKEN") ?? throw new InvalidOperationException("Missing fixture bridge token");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        async Task Mod(string kind, string session, string? tool = null, string? toolUseId = null, object? graph = null, string? sequence = null, string? summary = null)
        {
            var body = new Dictionary<string, object> { ["version"] = Json("1.0"), ["runId"] = runId, ["claudeSessionId"] = session, ["event"] = kind, ["agentId"] = "fixture-mod-child" };
            if (tool is not null) body["tool"] = tool;
            if (toolUseId is not null) body["toolUseId"] = toolUseId;
            if (graph is not null) body["graph"] = graph;
            if (sequence is not null) body["sequence"] = Json(sequence);
            if (summary is not null) body["summary"] = summary;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new("Bearer", token);
            if (graph is not null) request.Headers.Add("X-Mighty-Graph", "1");
            request.Content = new StringContent(Line(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.NoContent) throw new InvalidOperationException("Fixture Mods delivery failed: " + (int)response.StatusCode);
        }
        object Image() => new { type = "image", source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(AgentImageVerification.Png(7, 5)) } };
        Emit(new { type = "system", subtype = "init", session_id = "fixture-root" });
        Emit(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", id = "stdio-spawn", name = "Agent", input = new { description = "Stream reviewer", prompt = "Review" } } } } });
        Emit(new { type = "assistant", session_id = "fixture-stdio-child", parent_tool_use_id = "stdio-spawn", message = new { content = new object[] { new { type = "tool_use", id = "stdio-read", name = "Read", input = new { file_path = "screenshot.png" } } } } });
        Emit(new { type = "user", session_id = "fixture-stdio-child", parent_tool_use_id = "stdio-spawn", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "stdio-read", content = new[] { Image() } } } } });
        Emit(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "stdio-spawn", content = "Reviewed" } } } });

        // The only source of this child's starting tool row is Mods, so reversing the runner's ownership/parser order fails.
        await Mod("agent.spawn", "fixture-mod-session", graph: new { version = Json("1.0"), phase = "starting", agentId = "fixture-mod-child", parentToolUseId = "mod-spawn", name = "Mod reviewer" });
        await Mod("tool.call", "fixture-mod-session", "Read", "mod-read", sequence: "2.0", summary: "screenshot.png");
        await Mod("tool.call", "fixture-mod-session", "Read", "mod-read", sequence: "2e0", summary: "duplicate summary");
        await Mod("tool.call", "fixture-mod-session", "Read", "mod-read", sequence: "1.0", summary: "older summary");
        Emit(new { type = "user", parent_tool_use_id = "mod-spawn", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "mod-read", content = new[] { Image() } } } } });
        Emit(new { type = "result", subtype = "success", is_error = false, result = "Reviewed", session_id = "fixture-root" });
        await Mod("tool.complete", "fixture-mod-session", "Read", "mod-read", sequence: "3e0");
        await Mod("agent.complete", "fixture-mod-session", graph: new { version = Json("1e0"), phase = "completed", agentId = "fixture-mod-child", parentToolUseId = "mod-spawn", output = "Mod graph completed" });
    }
}
