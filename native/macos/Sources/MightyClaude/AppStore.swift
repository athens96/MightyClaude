import AppKit
import Combine
import Foundation
import UniformTypeIdentifiers
import GhosttyTerminal
import MightyCore

@MainActor
final class AppStore: ObservableObject {
    static let shared = AppStore()
    let companion = AgentCompanion()

    @Published var snapshot = AppSnapshot() { didSet { scheduleSave(); companion.refresh(snapshot) } }
    @Published var runtime: RuntimeInfo?
    @Published var remoteState = RemoteState()
    @Published var isLoaded = false
    @Published var isRefreshingRuntime = false
    @Published var isUpdatingCLIs = false
    @Published var updatingCLI: String?
    @Published var cliUpdateResults: [String: CLIUpdateResult] = [:]
    @Published var cliUpdateFinishedAt: Date?
    let cliUpdater = CLIUpdateService()
    var cliUpdateTask: Task<Void, Never>?
    var automaticCLIUpdateAttempted = false
    @Published var pluginBrowser: ClaudePluginBrowserModel?
    @Published var isManagingPlugins = false
    let claudePlugins = ClaudePluginService()
    let codexPlugins = CodexPluginService()
    @Published var remoteBusy = false
    @Published var search = ""
    @Published var focusSearch = false
    @Published var drafts: [String: String] = [:] {
        didSet {
            for id in Set(oldValue.keys).union(drafts.keys) where oldValue[id] != drafts[id] {
                draftRevisions[id, default: 0] &+= 1
            }
        }
    }
    @Published var attachmentDrafts: [String: [RunAttachment]] = [:]
    /// Requests typed while a pane was busy, in send order. Kept in memory:
    /// a restored session is never running, so nothing would drain it.
    @Published var queuedInputs: [String: [QueuedInput]] = [:]
    /// The live steer chain per session. The token identifies the chain's last
    /// link, so a finished chain clears itself without racing a newer one.
    private var steerTasks: [String: (token: UUID, task: Task<SubmitOutcome, Never>)] = [:]
    /// Phone access (docs/mobile-remote.md): listener, revision tracking, UI status.
    @Published var mobileStatus = MobileHostStatus()
    @Published var mobileBusy = false
    lazy var mobileRemote = MobileRemoteService(dataDirectory: dataDirectory.appendingPathComponent("mobile-remote", isDirectory: true), hostName: Host.current().localizedName ?? "MightyClaude Mac")
    var mobileBridge: MobileRemoteBridge?
    var mobileTracking = MobileRemoteTracking()
    var mobileSubscriptions = Set<AnyCancellable>()
    var mobileRetryTask: Task<Void, Never>?
    /// Settings → 구성 요소 (AppStore+Components.swift).
    @Published var components: [ComponentStatus] = []
    @Published var componentsRefreshing = false
    @Published var componentAction: String?
    @Published var componentMessage: String?
    @Published var componentMessageIsError = false
    /// Slash-command completion (AppStore+SlashCommands.swift).
    @Published var slashCatalogs: [String: SlashCatalogEntry] = [:]
    @Published var statusLines: [String: StatusLineState] = [:]
    @Published var appUpdate = AppUpdateState()
    /// Mighty styles (AppStore+Styles.swift): what was found, what was said
    /// yes to, and the per-pane state the guided panel draws from.
    @Published var styleRegistry = StyleRegistry()
    @Published var styleRejections: [StyleRejection] = []
    /// A trust store that could not be read answers no decision at all (§4.3).
    @Published var styleTrustLocked = false
    @Published var styleTrustPath = ""
    /// Prerequisites per style and workspace path; built-in feature readings
    /// per workspace and feature name.
    @Published var stylePrerequisites: [StylePrerequisiteKey: StylePrerequisiteResult] = [:]
    @Published var styleCapabilityStates: [StyleCapabilityKey: String] = [:]
    @Published var styleAttachments: [StyleCapabilityKey: [StyleAttachmentItem]] = [:]
    @Published var styleCasebooks: [String: StyleCasebook] = [:]
    /// Features that have been read at least once, per workspace.
    @Published var styleCapabilitiesLoaded: Set<StyleCapabilityKey> = []
    /// Composer-side progress through the agent's pending questions, per pane.
    @Published var guidedProgress: [String: QuestionnaireProgress] = [:]
    @Published var guidedAutoAllowing = Set<String>()
    lazy var styleTrust = StyleTrustStore(directory: styleTrustDirectory)
    /// The bytes each registered file was read from, kept so the approval card
    /// and the copy it makes never re-read the disk (§4.4). A rescan replaces
    /// the whole list, so removing a workspace drops its bytes with it.
    var styleDiscovered: [DiscoveredStyleFile] = []
    var scannedStyleWorkspaces: Set<String> = []
    /// Scans are chained so their results publish in order, and so the caller
    /// that must act on a fresh registry can wait for one.
    var styleScanTask: Task<Void, Never>?
    /// Reads already in flight, and the ones asked for again while they were.
    /// Both refreshes are asked for from a phone's long poll, which repeats
    /// until the first answer lands — without these a watched pane would spawn
    /// a process on every poll, and a button pressed meanwhile would be lost.
    var stylePrerequisiteLoading: Set<StylePrerequisiteKey> = []
    var stylePrerequisiteAgain: Set<StylePrerequisiteKey> = []
    var styleCapabilityLoading: Set<StyleCapabilityKey> = []
    var styleCapabilityAgain: Set<StyleCapabilityKey> = []
    var questionnaireCache: [String: UserQuestionnaire] = [:]
    @Published var cliAccounts: [String: CLIAccountStatus] = [:]
    @Published var cliAccountBusy = Set<String>()
    @Published var cliAccountRefreshing = Set<String>()
    /// Shown inside the provider's settings row (the sheet hides `error`).
    @Published var cliAccountMessages: [String: String] = [:]
    /// Providers whose sign-in terminal was opened and not yet confirmed.
    @Published var cliLoginPending = Set<String>()
    var cliLoginSessions: [String: String] = [:]
    var cliLoginTasks: [String: Task<Void, Never>] = [:]
    var pendingTerminalInput: [String: TerminalInput] = [:]
    let cliAccountService = CLIAccountService()
    /// Korean composition broke in a composer; shown until reconnected or dismissed.
    @Published var inputMethodProblem: InputMethodMonitor.Problem?
    var appUpdateServiceStorage: AppUpdateService?
    var slashScansInFlight = Set<String>()
    @Published var attachmentErrors: [String: String] = [:]
    @Published var importingAttachments = Set<String>()
    @Published var attachmentPanelSession: String?
    @Published var localTerminals: [String: LocalTerminalSession] = [:]
    @Published var terminalErrors: [String: String] = [:]
    @Published var terminalHistorySession: RunSession?
    @Published var draggedPane: PaneDragPayload?
    @Published var showRemote = false
    @Published var showSettings = false
    @Published var settingsShowsRemote = false
    @Published var renameTarget: RenameTarget?
    @Published var settingsSession: RunSession?
    @Published var sessionInfoSessionID: String?
    @Published var pendingRemoval: Workspace?
    @Published var error: String?
    @Published var remoteError: String?
    @Published var toolPermissions: [String: [ToolPermissionRequest]] = [:]
    @Published var permissionResponses = Set<String>()
    @Published var permissionErrors: [String: String] = [:]

    let dataDirectory: URL
    let repository: StateRepository
    let providers = ProviderService()
    let pluginDirectory: URL
    private var loading = false
    private(set) var ending = false
    private var canSave = false
    private var saveTask: Task<Void, Never>?
    private var pollTask: Task<Void, Never>?
    private var runtimeTask: Task<Void, Never>?
    @Published private(set) var pendingRuns = Set<String>()
    @Published var modelRefreshRevision = 0
    @Published var claudeModelResetInProgress = false
    var modelPriorCatalogs: [String: ModelCatalog] = [:]
    var modelRefreshSelections: [LocalModelContext: [String: [String]]] = [:]
    lazy var localModels = LocalModelCatalogRefresh(loader: { [weak self] key in
        guard let self else { return ProviderOptions.fallbackRuntime(key.provider) }
        self.modelRefreshSelections[key] = Dictionary(uniqueKeysWithValues: self.snapshot.sessions.filter { $0.workspaceId == key.workspaceID && $0.provider == key.provider }.map { ($0.id, [$0.model, $0.settings.effort]) })
        return await self.providers.providerRuntime(provider: key.provider, workspacePath: key.path, forceRefresh: true)
    }, changed: { [weak self] key, previous, refreshed in
        self?.acceptLocalModels(key, previous: previous, refreshed: refreshed)
    })
    private var startTasks: [String: Task<Void, Never>] = [:]
    private(set) var closingSessions = Set<String>()
    private var draftRevisions: [String: UInt64] = [:]
    var attachmentTasks: [String: Task<Void, Never>] = [:]
    var terminalController: TerminalController?
    var terminalTick: Task<Void, Never>?
    var terminalStarts = Set<String>()
    let paneDragToken = UUID().uuidString
    private let arguments = ProcessInfo.processInfo.arguments
    /// Smoke profiles must not run the user's own status line command.
    var smokeTesting: Bool { arguments.contains { $0.hasPrefix("--") && $0.hasSuffix("smoke-test") } }

    private lazy var runner = ProcessRunner(providerService: providers, pluginDirectory: pluginDirectory) { [weak self] event in
        Task { @MainActor in self?.apply(event) }
    }
    private lazy var remote = RemoteService(repository: repository, providers: providers, pluginDirectory: pluginDirectory, dataDirectory: dataDirectory) { [weak self] event in
        Task { @MainActor in self?.apply(event) }
    }

