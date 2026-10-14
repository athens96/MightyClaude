using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// One launch of the app, headless: the real <see cref="DesktopService"/> on a profile in a temp folder, its real
/// <see cref="RunManager"/> starting the scripted <see cref="DelegationFakeClaude"/>, and the real <see cref="AgentIOPipe"/> its
/// runs reach the delegation server's calls back through. The switch and the worktree root are injected: nothing here reads the
/// registry or touches <c>%USERPROFILE%\.mightyclaude</c>. It records what the checks judge: each pane's runs in order with
/// how they ended and what they were given, and every permission card.
/// </summary>
internal sealed class DelegationLaunch : IAsyncDisposable
{
    internal const string Parent = "parent";
    private readonly object gate = new();
    private readonly Dictionary<string, List<string>> runs = [];
    private readonly Dictionary<string, string> ends = [], inputs = [], going = [];
    private readonly List<ToolPermissionRequest> cards = [];
    private bool disposed;
    /// <summary>The hidden switch as this launch reads it, at each run start and each call.</summary>
    internal volatile bool SwitchOn;
    /// <summary>The binding each pane's newest run got.</summary>
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentIOBinding> Bindings = [];
    internal DesktopService Service { get; }
    internal AgentIOPipe Pipe { get; }
    /// <summary>Where the fake Claude reads its scripts and writes its logs and transcripts.</summary>
    internal string FakeRoot { get; }
    internal string Profile { get; }
    internal string Repo { get; }
    internal Workspace Workspace { get; private set; } = new();
    internal DelegationCoordinator Coordinator => Service.Delegation ?? throw new InvalidOperationException("delegation is unavailable");
    internal DelegationFile File => new DelegationFileStore(Profile).Load();

    private DelegationLaunch(string profile, string fakeRoot, string repo, string worktrees, bool switchOn)
    {
        Profile = profile; FakeRoot = fakeRoot; Repo = repo; SwitchOn = switchOn;
        var plugin = Path.Combine(fakeRoot, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        System.IO.File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
        var catalog = new ProviderCatalog((provider, _) => Task.FromResult<CliCommand?>(provider == "claude" ? Verification.Self("--fake-claude", fakeRoot) : provider == "codex" ? Verification.Self("--fake-cli", "codex", Path.Combine(fakeRoot, "codex")) : null));
        Service = new(profile, null, plugin, catalog, () => SwitchOn, new ChildWorktreeMaker(worktrees, _ => 50_000_000_000));
        Pipe = new((_, _, _, _) => throw new InvalidOperationException("No terminal calls are made here."));
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable");
        Service.ConfigureAgentIO((request, workspace) => Bindings[request.SessionId] = Pipe.Bindings.Bind(request.SessionId, workspace, request.Provider, Pipe.Name, executable));
        Service.ToolPermissionChanged += card => { lock (gate) cards.Add(card); };
        Service.RequestStarting += request => { lock (gate) inputs[request.SessionId + "/" + Service.RunId(request.SessionId)] = request.Input; };
        // A run's id is read as it starts, when no other run of the pane can begin; its end is labelled with the id its start
        // carried, never with whatever run the pane has by the time the end arrives.
        Service.RunEventReceived += value =>
        {
            if (value.Type != "status") return;
            lock (gate)
            {
                if (value.Status == "running" && Service.RunId(value.SessionId) is { } runId)
                {
                    if (!runs.TryGetValue(value.SessionId, out var list)) runs[value.SessionId] = list = [];
                    if (!list.Contains(runId)) list.Add(runId);
                    going[value.SessionId] = runId;
                }
                else if (value.Status is "completed" or "stopped" or "error" && going.Remove(value.SessionId, out var ended)) ends.TryAdd(ended, value.Status);
            }
        };
    }

    /// <summary>A launch on <paramref name="profile"/>. Without a saved workspace it adds <paramref name="repo"/> and the parent pane in <paramref name="parentMode"/>.</summary>
    internal static async Task<DelegationLaunch> Start(string profile, string fakeRoot, string repo, string worktrees, string parentMode = "acceptEdits", bool switchOn = true)
    {
        var launch = new DelegationLaunch(profile, fakeRoot, repo, worktrees, switchOn);
        await launch.Service.InitializeAsync();
        if (launch.Service.Snapshot.Workspaces.FirstOrDefault() is { } saved) { launch.Workspace = saved; return launch; }
        launch.Workspace = await launch.Service.AddWorkspaceAsync(repo);
        await launch.Service.UpdateAsync(state => state with { Sessions = [.. state.Sessions, new RunSession { Id = Parent, WorkspaceId = launch.Workspace.Id, Title = "Parent", Settings = new(PermissionMode: parentMode) }] });
        return launch;
    }

    internal RunSession Pane(string id) => Service.Snapshot.Sessions.Single(pane => pane.Id == id);
    internal List<string> Runs(string pane) { lock (gate) return runs.TryGetValue(pane, out var list) ? [.. list] : []; }
    internal string? Input(string pane, string runId) { lock (gate) return inputs.GetValueOrDefault(pane + "/" + runId); }
    internal List<string> Inputs(string pane) { lock (gate) return inputs.Where(pair => pair.Key.StartsWith(pane + "/", StringComparison.Ordinal)).Select(pair => pair.Value).ToList(); }

    /// <summary>What a run in <paramref name="folder"/> plays: one turn of <paramref name="steps"/> per prompt, in order.</summary>
    internal void Script(string folder, params JsonObject[][] turns)
    {
        var path = Path.Combine(FakeRoot, "scripts", SessionHistory.ClaudeProjectFolder(folder) + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, new JsonObject { ["turns"] = new JsonArray([.. turns.Select(steps => (JsonNode)new JsonArray([.. steps]))]) }.ToJsonString());
    }

    /// <summary>What a child's run plays when its first prompt holds <paramref name="phrase"/>.</summary>
    internal void ChildScript(string phrase, params JsonObject[] steps)
    {
        var path = Path.Combine(FakeRoot, "child-scripts.json");
        var list = System.IO.File.Exists(path) ? (JsonArray)JsonNode.Parse(System.IO.File.ReadAllText(path))! : [];
        list.Add(new JsonObject { ["phrase"] = phrase, ["steps"] = new JsonArray([.. steps]) });
        System.IO.File.WriteAllText(path, list.ToJsonString());
    }

    /// <summary>The fake Claude's log of runs in <paramref name="folder"/>.</summary>
    internal List<JsonObject> Log(string folder)
    {
        var path = Path.Combine(FakeRoot, "logs", SessionHistory.ClaudeProjectFolder(folder) + ".jsonl");
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path).Select(line => (JsonObject)JsonNode.Parse(line)!).ToList() : [];
    }

