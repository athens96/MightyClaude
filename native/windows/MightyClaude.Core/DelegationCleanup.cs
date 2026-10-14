using System.ComponentModel;
using System.IO.Enumeration;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// Why closing a merged child's pane kept its worktree and branch (M/DelegationCleanup.swift:3-26). Nothing on disk changed:
/// only the pane closed, and a human may still discard the child from its card.
/// </summary>
[JsonConverter(typeof(DelegationNameConverter<ChildCleanupHold>))]
public enum ChildCleanupHold
{
    /// <summary>The child is not merged, or the merge record is another child's.</summary>
    NotMerged,
    /// <summary>A run is going in the child's pane.</summary>
    ChildBusy,
    /// <summary>The child's worktree is gone, or is no worktree of the parent's repository.</summary>
    WorktreeMissing,
    /// <summary>The worktree is not on the child's branch at the head that was merged.</summary>
    HeadMoved,
    /// <summary>Tracked files in the worktree differ from its head, staged or not.</summary>
    TrackedChanges,
    /// <summary>The worktree holds files that are neither tracked nor ignored.</summary>
    UntrackedFiles,
    /// <summary>Another registered worktree lies inside the child's.</summary>
    NestedWorktree,
    /// <summary>The parent's checkout does not have the recorded parent branch checked out.</summary>
    BranchNotCheckedOut,
    /// <summary>The recorded parent branch no longer holds the child's head, so <c>git branch -d</c> would refuse.</summary>
    NotInParent,
    /// <summary>
    /// A directory junction or directory symbolic link lies inside the worktree, an ignored <c>node_modules</c> among others.
    /// Windows only: a git that descends one while removing the worktree empties the folder it points to, and one that skips it
    /// leaves the worktree behind. File links, cloud placeholders and deduplicated files are no such link: git never descends them.
    /// </summary>
    Link,
}

/// <summary>The answer to a human closing a delegated child's pane (M/DelegationCleanup.swift:28-38).</summary>
public abstract record ChildCleanupOutcome
{
    /// <summary>The worktree went by plain <c>git worktree remove</c> and the branch by <c>git branch -d</c>.</summary>
    public sealed record Cleaned : ChildCleanupOutcome;
    /// <summary>A condition did not hold, so only the pane closed: nothing on disk changed.</summary>
    public sealed record Kept(ChildCleanupHold Hold) : ChildCleanupOutcome;
    /// <summary>
    /// The copies could not be saved, or git was missing or failed. Git refuses before it deletes anything, and the branch goes
    /// last, so the child's work stays on disk or on its branch. On Windows a file another program holds open can stop git
    /// partway through the worktree's folder: what is left of the folder stays for a human's discard, and the branch stays too.
    /// </summary>
    public sealed record Failed(string Message) : ChildCleanupOutcome;
}

/// <summary>The answer to a human's discard of a delegated child from its card (M/DelegationCleanup.swift:40-48).</summary>
public abstract record ChildDiscardOutcome
{
    /// <summary>The child's worktree, every worktree nested in it, a leftover folder at its place and its branch are gone, whichever of them existed.</summary>
    public sealed record Discarded : ChildDiscardOutcome;
    /// <summary>
    /// Nothing after this step was removed; a later discard goes on from here. The words are in the app's language, with git's
    /// and Windows' own output kept as it is.
    /// </summary>
    public sealed record Failed(string Message) : ChildDiscardOutcome;
}

/// <summary>
/// Removes a delegated child's worktree and branch (M/DelegationCleanup.swift:50-190): by cleanup when a human closes a merged
/// child's pane, which never forces anything, and by a human's discard, the only path that force-removes unmerged work.
/// </summary>
public static class ChildCleanup
{
    /// <summary>The longest one git step may take. A human waits on these, not a tool call.</summary>
    public const double GitTimeout = 120;

