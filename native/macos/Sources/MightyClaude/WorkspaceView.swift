import SwiftUI
import AppKit
import MightyCore

struct WorkspaceView: View {
    @EnvironmentObject private var store: AppStore
    @FocusState private var searchFocused: Bool
    /// ⌘K asked for the search while the sidebar was folded: it takes focus once the sidebar is back.
    @ViewState private var focusSearchOnUnfold = false
    @StateObject private var gitState = WorkspaceGitState()
    @StateObject private var accountUsage = AccountUsageStatusController()

    var body: some View {
        SidebarSplit(sidebar: sidebar, detail:
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
            .ignoresSafeArea(.container, edges: .top))
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
        .sheet(item: $store.planDocument) { PlanDocumentSheet(document: $0) { store.planDocument = nil } }
        .sheet(item: $store.pluginBrowser) { browser in
            ClaudePluginView(model: browser, onClose: { store.pluginBrowser = nil })
                .interactiveDismissDisabled(browser.isMutating)
                .onDisappear { Task { await browser.shutdown() } }
        }
        .onChange(of: store.focusSearch) { _, value in
            guard value else { return }
            store.focusSearch = false
            // The search lives in the sidebar: a folded sidebar opens first, and the field
            // takes focus once the unfold has brought it back (below).
            if store.sidebarCollapsed {
                focusSearchOnUnfold = true
                withAnimation(.easeInOut(duration: 0.2)) { store.setSidebarCollapsed(false) }
            } else { searchFocused = true }
        }
        .onChange(of: store.sidebarCollapsed) { _, collapsed in
            // Folded, the sidebar stays in the tree (`SidebarSplit`), so its search lets go of the keyboard.
            if collapsed { searchFocused = false; return }
            guard focusSearchOnUnfold else { return }
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(250))
                guard focusSearchOnUnfold else { return }
                focusSearchOnUnfold = false; searchFocused = true
            }
        }
        .confirmationDialog(L("workspace.remove.title"), isPresented: Binding(get: { store.pendingRemoval != nil }, set: { if !$0 { store.pendingRemoval = nil } }), titleVisibility: .visible) {
            if let workspace = store.pendingRemoval {
                Button(L("workspace.menu.remove"), role: .destructive) { store.removeWorkspace(workspace) }
                Button(L("resume.cancel"), role: .cancel) { store.pendingRemoval = nil }
            }
        } message: { Text(L("workspace.remove.message")) }
        // A child's discard from its card, naming the worktrees nested in its own that go with it.
        .confirmationDialog(L("delegation.discard.title"), isPresented: Binding(get: { store.pendingChildDiscard != nil }, set: { if !$0 { store.pendingChildDiscard = nil } }),
                            titleVisibility: .visible, presenting: store.pendingChildDiscard) { pending in
            Button(L("delegation.discard.confirm"), role: .destructive) { store.discardChildConfirmed(pending.id) }
            Button(L("resume.cancel"), role: .cancel) { store.pendingChildDiscard = nil }
        } message: { pending in Text(DelegationText.discardMessage(pending)) }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "magnifyingglass").foregroundStyle(Palette.sidebarInk2)
                TextField(L("sidebar.searchPlaceholder"), text: $store.search).textFieldStyle(.plain).font(.system(size: 12))
                    .focused($searchFocused)
                    .accessibilityLabel(L("sidebar.searchPlaceholder"))
            }
            .padding(DesignMetrics.Inset.sidebarSearch).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7)).padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)

            dashboardEntry.padding(.horizontal, DesignMetrics.Spacing.sm).padding(.top, DesignMetrics.Spacing.sm)

            HStack {
                Text(L("sidebar.workspacesHeader")).font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.sidebarInk2)
                Text("\(store.snapshot.workspaces.count)").font(.system(size: 10, design: .monospaced)).foregroundStyle(Palette.sidebarInk2)
                Spacer()
            }
            .padding(.horizontal, DesignMetrics.Inset.sidebarSectionH).padding(.top, DesignMetrics.Inset.sidebarSectionT).padding(.bottom, DesignMetrics.Inset.sidebarSectionB)

            ScrollView {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Inset.listGap) {
                    ForEach(store.filteredWorkspaces) { workspace in workspaceRow(workspace) }
                    if store.filteredWorkspaces.isEmpty {
                        Text(store.search.isEmpty ? L("dashboard.empty") : L("sidebar.noSearchResults"))
                            .font(.system(size: 12)).foregroundStyle(Palette.sidebarInk2).padding(DesignMetrics.Spacing.lg)
                    }
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm)
            }

            // While a workspace is listed, a folder opens from its "Add Pane" menu or ⌘O;
            // with none listed (none yet, or none matching the search) this is the
            // sidebar's way in.
            if store.filteredWorkspaces.isEmpty {
                Button { store.openWorkspace() } label: {
                    Label(L("sidebar.openFolder"), systemImage: "folder.badge.plus").font(.system(size: 12)).frame(maxWidth: .infinity, alignment: .leading).padding(DesignMetrics.Spacing.sm)
                }
                .buttonStyle(.plain).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7)).padding(.horizontal, DesignMetrics.Spacing.md).padding(.bottom, DesignMetrics.Spacing.md)
            }
            Divider()
            HStack(spacing: DesignMetrics.Spacing.md) {
                Group {
                    if let image = BrandAssets.icon { Image(nsImage: image).resizable().interpolation(.high).scaledToFit() }
                    else { Image(systemName: "sparkles").resizable().scaledToFit().foregroundStyle(Palette.sidebarAccent) }
                }.frame(width: 20, height: 20).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text("Mighty Claude").font(.system(size: 12, weight: .semibold)).lineLimit(1)
                    Text("v\(store.appVersion)").font(.system(size: 10, design: .monospaced))
                        .foregroundStyle(Palette.sidebarInk2).lineLimit(1)
                        .accessibilityIdentifier("sidebar-app-version")
                }
                Spacer()
                Button { store.toggleTheme() } label: { Image(systemName: store.snapshot.theme == "dark" ? "sun.max" : "moon") }
                    .buttonStyle(.plain).help(L("sidebar.toggleTheme")).accessibilityLabel(L("sidebar.toggleTheme"))
                Button { store.showSettings = true } label: { Image(systemName: "gearshape") }
                    .buttonStyle(.plain).help(L("settings.settingsWindowTitle")).accessibilityLabel(L("settings.settingsWindowTitle"))
            }.padding(.horizontal, DesignMetrics.Spacing.lg).padding(.vertical, DesignMetrics.Spacing.md)
        }
        // A solid D surface, never vibrancy, so the wallpaper never decides the sidebar's
        // contrast; it runs up under the hidden title bar and the traffic lights.
        .background(Palette.sidebar.ignoresSafeArea())
    }

    /// The dashboard entry at the top of the sidebar, with what waits on the user, what runs and
    /// what stopped on an error, as glyph counts.
    private var dashboardEntry: some View {
        let badges = WorkDashboard.badges(sessions: store.snapshot.sessions, permissions: store.toolPermissions)
        let selected = store.showsDashboard
        return Button { store.showsDashboard = true } label: {
            HStack(spacing: DesignMetrics.Spacing.md) {
                RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Palette.run).frame(width: 22, height: 22)
                    .overlay { Image(systemName: "square.grid.2x2.fill").font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.onStatus) }
                    .accessibilityHidden(true)
                Text(L("phone.dashboard.title")).font(.system(size: 13, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                Spacer(minLength: 0)
                StatusCounts(badges: badges)
            }
            .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Inset.sidebarRowV)
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
        return VStack(alignment: .leading, spacing: DesignMetrics.Inset.listGap) {
            HStack(spacing: 0) {
            Button { store.selectWorkspace(workspace.id) } label: {
                HStack(spacing: DesignMetrics.Spacing.md) {
                    Image(systemName: "folder").font(.system(size: 14)).foregroundStyle(selected ? Palette.sidebarAccent : Palette.sidebarInk2)
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        Text(workspace.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                    workspaceBadges(badges, workspace: workspace)
                }
                .padding(.leading, DesignMetrics.Spacing.sm).padding(.trailing, DesignMetrics.Spacing.xs).padding(.vertical, DesignMetrics.Inset.sidebarRowV).frame(minHeight: 24).frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            // The disclosure is separate from selection: opening or closing a
            // list never changes the active workspace, and selecting never closes others.
            Button { store.toggleWorkspaceExpanded(workspace.id) } label: {
                Image(systemName: expanded ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .semibold)).foregroundStyle(Palette.sidebarInk2)
                    .frame(width: 18, height: 18).contentShape(Rectangle())
            }
            .buttonStyle(.plain).padding(.trailing, DesignMetrics.Spacing.sm)
            .help(expanded ? L("workspace.list.collapseHelp") : L("workspace.list.expandHelp"))
            .accessibilityLabel(expanded ? L("workspace.collapseAccessibility", ["workspace": workspace.name]) : L("workspace.expandAccessibility", ["workspace": workspace.name]))
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
                // Delegated children under their parent pane; a closed parent's under its node.
                let tree = DelegationSidebar.tree(workspaceId: workspace.id, workspaces: store.snapshot.workspaces, sessions: store.snapshot.sessions, children: store.delegationChildren)
                ForEach(tree.top) { session in
                    paneRow(session)
                    ForEach(tree.children[session.id] ?? []) { child in childRow(child) }
                }
                ForEach(tree.parentClosed) { node in parentClosedNode(node) }
                workspaceAddMenu(workspace)
            }
        }.padding(.bottom, selected ? DesignMetrics.Spacing.sm : 0)
    }

    /// What in this workspace wants a look: requests waiting on the user, panes running
    /// (and not waiting), panes stopped by an error — each a glyph and a count.
    private func workspaceBadges(_ badges: WorkDashboard.Badges, workspace: Workspace) -> some View {
        StatusCounts(badges: badges, runningIdentifier: "workspace-running-\(workspace.id)")
    }

    /// A pane in the sidebar as a plain row on the sidebar surface: its status glyph,
    /// the title, and a muted line with the provider, the clock and the context. Only a
    /// pane waiting on the user carries a word on the right ("Question 1"); a delegated
    /// child's pane, one step in under its parent, always carries its state there.
    private func paneRow(_ session: RunSession, child: DelegationChildRow? = nil) -> some View {
        let card = WorkDashboard.card(session, permissions: store.toolPermissions[session.id])
        let active = !store.showsDashboard && session.id == store.snapshot.activeSessionId
        let beta = session.kind == "claude" && ProviderOptions.isBeta(session.provider)
        let childState = child.map { DelegationSidebar.shownState($0.state, asksHuman: card.attention.total > 0) }
        let status = childState.map(DelegationText.state) ?? DashboardText.status(card)
        let localTerminal = store.usesLocalTerminal(session)
        let settled = [.done, .stop, .idle].contains(card.tone)
        return sidebarListRow(nested: child != nil, Button { store.selectSession(session.id) } label: {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                StatusGlyph(tone: card.tone, kind: session.kind).padding(.top, 1.5).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
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
                if let childState {
                    childStateWord(childState)
                } else if card.attention.total > 0 {
                    Text(status).font(.system(size: 10.5, weight: .bold)).foregroundStyle(Palette.waitText).lineLimit(1).fixedSize()
                        .frame(minHeight: 17)
                }
            }
            .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Inset.paneRowV)
            .frame(maxWidth: .infinity, alignment: .leading)
            .modifier(SidebarRowHighlight(selected: active))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain))
        // The Button stays the accessibility element (its own press action); only its
        // label is replaced, so the meta line's ticking clock is not read out.
        .accessibilityLabel(child != nil
            ? L("delegation.child.accessibility", ["title": session.title, "state": status])
            : "\(session.title)\(beta ? ", " + L("badge.betaAccessibility") : ""), \(card.attention.total > 0 ? status : Palette.word(card.tone))")
        .accessibilityAddTraits(active ? .isSelected : [])
        .accessibilityIdentifier(card.isRunning ? "sidebar-running-\(session.id)" : "sidebar-status-\(session.id)")
        .contextMenu {
            Button(L("menu.rename")) { store.beginRenameSession(session.id) }
            Button(L("menu.closePane"), role: .destructive) { store.closeSession(session.id) }
            if let child {
                Divider()
                childCardMenu(child)
            }
        }
    }

    /// A row of a workspace's list (a pane, a delegated child or a 'parent closed' node),
    /// in past the folder icon; a `nested` row, a child under its parent, one step more.
    private func sidebarListRow(nested: Bool, _ row: some View) -> some View {
        row.padding(.leading, nested ? DesignMetrics.Spacing.xl : 0)
            .padding(.leading, 22).padding(.trailing, 2)
    }

    /// A delegated child's card under its parent: its pane's row while the pane is
    /// open, otherwise a row for the child the delegation file keeps, and below it
    /// the human's actions and the answer the last one got, when there is either.
    private func childRow(_ child: DelegationChildRow) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            if let session = store.snapshot.sessions.first(where: { $0.id == child.id }) {
                paneRow(session, child: child)
            } else {
                closedChildRow(child)
            }
            // A discarded child has no action left; its card stays a plain row.
            if !child.cardActions.isEmpty || store.delegationCardBusy.contains(child.id)
                || store.delegationCardNotes[child.id].flatMap({ DelegationText.note($0, child) }) != nil {
                childCardActions(child)
            }
        }
    }

    /// The card's actions as a line of small buttons (merge while reported, undo
    /// while its merge can be undone, discard until discarded), a busy line while
    /// one is going, and the last answer: a refusal with its reason, or a failure.
    private func childCardActions(_ child: DelegationChildRow) -> some View {
        let busy = store.delegationCardBusy.contains(child.id)
        let note = store.delegationCardNotes[child.id]
        return sidebarListRow(nested: true, VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            HStack(spacing: DesignMetrics.Spacing.md) {
                if busy {
                    ProgressView().controlSize(.mini).accessibilityHidden(true)
                    Text(L("delegation.card.working")).font(.system(size: 10.5)).foregroundStyle(Palette.sidebarInk2).lineLimit(1)
                } else {
                    ForEach(child.cardActions, id: \.self) { action in
                        Button { perform(action, child) } label: {
                            Text(DelegationText.action(action)).font(.system(size: 10.5, weight: .semibold))
                                .foregroundStyle(action == .discard ? Palette.sidebarInk2 : Palette.sidebarAccent).lineLimit(1).fixedSize()
                        }
                        .buttonStyle(.plain)
                        .help(DelegationText.actionHelp(action, child))
                        .accessibilityLabel(DelegationText.actionHelp(action, child))
                        .accessibilityIdentifier("child-card-\(action.rawValue)-\(child.id)")
                    }
                }
                Spacer(minLength: 0)
            }
            if let note, !busy, let line = DelegationText.note(note, child) {
                Text(line.text).font(.system(size: 10.5)).foregroundStyle(Palette.text(line.tone))
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    .accessibilityIdentifier("child-card-note-\(child.id)")
            }
        }
        .padding(.leading, DesignMetrics.Spacing.xl + DesignMetrics.Spacing.sm).padding(.trailing, DesignMetrics.Spacing.sm).padding(.bottom, DesignMetrics.Spacing.xs)
        .frame(maxWidth: .infinity, alignment: .leading))
    }

    /// The card's actions in a child row's context menu.
    @ViewBuilder private func childCardMenu(_ child: DelegationChildRow) -> some View {
        ForEach(child.cardActions, id: \.self) { action in
            Button(DelegationText.action(action), role: action == .discard ? .destructive : nil) { perform(action, child) }
                .disabled(store.delegationCardBusy.contains(child.id))
        }
    }

    private func perform(_ action: DelegationCardAction, _ child: DelegationChildRow) {
        switch action {
        case .merge: store.mergeChildFromCard(child.id)
        case .undo: store.undoChildMergeFromCard(child.id)
        case .discard: store.askToDiscardChild(child.id)
        }
    }

    /// A child with no open pane (its start failed, or its pane was closed): its
    /// task and its state, still listed under its parent.
    private func closedChildRow(_ child: DelegationChildRow) -> some View {
        let title = child.task ?? L("delegation.child.untitled")
        let status = DelegationText.state(child.state)
        return sidebarListRow(nested: true, HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            StatusGlyph(tone: DelegationSidebar.tone(child.state)).padding(.top, 1.5).accessibilityHidden(true)
            Text(title).font(.system(size: 12.5, weight: .medium)).foregroundStyle(Palette.sidebarInk2)
                .lineLimit(1).truncationMode(.tail).frame(minHeight: 17).help(title)
            Spacer(minLength: 0)
            childStateWord(child.state)
        }
        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Inset.paneRowV)
        .frame(maxWidth: .infinity, alignment: .leading))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(L("delegation.child.accessibility", ["title": title, "state": status]))
        .accessibilityIdentifier("sidebar-child-\(child.id)")
        .contentShape(Rectangle())
        .contextMenu { childCardMenu(child) }
    }

    /// The children of a closed parent pane, kept under a node of their own.
    private func parentClosedNode(_ node: DelegationSidebar.ParentClosed) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Inset.listGap) {
            sidebarListRow(nested: false, HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "rectangle.badge.xmark").font(.system(size: 11)).foregroundStyle(Palette.sidebarInk2).frame(width: 14)
                    .accessibilityHidden(true)
                Text(L("delegation.tree.parentClosed")).font(.system(size: 11.5, weight: .semibold)).foregroundStyle(Palette.sidebarInk2).lineLimit(1)
                if !node.parentBranch.isEmpty {
                    Text(verbatim: node.parentBranch).font(.system(size: 10.5, design: .monospaced)).foregroundStyle(Palette.sidebarInk2)
                        .lineLimit(1).truncationMode(.middle)
                }
                Spacer(minLength: 0)
                Text("\(node.children.count)").font(.system(size: 10, design: .monospaced)).foregroundStyle(Palette.sidebarInk2)
            }
            .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Inset.paneRowV)
            .frame(maxWidth: .infinity, alignment: .leading))
            .help(L("delegation.tree.parentClosedHelp"))
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(L("delegation.tree.parentClosedAccessibility", ["count": "\(node.children.count)"]))
            .accessibilityIdentifier("sidebar-parent-closed-\(node.id)")
            ForEach(node.children) { child in childRow(child) }
        }
    }

    /// A child's state in words, in its tone's ink (a quiet state in the sidebar's muted ink).
    private func childStateWord(_ state: ChildState) -> some View {
        let tone = DelegationSidebar.tone(state)
        return Text(DelegationText.state(state)).font(.system(size: 10.5, weight: .bold))
            .foregroundStyle(tone == .idle ? Palette.sidebarInk2 : Palette.text(tone)).lineLimit(1).fixedSize()
            .frame(minHeight: 17)
    }

    /// `[mark] Claude · 02:14 · Context 41%`: the provider's mark, in its brand colour, goes
    /// before its name; a pane that is not an agent's names its kind with no mark.
    private func paneMeta(_ card: WorkDashboard.Card, localTerminal: Bool, at date: Date) -> some View {
        let parts = WorkDashboard.sidebarMeta(card, now: date).compactMap { part -> Text? in
            let words: String? = switch part {
            case .provider: ProviderOptions.label(card.provider)
            case .elapsed: card.timing.map { DashboardText.clock($0, at: date) }
            case .context(let percent): L("phone.card.context", ["percent": "\(percent)"])
            case .reason(let text, let isTool): DashboardText.reason(text, isTool: isTool)
            case .age(let age): DashboardText.age(age)
            }
            guard let words else { return nil }
            if part == .provider, let mark = ProviderBadgeIcon.mark(provider: card.provider, font: .systemFont(ofSize: 11)) {
                return mark + Text(verbatim: " " + words)
            }
            return Text(verbatim: words)
        }
        let line = parts.first.map { first in parts.dropFirst().reduce(first) { $0 + Text(verbatim: " · ") + $1 } }
            ?? Text(verbatim: DashboardText.kindLine(card, localTerminal: localTerminal))
        return line
            .font(.system(size: 11)).monospacedDigit().lineLimit(1).truncationMode(.tail)
            .frame(minHeight: 15)
    }

    private func workspaceHeader(_ workspace: Workspace) -> some View {
        HStack(spacing: DesignMetrics.Spacing.lg) {
            HStack(spacing: DesignMetrics.Spacing.md) {
                SidebarToggleButton()
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    HStack(spacing: DesignMetrics.Spacing.md) {
                        Text(workspace.name).font(.system(size: 17, weight: .semibold)).lineLimit(1)
                        Spacer(minLength: 0)
                    }
                    .overlay { WorkspaceTitlebarRegion(enabled: !store.hasModal, rename: { store.beginRenameWorkspace(workspace.id) }) }
                    HStack(spacing: DesignMetrics.Spacing.md) {
                        Text(workspace.path).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                        if let git = gitState.info(for: workspace) { WorkspaceGitBadge(info: git) }
                        Spacer(minLength: 0)
                            .overlay { WorkspaceTitlebarRegion(enabled: !store.hasModal, rename: { store.beginRenameWorkspace(workspace.id) }) }
                    }
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
        }.leadingPastTrafficLights(DesignMetrics.Inset.workspaceHeaderH).padding(.trailing, DesignMetrics.Inset.workspaceHeaderH)
            .padding(.top, DesignMetrics.Inset.workspaceHeaderT).padding(.bottom, DesignMetrics.Inset.workspaceHeaderB)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("workspace-header-\(workspace.id)")
    }

    /// Last row of a workspace's pane list in the sidebar, laid out like the rows above it.
    private func workspaceAddMenu(_ workspace: Workspace) -> some View {
        Menu {
            WorkspaceAddMenuItems(store: store, workspace: workspace)
        } label: {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "plus").font(.system(size: 10, weight: .semibold)).frame(width: 12)
                Text(L("workspace.addPane")).font(.system(size: 11))
                Spacer(minLength: 0)
            }
            .foregroundStyle(Palette.sidebarAccent)
            .padding(.leading, 28).padding(.trailing, DesignMetrics.Spacing.lg).padding(.vertical, DesignMetrics.Inset.sidebarRowV).frame(minHeight: 22)
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
            } else { ProgressView(L("layout.loadingPanes")).frame(maxWidth: .infinity, maxHeight: .infinity) }
        }
    }

    private var welcome: some View {
        VStack(spacing: 18) {
            Spacer()
            Image(systemName: "rectangle.split.2x1").font(.system(size: 50, weight: .ultraLight)).foregroundStyle(Palette.accent)
            Text(L("layout.welcome.title")).font(.system(size: 27, weight: .semibold))
            Text(L("layout.welcome.body"))
                .font(.system(size: 14)).foregroundStyle(.secondary).multilineTextAlignment(.center).lineSpacing(5)
            HStack(spacing: 10) {
                Button { store.openWorkspace() } label: { Label(L("layout.welcome.openProject"), systemImage: "folder.badge.plus").padding(.horizontal, 10).padding(.vertical, 5) }.buttonStyle(.borderedProminent)
            }.padding(.top, 8)
            Text(L("layout.welcome.shortcutsMac")).font(.system(size: 11)).foregroundStyle(.tertiary).padding(.top, 10)
            Spacer()
            Text(L("layout.welcome.footerMac")).font(.system(size: 11)).foregroundStyle(.tertiary).padding(.bottom, 25)
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
        .overlay(alignment: .topLeading) { SidebarToggleButton().leadingPastTrafficLights(DesignMetrics.Inset.workspaceHeaderH).padding(.top, DesignMetrics.Inset.workspaceHeaderT) }
    }

    private var emptyPanes: some View {
        ContentUnavailableView {
            Label(L("layout.empty.addPane"), systemImage: "square.stack.3d.up")
        } description: { Text(L("layout.empty.addPaneDetail")) }
        actions: { Button(L("menu.newClaudePane")) { store.addSession(kind: "claude") }.buttonStyle(.borderedProminent) }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var statusBar: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "desktopcomputer").font(.system(size: 10))
            Text(L("window.status.thisMac"))
            Spacer()
            Text(L("window.status.paneCount", ["count": "\(store.activeSessions.count)"]))
            Text("·").padding(.horizontal, 3)
            Text(L("window.status.runningCount", ["count": "\(WorkDashboard.stats(sessions: store.snapshot.sessions, permissions: store.toolPermissions).running)"]))
            if let version = store.appUpdate.availability?.manifest.version, [.available, .ready].contains(store.appUpdate.phase) {
                Button { store.showSettings = true } label: {
                    Label(L("window.status.updateBadge", ["version": version]), systemImage: "arrow.down.circle.fill").font(.system(size: 10, weight: .medium)).foregroundStyle(Palette.accent)
                }
                .buttonStyle(.plain).help(L("window.status.updateBadgeHelp")).accessibilityIdentifier("app-update-badge")
            }
            Divider().frame(height: 12).padding(.horizontal, 4)
            StatusBarUsageView(controller: accountUsage)
            AgentStatusControls(companion: store.companion)
        }
        .font(.system(size: 10)).foregroundStyle(.secondary).padding(.horizontal, DesignMetrics.Inset.statusBarH).padding(.vertical, DesignMetrics.Inset.statusBarV)
        .background(Palette.subtle).overlay(alignment: .top) { Divider() }
    }

    private func resourceWarningBanner(_ message: String) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(Palette.waitText)
            Text(message).font(.system(size: 12)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            Button { store.resourceWarning = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel(L("window.warning.dismiss"))
        }.padding([.vertical, .trailing], DesignMetrics.Spacing.md).leadingPastTrafficLights(DesignMetrics.Spacing.md).background(Palette.waitSoft)
    }

    private func errorBanner(_ message: String) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(Palette.errText)
            Text(message).font(.system(size: 12)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            Button { store.error = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel(L("window.error.dismiss"))
        }.padding([.vertical, .trailing], DesignMetrics.Spacing.md).leadingPastTrafficLights(DesignMetrics.Spacing.md).background(Palette.errSoft)
    }
}

