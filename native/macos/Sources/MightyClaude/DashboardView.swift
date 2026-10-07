import SwiftUI
import AppKit
import MightyCore

/// Concept D's status colours by tone, for the dashboard and the graph.
extension Palette {
    /// The soft tint behind a tone's ink in a pill.
    static func soft(_ tone: DesignTone) -> Color {
        switch tone {
        case .run: runSoft
        case .wait: waitSoft
        case .done: doneSoft
        case .err: errSoft
        case .stop, .idle: stopSoft
        }
    }

    /// Avenir Next Bold for headings and big numbers, the system bold where it is missing.
    static func heading(_ size: CGFloat) -> Font {
        NSFont(name: "AvenirNext-Bold", size: size) != nil ? .custom("AvenirNext-Bold", size: size) : .system(size: size, weight: .bold)
    }
    /// The AppKit font `heading` draws with, for measuring it.
    static func headingNSFont(_ size: CGFloat) -> NSFont {
        NSFont(name: "AvenirNext-Bold", size: size) ?? .systemFont(ofSize: size, weight: .bold)
    }
}

/// Words the dashboard and the sidebar rows share.
enum DashboardText {
    static func age(_ age: WorkDashboard.Age) -> String {
        switch age {
        case .now: L("resume.time.now")
        case .minutes(let count): L("resume.time.minutes", ["count": "\(count)"])
        case .hours(let count): L("resume.time.hours", ["count": "\(count)"])
        case .days(let count): L("resume.time.days", ["count": "\(count)"])
        }
    }

    /// The run clock, worded "약 02:14" when it was rebuilt from the saved record.
    static func clock(_ timing: AgentRunTiming, at date: Date) -> String {
        let reading = WorkDashboard.clock(timing.elapsed(at: date))
        return timing.isApproximate ? L("dashboard.card.approximate", ["time": reading]) : reading
    }

    /// The pane's status as a word: what it waits on, or its own status.
    static func status(_ card: WorkDashboard.Card) -> String {
        if card.attention.questions > 0 { return L("phone.card.questions", ["count": "\(card.attention.questions)"]) }
        if card.attention.permissions > 0 { return L("phone.card.permissions", ["count": "\(card.attention.permissions)"]) }
        return Palette.status(card.status)
    }

    static func kind(_ card: WorkDashboard.Card) -> String {
        switch card.kind {
        case SessionKind.claude: L("dashboard.kind.agent")
        case SessionKind.shell: L("dashboard.kind.shell")
        case SessionKind.browser: L("browser.tab.title")
        case AgentIOPaneKind.terminal: L("dashboard.kind.agentTerminal")
        case AgentIOPaneKind.browser: L("dashboard.kind.agentBrowser")
        case FilePaneKind.kind: L("files.pane.title")
        default: card.kind
        }
    }

    /// The second line for a pane that is not an agent's: its kind, and for a shell
    /// that runs in the app's own terminal, "셸 · 로컬 터미널".
    static func kindLine(_ card: WorkDashboard.Card, localTerminal: Bool) -> String {
        card.kind == SessionKind.shell && localTerminal ? L("dashboard.kind.shell") + " · " + L("phone.card.localTerminal") : kind(card)
    }

    /// Why a pane stopped on an error, for the second line: "Bash 실패", or the error's line.
    static func reason(_ text: String, isTool: Bool) -> String {
        isTool ? L("dashboard.card.toolFailed", ["tool": text]) : text
    }

    static func countLabel(_ label: String, _ count: Int) -> String {
        L("phone.dashboard.statLabel", ["label": label, "count": "\(count)"])
    }
}

/// The glyph for a pane that is not an agent's own: terminal, browser or files.
func paneSymbol(_ kind: String) -> String {
    switch kind {
    case SessionKind.browser, AgentIOPaneKind.browser: "globe"
    case FilePaneKind.kind: "folder"
    default: "terminal"
    }
}

