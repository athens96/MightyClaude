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
    var showsSessionIsland: Bool?
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
    @Published var notificationStatus = "확인 중"
    @Published var showsStatus = false
    private weak var store: AppStore?
    private var activities: [String: AgentActivity] = [:]
    private var liveTools: [String: [AgentActivity]] = [:]
    private var submittedInputs: [String: String] = [:]
    private var activeRuns = Set<String>()
    private var lastTouched: [String: Date] = [:]
    private var preferencesURL: URL?
    private var overlay: CompanionPanel?
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
    var selectedPet: CompanionPet? { pets.first { $0.id == preferences.selectedPet } ?? pets.first }

    func configure(store: AppStore) {
        guard self.store == nil else { return }
        self.store = store
        preferencesURL = store.dataDirectory.appendingPathComponent("companion-settings.json")
        if let url = preferencesURL, let data = try? Data(contentsOf: url), let settings = try? JSONDecoder().decode(CompanionPreferences.self, from: data) { preferences = settings }
        reloadPets()
        refresh(store.snapshot)
        notifications = CompletionNotifications { [weak self] id in self?.focus(id) }
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
        agents = snapshot.sessions.filter { $0.kind != "shell" }.map { session in
            let activity = activities[session.id]
            let live = session.status == "running"
            let state = live && activity?.state == "waiting" ? "waiting" : session.status
            let text: String
            if live, let activity, !activity.summary.isEmpty { text = activity.summary }
            else { text = state == "completed" ? "작업을 완료했어요" : state == "error" ? "작업에 문제가 생겼어요" : state == "stopped" ? "작업을 중지했어요" : live ? "작업을 시작하고 있어요" : "새 작업을 기다리고 있어요" }
            return AgentPresence(id: session.id, title: session.title,
                workspace: snapshot.workspaces.first { $0.id == session.workspaceId }?.name ?? "",
                provider: session.provider, status: state, summary: text,
                input: submittedInputs[session.id] ?? session.logs.last(where: { $0.kind == "user" }).map { Self.inputPreview($0.text) }, activity: activity, timing: session.runTiming)
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
                if preferences.notifications && !testMode { notifications?.send(sessionID: session.id, title: session.title) }
            }
        }
    }

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
        if selectedPet == nil { message = "펫 이미지를 불러오지 못했습니다." }
    }

    func importPet() {
        guard let directory = store?.dataDirectory else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.message = "Codex 펫 폴더, pet.json 또는 PNG/WebP 스프라이트를 선택하세요."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let pet = try CompanionPet.install(from: url, into: directory.appendingPathComponent("pets"))
            reloadPets()
            preferences.selectedPet = pet.id
            preferences.enabled = true
            message = "\(pet.name) 펫을 적용했습니다."
        } catch { message = error.localizedDescription }
    }

    func refreshNotificationStatus(request: Bool = false) async {
        guard !testMode else { notificationStatus = "검증 모드"; return }
        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        if request && settings.authorizationStatus == .notDetermined {
            do { _ = try await center.requestAuthorization(options: [.alert, .sound]) }
            catch { message = "알림 권한을 확인하지 못했습니다: \(error.localizedDescription)" }
        }
        let current = await center.notificationSettings()
        notificationStatus = current.authorizationStatus == .authorized ? "허용됨" : current.authorizationStatus == .denied ? "시스템 설정에서 알림을 허용하세요" : "권한 필요"
    }

    func shutdown() { stopped = true; overlay?.close(); overlay = nil; notifications = nil }
    private func priority(_ status: String) -> Int {
        switch status { case "waiting": return 5; case "running": return 4; case "error": return 3; case "completed": return 2; default: return 1 }
    }
    private func savePreferences() {
        guard let url = preferencesURL, !stopped else { return }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(preferences).write(to: url, options: .atomic)
        } catch { message = "펫 설정을 저장하지 못했습니다: \(error.localizedDescription)" }
    }
    private func updateOverlay() { overlay?.setVisible(preferences.enabled && !stopped) }
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
            content.title = "MightyClaude · 작업 완료"
            content.body = "\(title)의 작업이 완료되었습니다."
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
