import AppKit
import Combine
import MightyCore
import SwiftUI

struct IslandAccount: Identifiable {
    let id: String
    let provider: String
    let host: String
    let remote: Bool
    var sessionCount: Int
    var usage: AccountUsageSnapshot?
}

/// Account quotas only. A conversation's context window belongs in its composer.
@MainActor
final class SessionIslandController: ObservableObject {
    @Published private(set) var accounts: [IslandAccount] = []
    @Published private(set) var compactAccounts: [IslandAccount] = []
    @Published private(set) var refreshing = false
    @Published var showsBottomPopover = false
    private weak var store: AppStore?
    private let service = AccountUsageService()
    private var localUsage: [String: AccountUsageSnapshot] = [:]
    private var subscriptions = Set<AnyCancellable>()
    private var pollTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?
    private var panel: SessionIslandPanel?
    private var stopped = false
    private var overlayEnabled = true
    private var testing = false
    private var diagnosticPanelEnabled = false

    func configure(store: AppStore) {
        guard self.store == nil else { return }
        self.store = store
        testing = ProcessInfo.processInfo.arguments.contains { $0.hasPrefix("--") && $0.contains("smoke-test") }
        overlayEnabled = store.companion.preferences.showsSessionIsland != false
        store.$snapshot.sink { [weak self] snapshot in self?.update(snapshot) }.store(in: &subscriptions)
        store.companion.$preferences.sink { [weak self] preferences in
            guard let self else { return }
            self.overlayEnabled = preferences.showsSessionIsland != false
            self.updatePanel()
        }.store(in: &subscriptions)
        NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)
            .sink { [weak self] _ in self?.panel?.reposition() }.store(in: &subscriptions)
        if !testing {
            pollTask = Task { [weak self] in
                while !Task.isCancelled {
                    self?.refresh()
                    do { try await Task.sleep(for: .seconds(60)) } catch { break }
                }
            }
        }
    }

    private func update(_ snapshot: AppSnapshot) {
        guard !stopped else { return }
        var groups: [IslandAccount] = []
        var directUsage: [String: AccountUsageSnapshot] = [:]
        for session in snapshot.sessions where session.kind != "shell" && ProviderOptions.ids.contains(session.provider) {
            guard let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { continue }
            let key = session.provider + "|" + (workspace.remote?.connectionId ?? "local")
            if let measured = session.sessionUsage, measured.provider == session.provider,
               let clean = SessionUsageSupport.normalized(measured), let rates = clean.rateLimits {
                let windows = rates.compactMap { rate -> AccountUsageWindow? in
                    guard let percent = rate.percentUsed else { return nil }
                    return AccountUsageWindow(kind: rate.kind, usedPercent: percent, resetsAt: rate.resetsAt)
                }
                if !windows.isEmpty, let date = Self.date(clean.rateLimitsUpdatedAt),
                   date > (Self.date(directUsage[key]?.fetchedAt) ?? .distantPast) {
                    let stale = Date().timeIntervalSince(date) > 300
                    directUsage[key] = AccountUsageSnapshot(provider: session.provider, windows: windows,
                        fetchedAt: clean.rateLimitsUpdatedAt, status: stale ? "stale" : "available",
                        detail: stale ? "세션에서 마지막으로 받은 계정 한도입니다." : "실행 중인 세션에서 받은 계정 한도입니다.")
                }
            }
            if let index = groups.firstIndex(where: { $0.id == key }) { groups[index].sessionCount += 1 }
            else {
                groups.append(IslandAccount(id: key, provider: session.provider, host: workspace.remote?.hostName ?? "이 Mac",
                    remote: workspace.remote != nil, sessionCount: 1, usage: workspace.remote == nil ? localUsage[session.provider] : nil))
            }
        }
        for index in groups.indices {
            guard let direct = directUsage[groups[index].id] else { continue }
            let fetched = groups[index].usage
            // A remote host never borrows this Mac's account credentials or quota.
            // Direct measurements cannot prove that a cached login identity still matches.
            if fetched == nil || (Self.date(direct.fetchedAt) ?? .distantPast) > (Self.date(fetched?.fetchedAt) ?? .distantPast) {
                groups[index].usage = direct
            }
        }
        let previousLocalProviders = Set(accounts.filter { !$0.remote }.map(\.provider))
        accounts = groups.sorted { lhs, rhs in
            if lhs.remote != rhs.remote { return !lhs.remote }
            let l = ProviderOptions.ids.firstIndex(of: lhs.provider) ?? 0, r = ProviderOptions.ids.firstIndex(of: rhs.provider) ?? 0
            return l == r ? lhs.id < rhs.id : l < r
        }
        compactAccounts = IslandSummaryPolicy.representatives(accounts: accounts, snapshot: snapshot)
        updatePanel()
        let newProviders = Set(accounts.filter { !$0.remote }.map(\.provider))
        if !testing, !newProviders.subtracting(previousLocalProviders).isEmpty { refresh() }
    }

    func refresh(force: Bool = false) {
        guard !stopped, !testing, refreshTask == nil else { return }
        let providers = Array(Set(accounts.filter { !$0.remote }.map(\.provider))).sorted()
        guard !providers.isEmpty else { return }
        refreshing = true
        refreshTask = Task { [weak self] in
            guard let self else { return }
            defer { self.refreshing = false; self.refreshTask = nil }
            for provider in providers {
                guard !Task.isCancelled, !self.stopped else { break }
                guard self.accounts.contains(where: { !$0.remote && $0.provider == provider }) else { continue }
                self.localUsage[provider] = await self.service.read(provider: provider, force: force)
                if let snapshot = self.store?.snapshot { self.update(snapshot) }
            }
        }
    }

    private func updatePanel() {
        guard (!testing || diagnosticPanelEnabled), !stopped else { return }
        guard overlayEnabled, !compactAccounts.isEmpty else { panel?.orderOut(nil); panel?.closeDetails(); return }
        if panel == nil { panel = SessionIslandPanel(controller: self) }
        panel?.reposition()
        panel?.orderFrontRegardless()
    }

    func showFloatingDetails() { panel?.toggleDetails() }

    private static func date(_ value: String?) -> Date? {
        guard let value else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }

    func showDiagnosticPanel(_ enabled: Bool) -> SessionIslandPanel? {
        guard testing else { return nil }
        diagnosticPanelEnabled = enabled
        if enabled { updatePanel() }
        else { panel?.closeDetails(); panel?.orderOut(nil) }
        return panel
    }

    /// Isolated UI diagnostics may inject quota fixtures without reading login
    /// credentials or making a provider request.
    func setDiagnosticUsage(_ values: [String: AccountUsageSnapshot]) {
        guard testing, let store else { return }
        localUsage = values
        update(store.snapshot)
    }

    func shutdown() async {
        stopped = true
        pollTask?.cancel(); pollTask = nil
        let refresh = refreshTask
        refresh?.cancel()
        subscriptions.removeAll()
        panel?.closeDetails(); panel?.contentView = nil; panel?.close(); panel = nil
        await service.shutdown()
        await refresh?.value
        refreshTask = nil
    }
}

