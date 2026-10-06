import Foundation

// Claude Code's plan mode, read from the same stream the app already runs:
// the ExitPlanMode approval over `--permission-prompt-tool stdio`, the
// execution checklist (TodoWrite, or TaskCreate/TaskUpdate where the CLI uses
// those), and the background tasks the CLI reports with `system` task events.
// Windows Core (`ClaudePlanMode.cs`) behaves the same; both read
// `native/contracts/fixtures/claude-plan-mode.json`.

// MARK: - Plan approval

/// How the user answered a plan Claude presented with ExitPlanMode.
public enum PlanDecision: Sendable, Equatable {
    /// Allow, and the session continues in `acceptEdits`.
    case approveAutoEdit
    /// Allow, and the session continues in the CLI's `default` (the app's manual).
    case approveConfirmEach
    /// Deny with the user's feedback; Claude stays in plan mode and plans again.
    case revise(feedback: String)
    /// Deny and interrupt the turn.
    case cancel

    public var outcome: PlanOutcome {
        switch self {
        case .approveAutoEdit: return .approvedAuto
        case .approveConfirmEach: return .approvedConfirm
        case .revise: return .revised
        case .cancel: return .cancelled
        }
    }
}

public enum PlanOutcome: String, Codable, Sendable, CaseIterable {
    case approvedAuto, approvedConfirm, revised, cancelled
    /// The pane's own permission mode after this answer, so later runs and
    /// resumes start where the approved session continued. nil leaves it alone.
    public var paneMode: String? {
        switch self {
        case .approvedAuto: return "acceptEdits"
        case .approvedConfirm: return "manual"
        case .revised, .cancelled: return nil
        }
    }
    /// The mode Claude Code itself switches to (`setMode`) for this answer alone.
    var cliMode: String? {
        switch self {
        case .approvedAuto: return "acceptEdits"
        case .approvedConfirm: return "default"
        case .revised, .cancelled: return nil
        }
    }
}

/// A plan waiting for the user's answer. Like every permission request it is
/// ephemeral: only its outcome is kept, as a `PlanRecord`.
public struct PlanApprovalRequest: Codable, Sendable, Equatable, Identifiable {
    public var sessionId: String
    public var runId: String
    public var requestId: String
    public var toolUseId: String
    /// The plan as Markdown, exactly as Claude wrote it.
    public var plan: String
    /// ISO 8601, when the run's channel received the request.
    public var receivedAt: String
    public var id: String { requestId }

    /// The pending ExitPlanMode request of one pane, or nil for any other request.
    public init?(sessionId: String, permission: ToolPermissionRequest) {
        guard permission.canAnswerPlan, permission.state == "pending", let plan = permission.plan else { return nil }
        self.sessionId = sessionId; runId = permission.runId; requestId = permission.id
        toolUseId = permission.toolUseId; self.plan = plan; receivedAt = permission.receivedAt ?? ClaudePlanMode.timestamp(Date())
    }
}

/// One answered plan in the pane's history (decision 7): approved plans and
/// the plans the user sent back, with what they asked for.
public struct PlanRecord: Codable, Sendable, Equatable, Identifiable {
    /// The ExitPlanMode request id.
    public var id: String
    public var runId: String
    public var plan: String
    /// The stored plan was cut to `ClaudePlanMode.maximumStoredPlanBytes`.
    public var planTruncated: Bool?
    public var receivedAt: String
    public var decidedAt: String
    public var outcome: PlanOutcome
    public var feedback: String?
    /// The id of the diagram request the plan belongs to (`MightyGraphRun.sourceRunID`),
    /// so its history block attaches beside that request.
    public var graphRunId: String?
    /// The run was started in this permission mode by a per-run override (a
    /// plan-mode style, docs/mighty-styles.md §1.17.4), not by the pane's own
    /// mode: its approval leaves the pane's stored mode alone.
    public var launchOverride: String?
    public init(id: String, runId: String, plan: String, planTruncated: Bool? = nil, receivedAt: String, decidedAt: String, outcome: PlanOutcome, feedback: String? = nil, graphRunId: String? = nil, launchOverride: String? = nil) {
        self.id = id; self.runId = runId; self.plan = plan; self.planTruncated = planTruncated
        self.receivedAt = receivedAt; self.decidedAt = decidedAt; self.outcome = outcome; self.feedback = feedback; self.graphRunId = graphRunId
        self.launchOverride = launchOverride
    }
}

