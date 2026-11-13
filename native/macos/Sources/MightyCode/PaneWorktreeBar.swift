import AppKit
import MightyCore
import SwiftUI

/// The bar over an agent pane that works in its own worktree (``PaneWorktree``): its branch and base, how far it is from
/// its base (commits ahead and behind, changed files), its PORT and setup, and what can be done with it: merge (committing
/// first when it has changes), undo the last merge, ask the agent to resolve a conflict, review the changes, open the port,
/// a terminal there, a pull request, discard.
struct PaneWorktreeBar: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let worktree: PaneWorktree
    /// The pane's status: the bar reads its git again when a run ends.
    let paneStatus: String

    var body: some View {
        let busy = store.paneWorktreeBusy.contains(sessionId)
        let status = store.paneWorktreeStatus[sessionId]
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "arrow.triangle.branch").font(.ui(size: 11)).foregroundStyle(Palette.accent)
            Text(verbatim: L("paneWorktree.bar.branch", ["branch": worktree.branch, "base": worktree.baseBranch]))
                .font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.middle)
                .help(worktree.worktreePath)
            if let status { counts(status) }
            Button { store.openPaneWorktreePort(sessionId) } label: { Text(verbatim: "PORT \(worktree.port)").font(.ui(size: 10).monospacedDigit()) }
                .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                .help(L("paneWorktree.bar.portHelp", ["port": String(worktree.port)]))
                .accessibilityIdentifier("pane-worktree-port-\(sessionId)")
            if let setup = worktree.setupStatus { self.setup(setup) }
            Spacer(minLength: 0)
            if busy { ProgressView().controlSize(.mini) }
            if store.paneWorktreeConflicts.contains(sessionId) {
                Button(L("paneWorktree.bar.resolve")) { store.askPaneToResolveConflict(sessionId) }
                    .controlSize(.small).disabled(busy || store.hasModal)
                    .help(L("paneWorktree.bar.resolveHelp", ["base": worktree.baseBranch]))
                    .accessibilityIdentifier("pane-worktree-resolve-\(sessionId)")
            }
            let changed = (status?.changedFiles ?? 0) > 0
            Button(L(changed ? "paneWorktree.bar.commitMerge" : "paneWorktree.bar.merge")) { store.mergePaneWorktree(sessionId, commitFirst: changed) }
                .controlSize(.small).disabled(busy || store.hasModal)
                .help(L(changed ? "paneWorktree.bar.commitMergeHelp" : "paneWorktree.bar.mergeHelp", ["base": worktree.baseBranch]))
                .accessibilityIdentifier("pane-worktree-merge-\(sessionId)")
            Menu {
                Button(L("paneWorktree.bar.changes")) { store.showPaneWorktreeChanges(sessionId) }
                Button(L("paneWorktree.bar.terminal")) { store.openPaneWorktreeTerminal(sessionId) }
                Button(L("paneWorktree.bar.finder")) { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: worktree.runFolder) }
                Button(L("paneWorktree.bar.openPort", ["port": String(worktree.port)])) { store.openPaneWorktreePort(sessionId) }
                Divider()
                Button(L("paneWorktree.bar.undo", ["base": worktree.baseBranch])) { store.undoPaneWorktreeMerge(sessionId) }
                    .disabled(worktree.mergedCommit == nil || worktree.preMergeCommit == nil)
                Button(L("paneWorktree.bar.pr")) { store.createPaneWorktreePR(sessionId) }
                Divider()
                Button(L("paneWorktree.bar.discard"), role: .destructive) { store.discardPaneWorktree(sessionId) }
            } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                .disabled(busy || store.hasModal)
                .help(L("paneWorktree.bar.more"))
                .accessibilityIdentifier("pane-worktree-menu-\(sessionId)")
        }
        .padding(.horizontal, DesignMetrics.Spacing.md)
        .frame(height: 28)
        .background(Palette.accent.opacity(0.06))
        .overlay(alignment: .bottom) { Divider().overlay(Palette.border) }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("pane-worktree-bar-\(sessionId)")
        // Read again when a run ends and every 20 seconds while the pane is shown: git off the main thread.
        .task(id: paneStatus + (worktree.mergedCommit ?? "")) {
            while !Task.isCancelled {
                await store.refreshPaneWorktreeStatus(sessionId)
                try? await Task.sleep(for: .seconds(20))
            }
        }
    }

    private func counts(_ status: PaneWorktreeStatus) -> some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Text(verbatim: "↑\(status.ahead)").help(L("paneWorktree.bar.ahead", ["count": String(status.ahead), "base": worktree.baseBranch]))
            if status.behind > 0 {
                Text(verbatim: "↓\(status.behind)").foregroundStyle(Palette.waitText)
                    .help(L("paneWorktree.bar.behind", ["count": String(status.behind), "base": worktree.baseBranch]))
            }
            if status.changedFiles > 0 {
                Button { store.showPaneWorktreeChanges(sessionId) } label: { Text(verbatim: "±\(status.changedFiles)") }
                    .buttonStyle(.plain).foregroundStyle(Palette.run)
                    .help(L("paneWorktree.bar.changed", ["count": String(status.changedFiles)]))
            }
        }
        .font(.ui(size: 10).monospacedDigit()).foregroundStyle(Palette.ink2)
        .accessibilityIdentifier("pane-worktree-counts-\(sessionId)")
    }

    @ViewBuilder private func setup(_ status: PaneWorktree.SetupStatus) -> some View {
        switch status {
        case .running:
            HStack(spacing: DesignMetrics.Spacing.xs) { ProgressView().controlSize(.mini); Text(verbatim: L("paneWorktree.bar.setupRunning")) }
                .font(.ui(size: 10)).foregroundStyle(Palette.ink2)
        case .done:
            Label(L("paneWorktree.bar.setupDone"), systemImage: "checkmark.circle").font(.ui(size: 10)).foregroundStyle(Palette.done)
        case .failed:
            Label(L("paneWorktree.bar.setupFailed"), systemImage: "exclamationmark.triangle").font(.ui(size: 10)).foregroundStyle(Palette.err)
        }
    }
}

