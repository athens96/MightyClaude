import SwiftUI
import MightyCore

/// Lists the Claude and Codex sessions recorded for a workspace folder that no
/// open pane uses; choosing one adds a pane that continues it. Automated runs
/// (nested `claude --print` steps) show only with "모든 세션 보기".
struct ResumeSessionSheet: View {
    @EnvironmentObject private var store: AppStore
    let workspace: Workspace
    @ViewState private var listing: ResumableSessionListing?
    @ViewState private var search = ""
    @ViewState private var provider = "all"
    @ViewState private var showAll = false
    @FocusState private var searchFocused: Bool

    /// What the list shows: the search, the provider choice, and never a
    /// session a pane took while the sheet was open.
    private var shown: [ResumableSession] {
        let used = ResumableSessions.inUse(store.snapshot.sessions)
        let available = (listing?.items ?? []).filter { !used.contains($0.sessionID.lowercased()) && (provider == "all" || $0.provider == provider) }
        return ResumableSessions.filter(available, query: search)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(L("resume.title")).font(.headline)
                Text(workspace.path).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
            }
            HStack(spacing: 10) {
                HStack(spacing: 7) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.tertiary)
                    TextField(L("resume.search"), text: $search).textFieldStyle(.plain).font(.system(size: 12))
                        .focused($searchFocused)
                        .accessibilityIdentifier("resume-search")
                }
                .padding(7).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                Picker(L("resume.provider"), selection: $provider) {
                    Text(L("resume.provider.all")).tag("all")
                    ForEach(ResumableSessions.providers, id: \.self) { Text(ProviderOptions.label($0)).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
            }
            content.frame(maxWidth: .infinity, minHeight: 300, maxHeight: .infinity)
            HStack(spacing: 10) {
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
                Button(L("resume.cancel")) { store.resumePickerWorkspace = nil }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(20)
        .frame(width: 560, height: 520)
        .task(id: showAll) {
            listing = await store.resumableSessions(for: workspace, includeAutomated: showAll)
            searchFocused = true
        }
    }

    @ViewBuilder private var content: some View {
        if listing == nil {
            ProgressView(L("resume.loading")).frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if shown.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "clock.arrow.circlepath").font(.system(size: 26, weight: .light)).foregroundStyle(.tertiary)
                if search.trimmingCharacters(in: .whitespaces).isEmpty {
                    Text(L("resume.empty")).font(.system(size: 13, weight: .medium))
                    Text(L("resume.emptyReason")).font(.system(size: 11)).foregroundStyle(.secondary).multilineTextAlignment(.center)
                } else {
                    Text(L("resume.noMatch")).font(.system(size: 13, weight: .medium))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).padding(20)
            .accessibilityIdentifier("resume-empty")
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 2) {
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
                       item.model, item.automated ? L("resume.automated") : nil].compactMap { $0 }.joined(separator: " · ")
        // Written moments ago: a CLI elsewhere may still be running it.
        let busy = ResumableSessions.mayBeRunning(item)
        return Button { store.resumeSession(item, workspaceId: workspace.id) } label: {
            HStack(alignment: .top, spacing: 10) {
                ProviderIcon(provider: item.provider, size: 14).foregroundStyle(.secondary).frame(width: 18).padding(.top, 2)
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
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
            .padding(.horizontal, 10).padding(.vertical, 8)
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
