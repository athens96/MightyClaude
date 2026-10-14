using System.Text;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// A temp folder holding <c>repo</c> (the parent's checkout), <c>worktrees</c> (the children's root) and <c>profile</c> (the
/// delegation file). Fixture git is kept away from the user's and the system's settings; the repository sets its own identity
/// and line endings, which both the fixture and delegation's own git read, so the two agree whatever the PC is set to.
/// </summary>
internal sealed class DelegationPlace
{
    internal string Base { get; } = Verification.Temp();
    internal string Repo => Path.Combine(Base, "repo");
    internal string Root => Path.Combine(Base, "worktrees");
    internal string Profile => Path.Combine(Base, "profile");
    internal DelegationFileStore Store => new(Profile);
    internal ChildWorktreeMaker Maker => new(Root, _ => 50_000_000_000);
    internal void Remove() => RemoveFolder(Base);

    internal Task<string> Git(params string[] arguments) => GitIn(Repo, arguments);

    internal async Task<string> GitIn(string folder, params string[] arguments)
    {
        var settings = Path.Combine(Base, "fixture.gitconfig");
        if (!File.Exists(settings)) File.WriteAllText(settings, "");
        var info = ChildProcess.StartInfo(DelegationGit.Executable ?? throw new InvalidOperationException("git is not on the PATH"), ["-C", folder, .. arguments], folder);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_GLOBAL"] = settings; info.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; info.Environment["GIT_OPTIONAL_LOCKS"] = "0"; info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var who in new[] { "GIT_AUTHOR", "GIT_COMMITTER" }) { info.Environment[who + "_NAME"] = "Fixture"; info.Environment[who + "_EMAIL"] = "fixture@example.invalid"; }
        await using var process = ChildProcess.Start(info); process.Input.Close();
        var stdout = process.Output.ReadToEndAsync(); var stderr = process.Error.ReadToEndAsync();
        // Generous: the first git launch on a cold CI runner can take seconds.
        var exit = await process.Completion.WaitAsync(TimeSpan.FromSeconds(120));
        var output = await stdout; var errors = await stderr;
        Check(exit == 0, $"git {string.Join(" ", arguments)}: {errors}");
        return output.Trim().Replace("\r\n", "\n");
    }

    /// <summary><c>git init -b main</c> with <paramref name="commits"/> commits, each adding one file in <paramref name="folder"/>; returns the head.</summary>
    internal async Task<string> Repository(int commits = 1, string folder = "", string autocrlf = "false")
    {
        Directory.CreateDirectory(Repo);
        await Git("init", "-q", "-b", "main");
        foreach (var (key, value) in new[] { ("core.autocrlf", autocrlf), ("user.name", "Fixture"), ("user.email", "fixture@example.invalid"), ("commit.gpgsign", "false") }) await Git("config", key, value);
        var files = Path.Combine(Repo, folder); Directory.CreateDirectory(files);
        for (var index = 0; index < commits; index++)
        {
            File.WriteAllText(Path.Combine(files, $"file-{index}.txt"), index + "\n");
            await Git("add", "-A"); await Git("commit", "-q", "-m", "commit " + index);
        }
        return commits > 0 ? await Git("rev-parse", "HEAD") : "";
    }

    /// <summary>A running child made the app's way: its own <c>mighty/&lt;id&gt;</c> branch and worktree at the parent's head.</summary>
    internal async Task<ChildRecord> Child(string id, string? parentCheckout = null)
    {
        var made = await Maker.Create(id, Repo, "Do the work.\n") as ChildWorktreeOutcome.Created ?? throw new InvalidOperationException("the child was not made: " + id);
        return ChildRecord.Make(id, "parent", made.Worktree.WorktreePath, made.Worktree.ParentBranch, made.Worktree.BaseCommit, "acceptEdits", "key-" + id, state: ChildState.Running, parentCheckout: parentCheckout);
    }

    /// <summary>A child that committed one file per name and then reported at its head.</summary>
    internal async Task<(ChildRecord Child, string Head)> ReportedChild(string id, string[]? files = null, string? parentCheckout = null)
    {
        var child = await Child(id, parentCheckout); var head = child.BaseCommit;
        foreach (var name in files ?? [id + ".txt"]) head = await Commit(child.WorktreePath, name, $"{id} {name}\n");
        File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), $"# Report of {id}\nDone.\n");
        return (child.RecordingReport(head) ?? throw new InvalidOperationException("the child did not report"), head);
    }

    /// <summary>Writes <paramref name="name"/> in <paramref name="folder"/> and commits it; returns the new head.</summary>
    internal async Task<string> Commit(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Utf8(text));
        await GitIn(folder, "add", "--", name); await GitIn(folder, "commit", "-q", "-m", "add " + name);
        return await GitIn(folder, "rev-parse", "HEAD");
    }

    /// <summary>Every ref, and each checkout's HEAD, index entries and file status: a refusal leaves all of it as it was.</summary>
    internal async Task<string> Snapshot(params string[] checkouts)
    {
        var parts = new List<string> { await Git("for-each-ref", "--format=%(refname) %(objectname)") };
        foreach (var folder in checkouts.Prepend(Repo))
            foreach (var arguments in new[] { new[] { "rev-parse", "--symbolic-full-name", "HEAD" }, ["rev-parse", "HEAD"], ["ls-files", "-s"], ["status", "--porcelain", "--untracked-files=all"] }) parts.Add(await GitIn(folder, arguments));
        return string.Join("\n--\n", parts);
    }

    /// <summary>Every ref, every registered worktree, the parent's head and file status, and every file under the worktree root with what it holds.</summary>
    internal async Task<string> Disk()
    {
        var parts = new List<string> { await Git("for-each-ref", "--format=%(refname) %(objectname)"), await Git("worktree", "list", "--porcelain"), await Git("rev-parse", "HEAD"), await Git("status", "--porcelain", "--untracked-files=all") };
        if (Directory.Exists(Root))
            parts.AddRange(Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Directory.Exists(path) ? path + "\\" : path + ": " + Convert.ToHexString(File.ReadAllBytes(path))));
        return string.Join("\n", parts);
    }

    /// <summary>The registered worktrees, as delegation compares paths.</summary>
    internal async Task<List<string>> WorktreeList() =>
        (await Git("worktree", "list", "--porcelain")).Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Select(line => DelegationPaths.Canonical(line[9..])).ToList();

    internal async Task<bool> OnlyTheRepositoryIsListed() => (await WorktreeList()).SequenceEqual([DelegationPaths.Canonical(Repo)], DelegationPaths.Comparer);
    internal Task<string> MightyBranches() => Git("for-each-ref", "--format=%(refname)", "refs/heads/mighty/");
}

/// <summary>
/// Delegation's git on real repositories in temp folders, ported from the Mac's DelegationWorktreeTests, DelegationMergeTests,
/// DelegationCardUndoTests and the git half of DelegationCleanupTests, with the checks Windows needs of its own: paths,
/// line endings, long paths and folders another program holds open.
/// </summary>
internal static class DelegationGitVerification
{
    private static ChildWorktreeOutcome Refused(DelegationReasonCode reason) => new ChildWorktreeOutcome.Refused(reason);
    private static ChildMergeOutcome NotMerged(DelegationReasonCode reason) => new ChildMergeOutcome.Refused(reason);
    private static MergeRecord Merged(ChildMergeOutcome outcome) => outcome is ChildMergeOutcome.Merged merged ? merged.Record : throw new InvalidOperationException("not merged: " + outcome);
    private static ChildWorktree Made(ChildWorktreeOutcome outcome) => outcome is ChildWorktreeOutcome.Created created ? created.Worktree : throw new InvalidOperationException("not made: " + outcome);

