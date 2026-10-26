using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// The file the macOS AgentNoticeTests read too (native/contracts/fixtures/agent-notices.json): a background sub-agent's
/// <c>&lt;task-notification&gt;</c> and final report (its hand-back) settle its block the way a background command's
/// notification does, with one 작업 알림 per agent, and the turn a hand-back opens is a notice turn, not the request's. A
/// message the agent sends while still running only joins its block's log.
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
        StreamsSettleTheBlockWithOneNoticePerAgent();
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

    private static void StreamsSettleTheBlockWithOneNoticePerAgent()
    {
        var cases = Items(Fixture.Value, "streams");
        Check(cases.Count >= 13, "the fixture has every stream case; got " + cases.Count);
        foreach (var item in cases)
        {
            var name = item.Text("name") ?? "";
            const string runID = "agent-notices";
            var emitted = new List<ExecutionGraphNode>();
            var tracker = new ExecutionGraphTracker(runID, "Do the request", "claude", null, emitted.Add);
            foreach (var value in Items(item, "events")) tracker.Consume(value);
            var expect = MetadataJson.Property(item, "expect");
            var tool = item.Text("agentToolUseId") ?? throw new InvalidOperationException(name + ": no agentToolUseId");
            var latest = new Dictionary<string, ExecutionGraphNode>();
            foreach (var node in emitted) latest[node.Id] = node;
            var block = latest.GetValueOrDefault(ExecutionGraphSupport.AgentNodeID(runID, tool)) ?? throw new InvalidOperationException(name + ": no block for " + tool);
            Same(block.Kind, expect.Text("agentKind"), name + ": kind");
            Same(block.State, expect.Text("agentState"), name + ": state");
            Same(block.Output, expect.Text("agentOutput"), name + ": result");
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
            // A notice turn's answer follows in the result under the line its notices earned (a locale key, with the titles).
            foreach (var section in Items(expect, "mainNoticeSections"))
            {
                var header = Locale.Get(section.Text("header") ?? "", section.Text("title") is { } title ? new Dictionary<string, string> { ["title"] = title } : null);
                var part = MightyGraphSupport.NoticeSectionMark + header + "**\n\n" + (section.Text("answer") ?? "");
                Check(main.Output?.Contains(part, StringComparison.Ordinal) == true, name + ": the result holds '" + part + "'; got '" + main.Output + "'");
            }
        }
    }

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
    private static MightyGraphRun Replay(params object[] lines)
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
            return runs.FirstOrDefault() ?? throw new InvalidOperationException("the record replays no request");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static void CheckSettled(MightyGraphRun run, string why)
    {
        var agent = run.Agents.FirstOrDefault(a => a.Kind is null) ?? throw new InvalidOperationException(why + ": no agent block");
        Same(agent.Status, "completed", why + ": the agent's state");
        Same(agent.Entries.LastOrDefault(e => e.Id.EndsWith("-answer", StringComparison.Ordinal))?.Text, Report, why + ": the agent's result is its report");
        Same(run.Agents.Count(a => MightyGraphSupport.IsTaskNotice(a)), 1, why + ": one 작업 알림");
    }

    /// <summary>
    /// A hand-back recorded as a meta line opens a notice turn, as live: the block takes the report, one 작업 알림, and that
    /// turn's answer follows the request's in the result (macOS AgentNoticeTests.aRecordedHandBackTurnSettlesTheAgentAsLive).
    /// </summary>
    internal static Task ARecordedHandBackTurnSettlesTheAgentAsLive()
    {
        const string agent = "a1b2c3d4e5f6a7b8c";
        const string notice = "<task-notification>\n<task-id>" + agent + "</task-id>\n<tool-use-id>toolu_FAKEagent0001</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Survey the docs\" finished</summary>\n</task-notification>";
        var run = Replay(
            User("u1", 0, "survey the docs"),
            AgentCall("docs-surveyor"),
            Launch(agent),
            Answer("a2", "Started the survey.", 3),
            User("h1", 30, "Another Claude session sent a message:\n" + Handback(agent) + "\n\nThat other Claude session is an agent of this session.",
                ("isMeta", true), ("origin", new { kind = "peer", from = agent, senderTaskId = agent })),
            Answer("a3", "The survey found one page.", 31),
            User("n1", 32, notice, ("origin", new { kind = "task-notification" })),
            Answer("a4", "Already handled.", 33));
        CheckSettled(run, "a recorded hand-back turn");
        Check(run.FinalOutput?.StartsWith("Started the survey.", StringComparison.Ordinal) == true && run.FinalOutput.Contains("The survey found one page.", StringComparison.Ordinal),
            "the hand-back turn's answer follows the request's; got '" + run.FinalOutput + "'");
        return Task.CompletedTask;
    }

    /// <summary>A hand-back taken into the running turn is recorded as a queued command; it settles the block by the call's name
    /// (macOS AgentNoticeTests.aQueuedHandBackSettlesTheAgentByItsName).</summary>
    internal static Task AQueuedHandBackSettlesTheAgentByItsName()
    {
        var run = Replay(
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
        CheckSettled(run, "a queued hand-back");
        Same(run.FinalOutput, "The survey is done.", "the request's result");
        return Task.CompletedTask;
    }
}
