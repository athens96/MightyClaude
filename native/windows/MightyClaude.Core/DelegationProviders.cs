namespace MightyClaude.Core;

/// <summary>
/// Which agent panes take part in delegation, and how (M/DelegationProviders.swift). A child always runs its parent's agent CLI.
/// <list type="bullet">
/// <item>Claude and Codex panes may be parents by agent: their runs get the delegation server, and their delegate call makes
/// children. A Codex run gets it only where its tool calls can work: acceptEdits or fullAccess. Manual is read-only and refuses
/// every MCP call it cannot ask about, and onRequest declines the MCP prompts the app cannot show.</item>
/// <item>Claude, Codex and Antigravity panes may be parents by human: Ctrl+Enter on a busy one runs the text in a new child
/// worktree, in the parent's mode. A Codex or Antigravity parent offers it only in a mode where its child can change files
/// (<see cref="ChildCanWrite"/>). Antigravity has no delegation tools, so its children come only from Ctrl+Enter.</item>
/// <item>Only a Claude run takes text while it runs (steer).</item>
/// <item>A Claude child commits its own work. A Codex or Antigravity child cannot commit outside fullAccess, so the app commits
/// what it leaves when its report changes (<see cref="ChildAutoCommit"/>).</item>
/// <item>A Claude parent's merge tool call follows its CLI's permission mode; any other parent may merge by tool only when its run
/// started in fullAccess, and is otherwise refused with <c>merge_human_only</c>: the human merges from the child's card.</item>
/// </list>
/// </summary>
public static class DelegationProviders
{
    /// <summary>An agent pane (kind claude) running one of these may delegate by tool.</summary>
    private static readonly HashSet<string> AgentParents = ["claude", "codex"];
    /// <summary>The agents of agent panes that take part at all.</summary>
    private static readonly HashSet<string> Agents = ["claude", "codex", "gemini"];
    /// <summary>Every permission mode of any provider, narrowest first: how freely the agent acts without asking. Modes are compared within one provider.</summary>
    private static readonly string[] ModeRank = ["plan", "manual", "acceptEdits", "onRequest", "auto", "fullAccess"];

    /// <summary>An agent pane (kind claude) running Claude, Codex or Antigravity.</summary>
    public static bool IsAgentPane(string kind, string provider) => kind == "claude" && Agents.Contains(provider);

    public static bool CanParentByAgent(string kind, string provider) => kind == "claude" && AgentParents.Contains(provider);

    /// <summary>Whether a run in <paramref name="mode"/> gets the delegation tools: every Claude run, and a Codex run in acceptEdits or fullAccess. Ctrl+Enter does not depend on it.</summary>
    public static bool AttachesDelegation(string kind, string provider, string mode) => CanParentByAgent(kind, provider) && (provider != "codex" || CodexApprovesTools(mode));

    /// <summary>
    /// Whether a Codex run in <paramref name="mode"/> approves the delegation server's tools ahead: where it may write and never
    /// asks (acceptEdits, fullAccess). mighty-terminal's run_in_terminal is approved ahead only in fullAccess
    /// (<see cref="AgentIOBinding.AppendArguments"/>).
    /// </summary>
    public static bool CodexApprovesTools(string mode) => mode is "acceptEdits" or "fullAccess";

    /// <summary>Whether a <paramref name="provider"/> child in <paramref name="mode"/> can change files: any Claude mode (Claude asks the human), Codex acceptEdits, onRequest or fullAccess, Antigravity acceptEdits or fullAccess.</summary>
    public static bool ChildCanWrite(string provider, string mode) => provider switch
    {
        "claude" => true,
        "codex" => mode is "acceptEdits" or "onRequest" or "fullAccess",
        "gemini" => mode is "acceptEdits" or "fullAccess",
        _ => false,
    };

    /// <summary>Whether the pane, in <paramref name="mode"/> now, may have a child the human starts: an agent pane whose child, in the same mode, can change files.</summary>
    public static bool CanParentByHuman(string kind, string provider, string mode) => IsAgentPane(kind, provider) && ChildCanWrite(provider, mode);

    /// <summary>Whether a running turn of this pane takes text (steer): Claude only.</summary>
    public static bool CanSteer(string kind, string provider) => kind == "claude" && provider == "claude";

    /// <summary>The modes a child of <paramref name="provider"/> may start in, narrowest first.</summary>
    public static IReadOnlyList<string> StartingModes(string provider)
    {
        var modes = ProviderCatalog.PermissionModes(provider).ToHashSet();
        return ModeRank.Where(modes.Contains).ToArray();
    }

