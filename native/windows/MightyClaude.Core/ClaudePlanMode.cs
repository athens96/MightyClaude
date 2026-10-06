using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

// Claude Code's plan mode, read from the same stream the app already runs:
// the ExitPlanMode approval over --permission-prompt-tool stdio, the execution
// checklist (TodoWrite, or TaskCreate/TaskUpdate where the CLI uses those), and
// the background tasks the CLI reports with `system` task events. macOS Core
// (ClaudePlanMode.swift) behaves the same; both read
// native/contracts/fixtures/claude-plan-mode.json.

/// <summary>How the user answered a plan Claude presented with ExitPlanMode.</summary>
public sealed record PlanDecision(string Kind, string? Feedback = null)
{
    /// <summary>Allow, and the session continues in acceptEdits.</summary>
    public static readonly PlanDecision ApproveAutoEdit = new("approveAutoEdit");
    /// <summary>Allow, and the session continues in the CLI's default (the app's manual).</summary>
    public static readonly PlanDecision ApproveConfirmEach = new("approveConfirmEach");
    /// <summary>Deny and interrupt the turn.</summary>
    public static readonly PlanDecision Cancel = new("cancel");
    /// <summary>Deny with the user's feedback; Claude stays in plan mode and plans again.</summary>
    public static PlanDecision Revise(string feedback) => new("revise", feedback);

    public string Outcome => Kind switch
    {
        "approveAutoEdit" => PlanOutcome.ApprovedAuto,
        "approveConfirmEach" => PlanOutcome.ApprovedConfirm,
        "revise" => PlanOutcome.Revised,
        _ => PlanOutcome.Cancelled,
    };
}

public static class PlanOutcome
{
    public const string ApprovedAuto = "approvedAuto", ApprovedConfirm = "approvedConfirm", Revised = "revised", Cancelled = "cancelled";
    public static readonly string[] All = [ApprovedAuto, ApprovedConfirm, Revised, Cancelled];
    /// <summary>The pane's own permission mode after this answer, so later runs and resumes start where the approved session continued. Null leaves it alone.</summary>
    public static string? PaneMode(string outcome) => outcome switch { ApprovedAuto => "acceptEdits", ApprovedConfirm => "manual", _ => null };
    /// <summary>The mode Claude Code itself switches to (setMode) for this answer alone.</summary>
    internal static string? CliMode(string outcome) => outcome switch { ApprovedAuto => "acceptEdits", ApprovedConfirm => "default", _ => null };
    /// <summary>The CLI mode a pane's stored permission mode launches with; null for plan (or anything else), which approval never returns to.</summary>
    internal static string? PaneCliMode(string? paneMode) => paneMode switch { "manual" => "default", "acceptEdits" => "acceptEdits", "auto" => "auto", "fullAccess" => "bypassPermissions", _ => null };
    private static int Rank(string mode) => mode switch { "acceptEdits" => 1, "auto" => 2, "bypassPermissions" => 3, _ => 0 };

    /// <summary>
    /// The session mode an approval switches to: the button's choice, but never below the mode the pane itself
    /// runs in. A pane stored in plan (the user asked for planning) takes the button as is. A pane stored in
    /// manual, acceptEdits, auto or fullAccess entered plan mode on Claude's own call; approving must not leave
    /// it lower than it was launched with, so an auto or full-access pane returns to auto / bypassPermissions,
    /// and a manual or accept-edits pane takes the higher of its own mode and the button's (macOS approvedCLIMode).
    /// </summary>
    public static string? ApprovedCliMode(PlanDecision decision, string? paneMode)
    {
        if (CliMode(decision.Outcome) is not { } chosen) return null;
        return PaneCliMode(paneMode) is { } own && Rank(own) > Rank(chosen) ? own : chosen;
    }
}

/// <summary>A plan waiting for the user's answer. Like every permission request it is ephemeral: only its outcome is kept, as a PlanRecord.</summary>
public sealed record PlanApprovalRequest(string SessionId, string RunId, string RequestId, string ToolUseId, string Plan, string ReceivedAt)
{
    /// <summary>The pending ExitPlanMode request of one pane, or null for any other request.</summary>
    public static PlanApprovalRequest? From(string sessionId, ToolPermissionRequest permission)
        => permission is { CanAnswerPlan: true, State: "pending" } && permission.Plan is { } plan
            ? new(sessionId, permission.RunId, permission.Id, permission.ToolUseId, plan, permission.ReceivedAt ?? ClaudePlanMode.Timestamp(DateTimeOffset.UtcNow))
            : null;
}

