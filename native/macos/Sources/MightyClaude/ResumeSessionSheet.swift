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
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
                ProviderIcon(provider: provider, size: 18).foregroundStyle(.secondary).frame(width: 22).padding(.top, 1)
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    Text(L("resume.choice.title", ["provider": agent])).font(.headline)
                    if checking {
                        HStack(spacing: DesignMetrics.Spacing.sm) {
                            ProgressView().controlSize(.small)
                            Text(L("resume.loading")).font(.system(size: 12)).foregroundStyle(.secondary)
                        }
                        .accessibilityElement(children: .combine)
                        .accessibilityIdentifier("add-pane-choice-checking")
                    } else {
                        Text(L("resume.choice.message", ["provider": agent])).font(.system(size: 12)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Button(L("resume.cancel")) { store.closeResumeChoice() }
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("add-pane-choice-cancel")
                Spacer()
                Button(L("resume.choice.resume")) { store.showResumeList() }
                    .disabled(checking)
                    .accessibilityIdentifier("add-pane-choice-resume")
                Button(L("resume.choice.startNew")) { store.startNewFromResumeChoice() }
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("add-pane-choice-new")
            }
        }
        .padding(DesignMetrics.Inset.sheet)
        .frame(width: 400)
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

    /// What the list shows: the search, and never a session a pane took while
    /// the sheet was open.
    private var shown: [ResumableSession] {
        let used = ResumableSessions.inUse(store.snapshot.sessions)
        let available = (listing?.items ?? []).filter { !used.contains($0.sessionID.lowercased()) }
        return ResumableSessions.filter(available, query: search)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    ProviderIcon(provider: provider, size: 14).foregroundStyle(.secondary)
                    Text(L("resume.title")).font(.headline)
                }
                Text(workspace.path).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "magnifyingglass").foregroundStyle(.tertiary)
                TextField(L("resume.search"), text: $search).textFieldStyle(.plain).font(.system(size: 12))
                    .focused($searchFocused)
                    .accessibilityIdentifier("resume-search")
            }
            .padding(DesignMetrics.Spacing.sm).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
            content.frame(maxWidth: .infinity, minHeight: 300, maxHeight: .infinity)
            HStack(spacing: DesignMetrics.Spacing.md) {
                Toggle(L("resume.showAll"), isOn: $showAll).toggleStyle(.checkbox).font(.caption)
                    .accessibilityIdentifier("resume-show-all")
                if !showAll, let hidden = listing?.hidden, hidden > 0 {
                    Text(L("resume.hiddenCount", ["count": "\(hidden)"])).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
            }
            HStack {
                Text(L("resume.note")).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Spacer()
                Button(L("resume.cancel")) { store.resumePicker = nil }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(DesignMetrics.Inset.sheet)
        .frame(width: 560, height: 520)
        .task(id: showAll) {
            listing = await store.resumableSessions(for: workspace, provider: provider, includeAutomated: showAll)
            searchFocused = true
        }
    }

    @ViewBuilder private var content: some View {
        if listing == nil {
            ProgressView(L("resume.loading")).frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if shown.isEmpty {
            VStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "clock.arrow.circlepath").font(.system(size: 26, weight: .light)).foregroundStyle(.tertiary)
                if search.trimmingCharacters(in: .whitespaces).isEmpty {
                    Text(L("resume.empty")).font(.system(size: 13, weight: .medium))
                    Text(L("resume.emptyReason", ["provider": ProviderOptions.label(provider)])).font(.system(size: 11)).foregroundStyle(.secondary).multilineTextAlignment(.center)
                } else {
                    Text(L("resume.noMatch")).font(.system(size: 13, weight: .medium))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).padding(DesignMetrics.Spacing.xl)
            .accessibilityIdentifier("resume-empty")
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    ForEach(shown) { item in row(item) }
                }
            }
            .background(Palette.subtle.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
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
                ProviderIcon(provider: item.provider, size: 14).foregroundStyle(.secondary).frame(width: 18).padding(.top, DesignMetrics.Spacing.xxs)
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(title).font(.system(size: 12, weight: .medium)).lineLimit(2).foregroundStyle(item.title == nil ? .secondary : .primary)
                        if ProviderOptions.isBeta(item.provider) { BetaBadge() }
                    }
                    Text(details).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                    if busy {
                        Label(L("resume.recentlyModified"), systemImage: "exclamationmark.circle")
                            .font(.system(size: 10)).foregroundStyle(Palette.waitText).lineLimit(1)
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