    /// <summary>The prompts the model received in the one Claude-format transcript of the session run in <paramref name="folder"/>.</summary>
    internal List<string> Prompts(string folder)
    {
        var projects = Path.Combine(FakeRoot, "claude-config", "projects", SessionHistory.ClaudeProjectFolder(folder));
        var transcripts = Directory.GetFiles(projects, "*.jsonl");
        Check(transcripts.Length == 1, $"one session in {folder}, not {transcripts.Length}");
        return System.IO.File.ReadAllLines(transcripts[0]).Select(line => JsonNode.Parse(line)!).Where(line => (string?)line["type"] == "user" && line["message"]?["content"] is JsonValue)
            .Select(line => (string)line["message"]!["content"]!).ToList();
    }

    /// <summary>The card for <paramref name="tool"/> still open in the pane, skipping <paramref name="excluding"/>.</summary>
    internal ToolPermissionRequest? OpenCard(string pane, string tool, params string[] excluding)
    {
        lock (gate) return cards.FirstOrDefault(card => card.RunId == pane && card.ToolName == tool && card.State == "pending" && !excluding.Contains(card.Id) && !cards.Any(other => other.RunId == pane && other.Id == card.Id && other.State != "pending"));
    }

    internal static string? Shown(ToolPermissionRequest card, string name) => ToolPermissionPresentation.Make(card.ToolName, card.InputJson).Fields.FirstOrDefault(field => field.Label == name)?.Value;

    internal Task<T> Awaited<T>(string what, Func<T?> produce) where T : class => Scenario.Awaited(what, () => Task.FromResult(produce()));
    internal Task<string> Ended(string pane, string runId) => Awaited($"the end of run {runId} in {pane}", () => { lock (gate) return ends.GetValueOrDefault(runId); });

    /// <summary>The human's send in the pane with nothing held for it: the run it started.</summary>
    internal async Task<string> HumanSend(string text, string pane = Parent)
    {
        var before = Runs(pane).Count; var session = Pane(pane);
        Check(await Service.SendAsync(new(pane, session.WorkspaceId, "claude", text, [], session.Model, session.Provider, session.Settings, session.ResumeId)) is DelegationRelease.NothingHeld, "nothing was held for " + pane);
        return await Awaited($"the run of {text}", () => Runs(pane).Skip(before).FirstOrDefault());
    }

    internal StartRunRequest Request(string text, string pane = Parent)
    {
        var session = Pane(pane);
        return new(pane, session.WorkspaceId, "claude", text, [], session.Model, session.Provider, session.Settings, session.ResumeId);
    }

    /// <summary>Quits the app: every run ends, as quitting ends it, and the profile is saved.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        // The pipe closes, and the scenario's folders are removed after it, even when the service's quit throws.
        try { await Service.DisposeAsync(); }
        finally { await Pipe.DisposeAsync(); }
    }
}

/// <summary>Steps of the fake Claude's scripts, and waiting on what a scenario needs.</summary>
internal static class Scenario
{
    internal static JsonObject Say(string text) => new() { ["say"] = text };
    internal static JsonObject Call(string tool, bool ask = false, params (string Name, string Value)[] arguments) => new() { ["call"] = tool, ["ask"] = ask, ["arguments"] = new JsonObject([.. arguments.Select(argument => KeyValuePair.Create(argument.Name, (JsonNode?)argument.Value))]) };
    internal static JsonObject Permission(string tool, params (string Name, string Value)[] input) => new() { ["permission"] = tool, ["input"] = new JsonObject([.. input.Select(argument => KeyValuePair.Create(argument.Name, (JsonNode?)argument.Value))]) };
    internal static JsonObject Run(bool ask, params string[][] commands) => new() { ["run"] = new JsonArray([.. commands.Select(command => (JsonNode)new JsonArray([.. command.Select(part => (JsonNode)part)]))]), ["ask"] = ask };
    internal static JsonObject Write(string path, string text) => new() { ["write"] = path, ["text"] = text };
    internal static string Report => ChildWorktree.NotesFolder + "/REPORT.md";
    /// <summary>A child's steps: a tracked file and its commit, asked for first.</summary>
    internal static JsonObject[] Commit(string file, string text) => [Write(file, text), Run(true, ["git", "add", "--", file], ["git", "commit", "-q", "-m", "Add " + file])];

    /// <summary>Polls until <paramref name="produce"/> has a value. The bound only ends a scenario that broke; nothing here asserts how long a step took.</summary>
    internal static async Task<T> Awaited<T>(string what, Func<Task<T?>> produce) where T : class
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(120);
        while (true)
        {
            if (await produce() is { } found) return found;
            if (DateTimeOffset.UtcNow >= end) throw new TimeoutException("The scenario never reached: " + what);
            await Task.Delay(20);
        }
    }

    internal static string? Option(JsonObject argv, string name) => argv["arguments"] is JsonArray list && list.Select(item => (string?)item).ToList() is var values && values.IndexOf(name) is >= 0 and var index && index + 1 < values.Count ? values[index + 1] : null;
    internal static JsonObject Structured(JsonObject call) => call["result"]?["structuredContent"] as JsonObject ?? [];
    internal static string Text(JsonObject call) => string.Join("\n", (call["result"]?["content"] as JsonArray ?? []).Select(block => (string?)block?["text"]));
}

/// <summary>
/// The Mac's three delegation scenarios (M/Tests/DelegationScenarioS1Tests.swift, S2, S3), headless through the Windows run path:
/// the real desktop service, its run manager and its pipe, with a scripted fake Claude. Each makes its repository and profile in
/// a temp folder and is judged from what is on disk, by the rules scripts/delegation-record.py checks.
/// </summary>
internal static class DelegationScenarioVerification
{
    private const string DelegateTool = "mcp__mighty-delegation__delegate", MergeTool = "mcp__mighty-delegation__merge";

