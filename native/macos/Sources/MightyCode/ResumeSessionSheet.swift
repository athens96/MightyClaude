import SwiftUI
import MightyCore

/// The first "창 추가" step for an agent that has earlier sessions in this folder:
/// start a new session, or go on to the list of its sessions. Esc closes it
/// without adding a pane. `checking`: the look-up is still reading the folder's
/// records; "새로 시작" already works, "이어가기" waits for it.
struct ResumeChoiceSheet: View {
    @EnvironmentObject private var store: AppStore
    let provider: String
    var checking = false

    var body: some View {
        let agent = ProviderOptions.label(provider)
        DialogScaffold(L("resume.choice.title", ["provider": agent])) {
            if checking {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    ProgressView().controlSize(.small)
                    Text(L("resume.loading")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                }
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("add-pane-choice-checking")
            } else {
                Text(L("resume.choice.message", ["provider": agent])).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        } footer: {
            DialogFooter {
                Button(L("resume.choice.resume")) { store.showResumeList() }
                    .disabled(checking)
                    .accessibilityIdentifier("add-pane-choice-resume")
                Button(L("resume.cancel")) { store.closeResumeChoice() }
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("add-pane-choice-cancel")
                Button(L("resume.choice.startNew")) { store.startNewFromResumeChoice() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("add-pane-choice-new")
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthM)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("add-pane-choice")
    }
}

/// Lists one agent's sessions recorded for a workspace folder that no open pane
/// uses; choosing one adds a pane that continues it. Automated runs (nested
/// `claude --print` steps) show only with "모든 세션 보기".
struct ResumeSessionSheet: View {
    @EnvironmentObject private var store: AppStore
    let workspace: Workspace
    /// The agent picked in "창 추가".
    let provider: String
    @ViewState private var listing: ResumableSessionListing?
    @ViewState private var search = ""
    @ViewState private var showAll = false
    @FocusState private var searchFocused: Bool
    /// The rows' own height, so the list box hugs a short list instead of a fixed tall box.
    @ViewState private var rowsHeight: CGFloat = 0
    private static let listMinHeight = DesignMetrics.Dialog.headerHeight * 3
    private static let listMaxHeight: CGFloat = 360

    private var listHeight: CGFloat {
        guard listing != nil, !shown.isEmpty else { return Self.listMinHeight }
        return min(Self.listMaxHeight, max(Self.listMinHeight, ceil(rowsHeight)))
    }

    /// What the list shows: the search, and never a session a pane took while
    /// the sheet was open.
    private var shown: [ResumableSession] {
        let used = ResumableSessions.inUse(store.snapshot.sessions)
        let available = (listing?.items ?? []).filter { !used.contains($0.sessionID.lowercased()) }
        return ResumableSessions.filter(available, query: search)
    }

    var body: some View {
        DialogScaffold(L("resume.title"), subtitle: workspace.path) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "magnifyingglass").foregroundStyle(Palette.ink2)
                    TextField(L("resume.search"), text: $search).textFieldStyle(.plain).font(.ui(size: 12))
                        .focused($searchFocused)
                        .accessibilityIdentifier("resume-search")
                }
                .padding(DesignMetrics.Spacing.sm).background(Palette.raised, in: RoundedRectangle(cornerRadius: 7))
                content.frame(maxWidth: .infinity).frame(height: listHeight)
                HStack(spacing: DesignMetrics.Spacing.md) {
                    Toggle(L("resume.showAll"), isOn: $showAll).toggleStyle(.checkbox).font(.ui(size: 11))
                        .accessibilityIdentifier("resume-show-all")
                    if !showAll, let hidden = listing?.hidden, hidden > 0 {
                        Text(L("resume.hiddenCount", ["count": "\(hidden)"])).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                    }
                    Spacer()
                }
            }
        } footer: {
            DialogFooter {
                Text(L("resume.note")).foregroundStyle(Palette.ink2)
            } buttons: {
                Button(L("resume.cancel")) { store.resumePicker = nil }.keyboardShortcut(.cancelAction)
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .task(id: showAll) {
            listing = await store.resumableSessions(for: workspace, provider: provider, includeAutomated: showAll)
            searchFocused = true
        }
    }

    @ViewBuilder private var content: some View {
        if listing == nil {
            DialogLoading(skeletonRows: 3).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        } else if shown.isEmpty {
            VStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "clock.arrow.circlepath").font(.ui(size: 26, weight: .light)).foregroundStyle(Palette.ink2)
                if search.trimmingCharacters(in: .whitespaces).isEmpty {
                    Text(L("resume.empty")).font(.ui(size: 13, weight: .medium))
                    Text(L("resume.emptyReason", ["provider": ProviderOptions.label(provider)])).font(.ui(size: 11)).foregroundStyle(Palette.ink2).multilineTextAlignment(.center)
                } else {
                    Text(L("resume.noMatch")).font(.ui(size: 13, weight: .medium))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).padding(DesignMetrics.Spacing.xl)
            .accessibilityIdentifier("resume-empty")
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    ForEach(shown) { item in row(item) }
                }
                .background(GeometryReader { rows in Color.clear.preference(key: ResumeRowsHeight.self, value: rows.size.height) })
            }
            .onPreferenceChange(ResumeRowsHeight.self) { height in
                if height.isFinite, abs(rowsHeight - height) > 0.5 { rowsHeight = height }
            }
            .background(Palette.raised, in: RoundedRectangle(cornerRadius: 8))
            .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        }
    }

    private func row(_ item: ResumableSession) -> some View {
        let title = item.title ?? L("resume.untitled")
        let details = [ResumableSessions.relativeTime(item.modified),
                       item.requests.map { L("resume.requests", ["count": "\($0)"]) },
                       item.model.map { ModelLabel.text($0) }, item.automated ? L("resume.automated") : nil].compactMap { $0 }.joined(separator: " · ")
        // Written moments ago: a CLI elsewhere may still be running it.
        let busy = ResumableSessions.mayBeRunning(item)
        return Button { store.resumeSession(item, workspaceId: workspace.id) } label: {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
                ProviderIcon(provider: item.provider, size: 14).foregroundStyle(Palette.ink2).frame(width: 18).padding(.top, DesignMetrics.Spacing.xxs)
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(title).font(.ui(size: 12, weight: .medium)).lineLimit(2).foregroundStyle(item.title == nil ? .secondary : .primary)
                        if ProviderOptions.isBeta(item.provider) { BetaBadge() }
                    }
                    Text(details).font(.ui(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                    if busy {
                        Label(L("resume.recentlyModified"), systemImage: "exclamationmark.circle")
                            .font(.ui(size: 10)).foregroundStyle(Palette.waitText).lineLimit(1)
                            .accessibilityIdentifier("resume-busy-\(item.sessionID)")
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(item.title ?? item.sessionID)
        .accessibilityLabel(L("resume.rowAccessibility", ["provider": ProviderOptions.label(item.provider), "title": title,
                                                          "details": busy ? details + " · " + L("resume.recentlyModified") : details]))
        .accessibilityIdentifier("resume-row-\(item.sessionID)")
    }
}

private struct ResumeRowsHeight: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}
