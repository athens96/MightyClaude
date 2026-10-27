using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/pending.json (natural submit A1, B2, B5): the pending display rule, the submit answers a page or a
// phone reads, and where a view moves for a new request. The Mac (PendingTests), the Windows page (vitest) and the phone (jest)
// read the same file, so the four cannot drift apart. Also the Windows host's side of B5: a start names its user entry and graph
// run, the run's diagram block goes out before anything is looked up, and the phone's submit route answers with both.
internal static class PendingVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.Pending.json";
        using var stream = typeof(PendingVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    private static double? Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static Task EveryDisplayCaseHolds()
    {
        var cases = Fixture.Value.GetProperty("display").EnumerateArray().ToList();
        Check(cases.Count > 0, "the fixture has display cases");
        foreach (var entry in cases)
        {
            var name = entry.GetProperty("name").GetString()!;
            var started = Number(entry, "startedAt")!.Value; var ended = Number(entry, "endedAt"); var now = Number(entry, "now")!.Value;
            var failed = entry.TryGetProperty("failed", out var flag) && flag.ValueKind == JsonValueKind.True;
            var expected = entry.GetProperty("expectVisible").GetBoolean();
            Check(PendingDisplay.Visible(started, ended, now, failed) == expected, $"{name}: in milliseconds expected {expected}");

            // The same case as a state the views hold, at two origins: the clock's start, and a date far from it.
            foreach (var origin in new[] { DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 10, 27, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567) })
            {
                DateTimeOffset At(double milliseconds) => origin.AddTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));
                PendingState state = failed ? new PendingState.Failed("broke", At(started), ended is { } e ? At(e) : (DateTimeOffset?)null)
                    : ended is { } end ? (PendingState)new PendingState.Done(At(started), At(end)) : new PendingState.Pending(At(started));
                var indicator = PendingDisplay.Indicator(state, At(now));
                Check(PendingDisplay.Visible(state, At(now)) == expected, $"{name}: as {state.GetType().Name} at {origin:O} expected {expected}");
                // A failure shows its message from its end on, and the loading before it.
                PendingIndicator shown = !expected ? new PendingIndicator.Hidden() : failed && now >= ended!.Value ? (PendingIndicator)new PendingIndicator.Failed("broke") : new PendingIndicator.Loading();
                Check(indicator == shown, $"{name}: expected {shown}, got {indicator}");
            }
        }
        return Task.CompletedTask;
    }

    internal static Task IdleAndAnOpenFailure()
    {
        var now = DateTimeOffset.UtcNow;
        Check(PendingDisplay.Indicator(new PendingState.Idle(), now) == new PendingIndicator.Hidden() && !PendingDisplay.Visible(new PendingState.Idle(), now), "idle shows nothing");
        Check(PendingDisplay.Indicator(new PendingState.Failed("gone", now, null), now) == new PendingIndicator.Failed("gone"), "a failure without an end fails now, at once");
        Check(PendingDisplay.Indicator(new PendingState.Failed("gone", now.AddSeconds(-5), null), now) == new PendingIndicator.Failed("gone"), "and keeps its message");
        Check(PendingDisplay.Visible(0, null, 0, failed: true), "in milliseconds too");
        // The rule's own numbers are the motion tokens.
        Check(PendingDisplay.Visible(0, null, DesignMetrics.Motion.PendingShowAfter) && !PendingDisplay.Visible(0, null, DesignMetrics.Motion.PendingShowAfter - 1), "the loading waits pendingShowAfter");
        Check(PendingDisplay.Visible(0, 200, 150 + DesignMetrics.Motion.PendingMinVisible) && !PendingDisplay.Visible(0, 200, 151 + DesignMetrics.Motion.PendingMinVisible), "and stays pendingMinVisible");
        return Task.CompletedTask;
    }

    private static SubmitOutcome? Outcome(JsonElement value) => Text(value) switch
    {
        "started" => SubmitOutcome.Started,
        "queued" => SubmitOutcome.Queued,
        "steered" => SubmitOutcome.Steered,
        _ => null,
    };

    internal static Task EverySubmitResponseParses()
    {
        var cases = Fixture.Value.GetProperty("submitResponses").EnumerateArray().ToList();
        Check(cases.Count > 0, "the fixture has submit responses");
        foreach (var entry in cases)
        {
            var name = entry.GetProperty("name").GetString()!; var raw = entry.GetProperty("raw"); var parsed = entry.GetProperty("parsed");
            var expected = new SubmitResponse(parsed.GetProperty("accepted").GetBoolean(), Text(parsed.GetProperty("entryId")), Text(parsed.GetProperty("runId")), Outcome(parsed.GetProperty("outcome")));
            var fromElement = SubmitResponse.Parse(raw);
            Check(fromElement == expected, $"{name}: expected {expected}, got {fromElement}");
            var fromText = SubmitResponse.Parse(raw.GetRawText());
            Check(fromText == expected, $"{name}: as text expected {expected}, got {fromText}");
        }
        Check(SubmitResponse.Parse((JsonElement?)null) == new SubmitResponse(false), "no body is not accepted");
        Check(SubmitResponse.Parse("not json") == new SubmitResponse(false) && SubmitResponse.Parse("") == new SubmitResponse(false), "a body that is not JSON is not accepted");
        Check(SubmitResponse.Parse("""{"accepted":1}""") == new SubmitResponse(false), "1 is not true");
        Check(SubmitResponse.Parse("""{"accepted":"dropped"}""") == new SubmitResponse(false), "dropped is never an answer");
        Check(SubmitResponse.Parse("[\"started\"]") == new SubmitResponse(false), "an array is not a body");
        foreach (var outcome in Enum.GetValues<SubmitOutcome>())
            Check(SubmitResponse.Parse($$"""{"outcome":"{{SubmitResponse.Word(outcome)}}"}""").Outcome == outcome, "the word for " + outcome + " reads back");
        return Task.CompletedTask;
    }

    internal static Task TheHostAnswersParseBack()
    {
        SubmitResponse Read(object body) => SubmitResponse.Parse(JsonSerializer.SerializeToElement(body, Wire.Json));
        var started = new SubmitReceipt("started", "entry-1", "run-1");
        var phone = JsonSerializer.SerializeToElement(started.MobileReply(), Wire.Json);
        Check(phone.GetProperty("protocol").GetInt32() == 1 && phone.Text("accepted") == "started" && phone.Text("outcome") == "started", "the phone's reply keeps protocol 1 and the accepted word: " + phone);
        Check(Read(started.MobileReply()) == new SubmitResponse(true, "entry-1", "run-1", SubmitOutcome.Started), "a started reply parses with its ids");
        var queued = JsonSerializer.Serialize(new SubmitReceipt("queued").MobileReply(), Wire.Json);
        Check(queued == """{"protocol":1,"accepted":"queued","outcome":"queued"}""", "a queued reply has no id keys at all: " + queued);
        Check(Read(new SubmitReceipt("queued").MobileReply()) == new SubmitResponse(true, null, null, SubmitOutcome.Queued), "a queued reply parses without ids");

        // The page's send: accepted as a boolean, the composer's word, the ids, and the page's own clientId back.
        var answer = JsonSerializer.SerializeToElement(started.SendAnswer("client-7", "", new { busy = true }), Wire.Json);
        Check(answer.GetProperty("accepted").ValueKind == JsonValueKind.True && answer.Text("clientId") == "client-7" && answer.Text("draft") == "" && answer.GetProperty("state").GetProperty("busy").GetBoolean(),
            "the send answer carries accepted, the clientId, the draft and the state: " + answer);
        Check(SubmitResponse.Parse(answer) == new SubmitResponse(true, "entry-1", "run-1", SubmitOutcome.Started), "a started send parses with its ids");
        var blocked = JsonSerializer.SerializeToElement(new SubmitReceipt("blocked").SendAnswer("client-8", "hello", null), Wire.Json);
        Check(SubmitResponse.Parse(blocked) == new SubmitResponse(false) && blocked.Text("outcome") == "blocked" && !blocked.TryGetProperty("entryId", out _), "a send that made no request is not accepted: " + blocked);
        foreach (var word in new[] { "worktree", "answered", "builtin" })
            Check(SubmitResponse.Parse(JsonSerializer.SerializeToElement(new SubmitReceipt(word).SendAnswer(null, "", null), Wire.Json)) == new SubmitResponse(true), word + " is taken, with no block to match");
        foreach (var word in new[] { "notStarted", "blocked", "none" }) Check(!new SubmitReceipt(word).Accepted, word + " is not taken");
        Check(new SubmitReceipt("started").SendAnswer("../bad id", "", null).ClientId is null && new SubmitReceipt("started").SendAnswer(null, "", null).ClientId is null, "only an identifier is echoed");
        return Task.CompletedTask;
    }

    internal static Task EveryScrollTargetHolds()
    {
        var cases = Fixture.Value.GetProperty("scrollTargets").EnumerateArray().ToList();
        Check(cases.Count > 0, "the fixture has scroll targets");
        var views = new HashSet<SubmitScroll.View>();
        foreach (var entry in cases)
        {
            var name = entry.GetProperty("name").GetString()!; var input = entry.GetProperty("input"); var expect = entry.GetProperty("expect");
            var view = Enum.Parse<SubmitScroll.View>(input.Text("view")!, ignoreCase: true); views.Add(view);
            var decision = SubmitScroll.Decide(view, input.GetProperty("own").GetBoolean(), input.Text("chosenBoard") == "newest", input.GetProperty("nearBottom").GetBoolean());
            var expected = new SubmitScroll.Decision(Enum.Parse<SubmitScroll.Target>(expect.Text("scroll")!, ignoreCase: true), expect.GetProperty("resetBoard").GetBoolean());
            Check(decision == expected, $"{name}: expected {expected}, got {decision}");
        }
        Check(views.SetEquals(Enum.GetValues<SubmitScroll.View>()), "the cases cover all four views");
        foreach (var mode in new string?[] { null, "diagram", "timeline", "structure", "unknown" }) Check(SubmitScroll.ViewFor(false, mode) == SubmitScroll.View.Basic, "outside the Mighty view it is the transcript, whatever the mode: " + mode);
        Check(SubmitScroll.ViewFor(true, null) == SubmitScroll.View.Diagram && SubmitScroll.ViewFor(true, "unknown") == SubmitScroll.View.Diagram && SubmitScroll.ViewFor(true, "diagram") == SubmitScroll.View.Diagram, "a missing or unknown mode is the diagram");
        Check(SubmitScroll.ViewFor(true, "timeline") == SubmitScroll.View.Timeline && SubmitScroll.ViewFor(true, "structure") == SubmitScroll.View.Pipeline, "the timeline and the pipeline");
        return Task.CompletedTask;
    }

    internal static async Task AStartNamesItsEntryAndPublishesItsBlockAtOnce()
    {
        var directory = Verification.Temp();
        try
        {
            // The runner alone: its first graph_run goes out while the workspace is still being looked up, so it never waits for
            // the one-second loop (which starts only after it), and it carries the id the start was given.
            var workspace = new Workspace { Path = directory };
            var gate = new TaskCompletionSource<Workspace>(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<RunEvent>();
            await using (var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)))
            await using (var manager = new RunManager(_ => gate.Task, catalog, directory, ev => { lock (events) events.Add(ev); }))
            {
                await manager.StartAsync(new StartRunRequest("pane", workspace.Id, "claude", "hello", []) { GraphRunId = "graph-run-1" });
                await Verification.Until(() => { lock (events) return events.Any(e => e.Type == "graph_run"); });
                RunEvent[] early; lock (events) early = [.. events];
                var first = early.First(e => e.Type == "graph_run").GraphRun!;
                Check(first.Id == "graph-run-1" && first.Status == "running" && first.Input == "hello", $"the first block is the request's, running: {first.Id} {first.Status} {first.Input}");
                Check(!early.Any(e => e.Type == "status"), "it went out before the workspace was read");
                gate.SetResult(workspace);
                // No CLI is found, so nothing runs: the run ends in error and its last block keeps the id.
                await Verification.Until(() => { lock (events) return events.Any(e => e.Type == "status" && e.Status is "completed" or "error" or "stopped"); });
                RunEvent[] all; lock (events) all = [.. events];
                Check(all.Where(e => e.Type == "graph_run").All(e => e.GraphRun!.Id == "graph-run-1") && MightyGraphSupport.Terminal(all.Last(e => e.Type == "graph_run").GraphRun!.Status), "every block of the run is the one id, settled at the end");
            }

            // Through the service: the start writes the user entry's id and the graph run's into the receipt, and they are the ones the pane shows.
            await using var providers = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory, providers, delegationEnabled: () => false); await service.InitializeAsync();
            var added = await service.AddWorkspaceAsync(directory);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "agent", WorkspaceId = added.Id }, new RunSession { Id = "cmd", WorkspaceId = added.Id, Kind = "shell" }] });
            var receipt = new RunStartReceipt();
            await service.StartAsync(new StartRunRequest("agent", added.Id, "claude", "hello", []) { Receipt = receipt });
            var agent = service.Snapshot.Sessions.Single(s => s.Id == "agent");
            Check(receipt.EntryId is { } entry && agent.Logs.Last(l => l.Kind == "user").Id == entry, "the receipt names the pane's user entry");
            Check(Wire.Identifier(receipt.GraphRunId), "an agent start names its graph run");
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "agent").Status is "completed" or "error" or "stopped");
            Check(service.Snapshot.Sessions.Single(s => s.Id == "agent").GraphRuns?.Any(r => r.Id == receipt.GraphRunId) == true, "the pane's diagram block carries that id");
            var shell = new RunStartReceipt();
            await service.StartAsync(new StartRunRequest("cmd", added.Id, "shell", "echo hello", []) { Receipt = shell });
            Check(shell.EntryId == service.Snapshot.Sessions.Single(s => s.Id == "cmd").Logs.Last(l => l.Kind == "user").Id && shell.GraphRunId is null, "a shell start names its entry and no graph run");
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "cmd").Status is "completed" or "error" or "stopped");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task ThePhoneSubmitRouteAnswersWithTheReceipt()
    {
        var directory = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory, delegationEnabled: () => false); await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(directory);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "pane", WorkspaceId = workspace.Id }] });
            object? answer = null;
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult(answer), _ => new([]));
            async Task<JsonElement> Submit(object? result)
            {
                answer = result;
                var reply = await router.RouteAsync("POST", "/m1/sessions/pane/submit", JsonSerializer.SerializeToElement(new { text = "hello", mode = "queue" }, Wire.Json), "phone", CancellationToken.None);
                Check(reply.Status == 202, "a submit is accepted with 202: " + reply.Status);
                return JsonSerializer.SerializeToElement(reply.Body, Wire.Json);
            }
            var started = await Submit(new SubmitReceipt("started", "entry-1", "run-1"));
            Check(started.Text("accepted") == "started" && SubmitResponse.Parse(started) == new SubmitResponse(true, "entry-1", "run-1", SubmitOutcome.Started), "a start answers with its ids and the accepted word: " + started);
            var queued = await Submit(new SubmitReceipt("queued"));
            Check(!queued.TryGetProperty("entryId", out _) && SubmitResponse.Parse(queued) == new SubmitResponse(true, null, null, SubmitOutcome.Queued), "a queued request answers without ids: " + queued);
            var old = await Submit("started");
            Check(old.GetRawText() == """{"protocol":1,"accepted":"started"}""", "any other answer is as before: " + old);
        }
        finally { Directory.Delete(directory, true); }
    }
}