public enum ClaudePlanMode {
    public static let toolName = "ExitPlanMode"
    /// Revise feedback the app sends back to Claude.
    public static let maximumFeedbackBytes = 16_384
    /// History kept per pane, newest last.
    public static let maximumHistory = 10
    public static let maximumStoredPlanBytes = 32_768
    public static let maximumStoredFeedbackBytes = 4_096
    /// Plans plus feedback kept per pane. Newer records are kept whole first;
    /// an older one that no longer fits keeps its first lines only.
    public static let maximumHistoryBytes = 98_304
    static let summaryLines = 8, summaryBytes = 1_024, summaryFeedbackBytes = 512

    /// The CLI injects these from its plan file into the permission request.
    /// They are not the model's input and are never echoed back: an echoed
    /// `plan` is taken as a plan the user edited.
    static let injectedKeys: Set<String> = ["plan", "planFilePath"]

    static let reviseMessage = "The user reviewed your plan and wants changes before any code is written. Stay in plan mode, revise the plan, and call ExitPlanMode again. The user said:\n"
    static let cancelMessage = "The user cancelled this plan in Mighty Claude. Do not make any changes; stop and wait for the user's next request."
    static let tooLongMessage = "The plan is longer than Mighty Claude can show in full, so it cannot be approved. Shorten the plan and call ExitPlanMode again."

