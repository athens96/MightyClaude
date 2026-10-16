using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// The coordinator with a fake host on real git repositories in temp folders, ported from the Mac's DelegationCoordinatorTests,
/// DelegationDelegateToolTests, DelegationReportTests, DelegationActionToolTests, DelegationStatusToolTests, DelegationCardTests,
/// DelegationStoragePressureTests and the coordinator half of DelegationCleanupTests.
/// </summary>
internal static class DelegationCoordinatorVerification
{
    private const DelegationPaneActivity Running = DelegationPaneActivity.Running, Finished = DelegationPaneActivity.Finished, Idle = DelegationPaneActivity.Idle;

    /// <summary>A place with a coordinator over it. The pane "parent" works in the repository.</summary>
    private sealed record Fixture(DelegationPlace Place, DelegationTestHost Host, DelegationCoordinator Coordinator)
    {
        internal DelegationFileStore Store => Place.Store;
        internal DelegationFile File => Coordinator.File;
        internal Task<DelegationResponse> Call(string tool, params (string, string)[] arguments) => Coordinator.Handle(Request(tool, arguments), Parent);
        internal Task<DelegationResponse> CallFrom(string pane, string tool, params (string, string)[] arguments) => Coordinator.Handle(Request(tool, arguments), Caller(pane));
        internal Task<DelegationResponse> Delegate(string task, string mode, string from = "parent") => CallFrom(from, "delegate", ("task", task), ("mode", mode));
        internal ChildRecord Child(string id) => File.Children.Single(child => child.Id == id);
        internal List<Notice> Notices(string id) => File.Notices.Where(notice => notice.ChildId == id).ToList();
        internal byte[] Bytes() => System.IO.File.ReadAllBytes(Store.FilePath);
        /// <summary>The child is in <paramref name="state"/> and its start is over, the delivery of a failed start's notice included.</summary>
        internal Task<bool> WaitFor(string id, ChildState state) => Eventually(async () => File.Children.Find(child => child.Id == id)?.State == state && !await Coordinator.IsStarting(id));
        internal void SetParent(string mode = "auto", string runId = "run-1", DelegationPaneActivity activity = Running) => Host.Set(new("parent", mode, Place.Repo, RunId: runId, Activity: activity));
        /// <summary>A coordinator on the same profile, as the app makes one at launch.</summary>
        internal DelegationCoordinator Relaunch(DelegationTestHost? host = null) => new(Store, host ?? Host, Place.Maker, () => true);
        internal void Remove() => Place.Remove();
        /// <summary>A refused call made nothing: the same file, no worktree root, no <c>mighty/*</c> branch, no second worktree, and no pane or run asked for.</summary>
        internal async Task NothingMade(byte[] bytes, DelegationFile file, bool hasGit = true)
        {
            Check(Bytes().SequenceEqual(bytes) && File == file && !Directory.Exists(Place.Root) && Host.CreatedPanes.Count == 0 && Host.StartedRuns.Count == 0, "the refused call changed nothing");
            if (hasGit) Check((await Place.MightyBranches()).Length == 0 && (await Place.Git("worktree", "list", "--porcelain")).Split("worktree ").Length == 2, "no branch and no worktree was made");
        }
    }

    /// <summary><paramref name="commits"/> null leaves the repository folder without git. The host makes child panes and names their first run "run-of-&lt;id&gt;".</summary>
    private static async Task<Fixture> Make(int? commits = 1, DelegationFile? file = null, string parentMode = "auto", long freeBytes = 50_000_000_000, Action<DelegationTestHost>? setUp = null, bool switchOn = true, int replaceAttempts = 40)
    {
        var place = new DelegationPlace();
        var store = new DelegationFileStore(place.Profile, replaceAttempts); store.Save(file ?? DelegationFile.Empty);
        var host = new DelegationTestHost(store) { FirstRun = id => "run-of-" + id, MakesPanes = true };
        setUp?.Invoke(host);
        Directory.CreateDirectory(place.Repo);
        if (commits is { } count) await place.Repository(count);
        var fixture = new Fixture(place, host, new(store, host, new ChildWorktreeMaker(place.Root, _ => freeBytes), () => switchOn));
        fixture.SetParent(parentMode);
        return fixture;
    }

