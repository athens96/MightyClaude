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
                if let error = store.error { errorBanner(error) }
                if let workspace = store.activeWorkspace {
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
        .task(id: store.activeWorkspace.map { $0.id + "|" + $0.path + "|" + String($0.remote != nil) }) {
            await gitState.observe(store.activeWorkspace)
        }
        .task { accountUsage.configure(store: store) }
        .sheet(isPresented: $store.showSettings, onDismiss: { store.settingsShowsRemote = false }) { AppSettingsView().environmentObject(store) }
        .sheet(isPresented: $store.showRemote) { RemoteConnectionView().environmentObject(store) }
        .sheet(item: $store.renameTarget) { RenameSheet(target: $0).environmentObject(store) }
        .sheet(item: $store.terminalHistorySession) { LegacyTerminalHistory(session: $0) }
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
                Image(systemName: "magnifyingglass").foregroundStyle(.tertiary)
                TextField("워크스페이스 검색", text: $store.search).textFieldStyle(.plain).font(.system(size: 12))
                    .focused($searchFocused)
                    .accessibilityLabel("워크스페이스 검색")
            }
            .padding(9).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7)).padding(.horizontal, 14).padding(.top, 10)

            HStack {
                Text("워크스페이스").font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
                Text("\(store.snapshot.workspaces.count)").font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                Spacer()
                Button { store.openWorkspace() } label: { Image(systemName: "plus").font(.system(size: 12)) }
                    .buttonStyle(.plain).help("프로젝트 폴더 열기 (⌘O)").accessibilityLabel("프로젝트 폴더 열기")
            }
            .padding(.horizontal, 20).padding(.top, 24).padding(.bottom, 11)

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 4) {
                    ForEach(store.filteredWorkspaces) { workspace in workspaceRow(workspace) }
                    if store.filteredWorkspaces.isEmpty {
                        Text(store.search.isEmpty ? "폴더를 열고 작업을 시작하세요." : "검색 결과가 없습니다.")
                            .font(.system(size: 12)).foregroundStyle(.secondary).padding(16)
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
                    else { Image(systemName: "sparkles").resizable().scaledToFit().foregroundStyle(Palette.accent) }
                }.frame(width: 20, height: 20).accessibilityHidden(true)
                Text("Mighty Claude").font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Spacer()
                Button { store.toggleTheme() } label: { Image(systemName: store.snapshot.theme == "dark" ? "sun.max" : "moon") }
                    .buttonStyle(.plain).help("화면 테마 변경").accessibilityLabel("화면 테마 변경")
                Button { store.showSettings = true } label: { Image(systemName: "gearshape") }
                    .buttonStyle(.plain).help("설정").accessibilityLabel("설정")
            }.padding(16)
        }
        .background(GeometryReader { proxy in
            Color.clear.preference(key: SidebarWidthKey.self, value: proxy.size.width)
        })
        .onPreferenceChange(SidebarWidthKey.self) { width in
            if width >= 200, abs(store.snapshot.sidebarWidth - width) > 1 { store.snapshot.sidebarWidth = width }
        }
    }

    private func workspaceRow(_ workspace: Workspace) -> some View {
        let selected = workspace.id == store.snapshot.activeWorkspaceId
        let expanded = store.isWorkspaceExpanded(workspace.id)
        let sessions = store.snapshot.sessions.filter { $0.workspaceId == workspace.id }
        let runningAgents = sessions.filter { $0.kind != "shell" && $0.status == "running" }.count
        return VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 0) {
            Button { store.selectWorkspace(workspace.id) } label: {
                HStack(spacing: 9) {
                    Image(systemName: workspace.remote == nil ? "folder" : "desktopcomputer").font(.system(size: 14)).foregroundStyle(selected ? Palette.accent : .secondary)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(workspace.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                        if let reference = workspace.remote { Text(reference.hostName).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1) }
                    }
                    Spacer(minLength: 0)
                    if runningAgents > 0 {
                        HStack(spacing: 4) { AgentRunningIndicator(); Text("\(runningAgents)").font(.system(size: 10, weight: .medium)).monospacedDigit() }
                            .foregroundStyle(Palette.accent)
                            .help("\(runningAgents)개 에이전트가 작업 중입니다.")
                            .accessibilityLabel("\(runningAgents)개 에이전트 실행 중")
                            .accessibilityIdentifier("workspace-running-\(workspace.id)")
                    }
                }
                .padding(.leading, 11).padding(.trailing, 4).padding(.vertical, 10).frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            // The disclosure is separate from selection: opening or closing a
            // list never changes the active workspace, and selecting never closes others.
            Button { store.toggleWorkspaceExpanded(workspace.id) } label: {
                Image(systemName: expanded ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .semibold)).foregroundStyle(.secondary)
                    .frame(width: 18, height: 18).contentShape(Rectangle())
            }
            .buttonStyle(.plain).padding(.trailing, 6)
            .help(expanded ? "실행 창 목록 접기" : "실행 창 목록 펼치기")
            .accessibilityLabel(expanded ? "\(workspace.name) 실행 창 접기" : "\(workspace.name) 실행 창 펼치기")
            .accessibilityIdentifier("workspace-expand-\(workspace.id)")
            }
            .background(selected ? Palette.accent.opacity(0.10) : Color.clear, in: RoundedRectangle(cornerRadius: 7))
            .help(workspace.path)
            .contextMenu {
                Button("이름 변경…") { store.beginRenameWorkspace(workspace.id) }
                Button("목록에서 제거", role: .destructive) { store.pendingRemoval = workspace }
                if workspace.remote == nil {
                    Button("Finder에서 보기") { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: workspace.path) }
                }
            }
            if expanded {
                ForEach(sessions) { session in
                    let agentRunning = session.kind != "shell" && session.status == "running"
                    let permissionPending = !(store.toolPermissions[session.id] ?? []).isEmpty
                    Button { store.selectSession(session.id) } label: {
                        HStack(spacing: 8) {
                            if permissionPending { Image(systemName: "hand.raised.fill").font(.system(size: 10)).foregroundStyle(.orange).frame(width: 12) }
                            else if agentRunning { AgentRunningIndicator().accessibilityIdentifier("sidebar-running-\(session.id)") }
                            else { StatusDot(status: session.status).frame(width: 12) }
                            Group {
                                if session.kind == "shell" { Image(systemName: "terminal").font(.system(size: 10)) }
                                else { ProviderIcon(provider: session.provider, size: 10) }
                            }.foregroundStyle(.secondary).frame(width: 12)
                            Text(session.title).font(.system(size: 11)).lineLimit(1)
                            Spacer(minLength: 0)
                            if permissionPending { Text("승인 대기").font(.system(size: 9, weight: .medium)).foregroundStyle(.orange) }
                            else if agentRunning { Text("작업 중").font(.system(size: 9, weight: .medium)).foregroundStyle(Palette.accent) }
                            else if session.status == "error" { Text("오류").font(.system(size: 9)).foregroundStyle(.red) }
                        }
                        .padding(.leading, 26).padding(.trailing, 12).padding(.vertical, 8)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(session.title), \(permissionPending ? "승인 대기" : Palette.status(session.status))")
                    .background(session.id == store.snapshot.activeSessionId ? Palette.subtle : Color.clear, in: RoundedRectangle(cornerRadius: 6))
                    .contextMenu {
                        Button("이름 변경…") { store.beginRenameSession(session.id) }
                        Button("실행 창 닫기", role: .destructive) { store.closeSession(session.id) }
                    }
                }
                workspaceAddMenu(workspace)
            }
        }.padding(.bottom, selected ? 12 : 1)
    }

    private func workspaceHeader(_ workspace: Workspace) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text(workspace.name).font(.system(size: 17, weight: .semibold)).lineLimit(1)
                    if let remote = workspace.remote {
                        Label(remote.hostName, systemImage: "network").font(.system(size: 10, weight: .medium)).foregroundStyle(Palette.accent)
                            .padding(.horizontal, 7).padding(.vertical, 3).background(Palette.accent.opacity(0.09), in: Capsule())
                    }
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
        }.padding(.horizontal, 24).padding(.top, 14).padding(.bottom, 10)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("workspace-header-\(workspace.id)")
    }

    /// Last row of a workspace's pane list in the sidebar, laid out like the rows above it.
    private func workspaceAddMenu(_ workspace: Workspace) -> some View {
        Menu {
            ForEach(ProviderOptions.ids, id: \.self) { provider in
                Button { addSession(in: workspace, kind: "claude", provider: provider) } label: {
                    Label { Text("새 \(ProviderOptions.label(provider)) 실행 창") } icon: {
                        if let image = ProviderIconImage.image(provider: provider, pointSize: 12) { Image(nsImage: image) }
                        else { Image(systemName: Palette.symbol(provider)) }
                    }
                }
            }
            Divider()
            Button { addSession(in: workspace, kind: "shell") } label: {
                Label(workspace.remote == nil ? "새 터미널" : "새 원격 명령", systemImage: "terminal")
            }
        } label: {
            HStack(spacing: 8) {
                Image(systemName: "plus").font(.system(size: 10, weight: .semibold)).frame(width: 12)
                Text("에이전트 추가").font(.system(size: 11))
                Spacer(minLength: 0)
            }
            .foregroundStyle(Palette.accent)
            .padding(.leading, 26).padding(.trailing, 12).padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .disabled(store.hasModal)
        .help("이 워크스페이스에 Claude · Codex · Gemini 실행 창이나 터미널 추가")
        .accessibilityLabel("\(workspace.name)에 실행 창 추가")
        .accessibilityIdentifier("workspace-add-session-\(workspace.id)")
    }

    private func addSession(in workspace: Workspace, kind: String, provider: String = "claude") {
        guard !store.hasModal, store.snapshot.workspaces.contains(where: { $0.id == workspace.id }) else { return }
        store.selectWorkspace(workspace.id)
        store.addSession(kind: kind, provider: provider)
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
                Button { store.showRemote = true } label: { Label("원격 연결", systemImage: "network").padding(.horizontal, 10).padding(.vertical, 5) }.buttonStyle(.bordered)
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
            if let workspace = store.activeWorkspace, workspace.remote != nil {
                let connected = store.connection(for: workspace)?.status == "connected"
                Circle().fill(connected ? Color.green.opacity(0.8) : Color.orange).frame(width: 5, height: 5)
                Text(connected ? "원격 컴퓨터 연결됨" : "원격 컴퓨터 연결 끊김")
            } else {
                Image(systemName: "desktopcomputer").font(.system(size: 10))
                Text("이 Mac에서 실행")
            }
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

    private func errorBanner(_ message: String) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
            Text(message).font(.system(size: 12)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            Button { store.error = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel("오류 닫기")
        }.padding(12).background(Color.orange.opacity(0.07))
    }
}

private struct SidebarWidthKey: PreferenceKey {
    static var defaultValue: CGFloat = 252
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}