    /// <summary>Whether <paramref name="mode"/> lets a <paramref name="provider"/> agent act no more freely than <paramref name="stored"/> does. A mode that is not one of the provider's, on either side, is never no wider.</summary>
    public static bool IsNoWider(string mode, string stored, string provider)
    {
        var modes = StartingModes(provider).ToList();
        var asked = modes.IndexOf(mode); var own = modes.IndexOf(stored);
        return asked >= 0 && own >= 0 && asked <= own;
    }

    /// <summary>Whether a <paramref name="provider"/> child in <paramref name="mode"/> can commit on its own branch.</summary>
    public static bool CanCommit(string provider, string mode) => provider == "claude" || mode == "fullAccess";

    /// <summary>Whether a <paramref name="provider"/> parent whose run started in <paramref name="mode"/> may merge a child by its merge tool.</summary>
    public static bool AgentMayMerge(string provider, string mode) => provider == "claude" || mode == "fullAccess";
}

/// <summary>
/// The app's commit of a Codex or Antigravity child's work (M/DelegationProviders.swift ChildAutoCommit): when a run of the child
/// finishes normally with REPORT.md changed and the worktree changed, what it changed is committed on the child's branch before
/// its head is read for the report. Tracked files go in as changed; new files go in as .gitignore lets them (the notes folder
/// ignores itself, so TASK.md and REPORT.md never go in), except new files that look like secrets or are over
/// <see cref="MaximumNewFileBytes"/>, which stay uncommitted and are named to the human. Never with an unresolved conflict, and
/// never in a folder that is not the child's own worktree.
/// </summary>
public static class ChildAutoCommit
{
    /// <summary>The largest new file the app commits for a child.</summary>
    public const long MaximumNewFileBytes = 5_000_000;
    /// <summary>The longest one git step of the commit may take, in seconds.</summary>
    private const double Timeout = 300;

    /// <summary>Whether the app commits for the child now. A Claude child commits itself, so never; any other child whose report changed, with changes left and no unmerged path.</summary>
    public static bool ShouldCommit(string provider, bool reportChanged, bool hasChanges, bool unmerged) => provider != "claude" && reportChanged && hasChanges && !unmerged;

    /// <summary>The message of the commit for report revision <paramref name="revision"/>.</summary>
    public static string Message(string childId, int revision) => $"mighty: {childId} report r{revision}";

    /// <summary>The name starts that look like a secret.</summary>
    private static readonly string[] SecretStarts = [".env", "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", "credentials", "secrets."];
    /// <summary>The name ends that look like a secret.</summary>
    private static readonly string[] SecretEnds = [".pem", ".key", ".pfx", ".p12", ".ppk"];
    /// <summary>The whole names that look like a secret.</summary>
    private static readonly string[] SecretNames = [".npmrc", ".pypirc"];

    /// <summary>
    /// Whether a new file at <paramref name="path"/> (relative to the worktree) looks like a secret: <c>.env*</c>, <c>*.pem</c>,
    /// <c>*.key</c>, <c>*.pfx</c>, <c>*.p12</c>, <c>*.ppk</c>, <c>id_rsa*</c>, <c>id_ed25519*</c>, <c>id_ecdsa*</c>, <c>id_dsa*</c>,
    /// <c>credentials*</c>, <c>secrets.*</c>, <c>.npmrc</c> or <c>.pypirc</c>, in any case.
    /// </summary>
    public static bool LooksSecret(string path)
    {
        var name = path.Replace('\\', '/').TrimEnd('/').Split('/')[^1].ToLowerInvariant();
        return SecretStarts.Any(start => name.StartsWith(start, StringComparison.Ordinal)) || SecretEnds.Any(end => name.EndsWith(end, StringComparison.Ordinal)) || SecretNames.Contains(name);
    }

    /// <summary>What the app found or did in the child's worktree.</summary>
    public enum Outcome
    {
        /// <summary>Nothing to commit, or the child is one that commits itself.</summary>
        Nothing,
        Committed,
        /// <summary><c>git ls-files -u</c> lists paths: a conflict is unresolved, so nothing was committed.</summary>
        Unmerged,
        /// <summary>The run stopped or errored: nothing is committed until a run of the child finishes normally.</summary>
        AfterNormalFinish,
        /// <summary>Git could not read or commit the worktree, or it is not the child's own (<see cref="Result.Why"/>). Nothing was committed.</summary>
        Failed,
    }

    /// <param name="LeftOut">New files left uncommitted: secret-looking or too large.</param>
    public sealed record Result(Outcome Outcome, IReadOnlyList<string> LeftOut, string? Why = null)
    {
        public static Result Of(Outcome outcome) => new(outcome, []);
        public static Result Failing(string why, IReadOnlyList<string>? leftOut = null) => new(Outcome.Failed, leftOut ?? [], why);
    }

