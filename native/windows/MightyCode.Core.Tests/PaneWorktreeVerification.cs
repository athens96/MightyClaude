using MightyCode.Core;
using static DelegationSupport;

/// <summary>
/// Per-pane worktrees (PaneWorktree.cs), the Mac's PaneWorktreeTests and PaneWorktreeToolsTests on Windows: the decision, the PORT,
/// the note, saved records, the copy rules, the list, the pull request URL, and real git for merge, commit-and-merge, undo,
/// status and discard (docs/devtool-features.md §12 "옮길 규칙").
/// </summary>
internal static class PaneWorktreeVerification
{
    private static RunSession Agent(string id, string workspace, string? parent = null, string? folder = null) =>
        new() { Id = id, WorkspaceId = workspace, Kind = "claude", Provider = "claude", ParentSessionId = parent, WorkingFolder = folder };

    public static Task PolicyPortsNoteAndRecords()
    {
        var workspace = new Workspace { Id = "w", Path = Path.Combine(Path.GetTempPath(), "w") };
        Same(PaneWorktreePolicy.Decide(workspace, []), PaneWorktreeDecision.SameFolder, "the first pane works in the folder");
        Same(PaneWorktreePolicy.Decide(workspace, [Agent("a", "w")]), PaneWorktreeDecision.Ask, "a second pane asks");
        Same(PaneWorktreePolicy.Decide(workspace with { PaneWorktreeMode = "further" }, [Agent("a", "w")]), PaneWorktreeDecision.Worktree, "further opens a worktree");
        Same(PaneWorktreePolicy.Decide(workspace with { PaneWorktreeMode = "every" }, []), PaneWorktreeDecision.Worktree, "every opens the first in a worktree too");
        Same(PaneWorktreePolicy.Decide(workspace with { PaneWorktreeMode = "odd" }, [Agent("a", "w")]), PaneWorktreeDecision.Ask, "an unknown mode asks");
        // Children, worktree panes, terminals, other workspaces and closing panes do not count.
        var ignored = new[] { Agent("c", "w", parent: "a"), Agent("t", "w", folder: @"C:\wt\t"), new RunSession { Id = "s", WorkspaceId = "w", Kind = "shell" }, Agent("o", "other"), Agent("x", "w") };
        Same(PaneWorktreePolicy.Decide(workspace, ignored, new HashSet<string> { "x" }), PaneWorktreeDecision.SameFolder, "only open agent panes in the folder count");
        Same(PaneWorktreePolicy.DecideWithoutAsking(workspace, [Agent("a", "w")]), PaneWorktreeDecision.SameFolder, "the phone cannot be asked: the folder");
        Same(PaneWorktreePolicy.Decide(workspace with { Remote = new RemoteWorkspaceReference("c", "w", "h"), PaneWorktreeMode = "every" }, []), PaneWorktreeDecision.SameFolder, "a remote workspace works in its folder");
        Check(PaneWorktreePolicy.DiscardBlocker("a", [Agent("c", "w", parent: "a")], []) is { Length: > 0 } && PaneWorktreePolicy.DiscardBlocker("a", [Agent("b", "w")], []) is null, "children block a discard");
        // PORT: the Mac's FNV-1a start, then the next free one.
        Same(PaneWorktreePorts.Port("pane-a", new HashSet<int>(), _ => true), 4141, "the Mac's port for pane-a");
        Same(PaneWorktreePorts.Port("11111111-2222-3333-4444-555555555555", new HashSet<int>(), _ => true), 4189, "the Mac's port for a uuid");
        Same(PaneWorktreePorts.Port("pane-a", new HashSet<int> { 4141 }, _ => true), 4142, "a taken port is skipped");
        Same(PaneWorktreePorts.Port("pane-a", new HashSet<int>(), port => port != 4141), 4142, "a bound port is skipped");
        // The note: once, never on a slash command, with the branch, base and PORT.
        var pane = new PaneWorktree { WorktreePath = @"C:\wt\p", Branch = "mighty/p", BaseBranch = "main", BaseCommit = Commit, Checkout = @"C:\repo", Port = 4141 };
        Check(PaneWorktreeNote.Applies(pane, "fix it") && !PaneWorktreeNote.Applies(pane, " /compact") && !PaneWorktreeNote.Applies(pane with { NoteGiven = true }, "fix it"), "the note applies once, never to a slash command");
        var note = PaneWorktreeNote.Attach(pane, "fix it");
        Check(note.StartsWith("fix it\n\n[Mighty Code] This pane works in its own git worktree (C:\\wt\\p) on the branch mighty/p, made from main", StringComparison.Ordinal) && note.Contains("PORT=4141", StringComparison.Ordinal)
            && note.Contains("Do not merge, push, rebase or switch branches.", StringComparison.Ordinal), "the Mac's note");
        Check(PaneWorktreeNote.ConflictPrompt(pane).Contains("`git merge main`", StringComparison.Ordinal), "the conflict request names the merge");
        var env = pane.Environment(@"C:\repo");
        Check(env["PORT"] == "4141" && env["MIGHTY_WORKSPACE_FOLDER"] == @"C:\repo" && env["MIGHTY_WORKTREE"] == @"C:\wt\p" && env.Keys.All(StartRunRequest.PaneEnvironmentKeys.Contains), "the run environment");
        // Saved records.
        if (OperatingSystem.IsWindows())
        {
            Check(pane.Normalized("p", false) is not null && pane.Normalized("q", false) is null && (pane with { Port = 80 }).Normalized("p", false) is null && (pane with { Checkout = "relative" }).Normalized("p", false) is null, "only the app's own records load");
            var running = (pane with { SetupCommand = "npm ci", SetupStatus = PaneWorktree.Running }).Normalized("p", true)!;
            Same(running.SetupStatus, PaneWorktree.Failed, "a setup cut off by the quit reads failed");
            Check((pane with { MergedCommit = "x", PreMergeCommit = Commit }).Normalized("p", false) is { MergedCommit: null, PreMergeCommit: null }, "a bad merge record is dropped");
            Check((pane with { WorkingFolder = @"C:\elsewhere" }).Normalized("p", false) is { WorkingFolder: null } && (pane with { WorkingFolder = @"C:\wt\p\sub" }).Normalized("p", false) is { WorkingFolder: @"C:\wt\p\sub" }, "the run folder lies in the worktree");
            var kept = KeptPaneWorktree.Normalized([new KeptPaneWorktree { SessionId = "p", Title = "t", Worktree = pane }, new KeptPaneWorktree { SessionId = "p", Worktree = pane }, new KeptPaneWorktree { SessionId = "bad id!", Worktree = pane }], false);
            Check(kept is { Count: 1 }, "one kept record per session, valid ones only");
        }
        Same(PaneWorktreeMode.Saved("ask"), null, "ask is saved as nothing");
        Same(PaneWorktreeSetup.NormalizedCommand("  npm ci\r\nnpm test "), "npm ci  npm test", "one line");
        Check(PaneWorktreeSetup.NormalizedCommand("   ") is null, "empty is none");
        return Task.CompletedTask;
    }

