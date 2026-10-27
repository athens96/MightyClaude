import Foundation

/// What the plan card and the plan history read: which request is the plan,
/// where the Mighty view draws it, what its outcome says. The Mac views and
/// the phone projection both read these, so they cannot drift apart.
public enum PlanCardSupport {
    /// The pane's pending plan approval: the first one the run asked.
    public static func pendingPlan(_ requests: [ToolPermissionRequest]?) -> ToolPermissionRequest? {
        requests?.first { $0.canAnswerPlan && $0.state == "pending" && $0.plan != nil }
    }

    /// The plan the pet's bubble shows for its pending request: only a plan
    /// the user can answer (ExitPlanMode); nil for every other request.
    public static func companionPlan(_ request: ToolPermissionRequest) -> String? {
        request.canAnswerPlan ? request.plan : nil
    }

    /// The diagram request a plan's run drew (`MightyGraphRun.id`), nil when
    /// the diagram has none for it.
    public static func graphRunID(runId: String, runs: [MightyGraphRun]) -> String? {
        runs.last { $0.sourceRunID == runId }?.id
    }

    /// The diagram request the pending plan card goes under, when the pane
    /// shows its diagram; nil leaves the card docked above the composer.
    public static func diagramPlanRunID(_ request: ToolPermissionRequest?, showsDiagram: Bool, runs: [MightyGraphRun]) -> String? {
        guard showsDiagram, let request, request.canAnswerPlan, request.state == "pending" else { return nil }
        guard let id = graphRunID(runId: request.runId, runs: runs),
              let run = runs.first(where: { $0.id == id }), !MightyGraphLayout.finished(run) else { return nil }
        return id
    }

    /// Whether the pane shows its Mighty diagram (not the default view or the timeline).
    public static func showsDiagram(_ session: RunSession) -> Bool {
        session.kind == "claude" && MightyGraphSupport.providers.contains(session.provider)
            && session.agentViewMode == "mighty" && session.mightyViewMode == .diagram
    }

    /// Where the composer shows a pending plan's answers.
    public enum ComposerPlace: Equatable, Sendable {
        /// The whole card, plan and answers, docked above the composer: the diagram does not draw the plan.
        case card
        /// Only the answers, in the guided style's panel, where a style shows its choices.
        case panelActions
        /// Only the answers, docked above the composer (no style).
        case barActions
    }

    /// The diagram draws the plan, so the composer shows only its answers.
    public static func composerShowsPlanActions(_ request: ToolPermissionRequest?, showsDiagram: Bool, runs: [MightyGraphRun]) -> Bool {
        diagramPlanRunID(request, showsDiagram: showsDiagram, runs: runs) != nil
    }

    /// One place for each pending plan, so its answers never show twice: the
    /// whole card while the diagram does not draw the plan, else only its
    /// answers — in the guided panel under a style, docked above the composer
    /// without one. nil for anything but an answerable pending plan.
    public static func composerPlace(_ request: ToolPermissionRequest?, showsDiagram: Bool, runs: [MightyGraphRun], guided: Bool) -> ComposerPlace? {
        guard let request, request.canAnswerPlan, request.state == "pending", request.plan != nil else { return nil }
        guard composerShowsPlanActions(request, showsDiagram: showsDiagram, runs: runs) else { return .card }
        return guided ? .panelActions : .barActions
    }

    /// Answered plans whose request the diagram draws, oldest first.
    public static func diagramRecords(_ history: [PlanRecord]?, runs: [MightyGraphRun]) -> [MightyGraphLayout.PlanRecordBlock] {
        (history ?? []).compactMap { record in
            graphRunID(runId: record.graphRunId ?? record.runId, runs: runs).map { MightyGraphLayout.PlanRecordBlock(runID: $0, recordID: record.id) }
        }
    }

    /// 수정 요청 sends only text Core accepts: not empty, within the bound.
    public static func canSendRevise(_ text: String) -> Bool { (try? ClaudePlanMode.validatedFeedback(text)) != nil }

    public static func outcomeTitle(_ outcome: PlanOutcome) -> String {
        switch outcome {
        case .approvedAuto: return L("plan.outcome.approvedAuto")
        case .approvedConfirm: return L("plan.outcome.approvedConfirm")
        case .revised: return L("plan.outcome.revised")
        case .cancelled: return L("plan.outcome.cancelled")
        }
    }

    /// The pane header's word while the turn is over but background work
    /// still runs; nil otherwise.
    public static func backgroundStatus(_ work: BackgroundWork?) -> String? {
        guard let work, work.waitingOnBackground else { return nil }
        return L("plan.background.status", ["count": "\(work.running.count)"])
    }

    /// The pane's background tasks as the plan style's task list draws them
    /// (§1.17): running first, at most `StyleLimits.maximumTaskListItems`.
    public static func backgroundTasks(_ work: BackgroundWork?) -> [StyleWidgetPresentation.Task] {
        guard let work, !work.tasks.isEmpty else { return [] }
        let widget = StyleStateEngine.runState(StyleStateRunStateSource(source: .background, widget: .taskList),
                                               input: StyleRunStateInput(planStage: .planning, background: work))
        guard case .taskList(let items)? = StyleWidgetPresentation.make(widget) else { return [] }
        return items
    }

    /// One row of the background strip: the task list's row, and the kind words the
    /// diagram's block uses (nil: the row keeps the task list's own words) with, for a
    /// background run, its last output line.
    public struct BackgroundStripRow: Sendable, Equatable {
        public var task: StyleWidgetPresentation.Task
        public var kindText: String?
        public var tail: String?
        public init(task: StyleWidgetPresentation.Task, kindText: String? = nil, tail: String? = nil) {
            self.task = task; self.kindText = kindText; self.tail = tail
        }
    }