/// The change review of a pane worktree: the files it changed against the point it shares with its base on the left, the
/// selected file's diff on the right (``FileDiffView``).
struct PaneWorktreeChangesSheet: View {
    let target: PaneWorktreeChangesTarget
    let onClose: () -> Void
    @ViewState private var files: [PaneWorktreeChangedFile]?
    @ViewState private var selected: PaneWorktreeChangedFile?
    @ViewState private var changes: FileChangeSet?
    @ViewState private var loading = false

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(verbatim: L("paneWorktree.changes.title", ["branch": target.worktree.branch, "base": target.worktree.baseBranch])).font(.ui(size: 13, weight: .semibold))
                Spacer()
                Button(L("paneWorktree.bar.finder")) { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: target.worktree.runFolder) }
                Button(L("common.close"), action: onClose).keyboardShortcut(.cancelAction)
            }
            .padding(DesignMetrics.Spacing.md)
            Divider()
            HStack(spacing: 0) {
                List(files ?? [], selection: Binding(get: { selected }, set: { pick($0) })) { file in
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(verbatim: file.status).font(.ui(size: 10, weight: .bold).monospaced()).foregroundStyle(file.status == "D" ? Palette.err : Palette.run).frame(width: 14)
                        Text(verbatim: file.path).font(.ui(size: 11)).lineLimit(1).truncationMode(.middle)
                    }.tag(file)
                }
                .frame(width: 260)
                Divider()
                Group {
                    if let changes { FileDiffView(changes: changes) }
                    else if loading { ProgressView() }
                    else if files?.isEmpty == true { Text(verbatim: L("paneWorktree.changes.none")).foregroundStyle(Palette.ink2) }
                    else if files == nil { ProgressView() }
                    else { Text(verbatim: L("paneWorktree.changes.pick")).foregroundStyle(Palette.ink2) }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(minWidth: 820, minHeight: 520)
        .task { files = await PaneWorktreeGit.changedFiles(target.worktree) ?? [] }
    }

    private func pick(_ file: PaneWorktreeChangedFile?) {
        selected = file; changes = nil
        guard let file else { return }
        loading = true
        Task {
            let read = await PaneWorktreeGit.fileChanges(target.worktree, path: file.path)
            if selected == file { changes = read; loading = false }
        }
    }
}

