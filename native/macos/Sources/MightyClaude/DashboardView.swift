import SwiftUI
import AppKit
import MightyCore

/// Concept D's status colours by tone, for the dashboard and the sidebar's cards.
extension Palette {
    /// A card's left edge: the tone's fill, or the quiet track for an idle pane.
    static func edge(_ tone: DesignTone) -> Color {
        switch tone {
        case .run: run
        case .wait: wait
        case .done: done
        case .err: err
        case .stop: stop
        case .idle: track
        }
    }

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
}

/// Words the dashboard and the sidebar cards share.
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

    static func kind(_ card: WorkDashboard.Card, localTerminal: Bool) -> String {
        switch card.kind {
        case SessionKind.claude: L("dashboard.kind.agent")
        case SessionKind.shell: localTerminal ? L("phone.card.localTerminal") : L("dashboard.kind.shell")
        case SessionKind.browser: L("browser.tab.title")
        case AgentIOPaneKind.terminal: L("dashboard.kind.agentTerminal")
        case AgentIOPaneKind.browser: L("dashboard.kind.agentBrowser")
        case FilePaneKind.kind: L("files.pane.title")
        default: card.kind
        }
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

/// A small filled count, as on the sidebar's "작업 현황" entry and workspace rows.
struct CountPill: View {
    let text: String
    let fill: Color
    let ink: Color

    var body: some View {
        Text(text)
            .font(.system(size: 10.5, weight: .bold)).monospacedDigit().lineLimit(1)
            .foregroundStyle(ink)
            .padding(.horizontal, 6).frame(minWidth: 20, minHeight: 18)
            .background(fill, in: Capsule())
            .fixedSize()
    }
}

/// "작업 현황": every workspace's panes as status cards, under three tiles that count
/// what runs, what waits on the user and what has finished.
struct DashboardView: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var gitState: WorkspaceGitState
    @ObservedObject var accountUsage: AccountUsageStatusController

    var body: some View {
        let sessions = store.snapshot.sessions
        let stats = WorkDashboard.stats(sessions: sessions, permissions: store.toolPermissions)
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(L("phone.dashboard.title")).font(Palette.heading(29)).foregroundStyle(Palette.ink)
                        .accessibilityAddTraits(.isHeader)
                    Text(L("dashboard.subtitle", ["workspaces": "\(store.snapshot.workspaces.count)", "panes": "\(sessions.count)"]))
                        .font(.system(size: 12)).foregroundStyle(Palette.ink2)
                }
                statRow(stats).padding(.top, 16)
                if store.snapshot.workspaces.isEmpty {
                    Text(L("dashboard.empty")).font(.system(size: 13)).foregroundStyle(Palette.ink2).padding(.top, 28)
                }
                ForEach(store.snapshot.workspaces) { workspace in
                    group(workspace, sessions: sessions.filter { $0.workspaceId == workspace.id }).padding(.top, 22)
                }
            }
            .padding(.horizontal, 28).padding(.top, 20).padding(.bottom, 24)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .accessibilityIdentifier("work-dashboard")
    }

    // MARK: Tiles

    private func statRow(_ stats: WorkDashboard.Stats) -> some View {
        let usage = usageRows
        return HStack(alignment: .top, spacing: 12) {
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
            Spacer(minLength: 4)
            Text(label).font(.system(size: 12.5, weight: .bold)).foregroundStyle(ink).lineLimit(1)
        }
        .padding(.horizontal, 16).padding(.top, 12).padding(.bottom, 13)
        .frame(minWidth: 110, maxWidth: usageRows.isEmpty ? .infinity : 220, minHeight: 92, maxHeight: 92, alignment: .leading)
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
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 7) {
                Image(systemName: "waveform.path.ecg").font(.system(size: 11)).foregroundStyle(Palette.ink2)
                Text(L("dashboard.usage.title")).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink)
                Spacer(minLength: 6)
                Text(L("dashboard.usage.shared")).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
            }
            ForEach(rows, id: \.provider) { row in
                HStack(spacing: 14) {
                    HStack(spacing: 6) {
                        ProviderIcon(provider: row.provider, size: 11)
                        Text(ProviderOptions.label(row.provider)).font(.system(size: 11.5, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                    }.frame(width: 78, alignment: .leading)
                    ForEach(Array(row.windows.enumerated()), id: \.offset) { _, window in usageBar(window) }
                }
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 11)
        .frame(minWidth: 260, maxWidth: .infinity, minHeight: 92, alignment: .topLeading)
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
        return VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Text(workspace.name).font(Palette.heading(17)).foregroundStyle(Palette.ink).lineLimit(1)
                    .accessibilityAddTraits(.isHeader)
                Text(workspace.path).font(.system(size: 11.5, design: .monospaced)).foregroundStyle(Palette.ink2)
                    .lineLimit(1).truncationMode(.middle).help(workspace.path)
                if let git = gitState.info(for: workspace) { WorkspaceGitBadge(info: git) }
                Spacer(minLength: 8)
                Button { store.openFilePane(workspaceId: workspace.id) } label: {
                    Label(L("files.pane.title"), systemImage: "folder").font(.system(size: 11.5, weight: .bold))
                        .foregroundStyle(Palette.ink)
                        .padding(.horizontal, 11).frame(height: 26)
                        .background(Palette.panel, in: Capsule())
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain).disabled(store.hasModal)
                .help(L("menu.showFiles"))
                .accessibilityIdentifier("dashboard-open-files-\(workspace.id)")
                Menu { WorkspaceAddMenuItems(store: store, workspace: workspace) } label: {
                    Label(L("workspace.addPane"), systemImage: "plus").font(.system(size: 11.5, weight: .bold))
                        .foregroundStyle(Palette.panel)
                        .padding(.horizontal, 11).frame(height: 26)
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
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 300), spacing: 12, alignment: .top)], alignment: .leading, spacing: 12) {
                    ForEach(cards) { card in
                        DashboardCard(card: card, localTerminal: sessions.first { $0.id == card.id }.map { store.usesLocalTerminal($0) } ?? false)
                    }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("dashboard-workspace-\(workspace.id)")
    }
}

