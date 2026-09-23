using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class ActivityUsageVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static async Task Until(Func<bool> predicate) { var end = DateTimeOffset.UtcNow.AddSeconds(5); while (!predicate()) { if (DateTimeOffset.UtcNow > end) throw new TimeoutException("Structured event was not delivered"); await Task.Delay(20); } }
    internal static Task Activities()
    {
        var activity = new List<AgentActivity>(); var logs = new List<(string Kind, string Text)>(); double now = 0;
        var parser = new OutputParser("claude", (kind, text) => logs.Add((kind, text)), _ => { }, activity.Add, activityNamespace: "run-one", activityClock: () => now);
        parser.Parse("""{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"tool-1","name":"Bash","input":{"command":"dotnet test","secret":"NEVER_DISPLAY"}}]}}""");
        now = 100;
        parser.ReceiveMod(Json("""{"event":"tool.waiting","claudeSessionId":"session","toolUseId":"tool-1","tool":"Bash","summary":"dotnet test","sequence":2}"""));
        now = 1250;
        parser.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool-1","content":[{"type":"text","text":"passed"}]}]}}""");
        var complete = activity.Last(); Check(complete.State == "completed" && complete.Kind == "command" && complete.Summary == "dotnet test" && complete.Output == "passed" && complete.DurationMs == 1250, "Tool identity/duration/output must come from actual lifecycle");
        parser.ReceiveMod(Json("""{"event":"tool.call","claudeSessionId":"session","toolUseId":"tool-1","tool":"Bash","sequence":3}"""));
        Check(activity.Last() == complete && activity.Select(a => a.Id).Distinct().Count() == 1, "Late Mods must not resurrect or duplicate a tool");
        parser.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"unsettled","name":"Read","input":{"file_path":"README.md"}}]}}""");
        parser.FinishActivities(true); Check(activity.Last().State == "stopped", "Cancelled unfinished tool must be stopped");
        var count = activity.Count; parser.ReceiveMod(Json("""{"event":"tool.complete","claudeSessionId":"session","toolUseId":"unsettled","tool":"Read","sequence":4}""")); Check(activity.Count == count, "Finalized parser must reject late Mods");
        var next = new List<AgentActivity>(); new OutputParser("claude", (_, _) => { }, _ => { }, next.Add, activityNamespace: "run-two").Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tool-1","name":"Bash","input":{}}]}}"""); Check(next.Single().Id != complete.Id, "Activity identity must be run scoped");
        Check(ActivitySupport.DurationLabel(complete) == "1.2초", "Duration display must not round up");
        return Task.CompletedTask;
    }
    internal static Task ProviderUsage()
    {
        var activity = new List<AgentActivity>(); var usage = new List<SessionUsage>();
        var codex = new OutputParser("codex", (_, _) => { }, _ => { }, activity.Add, usage.Add);
        codex.Parse("""{"type":"thread.started","thread_id":"thread"}""");
        codex.Parse("""{"type":"item.started","item":{"id":"cmd","type":"command_execution","command":"git status"}}""");
        codex.Parse("""{"type":"item.completed","item":{"id":"cmd","type":"command_execution","command":"git status","aggregated_output":"clean","status":"completed"}}""");
        const string totals = """{"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":100,"output_tokens":80,"reasoning_output_tokens":40}}""";
        codex.Parse(totals); var emitted = usage.Count; codex.Parse(totals);
        Check(usage.Count == emitted && usage.Last().InputTokens == 1200 && usage.Last().TokenScope == "session" && usage.Last().ContextPercent is null && usage.Last().Model is null, "Codex cumulative snapshots must not sum or invent context/model");
        Check(activity.Any(a => a.Kind == "command" && a.Output == "clean"), "Codex command content missing");
        var text = new List<string>(); usage.Clear(); activity.Clear();
        var gemini = new OutputParser("gemini", (kind, message) => { if (kind == "assistant") text.Add(message); }, _ => { }, activity.Add, usage.Add);
        gemini.Parse("""{"type":"init","session_id":"gemini-session","model":"gemini-3-flash-preview"}""");
        var markdown = "```swift\n" + new string('x', 20000) + "\n```\n\n|a|b|\n|-|-|\n|1|2|";
        foreach (var chunk in markdown.Chunk(3000)) gemini.Parse(JsonSerializer.Serialize(new { type = "message", role = "assistant", delta = true, content = new string(chunk) }));
        gemini.Parse("""{"type":"tool_use","tool_id":"g-tool","tool_name":"read_file","parameters":{"file_path":"file.swift"}}""");
        gemini.Parse("""{"type":"tool_result","tool_id":"g-tool","status":"success","output":"content"}""");
        gemini.Parse("""{"type":"result","stats":{"input_tokens":1400,"output_tokens":90,"total_tokens":1600,"cached":200,"models":{"gemini-3-flash-preview":{}}}}""");
        Check(text.Single() == markdown, "Long Markdown must remain one logical message across delta reads");
        Check(usage.Last().TotalTokens == 1600 && usage.Last().ContextUsedTokens is null && usage.Last().ContextWindowTokens is null && usage.Last().TokenScope == "session", "Gemini totals are not context usage");
        Check(activity.Any(a => a.ToolName == "read_file" && a.Output == "content" && a.State == "completed"), "Gemini tool lifecycle missing");
        gemini.Parse("""{"type":"error","severity":"error","message":"failed"}"""); gemini.Parse("""{"type":"error","severity":"warning","message":"warning"}"""); Check(gemini.Failed, "Warnings must not clear a fatal error");
        return Task.CompletedTask;
    }
    internal static Task ClaudeContext()
    {
        var values = new List<SessionUsage>(); var parser = new OutputParser("claude", (_, _) => { }, _ => { }, usage: values.Add);
        parser.Parse("""{"type":"system","subtype":"init","session_id":"claude-session","model":"claude-sonnet-test"}""");
        parser.Parse("""{"type":"assistant","session_id":"claude-session","message":{"model":"claude-sonnet-test","usage":{"input_tokens":100,"cache_read_input_tokens":200,"cache_creation_input_tokens":50,"output_tokens":10},"content":[]}}""");
        Check(values.Last().InputTokens == 350 && values.Last().ContextUsedTokens == 350 && values.Last().ContextPercent is null, "Claude input must include caches without inventing capacity");
        parser.Parse("""{"type":"result","session_id":"claude-session","total_cost_usd":0.2,"modelUsage":{"claude-sonnet-test":{"inputTokens":2000,"cacheReadInputTokens":300,"cacheCreationInputTokens":100,"outputTokens":50,"contextWindow":200000}}}""");
        Check(values.Last().InputTokens == 2400 && values.Last().ContextUsedTokens == 350 && values.Last().ContextWindowTokens == 200000 && values.Last().TokenScope == "run", "Run totals must not overwrite current context");
        var stamp = "2026-09-16T00:00:00Z";
        var observed = new SessionUsage { ProviderSessionId = "claude-session", Source = "claude.mods", ContextUsedTokens = 1000, ContextWindowTokens = 200000, CostUSD = 0.5, CostScope = "session", RateLimits = [new("five_hour", 42)], UpdatedAt = stamp };
        JsonElement Envelope(long sequence, SessionUsage usage) => Json(JsonSerializer.Serialize(new { version = 1, claudeSessionId = "claude-session", @event = "session.usage", sequence, usage }, Wire.Json));
        parser.ReceiveMod(Envelope(3, observed)); var count = values.Count;
        parser.ReceiveMod(Envelope(2, observed with { ContextUsedTokens = 1900 })); Check(values.Count == count, "Stale Mods usage sequence must be ignored");
        Check(values.Last().RateLimitsUpdatedAt == stamp && values.Last().ContextPercent == 0.5 && values.Last().CostScope == "session", "Direct Mods context/quota source missing");
        parser.Parse("""{"type":"assistant","session_id":"claude-session","message":{"model":"claude-sonnet-test","usage":{"input_tokens":500,"output_tokens":10},"content":[]}}""");
        Check(values.Last().RateLimitsUpdatedAt == stamp && values.Last().InputTokens == 2400 && values.Last().ContextUsedTokens == 500, "stdout must not refresh quota age or replace final run totals");
        parser.Parse("""{"type":"system","subtype":"compact_boundary","session_id":"claude-session"}"""); Check(values.Last().ContextUsedTokens is null, "Compaction must clear unknown current context");
        count = values.Count; parser.Parse("""{"type":"assistant","session_id":"child-session","parent_tool_use_id":"agent-tool","message":{"model":"different","usage":{"input_tokens":123,"output_tokens":4},"content":[]}}"""); Check(values.Count == count, "Subagent usage must not overwrite main context");
        return Task.CompletedTask;
    }
    // Recorded from `claude --resume` after a process that left `sleep 600` running in the background.
    internal static Task LeftoverTaskResult()
    {
        var activity = new List<AgentActivity>(); var logs = new List<(string Kind, string Text)>(); var usage = new List<SessionUsage>();
        var parser = new OutputParser("claude", (kind, text) => logs.Add((kind, text)), _ => { }, activity.Add, usage.Add);
        parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"b9f44slha","status":"stopped","summary":"Background shell command didn't finish before the previous session ended"}""");
        var leftover = """{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","total_cost_usd":0,"origin":{"kind":"task-notification"}}""";
        Check(ClaudeStream.IsNotificationResult(Json(leftover)), "The recorded leftover event must be recognized as a notification result");
        parser.Parse(leftover);
        Check(activity.Count == 0 && usage.Count == 0, "A leftover task-notification result must not end a turn or publish usage before the real request even starts");
        parser.Parse("""{"type":"assistant","session_id":"claude-session","message":{"model":"claude-sonnet-test","usage":{"input_tokens":100,"output_tokens":20},"content":[{"type":"text","text":"pong"}]}}""");
        Check(logs.SequenceEqual([("assistant", "pong")]), "Only the real turn's text may reach the transcript");
        Check(usage.Last().InputTokens == 100 && usage.Last().OutputTokens == 20 && usage.Last().TokenScope == "response", "The leftover result must not have latched resultSeen before the real turn's own usage arrived");
        parser.Parse("""{"type":"result","session_id":"claude-session","subtype":"success","is_error":false,"num_turns":1,"result":"pong","total_cost_usd":0.01}""");
        Check(activity.Count(a => a.Kind == "turn") == 1, "Exactly one turn may finish: the leftover notification's result does not count as one");
        Check(usage.Last().InputTokens == 100 && usage.Last().CostUSD == 0.01 && usage.Last().TokenScope == "run", "The real result must still finalize usage");
        Check(!ClaudeStream.IsNotificationResult(Json("""{"type":"result","origin":{"kind":"user"}}""")) && !ClaudeStream.IsNotificationResult(Json("""{"type":"assistant","origin":{"kind":"task-notification"}}""")), "Only a result whose origin is a task notification qualifies");
        return Task.CompletedTask;
    }
    internal static async Task Persistence()
    {
        var malformed = JsonSerializer.Deserialize<RunSession>("""{"id":"pane","workspaceId":"work","runTiming":{"startedAt":"bad"},"sessionUsage":{"provider":"claude","source":"claude.mods","tokenScope":"run","updatedAt":"2026-09-16T00:00:00Z","inputTokens":"broken","outputTokens":5},"logs":[{"id":"line","kind":"assistant","text":"kept","timestamp":"2026-09-16T00:00:00Z","activity":{"id":12}}]}""", Wire.Json)!;
        Check(malformed.RunTiming is null && malformed.SessionUsage?.InputTokens is null && malformed.SessionUsage?.OutputTokens == 5 && malformed.Logs.Single().Activity is null && malformed.Logs.Single().Text == "kept", "Damaged optional metadata must not discard saved text");
        var directory = Path.Combine(Path.GetTempPath(), "mighty-activity-state-" + Wire.Id()); Directory.CreateDirectory(directory);
        try
        {
            var store = new StateStore(directory); await store.LoadAsync(); var a = store.ApproveLocal(directory); var secondPath = Path.Combine(directory, "other"); Directory.CreateDirectory(secondPath); var b = store.ApproveLocal(secondPath);
            var time = DateTimeOffset.UtcNow.AddSeconds(-10); var tool = new AgentActivity("tool", "claude", "read", "running", "README.md", "Read");
            var pane = new RunSession { Id = "pane", WorkspaceId = a.Id, Status = "running", Draft = "next draft", RunTiming = new(time, time.AddSeconds(2)), SessionUsage = new() { InputTokens = 100, ContextUsedTokens = 100, ContextWindowTokens = 200000 }, Logs = [new("tool", "system", "README.md", Wire.Now(), "claude", tool)] };
            var state = new AppSnapshot { Workspaces = [a,b], Sessions = [pane, new() { Id = "other", WorkspaceId = b.Id }], ActiveWorkspaceId = a.Id, ActiveSessionId = pane.Id, Layout = "focus" };
            state = state.Apply(new(pane.Id, "log", pane.Logs.Single() with { Activity = tool with { State = "completed", DurationMs = 123 } }));
            Check(state.Sessions[0].Logs.Count == 1 && state.Sessions[0].Status == "running" && state.Sessions[0].RunTiming?.FinishedAt is null, "Tool completion must update one row without completing run clock");
            await store.SaveAsync(state); var restored = await new StateStore(directory).LoadAsync(); var saved = restored.Sessions[0];
            Check(saved.Status == "stopped" && saved.RunTiming?.FinishedAt == saved.RunTiming?.LastObservedAt && saved.RunTiming?.IsApproximate == true && saved.SessionUsage?.ContextPercent == 0.05 && saved.Draft == "next draft", "Restart must freeze the last checkpoint and retain usage/draft");
            Check(restored.PaneLayoutModes?[a.Id] == "focus" && restored.PaneLayoutModes?[b.Id] == "grid" && restored.PaneLayoutActiveSessionIds?[b.Id] == "other", "Workspace focus/selection migration must be independent");
            var finished = state.Apply(RunEvent.State(pane.Id, "completed")); var end = finished.Sessions[0].RunTiming!; Check(end.FinishedAt is not null && end.Elapsed(DateTimeOffset.UtcNow.AddHours(1)) == end.Elapsed(), "Completed run elapsed must freeze");
            var logs = Enumerable.Range(0,300).Select(i => new LogEntry("line-"+i,"system","tool", Wire.Now(), "claude", tool with { Id = "a-"+i, Output = new string('한', 10000) })).ToList();
            var bounded = StateStore.Normalize(state with { Sessions = [pane with { Logs = logs }] }, false); Check(JsonSerializer.SerializeToUtf8Bytes(bounded, Wire.Json).Length < 8 * 1024 * 1024, "Metadata output must share persistence byte budget");
        }
        finally { Directory.Delete(directory,true); }
    }
    private sealed class FakeManager(Action<RunEvent> emit) : IRunManager
    {
        internal string? Job;
        public Task StartAsync(StartRunRequest request)
        {
            Job=request.SessionId; var activity = new AgentActivity("wire-tool", "claude", "command", "completed", "echo fixture", "Bash", "fixture output", 125);
            emit(RunEvent.State(Job,"running")); emit(new(Job,"log",new(activity.Id,"system",activity.Summary,Wire.Now(),"claude",activity))); emit(new(Job,"activity",Activity:activity));
            emit(new(Job,"usage",Usage:new() { Source="claude.mods", ContextUsedTokens=2000, ContextWindowTokens=200000, InputTokens=3000 })); emit(RunEvent.State(Job,"completed")); return Task.CompletedTask;
        }
        public Task StopAsync(string id) { emit(RunEvent.State(id,"stopped")); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static async Task Remote()
    {
        Console.WriteLine("TRACE structured remote: runtime");
        var directory = Path.Combine(Path.GetTempPath(),"mighty-activity-remote-"+Wire.Id()); Directory.CreateDirectory(directory);
        var workspace = new Workspace { Path=directory }; await using var catalog = new ProviderCatalog((_,_)=>Task.FromResult<CliCommand?>(null)); FakeManager? manager=null;
        var runtime = await catalog.GetRuntimeAsync(); runtime=runtime with { Providers=runtime.Providers.Select(p=>p with { Available=true }).ToList() };
        await using var host = new RemoteServer("metadata",[workspace.Id],()=>[workspace],_=>Task.FromResult(workspace),()=>Task.FromResult(runtime),emit=>manager=new(emit),true);
        var delivered=new ConcurrentQueue<RunEvent>();
        await using var client = new RemoteController(directory,()=>[],_=>throw new ArgumentException(),()=>Task.FromResult(runtime),emit=>new FakeManager(emit),delivered.Enqueue,testLoopback:true,discover:_=>Task.FromResult(new TailscaleInfo(false,[],null,"fixture")));
        try
        {
            Console.WriteLine("TRACE structured remote: host start");
            await host.StartAsync(IPAddress.Loopback,0).WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("TRACE structured remote: connect");
            var state=await client.ConnectAsync(new("fixture",host.Address,host.Token)).WaitAsync(TimeSpan.FromSeconds(20)); var connection=state.Connections.Single();
            var imported=workspace with { Id="imported",Remote=new(connection.Id,workspace.Id,"fixture") };
            Console.WriteLine("TRACE structured remote: start and events");
            await client.StartRunAsync(new("local-pane",imported.Id,"claude","fixture without model",[]),imported); await Until(()=>delivered.Any(e=>e.Status=="completed"));
            Check(delivered.All(e=>e.SessionId=="local-pane") && delivered.Any(e=>e.Activity?.DurationMs==125) && delivered.Any(e=>e.Usage?.ContextPercent==1) && delivered.Any(e=>e.Entry?.Activity?.Output=="fixture output"),"New remote client must opt in and preserve structured Mac-compatible DTOs");
            Console.WriteLine("TRACE structured remote: legacy poll");
            using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(10) }; using var request=new HttpRequestMessage(HttpMethod.Get,host.Address+$"/v1/runs/{manager!.Job}/events?cursor=0"); request.Headers.Add("Authorization","Bearer "+host.Token); request.Headers.Add(RemoteNetwork.VersionHeader,"1");
            using var response=await http.SendAsync(request); var poll=JsonSerializer.Deserialize<WirePoll>(await response.Content.ReadAsStringAsync(),Wire.Json)!;
            Check(poll.Events.Count==5 && poll.Cursor==5 && poll.Events.All(e=>e.Event.Type is "log" or "status") && poll.Events.All(e=>e.Event.Entry?.Activity is null) && poll.Events.Last().Event.Status=="completed", "Legacy poll must retain every cursor and terminal status without new metadata types");
        }
        finally { Console.WriteLine("TRACE structured remote: cleanup"); await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); Directory.Delete(directory,true); }
    }
    internal static async Task Mods()
    {
        var seen=new List<JsonElement>(); await using var bridge=new ModBridge(); using var connection=await bridge.RegisterAsync(v=>seen.Add(v.Clone()),CancellationToken.None); using var http=new HttpClient();
        async Task<HttpStatusCode> Send(object value, bool browser=false)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,connection.Url); request.Headers.Add("Authorization","Bearer "+connection.Token); if(browser)request.Headers.Add("Origin","http://localhost"); request.Content=new StringContent(JsonSerializer.Serialize(value,Wire.Json),Encoding.UTF8,"application/json"); using var response=await http.SendAsync(request); return response.StatusCode;
        }
        var usage=new SessionUsage { Source="claude.mods", ProviderSessionId="session",ContextUsedTokens=100,ContextWindowTokens=200000,RateLimits=[new("five_hour",25)] };
        Check(await Send(new { version=1,runId=connection.Id,claudeSessionId="session",@event="session.usage",sequence=1,usage })==HttpStatusCode.NoContent,"Authenticated Mods usage must be accepted");
        Check(await Send(new { version=1,runId=connection.Id,claudeSessionId="session",@event="tool.complete",tool="Read",toolUseId="tool",summary="README.md",output=new string('x',5000),isError=false,sequence=2 })==HttpStatusCode.NoContent,"Bounded structured output over old 4KiB limit must be accepted");
        Check(await Send(new { version=1,runId=connection.Id,claudeSessionId="other",@event="session.usage",usage })==HttpStatusCode.BadRequest,"Usage identity must match authenticated envelope");
        Check(await Send(new { version=1,runId=connection.Id,claudeSessionId="session",@event="tool.complete",tool="Read",sequence=3 })==HttpStatusCode.BadRequest,"Tool completion without identity must be rejected");
        Check(await Send(new { version=1,runId=connection.Id,claudeSessionId="session",@event="session.usage",usage },true)==HttpStatusCode.Forbidden,"Browser-origin usage must be rejected");
        Check(seen.Count==2,"Rejected metadata must never enter parser");
    }
}