    /// The strip's rows, in `backgroundTasks`' order. Each task meets its graph block in
    /// `run` (the pane's latest request) by `toolUseId`: a background sub-agent or a
    /// background run takes its kind from the block, and a run its last line of
    /// `output` (`AppStore.backgroundOutput`, by block id). Without a block the CLI's
    /// kind decides and there is no output line; an `other` task keeps its words.
    public static func backgroundStripRows(_ work: BackgroundWork?, run: MightyGraphRun?, output: [String: [String]] = [:]) -> [BackgroundStripRow] {
        guard let work else { return [] }
        let tasks = backgroundTasks(work)
        // The task list's order (`StyleStateEngine`): running work first, each group in the order it started.
        let sources = work.tasks.filter { $0.status == "running" } + work.tasks.filter { $0.status != "running" }
        let runID = run.map { $0.sourceRunID ?? $0.id }
        let blocks = Dictionary((run?.agents ?? []).map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return zip(sources, tasks).map { source, task in
            let block = source.toolUseId.flatMap { tool in runID.flatMap { blocks[ExecutionGraphSupport.agentNodeID(runId: $0, toolUseId: tool)] } }
            let isRun: Bool
            if let block, block.isTask || block.isBackgroundAgent { isRun = block.isTask }
            else if task.kind == "agent" || task.kind == "shell" { isRun = task.kind == "shell" }
            else { return BackgroundStripRow(task: task) }
            let tail = isRun ? block.flatMap { output[$0.id]?.last } : nil
            return BackgroundStripRow(task: task, kindText: MightyGraphSupport.backgroundLabel(run: isRun, status: task.status), tail: tail)
        }
    }

    /// The expandable background list outside a style: in any view once the
    /// turn is over, and in the Mighty view all along, while background work
    /// runs or after it ended and the agent was told. A style that draws its own
    /// task list keeps it, and nothing is drawn while the user hid the line
    /// (`AppSnapshot.showsBackgroundWork`). It stays until the next request
    /// starts a run and clears the work.
    public static func showsBackgroundStrip(_ work: BackgroundWork?, mighty: Bool, styleDrawsTasks: Bool, enabled: Bool = true) -> Bool {
        guard enabled, !styleDrawsTasks, let work, work.turnEnded || mighty else { return false }
        return work.tasks.contains { $0.status == "running" || noticeDelivered($0.status) }
    }

    /// The strip's folded line: the header's "turn done" word while waiting,
    /// else how many still run.
    public static func backgroundSummary(_ work: BackgroundWork) -> String {
        backgroundStatus(work) ?? L("styles.state.backgroundRunning", ["count": "\(work.running.count)"])
    }

    /// A task that ended with the CLI's own end report; the same report
    /// reaches the agent as its task notification. An `unknown` task ended
    /// with the process, so no notification went out.
    public static func noticeDelivered(_ status: String) -> Bool {
        ["completed", "failed", "stopped"].contains(status)
    }

    /// What the agent hears of a background task: a running one tells it when
    /// it ends (the runner keeps the CLI's input open while background work
    /// runs), an ended one has told it; nil for one that ended without telling.
    public static func backgroundNotice(_ status: String) -> String? {
        if status == "running" { return L("plan.background.awaitingNotice") }
        return noticeDelivered(status) ? L("plan.background.noticeDelivered") : nil
    }

    /// The strip's folded line: `backgroundSummary` and that the agent hears
    /// when each ends, or once all ended, that it was told.
    public static func backgroundStripSummary(_ work: BackgroundWork) -> String {
        work.running.isEmpty ? L("plan.background.allNotified") : backgroundSummary(work) + " · " + L("plan.background.awaitingNotice")
    }

    /// Whether a style draws a background task list of its own (§1.17).
    public static func styleDrawsTasks(_ manifest: StyleManifest?) -> Bool {
        manifest?.stateSources?.runState.contains { $0.source == .background && $0.widget == .taskList } ?? false
    }

    /// "HH:mm" in the given zone for an ISO 8601 time; the input when unreadable.
    public static func timeText(_ iso: String, timeZone: TimeZone = .current) -> String {
        guard let date = AgentRunTiming.parseTimestamp(iso) else { return iso }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = timeZone; formatter.dateFormat = "HH:mm"
        return formatter.string(from: date)
    }

    /// "계획 · 14:05 받음".
    public static func receivedText(_ iso: String, timeZone: TimeZone = .current) -> String {
        L("plan.card.received", ["time": timeText(iso, timeZone: timeZone)])
    }

    /// The first non-empty line of a plan, without Markdown heading marks, for a folded block.
    /// Answered plans show as blocks beside their requests in the Mighty
    /// diagram; everywhere else (the default view, the timeline) as the strip.
    public static func showsHistoryStrip(mightyDiagram: Bool) -> Bool { !mightyDiagram }

    /// Words the change request box holds that are still too long to send.
    public static func feedbackTooLong(_ text: String) -> Bool {
        !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !canSendRevise(text)
    }

    public static func headline(_ plan: String) -> String {
        let line = plan.split(whereSeparator: \.isNewline).lazy.map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty } ?? ""
        let text = line.drop { $0 == "#" }.trimmingCharacters(in: .whitespaces)
        return ActivitySupport.clean(text, maximumBytes: 240, singleLine: true)
    }
}
