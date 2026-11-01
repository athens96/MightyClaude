using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>The fixed words and ceilings of the m1 extension (M/MightyCore/Remote/MobileRemoteModels.swift:61-103).</summary>
public static class MobileWire
{
    public const string CliStyle = "cli";
    /// <summary>The old <c>mightyStyle</c> field's words; v7 dropped the removed style's, so an older phone that still offers it is refused.</summary>
    public static readonly string[] MightyStyles = [CliStyle, "ouroboros"];
    /// <summary>The newest entries a session detail carries; older ones are paged with <c>entries?before=</c>.</summary>
    public const int MaximumEntries = 80;
    public const int DefaultPageLimit = 50, MaximumPageLimit = 100;
    /// <summary>A block's <c>output</c> and the newest settled run's <c>result</c>, counted in characters.</summary>
    public const int MaximumBlockOutput = 2_000, MaximumRunResult = 20_000;
    /// <summary>The most entries a run's <c>resultFiles</c> carries: the first ones of the desktop panel's list (up to <see cref="ResultFiles.MaximumResultFiles"/>).</summary>
    public const int MaximumRunResultFiles = 50;
    /// <summary>A block's own prompt, and each of its activity lines, in bytes.</summary>
    public const int MaximumBlockSummary = 1_000;
    public const int MaximumBlockActivity = 8;
    /// <summary>How many of a pane's newest runs the Mighty payload carries.</summary>
    public const int MightyRuns = 20;
    public const int MaximumText = 32_768;
    /// <summary>The largest reply body the host sends: the relay closes a frame over 1 MiB (docs/relay.md), and the envelope and the seal take a little of that.</summary>
    public const int MaximumReplyBytes = 1_024 * 1_024 - 32 * 1_024;
}

/// <summary>One block of the Mighty graph as a phone draws it: the request itself (<c>main</c>) or one of its children.</summary>
/// <param name="Background"><c>true</c> for a background sub-agent and every task block; absent otherwise.</param>
/// <param name="OutputTail">A task block's last output lines (at most <see cref="BackgroundOutputTail.MaxLines"/>), read on the host; absent when none were read. The output file itself is never sent.</param>
/// <param name="NoticeReply">Claude's reply to the task's notice, bounded like <c>Output</c>.</param>
public sealed record MobileBlock(string Id, string Kind, string Title, string Status, string? Summary = null, string? Output = null, double? DurationMs = null, string? NodeModelLabel = null, IReadOnlyList<string>? Activity = null,
    bool? Background = null, IReadOnlyList<string>? OutputTail = null, string? NoticeReply = null);
/// <summary>One request of the pane. <c>Result</c> is carried only by the newest request and the notice runs after it once each has
/// settled. <c>NoticeRun</c> is <c>true</c> for Claude's reply to finished background work (<see cref="MightyGraphRun.NoticeRun"/>): not a
/// request, so it takes no request number; its <c>Title</c> and its <c>main</c> block's are its heading (<see cref="BackgroundNoticeRuns.Heading(MightyGraphRun)"/>).
/// Absent otherwise. <c>BackgroundWaiting</c> is the background work a request started that still runs after its own turn
/// (<see cref="MightyGraphSupport.BackgroundWaiting"/>): its result card reads 현재까지의 결과 meanwhile. Absent when none (and from older hosts).
/// <c>ResultFiles</c> is the desktop's 결과에 나온 파일 list for a run that carries <c>Result</c> and completed (<see cref="MobileMightySupport.RunResultFiles"/>).
/// Absent when it names none, on a workspace of another computer, and from older hosts.</summary>
public sealed record MobileMightyRun(string Id, string Input, string? Title, string Status, IReadOnlyList<MobileBlock> Blocks, string? Result = null, bool? NoticeRun = null,
    int? BackgroundWaiting = null, IReadOnlyList<MobileResultFile>? ResultFiles = null);
/// <summary>One file a run's final result names: <c>Path</c> relative to the workspace root, joined with "/" — what
/// <c>GET /m1/workspaces/{id}/file?path=</c> takes — and the line the result pointed at, if any.</summary>
public sealed record MobileResultFile(string Path, int? Line = null);

/// <summary>
/// One pane's <c>resultFiles</c>, kept per run until that run's result or the workspace folder changes, so a session reply built
/// for every long poll does not look through the workspace again (the desktop panel keeps its list the same way,
/// MainWindow.MightyGraph.cs <c>graphResultFilesByRun</c>). <see cref="MobileMightySupport.Runs"/> drops the runs it no longer looks up,
/// so the memo never holds more than the runs that carry a result; it goes with its pane.
/// </summary>
public sealed class MobileResultFilesMemo(Func<MightyGraphRun, string, IReadOnlyList<MobileResultFile>?>? lookThrough = null)
{
    private readonly Func<MightyGraphRun, string, IReadOnlyList<MobileResultFile>?> lookThrough = lookThrough ?? MobileMightySupport.LookThrough;
    private readonly Dictionary<string, (int Key, IReadOnlyList<MobileResultFile>? Files)> byRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> used = new(StringComparer.Ordinal);

