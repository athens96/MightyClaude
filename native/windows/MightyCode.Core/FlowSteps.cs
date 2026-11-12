using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace MightyCode.Core;

/// <summary>
/// One step of a request's checklist as the role view draws it (<see cref="FlowSteps"/>): the item Claude's task list holds (its TodoWrite list, or its
/// TaskCreate and TaskUpdate tasks) and the times the app saw it start and end. A time is when the app read the change from the stream, never a guess
/// (macOS FlowStep).
/// </summary>
public sealed record FlowStep(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("activeForm")] string? ActiveForm = null,
    [property: JsonPropertyName("status")] string Status = "pending",
    [property: JsonPropertyName("startedAt")] string? StartedAt = null,
    [property: JsonPropertyName("endedAt")] string? EndedAt = null)
{
    [JsonIgnore] public DateTimeOffset? Started => AgentRunTiming.Parse(StartedAt);
    [JsonIgnore] public DateTimeOffset? Ended => AgentRunTiming.Parse(EndedAt);
    [JsonIgnore] public bool IsPending => Status == "pending";
}

/// <summary>
/// A request's steps in the role view of a Claude pane: the checklist Claude keeps while it works. Each item that began is a card on the request's
/// spine (요청 → 1단계 → 2단계 … → 결과), with the sub-agents and background commands it launched during a step beside that step; every item stands in the
/// request's plan column, left of it. What a step holds is what the main session recorded between the step's start and its end (macOS FlowSteps).
/// </summary>
public static class FlowSteps
{
    /// <summary>What a Claude run is told on top of its system prompt (<c>--append-system-prompt</c>), so a request that does work has a plan for the
    /// role view to draw: its task list, kept up to date. A reply that needs no tool keeps no list and is drawn as request → result (Mac
    /// FlowSteps.planInstruction, the same words).</summary>
    /// <summary>Claude Code's task tools: run as the app runs it (stream-json input, prompts over stdio) it loads them only when
    /// <c>--allowedTools</c> names them (2.1.322). Mac FlowSteps.taskTools.</summary>
    public static readonly IReadOnlyList<string> TaskTools = ["TaskCreate", "TaskUpdate", "TaskList", "TaskGet"];

    public const string PlanInstruction = "Mighty Code draws your task list as this request's plan. Before any other tool call, create the task list of the steps you will take with TaskCreate (load it with ToolSearch first if it is not loaded yet; use TodoWrite only where the task tools are unavailable): one task per step, in the user's language, even when there are only one or two steps. Then mark each task in_progress with TaskUpdate when you start it and completed as soon as it is done, and add or remove tasks when the plan changes. Only a reply that needs no tool call at all needs no task list.";

    /// <summary>A step block's node suffix: <c>step:</c> and a hash of its key, so a node id never carries the item's words.</summary>
    public const string Suffix = "step:";
    /// <summary>From a spine block's trailing edge to the column of what it launched.</summary>
    public const double SideGap = 44;
    /// <summary>The 실행 card and a step's card: as wide as the request above them, so their one line reads.</summary>
    public const double CardWidth = MightyGraphCamera.RequestWidth, CardHeight = FlowCard.Height;

    public static string Key(TodoItem item) => item.Id is { } id ? "id:" + id : "text:" + item.Content;
    public static string NodeSuffix(FlowStep step) => Suffix + StableHash(step.Key);

