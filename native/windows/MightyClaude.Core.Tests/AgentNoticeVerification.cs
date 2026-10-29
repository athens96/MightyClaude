using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// The file the macOS AgentNoticeTests read too (native/contracts/fixtures/agent-notices.json): a background sub-agent's
/// <c>&lt;task-notification&gt;</c> and final report (its hand-back) settle its block the way a background command's
/// notification does, and the turn either opens is a notice turn with a run of its own (<see cref="BackgroundNoticeRuns"/>),
/// not the request's. A message the agent sends while still running only joins its block's log.
/// </summary>
internal static class AgentNoticeVerification
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.AgentNotices.json";
        using var stream = typeof(AgentNoticeVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Same<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{why}: expected <{expected}> but was <{actual}>"); }
    private static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why)
    {
        var (was, wanted) = (actual.ToList(), expected.ToList());
        if (!was.SequenceEqual(wanted)) throw new InvalidOperationException($"{why}: expected [{string.Join(" | ", wanted)}] but was [{string.Join(" | ", was)}]");
    }
    private static List<JsonElement> Items(JsonElement value, string key) =>
        MetadataJson.Property(value, key) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray().ToList() : [];

    internal static Task TheSharedCasesHold()
    {
        HandBacksParseAsTheFixtureSays();
        NoticeResultsAreTheFixtures();
        StreamsSettleTheBlockAndOpenNoticeRuns();
        AMixedTurnLeavesTheEarlierRunInItsOwnState();
        NoticeTitlesFollowTheFixture();
        ResultTitlesFollowTheFixture();
        return Task.CompletedTask;
    }

    private static void HandBacksParseAsTheFixtureSays()
    {
        var cases = Items(Fixture.Value, "handbacks");
        Check(cases.Count > 0, "the fixture has hand-back cases");
        foreach (var item in cases)
        {
            var name = item.Text("name") ?? "";
            var parsed = AgentHandback.Parse(item.Text("text") ?? throw new InvalidOperationException(name + ": no text"));
            var expected = Items(item, "expect");
            SameList(parsed.Select(m => m.From), expected.Select(e => e.Text("from") ?? ""), name + ": senders");
            SameList(parsed.Select(m => m.Body), expected.Select(e => e.Text("body") ?? ""), name + ": bodies");
            SameList(parsed.Select(m => m.IsHandback), expected.Select(e => MetadataJson.Flag(e, "isHandback")), name + ": final reports");
        }
    }

    private static void NoticeResultsAreTheFixtures()
    {
        var cases = Items(Fixture.Value, "results");
        Check(cases.Count > 0, "the fixture has result cases");
        foreach (var item in cases)
            Same(ClaudeStream.IsNotificationResult(MetadataJson.Property(item, "event")), MetadataJson.Flag(item, "expect"), (item.Text("name") ?? "") + ": a notice turn's result");
    }

    /// <summary>The notice runs a stream opened, in the order they were numbered: each run's main and its copies by id.</summary>
    private static List<(string Id, ExecutionGraphNode? Main, List<ExecutionGraphNode> Copies)> NoticeRuns(Dictionary<string, ExecutionGraphNode> latest)
    {
        static int Number(string id) => int.TryParse(id[(id.LastIndexOf(BackgroundNoticeRuns.Marker, StringComparison.Ordinal) + BackgroundNoticeRuns.Marker.Length)..], out var n) ? n : 0;
        return latest.Values.GroupBy(n => n.RunId).Where(g => BackgroundNoticeRuns.IsNoticeRun(g.Key)).OrderBy(g => Number(g.Key))
            .Select(g => (g.Key, g.FirstOrDefault(n => n.Kind == "main"), g.Where(n => n.Kind != "main").OrderBy(n => n.Id, StringComparer.Ordinal).ToList())).ToList();
    }

    /// <summary>Runs <paramref name="action"/> with the app's words in Korean: the fixture's notice run inputs are read in Korean.</summary>
    private static void InKorean(Action action)
    {
        var saved = Locale.LanguagePreference;
        try { Locale.LanguagePreference = "ko"; Locale.ResetCache(); action(); }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private static void StreamsSettleTheBlockAndOpenNoticeRuns() => InKorean(() =>
    {
        var cases = Items(Fixture.Value, "streams");
        Check(cases.Count >= 25, "the fixture has every stream case; got " + cases.Count);
        foreach (var item in cases)
        {
            var name = item.Text("name") ?? "";
            const string runID = "agent-notices";
            var emitted = new List<ExecutionGraphNode>();
            var tracker = new ExecutionGraphTracker(runID, "Do the request", "claude", null, emitted.Add);
            var events = Items(item, "events");
            foreach (var value in events) tracker.Consume(value);
            var expect = MetadataJson.Property(item, "expect");
            var tool = item.Text("agentToolUseId") ?? throw new InvalidOperationException(name + ": no agentToolUseId");
            var latest = new Dictionary<string, ExecutionGraphNode>();
            foreach (var node in emitted) latest[node.Id] = node;
            var block = latest.GetValueOrDefault(ExecutionGraphSupport.AgentNodeID(runID, tool)) ?? throw new InvalidOperationException(name + ": no block for " + tool);
            Same(block.Kind, expect.Text("agentKind"), name + ": kind");
            Same(block.State, expect.Text("agentState"), name + ": state");
            Same(block.Output, expect.Text("agentOutput"), name + ": result");
            // Started in the background, where its run writes, Claude's answer to its notice: checked when the case names them (null = absent).
            if (expect.TryGetProperty("agentBackground", out var background))
                Same(block.Background, background.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }, name + ": background");
            if (expect.TryGetProperty("agentOutputFile", out var outputFile))
                Same(block.OutputFile, outputFile.ValueKind == JsonValueKind.String ? outputFile.GetString() : null, name + ": output file");
            if (expect.TryGetProperty("agentNoticeReply", out var noticeReply))
                Same(block.NoticeReply, noticeReply.ValueKind == JsonValueKind.String ? noticeReply.GetString() : null, name + ": notice reply");
            if (expect.TryGetProperty("noticeReplyBlocks", out var replyBlocks) && replyBlocks.ValueKind == JsonValueKind.Number)
                Same(latest.Values.Count(n => n.NoticeReply is not null), replyBlocks.GetInt32(), name + ": blocks with a notice reply");
            // A message the agent sent while running is in its log, not its result.
            foreach (var text in Items(expect, "agentLog").Select(e => e.GetString()))
                Check(block.Entries.Any(e => e.Kind == "assistant" && e.Text == text), name + ": the log holds '" + text + "'");
            // A message that settles the block, or that nobody here sent, is not in its log.
            foreach (var text in Items(expect, "agentLogLacks").Select(e => e.GetString()))
                Check(!block.Entries.Any(e => e.Text == text), name + ": the log lacks '" + text + "'");
            var notices = latest.Values.Count(n => n.Kind == "steer" && (n.Input ?? "").StartsWith(MightyGraphSupport.TaskNoticePrefix, StringComparison.Ordinal));
            Same(notices, MetadataJson.Property(expect, "noticeBlocks").GetInt32(), name + ": 작업 알림 blocks");
            var main = latest[ExecutionGraphSupport.MainNodeID(runID)];
            foreach (var part in Items(expect, "mainOutputContains").Select(e => e.GetString() ?? ""))
                Check(main.Output?.Contains(part, StringComparison.Ordinal) == true, name + ": the result holds '" + part + "'; got '" + main.Output + "'");
            // No answer to a notice extends the request's result any more.
            Check(main.Output?.Contains(MightyGraphSupport.NoticeSectionMark, StringComparison.Ordinal) != true, name + ": notice section in the request's result; got '" + main.Output + "'");

            // Each notice turn is a run of its own: "<run>:notice:<n>", n from 1.
            var runs = NoticeRuns(latest);
            SameList(runs.Select(r => r.Id), runs.Select((_, index) => BackgroundNoticeRuns.RunID(runID, index + 1)), name + ": run ids");
            if (expect.TryGetProperty("noticeRuns", out var count) && count.ValueKind == JsonValueKind.Number) Same(runs.Count, count.GetInt32(), name + ": notice runs");
            if (expect.TryGetProperty("noticeRunChildren", out var children) && children.ValueKind == JsonValueKind.Array)
                SameList(runs.Select(r => r.Copies.Count), children.EnumerateArray().Select(c => c.GetInt32()), name + ": copies");
            if (expect.TryGetProperty("noticeRunOutput", out var output))
                Same(runs.FirstOrDefault().Main?.Output, output.ValueKind == JsonValueKind.String ? output.GetString() : null, name + ": notice run output");
            if (expect.Text("noticeRunInput") is { } input) Same(runs.FirstOrDefault().Main?.Input, input, name + ": notice run input");
            if (expect.TryGetProperty("noticeRunTitles", out var titles) && titles.ValueKind == JsonValueKind.Array)
                SameList(runs.Select(r => r.Main?.Title ?? ""), titles.EnumerateArray().Select(t => t.GetString() ?? ""), name + ": titles");
            if (expect.TryGetProperty("noticeRunOutputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                SameList(runs.Select(r => r.Main?.Output ?? "<none>"), outputs.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "<none>"), name + ": outputs");
            var kinds = new[] { BackgroundNoticeRuns.Opener.Work, BackgroundNoticeRuns.Opener.Message, BackgroundNoticeRuns.Opener.Delegation }.Select(BackgroundNoticeRuns.Kind).ToList();
            var endsInTurn = events.Count == 0 || events[^1].Text("type") != "result";
            foreach (var (id, noticeMain, copies) in runs)
            {
                var notice = noticeMain ?? throw new InvalidOperationException(name + ": " + id + " has no main");
                Check(kinds.Any(kind => notice.Title == kind || notice.Title.StartsWith(kind + " · ", StringComparison.Ordinal)) && notice.ParentId is null, name + ": " + id + " main '" + notice.Title + "'");
                // A run with Claude's answer is complete; only a stream that ends inside a turn answering in it again leaves it running.
                // The request's result never holds that answer.
                if (notice.Output is { } answer)
                {
                    Check(notice.State == "completed" || endsInTurn && notice.State == "running", name + ": " + id + " state " + notice.State);
                    Check(main.Output?.Contains(answer, StringComparison.Ordinal) != true, name + ": " + id + " answer in the request's result");
                }
                foreach (var copy in copies)
                {
                    var source = copy.SourceBlockId is { } sourceId ? latest.GetValueOrDefault(sourceId) : null;
                    Check(source is not null, name + ": " + copy.Id + " source");
                    Check(copy.Id == id + ":" + source!.Id && copy.ParentId == notice.Id && copy.Background == true, name + ": " + copy.Id);
                }
            }
            // A block's latest copy is the block as it ended: kind, title, request, state, result and output file.
            foreach (var source in runs.SelectMany(r => r.Copies).Select(c => c.SourceBlockId).OfType<string>().Distinct())
            {
                var copy = runs.Last(r => r.Copies.Any(c => c.SourceBlockId == source)).Copies.First(c => c.SourceBlockId == source);
                var original = latest[source];
                Check(copy.Kind == original.Kind && copy.Title == original.Title && copy.Input == original.Input, name + ": " + copy.Id + " copy");
                Check(copy.State == original.State && copy.Output == original.Output && copy.OutputFile == original.OutputFile, name + ": " + copy.Id + " copy");
            }
        }
    });

    /// <summary>The notice run title rule: kind and names in, "&lt;kind&gt; · &lt;name&gt;", "&lt;kind&gt; · &lt;first&gt; 외 &lt;n&gt;개" or the kind alone out
    /// (macOS AgentNoticeTests.noticeTitlesFollowTheFixture).</summary>
    private static void NoticeTitlesFollowTheFixture() => InKorean(() =>
    {
        var cases = Items(Fixture.Value, "noticeTitles");
        Check(cases.Count >= 4, "the fixture has notice title cases");
        foreach (var item in cases)
        {
            var names = Items(item, "names").Select(n => n.GetString() ?? "").ToList();
            var title = BackgroundNoticeRuns.Title(Locale.Get(item.Text("kind") ?? ""), names);
            Same(title, item.Text("expect"), "notice title: " + item.Text("name"));
        }
    });

    /// <summary>The result title rule: how the request ended, or 현재까지의 결과 with the count while background work runs
    /// (macOS AgentNoticeTests.resultTitlesFollowTheFixture).</summary>
    private static void ResultTitlesFollowTheFixture() => InKorean(() =>
    {
        var cases = Items(Fixture.Value, "resultTitles");
        Check(cases.Count >= 4, "the fixture has result title cases");
        foreach (var item in cases)
        {
            var expect = MetadataJson.Property(item, "expect");
            var count = MetadataJson.Property(expect, "count");
            var wanted = Locale.Get(expect.Text("key") ?? "", count.ValueKind == JsonValueKind.Number
                ? new Dictionary<string, string> { ["count"] = count.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) } : null);
            var waiting = MetadataJson.Property(item, "waiting") is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : 0;
            Same(MightyGraphBlockModel.ResultTitle(item.Text("status") ?? "", waiting), wanted, "result title: " + item.Text("name"));
        }
    });

    /// <summary>A turn that would answer in an earlier notice run but brings new work opens a run for that work; the earlier run ends as it
    /// was before the turn, an error included (macOS AgentNoticeTests.aMixedTurnLeavesTheEarlierRunInItsOwnState).</summary>
    private static void AMixedTurnLeavesTheEarlierRunInItsOwnState() => InKorean(() =>
    {
        var mixed = Items(Fixture.Value, "streams").First(c => c.Text("name")?.StartsWith("notice runs: a turn that also brings a new block", StringComparison.Ordinal) == true);
        var events = Items(mixed, "events").Select(e => System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!).ToList();
        // The first notice turn fails; stop after the mixed turn's answer.
        var first = events.FindIndex(e => (string?)e["type"] == "result" && (string?)e["result"] == "The build passed.");
        var mixedEnd = events.FindIndex(e => (string?)e["type"] == "result" && (string?)e["result"] == "The checks passed.");
        Check(first >= 0 && mixedEnd > first, "the mixed case has both answers");
        events[first]!["is_error"] = true;
        var nodes = new List<ExecutionGraphNode>();
        bool afterFirst = false, reopened = false;
        var earlier = ExecutionGraphSupport.MainNodeID("mixed:notice:1");
        var tracker = new ExecutionGraphTracker("mixed", "Do the request", "claude", null, node =>
        {
            nodes.Add(node);
            if (afterFirst && node.Id == earlier && node.State == "running") reopened = true;
        });
        for (var index = 0; index <= mixedEnd; index++)
        {
            tracker.Consume(JsonSerializer.SerializeToElement(events[index]));
            if (index == first) afterFirst = true;
        }
        var latest = new Dictionary<string, ExecutionGraphNode>();
        foreach (var node in nodes) latest[node.Id] = node;
        var runs = NoticeRuns(latest);
        Same(runs.Count, 2, "a mixed turn opens a second run");
        Check(reopened, "the turn about the copied build first answers in its run");
        Check(runs[0].Main is { State: "error", Output: "The build passed." }, "the earlier run ends as it was: " + runs[0].Main?.State + " / " + runs[0].Main?.Output);
        Check(runs[1].Main is { State: "completed", Output: "The checks passed." }, "the new run has the mixed turn's answer");
    });

    // ── history ─────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Base = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Report = "## Docs survey\n\n- help/board.md mentions the board view.";
    private static string Stamp(double seconds) => Base.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    private static string Handback(string sender) =>
        "<agent-message from=\"" + sender + "\">\n[Subagent hand-back] The text below is the final report of a subagent. The report follows:\n"
        + string.Join("\n", Report.Split('\n').Select(line => "  " + line)) + "\n</agent-message>";
    private static Dictionary<string, object?> User(string uuid, double at, object content, params (string Key, object? Value)[] extra)
    {
        var line = new Dictionary<string, object?> { ["type"] = "user", ["uuid"] = uuid, ["timestamp"] = Stamp(at), ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = content } };
        foreach (var (key, value) in extra) line[key] = value;
        return line;
    }
    private static object AgentCall(string name) => new
    {
        type = "assistant", uuid = "a1", timestamp = Stamp(1),
        message = new { id = "m1", role = "assistant", content = new object[] { new { type = "tool_use", id = "toolu_FAKEagent0001", name = "Agent", input = new { description = "Survey the docs", name, prompt = "Survey the docs.", run_in_background = true } } } },
    };
    private static object Launch(string agent) => User("t1", 2,
        new object[] { new { type = "tool_result", tool_use_id = "toolu_FAKEagent0001", content = new object[] { new { type = "text", text = "Async agent launched successfully.\nagentId: " + agent + " (internal ID)\nThe agent is working in the background." } } } },
        ("toolUseResult", new { }));
    private static object Answer(string uuid, string text, double at) => new
    {
        type = "assistant", uuid, timestamp = Stamp(at), message = new { id = "m-" + uuid, role = "assistant", content = new[] { new { type = "text", text } } },
    };
    private static List<MightyGraphRun> Replay(params object[] lines)
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-agent-notices-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (home, workspace) = (Path.Combine(root, "home"), Path.Combine(root, "work", "project"));
            Directory.CreateDirectory(workspace);
            var folder = Path.Combine(home, ".claude", "projects", SessionHistory.ClaudeProjectFolder(workspace));
            Directory.CreateDirectory(folder);
            const string id = "5b0c1d2e-3333-4444-8555-666677778888";
            File.WriteAllText(Path.Combine(folder, id + ".jsonl"), string.Join("\n", lines.Select(line => JsonSerializer.Serialize(line))) + "\n");
            var runs = SessionHistory.Load(new SessionHistoryRequest("claude", id, workspace) { Home = home }).Runs;
            return runs.Count > 0 ? runs : throw new InvalidOperationException("the record replays no request");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static void CheckSettled(MightyGraphRun run, string why)
    {
        var agent = run.Agents.FirstOrDefault(a => a.Kind is null) ?? throw new InvalidOperationException(why + ": no agent block");
        Same(agent.Status, "completed", why + ": the agent's state");
        Same(agent.Entries.LastOrDefault(e => e.Id.EndsWith("-answer", StringComparison.Ordinal))?.Text, Report, why + ": the agent's result is its report");
        Same(run.Agents.Count(a => MightyGraphSupport.IsTaskNotice(a)), 0, why + ": no 작업 알림");
    }

    /// <summary>
    /// A hand-back recorded as a meta line opens a notice turn, as live: the block takes the report, and that turn's answer is the result
    /// of a notice run of its own; the notification after it answers in the same run (macOS AgentNoticeTests.aRecordedHandBackTurnSettlesTheAgentAsLive).
    /// </summary>
    internal static Task ARecordedHandBackTurnSettlesTheAgentAsLive()
    {
        const string agent = "a1b2c3d4e5f6a7b8c";
        const string notice = "<task-notification>\n<task-id>" + agent + "</task-id>\n<tool-use-id>toolu_FAKEagent0001</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Survey the docs\" finished</summary>\n</task-notification>";
        var runs = Replay(
            User("u1", 0, "survey the docs"),
            AgentCall("docs-surveyor"),
            Launch(agent),
            Answer("a2", "Started the survey.", 3),
            User("h1", 30, "Another Claude session sent a message:\n" + Handback(agent) + "\n\nThat other Claude session is an agent of this session.",
                ("isMeta", true), ("origin", new { kind = "peer", from = agent, senderTaskId = agent })),
            Answer("a3", "The survey found one page.", 31),
            User("n1", 32, notice, ("origin", new { kind = "task-notification" })),
            Answer("a4", "Already handled.", 33));
        Same(runs.Count, 2, "the request and one notice run");
        var run = runs[0];
        CheckSettled(run, "a recorded hand-back turn");
        Same(run.FinalOutput, "Started the survey.", "the request keeps its own answer");
        Check(run.NoticeRun is null, "the request is no notice run");
        var noticeRun = runs[1];
        Check(noticeRun.NoticeRun == true && noticeRun.Status == "completed", "the hand-back turn is a notice run of its own");
        Same(noticeRun.FinalOutput, "The survey found one page.\n\nAlready handled.", "the notification after it answers in the same run");
        var copy = noticeRun.Agents.FirstOrDefault() ?? throw new InvalidOperationException("the notice run has no copy");
        Check(copy.SourceBlockId == run.Agents.First(a => a.Kind is null).Id && copy.Background == true, "the copy names its block");
        Same(copy.Entries.LastOrDefault(e => e.Id.EndsWith("-answer", StringComparison.Ordinal))?.Text, Report, "the copy's result is the report");
        return Task.CompletedTask;
    }

    /// <summary>A hand-back taken into the running turn is recorded as a queued command; it settles the block by the call's name, and its
    /// copy goes to a finished notice run without a result (macOS AgentNoticeTests.aQueuedHandBackSettlesTheAgentByItsName).</summary>
    internal static Task AQueuedHandBackSettlesTheAgentByItsName()
    {
        var runs = Replay(
            User("u1", 0, "survey the docs"),
            AgentCall("docs-surveyor"),
            Launch("a2b3c4d5e6f7a8b9c"),
            new Dictionary<string, object?>
            {
                ["type"] = "attachment", ["uuid"] = "q1", ["timestamp"] = Stamp(10), ["isMeta"] = true,
                ["attachment"] = new Dictionary<string, object?>
                {
                    ["type"] = "queued_command", ["commandMode"] = "prompt", ["isMeta"] = true, ["origin"] = new { kind = "peer", from = "docs-surveyor" }, ["prompt"] = Handback("docs-surveyor"),
                },
            },
            Answer("a2", "The survey is done.", 11));
        Same(runs.Count, 2, "the request and one notice run");
        var run = runs[0];
        CheckSettled(run, "a queued hand-back");
        Same(run.FinalOutput, "The survey is done.", "the request's result");
        Check(runs[1] is { NoticeRun: true, Status: "completed", FinalOutput: null, Agents.Count: 1 }, "the absorbed notice run is finished, without a result");
        return Task.CompletedTask;
    }
}
