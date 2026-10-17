using System.Threading.Channels;

namespace MightyClaude.Core;

/// <summary>
/// The tools the delegation MCP server lists (M/DelegationCoordinator.swift:3-81): exactly these six. The server is attached
/// only to a Claude or Codex pane's run while the <see cref="DelegationSwitch"/> is on, so with the switch on such a pane sees
/// these and with it off none. Every argument is a string, and every one is required but delegate's agent. The words are the Mac's, byte for byte: the agent reads them.
/// </summary>
public static class DelegationToolManifest
{
    public sealed record Argument(string Name, string Description, bool Required = true);
    /// <param name="Arguments">All strings, no others.</param>
    public sealed record Tool(string Name, string Description, IReadOnlyList<Argument> Arguments);

    private static readonly Argument Child = new("child", "The child's id, as delegate or list_children gave it.");

    public static readonly Tool Delegate = new("delegate",
        "Start a child pane that works on a task in its own git worktree, on the flat branch mighty/<child id> made from your current branch head. It runs your own agent CLI unless agent names another: claude, codex or antigravity, each only if installed and signed in on this PC. Use it only when the user asks for parallel or delegated work, or when the task clearly splits into independent parts that can run at the same time; never fan out speculatively, and do the work yourself otherwise. The child starts in the permission mode you ask for, which must be one of the child agent's modes and one in which it can change files. For your own agent it must be no wider than your own mode (Claude: plan < manual < acceptEdits < auto < fullAccess; Codex: manual < acceptEdits < onRequest < fullAccess). For another agent, the child's mode may do nothing yours may not: edit files without asking only if yours does (Claude acceptEdits and auto, Codex onRequest and acceptEdits, Antigravity acceptEdits, any fullAccess), edit after asking only if yours edits at all, run shell commands without asking only if yours does (Claude auto, Codex manual and acceptEdits, any fullAccess), and have full access only from your fullAccess. So from Claude acceptEdits a Codex child gets onRequest, not acceptEdits (which runs commands without asking), and from Claude plan or manual no Codex or Antigravity child can start. Answers as soon as the child exists; its run goes on in the background. The child writes its result to REPORT.md, and you get one notice per report. At most 3 open children per pane, of all agents together, and a child cannot delegate. The same call twice in one of your runs returns the same child.",
        [new("task", "What the child should do. It is saved as the child's TASK.md."),
         new("mode", "The child's starting permission mode, one of the child agent's in which it can change files, no wider than yours: plan, manual, acceptEdits, auto or fullAccess for a Claude child; acceptEdits, onRequest or fullAccess for a Codex child; acceptEdits or fullAccess for an Antigravity child."),
         new("agent", "Optional: the agent CLI the child runs, claude, codex or antigravity. Leave it out for your own agent.", Required: false)]);

    public static readonly Tool ListChildren = new("list_children", "List your children with their id, agent, state and current report revision.", []);

    public static readonly Tool ChildStatus = new("child_status", "Read one of your children: its agent, its state, its head and the body of its latest REPORT.md.", [Child]);

    public static readonly Tool Merge = new("merge",
        "Fast-forward your recorded branch to a reported child's head. Refused, changing nothing, unless the child has reported at exactly expected_head with no tracked changes, your recorded branch is checked out with no tracked changes and it can fast-forward. Follows your permission mode; a Codex pane may merge only when its run started in fullAccess and is otherwise refused with merge_human_only, so the human merges from the child's card.",
        [Child, new("expected_head", "The child's reported head commit, as child_status gave it.")]);

    public static readonly Tool FollowUp = new("follow_up",
        "Send one of your children a follow-up instruction, at most 2 per child. It reaches the child exactly once, as its next run, and clears its reported state.",
        [Child, new("text", "The instruction for the child.")]);

    public static readonly Tool Discard = new("discard",
        "Discarding a child is for the human only, from the child's card in the app. This tool always refuses with discard_human_only and changes nothing.", [Child]);

    /// <summary>All six in this order. Exactly this many; no more, no fewer.</summary>
    public static readonly IReadOnlyList<Tool> All = [Delegate, ListChildren, ChildStatus, Merge, FollowUp, Discard];

    public static Tool? Named(string name) => All.FirstOrDefault(tool => tool.Name == name);
}

