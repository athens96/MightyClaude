using System.Text.Json;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// The composer's second send and Mighty Orchestration in Codex and Antigravity panes, ported from the Mac's
/// ComposerSecondSendTests, DelegationHumanChildTests and DelegationProvidersTests: which pane opens a new worktree, the human's
/// child from the send to its first run with the parent left alone, and the app's commit of a Codex or Antigravity child's work.
/// </summary>
internal static class DelegationHumanChildVerification
{
    private const string Agent = "claude";

    /// <summary>The send's own start of a child's first run, as the window gives it: each run it started, and each child that failed after the answer.</summary>
    private sealed class Sends
    {
        private readonly object gate = new();
        private readonly List<(string ChildId, HumanChildFirstRun Run)> started = [];
        private readonly List<string> failed = [];
        internal bool RunFails;
        internal List<(string ChildId, HumanChildFirstRun Run)> Started { get { lock (gate) return [.. started]; } }
        internal List<string> Failed { get { lock (gate) return [.. failed]; } }
        internal HumanChildStarter Starter => new((childId, run) =>
        {
            lock (gate) { started.Add((childId, run)); return Task.FromResult(RunFails ? null : "send-run-of-" + childId); }
        }, childId => { lock (gate) failed.Add(childId); return Task.CompletedTask; });
    }

    /// <summary>A place with a coordinator over it. The pane "parent" works in the repository, running <paramref name="Provider"/>.</summary>
    private sealed record Fixture(DelegationPlace Place, DelegationTestHost Host, DelegationCoordinator Coordinator, Sends Sends)
    {
        internal DelegationFileStore Store => Place.Store;
        internal byte[] Bytes() => File.ReadAllBytes(Store.FilePath);
        internal Task<HumanChildStart> Send(string text, string from = "parent", bool planFirst = false) => Coordinator.StartHumanChild(text, from, Sends.Starter, planFirst);
        internal Task<DelegationResponse> Call(string tool, params (string, string)[] arguments) => Coordinator.Handle(Request(tool, arguments), Parent);
        internal Task<bool> WaitFor(string id, ChildState state) => Eventually(async () => Coordinator.File.Children.Find(child => child.Id == id)?.State == state && !await Coordinator.IsStarting(id));
        internal ChildRecord Only => Coordinator.File.Children.Single();
        internal Task<string> GitIn(string folder, params string[] arguments) => Place.GitIn(folder, arguments);
        internal void Remove() => Place.Remove();
        /// <summary>A refused send made nothing: the same file, no worktree root, no <c>mighty/*</c> branch, and no pane or run asked for.</summary>
        internal async Task NothingMade(byte[] bytes, bool hasGit = true)
        {
            Check(Bytes().SequenceEqual(bytes) && !Directory.Exists(Place.Root) && Host.CreatedPanes.Count == 0 && Sends.Started.Count == 0 && Host.StartedRuns.Count == 0, "the refused send changed nothing");
            if (hasGit) Check((await Place.MightyBranches()).Length == 0, "no branch was made");
        }
    }

    private static async Task<Fixture> Make(int? commits = 1, DelegationFile? file = null, string parentMode = "acceptEdits", string provider = Agent, long freeBytes = 50_000_000_000, bool switchOn = true)
    {
        var place = new DelegationPlace();
        var store = new DelegationFileStore(place.Profile); store.Save(file ?? DelegationFile.Empty);
        var host = new DelegationTestHost(store) { FirstRun = id => "host-run-of-" + id, MakesPanes = true };
        Directory.CreateDirectory(place.Repo);
        if (commits is { } count) await place.Repository(count);
        host.Set(new("parent", parentMode, place.Repo, Provider: provider, RunId: "run-1", Activity: DelegationPaneActivity.Running));
        return new(place, host, new(store, host, new ChildWorktreeMaker(place.Root, _ => freeBytes), () => switchOn), new());
    }

    private static ChildRecord OpenChild(string id, bool byHuman = false) =>
        ChildRecord.Make(id, "parent", Path.Combine(Path.GetTempPath(), "nonexistent-worktrees", id), "main", Commit, "plan", "key-" + id, state: ChildState.Running, startedByHuman: byHuman ? true : null);

    private static ChildRecord Started(HumanChildStart answer) => answer is HumanChildStart.Started { Child: var child } ? child : throw new InvalidOperationException("The send made no child: " + answer);

    internal static Task TheSecondSendChoosesAWorktreeOnlyWhereAChildCanWork()
    {
        ComposerSecondSend Choose(string provider, string mode = "acceptEdits", bool isChild = false, bool on = true, bool ready = true, string kind = "claude") =>
            ComposerSecondSends.Choose(kind, provider, mode, isChild, on, ready);
        // A Claude pane that may have children in a usable checkout opens one.
        Check(Choose("claude") == ComposerSecondSend.Worktree, "a Claude pane opens a new worktree");
        // A child, the switch off or no usable checkout: Claude queues too, as Enter; nothing a person sends joins the running turn.
        Check(Choose("claude", isChild: true) == ComposerSecondSend.Queue && Choose("claude", on: false) == ComposerSecondSend.Queue && Choose("claude", ready: false) == ComposerSecondSend.Queue,
            "otherwise Claude queues, as Enter");
        Check(Enum.GetValues<ComposerSecondSend>().SequenceEqual([ComposerSecondSend.Worktree, ComposerSecondSend.Queue]), "the second send never steers");
        foreach (var provider in new[] { "codex", "gemini" })
            Check(Choose(provider) == ComposerSecondSend.Worktree && Choose(provider, isChild: true) == ComposerSecondSend.Queue && Choose(provider, on: false) == ComposerSecondSend.Queue && Choose(provider, ready: false) == ComposerSecondSend.Queue, provider + " opens one too, and otherwise queues");
        Check(Choose("claude", kind: "shell") == ComposerSecondSend.Queue, "a shell pane queues");
        foreach (var (provider, mode, worktree) in new[] { ("codex", "manual", false), ("codex", "acceptEdits", true), ("codex", "onRequest", true), ("codex", "fullAccess", true), ("gemini", "manual", false), ("gemini", "plan", false), ("gemini", "acceptEdits", true), ("gemini", "fullAccess", true) })
            Check(Choose(provider, mode) == (worktree ? ComposerSecondSend.Worktree : ComposerSecondSend.Queue), $"{provider} {mode}");
        foreach (var mode in DelegationProviders.StartingModes("claude")) Check(Choose("claude", mode) == ComposerSecondSend.Worktree, "Claude " + mode);
        // A checkout can host one when only the disk is short.
        Check(new ChildWorktreeCheck.Ready(new("r", "", "main", Commit)).CanHostWorktree && new ChildWorktreeCheck.Refused(DelegationReasonCode.LowDisk).CanHostWorktree
            && new[] { DelegationReasonCode.NotGit, DelegationReasonCode.DetachedHead, DelegationReasonCode.UnbornBranch }.All(reason => !new ChildWorktreeCheck.Refused(reason).CanHostWorktree), "only a usable checkout can host one");
        return Task.CompletedTask;
    }