/// Folds the left sidebar away or brings it back (⌃⌘S). It sits at the content's
/// top-leading edge in every header, so it stays in reach with the sidebar gone.
struct SidebarToggleButton: View {
    @EnvironmentObject private var store: AppStore

    /// With the sidebar folded the window's traffic lights sit over the content's
    /// top-leading corner; a header's leading edge moves past them.
    static let trafficLightInset: CGFloat = 76

    var body: some View {
        let label = L(store.sidebarCollapsed ? "sidebar.expand" : "sidebar.collapse")
        Button { withAnimation(.easeInOut(duration: 0.2)) { store.toggleSidebar() } } label: {
            Image(systemName: "sidebar.left").font(.system(size: 13)).frame(width: 26, height: 24).contentShape(Rectangle())
        }
        .buttonStyle(.plain).foregroundStyle(.secondary)
        .help(label + " (⌃⌘S)")
        .accessibilityLabel(label)
        .accessibilityIdentifier("sidebar-toggle")
    }
}

/// Leading padding `base`, widened to clear the window's traffic lights while the sidebar is folded
/// (they then sit over the content's top-leading corner).
private struct TrafficLightClearance: ViewModifier {
    @EnvironmentObject private var store: AppStore
    let base: CGFloat

    func body(content: Content) -> some View {
        content.padding(.leading, store.sidebarCollapsed ? max(base, SidebarToggleButton.trafficLightInset) : base)
    }
}