    /// <summary>
    /// Commits the worktree at <paramref name="worktreePath"/> for report revision <paramref name="revision"/> when
    /// <see cref="ShouldCommit"/> says so. The report is known to have changed. A lock file left by a git of the app's own that
    /// did not finish in time is removed; one there before stops the commit.
    /// </summary>
    public static async Task<Result> Run(string worktreePath, string childId, int revision, string provider)
    {
        if (provider == "claude") return Result.Of(Outcome.Nothing);
        if (await DelegationGit.Run(["rev-parse", "--show-toplevel", "--absolute-git-dir"], worktreePath) is not { ExitCode: 0 } top) return Result.Failing("Git could not read the worktree.");
        var lines = top.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || !DelegationPaths.Same(DelegationPaths.Canonical(lines[0]), DelegationPaths.Canonical(worktreePath))) return Result.Failing("The child's folder is not its own git worktree.");
        var lockFile = Path.Combine(lines[1], "index.lock");
        if (await DelegationGit.Run(["ls-files", "-u"], worktreePath, Timeout) is not { ExitCode: 0 } unmerged
            || await DelegationGit.Run(["status", "--porcelain", "--untracked-files=normal"], worktreePath, Timeout) is not { ExitCode: 0 } status) return Result.Failing("Git could not read the worktree.");
        if (unmerged.Stdout.Length > 0) return Result.Of(Outcome.Unmerged);
        if (!ShouldCommit(provider, true, status.Stdout.Length > 0, false)) return Result.Of(Outcome.Nothing);
        if (File.Exists(lockFile)) return Result.Failing("Another git is working in the worktree (index.lock).");
        if (await DelegationGit.Run(["ls-files", "-z", "--others", "--exclude-standard"], worktreePath, Timeout) is not { ExitCode: 0 } others) return Result.Failing("Git could not read the worktree.");
        var fresh = others.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var leftOut = fresh.Where(path => LooksSecret(path) || Size(Path.Combine(worktreePath, path)) > MaximumNewFileBytes).ToArray();
        var taken = fresh.Where(path => !leftOut.Contains(path)).ToArray();
        Task<string?> Write(IReadOnlyList<string> arguments, string? input = null) => WriteStep(worktreePath, lockFile, arguments, input, Timeout);
        if (await Write(["add", "-u"]) is { } addFailed) return Result.Failing(addFailed, leftOut);
        if (taken.Length > 0 && await Write(["--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul"], string.Join('\0', taken)) is { } newFailed) return Result.Failing(newFailed, leftOut);
        if (await DelegationGit.Run(["diff", "--cached", "--quiet"], worktreePath, Timeout) is not { ExitCode: 0 or 1 } staged) return Result.Failing("Git could not read the worktree.", leftOut);
        // Only what was left out changed: nothing to commit.
        if (staged.ExitCode == 0) return new(Outcome.Nothing, leftOut);
        // An app commit never waits on a signing prompt or a hook.
        if (await Write(["-c", "commit.gpgsign=false", "commit", "-q", "--no-verify", "-m", Message(childId, revision)]) is { } commitFailed) return Result.Failing(commitFailed, leftOut);
        return new(Outcome.Committed, leftOut);
    }

    /// <summary>
    /// One git write of the commit: null when it went through, otherwise why not. Only a git the app itself killed on its
    /// deadline has its <c>index.lock</c> removed, since that lock is its own; a lock from any other failure (another git working
    /// there, one that never started, one that stopped reading its input) is left as it is.
    /// </summary>
    public static async Task<string?> WriteStep(string worktreePath, string lockFile, IReadOnlyList<string> arguments, string? input, double timeout)
    {
        var run = await DelegationGit.RunDetailed(arguments, worktreePath, timeout, input);
        switch (run.Failure)
        {
            case GitFailure.None: return run.Result!.ExitCode == 0 ? null : DelegationGit.Message(run.Result);
            case GitFailure.TimedOut:
                try { File.Delete(lockFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return "Git did not finish in time.";
            case GitFailure.NotStarted: return "Git could not be started.";
            case GitFailure.BrokenPipe: return run.Result is { ExitCode: not 0 } stopped ? DelegationGit.Message(stopped) : "Git stopped reading the list of new files.";
            default: return "Git did not finish.";
        }
    }

    /// <summary>The size of the file at <paramref name="path"/>, not following a link; 0 when unknown.</summary>
    private static long Size(string path)
    {
        try { var info = new FileInfo(path); return info.Exists && info.LinkTarget is null ? info.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }
}