    internal static Task EachAgentHasItsModesAndItsWayToCommit()
    {
        // Each agent asks for its own modes, no wider than its pane's.
        SameList(DelegationProviders.StartingModes("claude"), ["plan", "manual", "acceptEdits", "auto", "fullAccess"], "Claude's modes");
        SameList(DelegationProviders.StartingModes("codex"), ["manual", "acceptEdits", "onRequest", "fullAccess"], "Codex's modes");
        Check(DelegationCoordinator.IsNoWider("acceptEdits", "onRequest", "codex") && !DelegationCoordinator.IsNoWider("fullAccess", "onRequest", "codex") && !DelegationCoordinator.IsNoWider("plan", "fullAccess", "codex")
            && !DelegationCoordinator.IsNoWider("onRequest", "auto", "claude"), "a mode is compared within its agent");
        // Only a Codex run whose tools can work gets the delegation tools; Antigravity never does.
        Check(DelegationProviders.AttachesDelegation("claude", "claude", "plan") && DelegationProviders.AttachesDelegation("claude", "codex", "acceptEdits") && DelegationProviders.AttachesDelegation("claude", "codex", "fullAccess")
            && !DelegationProviders.AttachesDelegation("claude", "codex", "manual") && !DelegationProviders.AttachesDelegation("claude", "codex", "onRequest") && !DelegationProviders.AttachesDelegation("claude", "gemini", "fullAccess")
            && !DelegationProviders.AttachesDelegation("shell", "claude", "plan"), "which runs get the delegation tools");
        Check(DelegationProviders.StartingModes("claude").All(mode => DelegationProviders.ChildCanWrite("claude", mode)) && !DelegationProviders.ChildCanWrite("codex", "manual") && DelegationProviders.ChildCanWrite("codex", "onRequest")
            && !DelegationProviders.ChildCanWrite("gemini", "plan") && DelegationProviders.ChildCanWrite("gemini", "acceptEdits"), "a child can write in every Claude mode and only the editing modes of the others");
        Check(DelegationProviders.CanCommit("claude", "plan") && DelegationProviders.AgentMayMerge("claude", "manual") && !DelegationProviders.CanCommit("codex", "acceptEdits") && DelegationProviders.CanCommit("gemini", "fullAccess")
            && !DelegationProviders.AgentMayMerge("codex", "acceptEdits") && DelegationProviders.AgentMayMerge("codex", "fullAccess") && DelegationProviders.CanSteer("claude", "claude") && !DelegationProviders.CanSteer("claude", "codex"), "only Claude commits and merges in any mode");
        Check(!ChildAutoCommit.ShouldCommit("claude", true, true, false) && new[] { "codex", "gemini" }.All(provider => ChildAutoCommit.ShouldCommit(provider, true, true, false) && !ChildAutoCommit.ShouldCommit(provider, false, true, false)
            && !ChildAutoCommit.ShouldCommit(provider, true, false, false) && !ChildAutoCommit.ShouldCommit(provider, true, true, true)) && ChildAutoCommit.Message("c1", 2) == "mighty: c1 report r2", "the app commits only for another agent's changed report");
        foreach (var name in new[] { ".env", ".ENV.production", "a/b/cert.pem", "x.key", "id_rsa.pub", "credentials", @"keys\deploy.key", "cert.PFX", "store.p12", "putty.ppk", "id_ed25519", "id_ed25519.pub",
                     "id_ecdsa", "id_dsa.pub", ".npmrc", "home/.pypirc", "secrets.json", "Secrets.yaml" }) Check(ChildAutoCommit.LooksSecret(name), name + " looks secret");
        foreach (var name in new[] { "environment.txt", "keyboard.swift", "a/.envoy/config", "my-credentials.txt", "npmrc", ".npmrc.example", "my-secrets.json", "secrets", "pfx.txt" }) Check(!ChildAutoCommit.LooksSecret(name), name + " does not");

        // A Claude child's words are as before; another agent's name no Claude pane and leave the commit to the app.
        var worktree = new ChildWorktree("c1", @"C:\w\c1", @"C:\w\c1", "mighty/c1", "main", Commit);
        Same(DelegationCoordinator.FirstInput("Fix the parser.\n", worktree), $"Another Claude pane in Mighty Claude delegated this task to you. You work in your own git worktree on the branch mighty/c1; the task is also saved in {worktree.TaskFile}.\n\nWhen you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to {worktree.ReportFile}: what you did, what is left and what your parent should check. Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.\n\nTask:\nFix the parser.\n", "a Claude child's first run");
        Same(DelegationCoordinator.HumanNote(worktree), $"[Mighty Claude] This request runs in its own git worktree on the branch mighty/c1, opened beside another Claude pane so that pane keeps working undisturbed. When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write a short report to {worktree.ReportFile}: what you did and what is left. The person who sent this merges your branch from the app. Do not merge, push or switch branches.", "a Claude child's note");
        var followUp = new FollowUp("f-1", "c1", "Also add tests.", DeliveryLane.Pending);
        Check(DelegationCoordinator.Text(followUp, worktree.ReportFile).EndsWith($"When you are done, commit your work on your branch and write your updated report to {worktree.ReportFile}.", StringComparison.Ordinal), "a Claude child's follow-up");
        foreach (var provider in new[] { "codex", "gemini" })
        {
            string[] texts = [DelegationCoordinator.FirstInput("Fix the parser.", worktree, provider), DelegationCoordinator.HumanNote(worktree, provider), DelegationCoordinator.HumanFirstRun("Fix the parser.", worktree, provider).Input, DelegationCoordinator.Text(followUp, worktree.ReportFile, provider)];
            foreach (var text in texts) Check(!text.Contains("Claude pane", StringComparison.Ordinal) && !text.Contains("commit your work", StringComparison.Ordinal) && text.Contains("Do not commit", StringComparison.Ordinal) && text.Contains(worktree.ReportFile, StringComparison.Ordinal), $"{provider}: {text}");
            Check(texts[0].StartsWith("Another pane in Mighty Claude delegated this task to you.", StringComparison.Ordinal) && texts[0].EndsWith("Task:\nFix the parser.", StringComparison.Ordinal)
                && texts[0].Contains(DelegationCoordinator.AppCommitNote(worktree), StringComparison.Ordinal) && texts[1].Contains(DelegationCoordinator.AppCommitNote(worktree), StringComparison.Ordinal), provider + "'s first run");
        }
        // A slash command runs alone, the note following it as the next request.
        Check(DelegationCoordinator.HumanFirstRun("/review the parser", worktree) is { Input: "/review the parser", FollowingNote: { } note } && note == DelegationCoordinator.HumanNote(worktree)
            && DelegationCoordinator.HumanFirstRun("Fix it.", worktree) is { FollowingNote: null, Input: var whole } && whole == "Fix it.\n\n" + DelegationCoordinator.HumanNote(worktree), "a slash command runs alone");
        // A child whose run works in a folder inside its worktree may also write its report there: Codex by a writable root, Antigravity by an added folder.
        var root = Path.Combine(Path.GetTempPath(), "w", "c1");
        var codexRun = ProviderCatalog.Arguments(new StartRunRequest("c1", "ws", "claude", "hi", [], Provider: "codex", Settings: new(PermissionMode: "acceptEdits")) { DelegationWorktreeRoot = root }, Path.GetTempPath());
        var readOnly = ProviderCatalog.Arguments(new StartRunRequest("c1", "ws", "claude", "hi", [], Provider: "codex", Settings: new(PermissionMode: "manual")) { DelegationWorktreeRoot = root }, Path.GetTempPath());
        var agyRun = ProviderCatalog.Arguments(new StartRunRequest("c1", "ws", "claude", "hi", [], Provider: "gemini", Settings: new(PermissionMode: "acceptEdits")) { DelegationWorktreeRoot = root }, Path.GetTempPath());
        Check(codexRun.Contains("sandbox_workspace_write.writable_roots=" + JsonSerializer.Serialize(new[] { root }, Wire.Json)) && !readOnly.Any(argument => argument.Contains("writable_roots", StringComparison.Ordinal))
            && agyRun.IndexOf("--add-dir") is var at && at >= 0 && agyRun[at + 1] == root, "the worktree root reaches the run");
        // The provider rides with the follow-up's child.
        var file = DelegationFile.Of([ChildRecord.Make("c1", "p", @"C:\w\c1", "main", Commit, "acceptEdits", "k", state: ChildState.Reported, provider: "codex")]) with { FollowUps = [followUp] };
        Same(file.DeliveryItem(followUp)?.Text, DelegationCoordinator.Text(followUp, ChildWorktree.ReportFileIn(@"C:\w\c1"), "codex"), "a Codex child's follow-up");
        return Task.CompletedTask;
    }

