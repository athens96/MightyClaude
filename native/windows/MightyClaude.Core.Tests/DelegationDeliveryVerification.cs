using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// The app side as the delegation checks see it: the panes a check sets up, which take an item the way a pane does. A steer
/// reaches a running pane's run; a queued item or a started run starts a new run in a pane that is not running. Every
/// hand-over is recorded with the delegation file as it was on disk at that moment. A check may have the next hand-overs
/// refused, hold a child's pane or a pane's state back behind a gate, and name a child's first run itself.
/// </summary>
internal sealed class DelegationTestHost(DelegationFileStore? store = null, string runs = "run") : IDelegationHost
{
    /// <summary>One hand-over: by <paramref name="Route"/>, or a started run when null.</summary>
    internal sealed record Handed(string SessionId, DeliveryRoute? Route, string Input, DelegationFile? OnDisk, string? RunId);

    private readonly object gate = new();
    private readonly Dictionary<string, DelegationPaneState> panes = [];
    private readonly List<Handed> log = [];
    private readonly List<DelegationChildPane> created = [];
    private readonly List<(string SessionId, string Input)> firstRuns = [];
    private readonly List<string> stops = [], calls = [];
    private readonly List<(string SessionId, string Text)> notes = [];
    private readonly Queue<DelegationPaneActivity?> refusals = new();
    private int started;
    /// <summary>Names a child's first run; null hands a started run over as a pane takes any input.</summary>
    internal Func<string, string>? FirstRun;
    internal bool MakesPanes, PaneFails, RunFails;
    internal TaskCompletionSource? PaneGate;
    /// <summary>Awaited before a pane's state is answered.</summary>
    internal Func<string, Task>? StateGate;
    /// <summary>Called with the child's id and its first run's id while the run is being started.</summary>
    internal Func<string, string, Task>? OnFirstRun;
    private readonly Dictionary<string, DelegationAgentAvailability> agents = [];
    private readonly List<string> asked = [];
    /// <summary>Each agent the coordinator asked about, in order.</summary>
    internal List<string> AvailabilityAsked { get { lock (gate) return [.. asked]; } }
    /// <summary>What the host last learnt of <paramref name="provider"/>'s CLI; anything not set is ready.</summary>
    internal void SetAgent(string provider, DelegationAgentAvailability availability) { lock (gate) agents[provider] = availability; }

    internal List<Handed> AllHanded { get { lock (gate) return [.. log]; } }
    /// <summary>The inputs that reached a pane.</summary>
    internal List<Handed> Taken => AllHanded.Where(handed => handed.RunId is not null).ToList();
    internal List<DelegationChildPane> CreatedPanes { get { lock (gate) return [.. created]; } }
    internal List<(string SessionId, string Input)> StartedRuns { get { lock (gate) return [.. firstRuns]; } }
    internal List<string> Stops { get { lock (gate) return [.. stops]; } }
    internal List<string> Calls { get { lock (gate) return [.. calls]; } }
    /// <summary>The app's notes in panes' transcripts, in order.</summary>
    internal List<(string SessionId, string Text)> Notes { get { lock (gate) return [.. notes]; } }

    internal void Set(DelegationPaneState pane) { lock (gate) panes[pane.SessionId] = pane; }
    internal void Set(string sessionId, DelegationPaneActivity activity, string? runId = null, string provider = "claude", string? folder = null) =>
        Set(new(sessionId, "auto", folder ?? Path.GetTempPath(), Provider: provider, RunId: runId, Activity: activity));
    internal void Close(string sessionId) { lock (gate) panes.Remove(sessionId); }
    internal DelegationPaneState? Pane(string sessionId) { lock (gate) return panes.GetValueOrDefault(sessionId); }
    /// <summary>Refuses the next hand-overs, one per entry; each leaves its pane in that activity, or as it is for null.</summary>
    internal void RefuseNext(params DelegationPaneActivity?[] after) { lock (gate) foreach (var activity in after) refusals.Enqueue(activity); }

    public async Task<bool> CreatePane(DelegationChildPane pane)
    {
        lock (gate) { created.Add(pane); calls.Add("createPane " + pane.SessionId); }
        if (PaneGate is { } wait) await wait.Task;
        lock (gate)
        {
            if (!MakesPanes || PaneFails) return false;
            panes[pane.SessionId] = new(pane.SessionId, pane.Mode, pane.Folder, Provider: pane.Provider, ParentSessionId: pane.ParentSessionId);
            return true;
        }
    }

    public async Task<string?> StartRun(string sessionId, string input)
    {
        lock (gate) calls.Add("startRun " + sessionId);
        if (FirstRun is not { } name) return Hand(input, sessionId, null);
        lock (gate) { firstRuns.Add((sessionId, input)); if (RunFails) return null; }
        var runId = name(sessionId);
        if (OnFirstRun is { } hook) await hook(sessionId, runId);
        return runId;
    }

    public Task<string?> Deliver(string input, string sessionId, DeliveryRoute route)
    {
        lock (gate) calls.Add("deliver " + sessionId);
        return Task.FromResult(Hand(input, sessionId, route));
    }

    public async Task<DelegationPaneState?> PaneState(string sessionId)
    {
        lock (gate) calls.Add("paneState " + sessionId);
        if (StateGate is { } wait) await wait(sessionId);
        return Pane(sessionId);
    }

    public Task Note(string text, string sessionId)
    {
        lock (gate) notes.Add((sessionId, text));
        return Task.CompletedTask;
    }

    public Task<DelegationAgentAvailability> AgentAvailability(string provider)
    {
        lock (gate) { asked.Add(provider); return Task.FromResult(agents.GetValueOrDefault(provider, DelegationAgentAvailability.Ready)); }
    }

    public Task StopRun(string sessionId)
    {
        lock (gate)
        {
            calls.Add("stopRun " + sessionId);
            var there = panes.TryGetValue(sessionId, out var pane) && Directory.Exists(pane.Folder);
            stops.Add($"{sessionId} {(there ? "with" : "without")} its folder");
            if (pane is not null) panes[sessionId] = pane with { Activity = DelegationPaneActivity.Idle };
        }
        return Task.CompletedTask;
    }

    private string? Hand(string input, string sessionId, DeliveryRoute? route)
    {
        DelegationFile? onDisk = null;
        try { onDisk = store?.Load(); } catch (Exception ex) when (ex is DelegationFileException or IOException or UnauthorizedAccessException) { }
        lock (gate)
        {
            string? runId = null;
            if (refusals.TryDequeue(out var after)) { if (after is { } activity && panes.TryGetValue(sessionId, out var refused)) panes[sessionId] = refused with { Activity = activity }; }
            else if (panes.TryGetValue(sessionId, out var pane))
            {
                if (route == DeliveryRoute.Steer) runId = pane.Activity == DelegationPaneActivity.Running ? pane.RunId : null;
                else if (pane.Activity != DelegationPaneActivity.Running)
                {
                    runId = $"{runs}-{++started}";
                    panes[sessionId] = pane with { RunId = runId, Activity = DelegationPaneActivity.Running };
                }
            }
            log.Add(new(sessionId, route, input, onDisk, runId));
            return runId;
        }
    }
}