    /// ISO 8601 with milliseconds, as run timing writes them.
    public static func timestamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }

    /// The Markdown plan of an ExitPlanMode input, nil when there is none.
    public static func plan(inputJSON: String) -> String? {
        guard inputJSON.drop(while: \.isWhitespace).first == "{",
              let object = try? JSONSerialization.jsonObject(with: Data(inputJSON.utf8)) as? [String: Any] else { return nil }
        return plan(input: object)
    }
    static func plan(input: [String: Any]) -> String? {
        guard let text = input["plan"] as? String, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return text
    }

    /// The CLI mode a pane's stored permission mode launches with; nil for
    /// `plan` (or anything else), which approval never returns to.
    static func cliMode(paneMode: String?) -> String? {
        switch paneMode {
        case "manual": return "default"
        case "acceptEdits": return "acceptEdits"
        case "auto": return "auto"
        case "fullAccess": return "bypassPermissions"
        default: return nil
        }
    }
    /// How far each CLI mode lets Claude act without asking.
    static let cliModeRank = ["default": 0, "acceptEdits": 1, "auto": 2, "bypassPermissions": 3]

    /// The session mode an approval switches to: the button's choice, but never
    /// below the mode the pane itself runs in. A pane stored in `plan` (the user
    /// asked for planning) takes the button as is. A pane stored in manual,
    /// acceptEdits, auto or fullAccess entered plan mode on Claude's own call;
    /// approving must not leave it lower than it was launched with, so an auto
    /// or full-access pane returns to auto / bypassPermissions, and a manual or
    /// accept-edits pane takes the higher of its own mode and the button's.
    public static func approvedCLIMode(_ decision: PlanDecision, paneMode: String?) -> String? {
        guard let chosen = decision.outcome.cliMode else { return nil }
        guard let own = cliMode(paneMode: paneMode), cliModeRank[own, default: 0] > cliModeRank[chosen, default: 0] else { return chosen }
        return own
    }

    /// The `response` body of the control_response answering an ExitPlanMode
    /// `can_use_tool` request (Claude Code 2.1.x SDK permission result).
    /// `paneMode` is the pane's stored permission mode (`approvedCLIMode`).
    public static func response(for decision: PlanDecision, toolUseId: String, paneMode: String? = "plan") throws -> [String: Any] {
        switch decision {
        case .approveAutoEdit, .approveConfirmEach:
            // Same shape as the CLI's own "Yes" choices: an empty updatedInput
            // (the CLI keeps the model's own input, never the injected plan)
            // plus a session-only mode change.
            return ["behavior": "allow", "updatedInput": [String: Any](),
                    "updatedPermissions": [["type": "setMode", "mode": approvedCLIMode(decision, paneMode: paneMode)!, "destination": "session"]],
                    "toolUseID": toolUseId]
        case .revise(let feedback):
            return ["behavior": "deny", "message": reviseMessage + (try validatedFeedback(feedback)), "toolUseID": toolUseId]
        case .cancel:
            return ["behavior": "deny", "message": cancelMessage, "interrupt": true, "toolUseID": toolUseId]
        }
    }

    /// Trimmed, non-empty and bounded, or an error the card can show.
    public static func validatedFeedback(_ feedback: String) throws -> String {
        let text = feedback.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw MightyError(L("plan.error.emptyFeedback")) }
        guard text.utf8.count <= maximumFeedbackBytes else { throw MightyError(L("plan.error.feedbackTooLong")) }
        return text
    }

    public static func record(requestId: String, runId: String, plan: String, receivedAt: String, decidedAt: String, decision: PlanDecision) -> PlanRecord {
        var feedback: String?
        if case .revise(let text) = decision { feedback = text.trimmingCharacters(in: .whitespacesAndNewlines) }
        return bounded(PlanRecord(id: requestId, runId: runId, plan: plan, receivedAt: receivedAt, decidedAt: decidedAt, outcome: decision.outcome, feedback: feedback))
    }

    static func bounded(_ record: PlanRecord) -> PlanRecord {
        var value = record
        let plan = ActivitySupport.prefixUTF8(value.plan, maximumBytes: maximumStoredPlanBytes)
        if plan.utf8.count < value.plan.utf8.count { value.plan = plan; value.planTruncated = true }
        value.feedback = value.feedback.map { ActivitySupport.prefixUTF8($0, maximumBytes: maximumStoredFeedbackBytes) }
        if value.feedback?.isEmpty == true { value.feedback = nil }
        return value
    }

    /// The pane's history with `record` added (a repeat of the same request
    /// replaces it), newest last, at most `maximumHistory` long and within
    /// `maximumHistoryBytes`.
    public static func appending(_ record: PlanRecord, to history: [PlanRecord]?) -> [PlanRecord] {
        var list = (history ?? []).filter { $0.id != record.id || $0.runId != record.runId }
        list.append(bounded(record))
        return budgeted(Array(list.suffix(maximumHistory)))
    }

    public static func normalizedHistory(_ history: [PlanRecord]?) -> [PlanRecord]? {
        guard let history else { return nil }
        let list = history.filter { CoreValidation.identifier($0.id) && !$0.plan.isEmpty && AgentRunTiming.parseTimestamp($0.decidedAt) != nil }.map { record -> PlanRecord in
            var value = bounded(record)
            if let id = value.graphRunId, !CoreValidation.identifier(id) { value.graphRunId = nil }
            if let mode = value.launchOverride, mode != "plan" { value.launchOverride = nil }
            return value
        }
        return list.isEmpty ? nil : budgeted(Array(list.suffix(maximumHistory)))
    }

    /// Newest first, whole records while they fit; older ones keep a summary.
    static func budgeted(_ list: [PlanRecord]) -> [PlanRecord] {
        var used = 0
        var output = list
        for index in output.indices.reversed() {
            var record = output[index]
            let cost = record.plan.utf8.count + (record.feedback?.utf8.count ?? 0)
            if used + cost > maximumHistoryBytes {
                let summary = summarized(record.plan)
                if summary.utf8.count < record.plan.utf8.count { record.plan = summary; record.planTruncated = true }
                record.feedback = record.feedback.map { ActivitySupport.prefixUTF8($0, maximumBytes: summaryFeedbackBytes) }
                output[index] = record
            }
            used += record.plan.utf8.count + (record.feedback?.utf8.count ?? 0)
        }
        return output
    }
    /// The first lines of a plan, bounded.
    static func summarized(_ plan: String) -> String {
        ActivitySupport.prefixUTF8(plan.split(separator: "\n", omittingEmptySubsequences: false).prefix(summaryLines).joined(separator: "\n"), maximumBytes: summaryBytes)
    }
}

/// Decodes one element, or nothing: a damaged record is dropped alone.
struct LossyDecoded<Value: Decodable>: Decodable {
    let value: Value?
    init(from decoder: Decoder) throws { value = try? Value(from: decoder) }
}

// MARK: - Execution progress