    internal static Task RecordsAndPanesCarryTheMarkAndTheParentsAgent()
    {
        // A record kept before the marks is an agent's Claude child, and the marks are stored only when they say something.
        var old = JsonSerializer.Deserialize<ChildRecord>("""{"id":"c1","parentSessionId":"p","worktreePath":"/w/c1","branch":"mighty/c1","parentBranch":"main","baseCommit":"abc","startingMode":"plan","requestKey":"k","followUpCount":0,"reportRevision":0,"state":"running"}""", Wire.Json)!;
        Check(old.StartedByHuman is null && old.NotifiesParent && old.Provider is null && old.Agent == "claude" && !JsonSerializer.Serialize(old, Wire.Json).Contains("startedByHuman", StringComparison.Ordinal)
            && !JsonSerializer.Serialize(old, Wire.Json).Contains("provider", StringComparison.Ordinal), "an old record is an agent's Claude child");
        var human = JsonSerializer.Deserialize<ChildRecord>(JsonSerializer.Serialize(old with { StartedByHuman = true, Provider = "codex" }, Wire.Json), Wire.Json)!;
        Check(human is { StartedByHuman: true, Provider: "codex", Agent: "codex" } && !human.NotifiesParent, "the marks are stored");
        Check(ChildRecord.Make("c", "p", "/w/c", "main", Commit, "plan", "k", provider: "claude").Provider is null, "Claude is stored as no provider");

        // A human's child takes its parent's model, effort and view; only a Claude one takes the style.
        foreach (var provider in new[] { "claude", "codex", "gemini" })
        {
            var parent = new RunSession { Id = "parent", WorkspaceId = "ws", Provider = provider, Model = "model-x", Settings = new(Effort: "high", PermissionMode: "acceptEdits"), AgentViewMode = "mighty", GraphViewMode = "structure", MightyStyle = "claude-plan", MightyStyleHash = "h1", MightyStyleSince = "2026-10-15T00:00:00Z" };
            var pane = new DelegationChildPane("c1", "parent", "acceptEdits", "/w/c1", StartedByHuman: true, Provider: provider);
            var made = DelegationPanes.ChildSession(pane, "ws", "Fix it", parent);
            Check(made is { Kind: "claude", Model: "model-x", AgentViewMode: "mighty", GraphViewMode: "structure", ParentSessionId: "parent", WorkingFolder: "/w/c1", MightyStyleSince: null } && made.Provider == provider && made.Settings is { Effort: "high", PermissionMode: "acceptEdits" }
                && (provider == "claude" ? made is { MightyStyle: "claude-plan", MightyStyleHash: "h1" } : made is { MightyStyle: null, MightyStyleHash: null }), provider + ": a human's child takes the parent's model and view");
            var agent = DelegationPanes.ChildSession(pane with { StartedByHuman = false }, "ws", "Fix it", parent);
            Check(agent is { Model: "default", AgentViewMode: "mighty", GraphViewMode: "structure", MightyStyle: null } && agent.Settings.Effort == "default" && agent.Provider == provider, provider + ": a delegate call's child shows the view only");
        }
        var orphan = DelegationPanes.ChildSession(new("c4", "parent", "auto", "/w/c4"), "ws", "t");
        Check(orphan is { AgentViewMode: null, GraphViewMode: null, Provider: "claude" }, "no parent leaves the defaults");

        // The human's own child shows at once while the human is still on its parent with nothing typed there.
        var workspace = new Workspace { Id = "ws", Path = Path.GetTempPath() };
        var state = new AppSnapshot { Workspaces = [workspace], Sessions = [new RunSession { Id = "parent", WorkspaceId = "ws" }], ActiveWorkspaceId = "ws", ActiveSessionId = "parent" };
        Check(DelegationPanes.AddingChild(state, new("c1", "parent", "plan", Path.GetTempPath(), StartedByHuman: true), "Fix it") is { ActiveSessionId: "c1" }, "the human's child shows at once");
        Check(DelegationPanes.AddingChild(state, new("c1", "parent", "plan", Path.GetTempPath()), "Fix it") is { ActiveSessionId: "parent" }, "a delegate call's child does not take the focus");
        var typing = state with { Sessions = [new RunSession { Id = "parent", WorkspaceId = "ws", Draft = "half a thought" }] };
        Check(DelegationPanes.AddingChild(typing, new("c1", "parent", "plan", Path.GetTempPath(), StartedByHuman: true), "Fix it") is { ActiveSessionId: "parent" }, "nor does the human's while they type in the parent");
        return Task.CompletedTask;
    }

