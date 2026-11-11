using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// The role view's records as the graph tracker keeps them, checked against the very cases of the macOS suite: who a sub-agent block is
/// (GraphAgentIdentityTests), a foreground sub-agent's notification that opens no notice run (ForegroundAgentNoticeTests), the tool calls a
/// response record names over the events of one message (ResponseActivityIdsTests), a background command's start, end and exit code
/// (FlowTaskTests) and the advisor's consultations (GraphAdvisorTests). The recordings are the shared native/contracts/fixtures/claude-stream-*.jsonl:
/// replayed here, they must give what they give on the Mac.
/// </summary>
internal static class FlowTrackerVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Same<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{why}: expected <{expected}> but was <{actual}>"); }
    private static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why)
    {
        var (was, wanted) = (actual.ToList(), expected.ToList());
        if (!was.SequenceEqual(wanted)) throw new InvalidOperationException($"{why}: expected [{string.Join(" | ", wanted)}] but was [{string.Join(" | ", was)}]");
    }

    internal static IEnumerable<JsonElement> Fixture(string name)
    {
        var resource = "MightyCode.Core.Tests." + name + ".jsonl";
        using var stream = typeof(FlowTrackerVerification).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource + " is not embedded");
        using var reader = new StreamReader(stream);
        var lines = new List<JsonElement>();
        while (reader.ReadLine() is { } line) if (line.Trim().Length > 0) lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        return lines;
    }

    /// <summary>A tracker as a run makes it, and the pane's runs as the run pipeline hands them over: the request's, then each notice run.</summary>
    internal sealed class Pane(string runID, string? effort = null, string? advisor = null)
    {
        internal readonly List<ExecutionGraphNode> Nodes = [];
        internal readonly ExecutionGraphTracker Tracker = new(runID, "probe", "claude", null, _ => { }, effort, advisor);
        internal void Send(object value) => Tracker.Consume(JsonSerializer.SerializeToElement(value));
        internal void Send(JsonElement value) => Tracker.Consume(value);
        internal List<MightyGraphRun> Runs(bool live = true) =>
            [.. new[] { Tracker.BuildRun(live) }.OfType<MightyGraphRun>(), .. Tracker.BuildNoticeRuns(live)];
        internal List<MightyGraphRun> Finish(string state = "completed") { Tracker.Finish(state); return Runs(false); }
    }

    internal static List<MightyGraphRun> Replay(string fixture, string runID, string? effort = null)
    {
        var pane = new Pane(runID, effort);
        foreach (var line in Fixture(fixture)) pane.Send(line);
        return pane.Finish();
    }

    internal static void InLanguage(string language, Action action)
    {
        var saved = Locale.LanguagePreference;
        try { Locale.LanguagePreference = language; Locale.ResetCache(); action(); }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private static object Spawn(string id, object input) =>
        new { type = "assistant", uuid = "call-" + id, session_id = "main", message = new { id = "call-" + id, content = new object[] { new { type = "tool_use", id, name = "Agent", input } } } };
    private static MightyGraphAgent Agent(Pane pane, string tool, string run = "run") =>
        pane.Runs().SelectMany(r => r.Agents).LastOrDefault(a => a.Id == ExecutionGraphSupport.AgentNodeID(run, tool)) ?? throw new InvalidOperationException("no block for " + tool);
    private static object Result(string text, bool notice = false, object? usage = null, string? time = null)
    {
        var value = new Dictionary<string, object?> { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = text };
        if (notice) value["origin"] = new { kind = "task-notification" };
        if (usage is not null) value["usage"] = usage;
        if (time is not null) value["timestamp"] = time;
        return value;
    }
    private static object ToolResult(string tool, string text, bool error = false, object? structured = null, string key = "tool_use_result", string? parent = null)
    {
        var value = new Dictionary<string, object?> { ["type"] = "user", ["message"] = new { content = new object[] { new { type = "tool_result", tool_use_id = tool, content = text, is_error = error } } } };
        if (structured is not null) value[key] = structured;
        if (parent is not null) value["parent_tool_use_id"] = parent;
        return value;
    }
    private static object Notification(string tool, string task, string status = "completed", string summary = "Done") =>
        new { type = "system", subtype = "task_notification", task_id = task, tool_use_id = tool, status, summary, output_file = "/tmp/" + task + ".output" };

    // ── Who a block is (GraphAgentIdentityTests) ──

    internal static Task TheAgentCallTheLaunchResultTheCLIAndModsNameWhoABlockIs()
    {
        var pane = new Pane("run");
        pane.Send(Spawn("a", new { subagent_type = "Explore", model = "haiku", isolation = "worktree", description = "Map the repo", prompt = "Look around", run_in_background = true }));
        var block = Agent(pane, "a");
        Same(block.Identity, new GraphAgentIdentity("Explore", "haiku", null, "worktree"), "the call's type, model and isolation");
        Same(block.Background, true, "background");
        Same(block.Title, "Map the repo", "the description names the block");
        pane.Send(Spawn("b", new { description = "Plain", prompt = "Go" }));
        Same(Agent(pane, "b").Identity, null, "a call that says nothing records nothing");
        pane.Send(Spawn("c", new { subagent_type = "fork", prompt = "Go on" }));
        Same(Agent(pane, "c").Title, "fork", "a type alone titles the block");
        Same(Agent(pane, "c").Identity?.IsFork, true, "a fork");

        // The launch result names the model that runs; a foreground result the type and the worktree (the live key, then a transcript's).
        pane.Send(Spawn("bg", new { description = "Survey", prompt = "Go", model = "opus" }));
        pane.Send(ToolResult("bg", "Async agent launched successfully.\nagentId: a1", structured: new { isAsync = true, status = "async_launched", agentId = "a1", resolvedModel = "claude-opus-5-5" }));
        var background = Agent(pane, "bg");
        Same(background.Identity, new GraphAgentIdentity(null, "opus", "claude-opus-5-5"), "the launch's resolved model");
        Same(background.Background, true, "an asynchronous launch is a background agent");
        Same(background.Status, "running", "still running");
        pane.Send(Spawn("fg", new { description = "Count", prompt = "Go" }));
        pane.Send(ToolResult("fg", "3", key: "toolUseResult", structured: new { status = "completed", agentType = "general-purpose", resolvedModel = "claude-haiku-5-5", worktreePath = "/tmp/probe/.claude/worktrees/agent-1", worktreeBranch = "worktree-agent-1" }));
        var foreground = Agent(pane, "fg");
        Same(foreground.Identity, new GraphAgentIdentity("general-purpose", null, "claude-haiku-5-5", "worktree"), "a foreground result's type, model and worktree");
        Same(foreground.Status, "completed", "settled by its result");

        // task_started fills a type the call left unsaid and never replaces the call's own; a command's start is no agent's.
        pane.Send(Spawn("plain", new { description = "Plain", prompt = "Go" }));
        pane.Send(new { type = "system", subtype = "task_started", task_id = "t1", tool_use_id = "plain", subagent_type = "general-purpose", is_backgrounded = false, task_type = "local_agent" });
        Same(Agent(pane, "plain").Identity, new GraphAgentIdentity("general-purpose"), "the CLI's type");
        pane.Send(Spawn("named", new { subagent_type = "Explore", description = "Named", prompt = "Go" }));
        pane.Send(new { type = "system", subtype = "task_started", task_id = "t2", tool_use_id = "named", subagent_type = "other" });
        Same(Agent(pane, "named").Identity?.Type, "Explore", "the call's type stays");
        pane.Send(new { type = "assistant", uuid = "sh", message = new { id = "sh", content = new object[] { new { type = "tool_use", id = "shell", name = "Bash", input = new { command = "sleep 5", run_in_background = true } } } } });
        pane.Send(new { type = "system", subtype = "task_started", task_id = "t3", tool_use_id = "shell", subagent_type = "ghost", task_type = "local_bash" });
        Same(Agent(pane, "shell").Identity, null, "a command has no identity");
        Same(Agent(pane, "shell").Kind, "task", "a command is a task block");

        // Mods: the type, the requested model while it starts, the resolved one once it runs; a failed start names none that ran.
        var mods = new Pane("run");
        ModMetadata Mod(string phase, string? model) => new("main", "agent.spawn", Graph: new ModGraphMetadata(1, phase, phase == "starting" ? null : "agent-1", null, "m", "Review", "code-reviewer", model, "Review it"));
        mods.Tracker.ReceiveMod(Mod("starting", "sonnet"));
        Same(Agent(mods, "m").Identity, new GraphAgentIdentity("code-reviewer", "sonnet"), "while it starts");
        mods.Tracker.ReceiveMod(Mod("running", "claude-sonnet-5-5"));
        Same(Agent(mods, "m").Identity, new GraphAgentIdentity("code-reviewer", "sonnet", "claude-sonnet-5-5"), "once it runs");
        mods.Tracker.ReceiveMod(new ModMetadata("main", "agent.spawn", Graph: new ModGraphMetadata(1, "error", null, null, "denied", "Denied", "Explore", "opus", Output: "denied")));
        Same(Agent(mods, "denied").Identity, new GraphAgentIdentity("Explore"), "a failed start");
        return Task.CompletedTask;
    }

    internal static Task ANoticeRunsCopyCarriesTheIdentityAndOnlyASubagentKeepsOne()
    {
        var pane = new Pane("proc");
        pane.Send(Spawn("bg", new { subagent_type = "Explore", model = "haiku", description = "Survey", prompt = "Go", run_in_background = true }));
        pane.Send(ToolResult("bg", "Async agent launched successfully.\nagentId: a1", structured: new { isAsync = true, status = "async_launched", agentId = "a1", resolvedModel = "claude-haiku-5-5" }));
        pane.Send(Result("launched"));
        pane.Send(new { type = "system", subtype = "task_notification", task_id = "a1", tool_use_id = "bg", status = "completed", summary = "Done" });
        pane.Send(Result("done", notice: true));
        var runs = pane.Runs();
        Same(runs.Count, 2, "the request and its notice run");
        var expected = new GraphAgentIdentity("Explore", "haiku", "claude-haiku-5-5");
        Same(runs[0].Agents[0].Identity, expected, "the original");
        Same(runs[1].Agents[0].SourceBlockId, runs[0].Agents[0].Id, "the copy points at it");
        Same(runs[1].Agents[0].Identity, expected, "the copy is who its block is");

        // Bounded, and only a sub-agent block keeps one.
        var kept = ExecutionGraphSupport.Normalized(new ExecutionGraphNode("n", "r", null, "agent", "running", "t") { Identity = new(" multi\nline " + new string('x', 400), "two words", "claude-opus-5-5", "sandbox") })?.Identity;
        Check(kept is not null && !kept.Type!.Contains('\n') && System.Text.Encoding.UTF8.GetByteCount(kept.Type) <= 160, "a type on one line, bounded");
        Same(kept!.RequestedModel, null, "a model the CLI could not name");
        Same(kept.ResolvedModel, "claude-opus-5-5", "a model it could");
        Same(kept.Isolation, null, "an unknown isolation");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("n", "r", null, "agent", "running", "t") { Identity = new(RequestedModel: "a b") })?.Identity, null, "nothing left is none");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("n", "r", null, "task", "running", "t") { Identity = new("Explore") })?.Identity, null, "a task block has none");

        // Older saves decode; a block without one writes none; what is there travels.
        var old = JsonSerializer.Deserialize<ExecutionGraphNode>("""{"id":"n","runId":"r","kind":"agent","state":"completed","title":"Old","entries":[],"updatedAt":"2026-01-01T00:00:00Z"}""", Wire.Json)!;
        Same(old.Identity, null, "an old node");
        Check(!JsonSerializer.Serialize(old, Wire.Json).Contains("identity"), "an old node writes no identity");
        var agent = JsonSerializer.Deserialize<MightyGraphAgent>("""{"id":"a","title":"Old","input":"","status":"completed","entries":[]}""", Wire.Json)!;
        Same(agent.Identity, null, "an old agent");
        Check(!JsonSerializer.Serialize(agent, Wire.Json).Contains("identity") && !JsonSerializer.Serialize(agent, Wire.Json).Contains("figures"), "an old agent writes neither");
        var run = JsonSerializer.Deserialize<MightyGraphRun>("""{"id":"r","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[]}""", Wire.Json)!;
        Same(run.ConfiguredEffort, null, "an old run");
        Check(!JsonSerializer.Serialize(run, Wire.Json).Contains("configuredEffort") && !JsonSerializer.Serialize(run, Wire.Json).Contains("dvisor"), "an old run writes neither");
        agent.Identity = new GraphAgentIdentity("Explore", "haiku");
        var text = JsonSerializer.Serialize(agent, Wire.Json);
        Check(text.Contains("\"identity\":{") && text.Contains("\"type\":\"Explore\"") && !text.Contains("resolvedModel") && !text.Contains("isolation"), "only what is there: " + text);
        Same(JsonSerializer.Deserialize<MightyGraphAgent>(text, Wire.Json)!.Identity, agent.Identity, "it reads back");

        // A save keeps a sub-agent's identity and the run's effort, and nothing else.
        var budget = 1_000_000;
        var saved = MightyGraphSupport.Normalized([
            new MightyGraphRun { Id = "r", Status = "completed", ConfiguredEffort = "high", Agents = [
                new MightyGraphAgent { Id = "a", Title = "Agent", Status = "completed", Identity = new("Explore", "bad model", null, "worktree") },
                new MightyGraphAgent { Id = "t", Title = "Task", Status = "completed", Kind = "task", Identity = new("Explore") }] },
            new MightyGraphRun { Id = "s", Status = "completed", ConfiguredEffort = "extreme" }], true, ref budget);
        Same(saved[0].Agents[0].Identity, new GraphAgentIdentity("Explore", null, null, "worktree"), "the saved identity");
        Same(saved[0].Agents[1].Identity, null, "a task's");
        Same(saved[0].ConfiguredEffort, "high", "the saved effort");
        Same(saved[1].ConfiguredEffort, null, "a word that is no effort");

        // The run keeps the effort it started with; Settings › Models' effort for a pane left on Auto takes its place.
        var auto = new Pane("auto", "default");
        Same(auto.Runs()[0].ConfiguredEffort, "default", "the pane's effort");
        auto.Tracker.NoteEffort("medium");
        Same(auto.Runs()[0].ConfiguredEffort, "medium", "the effort it runs with");
        auto.Tracker.NoteEffort("not-a-level");
        Same(auto.Runs()[0].ConfiguredEffort, "medium", "a word that is no effort changes nothing");
        return Task.CompletedTask;
    }

    internal static Task TheRecordedSubagentsAreWhoTheCLISaid()
    {
        var runs = Replay("claude-stream-subagents", "recorded-a", "default");
        Same(runs[0].NoticeRun, null, "the first run is the request");
        Same(runs[0].FinalOutput, "launched.", "the request's answer");
        var agents = runs[0].Agents.Where(a => a.IsSubagent).ToList();
        SameList(agents.Select(a => a.Title), ["count a", "count b and c"], "the sub-agents");
        var expected = new GraphAgentIdentity("Explore", "haiku", "claude-haiku-5-5");
        SameList(agents.Select(a => a.Identity), [expected, expected], "who they are");
        SameList(agents.Select(a => a.Background == true), [false, true], "foreground, then background");
        Check(agents.All(a => a.Status == "completed"), "both ended");
        foreach (var agent in agents) SameList((agent.ResponseRecords ?? []).Select(r => r.Model).OfType<string>().Distinct(), ["claude-haiku-5-5"], "each block's own model");
        var notice = runs.LastOrDefault(run => run.NoticeRun == true && run.Agents.Any(a => a.SourceBlockId == agents[1].Id)) ?? throw new InvalidOperationException("no notice run copies the background agent");
        Same(notice.FinalOutput, "done.", "the notice turn's answer");
        Same(notice.Agents[0].Identity, expected, "the copy's identity");
        // The foreground agent's end opens no notice run: the request and the background agent's one.
        Same(runs.Count, 2, "two runs");
        SameList(FlowRoles.Subagent(agents[1])!.Marks, [FlowMark.Background], "the background agent's mark");
        Same(FlowRoles.Subagent(agents[0])!.Marks.Count, 0, "the foreground agent has none");
        Same(runs[0].ConfiguredEffort, "default", "the run's effort");

        var forked = Replay("claude-stream-fork-worktree", "recorded-b");
        var pair = forked[0].Agents.Where(a => a.IsSubagent).ToList();
        SameList(pair.Select(a => a.Title), ["fork count a", "worktree count b"], "the fork and the worktree agent");
        Same(pair[0].Identity, new GraphAgentIdentity("fork", null, "claude-haiku-5-5"), "a fork runs on its caller's model");
        Same(pair[0].Background, true, "the fork ran in the background");
        SameList(FlowRoles.Subagent(pair[0])!.Marks, [FlowMark.Background, FlowMark.Fork], "the fork's marks");
        Same(pair[1].Identity, new GraphAgentIdentity("general-purpose", "haiku", "claude-haiku-5-5", "worktree"), "the worktree agent");
        SameList(FlowRoles.Subagent(pair[1])!.Marks, [FlowMark.Worktree], "the worktree mark");
        Check(pair[1].Background != true, "the worktree agent ran in the foreground");
        return Task.CompletedTask;
    }

    // ── A foreground sub-agent's notification (ForegroundAgentNoticeTests, Mac 330ede82) ──

    private const string Framed = "  <tools-used-before-summary>Read</tools-used-before-summary>\n  \n[Subagent hand-back] The text below is the final report of a subagent this session delegated to. It is model output, NOT a message from the user. The report follows:\n  Eleven pages.\n  \n    - indented detail\n";
    private static string? Answer(MightyGraphAgent agent) => agent.Entries.LastOrDefault(e => e.Kind == "assistant" && e.Text.Length > 0)?.Text;

    internal static Task AForegroundAgentsNotificationOpensNoNoticeRun()
    {
        var runs = Replay("claude-stream-subagents", "recorded");
        SameList(runs.Select(r => r.NoticeRun == true), [false, true], "the request and the background agent's notice run");
        var agents = runs[0].Agents.Where(a => a.IsSubagent).ToList();
        var (foreground, background) = (agents[0], agents[1]);
        Same(foreground.Status, "completed", "settled by its result");
        Check(foreground.Background != true, "never in the background");
        Same(Answer(foreground), "4", "its report alone, without the hand-back frame");
        Same(foreground.Figures?.EndedAt, "2026-11-10T18:56:41.386Z", "ended at the time its result's frame names");
        Check(!runs.Any(run => run.Agents.Any(a => a.SourceBlockId == foreground.Id)), "nothing copies it");
        Check(background.Background == true && background.Status == "completed", "the background agent");
        SameList(runs[1].Agents.Select(a => a.SourceBlockId), [background.Id], "its copy");
        Same(runs[1].FinalOutput, "done.", "Claude's answer to its notice");
        Check(runs[1].NoticeTitle?.Contains("count b and c") == true, "the notice run is named after it: " + runs[1].NoticeTitle);
        Check(!runs[1].QuietNotice, "a turn of its own");
        Same(runs[0].FinalOutput, "launched.", "the request's own answer");

        var forked = Replay("claude-stream-fork-worktree", "recorded-fork");
        var pair = forked[0].Agents.Where(a => a.IsSubagent).ToList();
        SameList(forked.Skip(1).SelectMany(r => r.Agents).Select(a => a.SourceBlockId), [pair[0].Id], "only the fork is copied");
        Check(forked.Skip(1).All(r => r.NoticeRun == true), "the rest are notice runs");
        Same(pair[1].Status, "completed", "the worktree agent ended");
        Same(Answer(pair[1]), "3", "its report");

        // Built frames: the notification settles nothing; the result settles it with the report alone.
        var pane = new Pane("proc");
        pane.Send(Spawn("fg", new { description = "Count fg", prompt = "Count." }));
        pane.Send(Notification("fg", "t1", summary: "Eleven pages."));
        Same(pane.Runs().Count, 1, "no notice run");
        Same(Agent(pane, "fg", "proc").Status, "running", "still running");
        pane.Send(ToolResult("fg", Framed));
        pane.Send(Result("Eleven."));
        var built = pane.Finish();
        Same(built.Count, 1, "one run");
        Same(Answer(built[0].Agents[0]), "Eleven pages.\n\n  - indented detail", "the report, its indentation kept under the frame's two spaces");
        Same(built[0].FinalOutput, "Eleven.", "the request's answer");
        Check(MightyGraphLayout.HasResult(built[0]), "it has its result");
        Same(BackgroundNoticeRuns.AwaitingNotice(built[0], built), null, "it awaits nothing");

        // A failed foreground agent ends by its result too; the injected form of the notification is ignored the same.
        var failed = new Pane("proc");
        failed.Send(Spawn("fg", new { description = "Count fg", prompt = "Count.", run_in_background = false }));
        failed.Send(Notification("fg", "t1", "failed", "It broke"));
        Same(Agent(failed, "fg", "proc").Status, "running", "the notification settles nothing");
        failed.Send(ToolResult("fg", "The agent failed.", error: true));
        Same(Agent(failed, "fg", "proc").Status, "error", "its result does");
        Same(failed.Runs().Count, 1, "one run");
        var injected = new Pane("proc");
        injected.Send(Spawn("fg", new { description = "Count fg", prompt = "Count." }));
        injected.Send(new { type = "user", origin = new { kind = "task-notification" }, message = new { content = "<task-notification>\n<task-id>t1</task-id>\n<tool-use-id>fg</tool-use-id>\n<status>completed</status>\n<summary>Done</summary>\n</task-notification>" } });
        Same(injected.Runs().Count, 1, "no notice run from the injected form");
        Same(Agent(injected, "fg", "proc").Status, "running", "and nothing settled");

        // A background agent asked for, or launched asynchronously by the CLI, opens its notice run as before.
        var asked = new Pane("proc");
        asked.Send(Spawn("bg", new { description = "Count bg", prompt = "Count.", run_in_background = true }));
        asked.Send(ToolResult("bg", "Async agent launched successfully.\nagentId: a1"));
        asked.Send(Result("launched"));
        asked.Send(Notification("bg", "a1", summary: "Eleven pages."));
        asked.Send(Result("They are eleven.", notice: true));
        var askedRuns = asked.Runs();
        SameList(askedRuns.Select(r => r.NoticeRun == true), [false, true], "its notice run");
        Same(askedRuns[1].FinalOutput, "They are eleven.", "its answer");
        var auto = new Pane("proc");
        auto.Send(Spawn("text", new { description = "Count text", prompt = "Count." }));
        auto.Send(ToolResult("text", "Async agent launched successfully.\nagentId: a3"));
        auto.Send(Spawn("auto", new { description = "Count auto", prompt = "Count." }));
        auto.Send(ToolResult("auto", "Started.", structured: new { isAsync = true, status = "async_launched", agentId = "a2" }));
        auto.Send(Result("launched"));
        auto.Send(Notification("text", "a3"));
        auto.Send(Notification("auto", "a2", summary: "Done too"));
        auto.Send(Result("Noted.", notice: true));
        var autoRuns = auto.Runs();
        Check(autoRuns[0].Agents.All(a => a.Background == true && a.Status == "completed"), "both are background agents that ended");
        Same(autoRuns.Count, 2, "one notice run for both");
        SameList(autoRuns[1].Agents.Select(a => a.SourceBlockId).Order(StringComparer.Ordinal), autoRuns[0].Agents.Select(a => a.Id).Order(StringComparer.Ordinal), "both copies");
        return Task.CompletedTask;
    }

    internal static Task AResultIsTheReportUnderItsHandBackFrameOnly()
    {
        Same(ExecutionGraphTracker.Report("There are eleven help pages."), "There are eleven help pages.", "a result without the frame");
        Same(ExecutionGraphTracker.Report(Framed), "Eleven pages.\n\n  - indented detail", "the framed report");
        const string quoting = "The agent wrote [Subagent hand-back] in its notes. The report follows: nothing.";
        Same(ExecutionGraphTracker.Report(quoting), quoting, "the frame's words inside a sentence");
        const string unmarked = "[Subagent hand-back] Something else entirely.\n  body";
        Same(ExecutionGraphTracker.Report(unmarked), unmarked, "a frame without its marker");
        const string empty = "[Subagent hand-back] The report follows:\n";
        Same(ExecutionGraphTracker.Report(empty), empty, "a frame without a report");
        Same(ExecutionGraphTracker.Report("[Subagent hand-back] The report follows:\n  ok"), "ok", "the frame may open the text");
        const string trailed = "[Subagent hand-back] Preamble. The report follows:\r\n  first\r\n\r\n  second\r\nagentId: a1 (use SendMessage)\nworktreePath: /tmp/w\n<usage>subagent_tokens: 10\ntool_uses: 1</usage>";
        Same(ExecutionGraphTracker.Report(trailed), "first\n\nsecond", "the harness's own lines are no part of it");
        return Task.CompletedTask;
    }

    // ── The tool calls a response names (ResponseActivityIdsTests, Mac b5480f80) ──

    private static readonly object Usage = new Dictionary<string, object> { ["input_tokens"] = 100, ["output_tokens"] = 20, ["cache_read_input_tokens"] = 1_000 };
    private static object Message(string id, object[] blocks, string uuid, object? usage = null, string? parent = null)
    {
        var value = new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = uuid, ["session_id"] = "main", ["message"] = new { id, model = "claude-haiku-5-5", content = blocks, usage = usage ?? Usage } };
        if (parent is not null) value["parent_tool_use_id"] = parent;
        return value;
    }
    private static object Tool(string id, string name = "Read", object? input = null) => new { type = "tool_use", id, name, input = input ?? new { file_path = "a.txt" } };
    private static readonly object Thinking = new { type = "thinking", thinking = "" };

    internal static Task AResponseRecordNamesEveryToolCallItsMessageMade()
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("run", "request", "claude", null, emitted.Add);
        void Send(object value) => tracker.Consume(JsonSerializer.SerializeToElement(value));
        ExecutionGraphNode Main() => emitted.Last(n => n.Kind == "main");
        Send(Message("msg-1", [Thinking], "u1"));
        SameList(Main().ResponseRecords!.Select(r => string.Join(",", r.ActivityIds)), [""], "a thinking event names nothing");
        Send(Message("msg-1", [Tool("read-a")], "u2"));
        Send(Message("msg-1", [Tool("read-b")], "u3"));
        Same(Main().ResponseRecords!.Count, 1, "one record");
        SameList(Main().ResponseRecords![0].ActivityIds, ["read-a", "read-b"], "every call, in order");
        Same(Main().ResponseRecords![0].Model, "claude-haiku-5-5", "its model");
        Same(Main().Usage, new GraphTokenUsage(100, 20, 1_000), "counted once");
        var count = emitted.Count;
        Send(Message("msg-1", [Tool("read-b")], "u3"));
        Same(emitted.Count, count, "the same event again says nothing");

        var second = new List<ExecutionGraphNode>();
        var again = new ExecutionGraphTracker("run", "request", "claude", null, second.Add);
        void Again(object value) => again.Consume(JsonSerializer.SerializeToElement(value));
        Again(Message("msg-1", [Tool("read-a"), Tool("read-b")], "u1"));
        Again(Message("msg-1", [Tool("read-a"), Tool("read-b"), Tool("read-c")], "u1", new Dictionary<string, object> { ["input_tokens"] = 150, ["output_tokens"] = 60 }));
        SameList(second.Last(n => n.Kind == "main").ResponseRecords![0].ActivityIds, ["read-a", "read-b", "read-c"], "a larger figure with one more call");
        Same(second.Last(n => n.Kind == "main").Usage, new GraphTokenUsage(150, 60), "at its latest figure");
        Again(Message("msg-2", [Thinking], "u4"));
        Again(Message("msg-2", [Tool("grep")], "u5"));
        SameList(second.Last(n => n.Kind == "main").ResponseRecords!.Select(r => string.Join(",", r.ActivityIds)), ["read-a,read-b,read-c", "grep"], "the next message is its own record");

        // An Agent call after its message's thinking finds its child block; the second call of a message reads as the same response.
        var third = new List<ExecutionGraphNode>();
        var agents = new ExecutionGraphTracker("run", "request", "claude", null, third.Add);
        void Agents(object value) => agents.Consume(JsonSerializer.SerializeToElement(value));
        Agents(Message("msg-1", [Thinking], "u1"));
        Agents(Message("msg-1", [Tool("agent-a", "Agent", new { description = "Count", prompt = "Go" })], "u2"));
        var childUsage = new Dictionary<string, object> { ["input_tokens"] = 7, ["output_tokens"] = 3 };
        Agents(Message("child-1", [Thinking], "c1", childUsage, "agent-a"));
        Agents(Message("child-1", [Tool("child-read")], "c2", childUsage, "agent-a"));
        var root = third.Last(n => n.Kind == "main");
        var child = third.Last(n => n.Id == ExecutionGraphSupport.AgentNodeID("run", "agent-a"));
        SameList(root.ResponseRecords![0].ActivityIds, ["agent-a"], "the Agent call");
        SameList(child.ResponseRecords![0].ActivityIds, ["child-read"], "the child's own call");
        var blocks = GraphChildBlocks.Map(root.ResponseRecords, [new MightyGraphAgent { Id = child.Id, Title = child.Title, Status = child.State, Usage = child.Usage, ResponseRecords = child.ResponseRecords }], "run");
        Same(blocks.GetValueOrDefault("agent-a")?.Usage, new GraphTokenUsage(7, 3), "the Agent row's child block");

        var runs = Replay("claude-stream-subagents", "recorded");
        SameList((runs[0].ResponseRecords ?? []).SelectMany(r => r.ActivityIds), ["toolu_1", "toolu_3"], "every recorded message names its Agent call");
        var background = runs[0].Agents.First(a => a.Title == "count b and c");
        SameList((background.ResponseRecords ?? []).Select(r => string.Join(",", r.ActivityIds)).Where(s => s.Length > 0), ["toolu_4", "toolu_5"], "the background agent's reads");
        return Task.CompletedTask;
    }

    // ── A background command's figures (FlowTaskTests) ──

    internal static Task TheRecordedCommandsKeepTheirStartEndAndExitCode()
    {
        var runs = Replay("claude-stream-background-commands", "recorded");
        var tasks = runs[0].Agents.Where(a => a.IsTask).ToList();
        SameList(tasks.Select(a => a.Title), ["count to three", "fail on purpose"], "the commands");
        SameList(tasks.Select(a => a.Input), ["for i in 1 2 3; do echo line $i; sleep 1; done", "sleep 2; echo about to fail; exit 3"], "their command lines");
        Check(tasks.All(a => a.Background == true), "both in the background");
        SameList(tasks.Select(a => a.Status), ["completed", "error"], "how they ended");
        SameList(tasks.Select(a => a.Figures?.StartedAt), ["2026-11-11T02:08:51.382Z", "2026-11-11T02:08:51.646Z"], "started when the Bash call's frame was written");
        SameList(tasks.Select(a => a.Figures?.EndedAt), ["2026-11-11T02:08:54.545Z", "2026-11-11T02:08:53.760Z"], "ended when task_updated timed it");
        SameList(tasks.Select(a => a.Figures?.ExitCode), [0, 3], "the exit codes the notifications name");
        SameList(tasks.Select(a => a.Figures?.ToolCalls), [null, null], "no tool calls");
        var copies = runs.Where(r => r.NoticeRun == true).SelectMany(r => r.Agents).Where(a => a.IsTask).ToList();
        SameList(copies.Select(a => a.SourceBlockId).OfType<string>().Order(StringComparer.Ordinal), tasks.Select(a => a.Id).Order(StringComparer.Ordinal), "both copied");
        foreach (var copy in copies) Same(copy.Figures, tasks.First(t => t.Id == copy.SourceBlockId).Figures, "a copy carries the same figures");
        return Task.CompletedTask;
    }

    internal static Task ASubagentCountsItsToolCallsOnceAndKeepsItsStartAndEnd()
    {
        var runs = Replay("claude-stream-subagents", "recorded");
        var agents = runs[0].Agents.Where(a => a.IsSubagent).ToList();
        // One read each from their own messages, and the CLI's own count (task_progress / task_notification) when that is more.
        SameList(agents.Select(a => a.Figures?.ToolCalls), [1, 2], "tool calls");
        Check(agents.All(a => a.Figures?.Started is not null && a.Figures?.Ended is not null && a.Figures.Ended >= a.Figures.Started), "start and end");
        Same(agents[1].Figures?.StartedAt, runs[0].Agents[1].Figures?.StartedAt, "kept");
        // A figure is a time the app can read back and a count in range.
        Same(new GraphAgentFigures("yesterday", "2026-01-01T00:00:00Z", -1).Normalized, new GraphAgentFigures(null, "2026-01-01T00:00:00Z"), "normalized");
        Same(new GraphAgentFigures().Normalized, null, "none");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("n", "r", null, "steer", "running", "t") { Figures = new("2026-01-01T00:00:00Z") })?.Figures, null, "only a sub-agent's or a task's");
        return Task.CompletedTask;
    }

    // ── The advisor (GraphAdvisorTests) ──

    private static object Advised(object[] content, string id, string? parent = null, string? time = null)
    {
        var value = new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = Guid.NewGuid().ToString(), ["message"] = new { id, model = "claude-sonnet-5-5", content } };
        if (parent is not null) value["parent_tool_use_id"] = parent;
        if (time is not null) value["timestamp"] = time;
        return value;
    }
    private static object Asked(string call, string message = "m1", string? parent = null, string? time = null) =>
        Advised([new { type = "server_tool_use", id = call, name = "advisor", input = new { } }], message, parent, time);
    private static object Answered(string call, object content, string message = "m1", string? parent = null, string? time = null) =>
        Advised([new { type = "advisor_tool_result", tool_use_id = call, content }], message, parent, time);
    private static object Used(string tool, string name = "Read", string message = "m1", string? parent = null) =>
        Advised([new { type = "tool_use", id = tool, name, input = new { } }], message, parent);
    private static readonly object Redacted = new { type = "advisor_redacted_result", encrypted_content = "encrypted" };
    private static object AdvisorMessage(int input, int output, string model = "claude-opus-5-5") =>
        new { type = "advisor_message", model, input_tokens = input, output_tokens = output, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 };
    private static List<GraphAdvisorCall> Calls(Pane pane) => [.. pane.Runs()[0].Advisor?.Consultations ?? []];

    internal static Task TheRecordedConsultationIsKeptOnTheRunAndMakesNoBlock()
    {
        var runs = Replay("claude-stream-advisor", "recorded");
        Same(runs.Count, 1, "one run");
        var advisor = runs[0].Advisor ?? throw new InvalidOperationException("no advisor record");
        Check(advisor.Consultations.Count == 1 && advisor.Count == 1 && advisor.Total is null, "one consultation");
        var call = advisor.Consultations[0];
        Same(call.Id, "srvtoolu_1", "its id");
        Same(call.Caller, null, "the main session asked");
        Same(call.StartedAt, "2026-11-10T18:16:51.658Z", "asked at its frame's time");
        Same(call.EndedAt, "2026-11-10T18:16:59.540Z", "answered at its result's");
        Check(call.State == "done" && call.Redacted == true && call.Text is null && call.ErrorCode is null, "an encrypted reply, no text");
        Check(call.BeforeChange == true && call.Followed is null, "before any change, and the answer came next");
        Same(advisor.Model, "claude-opus-5-5", "the advisor's model");
        Same(advisor.Usage, new GraphTokenUsage(34_217, 570), "what its own message cost");
        Same(runs[0].Agents.Count, 0, "no block");
        Check(runs[0].Status == "completed" && runs[0].FinalOutput == "No", "the request as it would be");
        SameList((runs[0].ResponseRecords ?? []).Select(r => r.Model), ["claude-sonnet-5-5"], "the main session's own tokens do not count the advisor's");
        return Task.CompletedTask;
    }

    internal static Task TheAdvisorsResultsTheWorkAroundItAndWhoAsked()
    {
        var pane = new Pane("advised");
        pane.Send(Asked("srvtoolu_a", time: "2026-11-10T10:00:00Z"));
        SameList(Calls(pane).Select(c => c.State), ["consulting"], "consulting");
        pane.Send(Answered("srvtoolu_a", new { type = "advisor_result", text = "Check the cache key first.", stop_reason = "end_turn" }, time: "2026-11-10T10:00:07Z"));
        pane.Send(Asked("srvtoolu_b", "m2"));
        pane.Send(Answered("srvtoolu_b", new { type = "advisor_result", text = "", stop_reason = "refusal" }, "m2"));
        pane.Send(Asked("srvtoolu_c", "m3"));
        pane.Send(Answered("srvtoolu_c", new { type = "advisor_tool_result_error", error_code = "max_uses_exceeded" }, "m3"));
        pane.Send(Asked("srvtoolu_d", "m4"));
        pane.Send(Answered("srvtoolu_d", new { type = "advisor_tool_result_error" }, "m4"));
        pane.Send(Asked("srvtoolu_e", "m5"));
        pane.Send(Answered("srvtoolu_e", new { type = "advisor_some_later_result", payload = "x" }, "m5"));
        pane.Send(Answered("srvtoolu_zz", Redacted));
        pane.Send(Answered("srvtoolu_a", Redacted));
        var calls = Calls(pane);
        SameList(calls.Select(c => c.Id), ["srvtoolu_a", "srvtoolu_b", "srvtoolu_c", "srvtoolu_d", "srvtoolu_e"], "the consultations");
        SameList(calls.Select(c => c.State), ["done", "declined", "error", "error", "done"], "how they ended");
        Check(calls[0].Text == "Check the cache key first." && calls[0].Redacted is null, "the advice as text");
        Check(calls[0].StartedAt == "2026-11-10T10:00:00Z" && calls[0].EndedAt == "2026-11-10T10:00:07Z", "its times");
        Check(calls[1].Text is null && calls[2].ErrorCode == "max_uses_exceeded" && calls[3].ErrorCode is null, "a refusal and the errors");
        Check(calls[4].Text is null && calls[4].Redacted is null && calls[4].ErrorCode is null, "a shape the app does not know");
        SameList(calls.Select(c => c.Followed == true), [true, true, true, true, false], "asking again is going on");

        // Bounded advice; another server tool is not the advisor; an id that is no identifier is not kept.
        var bounded = new Pane("advised");
        bounded.Send(Asked("srvtoolu_a"));
        bounded.Send(Answered("srvtoolu_a", new { type = "advisor_result", text = new string('가', 4_000) }));
        Check(System.Text.Encoding.UTF8.GetByteCount(Calls(bounded)[0].Text!) <= GraphAdvisor.MaximumTextBytes, "bounded");
        bounded.Send(Advised([new { type = "server_tool_use", id = "srvtoolu_web", name = "web_search", input = new { } }], "m2"));
        bounded.Send(Asked("bad id", "m3"));
        SameList(Calls(bounded).Select(c => c.Id), ["srvtoolu_a"], "only advisor calls");

        // Before a file change, and whether work followed.
        var work = new Pane("advised");
        work.Send(Used("toolu_read"));
        work.Send(Asked("srvtoolu_a", "m2"));
        work.Send(Answered("srvtoolu_a", Redacted, "m2"));
        Check(Calls(work)[0].BeforeChange == true && Calls(work)[0].Followed is null, "before any change");
        work.Send(Used("toolu_edit", "Edit", "m3"));
        Same(Calls(work)[0].Followed, true, "work followed");
        work.Send(Asked("srvtoolu_b", "m4"));
        work.Send(Answered("srvtoolu_b", Redacted, "m4"));
        SameList(Calls(work).Select(c => c.BeforeChange), [true, null], "after the edit");
        work.Send(Advised([new { type = "text", text = "Done." }], "m4"));
        work.Send(Result("Done."));
        SameList(Calls(work).Select(c => c.Followed), [true, null], "the answer leaves it the last step");

        // One frame with several blocks is read in its order.
        var frame = new Pane("advised");
        frame.Send(Advised([new { type = "tool_use", id = "toolu_1", name = "Write", input = new { file_path = "/tmp/a" } },
            new { type = "server_tool_use", id = "srvtoolu_a", name = "advisor", input = new { } }, new { type = "advisor_tool_result", tool_use_id = "srvtoolu_a", content = Redacted },
            new { type = "server_tool_use", id = "srvtoolu_b", name = "advisor", input = new { } }, new { type = "advisor_tool_result", tool_use_id = "srvtoolu_b", content = Redacted }], "m1"));
        SameList(Calls(frame).Select(c => c.BeforeChange), [null, null], "after the write");
        SameList(Calls(frame).Select(c => c.Followed), [true, null], "the second asked after the first");

        // A sub-agent's consultation names its block and is kept on the request; the turn's end stops the main session's.
        var who = new Pane("advised");
        who.Send(Advised([new { type = "tool_use", id = "toolu_agent", name = "Agent", input = new { description = "scout", prompt = "Look.", subagent_type = "Explore" } }], "m1"));
        who.Send(Asked("srvtoolu_main", "m2"));
        who.Send(Asked("srvtoolu_child", "c1", "toolu_agent"));
        who.Send(Answered("srvtoolu_child", Redacted, "c1", "toolu_agent"));
        var scout = who.Runs()[0].Agents.First(a => a.IsSubagent);
        SameList(Calls(who).Select(c => c.Caller), [null, scout.Id], "who asked");
        SameList(Calls(who).Select(c => c.State), ["consulting", "done"], "their states");
        who.Send(Used("toolu_grep", "Grep", "c2", "toolu_agent"));
        SameList(Calls(who).Select(c => c.Followed), [null, true], "each caller's work is its own");
        who.Send(Result("stopped early"));
        SameList(Calls(who).Select(c => c.State), ["stopped", "done"], "the turn ended first");

        // A notice turn's consultation is kept on its own run, each turn's result gives its own run the cost.
        var notice = new Pane("advised");
        notice.Send(Advised([new { type = "tool_use", id = "toolu_bg", name = "Agent", input = new { description = "count", prompt = "Count.", run_in_background = true } }], "m1"));
        notice.Send(ToolResult("toolu_bg", "Async agent launched successfully.\nagentId: agent1", structured: new { isAsync = true, status = "async_launched", agentId = "agent1" }));
        notice.Send(Asked("srvtoolu_request", "m2"));
        notice.Send(Answered("srvtoolu_request", Redacted, "m2"));
        notice.Send(Result("launched.", usage: new { input_tokens = 4, output_tokens = 9, iterations = new[] { AdvisorMessage(1_000, 50) } }));
        notice.Send(new { type = "system", subtype = "task_notification", task_id = "agent1", tool_use_id = "toolu_bg", status = "completed", summary = "Done" });
        notice.Send(Asked("srvtoolu_notice", "m3"));
        notice.Send(Answered("srvtoolu_notice", Redacted, "m3"));
        notice.Send(Result("done.", notice: true, usage: new { input_tokens = 4, output_tokens = 9, iterations = new[] { AdvisorMessage(2_000, 70) } }));
        var noticeRuns = notice.Finish();
        SameList(noticeRuns.Select(r => r.NoticeRun == true), [false, true], "the request and its notice run");
        SameList(noticeRuns[0].Advisor!.Consultations.Select(c => c.Id), ["srvtoolu_request"], "the request's");
        SameList(noticeRuns[1].Advisor!.Consultations.Select(c => c.Id), ["srvtoolu_notice"], "the notice turn's");
        Same(noticeRuns[0].Advisor!.Usage, new GraphTokenUsage(1_000, 50), "the request's cost");
        Same(noticeRuns[1].Advisor!.Usage, new GraphTokenUsage(2_000, 70), "the notice turn's cost");
        Same(noticeRuns[1].Advisor!.Model, "claude-opus-5-5", "its model");

        // Still out when the process ends: stopped.
        var ended = new Pane("advised");
        ended.Send(Advised([new { type = "tool_use", id = "toolu_agent", name = "Agent", input = new { description = "scout", prompt = "Look." } }], "m1"));
        ended.Send(Asked("srvtoolu_child", "c1", "toolu_agent", "2026-11-10T10:00:00Z"));
        var stopped = ended.Finish("stopped")[0].Advisor!.Consultations[0];
        Check(stopped.State == "stopped" && stopped.EndedAt is not null, "stopped with its end");
        return Task.CompletedTask;
    }

    internal static Task TheAdvisorsTokensModelBoundsAndSaves()
    {
        var plain = new Pane("advised");
        plain.Send(Result("plain", usage: new { input_tokens = 4, output_tokens = 9, iterations = new object[] { new { type = "message", input_tokens = 9, output_tokens = 9 } } }));
        Same(plain.Runs()[0].Advisor, null, "a result without advisor messages adds nothing");
        var summed = new Pane("advised");
        summed.Send(Result("advised", usage: new { input_tokens = 4, output_tokens = 9, iterations = new[] { AdvisorMessage(100, 10), new { type = "message", model = "x", input_tokens = 9, output_tokens = 9, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 }, AdvisorMessage(300, 30) } }));
        var advisor = summed.Runs()[0].Advisor!;
        Same(advisor.Usage, new GraphTokenUsage(400, 40), "the advisor's messages alone");
        Check(advisor.Model == "claude-opus-5-5" && advisor.Consultations.Count == 0 && advisor.Count == 0, "its model and no consultation");

        // A transcript names the advisor and its tokens on the message itself, each message once; and the larger of the two sources counts.
        object Line(object[] content, string id, string time, string? model, object[] iterations)
        {
            var value = new Dictionary<string, object?> { ["type"] = "assistant", ["uuid"] = Guid.NewGuid().ToString(), ["timestamp"] = time, ["requestedModel"] = "claude-sonnet-5-5",
                ["message"] = new { id, model = "claude-sonnet-5-5", content, usage = new { input_tokens = 4, output_tokens = 177, cache_read_input_tokens = 43_694, iterations } } };
            if (model is not null) value["advisorModel"] = model;
            return value;
        }
        var iterations = new object[] { new { type = "message", input_tokens = 2, output_tokens = 174 }, AdvisorMessage(34_217, 570), new { type = "message", input_tokens = 2, output_tokens = 3 } };
        var transcript = new Pane("transcript");
        transcript.Send(Line([new { type = "thinking", thinking = "" }], "msg_1", "2026-11-10T18:16:51.656Z", "claude-opus-5-5", iterations));
        transcript.Send(Line([new { type = "server_tool_use", id = "srvtoolu_1", name = "advisor", input = new { } }], "msg_1", "2026-11-10T18:16:51.658Z", "claude-opus-5-5", iterations));
        transcript.Send(Line([new { type = "advisor_tool_result", tool_use_id = "srvtoolu_1", content = Redacted }], "msg_1", "2026-11-10T18:16:59.540Z", "claude-opus-5-5", iterations));
        transcript.Send(Line([new { type = "text", text = "No" }], "msg_1", "2026-11-10T18:16:59.541Z", "claude-opus-5-5", iterations));
        var read = transcript.Finish()[0].Advisor!;
        Check(read.Model == "claude-opus-5-5" && read.Usage == new GraphTokenUsage(34_217, 570), "counted once");
        Check(read.Consultations.Count == 1 && read.Consultations[0].State == "done" && read.Consultations[0].EndedAt == "2026-11-10T18:16:59.540Z", "the consultation");
        var unasked = new Pane("unasked");
        unasked.Send(Line([new { type = "text", text = "Done." }], "msg_1", "2026-11-10T18:17:00Z", "claude-opus-5-5", [new { type = "message", input_tokens = 2, output_tokens = 3 }]));
        Same(unasked.Runs()[0].Advisor, new GraphAdvisor("claude-opus-5-5", null, []), "on, never asked");
        var none = new Pane("none");
        none.Send(Line([new { type = "text", text = "Done." }], "msg_1", "2026-11-10T18:17:00Z", null, [new { type = "message", input_tokens = 2, output_tokens = 3 }]));
        Same(none.Runs()[0].Advisor, null, "off says nothing");
        var twice = new Pane("advised");
        twice.Send(Line([new { type = "text", text = "a" }], "msg_1", "2026-11-10T10:00:00Z", "claude-opus-5-5", [AdvisorMessage(100, 10)]));
        twice.Send(Line([new { type = "text", text = "b" }], "msg_2", "2026-11-10T10:00:01Z", "claude-opus-5-5", [AdvisorMessage(300, 30)]));
        twice.Send(Line([new { type = "text", text = "b2" }], "msg_2", "2026-11-10T10:00:02Z", "claude-opus-5-5", [AdvisorMessage(320, 32)]));
        Same(twice.Runs()[0].Advisor!.Usage, new GraphTokenUsage(420, 42), "a message at its newest figure");
        twice.Send(Result("done", usage: new { input_tokens = 4, output_tokens = 9, iterations = new[] { AdvisorMessage(420, 42) } }));
        Same(twice.Runs()[0].Advisor!.Usage, new GraphTokenUsage(420, 42), "the result says the same: not counted twice");

        // The newest consultations are kept, the count says all.
        var many = new Pane("advised");
        for (var index = 0; index < 40; index++) { many.Send(Asked("srvtoolu_" + index, "m" + index)); many.Send(Answered("srvtoolu_" + index, Redacted, "m" + index)); }
        var kept = many.Runs()[0].Advisor!;
        Check(kept.Consultations.Count == GraphAdvisor.MaximumCalls && kept.Consultations[0].Id == "srvtoolu_8" && kept.Consultations[^1].Id == "srvtoolu_39", "the newest 32");
        Check(kept.Count == 40 && kept.Total == 40, "the count says all");

        // A saved record is bounded and a restored consultation still out is stopped.
        const string time = "2026-11-10T10:00:00Z";
        var record = new GraphAdvisor("not a model", new GraphTokenUsage(5), [
            new("srvtoolu_a", StartedAt: time, State: "consulting", Redacted: false, BeforeChange: false), new("srvtoolu_a", State: "done"), new("bad id", State: "done"),
            new("srvtoolu_b", "not an id", State: "done"), new("srvtoolu_c", State: "thinking"),
            new("srvtoolu_d", "graph-abc", "yesterday", time, "error", Text: "", ErrorCode: "overloaded\nplease", Followed: true)]);
        var normal = record.Normalized() ?? throw new InvalidOperationException("nothing kept");
        Same(normal.Model, null, "a model that is no name");
        SameList(normal.Consultations.Select(c => c.Id), ["srvtoolu_a", "srvtoolu_d"], "what is kept");
        Same(normal.Consultations[0], new GraphAdvisorCall("srvtoolu_a", StartedAt: time, State: "consulting"), "the first");
        Check(normal.Consultations[1].StartedAt is null && normal.Consultations[1].EndedAt == time && normal.Consultations[1].Text is null, "the second's times and text");
        Check(normal.Consultations[1].ErrorCode == "overloaded please" && normal.Consultations[1].Followed == true && normal.Consultations[1].Caller == "graph-abc", "its error and caller");
        Check(normal.Total == 6 && normal.Count == 6, "six seen");
        SameList(record.Normalized(true)!.Consultations.Select(c => c.State), ["stopped", "error"], "restored");
        Same(new GraphAdvisor().Normalized(), null, "nothing");
        // The record always writes its calls, as the Mac's decoder needs them, and one written without them reads back with none.
        Check(JsonSerializer.Serialize(new GraphAdvisor("claude-opus-5-5"), Wire.Json).Contains("\"calls\":[]"), "an advisor without consultations writes an empty list");
        Same(JsonSerializer.Deserialize<GraphAdvisor>("""{"model":"claude-opus-5-5"}""", Wire.Json)!.Calls?.Count, 0, "an advisor read without calls has none");
        var run = new MightyGraphRun { Id = "r1", Input = "go", Status = "running", Provider = "claude", Advisor = record, ConfiguredAdvisor = "opus" };
        var decoded = JsonSerializer.Deserialize<MightyGraphRun>(JsonSerializer.Serialize(run, Wire.Json), Wire.Json)!;
        Check(Equals(decoded.Advisor, record) && decoded.ConfiguredAdvisor == "opus", "a run carries it through a save");
        var budget = 1_000_000;
        var restored = MightyGraphSupport.Normalized([run], true, ref budget)[0];
        Check(restored.Advisor!.Consultations.Select(c => c.State).SequenceEqual(["stopped", "error"]) && restored.ConfiguredAdvisor == "opus", "restored");
        run.ConfiguredAdvisor = "not a model";
        Same(MightyGraphSupport.Normalized([run], false, ref budget)[0].ConfiguredAdvisor, null, "a configured advisor that is no name");
        var advised = new MightyGraphRun { Id = "r3", Input = "go", Status = "completed", Provider = "claude", Advisor = new GraphAdvisor(Calls: [new("srvtoolu_t", State: "done", Text: new string('x', 900))]) };
        var small = 1_200;
        Same(MightyGraphSupport.Normalized([advised], false, ref small)[0].Advisor, null, "no room: the record goes whole");
        var roomy = 100_000;
        Same(MightyGraphSupport.Normalized([advised], false, ref roomy)[0].Advisor?.Consultations[0].Text?.Length, 900, "room: kept whole");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("graph-a", "r1", "graph-m", "agent", "running", "a") { Advisor = record })?.Advisor, null, "only a main block keeps one");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("graph-m", "r1", null, "main", "running", "Claude") { Advisor = record })?.Advisor, normal, "a main block's");

        // A request remembers the advisor it was started with, and its notice runs do too; another agent has none.
        Same(new Pane("r1", null, "opus").Runs()[0].ConfiguredAdvisor, "opus", "the pane's model");
        Same(new Pane("r2", null, "not a model").Runs()[0].ConfiguredAdvisor, null, "no name");
        Same(new ExecutionGraphTracker("c1", "go", "codex", null, _ => { }, null, "opus").BuildRun(true)!.ConfiguredAdvisor, null, "another agent");
        var learned = new Pane("r3");
        learned.Tracker.NoteAdvisor("sonnet");
        Same(learned.Runs()[0].ConfiguredAdvisor, "sonnet", "learned once Claude's settings were read");
        learned.Tracker.NoteAdvisor("not a model");
        Same(learned.Runs()[0].ConfiguredAdvisor, null, "a value that is no model");
        return Task.CompletedTask;
    }
}