public struct TodoItem: Codable, Sendable, Equatable {
    /// The task id for TaskCreate/TaskUpdate lists; nil for TodoWrite.
    public var id: String?
    public var content: String
    public var activeForm: String?
    /// `pending`, `in_progress` or `completed`.
    public var status: String
    public init(id: String? = nil, content: String, activeForm: String? = nil, status: String) {
        self.id = id; self.content = content; self.activeForm = activeForm; self.status = status
    }
}

/// The latest checklist the main agent keeps while it carries out a plan.
public struct TodoProgress: Codable, Sendable, Equatable {
    public var items: [TodoItem]
    public init(items: [TodoItem]) { self.items = items }
    public var total: Int { items.count }
    public var completed: Int { items.filter { $0.status == "completed" }.count }
    /// The item being worked on: the first `in_progress` one.
    public var current: TodoItem? { items.first { $0.status == "in_progress" } }
    /// What the current item says while it runs (its activeForm, else its content).
    public var currentText: String? { current.map { $0.activeForm ?? $0.content } }

    public static let maximumItems = 100
    static let statuses: Set<String> = ["pending", "in_progress", "completed"]

    public static func normalized(_ value: TodoProgress?) -> TodoProgress? {
        guard let value else { return nil }
        let items = value.items.compactMap { item -> TodoItem? in
            let content = ActivitySupport.clean(item.content, maximumBytes: 1_024, singleLine: true)
            guard !content.isEmpty else { return nil }
            let active = item.activeForm.map { ActivitySupport.clean($0, maximumBytes: 1_024, singleLine: true) }
            let id = item.id.map { ActivitySupport.clean($0, maximumBytes: 128, singleLine: true) }
            return TodoItem(id: id?.isEmpty == false ? id : nil, content: content, activeForm: active?.isEmpty == false ? active : nil,
                            status: statuses.contains(item.status) ? item.status : "pending")
        }
        return TodoProgress(items: Array(items.prefix(maximumItems)))
    }
}

/// Reads the main agent's checklist from stream-json frames the way Claude
/// Code's own checklist reader does: the task tools (TaskCreate, confirmed by
/// its "Task #N created" result, and TaskUpdate, applied as it is called, with
/// the CLI's input aliases) when there are any, else the last TodoWrite list.
public final class TodoProgressTracker {
    public private(set) var progress: TodoProgress?
    private var tasks: [TodoItem] = []
    private var creates: [(toolUseId: String, item: TodoItem)] = []
    private var todoWrite: [TodoItem]?

    /// `progress` is the pane's saved checklist, for a run that resumes it.
    public init(progress: TodoProgress? = nil) {
        guard let items = progress?.items, !items.isEmpty else { return }
        if items.contains(where: { $0.id != nil }) { tasks = items.filter { $0.id != nil } } else { todoWrite = items }
        self.progress = progress
    }