/// <summary>One answered plan in the pane's history: approved plans and the plans the user sent back, with what they asked for.</summary>
/// <summary>
/// One answered plan in the pane's history. <c>GraphRunId</c> is the id of the diagram request the plan
/// belongs to (<c>MightyGraphRun.SourceRunID</c>), so its history block attaches beside that request.
/// </summary>
public sealed record PlanRecord(string Id, string RunId, string Plan, string ReceivedAt, string DecidedAt, string Outcome, string? Feedback = null, bool? PlanTruncated = null, string? GraphRunId = null);

public static class ClaudePlanMode
{
    public const string ToolName = "ExitPlanMode";
    public const int MaximumFeedbackBytes = 16_384, MaximumHistory = 10, MaximumStoredPlanBytes = 32_768, MaximumStoredFeedbackBytes = 4_096;
    /// <summary>Plans plus feedback kept per pane. Newer records are kept whole first; an older one that no longer fits keeps its first lines only.</summary>
    public const int MaximumHistoryBytes = 98_304;
    private const int SummaryLines = 8, SummaryBytes = 1_024, SummaryFeedbackBytes = 512;

    public const string ReviseMessage = "The user reviewed your plan and wants changes before any code is written. Stay in plan mode, revise the plan, and call ExitPlanMode again. The user said:\n";
    public const string CancelMessage = "The user cancelled this plan in Mighty Claude. Do not make any changes; stop and wait for the user's next request.";
    public const string TooLongMessage = "The plan is longer than Mighty Claude can show in full, so it cannot be approved. Shorten the plan and call ExitPlanMode again.";

    /// <summary>ISO 8601 with milliseconds, as macOS writes them.</summary>
    public static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The Markdown plan of an ExitPlanMode input, null when there is none.</summary>
    public static string? Plan(string inputJson)
    {
        if (inputJson.AsSpan().TrimStart() is not ['{', ..]) return null;
        try { using var document = JsonDocument.Parse(inputJson); return Plan(document.RootElement); }
        catch (JsonException) { return null; }
    }
    internal static string? Plan(JsonElement input) => input.Text("plan") is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>
    /// The `response` body of the control_response answering an ExitPlanMode can_use_tool request (Claude Code 2.1.x SDK
    /// permission result). `paneMode` is the pane's stored permission mode (PlanOutcome.ApprovedCliMode).
    /// </summary>
    public static Dictionary<string, object> Response(PlanDecision decision, string toolUseId, string? paneMode = "plan")
    {
        switch (decision.Kind)
        {
            case "approveAutoEdit" or "approveConfirmEach":
                // Same shape as the CLI's own "Yes" choices: an empty updatedInput (the CLI keeps the model's own input,
                // never the injected plan) plus a session-only mode change.
                return new(StringComparer.Ordinal)
                {
                    ["behavior"] = "allow", ["updatedInput"] = new Dictionary<string, object>(),
                    ["updatedPermissions"] = new[] { new Dictionary<string, string> { ["type"] = "setMode", ["mode"] = PlanOutcome.ApprovedCliMode(decision, paneMode)!, ["destination"] = "session" } },
                    ["toolUseID"] = toolUseId,
                };
            case "revise":
                return new(StringComparer.Ordinal) { ["behavior"] = "deny", ["message"] = ReviseMessage + ValidatedFeedback(decision.Feedback), ["toolUseID"] = toolUseId };
            default:
                return new(StringComparer.Ordinal) { ["behavior"] = "deny", ["message"] = CancelMessage, ["interrupt"] = true, ["toolUseID"] = toolUseId };
        }
    }

