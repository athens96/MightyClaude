using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;

namespace MightyCode.Core;

/// <summary>
/// A workspace's agent pane that works in its own git worktree instead of the workspace folder (M/PaneWorktree.swift; the rules
/// are docs/devtool-features.md §12 "옮길 규칙"), so two agents of one workspace work at once without writing over each other's
/// files. It has no parent: it is made with the same rules and in the same place as a delegated child's worktree
/// (<see cref="ChildWorktreeMaker.MakePane"/>, branch <c>mighty/&lt;session id&gt;</c> at the workspace branch's head), and its run
/// folder is the session's <see cref="RunSession.WorkingFolder"/> like a child's (<see cref="DelegationPanes.RunWorkspace"/>).
/// </summary>
public sealed record PaneWorktree
{
    /// <summary>The worktree's top folder, <c>&lt;root&gt;\&lt;session id&gt;</c>.</summary>
    [JsonPropertyName("worktreePath")] public string WorktreePath { get; init; } = "";
    [JsonPropertyName("branch")] public string Branch { get; init; } = "";
    /// <summary>The workspace folder's branch and head when the worktree was made: the branch a merge goes into.</summary>
    [JsonPropertyName("baseBranch")] public string BaseBranch { get; init; } = "";
    [JsonPropertyName("baseCommit")] public string BaseCommit { get; init; } = "";
    /// <summary>The top folder of the workspace's checkout, where the branch lives and where merge and discard run git.</summary>
    [JsonPropertyName("checkout")] public string Checkout { get; init; } = "";
    /// <summary>The <c>PORT</c> every run of the pane gets, so two panes' dev servers do not collide.</summary>
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("setupCommand")] public string? SetupCommand { get; init; }
    /// <summary><c>running</c>, <c>done</c> or <c>failed</c>; null without a setup command.</summary>
    [JsonPropertyName("setupStatus")] public string? SetupStatus { get; init; }
    [JsonPropertyName("setupExitCode")] public int? SetupExitCode { get; init; }
    /// <summary>The commit the workspace branch moved to by the last merge from the pane; null before one.</summary>
    [JsonPropertyName("mergedCommit")] public string? MergedCommit { get; init; }
    /// <summary>The workspace branch's head before that merge: where an undo puts it back.</summary>
    [JsonPropertyName("preMergeCommit")] public string? PreMergeCommit { get; init; }
    /// <summary>The folder the pane's runs work in: the worktree, or the folder in it that matches a workspace that is a subfolder of its repository.</summary>
    [JsonPropertyName("workingFolder")] public string? WorkingFolder { get; init; }
    /// <summary>Whether the pane's agent was told it works in this worktree (<see cref="PaneWorktreeNote"/>).</summary>
    [JsonPropertyName("noteGiven")] public bool? NoteGiven { get; init; }

    public const string Running = "running", Done = "done", Failed = "failed";

    /// <summary>The record of <paramref name="made"/>, a pane worktree in the checkout <paramref name="checkout"/>.</summary>
    public static PaneWorktree From(ChildWorktree made, string checkout, int port, string? setupCommand) => new()
    {
        WorktreePath = made.WorktreePath, Branch = made.Branch, BaseBranch = made.ParentBranch, BaseCommit = made.BaseCommit, Checkout = checkout,
        Port = port, SetupCommand = setupCommand, SetupStatus = setupCommand is null ? null : Running, WorkingFolder = made.WorkingFolder,
    };

    /// <summary>The folder the pane's runs work in.</summary>
    [JsonIgnore] public string RunFolder => WorkingFolder ?? WorktreePath;

    /// <summary>The values every run of the pane (and its setup command) gets: its PORT, the workspace folder it was made from and its own worktree.</summary>
    public Dictionary<string, string> Environment(string workspaceFolder) => new()
    {
        ["PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["MIGHTY_WORKSPACE_FOLDER"] = workspaceFolder, ["MIGHTY_WORKTREE"] = RunFolder,
    };

    /// <summary>A saved record, or null when it is not one this app made. A setup still running when the app quit reads as failed.</summary>
    public PaneWorktree? Normalized(string sessionId, bool restoring)
    {
        static bool Absolute(string path) => path.Length is > 0 and <= 4096 && !path.Contains('\0') && Path.IsPathFullyQualified(path);
        if (!Absolute(WorktreePath) || !Absolute(Checkout) || Branch != ChildRecord.BranchName(sessionId) || BaseBranch.Length == 0 || BaseBranch.Contains('\0')
            || !PaneWorktreePorts.Contains(Port)) return null;
        var copy = this with { SetupCommand = PaneWorktreeSetup.NormalizedCommand(SetupCommand) };
        if (copy.SetupCommand is null) copy = copy with { SetupStatus = null, SetupExitCode = null };
        else if (copy.SetupStatus is not (Running or Done or Failed)) copy = copy with { SetupStatus = null };
        if (restoring && copy.SetupStatus == Running) copy = copy with { SetupStatus = Failed };
        if (MergedCommit is { } merged && !DelegationGit.IsObjectName(merged)) copy = copy with { MergedCommit = null };
        if (PreMergeCommit is { } pre && (copy.MergedCommit is null || !DelegationGit.IsObjectName(pre))) copy = copy with { PreMergeCommit = null };
        if (WorkingFolder is { } folder && (!Absolute(folder) || !(DelegationPaths.Same(folder, WorktreePath) || DelegationPaths.IsInside(folder, WorktreePath)))) copy = copy with { WorkingFolder = null };
        if (copy.NoteGiven != true) copy = copy with { NoteGiven = null };
        return copy;
    }
}

