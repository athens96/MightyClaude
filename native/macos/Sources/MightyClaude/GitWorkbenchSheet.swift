import AppKit
import MightyCore
import SwiftUI

/// The workspace badge's 커밋·PR panel: the changed files to pick, a commit
/// message, commit and push, and the branch's pull request with its checks
/// (`GitWorkbench`). Requests it drafts for the agent go to the workspace's
/// newest agent pane's composer. A panel (kind C).
struct GitWorkbenchSheet: View {
    @EnvironmentObject private var store: AppStore
    let workspace: Workspace
    let onClose: () -> Void

    @ViewState private var changes: [GitChange]?
    @ViewState private var chosen: Set<String> = []
    @ViewState private var message = ""
    @ViewState private var working = false
    @ViewState private var note: String?
    @ViewState private var failure: String?
    @ViewState private var host: GitWorkbench.Host?
    @ViewState private var hostingCLI = false
    @ViewState private var request: GitReviewRequest?
    @ViewState private var requestLoaded = false
    @ViewState private var branch: String?

    private var root: URL { URL(fileURLWithPath: workspace.path, isDirectory: true) }
    private var chosenPaths: [String] { (changes ?? []).map(\.path).filter(chosen.contains) }

    var body: some View {
        DialogScaffold(L("git.workbench.title"), subtitle: branch.map { L("git.workbench.branch", ["branch": $0]) } ?? workspace.path,
                       systemImage: "arrow.triangle.branch",
                       close: DialogClose(busy: working, identifier: "git-workbench-close", action: onClose)) {
            Button { Task { await reload() } } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.plain).foregroundStyle(Palette.ink2).disabled(working)
                .help(L("git.workbench.refresh")).accessibilityLabel(L("git.workbench.refresh"))
        } content: {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                reviewRequestRow
                filesList
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    Text(L("git.commit.message")).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
                    TextEditor(text: $message)
                        .font(.code(size: 12)).frame(height: 72)
                        .scrollContentBackground(.hidden)
                        .padding(DesignMetrics.Spacing.xs)
                        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
                        .accessibilityIdentifier("git-commit-message")
                }
            }
            .frame(minHeight: 420)
        } footer: {
            DialogFooter {
                if working { ProgressView().controlSize(.small) }
                else if let failure { Text(failure).foregroundStyle(Palette.errText).lineLimit(3).textSelection(.enabled) }
                else if let note { Text(note).foregroundStyle(Palette.ink2).lineLimit(2) }
            } buttons: {
                Button(L("git.commit.askAgent")) { askAgentToCommit() }
                    .disabled(chosenPaths.isEmpty || working)
                    .help(L("git.commit.askAgentHelp"))
                Button(L("git.push")) { perform { try await GitWorkbench.push(root: root); return L("git.push.done") } }
                    .disabled(working)
                Button(L("git.commit.button", ["count": "\(chosenPaths.count)"])) { commit() }
                    .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                    .disabled(chosenPaths.isEmpty || message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || working)
                    .accessibilityIdentifier("git-commit")
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .task { await reload() }
    }

    // MARK: - Pull request

    @ViewBuilder private var reviewRequestRow: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "arrow.triangle.pull").foregroundStyle(Palette.accent)
            if !requestLoaded {
                ProgressView().controlSize(.small)
                Text(L("dialog.loading")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            } else if let request {
                Text(L("git.pr.line", ["number": "\(request.number)", "state": stateLabel(request.state)]))
                    .font(.ui(size: 12, weight: .medium))
                checksMark(request.checks)
                Spacer(minLength: 0)
                if request.checks == .failed, host == .github {
                    Button(L("git.pr.fixChecks")) { askToFixChecks() }.disabled(working)
                }
                Button(L("git.pr.open")) { if let url = URL(string: request.url) { NSWorkspace.shared.open(url) } }
            } else if host == nil {
                Text(L("git.pr.noRemote")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                Spacer(minLength: 0)
            } else if !hostingCLI {
                Text(L(host == .gitlab ? "git.pr.error.glabMissing" : "git.pr.error.ghMissing")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                Spacer(minLength: 0)
            } else {
                Text(L("git.pr.none")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                Spacer(minLength: 0)
                Button(L(host == .gitlab ? "git.pr.createGitLab" : "git.pr.create")) { createRequest() }.disabled(working)
                    .help(L("git.pr.createHelp"))
            }
        }
        .padding(DesignMetrics.Spacing.sm)
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
    }

    private func stateLabel(_ state: String) -> String {
        switch state {
        case "open": return L("git.pr.state.open")
        case "merged": return L("git.pr.state.merged")
        case "closed": return L("git.pr.state.closed")
        default: return state
        }
    }

    @ViewBuilder private func checksMark(_ checks: GitReviewRequest.Checks) -> some View {
        switch checks {
        case .none: EmptyView()
        case .pending: Label(L("git.pr.checks.pending"), systemImage: "clock").font(.ui(size: 11)).foregroundStyle(Palette.waitText)
        case .passed: Label(L("git.pr.checks.passed"), systemImage: "checkmark.circle").font(.ui(size: 11)).foregroundStyle(Palette.doneText)
        case .failed: Label(L("git.pr.checks.failed"), systemImage: "xmark.octagon").font(.ui(size: 11)).foregroundStyle(Palette.errText)
        }
    }

    // MARK: - Files

    @ViewBuilder private var filesList: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            HStack {
                Text(L("git.commit.files", ["count": "\(changes?.count ?? 0)"])).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
                Spacer()
                if let changes, !changes.isEmpty {
                    Button(chosen.count == changes.count ? L("runChanges.chooseNone") : L("runChanges.chooseAll")) {
                        chosen = chosen.count == changes.count ? [] : Set(changes.map(\.path))
                    }
                    .buttonStyle(.plain).font(.ui(size: 11)).foregroundStyle(Palette.accent)
                }
            }
            Group {
                if let changes {
                    if changes.isEmpty {
                        Text(L("git.commit.clean")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ScrollView(.vertical) {
                            LazyVStack(alignment: .leading, spacing: 2) {
                                ForEach(changes) { change in fileRow(change) }
                            }
                        }
                    }
                } else {
                    DialogLoading(skeletonRows: 3)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 160, maxHeight: .infinity)
            .padding(DesignMetrics.Spacing.xs)
            .background(Palette.raised.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
        }
    }

    private func fileRow(_ change: GitChange) -> some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Toggle("", isOn: Binding(get: { chosen.contains(change.path) },
                                     set: { if $0 { chosen.insert(change.path) } else { chosen.remove(change.path) } }))
                .toggleStyle(.checkbox).labelsHidden().disabled(working)
                .accessibilityLabel(change.path)
            Text(change.mark).font(.code(size: 11)).frame(width: 14)
                .foregroundStyle(change.mark == "D" || change.mark == "!" ? Palette.errText : change.mark == "?" || change.mark == "A" ? Palette.doneText : Palette.waitText)
            Text(change.originalPath.map { "\($0) → \(change.path)" } ?? change.path)
                .font(.code(size: 11)).lineLimit(1).truncationMode(.middle).help(change.path)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 2)
    }

    // MARK: - Actions

    private func reload() async {
        let root = root
        failure = nil
        let fresh = (try? await GitWorkbench.status(root: root)) ?? []
        // A file picked before stays picked; new files come in picked, conflicts never.
        let known = Set((changes ?? []).map(\.path))
        chosen = Set(fresh.filter { !$0.conflicted && (chosen.contains($0.path) || !known.contains($0.path)) }.map(\.path))
        changes = fresh
        branch = await GitWorkbench.currentBranch(root: root)
        host = await GitWorkbench.remoteHost(root: root)
        if let host {
            hostingCLI = await GitWorkbench.hostingCLI(host) != nil
            request = hostingCLI ? await GitWorkbench.reviewRequest(root: root, host: host) : nil
        } else {
            hostingCLI = false; request = nil
        }
        requestLoaded = true
    }

    private func perform(_ action: @escaping () async throws -> String) {
        working = true; failure = nil; note = nil
        Task {
            do { note = try await action() }
            catch let error as GitWorkbench.Failure { failure = error.message }
            catch { failure = error.localizedDescription }
            working = false
            await reload()
        }
    }

    private func commit() {
        let root = root, paths = chosenPaths, message = message
        perform {
            let sha = try await GitWorkbench.commit(root: root, paths: paths, message: message)
            self.message = ""
            return L("git.commit.done", ["sha": sha, "count": "\(paths.count)"])
        }
    }

    private func createRequest() {
        guard let host else { return }
        let root = root
        perform {
            let printed = try await GitWorkbench.createReviewRequest(root: root, host: host)
            return L("git.pr.created", ["url": printed])
        }
    }

    private func draft(_ text: String) {
        if store.appendToAgentDraft(workspaceId: workspace.id, text: text) != nil {
            onClose()
        } else {
            failure = L("git.agent.noPane")
        }
    }

    private func askAgentToCommit() {
        let files = chosenPaths.map { "- `\($0)`" }.joined(separator: "\n")
        draft(L("git.commit.agentRequest") + "\n" + files)
    }

    private func askToFixChecks() {
        guard let branch else { return }
        let root = root
        working = true; failure = nil
        Task {
            let log = await GitWorkbench.failedCheckLog(root: root, branch: branch)
            working = false
            let text = L("git.pr.fixRequest", ["url": request?.url ?? ""]) + (log.map { "\n\n```\n" + $0 + "\n```" } ?? "")
            draft(text)
        }
    }
}
