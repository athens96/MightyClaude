import AppKit
import MightyCore
import SwiftUI
import UniformTypeIdentifiers

/// 대화 검색: the workspace's past Claude, Codex and Antigravity sessions
/// whose messages hold every word typed (`ConversationSearch`). A hit opens
/// as a new pane that continues the session, or is saved as Markdown. A panel (kind C).
struct ConversationSearchSheet: View {
    @EnvironmentObject private var store: AppStore
    let workspace: Workspace
    let onClose: () -> Void

    @ViewState private var query = ""
    @ViewState private var sessions: [ResumableSession]?
    @ViewState private var hits: [ConversationSearch.Hit] = []
    @ViewState private var searching = false
    @FocusState private var focused: Bool

    var body: some View {
        DialogScaffold(L("conversationSearch.title"), subtitle: workspace.path, systemImage: "text.magnifyingglass",
                       close: DialogClose(identifier: "conversation-search-close", action: onClose), tools: { EmptyView() }) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "magnifyingglass").foregroundStyle(Palette.ink2)
                    TextField(L("conversationSearch.placeholder"), text: $query).textFieldStyle(.plain).font(.ui(size: 13))
                        .focused($focused)
                        .accessibilityIdentifier("conversation-search-field")
                    if searching { ProgressView().controlSize(.small) }
                }
                .padding(DesignMetrics.Spacing.sm).background(Palette.raised, in: RoundedRectangle(cornerRadius: 7))
                results.frame(maxWidth: .infinity).frame(height: 380)
                Text(L("conversationSearch.note")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .task {
            focused = true
            var all: [ResumableSession] = []
            for provider in ResumableSessions.providers {
                all += await store.resumableSessions(for: workspace, provider: provider).items
            }
            sessions = all
        }
        // Each keystroke restarts the search after a short pause; the old one is cancelled.
        .task(id: "\(query)|\(sessions?.count ?? -1)") {
            guard let sessions else { return }
            let query = query
            guard !ConversationSearch.terms(query).isEmpty else { hits = []; return }
            do { try await Task.sleep(for: .milliseconds(250)) } catch { return }
            searching = true
            let reading = Task.detached(priority: .userInitiated) { ConversationSearch.search(sessions, query: query) }
            let found = await withTaskCancellationHandler { await reading.value } onCancel: { reading.cancel() }
            guard !Task.isCancelled else { return }
            hits = found; searching = false
        }
    }

    @ViewBuilder private var results: some View {
        if sessions == nil {
            DialogLoading(skeletonRows: 4)
        } else if ConversationSearch.terms(query).isEmpty {
            Text(L("conversationSearch.hint", ["count": "\(sessions?.count ?? 0)"])).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if hits.isEmpty && !searching {
            Text(L("conversationSearch.none")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    ForEach(hits) { hit in row(hit) }
                }
            }
        }
    }

    private func row(_ hit: ConversationSearch.Hit) -> some View {
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            if let image = ProviderIconImage.image(provider: hit.session.provider, pointSize: 13) { Image(nsImage: image) }
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                HStack {
                    Text(hit.session.title ?? hit.session.sessionID).font(.ui(size: 12, weight: .semibold)).lineLimit(1)
                    Spacer(minLength: DesignMetrics.Spacing.sm)
                    Text(hit.session.modified.formatted(date: .abbreviated, time: .shortened)).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                }
                Text(hit.snippet).font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(2)
                HStack(spacing: DesignMetrics.Spacing.md) {
                    Text(L("conversationSearch.matches", ["count": "\(hit.matches)"])).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                    Spacer()
                    Button(L("conversationSearch.export")) { export(hit) }.buttonStyle(.plain).font(.ui(size: 11)).foregroundStyle(Palette.accent)
                    Button(L("conversationSearch.resume")) {
                        onClose()
                        store.resumeSession(hit.session, workspaceId: workspace.id)
                    }
                    .buttonStyle(.plain).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.accent)
                    .accessibilityIdentifier("conversation-search-resume-\(hit.session.sessionID)")
                }
            }
        }
        .padding(DesignMetrics.Spacing.sm)
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
    }

    private func export(_ hit: ConversationSearch.Hit) {
        let title = hit.session.title ?? hit.session.sessionID
        guard let markdown = ConversationSearch.markdown(url: hit.session.url, title: title, userLabel: L("conversationSearch.speaker.user"),
                                                         agentLabel: ProviderOptions.label(hit.session.provider)) else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText]
        panel.nameFieldStringValue = String(title.prefix(60)).replacingOccurrences(of: "/", with: "-") + ".md"
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            try? Data(markdown.utf8).write(to: url, options: .atomic)
        }
    }
}