    /// <summary>The runs whose list is kept.</summary>
    public int Count { get { lock (byRun) return byRun.Count; } }

    /// <summary>What a result is read from, as the desktop panel's key: the folder, the result entries and the whole final answer.</summary>
    private static int Key(MightyGraphRun run, string root)
    {
        var hash = new HashCode(); hash.Add(root); hash.Add(run.ResultEntries.Count); hash.Add(run.ResultEntries.LastOrDefault()?.Id);
        foreach (var entry in run.ResultEntries) hash.Add(entry.Text);
        hash.Add(run.FinalOutput);
        return hash.ToHashCode();
    }

    internal IReadOnlyList<MobileResultFile>? Files(MightyGraphRun run, string root)
    {
        var key = Key(run, root);
        lock (byRun)
        {
            used.Add(run.Id);
            if (byRun.TryGetValue(run.Id, out var kept) && kept.Key == key) return kept.Files;
        }
        var files = lookThrough(run, root);
        lock (byRun) byRun[run.Id] = (key, files);
        return files;
    }

    /// <summary>Drops every run not looked up since the last prune.</summary>
    internal void Prune()
    {
        lock (byRun)
        {
            foreach (var id in byRun.Keys.Where(id => !used.Contains(id)).ToArray()) byRun.Remove(id);
            used.Clear();
        }
    }
}

/// <summary>
/// One background task still running in a pane (the detail's <c>backgroundWork</c>): a row of the background strip. Its output file's
/// path never travels; only the last lines the host read of it do (M/MightyCore/Remote/MobileRemoteModels.swift MobileBackgroundWork).
/// </summary>
/// <param name="Kind">"agent" (a background sub-agent) or "run" (a background command).</param>
/// <param name="Status">Always "running": ended work is in the notice run's diagram.</param>
/// <param name="StartedAt">ISO 8601, as the CLI reported the start.</param>
/// <param name="Command">The command a run runs, or the first line of a sub-agent's prompt.</param>
/// <param name="OutputTail">A run's last output lines (at most 6).</param>
public sealed record MobileBackgroundWork(string Id, string Kind, string Title, string Status, string StartedAt, string? Command = null, IReadOnlyList<string>? OutputTail = null)
{
    public const int MaximumTitle = 1_000;
    public const int MaximumCommand = 2_000;
}

/// <summary>
/// The Mighty graph as a phone reads it (M/MightyCore/Remote/MobileRemoteSupport.swift:190-384). Every status that reaches
/// the wire is one of the contract's five words: a status the table does not name is mapped to the nearest one, never forwarded.
/// </summary>
public static class MobileMightySupport
{
    public static string Status(string raw) => raw switch
    {
        "completed" => "completed", "error" or "failed" => "error", "stopped" or "cancelled" or "interrupted" => "stopped", "waiting" => "waiting", _ => "running",
    };
    public static bool Settled(string status) => status is "completed" or "error" or "stopped";