/// <summary>Where a workspace's agent panes work (M/PaneWorktree.swift PaneWorktreeMode). Saved on the workspace; a word this build does not know reads as <see cref="Ask"/>.</summary>
public static class PaneWorktreeMode
{
    /// <summary>The first agent pane works in the workspace folder; a further one asks (the default).</summary>
    public const string Ask = "ask";
    /// <summary>The first works in the workspace folder; every further one opens in its own worktree without asking.</summary>
    public const string Further = "further";
    /// <summary>Every agent pane, the first too, opens in its own worktree: the workspace folder only receives merges.</summary>
    public const string Every = "every";
    public static readonly string[] All = [Ask, Further, Every];
    public static string Of(string? saved) => saved is Further or Every ? saved : Ask;
    /// <summary>The value saved on the workspace: null for the default.</summary>
    public static string? Saved(string? mode) => Of(mode) == Ask ? null : Of(mode);
}

/// <summary>A pane worktree kept after its pane closed with work still unmerged (or children still on its branch).</summary>
public sealed record KeptPaneWorktree
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "claude";
    [JsonPropertyName("worktree")] public PaneWorktree Worktree { get; init; } = new();
    [JsonPropertyName("keptAt")] public string KeptAt { get; init; } = Wire.Now();

    /// <summary>The records worth keeping from a saved list: valid ids and worktrees, one per session, the newest 50.</summary>
    public static List<KeptPaneWorktree>? Normalized(IEnumerable<KeptPaneWorktree?>? list, bool restoring)
    {
        if (list is null) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = list.Reverse().OfType<KeptPaneWorktree>().Select(record =>
        {
            if (!Wire.Identifier(record.SessionId) || !seen.Add(record.SessionId) || record.Worktree?.Normalized(record.SessionId, restoring) is not { } worktree) return null;
            return record with { Worktree = worktree, Title = record.Title is { Length: > 200 } longTitle ? longTitle[..200] : record.Title ?? "", Provider = Wire.Providers.Contains(record.Provider) ? record.Provider : "claude" };
        }).OfType<KeptPaneWorktree>().Take(50).Reverse().ToList();
        return kept.Count == 0 ? null : kept;
    }
}

/// <summary>Where a new agent pane of a workspace works.</summary>
public enum PaneWorktreeDecision
{
    /// <summary>In the workspace folder: the first agent pane, a remote workspace, a resumed pane.</summary>
    SameFolder,
    /// <summary>In a new worktree, without asking: the workspace's mode says so.</summary>
    Worktree,
    /// <summary>The user chooses: another agent pane already works in the workspace folder.</summary>
    Ask,
}

public static class PaneWorktreePolicy
{
    /// <summary>Whether <paramref name="session"/> is an agent pane of <paramref name="workspaceId"/> that works in the workspace folder itself.</summary>
    public static bool WorksInWorkspaceFolder(RunSession session, string workspaceId) =>
        session.WorkspaceId == workspaceId && session.Kind == "claude" && session.ParentSessionId is null && session.WorkingFolder is null;

    /// <summary>Where a new agent pane of <paramref name="workspace"/> works (rule 1). The rule is "open", not "running".</summary>
    public static PaneWorktreeDecision Decide(Workspace workspace, IEnumerable<RunSession> sessions, IReadOnlySet<string>? closing = null)
    {
        if (workspace.Remote is not null) return PaneWorktreeDecision.SameFolder;
        var mode = PaneWorktreeMode.Of(workspace.PaneWorktreeMode);
        if (mode == PaneWorktreeMode.Every) return PaneWorktreeDecision.Worktree;
        if (!sessions.Any(s => closing?.Contains(s.Id) != true && WorksInWorkspaceFolder(s, workspace.Id))) return PaneWorktreeDecision.SameFolder;
        return mode == PaneWorktreeMode.Further ? PaneWorktreeDecision.Worktree : PaneWorktreeDecision.Ask;
    }

    /// <summary>The same decision where nobody can be asked (a pane added from the phone): asking becomes the workspace folder.</summary>
    public static PaneWorktreeDecision DecideWithoutAsking(Workspace workspace, IEnumerable<RunSession> sessions, IReadOnlySet<string>? closing = null) =>
        Decide(workspace, sessions, closing) is var decision && decision == PaneWorktreeDecision.Ask ? PaneWorktreeDecision.SameFolder : decision;

