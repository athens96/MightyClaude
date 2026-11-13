import AppKit
import Foundation
import MightyCore

/// The choice a new agent pane of a workspace waits on (``PaneWorktreePolicy/decide(workspace:sessions:closing:)`` said
/// `.ask`): its own worktree, or the workspace folder. `gitRefusal` is why the folder cannot host a worktree, nil when it can.
struct PaneWorktreeChoice: Identifiable, Equatable {
    let workspaceId: String
    let provider: String
    let gitRefusal: String?
    var id: String { workspaceId + ":" + provider }
}

/// The change review of a pane worktree: its files against the point it shares with its base.
/// What a worktree action of a pane came to: `done` false is a refusal or failure, which `message` (also in the pane's
/// transcript) explains; the phone shows it as the action's answer.
struct PaneWorktreeActionOutcome: Equatable {
    var done: Bool
    var message: String?
}

struct PaneWorktreeChangesTarget: Identifiable, Equatable {
    let sessionId: String
    let worktree: PaneWorktree
    var id: String { sessionId }
}

/// A workspace's agent panes that work in their own worktree (``PaneWorktree``): made when the pane is added, merged into
/// the workspace branch (committing first when asked), undone, discarded, listed after they closed, and given their own
/// PORT, the workspace's copied files and setup command.
extension AppStore {
    /// Where pane worktrees go: the same root as delegated children's.
    var paneWorktreeRoot: URL { smokeTesting ? dataDirectory.appendingPathComponent("worktrees", isDirectory: true) : ChildWorktreeMaker.defaultRoot }

    // MARK: Opening

    /// ⌘N, the menu and the empty layout's button: a new agent pane of `provider` in the active workspace, where
    /// ``PaneWorktreePolicy`` says.
    func addAgentPane(provider: String = "claude") {
        guard let workspace = activeWorkspace else { return }
        addAgentPaneChoosingFolder(provider: provider, workspaceId: workspace.id)
    }

