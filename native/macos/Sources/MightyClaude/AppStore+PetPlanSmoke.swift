import Foundation
import MightyCore

extension AppStore {
    /// The pet's plan bubble through the calls its buttons make (no clicks: CI has no accessibility
    /// client, so the state decides). A plan waiting in a running Claude pane becomes the companion's
    /// approval with its plan and a plain permission does not; Cancel through the companion answers the
    /// plan's own cancel, through a stand-in for the runner, and the request settles. The pane's status,
    /// requests and error are put back after; nothing reaches a CLI.
    func runPetPlanSmoke(sessionId: String) async -> [String: Any] {
        var result: [String: Any] = ["passed": false, "aiRequestSent": false]
        guard let index = snapshot.sessions.firstIndex(where: { $0.id == sessionId }) else {
            result["error"] = "The pet plan check has no pane."; return result
        }
        let originalStatus = snapshot.sessions[index].status
        let originalRequests = toolPermissions[sessionId], originalError = permissionErrors[sessionId]
        var answers: [(runId: String, requestId: String, decision: PlanDecision)] = []
        smokePlanAnswerer = { _, runId, requestId, decision in answers.append((runId, requestId, decision)) }
        let plan = ToolPermissionRequest(id: "smoke-pet-plan", runId: "smoke-pet-plan-run", toolUseId: "toolu_smoke_pet_plan", toolName: ClaudePlanMode.toolName,
                                         inputJSON: ##"{"plan":"# Smoke plan\n\n1. First step\n2. Second step"}"##, summary: "plan",
                                         canAllow: false, canAnswerPlan: true, receivedAt: "2027-01-15T08:05:00.000Z")
        let bash = ToolPermissionRequest(id: "smoke-pet-bash", runId: "smoke-pet-plan-run", toolUseId: "toolu_smoke_pet_bash", toolName: "Bash",
                                         inputJSON: #"{"command":"ls"}"#, summary: "ls")
        do {
            if let index = snapshot.sessions.firstIndex(where: { $0.id == sessionId }) { snapshot.sessions[index].status = "running" }
            toolPermissions[sessionId] = [plan]
            try await waitForSmoke(timeout: 2) { self.companion.approval?.request.id == plan.id }
            let carriesPlan = companion.approval?.plan == "# Smoke plan\n\n1. First step\n2. Second step"
            result["approvalCarriesPlan"] = carriesPlan

            toolPermissions[sessionId] = [bash]
            try await waitForSmoke(timeout: 2) { self.companion.approval?.request.id == bash.id }
            let permissionHasNoPlan = companion.approval?.plan == nil
            result["permissionHasNoPlan"] = permissionHasNoPlan

            toolPermissions[sessionId] = [plan]
            try await waitForSmoke(timeout: 2) { self.companion.approval?.request.id == plan.id }
            guard let answer = companion.answerPlan(.cancel) else { throw MightyError("The pet did not answer the plan.") }
            await answer.value
            try await waitForSmoke(timeout: 2) { self.toolPermissions[sessionId]?.contains { $0.id == plan.id } != true && self.companion.approval == nil && !self.companion.approvalBusy }
            let cancelSent = answers.count == 1 && answers[0].runId == plan.runId && answers[0].requestId == plan.id && answers[0].decision == .cancel
            result["cancelAnswersThePlan"] = cancelSent
            result["requestSettled"] = true
            result["passed"] = carriesPlan && permissionHasNoPlan && cancelSent
        } catch { result["error"] = error.localizedDescription }
        smokePlanAnswerer = nil
        toolPermissions[sessionId] = originalRequests
        permissionErrors[sessionId] = originalError
        if let index = snapshot.sessions.firstIndex(where: { $0.id == sessionId }) { snapshot.sessions[index].status = originalStatus }
        do { try await flush() } catch { result["restoreError"] = error.localizedDescription }
        result["restored"] = snapshot.sessions.first { $0.id == sessionId }?.status == originalStatus && toolPermissions[sessionId] == originalRequests
        return result
    }
}