/// <summary>
/// Delivery and follow-ups, ported from the Mac's DelegationDeliveryTests and DelegationFollowUpTests: each notice goes to its
/// child's parent pane and each follow-up to its child's pane, exactly once, by steer, by the next run, or held.
/// </summary>
internal static class DelegationDeliveryVerification
{
    private const DelegationPaneActivity Running = DelegationPaneActivity.Running, Finished = DelegationPaneActivity.Finished, Idle = DelegationPaneActivity.Idle;

    /// <summary>
    /// A profile in a temp folder with the delegation file, and a coordinator over it. The pane "parent" has the running
    /// children c1, c2 and c3, each on its first run "r-&lt;id&gt;", which delegate started. No child has a REPORT.md, so each
    /// run's end sends one ended_without_report notice.
    /// </summary>
    private sealed class Fixture
    {
        internal string Base { get; } = Verification.Temp();
        internal DelegationFileStore Store { get; }
        internal DelegationTestHost Host { get; private set; }
        internal DelegationCoordinator Coordinator { get; private set; }

        internal Fixture(DelegationPaneActivity? parent, DelegationFile? file = null, Func<Fixture, DelegationFile>? made = null)
        {
            Store = new(Path.Combine(Base, "profile"));
            Store.Save(file ?? made?.Invoke(this) ?? DelegationFile.Of(new[] { "c1", "c2", "c3" }.Select(id => Child(id))));
            Host = new(Store);
            if (parent is { } activity) Host.Set("parent", activity, activity == Idle ? null : "p-run");
            Coordinator = Launch(Host);
        }

        internal DelegationCoordinator Launch(DelegationTestHost host) => new(Store, host, new ChildWorktreeMaker(Path.Combine(Base, "worktrees"), _ => 50_000_000_000), () => true);
        /// <summary>The app quits and opens again from the same profile, its runs named after the launch.</summary>
        internal async Task<(DelegationTestHost Host, DelegationCoordinator Coordinator)> Relaunch(string launch)
        {
            Host = new(Store, launch + "-run"); Coordinator = Launch(Host);
            await Coordinator.InterruptRunsKilledByQuit();
            return (Host, Coordinator);
        }
        internal ChildRecord Child(string id, ChildState state = ChildState.Running) =>
            ChildRecord.Make(id, "parent", Path.Combine(Base, "worktrees", id), "main", Commit, "acceptEdits", "key-" + id, state: state) with { RunId = "r-" + id, AwaitedRunId = "r-" + id };
        internal void Remove() => RemoveFolder(Base);

        /// <summary>The child's run "r-&lt;id&gt;" finished: its notice, as it was made.</summary>
        internal async Task<Notice> End(string id) => await Coordinator.ChildRunEnded(id, "r-" + id, ChildRunEnd.Finished) ?? throw new InvalidOperationException("no notice for " + id);
        internal Notice? Saved(Notice notice) => Store.Load().Notices.Find(saved => saved.Id == notice.Id);
        internal static Notice Held(Notice notice) => notice with { Lane = DeliveryLane.Held };
        internal Task<DelegationResponse> FollowUp(string child, string text) => Coordinator.Handle(Request("follow_up", ("child", child), ("text", text)), Parent);
        internal string TextOf(string followUpId) => DelegationCoordinator.Text(Coordinator.File.FollowUps.Single(followUp => followUp.Id == followUpId), ChildWorktree.ReportFileIn(Path.Combine(Base, "worktrees", "c1")));
        /// <summary>Every one of <paramref name="ids"/> reached a pane in exactly one input.</summary>
        internal void EachTakenOnce(params string[] ids) { foreach (var id in ids) Check(Host.Taken.Count(handed => handed.Input.Contains(id, StringComparison.Ordinal)) == 1, id + " reached a pane exactly once"); }
    }

    private static DelegationRelease Released(string runId, params string[] ids) => new DelegationRelease.Released(runId, ids);
    private static bool Is(DelegationRelease release, DelegationRelease expected) => expected is DelegationRelease.Released wanted
        ? release is DelegationRelease.Released was && was.RunId == wanted.RunId && was.ItemIds.SequenceEqual(wanted.ItemIds) : release == expected;
    private static string Texts(IEnumerable<Notice> notices, params string[] more) => string.Join("\n\n", notices.Select(DelegationCoordinator.Text).Concat(more));
    private static readonly DelegationRelease NothingHeld = new DelegationRelease.NothingHeld(), NotStarted = new DelegationRelease.NotStarted();