    /// <summary>
    /// The cleanup of a merged child whose pane a human closed, after its copies are stored. <paramref name="record"/> is the
    /// child's merge, <paramref name="parentCheckout"/> the parent's side and <paramref name="childActivity"/> the child pane's
    /// (null once it is closed, which counts as idle). The first rule that applies keeps everything, and every rule is checked
    /// before anything is removed:
    /// 1. <c>not_merged</c>: the child is not merged, or <paramref name="record"/> is not its merge.
    /// 2. <c>child_busy</c>: a run is going in the child's pane.
    /// 3. <c>worktree_missing</c>: the worktree is gone, or the parent's repository does not list it.
    /// 4. <c>branch_not_checked_out</c>: the parent's checkout does not have the recorded parent branch checked out.
    /// 5. <c>head_moved</c>: the worktree is not on the child's branch at the head that was merged.
    /// 6. <c>tracked_changes</c>: tracked files in the worktree differ from its head.
    /// 7. <c>untracked_files</c>: the worktree holds files no ignore rule covers. Ignored files, TASK.md and REPORT.md among them, go with the worktree.
    /// 8. <c>nested_worktree</c>: another registered worktree lies inside it; an ignored one would otherwise go with the folder, its own work included.
    /// 9. <c>not_in_parent</c>: the parent branch no longer holds the child's head.
    /// Otherwise the worktree goes by plain <c>git worktree remove</c>, which checks for changes again itself, and then the
    /// branch by <c>git branch -d</c> from the parent's side. Never <c>--force</c>.
    /// </summary>
    public static async Task<ChildCleanupOutcome> CleanUp(ChildRecord child, MergeRecord record, string parentCheckout, DelegationPaneActivity? childActivity)
    {
        static ChildCleanupOutcome Kept(ChildCleanupHold hold) => new ChildCleanupOutcome.Kept(hold);
        static ChildCleanupOutcome Failed(string message) => new ChildCleanupOutcome.Failed(message);
        var head = record.ChildHead.ToLowerInvariant();
        if (child.State != ChildState.Merged || record.ChildId != child.Id || !DelegationGit.IsObjectName(head)) return Kept(ChildCleanupHold.NotMerged);
        if (childActivity == DelegationPaneActivity.Running) return Kept(ChildCleanupHold.ChildBusy);
        if (!ChildMerge.IsFolder(child.WorktreePath)) return Kept(ChildCleanupHold.WorktreeMissing);
        if (DelegationGit.Executable is null) return Failed("Git is missing.");
        if (await ChildMerge.CheckedOutHead(record.ParentBranch, parentCheckout) is not { } parentHead) return Kept(ChildCleanupHold.BranchNotCheckedOut);
        if (await Worktrees(parentCheckout) is not { } listed) return Failed("Git could not list the worktrees.");
        var own = DelegationPaths.Canonical(child.WorktreePath);
        if (!listed.Any(path => DelegationPaths.Same(path, own))) return Kept(ChildCleanupHold.WorktreeMissing);

        var worktree = child.WorktreePath;
        if (await DelegationGit.Run(["symbolic-ref", "-q", "HEAD"], worktree) is not { } branch) return Failed("Git did not finish.");
        if (branch.ExitCode != 0 || DelegationGit.Line(branch.Stdout) != "refs/heads/" + child.Branch || await ChildMerge.Head(worktree) != head) return Kept(ChildCleanupHold.HeadMoved);
        switch (await ChildMerge.HasTrackedChanges(worktree))
        {
            case null: return Failed("Git could not read the child's changes.");
            case true: return Kept(ChildCleanupHold.TrackedChanges);
        }
        // What plain `git worktree remove` checks itself: with tracked files clean, anything listed is a file no ignore rule covers.
        if (await DelegationGit.Run(["status", "--porcelain", "--ignore-submodules=none", "--untracked-files=normal"], worktree) is not { ExitCode: 0 } status) return Failed("Git could not read the child's files.");
        if (status.Stdout.Length > 0) return Kept(ChildCleanupHold.UntrackedFiles);
        if (listed.Any(path => DelegationPaths.IsInside(path, own))) return Kept(ChildCleanupHold.NestedWorktree);
        if (await LinkHold(worktree) is { } held) return held;
        if (await DelegationGit.Run(["merge-base", "--is-ancestor", head, parentHead], parentCheckout) is not { } merged) return Failed("Git did not finish.");
        if (merged.ExitCode == 1) return Kept(ChildCleanupHold.NotInParent);
        if (merged.ExitCode != 0) return Failed(DelegationGit.Message(merged));

        // Looked for again right before git: a link made since the first look keeps everything too.
        if (await LinkHold(worktree) is { } late) return late;
        if (await DelegationGit.Run(["worktree", "remove", worktree], parentCheckout, GitTimeout) is not { } removed) return Failed("Git did not finish.");
        if (removed.ExitCode != 0) return Failed(DelegationGit.Message(removed));
        // Git may leave the folder and still exit 0 (one it would not descend, a file held open): then the branch stays as well.
        if (Directory.Exists(worktree) || File.Exists(worktree)) return Failed("Git left the child's worktree folder in place, so the branch stays.");
        if (await DelegationGit.Run(["branch", "-d", child.Branch], parentCheckout) is not { } deleted) return Failed("Git did not finish; the branch stays.");
        if (deleted.ExitCode != 0) return Failed(DelegationGit.Message(deleted));
        return new ChildCleanupOutcome.Cleaned();
    }