    /// A new agent pane of `provider` for the workspace: in the workspace folder, in its own worktree, or after the user
    /// chose, as ``PaneWorktreePolicy`` decides.
    func addAgentPaneChoosingFolder(provider: String, workspaceId: String) {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }) else { return }
        switch PaneWorktreePolicy.decide(workspace: workspace, sessions: snapshot.sessions, closing: closingSessions) {
        case .sameFolder:
            selectWorkspace(workspaceId)
            addSession(kind: SessionKind.claude, provider: provider)
        case .worktree:
            // The workspace's setting chose it: a folder that cannot host one opens the pane in the folder, saying why.
            Task { [weak self] in _ = await self?.makeWorktreePane(provider: provider, workspaceId: workspaceId, fallbackToFolder: true) }
        case .ask:
            Task { [weak self] in
                let check = await ChildWorktreeMaker(root: self?.paneWorktreeRoot ?? ChildWorktreeMaker.defaultRoot).check(workspace: workspace.path)
                guard let self, self.snapshot.workspaces.contains(where: { $0.id == workspaceId }) else { return }
                let refusal: String?
                switch check {
                case .ready: refusal = nil
                case .refused(let code): refusal = DelegationText.reason(code)
                }
                self.paneWorktreeChoice = PaneWorktreeChoice(workspaceId: workspaceId, provider: provider, gitRefusal: refusal)
            }
        }
    }

    /// The user's answer to ``paneWorktreeChoice``. `always` also makes the workspace open every further agent pane in
    /// its own worktree (``PaneWorktreeMode/further``).
    func answerPaneWorktreeChoice(_ choice: PaneWorktreeChoice, worktree: Bool, always: Bool = false) {
        paneWorktreeChoice = nil
        if always, let index = snapshot.workspaces.firstIndex(where: { $0.id == choice.workspaceId }) { snapshot.workspaces[index].paneWorktreeMode = .further }
        if worktree, choice.gitRefusal == nil {
            Task { [weak self] in _ = await self?.makeWorktreePane(provider: choice.provider, workspaceId: choice.workspaceId, fallbackToFolder: false) }
        } else {
            guard snapshot.workspaces.contains(where: { $0.id == choice.workspaceId }) else { return }
            selectWorkspace(choice.workspaceId)
            addSession(kind: SessionKind.claude, provider: choice.provider)
        }
    }

    /// Makes the worktree first (branch `mighty/<id>` at the workspace branch's head, the same rules and place as a
    /// delegated child's), then the pane that works in it, copies the workspace's listed files and runs its setup command.
    /// When the worktree cannot be made, `fallbackToFolder` opens the pane in the workspace folder with the reason in it;
    /// otherwise nothing is added and the error says why. The new pane's id, nil when none was added.
    @discardableResult
    func makeWorktreePane(provider: String, workspaceId: String, fallbackToFolder: Bool, reuse id: String? = nil, existing: PaneWorktree? = nil,
                          title: String? = nil) async -> String? {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }), workspace.remote == nil else { return nil }
        let id = id ?? UUID().uuidString
        let taken = Set(snapshot.sessions.compactMap { $0.paneWorktree?.port })
        var pane: PaneWorktree
        if let existing {
            pane = existing
            if taken.contains(pane.port) { pane.port = PaneWorktreePorts.port(for: id, taken: taken) }
        } else {
            let maker = ChildWorktreeMaker(root: paneWorktreeRoot)
            let outcome: ChildWorktreeOutcome
            var checkout = ""
            switch await maker.check(workspace: workspace.path) {
            case .refused(let code): outcome = .refused(code)
            case .ready(let base): outcome = await maker.makePane(sessionId: id, base: base); checkout = base.repository
            }
            let reason: String
            switch outcome {
            case .created(let made):
                pane = PaneWorktree(made, checkout: checkout, port: PaneWorktreePorts.port(for: id, taken: taken), setupCommand: workspace.worktreeSetupCommand)
                reason = ""
            case .refused(let code): reason = DelegationText.reason(code); pane = PaneWorktree(worktreePath: "", branch: "", baseBranch: "", baseCommit: "", checkout: "", port: 0)
            case .failed(let message): reason = message; pane = PaneWorktree(worktreePath: "", branch: "", baseBranch: "", baseCommit: "", checkout: "", port: 0)
            }
            guard reason.isEmpty else {
                guard fallbackToFolder, snapshot.workspaces.contains(where: { $0.id == workspaceId }) else {
                    error = L("paneWorktree.error.cannotMake", ["reason": reason]); return nil
                }
                selectWorkspace(workspaceId)
                return addSession(kind: SessionKind.claude, provider: provider, workspaceId: workspaceId) { session in
                    session.logs.append(LogEntry(kind: "system", text: L("paneWorktree.log.fellBack", ["reason": reason])))
                }
            }
        }
        guard snapshot.workspaces.contains(where: { $0.id == workspaceId }) else { return nil }
        selectWorkspace(workspaceId)
        let made = pane
        let added = addSession(kind: SessionKind.claude, provider: provider, workspaceId: workspaceId, id: id) { session in
            session.workingFolder = made.runFolder
            session.paneWorktree = made
            if let title { session.title = title }
            session.logs.append(LogEntry(kind: "system", text: L(existing == nil ? "paneWorktree.log.made" : "paneWorktree.log.reopened",
                                                                ["branch": made.branch, "base": made.baseBranch, "port": String(made.port)])))
        }
        guard let added else {
            // The pane could not be added (the pane limit): a worktree made for it goes again; a reopened one stays.
            if existing == nil { _ = await PaneWorktreeGit.discard(made, sessionId: id, root: paneWorktreeRoot) }
            return nil
        }
        if existing == nil { preparePaneWorktree(added, pane: made, workspace: workspace) }
        return added
    }

    /// The workspace's listed files copied in, then its setup command, once, in the new worktree: both shown in the pane,
    /// a failure visible but not blocking.
    private func preparePaneWorktree(_ id: String, pane: PaneWorktree, workspace: Workspace) {
        let paths = workspace.worktreeCopyPaths ?? []
        let command = pane.setupCommand
        guard !paths.isEmpty || command != nil else { return }
        let source = URL(fileURLWithPath: workspace.path, isDirectory: true)
        let destination = URL(fileURLWithPath: pane.runFolder, isDirectory: true)
        Task { [weak self] in
            if !paths.isEmpty {
                let copied = await Task.detached(priority: .utility) { PaneWorktreeCopy.copy(paths, from: source, to: destination) }.value
                guard let self, self.snapshot.sessions.contains(where: { $0.id == id }) else { return }
                var text = copied.copied.isEmpty ? "" : L("paneWorktree.log.copied", ["paths": copied.copied.joined(separator: ", ")])
                if !copied.skipped.isEmpty {
                    let skipped = copied.skipped.sorted { $0.key < $1.key }.map { "\($0.key) (\(Self.copySkipText($0.value)))" }.joined(separator: ", ")
                    text += (text.isEmpty ? "" : "\n") + L("paneWorktree.log.copySkipped", ["paths": skipped])
                }
                if !text.isEmpty { self.updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: text)) } }
            }
            if let command, let self { self.runPaneWorktreeSetup(id, command: command, pane: pane, workspaceFolder: workspace.path) }
        }
    }

    private func runPaneWorktreeSetup(_ id: String, command: String, pane: PaneWorktree, workspaceFolder: String) {
        updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L("paneWorktree.log.setupStarted", ["command": command]))) }
        Task { [weak self] in
            var environment = ProviderService.runtimeEnvironment()
            for (key, value) in pane.environment(workspaceFolder: workspaceFolder) { environment[key] = value }
            let shell = URL(fileURLWithPath: environment["SHELL"].flatMap { $0.hasPrefix("/") ? $0 : nil } ?? "/bin/zsh")
            let result = try? await ProcessCapture.run(executable: shell, arguments: ["-l", "-c", command], environment: environment,
                                                       cwd: URL(fileURLWithPath: pane.runFolder, isDirectory: true), timeout: 1_800, maximumBytes: 262_144)
            guard let self, self.snapshot.sessions.contains(where: { $0.id == id }) else { return }
            let code = result?.exitCode ?? -1
            let tail = result.map { Self.lastLines(String(decoding: $0.stdout + $0.stderr, as: UTF8.self), count: 12) } ?? ""
            self.updateSession(id) { session in
                session.paneWorktree?.setupStatus = code == 0 ? .done : .failed
                session.paneWorktree?.setupExitCode = code
                let text = code == 0 ? L("paneWorktree.log.setupDone", ["command": command])
                    : L("paneWorktree.log.setupFailed", ["command": command, "code": String(code)])
                session.logs.append(LogEntry(kind: "system", text: tail.isEmpty ? text : text + "\n\n" + tail))
                session.logs = TranscriptRetention.trimmed(session.logs)
            }
        }
    }

    private static func lastLines(_ text: String, count: Int) -> String {
        text.split(separator: "\n", omittingEmptySubsequences: true).suffix(count).joined(separator: "\n")
    }

    /// A run of a pane worktree: its PORT and folders in the environment, and its first request told where it works.
    func preparePaneWorktreeRun(_ request: inout StartRunRequest, session: RunSession, workspace: Workspace) {
        guard let pane = session.paneWorktree else { return }
        request.paneEnvironment = pane.environment(workspaceFolder: workspace.path)
        guard request.kind == SessionKind.claude, PaneWorktreeNote.applies(pane, input: request.input) else { return }
        request.input = PaneWorktreeNote.attach(pane, to: request.input)
        updateSession(session.id) { $0.paneWorktree?.noteGiven = true }
    }

    // MARK: Merging

    static func copySkipText(_ word: String) -> String {
        switch word {
        case "missing": L("paneWorktree.copy.skip.missing")
        case "link": L("paneWorktree.copy.skip.link")
        case "outside": L("paneWorktree.copy.skip.outside")
        case "exists": L("paneWorktree.copy.skip.exists")
        case "limit": L("paneWorktree.copy.skip.limit")
        default: L("paneWorktree.copy.skip.failed")
        }
    }

    static func undoRefusalText(_ reason: PaneWorktreeUndoOutcome.Refusal, base: String) -> String {
        switch reason {
        case .notMerged: L("paneWorktree.undo.refused.notMerged")
        case .branchNotCheckedOut: L("paneWorktree.undo.refused.branchNotCheckedOut", ["base": base])
        case .baseMoved: L("paneWorktree.undo.refused.baseMoved", ["base": base])
        case .trackedChanges: L("paneWorktree.undo.refused.trackedChanges")
        }
    }

    static func modeText(_ mode: PaneWorktreeMode) -> String {
        switch mode {
        case .ask: L("paneWorktree.mode.ask")
        case .further: L("paneWorktree.mode.further")
        case .every: L("paneWorktree.mode.every")
        }
    }

    static func originText(_ origin: PaneWorktreeInventory.Origin) -> String {
        switch origin {
        case .open: L("paneWorktree.list.origin.open")
        case .kept: L("paneWorktree.list.origin.kept")
        case .found: L("paneWorktree.list.origin.found")
        }
    }

    static func refusalText(_ reason: PaneWorktreeMergeOutcome.Refusal, base: String) -> String {
        switch reason {
        case .worktreeMissing: L("paneWorktree.refused.worktreeMissing")
        case .uncommittedChanges: L("paneWorktree.refused.uncommittedChanges")
        case .nothingToMerge: L("paneWorktree.refused.nothingToMerge", ["base": base])
        case .branchNotCheckedOut: L("paneWorktree.refused.branchNotCheckedOut", ["base": base])
        case .trackedChanges: L("paneWorktree.refused.trackedChanges")
        case .mergeConflict: L("paneWorktree.refused.mergeConflict", ["base": base])
        }
    }

    /// What a merge of `pane` came to, in words, and the record of it: `commitFirst` commits the worktree's changes first
    /// ("커밋하고 병합"). Shared by the pane's bar and the worktree list.
    func mergeText(_ pane: PaneWorktree, commitFirst: Bool) async -> (text: String, merged: (commit: String, preMerge: String)?, conflict: Bool) {
        var lead = ""
        if commitFirst {
            switch await PaneWorktreeGit.commitAll(pane) {
            case .committed(let leftOut), .nothing(let leftOut):
                if !leftOut.isEmpty { return (L("paneWorktree.log.leftOut", ["paths": leftOut.joined(separator: ", ")]), nil, false) }
                lead = L("paneWorktree.log.committed") + "\n"
            case .unmerged: return (L("paneWorktree.refused.unmerged"), nil, false)
            case .failed(let why): return (L("paneWorktree.error.commitFailed", ["reason": why]), nil, false)
            }
        }
        switch await PaneWorktreeGit.merge(pane) {
        case .merged(let commit, let preMerge):
            return (lead + L("paneWorktree.log.merged", ["branch": pane.branch, "base": pane.baseBranch, "commit": String(commit.prefix(8))]), (commit, preMerge), false)
        case .refused(let reason): return (lead + Self.refusalText(reason, base: pane.baseBranch), nil, reason == .mergeConflict)
        case .failed(let message): return (lead + L("paneWorktree.error.mergeFailed", ["reason": message]), nil, false)
        }
    }

    /// The pane's branch into the workspace branch, from the pane's bar. A refusal says why in the pane and changes nothing.
    func mergePaneWorktree(_ id: String, commitFirst: Bool = false) {
        Task { [weak self] in _ = await self?.runPaneWorktreeMerge(id, commitFirst: commitFirst) }
    }

    /// The merge itself, for the bar and the phone: the pane's transcript gets the outcome, which comes back too. Nil when
    /// the pane has no worktree or another action of it is under way.
    func runPaneWorktreeMerge(_ id: String, commitFirst: Bool) async -> PaneWorktreeActionOutcome? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree, !paneWorktreeBusy.contains(id) else { return nil }
        paneWorktreeBusy.insert(id)
        let result = await mergeText(pane, commitFirst: commitFirst)
        paneWorktreeBusy.remove(id)
        if result.conflict { paneWorktreeConflicts.insert(id) } else { paneWorktreeConflicts.remove(id) }
        updateSession(id) { session in
            if let merged = result.merged { session.paneWorktree?.mergedCommit = merged.commit; session.paneWorktree?.preMergeCommit = merged.preMerge }
            session.logs.append(LogEntry(kind: "system", text: result.text)); session.logs = TranscriptRetention.trimmed(session.logs)
        }
        await refreshPaneWorktreeStatus(id)
        return PaneWorktreeActionOutcome(done: result.merged != nil, message: result.text)
    }

    /// Puts the workspace branch back where it was before the pane's last merge.
    func undoPaneWorktreeMerge(_ id: String) {
        Task { [weak self] in _ = await self?.runPaneWorktreeUndo(id) }
    }

    /// The undo itself, for the bar and the phone; nil as for ``runPaneWorktreeMerge(_:commitFirst:)``.
    func runPaneWorktreeUndo(_ id: String) async -> PaneWorktreeActionOutcome? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree, !paneWorktreeBusy.contains(id) else { return nil }
        paneWorktreeBusy.insert(id)
        let outcome = await PaneWorktreeGit.undoMerge(pane)
        paneWorktreeBusy.remove(id)
        let text: String
        var done = false
        switch outcome {
        case .undone:
            updateSession(id) { $0.paneWorktree?.mergedCommit = nil; $0.paneWorktree?.preMergeCommit = nil }
            text = L("paneWorktree.log.undone", ["base": pane.baseBranch])
            done = true
        case .refused(let reason): text = Self.undoRefusalText(reason, base: pane.baseBranch)
        case .failed(let message): text = L("paneWorktree.error.undoFailed", ["reason": message])
        }
        updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: text)); $0.logs = TranscriptRetention.trimmed($0.logs) }
        await refreshPaneWorktreeStatus(id)
        return PaneWorktreeActionOutcome(done: done, message: text)
    }

    /// The merge conflicted: the pane's agent is asked to bring the base branch in, resolve and commit (its next run).
    func askPaneToResolveConflict(_ id: String) {
        Task { [weak self] in _ = await self?.runPaneConflictRequest(id) }
    }

    /// The request itself, for the bar and the phone: `done` when the run started; a pane that cannot take a run now says
    /// why in its transcript and in the outcome. Nil for a pane without a worktree.
    func runPaneConflictRequest(_ id: String) async -> PaneWorktreeActionOutcome? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree,
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return nil }
        if let refusal = startRefusal(session, in: workspace, input: "conflict", attachments: []) {
            let text = L("paneWorktree.conflict.notSent", ["reason": refusal])
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: text)) }
            return PaneWorktreeActionOutcome(done: false, message: text)
        }
        paneWorktreeConflicts.remove(id)
        guard await startRun(sessionId: id, input: PaneWorktreeNote.conflictPrompt(pane)) != nil else {
            paneWorktreeConflicts.insert(id)
            return PaneWorktreeActionOutcome(done: false, message: L("paneWorktree.conflict.notSent", ["reason": error ?? L("remote.error.createFailed")]))
        }
        return PaneWorktreeActionOutcome(done: true, message: nil)
    }

    // MARK: Status

    /// The pane's distance from its base, read off the main thread; the bar shows it.
    func refreshPaneWorktreeStatus(_ id: String) async {
        guard let pane = snapshot.sessions.first(where: { $0.id == id })?.paneWorktree else { paneWorktreeStatus.removeValue(forKey: id); return }
        let status = await PaneWorktreeGit.status(pane)
        guard snapshot.sessions.contains(where: { $0.id == id }) else { return }
        if paneWorktreeStatus[id] != status { paneWorktreeStatus[id] = status }
    }

    func showPaneWorktreeChanges(_ id: String) {
        guard let pane = snapshot.sessions.first(where: { $0.id == id })?.paneWorktree, !hasModal else { return }
        paneWorktreeChangesTarget = PaneWorktreeChangesTarget(sessionId: id, worktree: pane)
    }

    // MARK: Port, terminal, pull request

    /// http://localhost:PORT in the pane's browser (or the browser the workspace chose).
    func openPaneWorktreePort(_ id: String) {
        guard let pane = snapshot.sessions.first(where: { $0.id == id })?.paneWorktree, let url = URL(string: "http://localhost:\(pane.port)") else { return }
        Task { [weak self] in
            guard let self else { return }
            if await !self.openInAgentBrowser(url, agentPaneId: id) { NSWorkspace.shared.open(url) }
        }
    }

    /// A terminal pane whose folder is the pane's worktree, beside it.
    func openPaneWorktreeTerminal(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree else { return }
        addSession(kind: SessionKind.shell, workspaceId: session.workspaceId) { terminal in
            terminal.workingFolder = pane.runFolder
            terminal.title = L("paneWorktree.terminal.title", ["branch": pane.branch])
        }
    }

    /// Pushes the pane's branch to `origin` (never forced) after asking, then opens GitHub's page to make its pull request;
    /// another host only gets the push.
    func createPaneWorktreePR(_ id: String) {
        guard !hasModal, let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree, !paneWorktreeBusy.contains(id) else { return }
        let alert = NSAlert()
        alert.messageText = L("paneWorktree.pr.title")
        alert.informativeText = L("paneWorktree.pr.detail", ["branch": pane.branch, "base": pane.baseBranch])
        alert.addButton(withTitle: L("paneWorktree.pr.confirm"))
        alert.addButton(withTitle: L("common.cancel"))
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        paneWorktreeBusy.insert(id)
        Task { [weak self] in
            let origin = await PaneWorktreePR.origin(pane)
            let failure: String? = origin == nil ? L("paneWorktree.pr.noOrigin") : await PaneWorktreePR.push(pane)
            guard let self else { return }
            self.paneWorktreeBusy.remove(id)
            if let failure {
                self.updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L("paneWorktree.pr.failed", ["reason": failure]))) }
                return
            }
            let repository = origin.flatMap(PaneWorktreePR.gitHubRepository)
            let page = repository.flatMap { PaneWorktreePR.compareURL(repository: $0, base: pane.baseBranch, branch: pane.branch) }
            self.updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L(page == nil ? "paneWorktree.pr.pushed" : "paneWorktree.pr.pushedOpening", ["branch": pane.branch]))) }
            if let page, await !self.openInAgentBrowser(page, agentPaneId: id) { NSWorkspace.shared.open(page) }
        }
    }

    // MARK: Discarding and closing

    /// Asks, then removes the pane's worktree and branch and closes the pane: its unmerged work goes with them. Refused
    /// while children still branch from it.
    func discardPaneWorktree(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree, !paneWorktreeBusy.contains(id) else { return }
        if let blocker = PaneWorktreePolicy.discardBlocker(sessionId: id, sessions: snapshot.sessions, children: delegationChildren) {
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: blocker)) }
            return
        }
        let alert = NSAlert()
        alert.messageText = L("paneWorktree.discard.title")
        alert.informativeText = L("paneWorktree.discard.detail", ["branch": pane.branch])
        alert.alertStyle = .warning
        alert.addButton(withTitle: L("paneWorktree.discard.confirm"))
        alert.addButton(withTitle: L("common.cancel"))
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Task { [weak self] in _ = await self?.runPaneWorktreeDiscard(id) }
    }

    /// The discard itself, once the person confirmed (on the Mac or on the phone): refused while children branch from the
    /// pane, else the run stops, the worktree and branch go and the pane closes. Nil as for ``runPaneWorktreeMerge(_:commitFirst:)``.
    func runPaneWorktreeDiscard(_ id: String) async -> PaneWorktreeActionOutcome? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let pane = session.paneWorktree, !paneWorktreeBusy.contains(id) else { return nil }
        if let blocker = PaneWorktreePolicy.discardBlocker(sessionId: id, sessions: snapshot.sessions, children: delegationChildren) {
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: blocker)) }
            return PaneWorktreeActionOutcome(done: false, message: blocker)
        }
        paneWorktreeBusy.insert(id)
        await stop(id)
        let outcome = await PaneWorktreeGit.discard(pane, sessionId: id, root: paneWorktreeRoot)
        paneWorktreeBusy.remove(id)
        switch outcome {
        case .discarded:
            // Gone already: the close must not look for unmerged work in it.
            updateSession(id) { $0.paneWorktree = nil }
            closeSession(id)
            return PaneWorktreeActionOutcome(done: true, message: nil)
        case .failed(let message):
            let text = L("paneWorktree.error.discardFailed", ["reason": message])
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: text)) }
            return PaneWorktreeActionOutcome(done: false, message: text)
        }
    }

    /// After a pane worktree's pane closed: a worktree with nothing unmerged and no children on its branch goes with it;
    /// one with work (or children) is kept and listed in the workspace's worktree list, and the app says where.
    func paneWorktreeClosed(_ id: String, pane: PaneWorktree, session: RunSession) async {
        let children = PaneWorktreePolicy.discardBlocker(sessionId: id, sessions: snapshot.sessions, children: delegationChildren) != nil
        switch children ? true : await PaneWorktreeGit.hasUnmergedWork(pane) {
        case false?:
            if case .failed(let message) = await PaneWorktreeGit.discard(pane, sessionId: id, root: paneWorktreeRoot) {
                error = L("paneWorktree.error.discardFailed", ["reason": message])
                keepPaneWorktree(session, pane: pane)
            }
        default:
            keepPaneWorktree(session, pane: pane)
            error = L("paneWorktree.closedWithWork", ["branch": pane.branch, "path": pane.worktreePath])
        }
        paneWorktreeStatus.removeValue(forKey: id); paneWorktreeConflicts.remove(id)
    }

    private func keepPaneWorktree(_ session: RunSession, pane: PaneWorktree) {
        guard let index = snapshot.workspaces.firstIndex(where: { $0.id == session.workspaceId }) else { return }
        var kept = snapshot.workspaces[index].keptPaneWorktrees ?? []
        kept.removeAll { $0.sessionId == session.id }
        kept.append(KeptPaneWorktree(sessionId: session.id, title: session.title, provider: session.provider, worktree: pane))
        snapshot.workspaces[index].keptPaneWorktrees = KeptPaneWorktree.normalized(kept, restoring: false)
    }

    private func forgetKeptPaneWorktree(_ sessionId: String, workspaceId: String) {
        guard let index = snapshot.workspaces.firstIndex(where: { $0.id == workspaceId }) else { return }
        snapshot.workspaces[index].keptPaneWorktrees?.removeAll { $0.sessionId == sessionId }
        if snapshot.workspaces[index].keptPaneWorktrees?.isEmpty == true { snapshot.workspaces[index].keptPaneWorktrees = nil }
    }

    // MARK: The worktree list

    func showPaneWorktreeList(_ workspaceId: String) {
        guard !hasModal, snapshot.workspaces.contains(where: { $0.id == workspaceId && $0.remote == nil }) else { return }
        paneWorktreeListWorkspace = workspaceId
    }

    /// The workspace's pane worktrees: open panes', kept ones and any other git still has under the root.
    func paneWorktreeRows(_ workspaceId: String) async -> [PaneWorktreeInventory.Row] {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }), workspace.remote == nil else { return [] }
        let open = snapshot.sessions.filter { $0.workspaceId == workspaceId && $0.paneWorktree != nil }
        let kept = workspace.keptPaneWorktrees ?? []
        let excluded = Set(delegationChildren.map(\.id)).union(snapshot.sessions.filter { $0.parentSessionId != nil }.map(\.id))
        var checkout = open.first?.paneWorktree?.checkout ?? kept.first?.worktree.checkout ?? ""
        var baseBranch: String?
        if case .ready(let base) = await ChildWorktreeMaker(root: paneWorktreeRoot).check(workspace: workspace.path) {
            checkout = base.repository; baseBranch = base.parentBranch
        }
        let onDisk = checkout.isEmpty ? [] : (await PaneWorktreeGit.worktrees(checkout: checkout, root: paneWorktreeRoot) ?? [])
        let taken = Set(snapshot.sessions.compactMap { $0.paneWorktree?.port })
        return PaneWorktreeInventory.rows(open: open, kept: kept, onDisk: onDisk, excluded: excluded, checkout: checkout, baseBranch: baseBranch,
                                          port: { PaneWorktreePorts.port(for: $0, taken: taken) })
    }

    /// A kept or found worktree as a pane again: the pane takes the closed one's id, so its branch stays its own.
    func reopenPaneWorktree(_ row: PaneWorktreeInventory.Row, workspaceId: String) {
        if row.origin == .open { selectSession(row.sessionId); paneWorktreeListWorkspace = nil; return }
        guard !snapshot.sessions.contains(where: { $0.id == row.sessionId }) else { return }
        paneWorktreeListWorkspace = nil
        Task { [weak self] in
            guard let self else { return }
            if await self.makeWorktreePane(provider: row.provider, workspaceId: workspaceId, fallbackToFolder: false, reuse: row.sessionId,
                                           existing: row.worktree, title: row.origin == .kept ? row.title : nil) != nil {
                self.forgetKeptPaneWorktree(row.sessionId, workspaceId: workspaceId)
            }
        }
    }

    /// A listed worktree merged into its base ("커밋하고 병합" when it has changes); the words come back to the list.
    func mergeListedPaneWorktree(_ row: PaneWorktreeInventory.Row, commitFirst: Bool) async -> String {
        if row.origin == .open {
            mergePaneWorktree(row.sessionId, commitFirst: commitFirst)
            return L("paneWorktree.list.mergingInPane")
        }
        let result = await mergeText(row.worktree, commitFirst: commitFirst)
        return result.text
    }

    /// A listed worktree that no pane has open removed with its branch; refused while children still branch from it.
    func deleteListedPaneWorktree(_ row: PaneWorktreeInventory.Row, workspaceId: String) async -> String? {
        guard row.origin != .open else { return L("paneWorktree.list.openPane") }
        if let blocker = PaneWorktreePolicy.discardBlocker(sessionId: row.sessionId, sessions: snapshot.sessions, children: delegationChildren) { return blocker }
        let alert = NSAlert()
        alert.messageText = L("paneWorktree.discard.title")
        alert.informativeText = L("paneWorktree.list.deleteDetail", ["branch": row.worktree.branch])
        alert.alertStyle = .warning
        alert.addButton(withTitle: L("paneWorktree.discard.confirm"))
        alert.addButton(withTitle: L("common.cancel"))
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        switch await PaneWorktreeGit.discard(row.worktree, sessionId: row.sessionId, root: paneWorktreeRoot) {
        case .discarded:
            forgetKeptPaneWorktree(row.sessionId, workspaceId: workspaceId)
            return nil
        case .failed(let message): return L("paneWorktree.error.discardFailed", ["reason": message])
        }
    }

    // MARK: Workspace settings

    /// The workspace row's menu: where its agent panes work.
    func setPaneWorktreeMode(_ workspaceId: String, _ mode: PaneWorktreeMode) {
        guard let index = snapshot.workspaces.firstIndex(where: { $0.id == workspaceId }), snapshot.workspaces[index].remote == nil else { return }
        snapshot.workspaces[index].paneWorktreeMode = mode == .ask ? nil : mode
    }

    /// The workspace row's menu: the setup command each new pane worktree runs once (empty: none).
    func editWorktreeSetupCommand(_ workspaceId: String) {
        guard let current = snapshot.workspaces.first(where: { $0.id == workspaceId }), current.remote == nil,
              let value = askWorkspaceText(title: L("paneWorktree.setup.title"), detail: L("paneWorktree.setup.detail"), value: current.worktreeSetupCommand ?? "", placeholder: "npm ci"),
              let index = snapshot.workspaces.firstIndex(where: { $0.id == workspaceId }) else { return }
        snapshot.workspaces[index].worktreeSetupCommand = PaneWorktreeSetup.normalizedCommand(value)
    }

    /// The workspace row's menu: files copied from the workspace folder into each new pane worktree.
    func editWorktreeCopyPaths(_ workspaceId: String) {
        guard let current = snapshot.workspaces.first(where: { $0.id == workspaceId }), current.remote == nil,
              let value = askWorkspaceText(title: L("paneWorktree.copy.title"), detail: L("paneWorktree.copy.detail"),
                                           value: (current.worktreeCopyPaths ?? []).joined(separator: ", "), placeholder: ".env, .env.local"),
              let index = snapshot.workspaces.firstIndex(where: { $0.id == workspaceId }) else { return }
        snapshot.workspaces[index].worktreeCopyPaths = PaneWorktreeCopy.normalizedPaths(PaneWorktreeCopy.entries(value))
    }

    private func askWorkspaceText(title: String, detail: String, value: String, placeholder: String) -> String? {
        guard !hasModal else { return nil }
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 340, height: 24))
        field.stringValue = value
        field.placeholderString = placeholder
        alert.accessoryView = field
        alert.addButton(withTitle: L("paneWorktree.setup.save"))
        alert.addButton(withTitle: L("common.cancel"))
        alert.window.initialFirstResponder = field
        return alert.runModal() == .alertFirstButtonReturn ? field.stringValue : nil
    }
}
