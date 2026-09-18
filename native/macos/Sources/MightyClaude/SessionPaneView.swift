import SwiftUI
import AppKit
import UniformTypeIdentifiers
import MightyCore

struct SessionPaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    @ViewState private var composerFocused = false
    @ViewState private var composerInput = ComposerInputController()
    @ViewState private var editorHeight: CGFloat = 22
    @ViewState private var attachmentDropTargeted = false
    @ViewState private var stopping = false
    @ViewState private var paletteIndex = 0
    @ViewState private var paletteDismissedFor: String?

    private var running: Bool { session.status == "running" }
    private var active: Bool { store.snapshot.activeSessionId == session.id }
    private var localTerminal: Bool { store.usesLocalTerminal(session) }
    private var remoteCommand: Bool { session.kind == "shell" && store.snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote != nil }
    private var runtime: ProviderRuntime { store.providerRuntime(session.provider, workspaceId: session.workspaceId) }
    private var models: [ModelOption] { store.modelOptions(for: session) }
    private var effortLevels: [String] { runtime.capabilities.effort ? ProviderOptions.effortLevels(provider: session.provider, model: session.model, catalog: runtime.modelCatalog) : [] }
    private var draft: Binding<String> { Binding(get: { store.drafts[session.id] ?? "" }, set: { store.drafts[session.id] = $0 }) }
    private var attachments: [RunAttachment] { store.attachmentDrafts[session.id] ?? [] }
    private var importingAttachments: Bool { store.importingAttachments.contains(session.id) }
    private var blockedReason: String? {
        if let reason = store.runBlockedReason(session) { return reason }
        if session.kind != "shell", session.settings.effort != "default", !effortLevels.contains(session.settings.effort) {
            return "선택한 모델에서 저장된 사고 강도를 확인할 수 없습니다. Auto 또는 지원되는 강도를 선택하세요."
        }
        if session.kind != "shell" {
            if !attachments.isEmpty, !runtime.capabilities.attachments { return "이 실행기는 첨부 파일을 지원하지 않습니다. 원격 앱을 업데이트하거나 첨부를 제거하세요." }
            if !runtime.capabilities.permissionModes.contains(session.settings.permissionMode) { return "이 실행기가 저장된 권한 모드를 지원하지 않습니다. 작업 권한을 다시 선택하세요." }
            if session.settings.fastMode, !runtime.capabilities.fastMode { return "이 실행기는 Fast를 지원하지 않습니다. Fast를 끄고 실행하세요." }
            if session.settings.webSearch != "default", !runtime.capabilities.webSearch { return "이 실행기는 웹 검색 설정을 지원하지 않습니다. 더보기에서 미지원 설정을 해제하세요." }
            if session.settings.networkAccess, !runtime.capabilities.networkAccess { return "이 실행기는 Shell 네트워크 설정을 지원하지 않습니다. 더보기에서 미지원 설정을 해제하세요." }
        }
        return nil
    }
    private var selectedModelName: String { models.first(where: { $0.value == session.model })?.displayName ?? session.model }
    /// Sending stays possible while a run is busy: a local Claude turn takes
    /// the text immediately, other panes queue it for after the current request.
    private var canSend: Bool { !stopping && !importingAttachments && blockedReason == nil && (!draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || (session.kind != "shell" && !attachments.isEmpty)) }
    private var steers: Bool { store.canSteer(session) }
    /// The draft while it is a `/name` being typed or a built-in's `/name arg`,
    /// or nil when the palette should be closed.
    private var paletteDraft: String? {
        let text = draft.wrappedValue
        guard session.kind != "shell", paletteDismissedFor != text,
              SlashCommandCatalog.query(from: text) != nil || SlashCommandCatalog.argumentQuery(from: text) != nil else { return nil }
        return text
    }
    private var paletteCommands: [SlashCommand] {
        guard let paletteDraft else { return [] }
        return Array(store.slashPalette(for: session, draft: paletteDraft).prefix(60))
    }
    private var paletteVisible: Bool { !paletteCommands.isEmpty }
    private var queued: [QueuedInput] { store.queuedInputs[session.id] ?? [] }
    /// Present when a `statusLine` command produced something, or a
    /// workspace-level command is waiting to be allowed.
    private var statusLine: AppStore.StatusLineState? {
        guard let state = store.statusLines[session.id] else { return nil }
        let hasOutput = state.config != nil && (state.result?.lines.isEmpty == false || state.result?.error != nil)
        return hasOutput || state.untrusted != nil ? state : nil
    }
    private var settingsPopover: Binding<RunSession?> {
        Binding(get: { store.settingsSession?.id == session.id ? store.settingsSession : nil }, set: { value in
            if let value { store.settingsSession = value }
            else if store.settingsSession?.id == session.id { store.settingsSession = nil }
        })
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if localTerminal { LocalTerminalPane(session: session) }
            else {
                output
                ToolPermissionBar(sessionId: session.id)
                Divider()
                composer
            }
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 11))
        .overlay { RoundedRectangle(cornerRadius: 11).stroke(active ? Palette.accent.opacity(0.58) : Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .clipShape(RoundedRectangle(cornerRadius: 11))
        .onChange(of: composerFocused) { _, focused in
            // SwiftUI can deliver this after a tab switch detached the editor.
            // Only the editor that still owns native focus may select a pane.
            guard focused, store.snapshot.activeWorkspaceId == session.workspaceId,
                  store.layoutForWorkspace(session.workspaceId)?.group(containing: session.id)?.selectedSessionId == session.id,
                  let editor = composerInput.editor, let window = editor.window,
                  !editor.isHiddenOrHasHiddenAncestor, window.firstResponder === editor else { return }
            store.selectSession(session.id)
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(session.title)
    }

    private var header: some View {
        HStack(spacing: 8) {
            if session.kind == "claude", MightyGraphSupport.providers.contains(session.provider) {
                HStack(spacing: 2) {
                    agentModeButton("기본", mode: "default", symbol: "text.alignleft")
                    agentModeButton("마이티", mode: "mighty", symbol: "point.3.connected.trianglepath.dotted")
                }
                .padding(2).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
                if session.agentViewMode == "mighty", ["claude", "codex"].contains(session.provider) {
                    Button { store.openPluginBrowser(sessionID: session.id) } label: {
                        Image(systemName: "puzzlepiece.extension").font(.system(size: 12))
                            .frame(width: 26, height: 24)
                    }
                    .buttonStyle(.plain).disabled(store.hasModal)
                    .help("\(ProviderOptions.label(session.provider)) 플러그인 · 설치 목록 및 마켓플레이스")
                    .accessibilityLabel("\(ProviderOptions.label(session.provider)) 플러그인")
                    .accessibilityIdentifier("mighty-plugins-\(session.id)")
                }
            }
            if session.kind != "shell" { AgentSessionElapsedView(companion: store.companion, sessionID: session.id).fixedSize(horizontal: true, vertical: false) }
            Spacer(minLength: 2)
            HStack(spacing: 4) { StatusDot(status: session.status); Text(Palette.status(session.status)).font(.system(size: 9)) }.foregroundStyle(.secondary)
            Menu {
                Button("이름 변경…") { store.beginRenameSession(session.id) }.disabled(store.hasModal)
                Button(store.activePaneLayoutMode == "focus" ? "이전 배치로 보기" : "집중 보기") { store.togglePaneFocus(session.id) }
                if localTerminal {
                    Button("이전 명령 실행 기록…") { store.terminalHistorySession = session }
                }
                Button("실행 기록 복사") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(session.logs.map { "[\(role($0))] \($0.text)" }.joined(separator: "\n\n"), forType: .string)
                }.disabled(session.logs.isEmpty)
                if session.kind != "shell" {
                    Button("새 대화로 시작") { store.resetConversation(session.id) }.disabled(running || session.resumeId == nil)
                }
                Divider()
                Button("실행 창 닫기", role: .destructive) { store.closeSession(session.id) }
            } label: { Image(systemName: "ellipsis").font(.system(size: 12)) }
            .menuStyle(.borderlessButton).frame(width: 18).help("실행 창 메뉴")
        }
        .padding(.horizontal, 13).padding(.vertical, 7)
        .contentShape(Rectangle())
        .simultaneousGesture(TapGesture().onEnded { store.selectSession(session.id) })
    }

    private func agentModeButton(_ title: String, mode: String, symbol: String) -> some View {
        let selected = (session.agentViewMode ?? "default") == mode
        return Button {
            store.selectSession(session.id)
            store.setAgentViewMode(session.id, mode: mode)
        } label: {
            Label(title, systemImage: symbol)
                .font(.system(size: 10, weight: selected ? .semibold : .regular))
                .padding(.horizontal, 7).padding(.vertical, 4)
                .background(selected ? Palette.accent.opacity(0.18) : Color.clear, in: RoundedRectangle(cornerRadius: 4))
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(title) 모드")
        .accessibilityValue(selected ? "선택됨" : "선택 안 됨")
        .accessibilityIdentifier("agent-mode-\(mode)-\(session.id)")
    }

    private func modelMenu(maximumTextWidth: CGFloat) -> some View {
        Menu {
            Section("공급자") {
                ForEach(ProviderOptions.ids, id: \.self) { provider in
                    Button { store.selectSession(session.id); store.changeProvider(session.id, to: provider) } label: {
                        if provider == session.provider { Label(ProviderOptions.label(provider), systemImage: "checkmark") }
                        else { Text(ProviderOptions.label(provider)) }
                    }
                }
            }
            Section("모델") {
                ForEach(models) { model in
                    Button {
                        store.selectSession(session.id)
                        store.changeModel(session.id, to: model.value)
                    } label: {
                        if model.value == session.model { Label(model.displayName, systemImage: "checkmark") }
                        else { Text(model.displayName) }
                    }
                }
            }
        } label: {
            ComposerPill(title: selectedModelName, provider: session.provider, chevron: true, maximumTextWidth: maximumTextWidth)
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).disabled(running)
        .help("\(ProviderOptions.label(session.provider)) · \(selectedModelName)")
        .accessibilityLabel("모델 및 공급자").accessibilityValue("\(ProviderOptions.label(session.provider)), \(selectedModelName)")
        .accessibilityIdentifier("composer-model-\(session.id)")
    }

    private var effortOptions: some View {
        ForEach(["default"] + effortLevels, id: \.self) { effort in
            Button { updateSettings { $0.effort = effort } } label: {
                if effort == session.settings.effort { Label(effortLabel(effort), systemImage: "checkmark") }
                else { Text(effortLabel(effort)) }
            }
        }
    }

    private func effortMenu(compact: Bool) -> some View {
        Menu {
            effortOptions
        } label: {
            ComposerPill(title: effortLabel(session.settings.effort), systemImage: "brain", chevron: true, maximumTextWidth: 48, compact: compact)
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .accessibilityLabel("사고 강도").accessibilityValue(effortLabel(session.settings.effort))
        .accessibilityIdentifier("composer-effort-\(session.id)")
        .help(effortLevels.isEmpty ? "이 모델에서 지원되는 강도를 확인하지 못해 Auto를 사용합니다." : "사고 강도 · 다음 요청에 적용")
        .disabled(running || (effortLevels.isEmpty && session.settings.effort == "default"))
    }

    private var permissionOptions: some View {
        ForEach(["plan", "manual", "acceptEdits", "auto", "fullAccess"].filter { runtime.capabilities.permissionModes.contains($0) }, id: \.self) { mode in
            Button { updateSettings { $0.permissionMode = mode } } label: {
                if mode == session.settings.permissionMode { Label(permissionLabel(mode, provider: session.provider), systemImage: "checkmark") }
                else { Text(permissionLabel(mode, provider: session.provider)) }
            }.help(permissionDescription(mode, provider: session.provider))
        }
    }

    private func permissionMenu(compact: Bool) -> some View {
        Menu {
            permissionOptions
        } label: {
            ComposerPill(title: permissionLabel(session.settings.permissionMode, provider: session.provider), systemImage: session.settings.permissionMode == "fullAccess" ? "lock.open" : "shield.lefthalf.filled", active: session.settings.permissionMode == "fullAccess", chevron: true, maximumTextWidth: 90, compact: compact)
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).disabled(running)
        .help(permissionDescription(session.settings.permissionMode, provider: session.provider))
        .accessibilityLabel("작업 권한").accessibilityValue(permissionLabel(session.settings.permissionMode, provider: session.provider))
        .accessibilityIdentifier("composer-permission-\(session.id)")
    }

    private func fastButton(compact: Bool) -> some View {
        Button { updateSettings { $0.fastMode.toggle() } } label: {
            ComposerPill(title: "Fast", systemImage: session.settings.fastMode ? "bolt.fill" : "bolt", active: session.settings.fastMode, compact: compact)
        }
        .buttonStyle(.plain).disabled(running || (!runtime.capabilities.fastMode && !session.settings.fastMode))
        .help("Fast · 지원 모델·계정에서 사용 · 사용량 증가")
        .accessibilityLabel("Codex Fast").accessibilityValue(session.settings.fastMode ? "켬" : "끔")
        .accessibilityIdentifier("composer-fast-\(session.id)")
    }

    private var moreButton: some View {
        let modified = session.settings.webSearch != "default" || session.settings.networkAccess || session.settings.maxTurns != nil || session.settings.maxBudgetUsd != nil
        return Button { store.selectSession(session.id); store.settingsSession = session } label: {
            ComposerPill(title: "", systemImage: "ellipsis", active: modified, compact: true)
        }
        .buttonStyle(.plain).disabled(running).help("추가 실행 설정").accessibilityLabel("실행 설정")
        .accessibilityIdentifier("composer-more-\(session.id)")
    }

    private var showsEffort: Bool { runtime.capabilities.effort || session.settings.effort != "default" }
    private var showsFast: Bool { session.provider == "codex" && (runtime.capabilities.fastMode || session.settings.fastMode) }

    private var overflowMenu: some View {
        Menu {
            if showsEffort {
                Menu("사고 강도 · \(effortLabel(session.settings.effort))") { effortOptions }
                    .disabled(effortLevels.isEmpty && session.settings.effort == "default")
            }
            Menu("작업 권한 · \(permissionLabel(session.settings.permissionMode, provider: session.provider))") { permissionOptions }
            if showsFast {
                Button { updateSettings { $0.fastMode.toggle() } } label: {
                    Label(session.settings.fastMode ? "Fast 켜짐" : "Fast 끔", systemImage: session.settings.fastMode ? "checkmark" : "bolt")
                }.disabled(!runtime.capabilities.fastMode && !session.settings.fastMode)
                    .help("지원 모델·계정에서 사용 · 사용량 증가")
            }
            Divider()
            Button("추가 실행 설정…") { store.selectSession(session.id); store.settingsSession = session }
        } label: {
            ComposerPill(title: "", systemImage: "slider.horizontal.3", active: session.settings.permissionMode == "fullAccess" || session.settings.fastMode, compact: true)
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).disabled(running)
        .accessibilityLabel("실행 옵션").accessibilityValue("\(effortLabel(session.settings.effort)), \(permissionLabel(session.settings.permissionMode, provider: session.provider))")
        .accessibilityIdentifier("composer-options-\(session.id)")
        .help("사고 강도 · 작업 권한 · 추가 실행 설정")
    }

    private func updateSettings(_ update: (inout RunSettings) -> Void) {
        guard !running else { return }
        store.selectSession(session.id)
        var settings = session.settings
        update(&settings)
        if session.provider == "codex", settings.permissionMode != "acceptEdits" { settings.networkAccess = false }
        store.saveSettings(session.id, settings: settings)
    }

    private var output: some View {
        Group {
            if session.kind == "claude", MightyGraphSupport.providers.contains(session.provider), session.agentViewMode == "mighty" {
                MightyGraphView(sessionID: session.id, provider: session.provider, runs: session.mightyGraphRuns, draft: draft.wrappedValue, running: running,
                    blockSizes: session.graphBlockSizes ?? [:],
                    onSaveBlockSize: { id, size in store.setGraphBlockSize(session.id, nodeID: id, size: size) },
                    workspaceRoot: store.snapshot.workspaces.first { $0.id == session.workspaceId && $0.remote == nil }.map { URL(fileURLWithPath: $0.path, isDirectory: true) }) {
                    store.selectSession(session.id)
                }
            } else if session.logs.isEmpty {
                ScrollView { emptyOutput }
            } else {
                AgentTranscriptView(sessionId: session.id, provider: session.provider, running: running, entries: session.logs) {
                    store.selectSession(session.id)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.horizontal, 12)
    }

    private var emptyOutput: some View {
        VStack(alignment: .leading, spacing: 10) {
            Group {
                if session.kind == "shell" { Image(systemName: "terminal").font(.system(size: 24, weight: .light)) }
                else { ProviderIcon(provider: session.provider, size: 24, weight: .light) }
            }.foregroundStyle(Palette.accent.opacity(0.75)).padding(.bottom, 5)
            Text(session.kind == "shell" ? (remoteCommand ? "원격 작업 폴더에서 명령 실행" : "작업 폴더에서 명령 실행") : "\(ProviderOptions.label(session.provider))와 작업을 시작하세요")
                .font(.system(size: 16, weight: .medium))
            Text(session.kind == "shell" ? "명령마다 새 셸을 시작합니다. 대화형 프로그램과 비밀번호 입력은 지원하지 않습니다." : "프로젝트를 설명하거나, 수정할 내용을 입력하세요. 이 창의 대화는 다음 실행에서도 이어집니다.")
                .font(.system(size: 12)).foregroundStyle(.secondary).lineSpacing(4).fixedSize(horizontal: false, vertical: true)
        }.frame(maxWidth: .infinity, alignment: .leading).padding(24)
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: 9) {
            if !attachments.isEmpty {
                ScrollView(.horizontal) {
                    HStack(spacing: 7) {
                        ForEach(attachments) { attachment in
                            AttachmentChip(attachment: attachment) { store.removeAttachment(session.id, attachmentId: attachment.id) }
                        }
                    }
                }
                .scrollIndicators(.hidden).fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 10).padding(.top, 10)
                .accessibilityIdentifier("attachments-\(session.id)")
            }
            if !queued.isEmpty {
                QueuedInputsView(sessionID: session.id, items: queued, running: running || store.hasModal,
                                 onRemove: { store.removeQueuedInput(session.id, itemId: $0) }, onRunNext: { store.runNextQueuedInput(session.id) })
                    .padding(.horizontal, 10).padding(.top, attachments.isEmpty ? 10 : 6)
            }
            if paletteVisible {
                SlashCommandPalette(commands: paletteCommands, selectedIndex: min(paletteIndex, paletteCommands.count - 1),
                                    onSelect: { applyCompletion($0) }, onHover: { paletteIndex = $0 })
                    .padding(.horizontal, 10).padding(.top, 10)
            }
            NativeComposerEditor(text: draft, monospaced: session.kind == "shell", accessibilityLabel: session.kind == "shell" ? "실행할 명령" : "메시지", accessibilityIdentifier: "composer-\(session.id)", onFocusChange: { composerFocused = $0 }, onPasteAttachments: { board in store.pasteAttachments(session.id, from: board) }, inputController: composerInput)
                .onChange(of: paletteDraft) { _, text in
                    paletteIndex = 0
                    if text != nil { store.refreshSlashCommands(for: session) }
                }
                .frame(height: editorHeight)
                .background(TextEditorHeightReader(text: draft.wrappedValue, height: $editorHeight, canSubmit: canSend && active && !store.hasModal, onSubmit: { submitComposer(command: $0) }, onNavigationKey: paletteVisible && !store.hasModal ? handlePaletteKey : nil, placeholder: running ? (steers ? "Enter: 다음 요청으로 대기 · ⌘Enter: 실행 중인 작업에 바로 전달" : "다음 요청을 입력하세요 · 현재 작업이 끝나면 이어서 실행됩니다") : session.kind == "shell" ? "명령을 입력하세요…" : "요청할 작업을 입력하세요…").allowsHitTesting(false))
                .padding(.horizontal, 8).padding(.top, attachments.isEmpty && queued.isEmpty ? 9 : 0)
                .help(running ? (steers ? "Enter: 현재 작업이 끝난 뒤 실행 · ⌘Enter: 실행 중인 Claude에 바로 전달 · Shift+Enter: 줄바꿈" : "Enter: 현재 작업이 끝난 뒤 실행 · Shift+Enter: 줄바꿈") : "Enter 또는 ⌘Enter로 전송 · Shift+Enter로 줄바꿈")
            if importingAttachments {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini)
                    Text("첨부 파일 읽는 중…").font(.system(size: 11)).foregroundStyle(.secondary)
                }.padding(.horizontal, 12)
            }
            if let attachmentError = store.attachmentErrors[session.id] {
                HStack(alignment: .top, spacing: 7) {
                    Image(systemName: "exclamationmark.circle").foregroundStyle(Palette.accent)
                    Text(attachmentError).frame(maxWidth: .infinity, alignment: .leading).fixedSize(horizontal: false, vertical: true)
                    Button { store.attachmentErrors.removeValue(forKey: session.id) } label: { Image(systemName: "xmark").font(.system(size: 9)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).accessibilityLabel("첨부 안내 닫기")
                }
                .font(.system(size: 11)).foregroundStyle(.secondary).padding(.horizontal, 12)
                .accessibilityIdentifier("attachment-error-\(session.id)")
            }
            if let reason = blockedReason {
                HStack(alignment: .top, spacing: 7) {
                    Image(systemName: "info.circle").foregroundStyle(Palette.accent).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("입력 가능 · 실행 준비 필요").fontWeight(.medium)
                        Text(reason).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .font(.system(size: 11)).lineSpacing(2)
                .padding(.horizontal, 12)
                .accessibilityElement(children: .combine).accessibilityIdentifier("run-blocked-\(session.id)")
            }
            composerToolbar
            .padding(.horizontal, 10).padding(.bottom, statusLine == nil ? 10 : 4)
            if let statusLine {
                StatusLineView(sessionID: session.id, state: statusLine, padding: statusLine.config?.padding ?? 0,
                               onTrust: { store.trustStatusLine($0, sessionID: session.id) }, onDismiss: { store.dismissUntrustedStatusLine(sessionID: session.id) })
            }
        }
        .background(Color.primary.opacity(0.025), in: RoundedRectangle(cornerRadius: 15))
        .overlay { RoundedRectangle(cornerRadius: 15).stroke(attachmentDropTargeted || composerFocused ? Palette.accent.opacity(0.8) : Palette.border, lineWidth: attachmentDropTargeted || composerFocused ? 1.25 : 1).allowsHitTesting(false) }
        .onDrop(of: [.fileURL], isTargeted: $attachmentDropTargeted) { providers in
            store.importAttachments(session.id, providers: providers)
            return !providers.isEmpty
        }
        .padding(12)
        // The status line follows the CLI's cadence loosely: on every state
        // change, plus a slow tick so elapsed time and repo info stay fresh.
        .task(id: session.id) {
            let id = session.id
            while !Task.isCancelled {
                store.refreshStatusLine(sessionID: id)
                let busy = store.snapshot.sessions.first { $0.id == id }?.status == "running"
                try? await Task.sleep(for: .seconds(busy ? 10 : 60))
            }
        }
        .onChange(of: session.status) { _, _ in store.refreshStatusLine(sessionID: session.id) }
        .onChange(of: session.sessionUsage) { _, _ in store.refreshStatusLine(sessionID: session.id) }
        .onChange(of: session.resumeId) { _, _ in store.refreshStatusLine(sessionID: session.id) }
        .onChange(of: session.model) { _, _ in store.refreshStatusLine(sessionID: session.id) }
    }

    private var composerToolbar: some View {
        GeometryReader { geometry in
            let actionsWidth: CGFloat = (running && canSend ? 64 : 32) + (session.resumeId == nil ? 0 : 22) + (session.kind == "shell" ? 0 : 38)
            let width = max(0, geometry.size.width - actionsWidth - ComposerToolbarMetrics.spacing)
            let style = ComposerToolbarMetrics.style(width: width, model: selectedModelName, effort: showsEffort ? effortLabel(session.settings.effort) : nil, permission: permissionLabel(session.settings.permissionMode, provider: session.provider), fast: showsFast)
            HStack(alignment: .center, spacing: ComposerToolbarMetrics.spacing) {
                if session.kind != "shell" {
                    HStack(alignment: .center, spacing: ComposerToolbarMetrics.spacing) {
                        attachmentButton
                        modelMenu(maximumTextWidth: ComposerToolbarMetrics.modelTextWidth(style: style, width: width))
                        if style == .overflow { overflowMenu }
                        else {
                            if showsEffort { effortMenu(compact: style == .compact) }
                            permissionMenu(compact: style == .compact)
                            if showsFast { fastButton(compact: style == .compact) }
                            moreButton
                        }
                    }
                    .fixedSize(horizontal: true, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                } else {
                    Text(remoteCommand ? "원격 명령 · 요청마다 새 셸" : "명령 실행").font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                    Spacer(minLength: 0)
                }
                HStack(alignment: .center, spacing: 6) {
                    if session.resumeId != nil {
                        Image(systemName: "arrow.triangle.branch").font(.system(size: 11)).frame(width: 16, height: 32).foregroundStyle(.secondary).help("이전 대화를 이어갑니다.").accessibilityLabel("대화 이어짐")
                    }
                    if session.kind != "shell" { SessionContextButton(sessionID: session.id) }
                    if running {
                        // With text waiting, stop shrinks beside the send button
                        // so Enter and the arrow keep meaning "send".
                        let compact = canSend
                        Button(action: stopRun) {
                            Image(systemName: "stop.fill").font(.system(size: compact ? 10 : 12, weight: .semibold)).frame(width: compact ? 28 : 32, height: compact ? 28 : 32)
                                .foregroundStyle(compact ? Palette.accent : Palette.canvas)
                                .background(compact ? Palette.accent.opacity(0.14) : Palette.accent, in: Circle()).contentShape(Circle())
                        }
                        .buttonStyle(.plain).disabled(stopping)
                        .help(stopping ? "중지하는 중…" : "작업 중지")
                        .accessibilityLabel(stopping ? "중지하는 중" : "중지")
                        .accessibilityIdentifier("composer-stop-" + session.id)
                    }
                    if !running || canSend {
                        Button { submitComposer() } label: {
                            Image(systemName: running ? "text.badge.plus" : "arrow.up").font(.system(size: running ? 13 : 14, weight: .semibold)).frame(width: 32, height: 32)
                                .foregroundStyle(canSend ? Palette.canvas : Color.secondary)
                                .background(canSend ? Palette.accent : Color.primary.opacity(0.08), in: Circle()).contentShape(Circle())
                        }
                        .buttonStyle(.plain).disabled(!canSend)
                        .help(running ? (steers ? "다음 요청으로 대기 (Enter) · 실행 중인 Claude에 바로 전달하려면 ⌘Enter" : "현재 작업이 끝난 뒤 실행 (Enter)") : "보내기 (Enter 또는 ⌘Enter) · Shift+Enter로 줄바꿈")
                        .accessibilityLabel(running ? "대기열에 추가" : "보내기")
                        .accessibilityIdentifier("send-" + session.id)
                    }
                }.fixedSize(horizontal: true, vertical: true)
            }.frame(width: geometry.size.width, height: ComposerToolbarMetrics.height, alignment: .leading)
        }
        .frame(height: ComposerToolbarMetrics.height)
        .popover(item: settingsPopover, arrowEdge: .bottom) { selected in RunSettingsView(session: selected).environmentObject(store) }
    }

    /// Arrows move the highlight, Enter/Tab insert it, Esc closes the list for
    /// this draft. Returns false when the key should reach the editor.
    private func handlePaletteKey(_ key: ComposerNavigationKey) -> Bool {
        let commands = paletteCommands
        guard !commands.isEmpty else { return false }
        switch key {
        case .up: paletteIndex = (paletteIndex - 1 + commands.count) % commands.count
        case .down: paletteIndex = (paletteIndex + 1) % commands.count
        case .select: applyCompletion(commands[min(paletteIndex, commands.count - 1)])
        case .dismiss: paletteDismissedFor = draft.wrappedValue
        }
        return true
    }

    /// Built-ins run in the app and clear the draft; a built-in that takes
    /// an argument keeps the palette open for its choices; anything else is
    /// inserted as `/name ` for the CLI.
    private func applyCompletion(_ command: SlashCommand) {
        guard !store.hasModal else { return }
        if let action = command.action {
            guard store.performSlashAction(action, sessionID: session.id) else { return }
            paletteDismissedFor = nil
            composerInput.replaceDraft("")
            store.drafts[session.id] = ""
            return
        }
        let text = "/" + command.invocation + " "
        paletteDismissedFor = command.argument == nil ? text : nil
        composerInput.replaceDraft(text)
        store.drafts[session.id] = text
    }

    private func stopRun() {
        guard !stopping, running else { return }
        stopping = true
        Task {
            defer { stopping = false }
            await store.stop(session.id)
        }
    }

    /// A built-in typed out in full never reaches the CLI, where it would
    /// be prompt text: `/clear` runs, `/model opus` applies the choice, and a
    /// `/model` with no or an unknown argument reopens (or explains) the list.
    /// Enter sends now, or queues for the next request while a run is busy;
    /// ⌘Enter hands the text to the running Claude turn instead.
    private func submitComposer(command: Bool = false) {
        guard canSend, !store.hasModal else { return }
        if session.kind != "shell", attachments.isEmpty, let builtin = typedBuiltin {
            if let argument = builtin.argument {
                let query = SlashCommandCatalog.argumentQuery(from: draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines))?.query ?? ""
                let choices = store.slashPalette(for: session, draft: "/" + builtin.invocation + " ")
                if let exact = choices.first(where: { $0.invocation == builtin.invocation + " " + query }) { applyCompletion(exact); return }
                if query.isEmpty { applyCompletion(builtin); return }
                store.noteSlashMismatch(sessionID: session.id, command: builtin.invocation, argument: argument, query: query)
                composerInput.replaceDraft(""); store.drafts[session.id] = ""
                return
            }
            applyCompletion(builtin); return
        }
        composerInput.prepareForSubmission()
        store.submit(session.id, steering: command)
    }

    /// The built-in the trimmed draft names exactly (`/clear`, `/model`, `/model opus`).
    private var typedBuiltin: SlashCommand? {
        let text = draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let name = SlashCommandCatalog.query(from: text) ?? SlashCommandCatalog.argumentQuery(from: text)?.command
        return name.flatMap { name in SlashCommandCatalog.builtins(provider: session.provider).first { $0.invocation == name } }
    }

    private var attachmentButton: some View {
        Menu {
            Button("파일 선택…") { store.chooseAttachments(session.id) }
            Button("이미지 또는 파일 붙여넣기") { store.pasteAttachments(session.id) }
            if !attachments.isEmpty {
                Divider()
                Button("첨부 모두 제거") { store.discardAttachments(session.id) }
            }
        } label: {
            ComposerPill(title: "", systemImage: "paperclip", compact: true)
        } primaryAction: { store.chooseAttachments(session.id) }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .disabled(importingAttachments || !runtime.capabilities.attachments)
        .help(runtime.capabilities.attachments ? "파일 첨부 · 최대 8개, 파일당 5 MiB, 합계 8 MiB" : "이 실행기는 첨부 파일을 지원하지 않습니다.")
        .accessibilityLabel("파일 첨부").accessibilityIdentifier("attach-\(session.id)")
    }

    private func role(_ entry: LogEntry) -> String {
        switch entry.kind { case "user": return "나"; case "assistant": return ProviderOptions.label(entry.provider ?? session.provider); case "output": return "출력"; case "error": return "오류"; default: return "시스템" }
    }
}

func effortLabel(_ effort: String) -> String {
    switch effort { case "low": return "Low"; case "medium": return "Medium"; case "high": return "High"; case "xhigh": return "XHigh"; case "max": return "Max"; default: return "Auto" }
}
