import AppKit
import Combine
import MightyCore
import UserNotifications

struct CompanionPreferences: Codable {
    var enabled = true
    var showsTask = true
    var notifications = true
    var reducedMotion = false
    var selectedPet = "mighty-raccoon"
    /// The bubble's size as the user left it; nil is the default width and a
    /// height that follows the content.
    var bubbleWidth: Double?
    var bubbleHeight: Double?
}

/// nil width is the default one; nil height follows the bubble's content.
struct CompanionBubbleSize: Equatable {
    var width: CGFloat?
    var height: CGFloat?
    var resolvedWidth: CGFloat { CompanionBubbleLayout.clampedWidth(width) }
    var resolvedHeight: CGFloat? { height.map(CompanionBubbleLayout.clampedHeight) }
}

/// The approval the pet bubble offers. Only local Claude panes raise these.
struct CompanionApproval: Equatable, Identifiable {
    let sessionId: String
    let sessionTitle: String
    let workspaceName: String
    let request: ToolPermissionRequest
    /// A finished plan (ExitPlanMode) the bubble shows with its four answers; nil for every other request.
    let plan: String?
    init(sessionId: String, sessionTitle: String, workspaceName: String, request: ToolPermissionRequest) {
        self.sessionId = sessionId; self.sessionTitle = sessionTitle; self.workspaceName = workspaceName; self.request = request
        plan = PlanCardSupport.companionPlan(request)
    }
    var id: String { sessionId + "|" + request.runId + "|" + request.id }
    var presentation: ToolPermissionPresentation { ToolPermissionPresentation.make(toolName: request.toolName, inputJSON: request.inputJSON) }
    /// Answered from the bubble one question at a time. The bubble has no "Write your own answer"
    /// row (the pet never takes the keyboard), so a typed answer still needs the pane.
    var quickQuestionnaire: UserQuestionnaire? {
        guard request.canAnswerQuestions, let questionnaire = request.questionnaire, !questionnaire.questions.isEmpty,
              questionnaire.questions.allSatisfy({ !$0.options.isEmpty }) else { return nil }
        return questionnaire
    }
}

struct AgentPresence: Identifiable {
    let id: String
    var title: String
    var workspace: String
    var provider: String
    var status: String
    var summary: String
    var input: String?
    var activity: AgentActivity?
    var timing: AgentRunTiming?
}