/// <summary>A child pane the coordinator asks the host to open (M/DelegationCoordinator.swift:83-97).</summary>
/// <param name="SessionId">Chosen by the coordinator, so the branch <c>mighty/&lt;session id&gt;</c> and the worktree exist before the pane does.</param>
/// <param name="Mode">The starting permission mode the parent asked for, written once.</param>
/// <param name="Folder">The folder the child works in, inside its worktree.</param>
/// <param name="StartedByHuman">The human started it from the parent's composer: it takes the parent's model and effort, and the app shows it at once.</param>
/// <param name="Provider">The agent CLI the child runs: its parent's.</param>
public sealed record DelegationChildPane(string SessionId, string ParentSessionId, string Mode, string Folder, bool StartedByHuman = false, string Provider = "claude");

/// <summary>What a pane is doing, as delivery needs to know it (M/DelegationCoordinator.swift:99-109).</summary>
public enum DelegationPaneActivity
{
    /// <summary>A run is going.</summary>
    Running,
    /// <summary>Idle after a run that finished normally since this launch, and a run can start in it now.</summary>
    Finished,
    /// <summary>Idle any other way: stopped, errored, no run since this launch, or no run can start in it now.</summary>
    Idle,
}

/// <summary>One open pane as the host sees it now (M/DelegationCoordinator.swift:111-130).</summary>
/// <param name="PermissionMode">The pane's stored permission mode.</param>
/// <param name="Folder">The folder its runs work in.</param>
/// <param name="ParentSessionId">The parent's session id when this pane is a delegated child.</param>
/// <param name="RunId">The run going now, or the last one; null before the first.</param>
public sealed record DelegationPaneState(string SessionId, string PermissionMode, string Folder, string Kind = "claude", string Provider = "claude", string? ParentSessionId = null,
    string? RunId = null, DelegationPaneActivity Activity = DelegationPaneActivity.Idle);

/// <summary>
/// The app side of delegation (M/DelegationCoordinator.swift:132-150): the desktop service in the app, a fake in tests. The
/// coordinator reaches panes only through this, and calls it from the thread pool, never from its own turn.
/// </summary>
public interface IDelegationHost
{
    /// <summary>Opens the child's pane of <c>pane.Provider</c> in <c>pane.Mode</c>, working in <c>pane.Folder</c> and linked to its parent. False when no pane could be made.</summary>
    Task<bool> CreatePane(DelegationChildPane pane);
    /// <summary>Starts a run with <paramref name="input"/> in the pane: the new run's id, or null when none started.</summary>
    Task<string?> StartRun(string sessionId, string input);
    /// <summary>
    /// Hands <paramref name="input"/> to the pane by <paramref name="route"/>: steered into its running Claude run, or as its
    /// next run. The id of the run that got it, or null when it did not get it.
    /// </summary>
    Task<string?> Deliver(string input, string sessionId, DeliveryRoute route);
    /// <summary>The pane now, or null when it is closed.</summary>
    Task<DelegationPaneState?> PaneState(string sessionId);
    /// <summary>Stops the pane's run, if one is going, and returns once it has ended. Does nothing for an idle or closed pane.</summary>
    Task StopRun(string sessionId);
    /// <summary>Shows <paramref name="text"/> in the pane's transcript as the app's note. Does nothing for a closed pane; a host that shows no notes keeps this.</summary>
    Task Note(string text, string sessionId) => Task.CompletedTask;
}

/// <summary>
/// The children whose run end is reported but not yet recorded (M/DelegationCoordinator.swift ChildRunsEnding): the host marks
/// one as it reports the end, and the coordinator clears it once the end is recorded. A child reported twice before the first is
/// recorded stays marked until both are. Until then delivery hands the child nothing and the app starts no run in its pane, so
/// the app's commit of a Codex or Antigravity child's work and its head read come first.
/// </summary>
public sealed class ChildRunsEnding
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> counts = [];

    /// <summary>The host is about to report an end of the child <paramref name="id"/>'s run.</summary>
    public void Begin(string id) { lock (gate) counts[id] = counts.GetValueOrDefault(id) + 1; }

    public bool Contains(string id) { lock (gate) return counts.ContainsKey(id); }

    /// <summary>One reported end of the child <paramref name="id"/> is recorded; false when none was marked.</summary>
    internal bool Finish(string id)
    {
        lock (gate)
        {
            if (!counts.TryGetValue(id, out var count)) return false;
            if (count > 1) counts[id] = count - 1; else counts.Remove(id);
            return true;
        }
    }
}