@MainActor
final class SessionIslandPanel: NSPanel {
    private let details = NSPopover()
    private weak var controller: SessionIslandController?
    private let islandView = CameraIslandView(frame: .zero)
    private var accountsSubscription: AnyCancellable?
    private(set) var islandGeometry = CameraIslandGeometry.make(screen: .zero, safeTop: 0, left: nil, right: nil)

    init(controller: SessionIslandController) {
        self.controller = controller
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isReleasedWhenClosed = false; isOpaque = false; backgroundColor = .clear
        hasShadow = false; level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1); hidesOnDeactivate = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        contentView = islandView
        islandView.onPress = { [weak self] in self?.toggleDetails() }
        accountsSubscription = controller.$compactAccounts.sink { [weak self] accounts in
            self?.islandView.accounts = accounts
            self?.reposition()
        }
        details.behavior = .transient
        details.contentViewController = NSHostingController(rootView: AccountIslandOverview(controller: controller))
        setAccessibilityLabel("MightyClaude 계정 사용 한도 아일랜드")
        reposition()
    }
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }

    func reposition() {
        guard let screen = NSScreen.screens.first(where: { $0.safeAreaInsets.top > 0 }) ?? NSScreen.main ?? NSScreen.screens.first else { return }
        islandGeometry = CameraIslandGeometry.make(screen: screen.frame, safeTop: screen.safeAreaInsets.top,
            left: screen.auxiliaryTopLeftArea, right: screen.auxiliaryTopRightArea, wing: islandView.desiredWing)
        islandView.geometry = islandGeometry
        setFrame(islandGeometry.frame, display: true)
    }
    func toggleDetails() {
        if details.isShown { details.performClose(nil) }
        else if let contentView { details.show(relativeTo: contentView.bounds, of: contentView, preferredEdge: .minY) }
    }
    func closeDetails() { details.performClose(nil) }
}