    /// One stream-json frame; the new progress when it changed. Sub-agent
    /// frames (`parent_tool_use_id`) never touch the main checklist.
    public func consume(_ frame: [String: Any]) -> TodoProgress? {
        guard ExecutionGraphTracker.parentToolID(frame) == nil, let type = frame["type"] as? String,
              let message = frame["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] else { return nil }
        var changed = false
        if type == "assistant" {
            for block in blocks where block["type"] as? String == "tool_use" {
                guard let id = block["id"] as? String, !id.isEmpty, let input = block["input"] as? [String: Any] else { continue }
                switch block["name"] as? String {
                case "TodoWrite":
                    guard let todos = input["todos"] as? [Any] else { continue }
                    todoWrite = todos.compactMap { value -> TodoItem? in
                        guard let row = value as? [String: Any], let content = row["content"] as? String else { return nil }
                        return TodoItem(content: content, activeForm: row["activeForm"] as? String, status: row["status"] as? String ?? "pending")
                    }
                    changed = true
                case "TaskCreate":
                    guard let subject = Self.string(input, ["subject", "title", "name"]), creates.count < 256 else { continue }
                    creates.append((id, TodoItem(content: subject, activeForm: Self.string(input, ["activeForm", "active_form"]) ?? subject, status: "pending")))
                    changed = true
                case "TaskUpdate":
                    guard let taskId = Self.string(input, ["taskId", "id", "task_id"]) else { continue }
                    let status = input["status"] as? String, subject = input["subject"] as? String
                    let active = Self.string(input, ["activeForm", "active_form"])
                    if status == "deleted" { tasks.removeAll { $0.id == taskId } }
                    else if let index = tasks.firstIndex(where: { $0.id == taskId }) {
                        tasks[index].content = subject ?? tasks[index].content
                        tasks[index].activeForm = active ?? tasks[index].activeForm
                        tasks[index].status = status ?? tasks[index].status
                    } else if tasks.count < 256 {
                        tasks.append(TodoItem(id: taskId, content: subject ?? taskId, activeForm: active ?? subject ?? taskId, status: status ?? "pending"))
                    }
                    changed = true
                default: break
                }
            }
        } else if type == "user" {
            for block in blocks where block["type"] as? String == "tool_result" {
                guard let id = block["tool_use_id"] as? String, let index = creates.firstIndex(where: { $0.toolUseId == id }) else { continue }
                if block["is_error"] as? Bool == true { creates.remove(at: index); changed = true; continue }
                let structured = (frame["tool_use_result"] as? [String: Any])?["task"] as? [String: Any]
                guard let taskId = structured?["id"].flatMap(Self.text) ?? Self.createdID(ActivitySupport.output(block["content"])) else { continue }
                var item = creates.remove(at: index).item; item.id = taskId
                if !tasks.contains(where: { $0.id == taskId }), tasks.count < 256 { tasks.append(item) }
                changed = true
            }
        }
        guard changed else { return nil }
        let items = tasks + creates.map(\.item)
        let next = TodoProgress.normalized(TodoProgress(items: items.isEmpty ? todoWrite ?? [] : items))
        guard next != progress else { return nil }
        progress = next
        return next
    }

    private static func string(_ input: [String: Any], _ keys: [String]) -> String? {
        for key in keys { if let value = input[key].flatMap(text), !value.isEmpty { return value } }
        return nil
    }
    private static func text(_ value: Any) -> String? {
        if let text = value as? String { return text }
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return number.stringValue }
        return nil
    }
    /// "Task #3 created successfully: …"
    static func createdID(_ output: String?) -> String? {
        guard let output, let match = output.firstMatch(of: #/^Task #(\S+) created successfully/#) else { return nil }
        return String(match.1)
    }
}

// MARK: - Background work

/// Work the CLI runs beside the turn: a backgrounded Agent or Bash, or any other
/// task it reports, from its `task_started` until its `task_notification`.
public struct BackgroundTask: Codable, Sendable, Equatable, Identifiable {
    /// The CLI's task id.
    public var id: String
    public var toolUseId: String?
    /// `agent`, `shell` or `other`.
    public var kind: String
    public var description: String
    public var startedAt: String
    /// `running`, `completed`, `failed`, `stopped` or `unknown` (the process
    /// ended before the CLI reported an end).
    public var status: String
    /// The CLI's final summary, when it gave one.
    public var summary: String?
    public var endedAt: String?
    public init(id: String, toolUseId: String? = nil, kind: String, description: String, startedAt: String, status: String = "running", summary: String? = nil, endedAt: String? = nil) {
        self.id = id; self.toolUseId = toolUseId; self.kind = kind; self.description = description
        self.startedAt = startedAt; self.status = status; self.summary = summary; self.endedAt = endedAt
    }
}

/// The background tasks of a pane's latest run.
public struct BackgroundWork: Codable, Sendable, Equatable {
    public var tasks: [BackgroundTask]
    /// The request's own result arrived; the process is still alive only for
    /// these tasks (the CLI waits for background agents before it exits).
    public var turnEnded: Bool
    public init(tasks: [BackgroundTask] = [], turnEnded: Bool = false) { self.tasks = tasks; self.turnEnded = turnEnded }
    public var running: [BackgroundTask] { tasks.filter { $0.status == "running" } }
    /// "Turn done, N background agents running": the request answered, the
    /// process still open for its background work (and for new input).
    public var waitingOnBackground: Bool { turnEnded && !running.isEmpty }

    public static let maximumTasks = 32
    static let statuses: Set<String> = ["running", "completed", "failed", "stopped", "unknown"]

