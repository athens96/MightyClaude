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
    @ViewState private var guidedSelection = GuidedSelection()
    @ViewState private var styleCandidate: StyleApprovalCandidate?
    @ViewState private var paletteIndex = 0
    @ViewState private var paletteDismissedFor: String?
    @ViewState private var nextActionsMemo = NextActionsMemo()

    private var running: Bool { session.status == "running" || store.pendingRuns.contains(session.id) }
    private var active: Bool { store.snapshot.activeSessionId == session.id }
    private var localTerminal: Bool { store.usesLocalTerminal(session) }
    private var runtime: ProviderRuntime { store.providerRuntime(session.provider, workspaceId: session.workspaceId) }
    private var models: [ModelOption] { store.modelOptions(for: session) }
    private var effortLevels: [String] { runtime.capabilities.effort ? ProviderOptions.effortLevels(provider: session.provider, model: session.model, catalog: runtime.modelCatalog, registeredModels: (session.provider == "codex" ? store.snapshot.modelDefaults?.codex : store.snapshot.modelDefaults?.claude)?.registeredModels ?? []) : [] }
    private var draft: Binding<String> { Binding(get: { store.drafts[session.id] ?? "" }, set: { store.drafts[session.id] = $0 }) }
    private var attachments: [RunAttachment] { store.attachmentDrafts[session.id] ?? [] }
    private var importingAttachments: Bool { store.importingAttachments.contains(session.id) }
    private var blockedReason: String? {
        if let reason = store.runBlockedReason(session) { return reason }
        if session.kind != "shell" {
            if !attachments.isEmpty, !runtime.capabilities.attachments { return L("composer.blocked.attachmentsUnsupported") }
            if !runtime.capabilities.permissionModes.contains(session.settings.permissionMode) { return L("composer.blocked.permissionUnsupported") }
            if session.settings.fastMode, !runtime.capabilities.fastMode { return L("composer.blocked.fastUnsupported") }
            if session.settings.webSearch != "default", !runtime.capabilities.webSearch { return L("composer.blocked.webSearchUnsupported") }
            if session.settings.networkAccess, !runtime.capabilities.networkAccess { return L("composer.blocked.networkUnsupported") }
        }
        return nil
    }
    private var selectedModelName: String { store.modelLabel(for: session) }
    /// Sending stays possible while a run is busy: a local Claude turn takes
    /// the text immediately, other panes queue it for after the current request.
    private var canSend: Bool {
        guard !stopping, !importingAttachments, blockedReason == nil else { return false }
        if !draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || (session.kind != "shell" && !attachments.isEmpty) { return true }
        // Multi-select picks in the question panel are confirmed with Enter on an empty draft.
        return guided && store.guidedCanConfirm(session.id)
    }
    private var steers: Bool { store.canSteer(session) }
    /// Steer is offered only for a draft the running turn can take: files always queue.
    private var offersSteer: Bool { steers && attachments.isEmpty }
    /// The registered style this pane actually runs, if any.
    private var style: RegisteredStyle? { store.guidedStyle(session) }
    /// A guided style: the composer answers the agent's questions itself.
    private var guided: Bool { style != nil }
    private var styleHint: String { style?.manifest.subtitle ?? L("composer.style.free") }
    private var offersMightyStyle: Bool {
        session.kind == "claude" && session.provider == "claude" && session.agentViewMode == "mighty"
            && store.snapshot.workspaces.contains { $0.id == session.workspaceId }
    }
    private var guidedPhase: StylePhase? {
        guard let style else { return nil }
        if guidedSelection.startingNew, case .actions(let start, _, _) = style.manifest.rules.start { return style.manifest.phase(start) }
        // §1.16: history plus the pane's state sources, as the phone panel reads them.
        return style.evaluator.currentPhase(session: session, fileSourceStates: store.styleFileStates(style, for: session),
                                            planStage: store.stylePlanStage(style, for: session))
    }
    /// The precedence sweep of §1.10, built once per render rather than three
    /// times per visible request block — and only for a pane that really runs
    /// a style, because a plain CLI pane has no prefixes at all.
    private var styleTitles: StyleRequestTitles {
        StyleLaunchWiring.requestTitles(guidedStyle: style, runnable: store.styleRegistry.runnableInPrecedence(workspace: store.styleWorkspaceRef(session)))
    }
    /// The style the pane aimed at but has not been said yes to yet (§6.1).
    private var pendingStyle: RegisteredStyle? {
        guard let id = session.mightyStyle, let found = store.applicableStyles(session).first(where: { $0.id == id }) else { return nil }
        return found.approval == .pending ? found : nil
    }
    /// The prefix the Enter rule would send, drawn as a non-editable chip while
    /// the rule can actually fire (§1.6).
    private var armedPrefix: String? {
        guard let style, store.guidedQuestion(for: session.id) == nil else { return nil }
        return style.evaluator.enterArmedPrefix(draft: draft.wrappedValue, phase: guidedPhase, hasAttachments: !attachments.isEmpty,
                                                running: running, hasRequests: hasRequests, startingNew: guidedSelection.startingNew)
    }
    /// Whether this pane has ever sent a request, read without rebuilding a
    /// legacy pane's runs — the composer asks on every keystroke.
    private var hasRequests: Bool {
        if let runs = session.graphRuns { return !runs.isEmpty }
        return session.logs.contains { $0.kind == "user" }
    }
    private var composerPlaceholder: String {
        if let style {
            let answering = store.guidedQuestion(for: session.id) != nil
            let value = style.evaluator.placeholder(phase: guidedPhase, running: running, answering: answering)
            // An empty string is the manifest saying "the app's own default",
            // which lives here and not in the engine (§1.7).
            if !value.isEmpty { return value }
        }
        if store.pendingRuns.contains(session.id), session.status != "running" { return L("composer.placeholder.checkingModels") }
        if running { return steers ? L("composer.placeholder.busyMac") : L("composer.placeholder.busyQueue") }
        return session.kind == "shell" ? L("composer.placeholder.shell") : L("composer.placeholder.idle")
    }
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
    /// Delegation's notices or follow-ups held for this pane: rows that run first.
    private var held: [DelegationDeliveryItem] { store.delegationHeld[session.id] ?? [] }
    /// Present when a `statusLine` command produced something, or a
    /// workspace-level command is waiting to be allowed.
    /// Local Claude panes can show Claude's `statusLine`; the button flips
    /// the same preference as Settings › Display for every pane at once.
    private var showsStatusLineToggle: Bool {
        session.kind == "claude" && session.provider == "claude"
            && store.snapshot.workspaces.contains { $0.id == session.workspaceId }
    }
    private var statusLineToggle: some View {
        let on = store.statusLineEnabled
        return Button { store.statusLineEnabled.toggle() } label: {
            Image(systemName: on ? "rectangle.bottomthird.inset.filled" : "rectangle").font(.system(size: 12)).frame(width: 16, height: DesignMetrics.Layout.toolbar)
                .foregroundStyle(on ? Palette.accent : Palette.ink2)
        }
        .buttonStyle(.plain)
        .help(on ? L("composer.statusLine.hide") : L("composer.statusLine.show"))
        .accessibilityLabel(L("composer.statusLine.name")).accessibilityValue(on ? L("accessibility.on") : L("accessibility.off"))
        .accessibilityIdentifier("status-line-toggle-\(session.id)")
    }
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
            if localTerminal { LocalTerminalPane(session: session) }
            else {
                output
                if session.kind != "shell", !running, let next = nextActionsMemo.latest(in: session.logs) {
                    NextActionButtons(sessionID: session.id, entryID: next.entryId, actions: next.actions) { fillComposer($0) }
                        .disabled(store.hasModal)
                }
                // The diagram keeps answered plans beside their requests; the default
                // view and the timeline show them as this strip.
                if PlanCardSupport.showsHistoryStrip(mightyDiagram: showsMightyGraph && session.mightyViewMode == .diagram),
                   session.kind == "claude", let plans = session.planHistory, !plans.isEmpty {
                    PlanHistoryStrip(sessionId: session.id, records: plans)
                }
                // Background agents still running: in any view once the turn is
                // over, and in the Mighty view all along — unless the pane's style
                // draws its own task list or the user hid the line.
                if session.kind == "claude", let work = session.backgroundWork,
                   PlanCardSupport.showsBackgroundStrip(work, mighty: showsMightyGraph, styleDrawsTasks: PlanCardSupport.styleDrawsTasks(style?.manifest), enabled: store.showsBackgroundWork) {
                    BackgroundWorkStrip(sessionId: session.id, work: work)
                }
                ToolPermissionBar(sessionId: session.id)
                if let request = store.webOpenRequests.first(where: { $0.agentPaneId == session.id }) {
                    WebOpenChoicePanel(request: request).id(request.id)
                }
                composer
            }
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 11))
        .overlay { RoundedRectangle(cornerRadius: 11).stroke(active ? Palette.accent.opacity(0.58) : Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .clipShape(RoundedRectangle(cornerRadius: 11))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(session.title)
    }

    private func composerFocusChanged(_ focused: Bool) {
        composerFocused = focused
        // Every native focus notification matters, including repeated true
        // values after a different pane became active. A coalesced SwiftUI
        // Boolean transition cannot represent these responder events.
        guard focused, store.snapshot.activeWorkspaceId == session.workspaceId,
              store.layoutForWorkspace(session.workspaceId)?.group(containing: session.id)?.selectedSessionId == session.id,
              let editor = composerInput.editor, let window = editor.window,
              !editor.isHiddenOrHasHiddenAncestor, window.firstResponder === editor else { return }
        store.selectSession(session.id)
    }

    /// The pane's header: the shell keeps the slim ink bar; an agent pane gets one `Layout.paneHeader`
    /// line on the pane's own surface (status v2), the same in Default and Mighty.
    @ViewBuilder private var header: some View {
        if session.kind == "shell" {
            SlimPaneHeader(kind: SessionKind.shell, title: session.title,
                           subtitle: localTerminal ? L("dashboard.kind.shell") + " · " + L("phone.card.localTerminal") : L("dashboard.kind.shell"),
                           status: Palette.status(session.status)) { paneMenu(ink: Palette.onStatus) }
                .contentShape(Rectangle())
                .simultaneousGesture(TapGesture().onEnded { store.selectSession(session.id) })
        } else {
            agentHeader
        }
    }

    /// The card the header reads: the pane as the dashboard sees it, running while a
    /// request is still being started. The header never shows the last activity line,
    /// so the log scan behind it is skipped on every streamed redraw.
    private var headerCard: WorkDashboard.Card {
        var shown = session
        if running { shown.status = "running" }
        return WorkDashboard.card(shown, permissions: store.toolPermissions[session.id], activity: false)
    }

    /// Glyph, title, a small status word in its ink, then the figures in mono, which
    /// give way (faded at the right) before the title does; the Default | Mighty switch and
    /// the pane's buttons stay at the right.
    private var agentHeader: some View {
        let card = headerCard
        let usageModel = ModelLabel.reportedModel(session)
        // The pane's own choice when it names one, else what the CLI reported; both labelled.
        let modelText = card.model != nil ? store.modelLabel(for: session) : usageModel.map { ModelLabel.text($0) }
        let figures = PaneHero.figures(session)
        let ticking = running && figures.contains { if case .elapsed(let timing) = $0 { timing.finishedAt == nil } else { false } }
        return HStack(spacing: DesignMetrics.Spacing.sm) {
            StatusGlyph(tone: card.tone, kind: session.kind)
            Text(session.title).font(.system(size: 13, weight: .bold)).tracking(-0.1).foregroundStyle(Palette.ink)
                .lineLimit(1).truncationMode(.tail)
                .help(session.titleTooltip ?? session.title)
                .accessibilityAddTraits(.isHeader)
                .layoutPriority(1)
            // A turn that is over while its background agents still run says so.
            Text(card.attention.total > 0 ? L("phone.card.attention", ["count": "\(card.attention.total)"]) : (session.status == "running" ? PlanCardSupport.backgroundStatus(session.backgroundWork) : nil) ?? Palette.word(card.tone))
                .font(.system(size: 11.5, weight: .semibold)).foregroundStyle(Palette.text(card.tone)).lineLimit(1).fixedSize()
                .accessibilityIdentifier("pane-status-\(session.id)")
            Group {
                if ticking {
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        PaneHeaderFigures(sessionID: session.id, figures: figures, provider: session.provider, model: modelText, date: context.date)
                    }
                } else {
                    PaneHeaderFigures(sessionID: session.id, figures: figures, provider: session.provider, model: modelText, date: Date())
                }
            }
            .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
            HStack(spacing: DesignMetrics.Spacing.sm) { headerControls }
                .foregroundStyle(Palette.ink2)
                .padding(.leading, DesignMetrics.Spacing.xs)
                .fixedSize()
        }
        .padding(.leading, DesignMetrics.Inset.paneHeaderLeading).padding(.trailing, DesignMetrics.Inset.paneHeaderTrailing)
        .frame(height: DesignMetrics.Layout.paneHeader)
        .background(Palette.panel)
        .overlay(alignment: .bottom) { Rectangle().fill(Palette.border).frame(height: 1).allowsHitTesting(false) }
        .contentShape(Rectangle())
        .simultaneousGesture(TapGesture().onEnded { store.selectSession(session.id) })
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("pane-header-\(session.id)")
    }

    @ViewBuilder private var headerControls: some View {
        if session.kind == "claude", MightyGraphSupport.providers.contains(session.provider) {
            HStack(spacing: 2) {
                agentModeButton(L("graph.view.default"), mode: "default", symbol: "text.alignleft")
                agentModeButton(L("graph.view.mighty"), mode: "mighty", symbol: "point.3.connected.trianglepath.dotted")
            }
            .padding(2).background(Palette.segmentTrack, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .fixedSize()
            if session.agentViewMode == "mighty", ["claude", "codex"].contains(session.provider) {
                Button { store.openPluginBrowser(sessionID: session.id) } label: {
                    Image(systemName: "puzzlepiece.extension").font(.system(size: 12, weight: .semibold))
                        .frame(width: 22, height: 24).contentShape(Rectangle())
                }
                .buttonStyle(.plain).disabled(store.hasModal)
                .help(L("pane.plugins.help", ["provider": ProviderOptions.label(session.provider)]))
                .accessibilityLabel(L("plugins.titleTemplate", ["provider": ProviderOptions.label(session.provider)]))
                .accessibilityIdentifier("mighty-plugins-\(session.id)")
            }
        }
        if store.agentTerminals[session.id] != nil {
            Button { store.openAgentTerminalPane(session.id, select: true) } label: {
                Image(systemName: "terminal").font(.system(size: 12, weight: .semibold)).frame(width: 22, height: 24).contentShape(Rectangle())
            }
            .buttonStyle(.plain).disabled(store.hasModal)
            .help(L("agentTerminal.terminalPane.open")).accessibilityLabel(L("agentTerminal.terminalPane.open"))
            .accessibilityIdentifier("agent-terminal-open-\(session.id)")
        }
        paneMenu(ink: Palette.ink2)
    }

    private func paneMenu(ink: Color) -> some View {
        Menu {
            Button(L("menu.rename")) { store.beginRenameSession(session.id) }.disabled(store.hasModal)
            Button(store.activePaneLayoutMode == "focus" ? L("pane.menu.restoreLayout") : L("menu.focusPane")) { store.togglePaneFocus(session.id) }
            if localTerminal {
                Button(L("pane.menu.terminalHistory")) { store.terminalHistorySession = session }
            }
            Button(L("pane.menu.copyLog")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(session.logs.map { "[\(role($0))] \($0.text)" }.joined(separator: "\n\n"), forType: .string)
            }.disabled(session.logs.isEmpty)
            if session.kind != "shell" {
                Button(L("pane.menu.newConversation")) { store.resetConversation(session.id) }.disabled(running || session.resumeId == nil)
            }
            if session.kind == "claude" {
                Toggle(L("pane.menu.backgroundWork"), isOn: Binding(get: { store.showsBackgroundWork }, set: { store.setShowsBackgroundWork($0) }))
            }
            Divider()
            Button(L("menu.closePane"), role: .destructive) { store.closeSession(session.id) }
        } label: {
            Image(systemName: "ellipsis").font(.system(size: 13, weight: .bold)).foregroundStyle(ink)
                .frame(width: 22, height: 24).contentShape(Rectangle())
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
        .help(L("pane.menu.accessibility")).accessibilityLabel(L("pane.menu.accessibility"))
    }

    /// One side of the header's Default | Mighty switch: the chosen side is a raised chip on
    /// the quiet track, the other keeps the muted ink.
    private func agentModeButton(_ title: String, mode: String, symbol: String) -> some View {
        let selected = (session.agentViewMode ?? "default") == mode
        return Button {
            store.selectSession(session.id)
            store.setAgentViewMode(session.id, mode: mode)
        } label: {
            Label(title, systemImage: symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(selected ? Palette.ink : Palette.ink2)
                .padding(.horizontal, 8).frame(height: 20)
                // The chip's shadow sits on its shape only, never on the words.
                .background { if selected { RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Palette.segmentOn).shadow(color: .black.opacity(0.12), radius: 1, y: 1) } }
                .contentShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(L("graph.view.modeAccessibility", ["mode": title]))
        .accessibilityValue(selected ? L("accessibility.selected") : L("accessibility.notSelected"))
        .accessibilityIdentifier("agent-mode-\(mode)-\(session.id)")
    }

    private func modelMenu(maximumTextWidth: CGFloat) -> some View {
        Menu {
            Section(L("composer.label.runner")) {
                ForEach(ProviderOptions.ids, id: \.self) { provider in
                    Button { store.selectSession(session.id); store.changeProvider(session.id, to: provider) } label: {
                        if provider == session.provider { Label(ProviderOptions.betaTitle(provider, ProviderOptions.label(provider)), systemImage: "checkmark") }
                        else { Text(ProviderOptions.betaTitle(provider, ProviderOptions.label(provider))) }
                    }
                }
            }
            Button(store.isRefreshingModels(for: session) ? L("composer.model.refreshing") : L("composer.model.refresh")) {
                store.refreshModels(for: session.id, invalidate: true)
            }.disabled(store.isRefreshingModels(for: session))
            Section(L("composer.label.model")) {
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
        .help("\(ProviderOptions.betaTitle(session.provider, ProviderOptions.label(session.provider))) · \(selectedModelName)")
        .accessibilityLabel(L("composer.model.menuAccessibility")).accessibilityValue("\(ProviderOptions.label(session.provider))\(ProviderOptions.isBeta(session.provider) ? ", " + L("badge.betaAccessibility") : ""), \(selectedModelName)")
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
        .accessibilityLabel(L("composer.effort.label")).accessibilityValue(effortLabel(session.settings.effort))
        .accessibilityIdentifier("composer-effort-\(session.id)")
        .help(effortLevels.isEmpty ? L("composer.effort.unknownLevels") : L("composer.effort.help"))
        .disabled(running || (effortLevels.isEmpty && session.settings.effort == "default"))
    }

    private func permissionModeMenuLabel(_ mode: String) -> String {
        permissionLabel(mode, provider: session.provider)
    }

    private var permissionOptions: some View {
        ForEach(["plan", "manual", "acceptEdits", "auto", "onRequest", "fullAccess"].filter { store.permissionModes(for: session).contains($0) }, id: \.self) { mode in
            Button { updateSettings { $0.permissionMode = mode } } label: {
                if mode == session.settings.permissionMode { Label(permissionModeMenuLabel(mode), systemImage: "checkmark") }
                else { Text(permissionModeMenuLabel(mode)) }
            }.help(permissionDescription(mode, provider: session.provider))
        }
    }

    private func permissionMenu(compact: Bool) -> some View {
        Menu {
            permissionOptions
        } label: {
            ComposerPill(title: permissionModeMenuLabel(session.settings.permissionMode), systemImage: session.settings.permissionMode == "fullAccess" ? "lock.open" : "shield.lefthalf.filled", active: session.settings.permissionMode == "fullAccess", chevron: true, maximumTextWidth: 90, compact: compact)
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).disabled(running)
        .help(permissionDescription(session.settings.permissionMode, provider: session.provider))
        .accessibilityLabel(L("composer.label.permission")).accessibilityValue(permissionModeMenuLabel(session.settings.permissionMode))
        .accessibilityIdentifier("composer-permission-\(session.id)")
    }

    private func fastButton(compact: Bool) -> some View {
        Button { updateSettings { $0.fastMode.toggle() } } label: {
            ComposerPill(title: "Fast", systemImage: session.settings.fastMode ? "bolt.fill" : "bolt", active: session.settings.fastMode, compact: compact)
        }
        .buttonStyle(.plain).disabled(running || (!runtime.capabilities.fastMode && !session.settings.fastMode))
        .help(L("composer.fast.help"))
        .accessibilityLabel(L("composer.fast.accessibility")).accessibilityValue(session.settings.fastMode ? L("composer.fast.valueOn") : L("composer.fast.valueOff"))
        .accessibilityIdentifier("composer-fast-\(session.id)")
    }

    private var moreButton: some View {
        let modified = session.settings.webSearch != "default" || session.settings.networkAccess || session.settings.maxTurns != nil || session.settings.maxBudgetUsd != nil
        return Button { store.selectSession(session.id); store.settingsSession = session } label: {
            ComposerPill(title: "", systemImage: "ellipsis", active: modified, compact: true)
        }
        .buttonStyle(.plain).disabled(running).help(L("composer.runSettings.help")).accessibilityLabel(L("settings.run.title"))
        .accessibilityIdentifier("composer-more-\(session.id)")
    }

    private var showsEffort: Bool { runtime.capabilities.effort || session.settings.effort != "default" }
    private var showsFast: Bool { session.provider == "codex" && (runtime.capabilities.fastMode || session.settings.fastMode) }

    private var overflowMenu: some View {
        Menu {
            if showsEffort {
                Menu(L("composer.effort.menu", ["effort": effortLabel(session.settings.effort)])) { effortOptions }
                    .disabled(effortLevels.isEmpty && session.settings.effort == "default")
            }
            Menu(L("composer.permission.menu", ["permission": permissionModeMenuLabel(session.settings.permissionMode)])) { permissionOptions }
            if showsFast {
                Button { updateSettings { $0.fastMode.toggle() } } label: {
                    Label(session.settings.fastMode ? L("composer.fast.on") : L("composer.fast.off"), systemImage: session.settings.fastMode ? "checkmark" : "bolt")
                }.disabled(!runtime.capabilities.fastMode && !session.settings.fastMode)
                    .help(L("composer.fast.helpShort"))
            }
            Divider()
            Button(L("composer.runSettings.more")) { store.selectSession(session.id); store.settingsSession = session }
        } label: {
            ComposerPill(title: "", systemImage: "slider.horizontal.3", active: session.settings.permissionMode == "fullAccess" || session.settings.fastMode, compact: true)
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).disabled(running)
        .accessibilityLabel(L("composer.runOptions.accessibility")).accessibilityValue("\(effortLabel(session.settings.effort)), \(permissionModeMenuLabel(session.settings.permissionMode))")
        .accessibilityIdentifier("composer-options-\(session.id)")
        .help(L("composer.runOptions.help"))
    }

    private func updateSettings(_ update: (inout RunSettings) -> Void) {
        guard !running else { return }
        store.selectSession(session.id)
        var settings = session.settings
        update(&settings)
        if session.provider == "codex", !["acceptEdits", "onRequest"].contains(settings.permissionMode) { settings.networkAccess = false }
        store.saveSettings(session.id, settings: settings)
    }

    private var showsMightyGraph: Bool {
        session.kind == "claude" && MightyGraphSupport.providers.contains(session.provider) && session.agentViewMode == "mighty"
    }

    private var output: some View {
        Group {
            if showsMightyGraph {
                let retained = session.mightyGraphRuns
                let history = store.graphHistory(for: session, retained: retained)
                MightyGraphView(sessionID: session.id, provider: session.provider, runs: history.runs + retained, draft: draft.wrappedValue, running: running,
                    blockSizes: session.graphBlockSizes ?? [:],
                    onSaveBlockSize: { id, size in store.setGraphBlockSize(session.id, nodeID: id, size: size) },
                    workspaceRoot: store.snapshot.workspaces.first { $0.id == session.workspaceId }.map { URL(fileURLWithPath: $0.path, isDirectory: true) },
                    styleTitles: styleTitles,
                    styleName: style?.manifest.name, styleSource: style?.source, stylePhase: guidedPhase?.title,
                    catalog: store.providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog.models,
                    graphResultSize: session.graphResultSize,
                    onSaveResultSize: { size in store.setGraphResultSize(session.id, size: size) },
                    graphPlanSize: session.graphPlanSize,
                    onSavePlanSize: { size in store.setGraphPlanSize(session.id, size: size) },
                    onOpenURL: { url in await store.openInAgentBrowser(url, agentPaneId: session.id) },
                    // A pane with no request and no session to read has no history block.
                    retainedStart: history.runs.count, history: retained.isEmpty && session.resumeId == nil ? nil : history,
                    onLoadOlder: { store.loadOlderGraphHistory(session.id) },
                    viewMode: session.mightyViewMode,
                    onViewMode: { store.setGraphViewMode(session.id, mode: $0) },
                    planRequest: PlanCardSupport.pendingPlan(store.toolPermissions[session.id]),
                    planHistory: session.planHistory ?? [],
                    planCard: { [store] request, fit in
                        AnyView(PlanBody(request: request,
                                         count: (store.toolPermissions[session.id] ?? []).filter { $0.state == "pending" }.count,
                                         fills: true, onFitToWindow: fit)
                            .accessibilityElement(children: .contain).accessibilityIdentifier("plan-body-\(request.id)")
                            .environmentObject(store))
                    },
                    planRecordCard: { [store] record, expanded, toggle in
                        AnyView(PlanRecordView(record: record, expanded: expanded, onToggle: toggle, inDiagram: true).environmentObject(store))
                    }) {
                        store.selectSession(session.id)
                    }
            } else if session.logs.isEmpty {
                ScrollView { emptyOutput }
            } else {
                AgentTranscriptView(sessionId: session.id, provider: session.provider, running: running, entries: session.logs,
                                    onFocus: { store.selectSession(session.id) },
                                    imageRoot: store.snapshot.workspaces.first { $0.id == session.workspaceId }.map { URL(fileURLWithPath: $0.path, isDirectory: true) },
                                    cards: true)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.horizontal, DesignMetrics.Spacing.xs)
        // Concept D: the conversation sits on the raised grey, its replies on white cards.
        .background(showsMightyGraph ? Color.clear : Palette.raised)
    }

    private var emptyOutput: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            Group {
                if session.kind == "shell" { Image(systemName: "terminal").font(.system(size: 24, weight: .light)) }
                else { ProviderIcon(provider: session.provider, size: 24, weight: .light) }
            }.foregroundStyle(Palette.accent.opacity(0.75)).padding(.bottom, DesignMetrics.Spacing.xs)
            HStack(spacing: 8) {
                Text(session.kind == "shell" ? L("pane.empty.shellTitle") : L("pane.empty.agentTitle", ["provider": ProviderOptions.label(session.provider)]))
                    .font(.system(size: 16, weight: .medium))
                if session.kind != "shell" && ProviderOptions.isBeta(session.provider) { BetaBadge() }
            }
            Text(session.kind == "shell" ? L("pane.empty.shellBody") : L("pane.empty.agentBody"))
                .font(.system(size: 12)).foregroundStyle(Palette.ink2).lineSpacing(4).fixedSize(horizontal: false, vertical: true)
        }.frame(maxWidth: .infinity, alignment: .leading).padding(DesignMetrics.Spacing.xl)
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Inset.composerStack) {
            if offersMightyStyle {
                HStack(spacing: DesignMetrics.Spacing.md) {
                    MightyStylePicker(session: session, onApprove: { openApproval($0) })
                    Text(verbatim: styleHint).font(.system(size: 10)).foregroundStyle(.tertiary).lineLimit(1)
                    Spacer(minLength: 0)
                }.padding(.horizontal, DesignMetrics.Inset.composerInnerH).padding(.top, DesignMetrics.Spacing.sm)
                // One state, one message: a manifest that changed on disk is
                // both "re-choose" and "needs confirming", and the strip is the
                // one that says what to do about it.
                if let pending = pendingStyle {
                    GuidedApprovalStrip(name: pending.manifest.name, source: pending.source, sessionID: session.id) { openApproval(pending) }
                        .padding(.horizontal, DesignMetrics.Inset.composerInnerH)
                } else if store.styleNeedsRechoosing(session) {
                    Text(L("composer.style.changed"))
                        .font(.system(size: 10)).foregroundStyle(Palette.waitText).padding(.horizontal, DesignMetrics.Inset.composerInnerH)
                        .accessibilityIdentifier("mighty-style-changed-\(session.id)")
                }
                if let style {
                    GuidedPanel(session: session, style: style, running: running, phase: guidedPhase,
                                selection: $guidedSelection, onPrepare: { composerInput.prepareForSubmission() })
                }
            }
            if !attachments.isEmpty {
                ScrollView(.horizontal) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        ForEach(attachments) { attachment in
                            AttachmentChip(attachment: attachment) { store.removeAttachment(session.id, attachmentId: attachment.id) }
                        }
                    }
                }
                .scrollIndicators(.hidden).fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, DesignMetrics.Inset.composerInnerH).padding(.top, DesignMetrics.Spacing.sm)
                .accessibilityIdentifier("attachments-\(session.id)")
            }
            if !queued.isEmpty || !held.isEmpty {
                QueuedInputsView(sessionID: session.id, held: held, items: queued, running: running || store.hasModal,
                                 onRemove: { store.removeQueuedInput(session.id, itemId: $0) }, onRunNext: { store.runNextQueuedInput(session.id) },
                                 notice: BackgroundQueuePolicy.waitsOnBackground(work: session.backgroundWork, launchesInPlan: store.styleLaunchesInPlanMode(session),
                                                                                 queued: queued.count) ? L("queue.waitingOnBackground.mac") : nil)
                    .padding(.horizontal, DesignMetrics.Inset.composerInnerH).padding(.top, attachments.isEmpty ? DesignMetrics.Spacing.sm : 0)
            }
            if paletteVisible {
                SlashCommandPalette(commands: paletteCommands, selectedIndex: min(paletteIndex, paletteCommands.count - 1),
                                    onSelect: { applyCompletion($0) }, onHover: { paletteIndex = $0 })
                    .padding(.horizontal, DesignMetrics.Inset.composerInnerH).padding(.top, DesignMetrics.Spacing.sm)
            }
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                // The composer says which command Enter is about to send; the
                // manifest's own placeholder cannot be trusted to (§1.6).
                if let armedPrefix {
                    Text(verbatim: armedPrefix)
                        .font(.system(size: 10, design: .monospaced)).foregroundStyle(Palette.accent).lineLimit(1)
                        .padding(.horizontal, 6).padding(.vertical, 3)
                        .background(Palette.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 5))
                        .accessibilityLabel(L("composer.armedCommand", ["command": armedPrefix]))
                        .accessibilityIdentifier("mighty-enter-armed-\(session.id)")
                }
                NativeComposerEditor(text: draft, monospaced: session.kind == "shell", accessibilityLabel: session.kind == "shell" ? L("composer.input.command") : L("composer.input.message"), accessibilityIdentifier: "composer-\(session.id)", onFocusChange: { composerFocusChanged($0) }, onPasteAttachments: { board in store.pasteAttachments(session.id, from: board) }, inputController: composerInput, canSubmit: { canSend && !store.hasModal }, onSubmit: { submitComposer(command: $0) }, onNavigationKey: { key in paletteVisible && !store.hasModal ? handlePaletteKey(key) : false })
                    .onChange(of: paletteDraft) { _, text in
                        paletteIndex = 0
                        if text != nil { store.refreshSlashCommands(for: session) }
                    }
                    .frame(height: editorHeight)
                    .background(TextEditorHeightReader(inputController: composerInput, height: $editorHeight, placeholder: composerPlaceholder).allowsHitTesting(false))
            }
                .padding(.horizontal, DesignMetrics.Inset.composerInnerH).padding(.top, attachments.isEmpty && queued.isEmpty ? DesignMetrics.Spacing.sm : 0)
                .help(running ? (steers ? L("composer.inputHelp.steer") : L("composer.inputHelp.queue")) : L("composer.inputHelp.idle"))
            if importingAttachments {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    ProgressView().controlSize(.mini)
                    Text(L("composer.hint.attachmentsLoading")).font(.system(size: 11)).foregroundStyle(Palette.ink2)
                }.padding(.horizontal, DesignMetrics.Inset.composerInnerH)
            }
            if let attachmentError = store.attachmentErrors[session.id] {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "exclamationmark.circle").foregroundStyle(Palette.accent)
                    Text(attachmentError).frame(maxWidth: .infinity, alignment: .leading).fixedSize(horizontal: false, vertical: true)
                    Button { store.attachmentErrors.removeValue(forKey: session.id) } label: { Image(systemName: "xmark").font(.system(size: 9)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).accessibilityLabel(L("composer.attachment.dismissError"))
                }
                .font(.system(size: 11)).foregroundStyle(Palette.ink2).padding(.horizontal, DesignMetrics.Inset.composerInnerH)
                .accessibilityIdentifier("attachment-error-\(session.id)")
            }
            if let problem = store.inputMethodProblem, composerFocused {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "keyboard.badge.ellipsis").foregroundStyle(problem.recoveryState == .reconnected ? Palette.doneText : Palette.waitText).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(problem.recoveryState == .reconnected ? L("inputRecovery.noticeReconnected") : L("inputRecovery.noticeCheck")).fontWeight(.medium)
                        Text(problem.recoveryState.message)
                            .foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
                        if let file = problem.file { Text(L("inputRecovery.diagnosticsFile", ["path": file.path])).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary).textSelection(.enabled).lineLimit(1) }
                    }
                    Spacer(minLength: 4)
                    Button(L("menu.reconnectInputMethod")) { store.reconnectInputMethod(editor: composerInput.editor) }.controlSize(.small)
                        .disabled(problem.recoveryState.isPending)
                    Button { store.dismissInputMethodProblem() } label: { Image(systemName: "xmark").font(.system(size: 9)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).accessibilityLabel(L("inputRecovery.dismiss"))
                }
                .font(.system(size: 11)).lineSpacing(2).padding(.horizontal, DesignMetrics.Inset.composerInnerH)
                .accessibilityIdentifier("input-method-problem-\(session.id)")
            }
            if let provider = store.loginRequired[session.id] {
                CLILoginRecoveryCard(provider: provider, sessionID: session.id)
            }
            if store.backgroundUpdateHolds(session) {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    ProgressView().controlSize(.mini).padding(.top, 1)
                    Text(L("settings.cliUpdate.backgroundUpdateQueued", ["provider": ProviderOptions.label(session.provider)]))
                        .foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
                }
                .font(.system(size: 11)).lineSpacing(2).padding(.horizontal, DesignMetrics.Inset.composerInnerH)
                .accessibilityElement(children: .combine).accessibilityIdentifier("background-update-\(session.id)")
            }
            if let reason = blockedReason {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "info.circle").foregroundStyle(Palette.accent).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(L("composer.blocked.title")).fontWeight(.medium)
                        Text(reason).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .font(.system(size: 11)).lineSpacing(2)
                .padding(.horizontal, DesignMetrics.Inset.composerInnerH)
                .accessibilityElement(children: .combine).accessibilityIdentifier("run-blocked-\(session.id)")
                .background(AccessibilityStateProbe(identifier: "run-blocked-\(session.id)", enabled: true))
            }
            composerToolbar
            .padding(.horizontal, DesignMetrics.Inset.composerInnerH).padding(.bottom, statusLine == nil ? DesignMetrics.Inset.composerInnerB : DesignMetrics.Spacing.xs)
            if let statusLine {
                StatusLineView(sessionID: session.id, state: statusLine, padding: statusLine.config?.padding ?? 0,
                               onTrust: { store.trustStatusLine($0, sessionID: session.id) }, onDismiss: { store.dismissUntrustedStatusLine(sessionID: session.id) })
            }
        }
        // Concept D: a white rounded card with the D border; the accent ring while focused.
        // The shadow is the card shape's own, so the IME text view inside is never
        // drawn through a shadow pass.
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Palette.panel).shadow(color: .black.opacity(0.05), radius: 1, y: 1))
        .overlay { RoundedRectangle(cornerRadius: 16, style: .continuous).stroke(attachmentDropTargeted || composerFocused ? Palette.accent.opacity(0.8) : Palette.border, lineWidth: attachmentDropTargeted || composerFocused ? 1.5 : 1).allowsHitTesting(false) }
        .onDrop(of: [.fileURL], isTargeted: $attachmentDropTargeted) { providers in
            store.importAttachments(session.id, providers: providers)
            return !providers.isEmpty
        }
        .padding(DesignMetrics.Inset.composerOuter)
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
        // A workspace's own manifests are read when it first draws a pane.
        .task(id: session.workspaceId) { store.scanWorkspaceStyles(for: session) }
        // The style id alone does not change when a restored pane's registry
        // finally arrives, so the hash of the resolved style is watched too.
        .task(id: styleIdentity) { if let style { store.refreshStyle(style, for: session) } }
        // A run may have installed the plugin or written a new cycle folder.
        .onChange(of: running) { _, busy in if !busy, let style { store.refreshStyle(style, for: session) } }
        // The first request in the style is where its state sources start (§1.16).
        .onChange(of: session.mightyStyleSince) { _, _ in if let style { store.refreshStyleState(style, for: session) } }
        // A different style means a different catalogue: the group the user
        // was in and the reset chip both belong to the one they left.
        .onChange(of: session.mightyStyle) { _, _ in guidedSelection = GuidedSelection() }
        .sheet(item: $styleCandidate) { item in
            StyleApprovalSheet(candidate: item, onApproved: { approved in
                store.setMightyStyle(session.id, style: approved.id)
            }, onClose: { styleCandidate = nil }).environmentObject(store)
        }
    }

    /// Both halves of what the pane is actually running: the id it stored, and
    /// the bytes the registry resolved it to.
    private var styleIdentity: String {
        (session.mightyStyle ?? "") + "|" + (style?.hash ?? "")
    }

    /// A pending row in the picker opens the card; only saying yes there moves
    /// the pane onto that style (§4.5). The bytes are not handed along: this
    /// file is already on disk and approving it must not copy it anywhere.
    private func openApproval(_ style: RegisteredStyle) {
        styleCandidate = StyleApprovalCandidate(style: style, data: nil)
    }

    private var composerToolbar: some View {
        GeometryReader { geometry in
            // Stop beside steer and queue, or send alone; the resume mark; the context ring;
            // the status-line toggle, which shows wherever steer can.
            let height = ComposerToolbarMetrics.height, gap = ComposerToolbarMetrics.spacing
            let actionsWidth = (running && canSend ? Self.compactStop + gap + height + (offersSteer ? height + gap : 0) : height) + (session.resumeId == nil ? 0 : 16 + gap) + (session.kind == "shell" ? 0 : height + gap) + (showsStatusLineToggle ? 16 + gap : 0)
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
                    Text(L("composer.shell.title")).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                    Spacer(minLength: 0)
                }
                HStack(alignment: .center, spacing: ComposerToolbarMetrics.spacing) {
                    if session.resumeId != nil {
                        Image(systemName: "arrow.triangle.branch").font(.system(size: 11)).frame(width: 16, height: ComposerToolbarMetrics.height).foregroundStyle(Palette.ink2).help(L("composer.resume.help")).accessibilityLabel(L("composer.resume.name"))
                    }
                    if session.kind != "shell" { SessionContextButton(sessionID: session.id) }
                    if showsStatusLineToggle { statusLineToggle }
                    if running {
                        // With text waiting, stop shrinks beside the steer and queue
                        // buttons so Enter and ⌘Enter keep their round buttons.
                        let compact = canSend
                        // Concept D: stop is the red square, send the round run blue.
                        Button(action: stopRun) {
                            Image(systemName: "stop.fill").font(.system(size: compact ? 9 : 11, weight: .semibold)).frame(width: compact ? Self.compactStop : ComposerToolbarMetrics.height, height: compact ? Self.compactStop : ComposerToolbarMetrics.height)
                                .foregroundStyle(Palette.onStatus)
                                .background(Palette.err, in: RoundedRectangle(cornerRadius: compact ? 7 : 8, style: .continuous))
                                .contentShape(RoundedRectangle(cornerRadius: compact ? 7 : 8, style: .continuous))
                        }
                        .buttonStyle(.plain).disabled(stopping)
                        .help(stopping ? L("composer.stop.stopping") : L("composer.stop.help"))
                        .accessibilityLabel(stopping ? L("composer.stop.stoppingAccessibility") : L("phone.composer.stop"))
                        .accessibilityIdentifier("composer-stop-" + session.id)
                        .background(AccessibilityStateProbe(identifier: "composer-stop-" + session.id, enabled: !stopping))
                        // A busy pane offers the draft both ways, each the send button's
                        // round disc: steer (⌘Enter) hands it to the running turn, queue
                        // (Enter) waits for the next request, at the far right where send sits.
                        if canSend {
                            if offersSteer {
                                roundSendButton(systemImage: "bolt.fill", size: 12, help: L("queue.steerHintMac"), label: L("phone.composer.steer"), identifier: "composer-steer-" + session.id) { submitComposer(command: true) }
                            }
                            roundSendButton(systemImage: "text.badge.plus", size: 12, help: L("queue.addHint"), label: L("queue.add"), identifier: "send-" + session.id) { submitComposer() }
                        }
                    } else {
                        roundSendButton(systemImage: "arrow.up", size: 13, help: L("composer.send.helpMac"), label: L("composer.send.name"), identifier: "send-" + session.id) { submitComposer() }
                    }
                }.fixedSize(horizontal: true, vertical: true)
            }.frame(width: geometry.size.width, height: ComposerToolbarMetrics.height, alignment: .leading)
        }
        .frame(height: ComposerToolbarMetrics.height)
        .popover(item: settingsPopover, arrowEdge: .bottom) { selected in RunSettingsView(session: selected).environmentObject(store) }
    }

    /// The stop square beside steer and queue while text waits: 4pt under the toolbar's height.
    private static let compactStop = ComposerToolbarMetrics.height - 4

    /// The send button's look, shared by send, steer and queue: the toolbar-high
    /// circle in run blue while the draft can go, the track grey (and disabled) while not.
    private func roundSendButton(systemImage: String, size: CGFloat, help: String, label: String, identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage).font(.system(size: size, weight: .semibold)).frame(width: ComposerToolbarMetrics.height, height: ComposerToolbarMetrics.height)
                .foregroundStyle(canSend ? Palette.onStatus : Palette.ink2)
                .background(canSend ? Palette.run : Palette.track, in: Circle()).contentShape(Circle())
        }
        .buttonStyle(.plain).disabled(!canSend)
        .help(help)
        .accessibilityLabel(label)
        .accessibilityIdentifier(identifier)
        .background(AccessibilityStateProbe(identifier: identifier, enabled: canSend))
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

    /// A `next:` suggestion goes into the composer, not out: the user edits it
    /// and presses Enter. A draft already there is kept and the suggestion goes
    /// on a new line after it. The slash palette stays shut for the result.
    private func fillComposer(_ text: String) {
        guard !store.hasModal, let editor = composerInput.editor else { return }
        composerInput.fillDraft(text)
        let filled = editor.string
        paletteDismissedFor = filled
        store.drafts[session.id] = filled
        if let window = editor.window { window.makeFirstResponder(editor) }
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
        if let style {
            composerInput.prepareForSubmission()
            let text = draft.wrappedValue
            // A waiting question always wins; otherwise the style's own Enter
            // rule decides, all six conditions of §1.6 together.
            switch StyleComposer.enter(style.evaluator, draft: text, phase: guidedPhase,
                                       answering: store.guidedQuestion(for: session.id) != nil,
                                       hasAttachments: !attachments.isEmpty, running: running,
                                       hasRequests: hasRequests, startingNew: guidedSelection.startingNew) {
            case .answerQuestion:
                if store.guidedAnswer(session.id, text: text) { composerInput.replaceDraft(""); store.drafts[session.id] = "" }
                return
            case .rewrite(let actionId):
                guidedSelection.startingNew = false
                composerInput.replaceDraft("")
                store.sendStyleAction(session.id, actionId: actionId, text: text)
                return
            case .verbatim:
                break
            }
        }
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
            Button(L("composer.attachment.choose")) { store.chooseAttachments(session.id) }
            Button(L("composer.attachment.paste")) { store.pasteAttachments(session.id) }
            if !attachments.isEmpty {
                Divider()
                Button(L("composer.attachment.removeAll")) { store.discardAttachments(session.id) }
            }
        } label: {
            ComposerPill(title: "", systemImage: "paperclip", compact: true)
        } primaryAction: { store.chooseAttachments(session.id) }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .disabled(importingAttachments || !runtime.capabilities.attachments)
        .help(runtime.capabilities.attachments ? L("composer.attach.helpMac") : L("composer.attachment.unsupported"))
        .accessibilityLabel(L("phone.composer.attach")).accessibilityIdentifier("attach-\(session.id)")
    }

    private func role(_ entry: LogEntry) -> String {
        switch entry.kind { case "user": return L("pane.log.roleUser"); case "assistant": return ProviderOptions.label(entry.provider ?? session.provider); case "output": return L("composer.sessionInfo.output"); case "error": return L("session.state.error"); default: return L("pane.log.roleSystem") }
    }
}

func effortLabel(_ effort: String) -> String {
    switch effort { case "low": return "Low"; case "medium": return "Medium"; case "high": return "High"; case "xhigh": return "XHigh"; case "max": return "Max"; default: return "Auto" }
}

/// The last reply's parsed `next:` options, kept while that reply's id and
/// length stay the same so a render does not parse it again.
@MainActor
private final class NextActionsMemo {
    private var key: String?
    private var value: (entryId: String, actions: [NextAction])?

    func latest(in logs: [LogEntry]) -> (entryId: String, actions: [NextAction])? {
        guard let reply = logs.last(where: { $0.kind == "user" || $0.kind == "assistant" }), reply.kind == "assistant" else { return nil }
        let key = "\(reply.id)#\(reply.text.utf8.count)"
        if key != self.key { self.key = key; value = NextActions.latest(in: logs) }
        return value
    }
}