    /// <summary>Why the pane may not be discarded now, null when it may: children still branch from its branch (rule 9).</summary>
    public static string? DiscardBlocker(string sessionId, IEnumerable<RunSession> sessions, IEnumerable<DelegationChildRow> children)
    {
        var count = Math.Max(sessions.Count(s => s.ParentSessionId == sessionId), children.Count(c => c.ParentSessionId == sessionId && c.State.IsOpen()));
        return count > 0 ? Locale.Get("paneWorktree.refused.hasChildren", new Dictionary<string, string> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null;
    }
}

/// <summary>A port for each worktree pane's PORT (rule 4): from the pane's id, then the next one no other pane holds and nothing listens on.</summary>
public static class PaneWorktreePorts
{
    public const int Lowest = 4100, Highest = 4899;
    public static bool Contains(int port) => port is >= Lowest and <= Highest;

    public static int Port(string sessionId, IReadOnlySet<int> taken, Func<int, bool>? isFree = null)
    {
        isFree ??= CanBind;
        const int span = Highest - Lowest + 1;
        // FNV-1a 32-bit: the same id gives the same start on every launch and on the Mac.
        uint hash = 2_166_136_261;
        foreach (var b in Encoding.UTF8.GetBytes(sessionId)) hash = unchecked((hash ^ b) * 16_777_619);
        var start = (int)(hash % span);
        for (var step = 0; step < span; step++)
        {
            var port = Lowest + (start + step) % span;
            if (!taken.Contains(port) && isFree(port)) return port;
        }
        return Lowest + start;
    }

    /// <summary>Whether a TCP server could listen on <paramref name="port"/> on this computer's loopback now.</summary>
    public static bool CanBind(int port)
    {
        try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop(); return true; }
        catch (SocketException) { return false; }
    }
}

/// <summary>The workspace's setup command for a new worktree (<c>npm ci</c>, …).</summary>
public static class PaneWorktreeSetup
{
    /// <summary>One line, at most 1,000 characters; empty is none.</summary>
    public static string? NormalizedCommand(string? raw)
    {
        if (raw is null) return null;
        var line = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length == 0 || line.Contains('\0') ? null : line.Length > 1000 ? line[..1000] : line;
    }
}

/// <summary>What a pane worktree's agent is told once, with its first request (rule 3).</summary>
public static class PaneWorktreeNote
{
    public static string Text(PaneWorktree pane) =>
        $"[Mighty Code] This pane works in its own git worktree ({pane.RunFolder}) on the branch {pane.Branch}, made from {pane.BaseBranch} so that the other panes of this workspace keep working undisturbed. Dev servers you start here use PORT={pane.Port} (already set in the environment); start them on that port. When you are done, commit your work on {pane.Branch}, leaving no uncommitted changes and no new files outside a commit. The person who sent this merges your branch into {pane.BaseBranch} from the app. Do not merge, push, rebase or switch branches.";

    /// <summary>Whether a request carries the note: the first one that is no slash command.</summary>
    public static bool Applies(PaneWorktree pane, string input) => pane.NoteGiven != true && !input.TrimStart().StartsWith('/');

    public static string Attach(PaneWorktree pane, string input) => input + "\n\n" + Text(pane);

    /// <summary>The request asking the pane's agent to bring its base in and resolve the conflict the merge refused.</summary>
    public static string ConflictPrompt(PaneWorktree pane) =>
        $"[Mighty Code] Merging your branch {pane.Branch} into {pane.BaseBranch} from the app was refused: the two conflict. In this worktree, bring {pane.BaseBranch} into your branch with `git merge {pane.BaseBranch}`, resolve every conflict so that the changes on both sides are kept (ask when a choice is unclear), run the checks that apply, and commit the merge on {pane.Branch}, leaving no uncommitted changes. The person merges your branch from the app again afterwards. Do not push, rebase or switch branches.";
}

public abstract record PaneWorktreeMergeOutcome
{
    public sealed record Merged(string Commit, string PreMerge) : PaneWorktreeMergeOutcome;
    public sealed record Refused(string Reason) : PaneWorktreeMergeOutcome;
    public sealed record Failed(string Message) : PaneWorktreeMergeOutcome;
}

/// <summary>The refusal codes of a merge (rule 6), in the order they are checked.</summary>
public static class PaneWorktreeRefusal
{
    public const string WorktreeMissing = "worktreeMissing", UncommittedChanges = "uncommittedChanges", NothingToMerge = "nothingToMerge",
        BranchNotCheckedOut = "branchNotCheckedOut", TrackedChanges = "trackedChanges", MergeConflict = "mergeConflict";

    public static string Text(string reason, string baseBranch) =>
        Locale.Get("paneWorktree.refused." + reason, new Dictionary<string, string> { ["base"] = baseBranch });
}

public abstract record PaneWorktreeUndoOutcome
{
    public sealed record Undone : PaneWorktreeUndoOutcome;
    /// <summary><c>notMerged</c>, <c>branchNotCheckedOut</c>, <c>baseMoved</c> or <c>trackedChanges</c> (rule 8).</summary>
    public sealed record Refused(string Reason) : PaneWorktreeUndoOutcome;
    public sealed record Failed(string Message) : PaneWorktreeUndoOutcome;
}