/// A workspace's pane worktrees: the ones open panes have, the ones kept after their pane closed with work, and any other
/// git still has under the root; each can be opened as a pane, merged or deleted.
struct PaneWorktreeListSheet: View {
    @EnvironmentObject private var store: AppStore
    let workspaceId: String
    @ViewState private var rows: [PaneWorktreeInventory.Row]?
    @ViewState private var statuses: [String: PaneWorktreeStatus] = [:]
    @ViewState private var messages: [String: String] = [:]
    @ViewState private var working: Set<String> = []

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(verbatim: L("paneWorktree.list.title")).font(.ui(size: 13, weight: .semibold))
                Spacer()
                Button(L("common.close")) { store.paneWorktreeListWorkspace = nil }.keyboardShortcut(.cancelAction)
            }
            .padding(DesignMetrics.Spacing.md)
            Divider()
            if let rows {
                if rows.isEmpty { Text(verbatim: L("paneWorktree.list.empty")).foregroundStyle(Palette.ink2).padding(DesignMetrics.Spacing.lg) }
                else {
                    List(rows) { row in line(row) }
                }
            } else { ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity) }
        }
        .frame(minWidth: 680, minHeight: 360)
        .task { await load() }
    }

    private func load() async {
        let read = await store.paneWorktreeRows(workspaceId)
        rows = read
        for row in read { statuses[row.sessionId] = await PaneWorktreeGit.status(row.worktree) }
    }

    private func line(_ row: PaneWorktreeInventory.Row) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "arrow.triangle.branch").foregroundStyle(Palette.accent)
                Text(verbatim: row.title).font(.ui(size: 12, weight: .semibold)).lineLimit(1)
                Text(verbatim: AppStore.originText(row.origin)).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                Spacer()
                if working.contains(row.sessionId) { ProgressView().controlSize(.mini) }
                Button(L(row.origin == .open ? "paneWorktree.list.show" : "paneWorktree.list.reopen")) { store.reopenPaneWorktree(row, workspaceId: workspaceId) }
                    .controlSize(.small)
                let changed = (statuses[row.sessionId]?.changedFiles ?? 0) > 0
                Button(L(changed ? "paneWorktree.bar.commitMerge" : "paneWorktree.bar.merge")) { act(row) { await store.mergeListedPaneWorktree(row, commitFirst: changed) } }
                    .controlSize(.small).disabled(working.contains(row.sessionId))
                Button(L("paneWorktree.list.delete"), role: .destructive) { act(row) { await store.deleteListedPaneWorktree(row, workspaceId: workspaceId) } }
                    .controlSize(.small).disabled(row.origin == .open || working.contains(row.sessionId))
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(verbatim: L("paneWorktree.bar.branch", ["branch": row.worktree.branch, "base": row.worktree.baseBranch]))
                if let status = statuses[row.sessionId] {
                    Text(verbatim: L("paneWorktree.list.counts", ["ahead": String(status.ahead), "behind": String(status.behind), "changed": String(status.changedFiles)]))
                }
                Text(verbatim: row.worktree.worktreePath).lineLimit(1).truncationMode(.middle)
            }
            .font(.ui(size: 10)).foregroundStyle(Palette.ink2)
            if let message = messages[row.sessionId] { Text(verbatim: message).font(.ui(size: 10)).foregroundStyle(Palette.ink) }
        }
        .padding(.vertical, DesignMetrics.Spacing.xs)
    }

    private func act(_ row: PaneWorktreeInventory.Row, _ action: @escaping () async -> String?) {
        working.insert(row.sessionId)
        Task {
            let message = await action()
            working.remove(row.sessionId)
            messages[row.sessionId] = message
            await load()
        }
    }
}

/// The small branch mark a pane worktree's tab and sidebar row carry; its help names the branch.
struct PaneWorktreeMarker: View {
    let worktree: PaneWorktree
    let size: CGFloat
    var body: some View {
        Image(systemName: "arrow.triangle.branch").font(.ui(size: size)).foregroundStyle(Palette.accent)
            .help(L("paneWorktree.marker", ["branch": worktree.branch, "base": worktree.baseBranch]))
            .accessibilityLabel(L("paneWorktree.marker", ["branch": worktree.branch, "base": worktree.baseBranch]))
    }
}

/// The worktree list sheet's item: the workspace it lists.
struct PaneWorktreeListTarget: Identifiable, Equatable {
    let id: String
}