    /// <summary>Trimmed, non-empty and bounded, or an error the card can show.</summary>
    public static string ValidatedFeedback(string? feedback)
    {
        var text = (feedback ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException(Locale.Get("plan.error.emptyFeedback"));
        if (Encoding.UTF8.GetByteCount(text) > MaximumFeedbackBytes) throw new ArgumentException(Locale.Get("plan.error.feedbackTooLong"));
        return text;
    }

    public static PlanRecord Record(string requestId, string runId, string plan, string receivedAt, string decidedAt, PlanDecision decision)
        => Bounded(new(requestId, runId, plan, receivedAt, decidedAt, decision.Outcome, decision.Kind == "revise" ? (decision.Feedback ?? "").Trim() : null));

    internal static PlanRecord Bounded(PlanRecord record)
    {
        var plan = ActivitySupport.PrefixUtf8(record.Plan ?? "", MaximumStoredPlanBytes);
        var feedback = record.Feedback is null ? null : ActivitySupport.PrefixUtf8(record.Feedback, MaximumStoredFeedbackBytes);
        return record with { Plan = plan, PlanTruncated = plan.Length < (record.Plan ?? "").Length ? true : record.PlanTruncated, Feedback = string.IsNullOrEmpty(feedback) ? null : feedback };
    }

    /// <summary>The pane's history with `record` added (a repeat of the same request replaces it), newest last, at most MaximumHistory long and within MaximumHistoryBytes.</summary>
    public static List<PlanRecord> Appending(PlanRecord record, IReadOnlyList<PlanRecord>? history)
        => Budgeted([.. (history ?? []).Where(r => r.Id != record.Id || r.RunId != record.RunId).Append(Bounded(record)).TakeLast(MaximumHistory)]);

    private static int Cost(PlanRecord r) => Encoding.UTF8.GetByteCount(r.Plan) + (r.Feedback is null ? 0 : Encoding.UTF8.GetByteCount(r.Feedback));
    /// <summary>Newest first, whole records while they fit; older ones keep a summary.</summary>
    internal static List<PlanRecord> Budgeted(List<PlanRecord> list)
    {
        var used = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var record = list[i];
            if (used + Cost(record) > MaximumHistoryBytes)
            {
                var summary = Summarized(record.Plan);
                if (Encoding.UTF8.GetByteCount(summary) < Encoding.UTF8.GetByteCount(record.Plan)) record = record with { Plan = summary, PlanTruncated = true };
                if (record.Feedback is not null) record = record with { Feedback = ActivitySupport.PrefixUtf8(record.Feedback, SummaryFeedbackBytes) };
                list[i] = record;
            }
            used += Cost(record);
        }
        return list;
    }
    /// <summary>The first lines of a plan, bounded.</summary>
    internal static string Summarized(string plan) => ActivitySupport.PrefixUtf8(string.Join('\n', plan.Split('\n').Take(SummaryLines)), SummaryBytes);

    public static List<PlanRecord>? NormalizedHistory(IReadOnlyList<PlanRecord>? history)
    {
        if (history is null) return null;
        var list = history.Where(r => r is not null && Wire.Identifier(r.Id) && !string.IsNullOrEmpty(r.Plan) && PlanOutcome.All.Contains(r.Outcome) && AgentRunTiming.Parse(r.DecidedAt) is not null)
            .Select(r => Bounded(r with { RunId = r.RunId ?? "", ReceivedAt = r.ReceivedAt ?? r.DecidedAt, GraphRunId = Wire.Identifier(r.GraphRunId) ? r.GraphRunId : null })).TakeLast(MaximumHistory).ToList();
        return list.Count == 0 ? null : Budgeted(list);
    }

    /// <summary>
    /// Plan answers, execution progress and background work a run reports. An approved plan also sets the
    /// pane's own permission mode, so the next request and a resume continue the way the user approved.
    /// </summary>
    internal static RunSession Record(RunSession session, RunEvent ev)
    {
        if (session.Kind != "claude" || session.Provider != "claude") return session;
        return ev.Type switch
        {
            "plan" when ev.Plan is { } record => session with
            {
                PlanHistory = Appending(record, session.PlanHistory),
                // Only a pane the user put in plan mode takes the approved mode; one Claude moved into planning by itself keeps its own.
                Settings = session.Settings.PermissionMode == "plan" && PlanOutcome.PaneMode(record.Outcome) is { } mode ? session.Settings with { PermissionMode = mode } : session.Settings,
            },
            "todos" => session with { TodoProgress = TodoProgress.Normalized(ev.Todos) },
            "background" => session with { BackgroundWork = BackgroundWork.Normalized(ev.Background, false) },
            // Background work belongs to the run that started it.
            "status" when ev.Status == "running" => session with { BackgroundWork = null },
            _ => session,
        };
    }
}