    /// <summary>
    /// S1: a parent in an asking mode delegates two children, a child's approval only a human answers, both report, the parent
    /// fast-forwards child 1 and a human discards child 2 from its card.
    /// </summary>
    internal static async Task S1()
    {
        var place = new DelegationPlace();
        try
        {
            var start = await place.Repository(); const string mode = "acceptEdits";
            await using var s = await DelegationLaunch.Start(place.Profile, Path.Combine(place.Base, "fake"), place.Repo, place.Root, mode);
            var task1 = "Feature one: add feature-one.txt saying one."; var task2 = "Feature two: add feature-two.txt saying two.";
            s.ChildScript(task1, [.. Scenario.Commit("feature-one.txt", "one\n"), Scenario.Write(Scenario.Report, "# Report\n\nAdded feature-one.txt.\n"), Scenario.Say("Committed and reported.")]);
            s.ChildScript(task2, [.. Scenario.Commit("feature-two.txt", "two\n"), Scenario.Write(Scenario.Report, "# Report\n\nAdded feature-two.txt.\n"), Scenario.Say("Committed and reported.")]);

            // 1. Each delegate call waits on a human approval that shows the starting mode asked for.
            s.Script(place.Repo, [Scenario.Call("delegate", true, ("task", task1), ("mode", mode)), Scenario.Call("delegate", true, ("task", task2), ("mode", "manual")), Scenario.Say("Two children are on it.")]);
            var first = await s.HumanSend("Delegate feature one at your own mode and feature two at manual.");
            var approveOne = await s.Awaited("the first delegate approval", () => s.OpenCard(DelegationLaunch.Parent, DelegateTool));
            Check(DelegationLaunch.Shown(approveOne, "mode") == mode && DelegationLaunch.Shown(approveOne, "task") == task1, "the first approval shows its task and mode");
            s.Service.RespondToToolPermission(DelegationLaunch.Parent, approveOne.Id, true);
            var approveTwo = await s.Awaited("the second delegate approval", () => s.OpenCard(DelegationLaunch.Parent, DelegateTool, approveOne.Id));
            Check(DelegationLaunch.Shown(approveTwo, "mode") == "manual" && DelegationLaunch.Shown(approveTwo, "task") == task2, "the second approval shows its task and mode");
            s.Service.RespondToToolPermission(DelegationLaunch.Parent, approveTwo.Id, true);
            Same(await s.Ended(DelegationLaunch.Parent, first), "completed", "the delegating run");
            var both = await Scenario.Awaited("both children on disk", () => Task.FromResult(s.File is { Children.Count: 2 } file && file.Children.Find(child => child.StartingMode == mode) is { } a && file.Children.Find(child => child.StartingMode == "manual") is { } b ? Tuple.Create(a, b) : null));
            var (one, two) = (both.Item1, both.Item2);

            // 2. Both children ask a human before committing; child 2's request stays open while the parent's run for child 1's
            //    notice tries to answer it, which its tools cannot.
            var askTwo = await s.Awaited("child 2's approval request", () => s.OpenCard(two.Id, "Bash"));
            var askOne = await s.Awaited("child 1's approval request", () => s.OpenCard(one.Id, "Bash"));
            Check(s.Pane(one.Id) is { ParentSessionId: DelegationLaunch.Parent, TitleMode: PaneTitle.Fixed } paneOne && paneOne.Settings.PermissionMode == mode && s.Pane(two.Id).Settings.PermissionMode == "manual"
                && paneOne.WorkingFolder == one.WorktreePath && paneOne.Title == PaneTitle.Shortened(task1), "each child pane is linked, titled by its task and in the mode its parent asked for");
            s.Script(place.Repo, [Scenario.Call("list_children"), Scenario.Call("answer_permission", false, ("child", two.Id), ("request", askTwo.Id), ("behavior", "allow")), Scenario.Call("child_status", false, ("child", two.Id)), Scenario.Say("Child 2 waits for its human.")]);
            s.Service.RespondToToolPermission(one.Id, askOne.Id, true);
            var noticeRunOne = await s.Awaited("child 1's notice starting a parent run", () => s.Runs(DelegationLaunch.Parent).Skip(1).FirstOrDefault());
            Same(await s.Ended(DelegationLaunch.Parent, noticeRunOne), "completed", "child 1's notice run");
            Check(s.OpenCard(two.Id, "Bash")?.Id == askTwo.Id && !s.Log(two.WorktreePath).Any(entry => (string?)entry["event"] == "permission") && s.File.Children.Single(child => child.Id == two.Id).ReportRevision == 0, "child 2's request waits for its human");

            // A human answers child 2; it commits and reports too.
            s.Script(place.Repo, [Scenario.Call("list_children"), Scenario.Say("Both children reported.")]);
            s.Service.RespondToToolPermission(two.Id, askTwo.Id, true);
            var noticeRunTwo = await s.Awaited("child 2's notice starting a parent run", () => s.Runs(DelegationLaunch.Parent).Skip(2).FirstOrDefault());
            Same(await s.Ended(DelegationLaunch.Parent, noticeRunTwo), "completed", "child 2's notice run");
            var head1 = await s.Awaited("child 1's reported head", () => s.File is var file && file.Children.All(child => child.State == ChildState.Reported) ? file.Children.Single(child => child.Id == one.Id).ReportHead : null);

            // 3. Asked by the human, the parent fast-forwards child 1 through the merge tool, which asks first in this mode.
            s.Script(place.Repo, [Scenario.Call("child_status", false, ("child", one.Id)), Scenario.Call("merge", true, ("child", one.Id), ("expected_head", head1)), Scenario.Say("Merged child 1.")]);
            var mergeRun = await s.HumanSend("Merge child 1 if its report is right.");
            var approveMerge = await s.Awaited("the merge approval", () => s.OpenCard(DelegationLaunch.Parent, MergeTool));
            Check(DelegationLaunch.Shown(approveMerge, "child") == one.Id && DelegationLaunch.Shown(approveMerge, "expected_head") == head1, "the merge approval shows the child and its head");
            s.Service.RespondToToolPermission(DelegationLaunch.Parent, approveMerge.Id, true);
            Same(await s.Ended(DelegationLaunch.Parent, mergeRun), "completed", "the merge run");

            // 4. A human discards child 2 from its card: its confirmation, then the discard, which closes its pane.
            var pending = await s.Service.AskToDiscardChildAsync(two.Id) ?? throw new InvalidOperationException("no discard confirmation");
            Check(pending is { Nested.Count: 0 } && pending.Branch == "mighty/" + two.Id && pending.Title == PaneTitle.Shortened(task2), "the confirmation names the child and no nested worktree");
            Check((await s.Service.DiscardChildAsync(pending))?.Result is DelegationCardResult.Done && !s.Service.Snapshot.Sessions.Any(pane => pane.Id == two.Id), "the discard is done and the child's pane closed");
            await s.DisposeAsync();

            // 5. The verdict, from disk alone.
            var saved = s.File;
            Check(saved.Children.Select(child => child.Id).ToHashSet().SetEquals([one.Id, two.Id]) && saved.Children.All(child => child.ParentSessionId == DelegationLaunch.Parent && child.ParentBranch == "main" && child.BaseCommit == start), "two children of the parent, from main's head");
            var child1 = saved.Children.Single(child => child.Id == one.Id); var child2 = saved.Children.Single(child => child.Id == two.Id);
            Check(child1.StartingMode == mode && child2.StartingMode == "manual" && DelegationCoordinator.IsNoWider(child2.StartingMode, mode), "each stored mode is its request: one the parent's own, one narrower");
            Check(child1 is { State: ChildState.Merged, ReportRevision: 1 } && child1.ReportHead == head1 && child2 is { State: ChildState.Discarded, ReportRevision: 1 } && child2.ReportHead is { } head2 && head2 != head1, "child 1 merged, child 2 discarded, each reported once");
            Check(saved.Copy(one.Id, DelegationCopyKind.Report)?.Text.Contains("Added feature-one.txt.") == true && saved.Copy(two.Id, DelegationCopyKind.Report)?.Text.Contains("Added feature-two.txt.") == true, "both reports are kept");
            Check(saved.Notices.Count == 2 && saved.Notices.Select(notice => notice.Id).Distinct().Count() == 2 && saved.FollowUps.IsEmpty, "one notice per child");
            foreach (var (child, run) in new[] { (one.Id, noticeRunOne), (two.Id, noticeRunTwo) })
                Check(saved.Notices.Single(notice => notice.ChildId == child) is { Kind: NoticeKind.Reported, ReportRevision: 1, Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Queue } notice && notice.Receipt!.RunId == run, "each notice was the next run of the idle parent, once");
            Check(saved.Merges.Single() is { Kind: MergeKind.ToolFastForward, ParentBranch: "main" } merge && merge.ChildId == one.Id && merge.PreMergeCommit == start && merge.MergedCommit == head1 && merge.ChildHead == head1, "the tool's one fast-forward of main");
            Same(await place.Git("symbolic-ref", "--short", "HEAD"), "main", "main is checked out");
            Same(await place.Git("rev-parse", "refs/heads/main"), head1, "main is at child 1's head");
            Same(await place.Git("rev-list", "--parents", "-n", "1", head1), head1 + " " + start, "with no merge commit");
            Check((await place.Git("status", "--porcelain", "--untracked-files=no")).Length == 0 && System.IO.File.Exists(Path.Combine(place.Repo, "feature-one.txt")) && !System.IO.File.Exists(Path.Combine(place.Repo, "feature-two.txt")), "the parent's checkout has child 1's work only");
            Same(await place.MightyBranches(), "refs/heads/" + child1.Branch, "child 1's branch stays, child 2's is gone");
            Check((await place.WorktreeList()).Contains(DelegationPaths.Canonical(child1.WorktreePath), DelegationPaths.Comparer) && !Directory.Exists(child2.WorktreePath), "child 1's worktree stays, child 2's is gone");
            var parentLog = s.Log(place.Repo);
            var launches = parentLog.Where(entry => (string?)entry["event"] == "argv").ToList();
            Check(launches.Count == 4 && launches.All(argv => Scenario.Option(argv, "--permission-mode") == mode && Scenario.Option(argv, "--permission-prompt-tool") == "stdio"), "the parent ran in its asking mode every time");
            var answers = parentLog.Where(entry => (string?)entry["event"] == "permission").ToList();
            SameList(answers.Select(entry => (string?)entry["tool"]), [DelegateTool, DelegateTool, MergeTool], "a human allowed each delegate call and the merge");
            Check(answers.All(entry => (string?)entry["behavior"] == "allow"), "every answer allowed");
            var calls = parentLog.Where(entry => (string?)entry["event"] == "call").ToList();
            var delegated = calls.Where(call => (string?)call["tool"] == "delegate").Select(call => Scenario.Structured(call)["child"]).ToList();
            SameList(delegated.Select(child => (string?)child?["id"]), [one.Id, two.Id], "the delegate answers name the children");
            SameList(delegated.Select(child => (string?)child?["mode"]), [mode, "manual"], "and their modes");
            SameList(parentLog.First(entry => (string?)entry["event"] == "mcp")["tools"]!.AsArray().Select(tool => (string?)tool), DelegationToolManifest.All.Select(tool => tool.Name), "the parent's server lists the six tools");
            var tried = calls.Single(call => (string?)call["tool"] == "answer_permission");
            Check((bool?)tried["result"]?["isError"] == true && Scenario.Text(tried).Contains("Unknown delegation tool answer_permission.", StringComparison.Ordinal), "the parent has no way to answer a child's approval");
            Check((bool?)calls.Single(call => (string?)call["tool"] == "merge")["result"]?["isError"] != true, "the merge call succeeded");
            foreach (var child in new[] { child1, child2 })
            {
                var log = s.Log(child.WorktreePath);
                var argv = log.Where(entry => (string?)entry["event"] == "argv").ToList();
                Check(argv.Count == 1 && Scenario.Option(argv[0], "--permission-mode") == child.StartingMode, child.Id + " ran once, in its stored mode");
                Check(log.Where(entry => (string?)entry["event"] == "permission").Select(entry => ((string?)entry["tool"], (string?)entry["behavior"])).SequenceEqual([("Bash", "allow")]) && log.Where(entry => (string?)entry["event"] == "bash").Select(entry => (int?)entry["code"]).SequenceEqual([0]), child.Id + " committed after one human answer");
            }
            var prompts = s.Prompts(place.Repo);
            foreach (var notice in saved.Notices) Check(prompts.Count(prompt => prompt.Contains(notice.Id, StringComparison.Ordinal)) == 1, "each notice is in exactly one prompt the parent received");
            var resumed = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(s.FakeRoot, "claude-config", "projects", SessionHistory.ClaudeProjectFolder(place.Repo)), "*.jsonl")[0]);
            Check(launches.Skip(1).All(argv => Scenario.Option(argv, "--resume") == resumed), "every later parent run resumed its one session");
        }
        finally { place.Remove(); }
    }

    /// <summary>
    /// S2: three children; child 1's notice reaches the idle parent; after the human stops the parent, children 2 and 3 report and
    /// their notices are held. The app quits, and from that state each release route (the send, and run next with a queued human
    /// row behind it) is played on its own copy of the profile and the Claude config, by a relaunched app: it starts no run by
    /// itself, and the route releases both notices together, oldest first, each reaching the parent's transcript once.
    /// </summary>
    internal static async Task S2()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository(); const string mode = "acceptEdits";
            var first = Path.Combine(place.Base, "launch-1");
            var tasks = new[] { "Feature one: note what one needs.", "Feature two: note what two needs.", "Feature three: note what three needs." };
            List<ChildRecord> children; List<Notice> notices; DelegationFile held;
            await using (var s = await DelegationLaunch.Start(Path.Combine(first, "profile"), first, place.Repo, place.Root, mode))
            {
                foreach (var (task, index) in tasks.Select((task, index) => (task, index)))
                    s.ChildScript(task, Scenario.Permission("Write", ("file_path", Scenario.Report)), Scenario.Write(Scenario.Report, $"# Report\n\nChild {index + 1} is done.\n"), Scenario.Say("Reported."));
                // 1. The parent delegates three children and finishes normally.
                s.Script(place.Repo, [.. tasks.Select(task => Scenario.Call("delegate", false, ("task", task), ("mode", mode))), Scenario.Say("Three children are on it.")]);
                var delegating = await s.HumanSend("Split the three features.");
                Same(await s.Ended(DelegationLaunch.Parent, delegating), "completed", "the delegating run");
                children = await Scenario.Awaited("three children on disk", () => Task.FromResult(s.File is { Children.Count: 3 } file ? tasks.Select(task => file.Children.Find(child => file.Copy(child.Id, DelegationCopyKind.Task)?.Text.Contains(task) == true)).OfType<ChildRecord>().ToList() is { Count: 3 } ordered ? ordered : null : null));
                var asks = new List<ToolPermissionRequest>();
                foreach (var child in children) asks.Add(await s.Awaited(child.Id + "'s request", () => s.OpenCard(child.Id, "Write")));

                // 2. Child 1 reports while the parent is idle after its normal finish: its notice is the parent's next run.
                s.Script(place.Repo, [Scenario.Say("Child one reported.")]);
                s.Service.RespondToToolPermission(children[0].Id, asks[0].Id, true);
                var noticeRun = await s.Awaited("child 1's notice starting a parent run", () => s.Runs(DelegationLaunch.Parent).Skip(1).FirstOrDefault());
                Same(await s.Ended(DelegationLaunch.Parent, noticeRun), "completed", "child 1's notice run");
                var notice1 = await s.Awaited("child 1's notice delivered", () => s.File.Notices.FirstOrDefault(notice => notice.ChildId == children[0].Id && notice.Lane == DeliveryLane.Delivered));
                Check(notice1 is { Kind: NoticeKind.Reported, ReportRevision: 1, Receipt.Route: DeliveryRoute.Queue } && notice1.Receipt!.RunId == noticeRun && s.Input(DelegationLaunch.Parent, noticeRun) == DelegationCoordinator.Text(notice1), "the notice alone was the parent's next run");

                // 3. The human stops the parent's next run while it waits on a request.
                s.Script(place.Repo, [Scenario.Permission("Bash", ("command", "dotnet build")), Scenario.Say("Built.")]);
                var stopped = await s.HumanSend("Check the build while they work.");
                await s.Awaited("the parent's request in the run to stop", () => s.OpenCard(DelegationLaunch.Parent, "Bash"));
                await s.Service.StopAsync(DelegationLaunch.Parent);
                Same(await s.Ended(DelegationLaunch.Parent, stopped), "stopped", "the stopped run");

                // 4. Children 2 and 3 report, in that order: both notices are held.
                foreach (var (child, ask) in children.Skip(1).Zip(asks.Skip(1)))
                {
                    s.Service.RespondToToolPermission(child.Id, ask.Id, true);
                    await s.Awaited(child.Id + "'s notice held", () => s.File.Notices.FirstOrDefault(notice => notice.ChildId == child.Id && notice.Lane == DeliveryLane.Held));
                }
                SameList(s.Runs(DelegationLaunch.Parent), [delegating, noticeRun, stopped], "the parent ran nothing more");
                held = s.File;
                notices = children.Select(child => held.Notices.Single(notice => notice.ChildId == child.Id)).ToList();
                Check(held.Notices.Count == 3 && notices.Select(notice => notice.Lane).SequenceEqual([DeliveryLane.Delivered, DeliveryLane.Held, DeliveryLane.Held]) && notices.All(notice => notice is { Kind: NoticeKind.Reported, ReportRevision: 1 }) && held.Children.All(child => child.State == ChildState.Reported), "one notice delivered and two held");
                SameList(s.Service.HeldItems(DelegationLaunch.Parent).Select(item => item.Id), [notices[1].Id, notices[2].Id], "the parent's queued list holds them, oldest first");
                held = s.File;
            }
            foreach (var (route, index) in new[] { "send", "runNext" }.Select((route, index) => (route, index)))
            {
                var folder = Path.Combine(place.Base, "launch-" + (index + 2));
                foreach (var name in new[] { "profile", "claude-config" }) CopyFolder(Path.Combine(first, name), Path.Combine(folder, name));
                await using var s = await DelegationLaunch.Start(Path.Combine(folder, "profile"), folder, place.Repo, place.Root, mode);
                // The relaunched app restores the panes, none running, and starts no run by itself, not even in a delivery pass.
                await s.Coordinator.DeliverPending();
                Check(s.Runs(DelegationLaunch.Parent).Count == 0 && children.All(child => s.Runs(child.Id).Count == 0) && s.File == held && s.Service.Snapshot.Sessions.All(pane => pane.Status != "running"), route + ": nothing ran at launch");
                Check(children.All(child => s.Pane(child.Id) is { ParentSessionId: DelegationLaunch.Parent } pane && pane.WorkingFolder == child.WorktreePath), route + ": the child panes came back linked");
                SameList(s.Service.HeldItems(DelegationLaunch.Parent).Select(item => item.Id), [notices[1].Id, notices[2].Id], route + ": both still held");
                s.Script(place.Repo, [Scenario.Say("Read both reports.")], [Scenario.Say("Tests pass.")]);
                var heldTexts = new[] { DelegationCoordinator.Text(notices[1]), DelegationCoordinator.Text(notices[2]) };
                string releaseRun, after;
                if (route == "send")
                {
                    after = "From the desktop: merge what is ready.";
                    if (await s.Service.SendAsync(s.Request(after)) is not DelegationRelease.Released released) throw new InvalidOperationException("the send released nothing");
                    SameList(released.ItemIds, [notices[1].Id, notices[2].Id], "the send released both");
                    Same(s.Input(DelegationLaunch.Parent, released.RunId), string.Join("\n\n", heldTexts.Append(after)), "oldest first, ahead of the human's text, in one run");
                    Same(await s.Ended(DelegationLaunch.Parent, released.RunId), "completed", "the release run");
                    SameList(s.Runs(DelegationLaunch.Parent), [released.RunId], "one run");
                    releaseRun = released.RunId;
                }
                else
                {
                    // Run next: what is held first; with nothing held, the pane's next queued human row.
                    after = "Human row: run the tests.";
                    if (await s.Service.RunNextAsync(DelegationLaunch.Parent) is not DelegationRelease.Released released) throw new InvalidOperationException("run next released nothing");
                    SameList(released.ItemIds, [notices[1].Id, notices[2].Id], "run next released both");
                    Same(s.Input(DelegationLaunch.Parent, released.RunId), string.Join("\n\n", heldTexts), "run next carries them alone");
                    Same(await s.Ended(DelegationLaunch.Parent, released.RunId), "completed", "the release run");
                    Check(await s.Service.RunNextAsync(DelegationLaunch.Parent) is DelegationRelease.NothingHeld, "then nothing is held");
                    s.Script(place.Repo, [Scenario.Say("Tests pass.")]);
                    await s.Service.StartAsync(s.Request(after));
                    var rowRun = await s.Awaited("the queued row's run", () => s.Runs(DelegationLaunch.Parent).Skip(1).FirstOrDefault());
                    Same(s.Input(DelegationLaunch.Parent, rowRun), after, "the queued human row runs next");
                    Same(await s.Ended(DelegationLaunch.Parent, rowRun), "completed", "the row's run");
                    releaseRun = released.RunId;
                }
                var file = s.File;
                Check(file.Notices.Single(notice => notice.Id == notices[0].Id) == notices[0], route + ": the first notice is as it was");
                foreach (var notice in notices.Skip(1)) Check(file.Notices.Single(saved => saved.Id == notice.Id) is { Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Queue } saved && saved.Receipt!.RunId == releaseRun, route + ": saved delivered by the release run");
                Check(s.Service.HeldItems(DelegationLaunch.Parent).Count == 0 && await s.Coordinator.Send("Again.", DelegationLaunch.Parent) is DelegationRelease.NothingHeld && await s.Coordinator.RunNext(DelegationLaunch.Parent) is DelegationRelease.NothingHeld, route + ": nothing more is held");
                await s.DisposeAsync();
                var prompts = s.Prompts(place.Repo);
                foreach (var notice in notices) Check(prompts.Count(prompt => prompt.Contains(notice.Id, StringComparison.Ordinal)) == 1, route + ": each notice in exactly one prompt");
                Check(prompts.Count(prompt => prompt.Contains(after, StringComparison.Ordinal)) == 1, route + ": the human's text in one prompt");
                var positions = new[] { notices[1].Id, notices[2].Id, after }.Select(needle => prompts.FindIndex(prompt => prompt.Contains(needle, StringComparison.Ordinal)) is var at and >= 0 ? (at, prompts[at].IndexOf(needle, StringComparison.Ordinal)) : (-1, -1)).ToList();
                Check(positions.All(position => position.Item1 >= 0) && positions.Zip(positions.Skip(1)).All(pair => pair.First.CompareTo(pair.Second) < 0), route + ": the released two oldest first and ahead of the human's text");
            }
        }
        finally { place.Remove(); }
    }

    /// <summary>
    /// S3: children A and B from one base. A is fast-forwarded into main; B's fast-forward is refused with diverged and changes
    /// nothing. A human stopped B after it reported, so the parent's two follow-ups to B are held; the app quits, and the
    /// relaunched app hands them to B exactly once, with the human's next send in B. B brings in main and reports revision 2,
    /// which the merge tool fast-forwards. A third follow-up is refused with follow_up_limit.
    /// </summary>
    internal static async Task S3()
    {
        var place = new DelegationPlace();
        try
        {
            var start = await place.Repository(); const string parentMode = "auto", childMode = "acceptEdits";
            const string taskA = "Feature A: add a.txt saying a.", taskB = "Feature B: add b.txt saying b.";
            const string bringInMain = "Bring in main: merge main into your branch, then report again.", keepB = "Keep b.txt as it is, and say so in REPORT.md.";
            var profile = Path.Combine(place.Base, "app", "profile"); var fake = Path.Combine(place.Base, "app");
            ChildRecord a, b; string headA, headB1; List<string> noticeRuns; DelegationFile file; List<FollowUp> followUps; string runB1;
            List<string> firstInputsB;
            await using (var s = await DelegationLaunch.Start(profile, fake, place.Repo, place.Root, parentMode))
            {
                s.ChildScript(taskA, [.. Scenario.Commit("a.txt", "a\n"), Scenario.Write(Scenario.Report, "# Report\n\nAdded a.txt.\n"), Scenario.Say("Committed and reported.")]);
                s.ChildScript(taskB, [.. Scenario.Commit("b.txt", "b\n"), Scenario.Write(Scenario.Report, "# Report\n\nAdded b.txt.\n"), Scenario.Permission("Bash", ("command", "make check")), Scenario.Say("Checked.")]);
                // 1. The parent delegates A and B, both from main's one head.
                s.Script(place.Repo, [Scenario.Call("delegate", false, ("task", taskA), ("mode", childMode)), Scenario.Call("delegate", false, ("task", taskB), ("mode", childMode)), Scenario.Say("A and B are on it.")]);
                var delegating = await s.HumanSend("Split features A and B.");
                Same(await s.Ended(DelegationLaunch.Parent, delegating), "completed", "the delegating run");
                var both = await Scenario.Awaited("both children on disk", () => Task.FromResult(s.File is { Children.Count: 2 } f && f.Children.Find(child => f.Copy(child.Id, DelegationCopyKind.Task)?.Text.Contains(taskA) == true) is { } ca && f.Children.Find(child => f.Copy(child.Id, DelegationCopyKind.Task)?.Text.Contains(taskB) == true) is { } cb ? Tuple.Create(ca, cb) : null));
                (a, b) = (both.Item1, both.Item2);
                Check(a.BaseCommit == start && b.BaseCommit == start && a.ParentBranch == "main" && b.ParentBranch == "main", "both from main's one head");

                // 2. A commits and reports while the parent is idle after a normal finish: its notice is the parent's next run.
                var askA = await s.Awaited("A's commit request", () => s.OpenCard(a.Id, "Bash"));
                s.Script(place.Repo, [Scenario.Say("A reported.")]);
                s.Service.RespondToToolPermission(a.Id, askA.Id, true);
                var noticeRunA = await s.Awaited("A's notice starting a parent run", () => s.Runs(DelegationLaunch.Parent).Skip(1).FirstOrDefault());
                Same(await s.Ended(DelegationLaunch.Parent, noticeRunA), "completed", "A's notice run");
                headA = await s.Awaited("A's reported head", () => s.File.Children.Single(child => child.Id == a.Id) is { State: ChildState.Reported, ReportRevision: 1 } child ? child.ReportHead : null);

                // 3. B commits and reports, then asks to run its checks, and a human stops it there; its notice is the parent's next run.
                var askB = await s.Awaited("B's commit request", () => s.OpenCard(b.Id, "Bash"));
                s.Script(place.Repo, [Scenario.Say("B reported.")]);
                s.Service.RespondToToolPermission(b.Id, askB.Id, true);
                await s.Awaited("B's request to run its checks", () => s.OpenCard(b.Id, "Bash", askB.Id));
                runB1 = s.Runs(b.Id).Single();
                await s.Service.StopAsync(b.Id);
                Same(await s.Ended(b.Id, runB1), "stopped", "B's stopped run");
                var noticeRunB = await s.Awaited("B's notice starting a parent run", () => s.Runs(DelegationLaunch.Parent).Skip(2).FirstOrDefault());
                Same(await s.Ended(DelegationLaunch.Parent, noticeRunB), "completed", "B's notice run");
                headB1 = await s.Awaited("B's reported head", () => s.File.Children.Single(child => child.Id == b.Id) is { State: ChildState.Reported, ReportRevision: 1 } child ? child.ReportHead : null);
                Check(headA != headB1 && headA != start && headB1 != start, "A and B each committed");
                noticeRuns = [noticeRunA, noticeRunB];

                // 4. The merge tool fast-forwards main to A's head.
                s.Script(place.Repo, [Scenario.Call("merge", false, ("child", a.Id), ("expected_head", headA)), Scenario.Say("Merged A.")]);
                var mergeA = await s.HumanSend("Merge A.");
                Same(await s.Ended(DelegationLaunch.Parent, mergeA), "completed", "the merge run");
                Same(await place.Git("rev-parse", "refs/heads/main"), headA, "main is at A's head");

                // 5. B's fast-forward is refused with diverged: main moved on from B's base. Nothing changes.
                var before = s.File; var disk = await place.Snapshot(b.WorktreePath);
                s.Script(place.Repo, [Scenario.Call("merge", false, ("child", b.Id), ("expected_head", headB1)), Scenario.Say("B diverged.")]);
                var mergeB = await s.HumanSend("Merge B.");
                Same(await s.Ended(DelegationLaunch.Parent, mergeB), "completed", "the refused merge's run");
                Check(s.File == before && await place.Snapshot(b.WorktreePath) == disk && !System.IO.File.Exists(Path.Combine(place.Repo, ".git", "MERGE_HEAD")), "the refused merge changed nothing");

                // 6. B's pane is idle after a stop, so both follow-ups are held and B runs nothing.
                s.Script(place.Repo, [Scenario.Call("follow_up", false, ("child", b.Id), ("text", bringInMain)), Scenario.Call("follow_up", false, ("child", b.Id), ("text", keepB)), Scenario.Say("B has its follow-ups.")]);
                var following = await s.HumanSend("B diverged: have it bring in main.");
                Same(await s.Ended(DelegationLaunch.Parent, following), "completed", "the follow-up run");
                file = s.File; followUps = [.. file.FollowUps];
                SameList(followUps.Select(followUp => followUp.Text), [bringInMain, keepB], "two follow-ups");
                Check(followUps.All(followUp => followUp.ChildId == b.Id && followUp.Lane == DeliveryLane.Held && followUp.Receipt is null) && file.Children.Single(child => child.Id == b.Id) is { FollowUpCount: 2, State: ChildState.Reported }, "both held, B still reported");
                SameList(s.Service.HeldItems(b.Id).Select(item => item.Id), followUps.Select(followUp => followUp.Id), "B's queued list holds them");
                SameList(s.Runs(b.Id), [runB1], "B ran nothing more");
                SameList(s.Runs(DelegationLaunch.Parent), [delegating, noticeRunA, noticeRunB, mergeA, mergeB, following], "the parent's runs");
                firstInputsB = s.Inputs(b.Id);
                a = file.Children.Single(child => child.Id == a.Id); b = file.Children.Single(child => child.Id == b.Id);
            }
            Check(new DelegationFileStore(profile).Load() == file, "quitting changed nothing in the profile");

            // The relaunch: the coordinator finds no run the quit killed, starts no run by itself and still holds both follow-ups.
            string runB2, headB2, mergeRun; Notice notice2;
            await using (var s = await DelegationLaunch.Start(profile, fake, place.Repo, place.Root, parentMode))
            {
                Check((await s.Coordinator.InterruptRunsKilledByQuit()).Count == 0, "no run was killed by the quit");
                await s.Coordinator.DeliverPending();
                Check(s.Runs(DelegationLaunch.Parent).Count == 0 && s.Runs(b.Id).Count == 0 && s.File == file && s.Pane(b.Id).Status == "stopped", "nothing ran at launch; B came back stopped");
                SameList(s.Service.HeldItems(b.Id).Select(item => item.Id), followUps.Select(followUp => followUp.Id), "both still held for B");
                Check(s.Service.HeldItems(DelegationLaunch.Parent).Count == 0, "nothing held for the parent");

                // 7. The human's next send in B releases both, oldest first, ahead of the text, in one run.
                s.ChildScript(bringInMain, Scenario.Run(false, ["git", "merge", "--no-edit", "-q", "main"]), Scenario.Write(Scenario.Report, "# Report\n\nAdded b.txt and brought in main; b.txt is as it was.\n"), Scenario.Say("Brought in main and reported again."));
                var reportFile = ChildWorktree.ReportFileIn(b.WorktreePath);
                var heldTexts = followUps.Select(followUp => DelegationCoordinator.Text(followUp, reportFile)).ToList();
                if (await s.Service.SendAsync(s.Request("Go ahead.", b.Id)) is not DelegationRelease.Released release) throw new InvalidOperationException("B's send released nothing");
                runB2 = release.RunId;
                SameList(release.ItemIds, followUps.Select(followUp => followUp.Id), "the send released both");
                Same(s.Input(b.Id, runB2), string.Join("\n\n", heldTexts.Append("Go ahead.")), "oldest first, ahead of the text");
                Same(await s.Ended(b.Id, runB2), "completed", "B's run");
                var revised = await s.Awaited("B's revision 2", () => s.File.Children.Single(child => child.Id == b.Id) is { State: ChildState.Reported, ReportRevision: 2 } child ? child : null);
                headB2 = revised.ReportHead!;
                Same(await place.GitIn(b.WorktreePath, "rev-parse", "HEAD"), headB2, "B's head is its revision 2");
                Same(await place.Git("rev-list", "--parents", "-n", "1", headB2), $"{headB2} {headB1} {headA}", "B's own commit brought main in");
                Check(s.File.FollowUps.All(followUp => followUp is { Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Queue } && followUp.Receipt!.RunId == runB2), "each follow-up saved delivered by that one run");
                await s.Coordinator.DeliverPending();
                Check(await s.Coordinator.Send("Anything else?", b.Id) is DelegationRelease.NothingHeld && await s.Service.RunNextAsync(b.Id) is DelegationRelease.NothingHeld, "nothing more is held for B");
                SameList(s.Runs(b.Id), [runB2], "B ran once since the relaunch");
                foreach (var followUp in followUps) Check(firstInputsB.Concat(s.Inputs(b.Id)).Count(input => input.Contains(followUp.Id, StringComparison.Ordinal)) == 1, "each follow-up reached B once");

                // 8. The parent had no run since the relaunch, so B's revision 2 notice is held; the human's send releases it, and
                //    the merge tool fast-forwards main to B's new head.
                notice2 = await s.Awaited("B's revision 2 notice held", () => s.File.Notices.FirstOrDefault(notice => notice.ChildId == b.Id && notice.ReportRevision == 2 && notice.Lane == DeliveryLane.Held));
                Check(notice2.Kind == NoticeKind.Reported && s.Runs(DelegationLaunch.Parent).Count == 0, "held, and the parent ran nothing");
                s.Script(place.Repo, [Scenario.Call("child_status", false, ("child", b.Id)), Scenario.Call("merge", false, ("child", b.Id), ("expected_head", headB2)), Scenario.Say("Merged B.")]);
                var text = "B reported again: merge it.";
                if (await s.Service.SendAsync(s.Request(text)) is not DelegationRelease.Released parentRelease) throw new InvalidOperationException("the parent's send released nothing");
                mergeRun = parentRelease.RunId;
                SameList(parentRelease.ItemIds, [notice2.Id], "the send released the notice");
                Same(s.Input(DelegationLaunch.Parent, mergeRun), DelegationCoordinator.Text(notice2) + "\n\n" + text, "the notice ahead of the text");
                Same(await s.Ended(DelegationLaunch.Parent, mergeRun), "completed", "the merge run");
                Same(await place.Git("rev-parse", "refs/heads/main"), headB2, "main is at B's revision 2 head");

                // 9. A third follow-up is refused with follow_up_limit and changes nothing.
                var before = s.File;
                s.Script(place.Repo, [Scenario.Call("follow_up", false, ("child", b.Id), ("text", "Also add a changelog line.")), Scenario.Say("B has had its two.")]);
                var third = await s.HumanSend("Ask B for one more change.");
                Same(await s.Ended(DelegationLaunch.Parent, third), "completed", "the third follow-up's run");
                Check(s.File == before && s.Runs(b.Id).Count == 1, "the third follow-up changed nothing");
                SameList(s.Runs(DelegationLaunch.Parent), [mergeRun, third], "the parent's runs since the relaunch");
                await s.DisposeAsync();

                // The verdict.
                var saved = s.File;
                var childA = saved.Children.Single(child => child.Id == a.Id); var childB = saved.Children.Single(child => child.Id == b.Id);
                Check(saved.Children.Count == 2 && saved.Children.All(child => child.ParentSessionId == DelegationLaunch.Parent && child.ParentBranch == "main" && child.BaseCommit == start && child.StartingMode == childMode), "both from one base, in the asked mode");
                Check(childA is { State: ChildState.Merged, ReportRevision: 1, FollowUpCount: 0 } && childA.ReportHead == headA && childB is { State: ChildState.Merged, ReportRevision: 2, FollowUpCount: 2 } && childB.ReportHead == headB2, "A merged at revision 1, B at revision 2");
                Check(saved.Copy(b.Id, DelegationCopyKind.Report)?.Text.Contains("brought in main") == true, "B's revision 2 report is kept");
                Check(saved.Merges.Count == 2 && saved.Merges.Single(merge => merge.ChildId == a.Id) is { Kind: MergeKind.ToolFastForward } mergeAR && mergeAR.PreMergeCommit == start && mergeAR.MergedCommit == headA
                    && saved.Merges.Single(merge => merge.ChildId == b.Id) is { Kind: MergeKind.ToolFastForward } mergeBR && mergeBR.PreMergeCommit == headA && mergeBR.MergedCommit == headB2 && mergeBR.ChildHead == headB2, "two tool fast-forwards");
                Check(saved.Notices.Count == 3 && saved.Notices.All(notice => notice is { Kind: NoticeKind.Reported, Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Queue })
                    && saved.Notices.Single(notice => notice.ChildId == a.Id).Receipt!.RunId == noticeRuns[0] && saved.Notices.Single(notice => notice.ChildId == b.Id && notice.ReportRevision == 1).Receipt!.RunId == noticeRuns[1]
                    && saved.Notices.Single(notice => notice.Id == notice2.Id).Receipt!.RunId == mergeRun, "three notices, each delivered once");
                Check(saved.FollowUps.Select(followUp => followUp.Id).SequenceEqual(followUps.Select(followUp => followUp.Id)) && saved.FollowUps.All(followUp => followUp.Receipt?.RunId == runB2), "both follow-ups by B's one run");
                Same(await place.Git("symbolic-ref", "--short", "HEAD"), "main", "main is checked out");
                Same(await place.Git("rev-list", "--count", "main"), "4", "main reached by fast-forwards only");
                Check((await place.Git("status", "--porcelain", "--untracked-files=no")).Length == 0 && System.IO.File.Exists(Path.Combine(place.Repo, "a.txt")) && System.IO.File.Exists(Path.Combine(place.Repo, "b.txt")), "the parent's checkout has both");
                Same(await place.Git("rev-parse", "refs/heads/" + a.Branch), headA, "A's branch stays");
                Same(await place.Git("rev-parse", "refs/heads/" + b.Branch), headB2, "B's branch stays");
                var calls = s.Log(place.Repo).Where(entry => (string?)entry["event"] == "call").ToList();
                var merges = calls.Where(call => (string?)call["tool"] == "merge").Select(Scenario.Structured).ToList();
                Check(merges.Count == 3 && (string?)merges[0]["merged"]?["mergedCommit"] == headA && (string?)merges[1]["refused"] == "diverged" && merges[1]["merged"] is null && (string?)merges[2]["merged"]?["mergedCommit"] == headB2 && (string?)merges[2]["merged"]?["preMergeCommit"] == headA, "merge A, B refused with diverged, merge B's revision 2");
                var asked = calls.Where(call => (string?)call["tool"] == "follow_up").Select(Scenario.Structured).ToList();
                Check(asked.Count == 3 && asked.Take(2).Select(answer => (string?)answer["followUp"]?["id"]).SequenceEqual(followUps.Select(followUp => followUp.Id)) && asked.Take(2).All(answer => (string?)answer["followUp"]?["lane"] == "held")
                    && asked.Take(2).Select(answer => (int?)answer["followUp"]?["remaining"]).SequenceEqual([1, 0]) && (string?)asked[2]["refused"] == "follow_up_limit" && asked[2]["followUp"] is null, "two follow-ups held, the third refused");
                var logB = s.Log(b.WorktreePath);
                Check(logB.Where(entry => (string?)entry["event"] == "argv").ToList() is { Count: 2 } argvB && argvB.All(argv => Scenario.Option(argv, "--permission-mode") == childMode), "B ran twice, in its stored mode");
                SameList(logB.Where(entry => (string?)entry["event"] == "bash").Select(entry => (int?)entry["code"]), [0, 0], "B's two commands succeeded");
                var parentPrompts = s.Prompts(place.Repo);
                foreach (var notice in saved.Notices) Check(parentPrompts.Count(prompt => prompt.Contains(notice.Id, StringComparison.Ordinal)) == 1, "each notice in exactly one prompt the parent received");
                var promptsB = s.Prompts(b.WorktreePath);
                foreach (var followUp in saved.FollowUps) Check(promptsB.Count(prompt => prompt.Contains(followUp.Id, StringComparison.Ordinal)) == 1, "each follow-up in exactly one prompt B received");
            }
        }
        finally { place.Remove(); }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