    internal static async Task TheSendMakesAChildThatNeverNotifiesItsParent()
    {
        var fixture = await Make();
        try
        {
            var head = await fixture.Place.Git("rev-parse", "HEAD"); var text = "Try the other parser in a branch.\n";
            var child = Started(await fixture.Send(text));
            Check(child is { StartedByHuman: true, NotifiesParent: false, State: ChildState.Creating, ParentSessionId: "parent", StartingMode: "acceptEdits", ParentBranch: "main" } && child.BaseCommit == head, "the child is the human's, in the parent's mode");
            Check(await fixture.WaitFor(child.Id, ChildState.Running), "it runs");
            var saved = fixture.Store.Load();
            Check(saved.Children.Single() is { StartedByHuman: true, RunId: var runId, AwaitedRunId: var awaited } && runId == "send-run-of-" + child.Id && awaited == runId && saved.Copy(child.Id, DelegationCopyKind.Task)?.Text == text && saved.Notices.Count == 0, "the record is saved with its task and first run");
            Check(fixture.Host.CreatedPanes.SequenceEqual([new DelegationChildPane(child.Id, "parent", "acceptEdits", child.WorktreePath, true)]) && fixture.Host.StartedRuns.Count == 0, "the host made its pane and started nothing");
            Check(fixture.Sends.Started is [var (id, run)] && id == child.Id && run.FollowingNote is null && run.Input.StartsWith(text, StringComparison.Ordinal) && run.Input.Contains("[Mighty Claude]", StringComparison.Ordinal)
                && run.Input.Contains(child.Branch, StringComparison.Ordinal) && run.Input.Contains(ChildWorktree.ReportFileIn(child.WorktreePath), StringComparison.Ordinal), "the send's own start runs the human's text first");
            Check(await fixture.Place.Git("rev-parse", "refs/heads/" + child.Branch) == head && await fixture.Place.Git("symbolic-ref", "--short", "HEAD") == "main" && (await fixture.Place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "the parent's checkout is untouched");

            // Its awaited first run ends without a report: ended, and no notice.
            await fixture.Coordinator.ChildRunEnded(child.Id, "send-run-of-" + child.Id, ChildRunEnd.Finished);
            Check(fixture.Only.State == ChildState.Ended && fixture.Coordinator.File.Notices.Count == 0, "an end without a report sends no notice");
            // A later run ends with a committed report: reported, merge on its card, and still no notice.
            await fixture.Coordinator.ChildRunStarted(child.Id, "run-2");
            File.WriteAllText(Path.Combine(child.WorktreePath, "note.txt"), "Note\n");
            await fixture.GitIn(child.WorktreePath, "add", "note.txt"); await fixture.GitIn(child.WorktreePath, "commit", "-q", "-m", "Note");
            File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\n\nWrote the note.\n");
            Check(await fixture.Coordinator.ChildRunEnded(child.Id, "run-2", ChildRunEnd.Finished) is null && fixture.Only is { State: ChildState.Reported, ReportRevision: 1 } && fixture.Coordinator.File.Notices.Count == 0
                && fixture.Coordinator.File.HeldItemsByPane().Count == 0 && DelegationSidebar.Rows(fixture.Coordinator.File)[0].CardActions.SequenceEqual([DelegationCardAction.Merge, DelegationCardAction.Discard]), "a report reaches its card and no notice its parent");
        }
        finally { fixture.Remove(); }

        // A failed start is shown on its card with no notice, and the window hears of it; so does a first run that does not start.
        fixture = await Make();
        try
        {
            fixture.Host.PaneFails = true;
            var child = Started(await fixture.Send("Write a note."));
            Check(await fixture.WaitFor(child.Id, ChildState.Failed) && fixture.Sends.Started.Count == 0 && fixture.Sends.Failed.SequenceEqual([child.Id]) && fixture.Coordinator.File.Notices.Count == 0
                && DelegationSidebar.Rows(fixture.Coordinator.File)[0].CardActions.SequenceEqual([DelegationCardAction.Discard]), "a pane that could not be made fails the child, with no notice");
        }
        finally { fixture.Remove(); }
        fixture = await Make();
        try
        {
            fixture.Sends.RunFails = true;
            var child = Started(await fixture.Send("Write a note."));
            Check(await fixture.WaitFor(child.Id, ChildState.Failed) && fixture.Sends.Started.Select(run => run.ChildId).SequenceEqual([child.Id]) && fixture.Sends.Failed.SequenceEqual([child.Id]) && fixture.Coordinator.File.Notices.Count == 0, "a first run that did not start fails the child");
        }
        finally { fixture.Remove(); }

        // A slash command runs alone and the note follows it; a plan-first style starts the child in plan mode where its agent has one.
        fixture = await Make(parentMode: "fullAccess");
        try
        {
            var command = Started(await fixture.Send("/review the parser"));
            Check(await fixture.WaitFor(command.Id, ChildState.Running) && fixture.Sends.Started[0].Run is { Input: "/review the parser", FollowingNote: { } note } && note.StartsWith("[Mighty Claude]", StringComparison.Ordinal) && note.Contains(command.Branch, StringComparison.Ordinal), "a slash command runs alone");
            var planned = Started(await fixture.Send("Plan the parser.", planFirst: true));
            Check(planned.StartingMode == "plan" && await fixture.WaitFor(planned.Id, ChildState.Running) && fixture.Host.CreatedPanes[^1].Mode == "plan", "a plan-first style starts the child in plan mode");
        }
        finally { fixture.Remove(); }
        fixture = await Make(parentMode: "onRequest", provider: "codex");
        try { Check(Started(await fixture.Send("Plan it.", planFirst: true)).StartingMode == "onRequest", "Codex has no plan mode: the child keeps the parent's mode"); }
        finally { fixture.Remove(); }
    }

    internal static async Task TheParentsToolsAndCapsLeaveTheHumansChildAlone()
    {
        // The parent's tools neither see nor act on a child the human started.
        var fixture = await Make(file: DelegationFile.Of([OpenChild("agent"), OpenChild("human", byHuman: true)]));
        try
        {
            var bytes = fixture.Bytes();
            Check((await fixture.Call("list_children")).Children?.Select(child => child.Id).SequenceEqual(["agent"]) == true, "list_children leaves it out");
            Check(await fixture.Call("child_status", ("child", "human")) == DelegationResponse.Failure(DelegationCoordinator.NotYourChildMessage("human")) && (await fixture.Call("child_status", ("child", "agent"))).Status?.Id == "agent", "child_status does not know it");
            Check(await fixture.Call("merge", ("child", "human"), ("expected_head", new string('b', 40))) == DelegationResponse.Refusal(DelegationReasonCode.HumanStarted)
                && await fixture.Call("follow_up", ("child", "human"), ("text", "Also do this.")) == DelegationResponse.Refusal(DelegationReasonCode.HumanStarted) && fixture.Bytes().SequenceEqual(bytes), "merge and follow_up are refused with human_started");
            Check(TextOfRefusal(DelegationReasonCode.MergeHumanOnly) == "Refused: merge_human_only (in this permission mode only the human merges, from the child's card in the app; tell the user the child is ready). Nothing was changed."
                && TextOfRefusal(DelegationReasonCode.ClaudeOnly) == "Refused: claude_only (this pane's agent cannot use the delegation tools). Nothing was changed." && TextOfRefusal(DelegationReasonCode.HumanStarted) == "Refused: human_started. Nothing was changed.", "a refusal whose name says too little gets a word on it");
            // A Codex parent outside fullAccess merges only from the card.
            Check(await fixture.Coordinator.Handle(Request("merge", ("child", "agent"), ("expected_head", Commit)), new("parent", Provider: "codex", Delegation: true, PermissionMode: "acceptEdits")) == DelegationResponse.Refusal(DelegationReasonCode.MergeHumanOnly), "a Codex acceptEdits merge is the human's");
        }
        finally { fixture.Remove(); }

        // Human and agent children each have their own cap of three.
        fixture = await Make(file: DelegationFile.Of(new[] { "a", "b", "c" }.Select(id => OpenChild(id)).ToList()));
        try
        {
            var child = Started(await fixture.Send("One of mine."));
            Check(await fixture.WaitFor(child.Id, ChildState.Running) && await fixture.Call("delegate", ("task", "One more."), ("mode", "plan")) == DelegationResponse.Refusal(DelegationReasonCode.WidthCap), "three agent children leave room for the human's");
        }
        finally { fixture.Remove(); }
        fixture = await Make(file: DelegationFile.Of(new[] { "a", "b", "c" }.Select(id => OpenChild(id, byHuman: true)).ToList()));
        try
        {
            var bytes = fixture.Bytes();
            Check(await fixture.Send("One more.") == new HumanChildStart.Refused(DelegationReasonCode.WidthCap), "a fourth of the human's is refused");
            await fixture.NothingMade(bytes);
            Check((await fixture.Call("delegate", ("task", "Split this."), ("mode", "plan"))).Child is { } delegated && await fixture.WaitFor(delegated.Id, ChildState.Running), "three of the human's leave room for the parent's delegate");
        }
        finally { fixture.Remove(); }

        // A folder that cannot host a worktree, and delegate's own limits, are refused and nothing is made.
        foreach (var (commits, reason) in new (int?, DelegationReasonCode)[] { (null, DelegationReasonCode.NotGit), (0, DelegationReasonCode.UnbornBranch) })
        {
            fixture = await Make(commits: commits);
            try { var bytes = fixture.Bytes(); Check(await fixture.Send("Try it.") == new HumanChildStart.Refused(reason), DelegationNames.Of(reason)); await fixture.NothingMade(bytes, hasGit: commits is not null); }
            finally { fixture.Remove(); }
        }
        fixture = await Make(freeBytes: 1_000_000_000);
        try { var bytes = fixture.Bytes(); Check(await fixture.Send("Try it.") == new HumanChildStart.Refused(DelegationReasonCode.LowDisk), "low_disk"); await fixture.NothingMade(bytes); }
        finally { fixture.Remove(); }
        fixture = await Make();
        try
        {
            var bytes = fixture.Bytes();
            fixture.Host.Set(new("child", "auto", fixture.Place.Repo, ParentSessionId: "parent", RunId: "run-c", Activity: DelegationPaneActivity.Running));
            fixture.Host.Set(new("shell", "acceptEdits", fixture.Place.Repo, Kind: "shell", RunId: "run-x", Activity: DelegationPaneActivity.Running));
            Check(await fixture.Send("Split further.", from: "child") == new HumanChildStart.Refused(DelegationReasonCode.ChildCannotDelegate) && await fixture.Send("Try it.", from: "shell") == new HumanChildStart.Refused(DelegationReasonCode.ClaudeOnly)
                && await fixture.Send("Try it.", from: "gone") == new HumanChildStart.Refused(DelegationReasonCode.ParentClosed), "a child, a pane that is no agent pane and a closed pane have no new worktree");
            Check(await fixture.Send("  \n") == new HumanChildStart.Failed(Locale.Get("delegation.human.error.invalidTask")) && await fixture.Send(new string('x', DelegationFileStore.MaximumCopyBytes + 1)) == new HumanChildStart.Failed(Locale.Get("delegation.human.error.invalidTask")), "no text is no task");
            await fixture.NothingMade(bytes);
        }
        finally { fixture.Remove(); }
        fixture = await Make(switchOn: false);
        try { var bytes = fixture.Bytes(); Check(await fixture.Send("Try it.") == new HumanChildStart.Failed(Locale.Get("delegation.human.error.switchedOff")), "switched off"); await fixture.NothingMade(bytes); }
        finally { fixture.Remove(); }
        foreach (var (provider, mode) in new[] { ("codex", "manual"), ("gemini", "manual"), ("gemini", "plan") })
        {
            fixture = await Make(parentMode: mode, provider: provider);
            try { var bytes = fixture.Bytes(); Check(await fixture.Send("Try it.") == new HumanChildStart.Failed(Locale.Get("delegation.human.error.readOnlyMode")), $"{provider} {mode} makes no child"); await fixture.NothingMade(bytes); }
            finally { fixture.Remove(); }
        }
    }

    private static string? TextOfRefusal(DelegationReasonCode reason) => DelegationMcp.Result(DelegationResponse.Refusal(reason))["content"]?[0]?["text"]?.GetValue<string>();

    internal static async Task TheAppCommitsACodexOrAntigravityChildsWork()
    {
        // A Codex or Antigravity parent's child runs its agent and leaves its commit to the app.
        foreach (var provider in new[] { "codex", "gemini" })
        {
            var each = await Make(provider: provider);
            try
            {
                var child = Started(await each.Send("Try the other parser."));
                Check(child.Provider == provider && child.Agent == provider && await each.WaitFor(child.Id, ChildState.Running) && each.Host.CreatedPanes.Select(pane => pane.Provider).SequenceEqual([provider]) && each.Store.Load().Children.Single().Provider == provider, provider + "'s child runs its agent");
                var run = each.Sends.Started.Single().Run.Input;
                Check(run.StartsWith("Try the other parser.", StringComparison.Ordinal) && !run.Contains("Claude pane", StringComparison.Ordinal) && !run.Contains("commit your work", StringComparison.Ordinal) && run.Contains("Do not commit", StringComparison.Ordinal), provider + "'s note leaves the commit to the app");
            }
            finally { each.Remove(); }
        }

        var fixture = await Make(provider: "codex");
        try
        {
            File.WriteAllText(Path.Combine(fixture.Place.Repo, ".gitignore"), "*.log\n");
            await fixture.Place.Git("add", ".gitignore"); await fixture.Place.Git("commit", "-q", "-m", "ignore logs");
            var start = await fixture.Place.Git("rev-parse", "HEAD");
            var child = Started(await fixture.Send("Add a note."));
            Check(await fixture.WaitFor(child.Id, ChildState.Running), "the child runs");
            var worktree = child.WorktreePath; var report = ChildWorktree.ReportFileIn(worktree);
            Task<string> Head() => fixture.GitIn(worktree, "rev-parse", "HEAD");
            // A run that leaves changes but no report: nothing is committed.
            File.WriteAllText(Path.Combine(worktree, "file-0.txt"), "changed\n"); File.WriteAllText(Path.Combine(worktree, "note.txt"), "Note\n"); File.WriteAllText(Path.Combine(worktree, "build.log"), "noise\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "send-run-of-" + child.Id, ChildRunEnd.Finished);
            Check(await Head() == start && fixture.Only.State == ChildState.Ended, "no report, no commit");
            // A run that writes its report but is stopped: nothing is committed and the report is not new yet.
            await fixture.Coordinator.ChildRunStarted(child.Id, "run-stopped");
            File.WriteAllText(report, "# Report\n\nAdded the note.\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "run-stopped", ChildRunEnd.Stopped);
            Check(await Head() == start && fixture.Only.ReportRevision == 0, "a stopped run commits nothing");
            // The next run finishes normally: the app commits what .gitignore lets in, then records the report at that head.
            await fixture.Coordinator.ChildRunStarted(child.Id, "run-2");
            Check(await fixture.Coordinator.ChildRunEnded(child.Id, "run-2", ChildRunEnd.Finished) is null, "no notice for the human's child");
            var head = await Head();
            Check(head != start && await fixture.GitIn(worktree, "log", "-1", "--format=%s") == ChildAutoCommit.Message(child.Id, 1) && await fixture.GitIn(worktree, "rev-parse", "HEAD^") == start
                && (await fixture.GitIn(worktree, "diff", "--name-only", start, head)).Split('\n').SequenceEqual(["file-0.txt", "note.txt"]) && (await fixture.GitIn(worktree, "status", "--porcelain")).Length == 0, "the app's one commit holds what .gitignore lets in");
            Check(fixture.Only is { State: ChildState.Reported, ReportRevision: 1 } && fixture.Only.ReportHead == head && fixture.Host.Notes.Count == 0, "the report is recorded at that head");
            // The human merges it from its card into the parent's branch.
            Check(await fixture.Coordinator.MergeFromCard(child.Id) is ChildMergeOutcome.Merged && await fixture.Place.Git("rev-parse", "HEAD") == head && File.Exists(Path.Combine(fixture.Place.Repo, "note.txt")), "the card merges it");
            // A run that ends with the same report commits nothing more.
            await fixture.Coordinator.ChildRunStarted(child.Id, "run-3");
            File.WriteAllText(Path.Combine(worktree, "note.txt"), "more\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "run-3", ChildRunEnd.Finished);
            Check(await Head() == head, "the same report commits nothing");
        }
        finally { fixture.Remove(); }

        // New files that look secret or are large stay uncommitted, and the pane names them.
        fixture = await Make(provider: "gemini");
        try
        {
            var child = Started(await fixture.Send("Add the config."));
            Check(await fixture.WaitFor(child.Id, ChildState.Running), "the child runs");
            var worktree = child.WorktreePath; Directory.CreateDirectory(Path.Combine(worktree, "keys"));
            string[] secrets = [".env.local", "keys/server.pem", "keys/deploy.key", "id_rsa", "credentials.json"];
            foreach (var name in secrets.Append("config.json")) File.WriteAllText(Path.Combine(worktree, name), "x\n");
            File.WriteAllBytes(Path.Combine(worktree, "big.bin"), new byte[ChildAutoCommit.MaximumNewFileBytes + 1]);
            File.WriteAllText(Path.Combine(worktree, "file-0.txt"), "changed\n"); File.WriteAllText(ChildWorktree.ReportFileIn(worktree), "# Report\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "send-run-of-" + child.Id, ChildRunEnd.Finished);
            SameList((await fixture.GitIn(worktree, "show", "--name-only", "--format=", "HEAD")).Split('\n').Order(StringComparer.Ordinal), ["config.json", "file-0.txt"], "the commit");
            SameList((await fixture.GitIn(worktree, "ls-files", "--others", "--exclude-standard")).Split('\n').Order(StringComparer.Ordinal), secrets.Append("big.bin").Order(StringComparer.Ordinal), "what stayed out");
            Check(fixture.Host.Notes is [.., var (sessionId, text)] && sessionId == child.Id && text.StartsWith(Locale.Get("delegation.autoCommit.leftOut", new Dictionary<string, string> { ["count"] = "6", ["paths"] = "" }), StringComparison.Ordinal)
                && secrets.Append("big.bin").All(name => text.Contains(name, StringComparison.Ordinal)) && fixture.Only.State == ChildState.Reported, "the pane names what stayed out");
        }
        finally { fixture.Remove(); }

        // An unresolved conflict is never committed, and the pane says so; resolved, the next end commits the merge.
        fixture = await Make(provider: "codex");
        try
        {
            await fixture.Place.Git("checkout", "-q", "-b", "other"); File.WriteAllText(Path.Combine(fixture.Place.Repo, "file-0.txt"), "theirs\n");
            await fixture.Place.Git("commit", "-q", "-am", "theirs"); await fixture.Place.Git("checkout", "-q", "main");
            var child = Started(await fixture.Send("Change file-0."));
            Check(await fixture.WaitFor(child.Id, ChildState.Running), "the child runs");
            var worktree = child.WorktreePath;
            File.WriteAllText(Path.Combine(worktree, "file-0.txt"), "ours\n"); await fixture.GitIn(worktree, "commit", "-q", "-am", "ours");
            var ours = await fixture.GitIn(worktree, "rev-parse", "HEAD");
            Check(await DelegationGit.Run(["merge", "-q", "other"], worktree) is { ExitCode: not 0 }, "the merge conflicts");
            File.WriteAllText(ChildWorktree.ReportFileIn(worktree), "# Report\n\nMerged other.\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "send-run-of-" + child.Id, ChildRunEnd.Finished);
            Check(await fixture.GitIn(worktree, "rev-parse", "HEAD") == ours && (await fixture.GitIn(worktree, "ls-files", "-u")).Length > 0 && fixture.Only is { State: ChildState.Ended, ReportRevision: 0, ReportDigest: null }
                && fixture.Host.Notes.SequenceEqual([(child.Id, Locale.Get("delegation.autoCommit.unmerged"))]), "nothing is committed and the pane says why");
            File.WriteAllText(Path.Combine(worktree, "file-0.txt"), "both\n"); await fixture.GitIn(worktree, "add", "file-0.txt");
            await fixture.Coordinator.ChildRunStarted(child.Id, "run-2");
            await fixture.Coordinator.ChildRunEnded(child.Id, "run-2", ChildRunEnd.Finished);
            var head = await fixture.GitIn(worktree, "rev-parse", "HEAD");
            Check(head != ours && (await fixture.GitIn(worktree, "rev-list", "--parents", "-n", "1", "HEAD")).Split(' ').Length == 3 && fixture.Only.ReportHead == head, "resolved, the merge is committed");
        }
        finally { fixture.Remove(); }

        // The app never commits for a Claude child.
        fixture = await Make();
        try
        {
            var start = await fixture.Place.Git("rev-parse", "HEAD");
            var child = Started(await fixture.Send("Add a note."));
            Check(await fixture.WaitFor(child.Id, ChildState.Running), "the child runs");
            File.WriteAllText(Path.Combine(child.WorktreePath, "note.txt"), "Note\n"); File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "send-run-of-" + child.Id, ChildRunEnd.Finished);
            Check(await fixture.GitIn(child.WorktreePath, "rev-parse", "HEAD") == start && await fixture.GitIn(child.WorktreePath, "status", "--porcelain") == "?? note.txt" && fixture.Only.ReportHead == start, "a Claude child commits its own work");
        }
        finally { fixture.Remove(); }
    }

    internal static async Task GitSaysWhyItGaveNoResultAndOnlyItsOwnLockGoes()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository(1);
            var head = await place.Git("rev-parse", "HEAD");
            // A git that does not read its input closes the pipe: told apart, with its own result.
            var unread = await DelegationGit.RunDetailed(["rev-parse", "HEAD"], place.Repo, 60, new string('x', 8_000_000));
            Check(unread is { Failure: GitFailure.BrokenPipe, Result: { ExitCode: 0 } result } && result.Stdout.Trim() == head && await DelegationGit.Run(["rev-parse", "HEAD"], place.Repo, 60, new string('x', 8_000_000)) is null, "a broken pipe is told apart");
            // Input is written while the output is read, within the deadline: one too short ends it as a timeout.
            var big = new string('x', 20_000_000);
            var late = await DelegationGit.RunDetailed(["hash-object", "--stdin"], place.Repo, 0.01, big);
            Check(late is { Failure: GitFailure.TimedOut, Result: null }, "the app's own kill on its deadline is a timeout: " + late);
            Check(await DelegationGit.RunDetailed(["hash-object", "--stdin"], place.Repo, 60, "hello\n") is { Failure: GitFailure.None, Result: { ExitCode: 0, Stdout: var hash } } && DelegationGit.IsObjectName(hash.Trim()), "input reaches git");
            // A lock git refuses on is not the app's: it stays. One the app's killed git left is removed.
            var lockFile = Path.Combine(place.Repo, ".git", "index.lock");
            File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "changed\n"); File.WriteAllText(lockFile, "");
            Check(await ChildAutoCommit.WriteStep(place.Repo, lockFile, ["add", "-u"], null, 60) is { } refused && refused.Contains("index.lock", StringComparison.Ordinal) && File.Exists(lockFile), "another git's lock stays");
            Check(await ChildAutoCommit.WriteStep(place.Repo, lockFile, ["hash-object", "--stdin"], big, 0.01) == "Git did not finish in time." && !File.Exists(lockFile), "the lock of the app's own killed git goes");
        }
        finally { place.Remove(); }
    }

    internal static async Task ACardMergeThatConflictsAsksTheChildOncePerReport()
    {
        var fixture = await Make(parentMode: "fullAccess");
        try
        {
            var child = Started(await fixture.Send("Change file-0."));
            Check(await fixture.WaitFor(child.Id, ChildState.Running), "the child runs");
            Check(await fixture.Coordinator.ConflictResolution(child.Id) is null, "a child that has not reported is not asked");
            File.WriteAllText(Path.Combine(child.WorktreePath, "file-0.txt"), "ours\n"); await fixture.GitIn(child.WorktreePath, "commit", "-q", "-am", "ours");
            File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\n");
            await fixture.Coordinator.ChildRunEnded(child.Id, "send-run-of-" + child.Id, ChildRunEnd.Finished);
            var reported = fixture.Only;
            Check(reported.State == ChildState.Reported && await fixture.Coordinator.ConflictResolution(child.Id) is { } text && text == ChildMerge.ConflictPrompt(reported) && await fixture.Coordinator.ConflictResolution(child.Id) is null, "asked once per report");
            Check(ChildMerge.ConflictPrompt(reported).StartsWith($"[Mighty Claude] Merging your branch {reported.Branch} into main from the app was refused: the two conflict.", StringComparison.Ordinal)
                && ChildMerge.ConflictPrompt(reported).Contains(ChildWorktree.ReportFileIn(reported.WorktreePath), StringComparison.Ordinal), "the request names the branches and the report");
            await fixture.Coordinator.ConflictResolutionNotStarted(child.Id);
            Check(await fixture.Coordinator.ConflictResolution(child.Id) is not null, "a request that did not start is asked again");
        }
        finally { fixture.Remove(); }
    }
}