public sealed record TodoItem(string Content, string Status, string? ActiveForm = null, string? Id = null);

/// <summary>The latest checklist the main agent keeps while it carries out a plan.</summary>
public sealed record TodoProgress(IReadOnlyList<TodoItem> Items)
{
    public const int MaximumItems = 100;
    [JsonIgnore] public int Total => Items.Count;
    [JsonIgnore] public int Completed => Items.Count(i => i.Status == "completed");
    /// <summary>The item being worked on: the first in_progress one.</summary>
    [JsonIgnore] public TodoItem? Current => Items.FirstOrDefault(i => i.Status == "in_progress");
    /// <summary>What the current item says while it runs (its activeForm, else its content).</summary>
    [JsonIgnore] public string? CurrentText => Current is { } item ? item.ActiveForm ?? item.Content : null;
    public bool SameAs(TodoProgress? other) => other is not null && Items.SequenceEqual(other.Items);

    public static TodoProgress? Normalized(TodoProgress? value)
    {
        if (value is null) return null;
        var items = (value.Items ?? []).Where(i => i is not null).Select(item =>
        {
            var content = ActivitySupport.Clean(item.Content, 1024, true);
            var active = item.ActiveForm is null ? null : ActivitySupport.Clean(item.ActiveForm, 1024, true);
            var id = item.Id is null ? null : ActivitySupport.Clean(item.Id, 128, true);
            return new TodoItem(content, item.Status is "pending" or "in_progress" or "completed" ? item.Status : "pending", string.IsNullOrEmpty(active) ? null : active, string.IsNullOrEmpty(id) ? null : id);
        }).Where(i => i.Content.Length > 0).Take(MaximumItems).ToList();
        return new(items);
    }
}

/// <summary>
/// Reads the main agent's checklist from stream-json frames the way Claude Code's own checklist reader does:
/// the task tools (TaskCreate, confirmed by its "Task #N created" result, and TaskUpdate, applied as it is
/// called, with the CLI's input aliases) when there are any, else the last TodoWrite list.
/// </summary>
public sealed class TodoProgressTracker
{
    private readonly List<TodoItem> tasks = [];
    private readonly List<(string ToolUseId, TodoItem Item)> creates = [];
    private List<TodoItem>? todoWrite;
    public TodoProgress? Progress { get; private set; }

    /// <param name="progress">The pane's saved checklist, for a run that resumes it.</param>
    public TodoProgressTracker(TodoProgress? progress = null)
    {
        if (progress?.Items is not { Count: > 0 } items) return;
        if (items.Any(i => i.Id is not null)) tasks.AddRange(items.Where(i => i.Id is not null)); else todoWrite = [.. items];
        Progress = progress;
    }