    /// <summary>The first <paramref name="count"/> characters as a reader counts them, so a glyph of several code units is never cut in half.</summary>
    public static string Prefix(string text, int count)
    {
        if (text.Length <= count) return text;
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= count ? text : text[..starts[count]];
    }
    public static string? Output(string? raw, int maximum = MobileWire.MaximumBlockOutput)
    {
        if (raw is null) return null;
        var clean = ActivitySupport.Clean(raw, 4 * maximum);
        return clean.Length == 0 ? null : Prefix(clean, maximum);
    }
    private static string? Summary(string raw) { var clean = ActivitySupport.Clean(raw, MobileWire.MaximumBlockSummary, true); return clean.Length == 0 ? null : clean; }
    private static bool Stamp(string value, out DateTimeOffset date) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);

    /// <summary>How long a settled block's own records span; the host keeps no per-block clock, and a block still in motion reports none.</summary>
    public static double? Duration(IReadOnlyList<LogEntry> entries, string status)
    {
        if (!Settled(status) || entries.Count < 2 || !Stamp(entries[0].Timestamp, out var first) || !Stamp(entries[^1].Timestamp, out var last)) return null;
        var milliseconds = (last - first).TotalMilliseconds;
        return ActivitySupport.ValidDuration(milliseconds) && milliseconds > 0 ? Math.Round(milliseconds, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>The recent steps of a block still in motion, oldest first; a settled block sends none — its output is what it has to say.</summary>
    public static IReadOnlyList<string>? Activity(IReadOnlyList<LogEntry> entries, string status)
    {
        if (status is not ("running" or "waiting")) return null;
        var lines = new List<string>();
        for (var index = entries.Count - 1; index >= 0 && lines.Count < MobileWire.MaximumBlockActivity; index--) if (Step(entries[index]) is { } line) lines.Add(line);
        if (lines.Count == 0) return null;
        lines.Reverse(); return lines;
    }
    /// <summary>A tool call is its summary (else its tool's name), a reply or an error its first line; the run's own lifecycle row and a question's raw form are left out.</summary>
    public static string? Step(LogEntry entry)
    {
        if (entry.Activity is { } activity) return activity.Kind == "turn" ? null : Summary(activity.Summary.Length == 0 ? activity.ToolName ?? "" : activity.Summary);
        if (entry.Kind is not ("assistant" or "error") || UserQuestionnaire.Parse(entry.Text) is not null) return null;
        return Summary(entry.Text.TrimStart().Split(['\r', '\n'], 2)[0]);
    }
    private static string? Answer(IReadOnlyList<LogEntry> entries) => entries.LastOrDefault(e => e.Kind == "assistant" && e.Text.Length > 0)?.Text;

    /// <summary>
    /// The blocks of one run: the request itself, then its children in the order the graph recorded them. Background blocks
    /// travel only in a notice run (<see cref="BackgroundNoticeRuns.ShownAgents"/>), whose main block takes the run's heading (<see cref="BackgroundNoticeRuns.Heading(MightyGraphRun)"/>).
    /// <paramref name="outputTails"/>: a background run's last output lines by block id, held in host memory only; the output
    /// file's path never reaches the wire.
    /// </summary>
    public static IReadOnlyList<MobileBlock> Blocks(MightyGraphRun run, int ordinal, IReadOnlyDictionary<string, IReadOnlyList<string>>? outputTails = null)
    {
        var main = Status(run.Status);
        var result = new List<MobileBlock>
        {
            new(run.Id + ":main", "main", run.NoticeRun == true ? BackgroundNoticeRuns.Heading(run) : Locale.Get("remote.block.request", new Dictionary<string, string> { ["ordinal"] = ordinal.ToString(CultureInfo.InvariantCulture) }), main,
                Summary(run.Input), Output(run.FinalOutput), Duration(run.RootEntries, main), run.NodeModelLabel, Activity(run.RootEntries, main)),
        };
        foreach (var agent in BackgroundNoticeRuns.ShownAgents(run))
        {
            var state = Status(agent.Status);
            result.Add(new(agent.Id, MightyGraphSupport.BlockKind(agent), MightyGraphSupport.BlockTitle(agent), state, Summary(agent.Input), Output(Answer(agent.Entries)), Duration(agent.Entries, state), Activity: Activity(agent.Entries, state),
                Background: agent.Background == true ? true : (bool?)null,
                OutputTail: agent.IsTask ? OutputTail(outputTails?.GetValueOrDefault(agent.Id)) : null,
                NoticeReply: Output(agent.NoticeReply)));
        }
        return result;
    }

    /// <summary>A background run's last output lines as the wire carries them: at most <see cref="BackgroundOutputTail.MaxLines"/>, absent when there are none.</summary>
    public static IReadOnlyList<string>? OutputTail(IReadOnlyList<string>? lines) =>
        lines is { Count: > 0 } ? lines.TakeLast(BackgroundOutputTail.MaxLines).ToArray() : null;

    /// <summary>
    /// The pane's newest runs, oldest first. <paramref name="title"/> is the guided style's own name for a request; only the
    /// newest request and the notice runs after it carry their full result, each once it has settled. Requests are numbered with
    /// <see cref="MightyGraphSupport.RequestOrdinals"/>: a notice run takes none. A notice run's <c>Title</c> is its heading (<see cref="BackgroundNoticeRuns.Heading(MightyGraphRun)"/>).
    /// <paramref name="workspaceRoot"/> is the pane's workspace folder on this computer (null for a workspace of another computer):
    /// a run that carries its result gets the files it names from it (<see cref="RunResultFiles"/>). With the pane's
    /// <paramref name="resultFiles"/> memo a result is looked through once, and the memo drops the runs this call no longer looks up.
    /// </summary>
    public static IReadOnlyList<MobileMightyRun> Runs(IReadOnlyList<MightyGraphRun> values, Func<string, string?>? title = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? outputTails = null, string? workspaceRoot = null,
        MobileResultFilesMemo? resultFiles = null)
    {
        var offset = Math.Max(0, values.Count - MobileWire.MightyRuns); var result = new List<MobileMightyRun>();
        var ordinals = MightyGraphSupport.RequestOrdinals(values); var carried = FirstCarried(values, offset);
        for (var index = offset; index < values.Count; index++)
        {
            var run = values[index]; var state = Status(run.Status); var notice = run.NoticeRun == true; var waiting = MightyGraphSupport.BackgroundWaiting(run);
            var output = index >= carried && Settled(state) ? Output(run.FinalOutput, MobileWire.MaximumRunResult) : null;
            result.Add(new(run.Id, ActivitySupport.Clean(run.Input, MobileWire.MaximumText), notice ? BackgroundNoticeRuns.Heading(run) : title?.Invoke(run.Input), state, Blocks(run, ordinals[index], outputTails),
                output, notice ? true : null, waiting > 0 ? waiting : null, output is null ? null : RunResultFiles(run, workspaceRoot, resultFiles)));
        }
        resultFiles?.Prune();
        return result;
    }

    /// <summary>
    /// The files a run's final result names, as the desktop's 결과에 나온 파일 panel lists them (<see cref="MightyGraphBlockModel.FilesFor"/>,
    /// read from the full result, not the clipped <c>result</c>): only for a run that completed with every block it draws ended
    /// (<see cref="MightyGraphLayout.Finished"/>), only files inside <paramref name="workspaceRoot"/>, the first
    /// <see cref="MobileWire.MaximumRunResultFiles"/>. Null when there is no root or nothing was named. With <paramref name="memo"/> the
    /// workspace is looked through once per result (<see cref="MobileResultFilesMemo"/>), never on every poll.
    /// </summary>
    public static IReadOnlyList<MobileResultFile>? RunResultFiles(MightyGraphRun run, string? workspaceRoot, MobileResultFilesMemo? memo = null)
    {
        if (workspaceRoot is null || run.Status != "completed" || !MightyGraphLayout.Finished(run)) return null;
        return memo is null ? LookThrough(run, workspaceRoot) : memo.Files(run, workspaceRoot);
    }

    /// <summary>The look through the workspace itself: the desktop panel's <see cref="MightyGraphBlockModel.FilesFor"/>, cut to the wire's cap.</summary>
    public static IReadOnlyList<MobileResultFile>? LookThrough(MightyGraphRun run, string workspaceRoot)
    {
        List<ResultFiles.ResultFile> files;
        try { files = MightyGraphBlockModel.FilesFor(run, workspaceRoot); }
        // A result that cannot be looked through names nothing, as on the desktop (LookThroughResultFiles); the session reply still goes.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        return files.Count == 0 ? null : files.Take(MobileWire.MaximumRunResultFiles).Select(file => new MobileResultFile(file.Path, file.Line)).ToArray();
    }

    /// <summary>The first run of the window from <paramref name="offset"/> that carries its result once settled: the newest request, after
    /// which only notice runs (Claude's replies to its background work) follow; the last run when the window holds no request.</summary>
    private static int FirstCarried(IReadOnlyList<MightyGraphRun> values, int offset)
    {
        for (var index = values.Count - 1; index >= offset; index--) if (values[index].NoticeRun != true) return index;
        return values.Count - 1;
    }

    /// <summary>The runs the payload is made from: the saved graph, or the requests grouped out of the transcript of a pane that has none.</summary>
    public static IReadOnlyList<MightyGraphRun> SavedRuns(RunSession session) => session.GraphRuns is { } saved ? saved : LegacyRuns(session);

    /// <summary>
    /// A pane without a saved graph has only its transcript: its requests are grouped from it, without inventing sub-agents
    /// (M/MightyCore/MightyGraph.swift:119-143). Replies before the first request stand under one run of their own.
    /// </summary>
    public static IReadOnlyList<MightyGraphRun> LegacyRuns(RunSession session)
    {
        var runs = new List<MightyGraphRun>();
        void Finish(MightyGraphRun run, string status)
        {
            run.Status = status;
            if (status == "completed") run.FinalOutput = run.RootEntries.LastOrDefault(e => e.Kind == "assistant")?.Text;
        }
        foreach (var entry in session.Logs)
        {
            if (entry.Kind == "user")
            {
                if (runs.Count > 0) Finish(runs[^1], "completed");
                runs.Add(new() { Id = entry.Id, Input = entry.Text, Provider = session.Provider });
            }
            else
            {
                if (runs.Count == 0) runs.Add(new() { Id = "history-" + session.Id, Input = "", Status = "completed", Provider = session.Provider });
                runs[^1].RootEntries.Add(entry);
            }
        }
        if (runs.Count > 0) Finish(runs[^1], session.Status == "idle" ? "completed" : session.Status);
        return runs;
    }

    /// <summary>
    /// What the phone would see change in the runs: their identities and statuses, and how many records each block holds with
    /// the newest one's id. Streamed text is left out — hashing it would wake every long poll on every token — except the
    /// newest settled run's answer length, since that run carries <c>result</c>.
    /// </summary>
    public static int Digest(IReadOnlyList<MightyGraphRun> values)
    {
        // The output lines of a background run are hashed beside this by the host (they live in its memory, not in the runs).
        var hash = new HashCode(); var offset = Math.Max(0, values.Count - MobileWire.MightyRuns);
        // The runs Runs gives their result: the newest request and the notice runs after it.
        for (var index = FirstCarried(values, offset); index >= 0 && index < values.Count; index++)
            if (Settled(Status(values[index].Status))) hash.Add(values[index].FinalOutput?.Length);
        for (var index = offset; index < values.Count; index++)
        {
            var run = values[index];
            hash.Add(run.Id); hash.Add(run.Status); hash.Add(run.Agents.Count); hash.Add(run.RootEntries.Count); hash.Add(run.RootEntries.Count > 0 ? run.RootEntries[^1].Id : null);
            // The result card's 현재까지의 결과 count and a notice run's title move without a new record.
            hash.Add(MightyGraphSupport.BackgroundWaiting(run)); hash.Add(run.NoticeTitle);
            // The blocks the phone draws: a request's background blocks wait for their notice run (the strip shows them meanwhile).
            // Claude's reply to a task's notice can land with no new record on the block.
            foreach (var agent in BackgroundNoticeRuns.ShownAgents(run)) { hash.Add(agent.Id); hash.Add(agent.Status); hash.Add(agent.Kind); hash.Add(agent.Entries.Count); hash.Add(agent.Entries.Count > 0 ? agent.Entries[^1].Id : null); hash.Add(agent.NoticeReply?.Length); }
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// The digest of the very runs the payload carries. A pane with no saved graph is read from its transcript by identity
    /// alone, walked backwards and stopped at the window the payload sends, so a long transcript costs no more than a short one.
    /// </summary>
    public static int Digest(RunSession session)
    {
        if (session.GraphRuns is { } saved) return Digest(saved);
        var ids = new List<(string Id, int Entries, string? Last)>(); var entries = 0; string? last = null; var index = session.Logs.Count - 1;
        for (; index >= 0 && ids.Count < MobileWire.MightyRuns; index--)
        {
            var entry = session.Logs[index];
            if (entry.Kind == "user") { ids.Add((entry.Id, entries, last)); entries = 0; last = null; }
            else { entries++; last ??= entry.Id; }
        }
        if (index < 0 && session.Logs.Count > 0 && session.Logs[0].Kind != "user" && ids.Count < MobileWire.MightyRuns) ids.Add(("history-" + session.Id, entries, last));
        var hash = new HashCode();
        if (ids.Count == 0) return hash.ToHashCode();
        ids.Reverse(); var status = session.Status == "idle" ? "completed" : session.Status;
        if (Settled(Status(status)))
        {
            // The newest run's result is its last reply, and only a completed run has one.
            int? length = null;
            if (status == "completed")
                for (var at = session.Logs.Count - 1; at >= 0 && session.Logs[at].Kind != "user"; at--)
                    if (session.Logs[at].Kind == "assistant") { length = session.Logs[at].Text.Length; break; }
            hash.Add(length);
        }
        // Digest(values)'s waiting count and notice title, which a legacy run never has.
        for (var at = 0; at < ids.Count; at++) { hash.Add(ids[at].Id); hash.Add(at == ids.Count - 1 ? status : "completed"); hash.Add(0); hash.Add(ids[at].Entries); hash.Add(ids[at].Last); hash.Add(0); hash.Add((string?)null); }
        return hash.ToHashCode();
    }
}

public sealed record MobilePermissionField(string Label, string Value);
/// <summary><c>MobilePermission.antigravity</c>: what an Antigravity turn was refused, each action as the card shows it.</summary>
public sealed record MobileAntigravityCard(IReadOnlyList<MobileAntigravityAction> Actions)
{
    public const string ToolName = "AntigravityPermission";
    public static MobileAntigravityCard From(AntigravityPermissionRequest card) =>
        new(card.Actions.Select(a => new MobileAntigravityAction(a.Kind, a.Action, a.DisplayName, a.Target, a.Rule, a.OnceOnly ? true : null,
            a.OnceOnlyReason is { } reason ? JsonNamingPolicy.CamelCase.ConvertName(reason.ToString()) : null)).ToArray());
}
/// <summary>
/// One action of <see cref="MobileAntigravityCard"/> (M/MightyCore/Remote/MobileRemoteModels.swift MobileAntigravityAction). <c>OnceOnly</c> is
/// true when its rule may be allowed only once (it chains, pipes, redirects or runs a destructive command): the card offers no 항상 허용 and
/// the host refuses always; absent otherwise. <c>OnceOnlyReason</c> (risky, interpreter or agyFolder) says why and picks the card's words; a
/// phone that does not know a reason shows the risky words.
/// </summary>
public sealed record MobileAntigravityAction(string Kind, string Action, string DisplayName, string? Target = null, string? Rule = null, bool? OnceOnly = null, string? OnceOnlyReason = null);
/// <summary>A pending request as the phone's card shows it. <c>Plan</c> is the Markdown of a Claude plan approval, answered with <c>POST …/plan</c>.
/// <c>Antigravity</c> is an Antigravity card (the actions a finished turn was refused), answered with <c>POST …/permission</c> and <c>always</c>; absent for any other request.</summary>
public sealed record MobilePermission(string Id, string RunId, string ToolName, string Title, string? Headline, IReadOnlyList<MobilePermissionField> Fields, string Summary, bool CanAllow, UserQuestionnaire? Questionnaire = null, string? Plan = null, string? ReceivedAt = null, MobileAntigravityCard? Antigravity = null)
{
    /// <summary>
    /// The Antigravity card as one permission (M/MightyCore/Remote/MobileRemoteModels.swift init(antigravity:)): toolName is
    /// AntigravityPermission, and the actions are its fields too, so a phone that does not know these cards shows them with allow (once) and deny.
    /// </summary>
    public static MobilePermission FromAntigravity(AntigravityPermissionRequest card) =>
        new(card.Id, card.RunId, MobileAntigravityCard.ToolName, Locale.Get("antigravity.permission.title"), null,
            card.Actions.Select(a => new MobilePermissionField(AntigravityPermissionFlow.KindLabel(a.Kind), a.Target ?? a.DisplayName)).ToArray(),
            ActivitySupport.Clean(string.Join(", ", card.Names), 2_048, true), card.CanAllow, ReceivedAt: card.ReceivedAt, Antigravity: MobileAntigravityCard.From(card));
    /// <summary>
    /// The structured card the desktop shows, so both surfaces read alike (M/MightyCore/Remote/MobileRemoteModels.swift:229-240).
    /// A plan carries its text as <c>plan</c>, and once more as its one field so a phone that does not know plans still shows it.
    /// </summary>
    public static MobilePermission From(ToolPermissionRequest request)
    {
        var plan = request.CanAnswerPlan ? request.Plan : null;
        if (plan is not null) return new(request.Id, request.RunId, request.ToolName, Locale.Get("plan.card.title"), null, [new(Locale.Get("plan.card.title"), plan)], request.Summary, request.CanAllow, null, plan, request.ReceivedAt);
        var card = ToolPermissionPresentation.Make(request.ToolName, request.InputJson);
        return new(request.Id, request.RunId, request.ToolName, card.Title, card.Headline, card.Fields.Select(f => new MobilePermissionField(f.Label, f.Value)).ToArray(), request.Summary, request.CanAllow,
            request.CanAnswerQuestions ? UserQuestionnaire.Parse(request.InputJson) : null);
    }
}

public sealed record MobileOption(string Id, string Label);
/// <summary>One style a pane may pick. <c>Source</c> is absent for <c>cli</c>, which belongs to no source.</summary>
public sealed record MobileStyleOption(string Id, string Label, string? Source = null);
/// <summary>One pane's pickers, in the order the desktop's menus show them. <c>Efforts</c> is absent when the provider has no reasoning-effort setting.</summary>
public sealed record MobileSettingsOptions(IReadOnlyList<MobileOption> Models, IReadOnlyList<MobileOption> PermissionModes, IReadOnlyList<MobileOption>? Efforts, IReadOnlyList<MobileOption> MightyStyles, IReadOnlyList<MobileStyleOption> Styles);
/// <summary><c>Editable</c> is false while the pane runs, matching the desktop's disabled pickers; <c>Effort</c> is absent together with <c>Options.Efforts</c>.</summary>
public sealed record MobileSettings(bool Editable, string Model, string PermissionMode, string? Effort, string AgentViewMode, string MightyStyle, string StyleId, MobileSettingsOptions Options);

/// <summary>Pure rules of the m1 extension (M/MightyCore/Remote/MobileRemoteSupport.swift:7-185), kept out of the app so the contract can be checked without a window.</summary>
public static class MobileRemoteSupport
{
    /// <summary>
    /// The phone's contract has no pictures: an <c>image</c> entry reaches it as the <c>system</c> line naming them, without
    /// references, so neither picture bytes nor cache names travel over the relay.
    /// </summary>
    public static IReadOnlyList<LogEntry> PhoneEntries(IEnumerable<LogEntry> entries)
        => entries.Select(entry => entry.Kind == "image" || entry.Images is not null ? entry with { Kind = entry.Kind == "image" ? "system" : entry.Kind, Images = null } : entry).ToArray();

    /// <summary>
    /// The page older than <paramref name="before"/>, newest last. A cursor the host no longer holds answers empty rather than
    /// restarting from the oldest entry, so the phone does not silently jump in time.
    /// </summary>
    public static (IReadOnlyList<LogEntry> Entries, bool HasMore) Page(IReadOnlyList<LogEntry> entries, string before, int limit)
    {
        var index = -1; for (var at = 0; at < entries.Count && index < 0; at++) if (entries[at].Id == before) index = at;
        if (index < 0) return ([], false);
        var count = Math.Min(index, Math.Max(1, limit));
        return (entries.Skip(index - count).Take(count).ToArray(), index > count);
    }

    /// <summary>The desktop stores nothing or "default" for the plain transcript; the wire says "plain".</summary>
    public static string ViewMode(string? raw) => raw == "mighty" ? "mighty" : "plain";
    /// <summary>The line a pane's preview shows in a push (M/MobileRemoteSupport.previewLine): the newest entry with text that is not a tool activity.</summary>
    public static LogEntry? PreviewLine(RunSession session) => session.Logs.LastOrDefault(entry => entry.Activity is null && entry.Text.Length > 0);
    /// <summary>The old two-word field: only the bundled Ouroboros has a word of its own, every other style travels as "cli" and carries its truth in <c>styleId</c>.</summary>
    public static string Style(string? raw) => raw is not null && MobileWire.MightyStyles.Contains(raw) ? raw : MobileWire.CliStyle;
    /// <summary>Guided styles exist only for a Claude pane in the Mighty view. <paramref name="viewMode"/> is the view the pane will be in, so one request can turn the view on and pick a style.</summary>
    public static bool GuidedStylesAvailable(string kind, string provider, string viewMode) => kind == "claude" && provider == "claude" && viewMode == "mighty";

    /// <summary><c>options.styles</c>: the CLI plus the styles this pane may really pick.</summary>
    public static IReadOnlyList<MobileStyleOption> StyleOptions(IEnumerable<RegisteredStyle> styles)
        => [new(MobileWire.CliStyle, MobileWire.CliStyle), .. styles.Where(s => s.Runnable).Select(s => new MobileStyleOption(s.Id, s.Manifest.Name, s.Source))];
    /// <summary>The old field keeps its fixed vocabulary, but offers only what this pane could actually pick.</summary>
    public static IReadOnlyList<MobileOption> LegacyStyleOptions(IEnumerable<RegisteredStyle> styles)
    {
        var runnable = styles.Where(s => s.Runnable).ToArray();
        return [new(MobileWire.CliStyle, "CLI"), .. MobileWire.MightyStyles.Skip(1).Select(id => runnable.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Select(s => new MobileOption(s!.Id, s.Manifest.Name))];
    }
    /// <summary>Absent when the provider has no reasoning-effort setting at all, so the phone shows no picker rather than one with a single dead choice.</summary>
    public static IReadOnlyList<MobileOption>? EffortOptions(bool supported, IEnumerable<string> levels)
        => supported ? [new("default", EffortLabel("default")), .. levels.Select(level => new MobileOption(level, EffortLabel(level)))] : null;
    public static string EffortLabel(string effort) => effort switch { "low" => "Low", "medium" => "Medium", "high" => "High", "xhigh" => "XHigh", "max" => "Max", _ => "Auto" };

    /// <summary>
    /// The style a settings request leaves the pane with (M/MightyClaude/AppStore+MobileRemote.swift:783-795).
    /// <paramref name="shown"/> is what this sender was last told: a pane in the plain view reports "cli" whatever it has stored,
    /// so a phone handing that pair back while it turns the Mighty view on is not read as "throw the stored style away".
    /// </summary>
    public static string? StyleAfter(string? requested, string shown, string? stored) => requested is null || requested == shown ? stored : requested == MobileWire.CliStyle ? null : requested;

    /// <summary>The phone's text for a style button is folded to one line first, whatever the style's own rule says: an action reads its argument up to the first line break.</summary>
    public static string SingleLine(string text) => string.Join(" ", text.Split(['\r', '\n', (char)0x85, (char)0x2028, (char)0x2029, (char)0x0B, (char)0x0C]).Select(line => line.Trim(' ', '\t')).Where(line => line.Length > 0));

    /// <summary>The argument a listed command takes, in the user's language (M/MightyCore/Remote/MobileRemoteSupport.swift:477-483).</summary>
    public static string? ArgumentHint(SlashArgument? argument) => argument switch { SlashArgument.Model => Locale.Get("remote.slashHint.model"), SlashArgument.Permission => Locale.Get("remote.slashHint.permission"), _ => null };

    /// <summary>The account windows a running session last reported, under the desktop's own names. Null when none is measurable, so the phone shows no limits rather than empty ones.</summary>
    public static IReadOnlyList<MobileRateLimit>? RateLimits(IEnumerable<SessionRateLimit>? limits)
    {
        var mapped = (limits ?? []).Where(l => l.PercentUsed is { } percent && double.IsFinite(percent)).Select(l => new MobileRateLimit(AccountUsageSupport.WindowLabel(l.Kind), Math.Clamp(l.PercentUsed!.Value, 0, 100), l.ResetsAt)).ToArray();
        return mapped.Length == 0 ? null : mapped;
    }

    /// <summary>
    /// A style button's request (M/MightyCore/Remote/MobileRemoteService.swift:929-945). <c>styleId</c>+<c>actionId</c> is the
    /// shape of today, <c>style</c>+<c>skill</c> the older one an older phone sends for the bundled Ouroboros (it also named
    /// Paperthin until v7); a half-migrated phone may send the new style field with the old action field. An unregistered id
    /// and a malformed one answer alike.
    /// </summary>
    public static MobileGuidedRequest GuidedRequest(JsonElement body)
    {
        var style = body.Text("styleId") ?? body.Text("style"); var action = body.Text("styleId") is not null ? body.Text("actionId") ?? body.Text("skill") : body.Text("skill");
        if (style is null || style == MobileWire.CliStyle || !Regex.IsMatch(style, @"^[a-z0-9][a-z0-9-]{0,39}\z")) throw new MobileRequestException(400, Locale.Get("remote.error.unknownStyle"));
        if (action is null || !Regex.IsMatch(action, @"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}\z")) throw new MobileRequestException(400, Locale.Get("remote.error.badSkillName"));
        var text = (body.Text("text") ?? "").Trim();
        if (Encoding.UTF8.GetByteCount(text) > MobileWire.MaximumText || text.Contains('\0')) throw new MobileRequestException(400, Locale.Get("remote.error.textTooLong"));
        return new(style, action, text);
    }
}
public sealed record MobileRateLimit(string Label, double UsedPercent, string? ResetsAt = null);
public sealed record MobileGuidedRequest(string Style, string Action, string Text);

public sealed record MobileGuidedSkill(string Skill, string Title, string Help);
public sealed record MobileOuroboros(string Phase, bool Ready, IReadOnlyList<string> TakesText, IReadOnlyList<MobileGuidedSkill> Next, IReadOnlyList<MobileGuidedSkill> All);

/// <summary>
/// An older phone knows <c>mighty.ouroboros</c> and nothing else, so the bundled Ouroboros keeps sending it, from the same
/// readings the new panel comes from (M/MightyCore/Remote/MobileLegacyStyleAdapter.swift).
/// </summary>
public static class MobileLegacyStyleAdapter
{
    /// <param name="phase">The pane's phase as its panel reads it.</param>
    /// <param name="ready">Whether the style's prerequisites are in place (<c>panel.setup.ready</c>).</param>
    public static MobileOuroboros? Payload(RegisteredStyle style, StylePhase? phase, bool ready) => style.Id == "ouroboros" ? Ouroboros(style, phase, ready) : null;

    private static MobileGuidedSkill Skill(StyleAction action) => new(action.Id, action.Title, action.Help);

    /// <summary>The old field is the phase map alone: at the first phase it was empty and the older phone fell back to <c>all</c>, so the start rule's buttons are not added to it.</summary>
    public static MobileOuroboros Ouroboros(RegisteredStyle style, StylePhase? phase, bool ready)
    {
        var actions = style.Manifest.Actions; var next = style.Manifest.Rules.GetProperty("next");
        var ids = phase is not null && StyleManifest.Text(next, "kind") == "byPhase" ? StyleManifest.Strings(next.GetProperty("map"), phase.Id) : [];
        return new(phase?.Id ?? style.Manifest.Phases.OrderBy(p => p.Order).FirstOrDefault()?.Id ?? "", ready, actions.Where(a => a.TakesText).Select(a => a.Id).ToArray(),
            ids.Select(id => actions.FirstOrDefault(a => a.Id == id)).Where(a => a is not null).Select(a => Skill(a!)).ToArray(), actions.Select(Skill).ToArray());
    }
}
