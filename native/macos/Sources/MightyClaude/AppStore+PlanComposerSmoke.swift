import Foundation
import MightyCore

extension AppStore {
    /// The plan card split between the diagram and the composer, read from state (CI has no accessibility
    /// client): a plan waiting under a running request of a Claude pane in its Mighty diagram leaves only its
    /// answers to the composer (above it without a style, in the panel with one), and the whole card docks in
    /// the default view and the timeline. The pane's one plan size is saved, reloaded from disk and cleared
    /// by 창에 맞춤. The pane's view, graph, requests and plan size are put back after; nothing reaches a CLI.
    func runPlanComposerSmoke(sessionId: String) async -> [String: Any] {
        var result: [String: Any] = ["passed": false, "aiRequestSent": false]
        guard let original = snapshot.sessions.first(where: { $0.id == sessionId }) else {
            result["error"] = "The plan composer check has no pane."; return result
        }
        let originalRequests = toolPermissions[sessionId]
        let plan = ToolPermissionRequest(id: "smoke-composer-plan", runId: "smoke-composer-plan-run", toolUseId: "toolu_smoke_composer_plan", toolName: ClaudePlanMode.toolName,
                                         inputJSON: ##"{"plan":"# Smoke plan\n\n1. First step"}"##, summary: "plan",
                                         canAllow: false, canAnswerPlan: true, receivedAt: "2027-01-15T08:05:00.000Z")
        let running = MightyGraphRun(id: "smoke-composer-graph", input: "Plan this", status: "running", sourceRunID: plan.runId)
        func place(guided: Bool) -> PlanCardSupport.ComposerPlace? {
            guard let session = snapshot.sessions.first(where: { $0.id == sessionId }) else { return nil }
            return PlanCardSupport.composerPlace(PlanCardSupport.pendingPlan(toolPermissions[sessionId]), showsDiagram: PlanCardSupport.showsDiagram(session),
                                                 runs: session.mightyGraphRuns, guided: guided)
        }
        do {
            updateSession(sessionId) { $0.agentViewMode = "mighty"; $0.graphViewMode = nil; $0.graphRuns = [running] }
            toolPermissions[sessionId] = [plan]
            let inDiagram = place(guided: false) == .barActions && place(guided: true) == .panelActions
            result["diagramLeavesOnlyTheAnswers"] = inDiagram
            setGraphViewMode(sessionId, mode: .timeline)
            let timeline = place(guided: false) == .card && place(guided: true) == .card
            updateSession(sessionId) { $0.agentViewMode = "default"; $0.graphViewMode = nil }
            let defaultView = place(guided: false) == .card
            result["otherViewsDockTheWholeCard"] = timeline && defaultView

            let saved = MightyGraphBlockSize(width: 820, height: 610)
            setGraphPlanSize(sessionId, size: saved)
            try await flush()
            let reloaded = try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load()
            let kept = reloaded.sessions.first { $0.id == sessionId }?.graphPlanSize == saved
            setGraphPlanSize(sessionId, size: nil)
            let cleared = snapshot.sessions.first { $0.id == sessionId }?.graphPlanSize == nil
            result["planSizeSavedAndCleared"] = kept && cleared
            result["passed"] = inDiagram && timeline && defaultView && kept && cleared
        } catch { result["error"] = error.localizedDescription }
        toolPermissions[sessionId] = originalRequests
        updateSession(sessionId) {
            $0.agentViewMode = original.agentViewMode; $0.graphViewMode = original.graphViewMode
            $0.graphRuns = original.graphRuns; $0.graphPlanSize = original.graphPlanSize
        }
        do { try await flush() } catch { result["restoreError"] = error.localizedDescription }
        let now = snapshot.sessions.first { $0.id == sessionId }
        result["restored"] = now?.agentViewMode == original.agentViewMode && now?.graphRuns == original.graphRuns
            && now?.graphPlanSize == original.graphPlanSize && toolPermissions[sessionId] == originalRequests
        return result
    }
}
