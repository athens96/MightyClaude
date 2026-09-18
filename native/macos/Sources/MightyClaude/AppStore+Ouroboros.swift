import Foundation
import MightyCore

/// The Ouroboros style of Mighty mode: skill prompts go out through the
/// pane's normal submit path, the agent's questions are answered from the
/// composer, and Ouroboros' own state tools are approved without a prompt.
extension AppStore {
    func usesOuroboros(_ session: RunSession) -> Bool {
        session.kind == "claude" && session.provider == "claude" && session.agentViewMode == "mighty" && session.mightyStyle == OuroborosFlow.style
            && snapshot.workspaces.contains { $0.id == session.workspaceId && $0.remote == nil }
    }

    func setMightyStyle(_ id: String, style: String?) {
        updateSession(id) { session in
            guard session.kind == "claude", session.provider == "claude" else { return }
            session.mightyStyle = style == OuroborosFlow.style ? OuroborosFlow.style : nil
        }
        ouroborosProgress.removeValue(forKey: id)
        if style == OuroborosFlow.style { refreshOuroborosPrerequisites() }
    }

    func refreshOuroborosPrerequisites() {
        Task.detached(priority: .utility) { [weak self] in
            let value = OuroborosFlow.prerequisites()
            await MainActor.run { self?.ouroborosPrerequisites = value }
        }
    }

    /// Sends `/ouroboros:<skill>` as the pane's next request. What the user
    /// typed rides along only for skills that take it; otherwise the draft
    /// stays in the composer untouched.
    func sendOuroboros(_ id: String, skill: String, text: String = "") {
        let takesText = OuroborosFlow.takesText(skill)
        guard let prompt = OuroborosFlow.prompt(skill: skill, text: takesText ? text : "") else { return }
        let kept = takesText ? "" : (drafts[id] ?? "")
        drafts[id] = prompt
        submit(id)
        if !kept.isEmpty { drafts[id] = kept }
    }

    /// The agent's answerable question waiting in this pane, if any. The
    /// decoded form is cached per request: views ask on every keystroke.
    func ouroborosQuestion(for sessionId: String) -> (request: ToolPermissionRequest, questionnaire: UserQuestionnaire)? {
        for request in toolPermissions[sessionId] ?? [] where request.canAnswerQuestions && request.toolName == "AskUserQuestion" {
            let key = permissionResponseKey(sessionId: sessionId, request: request)
            if let cached = questionnaireCache[key] { return (request, cached) }
            if let decoded = request.questionnaire {
                if questionnaireCache.count > 32 { questionnaireCache.removeAll() }
                questionnaireCache[key] = decoded
                return (request, decoded)
            }
        }
        return nil
    }
    /// Multi-select picks are waiting for Enter even though the draft is empty.
    func ouroborosCanConfirm(_ sessionId: String) -> Bool {
        guard let (request, _) = ouroborosQuestion(for: sessionId) else { return false }
        return !ouroborosProgress(for: sessionId, request: request).selected.isEmpty
    }

    func ouroborosProgress(for sessionId: String, request: ToolPermissionRequest) -> QuestionnaireProgress {
        let key = permissionResponseKey(sessionId: sessionId, request: request)
        if let progress = ouroborosProgress[sessionId], progress.requestKey == key { return progress }
        return QuestionnaireProgress(requestKey: key)
    }

    func ouroborosChoose(_ sessionId: String, option: String) {
        guard let (request, questionnaire) = ouroborosQuestion(for: sessionId) else { return }
        var progress = ouroborosProgress(for: sessionId, request: request)
        let step = progress.choose(option, in: questionnaire)
        finishOuroborosStep(step, progress: progress, sessionId: sessionId, request: request)
    }

    /// Enter in the composer while a question waits. Returns false when there
    /// was nothing to answer with, so the caller can leave the draft alone.
    @discardableResult func ouroborosAnswer(_ sessionId: String, text: String) -> Bool {
        guard let (request, questionnaire) = ouroborosQuestion(for: sessionId) else { return false }
        var progress = ouroborosProgress(for: sessionId, request: request)
        guard let step = progress.commit(customText: text, in: questionnaire) else { return false }
        finishOuroborosStep(step, progress: progress, sessionId: sessionId, request: request)
        return true
    }

    func ouroborosBack(_ sessionId: String) {
        guard let (request, _) = ouroborosQuestion(for: sessionId) else { return }
        guard let (_, questionnaire) = ouroborosQuestion(for: sessionId) else { return }
        var progress = ouroborosProgress(for: sessionId, request: request)
        progress.back(in: questionnaire); ouroborosProgress[sessionId] = progress
    }

    private func finishOuroborosStep(_ step: QuestionnaireProgress.Step?, progress: QuestionnaireProgress, sessionId: String, request: ToolPermissionRequest) {
        ouroborosProgress[sessionId] = progress
        guard case .complete(let answers)? = step else { return }
        Task { [weak self] in
            await self?.answerQuestionnaire(sessionId: sessionId, request: request, answers: answers)
            await MainActor.run {
                guard let self, self.ouroborosProgress[sessionId]?.requestKey == progress.requestKey else { return }
                // Keep what the user answered if the request is still pending (the send failed).
                let stillPending = self.toolPermissions[sessionId]?.contains { $0.id == request.id && $0.runId == request.runId } == true
                if stillPending { self.permissionErrors[sessionId] = self.permissionErrors[sessionId] ?? "답변을 전달하지 못했습니다. 다시 시도하세요." }
                else { self.ouroborosProgress.removeValue(forKey: sessionId) }
            }
        }
    }

    /// Approves Ouroboros' state tools and tool discovery for panes in this
    /// style; everything else keeps the pane's permission mode.
    func autoAllowOuroborosTool(_ permission: ToolPermissionRequest, session: RunSession) {
        guard usesOuroboros(session), permission.state == "pending", permission.canAllow, permission.toolName != "AskUserQuestion",
              OuroborosFlow.autoAllowed(toolName: permission.toolName) else { return }
        // The card is hidden only while this approval is actually in flight;
        // if it does not go through, the request shows up like any other.
        let key = permissionResponseKey(sessionId: session.id, request: permission)
        ouroborosAutoAllowing.insert(key)
        Task { [weak self] in
            await self?.answerPermission(sessionId: session.id, request: permission, allow: true)
            await MainActor.run { _ = self?.ouroborosAutoAllowing.remove(key) }
        }
    }

    /// What the permission bar shows for a pane in this style: not the
    /// questions the composer is answering, not approvals already in flight.
    func ouroborosVisibleRequests(_ sessionId: String) -> [ToolPermissionRequest] {
        let answering = ouroborosQuestion(for: sessionId)?.request
        return (toolPermissions[sessionId] ?? []).filter { request in
            if let answering, answering.id == request.id, answering.runId == request.runId { return false }
            return !ouroborosAutoAllowing.contains(permissionResponseKey(sessionId: sessionId, request: request))
        }
    }

    /// Opens a terminal pane that installs the plugin (the CLI's own commands).
    func startOuroborosInstall(from session: RunSession) {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId && $0.remote == nil }),
              let id = addSession(kind: "shell", workspaceId: workspace.id) else { error = "설치 터미널을 열지 못했습니다."; return }
        updateSession(id) { $0.title = "Ouroboros 설치" }
        pendingTerminalInput[id] = OuroborosFlow.installCommand
    }
}