    internal static async Task NoticesAreSteeredQueuedOrHeld()
    {
        // A pane gets items on its own only while it runs Claude or is idle after a normal finish; a running Codex or Antigravity
        // turn takes no text, so its end offers them again.
        DelegationPaneState Pane(DelegationPaneActivity activity, string kind = "claude", string provider = "claude") => new("p", "auto", Path.GetTempPath(), kind, provider, RunId: "p-run", Activity: activity);
        Check(DelegationCoordinator.Automatic(Pane(Running)) == new DelegationCoordinator.AutomaticDelivery.Route(DeliveryRoute.Steer) && DelegationCoordinator.Automatic(Pane(Finished)) == new DelegationCoordinator.AutomaticDelivery.Route(DeliveryRoute.Queue)
            && DelegationCoordinator.Automatic(Pane(Idle)) is DelegationCoordinator.AutomaticDelivery.Hold && DelegationCoordinator.Automatic(Pane(Running, provider: "codex")) is DelegationCoordinator.AutomaticDelivery.Later
            && DelegationCoordinator.Automatic(Pane(Running, provider: "gemini")) is DelegationCoordinator.AutomaticDelivery.Later && DelegationCoordinator.Automatic(Pane(Running, kind: "terminal")) is DelegationCoordinator.AutomaticDelivery.Hold
            && DelegationCoordinator.Automatic(null) is DelegationCoordinator.AutomaticDelivery.Hold, "the automatic delivery");
        var sample = new Notice("n-1", "c-1", 2, NoticeKind.Reported, DeliveryLane.Pending);
        Same(DelegationCoordinator.Text(sample), "[Mighty Claude notice n-1] Your child c-1 reported revision 2. Read the report with child_status; merge it with merge when it is right.", "a reported notice's words");
        Same(DelegationCoordinator.Text(sample with { Kind = NoticeKind.EndedWithoutReport }), "[Mighty Claude notice n-1] Your child c-1's run ended without a new report; its report revision is still 2. Call child_status to see where it stands.", "an ended notice's words");
        Same(DelegationCoordinator.Text(sample with { Kind = NoticeKind.FailedToStart }), "[Mighty Claude notice n-1] Your child c-1 could not be started. Only a human can discard it, from its card.", "a failed start's words");
        Same(DelegationCoordinator.Text(new FollowUp("f-1", "c-1", "Also add tests.\n", DeliveryLane.Pending), @"C:\w\c-1\.mighty-delegation\REPORT.md"),
            "[Mighty Claude follow-up f-1] Your parent sent you this follow-up:\n\nAlso add tests.\n\n\nWhen you are done, commit your work on your branch and write your updated report to C:\\w\\c-1\\.mighty-delegation\\REPORT.md.", "a follow-up's words");
        Check(System.Text.RegularExpressions.Regex.IsMatch(DelegationCoordinator.Now(), @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z"), "a receipt's time is ISO 8601 to the second");

        // A notice is steered into the parent's running Claude run once, and saved delivered before the run gets it.
        var fixture = new Fixture(Running);
        try
        {
            var notice = await fixture.End("c1");
            var handed = fixture.Host.AllHanded.Single();
            Check(handed is { SessionId: "parent", Route: DeliveryRoute.Steer, RunId: "p-run" } && handed.Input == DelegationCoordinator.Text(notice) && handed.Input.Contains(notice.Id, StringComparison.Ordinal) && handed.Input.Contains("child_status", StringComparison.Ordinal), "steered into the running run");
            Check(handed.OnDisk?.Notices.Find(written => written.Id == notice.Id) is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Steer, RunId: "p-run" } }, "on disk before the run got it: delivered, by steer into the running run");
            Check(fixture.Saved(notice) is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Steer, RunId: "p-run", Time.Length: > 0 } } && fixture.Coordinator.File == fixture.Store.Load(), "saved delivered with its receipt");
            // Never twice: the same end again, or another pass, hands nothing more.
            Check(await fixture.Coordinator.ChildRunEnded("c1", "r-c1", ChildRunEnd.Finished) is null, "the same end again sends nothing");
            await fixture.Coordinator.DeliverPending();
            Check(await fixture.Coordinator.Send("Go on.", "parent") == NothingHeld && fixture.Host.AllHanded.Count == 1, "never twice");
        }
        finally { fixture.Remove(); }

        // A notice starts the next run of a parent idle after a normal finish, and the next is steered into it.
        fixture = new Fixture(Finished);
        try
        {
            var first = await fixture.End("c1"); var second = await fixture.End("c2");
            var handed = fixture.Host.AllHanded;
            Check(handed.Select(item => item.Route).SequenceEqual([DeliveryRoute.Queue, DeliveryRoute.Steer]) && handed.Select(item => item.RunId).SequenceEqual(["run-1", "run-1"])
                && handed.Select(item => item.Input).SequenceEqual([DelegationCoordinator.Text(first), DelegationCoordinator.Text(second)]), "the first starts the next run and the second is steered into it");
            Check(handed[0].OnDisk?.Notices.Find(written => written.Id == first.Id) is { Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Queue } && fixture.Saved(first) is { Lane: DeliveryLane.Delivered, Receipt.RunId: "run-1" }
                && fixture.Saved(second)?.Receipt is { Route: DeliveryRoute.Steer, RunId: "run-1" }, "each receipt names the run that got it");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // A pending notice for a parent that finished normally but cannot start a run now is held, and no run is asked for.
        fixture = new Fixture(Running);
        try
        {
            // The parent's run is still starting: both steers are refused and the notice stays pending.
            fixture.Host.RefuseNext(Running, Running);
            var first = await fixture.End("c1");
            Check(fixture.Saved(first) == first, "a pane that takes nothing twice leaves the notice pending");
            var asked = fixture.Host.AllHanded.Count;
            // Its run then finished normally, but the app's start would refuse a new one now.
            var parent = new RunSession { Id = "parent", WorkspaceId = "ws", Title = "Parent", Settings = new(PermissionMode: "auto") }; var workspace = new Workspace { Id = "ws", Name = "repo", Path = fixture.Base };
            var refused = DelegationPanes.PaneState(parent, workspace, "p-run", Finished, true);
            Check(refused is { Activity: Idle, RunId: "p-run" }, "a pane the app would refuse reads as idle");
            fixture.Host.Set(refused);
            // The app offers it again as the run ends: it is held, a row of the parent's queued list, and the host is asked for no steer and no run. So is a notice made meanwhile.
            await fixture.Coordinator.DeliverPending();
            var second = await fixture.End("c2");
            Check(fixture.Host.AllHanded.Count == asked && fixture.Store.Load().Notices.SequenceEqual([Fixture.Held(first), Fixture.Held(second)]) && fixture.Coordinator.HeldItems("parent").Select(item => item.Id).SequenceEqual([first.Id, second.Id]), "both are held, and no run is asked for");
            // Once a run can start again, the human's next send releases both, oldest first, in one run.
            fixture.Host.Set(DelegationPanes.PaneState(parent, workspace, "p-run", Finished));
            Check(Is(await fixture.Coordinator.Send("Go on.", "parent"), Released("run-1", first.Id, second.Id)), "the next send releases both");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // A new item for a pane with held items is held behind them, whatever the pane is doing.
        fixture = new Fixture(Finished);
        try
        {
            var parent = new RunSession { Id = "parent", WorkspaceId = "ws", Title = "Parent", Settings = new(PermissionMode: "auto") }; var workspace = new Workspace { Id = "ws", Name = "repo", Path = fixture.Base };
            fixture.Host.Set(DelegationPanes.PaneState(parent, workspace, "p-run", Finished, true));
            var first = await fixture.End("c1");
            Check(fixture.Saved(first) == Fixture.Held(first), "held while the app would refuse a run");
            // The refusal clears and the parent reads finished again; then it runs Claude. Each new notice is held behind the first.
            fixture.Host.Set(DelegationPanes.PaneState(parent, workspace, "p-run", Finished));
            var second = await fixture.End("c2");
            fixture.Host.Set("parent", Running, "p-run-2");
            var third = await fixture.End("c3");
            await fixture.Coordinator.DeliverPending();
            Check(fixture.Host.AllHanded.Count == 0 && fixture.Store.Load().Notices.SequenceEqual(new[] { first, second, third }.Select(Fixture.Held)) && fixture.Coordinator.HeldItems("parent").Select(item => item.Id).SequenceEqual([first.Id, second.Id, third.Id]), "newer notices are held behind the first");
            // The human's next send releases all of them together, oldest first, ahead of the text, in one run.
            fixture.Host.Set("parent", Finished, "p-run-2");
            Check(Is(await fixture.Coordinator.Send("Go on.", "parent"), Released("run-1", first.Id, second.Id, third.Id)), "one send releases all three");
            var run = fixture.Host.AllHanded.Single();
            Check(run is { Route: null, SessionId: "parent" } && run.Input == Texts([first, second, third], "Go on.") && fixture.Store.Load().Notices.All(notice => notice.Receipt?.RunId == "run-1"), "oldest first, ahead of the human's text, in one run");
            fixture.EachTakenOnce(first.Id, second.Id, third.Id);
        }
        finally { fixture.Remove(); }
    }

    internal static async Task HeldItemsAreReleasedByASendOrRunNext()
    {
        // Stopped, errored, or no run since this launch: held until one send releases all of them, oldest first, ahead of the human's text, in one run.
        var fixture = new Fixture(Idle);
        try
        {
            var first = await fixture.End("c1"); var second = await fixture.End("c2");
            Check(fixture.Host.AllHanded.Count == 0 && fixture.Store.Load().Notices.SequenceEqual([Fixture.Held(first), Fixture.Held(second)]), "both are held");
            Check(fixture.Coordinator.HeldItems("parent").Select(item => item.Id).SequenceEqual([first.Id, second.Id]) && fixture.Coordinator.HeldItems("parent").All(item => item.Kind == DelegationDeliveryKind.Notice), "they are the parent's held rows");
            var words = "\uC774\uC81C \uD569\uCCD0 \uC918.";
            Check(Is(await fixture.Coordinator.Send(words, "parent"), Released("run-1", first.Id, second.Id)), "the send releases both");
            var run = fixture.Host.AllHanded.Single();
            Check(run is { Route: null, SessionId: "parent" } && run.Input == Texts([first, second], words) && run.OnDisk!.Notices.All(notice => notice.Lane == DeliveryLane.Delivered), "both saved delivered before the run got them");
            var saved = fixture.Store.Load().Notices;
            Check(saved.All(notice => notice is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Queue, RunId: "run-1" } }) && fixture.Coordinator.HeldItems("parent").Count == 0, "their receipts name the run");
            // The next send has nothing to release: the caller sends it as before.
            Check(await fixture.Coordinator.Send("Thanks.", "parent") == NothingHeld && await fixture.Coordinator.RunNext("parent") == NothingHeld && fixture.Host.AllHanded.Count == 1, "nothing more is held");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // Run next releases only the held items in one run, and the human's queued rows follow in later runs.
        fixture = new Fixture(Idle);
        try
        {
            var first = await fixture.End("c1"); var second = await fixture.End("c2");
            var rows = new Queue<string>(["Human row 1", "Human row 2"]);
            for (var turn = 0; turn < 3; turn++)
            {
                // The app's run next: held items first; with none, its own next queued row.
                if (await fixture.Coordinator.RunNext("parent") == NothingHeld) await fixture.Host.StartRun("parent", rows.Dequeue());
                fixture.Host.Set("parent", Finished, fixture.Host.Pane("parent")?.RunId);
            }
            SameList(fixture.Host.AllHanded.Select(handed => handed.Input), [Texts([first, second]), "Human row 1", "Human row 2"], "held items first, then the human's rows");
            Check(fixture.Host.AllHanded.Select(handed => handed.RunId).SequenceEqual(["run-1", "run-2", "run-3"]) && fixture.Store.Load().Notices.All(notice => notice.Receipt?.RunId == "run-1"), "each in its own run");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // Two sends at once release each held item once.
        fixture = new Fixture(Idle);
        try
        {
            var first = await fixture.End("c1"); var second = await fixture.End("c2");
            var results = await Task.WhenAll(fixture.Coordinator.Send("From the desktop.", "parent"), fixture.Coordinator.Send("From the phone.", "parent"));
            Check(results.Count(result => result == NothingHeld) == 1 && results.Any(result => Is(result, Released("run-1", first.Id, second.Id))) && fixture.Host.AllHanded.Count == 1, "one send releases them and the other finds nothing held");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // The app's send starts the release run itself, and run next steers held items into a running Claude run.
        fixture = new Fixture(Idle);
        try
        {
            var first = await fixture.End("c1"); var second = await fixture.End("c2");
            // The app starts the run with the human's attachments: the whole input reaches its starter once.
            var started = new List<string>();
            Check(Is(await fixture.Coordinator.Send("Merge what is ready.", "parent", input => { lock (started) started.Add(input); return Task.FromResult<string?>("app-run-1"); }), Released("app-run-1", first.Id, second.Id)), "the app's starter starts the release run");
            Check(started.SequenceEqual([Texts([first, second], "Merge what is ready.")]) && fixture.Host.AllHanded.Count == 0
                && fixture.Store.Load().Notices.All(notice => notice is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Queue, RunId: "app-run-1" } }), "the whole input reaches the starter once");
            // A starter that starts nothing leaves them held, as they were.
            var third = await fixture.End("c3");
            Check(await fixture.Coordinator.Send("Again.", "parent", _ => Task.FromResult<string?>(null)) == NotStarted && fixture.Saved(third) == Fixture.Held(third) && fixture.Coordinator.HeldItems("parent").Select(item => item.Id).SequenceEqual([third.Id]), "a starter that starts nothing leaves them held");
            // Run next while the parent runs Claude: steered into that run, nothing else.
            fixture.Host.Set("parent", Running, "p-run");
            Check(Is(await fixture.Coordinator.RunNext("parent"), Released("p-run", third.Id)), "run next steers into the running run");
            var steer = fixture.Host.AllHanded.Single();
            Check(steer is { Route: DeliveryRoute.Steer, RunId: "p-run" } && steer.Input == DelegationCoordinator.Text(third) && steer.OnDisk?.Notices.Find(notice => notice.Id == third.Id)?.Lane == DeliveryLane.Delivered
                && fixture.Saved(third) is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Steer, RunId: "p-run" } } && await fixture.Coordinator.RunNext("parent") == NothingHeld, "steered once, with its receipt");
            fixture.EachTakenOnce(third.Id);
        }
        finally { fixture.Remove(); }

        // Held rows come first in the phone's queued array, in its item shape, cannot be removed, and the first keeps 바로 실행 on offer.
        DelegationDeliveryItem[] held = [new("n-1", DelegationDeliveryKind.Notice, "c1", "parent", "Notice 1"), new("n-2", DelegationDeliveryKind.Notice, "c2", "parent", "Notice 2")];
        QueuedComposerInput[] queued = [new("q-1", "Human row", [])];
        SameList(DelegationQueueRows.MobileItems(held, queued), [new MobileQueuedItem("n-1", "Notice 1"), new MobileQueuedItem("n-2", "Notice 2"), new MobileQueuedItem("q-1", "Human row")], "held rows come first in the phone's queue");
        Same(System.Text.Json.JsonSerializer.Serialize(DelegationQueueRows.MobileItems(held, queued)[0], Wire.Json), """{"id":"n-1","text":"Notice 1"}""", "the existing item shape, nothing more");
        SameList(DelegationQueueRows.MobileItems([], queued), [new MobileQueuedItem("q-1", "Human row")], "with nothing held, the human's rows alone");
        Check(DelegationQueueRows.RemoveRefusal("n-2", held) == DelegationReasonCode.HeldNotRemovable && DelegationQueueRows.RemoveRefusal("q-1", held) is null && DelegationQueueRows.RemoveRefusal("n-2", []) is null, "a held row cannot be removed");
        // The first held row has 바로 실행 for them all, busy or not, and the others none; a human row on an idle pane runs here.
        static QueueRunNow.Pane RunNowPane(bool busy, int held) => new(busy, false, ComposerSecondSend.Worktree, false, false, held);
        foreach (var busy in new[] { true, false })
            Check(QueueRunNow.Decide(0, "", 0, RunNowPane(busy, 2)) == QueueRunNowPath.Held && QueueRunNow.Decide(1, "", 0, RunNowPane(busy, 2)) == QueueRunNowPath.Hidden, "the first held row sends them all, busy or not");
        Check(QueueRunNow.Decide(null, "Human row", 0, RunNowPane(false, 2)) == QueueRunNowPath.Here && QueueRunNow.Decide(null, "Human row", 0, RunNowPane(false, 0)) == QueueRunNowPath.Here, "a human row on an idle pane runs here");
        // Each row carries what 바로 실행 does on it; a hidden one carries neither field.
        SameList(DelegationQueueRows.MobileItems(held, queued, rowId => rowId == "n-1" ? QueueRunNowPath.Held : rowId == "n-2" ? QueueRunNowPath.Hidden : QueueRunNowPath.Worktree),
            [new MobileQueuedItem("n-1", "Notice 1", "here"), new MobileQueuedItem("n-2", "Notice 2"), new MobileQueuedItem("q-1", "Human row", "worktree")], "the phone's rows carry 바로 실행");
    }

    internal static async Task PendingItemsAreOfferedAgainAndNeverOvertaken()
    {
        // A run that ends as a notice is steered in gets it as the next run, and a pane that takes nothing leaves it pending.
        var fixture = new Fixture(Running);
        try
        {
            // The parent's run ends normally just as the notice is steered in.
            fixture.Host.RefuseNext(Finished);
            var first = await fixture.End("c1");
            Check(fixture.Host.AllHanded.Select(handed => handed.Route).SequenceEqual([DeliveryRoute.Steer, DeliveryRoute.Queue]) && fixture.Host.AllHanded.Select(handed => handed.RunId).SequenceEqual([null, "run-1"])
                && fixture.Saved(first) is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Queue, RunId: "run-1" } }, "a run that ended gets the notice as its next run");
            // A pane that takes nothing twice running: the notice stays pending, as it was.
            fixture.Host.RefuseNext(Running, Running);
            var second = await fixture.End("c2");
            Check(fixture.Host.AllHanded.Count == 4 && fixture.Saved(second) == second, "offered twice, then left pending");
            // It is offered again with the next notice, and each reaches the parent once.
            var third = await fixture.End("c3");
            var saved = fixture.Store.Load().Notices;
            Check(saved.All(notice => notice.Lane == DeliveryLane.Delivered) && saved.Select(notice => notice.Receipt?.Route).SequenceEqual([DeliveryRoute.Queue, DeliveryRoute.Steer, DeliveryRoute.Steer]), "offered again with the next notice");
            fixture.EachTakenOnce(first.Id, second.Id, third.Id);
        }
        finally { fixture.Remove(); }

        // A notice a run could not take while starting or ending goes on when the app offers it again.
        fixture = new Fixture(Running);
        try
        {
            fixture.Host.RefuseNext(Running, Running);
            var first = await fixture.End("c1");
            Check(fixture.Saved(first) == first && fixture.Coordinator.HeldItems("parent").Count == 0, "still pending, not held");
            // A pending notice is delegation work a restart would cut short: the automatic app update waits for it (M pendingWork).
            Check(fixture.Coordinator.PendingWork() == 1, $"a pending notice counts as pending work; got {fixture.Coordinator.PendingWork()}");
            // Once the run is up the app offers it again: steered into that run.
            await fixture.Coordinator.DeliverPending();
            Check(fixture.Coordinator.PendingWork() == 0, $"a delivered notice is no pending work; got {fixture.Coordinator.PendingWork()}");
            Check(fixture.Host.Taken[^1] is { Route: DeliveryRoute.Steer, RunId: "p-run" } steer && steer.Input == DelegationCoordinator.Text(first) && fixture.Saved(first) is { Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Steer }, "steered once the run is up");
            // A run that ends normally while its notice is offered: the run end offers it again, as the next run.
            fixture.Host.RefuseNext(Finished, Finished);
            var second = await fixture.End("c2");
            Check(fixture.Saved(second) == second, "left pending by a run that was ending");
            await fixture.Coordinator.DeliverPending();
            Check(fixture.Host.Taken[^1] is { Route: DeliveryRoute.Queue, RunId: "run-1" } next && next.Input == DelegationCoordinator.Text(second) && fixture.Saved(second)?.Receipt?.RunId == "run-1", "it goes on as the next run");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // An item its pane still did not take keeps the newer ones waiting behind it for the pass.
        fixture = new Fixture(Running);
        try
        {
            fixture.Host.RefuseNext(Running, Running);
            var first = await fixture.End("c1");
            // It takes nothing twice again when the second notice comes, which waits behind the first, offered to no pane.
            fixture.Host.RefuseNext(Running, Running);
            var second = await fixture.End("c2");
            Check(fixture.Saved(first) == first && fixture.Saved(second) == second && fixture.Host.AllHanded.Count == 4 && fixture.Host.Taken.Count == 0, "the newer notice waits behind the first");
            // Once the run takes them, the next pass hands both over, oldest first.
            await fixture.Coordinator.DeliverPending();
            SameList(fixture.Host.Taken.Select(handed => handed.Input), [DelegationCoordinator.Text(first), DelegationCoordinator.Text(second)], "the next pass hands both over, oldest first");
            fixture.EachTakenOnce(first.Id, second.Id);
        }
        finally { fixture.Remove(); }

        // An item waiting for its pane keeps no other pane's items waiting.
        fixture = new Fixture(Running);
        try
        {
            fixture.Host.Set("c1", Running, "r-c1");
            fixture.Host.RefuseNext(Running, Running);
            var notice = await fixture.End("c2");
            // The parent's run takes nothing twice again in the pass a follow-up starts, and the follow-up still goes into the running child's run.
            fixture.Host.RefuseNext(Running, Running);
            Check((await fixture.FollowUp("c1", "Go on.")).Error is null && fixture.Saved(notice) == notice && fixture.Host.Taken.Select(handed => handed.SessionId).SequenceEqual(["c1"]) && fixture.Host.Taken[0].Route == DeliveryRoute.Steer, "the follow-up goes to its own pane");
            await fixture.Coordinator.DeliverPending();
            SameList(fixture.Host.Taken.Select(handed => handed.SessionId), ["c1", "parent"], "the parent's notice goes on in the next pass");
            fixture.EachTakenOnce(notice.Id);
        }
        finally { fixture.Remove(); }
    }

    internal static async Task LaunchHoldsWhatWasPendingAndClosedParentsKeepTheirNotices()
    {
        // Everything pending at launch is held, and a rebuilt coordinator starts no run by itself.
        var receipt = new DeliveryReceipt("2026-10-09T01:00:00Z", DeliveryRoute.Steer, "old-run");
        Notice[] old = [new("n-old", "c1", 0, NoticeKind.EndedWithoutReport, DeliveryLane.Pending), new("n-done", "c1", 0, NoticeKind.FailedToStart, DeliveryLane.Delivered, receipt)];
        var fixture = new Fixture(null, made: made => DelegationFile.Of([made.Child("c1", ChildState.Ended), made.Child("c2")], old, [new("f-old", "c1", "\uD14C\uC2A4\uD2B8\uB3C4 \uCD94\uAC00\uD574 \uC918", DeliveryLane.Pending)]));
        try
        {
            // Both panes would take an item on their own now.
            var host = new DelegationTestHost(fixture.Store); host.Set("parent", Finished, "p-run"); host.Set("c1", Finished, "r-c1");
            var coordinator = fixture.Launch(host);
            var restored = fixture.Store.Load();
            Check(host.AllHanded.Count == 0 && restored.Notices.Select(notice => notice.Lane).SequenceEqual([DeliveryLane.Held, DeliveryLane.Delivered]) && restored.Notices[1].Receipt == receipt && restored.FollowUps.Single().Lane == DeliveryLane.Held
                && coordinator.File == restored, "what was pending at launch is held, on disk too");
            Check(coordinator.HeldItems("parent").Select(item => item.Id).SequenceEqual(["n-old"]) && coordinator.HeldItems("c1").Select(item => item.Id).SequenceEqual(["f-old"]), "each pane has its own held rows");
            // Held items survive a restart, so they keep no automatic app update waiting.
            Check(coordinator.PendingWork() == 0, $"held items are no pending work; got {coordinator.PendingWork()}");
            // A new notice does not go ahead of what launch held: it is held behind it.
            var fresh = await coordinator.ChildRunEnded("c2", "r-c2", ChildRunEnd.Finished) ?? throw new InvalidOperationException("no notice");
            Check(host.AllHanded.Count == 0 && coordinator.HeldItems("parent").Select(item => item.Id).SequenceEqual(["n-old", fresh.Id]), "a new notice is held behind what launch held");
            // The human's next send releases both, oldest first.
            Check(Is(await coordinator.Send("Go on.", "parent"), Released("run-1", "n-old", fresh.Id)) && host.AllHanded[^1].Input == Texts([old[0], fresh], "Go on.") && !host.AllHanded.Any(handed => handed.Input.Contains("n-done", StringComparison.Ordinal)), "the next send releases both, and never what was delivered");
        }
        finally { fixture.Remove(); }

        // Held notices for a closed parent's children stay on the child's card.
        fixture = new Fixture(Idle);
        try
        {
            // Held while the parent's pane was open, and then the pane closed; the second is made after it closed.
            var first = await fixture.End("c1");
            fixture.Host.Close("parent");
            var second = await fixture.End("c2");
            Check(fixture.Host.AllHanded.Count == 0 && fixture.Coordinator.HeldNotices("c1").SequenceEqual([Fixture.Held(first)]) && fixture.Coordinator.HeldNotices("c2").SequenceEqual([Fixture.Held(second)]) && fixture.Coordinator.HeldNotices("c3").Count == 0, "each child's card has its held notices");
            // The child rows the app's sidebar follows carry the count each card shows.
            Check(fixture.Coordinator.ChildRows.TryRead(out var rows) && rows.Select(row => row.HeldNotices).SequenceEqual([1, 1, 0]), "the rows carry the count");
            // Nothing releases them: the parent's pane takes no run.
            Check(await fixture.Coordinator.Send("Hello?", "parent") == NotStarted && await fixture.Coordinator.RunNext("parent") == NotStarted && fixture.Host.AllHanded.Count == 0
                && fixture.Store.Load().Notices.SequenceEqual([Fixture.Held(first), Fixture.Held(second)]), "nothing releases a closed parent's notices");
            // Across a relaunch they stay on the cards.
            var relaunched = fixture.Launch(fixture.Host);
            Check(relaunched.HeldNotices("c1").SequenceEqual([Fixture.Held(first)]) && relaunched.HeldNotices("c2").SequenceEqual([Fixture.Held(second)]) && relaunched.ChildRows.TryRead(out var again) && again.Select(row => row.HeldNotices).SequenceEqual([1, 1, 0])
                && fixture.Host.AllHanded.Count == 0, "across a relaunch they stay on the cards");
        }
        finally { fixture.Remove(); }

        // The held rows the app shows follow the file across a relaunch.
        fixture = new Fixture(Idle);
        try
        {
            Check(fixture.Coordinator.HeldRows.TryRead(out var none) && none.Count == 0, "no held rows at first");
            var first = await fixture.End("c1"); var second = await fixture.End("c2");
            var expected = new[] { first, second }.Select(notice => new DelegationDeliveryItem(notice.Id, DelegationDeliveryKind.Notice, notice.ChildId, "parent", DelegationCoordinator.Text(notice))).ToList();
            Check(fixture.Coordinator.HeldRows.TryRead(out var map) && map.Keys.SequenceEqual(["parent"]) && map["parent"].SequenceEqual(expected) && !fixture.Coordinator.HeldRows.TryRead(out _), "only the newest map waits for the app");
            SameList(fixture.Coordinator.HeldItems("parent"), expected, "the held items are the same rows");
            // Rebuilt from the same profile, the app's first map has them. Released, they leave the queued list.
            var relaunched = fixture.Launch(fixture.Host);
            Check(relaunched.HeldRows.TryRead(out var loaded) && loaded["parent"].SequenceEqual(expected), "the first map after a relaunch has them");
            Check(Is(await relaunched.Send("Go on.", "parent"), Released("run-1", first.Id, second.Id)) && relaunched.HeldRows.TryRead(out var released) && released.Count == 0, "released, they leave the queued list");
        }
        finally { fixture.Remove(); }
    }

    internal static async Task FollowUpsReachTheirChildOnce()
    {
        // A follow-up goes to its child's pane by the same rules.
        var fixture = new Fixture(Running, made: made => DelegationFile.Of([made.Child("c1") with { AwaitedRunId = null }, made.Child("c2", ChildState.Ended)]));
        try
        {
            fixture.Host.Set("c1", Running, "r-c1");
            // c2's run was stopped.
            fixture.Host.Set("c2", Idle, "r-c2");
            var toRunning = (await fixture.FollowUp("c1", "Also add tests.")).FollowUp!;
            var steer = fixture.Host.AllHanded.Single();
            Check(toRunning.Lane == DeliveryLane.Delivered && steer is { SessionId: "c1", Route: DeliveryRoute.Steer, RunId: "r-c1" } && steer.Input.Contains(toRunning.Id, StringComparison.Ordinal) && steer.Input.Contains("Also add tests.", StringComparison.Ordinal)
                && steer.OnDisk?.FollowUps[0].Lane == DeliveryLane.Delivered, "steered into the running child's run");
            Check(fixture.Coordinator.File.Children[0].AwaitedRunId == "r-c1", "the run that got it is the one c1's parent now awaits");
            var toStopped = (await fixture.FollowUp("c2", "Rebase on main.")).FollowUp!;
            Check(toStopped.Lane == DeliveryLane.Held && fixture.Host.AllHanded.Count == 1 && fixture.Coordinator.HeldItems("c2").Single().Kind == DelegationDeliveryKind.FollowUp, "held for a stopped child");
            Check(Is(await fixture.Coordinator.Send("Thanks.", "c2"), Released("run-1", toStopped.Id)), "the human's next send in the child releases it");
            var followUp = fixture.Coordinator.File.FollowUps.Single(saved => saved.Id == toStopped.Id); var run = fixture.Host.AllHanded[^1];
            Check(run.SessionId == "c2" && run.Input == DelegationCoordinator.Text(followUp, ChildWorktree.ReportFileIn(Path.Combine(fixture.Base, "worktrees", "c2"))) + "\n\nThanks." && followUp is { Lane: DeliveryLane.Delivered, Receipt.RunId: "run-1" }
                && fixture.Coordinator.File.Children[1].AwaitedRunId == "run-1", "ahead of the human's text, and its run is awaited");
            fixture.EachTakenOnce(toRunning.Id, toStopped.Id);
        }
        finally { fixture.Remove(); }

        // The profile of the Mac's follow-up tests: the parent's child c1 reported at revision 1 on its run "r-c1".
        Fixture Reported() => new(null, made: made => DelegationFile.Of([made.Child("c1", ChildState.Reported) with { ReportRevision = 1, ReportHead = Commit, ReportDigest = "digest" }]));

        // A follow-up to a reported child idle after a normal finish starts its next run once, and clears reported.
        fixture = Reported();
        try
        {
            var (host, coordinator) = await fixture.Relaunch("launch1");
            host.Set("parent", Running, "p-run"); host.Set("c1", Finished, "r-c1");
            var answer = (await fixture.FollowUp("c1", "Also add tests.")).FollowUp!;
            var run = host.AllHanded.Single();
            Check(answer is { Lane: DeliveryLane.Delivered, Remaining: 1, Child: "c1" } && run is { SessionId: "c1", Route: DeliveryRoute.Queue, RunId: "launch1-run-1" } && run.Input == fixture.TextOf(answer.Id)
                && run.OnDisk?.FollowUps.Find(saved => saved.Id == answer.Id)?.Lane == DeliveryLane.Delivered, "it starts the child's next run, saved delivered first");
            // c1 runs the follow-up: reported is cleared and its run is the one the parent awaits.
            Check(coordinator.File.Children[0] is { State: ChildState.Running, RunId: "launch1-run-1", AwaitedRunId: "launch1-run-1", ReportRevision: 1, FollowUpCount: 1 } && coordinator.File == fixture.Store.Load()
                && fixture.Store.Load().FollowUps[0] is { Lane: DeliveryLane.Delivered, Receipt: { Route: DeliveryRoute.Queue, RunId: "launch1-run-1" } }, "reported is cleared at once");
            Check((await coordinator.Handle(Request("list_children"), Parent)).Children!.Single().State == ChildState.Running, "the parent sees it running");
            // Never twice: another pass, a send or run next in c1 hands nothing more.
            await coordinator.DeliverPending();
            Check(await coordinator.Send("Thanks.", "c1") == NothingHeld && await coordinator.RunNext("c1") == NothingHeld && host.Taken.Count(handed => handed.Input.Contains(answer.Id, StringComparison.Ordinal)) == 1, "never twice");
            // The follow-up's run ends with no new report: the parent gets one notice.
            Check(await coordinator.ChildRunEnded("c1", "launch1-run-1", ChildRunEnd.Finished) is { Kind: NoticeKind.EndedWithoutReport, ReportRevision: 1 } && coordinator.File.Children[0].State == ChildState.Ended, "its end without a report sends one notice");
        }
        finally { fixture.Remove(); }

        // Held follow-ups reach the child once after a relaunch from the same profile, and clear reported then.
        fixture = Reported();
        try
        {
            var (host, coordinator) = await fixture.Relaunch("launch1");
            host.Set("parent", Running, "p-run");
            // A human stopped c1's run after it reported.
            host.Set("c1", Idle, "r-c1");
            var words = "\uADF8\uB9AC\uACE0 \uBB38\uC11C\uB3C4 \uACE0\uCCD0 \uC918.";
            var first = (await fixture.FollowUp("c1", "Also add tests.")).FollowUp!; var second = (await fixture.FollowUp("c1", words)).FollowUp!;
            Check(first.Lane == DeliveryLane.Held && second is { Lane: DeliveryLane.Held, Remaining: 0 } && host.AllHanded.Count == 0 && coordinator.File.Children[0].State == ChildState.Reported
                && coordinator.HeldItems("c1").Select(item => item.Id).SequenceEqual([first.Id, second.Id]), "held, so c1 has not run them: it is still reported");
            var texts = new[] { fixture.TextOf(first.Id), fixture.TextOf(second.Id) };

            // The app quits and opens again from the same profile.
            var (relaunchedHost, relaunched) = await fixture.Relaunch("launch2");
            relaunchedHost.Set("parent", Running, "p-run-2");
            Check(relaunched.File == fixture.Store.Load() && relaunched.File.FollowUps.All(followUp => followUp.Lane == DeliveryLane.Held) && relaunched.HeldItems("c1").Select(item => item.Id).SequenceEqual([first.Id, second.Id])
                && relaunched.HeldItems("c1").All(item => item.Kind == DelegationDeliveryKind.FollowUp), "they are still held after the relaunch");
            // Nothing reaches c1 on its own, even when it is idle after a normal finish.
            relaunchedHost.Set("c1", Finished, "launch2-run-0");
            await relaunched.DeliverPending();
            Check(relaunchedHost.AllHanded.Count == 0 && relaunched.File.Children[0].State == ChildState.Reported, "nothing reaches c1 on its own");
            // A third follow-up is refused and changes nothing.
            var before = fixture.Store.Load();
            Check((await fixture.FollowUp("c1", "One more.")).Refused == DelegationReasonCode.FollowUpLimit && fixture.Store.Load() == before, "a third follow-up is refused");
            // The human's next send in c1 releases both, oldest first, ahead of the text, in one run.
            relaunchedHost.Set("c1", Idle, "launch2-run-0");
            Check(Is(await relaunched.Send("Go on.", "c1"), Released("launch2-run-1", first.Id, second.Id)), "the send in c1 releases both");
            var run = relaunchedHost.AllHanded.Single();
            Check(run is { SessionId: "c1", Route: null } && run.Input == string.Join("\n\n", texts.Append("Go on.")) && run.OnDisk!.FollowUps.All(followUp => followUp.Lane == DeliveryLane.Delivered), "oldest first, ahead of the text");
            // c1 runs them now: reported is cleared.
            var saved = fixture.Store.Load();
            Check(relaunched.File.Children[0] is { State: ChildState.Running, RunId: "launch2-run-1", AwaitedRunId: "launch2-run-1" } && saved.FollowUps.All(followUp => followUp is { Lane: DeliveryLane.Delivered, Receipt.RunId: "launch2-run-1" }), "c1 runs them now");
            // Opened once more: they are delivered, so nothing goes again. The relaunch's sweep found c1 running the follow-ups when the app quit.
            var (thirdHost, third) = await fixture.Relaunch("launch3");
            thirdHost.Set("c1", Finished, "launch3-run-0");
            await third.DeliverPending();
            Check(third.HeldItems("c1").Count == 0 && await third.Send("Hello?", "c1") == NothingHeld && await third.RunNext("c1") == NothingHeld && thirdHost.AllHanded.Count == 0 && fixture.Store.Load().FollowUps.SequenceEqual(saved.FollowUps)
                && third.File.Children[0].State == ChildState.Interrupted, "delivered, so nothing goes again");
            foreach (var id in new[] { first.Id, second.Id }) Check(host.Taken.Concat(relaunchedHost.Taken).Concat(thirdHost.Taken).Count(handed => handed.Input.Contains(id, StringComparison.Ordinal)) == 1, id + " reached c1 exactly once across three launches");
        }
        finally { fixture.Remove(); }

        // A follow-up left pending at quit is held at launch, and run next hands it over once.
        fixture = Reported();
        try
        {
            var (host, coordinator) = await fixture.Relaunch("launch1");
            host.Set("parent", Running, "p-run"); host.Set("c1", Finished, "r-c1");
            // c1's pane does not take it, twice running: it stays pending.
            host.RefuseNext(null, null);
            var answer = (await fixture.FollowUp("c1", "Also add tests.")).FollowUp!;
            Check(answer.Lane == DeliveryLane.Pending && host.AllHanded.Count == 2 && host.Taken.Count == 0 && fixture.Store.Load().FollowUps.Single().Lane == DeliveryLane.Pending && coordinator.File.Children[0].State == ChildState.Reported, "it stays pending");
            // At the next launch it is held, and run next in c1 hands it over alone, once.
            var (relaunchedHost, relaunched) = await fixture.Relaunch("launch2");
            Check(fixture.Store.Load().FollowUps.Single().Lane == DeliveryLane.Held && relaunched.HeldItems("c1").Select(item => item.Id).SequenceEqual([answer.Id]) && relaunchedHost.AllHanded.Count == 0, "held at the next launch");
            relaunchedHost.Set("c1", Idle, "r-c1");
            Check(Is(await relaunched.RunNext("c1"), Released("launch2-run-1", answer.Id)) && relaunchedHost.AllHanded.Select(handed => handed.Input).SequenceEqual([fixture.TextOf(answer.Id)])
                && relaunched.File.Children[0] is { State: ChildState.Running, RunId: "launch2-run-1", AwaitedRunId: "launch2-run-1" } && await relaunched.RunNext("c1") == NothingHeld, "run next hands it over alone, once");
            Check(host.Taken.Concat(relaunchedHost.Taken).Count(handed => handed.Input.Contains(answer.Id, StringComparison.Ordinal)) == 1, "exactly once across the launches");
        }
        finally { fixture.Remove(); }

        // A follow-up saved delivered just before the app quit is never handed over again.
        fixture = Reported();
        try
        {
            var file = fixture.Store.Load();
            file = file.SettingChild(0, file.Children[0] with { FollowUpCount = 1 }) with { FollowUps = [new("f-written", "c1", "Also add tests.", DeliveryLane.Delivered, new("2026-10-09T01:00:00Z", DeliveryRoute.Queue, ""))] };
            fixture.Store.Save(file);
            var (host, coordinator) = await fixture.Relaunch("launch1");
            host.Set("c1", Finished, "r-c1");
            await coordinator.DeliverPending();
            Check(coordinator.HeldItems("c1").Count == 0 && await coordinator.Send("Go on.", "c1") == NothingHeld && await coordinator.RunNext("c1") == NothingHeld && host.AllHanded.Count == 0 && fixture.Store.Load() == file, "saved delivered is never handed over again");
        }
        finally { fixture.Remove(); }

        // Two held follow-ups, each at the most a follow-up may take, fit the one run that run next releases them in.
        fixture = Reported();
        try
        {
            var (host, coordinator) = await fixture.Relaunch("launch1");
            host.Set("parent", Running, "p-run"); host.Set("c1", Idle, "r-c1");
            var longest = new string('x', DelegationCoordinator.MaximumFollowUpBytes);
            var first = (await fixture.FollowUp("c1", longest)).FollowUp!; var second = (await fixture.FollowUp("c1", longest)).FollowUp!;
            Check(first.Lane == DeliveryLane.Held && second is { Lane: DeliveryLane.Held, Remaining: 0 } && host.AllHanded.Count == 0, "both are held");
            Check(Is(await coordinator.RunNext("c1"), Released("launch1-run-1", first.Id, second.Id)), "run next releases both");
            var run = host.AllHanded.Single();
            Check(run is { SessionId: "c1", Route: null } && run.Input == fixture.TextOf(first.Id) + "\n\n" + fixture.TextOf(second.Id) && coordinator.HeldItems("c1").Count == 0, "oldest first, in one run");
            // The app's start accepts that run's input.
            new StartRunRequest("c1", "ws-1", "claude", run.Input, []).Validate();
            new QueuedInputBuffer().Add(run.Input, []);
        }
        finally { fixture.Remove(); }
    }
}