    init() {
        let arguments = ProcessInfo.processInfo.arguments
        let profile = arguments.firstIndex(of: "--profile").flatMap { arguments.indices.contains($0 + 1) ? arguments[$0 + 1] : nil }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        dataDirectory = profile.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? support.appendingPathComponent("MightyClaude Native", isDirectory: true)
        repository = StateRepository(directory: dataDirectory, legacyStateURL: profile == nil ? StateRepository.defaultLegacyStateURL() : nil)
        let bundled = Bundle.main.resourceURL?.appendingPathComponent("mods/mighty-bridge", isDirectory: true)
        pluginDirectory = bundled.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil } ?? URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("mods/mighty-bridge")
    }

    var activeWorkspace: Workspace? { snapshot.workspaces.first { $0.id == snapshot.activeWorkspaceId } }
    var activeSessions: [RunSession] { snapshot.sessions.filter { $0.workspaceId == snapshot.activeWorkspaceId } }
    var filteredWorkspaces: [Workspace] {
        let needle = search.trimmingCharacters(in: .whitespacesAndNewlines)
        return snapshot.workspaces.filter { needle.isEmpty || $0.name.localizedCaseInsensitiveContains(needle) || $0.path.localizedCaseInsensitiveContains(needle) }
    }
    var hasModal: Bool { showRemote || showSettings || renameTarget != nil || settingsSession != nil || sessionInfoSessionID != nil || pendingRemoval != nil || attachmentPanelSession != nil || terminalHistorySession != nil || pluginBrowser != nil }

    func canEditAttachments(_ id: String) -> Bool {
        !ending && !closingSessions.contains(id) && snapshot.sessions.contains { $0.id == id }
    }

    func load() async {
        guard !loading, !isLoaded, !ending else { return }
        loading = true
        do { snapshot = try await repository.load(); preparePaneLayouts(); canSave = true }
        catch { self.error = "상태를 불러오지 못했습니다. 기존 파일을 보호하기 위해 저장을 중단했습니다. \(error.localizedDescription)" }
        guard !ending, !Task.isCancelled else { loading = false; return }
        isLoaded = true
        // The bundled and user styles are read once at start; a workspace's
        // own are read when it first draws a pane (§3.1).
        rescanStyles()
        companion.configure(store: self)
        InputMethodMonitor.shared.dataDirectory = dataDirectory
        InputMethodMonitor.shared.onProblem = { [weak self] problem in self?.inputMethodProblem = problem }
        InputMethodMonitor.shared.onRecovered = { [weak self] in self?.inputMethodProblem = nil }
        loading = false
        Task {
            await refreshRuntime()
            if !arguments.contains(where: { $0.hasPrefix("--") && $0.hasSuffix("smoke-test") }) { beginAutomaticCLIUpdatesIfNeeded() }
            checkForAppUpdateAutomatically()
        }
        remoteState = await remote.state()
        configureMobileRemote()
        guard !ending, !Task.isCancelled else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(10))
                guard !Task.isCancelled, let self else { break }
                if self.canSave, !self.ending, self.snapshot.sessions.contains(where: { $0.kind != "shell" && $0.status == "running" }) {
                    do { try await self.flush() }
                    catch { if !self.ending { self.error = "실행 시간을 저장하지 못했습니다: \(error.localizedDescription)" } }
                }
                await self.pollRemote()
            }
        }
        if arguments.contains("--plugin-smoke-test") { Task { await runPluginSmokeTest() } }
        else if arguments.contains("--browser-smoke-test") { Task { await runBrowserSmokeTest() } }
        else if arguments.contains("--cli-update-smoke-test") { Task { await runCLIUpdateSmokeTest() } }
        else if arguments.contains("--question-smoke-test") { Task { await runQuestionnaireSmokeTest() } }
        else if arguments.contains("--graph-smoke-test") { Task { await runGraphSmokeTest() } }
        else if arguments.contains("--agent-smoke-test") { Task { await runAgentSmokeTest() } }
        else if arguments.contains("--layout-smoke-test") { Task { await runLayoutSmokeTest() } }
        else if arguments.contains("--usage-reset-smoke-test") { Task { await runUsageResetSmokeTest() } }
        else if terminalSmokeMode { Task { await runTerminalSmokeTest() } }
        else if arguments.contains("--smoke-test") { Task { await runSmokeTest() } }
    }

    var canManageCLIUpdates: Bool { isLoaded && !ending }

    func localCLIIsRunning(_ provider: String) -> Bool {
        snapshot.sessions.contains { session in
            session.kind != "shell" && session.provider == provider &&
            (session.status == "running" || pendingRuns.contains(session.id)) &&
            snapshot.workspaces.contains { $0.id == session.workspaceId && $0.remote == nil }
        }
    }

    func refreshRuntimeAfterCLIUpdate() async {
        if let task = runtimeTask { _ = await task.value }
        guard !ending else { return }
        await providers.invalidateCaches()
        invalidateLocalModels()
        await refreshRuntime()
    }

    func refreshRuntime() async {
        guard !ending else { return }
        if let task = runtimeTask { _ = await task.value; return }
        isRefreshingRuntime = true
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.1.0"
        let task = Task { [weak self] in
            guard let self else { return }
            let value = await self.providers.runtimeInfo(appVersion: version)
            if !self.ending, !Task.isCancelled {
                self.runtime = value
                if let session = self.snapshot.sessions.first(where: { $0.id == self.snapshot.activeSessionId }) { self.refreshModels(for: session.id, force: false) }
            }
            // Complete the whole refresh before waiters invalidate the caches
            // and start a new one; they must not join an old completed probe.
            self.runtimeTask = nil
            self.isRefreshingRuntime = false
        }
        runtimeTask = task
        await task.value
    }

    func providerRuntime(_ provider: String, workspaceId: String) -> ProviderRuntime {
        let workspace = snapshot.workspaces.first { $0.id == workspaceId }
        let info: RuntimeInfo?
        if let reference = workspace?.remote {
            info = remoteState.connections.first { $0.id == reference.connectionId }?.runtime
        } else {
            if let workspace, let value = localModels.value(for: LocalModelContext(workspaceID: workspace.id, path: workspace.path, provider: provider)) { return value }
            info = runtime
        }
        if var runtime = info?.providers?.first(where: { $0.id == provider }) {
            if workspace?.remote == nil, localModels.hasDiscarded(provider) { runtime.modelCatalog = ProviderOptions.fallbackCatalog(provider) }
            return runtime
        }
        var fallback = ProviderOptions.fallbackRuntime(provider)
        if workspace?.remote != nil {
            // New options require an explicit capability advertisement from the host.
            fallback.capabilities.fastMode = false
            fallback.capabilities.webSearch = false
            fallback.capabilities.networkAccess = false
            fallback.capabilities.attachments = false
            fallback.capabilities.permissionModes.removeAll { $0 == "fullAccess" || $0 == "auto" || $0 == "onRequest" }
        }
        return fallback
    }

    func connection(for workspace: Workspace) -> RemoteConnectionInfo? {
        guard let reference = workspace.remote else { return nil }
        return remoteState.connections.first { $0.id == reference.connectionId }
    }

    func runBlockedReason(_ session: RunSession, checkRuntime: Bool = true) -> String? {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return "워크스페이스를 선택하세요." }
        if workspace.remote == nil, session.kind != "shell", updatingCLI == session.provider {
            return "\(ProviderOptions.label(session.provider)) CLI를 업데이트하고 있습니다. 완료 후 전송하세요."
        }
        if workspace.remote == nil, session.kind != "shell", ["claude", "codex"].contains(session.provider), isManagingPlugins {
            return "플러그인을 변경하고 있습니다. 완료 후 전송하세요."
        }
        if workspace.remote != nil, connection(for: workspace)?.status != "connected" { return "원격 컴퓨터에 연결한 후 실행하세요." }
        if session.kind != "shell", session.settings.permissionMode == "onRequest", workspace.remote != nil {
            return "Codex 승인 요청은 이 Mac의 로컬 세션에서 사용할 수 있습니다. 원격 세션에서는 다른 권한을 선택하세요."
        }
        if session.kind != "shell", checkRuntime {
            let provider = providerRuntime(session.provider, workspaceId: session.workspaceId)
            if !provider.available { return provider.detail.isEmpty ? "\(provider.name) CLI를 설치하고 로그인하세요." : provider.detail }
            if session.settings.permissionMode == "auto", !provider.capabilities.permissionModes.contains("auto") { return "이 실행 환경의 Auto mode 지원을 확인하지 못했습니다. CLI·원격 앱을 업데이트하거나 다른 권한을 선택하세요." }
            if session.settings.permissionMode == "onRequest", !provider.capabilities.permissionModes.contains("onRequest") { return "승인 요청을 사용하려면 Codex CLI 0.153.4 이상으로 업데이트하세요." }
        }
        return nil
    }

    func openWorkspace() {
        guard !hasModal else { return }
        let panel = NSOpenPanel()
        panel.title = "프로젝트 폴더 열기"
        panel.prompt = "워크스페이스 열기"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.begin { [weak self] response in
            guard response == .OK, let url = panel.url else { return }
            Task { @MainActor in
                guard let self else { return }
                do {
                    let workspace = try await self.repository.approveWorkspace(Workspace(name: url.lastPathComponent, path: url.path))
                    self.addWorkspace(workspace)
                } catch { self.error = error.localizedDescription }
            }
        }
    }

    func addWorkspace(_ workspace: Workspace) {
        if let index = snapshot.workspaces.firstIndex(where: { $0.id == workspace.id }) { snapshot.workspaces[index] = workspace }
        else { snapshot.workspaces.append(workspace) }
        selectWorkspace(workspace.id)
        if !snapshot.sessions.contains(where: { $0.workspaceId == workspace.id }) { addSession(kind: "claude") }
    }

    func selectWorkspace(_ id: String) {
        guard snapshot.workspaces.contains(where: { $0.id == id }) else { return }
        let remembered = (snapshot.paneLayoutActiveSessionIds?[id]).flatMap { saved in snapshot.sessions.first { $0.id == saved && $0.workspaceId == id }?.id }
        let selected = remembered ?? layoutForWorkspace(id)?.firstSelectedSessionId ?? snapshot.sessions.first { $0.workspaceId == id }?.id
        var next = paneSelectionSnapshot(workspaceId: id, sessionId: selected)
        // Selecting opens this workspace's list without closing the others.
        next.expandedWorkspaceIds = expandedWorkspaceSet().union([id]).sorted()
        let changed = snapshot.activeWorkspaceId != id || snapshot.activeSessionId != selected
        if next != snapshot { snapshot = next }
        if changed, let selected { refreshModels(for: selected) }
    }

    private func expandedWorkspaceSet() -> Set<String> {
        Set(snapshot.expandedWorkspaceIds ?? snapshot.activeWorkspaceId.map { [$0] } ?? [])
    }
    func isWorkspaceExpanded(_ id: String) -> Bool { expandedWorkspaceSet().contains(id) }
    func toggleWorkspaceExpanded(_ id: String) {
        var ids = expandedWorkspaceSet()
        if !ids.insert(id).inserted { ids.remove(id) }
        snapshot.expandedWorkspaceIds = ids.sorted()
    }

    func selectSession(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return }
        let next = paneSelectionSnapshot(workspaceId: session.workspaceId, sessionId: id)
        let changed = snapshot.activeSessionId != id || snapshot.activeWorkspaceId != session.workspaceId
        if next != snapshot { snapshot = next }
        if changed { refreshModels(for: id) }
    }

    @discardableResult
    func addSession(kind: String, provider: String = "claude", targetGroupId: String? = nil, placement: String = "tab", workspaceId: String? = nil) -> String? {
        guard !hasModal, let workspace = workspaceId.flatMap({ id in snapshot.workspaces.first { $0.id == id } }) ?? activeWorkspace else { return nil }
        guard snapshot.sessions.count < 128 else { error = "실행 창은 최대 128개까지 만들 수 있습니다."; return nil }
        let name = kind == "shell" ? (workspace.remote == nil ? "터미널" : "원격 명령") : kind == "browser" ? L("browser.tab.title") : ProviderOptions.label(provider)
        var session = RunSession(workspaceId: workspace.id, title: name, kind: kind, provider: provider)
        if kind == "browser" { session.workspaceProfileKey = workspace.id }
        // Start from the most recently used pane of the same provider.
        if let template = RunSession.template(kind: kind, provider: provider, in: snapshot.sessions) { session.inheritSettings(from: template) }
        reconcilePaneLayout(workspace.id)
        let previousGroup = targetGroupId ?? snapshot.activeSessionId.flatMap { layoutForWorkspace(workspace.id)?.group(containing: $0)?.id }
        let previousMode = paneLayoutMode(workspace.id)
        guard let next = PaneLayouts.inserting(root: layoutForWorkspace(workspace.id), sessionId: session.id, targetGroupId: previousGroup, placement: placement), next.group(containing: session.id) != nil else {
            error = "실행 창을 추가할 그룹이 없거나 분할 한도에 도달했습니다."; return nil
        }
        snapshot.sessions.append(session)
        savePaneLayout(next, workspaceId: workspace.id)
        setPaneLayoutMode(previousMode == "focus" && placement == "tab" ? "focus" : (next.kind == "split" ? "custom" : "tabs"), workspaceId: workspace.id)
        selectSession(session.id)
        return session.id
    }

    func closeSession(_ id: String) {
        guard closingSessions.insert(id).inserted else { return }
        let workspaceId = snapshot.sessions.first(where: { $0.id == id })?.workspaceId
        let previousGroup = workspaceId.flatMap { layoutForWorkspace($0)?.group(containing: id) }
        Task {
            await stop(id)
            snapshot.sessions.removeAll { $0.id == id }
            drafts.removeValue(forKey: id)
            statusLines.removeValue(forKey: id)
            pendingTerminalInput.removeValue(forKey: id); cliLoginEnded(sessionID: id); guidedProgress.removeValue(forKey: id)
            discardAttachments(id)
            queuedInputs.removeValue(forKey: id); steerTasks.removeValue(forKey: id)?.task.cancel()
            draftRevisions.removeValue(forKey: id)
            if snapshot.activeSessionId == id {
                snapshot.activeSessionId = previousGroup?.sessionIds.first(where: { candidate in snapshot.sessions.contains { $0.id == candidate } }) ?? activeSessions.first?.id
            }
            if let workspaceId { reconcilePaneLayout(workspaceId) }
            closingSessions.remove(id)
        }
    }

    func removeWorkspace(_ workspace: Workspace) {
        pendingRemoval = nil
        let ids = snapshot.sessions.filter { $0.workspaceId == workspace.id }.map(\.id)
        closingSessions.formUnion(ids)
        Task {
            for session in snapshot.sessions where session.workspaceId == workspace.id {
                await stop(session.id)
                drafts.removeValue(forKey: session.id)
                statusLines.removeValue(forKey: session.id)
                pendingTerminalInput.removeValue(forKey: session.id); cliLoginEnded(sessionID: session.id); guidedProgress.removeValue(forKey: session.id)
                discardAttachments(session.id)
                queuedInputs.removeValue(forKey: session.id); steerTasks.removeValue(forKey: session.id)?.task.cancel()
                draftRevisions.removeValue(forKey: session.id)
            }
            snapshot.sessions.removeAll { $0.workspaceId == workspace.id }
            snapshot.workspaces.removeAll { $0.id == workspace.id }
            forgetWorkspaceStyles(workspace)
            snapshot.paneLayouts?.removeValue(forKey: workspace.id)
            snapshot.paneLayoutModes?.removeValue(forKey: workspace.id)
            snapshot.paneLayoutActiveSessionIds?.removeValue(forKey: workspace.id)
            if snapshot.activeWorkspaceId == workspace.id {
                if let next = snapshot.workspaces.first?.id { selectWorkspace(next) }
                else { snapshot.activeWorkspaceId = nil; snapshot.activeSessionId = nil }
            }
            if let activeWorkspaceId = snapshot.activeWorkspaceId { reconcilePaneLayout(activeWorkspaceId) }
            closingSessions.subtract(ids)
        }
    }

    func updateSession(_ id: String, _ update: (inout RunSession) -> Void) {
        guard let index = snapshot.sessions.firstIndex(where: { $0.id == id }) else { return }
        update(&snapshot.sessions[index])
    }

    func setAgentViewMode(_ id: String, mode: String) {
        guard ["default", "mighty"].contains(mode) else { return }
        updateSession(id) { session in
            guard session.kind == "claude", MightyGraphSupport.providers.contains(session.provider) else { return }
            session.agentViewMode = mode
        }
    }

    func changeProvider(_ id: String, to provider: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status != "running", !pendingRuns.contains(id), session.provider != provider else { return }
        updateSession(id) {
            $0.provider = ProviderOptions.normalizeProvider(provider)
            $0.model = "default"
            $0.settings = RunSettings()
            $0.resumeId = nil
            $0.sessionUsage = nil
            $0.logs.append(LogEntry(kind: "system", text: "\(ProviderOptions.label(provider))로 전환했습니다. 다음 입력은 새 대화로 시작합니다."))
            $0.logs = TranscriptRetention.trimmed($0.logs)
        }
        refreshModels(for: id, invalidate: true)
    }

    func providerRegisteredModels(_ provider: String) -> [RegisteredModelEntry] {
        guard let config = snapshot.modelDefaults else { return [] }
        return provider == "codex" ? config.codex.registeredModels : config.claude.registeredModels
    }

    func changeModel(_ id: String, to model: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status != "running", !pendingRuns.contains(id) else { return }
        let catalog = providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog
        updateSession(id) {
            $0.model = model
            if !ProviderOptions.effortLevels(provider: $0.provider, model: model, catalog: catalog, registeredModels: providerRegisteredModels($0.provider)).contains($0.settings.effort) { $0.settings.effort = "default" }
        }
    }

    func saveSettings(_ id: String, settings: RunSettings) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status != "running", !pendingRuns.contains(id) else { return }
        if settings.permissionMode != session.settings.permissionMode,
           !providerRuntime(session.provider, workspaceId: session.workspaceId).capabilities.permissionModes.contains(settings.permissionMode) {
            error = "이 실행 환경이 선택한 권한 모드를 지원하는지 확인하지 못했습니다."; return
        }
        updateSession(id) { $0.settings = ProviderOptions.normalizedSettings(provider: $0.provider, settings: settings) }
    }

    func resetConversation(_ id: String) {
        guard !pendingRuns.contains(id), snapshot.sessions.first(where: { $0.id == id })?.status != "running" else { return }
        updateSession(id) {
            guard $0.status != "running" else { return }
            $0.resumeId = nil
            $0.sessionUsage = nil
            $0.logs.append(LogEntry(kind: "system", text: "다음 입력은 새 대화로 시작합니다. 이전 실행 기록은 유지됩니다."))
        }
        refreshModels(for: id, invalidate: true)
    }

    /// `steering`: while a run is busy, hand the text to the running Claude
    /// turn (⌘Enter) instead of queueing it for the next request (Enter).
    func submit(_ id: String, steering: Bool = false) {
        guard !ending, !closingSessions.contains(id), !importingAttachments.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return }
        guard !usesLocalTerminal(session) else { error = "로컬 터미널 안에 명령을 직접 입력하세요."; return }
        let originalDraft = drafts[id] ?? ""
        let input = originalDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        let attachments = attachmentDrafts[id] ?? []
        guard !input.isEmpty || !attachments.isEmpty else { return }
        if let reason = runBlockedReason(session, checkRuntime: workspace.remote != nil) { error = reason; return }
        if session.status == "running" || pendingRuns.contains(id) {
            deferInput(id, session: session, workspace: workspace, item: QueuedInput(text: input, attachments: attachments), steering: steering)
            return
        }
        start(id, session: session, workspace: workspace, input: input, attachments: attachments, restoringDraft: originalDraft)
    }

    /// A local Claude run reads follow-ups on its open stdin frame, so the text
    /// joins the running turn. Everything else waits for the current request.
    func canSteer(_ session: RunSession) -> Bool {
        session.kind == "claude" && session.provider == "claude" && !pendingRuns.contains(session.id)
            && snapshot.workspaces.contains { $0.id == session.workspaceId && $0.remote == nil }
    }

    /// What deferring did. Steering is only known once the runner answers, so
    /// the task carries the outcome for callers (the phone) that must report it.
    enum DeferOutcome {
        case refused, queued
        case steering(Task<SubmitOutcome, Never>)
    }

    @discardableResult
    func deferInput(_ id: String, session: RunSession, workspace: Workspace, item: QueuedInput, steering: Bool = true) -> DeferOutcome {
        guard (queuedInputs[id]?.count ?? 0) < QueuedInput.maximumItems else { error = "대기열에는 최대 \(QueuedInput.maximumItems)개까지 넣을 수 있습니다."; return .refused }
        drafts[id] = ""
        let submittedIds = Set(item.attachments.map(\.id))
        attachmentDrafts[id]?.removeAll { submittedIds.contains($0.id) }
        guard steering, canSteer(session), item.attachments.isEmpty else {
            queuedInputs[id, default: []].append(item)
            return .queued
        }
        // One chain per session keeps rapid follow-ups in send order.
        let previous = steerTasks[id]?.task
        let token = UUID()
        let task = Task { [weak self] in
            _ = await previous?.value
            // Cancelled while waiting its turn: the text never reached anything.
            guard let self, !Task.isCancelled else { return SubmitOutcome.dropped }
            let outcome = await self.steer(id, item: item)
            self.finishSteerChain(id, token: token)
            return outcome
        }
        steerTasks[id] = (token, task)
        return .steering(task)
    }

    /// Drops the chain handle once its last link finished, so a session that
    /// stopped steering does not keep a completed task alive.
    private func finishSteerChain(_ id: String, token: UUID) {
        guard steerTasks[id]?.token == token else { return }
        steerTasks.removeValue(forKey: id)
    }

    /// What the text finally did. The runner refusing it is not the end: the
    /// item goes to the queue, and settling may start it right there, which a
    /// caller reporting to a phone must be able to tell apart from waiting.
    private func steer(_ id: String, item: QueuedInput) async -> SubmitOutcome {
        let accepted = await runner.steer(sessionId: id, text: item.text)
        // Closing meanwhile: the item is deliberately not queued, so say so.
        guard !ending, !closingSessions.contains(id) else { return .dropped }
        guard accepted else {
            // The turn ended (or never opened stdin) meanwhile; run it next.
            queuedInputs[id, default: []].append(item)
            settleQueue(id, status: snapshot.sessions.first { $0.id == id }?.status ?? "idle")
            if queuedInputs[id]?.contains(where: { $0.id == item.id }) == true { return .queued }
            // Gone from the queue: settling either started it now, or threw the
            // whole queue away because the pane cannot run (it logged why).
            if pendingRuns.contains(id) { return .started }
            let status = snapshot.sessions.first { $0.id == id }?.status
            return status == "running" ? .started : .dropped
        }
        companion.recordInput(sessionID: id, text: item.text)
        updateSession(id) {
            $0.logs.append(LogEntry(id: item.id, kind: "user", text: item.text)); $0.logs = TranscriptRetention.trimmed($0.logs)
        }
        return .steered
    }

    func removeQueuedInput(_ id: String, itemId: String) {
        queuedInputs[id]?.removeAll { $0.id == itemId }
        if queuedInputs[id]?.isEmpty == true { queuedInputs.removeValue(forKey: id) }
    }

    /// Runs the first queued item now; used after an error left the queue paused.
    func runNextQueuedInput(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status != "running", !pendingRuns.contains(id) else { return }
        settleQueue(id, status: "completed")
    }

    private func settleQueue(_ id: String, status: String) {
        guard let queue = queuedInputs[id], !queue.isEmpty, !ending, !closingSessions.contains(id) else { return }
        switch status {
        case "completed", "idle":
            guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status != "running", !pendingRuns.contains(id),
                  let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return }
            let next = queue[0]
            if let reason = runBlockedReason(session, checkRuntime: workspace.remote != nil) {
                queuedInputs.removeValue(forKey: id)
                updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: "대기 중인 요청 \(queue.count)개를 실행할 수 없어 취소했습니다. \(reason)")) }
                return
            }
            queuedInputs[id] = queue.count > 1 ? Array(queue.dropFirst()) : nil
            if !start(id, session: session, workspace: workspace, input: next.text, attachments: next.attachments, restoringDraft: nil) {
                // Validation refused it; keep the item so nothing typed is lost.
                queuedInputs[id, default: []].insert(next, at: 0)
                updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: "대기 중인 요청을 실행하지 못해 대기열에 남겨 두었습니다. \(error ?? "")")) }
            }
        case "stopped":
            queuedInputs.removeValue(forKey: id)
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: "실행을 중지해 대기 중인 요청 \(queue.count)개를 취소했습니다.")) }
        case "error":
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: "실행 오류로 대기 중인 요청 \(queue.count)개를 보류합니다. 대기열의 실행 버튼으로 이어갈 수 있습니다.")) }
        default: break
        }
    }

    @discardableResult
    func start(_ id: String, session: RunSession, workspace: Workspace, input: String, attachments: [RunAttachment], restoringDraft: String?) -> Bool {
        if claudeModelResetInProgress, session.provider == "claude", session.kind != "shell", workspace.remote == nil {
            error = "Claude 모델 목록을 다시 불러오는 중입니다. 완료 후 다시 실행하세요."
            return false
        }
        guard !ending, !closingSessions.contains(id), !pendingRuns.contains(id), session.status != "running" else { return false }
        let initialRequest = StartRunRequest(sessionId: id, workspaceId: workspace.id, kind: session.kind, input: input, model: session.model, provider: session.provider, settings: session.settings, resumeId: session.resumeId, attachments: attachments)
        var admissionRequest = initialRequest
        if workspace.remote == nil, session.kind != "shell" { admissionRequest.settings.effort = "default" }
        do { try CoreValidation.validate(admissionRequest) }
        catch { self.error = error.localizedDescription; return false }
        if let reason = runBlockedReason(session, checkRuntime: workspace.remote != nil) { error = reason; return false }
        pendingRuns.insert(id)
        if restoringDraft != nil { drafts[id] = "" }
        let submittedRevision = draftRevisions[id, default: 0]
        // Reserve attachments with the input, so another Enter during metadata
        // preparation cannot submit the same attachment-only request twice.
        let submittedIds = Set(attachments.map(\.id))
        attachmentDrafts[id]?.removeAll { submittedIds.contains($0.id) }
        let attachmentSummary = attachments.map { "첨부: \($0.name) (\(AttachmentImport.sizeLabel($0)))" }.joined(separator: "\n")
        let logText = [input, attachmentSummary].filter { !$0.isEmpty }.joined(separator: "\n\n")
        let inputEntry = LogEntry(kind: "user", text: logText)
        let configuredModel = ModelDefaultsResolution.resolve(
            sessionModel: session.model,
            provider: session.provider,
            permissionMode: session.settings.permissionMode,
            workspaceDefaults: workspace.modelDefaults,
            appDefaults: snapshot.modelDefaults
        )
        updateSession(id) {
            $0.beginGraphRun(input: logText, id: inputEntry.id, configuredModel: configuredModel)
            $0.logs.append(inputEntry); $0.logs = TranscriptRetention.trimmed($0.logs)
        }
        startTasks[id] = Task {
            do {
                try await prepareLocalModels(for: session)
                try Task.checkCancellation()
                guard !ending, !closingSessions.contains(id),
                      let current = snapshot.sessions.first(where: { $0.id == id }),
                      current.provider == session.provider, current.workspaceId == workspace.id,
                      let currentWorkspace = snapshot.workspaces.first(where: { $0.id == workspace.id }),
                      currentWorkspace.path == workspace.path, currentWorkspace.remote == workspace.remote else { throw CancellationError() }
                if let reason = runBlockedReason(current) { throw MightyError(reason) }
                let resolvedModel = ModelDefaultsResolution.resolve(
                    sessionModel: current.model,
                    provider: current.provider,
                    permissionMode: current.settings.permissionMode,
                    workspaceDefaults: currentWorkspace.modelDefaults,
                    appDefaults: snapshot.modelDefaults
                )
                let registered = providerRegisteredModels(current.provider)
                let request = StartRunRequest(sessionId: id, workspaceId: workspace.id, kind: current.kind, input: input, model: resolvedModel, provider: current.provider, settings: current.settings, resumeId: current.resumeId, attachments: attachments, registeredModels: registered)
                try CoreValidation.validate(request)
                if current.kind != "shell" {
                    let provider = providerRuntime(current.provider, workspaceId: workspace.id)
                    try CoreValidation.validateSelection(request, catalog: provider.modelCatalog, registeredModels: registered)
                    try CoreValidation.validateCapabilities(request, capabilities: provider.capabilities)
                }
                companion.recordInput(sessionID: id, text: logText)
                if current.kind != "shell" { companion.beginRun(sessionID: id) }
                updateSession(id) { $0.beginRunTiming(); $0.status = "running" }
                try await flush()
                try Task.checkCancellation()
                if currentWorkspace.remote != nil { try await remote.start(request: request, workspace: currentWorkspace) }
                else { try await runner.start(request: request, workspace: currentWorkspace, allowPermissionPrompts: current.kind == "claude" && (current.provider == "claude" || (current.provider == "codex" && current.settings.permissionMode == "onRequest"))) }
            } catch {
                if let restoringDraft, draftRevisions[id, default: 0] == submittedRevision, (drafts[id] ?? "").isEmpty, canEditAttachments(id) {
                    drafts[id] = restoringDraft
                }
                if !ending, !closingSessions.contains(id), snapshot.sessions.contains(where: { $0.id == id }) {
                    let existing = Set((attachmentDrafts[id] ?? []).map(\.id))
                    attachmentDrafts[id, default: []].insert(contentsOf: attachments.filter { !existing.contains($0.id) }, at: 0)
                }
                if Task.isCancelled || error is CancellationError {
                    apply(RunEvent(sessionId: id, type: "status", status: "stopped"))
                } else {
                    apply(RunEvent(sessionId: id, type: "log", entry: LogEntry(kind: "error", text: error.localizedDescription)))
                    apply(RunEvent(sessionId: id, type: "status", status: "error"))
                }
            }
            pendingRuns.remove(id)
            startTasks.removeValue(forKey: id)
            settleQueue(id, status: snapshot.sessions.first { $0.id == id }?.status ?? "idle")
        }
        return true
    }

    func stop(_ id: String) async {
        if localTerminals[id] != nil {
            disposeTerminal(id)
            updateSession(id) { $0.status = "stopped" }
            return
        }
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status == "running" || pendingRuns.contains(id) else { return }
        let starting = startTasks[id]
        starting?.cancel()
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        if workspace?.remote != nil { await remote.stop(id: id) } else { await runner.stop(id: id) }
        await starting?.value
    }

    func apply(_ event: RunEvent) {
        receivePermissionEvent(event)
        let receivedAt = Date()
        updateSession(event.sessionId) { session in
            session.recordRunTiming(event, at: receivedAt)
            session.recordSessionUsage(event)
            session.recordGraph(event)
            switch event.type {
            case "log":
                guard let entry = event.entry else { return }
                if let index = session.logs.firstIndex(where: { $0.id == entry.id }) { session.logs[index] = entry }
                else { session.logs.append(entry) }
                session.logs = TranscriptRetention.trimmed(session.logs)
            case "status": if let status = event.status { session.status = status }
            case "resume": session.resumeId = event.resumeId
            default: break
            }
        }
        companion.receive(event, snapshot: snapshot, at: receivedAt)
        if event.type == "status", let status = event.status, status != "running" { settleQueue(event.sessionId, status: status) }
    }

    func toggleTheme() { snapshot.theme = snapshot.theme == "light" ? "dark" : "light" }

    func receivePermissionEvent(_ event: RunEvent) {
        guard !ending else { return }
        if event.type == "status", let status = event.status, ["running", "completed", "error", "stopped"].contains(status) {
            toolPermissions.removeValue(forKey: event.sessionId)
            permissionErrors.removeValue(forKey: event.sessionId)
        }
        guard event.type == "permission", let permission = event.permission,
              let session = snapshot.sessions.first(where: { $0.id == event.sessionId }),
              session.kind == "claude", (session.provider == "claude" || (session.provider == "codex" && session.settings.permissionMode == "onRequest")),
              snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote == nil else { return }
        var requests = toolPermissions[event.sessionId] ?? []
        let previousFirst = requests.first.map { permissionResponseKey(sessionId: event.sessionId, request: $0) }
        requests.removeAll { $0.id == permission.id && $0.runId == permission.runId }
        if permission.state == "pending", session.status == "running" { requests.append(permission) }
        toolPermissions[event.sessionId] = requests
        if session.provider == "claude" { autoAllowGuidedTool(permission, session: session) }
        if previousFirst != requests.first.map({ permissionResponseKey(sessionId: event.sessionId, request: $0) }) {
            permissionErrors.removeValue(forKey: event.sessionId)
        }
    }

    func permissionResponseKey(sessionId: String, request: ToolPermissionRequest) -> String {
        "\(sessionId)|\(request.runId)|\(request.id)"
    }

    func answerPermission(sessionId: String, request: ToolPermissionRequest, allow: Bool) async {
        let key = permissionResponseKey(sessionId: sessionId, request: request)
        guard !ending, !closingSessions.contains(sessionId), !permissionResponses.contains(key),
              snapshot.sessions.first(where: { $0.id == sessionId })?.status == "running",
              toolPermissions[sessionId]?.contains(where: { $0.id == request.id && $0.runId == request.runId && $0.state == "pending" }) == true,
              !allow || request.canAllow else { return }
        permissionResponses.insert(key)
        permissionErrors.removeValue(forKey: sessionId)
        defer { permissionResponses.remove(key) }
        do {
            try await runner.respondToPermission(sessionId: sessionId, runId: request.runId, requestId: request.id, allow: allow)
            toolPermissions[sessionId]?.removeAll { $0.id == request.id && $0.runId == request.runId }
        } catch {
            if !ending, toolPermissions[sessionId]?.first.map({ $0.id == request.id && $0.runId == request.runId && $0.state == "pending" }) == true {
                permissionErrors[sessionId] = error.localizedDescription
            }
        }
    }

    func answerQuestionnaire(sessionId: String, request: ToolPermissionRequest, answers: [String: UserQuestionAnswer]) async {
        let key = permissionResponseKey(sessionId: sessionId, request: request)
        guard !ending, !closingSessions.contains(sessionId), !permissionResponses.contains(key),
              request.canAnswerQuestions,
              snapshot.sessions.first(where: { $0.id == sessionId })?.status == "running",
              toolPermissions[sessionId]?.contains(where: { $0.id == request.id && $0.runId == request.runId && $0.state == "pending" }) == true else { return }
        permissionResponses.insert(key)
        permissionErrors.removeValue(forKey: sessionId)
        defer { permissionResponses.remove(key) }
        do {
            try await runner.answerUserQuestions(sessionId: sessionId, runId: request.runId, requestId: request.id, answers: answers)
            toolPermissions[sessionId]?.removeAll { $0.id == request.id && $0.runId == request.runId }
        } catch {
            if !ending, toolPermissions[sessionId]?.first.map({ $0.id == request.id && $0.runId == request.runId && $0.state == "pending" }) == true {
                permissionErrors[sessionId] = error.localizedDescription
            }
        }
    }

    private func scheduleSave() {
        guard isLoaded, canSave, !ending else { return }
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            do { try await Task.sleep(for: .milliseconds(220)) } catch { return }
            guard let self, !Task.isCancelled else { return }
            do { try await self.repository.save(self.snapshot) }
            catch { if !Task.isCancelled { self.error = "상태를 저장하지 못했습니다: \(error.localizedDescription)" } }
        }
    }

    func flush() async throws {
        guard canSave else { throw MightyError("상태를 불러오지 못해 저장할 수 없습니다. 앱을 다시 시작하기 전에 저장 파일을 확인하세요.") }
        saveTask?.cancel()
        await saveTask?.value
        saveTask = nil
        try await repository.save(snapshot)
    }

    func shutdown() async {
        ending = true
        CefBrowserRuntime.shared.shutDown()
        await claudePlugins.shutdown()
        await codexPlugins.shutdown()
        await pluginBrowser?.shutdown()
        pluginBrowser = nil
        cliUpdateTask?.cancel()
        await cliUpdater.shutdown()
        await cliUpdateTask?.value
        toolPermissions.removeAll()
        permissionResponses.removeAll()
        companion.shutdown()
        shutdownTerminals()
        for task in attachmentTasks.values { task.cancel() }
        attachmentTasks.removeAll()
        importingAttachments.removeAll()
        attachmentDrafts.removeAll()
        pollTask?.cancel()
        runtimeTask?.cancel()
        localModels.shutdown()
        saveTask?.cancel()
        await saveTask?.value
        let starting = Array(startTasks.values)
        for task in starting { task.cancel() }
        await providers.shutdown()
        await runner.shutdown()
        await remote.shutdown()
        await shutdownMobileRemote()
        for task in starting { await task.value }
        // Process callbacks enqueue onto the main actor. Let terminal events settle before the final save.
        try? await Task.sleep(for: .milliseconds(60))
        // Cleanup is complete even if a terminal callback was unavailable.
        // Freeze the durable clock while this process is still alive.
        let stoppedAt = Date()
        for index in snapshot.sessions.indices where snapshot.sessions[index].kind != "shell" && snapshot.sessions[index].status == "running" {
            snapshot.sessions[index].runTiming?.finish(at: stoppedAt)
            snapshot.sessions[index].status = "stopped"
        }
        do { if canSave { try await repository.save(snapshot) } }
        catch { NSLog("MightyClaude save failed: %@", error.localizedDescription) }
    }

    private func pollRemote() async {
        guard !remoteBusy, showRemote || (showSettings && settingsShowsRemote) || activeWorkspace?.remote != nil || remoteState.host.enabled else { return }
        if let reference = activeWorkspace?.remote, remoteState.connections.first(where: { $0.id == reference.connectionId })?.status == "connected" {
            do { remoteState = try await remote.refreshRemote(id: reference.connectionId) }
            catch { remoteError = error.localizedDescription; remoteState = await remote.state() }
        } else { remoteState = await remote.state() }
    }

    func reloadRemoteState() async { remoteState = await remote.state() }

    private func remoteAction(_ operation: @escaping () async throws -> RemoteState) {
        guard !remoteBusy else { return }
        remoteBusy = true
        remoteError = nil
        Task {
            do { remoteState = try await operation() }
            catch { remoteError = error.localizedDescription; remoteState = await remote.state() }
            remoteBusy = false
        }
    }

    func startSharing(workspaceIds: [String], port: Int) {
        guard !isManagingPlugins else { remoteError = "플러그인 변경이 끝난 후 공유를 시작하세요."; return }
        guard !isUpdatingCLIs else { remoteError = "CLI 업데이트가 끝난 후 공유를 시작하세요."; return }
        remoteAction { [self] in try await flush(); return try await remote.startSharing(workspaceIds: workspaceIds, port: port) }
    }
    func stopSharing() { remoteAction { [self] in await remote.stopSharing() } }
    func connectRemote(name: String, address: String, token: String) { remoteAction { [self] in try await remote.connectRemote(name: name, address: address, token: token) } }
    func refreshConnection(_ id: String) { remoteAction { [self] in try await remote.refreshRemote(id: id) } }
    func disconnectRemote(_ id: String) { remoteAction { [self] in await remote.disconnectRemote(id: id) } }
    func importRemoteWorkspace(connectionId: String, workspaceId: String) {
        guard !remoteBusy else { return }
        remoteBusy = true
        Task {
            do {
                let workspace = try await remote.importWorkspace(connectionId: connectionId, workspaceId: workspaceId)
                showRemote = false
                settingsShowsRemote = false
                showSettings = false
                addWorkspace(workspace)
                try await flush()
            } catch { remoteError = error.localizedDescription }
            remoteBusy = false
        }
    }

    private func runSmokeTest() async {
        guard arguments.contains("--profile") else { error = "스모크 테스트에는 --profile 임시 폴더가 필요합니다."; return }
        var result: [String: Any] = ["native": true, "passed": false]
        do {
            await refreshRuntime()
            while isRefreshingRuntime { try await Task.sleep(for: .milliseconds(25)) }
            result["providers"] = (runtime?.providers ?? []).map { provider -> [String: Any] in
                ["id": provider.id, "available": provider.available, "modelCatalogSource": provider.modelCatalog.source, "modelCount": provider.modelCatalog.models.count, "version": provider.version ?? ""]
            }
            let folder = dataDirectory.appendingPathComponent("Smoke Workspace", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let workspace = try await repository.approveWorkspace(Workspace(name: "Native Smoke", path: folder.path))
            addWorkspace(workspace)
            guard let claudeId = snapshot.activeSessionId else { throw MightyError("Claude 실행 창을 만들지 못했습니다.") }
            changeModel(claudeId, to: "sonnet")
            saveSettings(claudeId, settings: RunSettings(effort: "high", permissionMode: "plan", maxTurns: 12, maxBudgetUsd: 1.5))
            addSession(kind: "claude", provider: "codex")
            guard let codexId = snapshot.activeSessionId else { throw MightyError("Codex 실행 창을 만들지 못했습니다.") }
            let codexCatalog = providerRuntime("codex", workspaceId: workspace.id).modelCatalog
            if let known = codexCatalog.models.first(where: { $0.value != "default" && ProviderOptions.effortLevels(provider: "codex", model: $0.value, catalog: codexCatalog, registeredModels: providerRegisteredModels("codex")).contains("high") }) {
                changeModel(codexId, to: known.value)
                saveSettings(codexId, settings: RunSettings(effort: "high", permissionMode: "acceptEdits"))
                result["codexHighModel"] = known.value
            } else { result["codexHighModel"] = "skipped: CLI capability metadata unavailable" }
            var codexSettings = snapshot.sessions.first { $0.id == codexId }!.settings
            codexSettings.permissionMode = "acceptEdits"
            codexSettings.fastMode = true
            codexSettings.webSearch = "cached"
            codexSettings.networkAccess = true
            var unrestricted = codexSettings
            unrestricted.permissionMode = "fullAccess"
            saveSettings(codexId, settings: unrestricted)
            try await flush()
            let unrestrictedRestore = try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load().sessions.first { $0.id == codexId }
            let fullAccessRoundTrip = unrestrictedRestore?.settings.permissionMode == "fullAccess" && unrestrictedRestore?.settings.networkAccess == false
            result["fullAccessRoundTrip"] = fullAccessRoundTrip
            guard fullAccessRoundTrip else { throw MightyError("전체 접근 권한의 저장·복원 또는 네트워크 정규화를 확인하지 못했습니다.") }
            saveSettings(codexId, settings: codexSettings)
            addSession(kind: "claude", provider: "gemini")
            addSession(kind: "shell")
            guard let sessionId = snapshot.activeSessionId else { throw MightyError("스모크 실행 창을 만들지 못했습니다.") }
            drafts[sessionId] = "printf 'MIGHTY_NATIVE_SMOKE_OK\\n'"
            submit(sessionId)
            try await waitForSmoke(timeout: 20) { !self.pendingRuns.contains(sessionId) && self.snapshot.sessions.first(where: { $0.id == sessionId })?.status != "running" }
            let completed = snapshot.sessions.first { $0.id == sessionId }
            let marker = completed?.logs.contains { $0.kind == "output" && $0.text.contains("MIGHTY_NATIVE_SMOKE_OK") } == true
            let completedCorrectly = completed?.status == "completed" && marker
            result["shellCompleted"] = completedCorrectly
            result["output"] = completed?.logs.map(\.text).joined(separator: "\n") ?? ""
            drafts[sessionId] = "/bin/sleep 30"
            submit(sessionId)
            try await waitForSmoke(timeout: 10) { !self.pendingRuns.contains(sessionId) }
            try await Task.sleep(for: .milliseconds(200))
            try await verifySmokeComposer(sessionId: sessionId, forceUnavailable: false) { result["runningComposer"] = $0 }
            await stop(sessionId)
            try await waitForSmoke(timeout: 5) { self.snapshot.sessions.first(where: { $0.id == sessionId })?.status == "stopped" }
            result["shellStopped"] = true
            try await verifySmokeComposer(sessionId: sessionId, forceUnavailable: false, expectedSendEnabled: true) { result["readyComposer"] = $0 }
            snapshot.activeSessionId = claudeId
            try await flush()
            let restored = try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load()
            let settingsRestored = snapshot.sessions.allSatisfy { expected in
                restored.sessions.contains { $0.id == expected.id && $0.provider == expected.provider && $0.model == expected.model && $0.settings == expected.settings && $0.status == expected.status }
            }
            result["settingsRestored"] = settingsRestored
            let restoredCodex = restored.sessions.first { $0.id == codexId }
            let advancedRestored = restoredCodex?.settings == codexSettings
            result["codexAdvancedSettingsRestored"] = advancedRestored
            guard advancedRestored else { throw MightyError("Codex Fast·웹 검색·Shell 네트워크 설정을 복원하지 못했습니다.") }
            result["workspaceCount"] = restored.workspaces.count
            result["sessionCount"] = restored.sessions.count
            try await verifySmokeComposer(sessionId: claudeId, forceUnavailable: true) { result["blockedComposer"] = $0 }
            try await verifySmokeAttachments(sessionId: claudeId, shellId: sessionId) { result["attachments"] = $0 }
            // New panes join tabs; this regression intentionally inspects all
            // four providers and the Codex popover at the same time.
            setPaneLayoutPreset("grid")
            try await Task.sleep(for: .milliseconds(350))
            let window = NSApp.windows.first { $0.isVisible && $0.contentView != nil && $0.level == .normal }
            result["windowVisible"] = window != nil
            if let window {
                result["screenshot"] = try captureSmokeWindow(window, filename: "native-window.png").path
                let originalFrame = window.frame
                window.setContentSize(NSSize(width: 1060, height: 780))
                try await Task.sleep(for: .milliseconds(250))
                result["narrowScreenshot"] = try captureSmokeWindow(window, filename: "native-window-narrow.png").path
                window.setFrame(originalFrame, display: true)
                try await Task.sleep(for: .milliseconds(250))
                settingsSession = snapshot.sessions.first { $0.id == codexId }
                try await Task.sleep(for: .milliseconds(300))
                if let popover = NSApp.windows.first(where: { $0 !== window && $0.isVisible && $0.contentView != nil && $0.frame.width >= 350 && $0.frame.width < 600 }) {
                    result["settingsScreenshot"] = try captureSmokeWindow(popover, filename: "codex-settings.png").path
                    result["settingsPopoverVisible"] = true
                } else { throw MightyError("Codex 추가 설정 팝오버가 표시되지 않았습니다.") }
                settingsSession = nil
            }
            // The read-only 리셋권 rows, rendered from an injected service with a
            // fixture clock and a fake transport (GET only). A second instance is
            // never launched on a developer Mac, so this run is observed from the
            // macOS CI smoke artifact, exactly as Windows records it under the
            // same usageReset key.
            let usageReset = await AccountUsageStatusController.runUsageResetSmoke()
            result["usageReset"] = usageReset
            result["passed"] = completedCorrectly && settingsRestored && window != nil
                && (usageReset["passed"] as? Bool == true)
            result["status"] = snapshot.sessions.first { $0.id == sessionId }?.status ?? "missing"
        } catch {
            result["error"] = error.localizedDescription
            self.error = "스모크 테스트 실패: \(error.localizedDescription)"
        }
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
            try data.write(to: dataDirectory.appendingPathComponent("smoke-result.json"), options: .atomic)
        } catch { self.error = "스모크 결과 저장 실패: \(error.localizedDescription)" }
        if arguments.contains("--smoke-exit") { NSApp.terminate(nil) }
    }

    func waitForSmoke(timeout: TimeInterval, predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !predicate() {
            if Date() > deadline { throw MightyError("스모크 테스트의 실행 상태 확인 시간이 초과되었습니다.") }
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    func captureSmokeWindow(_ window: NSWindow, filename: String) throws -> URL {
        guard let view = window.contentView?.superview ?? window.contentView else { throw MightyError("네이티브 창 콘텐츠를 찾지 못했습니다.") }
        view.layoutSubtreeIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw MightyError("네이티브 창 비트맵을 만들지 못했습니다.") }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw MightyError("네이티브 창 PNG를 만들지 못했습니다.") }
        let screenshot = dataDirectory.appendingPathComponent(filename)
        try png.write(to: screenshot, options: .atomic)
        return screenshot
    }

    // Exercise the native editor in an isolated smoke profile without submitting an AI request.
    private func verifySmokeComposer(sessionId: String, forceUnavailable: Bool, expectedSendEnabled: Bool = false, report: ([String: Any]) -> Void) async throws {
        guard let session = snapshot.sessions.first(where: { $0.id == sessionId }) else { throw MightyError("입력을 검증할 실행 창이 없습니다.") }
        let previousLayoutModes = snapshot.paneLayoutModes
        let previousLayoutSelections = snapshot.paneLayoutActiveSessionIds
        let previousActive = snapshot.activeSessionId
        let previousDraft = drafts[sessionId]
        let previousRuntime = runtime
        let diagnosticLog = LogEntry(kind: "system", text: "입력 검증 중입니다. AI 요청은 전송하지 않습니다.")
        defer {
            drafts[sessionId] = previousDraft
            runtime = previousRuntime
            if forceUnavailable { updateSession(sessionId) { $0.logs.removeAll { $0.id == diagnosticLog.id } } }
            snapshot.activeSessionId = previousActive
            snapshot.paneLayoutModes = previousLayoutModes
            snapshot.paneLayoutActiveSessionIds = previousLayoutSelections
        }
        if forceUnavailable {
            guard var info = runtime, let index = info.providers?.firstIndex(where: { $0.id == session.provider }) else { throw MightyError("입력 검증용 CLI 상태를 찾지 못했습니다.") }
            info.providers?[index].available = false
            info.providers?[index].detail = "입력 검증을 위해 실행 연결을 잠시 사용할 수 없는 상태로 설정했습니다."
            runtime = info
            updateSession(sessionId) { $0.logs.append(diagnosticLog) }
        }
        drafts[sessionId] = ""
        selectSession(sessionId)
        setPaneFocus(true)
        try await Task.sleep(for: .milliseconds(200))
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil && $0.level == .normal }), let view = window.contentView else { throw MightyError("입력을 검증할 창이 표시되지 않았습니다.") }
        view.layoutSubtreeIfNeeded()
        func textEditors(_ view: NSView) -> [NSTextView] {
            let editor = (view as? NSTextView).map { [$0] } ?? []
            return editor + view.subviews.flatMap(textEditors)
        }
        let editors = textEditors(view).filter { $0.isEditable && !$0.isFieldEditor && !$0.isHiddenOrHasHiddenAncestor }
        var diagnostic: [String: Any] = ["editorFound": editors.count == 1]
        report(diagnostic)
        guard editors.count == 1, let editor = editors.first, window.makeFirstResponder(editor) else { throw MightyError("실제 텍스트 편집기에 포커스를 주지 못했습니다.") }
        diagnostic["editorEditable"] = editor.isEditable
        diagnostic["editorFocused"] = window.firstResponder === editor
        report(diagnostic)
        func viewportHeight() -> CGFloat { editor.enclosingScrollView?.contentView.bounds.height ?? 0 }
        func replaceDraft(_ text: String) async throws {
            editor.insertText(text, replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
            try await waitForSmoke(timeout: 3) { self.drafts[sessionId] == text }
            try await Task.sleep(for: .milliseconds(100))
            view.layoutSubtreeIfNeeded()
        }
        let emptyHeight = viewportHeight()
        diagnostic["emptyViewportHeight"] = emptyHeight
        report(diagnostic)
        guard emptyHeight > 0, emptyHeight < 44 else { throw MightyError("빈 입력창이 한 줄 높이가 아닙니다.") }
        try await replaceDraft("첫째 줄\n둘째 줄\n셋째 줄")
        let multilineHeight = viewportHeight()
        diagnostic["multilineViewportHeight"] = multilineHeight
        diagnostic["newlineGrowth"] = multilineHeight > emptyHeight + 10
        report(diagnostic)
        guard multilineHeight > emptyHeight + 10 else { throw MightyError("여러 줄 입력 시 입력창 높이가 늘어나지 않았습니다.") }
        try await replaceDraft("첫째 줄\n둘째 줄\n셋째 줄\n")
        let trailingHeight = viewportHeight()
        diagnostic["trailingNewlineViewportHeight"] = trailingHeight
        diagnostic["trailingNewlineGrowth"] = trailingHeight > multilineHeight + 5
        report(diagnostic)
        guard trailingHeight > multilineHeight + 5 else { throw MightyError("마지막 줄바꿈을 입력창 높이에 반영하지 못했습니다.") }
        let width = editor.enclosingScrollView?.contentView.bounds.width ?? 600
        try await replaceDraft(String(repeating: "가", count: max(60, Int(width / 3))))
        let wrappedHeight = viewportHeight()
        diagnostic["wrappedViewportHeight"] = wrappedHeight
        diagnostic["wrapGrowth"] = wrappedHeight > emptyHeight + 10
        report(diagnostic)
        guard wrappedHeight > emptyHeight + 10 else { throw MightyError("자동 줄바꿈을 입력창 높이에 반영하지 못했습니다.") }
        try await replaceDraft(Array(repeating: "여러 줄 입력", count: 20).joined(separator: "\n"))
        let cappedHeight = viewportHeight()
        let lineHeight = NSLayoutManager().defaultLineHeight(for: editor.font ?? NSFont.systemFont(ofSize: 13))
        diagnostic["cappedViewportHeight"] = cappedHeight
        diagnostic["heightCapped"] = cappedHeight > multilineHeight && cappedHeight <= emptyHeight + lineHeight * 5 + 4
        report(diagnostic)
        guard cappedHeight > multilineHeight, cappedHeight <= emptyHeight + lineHeight * 5 + 4 else { throw MightyError("긴 입력이 여섯 줄 높이에서 제한되지 않았습니다.") }
        if expectedSendEnabled { diagnostic["expandedScreenshot"] = try captureSmokeWindow(window, filename: "composer-expanded.png").path }
        try await replaceDraft("")
        diagnostic["shrunkViewportHeight"] = viewportHeight()
        diagnostic["shrinksAfterDeletion"] = abs(viewportHeight() - emptyHeight) <= 1
        diagnostic["sameNativeEditor"] = textEditors(view).contains { $0 === editor }
        report(diagnostic)
        guard abs(viewportHeight() - emptyHeight) <= 1, textEditors(view).contains(where: { $0 === editor }) else { throw MightyError("삭제 후 입력창 축소 또는 편집기 유지를 확인하지 못했습니다.") }
        let diagnosticText = "입력 확인 · 다음 요청 초안"
        editor.insertText(diagnosticText, replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
        try await waitForSmoke(timeout: 3) { self.drafts[sessionId] == diagnosticText }
        diagnostic["bindingUpdated"] = drafts[sessionId] == diagnosticText
        report(diagnostic)
        try await Task.sleep(for: .milliseconds(100))
        var tree: [[String: Any]] = []
        let running = session.status == "running"
        // A busy pane keeps accepting text: it steers a local Claude turn or
        // waits in the queue. With a draft present, stop and send coexist.
        let sendsWhileRunning = running && !forceUnavailable
        let sendExpected = expectedSendEnabled || sendsWhileRunning
        let actionIdentifier = (running ? "composer-stop-" : "send-") + sessionId
        let absentIdentifier = (running ? "send-" : "composer-stop-") + sessionId
        func secondaryMatches(_ value: Bool?) -> Bool { sendsWhileRunning ? value == true : value == nil }
        var action: Bool?
        var duplicate: Bool?
        let actionDeadline = Date().addingTimeInterval(3)
        repeat {
            // SwiftUI publishes virtual accessibility children after the layout
            // transaction. Wait for the actual button, not a fixed frame delay.
            view.layoutSubtreeIfNeeded()
            tree = []
            action = smokeAccessibilityElement(window, identifier: actionIdentifier, tree: &tree)
            duplicate = smokeAccessibilityElement(window, identifier: absentIdentifier, tree: &tree)
            if action == (running || expectedSendEnabled), secondaryMatches(duplicate) { break }
            try await Task.sleep(for: .milliseconds(50))
        } while Date() < actionDeadline
        diagnostic["primaryAction"] = running ? "stop" : "send"
        diagnostic["primaryActionLocated"] = action != nil
        diagnostic["singlePrimaryAction"] = secondaryMatches(duplicate)
        diagnostic["sendWhileRunning"] = sendsWhileRunning
        report(diagnostic)
        guard let action, secondaryMatches(duplicate) else {
            let data = try JSONSerialization.data(withJSONObject: tree, options: [.prettyPrinted, .sortedKeys])
            try data.write(to: dataDirectory.appendingPathComponent("composer-accessibility.json"), options: .atomic)
            throw MightyError("단일 실행·중지 버튼의 접근성 상태를 확인하지 못했습니다.")
        }
        diagnostic["primaryActionEnabled"] = action
        report(diagnostic)
        guard action == (running || expectedSendEnabled) else { throw MightyError("실행·중지 버튼이 현재 작업 상태와 일치하지 않습니다.") }
        if forceUnavailable {
            tree = []
            let noticeVisible = smokeAccessibilityElement(window, identifier: "run-blocked-\(sessionId)", tree: &tree) != nil
            diagnostic["blockedNoticeVisible"] = noticeVisible
            report(diagnostic)
            guard noticeVisible else { throw MightyError("실행 기록이 있는 창에서 연결 안내를 확인하지 못했습니다.") }
        }

        // Exercise commands already delivered by the native input context.
        // A temporary submit callback prevents the diagnostic from starting a run.
        guard let composer = editor as? ComposerTextView, composer.onSubmit != nil,
              composer.canSubmit() == sendExpected else { throw MightyError("입력창의 키 전송 조건을 확인하지 못했습니다.") }
        func returnEvent(_ modifiers: NSEvent.ModifierFlags = [], keyCode: UInt16 = 36, repeating: Bool = false) throws -> NSEvent {
            guard let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers, timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: repeating, keyCode: keyCode) else { throw MightyError("입력 키 검증 이벤트를 만들지 못했습니다.") }
            return event
        }
        func route(_ event: NSEvent) -> (remaining: NSEvent?, callbacks: Int) {
            let original = composer.onSubmit
            var callbacks = 0, consumed = false
            composer.onSubmit = { _ in callbacks += 1 }
            defer { composer.onSubmit = original }
            composer.performNativeKeyEvent(event) {
                consumed = composer.handleNativeCommand(NSSelectorFromString("insertNewline:"))
            }
            return (consumed ? nil : event, callbacks)
        }
        let enter = route(try returnEvent())
        let commandEnter = route(try returnEvent(.command))
        let keypadEnter = route(try returnEvent(.numericPad, keyCode: 76))
        let repeatedEnter = route(try returnEvent([], repeating: true))
        let expectedCallbacks = sendExpected ? 1 : 0
        diagnostic["keyVerification"] = "synthetic post-IME delegate commands and native NSTextView.keyDown"
        diagnostic["enterCallbackCount"] = enter.callbacks
        diagnostic["commandEnterCallbackCount"] = commandEnter.callbacks
        diagnostic["keypadEnterCallbackCount"] = keypadEnter.callbacks
        diagnostic["repeatedEnterCallbackCount"] = repeatedEnter.callbacks
        report(diagnostic)
        guard enter.remaining == nil, commandEnter.remaining == nil, keypadEnter.remaining == nil,
              enter.callbacks == expectedCallbacks, commandEnter.callbacks == expectedCallbacks,
              keypadEnter.callbacks == expectedCallbacks, repeatedEnter.callbacks == 0,
              editor.string == diagnosticText else { throw MightyError("Enter 전송 또는 비활성·반복 키 차단을 확인하지 못했습니다.") }

        let shifted = route(try returnEvent(.shift))
        guard shifted.callbacks == 0, let newlineEvent = shifted.remaining else { throw MightyError("Shift+Enter가 네이티브 줄바꿈으로 전달되지 않았습니다.") }
        editor.setSelectedRange(NSRange(location: (editor.string as NSString).length, length: 0))
        editor.keyDown(with: newlineEvent)
        try await waitForSmoke(timeout: 3) { self.drafts[sessionId] == diagnosticText + "\n" }
        diagnostic["shiftEnterNewline"] = true
        report(diagnostic)

        editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
        guard editor.hasMarkedText() else { throw MightyError("IME 조합 중 상태를 설정하지 못했습니다.") }
        let markedEnter = route(try returnEvent())
        let markedCommandEnter = route(try returnEvent(.command))
        let compositionPreserved = editor.hasMarkedText()
        editor.unmarkText()
        diagnostic["markedEnterCallbackCount"] = markedEnter.callbacks
        diagnostic["markedCommandEnterCallbackCount"] = markedCommandEnter.callbacks
        diagnostic["markedTextPassedThrough"] = markedEnter.remaining != nil && markedCommandEnter.remaining != nil && compositionPreserved
        report(diagnostic)
        guard markedEnter.callbacks == 0, markedCommandEnter.callbacks == 0,
              markedEnter.remaining != nil, markedCommandEnter.remaining != nil, compositionPreserved else { throw MightyError("IME 조합 중 Enter 전송 차단을 확인하지 못했습니다.") }

        try await replaceDraft(" \n ")
        let emptyEnter = route(try returnEvent())
        diagnostic["emptyEnterCallbackCount"] = emptyEnter.callbacks
        report(diagnostic)
        guard !composer.canSubmit(), emptyEnter.remaining == nil, emptyEnter.callbacks == 0 else { throw MightyError("빈 입력의 Enter 전송 차단을 확인하지 못했습니다.") }
        try await replaceDraft(diagnosticText)
    }

    private func verifySmokeAttachments(sessionId: String, shellId: String, report: ([String: Any]) -> Void) async throws {
        let previousLayoutModes = snapshot.paneLayoutModes
        let previousLayoutSelections = snapshot.paneLayoutActiveSessionIds
        let previousActive = snapshot.activeSessionId
        let previousDraft = drafts[sessionId]
        let previousAttachments = attachmentDrafts[sessionId]
        let clipboard = NSPasteboard(name: NSPasteboard.Name("dev.mightyclaude.smoke.\(UUID().uuidString)"))
        defer {
            clipboard.clearContents()
            discardAttachments(sessionId)
            attachmentDrafts[sessionId] = previousAttachments
            attachmentErrors.removeValue(forKey: shellId)
            drafts[sessionId] = previousDraft
            snapshot.activeSessionId = previousActive
            snapshot.paneLayoutModes = previousLayoutModes
            snapshot.paneLayoutActiveSessionIds = previousLayoutSelections
        }
        var diagnostic: [String: Any] = ["systemClipboardTouched": false, "aiRequestSent": false]
        drafts[sessionId] = ""
        attachmentDrafts[sessionId] = []
        selectSession(sessionId)
        setPaneFocus(true)
        try await Task.sleep(for: .milliseconds(200))
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil && $0.level == .normal }), let view = window.contentView else { throw MightyError("첨부 입력창을 찾지 못했습니다.") }
        func editors(_ root: NSView) -> [NSTextView] {
            let own = (root as? NSTextView).map { [$0] } ?? []
            return own + root.subviews.flatMap(editors)
        }
        guard let editor = editors(view).first(where: { $0.isEditable && !$0.isFieldEditor && !$0.isHiddenOrHasHiddenAncestor }), window.makeFirstResponder(editor) else { throw MightyError("첨부 입력창의 네이티브 편집기에 포커스를 주지 못했습니다.") }
        func sendEnabled() -> Bool? {
            var tree: [[String: Any]] = []
            return smokeAccessibilityElement(window, identifier: "send-\(sessionId)", tree: &tree)
        }
        let folder = dataDirectory.appendingPathComponent("Attachment Fixtures", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 32, pixelsHigh: 32, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { throw MightyError("첨부 이미지 검증 자료를 만들지 못했습니다.") }
        let orange = NSColor(deviceRed: 0.96, green: 0.49, blue: 0.16, alpha: 1)
        let blue = NSColor(deviceRed: 0.16, green: 0.49, blue: 0.96, alpha: 1)
        for y in 0..<32 { for x in 0..<32 { bitmap.setColor((x / 8 + y / 8).isMultiple(of: 2) ? orange : blue, atX: x, y: y) } }
        guard bitmap.colorAt(x: 0, y: 0)?.alphaComponent == 1 else { throw MightyError("첨부 검증 이미지의 픽셀이 투명합니다.") }
        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw MightyError("첨부 PNG 검증 자료를 만들지 못했습니다.") }
        let imageURL = folder.appendingPathComponent("미리보기.png")
        let noteURL = folder.appendingPathComponent("작업 메모.txt")
        try png.write(to: imageURL)
        try Data("ATTACHMENT_FIXTURE_PRIVATE_DATA".utf8).write(to: noteURL)
        importAttachments(sessionId, urls: [imageURL, noteURL])
        diagnostic["importBusy"] = importingAttachments.contains(sessionId)
        report(diagnostic)
        try await waitForSmoke(timeout: 5) { !self.importingAttachments.contains(sessionId) }
        guard let attached = attachmentDrafts[sessionId], attached.count == 2 else { throw MightyError(attachmentErrors[sessionId] ?? "파일 첨부를 읽지 못했습니다.") }
        guard let imageAttachment = attached.first(where: { $0.mediaType == "image/png" }) else { throw MightyError("PNG 첨부의 형식이 일치하지 않습니다.") }
        diagnostic["fileURLImport"] = true
        diagnostic["thumbnailAvailable"] = AttachmentImport.thumbnail(imageAttachment) != nil
        try await Task.sleep(for: .milliseconds(150))
        diagnostic["attachmentOnlySendEnabled"] = sendEnabled() == true
        diagnostic["sameNativeEditor"] = editors(view).contains { $0 === editor }
        diagnostic["emptyViewportHeight"] = editor.enclosingScrollView?.contentView.bounds.height ?? 0
        report(diagnostic)
        guard AttachmentImport.thumbnail(imageAttachment) != nil, sendEnabled() == true, editors(view).contains(where: { $0 === editor }) else { throw MightyError("첨부 썸네일, 첨부 전용 보내기 또는 편집기 유지를 확인하지 못했습니다.") }
        diagnostic["screenshot"] = try captureSmokeWindow(window, filename: "composer-attachments.png").path
        try await flush()
        let stateText = try String(contentsOf: dataDirectory.appendingPathComponent("workspace-state.json"), encoding: .utf8)
        diagnostic["attachmentBytesNotPersisted"] = !stateText.contains(imageAttachment.dataBase64) && !stateText.contains("ATTACHMENT_FIXTURE_PRIVATE_DATA")
        guard diagnostic["attachmentBytesNotPersisted"] as? Bool == true else { throw MightyError("첨부 내용이 앱 상태에 저장되었습니다.") }
        importAttachments(sessionId, urls: Array(repeating: imageURL, count: AttachmentSupport.maximumCount))
        diagnostic["countLimitPreservesDraft"] = attachmentErrors[sessionId] != nil && attachmentDrafts[sessionId] == attached
        report(diagnostic)
        guard diagnostic["countLimitPreservesDraft"] as? Bool == true else { throw MightyError("첨부 개수 제한에서 기존 첨부가 유지되지 않았습니다.") }
        removeAttachment(sessionId, attachmentId: imageAttachment.id)
        diagnostic["remove"] = attachmentDrafts[sessionId]?.count == 1
        discardAttachments(sessionId)
        try await Task.sleep(for: .milliseconds(100))
        diagnostic["clear"] = attachmentDrafts[sessionId] == nil && sendEnabled() == false
        guard diagnostic["remove"] as? Bool == true, diagnostic["clear"] as? Bool == true else { throw MightyError("첨부 제거 또는 보내기 상태 초기화를 확인하지 못했습니다.") }

        // A private pasteboard exercises the same explicit attachment-paste menu
        // handler without reading or replacing the user's system clipboard.
        clipboard.setData(png, forType: .png)
        pasteAttachments(sessionId, from: clipboard)
        try await waitForSmoke(timeout: 5) { !self.importingAttachments.contains(sessionId) }
        diagnostic["privateImagePaste"] = attachmentDrafts[sessionId]?.first?.mediaType == "image/png"
        report(diagnostic)
        guard diagnostic["privateImagePaste"] as? Bool == true else { throw MightyError(attachmentErrors[sessionId] ?? "전용 클립보드 이미지 첨부를 확인하지 못했습니다.") }
        discardAttachments(sessionId)
        let provider = NSItemProvider(item: imageURL as NSURL, typeIdentifier: UTType.fileURL.identifier)
        importAttachments(sessionId, providers: [provider])
        try await waitForSmoke(timeout: 5) { !self.importingAttachments.contains(sessionId) }
        diagnostic["fileDropProvider"] = attachmentDrafts[sessionId]?.first?.name == imageURL.lastPathComponent
        guard diagnostic["fileDropProvider"] as? Bool == true else { throw MightyError(attachmentErrors[sessionId] ?? "파일 드롭 데이터 처리를 확인하지 못했습니다.") }
        discardAttachments(sessionId)
        importAttachments(shellId, urls: [imageURL])
        diagnostic["shellRejected"] = attachmentErrors[shellId] != nil && attachmentDrafts[shellId] == nil && !importingAttachments.contains(shellId)
        report(diagnostic)
        guard diagnostic["shellRejected"] as? Bool == true else { throw MightyError("명령 창의 첨부 거부를 확인하지 못했습니다.") }
        clipboard.clearContents()
        let text = "일반 텍스트 붙여넣기\n한글 초안 유지"
        clipboard.setString(text, forType: .string)
        window.makeFirstResponder(editor)
        editor.setSelectedRange(NSRange(location: 0, length: (editor.string as NSString).length))
        let pasted = editor.readSelection(from: clipboard)
        try await waitForSmoke(timeout: 3) { self.drafts[sessionId] == text }
        try await Task.sleep(for: .milliseconds(100))
        diagnostic["nativeTextPaste"] = pasted && drafts[sessionId] == text
        diagnostic["editorRetainedAfterAttachments"] = editors(view).contains { $0 === editor }
        diagnostic["pasteGrowsViewport"] = (editor.enclosingScrollView?.contentView.bounds.height ?? 0) > 25
        report(diagnostic)
        guard pasted, editors(view).contains(where: { $0 === editor }), diagnostic["pasteGrowsViewport"] as? Bool == true else { throw MightyError("첨부 이후 네이티브 텍스트 붙여넣기 또는 자동 높이를 확인하지 못했습니다.") }
    }

    private func smokeAccessibilityElement(_ element: Any, identifier: String, tree: inout [[String: Any]]) -> Bool? {
        var visited = Set<ObjectIdentifier>()
        func find(_ element: Any, depth: Int) -> Bool? {
            if let view = element as? NSView, view.isHiddenOrHasHiddenAncestor { return nil }
            guard depth < 128, visited.count < 10_000, let object = element as? NSObject,
                  visited.insert(ObjectIdentifier(object)).inserted else { return nil }
            var children: [Any] = []
            if let node = element as? any NSAccessibilityProtocol {
                let currentId = node.accessibilityIdentifier() ?? ""
                tree.append(["depth": depth, "class": String(describing: type(of: element)), "id": currentId, "enabled": node.isAccessibilityEnabled()])
                if currentId == identifier { return node.isAccessibilityEnabled() }
                children = node.accessibilityChildren() ?? []
            } else {
                // SwiftUI virtual nodes may expose Objective-C getters without
                // declaring NSAccessibilityProtocol conformance.
                let currentId = object.responds(to: NSSelectorFromString("accessibilityIdentifier")) ? object.value(forKey: "accessibilityIdentifier") as? String ?? "" : ""
                let enabled = object.responds(to: NSSelectorFromString("isAccessibilityEnabled")) ? object.value(forKey: "accessibilityEnabled") as? Bool : nil
                tree.append(["depth": depth, "class": String(describing: type(of: element)), "id": currentId, "enabled": enabled ?? false])
                if currentId == identifier { return enabled }
                if object.responds(to: NSSelectorFromString("accessibilityChildren")) { children = object.value(forKey: "accessibilityChildren") as? [Any] ?? [] }
            }
            // AppKit can omit ignored hosting wrappers from the parent AX tree
            // before their SwiftUI children are materialized. Inspect the native
            // subtree too; identity tracking avoids traversing shared nodes twice.
            if let view = element as? NSView { children.append(contentsOf: view.subviews) }
            if let window = element as? NSWindow, let content = window.contentView { children.append(content) }
            for child in children {
                if let found = find(child, depth: depth + 1) { return found }
            }
            return nil
        }
        return find(element, depth: 0)
    }
}