    /// <summary>One stream-json frame; the new progress when it changed. Sub-agent frames never touch the main checklist.</summary>
    public TodoProgress? Consume(JsonElement frame)
    {
        if (MetadataJson.Property(frame, "parent_tool_use_id").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) return null;
        var blocks = MetadataJson.Property(MetadataJson.Property(frame, "message"), "content");
        if (blocks.ValueKind != JsonValueKind.Array) return null;
        var changed = false;
        if (frame.Text("type") == "assistant")
        {
            foreach (var block in blocks.EnumerateArray().Where(b => b.Text("type") == "tool_use"))
            {
                var input = MetadataJson.Property(block, "input");
                if (block.Text("id") is not { Length: > 0 } id || input.ValueKind != JsonValueKind.Object) continue;
                switch (block.Text("name"))
                {
                    case "TodoWrite":
                        var todos = MetadataJson.Property(input, "todos");
                        if (todos.ValueKind != JsonValueKind.Array) continue;
                        todoWrite = todos.EnumerateArray().Where(t => t.Text("content") is not null).Select(t => new TodoItem(t.Text("content")!, t.Text("status") ?? "pending", t.Text("activeForm"))).ToList();
                        changed = true;
                        break;
                    case "TaskCreate":
                        if (Field(input, "subject", "title", "name") is not { } subject || creates.Count >= 256) continue;
                        creates.Add((id, new(subject, "pending", Field(input, "activeForm", "active_form") ?? subject)));
                        changed = true;
                        break;
                    case "TaskUpdate":
                        if (Field(input, "taskId", "id", "task_id") is not { } taskId) continue;
                        var status = input.Text("status"); var subjectUpdate = input.Text("subject"); var active = Field(input, "activeForm", "active_form");
                        var index = tasks.FindIndex(t => t.Id == taskId);
                        if (status == "deleted") tasks.RemoveAll(t => t.Id == taskId);
                        else if (index >= 0) tasks[index] = tasks[index] with { Content = subjectUpdate ?? tasks[index].Content, ActiveForm = active ?? tasks[index].ActiveForm, Status = status ?? tasks[index].Status };
                        else if (tasks.Count < 256) tasks.Add(new(subjectUpdate ?? taskId, status ?? "pending", active ?? subjectUpdate ?? taskId, taskId));
                        changed = true;
                        break;
                }
            }
        }
        else if (frame.Text("type") == "user")
        {
            foreach (var block in blocks.EnumerateArray().Where(b => b.Text("type") == "tool_result"))
            {
                if (block.Text("tool_use_id") is not { } id) continue;
                var index = creates.FindIndex(c => c.ToolUseId == id);
                if (index < 0) continue;
                if (MetadataJson.Flag(block, "is_error")) { creates.RemoveAt(index); changed = true; continue; }
                var structured = MetadataJson.Property(MetadataJson.Property(frame, "tool_use_result"), "task");
                if ((Text(MetadataJson.Property(structured, "id")) ?? CreatedId(ActivitySupport.Output(MetadataJson.Property(block, "content")))) is not { } taskId) continue;
                var item = creates[index].Item with { Id = taskId };
                creates.RemoveAt(index);
                if (!tasks.Any(t => t.Id == taskId) && tasks.Count < 256) tasks.Add(item);
                changed = true;
            }
        }
        if (!changed) return null;
        var items = tasks.Concat(creates.Select(c => c.Item)).ToList();
        var next = TodoProgress.Normalized(new(items.Count == 0 ? todoWrite ?? [] : items));
        if (next is null || next.SameAs(Progress)) return null;
        Progress = next;
        return next;
    }

    private static string? Field(JsonElement input, params string[] keys)
    {
        foreach (var key in keys) if (Text(MetadataJson.Property(input, key)) is { Length: > 0 } value) return value;
        return null;
    }
    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };
    /// <summary>"Task #3 created successfully: …"</summary>
    internal static string? CreatedId(string? output) => output is not null && Regex.Match(output, @"^Task #(\S+) created successfully") is { Success: true } match ? match.Groups[1].Value : null;
}

/// <summary>Work the CLI runs beside the turn: a backgrounded Agent or Bash, or any other task it reports, from its task_started until its task_notification.</summary>
/// <param name="Kind">agent, shell or other.</param>
/// <param name="Status">running, completed, failed, stopped or unknown (the process ended before the CLI reported an end).</param>
public sealed record BackgroundTask(string Id, string Kind, string Description, string StartedAt, string Status = "running", string? ToolUseId = null, string? Summary = null, string? EndedAt = null);

/// <summary>The background tasks of a pane's latest run.</summary>
/// <param name="TurnEnded">The request's own result arrived; the process is still alive only for these tasks (the CLI waits for background agents before it exits).</param>
public sealed record BackgroundWork(IReadOnlyList<BackgroundTask> Tasks, bool TurnEnded = false)
{
    public const int MaximumTasks = 32;
    [JsonIgnore] public IReadOnlyList<BackgroundTask> Running => Tasks.Where(t => t.Status == "running").ToList();
    /// <summary>"Turn done, N background agents running": the request answered, the process still open for its background work (and for new input).</summary>
    [JsonIgnore] public bool WaitingOnBackground => TurnEnded && Tasks.Any(t => t.Status == "running");
    public bool SameAs(BackgroundWork? other) => other is not null && TurnEnded == other.TurnEnded && Tasks.SequenceEqual(other.Tasks);