extension View {
    func leadingPastTrafficLights(_ base: CGFloat) -> some View { modifier(TrafficLightClearance(base: base)) }
}

/// The sidebar at its saved width, its border handle, and the content beside it. Folded, the
/// sidebar slides out past the window's leading edge and the content takes the window; it stays
/// in the tree, so its scroll position is there when it comes back, but is hidden from VoiceOver,
/// the pointer and the keyboard meanwhile. The handle leaves. While the border is dragged the
/// width lives here, so only this view redraws per step; the saved width changes once, at the end.
private struct SidebarSplit<Sidebar: View, Detail: View>: View {
    @EnvironmentObject private var store: AppStore
    let sidebar: Sidebar
    let detail: Detail
    @ViewState private var liveWidth: Double?

    var body: some View {
        let width = CGFloat(liveWidth ?? store.snapshot.sidebarWidth)
        let collapsed = store.sidebarCollapsed
        HStack(spacing: 0) {
            // Laid out at its own width and pinned to the trailing edge of a column that folds
            // to zero, the sidebar lies wholly past the window's edge when folded. Not clipped,
            // so its surface still runs up under the hidden title bar.
            sidebar.frame(width: width)
                .frame(width: collapsed ? 0 : width, alignment: .trailing)
                .accessibilityHidden(collapsed)
                .allowsHitTesting(!collapsed)
                .disabled(collapsed)
            detail.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .overlay(alignment: .leading) {
            if !collapsed {
                SidebarResizeHandle(width: width, liveWidth: $liveWidth)
                    .padding(.leading, max(0, width - SidebarResizeHandle.hitWidth / 2))
                    // Full height, like the sidebar surface it resizes.
                    .ignoresSafeArea(.container, edges: .top)
                    .transition(.move(edge: .leading))
            }
        }
    }
}

/// The sidebar's trailing border, drawn as a hairline over a hit strip that straddles it, like the pane
/// dock's dividers (`PaneDockView`): dragging resizes the sidebar between `SidebarFold`'s bounds, dragging
/// it narrower than the fold threshold folds it away (keeping the saved width), a double-click puts the
/// default width back. The width is saved only when a drag ends. It takes keyboard focus, and the left
/// and right arrows step the width like the accessibility action.
private struct SidebarResizeHandle: View {
    static let hitWidth: CGFloat = 6
    @EnvironmentObject private var store: AppStore
    let width: CGFloat
    @Binding var liveWidth: Double?
    /// The sidebar's width when the current drag began; nil while not dragging.
    @ViewState private var origin: Double?
    @ViewState private var hovered = false
    @FocusState private var focused: Bool