    /// <summary><c>link</c> when a directory link lies inside <paramref name="worktree"/>, looked for off the caller's thread up to the first one; null when none does.</summary>
    private static async Task<ChildCleanupOutcome?> LinkHold(string worktree)
    {
        try { return await Task.Run(() => HasLinks(worktree)) ? new ChildCleanupOutcome.Kept(ChildCleanupHold.Link) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new ChildCleanupOutcome.Failed(ex.Message); }
    }

    /// <summary>The registered worktrees inside the child's worktree, deepest first, for the discard confirmation to name; null when git failed.</summary>
    public static async Task<IReadOnlyList<string>?> NestedWorktrees(ChildRecord child, string parentCheckout) =>
        await Worktrees(parentCheckout) is { } listed ? Nested(listed, DelegationPaths.Canonical(child.WorktreePath)) : null;

    private static List<string> Nested(IEnumerable<string> listed, string own) => listed.Where(path => DelegationPaths.IsInside(path, own)).OrderByDescending(path => path.Length).ToList();

    /// <summary>
    /// A human's discard from the child's card, once its run is stopped and its report copy kept.
    /// <paramref name="parentCheckout"/> is the parent's side and <paramref name="worktreeRoot"/> the root children's worktrees
    /// are made under. A child whose recorded worktree is not its place <c>&lt;root&gt;\&lt;child id&gt;</c> is refused before
    /// anything is removed. Otherwise, in this order, each whichever exists:
    /// 1. every registered worktree nested in the child's, deepest first, then the child's own, each by
    ///    <c>git worktree remove --force --force</c> (twice, so a worktree a killed start left locked goes too; one whose folder
    ///    is already gone loses its registration);
    /// 2. a folder git does not know at the child's place, as a failed start may leave it;
    /// 3. the child's branch, by <c>git branch -D</c>: it may hold unmerged work.
    /// The parent's branch and checkout, and the branches of nested worktrees, are never touched. Windows does not delete a
    /// folder while a program holds a file in it open: the step then fails with what git or Windows said, nothing after it is
    /// removed, and a later discard goes on with what is left (git drops a worktree's registration even when it could not empty
    /// its folder, and step 2 then takes the rest).
    /// </summary>
    public static async Task<ChildDiscardOutcome> Discard(ChildRecord child, string parentCheckout, string worktreeRoot)
    {
        static ChildDiscardOutcome Failed(string message) => new ChildDiscardOutcome.Failed(message);
        // Only the child's own place is ever forced away, never a folder a record names elsewhere.
        var own = DelegationPaths.Canonical(child.WorktreePath);
        if (!ChildWorktreeMaker.IsSafeSessionId(child.Id) || !DelegationPaths.Same(own, DelegationPaths.Canonical(Path.Combine(worktreeRoot, child.Id)))) return Failed(Locale.Get("delegation.card.error.worktreeMisplaced"));
        // Only the child's own branch is ever deleted by force.
        if (child.Branch != ChildRecord.BranchName(child.Id)) return Failed(Locale.Get("delegation.card.error.branchMisplaced"));
        if (DelegationGit.Executable is null) return Failed(Locale.Get("delegation.card.error.gitMissing"));
        if (await Worktrees(parentCheckout) is not { } listed) return Failed(Locale.Get("delegation.card.error.parentCheckoutUnreachable"));
        // A directory link inside goes first, by itself: git's forced remove must never reach what it points to. Looked for again
        // right before git, a link made meanwhile stops the discard with nothing removed by git.
        try
        {
            await Task.Run(() => { foreach (var link in Links(child.WorktreePath)) Unlink(link); });
            if (await Task.Run(() => HasLinks(child.WorktreePath))) return Failed(Locale.Get("delegation.card.error.linkAppeared"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed(ex.Message); }
        foreach (var path in Nested(listed, own).Concat(listed.Where(path => DelegationPaths.Same(path, own))))
        {
            if (await DelegationGit.Run(["worktree", "remove", "--force", "--force", path], parentCheckout, GitTimeout) is not { } removed) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
            if (removed.ExitCode != 0) return Failed(DelegationGit.Message(removed));
        }
        try { Remove(child.WorktreePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed(ex.Message); }
        // Windows may only have marked the folder for removal while another program still holds it open: it is not gone yet.
        if (Directory.Exists(child.WorktreePath) || File.Exists(child.WorktreePath)) return Failed(new Win32Exception(303).Message);
        if (await DelegationGit.Run(["rev-parse", "-q", "--verify", "refs/heads/" + child.Branch], parentCheckout) is not { ExitCode: 0 or 1 } found) return Failed(Locale.Get("delegation.card.error.childBranchUnread"));
        if (found.ExitCode == 0)
        {
            if (await DelegationGit.Run(["branch", "-D", child.Branch], parentCheckout) is not { } deleted) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
            if (deleted.ExitCode != 0) return Failed(DelegationGit.Message(deleted));
        }
        return new ChildDiscardOutcome.Discarded();
    }

    /// <summary>
    /// The directory junctions and directory symbolic links inside <paramref name="folder"/>, deepest first, found without
    /// following any of them; none when the folder is not there. A file link, a cloud placeholder or a deduplicated file is a
    /// reparse point git never descends, so it is not one. Throws what Windows said when a folder in it cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Links(string folder) => DirectoryLinks(folder).OrderByDescending(path => path.Length).ToList();

    /// <summary>Whether a directory link lies inside <paramref name="folder"/>: the walk stops at the first.</summary>
    public static bool HasLinks(string folder) => DirectoryLinks(folder).Any();

    private static IEnumerable<string> DirectoryLinks(string folder) => !Directory.Exists(folder) ? [] :
        new FileSystemEnumerable<string>(folder, (ref FileSystemEntry entry) => entry.ToFullPath(), new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None, IgnoreInaccessible = false })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => IsDirectoryLink(ref entry),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsDirectoryLink(ref entry),
        };

    /// <summary>A folder that is a junction or a symbolic link: Windows names a target for those two kinds of reparse point only.</summary>
    private static bool IsDirectoryLink(ref FileSystemEntry entry) =>
        entry.IsDirectory && entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && new DirectoryInfo(entry.ToFullPath()).LinkTarget is not null;

    /// <summary>Removes the junction or link at <paramref name="path"/> itself, never what it points to.</summary>
    private static void Unlink(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path, false); else File.Delete(path);
    }