    /// A saved run is over: what was still running did not report an end.
    public static func normalized(_ value: BackgroundWork?, restoring: Bool) -> BackgroundWork? {
        guard let value else { return nil }
        let tasks = value.tasks.compactMap { task -> BackgroundTask? in
            guard BackgroundTaskTracker.validID(task.id), AgentRunTiming.parseTimestamp(task.startedAt) != nil else { return nil }
            var item = task
            item.kind = ["agent", "shell"].contains(task.kind) ? task.kind : "other"
            item.description = ActivitySupport.clean(task.description, maximumBytes: 1_024, singleLine: true)
            item.summary = task.summary.map { ActivitySupport.clean($0, maximumBytes: 4_096) }.flatMap { $0.isEmpty ? nil : $0 }
            if !statuses.contains(item.status) { item.status = "unknown" }
            if restoring, item.status == "running" { item.status = "unknown" }
            if item.endedAt.flatMap(AgentRunTiming.parseTimestamp) == nil { item.endedAt = nil }
            if let id = item.toolUseId, !CoreValidation.identifier(id) { item.toolUseId = nil }
            return item
        }
        guard !tasks.isEmpty else { return nil }
        return BackgroundWork(tasks: Array(tasks.suffix(maximumTasks)), turnEnded: value.turnEnded || restoring)
    }
}

/// Reads Claude Code's `system` task events (`task_started`, `task_updated`,
/// `task_progress`, `task_notification`, `background_tasks_changed`). A task
/// the CLI registered in the foreground (a blocking Agent call) stays out until
/// it is moved to the background.
public final class BackgroundTaskTracker {
    public private(set) var work = BackgroundWork()
    private var hidden: [String: BackgroundTask] = [:]
    private let clock: () -> Date
    public init(clock: @escaping () -> Date = Date.init) { self.clock = clock }

    static func validID(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 128 && !value.unicodeScalars.contains { $0.properties.generalCategory == .control || $0 == " " }
    }
    static func kind(_ type: String?) -> String {
        switch type {
        case "local_agent", "remote_agent", "local_workflow", "in_process_teammate": return "agent"
        case "local_bash": return "shell"
        default: return "other"
        }
    }
    static func status(_ value: String?) -> String? {
        switch value {
        case "running", "pending": return "running"
        case "completed": return "completed"
        case "failed", "error": return "failed"
        case "killed", "stopped", "cancelled": return "stopped"
        default: return nil
        }
    }
    private var now: String { ClaudePlanMode.timestamp(clock()) }

    /// One stream-json frame; the new state when the visible tasks changed.
    public func consume(_ frame: [String: Any]) -> BackgroundWork? {
        guard frame["type"] as? String == "system", let subtype = frame["subtype"] as? String else { return nil }
        let before = work
        switch subtype {
        case "task_started":
            guard let id = frame["task_id"] as? String, Self.validID(id), frame["ambient"] as? Bool != true, hidden[id] == nil else { break }
            if let listed = index(id) {
                // The level event listed it first: fill in what only the start carries.
                if work.tasks[listed].toolUseId == nil, let tool = frame["tool_use_id"] as? String, CoreValidation.identifier(tool) { work.tasks[listed].toolUseId = tool }
                if let type = frame["task_type"] as? String { work.tasks[listed].kind = Self.kind(type) }
                if work.tasks[listed].description.isEmpty, let description = frame["description"] as? String {
                    work.tasks[listed].description = ActivitySupport.clean(description, maximumBytes: 1_024, singleLine: true)
                }
                break
            }
            let task = BackgroundTask(id: id, toolUseId: (frame["tool_use_id"] as? String).flatMap { CoreValidation.identifier($0) ? $0 : nil },
                                      kind: Self.kind(frame["task_type"] as? String),
                                      description: ActivitySupport.clean(frame["description"] as? String ?? "", maximumBytes: 1_024, singleLine: true),
                                      startedAt: now)
            if frame["is_backgrounded"] as? Bool == false { if hidden.count < 256 { hidden[id] = task } }
            else { insert(task) }
        case "task_updated":
            guard let id = frame["task_id"] as? String, let patch = frame["patch"] as? [String: Any] else { break }
            if patch["is_backgrounded"] as? Bool == true, let task = hidden.removeValue(forKey: id) { insert(task) }
            if var task = hidden[id] {
                // Still in the foreground: keep its state for a later move.
                if let description = patch["description"] as? String { task.description = ActivitySupport.clean(description, maximumBytes: 1_024, singleLine: true) }
                if let status = Self.status(patch["status"] as? String), status != "running" { hidden.removeValue(forKey: id) } else { hidden[id] = task }
                break
            }
            update(id) { task in
                if let description = patch["description"] as? String { task.description = ActivitySupport.clean(description, maximumBytes: 1_024, singleLine: true) }
                if let status = Self.status(patch["status"] as? String) { finish(&task, status: status) }
                if task.status == "failed", task.summary == nil, let error = patch["error"] as? String { task.summary = ActivitySupport.clean(error, maximumBytes: 4_096) }
            }
        case "task_progress":
            guard let id = frame["task_id"] as? String, let description = frame["description"] as? String else { break }
            update(id) { $0.description = ActivitySupport.clean(description, maximumBytes: 1_024, singleLine: true) }
        case "task_notification":
            guard let id = frame["task_id"] as? String else { break }
            hidden.removeValue(forKey: id)
            update(id) { task in
                // Only the report that ends the task may set its summary.
                guard task.status == "running" else { return }
                finish(&task, status: Self.status(frame["status"] as? String) ?? "completed")
                if let summary = frame["summary"] as? String {
                    let text = ActivitySupport.clean(summary, maximumBytes: 4_096)
                    if !text.isEmpty { task.summary = text }
                }
            }
        case "background_tasks_changed":
            // A level signal: what is listed is live and in the background.
            for case let row as [String: Any] in frame["tasks"] as? [Any] ?? [] {
                guard let id = row["task_id"] as? String, Self.validID(id), row["ambient"] as? Bool != true, index(id) == nil else { continue }
                insert(hidden.removeValue(forKey: id) ?? BackgroundTask(id: id, kind: Self.kind(row["task_type"] as? String),
                    description: ActivitySupport.clean(row["description"] as? String ?? "", maximumBytes: 1_024, singleLine: true), startedAt: now))
            }
        default: break
        }
        return work != before ? work : nil
    }