/// <summary>
/// The app's delegation service (M/DelegationCoordinator.swift:152-311). It owns the child records (the delegation file is their
/// authority), the six tools, delivery and the git actions, and reaches panes only through its <see cref="IDelegationHost"/>.
/// Each call is answered by the first rule that applies:
/// 1. The gates of <see cref="DelegationIOHandler"/>: switched off or a run without the server reaches nothing; a pane whose
///    agent has no delegation tools is refused with <c>claude_only</c>.
/// 2. A name that is not one of the six tools reaches nothing.
/// 3. <c>discard</c> is always refused with <c>discard_human_only</c>, whatever it names: only a human discards, from the child's card.
/// 4. A call without exactly the tool's string arguments is answered with an error and changes nothing.
/// 5. Otherwise the tool runs, and its answer comes within <see cref="AnswerSeconds"/>.
/// Where the Mac's is an actor, this one runs its work in turns on one exclusive scheduler: between two awaits nothing else of
/// it runs, and at an await another call may take its turn, exactly as there. Its methods may be called from any thread.
/// </summary>
public sealed partial class DelegationCoordinator : IDelegationRequestHandler
{
    /// <summary>The longest any call takes to answer, well inside the limit of a tool call's transport.</summary>
    public const double AnswerSeconds = 45;

    public DelegationFileStore Store { get; }
    private readonly IDelegationHost host;
    /// <summary>Makes each child's worktree: under <c>%USERPROFILE%\.mightyclaude\worktrees</c> in the app, under a temp folder in tests.</summary>
    private readonly ChildWorktreeMaker worktrees;
    /// <summary>Makes each child's worktree: the composer asks it whether a folder can host one (<see cref="ChildWorktreeCheck.CanHostWorktree"/>).</summary>
    public ChildWorktreeMaker Worktrees => worktrees;
    /// <summary>
    /// The children whose run end the host reported and this coordinator has not recorded yet. Until it is (the app's commit of a
    /// Codex or Antigravity child's work and its head read among it), delivery hands the child nothing and the app starts no run in its pane.
    /// </summary>
    public ChildRunsEnding EndingRuns { get; } = new();
    /// <summary>
    /// The report revision at which each child was last asked to resolve its conflict with the parent branch, by child id, so a
    /// card's merge that conflicts asks once per report and never loops (M/DelegationCoordinator.swift conflictAsked).
    /// </summary>
    private readonly Dictionary<string, int> conflictAsked = [];
    private readonly Func<bool> isSwitchOn;
    private readonly double answerSeconds;
    private readonly TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
    private readonly TaskFactory turn;
    private volatile DelegationFile file;
    private readonly Channel<IReadOnlyList<DelegationChildRow>> childRows = Latest<IReadOnlyList<DelegationChildRow>>();
    private readonly Channel<IReadOnlyDictionary<string, IReadOnlyList<DelegationDeliveryItem>>> heldRows = Latest<IReadOnlyDictionary<string, IReadOnlyList<DelegationDeliveryItem>>>();
    private IReadOnlyList<DelegationChildRow> publishedChildRows;
    private IReadOnlyDictionary<string, IReadOnlyList<DelegationDeliveryItem>> publishedHeldRows;
    /// <summary>Children whose start is still going: delegate saved their record and their start has not finished. A discard waits for it.</summary>
    private readonly HashSet<string> starting = [];
    /// <summary>A merge (the tool's or a card's) or a card's undo or discard: they take turns, so two never run git in a parent's checkout at once.</summary>
    private readonly Turns mergeTurns = new();
    /// <summary>
    /// The panes whose checkout a card's merge or undo is changing: the parent's, and the child's during a merge. Delivery
    /// starts no run in them until it is over and offers their items again then, so an undo finds its parent idle from start to end.
    /// </summary>
    private readonly HashSet<string> cardPanes = [];
    /// <summary>A run event of a child's pane (its start, its end, the parent awaiting it, or the launch's sweep): each is recorded whole before the next.</summary>
    private readonly Turns runTurns = new();
    /// <summary>Whether a delivery pass is offering pending items to their panes, and whether another pass was asked for meanwhile.</summary>
    private bool isDelivering, wantsDelivery;

