import Foundation
import MightyCore

/// The Ouroboros style of Mighty mode: skill prompts go out through the
/// pane's normal submit path, the agent's questions are answered from the
/// composer, and Ouroboros' own state tools are approved without a prompt.
extension AppStore {
    /// The guided style in effect for a pane: only local Claude panes in Mighty mode have one.
    func guidedStyle(_ session: RunSession) -> String? {
        guard session.kind == "claude", session.provider == "claude", session.agentViewMode == "mighty",
              snapshot.workspaces.contains(where: { $0.id == session.workspaceId && $0.remote == nil }) else { return nil }
        return MightyStyles.normalized(session.mightyStyle)
    }
    func usesOuroboros(_ session: RunSession) -> Bool { guidedStyle(session) == OuroborosFlow.style }
    func usesPaperthin(_ session: RunSession) -> Bool { guidedStyle(session) == PaperthinCatalog.style }
    /// Guided styles answer the agent's questions from the composer.
    func usesGuidedStyle(_ session: RunSession) -> Bool { guidedStyle(session) != nil }

    func setMightyStyle(_ id: String, style: String?) {
        updateSession(id) { session in
            guard session.kind == "claude", session.provider == "claude" else { return }
            session.mightyStyle = MightyStyles.normalized(style)
        }
        ouroborosProgress.removeValue(forKey: id)
        if style == OuroborosFlow.style { refreshOuroborosPrerequisites() }
        if style == PaperthinCatalog.style, let session = snapshot.sessions.first(where: { $0.id == id }) { refreshPaperthin(for: session) }
    }

    func refreshOuroborosPrerequisites() {
        // Set before the task starts, not inside it: a phone's long poll asks
        // again the moment it wakes, and would otherwise spawn one read per
        // poll until the first answer finally lands.
        guard !ouroborosPrerequisitesLoading else { return }
        ouroborosPrerequisitesLoading = true
        Task.detached(priority: .utility) { [weak self] in
            let value = OuroborosFlow.prerequisites()
            await MainActor.run {
                guard let self else { return }
                self.ouroborosPrerequisitesLoading = false
                self.ouroborosPrerequisites = value
                // The phone's Mighty payload carries this and it does not live
                // in the snapshot, so the revision has to be nudged by hand.
                self.mobileObserve()
            }
        }
    }

    /// Sends `/ouroboros:<skill>` as the pane's next request. What the user
    /// typed rides along only for skills that take it; otherwise the draft
    /// stays in the composer untouched.
    func sendOuroboros(_ id: String, skill: String, text: String = "") {
        let takesText = OuroborosFlow.takesText(skill)
        guard let prompt = OuroborosFlow.prompt(skill: skill, text: takesText ? text : "") else { return }
        sendGuidedPrompt(id, prompt: prompt, consumesDraft: takesText)
    }

    /// Submits a style's prompt through the pane's normal path. A prompt that
    /// did not use the draft puts it back afterwards, and so does a submit that
    /// was refused (the prompt is still sitting in the draft then).
    func sendGuidedPrompt(_ id: String, prompt: String, consumesDraft: Bool) {
        let original = drafts[id] ?? ""
        drafts[id] = prompt
        submit(id)
        if drafts[id] == prompt || !consumesDraft { drafts[id] = original }
    }

    // MARK: Paperthin

    /// Every Paperthin skill reads what the user typed as its target (a path, an instruction).
    func sendPaperthin(_ id: String, skill: String, text: String = "") {
        guard let prompt = PaperthinCatalog.prompt(skill: skill, text: text) else { return }
        sendGuidedPrompt(id, prompt: prompt, consumesDraft: true)
    }

    /// Re-reads whether the skills are installed and the workspace's newest casebook.
    func refreshPaperthin(for session: RunSession) {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId && $0.remote == nil }) else { return }
        let path = workspace.path, workspaceId = workspace.id
        // Marked before the task starts: a phone watching this pane polls
        // again as soon as it wakes, and every poll would otherwise start
        // another scan of the workspace until the first one answered.
        guard !paperthinLoading.contains(workspaceId) else { return }
        paperthinLoading.insert(workspaceId)
        Task.detached(priority: .utility) { [weak self] in
            let installed = PaperthinCatalog.installed(workspacePath: path)
            let casebook = PaperthinCasebook.latest(workspacePath: path)
            await MainActor.run {
                guard let self else { return }
                self.paperthinLoading.remove(workspaceId)
                self.paperthinInstalled = installed
                if let casebook { self.paperthinCasebooks[workspaceId] = casebook } else { self.paperthinCasebooks.removeValue(forKey: workspaceId) }
                self.paperthinLoaded.insert(workspaceId)
                // Neither the casebook nor the install state lives in the
                // snapshot, so a watching phone is told about them from here.
                self.mobileObserve()
            }
        }
    }

    func startPaperthinInstall(from session: RunSession) {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId && $0.remote == nil }),
              let id = addSession(kind: "shell", workspaceId: workspace.id) else { error = "설치 터미널을 열지 못했습니다."; return }
        updateSession(id) { $0.title = "Paperthin 설치" }
        pendingTerminalInput[id] = PaperthinCatalog.installCommand
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