/// "작업 현황": every workspace's panes as glyph rows, under three tiles that count
/// what runs, what waits on the user and what has finished.
struct DashboardView: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var gitState: WorkspaceGitState
    @ObservedObject var accountUsage: AccountUsageStatusController

    var body: some View {
        let sessions = store.snapshot.sessions
        let stats = WorkDashboard.stats(sessions: sessions, permissions: store.toolPermissions)
        // The page's inset (Inset.dashboardPage); only the header row moves past the traffic lights while folded.
        let inset = DesignMetrics.Inset.dashboardPageH
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: DesignMetrics.Spacing.md) {
                    SidebarToggleButton()
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        Text(L("phone.dashboard.title")).font(Palette.heading(29)).foregroundStyle(Palette.ink)
                            .accessibilityAddTraits(.isHeader)
                        Text(L("dashboard.subtitle", ["workspaces": "\(store.snapshot.workspaces.count)", "panes": "\(sessions.count)"]))
                            .font(.system(size: 12)).foregroundStyle(Palette.ink2)
                    }
                }
                .leadingPastTrafficLights(inset)
                VStack(alignment: .leading, spacing: 0) {
                    statRow(stats).padding(.top, DesignMetrics.Spacing.md)
                    if store.snapshot.workspaces.isEmpty {
                        Text(L("dashboard.empty")).font(.system(size: 13)).foregroundStyle(Palette.ink2).padding(.top, DesignMetrics.Spacing.lg)
                    }
                    ForEach(store.snapshot.workspaces) { workspace in
                        group(workspace, sessions: sessions.filter { $0.workspaceId == workspace.id }).padding(.top, DesignMetrics.Spacing.lg)
                    }
                }
                .padding(.leading, inset)
            }
            .padding(.trailing, inset).padding(.top, DesignMetrics.Inset.dashboardPageT).padding(.bottom, DesignMetrics.Inset.dashboardPageB)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .accessibilityIdentifier("work-dashboard")
    }

    // MARK: Tiles

    /// A number tile's height: the 34pt heading figure, its label and the tile's inset, with a little air.
    private static let tileHeight: CGFloat = 84

    private func statRow(_ stats: WorkDashboard.Stats) -> some View {
        let usage = usageRows
        return HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
            tile(stats.running, L("phone.dashboard.stat.running"), fill: Palette.run, number: Palette.onStatus, label: Palette.onStatus)
                .accessibilityIdentifier("dashboard-stat-running")
            tile(stats.waiting, L("phone.dashboard.stat.waiting"), fill: Palette.wait, number: Palette.onWait, label: Palette.onWait)
                .accessibilityIdentifier("dashboard-stat-waiting")
            tile(stats.done, L("phone.dashboard.stat.done"), fill: Palette.panel, number: Palette.doneText, label: Palette.ink2)
                .accessibilityIdentifier("dashboard-stat-done")
            if !usage.isEmpty { usageCard(usage) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func tile(_ count: Int, _ label: String, fill: Color, number: Color, label ink: Color) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("\(count)").font(Palette.heading(34)).monospacedDigit().foregroundStyle(number)
            Spacer(minLength: DesignMetrics.Spacing.xxs)
            Text(label).font(.system(size: 12.5, weight: .bold)).foregroundStyle(ink).lineLimit(1)
        }
        .padding(.horizontal, DesignMetrics.Inset.dashboardTileH).padding(.vertical, DesignMetrics.Inset.dashboardTileV)
        .frame(minWidth: 110, maxWidth: usageRows.isEmpty ? .infinity : 220, minHeight: Self.tileHeight, maxHeight: Self.tileHeight, alignment: .leading)
        // The shadow sits on the tile's shape only, never on the words drawn on it.
        .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(fill).shadow(color: .black.opacity(0.05), radius: 1, y: 1))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(DashboardText.countLabel(label, count))
    }

    /// The providers with account windows the app has already read, and their leading
    /// two windows (session, then weekly). Empty when the app has none: no card then.
    private var usageRows: [(provider: String, windows: [AccountUsageWindow])] {
        accountUsage.providers.compactMap { provider in
            guard let windows = accountUsage.snapshots[provider]?.windows, !windows.isEmpty else { return nil }
            let leading = StatusBarUsageView.leading(windows)
            return leading.isEmpty ? nil : (provider, leading)
        }
    }

    private func usageCard(_ rows: [(provider: String, windows: [AccountUsageWindow])]) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "waveform.path.ecg").font(.system(size: 11)).foregroundStyle(Palette.ink2)
                Text(L("dashboard.usage.title")).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink)
                Spacer(minLength: 6)
                Text(L("dashboard.usage.shared")).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
            }
            ForEach(rows, id: \.provider) { row in
                HStack(spacing: DesignMetrics.Spacing.lg) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        ProviderIcon(provider: row.provider, size: 11)
                        Text(ProviderOptions.label(row.provider)).font(.system(size: 11.5, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                    }.frame(width: 78, alignment: .leading)
                    ForEach(Array(row.windows.enumerated()), id: \.offset) { _, window in usageBar(window) }
                }
            }
        }
        .padding(.horizontal, DesignMetrics.Inset.dashboardTileH).padding(.vertical, DesignMetrics.Inset.dashboardTileV)
        .frame(minWidth: 260, maxWidth: .infinity, minHeight: Self.tileHeight, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Palette.panel).shadow(color: .black.opacity(0.05), radius: 1, y: 1))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("dashboard-usage")
    }

    private func usageBar(_ window: AccountUsageWindow) -> some View {
        let fraction = min(1, max(0, window.usedPercent / 100))
        return HStack(spacing: 8) {
            Text(StatusBarUsageView.windowLabel(window.kind)).font(.system(size: 11.5)).foregroundStyle(Palette.ink2).lineLimit(1)
            Capsule().fill(Palette.runSoft).frame(height: 6)
                .overlay(alignment: .leading) {
                    GeometryReader { proxy in
                        Capsule().fill(window.usedPercent >= 90 ? Palette.waitText : Palette.run).frame(width: proxy.size.width * fraction)
                    }
                }
                .frame(minWidth: 40)
                .accessibilityHidden(true)
            Text(StatusBarUsageView.percent(window.usedPercent)).font(.system(size: 11.5, weight: .bold)).monospacedDigit()
                .foregroundStyle(Palette.ink).frame(minWidth: 30, alignment: .trailing)
        }
    }

    // MARK: Workspace groups

    private func group(_ workspace: Workspace, sessions: [RunSession]) -> some View {
        let cards = WorkDashboard.ordered(sessions.map { WorkDashboard.card($0, permissions: store.toolPermissions[$0.id]) })
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.md) {
                Text(workspace.name).font(Palette.heading(17)).foregroundStyle(Palette.ink).lineLimit(1)
                    .accessibilityAddTraits(.isHeader)
                Text(workspace.path).font(.system(size: 11.5, design: .monospaced)).foregroundStyle(Palette.ink2)
                    .lineLimit(1).truncationMode(.middle).help(workspace.path)
                if let git = gitState.info(for: workspace) { WorkspaceGitBadge(info: git) }
                Spacer(minLength: 8)
                Button { store.openFilePane(workspaceId: workspace.id) } label: {
                    Label(L("files.pane.title"), systemImage: "folder").font(.system(size: 11.5, weight: .bold))
                        .foregroundStyle(Palette.ink)
                        .padding(.horizontal, DesignMetrics.Spacing.md).frame(height: 24)
                        .background(Palette.panel, in: Capsule())
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain).disabled(store.hasModal)
                .help(L("menu.showFiles"))
                .accessibilityIdentifier("dashboard-open-files-\(workspace.id)")
                Menu { WorkspaceAddMenuItems(store: store, workspace: workspace) } label: {
                    Label(L("workspace.addPane"), systemImage: "plus").font(.system(size: 11.5, weight: .bold))
                        .foregroundStyle(Palette.panel)
                        .padding(.horizontal, DesignMetrics.Spacing.md).frame(height: 24)
                        .background(Palette.ink, in: Capsule())
                        .contentShape(Capsule())
                }
                .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
                .disabled(store.hasModal)
                .help(L("workspace.addPaneHelp"))
                .accessibilityLabel(L("workspace.addPaneAccessibility", ["workspace": workspace.name]))
                .accessibilityIdentifier("dashboard-add-session-\(workspace.id)")
            }
            .padding(.horizontal, 2)
            if cards.isEmpty {
                Text(L("phone.workspaces.noSessions")).font(.system(size: 12)).foregroundStyle(Palette.ink2).padding(.horizontal, 2)
            } else {
                // One white group per workspace, its panes as glyph rows (status v2).
                VStack(spacing: 0) {
                    ForEach(Array(cards.enumerated()), id: \.element.id) { index, card in
                        if index > 0 { Rectangle().fill(Palette.border).frame(height: 1).padding(.leading, DashboardRow.textLeading) }
                        DashboardRow(card: card, localTerminal: sessions.first { $0.id == card.id }.map { store.usesLocalTerminal($0) } ?? false)
                    }
                }
                .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Palette.panel).shadow(color: .black.opacity(0.05), radius: 1, y: 1))
                .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("dashboard-workspace-\(workspace.id)")
    }
}