/// One pane as a status card on the dashboard. Clicking it opens the workspace on that pane.
struct DashboardCard: View {
    @EnvironmentObject private var store: AppStore
    let card: WorkDashboard.Card
    let localTerminal: Bool

    private var loud: Color {
        switch card.tone {
        case .run: Palette.run
        case .wait: Palette.wait
        default: .clear
        }
    }

    var body: some View {
        Button { store.selectSession(card.id) } label: {
            VStack(alignment: .leading, spacing: 11) {
                head
                if card.contextPercent != nil || card.timing != nil { numbers }
                if let last = card.lastActivity { lastLine(last) }
            }
            .padding(.leading, 19).padding(.trailing, 14).padding(.vertical, 13)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Palette.panel)
            .overlay(alignment: .leading) { Rectangle().fill(Palette.edge(card.tone)).frame(width: 5) }
            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(loud, lineWidth: 2))
            // The shadow belongs to the card's shape alone, not to every view on it.
            .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Palette.panel).shadow(color: .black.opacity(0.05), radius: 1, y: 1))
            .contentShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(L("phone.card.label", ["title": card.title, "status": card.attention.total > 0 ? L("phone.card.attention", ["count": "\(card.attention.total)"]) : Palette.status(card.status)]))
        .accessibilityIdentifier("dashboard-card-\(card.id)")
    }

    private var head: some View {
        HStack(spacing: 11) {
            avatar
            VStack(alignment: .leading, spacing: 2) {
                Text(card.title).font(.system(size: 14.5, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                TimelineView(.everyMinute) { context in meta(at: context.date) }
            }
            Spacer(minLength: 4)
            pill
        }
    }

    private var avatar: some View {
        RoundedRectangle(cornerRadius: 11, style: .continuous)
            .fill(card.isAgent ? AnyShapeStyle(ProviderBrand.gradient(card.provider)) : AnyShapeStyle(Palette.idle))
            .frame(width: 34, height: 34)
            .overlay {
                if card.isAgent {
                    ProviderMarkShape(provider: card.provider).fill(Palette.onStatus).frame(width: 18, height: 18)
                } else {
                    Image(systemName: paneSymbol(card.kind)).font(.system(size: 15, weight: .semibold)).foregroundStyle(Palette.onStatus)
                }
            }
            .accessibilityHidden(true)
    }

    private func meta(at date: Date) -> some View {
        HStack(spacing: 4) {
            if card.kind == SessionKind.shell, localTerminal {
                Text(L("dashboard.kind.shell") + " ·")
                Text(L("phone.card.localTerminal")).fontWeight(.semibold).foregroundStyle(Palette.waitText)
            } else {
                Text(DashboardText.kind(card, localTerminal: false))
            }
            if card.isAgent || AgentIOPaneKind.isAgentIOPane(card.kind) {
                Text("· " + ProviderOptions.label(card.provider))
                if ProviderOptions.isBeta(card.provider) { BetaBadge() }
            }
            if card.isAgent, let model = card.model { Text("· " + model) }
            if !card.isRunning, card.isAgent, let updated = card.updatedAt {
                Text("· " + DashboardText.age(WorkDashboard.age(of: updated, now: date)))
            }
        }
        .font(.system(size: 11.5)).foregroundStyle(Palette.ink2).lineLimit(1)
    }

    @ViewBuilder private var pill: some View {
        if card.attention.total > 0 {
            Text(DashboardText.status(card)).font(.system(size: 11, weight: .bold)).foregroundStyle(Palette.onWait)
                .padding(.horizontal, 9).frame(height: 21).background(Palette.wait, in: Capsule()).fixedSize()
        } else {
            Text(DashboardText.status(card)).font(.system(size: 11, weight: .bold)).foregroundStyle(Palette.text(card.status))
                .padding(.horizontal, 9).frame(height: 21).background(Palette.soft(card.tone), in: Capsule()).fixedSize()
        }
    }

    private var numbers: some View {
        HStack(spacing: 10) {
            if let percent = card.contextPercent {
                Capsule().fill(Palette.runSoft).frame(height: 8)
                    .overlay(alignment: .leading) {
                        GeometryReader { proxy in Capsule().fill(Palette.run).frame(width: proxy.size.width * percent / 100) }
                    }
                    .accessibilityHidden(true)
                Text(L("phone.card.context", ["percent": "\(Int(percent.rounded()))"]))
                    .font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2).fixedSize()
                    .accessibilityLabel(L("phone.card.contextLabel", ["percent": "\(Int(percent.rounded()))"]))
            } else {
                Spacer(minLength: 0)
            }
            if let timing = card.timing {
                if card.isRunning && timing.finishedAt == nil {
                    TimelineView(.periodic(from: .now, by: 1)) { context in clock(timing, at: context.date) }
                } else {
                    clock(timing, at: Date())
                }
            }
        }
    }

    private func clock(_ timing: AgentRunTiming, at date: Date) -> some View {
        let text = DashboardText.clock(timing, at: date)
        return Text(text).font(Palette.heading(23)).monospacedDigit().foregroundStyle(Palette.ink).fixedSize()
            .accessibilityLabel(L("phone.card.elapsedLabel", ["time": text]))
    }

    private func lastLine(_ last: WorkDashboard.LastActivity) -> some View {
        HStack(spacing: 8) {
            if last.isError {
                RoundedRectangle(cornerRadius: 4).fill(Palette.err).frame(width: 14, height: 14)
                    .overlay { Image(systemName: "xmark").font(.system(size: 8, weight: .bold)).foregroundStyle(Palette.onStatus) }
                    .accessibilityHidden(true)
            } else if card.isRunning {
                PulseDot()
            }
            Text(last.text).font(.system(size: 11.3, design: .monospaced)).foregroundStyle(Palette.ink2)
                .lineLimit(1).truncationMode(.tail)
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
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