    var body: some View {
        let active = hovered || origin != nil || focused
        Rectangle().fill(Color.clear)
            .frame(width: Self.hitWidth)
            .frame(maxHeight: .infinity)
            .overlay { Rectangle().fill(active ? Palette.accent : Palette.border).frame(width: active ? 2 : 1) }
            .contentShape(Rectangle())
            .onHover { hovering in
                hovered = hovering
                // A drag keeps its cursor when the pointer runs ahead of the border.
                if hovering || origin == nil { (hovering ? NSCursor.resizeLeftRight : NSCursor.arrow).set() }
            }
            // A press that wobbles a point or two stays a click, so a double-click still resets.
            .gesture(DragGesture(minimumDistance: 3, coordinateSpace: .global)
                .onChanged { value in
                    guard !store.sidebarCollapsed else { return }
                    let start = origin ?? Double(width)
                    origin = start
                    switch SidebarFold.drag(startWidth: start, translation: Double(value.translation.width)) {
                    case .width(let next): liveWidth = next
                    case .fold:
                        origin = nil; liveWidth = nil
                        NSCursor.arrow.set()
                        withAnimation(.easeInOut(duration: 0.2)) { store.applySidebarDrag(.fold) }
                    }
                }
                .onEnded { value in
                    guard let start = origin else { return }
                    origin = nil; liveWidth = nil
                    if !hovered { NSCursor.arrow.set() }
                    store.applySidebarDrag(SidebarFold.drag(startWidth: start, translation: Double(value.translation.width)))
                })
            .onTapGesture(count: 2) { store.resetSidebarWidth() }
            // Folding (or the drag folding it) takes the handle away under the pointer; no
            // exit hover comes then, so the resize cursor would stay.
            .onDisappear { if hovered || origin != nil { NSCursor.arrow.set() } }
            .focusable()
            .focused($focused)
            // The accent line is the focus mark; the system ring would not fit a 6 pt strip.
            .focusEffectDisabled()
            .onKeyPress(.leftArrow) { store.stepSidebarWidth(-1); return .handled }
            .onKeyPress(.rightArrow) { store.stepSidebarWidth(1); return .handled }
            .accessibilityElement()
            .accessibilityLabel(L("sidebar.resize"))
            .accessibilityValue(L("sidebar.resizeValue", ["value": "\(Int(width.rounded()))"]))
            .accessibilityAdjustableAction { direction in
                switch direction {
                case .increment: store.stepSidebarWidth(1)
                case .decrement: store.stepSidebarWidth(-1)
                @unknown default: break
                }
            }
            .accessibilityIdentifier("sidebar-resize")
            .help(L("sidebar.resizeTooltip"))
    }
}

/// The "Add Pane" menu's items, shared by the sidebar's last row and the dashboard's
/// workspace header: new agent panes (Claude and Codex then ask whether to continue
/// an earlier session, `AppStore.addAgentPane`), a terminal, a browser tab, and
/// another project folder.
struct WorkspaceAddMenuItems: View {
    let store: AppStore
    let workspace: Workspace