    /// <summary>A saved run is over: what was still running did not report an end.</summary>
    public static BackgroundWork? Normalized(BackgroundWork? value, bool restoring)
    {
        if (value is null) return null;
        var tasks = (value.Tasks ?? []).Where(t => t is not null && BackgroundTaskTracker.ValidId(t.Id) && AgentRunTiming.Parse(t.StartedAt) is not null).Select(t =>
        {
            var summary = t.Summary is null ? null : ActivitySupport.Clean(t.Summary, 4096);
            var status = t.Status is "running" or "completed" or "failed" or "stopped" or "unknown" ? t.Status : "unknown";
            return t with
            {
                Kind = t.Kind is "agent" or "shell" ? t.Kind : "other",
                Description = ActivitySupport.Clean(t.Description, 1024, true),
                Summary = string.IsNullOrEmpty(summary) ? null : summary,
                Status = restoring && status == "running" ? "unknown" : status,
                EndedAt = AgentRunTiming.Parse(t.EndedAt) is null ? null : t.EndedAt,
                ToolUseId = Wire.Identifier(t.ToolUseId) ? t.ToolUseId : null,
            };
        }).TakeLast(MaximumTasks).ToList();
        return tasks.Count == 0 ? null : new(tasks, value.TurnEnded || restoring);
    }
}