    /// <summary>FNV-1a, 64 bits, in lowercase hex without leading zeros: the same on every run and every platform (Swift <c>String(hash, radix: 16)</c>).</summary>
    internal static string StableHash(string text)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text)) { hash ^= b; hash = unchecked(hash * 0x00000100000001b3UL); }
        return hash.ToString("x", CultureInfo.InvariantCulture);
    }

    internal static string Stamp(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The steps a run draws: its own, never a notice run's (a notice turn's changes are its request's).</summary>
    public static IReadOnlyList<FlowStep> Steps(MightyGraphRun run) => run.NoticeRun == true ? [] : run.Steps ?? [];

    // ── The progress panel ──

    /// <summary>The progress panel over the role view's top-left corner (Mac MightyGraphProgressPanel): the request the diagram shows, in brief,
    /// and how far its plan got. It stays where it is while the diagram scrolls under it, and reads the request in view.</summary>
    public const double ProgressWidth = 280, ProgressRowHeight = 24;
    /// <summary>The plan's lines at most: every step when they fit, else one fewer steps and a last line that counts the rest.</summary>
    public const int ProgressVisibleRows = 12;

    public enum ProgressState { Done, Current, Pending, Stopped, Failed }
    /// <summary>A step's line in the panel. <c>Index</c>: the step's index among the request's steps (<c>step</c> nodes).</summary>
    public sealed record ProgressRow(int Index, string Id, string Text, ProgressState State, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
    /// <summary>What the progress panel says (Mac FlowSteps.Progress). <c>Count</c>: "3/9", null for a request without a plan.</summary>
    public sealed record Progress(string Title, string Heading, string Request, string StateWord, string Status, string? Count,
        IReadOnlyList<ProgressRow> Rows, int Hidden, bool Running);

    /// <summary>The progress of request <c>runs[index]</c> (a request, never a notice run), headed <paramref name="heading"/> ("요청 27"): its request in
    /// brief, its state, and every step of its plan with its state, the one in progress kept in view when there are more than the panel holds. A
    /// request without a plan has no rows.</summary>
    public static Progress ProgressOf(int index, IReadOnlyList<MightyGraphRun> runs, string heading)
    {
        var request = runs[index];
        var steps = Steps(request);
        var activity = ActivityRuns(index, runs);
        var awaiting = BackgroundNoticeRuns.AwaitingNotice(request, activity) is not null;
        var running = activity.Any(r => !MightyGraphLayout.Terminal(r.Status)) || awaiting;
        var rows = steps.Select((step, offset) =>
        {
            var state = step.Status switch
            {
                "completed" => ProgressState.Done,
                "in_progress" => State(step, activity) switch
                {
                    "running" => ProgressState.Current,
                    "error" => ProgressState.Failed,
                    "completed" => ProgressState.Done,
                    _ => ProgressState.Stopped,
                },
                _ => ProgressState.Pending,
            };
            var words = state == ProgressState.Current ? step.ActiveForm ?? step.Content : step.Content;
            var ended = step.Ended ?? (state is ProgressState.Stopped or ProgressState.Failed ? ActivityEnd(index, runs) : null);
            return new ProgressRow(offset, step.Key, Title(offset, words), state, step.Started, ended);
        }).ToList();
        var room = rows.Count <= ProgressVisibleRows ? ProgressVisibleRows : ProgressVisibleRows - 1;
        var focus = rows.FindIndex(r => r.State == ProgressState.Current);
        if (focus < 0) focus = rows.FindLastIndex(r => r.State == ProgressState.Done);
        if (focus < 0) focus = 0;
        var start = rows.Count <= room ? 0 : Math.Max(0, Math.Min(focus - room / 3, rows.Count - room));
        var shown = rows.Skip(start).Take(room).ToList();
        var done = rows.Count(r => r.State == ProgressState.Done);
        var status = running ? "running" : MobileMightySupport.Status(request.Status);
        return new(Locale.Get("graph.progress.title"), heading, FlowCard.Line(FlowCard.FirstLine(request.Input)) ?? "",
            awaiting ? BackgroundNoticeRuns.StateWord : FlowCard.StateWord(status), awaiting ? "waiting" : status,
            rows.Count == 0 ? null : Locale.Get("graph.steps.count", new Dictionary<string, string> { ["done"] = done.ToString(CultureInfo.InvariantCulture), ["total"] = rows.Count.ToString(CultureInfo.InvariantCulture) }),
            shown, rows.Count - shown.Count, running);
    }

    /// <summary>The step a block launched from the main session belongs to, as its card says it ("2. 노트 작성"): the step whose span its launch time
    /// falls in. Null for one launched outside every step, one under another block, and in a run without steps (Mac FlowSteps.stepLabel).</summary>
    public static string? StepLabel(MightyGraphAgent agent, MightyGraphRun run)
    {
        if (agent.ParentID is { } parent && parent != agent.Id && run.FlowAgents.Any(a => a.Id == parent)) return null;
        var started = Steps(run).Select((step, index) => (step, index)).Where(p => !p.step.IsPending).ToList();
        if (started.Count == 0) return null;
        var slot = Slot(agent, started.Select(p => p.step).ToList());
        if (slot == 0) return null;
        var (chosen, at) = started[slot - 1];
        return Title(at, chosen.Content);
    }

    private static string Title(int index, string text) =>
        Locale.Get("graph.step.title", new Dictionary<string, string> { ["n"] = (index + 1).ToString(CultureInfo.InvariantCulture), ["text"] = text });

    // ── Recording ──

    /// <summary>
    /// A checklist change (<see cref="TodoProgress"/>, named by the request whose stream sent it) applied to that request's run: each item keeps the times
    /// of the step it was, an item that goes in progress starts now, one that is done ends now. <paramref name="previous"/> is the pane's list before the
    /// change: the items it already had done when this request first sent a list are the earlier request's, which Claude's task list keeps, and never
    /// this request's steps.
    /// </summary>
    public static void Record(TodoProgress progress, List<MightyGraphRun> runs, TodoProgress? previous, DateTimeOffset now)
    {
        if (progress.RunId is not { } runID) return;
        var index = runs.FindLastIndex(r => r.NoticeRun != true && (r.SourceRunID == runID || r.Id == runID));
        if (index < 0) return;
        var run = runs[index].Copy();
        if (run.Steps is null)
        {
            var carried = previous is { } before && before.RunId != runID ? before.Items.Where(i => i.Status == "completed").Select(Key).ToList() : [];
            run.StepsCarried = carried.Count == 0 ? null : carried;
        }
        var skip = new HashSet<string>(run.StepsCarried ?? [], StringComparer.Ordinal);
        var old = new Dictionary<string, FlowStep>(StringComparer.Ordinal);
        foreach (var step in run.Steps ?? []) old.TryAdd(step.Key, step);
        var time = Stamp(now);
        // The latest a step ended before this change: where a step marked done without going in progress began.
        var lastEnd = (run.Steps ?? []).Select(s => s.EndedAt).OfType<string>().OrderBy(e => AgentRunTiming.Parse(e) ?? DateTimeOffset.MinValue).LastOrDefault();
        var steps = new List<FlowStep>();
        foreach (var item in progress.Items)
        {
            var key = Key(item);
            if (skip.Contains(key)) continue;
            var step = (old.GetValueOrDefault(key) ?? new FlowStep(key, item.Content)) with { Content = item.Content, ActiveForm = item.ActiveForm };
            switch (item.Status)
            {
                case "in_progress":
                    step = step with { StartedAt = step.StartedAt is null || step.Status == "pending" ? time : step.StartedAt, EndedAt = null };
                    break;
                case "completed":
                    if (step.StartedAt is null) step = step with { StartedAt = lastEnd ?? run.RootEntries.FirstOrDefault()?.Timestamp ?? time };
                    if (step.EndedAt is null) { step = step with { EndedAt = time }; lastEnd = time; }
                    break;
                default:
                    step = step with { StartedAt = null, EndedAt = null };
                    break;
            }
            steps.Add(step with { Status = item.Status });
        }
        run.Steps = steps;
        runs[index] = run;
    }

    /// <summary>A checklist change of the main session's, kept on the request it belongs to with the time it came. Read before the pane's list is
    /// replaced with it (<see cref="ClaudePlanMode"/>).</summary>
    public static RunSession RecordSteps(RunSession session, RunEvent ev, DateTimeOffset now)
    {
        // Every agent that keeps a plan: Claude's task list, Codex's plan, an ACP agent's (opencode, Hermes) plan updates.
        if (ev.Type != "todos" || session.Kind != "claude" || TodoProgress.Normalized(ev.Todos) is not { } progress
            || session.GraphRuns is not { Count: > 0 } live) return session;
        var runs = live.ToList();
        Record(progress, runs, session.TodoProgress, now);
        return session with { GraphRuns = runs };
    }

    // ── What a step holds ──

    /// <summary>The step of <paramref name="steps"/> whose span holds <paramref name="time"/>, the one that started last when spans meet; null for the main
    /// session's own time between steps. A step's start counts from its whole second, as the records' times do.</summary>
    public static int? StepIndex(DateTimeOffset time, IReadOnlyList<FlowStep> steps)
    {
        (int Index, DateTimeOffset Start)? best = null;
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].Started is not { } start) continue;
            if (Math.Floor(start.ToUnixTimeMilliseconds() / 1000.0) > time.ToUnixTimeMilliseconds() / 1000.0) continue;
            if (steps[index].Ended is { } end && end < time) continue;
            if (best is null || start >= best.Value.Start) best = (index, start);
        }
        return best?.Index;
    }

    /// <summary>Where on the spine a block launched from the main session stands: 0 beside the request, k beside the k-th of <paramref name="steps"/>.</summary>
    public static int Slot(MightyGraphAgent agent, IReadOnlyList<FlowStep> steps) =>
        agent.Figures?.Started is { } start && StepIndex(start, steps) is { } index ? index + 1 : 0;

    /// <summary>The runs of the activity <c>runs[index]</c> heads: the request and the notice runs that answer for its work.</summary>
    public static List<MightyGraphRun> ActivityRuns(int index, IReadOnlyList<MightyGraphRun> runs)
    {
        var request = runs[index];
        if (request.SourceRunID is not { } source) return [request];
        return [request, .. runs.Where(r => r.NoticeRun == true && r.SourceRunID is { } id && BackgroundNoticeRuns.OriginalRunID(id) == source)];
    }

    /// <summary>What the main session recorded during step <paramref name="step"/> of <c>runs[index]</c>: in the request and in the notice turns after it.</summary>
    public static List<LogEntry> Entries(int step, int index, IReadOnlyList<MightyGraphRun> runs)
    {
        var steps = Steps(runs[index]);
        if (step < 0 || step >= steps.Count) return [];
        return ActivityRuns(index, runs).SelectMany(r => r.RootEntries)
            .Where(entry => AgentRunTiming.Parse(entry.Timestamp) is { } time && StepIndex(time, steps) == step).ToList();
    }

    // ── Cards ──

    /// <summary>The newest thing the main session did among <paramref name="entries"/>, as a card's one line: what it last said (prose), or the tool call it
    /// made after that (a command, as the transcript words it).</summary>
    internal static (string Text, bool Prose)? Latest(IReadOnlyList<LogEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.Activity is { } activity)
            {
                if (activity.Kind == "turn") continue;
                if (FlowCard.Line(activity.Summary.Length == 0 ? activity.ToolName ?? "" : activity.Summary) is { } line) return (line, false);
            }
            else if (entry.Kind == "assistant" && Prose(FlowCard.LastLine(entry.Text)) is { } said) return (said, true);
        }
        return null;
    }

    /// <summary>A line of Claude's reply without the markdown marks it is written in.</summary>
    internal static string? Prose(string raw)
    {
        var text = raw.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        var start = 0;
        while (start < text.Length && "#>-* ".Contains(text[start])) start++;
        return FlowCard.Line(text[start..]);
    }

    private static int Calls(IEnumerable<LogEntry> entries) => entries.Count(e => e.Activity is { } a && a.Kind != "turn");

    /// <summary>A step as its card: its number and words, the main session's model, what it is doing (or did) last, its state, time and tool calls. A step
    /// not begun says so and has no figures.</summary>
    public static FlowCard.Content? Card(int step, int index, IReadOnlyList<MightyGraphRun> runs)
    {
        var request = runs[index];
        var steps = Steps(request);
        if (step < 0 || step >= steps.Count) return null;
        var item = steps[step];
        var entries = Entries(step, index, runs);
        var status = State(item, ActivityRuns(index, runs));
        var running = !MightyGraphLayout.Terminal(status) && !item.IsPending;
        var newest = Latest(entries) ?? (running && item.ActiveForm is { } form && FlowCard.Line(form) is { } line ? (line, true) : null);
        var activity = item.IsPending ? null : newest;
        var ended = item.Ended ?? (MightyGraphLayout.Terminal(status) && !item.IsPending ? ActivityEnd(index, runs) : null);
        return new(Title(step, item.Content), FlowLog.LeadName(request), activity?.Text, item.IsPending ? "pending" : status,
            item.IsPending ? Locale.Get("graph.state.pending") : FlowCard.StateWord(status), item.Started, ended, Calls(entries), null, 0, [],
            FlowRoles.Ink(FlowRole.Lead), item.Content, IsPending: item.IsPending, Prose: activity?.Prose ?? false);
    }

    /// <summary>The state a step's card shows: done as the list says; in progress, the state of its activity (still at work, else how it ended).</summary>
    internal static string State(FlowStep step, IReadOnlyList<MightyGraphRun> activity)
    {
        if (step.Status == "completed") return "completed";
        if (activity.Any(r => !MightyGraphLayout.Terminal(r.Status))) return "running";
        if (activity.Count > 0 && BackgroundNoticeRuns.AwaitingNotice(activity[0], activity) is not null) return "running";
        return MobileMightySupport.Status(activity.Count > 0 ? activity[^1].Status : "completed");
    }

    /// <summary>When the activity's last turn ended: the last thing it recorded.</summary>
    internal static DateTimeOffset? ActivityEnd(int index, IReadOnlyList<MightyGraphRun> runs) =>
        ActivityRuns(index, runs).Select(r => (r.ResultEntries.LastOrDefault() ?? r.RootEntries.LastOrDefault()) is { } last ? AgentRunTiming.Parse(last.Timestamp) : null)
            .OfType<DateTimeOffset>().DefaultIfEmpty().Max() is var end && end != default ? end : null;

    /// <summary>The 실행 block as its card: the main session's model, what it is doing (or answered), its state, time, tool calls and tokens. Running: the
    /// newest thing it said or did; ended: the first line of its answer.</summary>
    public static FlowCard.Content LeadCard(MightyGraphRun run, string title)
    {
        var status = MobileMightySupport.Status(run.Status);
        var running = !MightyGraphLayout.Terminal(status);
        var current = Steps(run).FirstOrDefault(s => s.Status == "in_progress") is { } step && FlowCard.Line(step.ActiveForm ?? step.Content) is { } words ? words : null;
        (string Text, bool Prose)? answer = run.FinalOutput is { } output && Prose(FlowCard.FirstLine(output)) is { } first ? (first, true) : null;
        var line = running ? Latest(run.RootEntries) ?? (current is null ? null : (current, true)) : answer ?? Latest(run.RootEntries);
        var started = run.RootEntries.FirstOrDefault() is { } head ? AgentRunTiming.Parse(head.Timestamp) : null;
        var ended = running ? null : (run.ResultEntries.LastOrDefault() ?? run.RootEntries.LastOrDefault()) is { } last ? AgentRunTiming.Parse(last.Timestamp) : null;
        return new(title, FlowLog.LeadName(run), line?.Text, status, FlowCard.StateWord(status), started, ended, Calls(run.RootEntries),
            run.Usage is { IsEmpty: false } usage ? usage.Total : null, 0, [], FlowRoles.Ink(FlowRole.Lead), FlowLog.LeadName(run), Prose: line?.Prose ?? false);
    }
}