    internal static async Task WorktreesAreMadeBesideTheCheckout()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository(2);
            var head = await place.Git("rev-parse", "HEAD"); var maker = place.Maker;
            var child = Made(await maker.Create("child-1", place.Repo, "Fix the parser.\n"));
            Check(child is { ParentBranch: "main", Branch: "mighty/child-1" } && child.BaseCommit == head && child.WorktreePath == Path.Combine(place.Root, "child-1") && child.WorkingFolder == child.WorktreePath, "the flat branch at the parent's head, in its own folder under the root");
            Check(await place.Git("rev-parse", "refs/heads/mighty/child-1") == head && await place.GitIn(child.WorktreePath, "symbolic-ref", "HEAD") == "refs/heads/mighty/child-1", "the worktree is on the child's branch");
            var list = await place.Git("worktree", "list", "--porcelain");
            Check(list.Split("worktree ").Length == 3 && list.Contains("branch refs/heads/mighty/child-1", StringComparison.Ordinal), "one more worktree is registered");
            Check(child.TaskFile == Path.Combine(child.WorktreePath, ".mighty-delegation", "TASK.md") && File.ReadAllBytes(child.TaskFile).SequenceEqual(Utf8("Fix the parser.\n")) && File.ReadAllBytes(child.ReportFile).Length == 0
                && File.ReadAllBytes(Path.Combine(child.WorktreePath, ".mighty-delegation", ".gitignore")).SequenceEqual(Utf8("*\n")), "TASK.md holds the task byte for byte, REPORT.md is empty and the folder ignores itself");
            // Both notes are ignored: no change in the child, and a commit of everything leaves them out.
            SameList((await place.GitIn(child.WorktreePath, "check-ignore", ".mighty-delegation/TASK.md", ".mighty-delegation/REPORT.md")).Split('\n'), [".mighty-delegation/TASK.md", ".mighty-delegation/REPORT.md"], "both notes are ignored");
            Check((await place.GitIn(child.WorktreePath, "status", "--porcelain", "--untracked-files=all")).Length == 0, "a fresh worktree has no change");
            File.WriteAllText(child.ReportFile, "REPORT: done\n"); File.WriteAllText(Path.Combine(child.WorktreePath, "work.txt"), "work\n");
            await place.GitIn(child.WorktreePath, "add", "-A"); await place.GitIn(child.WorktreePath, "commit", "-q", "-m", "child work");
            SameList((await place.Git("ls-tree", "-r", "--name-only", "mighty/child-1")).Split('\n').Order(), ["file-0.txt", "file-1.txt", "work.txt"], "a commit of everything leaves the notes out");
            Check(await place.Git("symbolic-ref", "--short", "HEAD") == "main" && await place.Git("rev-parse", "HEAD") == head && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "the parent checkout is untouched");
            Check(DelegationCoordinator.FirstInput("Fix the parser.\n", child) == $"Another Claude pane in Mighty Claude delegated this task to you. You work in your own git worktree on the branch mighty/child-1; the task is also saved in {child.TaskFile}.\n\nWhen you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to {child.ReportFile}: what you did, what is left and what your parent should check. Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.\n\nTask:\nFix the parser.\n", "the child's first input is the Mac's, naming its own files");