    /// <summary>
    /// Removes the file or folder at <paramref name="path"/> with everything in it, if it is there. Windows refuses a read-only
    /// file, so those are made writable and the removal tried once more; a link or junction inside is removed, never followed.
    /// </summary>
    private static void Remove(string path)
    {
        if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return; }
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (UnauthorizedAccessException)
        {
            var readOnly = new FileSystemEnumerable<string>(path, (ref FileSystemEntry entry) => entry.ToFullPath(), new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None })
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.Attributes.HasFlag(FileAttributes.ReadOnly),
                ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
            };
            foreach (var entry in readOnly.ToList()) File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, true);
        }
    }

    /// <summary>The canonical paths of the worktrees registered in <paramref name="folder"/>'s repository, or null when git failed.</summary>
    public static async Task<IReadOnlyList<string>?> Worktrees(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.Contains('\0') || !ChildMerge.IsFolder(folder)
            || await DelegationGit.Run(["worktree", "list", "--porcelain", "-z"], folder) is not { ExitCode: 0 } list) return null;
        return list.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(entry => entry.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(entry => DelegationPaths.Canonical(entry[9..])).ToList();
    }
}

/// <summary>Whether a human may remove a workspace while delegation has children there (M/DelegationCleanup.swift:192-212).</summary>
public static class DelegationWorkspaceRemoval
{
    /// <summary>
    /// <c>workspace_has_children</c> when the workspace at <paramref name="folder"/>, with the panes
    /// <paramref name="paneIds"/>, still has a child that is open or whose worktree is still on disk (a closed child kept, or
    /// what a failed start left); null when it may go. A child is the workspace's when its pane or its parent's is one of
    /// <paramref name="paneIds"/>, or its parent's checkout is <paramref name="folder"/> or inside it. A discarded child's
    /// worktree is gone, so it never counts.
    /// </summary>
    public static DelegationReasonCode? Refusal(IEnumerable<ChildRecord> children, IReadOnlySet<string> paneIds, string folder, Func<string, bool>? worktreeExists = null)
    {
        worktreeExists ??= path => Directory.Exists(path) || File.Exists(path);
        var root = DelegationPaths.Canonical(folder);
        foreach (var child in children.Where(child => child.State != ChildState.Discarded))
        {
            var checkout = child.ParentCheckout is { } recorded ? DelegationPaths.Canonical(recorded) : null;
            var ours = paneIds.Contains(child.Id) || paneIds.Contains(child.ParentSessionId) || checkout is not null && (DelegationPaths.Same(checkout, root) || DelegationPaths.IsInside(checkout, root));
            if (ours && (child.State.IsOpen() || worktreeExists(child.WorktreePath))) return DelegationReasonCode.WorkspaceHasChildren;
        }
        return null;
    }
}