/// Consumes the same live events as the conversation, never provider log files.
@MainActor
final class AgentCompanion: ObservableObject {
    @Published var preferences = CompanionPreferences() { didSet { savePreferences(); updateOverlay() } }
    @Published private(set) var agents: [AgentPresence] = []
    @Published private(set) var pets: [CompanionPet] = []
    @Published var message: String?
    @Published var notificationStatus = L("settings.components.statusChecking")
    @Published var showsStatus = false
    @Published private(set) var approval: CompanionApproval?
    @Published private(set) var approvalBusy = false
    @Published private(set) var approvalError: String?
    @Published private(set) var pinnedAgent: String?
    /// Where the user is in each pending questionnaire, by approval id, so paging
    /// to another agent's request and back keeps the answers already given.
    @Published private var progressByRequest: [String: QuestionnaireProgress] = [:]
    var questionProgress: QuestionnaireProgress {
        let key = approval?.id ?? ""
        return progressByRequest[key] ?? QuestionnaireProgress(requestKey: key)
    }
    private var subscriptions = Set<AnyCancellable>()
    private weak var store: AppStore?
    private var activities: [String: AgentActivity] = [:]
    private var liveTools: [String: [AgentActivity]] = [:]
    private var submittedInputs: [String: String] = [:]
    private var activeRuns = Set<String>()
    private var lastTouched: [String: Date] = [:]
    private var preferencesURL: URL?
    private var overlay: CompanionPanel?
    private var planWindow: CompanionPlanWindow?
    private var notifications: CompletionNotifications?
    private var stopped = false
    private(set) var completionCount = 0
    var testMode: Bool { ProcessInfo.processInfo.arguments.contains { $0.contains("smoke") } }
    var runningCount: Int { agents.filter { $0.status == "running" || $0.status == "waiting" }.count }
    var current: AgentPresence? {
        agents.sorted {
            let l = priority($0.status), r = priority($1.status)
            if l != r { return l > r }
            return (lastTouched[$0.id] ?? .distantPast) > (lastTouched[$1.id] ?? .distantPast)
        }.first
    }
    /// Busy agents in sidebar order, so paging through them never reshuffles.
    var activeAgents: [AgentPresence] { agents.filter { $0.status == "running" || $0.status == "waiting" } }
    /// What the bubble shows: the agent the user paged to while it is still busy, else `current`.
    var shown: AgentPresence? {
        let id = CompanionCarousel.shown(pinned: pinnedAgent, active: activeAgents.map(\.id), fallback: current?.id)
        return agents.first { $0.id == id }
    }
    /// "2 / 3" for the bubble; nil when there is nothing to page through.
    var page: (position: Int, count: Int)? {
        let ids = activeAgents.map(\.id)
        guard ids.count > 1, let position = CompanionCarousel.position(of: shown?.id, in: ids) else { return nil }
        return (position, ids.count)
    }
    func showNeighbour(_ offset: Int) {
        guard let next = CompanionCarousel.step(from: shown?.id, in: activeAgents.map(\.id), by: offset) else { return }
        // Back on the pet's own choice means no pick at all: it may follow urgency again.
        pinnedAgent = next == current?.id ? nil : next
        // The approval card follows the page: the agent now shown may have its own request.
        if let store { updateApproval(permissions: store.toolPermissions, responses: store.permissionResponses, errors: store.permissionErrors) }
        updateOverlay()
    }
    /// Turns to the agent whose request is hidden behind the page the user chose.
    func showApprovalAgent() {
        guard let approval = hiddenApproval else { return }
        pinnedAgent = approval.sessionId == current?.id ? nil : approval.sessionId
        updateOverlay()
    }
    /// A request hidden behind the page the user chose, while its agent can still be turned to.
    var hiddenApproval: CompanionApproval? {
        guard let approval, visibleApproval == nil, activeAgents.contains(where: { $0.id == approval.sessionId }) else { return nil }
        return approval
    }
    /// An approval interrupts the bubble unless the user paged to an agent that
    /// is not the one asking; paging back (or that agent finishing) brings it up again.
    var visibleApproval: CompanionApproval? {
        guard let approval else { return nil }
        return pinnedAgent == nil || approval.sessionId == shown?.id ? approval : nil
    }
    var selectedPet: CompanionPet? { pets.first { $0.id == preferences.selectedPet } ?? pets.first }

    func configure(store: AppStore) {
        guard self.store == nil else { return }
        self.store = store
        preferencesURL = store.dataDirectory.appendingPathComponent("companion-settings.json")
        if let url = preferencesURL, let data = try? Data(contentsOf: url), let settings = try? JSONDecoder().decode(CompanionPreferences.self, from: data) { preferences = settings }
        reloadPets()
        refresh(store.snapshot)
        notifications = CompletionNotifications { [weak self] id in self?.focus(id) }
        store.$toolPermissions.combineLatest(store.$permissionResponses, store.$permissionErrors)
            .sink { [weak self] permissions, responses, errors in self?.updateApproval(permissions: permissions, responses: responses, errors: errors) }
            .store(in: &subscriptions)
        if !testMode {
            Task { await refreshNotificationStatus(request: preferences.notifications) }
            overlay = CompanionPanel(companion: self)
            updateOverlay()
        }
    }

    func refresh(_ snapshot: AppSnapshot) {
        guard !stopped else { return }
        let ids = Set(snapshot.sessions.map(\.id))
        activities = activities.filter { ids.contains($0.key) }
        liveTools = liveTools.filter { ids.contains($0.key) }
        submittedInputs = submittedInputs.filter { ids.contains($0.key) }
        activeRuns.formIntersection(ids)
        lastTouched = lastTouched.filter { ids.contains($0.key) }
        agents = snapshot.sessions.filter { $0.kind != "shell" && !FilePaneKind.isFilePane($0.kind) }.map { session in
            let activity = activities[session.id]
            let live = session.status == "running"
            let state = live && activity?.state == "waiting" ? "waiting" : session.status
            let text: String
            if live, let activity, !activity.summary.isEmpty { text = activity.summary }
            else { text = state == "completed" ? L("companion.status.completed") : state == "error" ? L("companion.status.error") : state == "stopped" ? L("companion.status.stopped") : live ? L("companion.status.running") : L("companion.status.idle") }
            return AgentPresence(id: session.id, title: session.title,
                workspace: snapshot.workspaces.first { $0.id == session.workspaceId }?.name ?? "",
                provider: session.provider, status: state, summary: text,
                input: submittedInputs[session.id] ?? session.logs.last(where: { $0.kind == "user" }).map { Self.inputPreview($0.text) }, activity: activity, timing: session.runTiming)
        }
        if let pinnedAgent, !activeAgents.contains(where: { $0.id == pinnedAgent }) {
            self.pinnedAgent = nil
            // Resizing the panel lays its view out synchronously; keep that out of this publish.
            Task { @MainActor [weak self] in self?.updateOverlay() }
        }
    }