/// <summary>
/// Reads Claude Code's `system` task events (task_started, task_updated, task_progress, task_notification,
/// background_tasks_changed). A task the CLI registered in the foreground (a blocking Agent call) stays out
/// until it is moved to the background.
/// </summary>
public sealed class BackgroundTaskTracker(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly List<BackgroundTask> tasks = [];
    private readonly Dictionary<string, BackgroundTask> hidden = [];
    private bool turnEnded;
    public BackgroundWork Work => new(tasks.ToList(), turnEnded);

    internal static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && Encoding.UTF8.GetByteCount(value) <= 128 && !value.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Control || r.Value == ' ');
    internal static string KindOf(string? type) => type switch
    {
        "local_agent" or "remote_agent" or "local_workflow" or "in_process_teammate" => "agent",
        "local_bash" => "shell",
        _ => "other",
    };
    internal static string? StatusOf(string? value) => value switch
    {
        "running" or "pending" => "running",
        "completed" => "completed",
        "failed" or "error" => "failed",
        "killed" or "stopped" or "cancelled" => "stopped",
        _ => null,
    };
    private string Now => ClaudePlanMode.Timestamp(clock());
    private static string Description(string? value) => ActivitySupport.Clean(value ?? "", 1024, true);

    /// <summary>One stream-json frame; the new state when the visible tasks changed.</summary>
    public BackgroundWork? Consume(JsonElement frame)
    {
        if (frame.Text("type") != "system" || frame.Text("subtype") is not { } subtype) return null;
        var before = Work;
        switch (subtype)
        {
            case "task_started":
                if (frame.Text("task_id") is not { } id || !ValidId(id) || MetadataJson.Flag(frame, "ambient") || hidden.ContainsKey(id)) break;
                if (Index(id) is var existing and >= 0)
                {
                    // The level event listed it first: fill in what only the start carries.
                    var known = tasks[existing];
                    if (known.ToolUseId is null && Wire.Identifier(frame.Text("tool_use_id"))) known = known with { ToolUseId = frame.Text("tool_use_id") };
                    if (frame.Text("task_type") is { } type) known = known with { Kind = KindOf(type) };
                    if (known.Description.Length == 0 && frame.Text("description") is { } d) known = known with { Description = Description(d) };
                    tasks[existing] = known;
                    break;
                }
                var task = new BackgroundTask(id, KindOf(frame.Text("task_type")), Description(frame.Text("description")), Now, ToolUseId: Wire.Identifier(frame.Text("tool_use_id")) ? frame.Text("tool_use_id") : null);
                if (MetadataJson.Property(frame, "is_backgrounded").ValueKind == JsonValueKind.False) { if (hidden.Count < 256) hidden[id] = task; }
                else Insert(task);
                break;
            case "task_updated":
            {
                if (frame.Text("task_id") is not { } updated) break;
                var patch = MetadataJson.Property(frame, "patch");
                if (patch.ValueKind != JsonValueKind.Object) break;
                if (MetadataJson.Flag(patch, "is_backgrounded") && hidden.Remove(updated, out var moved)) Insert(moved);
                if (hidden.TryGetValue(updated, out var waiting))
                {
                    // Still in the foreground: keep its state for a later move.
                    if (patch.Text("description") is { } d) waiting = waiting with { Description = Description(d) };
                    if (StatusOf(patch.Text("status")) is { } s && s != "running") hidden.Remove(updated); else hidden[updated] = waiting;
                    break;
                }
                Update(updated, t =>
                {
                    if (patch.Text("description") is { } d) t = t with { Description = Description(d) };
                    if (StatusOf(patch.Text("status")) is { } s) t = Finish(t, s);
                    if (t.Status == "failed" && t.Summary is null && patch.Text("error") is { } error) t = t with { Summary = ActivitySupport.Clean(error, 4096) };
                    return t;
                });
                break;
            }
            case "task_progress":
                if (frame.Text("task_id") is { } progressed && frame.Text("description") is { } description) Update(progressed, t => t with { Description = Description(description) });
                break;
            case "task_notification":
                if (frame.Text("task_id") is not { } notified) break;
                hidden.Remove(notified);
                Update(notified, t =>
                {
                    // Only the report that ends the task may set its summary.
                    if (t.Status != "running") return t;
                    t = Finish(t, StatusOf(frame.Text("status")) ?? "completed");
                    var summary = ActivitySupport.Clean(frame.Text("summary") ?? "", 4096);
                    return summary.Length > 0 ? t with { Summary = summary } : t;
                });
                break;
            case "background_tasks_changed":
                // A level signal: what is listed is live and in the background.
                var live = MetadataJson.Property(frame, "tasks");
                if (live.ValueKind != JsonValueKind.Array) break;
                foreach (var row in live.EnumerateArray())
                {
                    if (row.Text("task_id") is not { } listed || !ValidId(listed) || MetadataJson.Flag(row, "ambient") || Index(listed) >= 0) continue;
                    Insert(hidden.Remove(listed, out var known) ? known : new BackgroundTask(listed, KindOf(row.Text("task_type")), Description(row.Text("description")), Now));
                }
                break;
        }
        var after = Work;
        return after.SameAs(before) ? null : after;
    }

    /// <summary>The request's own result arrived (not a task notification's).</summary>
    public BackgroundWork? TurnEnded()
    {
        if (turnEnded) return null;
        turnEnded = true;
        return tasks.Count == 0 ? null : Work;
    }

    /// <summary>The process ended: a task without an end report is unknown.</summary>
    public BackgroundWork? Finish()
    {
        var changed = false;
        for (var i = 0; i < tasks.Count; i++) if (tasks[i].Status == "running") { tasks[i] = Finish(tasks[i], "unknown"); changed = true; }
        return changed ? Work : null;
    }

    private int Index(string id) => tasks.FindIndex(t => t.Id == id);
    private void Insert(BackgroundTask task)
    {
        tasks.Add(task);
        while (tasks.Count > BackgroundWork.MaximumTasks) { var settled = tasks.FindIndex(t => t.Status != "running"); tasks.RemoveAt(settled >= 0 ? settled : 0); }
    }
    private void Update(string id, Func<BackgroundTask, BackgroundTask> change) { var index = Index(id); if (index >= 0) tasks[index] = change(tasks[index]); }
    /// <summary>A settled task never runs again; a late or repeated report is ignored.</summary>
    private BackgroundTask Finish(BackgroundTask task, string status) => task.Status != "running" || status == "running" ? task : task with { Status = status, EndedAt = Now };
}

/// <summary>Optional saved state that, when damaged, loads as nothing instead of failing the whole snapshot.</summary>
internal sealed class LenientJsonConverter<T> : JsonConverter<T?> where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try { return document.RootElement.Deserialize<T>(options); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException) { return null; }
    }
    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>A saved list whose damaged elements are dropped one by one (macOS LossyDecoded); a damaged whole loads as nothing.</summary>
internal sealed class LenientListConverter<T> : JsonConverter<List<T>?> where T : class
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var list = new List<T>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            try { if (element.Deserialize<T>(options) is { } value) list.Add(value); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException) { }
        }
        return list;
    }
    public override void Write(Utf8JsonWriter writer, List<T>? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else JsonSerializer.Serialize(writer, value, options);
    }
}
