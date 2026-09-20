import AppKit
import Foundation
import MightyCore

/// What reading a file the user picked produced: bytes worth showing, or the
/// refusal to print beside the register button.
enum StyleCandidateOutcome {
    case candidate(RegisteredStyle, Data)
    case refused(String)
}

/// A probe with `scopes: ["workspace"]` reads the pane's repository, so the
/// answer belongs to the pair, not to the style alone (§1.5).
struct StylePrerequisiteKey: Hashable {
    var styleId: String
    var workspacePath: String?
}

/// One built-in feature read in one workspace (§1.8).
struct StyleCapabilityKey: Hashable {
    var workspaceId: String
    var name: String
}

/// Mighty mode's guided styles: the registry and its trust store, the pane's
/// binding to one manifest, the prompts its buttons send, the agent's
/// questions answered from the composer, and the style's own auto-allow list.
extension AppStore {
    // MARK: Sources

    var styleDirectory: URL { dataDirectory.appendingPathComponent("styles", isDirectory: true) }
    var styleTrustDirectory: URL { dataDirectory.appendingPathComponent("style-trust", isDirectory: true) }

    /// A path string alone cannot tell a remote host's folder from the Mac's,
    /// so the workspace travels as a pair (§3.1).
    func styleWorkspaceRef(_ session: RunSession) -> StyleWorkspaceRef? {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return nil }
        return StyleWorkspaceRef(path: workspace.path, isRemote: workspace.remote != nil)
    }

    /// Re-reads the three sources. There is no file watcher: a manifest that
    /// changed on disk stays as it was until the next scan (§3.1).
    ///
    /// The returned task settles once the new registry has been published, so
    /// a caller that must act on the result — approving a style and then
    /// putting the pane on it — can wait for it. Scans are chained rather than
    /// run together: two overlapping sweeps would publish in arrival order.
    @discardableResult
    func rescanStyles(workspacePath: String? = nil) -> Task<Void, Never> {
        if let workspacePath { scannedStyleWorkspaces.insert(workspacePath) }
        let previous = styleScanTask
        let directory = styleDirectory
        let store = styleTrust
        let task = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            let workspaces = self.scannedStyleWorkspaces
            // The whole scan runs off the main actor; only the result comes back.
            let scanned = await Task.detached(priority: .utility) { () -> ([DiscoveredStyleFile], [StyleApprovalRecord], Bool, String) in
                let files = Self.discover(directory: directory, workspaces: workspaces)
                let records = (try? await store.load()) ?? []
                return (files, records, await store.isLocked, await store.path)
            }.value
            let made = StyleRegistry.make(files: scanned.0, approvals: scanned.1)
            self.styleDiscovered = scanned.0
            self.styleRegistry = StyleRegistry(styles: made.styles)
            self.styleRejections = made.rejections
            self.styleTrustLocked = scanned.2
            self.styleTrustPath = scanned.3
            // Approval, revocation and rescans change nothing in the
            // snapshot, so a watching phone is told by hand (§4.6).
            self.mobileObserve()
            // A restored pane has no style-id change to react to, so the reads
            // its panel needs are started here, once the registry is known.
            self.refreshStylesInView()
        }
        styleScanTask = task
        return task
    }

    /// Every pane that really runs a style gets its prerequisites and built-in
    /// features read. Both calls are cached and debounced, so this is cheap to
    /// repeat and is the only thing that covers a pane restored from disk.
    func refreshStylesInView() {
        for session in snapshot.sessions {
            guard let style = guidedStyle(session) else { continue }
            refreshStyle(style, for: session)
        }
    }

    /// The three sources read in one sweep, off the main actor.
    nonisolated static func discover(directory: URL, workspaces: Set<String>) -> [DiscoveredStyleFile] {
        var files = StyleSourceScanner.bundled()
        files += StyleSourceScanner.user(directory: directory)
        for path in workspaces.sorted() { files += StyleSourceScanner.workspace(path: path) }
        return files
    }

    /// A removed workspace keeps nothing behind: its scanned path would be
    /// walked by every later scan, and its readings and manifest bytes would
    /// sit in memory for the life of the process.
    func forgetWorkspaceStyles(_ workspace: Workspace) {
        scannedStyleWorkspaces.remove(workspace.path)
        styleCapabilityStates = styleCapabilityStates.filter { $0.key.workspaceId != workspace.id }
        styleAttachments = styleAttachments.filter { $0.key.workspaceId != workspace.id }
        styleCapabilitiesLoaded = styleCapabilitiesLoaded.filter { $0.workspaceId != workspace.id }
        styleCapabilityLoading = styleCapabilityLoading.filter { $0.workspaceId != workspace.id }
        styleCapabilityAgain = styleCapabilityAgain.filter { $0.workspaceId != workspace.id }
        styleCasebooks.removeValue(forKey: workspace.id)
        stylePrerequisites = stylePrerequisites.filter { $0.key.workspacePath != workspace.path }
        stylePrerequisiteAgain = stylePrerequisiteAgain.filter { $0.workspacePath != workspace.path }
        // The rescan is what drops that workspace's discovered files, and with
        // them the manifest bytes the approval card would have shown.
        rescanStyles()
    }

    /// Called when a workspace draws its first pane, so a repo's manifests are
    /// found before anything can pick one. Remote workspaces are never scanned.
    func scanWorkspaceStyles(for session: RunSession) {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }), workspace.remote == nil,
              !scannedStyleWorkspaces.contains(workspace.path) else { return }
        rescanStyles(workspacePath: workspace.path)
    }

    // MARK: Resolution

    /// The guided style in effect for a pane: only local Claude panes in
    /// Mighty mode have one, and only a manifest whose bytes still match the
    /// ones this pane chose (§3.4).
    func guidedStyle(_ session: RunSession) -> RegisteredStyle? {
        guard session.kind == "claude", session.provider == "claude", session.agentViewMode == "mighty",
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }), workspace.remote == nil,
              let id = session.mightyStyle else { return nil }
        return styleRegistry.runnable(id, workspace: styleWorkspaceRef(session), hash: session.mightyStyleHash)
    }

    /// Guided styles answer the agent's questions from the composer.
    func usesGuidedStyle(_ session: RunSession) -> Bool { guidedStyle(session) != nil }

    /// Everything this pane may see in its picker, approved or not (§4.4).
    func applicableStyles(_ session: RunSession) -> [RegisteredStyle] {
        guard session.kind == "claude", session.provider == "claude", session.agentViewMode == "mighty" else { return [] }
        return styleRegistry.applicable(workspace: styleWorkspaceRef(session))
    }

    /// The pane's stored style is an id, and the pane may be showing one whose
    /// bytes have since changed — that pane is a plain CLI and says so.
    func styleNeedsRechoosing(_ session: RunSession) -> Bool {
        session.mightyStyle != nil && guidedStyle(session) == nil && !applicableStyles(session).isEmpty
    }

    func setMightyStyle(_ id: String, style: String?) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return }
        // An unapproved style is never entered from here: the sheet does it.
        let chosen = style.flatMap { value in applicableStyles(session).first { $0.id == value && StyleLaunchWiring.canBindRunWindow(to: $0) } }
        // Only choosing CLI clears a pane; a style that cannot be entered leaves it as it was.
        if style != nil, chosen == nil { return }
        updateSession(id) { session in
            guard session.kind == "claude", session.provider == "claude" else { return }
            session.mightyStyle = chosen?.id
            session.mightyStyleHash = chosen?.hash
        }
        guidedProgress.removeValue(forKey: id)
        if let chosen, let updated = snapshot.sessions.first(where: { $0.id == id }) { refreshStyle(chosen, for: updated) }
        mobileObserve()
    }

    /// Both reads a style needs before its panel can be drawn.
    func refreshStyle(_ style: RegisteredStyle, for session: RunSession) {
        refreshStylePrerequisites(style, for: session)
        refreshStyleCapabilities(style, for: session)
    }

    // MARK: Prerequisites

    /// The path a probe with `scopes: ["workspace"]` reads, which is part of
    /// the answer: the same style is ready in one clone and not in another.
    func stylePrerequisiteKey(_ style: RegisteredStyle, for session: RunSession) -> StylePrerequisiteKey {
        StylePrerequisiteKey(styleId: style.id,
                             workspacePath: snapshot.workspaces.first { $0.id == session.workspaceId && $0.remote == nil }?.path)
    }

    func stylePrerequisite(_ style: RegisteredStyle, for session: RunSession) -> StylePrerequisiteResult? {
        stylePrerequisites[stylePrerequisiteKey(style, for: session)]
    }

    /// Re-reads whether this style's requirements are met, cached per style and
    /// workspace path.
    func refreshStylePrerequisites(_ style: RegisteredStyle, for session: RunSession) {
        loadStylePrerequisites(stylePrerequisiteKey(style, for: session),
                               prerequisites: style.manifest.prerequisites, install: style.manifest.install)
    }

    private func loadStylePrerequisites(_ key: StylePrerequisiteKey, prerequisites: StylePrerequisites, install: StyleInstall?) {
        // Marked before the task starts, not inside it: a phone's long poll
        // asks again the moment it wakes, and would otherwise spawn one read
        // per poll until the first answer finally lands.
        guard stylePrerequisiteLoading.insert(key).inserted else { stylePrerequisiteAgain.insert(key); return }
        let workspacePath = key.workspacePath
        Task.detached(priority: .utility) { [weak self] in
            let value = StylePrerequisiteProbe.evaluate(prerequisites, install: install, workspacePath: workspacePath)
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.stylePrerequisiteLoading.remove(key)
                self.stylePrerequisites[key] = value
                // The phone's Mighty payload carries this and it does not live
                // in the snapshot, so the revision has to be nudged by hand.
                self.mobileObserve()
                // A press of 다시 확인 while a read was already in flight is a
                // request for a *fresh* answer, not for the one in flight.
                if self.stylePrerequisiteAgain.remove(key) != nil {
                    self.loadStylePrerequisites(key, prerequisites: prerequisites, install: install)
                }
            }
        }
    }

    // MARK: Built-in capabilities

    /// Re-reads the named built-in features for this pane's workspace, cached
    /// per workspace **and feature name** because that is what they read (§1.8):
    /// two styles naming different features in one workspace must not erase
    /// each other's readings.
    func refreshStyleCapabilities(_ style: RegisteredStyle, for session: RunSession) {
        guard !style.manifest.capabilities.isEmpty,
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId && $0.remote == nil }) else { return }
        loadStyleCapabilities(style.manifest.capabilities, workspaceId: workspace.id, path: workspace.path)
    }

    private func loadStyleCapabilities(_ names: [String], workspaceId: String, path: String) {
        let keys = Set(names.map { StyleCapabilityKey(workspaceId: workspaceId, name: $0) })
        guard styleCapabilityLoading.isDisjoint(with: keys) else { styleCapabilityAgain.formUnion(keys); return }
        styleCapabilityLoading.formUnion(keys)
        Task.detached(priority: .utility) { [weak self] in
            let value = StyleCapabilities.evaluate(names, workspacePath: path)
            // The legacy phone payload carries the casebook itself, so the
            // raw reading is kept beside the normalised chips.
            let casebook = names.contains(StyleCapabilityID.casebook) ? StyleCasebook.latest(workspacePath: path) : nil
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.styleCapabilityLoading.subtract(keys)
                for key in keys {
                    self.styleCapabilityStates[key] = value.states[key.name]
                    self.styleAttachments[key] = value.attachments
                    self.styleCapabilitiesLoaded.insert(key)
                }
                if let casebook { self.styleCasebooks[workspaceId] = casebook } else { self.styleCasebooks.removeValue(forKey: workspaceId) }
                self.mobileObserve()
                if !self.styleCapabilityAgain.isDisjoint(with: keys) {
                    self.styleCapabilityAgain.subtract(keys)
                    self.loadStyleCapabilities(names, workspaceId: workspaceId, path: path)
                }
            }
        }
    }

    /// Only the features this style declared: another style's reading in the
    /// same workspace is not part of this style's state (§1.8).
    func styleStates(_ style: RegisteredStyle, for session: RunSession) -> [String: String] {
        var states: [String: String] = [:]
        for name in style.manifest.capabilities {
            states[name] = styleCapabilityStates[StyleCapabilityKey(workspaceId: session.workspaceId, name: name)]
        }
        return states
    }

    func styleChips(_ style: RegisteredStyle, for session: RunSession) -> [StyleAttachmentItem] {
        style.manifest.capabilities.flatMap { styleAttachments[StyleCapabilityKey(workspaceId: session.workspaceId, name: $0)] ?? [] }
    }

    func styleCapabilitiesAreLoaded(_ style: RegisteredStyle, for session: RunSession) -> Bool {
        style.manifest.capabilities.allSatisfy { styleCapabilitiesLoaded.contains(StyleCapabilityKey(workspaceId: session.workspaceId, name: $0)) }
    }

    // MARK: Sending

    /// Sends the action's prompt as the pane's next request. What the user
    /// typed rides along only for actions that take it; otherwise the draft
    /// stays in the composer untouched.
    func sendStyleAction(_ id: String, actionId: String, text: String = "") {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let style = guidedStyle(session),
              let action = style.manifest.action(actionId),
              let prompt = style.evaluator.prompt(actionId: actionId, text: action.takesText ? text : "") else { return }
        sendGuidedPrompt(id, prompt: prompt, consumesDraft: action.takesText)
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

    /// Opens a terminal pane with the install command typed into it. The
    /// command is never run: a manifest string does not press Enter (§1.5).
    func startStyleInstall(_ style: RegisteredStyle, from session: RunSession) {
        guard let install = style.manifest.install else { return }
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId && $0.remote == nil }),
              let id = addSession(kind: "shell", workspaceId: workspace.id) else { error = "설치 터미널을 열지 못했습니다."; return }
        updateSession(id) { $0.title = StyleChrome.installPaneTitle(install.paneTitle, styleName: style.manifest.name, source: style.source) }
        pendingTerminalInput[id] = TerminalInput(text: install.command, autoRun: false)
    }

    // MARK: The agent's questions

    /// The agent's answerable question waiting in this pane, if any. The
    /// decoded form is cached per request: views ask on every keystroke.
    func guidedQuestion(for sessionId: String) -> (request: ToolPermissionRequest, questionnaire: UserQuestionnaire)? {
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
    func guidedCanConfirm(_ sessionId: String) -> Bool {
        guard let (request, _) = guidedQuestion(for: sessionId) else { return false }
        return !guidedProgress(for: sessionId, request: request).selected.isEmpty
    }

    func guidedProgress(for sessionId: String, request: ToolPermissionRequest) -> QuestionnaireProgress {
        let key = permissionResponseKey(sessionId: sessionId, request: request)
        if let progress = guidedProgress[sessionId], progress.requestKey == key { return progress }
        return QuestionnaireProgress(requestKey: key)
    }

    func guidedChoose(_ sessionId: String, option: String) {
        guard let (request, questionnaire) = guidedQuestion(for: sessionId) else { return }
        var progress = guidedProgress(for: sessionId, request: request)
        let step = progress.choose(option, in: questionnaire)
        finishGuidedStep(step, progress: progress, sessionId: sessionId, request: request)
    }

    /// Enter in the composer while a question waits. Returns false when there
    /// was nothing to answer with, so the caller can leave the draft alone.
    @discardableResult func guidedAnswer(_ sessionId: String, text: String) -> Bool {
        guard let (request, questionnaire) = guidedQuestion(for: sessionId) else { return false }
        var progress = guidedProgress(for: sessionId, request: request)
        guard let step = progress.commit(customText: text, in: questionnaire) else { return false }
        finishGuidedStep(step, progress: progress, sessionId: sessionId, request: request)
        return true
    }

    func guidedBack(_ sessionId: String) {
        guard let (request, questionnaire) = guidedQuestion(for: sessionId) else { return }
        var progress = guidedProgress(for: sessionId, request: request)
        progress.back(in: questionnaire); guidedProgress[sessionId] = progress
    }

    private func finishGuidedStep(_ step: QuestionnaireProgress.Step?, progress: QuestionnaireProgress, sessionId: String, request: ToolPermissionRequest) {
        guidedProgress[sessionId] = progress
        guard case .complete(let answers)? = step else { return }
        Task { [weak self] in
            await self?.answerQuestionnaire(sessionId: sessionId, request: request, answers: answers)
            await MainActor.run {
                guard let self, self.guidedProgress[sessionId]?.requestKey == progress.requestKey else { return }
                // Keep what the user answered if the request is still pending (the send failed).
                let stillPending = self.toolPermissions[sessionId]?.contains { $0.id == request.id && $0.runId == request.runId } == true
                if stillPending { self.permissionErrors[sessionId] = self.permissionErrors[sessionId] ?? "답변을 전달하지 못했습니다. 다시 시도하세요." }
                else { self.guidedProgress.removeValue(forKey: sessionId) }
            }
        }
    }

    // MARK: Permission

    /// Approves the style's own listed tools for panes in it; everything else
    /// keeps the pane's permission mode. The judgement is made for each
    /// permission event against the pane's style as it stands right then, so a
    /// revocation, a changed hash or a style switch takes effect at once (§4.6).
    func autoAllowGuidedTool(_ permission: ToolPermissionRequest, session: RunSession) {
        guard permission.state == "pending", permission.canAllow, permission.toolName != "AskUserQuestion",
              let style = guidedStyle(session), style.evaluator.autoAllowed(toolName: permission.toolName) else { return }
        // The card is hidden only while this approval is actually in flight;
        // if it does not go through, the request shows up like any other.
        let key = permissionResponseKey(sessionId: session.id, request: permission)
        guidedAutoAllowing.insert(key)
        Task { [weak self] in
            await self?.answerPermission(sessionId: session.id, request: permission, allow: true)
            await MainActor.run { _ = self?.guidedAutoAllowing.remove(key) }
        }
    }

    /// What the permission bar shows for a pane in a guided style: not the
    /// questions the composer is answering, not approvals already in flight.
    func guidedVisibleRequests(_ sessionId: String) -> [ToolPermissionRequest] {
        let answering = guidedQuestion(for: sessionId)?.request
        return (toolPermissions[sessionId] ?? []).filter { request in
            if let answering, answering.id == request.id, answering.runId == request.runId { return false }
            return !guidedAutoAllowing.contains(permissionResponseKey(sessionId: sessionId, request: request))
        }
    }

    // MARK: Trust

    /// Says yes to the bytes that were shown. A workspace manifest is already
    /// on disk; a file the user picked is copied from the very bytes the card
    /// was built from, never re-read (§4.4).
    ///
    /// `then` runs after the rescan has published the new registry, because
    /// its whole job is to move the pane onto a style that only becomes
    /// runnable in that registry (§4.5). It is never called with `true` for a
    /// refused approval, so a pane already on another style keeps it.
    func approveStyle(_ style: RegisteredStyle, data: Data? = nil, then: ((RegisteredStyle) -> Void)? = nil) {
        let store = styleTrust
        Task { [weak self] in
            var failure: String?
            // Only a file the user picked is copied, and only while it is not
            // already where it would be written: approving a manifest found in
            // a repository must not drop a second copy into the app's own data
            // directory, where it would win precedence and refuse the original.
            if let data, StyleLaunchWiring.shouldCopyOnApproval(source: style.source), !FileManager.default.fileExists(atPath: style.path) {
                failure = await MainActor.run { self?.writeUserStyle(data, id: style.id) }
            }
            if failure == nil {
                do { try await store.approve(style) }
                catch { failure = Self.styleTrustMessage(error) }
            }
            guard let self else { return }
            if let failure {
                self.error = failure
                self.rescanStyles()
                return
            }
            await self.rescanStyles().value
            // The registry now holds the approved bytes; the pane may move.
            guard let approved = self.styleRegistry.resolve(style.id), approved.hash == style.hash, StyleLaunchWiring.canBindRunWindow(to: approved) else {
                self.error = self.error ?? "스타일을 허용하지 못했습니다: " + style.manifest.name
                return
            }
            then?(approved)
        }
    }

    func revokeStyle(_ style: RegisteredStyle) { mutateStyleTrust { try await $0.revoke(style) } }
    func allowStyleAgain(_ style: RegisteredStyle) {
        mutateStyleTrust { try await $0.allowAgain(styleId: style.id, path: style.path, workspacePath: style.workspacePath) }
    }

    /// Removes a user-registered file and the decision that went with it.
    func removeStyle(_ style: RegisteredStyle) {
        guard style.source == .user else { return }
        try? FileManager.default.removeItem(atPath: style.path)
        mutateStyleTrust { try await $0.forget(styleId: style.id, path: style.path) }
    }

    private func mutateStyleTrust(_ body: @escaping (StyleTrustStore) async throws -> Void) {
        let store = styleTrust
        Task { [weak self] in
            var failure: String?
            do { try await body(store) } catch { failure = Self.styleTrustMessage(error) }
            await MainActor.run {
                guard let self else { return }
                if let failure { self.error = failure }
                self.rescanStyles()
            }
        }
    }

    static func styleTrustMessage(_ error: Error) -> String {
        switch error {
        case StyleTrustFailure.locked(let path): return StyleSettingsList.lockedMessage(path)
        case StyleTrustFailure.full: return "신뢰 기록이 가득 찼습니다. 설정에서 오래된 항목을 지우세요."
        default: return "신뢰 기록을 저장하지 못했습니다."
        }
    }

    // MARK: Registering a file

    /// Reads a file the user picked exactly once and decodes it, so the card,
    /// the hash and the copy all come from the same bytes (§4.4).
    func readStyleCandidate(at url: URL) -> StyleCandidateOutcome {
        let opened = url.startAccessingSecurityScopedResource()
        defer { if opened { url.stopAccessingSecurityScopedResource() } }
        guard let data = Self.boundedStyleData(url) else { return .refused("파일을 읽지 못했습니다: " + url.path) }
        do {
            let manifest = try StyleManifestDecoder.decode(data, source: .user)
            // The copy is named from the validated id and an id already taken
            // is refused, not shadowed (§3.3).
            if let existing = styleRegistry.resolve(manifest.id) {
                return .refused(StyleErrors.idCollision(manifest.id, existing.source).message)
            }
            let destination = styleDirectory.appendingPathComponent(manifest.id + ".json")
            let style = RegisteredStyle(manifest: manifest, source: .user, path: destination.path,
                                        workspacePath: nil, hash: StyleHash.of(data), approval: .pending)
            return .candidate(style, data)
        } catch let error as StyleManifestError {
            return .refused(error.code + " " + StyleChrome.separator + " " + error.message)
        } catch {
            return .refused(StyleErrors.notJSON.message)
        }
    }

    /// The manifest limit applies before a byte is parsed, and the app module
    /// cannot reach the engine's own bounded reader (§1.11).
    /// A plain read, never `.mappedIfSafe`: a mapped `Data` is a live view of
    /// the file, so the card, the hash and the copy would all drift if the
    /// original were rewritten while the card was open — and truncating it
    /// under the mapping raises `SIGBUS` (§4.4).
    static func boundedStyleData(_ url: URL) -> Data? {
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        guard size <= StyleLimits.maximumBytes else { return nil }
        return try? Data(contentsOf: url)
    }

    /// The copy is named from the validated id, never from the file the user
    /// picked, and an existing name is refused rather than overwritten (§6.2).
    private func writeUserStyle(_ data: Data, id: String) -> String? {
        let destination = styleDirectory.appendingPathComponent(id + ".json")
        do {
            try FileManager.default.createDirectory(at: styleDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: styleDirectory.path)
            guard !FileManager.default.fileExists(atPath: destination.path) else {
                return StyleErrors.idCollision(id, .user).message
            }
            try data.write(to: destination, options: [.atomic])
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: destination.path)
            return nil
        } catch {
            return "스타일 파일을 저장하지 못했습니다: " + destination.path
        }
    }

    /// The bytes a registered style was read from. The disk is read at scan
    /// time only, so the card shows what the registry is actually using (§4.4).
    func styleBytes(for style: RegisteredStyle) -> Data? {
        styleDiscovered.first { $0.url.path == style.path && $0.hash == style.hash }?.data
    }

    /// Settings › 마이티 스타일 › 파일에서 스타일 등록…
    func chooseStyleFile() -> URL? {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.prompt = "읽기"
        panel.message = "마이티 스타일 매니페스트(.json)를 고르세요. 내용을 확인한 뒤에만 등록됩니다."
        return panel.runModal() == .OK ? panel.url : nil
    }
}
