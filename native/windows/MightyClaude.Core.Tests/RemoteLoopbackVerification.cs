using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>Host and client of remote workspaces in one process over loopback (injected in code, as only checks may).</summary>
internal static class RemoteLoopbackVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Key => RemoteClientVerification.Key;

    private static async Task<(RemoteHostVerification.HostFixture Host, RemoteClientService Client, Workspace Workspace, RemoteClientVerification.Collected Seen, string Folder, RemoteHostState State)> Pair(
        TimeSpan? heartbeat = null, Func<StartRunRequest, string?>? admission = null, Func<RemoteHostVerification.HostFixture, RemoteRunnerSinks, IRemoteRunner>? runner = null)
    {
        var fixture = await RemoteHostVerification.MakeHost(heartbeat, admission, runner);
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var (store, folder) = await RemoteClientVerification.ClientStore(); var seen = new RemoteClientVerification.Collected();
        var client = new RemoteClientService(store, folder, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { AllowLoopbackForTests = true });
        var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
        return (fixture, client, client.OpenWorkspace(info.Id, fixture.Shared.Id), seen, folder, state);
    }

    /// <summary>A Mac client's start (no graph header), straight to the host; the host's job id.</summary>
    private static async Task<string> StartRaw(RemoteTarget target, string key, string workspaceId, string kind, string input)
    {
        var start = new RemoteWireStart(new RemoteWireRequest("mac-pane", workspaceId, kind, input, "default", "claude", new()), []);
        return JsonSerializer.Deserialize<RemoteWireJob>(await RemoteTransport.RequestAsync(target, key, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(start, RemoteWire.Json)), RemoteWire.Json)!.JobId;
    }

    private static async Task<RemoteWirePoll> PollRaw(RemoteTarget target, string key, string job, int cursor) =>
        JsonSerializer.Deserialize<RemoteWirePoll>(await RemoteTransport.RequestAsync(target, key, HttpMethod.Get, $"/v2/runs/{job}/events?cursor={cursor}"), RemoteWire.Json)!;

    internal static async Task CommandsAndFullAccessWaitForTheSwitchThenRunOnTheHost()
    {
        var (fixture, client, workspace, seen, folder, _) = await Pair();
        try
        {
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace), "commands wait for the switch", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.commandsOff"));
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", [], Settings: new(PermissionMode: "fullAccess")), workspace), "full access waits for the switch", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.fullAccessOff"));
            Check(fixture.Runner is null || fixture.Runner.Started.IsEmpty, "nothing ran");
            fixture.Host.SetAllowsFullAccess(true);
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-1" && e.Status == "completed"));
            Check(seen.Events.Where(e => e.SessionId == "pane-1").Select(e => e.Type + ":" + (e.Status ?? e.Entry?.Text)).SequenceEqual(["status:running", "log:echo:dir", "status:completed"]), "the host's events became the pane's in order");
            var ran = fixture.Runner!.Started.Single();
            Check(ran.WorkspaceId == fixture.Shared.Id && ran.SessionId != "pane-1", "the host ran it in its shared workspace under its own job id");
            Check(fixture.Host.Current().ActiveRuns == 0, "a finished run is not active");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task ToolRequestsAreAnsweredFromTheClientAndGraphRunsGoOnlyWhereAsked()
    {
        var (fixture, client, workspace, seen, folder, state) = await Pair();
        try
        {
            var target = RemoteClientVerification.Loopback(state.Port!.Value);
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "claude", "ask", []), workspace);
            await Verification.Until(() => seen.Permissions.Any(p => p.State == "pending"));
            Check(seen.Permissions[0] is { RunId: "pane-2", Id: "perm-1", InputJson: "{\"command\":\"ls\"}" }, "the host's tool request is the pane's");
            var asked = Encoding.UTF8.GetString(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, $"/v2/runs/{fixture.Runner!.Started.Single().SessionId}/events?cursor=0"));
            Check(asked.Contains("\"state\":\"pending\"", StringComparison.Ordinal) && asked.Contains("\"canAllow\":true", StringComparison.Ordinal), "a tool request carries its state and whether it can be allowed: " + asked);
            await client.RespondToPermissionAsync("pane-2", "perm-1", true);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-2" && e.Status == "completed"));
            var answer = fixture.Runner!.Answers.Single();
            Check(answer.Request == "perm-1" && answer.Allow && answer.Session == fixture.Runner.Started.Single().SessionId, "the answer reached the host's run");
            Check(seen.Permissions.Any(p => p.Id == "perm-1" && p.State == "allowed"), "the settled request reached the pane");

            await client.StartAsync(new StartRunRequest("pane-3", workspace.Id, "claude", "graph", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-3" && e.Status == "completed"));
            Check(seen.Events.Any(e => e.SessionId == "pane-3" && e.Type == "graph_run" && e.GraphRun?.Id == "gr-1"), "a Windows client that asked gets graph runs");
            // A client that did not ask (the Mac's) gets graph nodes and no graph runs.
            var job = await StartRaw(target, state.Key!, fixture.Shared.Id, "claude", "graph");
            string poll = "";
            await Verification.Until(() => (poll = Encoding.UTF8.GetString(RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, $"/v2/runs/{job}/events?cursor=0").GetAwaiter().GetResult())).Contains("\"done\":true", StringComparison.Ordinal));
            Check(poll.Contains("\"type\":\"graph\"", StringComparison.Ordinal) && !poll.Contains("graph_run", StringComparison.Ordinal), "a Mac client gets the Mac's graph events: " + poll);
            // The Mac writes whole seconds (ISO8601DateFormatter) and reads them or milliseconds (AgentRunTiming.parseTimestamp).
            var stamps = System.Text.RegularExpressions.Regex.Matches(poll, "\"(updatedAt|timestamp)\":\"([^\"]*)\"").Select(m => m.Groups[2].Value).ToList();
            Check(stamps.Count >= 2 && stamps.All(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d{3})?Z\\z")), "every timestamp is one the Mac reads: " + poll);
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task UnsharedFoldersBusyHostsAndOversizeRequestsAreRefused()
    {
        var holding = false;
        var (fixture, client, workspace, seen, folder, state) = await Pair(admission: _ => holding ? Locale.Get("run.blocked.changingPlugins") : null);
        try
        {
            var target = RemoteClientVerification.Loopback(state.Port!.Value);
            var unshared = new RemoteWireStart(new RemoteWireRequest("x", fixture.Private.Id, "claude", "hi", "default", "claude", new()), []);
            await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(unshared, RemoteWire.Json)), "an unshared workspace is refused", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.notShared"));
            var remote = unshared with { Request = unshared.Request with { WorkspaceId = fixture.Remote.Id } };
            await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(remote, RemoteWire.Json)), "a remote workspace is never a folder here", ex => ex is RemoteHttpFailure { Status: 403 });
            holding = true;
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-4", workspace.Id, "claude", "hi", []), workspace), "a host busy with its own update holds the run", ex => ex is RemoteHttpFailure { Status: 503, Transient: true });
            holding = false;
            var huge = await RemoteHostVerification.Raw(state.Port.Value, $"POST /v2/runs HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\nContent-Type: application/json\r\nContent-Length: {13 * 1024 * 1024}\r\n\r\n");
            Check(huge.StartsWith("HTTP/1.1 413", StringComparison.Ordinal), "over 12 MiB is refused before reading");
            var bad = await RemoteHostVerification.Raw(state.Port.Value, $"GET /v2/runs/nope/events?cursor=0 HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\n\r\n");
            Check(bad.StartsWith("HTTP/1.1 404", StringComparison.Ordinal), "an unknown run is gone");
            var empty = await RemoteHostVerification.Raw(state.Port.Value, $"POST /v2/runs HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\nContent-Type: application/json\r\nContent-Length: 0\r\n\r\n");
            Check(empty.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a start with no body is refused: " + empty);
            // Over 512 KiB is only for a run that brings attachments.
            var padded = Encoding.UTF8.GetBytes("{\"request\":{\"sessionId\":\"x\",\"workspaceId\":\"" + fixture.Shared.Id + "\",\"kind\":\"claude\",\"input\":\"hi\",\"model\":\"default\"},\"registeredModels\":[]" + new string(' ', RemoteProtocol.MaximumRequestBytes) + "}");
            await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", padded), "a start over 512 KiB without attachments is refused", ex => ex is RemoteHttpFailure { Status: 413 });
            var crowded = unshared with { Request = unshared.Request with { WorkspaceId = fixture.Shared.Id }, RegisteredModels = Enumerable.Range(0, 257).Select(i => new RemoteWireModel("model-" + i)).ToList() };
            await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(crowded, RemoteWire.Json)), "more than 256 registered models are refused", ex => ex is RemoteHttpFailure { Status: 400 } && ex.Message == Locale.Get("wire.startRun.invalidFormat"));
            fixture.Host.SetAllowsFullAccess(true);
            var longCommand = "Write-Output '" + new string('x', 40_000) + "'";
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-8", workspace.Id, "shell", longCommand, []), workspace), "a command longer than a Windows command line is refused", ex => ex is RemoteHttpFailure { Status: 400 } && ex.Message == RemoteWindowsStrings.HostCommandTooLong);
            Check(fixture.Runner is null || fixture.Runner.Started.IsEmpty, "nothing refused ever ran");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task ARunNobodyPollsIsStoppedAndAStoppedHostEndsTheClientsRuns()
    {
        await using (var unpolled = await RemoteHostVerification.MakeHost(heartbeat: TimeSpan.FromSeconds(1)))
        {
            var sharing = await unpolled.Host.StartAsync([unpolled.Shared.Id], 0);
            var target = RemoteClientVerification.Loopback(sharing.Port!.Value);
            var job = await StartRaw(target, sharing.Key!, unpolled.Shared.Id, "claude", "hold");
            await Verification.Until(() => unpolled.Runner!.Stopped.Contains(job), 6000);
            var poll = await PollRaw(target, sharing.Key!, job, 0);
            Check(poll.Done && poll.Events[^1].Event["status"]?.GetValue<string>() == "stopped", "an unpolled run was stopped after the heartbeat");
        }
        // The client polls at most every 850 ms while nothing comes: a 3 s heartbeat leaves it room on a slow machine.
        var (fixture, client, workspace, seen, folder, _) = await Pair(heartbeat: TimeSpan.FromSeconds(3));
        try
        {
            fixture.Host.SetAllowsFullAccess(true);
            await client.StartAsync(new StartRunRequest("pane-5", workspace.Id, "shell", "hold", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-5" && e.Type == "log"));
            // Two heartbeats: a run whose polls did not count would be stopped by 4 s and its stop polled well before 6 s.
            await Task.Delay(6000);
            Check(!seen.Events.Any(e => e.SessionId == "pane-5" && e.Status == "stopped"), "a polled run outlives the heartbeat");
            await fixture.Host.StopAsync();
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-5" && e.Status == "error"), 20000);
            Check(client.List().Single().Status == RemoteConnectionStatus.Disconnected, "a host that stopped sharing ends the connection");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task ARealCommandRunsInTheSharedFolderAndStops()
    {
        var plugin = Verification.Temp();
        var (fixture, client, workspace, seen, folder, _) = await Pair(runner: (host, sinks) =>
            new RunManager(host.Store.ResolveLocalAsync, new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)), plugin, sinks.Event, sinks.Permission) { PowerShellCommands = true, GraphNodes = sinks.GraphNode, DelegationEnabled = () => false });
        try
        {
            fixture.Host.SetAllowsFullAccess(true);
            await client.StartAsync(new StartRunRequest("pane-6", workspace.Id, "shell", OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-6" && e.Status is "completed" or "error"), 20000);
            Check(seen.Events.Any(e => e.SessionId == "pane-6" && e.Entry?.Text.Trim() == fixture.Shared.Path), "the command ran in the shared folder: " + string.Join(" | ", seen.Events.Where(e => e.Entry is not null).Select(e => e.Entry!.Text)));
            await client.StartAsync(new StartRunRequest("pane-7", workspace.Id, "shell", OperatingSystem.IsWindows() ? "Start-Sleep 30" : "sleep 30", []), workspace);
            await Task.Delay(500);
            await client.StopAsync("pane-7");
            Check(seen.Events.Last(e => e.SessionId == "pane-7").Status == "stopped", "a stop ends the pane at once");
            await Verification.Until(() => fixture.Host.Current().ActiveRuns == 0, 15000);
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); Directory.Delete(plugin, true); }
    }

    /// <summary>A runner whose disposal waits until the check lets it go.</summary>
    private sealed class SlowDisposal(IRemoteRunner inner, TaskCompletionSource entered, Task release) : IRemoteRunner
    {
        public Task StartAsync(StartRunRequest request) => inner.StartAsync(request);
        public Task StopAsync(string sessionId) => inner.StopAsync(sessionId);
        public Task<bool> TrySteerAsync(string sessionId, string text) => inner.TrySteerAsync(sessionId, text);
        public void RespondToToolPermission(string sessionId, string requestId, bool allow) => inner.RespondToToolPermission(sessionId, requestId, allow);
        public void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) => inner.AnswerQuestionnaire(sessionId, requestId, answers);
        public PlanRecord AnswerPlan(string sessionId, string requestId, PlanDecision decision) => inner.AnswerPlan(sessionId, requestId, decision);
        public async ValueTask DisposeAsync() { entered.TrySetResult(); await release; await inner.DisposeAsync(); }
    }

    internal static async Task AnOldStopsRunCountNeverLandsAfterANewerOne()
    {
        var told = new ConcurrentQueue<int>(); var made = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await RemoteHostVerification.MakeHost(runsChanged: told.Enqueue, runner: (f, sinks) =>
            Interlocked.Increment(ref made) == 1 ? new SlowDisposal(new RemoteHostVerification.FakeRemoteRunner(sinks), entered, release.Task) : f.Runner = new RemoteHostVerification.FakeRemoteRunner(sinks));
        await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        // The first sharing's stop waits in its runner's disposal while a new sharing starts and takes a run.
        var stopping = fixture.Host.StopAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        await StartRaw(RemoteClientVerification.Loopback(state.Port!.Value), state.Key!, fixture.Shared.Id, "claude", "hold");
        await Verification.Until(() => told.LastOrDefault() == 1);
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Check(told.Last() == 1 && fixture.Host.Current().ActiveRuns == 1, "the old stop never tells its count after the newer one: " + string.Join(",", told));
    }

    internal static async Task AnswersReachOnlyTheirOwnJobsRequests()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var target = RemoteClientVerification.Loopback(state.Port!.Value); var key = state.Key!; var port = state.Port.Value;
        var asking = await StartRaw(target, key, fixture.Shared.Id, "claude", "ask");
        var holding = await StartRaw(target, key, fixture.Shared.Id, "claude", "hold");
        await Verification.Until(() => fixture.Runner!.Started.Count == 2);
        for (var tries = 0; !(await PollRaw(target, key, asking, 0)).Events.Any(e => e.Event["type"]?.GetValue<string>() == "permission"); tries++)
        {
            Check(tries < 200, "the tool request reached the host");
            await Task.Delay(20);
        }
        Task<byte[]> Post(string job, string action, object body) => RemoteTransport.RequestAsync(target, key, HttpMethod.Post, $"/v2/runs/{job}/{action}", JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), RemoteWire.Json));
        static bool Conflict(Exception ex) => ex is RemoteHttpFailure { Status: 409 };
        await RemoteClientVerification.RejectAsync(() => Post(holding, "permission", new RemoteWirePermission(holding, "perm-1", true)), "another job's request is never this job's", Conflict);
        await RemoteClientVerification.RejectAsync(() => Post(asking, "permission", new RemoteWirePermission(holding, "perm-1", true)), "an answer naming another run is refused", Conflict);
        await RemoteClientVerification.RejectAsync(() => Post(holding, "questions", new RemoteWireQuestions(holding, "perm-1", new Dictionary<string, UserQuestionAnswer>())), "questions only for this job's requests", Conflict);
        await RemoteClientVerification.RejectAsync(() => Post(holding, "plan", RemoteWirePlan.From(holding, "perm-1", PlanDecision.ApproveAutoEdit)), "a plan only for this job's requests", Conflict);
        string Head(string job, string action, string extra) => $"POST /v2/runs/{job}/{action} HTTP/1.1\r\nAuthorization: Bearer {key}\r\nx-mighty-remote-version: 2\r\n{extra}\r\n";
        Check((await RemoteHostVerification.Raw(port, Head(asking, "permission", "Content-Type: application/json\r\nContent-Length: 0\r\n"))).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "an answer with no body is refused");
        Check((await RemoteHostVerification.Raw(port, Head(asking, "permission", "Content-Type: text/plain\r\nContent-Length: 2\r\n"), "{}"u8.ToArray())).StartsWith("HTTP/1.1 415", StringComparison.Ordinal), "an answer must be JSON");
        Check(fixture.Runner!.Answers.IsEmpty, "no refused answer reached the runner");
        await Post(asking, "permission", new RemoteWirePermission(asking, "perm-1", false));
        Check(fixture.Runner.Answers.Single() == (asking, "perm-1", false), "the job's own request is answered");
        var steered = JsonSerializer.Deserialize<RemoteWireAnswer>(await Post(holding, "steer", new RemoteWireSteer("more")), RemoteWire.Json)!;
        Check(steered.Accepted && fixture.Runner.Steered.Single() == (holding, "more"), "steering reaches that job alone");
        // A stop reads no body, as the Mac's does.
        Check((await RemoteHostVerification.Raw(port, Head(holding, "stop", ""))).StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && fixture.Runner.Stopped.Contains(holding), "a stop without a body stops the run");
        Check(!fixture.Runner.Stopped.Contains(asking) && fixture.Runner.Steered.Count == 1, "a stop and a steer reach their own job alone");
        var stopped = await PollRaw(target, key, holding, 0);
        Check(stopped.Done && stopped.Events[^1].Event["status"]?.GetValue<string>() == "stopped", "and the run ends stopped");
    }

    /// <summary>A runner that takes each run only when the check lets it (StartAsync waits), and keeps it until it is stopped.</summary>
    private sealed class GatedRunner : IRemoteRunner
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentDictionary<string, bool> Live = new();
        internal volatile bool Taken;
        public async Task StartAsync(StartRunRequest request) { Entered.TrySetResult(); await Release.Task; Live[request.SessionId] = true; Taken = true; }
        public Task StopAsync(string sessionId) { Live.TryRemove(sessionId, out _); return Task.CompletedTask; }
        public Task<bool> TrySteerAsync(string sessionId, string text) => Task.FromResult(false);
        public void RespondToToolPermission(string sessionId, string requestId, bool allow) { }
        public void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) { }
        public PlanRecord AnswerPlan(string sessionId, string requestId, PlanDecision decision) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { Live.Clear(); return ValueTask.CompletedTask; }
    }

    internal static async Task AStopBeforeTheRunnerTakesTheRunStillEndsIt()
    {
        var gated = new GatedRunner();
        await using var fixture = await RemoteHostVerification.MakeHost(runner: (_, _) => gated);
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var target = RemoteClientVerification.Loopback(state.Port!.Value);
        var job = await StartRaw(target, state.Key!, fixture.Shared.Id, "claude", "hold");
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The stop lands while the runner is still taking the run: it has nothing to stop yet.
        await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, $"/v2/runs/{job}/stop", "{}"u8.ToArray());
        var poll = await PollRaw(target, state.Key!, job, 0);
        Check(poll.Done && poll.Events[^1].Event["status"]?.GetValue<string>() == "stopped" && fixture.Host.Current().ActiveRuns == 0, "the run ends stopped at once");
        gated.Release.TrySetResult();
        await Verification.Until(() => gated.Taken);
        await Verification.Until(() => gated.Live.IsEmpty, 3000);
    }

    internal static async Task StoppingSharingCancelsALaunchNotBegun()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await RemoteHostVerification.MakeHost(beforeLaunch: async token => { entered.TrySetResult(token); await release.Task; });
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        await StartRaw(RemoteClientVerification.Loopback(state.Port!.Value), state.Key!, fixture.Shared.Id, "claude", "hold");
        var launch = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var runner = fixture.Runner!;
        Check(!launch.IsCancellationRequested, "a launch on its way is not cancelled while sharing goes on");
        await fixture.Host.StopAsync();
        Check(launch.IsCancellationRequested, "stopping sharing cancels the launch");
        release.TrySetResult();
        await Task.Delay(200);
        Check(runner.Started.IsEmpty && runner.Disposed, "the run never reaches the runner");
    }

    internal static async Task TheBufferKeepsTheNewestEventsWithinItsBoundsAndSaysTheGap()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var target = RemoteClientVerification.Loopback(state.Port!.Value); var key = state.Key!;
        // "flood": 5000 short lines (over the event bound); "bulk": 200 lines of 30 000 characters (over the byte bound). Each run also
        // says running, echoes its input and ends.
        foreach (var (input, last) in new[] { ("flood", 5003), ("bulk", 203) })
        {
            var job = await StartRaw(target, key, fixture.Shared.Id, "claude", input);
            await Verification.Until(() => fixture.Host.Current().ActiveRuns == 0);
            // The body has the Mac's keys, at the top and in each event (native/contracts/fixtures/remote-v2/poll.json).
            var raw = JsonNode.Parse(await RemoteTransport.RequestAsync(target, key, HttpMethod.Get, $"/v2/runs/{job}/events?cursor=0"))!.AsObject();
            var sample = RemoteWireFixtureVerification.Node("poll.json").AsObject();
            static string Keys(JsonObject o) => string.Join(",", o.Select(p => p.Key).Order(StringComparer.Ordinal));
            Check(Keys(raw) == Keys(sample), "the poll's keys are the Mac's: " + Keys(raw));
            Check(raw["events"]!.AsArray().All(e => Keys(e!.AsObject()) == Keys(sample["events"]![0]!.AsObject())), "each polled event's keys are the Mac's");
            var poll = await PollRaw(target, key, job, 0);
            Check(poll.LastCursor == last, input + ": every event took a cursor: " + poll.LastCursor);
            Check(poll.Gap && poll.Events.Count is > 0 and <= RemoteProtocol.MaximumPollEvents && poll.Events[0].Cursor > 2 && poll.Cursor == poll.Events[^1].Cursor, input + ": the oldest events were dropped and the poll says so: " + poll.Events[0].Cursor);
            Check(poll.Events.Select((e, i) => e.Cursor == poll.Events[0].Cursor + i).All(x => x) && poll.Events.All(e => e.Event["sessionId"]?.GetValue<string>() == job), input + ": one job's events, in order");
            if (input == "flood") Check(poll.Events[0].Cursor == last - RemoteProtocol.MaximumBufferedEvents + 1 && poll.Events.Count == RemoteProtocol.MaximumPollEvents, "the newest 4096 events are kept, 256 a poll");
            else Check(poll.Events.Sum(e => JsonSerializer.SerializeToUtf8Bytes(e, RemoteWire.Json).Length) <= RemoteProtocol.MaximumPollBytes, "a poll stays within its bytes");
            var read = poll.Events.Count; var cursor = poll.Cursor; var done = poll.Done;
            while (!done)
            {
                var next = await PollRaw(target, key, job, cursor);
                Check(!next.Gap && next.Events.Count > 0 && next.Events[0].Cursor == cursor + 1, input + ": nothing more is missing");
                read += next.Events.Count; cursor = next.Cursor; done = next.Done;
            }
            Check(cursor == last && read <= RemoteProtocol.MaximumBufferedEvents, input + ": read to the end within the bound: " + read);
            var empty = await PollRaw(target, key, job, last);
            Check(empty is { Events.Count: 0, Gap: false, Done: true } && empty.Cursor == last, "an empty poll answers with the request's cursor");
            foreach (var query in new[] { "?cursor=" + (last + 1), "?cursor=1&x=2", "?cursor=-1", "?Cursor=1", "?cursor=%31", "" })
                Check((await RemoteHostVerification.Raw(state.Port.Value, $"GET /v2/runs/{job}/events{query} HTTP/1.1\r\nAuthorization: Bearer {key}\r\nx-mighty-remote-version: 2\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a bad cursor is refused: " + query);
        }
    }
}