public abstract record PaneWorktreeCommitOutcome
{
    /// <summary>Committed; <paramref name="LeftOut"/> are new files not taken: they still block the merge.</summary>
    public sealed record Committed(IReadOnlyList<string> LeftOut) : PaneWorktreeCommitOutcome;
    public sealed record Nothing(IReadOnlyList<string> LeftOut) : PaneWorktreeCommitOutcome;
    public sealed record Unmerged : PaneWorktreeCommitOutcome;
    public sealed record Failed(string Message) : PaneWorktreeCommitOutcome;
}

/// <summary>How far a pane worktree is from its base.</summary>
public sealed record PaneWorktreeStatus(int Ahead, int Behind, int ChangedFiles);

/// <summary>One file a pane worktree changed against the point it shares with its base; <c>?</c> is new and not committed.</summary>
public sealed record PaneWorktreeChangedFile(string Path, string Status);

/// <summary>A pane worktree found on disk.</summary>
public sealed record PaneWorktreeOnDisk(string SessionId, string WorktreePath, string Branch, string Head);

/// <summary>
/// Merges a worktree pane's branch into the workspace branch, commits and undoes, and discards the worktree: the same git as a
/// delegated child's card merge (<see cref="ChildMerge.CardMerge"/>) and discard (<see cref="ChildCleanup"/>).
/// </summary>
public static class PaneWorktreeGit
{
    private const double Timeout = 60;

    /// <summary>The pane's commits into the branch the workspace folder was on (rule 6). Every refusal comes before any git write.</summary>
    public static async Task<PaneWorktreeMergeOutcome> Merge(PaneWorktree pane)
    {
        static PaneWorktreeMergeOutcome Refused(string reason) => new PaneWorktreeMergeOutcome.Refused(reason);
        static PaneWorktreeMergeOutcome Failed(string message) => new PaneWorktreeMergeOutcome.Failed(message);
        if (DelegationGit.Executable is null) return Failed(Locale.Get("delegation.card.error.gitMissing"));
        if (!ChildMerge.IsFolder(pane.WorktreePath)) return Refused(PaneWorktreeRefusal.WorktreeMissing);
        switch (await HasAnyChanges(pane.WorktreePath))
        {
            case null: return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
            case true: return Refused(PaneWorktreeRefusal.UncommittedChanges);
        }
        if (await BranchHead(pane) is not { } head) return Refused(PaneWorktreeRefusal.WorktreeMissing);
        if (await ChildMerge.CheckedOutHead(pane.BaseBranch, pane.Checkout) is not { } preMerge) return Refused(PaneWorktreeRefusal.BranchNotCheckedOut);
        if (await DelegationGit.Run(["merge-base", "--is-ancestor", head, preMerge], pane.Checkout) is not { } contained) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (contained.ExitCode == 0) return Refused(PaneWorktreeRefusal.NothingToMerge);
        switch (await ChildMerge.HasTrackedChanges(pane.Checkout))
        {
            case null: return Failed(Locale.Get("delegation.card.error.parentChangesUnread"));
            case true: return Refused(PaneWorktreeRefusal.TrackedChanges);
        }
        if (await DelegationGit.Run(["merge-tree", "--write-tree", preMerge, head], pane.Checkout, Timeout) is not { } preview) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (preview.ExitCode == 1) return Refused(PaneWorktreeRefusal.MergeConflict);
        var tree = DelegationGit.Line(preview.Stdout).Split('\n')[0].Trim();
        if (preview.ExitCode != 0 || !DelegationGit.IsObjectName(tree)) return Failed(DelegationGit.Message(preview));
        if (await DelegationGit.Run(["merge-base", "--is-ancestor", preMerge, head], pane.Checkout) is not { } ancestor) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        string target;
        switch (ancestor.ExitCode)
        {
            case 0: target = head; break;
            case 1:
                var message = $"Merge branch '{pane.Branch}' into {pane.BaseBranch}";
                if (await DelegationGit.Run(["commit-tree", tree, "-p", preMerge, "-p", head, "-m", message], pane.Checkout, Timeout) is not { } made) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
                var commit = DelegationGit.Line(made.Stdout);
                if (made.ExitCode != 0 || !DelegationGit.IsObjectName(commit)) return Failed(DelegationGit.Message(made));
                target = commit; break;
            default: return Failed(DelegationGit.Message(ancestor));
        }
        if (await DelegationGit.Run(["merge", "--ff-only", "--no-autostash", "--no-stat", "-q", target], pane.Checkout, Timeout) is not { } moved) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (moved.ExitCode != 0) return Failed(DelegationGit.Message(moved));
        if (await ChildMerge.Head(pane.Checkout) != target) return Failed(Locale.Get("delegation.card.error.parentNotAtMerge"));
        return new PaneWorktreeMergeOutcome.Merged(target, preMerge);
    }