            // Never replaces an existing branch, and never makes an unsafe name.
            await place.Git("branch", "mighty/taken", "HEAD~1");
            var old = await place.Git("rev-parse", "mighty/taken");
            Check(await maker.Create("taken", place.Repo, "t") is ChildWorktreeOutcome.Failed && await place.Git("rev-parse", "mighty/taken") == old && (await place.Git("worktree", "list", "--porcelain")).Split("worktree ").Length == 3, "an existing branch is never reused");
            foreach (var unsafeId in new[] { "", "../escape", "a/b", @"a\b", ".hidden", "-flag", "x.lock", "a..b", "sp ace", "\uD55C\uAE00", "nul", "CON", "com1.txt", "LPT9", "a:b", "trailing." })
                Check(!ChildWorktreeMaker.IsSafeSessionId(unsafeId) && await maker.Create(unsafeId, place.Repo, "t") is ChildWorktreeOutcome.Failed, "an unsafe id is never made: " + unsafeId);
            Check(ChildWorktreeMaker.IsSafeSessionId(Wire.Id()) && ChildWorktreeMaker.IsSafeSessionId(Guid.NewGuid().ToString().ToUpperInvariant()) && ChildWorktreeMaker.IsSafeSessionId("console") && ChildWorktreeMaker.IsSafeSessionId("a.b_c-d"), "a session id is safe");
            SameList((await place.MightyBranches()).Split('\n'), ["refs/heads/mighty/child-1", "refs/heads/mighty/taken"], "no other branch was made");
            SameList(Directory.GetFileSystemEntries(place.Root).Select(Path.GetFileName), ["child-1"], "no other folder was made");
        }
        finally { place.Remove(); }

        // A subfolder workspace works in the same subfolder of the worktree.
        var nested = new DelegationPlace();
        try
        {
            await nested.Repository(1, Path.Combine("app", "src"));
            var child = Made(await nested.Maker.Create("child-sub", Path.Combine(nested.Repo, "app", "src"), "t")); Check(child.WorkingFolder == Path.Combine(child.WorktreePath, "app", "src")
                && File.Exists(Path.Combine(child.WorkingFolder, "file-0.txt")) && File.Exists(Path.Combine(child.WorktreePath, ".mighty-delegation", "TASK.md")), "a subfolder workspace works in the same subfolder");
        }
        finally { nested.Remove(); }

        // The app's root is under the user's profile; unknown free space is read from the nearest existing folder.
        Check(ChildWorktreeMaker.DefaultRoot == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mightyclaude", "worktrees") && new ChildWorktreeMaker().Root == ChildWorktreeMaker.DefaultRoot, "the app's root is %USERPROFILE%\\.mightyclaude\\worktrees");
        var missing = Path.Combine(Path.GetTempPath(), "mighty-missing-" + Wire.Id(), "a", "b");
        Check(ChildWorktreeMaker.AvailableBytes(missing) is > 0 && !Directory.Exists(missing) && (Enumerable.Range('D', 23).Select(letter => (char)letter + @":\").FirstOrDefault(drive => !Directory.Exists(drive)) is not { } none || ChildWorktreeMaker.AvailableBytes(none + @"a\b") is null), "free space is the volume's, and unknown where there is none");
    }

    internal static async Task WorkspacesThatCannotHostAChildAreRefused()
    {
        var plenty = (Func<string, long?>)(_ => 50_000_000_000);
        async Task NothingCreated(DelegationPlace place) => Check(!Directory.Exists(place.Root) && (await place.MightyBranches()).Length == 0 && (await place.Git("worktree", "list", "--porcelain")).Split("worktree ").Length == 2, "nothing was created");
        var plain = new DelegationPlace(); var unborn = new DelegationPlace(); var detached = new DelegationPlace(); var low = new DelegationPlace();
        try
        {
            // Outside git, a missing folder, or a path that is not full.
            Directory.CreateDirectory(plain.Repo); var maker = new ChildWorktreeMaker(plain.Root, plenty);
            foreach (var workspace in new[] { plain.Repo, Path.Combine(plain.Repo, "missing"), "relative/folder", @"\rooted\only", "C:relative", plain.Repo + "\0x" })
                Check(await maker.Create("c", workspace, "t") == Refused(DelegationReasonCode.NotGit), "not git: " + workspace.Replace("\0", "<NUL>"));
            Check(!Directory.Exists(plain.Root), "nothing was created outside git");

            await unborn.Repository(0);
            Check(await new ChildWorktreeMaker(unborn.Root, plenty).Create("c", unborn.Repo, "t") == Refused(DelegationReasonCode.UnbornBranch), "a branch with no commit is refused");
            await NothingCreated(unborn);

            await detached.Repository(2); await detached.Git("checkout", "-q", "--detach", "HEAD~1");
            Check(await new ChildWorktreeMaker(detached.Root, plenty).Create("c", detached.Repo, "t") == Refused(DelegationReasonCode.DetachedHead), "a detached HEAD is refused");
            await NothingCreated(detached);

            // Under 10 GB free on the root's volume, or unknown.
            await low.Repository(1); var asked = new List<string>();
            Check(await new ChildWorktreeMaker(low.Root, path => { lock (asked) asked.Add(path); return ChildWorktreeMaker.MinimumFreeBytes - 1; }).Create("c", low.Repo, "t") == Refused(DelegationReasonCode.LowDisk), "under 10 GB is refused");
            SameList(asked, [low.Root], "the free space asked about is the worktree root's");
            Check(await new ChildWorktreeMaker(low.Root, _ => null).Create("c", low.Repo, "t") == Refused(DelegationReasonCode.LowDisk), "unknown free space counts as low");
            await NothingCreated(low);
            Check(await new ChildWorktreeMaker(low.Root, _ => ChildWorktreeMaker.MinimumFreeBytes).Create("c", low.Repo, "t") is ChildWorktreeOutcome.Created, "exactly 10 GB free is enough");
        }
        finally { plain.Remove(); unborn.Remove(); detached.Remove(); low.Remove(); }
    }

    internal static async Task TheToolMergeFastForwardsOrRefuses()
    {
        var place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var child = await place.Child("c1");
            await place.Commit(child.WorktreePath, "a.txt", "a\n"); var head = await place.Commit(child.WorktreePath, "b.txt", "b\n");
            File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\nDone.\n");
            // Running with no report yet, and every other state even with a report head on record.
            var before = await place.Snapshot(child.WorktreePath);
            Check(await ChildMerge.ToolMerge(child, head, place.Repo) == NotMerged(DelegationReasonCode.NotReported), "a running child has not reported");
            foreach (var state in Enum.GetValues<ChildState>().Where(state => state != ChildState.Reported))
                Check(await ChildMerge.ToolMerge(child with { State = state, ReportRevision = 1, ReportHead = head }, head, place.Repo) == NotMerged(DelegationReasonCode.NotReported), state + " is not reported");
            Check(await place.Snapshot(child.WorktreePath) == before, "a refusal changes nothing");
            child = child.RecordingReport(head)!;

            var record = Merged(await ChildMerge.ToolMerge(child, head, place.Repo)); Check(record == new MergeRecord("c1", MergeKind.ToolFastForward, "main", start, head, head), "the fast-forward writes its merge record");
            // A fast-forward alone: main is still checked out, now at the child's head, with no merge commit and a clean checkout.
            Check(await place.Git("symbolic-ref", "HEAD") == "refs/heads/main" && await place.Git("rev-parse", "HEAD") == head && await place.Git("rev-list", "--count", start + "..HEAD") == "2"
                && (await place.Git("rev-list", "--merges", "HEAD")).Length == 0 && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0 && File.ReadAllText(Path.Combine(place.Repo, "b.txt")) == "b\n", "main fast-forwarded to the child's head");
            Check(await place.Git("rev-parse", "refs/heads/mighty/c1") == head && await place.GitIn(child.WorktreePath, "symbolic-ref", "HEAD") == "refs/heads/mighty/c1", "the child's branch and checkout are only read");

            // The record is written into the delegation file, which keeps it across a restart.
            var file = DelegationFile.Of([child]).RecordingMerge(record)!;
            Check(file.Children[0].State == ChildState.Merged && file.Merges.SequenceEqual([record]), "the merge goes on record with the child merged");
            place.Store.Save(file); var restored = place.Store.Load();
            Check(restored.Merges.SequenceEqual([record]) && restored.Merges[0].PreMergeCommit == start && restored.Children[0].State == ChildState.Merged, "a restart reads both back");
            // A merged child is no longer reported: a second merge is refused and writes no second record.
            before = await place.Snapshot(child.WorktreePath);
            Check(await ChildMerge.ToolMerge(restored.Children[0], head, place.Repo) == NotMerged(DelegationReasonCode.NotReported) && await place.Snapshot(child.WorktreePath) == before, "a merged child is not merged again");
            Check(restored.RecordingMerge(record) is null && restored.RecordingMerge(record with { ChildId = "missing" }) is null, "no second record is written");
        }
        finally { place.Remove(); }

        place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var (child, head) = await place.ReportedChild("c1", ["a.txt"]);
            async Task Refuses(ChildRecord subject, string expected, string checkout, DelegationReasonCode reason, string why)
            {
                var before = await place.Snapshot(child.WorktreePath);
                Same(await ChildMerge.ToolMerge(subject, expected, checkout), NotMerged(reason), why);
                Check(await place.Snapshot(child.WorktreePath) == before, why + ": nothing changed");
            }
            // A moved head: the expected head is not the reported one.
            foreach (var wrong in new[] { child.BaseCommit, head[..12], new string('0', 40), "HEAD", "mighty/c1", "" }) await Refuses(child, wrong, place.Repo, DelegationReasonCode.HeadMoved, "expected head " + wrong);
            // An unstaged, then a staged change to a tracked file in the child.
            File.WriteAllText(Path.Combine(child.WorktreePath, "a.txt"), "changed\n");
            await Refuses(child, head, place.Repo, DelegationReasonCode.TrackedChanges, "an unstaged change in the child");
            await place.GitIn(child.WorktreePath, "add", "a.txt");
            await Refuses(child, head, place.Repo, DelegationReasonCode.TrackedChanges, "a staged change in the child");
            await place.GitIn(child.WorktreePath, "reset", "-q", "--hard");
            // An untracked file and REPORT.md in the child do not count; a tracked change in the parent does.
            File.WriteAllText(Path.Combine(child.WorktreePath, "untracked.txt"), "note\n"); File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "parent edit\n");
            await Refuses(child, head, place.Repo, DelegationReasonCode.TrackedChanges, "a tracked change in the parent");
            await place.Git("checkout", "-q", "--", "file-0.txt");
            // The parent's side: another branch at the same commit, a detached HEAD, a folder that is no checkout of it.
            await place.Git("checkout", "-q", "-b", "other");
            await Refuses(child, head, place.Repo, DelegationReasonCode.BranchNotCheckedOut, "another branch checked out");
            await place.Git("checkout", "-q", "--detach", "main");
            await Refuses(child, head, place.Repo, DelegationReasonCode.BranchNotCheckedOut, "a detached parent");
            foreach (var folder in new[] { place.Base, Path.Combine(place.Base, "missing"), "relative/folder" }) await Refuses(child, head, folder, DelegationReasonCode.BranchNotCheckedOut, "no checkout at " + folder);
            await place.Git("checkout", "-q", "main");
            // A worktree that is gone.
            await Refuses(child with { WorktreePath = Path.Combine(place.Root, "gone") }, head, place.Repo, DelegationReasonCode.WorktreeMissing, "the worktree is gone");
            // The upper-case head a human might paste is the same commit, and the parent's folder may be written any way Windows reads it.
            var sibling = await place.ReportedChild("c2", ["s.txt"]);
            File.WriteAllText(Path.Combine(place.Repo, "scratch.txt"), "scratch\n");
            var record = Merged(await ChildMerge.ToolMerge(child, " " + head.ToUpperInvariant() + "\n", place.Repo.ToUpperInvariant().Replace('\\', '/') + "/")); Check(record.PreMergeCommit == start && record.MergedCommit == head, "untracked files on either side do not block the merge");
            Check(File.ReadAllText(Path.Combine(place.Repo, "scratch.txt")) == "scratch\n" && File.Exists(Path.Combine(child.WorktreePath, "untracked.txt")), "untracked files stay");
            // Once one child is merged, a sibling made from the same base has diverged.
            await Refuses(sibling.Child, sibling.Head, place.Repo, DelegationReasonCode.Diverged, "diverged");
            // The child's branch moved past its report, or its checkout left its branch though at the reported head.
            var moved = await place.ReportedChild("c3", ["m.txt"]);
            var later = await place.Commit(moved.Child.WorktreePath, "m2.txt", "later\n");
            Check(await ChildMerge.ToolMerge(moved.Child, moved.Head, place.Repo) == NotMerged(DelegationReasonCode.HeadMoved) && await ChildMerge.ToolMerge(moved.Child, later, place.Repo) == NotMerged(DelegationReasonCode.HeadMoved), "the child's branch moved past its report");
            await place.GitIn(moved.Child.WorktreePath, "reset", "-q", "--hard", moved.Head); await place.GitIn(moved.Child.WorktreePath, "checkout", "-q", "--detach", moved.Head);
            Check(await ChildMerge.ToolMerge(moved.Child, moved.Head, place.Repo) == NotMerged(DelegationReasonCode.HeadMoved), "the child's checkout left its branch");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheCardMergesAndUndoes()
    {
        // Fast-forwards when it can, and undo returns the child to reported.
        var place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var (child, head) = await place.ReportedChild("c1", ["a.txt"]);
            var record = Merged(await ChildMerge.CardMerge(child, place.Repo)); Check(record == new MergeRecord("c1", MergeKind.CardFastForward, "main", start, head, head), "the card fast-forwards when it can");
            Check(await place.Git("rev-parse", "HEAD") == head && (await place.Git("rev-list", "--merges", "HEAD")).Length == 0, "no merge commit for a fast-forward");
            var file = DelegationFile.Of([child]).RecordingMerge(record)!; var merged = file.Children[0];
            // An open, idle parent: the branch, index and files go back; an untracked file stays.
            File.WriteAllText(Path.Combine(place.Repo, "scratch.txt"), "scratch\n");
            Check(await ChildMerge.Undo(record, merged, place.Repo, DelegationPaneActivity.Finished) == new ChildUndoOutcome.Undone(record, false), "the undo goes through");
            Check(await place.Git("symbolic-ref", "HEAD") == "refs/heads/main" && await place.Git("rev-parse", "HEAD") == start && (await place.Git("status", "--porcelain", "--untracked-files=no")).Length == 0
                && !File.Exists(Path.Combine(place.Repo, "a.txt")) && File.ReadAllText(Path.Combine(place.Repo, "scratch.txt")) == "scratch\n" && await place.Git("rev-parse", "refs/heads/mighty/c1") == head, "the branch, index and files are back, and an untracked file stays");
            // The open child is reported again, at the same head, with the undone record gone; it can be merged again.
            var undone = file.RecordingUndo(record)!;
            Check(undone.Children[0] is { State: ChildState.Reported } again && again.ReportHead == head && undone.Merges.Count == 0 && undone.RecordingUndo(record) is null, "the child is reported again");
            Check(await ChildMerge.CardMerge(undone.Children[0], place.Repo) is ChildMergeOutcome.Merged && await place.Git("rev-parse", "HEAD") == head, "it can be merged again");
        }
        finally { place.Remove(); }

        // Leaves exactly one merge commit when the parent moved on, and undoes it.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (child, head) = await place.ReportedChild("c1", ["a.txt", "b.txt"]);
            var moved = await place.Commit(place.Repo, "parent.txt", "p\n");
            var record = Merged(await ChildMerge.CardMerge(child, place.Repo)); Check(record is { Kind: MergeKind.CardMergeCommit, ParentBranch: "main" } && record.PreMergeCommit == moved && record.ChildHead == head, "the card makes a merge commit when the parent moved on");
            // One merge commit on top of the old parent head, its parents the old parent head and the child head.
            Check(await place.Git("rev-parse", "HEAD") == record.MergedCommit && await place.Git("rev-list", "--parents", "-n", "1", "HEAD") == $"{record.MergedCommit} {moved} {head}" && await place.Git("rev-list", "--merges", moved + "..HEAD") == record.MergedCommit
                && await place.Git("rev-list", "--count", moved + "..HEAD") == "3" && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "exactly one merge commit");
            Check(await place.Git("log", "-1", "--format=%s") == "Merge branch 'mighty/c1' into main" && new[] { "a.txt", "b.txt", "parent.txt" }.All(name => File.Exists(Path.Combine(place.Repo, name))) && await place.Git("rev-parse", "refs/heads/mighty/c1") == head, "the merge holds both sides");
            var file = DelegationFile.Of([child]).RecordingMerge(record)!;
            Check(await ChildMerge.Undo(record, file.Children[0], place.Repo, DelegationPaneActivity.Idle) == new ChildUndoOutcome.Undone(record, false) && await place.Git("rev-parse", "HEAD") == moved
                && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0 && await place.Git("ls-files") == "file-0.txt\nparent.txt" && file.RecordingUndo(record)!.Children[0].State == ChildState.Reported, "the merge commit is undone");

            // Undo on a closed child makes its branch again and leaves it closed.
            var second = Merged(await ChildMerge.CardMerge(child, place.Repo));
            file = DelegationFile.Of([child]).RecordingMerge(second)!;
            await place.Git("worktree", "remove", child.WorktreePath); await place.Git("branch", "-d", "mighty/c1");
            var closed = file.SettingChild(0, file.Children[0].Applying(ChildStateEvent.ClosePane)!);
            Check(await ChildMerge.Undo(second, closed.Children[0], place.Repo, null) == new ChildUndoOutcome.Undone(second, true) && await place.Git("rev-parse", "refs/heads/mighty/c1") == head && await place.Git("rev-parse", "HEAD") == moved
                && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "a closed parent counts as idle, and the deleted branch is made again");
            Check(closed.RecordingUndo(second) is { } card && card.Children[0].State == ChildState.Closed && card.Merges.Count == 0 && card.Children[0].State.After(ChildStateEvent.Discard) == ChildState.Discarded, "the closed child stays closed, for a human to discard");
        }
        finally { place.Remove(); }

        // Refuses a conflict, tracked changes or another branch, changing nothing.
        place = new DelegationPlace();
        try
        {
            var start = await place.Repository();
            var clashing = await place.Child("c1"); var clash = await place.Commit(clashing.WorktreePath, "file-0.txt", "child\n"); clashing = clashing.RecordingReport(clash)!;
            var (child, head) = await place.ReportedChild("c2", ["a.txt"]);
            await place.Commit(place.Repo, "file-0.txt", "parent\n");
            async Task Refuses(ChildRecord subject, string checkout, DelegationReasonCode reason, string why)
            {
                var before = await place.Snapshot();
                Same(await ChildMerge.CardMerge(subject, checkout), NotMerged(reason), why);
                Check(await place.Snapshot() == before, why + ": nothing changed");
            }
            await Refuses(clashing, place.Repo, DelegationReasonCode.MergeConflict, "both sides changed the same line");
            Check(!File.Exists(Path.Combine(place.Repo, ".git", "MERGE_HEAD")), "no merge was started");
            File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "edit\n");
            await Refuses(child, place.Repo, DelegationReasonCode.TrackedChanges, "an unstaged change in the parent");
            await place.Git("add", "file-0.txt");
            await Refuses(child, place.Repo, DelegationReasonCode.TrackedChanges, "a staged change in the parent");
            await place.Git("reset", "-q", "--hard"); await place.Git("checkout", "-q", "-b", "other");
            await Refuses(child, place.Repo, DelegationReasonCode.BranchNotCheckedOut, "another branch");
            await place.Git("checkout", "-q", "--detach", "main");
            await Refuses(child, place.Repo, DelegationReasonCode.BranchNotCheckedOut, "a detached HEAD");
            foreach (var folder in new[] { place.Base, "relative/folder" }) await Refuses(child, folder, DelegationReasonCode.BranchNotCheckedOut, "no checkout at " + folder);
            await place.Git("checkout", "-q", "main");
            foreach (var state in Enum.GetValues<ChildState>().Where(state => state != ChildState.Reported)) await Refuses(child with { State = state }, place.Repo, DelegationReasonCode.NotReported, state + " is not reported");
            Check(await place.Git("rev-list", "--count", start + "..main") == "1", "the parent branch did not move");

            // Undo is refused unless the branch is checked out at the merge, the parent idle and tracked files clean.
            var record = Merged(await ChildMerge.CardMerge(child, place.Repo));
            var merged = DelegationFile.Of([child]).RecordingMerge(record)!.Children[0];
            async Task UndoRefuses(DelegationReasonCode reason, DelegationPaneActivity? activity = DelegationPaneActivity.Idle)
            {
                var before = await place.Snapshot();
                Same(await ChildMerge.Undo(record, merged, place.Repo, activity), new ChildUndoOutcome.Refused(reason), "undo refuses with " + reason);
                Check(await place.Snapshot() == before, reason + ": nothing changed");
            }
            await place.Git("checkout", "-q", "-b", "elsewhere");
            await UndoRefuses(DelegationReasonCode.BranchNotCheckedOut);
            await place.Git("checkout", "-q", "main");
            await UndoRefuses(DelegationReasonCode.ParentBusy, DelegationPaneActivity.Running);
            File.WriteAllText(Path.Combine(place.Repo, "a.txt"), "edit\n");
            await UndoRefuses(DelegationReasonCode.TrackedChanges);
            await place.Git("checkout", "-q", "--", "a.txt");
            // A file the undo would put back is in the way as an untracked file: never overwritten.
            await place.Git("rm", "-q", "file-0.txt"); await place.Git("commit", "-q", "-m", "drop file-0");
            var dropped = await place.Git("rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(place.Repo, "file-0.txt"), "mine\n");
            var inTheWay = await place.Snapshot();
            Check(await ChildMerge.Undo(record with { PreMergeCommit = record.MergedCommit, MergedCommit = dropped }, merged, place.Repo, DelegationPaneActivity.Idle) is ChildUndoOutcome.Failed && await place.Snapshot() == inTheWay
                && File.ReadAllText(Path.Combine(place.Repo, "file-0.txt")) == "mine\n", "an untracked file in the way fails the undo and is never overwritten");
            File.Delete(Path.Combine(place.Repo, "file-0.txt"));
            // The branch moved past the merged commit.
            await UndoRefuses(DelegationReasonCode.UndoParentMoved);
            await place.Git("reset", "-q", "--hard", record.MergedCommit);
            // A record of another child, or a child that ran again since the merge, has nothing to undo.
            Check(await ChildMerge.Undo(record, merged with { Id = "c9" }, place.Repo, DelegationPaneActivity.Idle) is ChildUndoOutcome.Failed { Message: var none } && none == Locale.Get("delegation.card.error.noMergeToUndo")
                && await ChildMerge.Undo(record, merged.Applying(ChildStateEvent.StartRun)!, place.Repo, DelegationPaneActivity.Idle) is ChildUndoOutcome.Failed
                && await ChildMerge.Undo(record with { ChildHead = "not a commit" }, merged, place.Repo, DelegationPaneActivity.Idle) is ChildUndoOutcome.Failed { Message: var bad } && bad == Locale.Get("delegation.card.error.badMergeRecord")
                && await place.Git("rev-parse", "HEAD") == record.MergedCommit, "nothing to undo for another child's record or a child that ran again");
            Check(await ChildMerge.Undo(record, merged, place.Repo, DelegationPaneActivity.Finished) == new ChildUndoOutcome.Undone(record, false) && await place.Git("rev-parse", "HEAD") == record.PreMergeCommit, "with every condition met, the undo goes through");
        }
        finally { place.Remove(); }
    }

    /// <summary>A child linked to the parent's checkout, which committed <c>&lt;id&gt;.txt</c>, reported and was fast-forward-merged by the parent's tool.</summary>
    internal static async Task<(ChildRecord Child, MergeRecord Merge)> MergedChild(DelegationPlace place, string id = "c1")
    {
        var (child, head) = await place.ReportedChild(id, parentCheckout: place.Repo);
        var merge = (await ChildMerge.ToolMerge(child, head, place.Repo) as ChildMergeOutcome.Merged ?? throw new InvalidOperationException("the child was not merged")).Record;
        return (child.Applying(ChildStateEvent.Merge)!, merge);
    }

    /// <summary>Adds <paramref name="pattern"/> to the repository's shared exclude file, which every worktree of it reads.</summary>
    internal static void Ignore(DelegationPlace place, string pattern) => File.AppendAllText(Path.Combine(place.Repo, ".git", "info", "exclude"), pattern + "\n");

    internal static async Task CleanupNeverForcesAndDiscardDoes()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (child, merge) = await MergedChild(place);
            async Task Kept(ChildCleanupHold hold, ChildRecord? subject = null, MergeRecord? record = null, DelegationPaneActivity? activity = DelegationPaneActivity.Finished)
            {
                var before = await place.Disk();
                Same(await ChildCleanup.CleanUp(subject ?? child, record ?? merge, place.Repo, activity), new ChildCleanupOutcome.Kept(hold), "cleanup keeps everything for " + hold);
                Check(await place.Disk() == before, hold + ": nothing on disk changed");
            }
            // Not merged: every other state, or another child's merge record. A run going in the child's pane.
            foreach (var state in Enum.GetValues<ChildState>().Where(state => state != ChildState.Merged)) await Kept(ChildCleanupHold.NotMerged, child with { State = state });
            await Kept(ChildCleanupHold.NotMerged, record: merge with { ChildId = "c9" });
            await Kept(ChildCleanupHold.ChildBusy, activity: DelegationPaneActivity.Running);
            // The worktree is gone, or is a folder the parent's repository does not list.
            await Kept(ChildCleanupHold.WorktreeMissing, child with { WorktreePath = Path.Combine(place.Root, "gone") });
            var stray = Path.Combine(place.Root, "stray"); Directory.CreateDirectory(stray);
            await Kept(ChildCleanupHold.WorktreeMissing, child with { WorktreePath = stray });
            Directory.Delete(stray);
            // The parent's checkout left the recorded branch, or that branch no longer holds the child's head.
            await place.Git("checkout", "-q", "-b", "elsewhere");
            await Kept(ChildCleanupHold.BranchNotCheckedOut);
            await place.Git("checkout", "-q", "main"); await place.Git("branch", "-q", "-D", "elsewhere"); await place.Git("reset", "-q", "--hard", child.BaseCommit);
            await Kept(ChildCleanupHold.NotInParent);
            await place.Git("reset", "-q", "--hard", merge.MergedCommit);
            // The child's branch moved past the merge, or its worktree left the branch at the merged head.
            await place.Commit(child.WorktreePath, "later.txt", "later\n");
            await Kept(ChildCleanupHold.HeadMoved);
            await place.GitIn(child.WorktreePath, "reset", "-q", "--hard", merge.ChildHead); await place.GitIn(child.WorktreePath, "checkout", "-q", "--detach");
            await Kept(ChildCleanupHold.HeadMoved);
            await place.GitIn(child.WorktreePath, "checkout", "-q", child.Branch);
            // A tracked change, unstaged and then staged; a file no ignore rule covers.
            File.WriteAllText(Path.Combine(child.WorktreePath, "c1.txt"), "changed\n");
            await Kept(ChildCleanupHold.TrackedChanges);
            await place.GitIn(child.WorktreePath, "add", "c1.txt");
            await Kept(ChildCleanupHold.TrackedChanges);
            await place.GitIn(child.WorktreePath, "reset", "-q", "--hard");
            File.WriteAllText(Path.Combine(child.WorktreePath, "notes.txt"), "note\n");
            await Kept(ChildCleanupHold.UntrackedFiles);
            File.Delete(Path.Combine(child.WorktreePath, "notes.txt"));
            // A registered worktree inside the child's, hidden by an ignore rule and holding work of its own: plain removal would take it along.
            Ignore(place, "nested/"); var nested = Path.Combine(child.WorktreePath, "nested");
            await place.Git("worktree", "add", "-q", "-b", "side", nested, "main");
            File.WriteAllText(Path.Combine(nested, "side.txt"), "side work\n");
            await Kept(ChildCleanupHold.NestedWorktree);
            Check(await ChildCleanup.NestedWorktrees(child, place.Repo) is { Count: 1 } named && DelegationPaths.Same(named[0], DelegationPaths.Canonical(nested)) && File.ReadAllText(Path.Combine(nested, "side.txt")) == "side work\n", "the nested worktree is named, and untouched");
            // The child's folder written another way is the same worktree.
            Check(await ChildCleanup.NestedWorktrees(child with { WorktreePath = child.WorktreePath.ToUpperInvariant().Replace('\\', '/') + "/" }, place.Repo.ToLowerInvariant()) is { Count: 1 }, "paths compare without regard to case or separator");
            await place.Git("worktree", "remove", "--force", nested);
            // Ignored build output beside TASK.md and REPORT.md goes with the worktree.
            Ignore(place, "build/"); Directory.CreateDirectory(Path.Combine(child.WorktreePath, "build")); File.WriteAllText(Path.Combine(child.WorktreePath, "build", "out.o"), "o\n");
            // With every condition met, and the pane already closed, the cleanup goes through and leaves the nested worktree's branch alone.
            Check(await ChildCleanup.CleanUp(child, merge, place.Repo, null) is ChildCleanupOutcome.Cleaned && !Directory.Exists(child.WorktreePath) && await place.OnlyTheRepositoryIsListed()
                && await place.Git("for-each-ref", "--format=%(refname)", "refs/heads/") == "refs/heads/main\nrefs/heads/side" && await place.Git("rev-parse", "refs/heads/main") == merge.MergedCommit, "the worktree goes by plain removal and the branch by branch -d");
        }
        finally { place.Remove(); }

        // Discard removes what a failed start left, and never a folder that is not the child's place.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var maker = place.Maker;
            var first = (await maker.Check(place.Repo) as ChildWorktreeCheck.Ready)?.Base ?? throw new InvalidOperationException("the workspace cannot host a child");
            Check(DelegationPaths.Same(DelegationPaths.Canonical(first.Repository), DelegationPaths.Canonical(place.Repo)) && !first.Repository.Contains('/') && first is { Prefix: "", ParentBranch: "main" }, "the repository's top folder is read the Windows way");
            ChildRecord Failed(string id) => ChildRecord.Make(id, "parent", maker.WorktreePath(id), "main", first.BaseCommit, "plan", "key-" + id, state: ChildState.Failed, parentCheckout: place.Repo);
            // f1: its place was taken, so `git worktree add` failed.
            var taken = Path.Combine(place.Root, "f1"); Directory.CreateDirectory(taken); File.WriteAllText(Path.Combine(taken, "junk.txt"), "junk\n");
            Check(await maker.Make("f1", first, "Task\n") is ChildWorktreeOutcome.Failed, "f1 was not made over a folder in its place");
            // f2: its worktree was made but not its notes, since the workspace has a folder of that name.
            await place.Commit(place.Repo, ChildWorktree.NotesFolder + "/keep", "keep\n");
            var second = (await maker.Check(place.Repo) as ChildWorktreeCheck.Ready)!.Base;
            Check(await maker.Make("f2", second, "Task\n") is ChildWorktreeOutcome.Failed && Directory.Exists(maker.WorktreePath("f2")) && !File.Exists(ChildWorktree.TaskFileIn(maker.WorktreePath("f2"))), "f2's notes never replace a folder the repository has");
            // f3: a start killed partway, its worktree still locked while being made and its folder gone.
            await place.Git("worktree", "add", "-q", "-b", "mighty/f3", maker.WorktreePath("f3"), "main"); await place.Git("worktree", "lock", "--reason", "initializing", maker.WorktreePath("f3"));
            RemoveFolder(maker.WorktreePath("f3"));
            // f4: a start that failed before it made anything. f5: a leftover with a read-only file, which Windows does not delete as it is.
            Directory.CreateDirectory(maker.WorktreePath("f5")); File.WriteAllText(Path.Combine(maker.WorktreePath("f5"), "locked.txt"), "read only\n"); File.SetAttributes(Path.Combine(maker.WorktreePath("f5"), "locked.txt"), FileAttributes.ReadOnly);
            var main = await place.Git("rev-parse", "refs/heads/main");
            foreach (var id in new[] { "f1", "f2", "f3", "f4", "f5" }) Same(await ChildCleanup.Discard(Failed(id), place.Repo, place.Root), new ChildDiscardOutcome.Discarded(), "discard removes what the failed start of " + id + " left");
            // Nothing of them is left: no branch, no registration, no folder under the root; the parent's branch is untouched.
            Check((await place.MightyBranches()).Length == 0 && await place.OnlyTheRepositoryIsListed() && Directory.GetFileSystemEntries(place.Root).Length == 0 && await place.Git("rev-parse", "refs/heads/main") == main
                && (await place.Git("status", "--porcelain", "--untracked-files=all")).Length == 0, "nothing of them is left");
            // A folder that is not the child's place under the worktree root is never removed.
            var elsewhere = Path.Combine(place.Base, "elsewhere", "f6"); Directory.CreateDirectory(elsewhere); File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "mine\n");
            foreach (var outside in new[] { Failed("f6") with { WorktreePath = elsewhere }, Failed("f6") with { WorktreePath = place.Root }, Failed("f6") with { WorktreePath = Path.Combine(place.Root, "f6", "..", "..", "elsewhere", "f6") },
                ChildRecord.Make("..", "parent", Path.Combine(place.Root, ".."), "main", first.BaseCommit, "plan", "k", state: ChildState.Failed, parentCheckout: place.Repo) })
                Check(await ChildCleanup.Discard(outside, place.Repo, place.Root) is ChildDiscardOutcome.Failed { Message: var why } && why == Locale.Get("delegation.card.error.worktreeMisplaced"), "a folder outside the child's place is never removed");
            Check(File.ReadAllText(Path.Combine(elsewhere, "keep.txt")) == "mine\n" && Directory.Exists(place.Root), "the other folder is as it was");
            // The child's place written another way is still its place.
            Directory.CreateDirectory(maker.WorktreePath("f7"));
            Check(await ChildCleanup.Discard(Failed("f7") with { WorktreePath = maker.WorktreePath("f7").ToUpperInvariant().Replace('\\', '/') }, place.Repo, place.Root + "\\") is ChildDiscardOutcome.Discarded && !Directory.Exists(maker.WorktreePath("f7")), "the child's place written another way is still its place");
            Check(await ChildCleanup.Discard(Failed("f8"), Path.Combine(place.Base, "missing"), place.Root) is ChildDiscardOutcome.Failed { Message: var unreachable } && unreachable == Locale.Get("delegation.card.error.parentCheckoutUnreachable"), "a parent's folder that is gone fails the discard");
        }
        finally { place.Remove(); }

        // Removing a workspace: refused while it has an open child or a child worktree not cleaned up.
        ChildRecord Removal(string id, string parent, ChildState state, string? checkout = @"C:\work\app") => ChildRecord.Make(id, parent, @"C:\worktrees\" + id, "main", Commit, "default", "key-" + id, state: state, parentCheckout: checkout);
        static HashSet<string> Panes(params string[] ids) => [.. ids];
        foreach (var state in Enum.GetValues<ChildState>().Where(state => state.IsOpen()))
        {
            // By the parent's pane, the child's own pane, or the parent's checkout alone.
            Check(DelegationWorkspaceRemoval.Refusal([Removal("c1", "p1", state, null)], Panes("p1"), @"C:\work\app", _ => false) == DelegationReasonCode.WorkspaceHasChildren
                && DelegationWorkspaceRemoval.Refusal([Removal("c1", "gone", state, null)], Panes("c1"), @"C:\work\app", _ => false) == DelegationReasonCode.WorkspaceHasChildren
                && DelegationWorkspaceRemoval.Refusal([Removal("c1", "gone", state)], Panes(), @"C:\work\app", _ => false) == DelegationReasonCode.WorkspaceHasChildren
                && DelegationWorkspaceRemoval.Refusal([Removal("c1", "gone", state, @"c:/WORK/app/sub")], Panes(), @"C:\work\app\", _ => false) == DelegationReasonCode.WorkspaceHasChildren, "a workspace with an open child is refused: " + state);
        }
        var onDisk = new HashSet<string> { @"C:\worktrees\c1" };
        foreach (var state in new[] { ChildState.Closed, ChildState.Failed })
            Check(DelegationWorkspaceRemoval.Refusal([Removal("c1", "p1", state)], Panes(), @"C:\work\app", onDisk.Contains) == DelegationReasonCode.WorkspaceHasChildren, "a child worktree not cleaned up refuses the removal");
        // A discarded child never counts; a cleaned one's worktree is gone.
        Check(DelegationWorkspaceRemoval.Refusal([Removal("c1", "p1", ChildState.Discarded)], Panes("p1"), @"C:\work\app", onDisk.Contains) is null, "a discarded child never counts");
        onDisk.Clear();
        Check(DelegationWorkspaceRemoval.Refusal([Removal("c1", "p1", ChildState.Closed)], Panes("p1"), @"C:\work\app", onDisk.Contains) is null && DelegationWorkspaceRemoval.Refusal([Removal("c1", "p1", ChildState.Failed)], Panes("p1"), @"C:\work\app", onDisk.Contains) is null, "a cleaned child's worktree is gone");
        // Another workspace's children never refuse the removal.
        ChildRecord[] others = [Removal("c1", "p1", ChildState.Running, @"C:\work\other"), Removal("c2", "p2", ChildState.Closed, @"C:\work\apple")];
        Check(DelegationWorkspaceRemoval.Refusal(others, Panes("p3"), @"C:\work\app", _ => true) is null && DelegationWorkspaceRemoval.Refusal(others, Panes("p2"), @"C:\work\app", _ => true) == DelegationReasonCode.WorkspaceHasChildren, "another workspace's children never refuse the removal");
    }

    /// <summary>What Windows needs of its own: paths, line endings, long paths, and a folder another program holds open.</summary>
    internal static async Task WindowsPathsLineEndingsAndOpenHandles()
    {
        // Paths: full, one kind of separator, no closing one, no regard to case; as far as they exist, their final names.
        var folder = Verification.Temp();
        try
        {
            var real = Path.Combine(folder, "Real Folder With A Long Name"); Directory.CreateDirectory(real);
            Check(DelegationPaths.Standardized(real.Replace('\\', '/') + "//") == real && DelegationPaths.Standardized(Path.Combine(real, "..", "x", ".")) == Path.Combine(folder, "x") && DelegationPaths.Standardized("\0") == "\0", "a path is standardized");
            Check(DelegationPaths.Same(DelegationPaths.Canonical(real.ToUpperInvariant()), DelegationPaths.Canonical(real)) && DelegationPaths.Canonical(real.ToUpperInvariant().Replace('\\', '/')).EndsWith("Real Folder With A Long Name", StringComparison.Ordinal), "the final name is the folder's own spelling");
            Check(DelegationPaths.Canonical(Path.Combine(real.ToLowerInvariant(), "Not", "There")) is var missing && missing.EndsWith(Path.Combine("Real Folder With A Long Name", "Not", "There"), StringComparison.Ordinal), "a path is resolved as far as it exists: " + missing);
            Check(DelegationPaths.IsInside(Path.Combine(real, "in"), real.ToUpperInvariant()) && !DelegationPaths.IsInside(real + "2", real) && !DelegationPaths.IsInside(real, real) && DelegationPaths.IsInside(@"C:\x", @"C:\"), "inside means below, not beside");
            if (OperatingSystem.IsWindows())
            {
                // A junction to the folder and its 8.3 short name (where the volume keeps one) are the same folder, as git lists it.
                var link = Path.Combine(folder, "link");
                await using (var made = ChildProcess.Start(ChildProcess.StartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/d", "/c", "mklink", "/J", link, real], folder))) { made.Input.Close(); await made.Completion.WaitAsync(TimeSpan.FromSeconds(30)); }
                Check(Directory.Exists(link) && DelegationPaths.Same(DelegationPaths.Canonical(Path.Combine(link, "sub")), Path.Combine(DelegationPaths.Canonical(real), "sub")), "a junction resolves to the folder it names");
                Directory.Delete(link);
                var shortName = Path.Combine(folder, "REALFO~1");
                if (Directory.Exists(shortName)) Check(DelegationPaths.Same(DelegationPaths.Canonical(shortName), DelegationPaths.Canonical(real)), "a short 8.3 name resolves to the long one");
            }
        }
        finally { RemoveFolder(folder); }

        // Line endings: with core.autocrlf on, a fresh worktree reads as unchanged, the notes never count, and merge, cleanup and discard decide as with it off.
        var place = new DelegationPlace();
        try
        {
            await place.Repository(1, autocrlf: "true");
            // Committed with LF, with CRLF, and mixed; git writes each out through the user's setting.
            await place.Commit(place.Repo, "lf.txt", "one\ntwo\n");
            File.WriteAllBytes(Path.Combine(place.Repo, "crlf.txt"), Utf8("one\r\ntwo\r\n")); await place.Git("-c", "core.autocrlf=false", "add", "crlf.txt");
            File.WriteAllBytes(Path.Combine(place.Repo, "mixed.txt"), Utf8("one\r\ntwo\nthree\r\n")); await place.Git("-c", "core.autocrlf=false", "add", "mixed.txt");
            await place.Git("commit", "-q", "-m", "line endings"); await place.Git("reset", "-q", "--hard");
            var task = "Fix it.\r\nKeep these endings.\nAnd these.\n";
            var made = await place.Maker.Create("eol", place.Repo, task) as ChildWorktreeOutcome.Created ?? throw new InvalidOperationException("the child was not made");
            var worktree = made.Worktree.WorktreePath;
            Check(File.ReadAllBytes(Path.Combine(worktree, "lf.txt")).SequenceEqual(Utf8("one\r\ntwo\r\n")), "git wrote the checkout through core.autocrlf");
            Check(File.ReadAllBytes(made.Worktree.TaskFile).SequenceEqual(Utf8(task)) && File.ReadAllBytes(Path.Combine(worktree, ChildWorktree.NotesFolder, ".gitignore")).SequenceEqual(Utf8("*\n")), "the app's own files are written byte for byte");
            // Git compares what it wrote a moment ago by content, not by its stamp, once the stamps are stale.
            foreach (var name in new[] { "lf.txt", "crlf.txt", "mixed.txt", "file-0.txt" }) File.SetLastWriteTimeUtc(Path.Combine(worktree, name), DateTime.UtcNow.AddMinutes(-7));
            Check(await ChildMerge.HasTrackedChanges(worktree) == false && await ChildMerge.HasTrackedChanges(place.Repo) == false, "a fresh worktree is not changed by its line endings");
            Check((await DelegationGit.Run(["status", "--porcelain", "--ignore-submodules=none", "--untracked-files=normal"], worktree))?.Stdout.Length == 0, "TASK.md, REPORT.md and .gitignore are no changes");
            var child = ChildRecord.Make("eol", "parent", worktree, "main", made.Worktree.BaseCommit, "acceptEdits", "key", state: ChildState.Running, parentCheckout: place.Repo);
            File.WriteAllBytes(Path.Combine(worktree, "work.txt"), Utf8("work\r\nmore\r\n")); await place.GitIn(worktree, "add", "--", "work.txt"); await place.GitIn(worktree, "commit", "-q", "-m", "work");
            var head = await place.GitIn(worktree, "rev-parse", "HEAD");
            // The report is hashed byte for byte: the same words with other line endings are another report.
            File.WriteAllBytes(made.Worktree.ReportFile, Utf8("Done.\r\nAll of it.\r\n"));
            var windows = ChildReportFile.Read(made.Worktree.ReportFile)!;
            File.WriteAllBytes(made.Worktree.ReportFile, Utf8("Done.\nAll of it.\n"));
            var unix = ChildReportFile.Read(made.Worktree.ReportFile)!;
            Check(windows.Digest == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Utf8("Done.\r\nAll of it.\r\n"))) && windows.Digest != unix.Digest && windows.TotalBytes == 19 && unix.TotalBytes == 17, "the report's digest is of its bytes");
            child = child.RecordingReport(head)!;
            var merge = Merged(await ChildMerge.ToolMerge(child, head, place.Repo)); Check(File.ReadAllBytes(Path.Combine(place.Repo, "work.txt")).SequenceEqual(Utf8("work\r\nmore\r\n")) && await ChildMerge.HasTrackedChanges(place.Repo) == false, "the merge leaves the parent's checkout unchanged in git's eyes");
            // An undo and a merge again through the card, and then the cleanup on close: every decision rests on an empty status.
            var merged = child.Applying(ChildStateEvent.Merge)!;
            Check(await ChildMerge.Undo(merge, merged, place.Repo, null) is ChildUndoOutcome.Undone && !File.Exists(Path.Combine(place.Repo, "work.txt")) && await ChildMerge.HasTrackedChanges(place.Repo) == false, "the undo puts the files back");
            var again = Merged(await ChildMerge.CardMerge(child, place.Repo)); Check(await ChildCleanup.CleanUp(merged, again, place.Repo, null) is ChildCleanupOutcome.Cleaned && !Directory.Exists(worktree), "the cleanup removes the worktree with core.autocrlf on");
        }
        finally { place.Remove(); }

        // Long paths: a child's worktree sits deeper than its parent's checkout, so a file that fits there may pass 260 characters here.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var id = "long-" + new string('p', 60); var parts = new List<string>(); var budget = 250 - place.Repo.Length - 1 - 4;
            for (; budget > 60; budget -= 51) parts.Add(new string('d', 50));
            var name = string.Join('/', parts.Append(new string('f', Math.Max(1, budget)) + ".txt"));
            Check(Path.Combine(place.Repo, name).Length < 260 && Path.Combine(place.Root, id, name).Length > 260, "the file fits in the parent's checkout and not in the child's without long paths");
            await place.Git("config", "core.longpaths", "false");
            await place.Commit(place.Repo, name, "deep\n");
            var (child, head) = await place.ReportedChild(id, ["work.txt"], place.Repo);
            Check(File.ReadAllText(Path.Combine(child.WorktreePath, name)) == "deep\n" && await ChildMerge.HasTrackedChanges(child.WorktreePath) == false, "the long file is checked out in the child's worktree");
            var merge = Merged(await ChildMerge.ToolMerge(child, head, place.Repo)); Check(await ChildCleanup.CleanUp(child.Applying(ChildStateEvent.Merge)!, merge, place.Repo, null) is ChildCleanupOutcome.Cleaned && !Directory.Exists(child.WorktreePath), "a worktree with a long path is merged and cleaned up");
            var (second, _) = await place.ReportedChild(id + "2", ["more.txt"], place.Repo);
            Check(await ChildCleanup.Discard(second, place.Repo, place.Root) is ChildDiscardOutcome.Discarded && !Directory.Exists(second.WorktreePath) && (await place.MightyBranches()).Length == 0, "and discarded");
        }
        finally { place.Remove(); }

        // A folder another program holds open: the discard says so, removes nothing after that step, and goes on when tried again.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var (child, head) = await place.ReportedChild("held", parentCheckout: place.Repo);
            File.WriteAllText(Path.Combine(child.WorktreePath, "unsaved.txt"), "not committed\n");
            using (new FileStream(Path.Combine(child.WorktreePath, "held.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(await ChildCleanup.Discard(child, place.Repo, place.Root) is ChildDiscardOutcome.Failed { Message.Length: > 0 }, "a discard that Windows refuses is a failure, in git's or Windows' words");
                Check(File.Exists(Path.Combine(child.WorktreePath, "held.txt")) && await place.Git("rev-parse", "refs/heads/mighty/held") == head, "the file held open and the child's branch are still there");
                // Tried again while still held: still an honest failure, and still the branch.
                Check(await ChildCleanup.Discard(child, place.Repo, place.Root) is ChildDiscardOutcome.Failed && Directory.Exists(child.WorktreePath) && await place.Git("rev-parse", "refs/heads/mighty/held") == head, "a second try while it is held fails the same way");
            }
            Check(await ChildCleanup.Discard(child, place.Repo, place.Root) is ChildDiscardOutcome.Discarded && !Directory.Exists(child.WorktreePath) && (await place.MightyBranches()).Length == 0 && await place.OnlyTheRepositoryIsListed(), "once the other program lets go, the discard goes on and finishes");
        }
        finally { place.Remove(); }

        // Delegation's git: the user's settings, but no hooks, no prompts, no inherited GIT_* variables, bounded output, and a time limit.
        place = new DelegationPlace();
        try
        {
            await place.Repository();
            var hook = Path.Combine(place.Repo, ".git", "hooks", "post-checkout"); Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            File.WriteAllText(hook, "#!/bin/sh\necho ran > \"$(git rev-parse --git-common-dir)/hook-ran\"\n");
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(place.Base, "nowhere")); Environment.SetEnvironmentVariable("GIT_WORK_TREE", place.Base);
            try
            {
                Check(await place.Maker.Create("hooked", place.Repo, "t") is ChildWorktreeOutcome.Created && !File.Exists(Path.Combine(place.Repo, ".git", "hook-ran")), "inherited GIT_* variables are dropped and no hook runs");
            }
            finally { Environment.SetEnvironmentVariable("GIT_DIR", null); Environment.SetEnvironmentVariable("GIT_WORK_TREE", null); }
            Check(await DelegationGit.Run(["rev-parse", "--is-inside-work-tree"], place.Repo) is { ExitCode: 0 } inside && DelegationGit.Line(inside.Stdout) == "true" && inside.Stderr.Length == 0, "git runs in the folder it is given");
            Check(await DelegationGit.Run(["rev-parse", "--verify", "no-such-ref"], place.Repo) is { ExitCode: not 0 } failed && DelegationGit.Message(failed).StartsWith("fatal:", StringComparison.Ordinal) && failed.Stdout.Length == 0, "a failure carries git's own words, apart from its output");
            Check(await DelegationGit.Run(["status"], Path.Combine(place.Base, "missing")) is null && await DelegationGit.Run(["cat-file", "--batch"], place.Repo, 60) is { ExitCode: 0 }
, "a folder that is gone starts no git, and stdin is closed");
            // A command past its time is stopped with everything it started, and is no answer.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Check(await DelegationGit.Run(["-c", "alias.wait=!sleep 20", "wait"], place.Repo, 1) is null && clock.Elapsed.TotalSeconds < 15, "a command past its time is no answer");
            File.WriteAllBytes(Path.Combine(place.Repo, "big.txt"), Utf8(string.Concat(Enumerable.Repeat("0123456789abcdef\n", 70_000))));
            Check(await DelegationGit.Run(["--no-pager", "diff", "--no-index", "--", "file-0.txt", "big.txt"], place.Repo) is null, "more than 1 MiB of output is no answer");
            Check(DelegationGit.IsObjectName(new string('a', 40)) && DelegationGit.IsObjectName(new string('F', 64)) && !DelegationGit.IsObjectName(new string('a', 39)) && !DelegationGit.IsObjectName(new string('g', 40)) && !DelegationGit.IsObjectName("HEAD"), "an object name is 40 or 64 hex digits");
        }
        finally { place.Remove(); }
    }
}