    /// <summary>Loads the delegation file from <paramref name="store"/>; throws when it is unreadable.</summary>
    public DelegationCoordinator(DelegationFileStore store, IDelegationHost host, ChildWorktreeMaker? worktrees = null, Func<bool>? isSwitchOn = null, double answerSeconds = AnswerSeconds)
    {
        Store = store; this.host = host; this.worktrees = worktrees ?? new ChildWorktreeMaker(); this.isSwitchOn = isSwitchOn ?? DelegationSwitch.IsOn;
        this.answerSeconds = Math.Min(answerSeconds, AnswerSeconds); turn = new TaskFactory(scheduler);
        file = store.Load();
        // What the last launch left pending is held, never delivered on its own. The move only shortens the file, so it always fits.
        if (file.HoldingPending() is { } held)
        {
            file = held;
            try { store.Save(file); } catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { }
        }
        publishedChildRows = DelegationSidebar.Rows(file); childRows.Writer.TryWrite(publishedChildRows);
        publishedHeldRows = file.HeldItemsByPane(); heldRows.Writer.TryWrite(publishedHeldRows);
    }

    /// <summary>
    /// The delegation file as last loaded or saved. The exceptions: a child's start, a merge or an undo that the disk refused
    /// to save is held here until the next save writes it.
    /// </summary>
    public DelegationFile File
    {
        get => file;
        private set { file = value; PublishChildRows(); PublishHeldRows(); }
    }

    /// <summary>
    /// The children as the sidebar lists them: the list at load, then the list after each change to it, in order. Only the
    /// newest waits when they come faster than they are read. For one reader, the app.
    /// </summary>
    public ChannelReader<IReadOnlyList<DelegationChildRow>> ChildRows => childRows.Reader;

    /// <summary>
    /// The items held for each pane, oldest first, as its queued list shows them: the map at load, then the map after each
    /// change to it, in order. Only the newest waits. For one reader, the app.
    /// </summary>
    public ChannelReader<IReadOnlyDictionary<string, IReadOnlyList<DelegationDeliveryItem>>> HeldRows => heldRows.Reader;

    private static Channel<T> Latest<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private void PublishChildRows()
    {
        var rows = DelegationSidebar.Rows(file);
        if (rows.SequenceEqual(publishedChildRows)) return;
        publishedChildRows = rows; childRows.Writer.TryWrite(rows);
    }

    private void PublishHeldRows()
    {
        var rows = file.HeldItemsByPane();
        if (rows.Count == publishedHeldRows.Count && rows.All(pane => publishedHeldRows.TryGetValue(pane.Key, out var held) && held.SequenceEqual(pane.Value))) return;
        publishedHeldRows = rows; heldRows.Writer.TryWrite(rows);
    }

    public async Task<DelegationResponse> Handle(DelegationRequest request, DelegationCaller caller)
    {
        if (DelegationIOHandler.Gate(caller, isSwitchOn()) is { } gated) return gated;
        if (DelegationToolManifest.Named(request.Tool) is not { } tool) return DelegationResponse.Failure(DelegationIOHandler.UnknownToolMessage(request.Tool));
        if (tool == DelegationToolManifest.Discard) return DelegationResponse.Refusal(DelegationReasonCode.DiscardHumanOnly);
        if (ArgumentProblem(request.Arguments, tool) is { } problem) return DelegationResponse.Failure(problem);
        var late = DelegationResponse.Failure(LateMessage(tool.Name, answerSeconds));
        return await Answer(answerSeconds, late, () => Isolated(() => Perform(tool, request.Arguments, caller))).ConfigureAwait(false);
    }