    /// The submitted request stays visible while tool/output events change.
    /// The snapshot fallback is MightyClaude's own conversation, not provider logs.
    func recordInput(sessionID: String, text: String) {
        submittedInputs[sessionID] = Self.inputPreview(text)
    }
    func beginRun(sessionID: String, at date: Date = Date()) {
        activeRuns.insert(sessionID)
        activities.removeValue(forKey: sessionID)
        liveTools.removeValue(forKey: sessionID)
        lastTouched[sessionID] = date
    }
    private static func inputPreview(_ text: String) -> String {
        ActivitySupport.clean(text, maximumBytes: 2_048, singleLine: true)
    }

    func receive(_ event: RunEvent, snapshot: AppSnapshot, at date: Date = Date()) {
        guard !stopped, let session = snapshot.sessions.first(where: { $0.id == event.sessionId }), session.kind != "shell" else { return }
        defer { refresh(snapshot) }
        if event.type == "log", event.entry?.kind == "user", let text = event.entry?.text { recordInput(sessionID: event.sessionId, text: text) }
        if event.type == "activity", let activity = event.activity {
            var pending = liveTools[event.sessionId] ?? []
            if activity.kind != "turn" {
                pending.removeAll { $0.id == activity.id }
                if ["running", "waiting"].contains(activity.state), session.status == "running" { pending.append(activity) }
            } else if ["completed", "error", "stopped"].contains(activity.state) { pending.removeAll() }
            liveTools[event.sessionId] = Array(pending.suffix(512))
            activities[event.sessionId] = pending.last(where: { $0.state == "waiting" }) ?? pending.last ?? activity
            lastTouched[event.sessionId] = Date()
        }
        guard event.type == "status", let status = event.status else { return }
        if status == "running" {
            activeRuns.insert(event.sessionId)
            activities.removeValue(forKey: event.sessionId)
            liveTools.removeValue(forKey: event.sessionId)
            lastTouched[event.sessionId] = Date()
        } else if ["completed", "error", "stopped"].contains(status) {
            let wasRunning = activeRuns.remove(event.sessionId) != nil
            liveTools.removeValue(forKey: event.sessionId)
            lastTouched[event.sessionId] = Date()
            if wasRunning && status == "completed" {
                completionCount += 1
                let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }?.name
                if preferences.notifications && !testMode { notifications?.send(sessionID: session.id, title: workspace.map { $0 + " · " + session.title } ?? session.title) }
            }
        }
    }

    /// The oldest pending request of the agent the bubble already shows, else
    /// the oldest pending request anywhere. Answered or cancelled ones vanish.
    private func updateApproval(permissions: [String: [ToolPermissionRequest]], responses: Set<String>, errors: [String: String]) {
        guard let store, !stopped else { approval = nil; return }
        let candidates = permissions.compactMap { sessionId, requests -> CompanionApproval? in
            guard let request = requests.first(where: { $0.state == "pending" }),
                  let session = store.snapshot.sessions.first(where: { $0.id == sessionId }), session.status == "running",
                  store.snapshot.workspaces.contains(where: { $0.id == session.workspaceId }) else { return nil }
            let workspace = store.snapshot.workspaces.first { $0.id == session.workspaceId }?.name ?? ""
            return CompanionApproval(sessionId: sessionId, sessionTitle: session.title, workspaceName: workspace, request: request)
        }
        let preferred = shown?.id
        let next = candidates.first { $0.sessionId == preferred } ?? candidates.sorted { $0.sessionTitle < $1.sessionTitle }.first
        let pending = Set(candidates.map(\.id))
        if progressByRequest.keys.contains(where: { !pending.contains($0) }) { progressByRequest = progressByRequest.filter { pending.contains($0.key) } }
        if next != approval { approval = next; updateOverlay() }
        // Only publish real changes; a same-value assignment would still redraw
        // every view that observes the companion, including pane headers.
        let busy = next.map { responses.contains(store.permissionResponseKey(sessionId: $0.sessionId, request: $0.request)) } ?? false
        if busy != approvalBusy { approvalBusy = busy }
        let error = next.flatMap { errors[$0.sessionId] }
        if error != approvalError { approvalError = error }
        // The plan's own window goes with its request: answered, cancelled, or its run ended.
        if let window = planWindow, permissions[window.approval.sessionId]?.contains(where: {
            $0.id == window.approval.request.id && $0.runId == window.approval.request.runId && $0.state == "pending"
        }) != true { closePlanWindow() }
    }
    func answerApproval(allow: Bool) {
        guard let store, let approval, !approvalBusy else { return }
        Task { await store.answerPermission(sessionId: approval.sessionId, request: approval.request, allow: allow) }
    }
    /// One of the plan's answers from the bubble; Cancel is the plan's own cancel, not a generic deny.
    @discardableResult func answerPlan(_ decision: PlanDecision) -> Task<Void, Never>? {
        guard let store, let approval, approval.plan != nil, !approvalBusy else { return nil }
        return Task { await store.answerPlan(sessionId: approval.sessionId, request: approval.request, decision: decision) }
    }
    /// The plan in a window that takes the keyboard, for a change request or to read all of it.
    /// Only a click opens it, so bringing it forward never takes focus the user did not give.
    func openPlanWindow(revising: Bool) {
        guard let store, let approval, approval.plan != nil else { return }
        if let planWindow, planWindow.approval.id == approval.id { planWindow.show(); return }
        closePlanWindow()
        let window = CompanionPlanWindow(approval: approval, store: store, revising: revising) { [weak self] closed in
            if self?.planWindow === closed { self?.planWindow = nil }
        }
        planWindow = window
        window.show()
    }
    private func closePlanWindow() {
        let window = planWindow
        planWindow = nil
        window?.close()
    }
    /// A single-choice tap records the answer and moves on; a multi-choice tap toggles.
    /// Nothing is sent from a tap when there are several questions: the last one waits for "Send".
    func chooseOption(_ label: String) {
        guard let approval, !approvalBusy, let questionnaire = approval.quickQuestionnaire else { return }
        var progress = questionProgress
        let step = progress.choose(label, in: questionnaire)
        progressByRequest[approval.id] = progress
        if questionnaire.questions.count == 1 { send(step, approval: approval) }
    }
    /// "Next" / "Send": needed under a multi-choice question and on the last of several questions.
    func needsCommitButton(_ question: UserQuestionnaire.Question, in questionnaire: UserQuestionnaire) -> Bool {
        question.multiSelect || (questionnaire.questions.count > 1 && questionProgress.index + 1 == questionnaire.questions.count)
    }
    func canCommit(_ question: UserQuestionnaire.Question) -> Bool {
        question.multiSelect ? !questionProgress.selected.isEmpty : questionProgress.answers[question.question] != nil
    }
    func commitQuestion() {
        guard let approval, !approvalBusy, let questionnaire = approval.quickQuestionnaire,
              let question = questionProgress.current(in: questionnaire), canCommit(question) else { return }
        var progress = questionProgress
        if question.multiSelect {
            let step = progress.commit(customText: "", in: questionnaire)
            progressByRequest[approval.id] = progress
            send(step, approval: approval)
        } else if (try? questionnaire.validatedAnswers(progress.answers)) != nil {
            send(.complete(progress.answers), approval: approval)
        }
    }
    func previousQuestion() {
        guard let approval, !approvalBusy, let questionnaire = approval.quickQuestionnaire else { return }
        var progress = questionProgress
        progress.back(in: questionnaire)
        progressByRequest[approval.id] = progress
    }
    private func send(_ step: QuestionnaireProgress.Step?, approval: CompanionApproval) {
        guard let store, case .complete(let answers) = step else { return }
        Task { await store.answerQuestionnaire(sessionId: approval.sessionId, request: approval.request, answers: answers) }
    }
    func openApproval() { focus(approval?.sessionId) }

    func focus(_ id: String?) {
        guard let store else { return }
        if let id { store.selectSession(id) }
        showsStatus = false
        NSApp.activate(ignoringOtherApps: true)
        let window = NSApp.windows.first { $0.canBecomeMain && !$0.isSheet && !($0 is NSPanel) && $0.contentView != nil }
        window?.deminiaturize(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    func reloadPets() {
        CompanionPet.clearCache()
        pets = CompanionPet.loadAvailable(dataDirectory: store?.dataDirectory)
        if selectedPet == nil { message = L("companion.error.loadFailed") }
    }

    func importPet() {
        guard let directory = store?.dataDirectory else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.message = L("companion.import.panelMessage")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let pet = try CompanionPet.install(from: url, into: directory.appendingPathComponent("pets"))
            reloadPets()
            preferences.selectedPet = pet.id
            preferences.enabled = true
            message = L("companion.import.applied", ["name": pet.name])
        } catch { message = error.localizedDescription }
    }

    func refreshNotificationStatus(request: Bool = false) async {
        guard !testMode else { notificationStatus = L("windows.notifications.statusVerificationMode"); return }
        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        if request && settings.authorizationStatus == .notDetermined {
            do { _ = try await center.requestAuthorization(options: [.alert, .sound]) }
            catch { message = L("companion.notifications.permissionFailed", ["error": error.localizedDescription]) }
        }
        let current = await center.notificationSettings()
        notificationStatus = current.authorizationStatus == .authorized ? L("windows.notifications.statusAllowed") : current.authorizationStatus == .denied ? L("windows.notifications.statusDenied") : L("windows.notifications.statusNeedPermission")
    }

    func shutdown() { stopped = true; closePlanWindow(); overlay?.close(); overlay = nil; notifications = nil }
    private func priority(_ status: String) -> Int {
        switch status { case "waiting": return 5; case "running": return 4; case "error": return 3; case "completed": return 2; default: return 1 }
    }
    private func savePreferences() {
        guard let url = preferencesURL, !stopped else { return }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(preferences).write(to: url, options: .atomic)
        } catch { message = L("companion.error.saveFailed", ["error": error.localizedDescription]) }
    }
    private func updateOverlay() {
        overlay?.setVisible(preferences.enabled && !stopped)
        overlay?.fit(bubble: bubbleSize, tall: visibleApproval != nil, keeping: .left)
    }

    /// Only while an edge of the bubble is being dragged.
    @Published private(set) var liveBubbleSize: CompanionBubbleSize?
    var bubbleSize: CompanionBubbleSize {
        liveBubbleSize ?? CompanionBubbleSize(width: preferences.bubbleWidth.map { CGFloat($0) }, height: preferences.bubbleHeight.map { CGFloat($0) })
    }
    /// Follows a drag of the bubble's edge. The window grows toward the side
    /// being dragged and keeps the other one; the size is saved once, at the end.
    func resizeBubble(to size: CompanionBubbleSize, keeping side: CompanionPanel.Side, finished: Bool) {
        overlay?.fit(bubble: size, tall: visibleApproval != nil, keeping: side)
        guard finished else { liveBubbleSize = size; return }
        liveBubbleSize = nil
        var next = preferences
        next.bubbleWidth = size.width.map { Double($0) }
        next.bubbleHeight = size.height.map { Double($0) }
        preferences = next
    }
    /// A drag that ended where it began, or whose bubble went away, leaves
    /// the saved size as it was.
    func cancelBubbleResize(keeping side: CompanionPanel.Side) {
        guard liveBubbleSize != nil else { return }
        liveBubbleSize = nil
        overlay?.fit(bubble: bubbleSize, tall: visibleApproval != nil, keeping: side)
    }
}

/// The notification contains no prompt, tool arguments or project path.
@MainActor
final class CompletionNotifications: NSObject, UNUserNotificationCenterDelegate {
    let onFocus: (String) -> Void
    init(onFocus: @escaping (String) -> Void) {
        self.onFocus = onFocus
        super.init()
        UNUserNotificationCenter.current().delegate = self
    }
    func send(sessionID: String, title: String) {
        Task {
            let center = UNUserNotificationCenter.current()
            guard (await center.notificationSettings()).authorizationStatus == .authorized else { return }
            let content = UNMutableNotificationContent()
            content.title = L("companion.notification.title")
            content.body = L("windows.notifications.notificationBodyTemplate", ["title": title])
            content.sound = .default
            content.userInfo = ["sessionID": sessionID]
            try? await center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
        let id = response.notification.request.content.userInfo["sessionID"] as? String
        Task { @MainActor [weak self] in if let id { self?.onFocus(id) }; completionHandler() }
    }
}