    public static Task CopyListAndPullRequest()
    {
        SameList(PaneWorktreeCopy.NormalizedPaths(["./.env", ".env", "../x", "/abs", "~/h", "a/.git/b", "dir/", "C:\\x", "a\\b"]) ?? [], [".env", "dir", "a/b"], "relative paths inside only");
        SameList(PaneWorktreeCopy.Entries(".env, .env.local\nconfig/").Select(e => e.Trim()), [".env", ".env.local", "config/"], "commas and lines");
        var base_ = Verification.Temp();
        try
        {
            var source = Path.Combine(base_, "ws"); var target = Path.Combine(base_, "wt");
            Directory.CreateDirectory(Path.Combine(source, "conf", "deep")); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, ".env"), "A=1"); File.WriteAllText(Path.Combine(source, "conf", "deep", "x.txt"), "x");
            File.WriteAllText(Path.Combine(source, "exists.txt"), "new"); File.WriteAllText(Path.Combine(target, "exists.txt"), "old");
            var result = PaneWorktreeCopy.Copy([".env", "conf", "exists.txt", "missing.txt"], source, target);
            SameList(result.Copied, [".env", "conf"], "files and folders are copied");
            Check(File.ReadAllText(Path.Combine(target, "conf", "deep", "x.txt")) == "x" && File.ReadAllText(Path.Combine(target, "exists.txt")) == "old", "nothing is overwritten");
            Check(result.Skipped["exists.txt"] == "exists" && result.Skipped["missing.txt"] == "missing", "skips say why");
        }
        finally { RemoveFolder(base_); }
        // The list read from git.
        var root = OperatingSystem.IsWindows() ? @"C:\Users\me\.mightycode\worktrees" : "/home/me/.mightycode/worktrees";
        var porcelain = $"worktree {(OperatingSystem.IsWindows() ? "C:/repo" : "/repo")}\nHEAD {Commit}\nbranch refs/heads/main\n\nworktree {root.Replace('\\', '/')}/pane-1\nHEAD {Commit}\nbranch refs/heads/mighty/pane-1\n\nworktree {root.Replace('\\', '/')}/other\nHEAD {Commit}\nbranch refs/heads/feature\n\n";
        var found = PaneWorktreeInventory.Parse(porcelain, root);
        Check(found is [{ SessionId: "pane-1", Branch: "mighty/pane-1" }], "only <root>/<id> on mighty/<id>");
        var open = new RunSession { Id = "open-1", WorkspaceId = "w", Kind = "claude", Title = "Open", PaneWorktree = new PaneWorktree { WorktreePath = Path.Combine(root, "open-1"), Branch = "mighty/open-1", BaseBranch = "main", Checkout = root, Port = 4100 } };
        var kept = new KeptPaneWorktree { SessionId = "kept-1", Title = "Kept", Worktree = new PaneWorktree { WorktreePath = Path.Combine(root, "kept-1"), Branch = "mighty/kept-1", BaseBranch = "main", Checkout = root, Port = 4101 } };
        var rows = PaneWorktreeInventory.Rows([open], [kept], [.. found, new PaneWorktreeOnDisk("child-1", Path.Combine(root, "child-1"), "mighty/child-1", Commit)], new HashSet<string> { "child-1" }, root, "main", _ => 4200);
        SameList(rows.Select(r => r.SessionId + ":" + r.Origin), ["open-1:open", "kept-1:kept", "pane-1:found"], "open, kept, then found; children left out");
        // The pull request page.
        foreach (var remote in new[] { "https://github.com/me/app.git", "git@github.com:me/app.git", "ssh://git@github.com/me/app" })
            Same(PaneWorktreePR.GitHubRepository(remote), "me/app", "a GitHub remote: " + remote);
        Check(PaneWorktreePR.GitHubRepository("https://gitlab.com/me/app.git") is null && PaneWorktreePR.GitHubRepository("https://github.com/me/app/extra") is null, "other hosts and paths are none");
        Same(PaneWorktreePR.CompareUrl("me/app", "main", "mighty/p"), "https://github.com/me/app/compare/main...mighty/p?expand=1", "the compare page");
        return Task.CompletedTask;
    }

    private static async Task<(DelegationPlace Place, PaneWorktree Pane)> Pane(string id)
    {
        var place = new DelegationPlace();
        await place.Repository(1);
        var check = await place.Maker.Check(place.Repo) as ChildWorktreeCheck.Ready ?? throw new InvalidOperationException("the repository cannot host a worktree");
        var made = await place.Maker.MakePane(id, check.Base) as ChildWorktreeOutcome.Created ?? throw new InvalidOperationException("no pane worktree");
        Check(!Directory.Exists(Path.Combine(made.Worktree.WorktreePath, ChildWorktree.NotesFolder)), "a pane worktree has no TASK and REPORT");
        return (place, PaneWorktree.From(made.Worktree, check.Base.Repository, 4141, null));
    }

    public static async Task GitMergeCommitUndoStatusAndDiscard()
    {
        if (DelegationGit.Executable is null) return;
        var (place, pane) = await Pane("pane-a");
        try
        {
            Check(await PaneWorktreeGit.Merge(pane) is PaneWorktreeMergeOutcome.Refused { Reason: PaneWorktreeRefusal.NothingToMerge }, "nothing to merge yet");
            // A new file blocks the merge; commit-and-merge commits it, leaving a secret-looking one out.
            File.WriteAllText(Path.Combine(pane.WorktreePath, "feature.txt"), "feature\n");
            File.WriteAllText(Path.Combine(pane.WorktreePath, "id_rsa"), "secret\n");
            Check(await PaneWorktreeGit.Merge(pane) is PaneWorktreeMergeOutcome.Refused { Reason: PaneWorktreeRefusal.UncommittedChanges }, "an untracked file blocks the merge");
            Check(await PaneWorktreeGit.Status(pane) is { Ahead: 0, Behind: 0, ChangedFiles: 2 }, "two changed files");
            var committed = await PaneWorktreeGit.CommitAll(pane);
            Check(committed is PaneWorktreeCommitOutcome.Committed { LeftOut: ["id_rsa"] }, "the secret-looking file is left out: " + committed);
            Check(await PaneWorktreeGit.Merge(pane) is PaneWorktreeMergeOutcome.Refused { Reason: PaneWorktreeRefusal.UncommittedChanges }, "the left-out file still blocks");
            File.Delete(Path.Combine(pane.WorktreePath, "id_rsa"));
            var files = await PaneWorktreeGit.ChangedFiles(pane);
            Check(files is [{ Path: "feature.txt", Status: "A" }], "the changed files against the base");
            Check((await PaneWorktreeGit.FileDiff(pane, "feature.txt"))?.Contains("+feature", StringComparison.Ordinal) == true, "the file's diff");
            var before = await place.Git("rev-parse", "HEAD");
            var merged = await PaneWorktreeGit.Merge(pane) as PaneWorktreeMergeOutcome.Merged ?? throw new InvalidOperationException("not merged");
            Check(merged.PreMerge == before && await place.Git("rev-parse", "HEAD") == merged.Commit && File.Exists(Path.Combine(place.Repo, "feature.txt")), "a fast-forward into main");
            // Undo puts main back and keeps the pane's commits.
            var recorded = pane with { MergedCommit = merged.Commit, PreMergeCommit = merged.PreMerge };
            Check(await PaneWorktreeGit.UndoMerge(recorded) is PaneWorktreeUndoOutcome.Undone && await place.Git("rev-parse", "HEAD") == before && !File.Exists(Path.Combine(place.Repo, "feature.txt")), "the merge is undone");
            Check(await PaneWorktreeGit.UndoMerge(recorded) is PaneWorktreeUndoOutcome.Refused { Reason: "baseMoved" }, "a second undo finds the base moved");
            Check(await PaneWorktreeGit.UndoMerge(pane) is PaneWorktreeUndoOutcome.Refused { Reason: "notMerged" }, "no merge on record");
            // The base moves: one merge commit; a conflict is refused before any write.
            File.WriteAllText(Path.Combine(place.Repo, "base.txt"), "base\n"); await place.Git("add", "-A"); await place.Git("commit", "-q", "-m", "base");
            Check(await PaneWorktreeGit.Status(pane) is { Ahead: 1, Behind: 1 }, "ahead and behind");
            var second = await PaneWorktreeGit.Merge(pane) as PaneWorktreeMergeOutcome.Merged ?? throw new InvalidOperationException("no merge commit");
            Check((await place.Git("rev-list", "--parents", "-n", "1", second.Commit)).Split(' ').Length == 3, "a merge commit with two parents");
            File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "main side\n"); await place.Git("commit", "-q", "-am", "main side");
            File.WriteAllText(Path.Combine(pane.WorktreePath, "file-0.txt"), "pane side\n"); await place.GitIn(pane.WorktreePath, "commit", "-q", "-am", "pane side");
            var head = await place.Git("rev-parse", "HEAD");
            Check(await PaneWorktreeGit.Merge(pane) is PaneWorktreeMergeOutcome.Refused { Reason: PaneWorktreeRefusal.MergeConflict } && await place.Git("rev-parse", "HEAD") == head, "a conflict changes nothing");
            // Tracked changes in the workspace folder refuse; so does another branch checked out.
            File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "dirty\n");
            Check(await PaneWorktreeGit.Merge(pane) is PaneWorktreeMergeOutcome.Refused { Reason: PaneWorktreeRefusal.TrackedChanges or PaneWorktreeRefusal.MergeConflict }, "a dirty workspace folder refuses");
            await place.Git("checkout", "-q", "--", "file-0.txt");
            await place.Git("checkout", "-q", "-b", "other");
            Check(await PaneWorktreeGit.Merge(pane) is PaneWorktreeMergeOutcome.Refused { Reason: PaneWorktreeRefusal.BranchNotCheckedOut }, "another branch checked out refuses");
            await place.Git("checkout", "-q", "main");
            // The list finds the worktree; unmerged work keeps it; discard removes it and its branch.
            Check(await PaneWorktreeGit.Worktrees(place.Repo, place.Root) is [{ SessionId: "pane-a" }], "git lists the pane worktree");
            Check(await PaneWorktreeGit.HasUnmergedWork(pane) == true, "unmerged commits are work");
            Check(await PaneWorktreeGit.Discard(pane, "pane-a", place.Root) is ChildDiscardOutcome.Discarded && !Directory.Exists(pane.WorktreePath) && (await place.MightyBranches()).Length == 0, "discard removes the worktree and its branch");
        }
        finally { place.Remove(); }
    }
}