    /// <summary>Runs one checked call from the pane <paramref name="caller"/>.</summary>
    private Task<DelegationResponse> Perform(DelegationToolManifest.Tool tool, IReadOnlyDictionary<string, string> arguments, DelegationCaller caller)
    {
        string Value(string name) => arguments.GetValueOrDefault(name) ?? "";
        if (tool == DelegationToolManifest.Delegate) return DelegateNow(Value("task"), Value("mode"), caller);
        if (tool == DelegationToolManifest.ListChildren) return Task.FromResult(ListChildren(caller));
        if (tool == DelegationToolManifest.ChildStatus) return Task.FromResult(ChildStatus(Value("child"), caller));
        if (tool == DelegationToolManifest.Merge) return MergeNow(Value("child"), Value("expected_head"), caller);
        if (tool == DelegationToolManifest.FollowUp) return FollowUpNow(Value("child"), Value("text"), caller);
        // discard, which Handle already refused: only a human discards.
        return Task.FromResult(DelegationResponse.Refusal(DelegationReasonCode.DiscardHumanOnly));
    }

    public static string LateMessage(string tool, double seconds) =>
        $"{tool} did not finish within {(int)seconds} seconds. Its work goes on in Mighty Claude; call list_children to see where your children stand.";

    /// <summary>Why <paramref name="arguments"/> are not <paramref name="tool"/>'s (every required one, and no other), or null when they are.</summary>
    public static string? ArgumentProblem(IReadOnlyDictionary<string, string> arguments, DelegationToolManifest.Tool tool)
    {
        var names = tool.Arguments.Select(argument => argument.Name).ToList();
        var required = tool.Arguments.Where(argument => argument.Required).Select(argument => argument.Name).ToList();
        if (arguments.Keys.All(names.Contains) && required.All(arguments.ContainsKey)) return null;
        if (names.Count == 0) return $"{tool.Name} takes no arguments.";
        var optional = tool.Arguments.Where(argument => !argument.Required).Select(argument => argument.Name).ToList();
        return $"{tool.Name} takes exactly these string arguments: {string.Join(", ", required)}" + (optional.Count == 0 ? "." : $", and optionally {string.Join(", ", optional)}.");
    }

    /// <summary>
    /// <paramref name="work"/>'s answer, or <paramref name="late"/> once <paramref name="seconds"/> pass first. The work is not
    /// cancelled: it finishes in the background and keeps what it records, but its answer is dropped, so a call is answered exactly once.
    /// </summary>
    public static async Task<DelegationResponse> Answer(double seconds, DelegationResponse late, Func<Task<DelegationResponse>> work)
    {
        using var stop = new CancellationTokenSource();
        var answer = Task.Run(work); var timer = Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds)), stop.Token);
        var first = await Task.WhenAny(answer, timer).ConfigureAwait(false);
        stop.Cancel();
        // Work that failed is answered as a call that reached no answer: the call is still answered exactly once.
        return first != answer ? late : answer.IsCompletedSuccessfully ? answer.Result : DelegationResponse.Failure(NoAnswerMessage);
    }

    public const string NoAnswerMessage = "Mighty Claude did not answer the request.";

    /// <summary>
    /// Runs <paramref name="work"/> in the coordinator's turn. Called from one of its own turns it simply goes on; from anywhere
    /// else it waits its turn, and the caller goes on from the thread pool afterwards, never inside the coordinator's turn.
    /// </summary>
    private Task<T> Isolated<T>(Func<Task<T>> work)
    {
        if (TaskScheduler.Current == scheduler) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        turn.StartNew(async () =>
        {
            try { done.SetResult(await work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private Task Isolated(Func<Task> work) => Isolated(async () => { await work(); return true; });

    /// <summary>
    /// Calls the host from the thread pool, and comes back into the coordinator's turn with its answer. A host call that throws
    /// is a call the host did not take: no pane made, no run started, nothing delivered, no pane state (a closed pane).
    /// </summary>
    private static Task<T?> Host<T>(Func<Task<T>> call) => Task.Run<T?>(async () =>
    {
        try { return await call(); } catch (Exception ex) when (ex is not OutOfMemoryException) { return default; }
    });
    private static Task Host(Func<Task> call) => Task.Run(async () =>
    {
        try { await call(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    });

    /// <summary>Whoever asks first holds the turn; the others wait here, first come first, and each is handed it in order.</summary>
    private sealed class Turns
    {
        private bool taken;
        private readonly Queue<TaskCompletionSource> waiting = new();

        public Task Take()
        {
            if (!taken) { taken = true; return Task.CompletedTask; }
            // The turn is handed over still taken.
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); waiting.Enqueue(next);
            return next.Task;
        }

        public void Pass() { if (waiting.Count == 0) taken = false; else waiting.Dequeue().SetResult(); }
    }
}