/// <summary>A human's close and discard of a child (M/DelegationCleanup.swift:214-313). The tools never reach these.</summary>
public sealed partial class DelegationCoordinator
{
    /// <summary><see cref="DelegationWorkspaceRemoval.Refusal"/> for the children in the delegation file.</summary>
    public DelegationReasonCode? WorkspaceRemovalRefusal(IReadOnlySet<string> paneIds, string folder) => DelegationWorkspaceRemoval.Refusal(File.Children, paneIds, folder);

    /// <summary>
    /// A human closed the child's pane, on the desktop or from the phone. An open child becomes closed and keeps its card. A
    /// merged one first has copies of its REPORT.md and TASK.md and its branch head stored in the delegation file, and only once
    /// they are saved is its worktree and branch cleaned up, when every rule of <see cref="ChildCleanup.CleanUp"/> holds.
    /// Otherwise nothing on disk changes. The merge record stays, so the merge can still be undone. <paramref name="stillRunning"/>:
    /// the host could not end the child's run before its pane went, so the child counts as busy whatever its pane reads.
    /// </summary>
    public Task<ChildCleanupOutcome> CloseChild(string id, bool stillRunning = false) => Isolated<ChildCleanupOutcome>(async () =>
    {
        if (File.Children.Find(child => child.Id == id) is not { } found) return new ChildCleanupOutcome.Failed("No child has this id.");
        var activity = stillRunning ? DelegationPaneActivity.Running : (await Host(() => host.PaneState(id)))?.Activity;
        var checkout = await ParentCheckout(found);
        string? head = null;
        if (found.State == ChildState.Merged) head = await BranchHead(found, checkout);
        var context = await PruneContext();
        // Nothing below suspends until the close is saved.
        var index = File.Children.FindIndex(child => child.Id == id);
        if (index < 0 || !File.Children[index].State.IsOpen()) return new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged);
        var child = File.Children[index];
        var merge = child.State == ChildState.Merged ? File.Merges.FindLast(record => record.ChildId == id) : null;
        var next = File;
        if (merge is not null)
        {
            foreach (var (kind, path, revision) in new[] { (DelegationCopyKind.Report, ChildWorktree.ReportFileIn(child.WorktreePath), child.ReportRevision), (DelegationCopyKind.Task, ChildWorktree.TaskFileIn(child.WorktreePath), 0) })
                if (DelegationCopy.TryRead(id, kind, revision, path) is { } copy) next = next.SettingCopy(copy);
            next = next.SettingChild(index, next.Children[index] with { ClosedHead = head });
        }
        next = next.SettingChild(index, next.Children[index].Applied(ChildStateEvent.ClosePane));
        try { Commit(next, context); }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex))
        {
            // Closed all the same; the next save writes it. Without the copies on disk, nothing is removed.
            File = next;
            return merge is null ? new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged) : new ChildCleanupOutcome.Failed("The delegation file could not be saved, so the child's worktree and branch were kept.");
        }
        if (merge is null) return new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged);
        if (checkout is null) return new ChildCleanupOutcome.Kept(ChildCleanupHold.BranchNotCheckedOut);
        return await ChildCleanup.CleanUp(child, merge, checkout, activity);
    });

    /// <summary>
    /// A human's discard of the child from its card, after a confirmation naming any nested worktrees
    /// (<see cref="ChildCleanup.NestedWorktrees"/>). It is the only path that removes unmerged work: the child's run is stopped,
    /// a copy of its REPORT.md is kept in the delegation file, and then <see cref="ChildCleanup.Discard"/> removes its worktree,
    /// the worktrees nested in it and its branch by force, with whatever a failed start left. While the child's start is still
    /// going the discard is refused with nothing changed. The child is discarded once its worktree and branch are gone; until
    /// then it keeps its state and card, and a discard may be tried again.
    /// </summary>
    public Task<ChildDiscardOutcome> DiscardChild(string id) => Isolated(() => DiscardChildNow(id));

    private async Task<ChildDiscardOutcome> DiscardChildNow(string id)
    {
        static ChildDiscardOutcome Failed(string key) => new ChildDiscardOutcome.Failed(Locale.Get(key));
        if (File.Children.Find(child => child.Id == id) is not { } found || found.State == ChildState.Discarded) return Failed("delegation.card.error.cannotDiscard");
        if (starting.Contains(id)) return Failed("delegation.card.error.stillStarting");
        await Host(() => host.StopRun(id));
        if (await ParentCheckout(found) is not { } checkout) return Failed("delegation.card.error.parentCheckoutUnknown");
        var context = await PruneContext();
        var index = File.Children.FindIndex(child => child.Id == id);
        if (index < 0 || File.Children[index].State == ChildState.Discarded) return Failed("delegation.card.error.cannotDiscard");
        var child = File.Children[index];
        var next = File;
        if (DelegationCopy.TryRead(id, DelegationCopyKind.Report, child.ReportRevision, ChildWorktree.ReportFileIn(child.WorktreePath)) is { OriginalBytes: > 0 } copy) next = next.SettingCopy(copy);
        try { Commit(next, context); } catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return Failed("delegation.card.error.notSaved"); }

        var outcome = await ChildCleanup.Discard(child, checkout, worktrees.Root);
        if (outcome is not ChildDiscardOutcome.Discarded) return outcome;
        var after = await PruneContext();
        var doneIndex = File.Children.FindIndex(record => record.Id == id);
        if (doneIndex >= 0 && File.Children[doneIndex].Applying(ChildStateEvent.Discard) is { } discarded) SaveOrKeep(File.SettingChild(doneIndex, discarded), after);
        return outcome;
    }

    /// <summary>Where git runs on the parent's side: the checkout recorded when the child was made, else the parent pane's folder while it is open.</summary>
    private async Task<string?> ParentCheckout(ChildRecord child) => child.ParentCheckout ?? (await Host(() => host.PaneState(child.ParentSessionId)))?.Folder;

    /// <summary>The head of the child's branch, read on the parent's side or else in its worktree; null when neither has it.</summary>
    private static async Task<string?> BranchHead(ChildRecord child, string? parentCheckout)
    {
        foreach (var folder in new[] { parentCheckout, child.WorktreePath }.OfType<string>().Where(ChildMerge.IsFolder))
        {
            if (await DelegationGit.Run(["rev-parse", "-q", "--verify", $"refs/heads/{child.Branch}^{{commit}}"], folder) is not { ExitCode: 0 } found) continue;
            var sha = DelegationGit.Line(found.Stdout);
            if (DelegationGit.IsObjectName(sha)) return sha;
        }
        return null;
    }
}
