import Foundation

/// What the plan card and the plan history read: which request is the plan,
/// where the Mighty view draws it, what its outcome says. The Mac views and
/// the phone projection both read these, so they cannot drift apart.
public enum PlanCardSupport {
    /// The pane's pending plan approval: the first one the run asked.
    public static func pendingPlan(_ requests: [ToolPermissionRequest]?) -> ToolPermissionRequest? {
        requests?.first { $0.canAnswerPlan && $0.state == "pending" && $0.plan != nil }
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
