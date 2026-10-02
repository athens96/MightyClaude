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
        .sheet(item: $store.resumePicker) { request in
            Group {
                switch request.stage {
                case .checking: ResumeChoiceSheet(provider: request.provider, checking: true)
                case .choice: ResumeChoiceSheet(provider: request.provider)
                case .list: ResumeSessionSheet(workspace: request.workspace, provider: request.provider)
                }
            }
            .environmentObject(store)
        }
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

            // While a workspace is listed, a folder opens from its "창 추가" menu or ⌘O;
            // with none listed (none yet, or none matching the search) this is the
            // sidebar's way in.
            if store.filteredWorkspaces.isEmpty {
                Button { store.openWorkspace() } label: {
                    Label("폴더 열기", systemImage: "folder.badge.plus").font(.system(size: 12)).frame(maxWidth: .infinity, alignment: .leading).padding(10)
                }
                .buttonStyle(.plain).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7)).padding(.horizontal, 14).padding(.bottom, 12)
            }
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

    /// "작업 현황" at the top of the sidebar, with what waits on the user, what runs and
    /// what stopped on an error, as glyph counts.
    private var dashboardEntry: some View {
        let badges = WorkDashboard.badges(sessions: store.snapshot.sessions, permissions: store.toolPermissions)
        let selected = store.showsDashboard
        return Button { store.showsDashboard = true } label: {
            HStack(spacing: 9) {
                RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Palette.run).frame(width: 24, height: 24)
                    .overlay { Image(systemName: "square.grid.2x2.fill").font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.onStatus) }
                    .accessibilityHidden(true)
                Text(L("phone.dashboard.title")).font(.system(size: 13, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                Spacer(minLength: 0)
                StatusCounts(badges: badges)
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(selected ? Palette.panel : Color.clear)
                .shadow(color: .black.opacity(selected ? 0.06 : 0), radius: 1, y: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(L("dashboard.sidebarHelp"))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(([L("phone.dashboard.title")] + StatusCounts.labels(badges)).joined(separator: ", "))
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
                ForEach(sessions) { session in paneRow(session) }
                workspaceAddMenu(workspace)
            }
        }.padding(.bottom, selected ? 12 : 1)
    }

    /// What in this workspace wants a look: requests waiting on the user, panes running
    /// (and not waiting), panes stopped by an error — each a glyph and a count.
    private func workspaceBadges(_ badges: WorkDashboard.Badges, workspace: Workspace) -> some View {
        StatusCounts(badges: badges, runningIdentifier: "workspace-running-\(workspace.id)")
    }

    /// A pane in the sidebar as a plain row on the sidebar surface: its status glyph,
    /// the title, and a muted line with the provider, the clock and the context. Only a
    /// pane waiting on the user carries a word on the right ("질문 1").
    private func paneRow(_ session: RunSession) -> some View {
        let card = WorkDashboard.card(session, permissions: store.toolPermissions[session.id])
        let active = !store.showsDashboard && session.id == store.snapshot.activeSessionId
        let beta = session.kind == "claude" && ProviderOptions.isBeta(session.provider)
        let status = DashboardText.status(card)
        let localTerminal = store.usesLocalTerminal(session)
        let settled = [.done, .stop, .idle].contains(card.tone)
        return Button { store.selectSession(session.id) } label: {
            HStack(alignment: .top, spacing: 8) {
                StatusGlyph(tone: card.tone, kind: session.kind).padding(.top, 1.5).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 6) {
                        Text(session.title).font(.system(size: 12.5, weight: settled ? .medium : .semibold)).foregroundStyle(Palette.ink)
                            .lineLimit(1).help(session.titleHelp)
                        if beta { BetaBadge() }
                    }
                    .frame(minHeight: 17)
                    // Only a running pane with a clock of its own ticks every second.
                    Group {
                        if card.isRunning, let timing = card.timing, timing.finishedAt == nil {
                            TimelineView(.periodic(from: .now, by: 1)) { context in paneMeta(card, localTerminal: localTerminal, at: context.date) }
                        } else {
                            TimelineView(.everyMinute) { context in paneMeta(card, localTerminal: localTerminal, at: context.date) }
                        }
                    }
                    .foregroundStyle(active ? Palette.ink2 : Palette.sidebarInk2)
                }
                Spacer(minLength: 0)
                if card.attention.total > 0 {
                    Text(status).font(.system(size: 10.5, weight: .bold)).foregroundStyle(Palette.waitText).lineLimit(1).fixedSize()
                        .frame(minHeight: 17)
                }
            }
            .padding(.leading, 8).padding(.trailing, 9).padding(.top, 6).padding(.bottom, 7)
            .frame(maxWidth: .infinity, alignment: .leading)
            .modifier(SidebarRowHighlight(selected: active))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.leading, 22).padding(.trailing, 2).padding(.vertical, 1)
        // The Button stays the accessibility element (its own press action); only its
        // label is replaced, so the meta line's ticking clock is not read out.
        .accessibilityLabel("\(session.title)\(beta ? ", " + L("badge.betaAccessibility") : ""), \(card.attention.total > 0 ? status : Palette.word(card.tone))")
        .accessibilityAddTraits(active ? .isSelected : [])
        .accessibilityIdentifier(card.isRunning ? "sidebar-running-\(session.id)" : "sidebar-status-\(session.id)")
        .contextMenu {
            Button(L("menu.rename")) { store.beginRenameSession(session.id) }
            Button(L("menu.closePane"), role: .destructive) { store.closeSession(session.id) }
        }
    }

    private func paneMeta(_ card: WorkDashboard.Card, localTerminal: Bool, at date: Date) -> some View {
        let parts = WorkDashboard.sidebarMeta(card, now: date).compactMap { part -> String? in
            switch part {
            case .provider: ProviderOptions.label(card.provider)
            case .elapsed: card.timing.map { DashboardText.clock($0, at: date) }
            case .context(let percent): L("phone.card.context", ["percent": "\(percent)"])
            case .reason(let text, let isTool): DashboardText.reason(text, isTool: isTool)
            case .age(let age): DashboardText.age(age)
            }
        }
        return Text(parts.isEmpty ? DashboardText.kindLine(card, localTerminal: localTerminal) : parts.joined(separator: " · "))
            .font(.system(size: 11)).monospacedDigit().lineLimit(1).truncationMode(.tail)
            .frame(minHeight: 15)
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
            StatusCounts(badges: WorkDashboard.badges(sessions: store.snapshot.sessions.filter { $0.workspaceId == workspace.id }, permissions: store.toolPermissions),
                         runningIdentifier: "workspace-header-running-\(workspace.id)", long: true)
                .accessibilityIdentifier("workspace-header-status-\(workspace.id)")
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
/// workspace header: new agent panes (Claude and Codex then ask whether to continue
/// an earlier session, `AppStore.addAgentPane`), a terminal, a browser tab, and
/// another project folder.
struct WorkspaceAddMenuItems: View {
    let store: AppStore
    let workspace: Workspace

    var body: some View {
        ForEach(ProviderOptions.ids, id: \.self) { provider in
            Button { store.addAgentPane(provider: provider, workspaceId: workspace.id) } label: {
                Label { Text(ProviderOptions.betaTitle(provider, "새 \(ProviderOptions.label(provider)) 실행 창")) } icon: {
                    if let image = ProviderIconImage.image(provider: provider, pointSize: 12) { Image(nsImage: image) }
                    else { Image(systemName: Palette.symbol(provider)) }
                }
            }
            .accessibilityIdentifier("workspace-add-agent-\(provider)-\(workspace.id)")
        }
        Divider()
        Button { addSession(kind: "shell") } label: {
            Label(L("workspace.newTerminal"), systemImage: "terminal")
        }
        Divider()
        Button { addSession(kind: "browser") } label: {
            Label(L("browser.newTab"), systemImage: "globe")
        }
        .accessibilityIdentifier("new-browser-tab")
        Divider()
        Button { store.openWorkspace() } label: {
            Label(L("menu.openProject"), systemImage: "folder.badge.plus")
        }
        .accessibilityIdentifier("workspace-open-folder-\(workspace.id)")
    }

    private func addSession(kind: String) {
        guard !store.hasModal, store.snapshot.workspaces.contains(where: { $0.id == workspace.id }) else { return }
        store.selectWorkspace(workspace.id)
        store.addSession(kind: kind)
    }
}

/// Counts as glyphs (status v2): "? 1  ✻ 2  ! 1" in the sidebar — waiting on the user,
/// running, stopped by an error. `long` is the workspace header's summary, which also
/// counts what has settled and names each state: "? 1 응답 대기  ✻ 1 실행 중  ✓ 1 완료  ○ 1 준비".
/// A zero is left out.
private struct StatusCounts: View {
    let badges: WorkDashboard.Badges
    var runningIdentifier = "sidebar-dashboard-running"
    var long = false

    var body: some View {
        HStack(spacing: 9) {
            ForEach(Self.entries(badges, long: long), id: \.tone) { entry in
                count(entry)
                    .accessibilityIdentifier(entry.tone == .run ? runningIdentifier : "status-count-\(entry.tone.rawValue)")
            }
        }
        .fixedSize()
    }

    private func count(_ entry: Entry) -> some View {
        HStack(spacing: 3) {
            StatusGlyph(tone: entry.tone, size: 12)
            Text("\(entry.count)").font(.system(size: 11, weight: .bold)).monospacedDigit().foregroundStyle(Palette.ink)
            if long { Text(Palette.word(entry.tone)).font(.system(size: 11, weight: .medium)).foregroundStyle(Palette.ink2).padding(.leading, 1) }
        }
        .help(entry.label)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(entry.label)
    }

    struct Entry {
        let tone: DesignTone
        let count: Int
        let label: String
    }

    /// In the mockup's order: waiting, running, error, then (long only) done, stopped, idle.
    static func entries(_ badges: WorkDashboard.Badges, long: Bool) -> [Entry] {
        let waiting = badges.questions + badges.permissions
        var entries: [Entry] = []
        if waiting > 0 { entries.append(Entry(tone: .wait, count: waiting, label: waitingLabel(badges))) }
        var counted: [(DesignTone, Int)] = [(.run, badges.running), (.err, badges.errors)]
        if long { counted += [(.done, badges.done), (.stop, badges.stopped), (.idle, badges.idle)] }
        for (tone, count) in counted where count > 0 {
            entries.append(Entry(tone: tone, count: count, label: DashboardText.countLabel(Palette.word(tone), count)))
        }
        return entries
    }

    /// Questions and permission requests in words, as the amber pills used to say them.
    static func waitingLabel(_ badges: WorkDashboard.Badges) -> String {
        [badges.questions > 0 ? L("phone.card.questions", ["count": "\(badges.questions)"]) : nil,
         badges.permissions > 0 ? L("phone.card.permissions", ["count": "\(badges.permissions)"]) : nil]
            .compactMap { $0 }.joined(separator: ", ")
    }

    static func labels(_ badges: WorkDashboard.Badges) -> [String] {
        entries(badges, long: false).map(\.label)
    }
}

/// The neutral rounded wash behind a sidebar row: the card surface while selected,
/// a faint tint under the pointer, never a status colour.
private struct SidebarRowHighlight: ViewModifier {
    let selected: Bool
    @ViewState private var hovering = false

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        content
            .background(selected ? Palette.panel : hovering ? Palette.subtle : Color.clear, in: shape)
            .overlay { if selected { shape.strokeBorder(Color.black.opacity(0.07), lineWidth: 0.5).allowsHitTesting(false) } }
            .onHover { hovering = $0 }
    }
}

private struct SidebarWidthKey: PreferenceKey {
    static var defaultValue: CGFloat = 252
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}