    var body: some View {
        ForEach(ProviderOptions.ids, id: \.self) { provider in
            Button { store.addAgentPane(provider: provider, workspaceId: workspace.id) } label: {
                Label { Text(ProviderOptions.betaTitle(provider, L("workspace.newAgentPane", ["provider": ProviderOptions.label(provider)]))) } icon: {
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
/// counts what has settled and names each state: "? 1 waiting  ✻ 1 running  ✓ 1 done  ○ 1 ready".
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

/// Words for delegated children in the sidebar.
enum DelegationText {
    /// A child's state (`delegation.child.state.*`).
    static func state(_ state: ChildState) -> String {
        switch state {
        case .creating: L("delegation.child.state.creating")
        case .running: L("delegation.child.state.running")
        case .waiting: L("delegation.child.state.waiting")
        case .reported: L("delegation.child.state.reported")
        case .merged: L("delegation.child.state.merged")
        case .ended: L("delegation.child.state.ended")
        case .interrupted: L("delegation.child.state.interrupted")
        case .failed: L("delegation.child.state.failed")
        case .closed: L("delegation.child.state.closed")
        case .discarded: L("delegation.child.state.discarded")
        }
    }

    /// A card action's button (`delegation.card.*`).
    static func action(_ action: DelegationCardAction) -> String {
        switch action {
        case .merge: L("delegation.card.merge")
        case .undo: L("delegation.card.undo")
        case .discard: L("delegation.card.discard")
        }
    }

    /// What a card action does to this child, for its help and accessibility.
    static func actionHelp(_ action: DelegationCardAction, _ child: DelegationChildRow) -> String {
        let branch = child.parentBranch.isEmpty ? L("delegation.card.parentBranch") : child.parentBranch
        return switch action {
        case .merge: L("delegation.card.mergeHelp", ["branch": branch])
        case .undo: L("delegation.card.undoHelp", ["branch": branch])
        case .discard: L("delegation.card.discardHelp", ["branch": ChildRecord.branchName(for: child.id)])
        }
    }

    /// The card's line for its last answer, in its tone; nil when there is nothing to say.
    static func note(_ note: DelegationCardNote, _ child: DelegationChildRow) -> (text: String, tone: DesignTone)? {
        let branch = child.parentBranch.isEmpty ? L("delegation.card.parentBranch") : child.parentBranch
        switch (note.action, note.result) {
        case (.merge, .done): return (L("delegation.card.merged", ["branch": branch]), .done)
        case (.undo, .done): return (L("delegation.card.undone", ["branch": branch]), .done)
        case (.discard, .done): return nil
        case (.merge, .refused(let reason)): return (L("delegation.card.mergeRefused", ["reason": Self.reason(reason)]), .wait)
        case (.undo, .refused(let reason)): return (L("delegation.card.undoRefused", ["reason": Self.reason(reason)]), .wait)
        case (.discard, .refused(let reason)): return (L("delegation.card.discardFailed", ["error": Self.reason(reason)]), .err)
        case (.merge, .failed(let message)): return (L("delegation.card.mergeFailed", ["error": message]), .err)
        case (.undo, .failed(let message)): return (L("delegation.card.undoFailed", ["error": message]), .err)
        case (.discard, .failed(let message)): return (L("delegation.card.discardFailed", ["error": message]), .err)
        }
    }

    /// Why a card's merge or undo was refused, ending in its reason code.
    static func reason(_ reason: DelegationReasonCode) -> String {
        switch reason {
        case .notReported: L("delegation.reason.notReported")
        case .branchNotCheckedOut: L("delegation.reason.branchNotCheckedOut")
        case .trackedChanges: L("delegation.reason.trackedChanges")
        case .mergeConflict: L("delegation.reason.mergeConflict")
        case .undoParentMoved: L("delegation.reason.undoParentMoved")
        case .parentBusy: L("delegation.reason.parentBusy")
        default: L("delegation.reason.other", ["code": reason.rawValue])
        }
    }

    /// The discard confirmation's text: what goes, and the nested worktrees that go with it.
    static func discardMessage(_ pending: PendingChildDiscard) -> String {
        let nested: String = switch pending.nested {
        case nil: L("delegation.discard.nestedUnknown")
        case let paths? where paths.isEmpty: L("delegation.discard.noNested")
        case let paths?: L("delegation.discard.nested", ["count": "\(paths.count)", "paths": paths.joined(separator: "\n")])
        }
        return L("delegation.discard.message", ["title": pending.title, "branch": pending.branch]) + "\n\n" + nested
    }
}