    /// The request's own result arrived (not a task notification's).
    public func turnEnded() -> BackgroundWork? {
        guard !work.turnEnded else { return nil }
        work.turnEnded = true
        return work.tasks.isEmpty ? nil : work
    }

    /// The process ended: a task without an end report is `unknown`.
    public func finish() -> BackgroundWork? {
        let before = work
        for index in work.tasks.indices where work.tasks[index].status == "running" { finish(&work.tasks[index], status: "unknown") }
        return work != before ? work : nil
    }

    private func index(_ id: String) -> Int? { work.tasks.firstIndex { $0.id == id } }
    private func insert(_ task: BackgroundTask) {
        work.tasks.append(task)
        while work.tasks.count > BackgroundWork.maximumTasks {
            work.tasks.remove(at: work.tasks.firstIndex { $0.status != "running" } ?? 0)
        }
    }
    private func update(_ id: String, _ change: (inout BackgroundTask) -> Void) {
        guard let index = index(id) else { return }
        change(&work.tasks[index])
    }
    /// A settled task never runs again; a late or repeated report is ignored.
    private func finish(_ task: inout BackgroundTask, status: String) {
        guard task.status == "running", status != "running" else { return }
        task.status = status; task.endedAt = now
    }
}

// MARK: - Pane state

extension RunSession {
    /// Plan answers, execution progress and background work a run reports.
    /// An approved plan also sets the pane's own permission mode, so the next
    /// request and a resume continue the way the user approved.
    public mutating func recordPlanMode(_ event: RunEvent) {
        guard kind == "claude", provider == "claude" else { return }
        switch event.type {
        case "plan":
            guard let record = event.plan else { return }
            planHistory = ClaudePlanMode.appending(record, to: planHistory)
            // Only a pane the user put in plan mode takes the approved mode; one
            // Claude moved into planning by itself keeps its own, and so does a
            // run a plan-mode style started in plan mode (§1.17.4) — even in a
            // pane stored in plan, whose next request plans again.
            if settings.permissionMode == "plan", record.launchOverride == nil, let mode = record.outcome.paneMode { settings.permissionMode = mode }
        case "todos": todoProgress = TodoProgress.normalized(event.todos)
        case "background": backgroundWork = BackgroundWork.normalized(event.background, restoring: false)
        case "status":
            // Background work belongs to the run that started it.
            if event.status == "running" { backgroundWork = nil }
        default: break
        }
    }
}