    /// <summary>A child record that only needs to exist: no worktree behind it.</summary>
    private static ChildRecord Record(string id, ChildState state, string parent = "parent", string? runId = null, int followUps = 0, int revision = 0, string? head = null) =>
        ChildRecord.Make(id, parent, Path.Combine(Path.GetTempPath(), "nonexistent-worktrees", id), "main", Commit, "plan", "key-" + id, followUps, revision, head, state) with { RunId = runId };

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Utf8(text)));
    private static string? TextOf(JsonObject result) => result["content"]?[0]?["text"]?.GetValue<string>();
    private static JsonNode? Data(JsonObject result, string key) => result["structuredContent"]?[key];

    internal static async Task DelegateStartsAChildInItsOwnWorktree()
    {
        // Delegate answers once the record exists in creating, then opens the child in the asked mode and runs it.
        var fixture = await Make(setUp: host => host.PaneGate = new());
        try
        {
            var head = await fixture.Place.Git("rev-parse", "HEAD"); var task = "Write the release notes.\n";
            var answer = await fixture.Delegate(task, "acceptEdits");
            var info = answer.Child ?? throw new InvalidOperationException("no child: " + answer);
            Check(answer is { Refused: null, Error: null } && info is { State: ChildState.Creating, StartingMode: "acceptEdits" } && info.Branch == "mighty/" + info.Id && ChildWorktreeMaker.IsSafeSessionId(info.Id), "the answer names the child in creating");
            // The answer came once the record was saved in creating, with the call's key and a copy of its task.
            var saved = fixture.Store.Load(); var child = saved.Children.Single();
            Check(child is { State: ChildState.Creating, ParentSessionId: "parent", StartingMode: "acceptEdits", ParentBranch: "main" } && child.Id == info.Id && child.RequestKey == DelegationRequestKey.Make("parent", "run-1", task, "acceptEdits")
                && child.WorktreePath == Path.Combine(fixture.Place.Root, info.Id) && child.BaseCommit == head && saved.Copy(info.Id, DelegationCopyKind.Task)?.Text == task && saved.Notices.Count == 0, "the record is saved before the answer");
            Check(child.ParentCheckout is { } recorded && DelegationPaths.Same(DelegationPaths.Canonical(recorded), DelegationPaths.Canonical(fixture.Place.Repo)), "the record keeps the parent's side, for cleanup and discard after the parent pane closes");
            // The host is asked for the child's pane in the asked mode, working in its own worktree.
            Check(await Eventually(() => fixture.Host.CreatedPanes.Count == 1) && fixture.Host.CreatedPanes[0] == new DelegationChildPane(info.Id, "parent", "acceptEdits", child.WorktreePath), "the host is asked for the child's pane");
            Check(System.IO.File.ReadAllText(ChildWorktree.TaskFileIn(child.WorktreePath)) == task && await fixture.Place.Git("rev-parse", "refs/heads/" + info.Branch) == head && fixture.File.Children[0].State == ChildState.Creating && fixture.Host.StartedRuns.Count == 0
                && await fixture.Coordinator.IsStarting(info.Id), "its worktree is made, and its start is still going");
            // While its start is going, discard is refused and changes nothing.
            var disk = await fixture.Place.Disk();
            Check(await fixture.Coordinator.DiscardChild(info.Id) is ChildDiscardOutcome.Failed { Message: var starting } && starting == Locale.Get("delegation.card.error.stillStarting") && await fixture.Place.Disk() == disk && fixture.Host.Stops.Count == 0
                && await fixture.Coordinator.DiscardFromCard(info.Id, []) is ChildCardDiscardOutcome.Failed, "a discard waits for the child's start");
            // Once its pane is made, its first run starts and it is running.
            fixture.Host.PaneGate!.SetResult();
            Check(await fixture.WaitFor(info.Id, ChildState.Running) && fixture.Store.Load().Children.Single().State == ChildState.Running && fixture.Store.Load().Notices.Count == 0, "once its pane is made it runs");
            var run = fixture.Host.StartedRuns.Single();
            Check(run.SessionId == info.Id && run.Input == DelegationCoordinator.FirstInput(task, new(info.Id, child.WorktreePath, child.WorktreePath, info.Branch, "main", head)) && run.Input.Contains(ChildWorktree.ReportFileIn(child.WorktreePath), StringComparison.Ordinal)
                && fixture.Child(info.Id) is { RunId: var first, AwaitedRunId: var awaited } && first == "run-of-" + info.Id && awaited == first, "its first run carries the task, and is the run its parent awaits");
            Check(await fixture.Place.Git("symbolic-ref", "--short", "HEAD") == "main" && (await fixture.Place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "the parent's checkout is untouched");
            // Through the delegation MCP server the child is tool data.
            var result = DelegationMcp.Result(answer);
            Check(result["isError"] is null && Data(result, "child")!.ToJsonString() == $$"""{"id":"{{info.Id}}","state":"creating","branch":"{{info.Branch}}","mode":"acceptEdits"}"""
                && TextOf(result) == $"Child {info.Id} is creating, on the branch {info.Branch}, starting in acceptEdits mode.", "the child is tool data");
        }
        finally { fixture.Remove(); }

        // The same call twice in one parent run returns the same child.
        fixture = await Make();
        try
        {
            var first = (await fixture.Delegate("Fix the parser.", "plan")).Child!;
            Check((await fixture.Delegate("Fix the parser.", "plan")).Child?.Id == first.Id && await fixture.WaitFor(first.Id, ChildState.Running), "the same call is the same child");
            // Again once it runs, and twice at once: still that child, now running.
            var repeats = await Task.WhenAll(fixture.Delegate("Fix the parser.", "plan"), fixture.Delegate("Fix the parser.", "plan"));
            Check(repeats.All(repeat => repeat.Child is { State: ChildState.Running } same && same.Id == first.Id), "repeats return the running child");
            // A new call made twice at once is still one child.
            var both = await Task.WhenAll(fixture.Delegate("Write the docs.", "plan"), fixture.Delegate("Write the docs.", "plan"));
            var second = both[0].Child!;
            Check(both[1].Child?.Id == second.Id && second.Id != first.Id, "two calls at once are one child");
            // The same task and mode in the parent's next run is a new call.
            fixture.SetParent(runId: "run-2");
            var third = (await fixture.Delegate("Fix the parser.", "plan")).Child!;
            Check(new[] { first.Id, second.Id, third.Id }.Distinct().Count() == 3 && await fixture.WaitFor(second.Id, ChildState.Running) && await fixture.WaitFor(third.Id, ChildState.Running), "the parent's next run makes a new child");
            var saved = fixture.Store.Load();
            Check(saved.Children.Select(child => child.Id).SequenceEqual([first.Id, second.Id, third.Id]) && saved.Children.Select(child => child.RequestKey).Distinct().Count() == 3
                && fixture.Host.CreatedPanes.Select(pane => pane.SessionId).Order().SequenceEqual(new[] { first.Id, second.Id, third.Id }.Order()), "three children, three keys, three panes");
            SameList((await fixture.Place.MightyBranches()).Split('\n').Order(), new[] { first, second, third }.Select(child => "refs/heads/" + child.Branch).Order(), "three branches");

            // Three open children hit the width cap; closing one makes room.
            var bytes = fixture.Bytes(); var file = fixture.File;
            Check(await fixture.Delegate("A fourth one.", "plan") == DelegationResponse.Refusal(DelegationReasonCode.WidthCap) && await fixture.Delegate("A fourth one.", "manual") == DelegationResponse.Refusal(DelegationReasonCode.WidthCap)
                && fixture.Bytes().SequenceEqual(bytes) && fixture.File == file && fixture.Host.CreatedPanes.Count == 3 && Directory.GetFileSystemEntries(fixture.Place.Root).Length == 3, "a fourth open child is refused with width_cap");
            fixture.Host.Close(third.Id);
            Check(await fixture.Coordinator.CloseChild(third.Id) == new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged) && (await fixture.Delegate("A fourth one.", "plan")).Child is { } fourth && await fixture.WaitFor(fourth.Id, ChildState.Running), "closing a child's pane makes room");
        }
        finally { fixture.Remove(); }

        // Only open children count toward the cap, and a pane with a parent link cannot delegate.
        var others = new[] { Record("waiting", ChildState.Waiting), Record("merged", ChildState.Merged), Record("failed", ChildState.Failed), Record("closed", ChildState.Closed), Record("discarded", ChildState.Discarded), Record("not-mine", ChildState.Running, "someone-else") };
        fixture = await Make(file: DelegationFile.Of(others));
        try
        {
            // "waiting" is a child in the delegation file; "linked" is one by its pane's link.
            fixture.Host.Set(new("waiting", "fullAccess", fixture.Place.Repo, RunId: "run-r", Activity: Running)); fixture.Host.Set(new("linked", "fullAccess", fixture.Place.Repo, ParentSessionId: "parent", RunId: "run-l", Activity: Running));
            var bytes = fixture.Bytes(); var file = fixture.File;
            foreach (var caller in new[] { "waiting", "linked" }) Check(await fixture.Delegate("Split the work further.", "plan", caller) == DelegationResponse.Refusal(DelegationReasonCode.ChildCannotDelegate), caller + " cannot delegate");
            await fixture.NothingMade(bytes, file);
            // Two open children of its own: a third may be made, and then no more.
            var third = (await fixture.Delegate("The third one.", "plan")).Child!;
            Check(await fixture.WaitFor(third.Id, ChildState.Running) && await fixture.Delegate("A fourth one.", "plan") == DelegationResponse.Refusal(DelegationReasonCode.WidthCap) && fixture.Host.CreatedPanes.Select(pane => pane.SessionId).SequenceEqual([third.Id]), "failed, closed and discarded children and another parent's do not count");
        }
        finally { fixture.Remove(); }
    }

    internal static async Task DelegateRefusesBeforeAnythingIsMade()
    {
        // A mode wider than the parent's stored mode is refused and stores no key.
        var modes = DelegationCoordinator.StartingModes;
        SameList(modes, ["plan", "manual", "acceptEdits", "auto", "fullAccess"], "the starting modes, narrowest first");
        for (var own = 0; own < modes.Length; own++) for (var asked = 0; asked < modes.Length; asked++) Check(DelegationCoordinator.IsNoWider(modes[asked], modes[own]) == (asked <= own), $"{modes[asked]} under {modes[own]}");
        Check(!DelegationCoordinator.IsNoWider("plan", "onRequest") && !DelegationCoordinator.IsNoWider("bypassPermissions", "fullAccess"), "an unknown mode is never no wider");
        var fixture = await Make(parentMode: "acceptEdits");
        try
        {
            var bytes = fixture.Bytes(); var file = fixture.File;
            foreach (var mode in new[] { "auto", "fullAccess" }) Check(await fixture.Delegate("Ship it.", mode) == DelegationResponse.Refusal(DelegationReasonCode.WiderMode), mode + " is wider than acceptEdits");
            // Not a starting mode, or no task: an error, and nothing made either.
            foreach (var mode in new[] { "bypassPermissions", "default", "", "PLAN" }) Check(await fixture.Delegate("Ship it.", mode) is { Refused: null, Child: null, Error: "mode must be one of plan, manual, acceptEdits, auto, fullAccess." }, "not a starting mode: " + mode);
            Check((await fixture.Delegate(" \n\t", "plan")).Error == "delegate needs a task: what the child should do." && (await fixture.Delegate("Ship it.\0Now.", "plan")).Error == "A task may not contain a NUL character."
                && (await fixture.Delegate(new string('x', DelegationFileStore.MaximumCopyBytes + 1), "plan")).Error == "A task may be at most 64 KiB.", "an empty task, one with a NUL and one over 64 KiB are errors");
            Check((await fixture.Call("delegate", ("task", "x"))).Error == "delegate takes exactly these string arguments: task, mode." && (await fixture.Call("delegate", ("task", "x"), ("mode", "plan"), ("branch", "main"))).Error!.Contains("task, mode", StringComparison.Ordinal), "a call needs exactly its arguments");
            fixture.Host.Close("parent");
            Check((await fixture.Delegate("Ship it.", "plan")).Error == "The calling pane is closed.", "a closed pane cannot delegate");
            await fixture.NothingMade(bytes, file);
            // The parent's stored mode is now auto: the same call in the same run makes a child in auto.
            fixture.SetParent("auto");
            var child = (await fixture.Delegate("Ship it.", "auto")).Child!;
            Check(child.StartingMode == "auto" && await fixture.WaitFor(child.Id, ChildState.Running) && fixture.Store.Load().Children.Single().StartingMode == "auto" && fixture.Host.CreatedPanes.Single().Mode == "auto", "the mode is checked against the parent's stored mode now");
        }
        finally { fixture.Remove(); }

        // A folder that cannot host a child worktree is refused before anything is made.
        var plain = await Make(commits: null); var unborn = await Make(commits: 0); var detached = await Make(commits: 2); var full = await Make(freeBytes: ChildWorktreeMaker.MinimumFreeBytes - 1);
        try
        {
            await detached.Place.Git("checkout", "-q", "--detach", "HEAD~1");
            foreach (var (refused, reason) in new[] { (plain, DelegationReasonCode.NotGit), (unborn, DelegationReasonCode.UnbornBranch), (detached, DelegationReasonCode.DetachedHead), (full, DelegationReasonCode.LowDisk) })
            {
                var bytes = refused.Bytes(); var file = refused.File;
                Same(await refused.Delegate("Do the work.", "plan"), DelegationResponse.Refusal(reason), "the workspace is refused");
                await refused.NothingMade(bytes, file, reason != DelegationReasonCode.NotGit);
            }
        }
        finally { plain.Remove(); unborn.Remove(); detached.Remove(); full.Remove(); }

        // No room for the child and its report, even after pruning, is store_full; pruning a closed child's report copy makes the room.
        static DelegationFile NearlyFull(bool closedFirst)
        {
            // Open children of other parents, each with a full report copy, then one smaller copy, leaving about 2 KB under the cap.
            var file = DelegationFile.Empty; var fullCopy = new string('r', DelegationFileStore.MaximumCopyBytes);
            for (var index = 0; ; index++)
            {
                var child = Record("filler-" + index, closedFirst && index == 0 ? ChildState.Closed : ChildState.Running, "other-" + index);
                var next = (file with { Children = file.Children.Add(child) }).SettingCopy(Copy(child.Id, DelegationCopyKind.Report, fullCopy));
                if (DelegationFileStore.Encode(next).Length > DelegationFileStore.MaximumFileBytes) break;
                file = next;
            }
            var room = DelegationFileStore.MaximumFileBytes - DelegationFileStore.Encode(file).Length;
            file = (file with { Children = file.Children.Add(Record("pad", ChildState.Running, "other-pad")) }).SettingCopy(Copy("pad", DelegationCopyKind.Report, new string('p', Math.Max(0, room - 3_000))));
            var size = DelegationFileStore.Encode(file).Length;
            Check(size <= DelegationFileStore.MaximumFileBytes && DelegationFileStore.MaximumFileBytes - size < 4_000, "the file is nearly full");
            return file;
        }
        var packed = await Make(file: NearlyFull(false)); var prunable = await Make(file: NearlyFull(true));
        try
        {
            var bytes = packed.Bytes(); var file = packed.File;
            Check(await packed.Delegate("One more child.", "plan") == DelegationResponse.Refusal(DelegationReasonCode.StoreFull), "no room even after pruning is store_full");
            await packed.NothingMade(bytes, file);
            var child = (await prunable.Delegate("One more child.", "plan")).Child!; var saved = prunable.Store.Load();
            Check(saved.Children.Any(listed => listed.Id == child.Id) && saved.Copy("filler-0", DelegationCopyKind.Report) is null && saved.Copies.Count(copy => copy.Kind == DelegationCopyKind.Report) == NearlyFull(true).Copies.Count - 1
                && prunable.Bytes().Length <= DelegationFileStore.MaximumFileBytes && await prunable.WaitFor(child.Id, ChildState.Running), "pruning the closed child's report copy makes the room");
        }
        finally { packed.Remove(); prunable.Remove(); }

        // A task at the most the tool takes still fits the child's first run input.
        var id = Wire.Id(); var deep = Path.Combine(Path.GetTempPath(), string.Concat(Enumerable.Repeat(@"deep\", 40)), "worktrees", id);
        new StartRunRequest(id, "ws-1", "claude", DelegationCoordinator.FirstInput(new string('x', DelegationFileStore.MaximumCopyBytes), new(id, deep, deep, ChildRecord.BranchName(id), "main", Commit)), []).Validate();

        // A start that fails leaves the child failed with one failed_to_start notice, and keeps what it made.
        fixture = await Make();
        try
        {
            // The host could not make the pane; then the pane was made but its run did not start.
            fixture.Host.PaneFails = true;
            var noPane = (await fixture.Delegate("Try it.", "plan")).Child!;
            Check(noPane.State == ChildState.Creating && await fixture.WaitFor(noPane.Id, ChildState.Failed) && fixture.Host.StartedRuns.Count == 0, "no pane, no run");
            fixture.Host.PaneFails = false; fixture.Host.RunFails = true;
            var noRun = (await fixture.Delegate("Try again.", "plan")).Child!;
            Check(await fixture.WaitFor(noRun.Id, ChildState.Failed) && fixture.Host.StartedRuns.Select(run => run.SessionId).SequenceEqual([noRun.Id]), "a pane, but no run");
            var saved = fixture.Store.Load();
            Check(saved.Children.All(child => child.State == ChildState.Failed) && saved.Notices.Select(notice => notice.ChildId).SequenceEqual([noPane.Id, noRun.Id]) && saved.Notices.All(notice => notice is { Kind: NoticeKind.FailedToStart, ReportRevision: 0 }), "each failed start has its one notice");
            // The parent runs Claude, so each notice was steered into its run.
            Check(saved.Notices.All(notice => notice is { Lane: DeliveryLane.Delivered, Receipt.Route: DeliveryRoute.Steer }) && fixture.Host.Taken.Select(handed => handed.Input).SequenceEqual(saved.Notices.Select(DelegationCoordinator.Text)), "the failed starts' notices reach the parent");
            // What the failed starts made stays until a human discards them.
            Check(saved.Children.All(child => System.IO.File.Exists(ChildWorktree.TaskFileIn(child.WorktreePath))) && (await fixture.Place.MightyBranches()).Split('\n').Order().SequenceEqual(new[] { noPane, noRun }.Select(child => "refs/heads/" + child.Branch).Order()), "what they made stays");
            // The same call again is that failed child, with no second start or notice.
            Check((await fixture.Delegate("Try it.", "plan")).Child is { State: ChildState.Failed } again && again.Id == noPane.Id && fixture.Host.CreatedPanes.Select(pane => pane.SessionId).SequenceEqual([noPane.Id, noRun.Id]) && fixture.Store.Load().Notices.Count == 2, "the same call is that failed child");
            // A human discards what the failed starts left, from their cards.
            // A host that breaks while starting the run has started nothing either: failed, never left creating.
            fixture.Host.RunFails = false; fixture.Host.OnFirstRun = (_, _) => throw new InvalidOperationException("the host broke");
            var broke = (await fixture.Delegate("Once more.", "plan")).Child!;
            Check(await fixture.WaitFor(broke.Id, ChildState.Failed) && fixture.Store.Load().Notices.Count == 3, "a host that breaks leaves the child failed, with its notice");
            foreach (var failed in new[] { noPane, noRun, broke }) Check(await fixture.Coordinator.DiscardFromCard(failed.Id, []) is ChildCardDiscardOutcome.Discarded, "a failed start is discarded");
            Check((await fixture.Place.MightyBranches()).Length == 0 && await fixture.Place.OnlyTheRepositoryIsListed() && Directory.GetFileSystemEntries(fixture.Place.Root).Length == 0 && fixture.Store.Load().Children.All(child => child.State == ChildState.Discarded), "nothing of them is left");
        }
        finally { fixture.Remove(); }

        // Its worktree cannot be made (a file stands where the root goes): failed before any pane is asked for.
        var blocked = await Make();
        try
        {
            System.IO.File.WriteAllText(blocked.Place.Root, "not a folder");
            var noWorktree = (await blocked.Delegate("Try it.", "plan")).Child!;
            Check(await blocked.WaitFor(noWorktree.Id, ChildState.Failed) && blocked.Store.Load().Notices.Single().Kind == NoticeKind.FailedToStart && blocked.Host.CreatedPanes.Count == 0, "no worktree, no pane");
        }
        finally { blocked.Remove(); }
    }

    /// <summary>The Mac's report fixture: the host names a child's first run "first-&lt;id&gt;".</summary>
    private static async Task<Fixture> Reports(DelegationFile? file = null)
    {
        var fixture = await Make(file: file, setUp: host => host.FirstRun = id => "first-" + id);
        fixture.SetParent(runId: "parent-run");
        return fixture;
    }

    private static async Task<ChildRecord> RunningChild(Fixture fixture, string mode = "acceptEdits")
    {
        var id = (await fixture.Delegate("Fix the parser.", mode)).Child!.Id;
        Check(await fixture.WaitFor(id, ChildState.Running), "the child's first run started");
        return fixture.Child(id);
    }

    private static async Task<string> CommitWork(Fixture fixture, ChildRecord child, string name)
    {
        System.IO.File.WriteAllText(Path.Combine(child.WorktreePath, name + ".txt"), name + "\n");
        await fixture.Place.GitIn(child.WorktreePath, "add", "-A"); await fixture.Place.GitIn(child.WorktreePath, "commit", "-q", "-m", name);
        return await fixture.Place.GitIn(child.WorktreePath, "rev-parse", "HEAD");
    }

    private static void WriteReport(ChildRecord child, string text) => System.IO.File.WriteAllBytes(ChildWorktree.ReportFileIn(child.WorktreePath), Utf8(text));

    internal static async Task RunEndsRecordReportRevisions()
    {
        // A run ending with a changed report records the next revision at the child's head and sends one reported notice.
        var fixture = await Reports();
        try
        {
            var started = await RunningChild(fixture); var id = started.Id; var run = "first-" + id;
            Check(started is { State: ChildState.Running, ReportRevision: 0, ReportHead: null, ReportDigest: null } && started.RunId == run && started.AwaitedRunId == run, "the child runs its first run, which its parent awaits");
            var head = await CommitWork(fixture, started, "parser"); var text = "Fixed the parser.\nLeft: the docs.\n";
            WriteReport(started, text);
            var notice = await fixture.Coordinator.ChildRunEnded(id, run, ChildRunEnd.Finished);
            Check(notice is { Kind: NoticeKind.Reported, ReportRevision: 1 } && notice.ChildId == id, "one reported notice for revision 1");
            var reported = fixture.Child(id);
            Check(reported is { State: ChildState.Reported, ReportRevision: 1 } && reported.ReportHead == head && reported.ReportDigest == Sha256(text) && fixture.Notices(id).Single().Id == notice!.Id
                && fixture.File.Copy(id, DelegationCopyKind.Report) is { Revision: 1, Truncated: false } copy && copy.Text == text && fixture.Store.Load() == fixture.File, "the revision is recorded at the child's head, with a copy of the report");
            // The parent runs Claude: the notice was steered into its run.
            Check(fixture.Host.Taken.Single() is { SessionId: "parent", Route: DeliveryRoute.Steer, RunId: "parent-run" } steered && steered.Input == DelegationCoordinator.Text(notice!), "the notice reaches the parent once");
            // The same end reported again, or a start of that ended run, changes nothing.
            var bytes = fixture.Bytes();
            Check(await fixture.Coordinator.ChildRunEnded(id, run, ChildRunEnd.Finished) is null, "the same end again is nothing");
            await fixture.Coordinator.ChildRunStarted(id, run);
            Check(fixture.Bytes().SequenceEqual(bytes) && fixture.Child(id) == reported, "nothing changed");
            // The parent reads the revision, its head and its body through child_status.
            Check((await fixture.Call("child_status", ("child", id))).Status is { State: ChildState.Reported, Revision: 1 } status && status.Head == head && status.Report == text, "child_status gives the report");

            // Any new run clears reported, and only a changed report makes the next revision.
            await fixture.Coordinator.ChildRunStarted(id, "human-2");
            Check(fixture.Child(id) is { State: ChildState.Running, RunId: "human-2", ReportRevision: 1 } running && running.ReportHead == head && (await fixture.Call("merge", ("child", id), ("expected_head", head))).Refused == DelegationReasonCode.NotReported
                && fixture.Store.Load() == fixture.File, "a human's run in the child's pane clears reported, so a merge waits");
            // It ends with the report unchanged: ended, and no notice for a human's run.
            Check(await fixture.Coordinator.ChildRunEnded(id, "human-2", ChildRunEnd.Finished) is null && fixture.Child(id).State == ChildState.Ended && fixture.Notices(id).Count == 1, "an unchanged report is no new revision");
            // A changed report makes revision 2 at the new head, with its one notice; a report over 64 KiB is kept as the marked copy.
            await fixture.Coordinator.ChildRunStarted(id, "human-3");
            var secondHead = await CommitWork(fixture, started, "two"); var longReport = string.Concat(Enumerable.Repeat("a line of the second report\n", 4_000));
            WriteReport(started, longReport);
            Check(await fixture.Coordinator.ChildRunEnded(id, "human-3", ChildRunEnd.Stopped) is { Kind: NoticeKind.Reported, ReportRevision: 2 }, "a changed report is revision 2");
            Check(fixture.Child(id) is { State: ChildState.Reported, ReportRevision: 2 } second && second.ReportHead == secondHead && second.ReportDigest == Sha256(longReport) && fixture.Notices(id).Select(sent => sent.ReportRevision).SequenceEqual([1, 2])
                && fixture.File.Copy(id, DelegationCopyKind.Report) is { Revision: 2, Truncated: true } cut && cut.Text.EndsWith(DelegationCopy.TruncationMarker, StringComparison.Ordinal) && cut.OriginalBytes == Encoding.UTF8.GetByteCount(longReport), "a long report is kept as the marked copy");
            Check((await fixture.Call("merge", ("child", id), ("expected_head", secondHead))).Merged?.MergedCommit == secondHead, "the reported head merges");
            // The same report written again is no new revision, even after a merge.
            await fixture.Coordinator.ChildRunStarted(id, "human-4");
            Check(fixture.Child(id).State == ChildState.Running, "a merged child runs again");
            WriteReport(started, longReport);
            Check(await fixture.Coordinator.ChildRunEnded(id, "human-4", ChildRunEnd.Errored) is null && fixture.Child(id) is { State: ChildState.Ended, ReportRevision: 2 }, "the same report again is no new revision");
            // A REPORT.md that is a folder or a junction is no report, and one with no text is none either.
            await fixture.Coordinator.ChildRunStarted(id, "human-5");
            var reportPath = ChildWorktree.ReportFileIn(started.WorktreePath);
            System.IO.File.Delete(reportPath); Directory.CreateDirectory(reportPath);
            Check(ChildReportFile.Read(reportPath) is null && await fixture.Coordinator.ChildRunEnded(id, "human-5", ChildRunEnd.Finished) is null, "a folder in the report's place is no report");
            Directory.Delete(reportPath);
            // A link in the report's place is not followed (where this account may make one).
            var outside = Path.Combine(fixture.Place.Base, "outside.md"); System.IO.File.WriteAllText(outside, "Not the child's report.\n");
            try { System.IO.File.CreateSymbolicLink(reportPath, outside); Check(ChildReportFile.Read(reportPath) is null && ChildReportFile.Read(outside) is not null, "a link in the report's place is not followed"); System.IO.File.Delete(reportPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await fixture.Coordinator.ChildRunStarted(id, "human-6");
            WriteReport(started, " \r\n\t\n");
            Check(await fixture.Coordinator.ChildRunEnded(id, "human-6", ChildRunEnd.Finished) is null && fixture.Child(id).ReportRevision == 2 && fixture.Notices(id).Count == 2 && fixture.Store.Load() == fixture.File, "a report with no text is no report");
        }
        finally { fixture.Remove(); }

        // A delegate or follow-up run ending without a new report sends one ended_without_report notice.
        fixture = await Reports();
        try
        {
            var started = await RunningChild(fixture); var id = started.Id;
            // Delegate's first run ends with REPORT.md still empty.
            var first = await fixture.Coordinator.ChildRunEnded(id, "first-" + id, ChildRunEnd.Stopped);
            Check(first is { Kind: NoticeKind.EndedWithoutReport, ReportRevision: 0 } && fixture.Child(id).State == ChildState.Ended && await fixture.Coordinator.ChildRunEnded(id, "first-" + id, ChildRunEnd.Finished) is null && fixture.Notices(id).Count == 1
                && fixture.Store.Load() == fixture.File, "the first run's end without a report sends one notice");
            // A follow-up's run: the follow-up is recorded, its delivery names the run it went to, and that run ends with no new report.
            fixture.Host.Set(id, Idle, "first-" + id);
            Check((await fixture.Call("follow_up", ("child", id), ("text", "Also update the docs."))).FollowUp is { Lane: DeliveryLane.Held }, "the follow-up is recorded");
            await fixture.Coordinator.ChildRunStarted(id, "follow-2");
            Check(await fixture.Coordinator.AwaitRun("follow-2", id) is null && fixture.Child(id).AwaitedRunId == "follow-2", "the follow-up's run is awaited");
            var second = await fixture.Coordinator.ChildRunEnded(id, "follow-2", ChildRunEnd.Errored);
            Check(second is { Kind: NoticeKind.EndedWithoutReport, ReportRevision: 0 } && await fixture.Coordinator.ChildRunEnded(id, "follow-2", ChildRunEnd.Errored) is null && await fixture.Coordinator.AwaitRun("follow-2", id) is null && fixture.Notices(id).Count == 2, "its end sends one notice, never two");
            // When the run's end came before its delivery named it, the notice goes out then, once.
            await fixture.Coordinator.ChildRunStarted(id, "follow-3");
            Check(await fixture.Coordinator.ChildRunEnded(id, "follow-3", ChildRunEnd.Finished) is null && await fixture.Coordinator.AwaitRun("follow-3", id) is { Kind: NoticeKind.EndedWithoutReport } && await fixture.Coordinator.AwaitRun("follow-3", id) is null
                && fixture.Notices(id).All(notice => notice.Kind == NoticeKind.EndedWithoutReport) && fixture.Notices(id).Count == 3, "an end that came first is noticed when the run is awaited");
            // A follow-up's run that ends with a report sends the reported notice only.
            await fixture.Coordinator.ChildRunStarted(id, "follow-4");
            Check(await fixture.Coordinator.AwaitRun("follow-4", id) is null, "awaited");
            WriteReport(started, "Docs updated.\n");
            Check((await fixture.Coordinator.ChildRunEnded(id, "follow-4", ChildRunEnd.Finished))?.Kind == NoticeKind.Reported && await fixture.Coordinator.AwaitRun("follow-4", id) is null && fixture.Notices(id).Select(notice => notice.Kind).Last() == NoticeKind.Reported && fixture.Notices(id).Count == 4, "a report sends the reported notice only");
            // A human's run that ends with no new report sends nothing.
            await fixture.Coordinator.ChildRunStarted(id, "human-5");
            Check(await fixture.Coordinator.ChildRunEnded(id, "human-5", ChildRunEnd.Finished) is null && fixture.Notices(id).Count == 4 && fixture.Store.Load() == fixture.File, "a human's run sends nothing");
        }
        finally { fixture.Remove(); }

        // The first run's end may come before the host's start answers, and still sends one notice.
        fixture = await Reports();
        try
        {
            var coordinator = fixture.Coordinator;
            fixture.Host.OnFirstRun = async (child, run) => { await coordinator.ChildRunStarted(child, run); await coordinator.ChildRunEnded(child, run, ChildRunEnd.Errored); };
            var id = (await fixture.Delegate("Fix the parser.", "plan")).Child!.Id;
            Check(await Eventually(async () => !await coordinator.IsStarting(id)) && fixture.Child(id) is { State: ChildState.Ended } ended && ended.RunId == "first-" + id && ended.AwaitedRunId == "first-" + id
                && fixture.Notices(id).Single().Kind == NoticeKind.EndedWithoutReport && fixture.Store.Load() == fixture.File, "an end before the start's answer still sends one notice");
        }
        finally { fixture.Remove(); }

        // Run events take turns, so an end half recorded is never overtaken.
        fixture = await Reports();
        try
        {
            var coordinator = fixture.Coordinator; var id = (await RunningChild(fixture)).Id;
            var gate = new TaskCompletionSource(); var asked = 0;
            fixture.Host.StateGate = _ => { Interlocked.Increment(ref asked); return gate.Task; };
            // Delegate's first run ends; its end waits halfway, before it is saved, while the next run's start and its parent's await come in.
            var end = coordinator.ChildRunEnded(id, "first-" + id, ChildRunEnd.Finished);
            Check(await Eventually(() => Volatile.Read(ref asked) > 0), "the end is waiting on the host");
            var start = coordinator.ChildRunStarted(id, "follow-2"); await Task.Delay(100);
            var awaited = coordinator.AwaitRun("follow-2", id); await Task.Delay(100);
            Check(fixture.Child(id).RunId == "first-" + id && !end.IsCompleted && !start.IsCompleted && !awaited.IsCompleted, "the start and the await wait their turn");
            gate.SetResult();
            // Each is recorded whole, in the order it came: the first run's notice is not lost.
            Check(await end is { Kind: NoticeKind.EndedWithoutReport, ReportRevision: 0 }, "the first run's notice is not lost");
            await start;
            Check(await awaited is null && fixture.Child(id) is { State: ChildState.Running, RunId: "follow-2", AwaitedRunId: "follow-2" } && (await coordinator.ChildRunEnded(id, "follow-2", ChildRunEnd.Finished))?.Kind == NoticeKind.EndedWithoutReport
                && fixture.Notices(id).Count == 2 && fixture.Store.Load() == fixture.File, "each is recorded whole, in the order it came");
        }
        finally { fixture.Remove(); }

        // A run killed by quitting the app sends no notice and shows interrupted.
        fixture = await Reports();
        try
        {
            var started = await RunningChild(fixture); var id = started.Id;
            // Killed by quitting with a report written: interrupted, no notice, no revision.
            WriteReport(started, "Half done.\n");
            Check(await fixture.Coordinator.ChildRunEnded(id, "first-" + id, ChildRunEnd.Quit) is null && fixture.Child(id) is { State: ChildState.Interrupted, ReportRevision: 0, ReportHead: null } && fixture.Notices(id).Count == 0
                && (await fixture.Call("list_children")).Children!.Single().State == ChildState.Interrupted && fixture.Store.Load() == fixture.File, "a quit interrupts with no notice and no revision");
            // The report it left is the next run's to record.
            await fixture.Coordinator.ChildRunStarted(id, "human-2");
            Check((await fixture.Coordinator.ChildRunEnded(id, "human-2", ChildRunEnd.Finished))?.ReportRevision == 1 && fixture.Child(id).State == ChildState.Reported, "the report it left is the next run's to record");
        }
        finally { fixture.Remove(); }
        // At the next launch, the children whose runs the quit killed come back interrupted, with no notice.
        fixture = await Reports(DelegationFile.Of([Record("running", ChildState.Running, runId: "r"), Record("waiting", ChildState.Waiting, runId: "w"), Record("reported", ChildState.Reported), Record("closed", ChildState.Closed, runId: "c")]));
        try
        {
            SameList(await fixture.Coordinator.InterruptRunsKilledByQuit(), ["running", "waiting"], "the launch's sweep finds the runs the quit killed");
            Check(fixture.File.Children.Select(child => child.State).SequenceEqual([ChildState.Interrupted, ChildState.Interrupted, ChildState.Reported, ChildState.Closed]) && fixture.File.Notices.Count == 0 && fixture.Store.Load() == fixture.File, "they are interrupted, with no notice");
            // Their killed runs' ends, should the host still report them, and runs of a closed child change nothing.
            var bytes = fixture.Bytes();
            Check(await fixture.Coordinator.ChildRunEnded("running", "r", ChildRunEnd.Finished) is null, "a killed run's late end is nothing");
            await fixture.Coordinator.ChildRunStarted("closed", "c2");
            Check(await fixture.Coordinator.ChildRunEnded("closed", "c2", ChildRunEnd.Finished) is null && (await fixture.Coordinator.InterruptRunsKilledByQuit()).Count == 0 && fixture.Bytes().SequenceEqual(bytes), "a closed child takes no runs");
            // The coordinator hands on the child list at load and after each change; a run event reaches it through the app's ordered pump.
            Check(fixture.Coordinator.ChildRows.TryRead(out var rows) && rows.Select(row => row.State).SequenceEqual([ChildState.Interrupted, ChildState.Interrupted, ChildState.Reported, ChildState.Closed]), "the rows follow the file");
            var pump = new DelegationRunEventPump(fixture.Coordinator);
            pump.Send(new DelegationRunEvent.Started("running", "r2")); pump.Send(new DelegationRunEvent.Ended("running", "r2", ChildRunEnd.Stopped));
            await pump.Finish();
            Check(fixture.Child("running") is { State: ChildState.Ended, RunId: "r2" } && fixture.Coordinator.ChildRows.TryRead(out rows) && rows[0].State == ChildState.Ended, "the pump's events are recorded in order");
        }
        finally { fixture.Remove(); }
    }

    internal static async Task TheGatesDiscardAndStatusTools()
    {
        var reportedHead = new string('b', 40); var reportText = "# 보고\n\nFixed the parser.\n";
        ChildRecord[] children = [Record("running", ChildState.Running), Record("reported", ChildState.Reported, revision: 2, head: reportedHead), Record("merged", ChildState.Merged, "other", revision: 1, head: new string('c', 40)),
            Record("failed", ChildState.Failed), Record("closed", ChildState.Closed, revision: 3, head: new string('d', 40)), Record("discarded", ChildState.Discarded), Record("someone-elses", ChildState.Running, "stranger")];
        var standard = DelegationFile.Of(children, copies: [Copy("reported", DelegationCopyKind.Report, reportText, 2), Copy("merged", DelegationCopyKind.Report, "Merged work.", 1), Copy("someone-elses", DelegationCopyKind.Report, "Not yours.", 1)]);
        var off = await Make(commits: null, file: standard, switchOn: false); var fixture = await Make(commits: null, file: standard);
        try
        {
            static (string, string)[] Arguments(DelegationToolManifest.Tool tool) => tool.Arguments.Select(argument => (argument.Name, argument.Name == "mode" ? "plan" : argument.Name == "child" ? "reported" : "x")).ToArray();
            // The gates answer before any tool.
            foreach (var tool in DelegationToolManifest.All) Check(await off.Call(tool.Name, Arguments(tool)) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage), tool.Name + " reaches nothing with the switch off");
            var bytes = fixture.Bytes(); var file = fixture.File;
            foreach (var caller in new[] { Caller("gemini", provider: "gemini"), Caller("shell", kind: "shell") })
                foreach (var tool in DelegationToolManifest.All) Check(await fixture.Coordinator.Handle(Request(tool.Name, Arguments(tool)), caller) == DelegationResponse.Refusal(DelegationReasonCode.ClaudeOnly), $"{caller.PaneId} {tool.Name} is claude_only");
            Check(await fixture.Coordinator.Handle(Request("discard", ("child", "reported")), Caller("detached", delegation: false)) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage)
                && await fixture.Call("no_such_tool") == DelegationResponse.Failure("Unknown delegation tool no_such_tool."), "a detached run and an unknown tool reach nothing");
            // discard from the parent is always refused with discard_human_only, whatever it names.
            foreach (var arguments in new (string, string)[][] { [("child", "reported")], [("child", "no-such-child")], [], [("child", "reported"), ("force", "true")] })
                Check(await fixture.Call("discard", arguments) == DelegationResponse.Refusal(DelegationReasonCode.DiscardHumanOnly), "discard is for the human only");
            var refusal = DelegationMcp.Result(await fixture.Call("discard", ("child", "reported")));
            Check(refusal["isError"]?.GetValue<bool>() == true && refusal["structuredContent"]!.ToJsonString() == """{"refused":"discard_human_only"}""" && TextOf(refusal) == "Refused: discard_human_only. Nothing was changed.", "the refusal is tool data naming that one reason code");
            // A call without exactly its arguments is answered and changes nothing.
            Check((await fixture.Call("merge", ("child", "reported"))) is { Refused: null, Error: "merge takes exactly these string arguments: child, expected_head." } && (await fixture.Call("child_status")).Error == "child_status takes exactly these string arguments: child."
                && (await fixture.Call("list_children", ("child", "reported"))).Error == "list_children takes no arguments." && (await fixture.Call("follow_up", ("child", "reported"))).Error == "follow_up takes exactly these string arguments: child, text.", "a call needs exactly its tool's arguments");
            Check(fixture.Bytes().SequenceEqual(bytes) && fixture.File == file && fixture.Host.Calls.Count == 0 && off.Host.Calls.Count == 0 && !Directory.Exists(fixture.Place.Root), "the gates and refusals changed nothing and reached no pane");

            // list_children gives the caller's children with id, state and current revision.
            var mine = await fixture.Call("list_children");
            (string Id, ChildState State, int Revision)[] expected = [("running", ChildState.Running, 0), ("reported", ChildState.Reported, 2), ("failed", ChildState.Failed, 0), ("closed", ChildState.Closed, 3), ("discarded", ChildState.Discarded, 0)];
            Check(mine is { Refused: null, Error: null, Child: null, Status: null } && mine.Children!.Select(child => (child.Id, child.State, child.Revision)).SequenceEqual(expected), "the caller's children in the order they were made");
            // Another parent sees only its own child; a pane without children gets an empty list.
            Check((await fixture.CallFrom("other", "list_children")).Children!.SequenceEqual([DelegationChildSummary.Of(children[2])]) && (await fixture.CallFrom("lonely", "list_children")).Children!.Count == 0, "each parent sees only its own");
            var listed = DelegationMcp.Result(mine);
            Check(listed["isError"] is null && Data(listed, "children")!.AsArray().Select(item => (item!["id"]!.GetValue<string>(), item["state"]!.GetValue<string>(), item["revision"]!.GetValue<int>())).SequenceEqual(expected.Select(child => (child.Id, DelegationNames.Of(child.State), child.Revision)))
                && TextOf(listed) == "Your children:\n- running: running, report revision 0\n- reported: reported, report revision 2\n- failed: failed, report revision 0\n- closed: closed, report revision 3\n- discarded: discarded, report revision 0", "the list is tool data");
            var none = DelegationMcp.Result(await fixture.CallFrom("lonely", "list_children"));
            Check(Data(none, "children")!.AsArray().Count == 0 && TextOf(none) == "You have no children.", "no children is an empty list");

            // child_status gives one child's state, head and report body as tool data.
            var answer = await fixture.Call("child_status", ("child", "reported")); var status = answer.Status!;
            Check(answer is { Refused: null, Error: null, Child: null, Children: null } && status == DelegationChildStatus.Of(children[1], Copy("reported", DelegationCopyKind.Report, reportText, 2)) && status is { Id: "reported", State: ChildState.Reported, Revision: 2, ReportTruncated: false }
                && status.Head == reportedHead && status.Report == reportText, "the state, head and report body");
            var result = DelegationMcp.Result(answer); var data = Data(result, "status")!;
            Check(result["isError"] is null && data["id"]!.GetValue<string>() == "reported" && data["state"]!.GetValue<string>() == "reported" && data["revision"]!.GetValue<int>() == 2 && data["head"]!.GetValue<string>() == reportedHead && data["report"]!.GetValue<string>() == reportText
                && data["reportTruncated"]!.GetValue<bool>() == false && TextOf(result) == $"Child reported is reported. Its report revision 2 was made at head {reportedHead}.\n\nREPORT.md:\n{reportText}", "the status is tool data, the report body in both");
            // Before its first report a child has no head and no report body.
            var running = (await fixture.Call("child_status", ("child", "running"))).Status!; var runningResult = DelegationMcp.Result(new(Status: running));
            Check(running is { State: ChildState.Running, Revision: 0, Head: null, Report: null, ReportTruncated: false } && Data(runningResult, "status")!.ToJsonString().Contains("\"head\":null,\"report\":null", StringComparison.Ordinal)
                && TextOf(runningResult) == "Child running is running. It has not reported yet.", "a child that has not reported");
            // A closed child whose copy was pruned keeps its head only.
            var closed = (await fixture.Call("child_status", ("child", " closed\n"))).Status!;
            Check(closed is { State: ChildState.Closed, Revision: 3, Report: null } && closed.Head == new string('d', 40) && TextOf(DelegationMcp.Result(new(Status: closed))) == $"Child closed is closed. Its report revision 3 was made at head {new string('d', 40)}. Its REPORT.md copy is no longer kept.", "a closed child whose copy was pruned");
            Check((await fixture.CallFrom("other", "child_status", ("child", "merged"))).Status is { State: ChildState.Merged, Report: "Merged work." } && (await fixture.Call("child_status", ("child", "discarded"))).Status?.State == ChildState.Discarded
                && (await fixture.Call("child_status", ("child", "failed"))).Status?.State == ChildState.Failed, "every state is read");
            // A child that is not the caller's is an error, and changes nothing.
            foreach (var (id, caller) in new[] { ("someone-elses", "parent"), ("reported", "other"), ("no-such-child", "parent"), ("", "parent"), ("reported", "lonely") })
                Check(await fixture.CallFrom(caller, "child_status", ("child", id)) == DelegationResponse.Failure(DelegationCoordinator.NotYourChildMessage(id)), $"{caller} may not read {id}");
            Same(DelegationCoordinator.NotYourChildMessage("x"), "No child \"x\" of yours. list_children names your children.", "the words for a child that is not the caller's");
            Check(fixture.Bytes().SequenceEqual(bytes) && fixture.File == file && fixture.Host.Calls.Count == 0, "reading changed nothing and reached no pane");

            // With the switch on, each tool answers itself, well within the limit.
            foreach (var tool in DelegationToolManifest.All)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew(); var given = await fixture.Call(tool.Name, Arguments(tool));
                Check(clock.Elapsed.TotalSeconds < DelegationCoordinator.AnswerSeconds && (given.Refused is not null || given.Error is not null || given.Children is not null || given.Status is not null || given.Merged is not null || given.FollowUp is not null) && given != DelegationResponse.Failure(DelegationIOHandler.DetachedMessage)
                    && given.Refused != DelegationReasonCode.ClaudeOnly && given.Error != "Unknown delegation tool " + tool.Name + ".", tool.Name + " is answered by its tool");
            }
        }
        finally { off.Remove(); fixture.Remove(); }

        // A long report is the marked copy, and a stale copy is no report.
        var longReport = string.Concat(Enumerable.Repeat("line of the report\n", 5_000));
        fixture = await Make(commits: null, file: DelegationFile.Of([Record("long", ChildState.Reported, revision: 1, head: reportedHead), Record("stale", ChildState.Reported, revision: 2, head: reportedHead)],
            copies: [Copy("long", DelegationCopyKind.Report, longReport, 1), Copy("stale", DelegationCopyKind.Report, "The first report.", 1)]));
        try
        {
            var status = (await fixture.Call("child_status", ("child", "long"))).Status!; var body = status.Report!;
            Check(status.ReportTruncated && body.EndsWith(DelegationCopy.TruncationMarker, StringComparison.Ordinal) && longReport.StartsWith(body[..^DelegationCopy.TruncationMarker.Length], StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(body) <= DelegationFileStore.MaximumCopyBytes
                && Data(DelegationMcp.Result(new(Status: status)), "status")!["reportTruncated"]!.GetValue<bool>(), "a long report is the marked copy");
            Check((await fixture.Call("child_status", ("child", "stale"))).Status is { Revision: 2, Report: null, ReportTruncated: false } stale && stale.Head == reportedHead, "a copy of an earlier revision is not the current report");
        }
        finally { fixture.Remove(); }

        // The delegation server's side of the protocol: its name, its six tools, and calls forwarded with their string arguments.
        var calls = new List<DelegationRequest>(); var reply = DelegationResponse.Refusal(DelegationReasonCode.ClaudeOnly);
        var mcp = new DelegationMcp((request, _) => { lock (calls) calls.Add(request); return Task.FromResult(reply); });
        static JsonNode Json(string? line) => JsonNode.Parse(line ?? throw new InvalidOperationException("no reply"))!;
        static string ToolCall(string name, string arguments) => """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":""" + System.Text.Json.JsonSerializer.Serialize(name) + ",\"arguments\":" + arguments + "}}";
        var hello = Json(await mcp.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""))["result"]!;
        Check(hello["protocolVersion"]!.GetValue<string>() == "2025-03-26" && hello["serverInfo"]!["name"]!.GetValue<string>() == "mighty-delegation" && DelegationMcp.ServerName != AgentIOBinding.ServerName && hello["capabilities"]!["tools"] is not null
            && hello["instructions"] is null && DelegationMcp.HeadlessArgument == "--agent-delegation-mcp", "initialize names the delegation server, not mighty-terminal");
        Check(Json(await mcp.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}"""))["result"]!["protocolVersion"]!.GetValue<string>() == "2025-06-18" && Json(await mcp.Handle("""{"jsonrpc":"2.0","id":"p","method":"ping"}"""))["result"] is JsonObject { Count: 0 }, "an unknown version gets the latest, and ping answers");
        var tools = Json(await mcp.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))["result"]!["tools"]!.AsArray();
        SameList(tools.Select(tool => tool!["name"]!.GetValue<string>()), ["delegate", "list_children", "child_status", "merge", "follow_up", "discard"], "the server lists exactly the six tools");
        foreach (var tool in tools)
        {
            var schema = tool!["inputSchema"]!; var manifest = DelegationToolManifest.Named(tool["name"]!.GetValue<string>())!;
            Check(tool["description"]!.GetValue<string>() == manifest.Description && schema["type"]!.GetValue<string>() == "object" && schema["additionalProperties"]!.GetValue<bool>() == false
                && schema["required"]!.AsArray().Select(name => name!.GetValue<string>()).SequenceEqual(manifest.Arguments.Select(argument => argument.Name))
                && schema["properties"]!.AsObject().Select(property => property.Key).Order().SequenceEqual(manifest.Arguments.Select(argument => argument.Name).Order())
                && schema["properties"]!.AsObject().All(property => property.Value!["type"]!.GetValue<string>() == "string" && property.Value["description"]!.GetValue<string>().Length > 0), manifest.Name + " lists its string arguments, all required");
        }
        // A call is forwarded with its string arguments; a refusal is tool data with its one reason code.
        var refused = Json(await mcp.Handle(ToolCall("delegate", """{"task":"Write the release notes","mode":"plan"}""")))["result"]!;
        Check(refused["isError"]!.GetValue<bool>() && refused["structuredContent"]!.ToJsonString() == """{"refused":"claude_only"}""" && refused["content"]![0]!["text"]!.GetValue<string>() == "Refused: claude_only (this pane's agent cannot use the delegation tools). Nothing was changed."
            && calls.Single() is { Tool: "delegate" } forwarded && forwarded.Arguments.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value).SequenceEqual(["mode=plan", "task=Write the release notes"]), "the call is forwarded as it was given");
        reply = DelegationResponse.Failure(DelegationIOHandler.DetachedMessage);
        var failed = Json(await mcp.Handle(ToolCall("list_children", "{}")))["result"]!;
        Check(failed["isError"]!.GetValue<bool>() && failed["structuredContent"] is null && failed["content"]![0]!["text"]!.GetValue<string>() == DelegationIOHandler.DetachedMessage && TextOf(DelegationMcp.Result(new())) == "Mighty Claude did not answer the request.", "a failure is the tool's error text");
        // Malformed calls never reach the app.
        calls.Clear();
        foreach (var name in new[] { "Delegate", "../delegate", "", new string('a', 65), "1delegate" }) Check(Json(await mcp.Handle(ToolCall(name, "{}")))["error"]!["code"]!.GetValue<int>() == -32602, "no tool is named " + name);
        foreach (var arguments in new[] { """{"task":42}""", """{"task":["a"]}""", """{"Task":"x"}""", "\"not an object\"", "null" }) Check(Json(await mcp.Handle(ToolCall("delegate", arguments)))["result"]!["isError"]!.GetValue<bool>(), "arguments must be strings with lowercase names: " + arguments);
        Check(Json(await new DelegationMcp(null).Handle(ToolCall("delegate", """{"task":"x","mode":"plan"}""")))["result"]!["content"]![0]!["text"]!.GetValue<string>() == "This MCP server was started without a Mighty Claude pane connection, so it cannot reach the app." && calls.Count == 0, "a server with no pane connection reaches nothing");
        Check(await mcp.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""") is null && await mcp.Handle("  \t") is null && Json(await mcp.Handle("{"))["error"]!["code"]!.GetValue<int>() == -32700 && Json(await mcp.Handle("""{"jsonrpc":"1.0","id":3,"method":"ping"}"""))["error"]!["code"]!.GetValue<int>() == -32600
            && Json(await mcp.Handle("""{"jsonrpc":"2.0","id":true,"method":"ping"}"""))["error"]!["code"]!.GetValue<int>() == -32600 && Json(await mcp.Handle("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}"""))["error"]!["message"]!.GetValue<string>() == "Method not found: resources/list", "notifications get no reply, and bad requests their protocol error");
    }

    /// <summary>A coordinator over <paramref name="children"/>, saved in the profile before it loads; the pane "parent" runs in the repository.</summary>
    private static Fixture Tools(DelegationPlace place, IEnumerable<ChildRecord> children, int replaceAttempts = 40)
    {
        var store = new DelegationFileStore(place.Profile, replaceAttempts); store.Save(DelegationFile.Of(children));
        var host = new DelegationTestHost(store); host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Running));
        return new(place, host, new(store, host, place.Maker, () => true));
    }

    /// <summary>Makes every save of the profile's delegation file fail, as a disk that refuses it: a folder stands where the file goes.</summary>
    private static Action BlockSaves(DelegationFileStore store)
    {
        var aside = store.FilePath + ".aside";
        System.IO.File.Move(store.FilePath, aside); Directory.CreateDirectory(store.FilePath);
        return () => { Directory.Delete(store.FilePath); System.IO.File.Move(aside, store.FilePath); };
    }

    internal static async Task TheMergeToolAndFollowUps()
    {
        // merge fast-forwards a reported child and writes its MergeRecord.
        var place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var (child, head) = await place.ReportedChild("c1", ["a.txt", "b.txt"], place.Repo);
            var fixture = Tools(place, [child]);
            var answer = await fixture.Call("merge", ("child", " c1\n"), ("expected_head", head));
            var record = answer.Merged ?? throw new InvalidOperationException("not merged: " + answer);
            Check(answer is { Refused: null, Error: null } && record == new MergeRecord("c1", MergeKind.ToolFastForward, "main", start, head, head), "the fast-forward's record is the answer");
            Check(await place.Git("symbolic-ref", "HEAD") == "refs/heads/main" && await place.Git("rev-parse", "HEAD") == head && (await place.Git("rev-list", "--merges", "HEAD")).Length == 0 && (await place.Git("status", "--porcelain", "--untracked-files=no")).Length == 0
                && await place.Git("rev-parse", "refs/heads/mighty/c1") == head, "one fast-forward, and the child's branch is only read");
            // The MergeRecord is saved in the delegation file with the child merged, and a relaunch reads both back.
            var file = fixture.File;
            Check(file.Merges.SequenceEqual([record]) && file.Children[0].State == ChildState.Merged && fixture.Store.Load() == file && fixture.Relaunch().File.Merges.SequenceEqual([record]), "the record is saved with the child merged");
            var result = DelegationMcp.Result(answer);
            Check(result["isError"] is null && Data(result, "merged")!.ToJsonString() == $$"""{"child":"c1","kind":"tool_fast_forward","parentBranch":"main","preMergeCommit":"{{start}}","mergedCommit":"{{head}}","childHead":"{{head}}"}"""
                && TextOf(result) == $"Merged child c1: main fast-forwarded from {start} to {head}. The merge is recorded; only a human can undo it, from the child's card.", "the merge is tool data");
            // A merged child is no longer reported: a second merge is refused and changes nothing.
            var bytes = fixture.Bytes(); var before = await place.Snapshot(child.WorktreePath);
            Check(await fixture.Call("merge", ("child", "c1"), ("expected_head", head)) == DelegationResponse.Refusal(DelegationReasonCode.NotReported) && fixture.Bytes().SequenceEqual(bytes) && await place.Snapshot(child.WorktreePath) == before && fixture.File == file, "a second merge is refused");
        }
        finally { place.Remove(); }

        // Each merge refusal is answered with its reason code and changes nothing.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var running = await place.Child("running", place.Repo);
            var (moved, movedHead) = await place.ReportedChild("moved", parentCheckout: place.Repo); var (dirty, dirtyHead) = await place.ReportedChild("dirty", parentCheckout: place.Repo); var (gone, goneHead) = await place.ReportedChild("gone", parentCheckout: place.Repo);
            var (first, firstHead) = await place.ReportedChild("first", parentCheckout: place.Repo); var (late, lateHead) = await place.ReportedChild("late", parentCheckout: place.Repo);
            var fixture = Tools(place, [running, moved, dirty, gone, first, late]);
            // A tracked change in one child, and another child's worktree gone.
            System.IO.File.WriteAllText(Path.Combine(dirty.WorktreePath, "dirty.txt"), "edited\n"); RemoveFolder(gone.WorktreePath);
            async Task Refuses(string id, string expectedHead, DelegationReasonCode reason, ChildRecord look, string why)
            {
                var bytes = fixture.Bytes(); var file = fixture.File; var before = await place.Snapshot(look.WorktreePath);
                var answer = await fixture.Call("merge", ("child", id), ("expected_head", expectedHead));
                Same(answer, DelegationResponse.Refusal(reason), why);
                Check(fixture.Bytes().SequenceEqual(bytes) && fixture.File == file && await place.Snapshot(look.WorktreePath) == before, why + ": nothing changed");
                // Through the delegation MCP server: exactly that one reason code.
                var result = DelegationMcp.Result(answer);
                Check(result["isError"]!.GetValue<bool>() && result["structuredContent"]!.ToJsonString() == $$"""{"refused":"{{DelegationNames.Of(reason)}}"}""" && TextOf(result) == $"Refused: {DelegationNames.Of(reason)}. Nothing was changed.", why + ": one reason code");
            }
            await Refuses("running", running.BaseCommit, DelegationReasonCode.NotReported, running, "running, no report");
            await Refuses("moved", running.BaseCommit, DelegationReasonCode.HeadMoved, moved, "expected head is not the reported one");
            await Refuses("dirty", dirtyHead, DelegationReasonCode.TrackedChanges, running, "tracked change in the child");
            await Refuses("gone", goneHead, DelegationReasonCode.WorktreeMissing, running, "worktree gone");
            var later = await place.Commit(moved.WorktreePath, "m2.txt", "later\n");
            await Refuses("moved", movedHead, DelegationReasonCode.HeadMoved, moved, "child branch moved past its report");
            await Refuses("moved", later, DelegationReasonCode.HeadMoved, moved, "a head that was never reported");
            await place.GitIn(moved.WorktreePath, "reset", "-q", "--hard", movedHead);
            // The parent's side: a tracked change, then another branch checked out.
            System.IO.File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "parent edit\n");
            await Refuses("moved", movedHead, DelegationReasonCode.TrackedChanges, moved, "tracked change in the parent");
            await place.Git("checkout", "-q", "--", "file-0.txt"); await place.Git("checkout", "-q", "-b", "elsewhere");
            await Refuses("moved", movedHead, DelegationReasonCode.BranchNotCheckedOut, moved, "parent branch not checked out");
            await place.Git("checkout", "-q", "main");
            // Once one child is merged, a sibling made from the same base has diverged.
            Check((await fixture.Call("merge", ("child", "first"), ("expected_head", firstHead))).Merged?.MergedCommit == firstHead, "the first child merges");
            await Refuses("late", lateHead, DelegationReasonCode.Diverged, late, "diverged");
            Check(await place.Git("rev-parse", "HEAD") == firstHead && fixture.File.Merges.Select(merge => merge.ChildId).SequenceEqual(["first"]), "only the first is merged");
            // A child that is not the caller's is an error, and changes nothing either.
            var saved = fixture.Bytes();
            foreach (var (id, caller) in new[] { ("late", "other"), ("no-such-child", "parent") })
                Check(await fixture.CallFrom(caller, "merge", ("child", id), ("expected_head", lateHead)) == DelegationResponse.Failure(DelegationCoordinator.NotYourChildMessage(id)), $"{caller} may not merge {id}");
            Check(fixture.Bytes().SequenceEqual(saved), "nothing changed");
            // The diverged sibling goes in by the card, as one merge commit.
            Check(await fixture.Coordinator.MergeFromCard("late") is ChildMergeOutcome.Merged { Record.Kind: MergeKind.CardMergeCommit } && fixture.File.Children.Single(child => child.Id == "late").State == ChildState.Merged, "the card merges the diverged sibling");
        }
        finally { place.Remove(); }

        // Merges at once take turns, so exactly one fast-forwards.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (one, oneHead) = await place.ReportedChild("one", parentCheckout: place.Repo); var (two, twoHead) = await place.ReportedChild("two", parentCheckout: place.Repo);
            var fixture = Tools(place, [one, two]);
            // The same child three times and a sibling from the same base, all at once.
            var answers = await Task.WhenAll(fixture.Call("merge", ("child", "one"), ("expected_head", oneHead)), fixture.Call("merge", ("child", "one"), ("expected_head", oneHead)), fixture.Call("merge", ("child", "two"), ("expected_head", twoHead)), fixture.Call("merge", ("child", "one"), ("expected_head", oneHead)));
            // One fast-forward; every other call is refused, never failed by git running twice at once.
            Check(answers.Count(answer => answer.Merged is not null) == 1 && answers.Where(answer => answer.Merged is null).All(answer => answer.Refused is DelegationReasonCode.NotReported or DelegationReasonCode.Diverged) && fixture.File.Merges.Count == 1
                && fixture.File.Children.Count(child => child.State == ChildState.Merged) == 1 && await place.Git("rev-parse", "HEAD") == fixture.File.Merges[0].MergedCommit, "exactly one fast-forward: " + string.Join(" | ", answers.Select(answer => answer.ToString())));
        }
        finally { place.Remove(); }

        // A tool merge whose save fails tells the parent it stands, and the next save writes it.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (child, head) = await place.ReportedChild("c1", parentCheckout: place.Repo);
            var fixture = Tools(place, [child], 2);
            var unblock = BlockSaves(fixture.Store);
            var answer = await fixture.Call("merge", ("child", "c1"), ("expected_head", head));
            // The fast-forward landed, and the parent is told so and not to merge again.
            Check(answer.Error is { } error && error.StartsWith($"The child is merged: main now points at {head}, and the app keeps the merge on record, but the delegation file could not be saved (", StringComparison.Ordinal)
                && error.EndsWith("). Mighty Claude saves it again by itself; nothing is needed from you. Do not merge this child again.", StringComparison.Ordinal), "the parent is told the merge stands: " + answer.Error);
            Check(await place.Git("rev-parse", "HEAD") == head && fixture.File.Merges.Select(merge => merge.ChildId).SequenceEqual(["c1"]) && fixture.File.Children[0].State == ChildState.Merged, "the merge is on record in memory");
            Check((await fixture.Call("merge", ("child", "c1"), ("expected_head", head))).Refused == DelegationReasonCode.NotReported, "and is not merged again");
            unblock();
            Check(fixture.Store.Load().Merges.Count == 0, "the disk does not have it yet");
            await fixture.Coordinator.Save();
            Check(fixture.Store.Load() == fixture.File && fixture.Relaunch().File.Merges.Count == 1, "the next save writes it");
        }
        finally { place.Remove(); }

        // follow_up records at most two per child, and refuses the third with follow_up_limit.
        place = new DelegationPlace();
        try
        {
            var fixture = Tools(place, [Record("c1", ChildState.Running), Record("c2", ChildState.Reported)]);
            var first = await fixture.Call("follow_up", ("child", "c1"), ("text", "Also add tests.")); var info = first.FollowUp!;
            // c1 has no pane, so the follow-up is held for it.
            Check(first is { Refused: null, Error: null, Merged: null } && info is { Child: "c1", Lane: DeliveryLane.Held, Remaining: 1, Id.Length: > 0 }, "the first follow-up is recorded");
            var file = fixture.File;
            Check(file.FollowUps.SequenceEqual([new FollowUp(info.Id, "c1", "Also add tests.", DeliveryLane.Held)]) && file.Children.Select(child => child.FollowUpCount).SequenceEqual([1, 0]) && fixture.Store.Load() == file, "written to disk before the answer");
            var result = DelegationMcp.Result(first);
            Check(result["isError"] is null && Data(result, "followUp")!.ToJsonString() == $$"""{"id":"{{info.Id}}","child":"c1","lane":"held","remaining":1}"""
                && TextOf(result) == $"Follow-up {info.Id} for child c1 is recorded; Mighty Claude hands it to the child exactly once. This child may get 1 more.", "the follow-up is tool data");
            // The second, kept exactly as given; the other child's count is its own.
            var words = "그리고 문서도 고쳐 줘.\n";
            var second = await fixture.Call("follow_up", ("child", " c1\n"), ("text", words));
            Check(second.FollowUp is { Remaining: 0 } secondInfo && secondInfo.Id != info.Id && TextOf(DelegationMcp.Result(second))!.EndsWith("It was this child's last follow-up.", StringComparison.Ordinal)
                && (await fixture.Call("follow_up", ("child", "c2"), ("text", "Rebase on main."))).FollowUp?.Remaining == 1, "the second is the last, and the other child's count is its own");
            file = fixture.File;
            Check(file.FollowUps.Select(followUp => followUp.Text).SequenceEqual(["Also add tests.", words, "Rebase on main."]) && file.FollowUps.Select(followUp => followUp.ChildId).SequenceEqual(["c1", "c1", "c2"])
                && file.FollowUps.All(followUp => followUp is { Lane: DeliveryLane.Held, Receipt: null }) && file.FollowUps.Select(followUp => followUp.Id).Distinct().Count() == 3 && file.Children.Select(child => child.FollowUpCount).SequenceEqual([2, 1]), "each is kept exactly as given");
            // The third to c1 is refused with follow_up_limit and changes nothing.
            var bytes = fixture.Bytes(); var third = await fixture.Call("follow_up", ("child", "c1"), ("text", "One more thing."));
            Check(third == DelegationResponse.Refusal(DelegationReasonCode.FollowUpLimit) && fixture.Bytes().SequenceEqual(bytes) && fixture.File == file && DelegationMcp.Result(third)["structuredContent"]!.ToJsonString() == """{"refused":"follow_up_limit"}""", "the third is refused");
            // A relaunch from the same profile keeps both follow-ups and the limit.
            var relaunched = fixture.Relaunch();
            Check(relaunched.File == file && await relaunched.Handle(Request("follow_up", ("child", "c1"), ("text", "Again.")), Parent) == DelegationResponse.Refusal(DelegationReasonCode.FollowUpLimit) && fixture.Bytes().SequenceEqual(bytes), "a relaunch keeps the follow-ups and the limit");
        }
        finally { place.Remove(); }

        // Follow-ups at once never pass the limit.
        place = new DelegationPlace();
        try
        {
            var fixture = Tools(place, [Record("c1", ChildState.Running)]);
            var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => fixture.Call("follow_up", ("child", "c1"), ("text", "Follow-up " + index))));
            Check(answers.Count(answer => answer.FollowUp is not null) == 2 && answers.Count(answer => answer.Refused == DelegationReasonCode.FollowUpLimit) == 4 && fixture.File.FollowUps.Count == 2 && fixture.File.Children[0].FollowUpCount == 2
                && answers.Where(answer => answer.FollowUp is not null).Select(answer => answer.FollowUp!.Remaining).Order().SequenceEqual([0, 1]), "six at once record exactly two");
        }
        finally { place.Remove(); }

        // follow_up is refused for a closed child or a closed parent, and changes nothing.
        place = new DelegationPlace();
        try
        {
            var fixture = Tools(place, [Record("closed", ChildState.Closed), Record("discarded", ChildState.Discarded), Record("failed", ChildState.Failed), Record("open", ChildState.Reported), Record("full", ChildState.Running, followUps: 2), Record("theirs", ChildState.Running, "other")]);
            var bytes = fixture.Bytes(); var file = fixture.File;
            // A child that is not open: its pane closed, a human discarded it, or its start failed.
            foreach (var id in new[] { "closed", "discarded", "failed" })
                Check(await fixture.Call("follow_up", ("child", id), ("text", "Go on.")) is { Refused: DelegationReasonCode.ChildClosed } answer && DelegationMcp.Result(answer)["structuredContent"]!.ToJsonString() == """{"refused":"child_closed"}""", id + " is not open");
            Check(await fixture.Call("follow_up", ("child", "full"), ("text", "Go on.")) == DelegationResponse.Refusal(DelegationReasonCode.FollowUpLimit), "a child with its two follow-ups takes no more");
            // A child that is not the caller's, an empty text or one over 48 KiB is an error.
            Check(await fixture.Call("follow_up", ("child", "theirs"), ("text", "Go on.")) == DelegationResponse.Failure(DelegationCoordinator.NotYourChildMessage("theirs")) && await fixture.CallFrom("other", "follow_up", ("child", "open"), ("text", "Go on.")) == DelegationResponse.Failure(DelegationCoordinator.NotYourChildMessage("open"))
                && await fixture.Call("follow_up", ("child", "no-such-child"), ("text", "Go on.")) == DelegationResponse.Failure(DelegationCoordinator.NotYourChildMessage("no-such-child")), "a child that is not the caller's is an error");
            Check((await fixture.Call("follow_up", ("child", "open"), ("text", " \n"))).Error == "follow_up needs a text: the instruction for the child."
                && (await fixture.Call("follow_up", ("child", "open"), ("text", new string('x', DelegationCoordinator.MaximumFollowUpBytes + 1)))).Error == "A follow-up's text may be at most 48 KiB.", "an empty text or one over 48 KiB is an error");
            // Measured as it is stored: under 48 KiB in UTF-8, over it with its escapes.
            Check((await fixture.Call("follow_up", ("child", "open"), ("text", new string('"', DelegationCoordinator.MaximumFollowUpBytes / 2 + 1)))).Error == "A follow-up's text may be at most 48 KiB."
                && (await fixture.Call("follow_up", ("child", "open"), ("text", "Go on.\0Then stop."))).Error == "A follow-up's text may not contain a NUL character.", "the text is measured as it is stored, and holds no NUL");
            // The parent's pane closed: its children keep their cards, and nothing more is sent to them.
            fixture.Host.Close("parent");
            foreach (var id in new[] { "open", "full", "closed" })
                Check(await fixture.Call("follow_up", ("child", id), ("text", "Go on.")) is { Refused: DelegationReasonCode.ParentClosed } answer && TextOf(DelegationMcp.Result(answer)) == "Refused: parent_closed. Nothing was changed.", "a closed parent sends nothing more to " + id);
            Check(fixture.Bytes().SequenceEqual(bytes) && fixture.File == file && !fixture.Host.Calls.Any(call => call.StartsWith("deliver", StringComparison.Ordinal) || call.StartsWith("startRun", StringComparison.Ordinal) || call.StartsWith("createPane", StringComparison.Ordinal)), "nothing changed, and no pane was handed anything");
        }
        finally { place.Remove(); }
    }

    /// <summary>A repository on main, and a coordinator over a child c1 made the app's way that committed a.txt and reported it, with its parent's open pane idle in the repository.</summary>
    private static async Task<(Fixture Fixture, string Start, string Head)> Card(DelegationPlace place, bool recordsCheckout = true, int replaceAttempts = 40)
    {
        var start = await place.Repository();
        var (child, head) = await place.ReportedChild("c1", ["a.txt"], recordsCheckout ? place.Repo : null);
        var fixture = Tools(place, [child], replaceAttempts);
        fixture.SetParent(activity: Finished);
        return (fixture, start, head);
    }

    private static List<IReadOnlyList<DelegationCardAction>> Actions(Fixture fixture) => DelegationSidebar.Rows(fixture.File).Select(row => row.CardActions).ToList();

    internal static async Task CardsMergeUndoAndDiscard()
    {
        // Merges and undoes from the card, with the actions that apply.
        var place = new DelegationPlace();
        try
        {
            var (fixture, start, head) = await Card(place); var coordinator = fixture.Coordinator;
            Check(Actions(fixture).Single().SequenceEqual([DelegationCardAction.Merge, DelegationCardAction.Discard]), "a reported child's card offers merge and discard");
            var outcome = await coordinator.MergeFromCard("c1"); var record = (outcome as ChildMergeOutcome.Merged)?.Record ?? throw new InvalidOperationException("not merged: " + outcome);
            Check(record == new MergeRecord("c1", MergeKind.CardFastForward, "main", start, head, head) && await place.Git("rev-parse", "HEAD") == head && fixture.File.Children.Single().State == ChildState.Merged && fixture.File.Merges.SequenceEqual([record])
                && fixture.Store.Load().Merges.SequenceEqual([record]) && DelegationSidebar.Rows(fixture.File).Single() is { CanUndo: true } merged && merged.CardActions.SequenceEqual([DelegationCardAction.Undo, DelegationCardAction.Discard]), "the card's merge is on record, and the card offers undo");
            // A running parent refuses the undo with parent_busy, and nothing changes.
            fixture.SetParent(runId: "run-2", activity: Running);
            Check(await coordinator.UndoMergeFromCard("c1") == new ChildUndoOutcome.Refused(DelegationReasonCode.ParentBusy) && await place.Git("rev-parse", "HEAD") == head && fixture.File.Merges.SequenceEqual([record]) && fixture.File.Children.Single().State == ChildState.Merged, "a running parent refuses the undo");
            // Idle again: the branch goes back, the record leaves the file and the child is reported again.
            fixture.SetParent(runId: "run-2", activity: Finished);
            Check(await coordinator.UndoMergeFromCard("c1") == new ChildUndoOutcome.Undone(record, false) && await place.Git("rev-parse", "HEAD") == start && fixture.File.Merges.Count == 0 && fixture.File.Children.Single().State == ChildState.Reported
                && fixture.Store.Load().Merges.Count == 0 && Actions(fixture).Single().SequenceEqual([DelegationCardAction.Merge, DelegationCardAction.Discard]) && await coordinator.UndoMergeFromCard("c1") is ChildUndoOutcome.Failed, "the undo takes the record out, and there is no second undo");

            // A refusal carries its reason and changes nothing.
            var before = fixture.File;
            System.IO.File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "changed\n");
            Check(await coordinator.MergeFromCard("c1") == new ChildMergeOutcome.Refused(DelegationReasonCode.TrackedChanges), "tracked changes in the parent's folder");
            await place.Git("checkout", "--", "file-0.txt"); await place.Git("checkout", "-q", "-b", "elsewhere");
            Check(await coordinator.MergeFromCard("c1") == new ChildMergeOutcome.Refused(DelegationReasonCode.BranchNotCheckedOut), "another branch checked out in the parent's folder");
            await place.Git("checkout", "-q", "main");
            Check(await place.Git("rev-parse", "HEAD") == start && fixture.File == before && await coordinator.MergeFromCard("nobody") is ChildMergeOutcome.Failed, "nothing changed, and nobody is not merged");

            // A card's failure reads in the app's language: each language's own words.
            var language = Locale.LanguagePreference; var said = new HashSet<string>();
            try
            {
                foreach (var chosen in Locale.Languages)
                {
                    Locale.LanguagePreference = chosen;
                    Check(await coordinator.UndoMergeFromCard("c1") == new ChildUndoOutcome.Failed(Locale.Get("delegation.card.error.noMergeToUndo")) && await coordinator.DiscardFromCard("nobody", []) == new ChildCardDiscardOutcome.Failed(Locale.Get("delegation.card.error.cannotDiscard"))
                        && await coordinator.MergeFromCard("nobody") is ChildMergeOutcome.Failed { Message: var message } && message == Locale.Get("delegation.card.error.noChild") && said.Add(message), "the card's words in " + chosen);
                }
            }
            finally { Locale.LanguagePreference = language; }
            Check(said.Count == 4, "four languages, four texts");
            Check(DelegationCoordinator.UnsavedReason(new DelegationFileException(DelegationFileError.Full)) == Locale.Get("delegation.card.error.fileFull") && DelegationCoordinator.UnsavedReason(new DelegationFileException(DelegationFileError.Unreadable)) == Locale.Get("delegation.card.error.fileUnreadable")
                && DelegationCoordinator.UnsavedReasonForAgent(new DelegationFileException(DelegationFileError.Full)) == "the delegation file is at its 4 MiB cap" && DelegationCoordinator.UnsavedReasonForAgent(new DelegationFileException(DelegationFileError.Unreadable)) == "the delegation file on disk cannot be read"
                && DelegationCoordinator.UnsavedReason(new IOException("Access to the path is denied.\r\n")) == "Access to the path is denied", "an unsaved file's reason reads as words for the card and the agent, with no closing stop");

            // The discard confirmation gets the worktrees nested in the child's, and asks again when one was nested since.
            var child = fixture.File.Children.Single();
            Check(await coordinator.NestedWorktrees("c1") is { Count: 0 } confirmed && await coordinator.NestedWorktrees("nobody") is null, "no worktree is nested yet");
            var nested = Path.Combine(child.WorktreePath, "inner");
            await place.Git("worktree", "add", "-q", "-b", "inner", nested);
            Check(await coordinator.NestedWorktrees("c1") is { Count: 1 } named && DelegationPaths.Same(named[0], DelegationPaths.Canonical(nested)), "the nested worktree is named");
            Check(await coordinator.DiscardFromCard("c1", []) is ChildCardDiscardOutcome.NestedChanged { Nested: { Count: 1 } all } && DelegationPaths.Same(all[0], DelegationPaths.Canonical(nested)) && Directory.Exists(child.WorktreePath) && Directory.Exists(nested)
                && (await place.Git("rev-parse", "--verify", "-q", "refs/heads/mighty/c1")).Length == 40 && fixture.File.Children.Single().State == ChildState.Reported, "nested after the confirmation named none: nothing is removed, and the answer names it");
            // Confirmed with it named, however its path is written: the child, the nested worktree and the child's branch go.
            Check(await coordinator.DiscardFromCard("c1", [DelegationPaths.Canonical(nested).ToUpperInvariant().Replace('\\', '/')]) is ChildCardDiscardOutcome.Discarded && !Directory.Exists(child.WorktreePath) && (await place.MightyBranches()).Length == 0
                && fixture.File.Children.Single().State == ChildState.Discarded && DelegationSidebar.Rows(fixture.File).Count == 0 && await place.Git("rev-parse", "refs/heads/inner") == start, "confirmed with it named, they go, and the nested worktree's branch stays");
        }
        finally { place.Remove(); }

        // A merge or undo whose save fails stands, stays on record in memory, and the next save writes it.
        place = new DelegationPlace();
        try
        {
            var (fixture, start, head) = await Card(place, replaceAttempts: 2); var coordinator = fixture.Coordinator;
            var unblock = BlockSaves(fixture.Store);
            var outcome = await coordinator.MergeFromCard("c1");
            var unsaved = outcome as ChildMergeOutcome.MergedUnsaved ?? throw new InvalidOperationException("not merged unsaved: " + outcome);
            // The reason sits inside the card's sentence, so it brings no closing stop of its own.
            Check(unsaved.Reason.Length > 0 && !unsaved.Reason.EndsWith('.') && await place.Git("rev-parse", "HEAD") == head && fixture.File.Merges.SequenceEqual([unsaved.Record]) && DelegationSidebar.Rows(fixture.File).Single().State == ChildState.Merged, "the merge stands and is on record in memory");
            unblock();
            Check(fixture.Store.Load().Merges.Count == 0, "the disk does not have it yet");
            await coordinator.Save();
            Check(fixture.Store.Load().Merges.SequenceEqual([unsaved.Record]), "the next save writes the merge");
            // An undo whose save fails is made and taken out in memory the same way.
            unblock = BlockSaves(fixture.Store);
            Check(await coordinator.UndoMergeFromCard("c1") is ChildUndoOutcome.UndoneUnsaved { RestoredBranch: false, Reason.Length: > 0 } undone && undone.Record == unsaved.Record && fixture.File.Merges.Count == 0 && await place.Git("rev-parse", "HEAD") == start, "the undo stands too");
            unblock();
            Check(fixture.Store.Load().Merges.SequenceEqual([unsaved.Record]), "the disk still has the merge until the next save");
        }
        finally { place.Remove(); }

        // A discard that cannot list the nested worktrees leaves the child's run alone.
        place = new DelegationPlace();
        try
        {
            var (fixture, _, _) = await Card(place); var coordinator = fixture.Coordinator; var child = fixture.File.Children.Single();
            fixture.Host.Set(new("c1", "acceptEdits", child.WorktreePath, ParentSessionId: "parent", RunId: "c1-run", Activity: Running));
            // The parent's folder is gone, so the worktrees nested in the child's cannot be listed.
            var moved = Path.Combine(place.Base, "repo-moved");
            Directory.Move(place.Repo, moved);
            Check(await coordinator.NestedWorktrees("c1") is null && await coordinator.DiscardFromCard("c1", null) == new ChildCardDiscardOutcome.Failed(Locale.Get("delegation.card.error.nestedUnlisted")), "a discard that cannot name everything it would remove fails");
            // Its run is never stopped, and nothing is removed.
            Check(fixture.Host.Stops.Count == 0 && fixture.Host.Pane("c1")?.Activity == Running && Directory.Exists(child.WorktreePath) && fixture.File.Children.Single().State == ChildState.Reported, "its run is never stopped, and nothing is removed");
            // Listed again with the folder back, the discard goes on: its run is stopped, then it goes.
            Directory.Move(moved, place.Repo);
            Same(await coordinator.DiscardFromCard("c1", []), new ChildCardDiscardOutcome.Discarded(), "with the folder back, the discard goes on");
            Check(fixture.Host.Stops.Distinct().SequenceEqual(["c1 with its folder"]) && fixture.Host.Pane("c1")?.Activity == Idle && !Directory.Exists(child.WorktreePath)
                && fixture.File.Children.Single().State == ChildState.Discarded, "with the folder back, the discard goes on");
        }
        finally { place.Remove(); }

        // Delivery starts no run in a pane whose checkout a card is changing; the notice goes on once the card is done.
        place = new DelegationPlace();
        try
        {
            // Without a recorded checkout the card asks the host for the parent's folder, and the check holds that answer back.
            var (fixture, _, head) = await Card(place, recordsCheckout: false); var coordinator = fixture.Coordinator;
            var other = Record("c2", ChildState.Running, runId: "r-c2") with { AwaitedRunId = "r-c2" };
            fixture.Store.Save(fixture.File with { Children = fixture.File.Children.Add(other) });
            coordinator = fixture.Relaunch(); fixture = fixture with { Coordinator = coordinator };
            var gate = new TaskCompletionSource(); var asked = 0;
            fixture.Host.StateGate = _ => Interlocked.Increment(ref asked) == 1 ? gate.Task : Task.CompletedTask;
            var merging = coordinator.MergeFromCard("c1");
            Check(await Eventually(() => Volatile.Read(ref asked) >= 1), "the card's merge is under way");
            var notice = await coordinator.ChildRunEnded("c2", "r-c2", ChildRunEnd.Finished) ?? throw new InvalidOperationException("no notice");
            Check(fixture.Host.AllHanded.Count == 0 && fixture.File.Notices.Single().Lane == DeliveryLane.Pending && !merging.IsCompleted, "no run starts in the parent's pane while the card changes its checkout");
            gate.SetResult();
            Check(await merging is ChildMergeOutcome.Merged && await place.Git("rev-parse", "HEAD") == head, "the card's merge finishes");
            Check(fixture.Host.AllHanded.Single() is { SessionId: "parent", Route: DeliveryRoute.Queue } handed && handed.Input == DelegationCoordinator.Text(notice) && fixture.File.Notices.Single().Lane == DeliveryLane.Delivered, "the notice starts the parent's next run once the card is done");
        }
        finally { place.Remove(); }
    }

    internal static async Task AChildWhoseRunWouldNotEndIsNeverCleanedUp()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (child, merge) = await DelegationGitVerification.MergedChild(place);
            var store = place.Store; store.Save(DelegationFile.Of([child], merges: [merge]));
            var host = new DelegationTestHost(store); host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Finished));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => true);
            // Its pane is gone, which reads as idle, but the host could not end its run: the child is busy, and only closes.
            Check(await coordinator.CloseChild("c1", stillRunning: true) == new ChildCleanupOutcome.Kept(ChildCleanupHold.ChildBusy) && Directory.Exists(child.WorktreePath)
                && (await place.MightyBranches()).Contains(child.Branch, StringComparison.Ordinal) && coordinator.File.Children.Single().State == ChildState.Closed, "a child whose run would not end keeps its worktree and branch");
        }
        finally { place.Remove(); }
    }

    internal static async Task ClosingAndDiscardingThroughTheCoordinator()
    {
        // Closing a merged child stores its copies, then removes its worktree and branch.
        var place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var (child, merge) = await DelegationGitVerification.MergedChild(place);
            // Ignored build output beside TASK.md and REPORT.md goes with the worktree.
            DelegationGitVerification.Ignore(place, "build/"); Directory.CreateDirectory(Path.Combine(child.WorktreePath, "build")); System.IO.File.WriteAllText(Path.Combine(child.WorktreePath, "build", "out.o"), "o\n");
            var store = place.Store; store.Save(DelegationFile.Of([child], merges: [merge]));
            var host = new DelegationTestHost(store); host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Finished)); host.Set(new("c1", "acceptEdits", child.WorktreePath, ParentSessionId: "parent", RunId: "run-c1", Activity: Finished));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => true);
            Check(coordinator.WorkspaceRemovalRefusal(new HashSet<string> { "parent" }, place.Repo) == DelegationReasonCode.WorkspaceHasChildren, "a workspace with an open child cannot be removed");
            Check(await coordinator.CloseChild("c1") is ChildCleanupOutcome.Cleaned, "the merged child is cleaned up on close");
            // Plain removal from the parent's side: the worktree with its ignored files, and the branch, are gone; the merge stays on main.
            Check(!Directory.Exists(child.WorktreePath) && await place.OnlyTheRepositoryIsListed() && (await place.MightyBranches()).Length == 0 && await place.Git("rev-parse", "refs/heads/main") == merge.MergedCommit
                && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0 && host.Stops.Count == 0, "the worktree and branch are gone by plain removal");
            // The copies hold files that are gone now, so they were read and stored before the removal.
            var saved = store.Load(); var closed = saved.Children.Single();
            Check(coordinator.File == saved && closed.State == ChildState.Closed && closed.ClosedHead == merge.ChildHead && saved.Copy("c1", DelegationCopyKind.Report) is { Revision: 1, Truncated: false, Text: "# Report of c1\nDone.\n" }
                && saved.Copy("c1", DelegationCopyKind.Task)?.Text == "Do the work.\n" && saved.Merges.SequenceEqual([merge]), "the copies and the branch head were stored first");
            Check(coordinator.WorkspaceRemovalRefusal(new HashSet<string> { "parent" }, place.Repo) is null, "with the child cleaned up, the workspace may go");
            // The merge record survives the close: the card still offers undo, which makes the branch again at its recorded head and leaves the child closed.
            Check(DelegationSidebar.Rows(saved).Single().CardActions.SequenceEqual([DelegationCardAction.Undo, DelegationCardAction.Discard]) && await coordinator.UndoMergeFromCard("c1") == new ChildUndoOutcome.Undone(merge, true)
                && await place.Git("rev-parse", "refs/heads/mighty/c1") == merge.ChildHead && await place.Git("rev-parse", "HEAD") == start && coordinator.File.Children.Single().State == ChildState.Closed && coordinator.File.Merges.Count == 0, "the undo after the close makes the branch again");
            Check(await coordinator.DiscardFromCard("c1", []) is ChildCardDiscardOutcome.Discarded && (await place.MightyBranches()).Length == 0 && coordinator.File.Children.Single().State == ChildState.Discarded, "the closed child's branch goes by discard");
        }
        finally { place.Remove(); }

        // Closing keeps the worktree and branch unless the child is merged, clean, and its copies are saved.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            // c1 merged but left an untracked file; c2 runs unmerged work; c3 merged clean.
            var (c1, m1) = await DelegationGitVerification.MergedChild(place, "c1");
            System.IO.File.WriteAllText(Path.Combine(c1.WorktreePath, "notes.txt"), "note\n");
            var c2 = await place.Child("c2", place.Repo);
            await place.Commit(c2.WorktreePath, "c2.txt", "c2\n"); System.IO.File.WriteAllText(Path.Combine(c2.WorktreePath, "c2.txt"), "unsaved\n");
            var (c3, m3) = await DelegationGitVerification.MergedChild(place, "c3");
            var store = new DelegationFileStore(place.Profile, 2); store.Save(DelegationFile.Of([c1, c2, c3], merges: [m1, m3]));
            var host = new DelegationTestHost(store); host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Finished)); host.Set(new("c2", "acceptEdits", c2.WorktreePath, ParentSessionId: "parent", RunId: "run-c2", Activity: Running));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => true);
            // Not merged: only the pane closes, and the card stays.
            var before = await place.Disk();
            Check(await coordinator.CloseChild("c2") == new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged) && await place.Disk() == before, "an unmerged child's close removes nothing");
            var saved = store.Load();
            Check(saved.Children.Select(child => child.State).SequenceEqual([ChildState.Merged, ChildState.Closed, ChildState.Merged]) && saved.Children[1].ClosedHead is null && saved.Copy("c2", DelegationCopyKind.Report) is null
                && DelegationSidebar.Rows(saved)[1].CardActions.SequenceEqual([DelegationCardAction.Discard]), "the closed child keeps its card, with only discard");
            Check((await coordinator.Handle(Request("follow_up", ("child", "c2"), ("text", "Go on.")), Parent)).Refused == DelegationReasonCode.ChildClosed, "a closed child takes no follow-up");
            // A closed child's unmerged work stays until a human discards it from its card.
            Check(await coordinator.DiscardChild("c2") is ChildDiscardOutcome.Discarded && host.Stops.SequenceEqual(["c2 with its folder"]) && !Directory.Exists(c2.WorktreePath) && await place.MightyBranches() == "refs/heads/mighty/c1\nrefs/heads/mighty/c3", "a human's discard removes the unmerged work");
            // Merged, but a check fails: the copies are stored all the same, and nothing on disk changes.
            before = await place.Disk();
            Check(await coordinator.CloseChild("c1") == new ChildCleanupOutcome.Kept(ChildCleanupHold.UntrackedFiles) && await place.Disk() == before, "a merged child with an untracked file is kept");
            saved = store.Load();
            Check(saved.Children[0].State == ChildState.Closed && saved.Children[0].ClosedHead == m1.ChildHead && saved.Copy("c1", DelegationCopyKind.Report)?.Text == "# Report of c1\nDone.\n" && saved.Copy("c1", DelegationCopyKind.Task)?.Text == "Do the work.\n" && saved.Merges.SequenceEqual([m1, m3]), "its copies are stored all the same");
            // A closed child closes no further.
            Check(await coordinator.CloseChild("c1") == new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged) && await place.Disk() == before && await coordinator.CloseChild("nobody") is ChildCleanupOutcome.Failed, "a closed child closes no further");
            // Merged and clean, but its copies cannot be saved: nothing is removed.
            BlockSaves(store);
            before = await place.Disk();
            Check(await coordinator.CloseChild("c3") is ChildCleanupOutcome.Failed { Message: "The delegation file could not be saved, so the child's worktree and branch were kept." } && await place.Disk() == before && Directory.Exists(c3.WorktreePath)
                && await place.Git("rev-parse", "refs/heads/mighty/c3") == m3.ChildHead && coordinator.File.Children[2].State == ChildState.Closed, "without its copies saved, nothing is removed");
            Check(await coordinator.DiscardChild("c3") is ChildDiscardOutcome.Failed { Message: var unsaved } && unsaved == Locale.Get("delegation.card.error.notSaved") && Directory.Exists(c3.WorktreePath), "a discard whose record cannot be saved removes nothing");
        }
        finally { place.Remove(); }

        // Discard stops the child, keeps its report, and force-removes its unmerged work.
        place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var child = await place.Child("c1", place.Repo);
            // Unmerged work of every kind: a commit only on the child's branch, a tracked change, an untracked file, and a locked nested worktree with work of its own.
            await place.Commit(child.WorktreePath, "c1.txt", "c1\n");
            System.IO.File.WriteAllText(Path.Combine(child.WorktreePath, "c1.txt"), "more\n"); System.IO.File.WriteAllText(Path.Combine(child.WorktreePath, "notes.txt"), "note\n");
            var nested = Path.Combine(child.WorktreePath, "nested");
            await place.Git("worktree", "add", "-q", "-b", "side", nested, "main"); await place.Git("worktree", "lock", nested);
            System.IO.File.WriteAllText(Path.Combine(nested, "side.txt"), "side work\n"); System.IO.File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\nHalf done.\n");
            var store = place.Store; store.Save(DelegationFile.Of([child]));
            var host = new DelegationTestHost(store); host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Finished)); host.Set(new("c1", "acceptEdits", child.WorktreePath, ParentSessionId: "parent", RunId: "run-c1", Activity: Running));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => true);
            Check(await coordinator.NestedWorktrees("c1") is { Count: 1 } && await coordinator.DiscardChild("c1") is ChildDiscardOutcome.Discarded, "the child is discarded");
            // The child's run was stopped first, while its worktree was still there.
            Check(host.Stops.SequenceEqual(["c1 with its folder"]) && host.Pane("c1")?.Activity == Idle, "its run was stopped first");
            // Its worktree, the nested one and its unmerged branch are gone, by force; the parent's branch and checkout are untouched, and so is the nested worktree's own branch.
            Check(!Directory.Exists(child.WorktreePath) && await place.OnlyTheRepositoryIsListed() && (await place.MightyBranches()).Length == 0 && await place.Git("rev-parse", "refs/heads/main") == start && await place.Git("rev-parse", "refs/heads/side") == start
                && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "its unmerged work is gone, and nothing else");
            // A copy of its report is kept, and the child is discarded, in the file on disk.
            var saved = store.Load();
            Check(coordinator.File == saved && saved.Children.Single().State == ChildState.Discarded && saved.Copy("c1", DelegationCopyKind.Report)?.Text == "# Report\nHalf done.\n", "a copy of its report is kept");
            // Discarded is final: another discard is refused and stops nothing.
            Check(await coordinator.DiscardChild("c1") is ChildDiscardOutcome.Failed && host.Stops.Count == 1 && store.Load() == saved && (await coordinator.Handle(Request("child_status", ("child", "c1")), Parent)).Status is { State: ChildState.Discarded, Report: null }, "discarded is final");
        }
        finally { place.Remove(); }

        // Windows: a discard that another program's open file stops is told honestly, the child keeps its state and card, and a second discard finishes.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (child, head) = await place.ReportedChild("c1", parentCheckout: place.Repo);
            var store = place.Store; store.Save(DelegationFile.Of([child]));
            var host = new DelegationTestHost(store); host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Finished));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => true);
            using (new FileStream(Path.Combine(child.WorktreePath, "c1.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(await coordinator.DiscardFromCard("c1", []) is ChildCardDiscardOutcome.Failed { Message.Length: > 0 }, "the card hears that the discard failed");
                Check(coordinator.File.Children.Single().State == ChildState.Reported && store.Load().Children.Single().State == ChildState.Reported && Directory.Exists(child.WorktreePath) && await place.Git("rev-parse", "refs/heads/mighty/c1") == head
                    && DelegationSidebar.Rows(coordinator.File).Single().CardActions.Contains(DelegationCardAction.Discard) && coordinator.WorkspaceRemovalRefusal(new HashSet<string>(), place.Repo) == DelegationReasonCode.WorkspaceHasChildren, "the child is not called discarded: it keeps its state, its branch and its card");
            }
            Check(await coordinator.DiscardFromCard("c1", []) is ChildCardDiscardOutcome.Discarded && !Directory.Exists(child.WorktreePath) && (await place.MightyBranches()).Length == 0 && store.Load().Children.Single().State == ChildState.Discarded, "tried again once the file is let go, the discard finishes");
        }
        finally { place.Remove(); }
    }

    internal static async Task StoragePressureAndTheSwitchTurnedOff()
    {
        // The delegation file near its 4 MiB cap: a relaunch reads it back, a report still gets its copy, and a new child is refused with store_full.
        var fixture = await Make();
        try
        {
            var file = DelegationFile.Of([Record("mine", ChildState.Running, runId: "r-mine") with { WorktreePath = Path.Combine(fixture.Place.Root, "mine"), AwaitedRunId = "r-mine" }]);
            // Open children of other parents, three to a pane, each with a full report copy, until one more would leave less than a child's room under the cap.
            for (var index = 0; ; index++)
            {
                var child = Record($"open{index:000}", ChildState.Running, $"pane{index / 3:000}"); var copy = Copy(child.Id, DelegationCopyKind.Report, new string('r', DelegationFileStore.MaximumCopyBytes));
                if (DelegationFileStore.Encode(file).Length + DelegationFile.ElementBytes(child) + DelegationFile.ElementBytes(copy) + DelegationFile.NewChildRoom() > DelegationFileStore.MaximumFileBytes) break;
                file = (file with { Children = file.Children.Add(child) }).SettingCopy(copy);
            }
            // One more with a smaller copy, so that just over one child's room is left whatever the folders are called.
            file = (file with { Children = file.Children.Add(Record("pad", ChildState.Running, "pane-pad")) }).SettingCopy(Copy("pad", DelegationCopyKind.Report, "p"));
            var spare = DelegationFileStore.MaximumFileBytes - DelegationFileStore.Encode(file).Length - (DelegationFile.NewChildRoom() + 2_000);
            if (spare > 0) file = file.SettingCopy(Copy("pad", DelegationCopyKind.Report, new string('p', 1 + spare)));
            var room = DelegationFileStore.MaximumFileBytes - DelegationFileStore.Encode(file).Length;
            Check(file.Children.Count > 50 && room >= DelegationFile.NewChildRoom() && room <= DelegationFile.NewChildRoom() + 2_100, "the file is near its cap: " + room);
            fixture.Store.Save(file);
            var coordinator = fixture.Relaunch(); fixture = fixture with { Coordinator = coordinator };
            Check(coordinator.File == file && fixture.Bytes().Length <= DelegationFileStore.MaximumFileBytes, "a relaunch reads a nearly full file back");
            // A report longer than a copy: the run's end still records its revision, and the marked copy fits in the room kept for it.
            await fixture.Place.Maker.Create("mine", fixture.Place.Repo, "Task\n");
            var report = string.Concat(Enumerable.Repeat("보고서의 한 줄 \"quoted\" \\ and / more\n", 4_000));
            WriteReport(fixture.Child("mine"), report);
            Check(await coordinator.ChildRunEnded("mine", "r-mine", ChildRunEnd.Finished) is { Kind: NoticeKind.Reported, ReportRevision: 1 } && coordinator.File.Copy("mine", DelegationCopyKind.Report) is { Truncated: true, Revision: 1 } copyOfMine
                && copyOfMine.OriginalBytes == Encoding.UTF8.GetByteCount(report) && fixture.Store.Load() == coordinator.File && fixture.Bytes().Length <= DelegationFileStore.MaximumFileBytes, "a long report is recorded and saved near the cap");
            Check((await fixture.Call("child_status", ("child", "mine"))).Status is { ReportTruncated: true, Report: { } body } && body.EndsWith(DelegationCopy.TruncationMarker, StringComparison.Ordinal) && report.StartsWith(body[..^DelegationCopy.TruncationMarker.Length], StringComparison.Ordinal), "the parent reads the marked copy");
            // No room is left for one more child and its report: store_full, and nothing is made.
            var bytes = fixture.Bytes(); var before = coordinator.File;
            Check(await fixture.Delegate("One more.", "plan") == DelegationResponse.Refusal(DelegationReasonCode.StoreFull) && fixture.Bytes().SequenceEqual(bytes) && coordinator.File == before && fixture.Host.CreatedPanes.Count == 0, "a new child is refused with store_full");
            // Nothing of what the file holds was dropped to make room: every child is open.
            Check(coordinator.File.Children.Count == file.Children.Count && coordinator.File.Copies.Count == file.Copies.Count + 1, "open children are never pruned");
        }
        finally { fixture.Remove(); }

        // Turning the switch off leaves an existing child working, and only detaches the server.
        var on = true;
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            var store = place.Store;
            // A launch with no delegation file starts empty and writes nothing; one that cannot be read stops the launch and is left alone.
            Check(new DelegationCoordinator(store, new DelegationTestHost(), place.Maker, () => true).File == DelegationFile.Empty && !System.IO.File.Exists(store.FilePath) && !Directory.Exists(place.Profile), "a launch with no delegation file writes nothing");
            Directory.CreateDirectory(place.Profile); System.IO.File.WriteAllText(store.FilePath, "{");
            try { _ = new DelegationCoordinator(store, new DelegationTestHost(), place.Maker, () => true); throw new InvalidOperationException("a damaged file was loaded"); } catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Unreadable) { }
            Check(System.IO.File.ReadAllText(store.FilePath) == "{", "a file that cannot be read is left as it is");
            store.Save(DelegationFile.Empty);
            var host = new DelegationTestHost(store) { FirstRun = id => "run-of-" + id, MakesPanes = true }; host.Set(new("parent", "auto", place.Repo, RunId: "run-1", Activity: Running));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => on);
            var id = (await coordinator.Handle(Request("delegate", ("task", "Do it."), ("mode", "plan")), Parent)).Child!.Id;
            Check(await Eventually(async () => coordinator.File.Children.Single().State == ChildState.Running && !await coordinator.IsStarting(id)), "the child runs");
            on = false;
            // The server is detached: a call reaches nothing and changes nothing.
            var bytes = System.IO.File.ReadAllBytes(store.FilePath);
            Check(await coordinator.Handle(Request("list_children"), Parent) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage) && await coordinator.Handle(Request("child_status", ("child", id)), Parent) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage)
                && System.IO.File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "with the switch off the tools reach nothing");
            // The child still runs in its worktree, and its run's end, through the app's ordered events, still records its report for the parent; the sidebar and the card go on too.
            var child = coordinator.File.Children.Single();
            WriteReport(child, "Done with the switch off.\n");
            var pump = new DelegationRunEventPump(coordinator);
            pump.Send(new DelegationRunEvent.Ended(id, "run-of-" + id, ChildRunEnd.Finished));
            await pump.Finish();
            Check(coordinator.File.Children.Single() is { State: ChildState.Reported, ReportRevision: 1 } && coordinator.File.Notices.Single() is { Kind: NoticeKind.Reported, Lane: DeliveryLane.Delivered } && DelegationSidebar.Rows(coordinator.File).Single().CardActions.SequenceEqual([DelegationCardAction.Merge, DelegationCardAction.Discard])
                && await coordinator.MergeFromCard(id) is ChildMergeOutcome.Merged && await coordinator.CloseChild(id) is ChildCleanupOutcome.Cleaned, "existing children, their cards and their cleanup go on with the switch off");
        }
        finally { place.Remove(); }
    }
}