    private static async Task<string?> BranchHead(PaneWorktree pane)
    {
        if (await DelegationGit.Run(["rev-parse", "-q", "--verify", "refs/heads/" + pane.Branch + "^{commit}"], pane.Checkout) is not { ExitCode: 0 } found) return null;
        var head = DelegationGit.Line(found.Stdout);
        return DelegationGit.IsObjectName(head) ? head : null;
    }

    /// <summary>Whether <paramref name="folder"/> has changes git would show: tracked or new files, ignored ones aside. null when git could not tell.</summary>
    public static async Task<bool?> HasAnyChanges(string folder) =>
        await DelegationGit.Run(["status", "--porcelain", "--untracked-files=normal"], folder) is { ExitCode: 0 } status ? status.Stdout.Length > 0 : null;

    /// <summary>Whether the pane holds work the base branch does not; null when git could not tell (counts as work).</summary>
    public static async Task<bool?> HasUnmergedWork(PaneWorktree pane)
    {
        if (!ChildMerge.IsFolder(pane.WorktreePath)) return false;
        if (await DelegationGit.Run(["status", "--porcelain"], pane.WorktreePath) is not { ExitCode: 0 } status) return null;
        if (status.Stdout.Length > 0) return true;
        if (await DelegationGit.Run(["rev-parse", "-q", "--verify", "refs/heads/" + pane.Branch + "^{commit}"], pane.Checkout) is not { } found) return null;
        if (found.ExitCode != 0) return false;
        if (await DelegationGit.Run(["merge-base", "--is-ancestor", DelegationGit.Line(found.Stdout), "refs/heads/" + pane.BaseBranch], pane.Checkout) is not { } contained) return null;
        return contained.ExitCode switch { 0 => false, 1 => true, _ => null };
    }

    /// <summary>The worktree, every worktree nested in it, a leftover folder at its place and its branch.</summary>
    public static Task<ChildDiscardOutcome> Discard(PaneWorktree pane, string sessionId, string root) =>
        ChildCleanup.RemoveOwnWorktree(sessionId, pane.WorktreePath, pane.Branch, pane.Checkout, root);

