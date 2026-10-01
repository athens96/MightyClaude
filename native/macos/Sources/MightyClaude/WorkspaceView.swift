import SwiftUI
import AppKit
import MightyCore

struct WorkspaceView: View {
    @EnvironmentObject private var store: AppStore
    @FocusState private var searchFocused: Bool
    @StateObject private var gitState = WorkspaceGitState()
    @StateObject private var accountUsage = AccountUsageStatusController()

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 210, ideal: store.snapshot.sidebarWidth, max: 360)
        } detail: {
            VStack(spacing: 0) {
                if let warning = store.resourceWarning { resourceWarningBanner(warning) }
                if let error = store.error { errorBanner(error) }
                if store.showsDashboard { DashboardView(gitState: gitState, accountUsage: accountUsage) }
                else if let workspace = store.activeWorkspace {
                    workspaceHeader(workspace)
                    Divider()
                    if store.activeSessions.isEmpty { emptyPanes }
                    else { paneCollection }
                } else { welcome }
                statusBar
            }
            .background(Palette.canvas)
            // The hidden title bar still reserves its height as a top safe area. The
            // traffic lights sit over the sidebar, so the detail column can use that band.
            .ignoresSafeArea(.container, edges: .top)
        }
        .navigationSplitViewStyle(.balanced)
        .toolbar(removing: .sidebarToggle)
        .toolbar(.hidden, for: .windowToolbar)
        .task(id: store.activeWorkspace.map { $0.id + "|" + $0.path }) {
            await gitState.observe(store.activeWorkspace)
        }
        .task { accountUsage.configure(store: store) }
        .sheet(isPresented: $store.showSettings) { AppSettingsView().environmentObject(store) }
        .sheet(item: $store.renameTarget) { RenameSheet(target: $0).environmentObject(store) }
        .sheet(item: $store.terminalHistorySession) { LegacyTerminalHistory(session: $0) }
        .sheet(item: $store.resumePickerWorkspace) { ResumeSessionSheet(workspace: $0).environmentObject(store) }
        .sheet(item: $store.pluginBrowser) { browser in
            ClaudePluginView(model: browser, onClose: { store.pluginBrowser = nil })
                .interactiveDismissDisabled(browser.isMutating)
                .onDisappear { Task { await browser.shutdown() } }
        }
        .onChange(of: store.focusSearch) { _, value in
            if value { searchFocused = true; store.focusSearch = false }
        }
        .confirmationDialog("워크스페이스를 목록에서 제거할까요?", isPresented: Binding(get: { store.pendingRemoval != nil }, set: { if !$0 { store.pendingRemoval = nil } }), titleVisibility: .visible) {
            if let workspace = store.pendingRemoval {
                Button("목록에서 제거", role: .destructive) { store.removeWorkspace(workspace) }
                Button("취소", role: .cancel) { store.pendingRemoval = nil }
            }
        } message: { Text("실행 중인 작업을 중지하고 앱의 실행 기록을 제거합니다. 프로젝트 폴더와 파일은 유지됩니다.") }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: "magnifyingglass").foregroundStyle(Palette.sidebarInk2)
                TextField("워크스페이스 검색", text: $store.search).textFieldStyle(.plain).font(.system(size: 12))
                    .focused($searchFocused)
                    .accessibilityLabel("워크스페이스 검색")
            }
            .padding(9).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7)).padding(.horizontal, 14).padding(.top, 10)

            dashboardEntry.padding(.horizontal, 9).padding(.top, 12)

            HStack {
                Text("워크스페이스").font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.sidebarInk2)
                Text("\(store.snapshot.workspaces.count)").font(.system(size: 10, design: .monospaced)).foregroundStyle(Palette.sidebarInk2)
                Spacer()
                Button { store.openWorkspace() } label: { Image(systemName: "plus").font(.system(size: 12)) }
                    .buttonStyle(.plain).help("프로젝트 폴더 열기 (⌘O)").accessibilityLabel("프로젝트 폴더 열기")
            }
            .padding(.horizontal, 20).padding(.top, 20).padding(.bottom, 11)

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 4) {
                    ForEach(store.filteredWorkspaces) { workspace in workspaceRow(workspace) }
                    if store.filteredWorkspaces.isEmpty {
                        Text(store.search.isEmpty ? "폴더를 열고 작업을 시작하세요." : "검색 결과가 없습니다.")
                            .font(.system(size: 12)).foregroundStyle(Palette.sidebarInk2).padding(16)
                    }
                }
                .padding(.horizontal, 9)
            }

            Button { store.openWorkspace() } label: {
                Label("폴더 열기", systemImage: "folder.badge.plus").font(.system(size: 12)).frame(maxWidth: .infinity, alignment: .leading).padding(10)
            }
            .buttonStyle(.plain).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7)).padding(.horizontal, 14).padding(.bottom, 12)
            Divider()
            HStack(spacing: 9) {
                Group {
                    if let image = BrandAssets.icon { Image(nsImage: image).resizable().interpolation(.high).scaledToFit() }
                    else { Image(systemName: "sparkles").resizable().scaledToFit().foregroundStyle(Palette.sidebarAccent) }
                }.frame(width: 20, height: 20).accessibilityHidden(true)
                Text("Mighty Claude").font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Spacer()
                Button { store.toggleTheme() } label: { Image(systemName: store.snapshot.theme == "dark" ? "sun.max" : "moon") }
                    .buttonStyle(.plain).help("화면 테마 변경").accessibilityLabel("화면 테마 변경")
                Button { store.showSettings = true } label: { Image(systemName: "gearshape") }
                    .buttonStyle(.plain).help("설정").accessibilityLabel("설정")
            }.padding(16)
        }
        // A solid D surface over the split view's vibrancy, so the wallpaper never
        // decides the sidebar's contrast.
        .background(Palette.sidebar.ignoresSafeArea())
        .background(GeometryReader { proxy in
            Color.clear.preference(key: SidebarWidthKey.self, value: proxy.size.width)
        })
        .onPreferenceChange(SidebarWidthKey.self) { width in
            if width >= 200, abs(store.snapshot.sidebarWidth - width) > 1 { store.snapshot.sidebarWidth = width }
        }
    }

    /// "작업 현황" at the top of the sidebar, with what runs and what waits on the user.
    private var dashboardEntry: some View {
        let stats = WorkDashboard.stats(sessions: store.snapshot.sessions, permissions: store.toolPermissions)
        let selected = store.showsDashboard
        return Button { store.showsDashboard = true } label: {
            HStack(spacing: 9) {
                RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Palette.run).frame(width: 24, height: 24)
                    .overlay { Image(systemName: "square.grid.2x2.fill").font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.onStatus) }
                    .accessibilityHidden(true)
                Text(L("phone.dashboard.title")).font(.system(size: 13, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                Spacer(minLength: 0)
                if stats.running > 0 { CountPill(text: "\(stats.running)", fill: Palette.run, ink: Palette.onStatus) }
                if stats.waiting > 0 { CountPill(text: "\(stats.waiting)", fill: Palette.wait, ink: Palette.onWait) }
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(selected ? Palette.panel : Color.clear)
                .shadow(color: .black.opacity(selected ? 0.06 : 0), radius: 1, y: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(L("dashboard.sidebarHelp"))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel([L("phone.dashboard.title"),
                             DashboardText.countLabel(L("phone.dashboard.stat.running"), stats.running),
                             DashboardText.countLabel(L("phone.dashboard.stat.waiting"), stats.waiting)].joined(separator: ", "))
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier("sidebar-dashboard")
    }

    private func workspaceRow(_ workspace: Workspace) -> some View {
        let selected = !store.showsDashboard && workspace.id == store.snapshot.activeWorkspaceId
        let expanded = store.isWorkspaceExpanded(workspace.id)
        let sessions = store.snapshot.sessions.filter { $0.workspaceId == workspace.id }
        let badges = WorkDashboard.badges(sessions: sessions, permissions: store.toolPermissions)
        return VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 0) {
            Button { store.selectWorkspace(workspace.id) } label: {
                HStack(spacing: 9) {
                    Image(systemName: "folder").font(.system(size: 14)).foregroundStyle(selected ? Palette.sidebarAccent : Palette.sidebarInk2)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(workspace.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                    workspaceBadges(badges, workspace: workspace)
                }
                .padding(.leading, 11).padding(.trailing, 4).padding(.vertical, 10).frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            // The disclosure is separate from selection: opening or closing a
            // list never changes the active workspace, and selecting never closes others.
            Button { store.toggleWorkspaceExpanded(workspace.id) } label: {
                Image(systemName: expanded ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .semibold)).foregroundStyle(Palette.sidebarInk2)
                    .frame(width: 18, height: 18).contentShape(Rectangle())
            }
            .buttonStyle(.plain).padding(.trailing, 6)
            .help(expanded ? "실행 창 목록 접기" : "실행 창 목록 펼치기")
            .accessibilityLabel(expanded ? "\(workspace.name) 실행 창 접기" : "\(workspace.name) 실행 창 펼치기")
            .accessibilityIdentifier("workspace-expand-\(workspace.id)")
            }
            .background(selected ? Palette.sidebarAccent.opacity(0.10) : Color.clear, in: RoundedRectangle(cornerRadius: 7))
            .help(workspace.path)
            .contextMenu {
                Button(L("menu.rename")) { store.beginRenameWorkspace(workspace.id) }
                Button(L("workspace.menu.remove"), role: .destructive) { store.pendingRemoval = workspace }
                Button(L("menu.showInFinder")) { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: workspace.path) }
            }
            if expanded {
                ForEach(sessions) { session in paneCard(session) }
                workspaceAddMenu(workspace)
            }
        }.padding(.bottom, selected ? 12 : 1)
    }

    /// What in this workspace wants a look: questions and permission requests in amber,
    /// errors in red, panes running (and not waiting on the user) in blue.
    private func workspaceBadges(_ badges: WorkDashboard.Badges, workspace: Workspace) -> some View {
        HStack(spacing: 4) {
            if badges.questions > 0 {
                CountPill(text: L("phone.card.questions", ["count": "\(badges.questions)"]), fill: Palette.wait, ink: Palette.onWait)
            }
            if badges.permissions > 0 {
                CountPill(text: L("phone.card.permissions", ["count": "\(badges.permissions)"]), fill: Palette.wait, ink: Palette.onWait)
            }
            if badges.errors > 0 {
                CountPill(text: "\(badges.errors)", fill: Palette.err, ink: Palette.onStatus)
                    .help(DashboardText.countLabel(L("session.state.error"), badges.errors))
                    .accessibilityLabel(DashboardText.countLabel(L("session.state.error"), badges.errors))
            }
            if badges.running > 0 {
                CountPill(text: "\(badges.running)", fill: Palette.run, ink: Palette.onStatus)
                    .help(DashboardText.countLabel(L("session.state.running"), badges.running))
                    .accessibilityLabel(DashboardText.countLabel(L("session.state.running"), badges.running))
                    .accessibilityIdentifier("workspace-running-\(workspace.id)")
            }
        }
    }

    /// A pane in the sidebar as a compact status card: a status edge, the provider mark,
    /// the title and status word, and a mono line with what the app knows of its run.
    private func paneCard(_ session: RunSession) -> some View {
        let card = WorkDashboard.card(session, permissions: store.toolPermissions[session.id])
        let active = !store.showsDashboard && session.id == store.snapshot.activeSessionId
        let beta = session.kind == "claude" && ProviderOptions.isBeta(session.provider)
        let status = DashboardText.status(card)
        let localTerminal = store.usesLocalTerminal(session)
        return Button { store.selectSession(session.id) } label: {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Group {
                        if session.kind == "claude" { ProviderIcon(provider: session.provider, size: 10) }
                        else { Image(systemName: paneSymbol(session.kind)).font(.system(size: 10)).foregroundStyle(Palette.ink2) }
                    }.frame(width: 12)
                    Text(session.title).font(.system(size: 12, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1).help(session.titleHelp)
                    if beta { BetaBadge() }
                    Spacer(minLength: 0)
                    Text(status).font(.system(size: 10.5, weight: .bold)).foregroundStyle(Palette.text(card.displayStatus)).lineLimit(1).fixedSize()
                        .accessibilityIdentifier(card.isRunning ? "sidebar-running-\(session.id)" : "sidebar-status-\(session.id)")
                }
                // Only a running pane with a clock of its own ticks every second.
                if card.isRunning, let timing = card.timing, timing.finishedAt == nil {
                    TimelineView(.periodic(from: .now, by: 1)) { context in paneMeta(card, localTerminal: localTerminal, at: context.date) }
                } else {
                    TimelineView(.everyMinute) { context in paneMeta(card, localTerminal: localTerminal, at: context.date) }
                }
            }
            .padding(.leading, 13).padding(.trailing, 9).padding(.vertical, 7)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Palette.panel)
            .overlay(alignment: .leading) { Rectangle().fill(Palette.edge(card.tone)).frame(width: 3) }
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(active ? Palette.run : Color.clear, lineWidth: 1.5))
            // The shadow belongs to the card's shape alone, not to every view on it.
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Palette.panel).shadow(color: .black.opacity(0.05), radius: 1, y: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.leading, 22).padding(.trailing, 2).padding(.vertical, 1)
        .accessibilityLabel("\(session.title)\(beta ? ", " + L("badge.betaAccessibility") : ""), \(status)")
        .accessibilityAddTraits(active ? .isSelected : [])
        .contextMenu {
            Button(L("menu.rename")) { store.beginRenameSession(session.id) }
            Button(L("menu.closePane"), role: .destructive) { store.closeSession(session.id) }
        }
    }

    private func paneMeta(_ card: WorkDashboard.Card, localTerminal: Bool, at date: Date) -> some View {
        let parts = WorkDashboard.sidebarMeta(card, now: date).compactMap { part -> String? in
            switch part {
            case .elapsed: card.timing.map { DashboardText.clock($0, at: date) }
            case .context(let percent): L("phone.card.context", ["percent": "\(percent)"])
            case .age(let age): DashboardText.age(age)
            }
        }
        return Text(parts.isEmpty ? DashboardText.kind(card, localTerminal: localTerminal) : parts.joined(separator: " · "))
            .font(.system(size: 10.5, design: .monospaced)).foregroundStyle(Palette.ink2).lineLimit(1)
            .padding(.leading, 18)
    }

    private func workspaceHeader(_ workspace: Workspace) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text(workspace.name).font(.system(size: 17, weight: .semibold)).lineLimit(1)
                    Spacer(minLength: 0)
                }
                .overlay { WorkspaceTitlebarRegion(enabled: !store.hasModal, rename: { store.beginRenameWorkspace(workspace.id) }) }
                HStack(spacing: 10) {
                    Text(workspace.path).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                    if let git = gitState.info(for: workspace) { WorkspaceGitBadge(info: git) }
                    Spacer(minLength: 0)
                        .overlay { WorkspaceTitlebarRegion(enabled: !store.hasModal, rename: { store.beginRenameWorkspace(workspace.id) }) }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Button { store.openFilePane(workspaceId: workspace.id) } label: {
                Image(systemName: "folder").font(.system(size: 13)).frame(width: 26, height: 24).contentShape(Rectangle())
            }
            .buttonStyle(.plain).foregroundStyle(.secondary)
            .disabled(store.hasModal)
            .help(L("menu.showFiles") + " (⇧⌘E)")
            .accessibilityLabel(L("menu.showFiles"))
            .accessibilityIdentifier("workspace-open-files-\(workspace.id)")
        }.padding(.horizontal, 24).padding(.top, 14).padding(.bottom, 10)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("workspace-header-\(workspace.id)")
    }

    /// Last row of a workspace's pane list in the sidebar, laid out like the rows above it.
    private func workspaceAddMenu(_ workspace: Workspace) -> some View {
        Menu {
            WorkspaceAddMenuItems(store: store, workspace: workspace)
        } label: {
            HStack(spacing: 8) {
                Image(systemName: "plus").font(.system(size: 10, weight: .semibold)).frame(width: 12)
                Text(L("workspace.addPane")).font(.system(size: 11))
                Spacer(minLength: 0)
            }
            .foregroundStyle(Palette.sidebarAccent)
            .padding(.leading, 26).padding(.trailing, 12).padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .disabled(store.hasModal)
        .help(L("workspace.addPaneHelp"))
        .accessibilityLabel(L("workspace.addPaneAccessibility", ["workspace": workspace.name]))
        .accessibilityIdentifier("workspace-add-session-\(workspace.id)")
    }

    private var paneCollection: some View {
        Group {
            if let workspace = store.activeWorkspace, let root = store.layoutForWorkspace(workspace.id) {
                PaneDockView(root: root, workspaceId: workspace.id)
            } else { ProgressView("창 배치 불러오는 중…").frame(maxWidth: .infinity, maxHeight: .infinity) }
        }
    }

    private var welcome: some View {
        VStack(spacing: 18) {
            Spacer()
            Image(systemName: "rectangle.split.2x1").font(.system(size: 50, weight: .ultraLight)).foregroundStyle(Palette.accent)
            Text("하나의 프로젝트, 여러 실행 창").font(.system(size: 27, weight: .semibold))
            Text("폴더를 열고 Claude, Codex, Gemini와 작업하세요.\n각 실행 창의 대화와 설정은 따로 유지됩니다.")
                .font(.system(size: 14)).foregroundStyle(.secondary).multilineTextAlignment(.center).lineSpacing(5)
            HStack(spacing: 10) {
                Button { store.openWorkspace() } label: { Label("프로젝트 폴더 열기", systemImage: "folder.badge.plus").padding(.horizontal, 10).padding(.vertical, 5) }.buttonStyle(.borderedProminent)
            }.padding(.top, 8)
            Text("⌘O  폴더 열기   ·   ⌘N  실행 창 추가").font(.system(size: 11)).foregroundStyle(.tertiary).padding(.top, 10)
            Spacer()
            Text("로컬 CLI와 직접 연결되는 macOS 앱").font(.system(size: 11)).foregroundStyle(.tertiary).padding(.bottom, 25)
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var emptyPanes: some View {
        ContentUnavailableView {
            Label("실행 창을 추가하세요", systemImage: "square.stack.3d.up")
        } description: { Text("AI 대화와 프로젝트 명령을 나란히 실행할 수 있습니다.") }
        actions: { Button("새 Claude 실행 창") { store.addSession(kind: "claude") }.buttonStyle(.borderedProminent) }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var statusBar: some View {
        HStack(spacing: 7) {
            Image(systemName: "desktopcomputer").font(.system(size: 10))
            Text("이 Mac에서 실행")
            Spacer()
            Text("\(store.activeSessions.count)개 실행 창")
            Text("·").padding(.horizontal, 3)
            Text("\(store.snapshot.sessions.filter { $0.status == "running" }.count)개 실행 중")
            if let version = store.appUpdate.availability?.manifest.version, [.available, .ready].contains(store.appUpdate.phase) {
                Button { store.showSettings = true } label: {
                    Label("새 버전 \(version)", systemImage: "arrow.down.circle.fill").font(.system(size: 10, weight: .medium)).foregroundStyle(Palette.accent)
                }
                .buttonStyle(.plain).help("설정에서 업데이트를 받거나 설치할 수 있습니다.").accessibilityIdentifier("app-update-badge")
            }
            Divider().frame(height: 12).padding(.horizontal, 4)
            StatusBarUsageView(controller: accountUsage)
            AgentStatusControls(companion: store.companion)
        }
        .font(.system(size: 10)).foregroundStyle(.secondary).padding(.horizontal, 20).padding(.vertical, 8)
        .background(Palette.subtle).overlay(alignment: .top) { Divider() }
    }

    private func resourceWarningBanner(_ message: String) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(Palette.waitText)
            Text(message).font(.system(size: 12)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            Button { store.resourceWarning = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel("경고 닫기")
        }.padding(12).background(Palette.waitSoft)
    }

    private func errorBanner(_ message: String) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(Palette.errText)
            Text(message).font(.system(size: 12)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            Button { store.error = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel("오류 닫기")
        }.padding(12).background(Palette.errSoft)
    }
}

/// The "창 추가" menu's items, shared by the sidebar's last row and the dashboard's
/// workspace header: new agent panes, an earlier session, a terminal, a browser tab.
struct WorkspaceAddMenuItems: View {
    let store: AppStore
    let workspace: Workspace

    var body: some View {
        ForEach(ProviderOptions.ids, id: \.self) { provider in
            Button { addSession(kind: "claude", provider: provider) } label: {
                Label { Text(ProviderOptions.betaTitle(provider, "새 \(ProviderOptions.label(provider)) 실행 창")) } icon: {
                    if let image = ProviderIconImage.image(provider: provider, pointSize: 12) { Image(nsImage: image) }
                    else { Image(systemName: Palette.symbol(provider)) }
                }
            }
        }
        Button { store.openResumePicker(workspace.id) } label: {
            Label(L("resume.menu"), systemImage: "clock.arrow.circlepath")
        }
        .accessibilityIdentifier("workspace-resume-session-\(workspace.id)")
        Divider()
        Button { addSession(kind: "shell") } label: {
            Label(L("workspace.newTerminal"), systemImage: "terminal")
        }
        Divider()
        Button { addSession(kind: "browser") } label: {
            Label(L("browser.newTab"), systemImage: "globe")
        }
        .accessibilityIdentifier("new-browser-tab")
    }

    private func addSession(kind: String, provider: String = "claude") {
        guard !store.hasModal, store.snapshot.workspaces.contains(where: { $0.id == workspace.id }) else { return }
        store.selectWorkspace(workspace.id)
        store.addSession(kind: kind, provider: provider)
    }
}

private struct SidebarWidthKey: PreferenceKey {
    static var defaultValue: CGFloat = 252
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}