/// One pane as a row on the dashboard: its status glyph, the title, a muted line with
/// provider, model and what the app knows of its run, and for a running pane its last
/// step in mono. Only a pane waiting on the user carries a word on the right ("질문 1").
/// Clicking it opens the workspace on that pane.
struct DashboardRow: View {
    static let glyphSize: CGFloat = 18
    static let glyphGap: CGFloat = 10
    /// Where a row's words start: the rule between rows begins there.
    static let textLeading = DesignMetrics.Inset.dashboardRowH + glyphSize + glyphGap
    @EnvironmentObject private var store: AppStore
    let card: WorkDashboard.Card
    let localTerminal: Bool
    @ViewState private var hovering = false

    var body: some View {
        let settled = [.done, .stop, .idle].contains(card.tone)
        Button { store.selectSession(card.id) } label: {
            HStack(alignment: .top, spacing: Self.glyphGap) {
                StatusGlyph(tone: card.tone, kind: card.kind, size: Self.glyphSize).padding(.top, 1).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 1) {
                    Text(card.title).font(.system(size: 14, weight: settled ? .medium : .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                        .frame(minHeight: 20)
                    if card.isRunning, let timing = card.timing, timing.finishedAt == nil {
                        TimelineView(.periodic(from: .now, by: 1)) { context in meta(at: context.date) }
                    } else {
                        TimelineView(.everyMinute) { context in meta(at: context.date) }
                    }
                    if let last = card.lastActivity, card.isRunning {
                        Text(last.text).font(.system(size: 11.3, design: .monospaced))
                            .foregroundStyle(last.isError ? Palette.errText : Palette.ink2)
                            .lineLimit(1).truncationMode(.tail).padding(.top, DesignMetrics.Spacing.xxs)
                    }
                }
                Spacer(minLength: 4)
                if card.attention.total > 0 {
                    Text(DashboardText.status(card)).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.waitText)
                        .lineLimit(1).fixedSize().frame(minHeight: 20)
                }
            }
            .padding(.horizontal, DesignMetrics.Inset.dashboardRowH).padding(.vertical, DesignMetrics.Inset.dashboardRowV)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(hovering ? Palette.subtle : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(L("phone.card.label", ["title": card.title, "status": card.attention.total > 0 ? L("phone.card.attention", ["count": "\(card.attention.total)"]) : Palette.word(card.tone)]))
        .accessibilityIdentifier("dashboard-card-\(card.id)")
    }

    /// The pane's own model choice, labelled exactly as its composer chip; none for the CLI default.
    private var modelText: String? {
        guard card.model != nil, let session = store.snapshot.sessions.first(where: { $0.id == card.id }) else { return card.model }
        return store.modelLabel(for: session)
    }

    /// `Claude · Opus 5.5 · 02:14 · 컨텍스트 41%` while it runs, `Codex · GPT-5 · 11분 전` at
    /// rest; a pane that is not an agent's names its kind.
    private func meta(at date: Date) -> some View {
        HStack(spacing: 4) {
            if card.isAgent {
                ProviderBadgeIcon.labelled(ProviderOptions.label(card.provider), provider: card.provider, font: .systemFont(ofSize: 12))
                if ProviderOptions.isBeta(card.provider) { BetaBadge() }
                let rest = ([modelText] + WorkDashboard.sidebarMeta(card, now: date).map { part -> String? in
                    switch part {
                    case .provider: nil
                    case .elapsed: card.timing.map { DashboardText.clock($0, at: date) }
                    case .context(let percent): L("phone.card.context", ["percent": "\(percent)"])
                    case .reason(let text, let isTool): DashboardText.reason(text, isTool: isTool)
                    case .age(let age): DashboardText.age(age)
                    }
                }).compactMap { $0 }
                if !rest.isEmpty { Text("· " + rest.joined(separator: " · ")) }
            } else if card.kind == SessionKind.shell, localTerminal {
                Text(L("dashboard.kind.shell") + " ·")
                Text(L("phone.card.localTerminal")).fontWeight(.semibold).foregroundStyle(Palette.waitText)
            } else {
                Text(DashboardText.kind(card))
                if AgentIOPaneKind.isAgentIOPane(card.kind) { Text("· " + ProviderOptions.label(card.provider)) }
            }
        }
        .font(.system(size: 12)).monospacedDigit().foregroundStyle(Palette.ink2).lineLimit(1)
    }
}

/// The run-blue dot that breathes beside a running pane's last step; still when the
/// user asks for reduced motion.
struct PulseDot: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @ViewState private var spread = false

    var body: some View {
        ZStack {
            Circle().fill(Palette.run.opacity(0.22)).frame(width: 13, height: 13)
                .scaleEffect(reduceMotion ? 1 : (spread ? 1.35 : 0.8))
                .opacity(reduceMotion ? 1 : (spread ? 0 : 1))
            Circle().fill(Palette.run).frame(width: 7, height: 7)
        }
        .frame(width: 14, height: 14)
        .onAppear {
            guard !reduceMotion else { return }
            withAnimation(.easeOut(duration: 1.4).repeatForever(autoreverses: false)) { spread = true }
        }
        .accessibilityHidden(true)
    }
}