    /// <summary>Commits ahead of the base, commits the base moved ahead, and changed files; null when git could not tell.</summary>
    public static async Task<PaneWorktreeStatus?> Status(PaneWorktree pane)
    {
        if (!ChildMerge.IsFolder(pane.WorktreePath)) return null;
        if (await DelegationGit.Run(["rev-list", "--left-right", "--count", "refs/heads/" + pane.BaseBranch + "...refs/heads/" + pane.Branch], pane.Checkout) is not { ExitCode: 0 } counts
            || await DelegationGit.Run(["status", "--porcelain", "--untracked-files=normal"], pane.WorktreePath) is not { ExitCode: 0 } changed) return null;
        var numbers = DelegationGit.Line(counts.Stdout).Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).Select(n => int.TryParse(n, out var v) ? v : -1).ToArray();
        if (numbers.Length != 2 || numbers.Any(n => n < 0)) return null;
        return new(numbers[1], numbers[0], changed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static async Task<string?> MergeBase(PaneWorktree pane)
    {
        if (await DelegationGit.Run(["merge-base", "refs/heads/" + pane.BaseBranch, "HEAD"], pane.WorktreePath) is not { ExitCode: 0 } found) return null;
        var commit = DelegationGit.Line(found.Stdout);
        return DelegationGit.IsObjectName(commit) ? commit : null;
    }

    /// <summary>The files the pane changed against the commit it shares with its base: its commits and what is not committed yet.</summary>
    public static async Task<IReadOnlyList<PaneWorktreeChangedFile>?> ChangedFiles(PaneWorktree pane)
    {
        if (await MergeBase(pane) is not { } since
            || await DelegationGit.Run(["diff", "--name-status", "-z", "--no-renames", since], pane.WorktreePath, 30) is not { ExitCode: 0 } diff
            || await DelegationGit.Run(["ls-files", "-z", "--others", "--exclude-standard"], pane.WorktreePath, 30) is not { ExitCode: 0 } others) return null;
        var files = new List<PaneWorktreeChangedFile>();
        var fields = diff.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < fields.Length; i += 2) files.Add(new(fields[i + 1], fields[i][..1]));
        foreach (var path in others.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (!files.Any(f => f.Path == path)) files.Add(new(path, "?"));
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>The unified diff of <paramref name="path"/> against the commit the pane shares with its base (a new file: all added); null when git could not tell.</summary>
    public static async Task<string?> FileDiff(PaneWorktree pane, string path)
    {
        if (path.Length == 0 || path.Contains('\0') || Path.IsPathRooted(path) || path.Split('/', '\\').Contains("..")) return null;
        if (await MergeBase(pane) is not { } since) return null;
        if (await DelegationGit.Run(["--literal-pathspecs", "ls-files", "--error-unmatch", "--", path], pane.WorktreePath) is { ExitCode: 0 } || await DelegationGit.Run(["cat-file", "-e", since + ":" + path], pane.WorktreePath) is { ExitCode: 0 })
            return await DelegationGit.Run(["--literal-pathspecs", "diff", "--no-color", since, "--", path], pane.WorktreePath, 30) is { ExitCode: 0 } shown ? shown.Stdout : null;
        // Untracked: shown whole as added lines.
        try
        {
            var full = Path.Combine(pane.WorktreePath, path);
            var info = new FileInfo(full);
            if (!info.Exists || info.LinkTarget is not null || info.Length > 1_000_000) return null;
            var text = await File.ReadAllTextAsync(full);
            return "+++ b/" + path + "\n" + string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => "+" + line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Commits everything the worktree changed, new files too, except secret-looking or over-5-MB new files (rule 7).</summary>
    public static async Task<PaneWorktreeCommitOutcome> CommitAll(PaneWorktree pane)
    {
        static PaneWorktreeCommitOutcome Failed(string message) => new PaneWorktreeCommitOutcome.Failed(message);
        var folder = pane.WorktreePath;
        if (!ChildMerge.IsFolder(folder)) return Failed(Locale.Get("paneWorktree.refused.worktreeMissing"));
        if (await DelegationGit.Run(["ls-files", "-u"], folder, 60) is not { ExitCode: 0 } unmerged
            || await DelegationGit.Run(["ls-files", "-z", "--others", "--exclude-standard"], folder, 60) is not { ExitCode: 0 } others) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (unmerged.Stdout.Length > 0) return new PaneWorktreeCommitOutcome.Unmerged();
        var fresh = others.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var leftOut = fresh.Where(path => ChildAutoCommit.LooksSecret(path) || Size(Path.Combine(folder, path)) > ChildAutoCommit.MaximumNewFileBytes).ToArray();
        var taken = fresh.Except(leftOut).ToArray();
        async Task<string?> Write(IReadOnlyList<string> arguments, string? input = null) =>
            await DelegationGit.Run(arguments, folder, 300, input) is not { } result ? Locale.Get("delegation.card.error.gitUnfinished") : result.ExitCode == 0 ? null : DelegationGit.Message(result);
        if (await Write(["add", "-u"]) is { } addFailed) return Failed(addFailed);
        if (taken.Length > 0 && await Write(["--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul"], string.Join('\0', taken)) is { } newFailed) return Failed(newFailed);
        if (await DelegationGit.Run(["diff", "--cached", "--quiet"], folder, 60) is not { ExitCode: 0 or 1 } staged) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (staged.ExitCode == 0) return new PaneWorktreeCommitOutcome.Nothing(leftOut);
        if (await Write(["-c", "commit.gpgsign=false", "commit", "-q", "--no-verify", "-m", $"mighty: commit {pane.Branch} for its merge"]) is { } commitFailed) return Failed(commitFailed);
        return new PaneWorktreeCommitOutcome.Committed(leftOut);
    }

    private static long Size(string path)
    {
        try { var info = new FileInfo(path); return info.Exists && info.LinkTarget is null ? info.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }

    /// <summary>Puts the base branch back where it was before the pane's last merge (rule 8): read-tree, then compare-and-swap.</summary>
    public static async Task<PaneWorktreeUndoOutcome> UndoMerge(PaneWorktree pane)
    {
        static PaneWorktreeUndoOutcome Refused(string reason) => new PaneWorktreeUndoOutcome.Refused(reason);
        static PaneWorktreeUndoOutcome Failed(string message) => new PaneWorktreeUndoOutcome.Failed(message);
        if (pane.MergedCommit?.ToLowerInvariant() is not { } merged || pane.PreMergeCommit?.ToLowerInvariant() is not { } preMerge
            || !DelegationGit.IsObjectName(merged) || !DelegationGit.IsObjectName(preMerge)) return Refused("notMerged");
        if (DelegationGit.Executable is null) return Failed(Locale.Get("delegation.card.error.gitMissing"));
        if (await ChildMerge.CheckedOutHead(pane.BaseBranch, pane.Checkout) is not { } head) return Refused("branchNotCheckedOut");
        if (head != merged) return Refused("baseMoved");
        switch (await ChildMerge.HasTrackedChanges(pane.Checkout))
        {
            case null: return Failed(Locale.Get("delegation.card.error.parentChangesUnread"));
            case true: return Refused("trackedChanges");
        }
        await DelegationGit.Run(["update-index", "-q", "--refresh"], pane.Checkout, 60);
        if (await DelegationGit.Run(["read-tree", "-m", "-u", merged, preMerge], pane.Checkout, 60) is not { ExitCode: 0 }) return Failed(Locale.Get("delegation.card.error.filesNotRestored"));
        if (await DelegationGit.Run(["update-ref", "-m", $"mighty: undo the merge of {pane.Branch}", "refs/heads/" + pane.BaseBranch, preMerge, merged], pane.Checkout) is not { ExitCode: 0 })
        {
            await DelegationGit.Run(["read-tree", "-m", "-u", preMerge, merged], pane.Checkout, 60);
            return Failed(Locale.Get("delegation.card.error.parentMovedDuringUndo"));
        }
        return new PaneWorktreeUndoOutcome.Undone();
    }

    /// <summary>The pane worktrees <paramref name="checkout"/> has under <paramref name="root"/>; delegated children's ids are left out by the caller.</summary>
    public static async Task<IReadOnlyList<PaneWorktreeOnDisk>?> Worktrees(string checkout, string root) =>
        await DelegationGit.Run(["worktree", "list", "--porcelain"], checkout, 30) is { ExitCode: 0 } listed ? PaneWorktreeInventory.Parse(listed.Stdout, root) : null;
}

/// <summary>The rows of a workspace's worktree list (rule 10): open panes, then kept ones, then those found on disk.</summary>
public static class PaneWorktreeInventory
{
    public const string Open = "open", Kept = "kept", Found = "found";
    public sealed record Row(string SessionId, string Title, string Provider, PaneWorktree Worktree, string Origin);

    /// <summary><c>git worktree list --porcelain</c> read for <c>&lt;root&gt;\&lt;id&gt;</c> folders on <c>mighty/&lt;id&gt;</c> branches.</summary>
    public static IReadOnlyList<PaneWorktreeOnDisk> Parse(string text, string root)
    {
        var rootPath = RealPath(root);
        var found = new List<PaneWorktreeOnDisk>();
        foreach (var block in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            string? path = null, head = null, branch = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
                else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line[5..];
                else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = line[18..];
            }
            if (path is null || branch is null || head is null) continue;
            var folder = DelegationPaths.Standardized(path);
            var id = Path.GetFileName(folder);
            if (Path.GetDirectoryName(folder) is not { } parent || !DelegationPaths.Same(RealPath(parent), rootPath) || !Wire.Identifier(id) || branch != ChildRecord.BranchName(id)) continue;
            found.Add(new(id, folder, branch, head));
        }
        return found;
    }

    /// <summary>
    /// The path with its links resolved, as git names a worktree: Windows' final path name (<see cref="DelegationPaths.Canonical"/>),
    /// elsewhere every linked folder on the way followed (a temp folder under <c>/var</c> is <c>/private/var</c> to git on a Mac).
    /// </summary>
    private static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return DelegationPaths.Canonical(path);
        var full = DelegationPaths.Standardized(path);
        var current = Path.GetPathRoot(full) ?? "/";
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            try { current = new DirectoryInfo(next).ResolveLinkTarget(true)?.FullName ?? next; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { current = next; }
        }
        return DelegationPaths.Standardized(current);
    }

    public static IReadOnlyList<Row> Rows(IEnumerable<RunSession> open, IEnumerable<KeptPaneWorktree> kept, IEnumerable<PaneWorktreeOnDisk> onDisk, IReadOnlySet<string> excluded,
        string checkout, string? baseBranch, Func<string, int> port)
    {
        var rows = new List<Row>();
        foreach (var session in open) if (session.PaneWorktree is { } pane) rows.Add(new(session.Id, session.Title, session.Provider, pane, Open));
        foreach (var record in kept) if (!rows.Any(r => r.SessionId == record.SessionId)) rows.Add(new(record.SessionId, record.Title, record.Provider, record.Worktree, Kept));
        if (baseBranch is not null)
            foreach (var disk in onDisk)
                if (!excluded.Contains(disk.SessionId) && !rows.Any(r => r.SessionId == disk.SessionId))
                    rows.Add(new(disk.SessionId, disk.Branch, "claude", new PaneWorktree { WorktreePath = disk.WorktreePath, Branch = disk.Branch, BaseBranch = baseBranch, BaseCommit = disk.Head, Checkout = checkout, Port = port(disk.SessionId), WorkingFolder = disk.WorktreePath }, Found));
        return rows;
    }
}

/// <summary>
/// Files the workspace folder has but git leaves out of a worktree (<c>.env</c>, …), copied into a new pane worktree before its
/// setup command (rule 5): plain files and folders inside the workspace folder only, never a link (a junction, symbolic link or
/// any reparse point) and nothing one leads to, never over a file the worktree has, at most 50 MB in 2,000 files.
/// </summary>
public static class PaneWorktreeCopy
{
    public const long MaximumBytes = 50_000_000;
    public const int MaximumFiles = 2_000, MaximumEntries = 32;

    /// <summary>The list as saved: relative paths without <c>..</c>, a leading separator, <c>~</c> or <c>.git</c>, at most 32.</summary>
    public static List<string>? NormalizedPaths(IEnumerable<string?>? raw)
    {
        if (raw is null) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var entry in raw)
        {
            var path = (entry ?? "").Trim().Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            path = path.TrimEnd('/');
            if (path.Length is 0 or > 512 || path.StartsWith('/') || path.StartsWith('~') || path.Contains('\0') || path.Contains(':')
                || path.Split('/').Any(part => part is ".." or "." or ".git" or "") || !seen.Add(path)) continue;
            paths.Add(path);
            if (paths.Count == MaximumEntries) break;
        }
        return paths.Count == 0 ? null : paths;
    }

    /// <summary>The settings field (<c>.env, .env.local</c> or one per line) as entries.</summary>
    public static IEnumerable<string> Entries(string text) => text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

    public sealed record Result
    {
        public List<string> Copied { get; } = [];
        /// <summary>Entries not copied with why: <c>missing</c>, <c>link</c>, <c>outside</c>, <c>exists</c>, <c>limit</c>, <c>failed</c>.</summary>
        public Dictionary<string, string> Skipped { get; } = new(StringComparer.Ordinal);
    }

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Copies <paramref name="paths"/> from <paramref name="source"/> (the workspace folder) into <paramref name="destination"/> (the pane's run folder).</summary>
    public static Result Copy(IEnumerable<string> paths, string source, string destination)
    {
        var result = new Result();
        var sourceRoot = DelegationPaths.Canonical(source);
        long bytes = 0; var files = 0;
        foreach (var path in NormalizedPaths(paths) ?? [])
        {
            var from = Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar));
            FileSystemInfo info = Directory.Exists(from) ? new DirectoryInfo(from) : new FileInfo(from);
            if (!info.Exists) { result.Skipped[path] = "missing"; continue; }
            if (IsLink(info)) { result.Skipped[path] = "link"; continue; }
            // A folder on the way may be a link out of the workspace: the real place must still be inside it.
            if (!DelegationPaths.IsInside(DelegationPaths.Canonical(from), sourceRoot)) { result.Skipped[path] = "outside"; continue; }
            var to = Path.Combine(destination, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(to) || Directory.Exists(to)) { result.Skipped[path] = "exists"; continue; }
            var items = new List<(string From, string To)>();
            try
            {
                if (info is DirectoryInfo folder)
                {
                    var pending = new Stack<DirectoryInfo>([folder]);
                    while (pending.Count > 0)
                    {
                        var current = pending.Pop();
                        foreach (var child in current.EnumerateFileSystemInfos())
                        {
                            if (IsLink(child)) continue;
                            if (child is DirectoryInfo sub) { pending.Push(sub); continue; }
                            var file = (FileInfo)child;
                            items.Add((file.FullName, Path.Combine(to, Path.GetRelativePath(folder.FullName, file.FullName))));
                            bytes += file.Length; files++;
                        }
                    }
                }
                else { items.Add((from, to)); bytes += ((FileInfo)info).Length; files++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skipped[path] = "failed"; continue; }
            if (bytes > MaximumBytes || files > MaximumFiles) { result.Skipped[path] = "limit"; break; }
            try
            {
                foreach (var (item, target) in items) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(item, target, overwrite: false); }
                if (info is DirectoryInfo && items.Count == 0) Directory.CreateDirectory(to);
                result.Copied.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skipped[path] = "failed"; }
        }
        return result;
    }
}

/// <summary>Pushing a pane's branch and the page where its pull request is made (rule 11).</summary>
public static class PaneWorktreePR
{
    /// <summary>The <c>owner/repo</c> of a GitHub remote URL (https, ssh or scp-like), null for any other host.</summary>
    public static string? GitHubRepository(string remote)
    {
        var rest = remote.Trim();
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "ssh://git@github.com/", "git@github.com:", "git://github.com/" })
        {
            if (!rest.StartsWith(prefix, StringComparison.Ordinal)) continue;
            rest = rest[prefix.Length..];
            if (rest.EndsWith(".git", StringComparison.Ordinal)) rest = rest[..^4];
            rest = rest.TrimEnd('/');
            var parts = rest.Split('/');
            return parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) ? parts[0] + "/" + parts[1] : null;
        }
        return null;
    }

    /// <summary>GitHub's page for a pull request of <paramref name="branch"/> into <paramref name="baseBranch"/>.</summary>
    public static string CompareUrl(string repository, string baseBranch, string branch)
    {
        static string Escape(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
        return $"https://github.com/{repository}/compare/{Escape(baseBranch)}...{Escape(branch)}?expand=1";
    }

    /// <summary>The <c>origin</c> remote of the checkout, null without one.</summary>
    public static async Task<string?> Origin(PaneWorktree pane) =>
        await DelegationGit.Run(["remote", "get-url", "origin"], pane.Checkout) is { ExitCode: 0 } found && DelegationGit.Line(found.Stdout) is { Length: > 0 } url ? url : null;

    /// <summary>Pushes the pane's branch to <c>origin</c> under the same name, never forced. null when it went; git's words otherwise.</summary>
    public static async Task<string?> Push(PaneWorktree pane)
    {
        var reference = "refs/heads/" + pane.Branch;
        if (await DelegationGit.Run(["push", "--porcelain", "origin", reference + ":" + reference], pane.Checkout, 180) is not { } pushed) return Locale.Get("delegation.card.error.gitUnfinished");
        return pushed.ExitCode == 0 ? null : DelegationGit.Message(pushed);
    }
}
