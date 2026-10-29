import Foundation

/// The answer to a human's run of a text in a new child worktree (macOS only).
public enum HumanChildStart: Sendable, Equatable {
    /// The child's record is saved in creating; its worktree, pane and first
    /// run follow in the background.
    case started(ChildRecord)
    /// Refused with exactly this reason before anything was made.
    case refused(DelegationReasonCode)
    /// Nothing was made, for this reason in the app's language.
    case failed(String)
}

/// The first run of a child the human started: its whole input, and the
/// app's note as the request that follows it when the text is a slash command.
public struct HumanChildFirstRun: Sendable, Equatable {
    public var input: String
    public var followingNote: String?

    public init(input: String, followingNote: String?) { self.input = input; self.followingNote = followingNote }
}

/// How the app starts a child the human made from its parent's composer.
public struct HumanChildStarter: Sendable {
    /// Starts the child's first run in its pane: its run id, or nil when none started.
    public var start: @Sendable (_ childId: String, _ run: HumanChildFirstRun) async -> String?
    /// The child's worktree, its pane or its first run could not be made
    /// after the send was answered: the child is failed, and the human's
    /// text needs another way.
    public var failed: @Sendable (_ childId: String) async -> Void

    public init(start: @escaping @Sendable (_ childId: String, _ run: HumanChildFirstRun) async -> String?, failed: @escaping @Sendable (_ childId: String) async -> Void) {
        self.start = start; self.failed = failed
    }
}

/// The delegate tool (macOS only): a parent Claude or Codex pane starts a
/// child pane of its own agent, or of the agent it names, on a task, in the
/// child's own worktree, one layer deep.
extension DelegationCoordinator {
    /// The open children one parent may have at once.
    public static let maximumOpenChildren = 3
    /// How deep delegation goes: a parent's children cannot delegate.
    public static let maximumDepth = 1
    /// The starting modes a Claude parent may ask for, narrowest first; a
    /// parent of any agent asks for one of ``DelegationProviders/startingModes(provider:)``.
    public static let startingModes = DelegationProviders.startingModes(provider: "claude")

    /// Whether `mode` lets a `provider` agent act no more freely than
    /// `stored` does, by rank (Claude: plan < manual < acceptEdits < auto <
    /// fullAccess; Codex: manual < acceptEdits < onRequest < fullAccess). A
    /// mode the provider lacks, on either side, is never no wider.
    public static func isNoWider(_ mode: String, than stored: String, provider: String = "claude") -> Bool {
        DelegationProviders.isNoWider(mode, than: stored, provider: provider)
    }

    /// delegate(task, mode, agent) from `caller`. The first rule that applies answers:
    ///
    /// 1. `agent_unknown`: `agent` names no agent (claude, codex,
    ///    antigravity or gemini, any case). Left out or blank, the child runs
    ///    the caller's own agent.
    /// 2. A mode that is not one of the child agent's, one in which that
    ///    child could not change files (a manual Codex child, a plan or manual
    ///    Antigravity child), or an empty task, one with a NUL
    ///    character or one over 64 KiB, is answered with an error. The child's
    ///    first run input holds the whole task, and no run input may hold a
    ///    NUL; 64 KiB is its TASK.md copy's cap, which keeps that input well
    ///    under a run input's 100,000 characters.
    /// 3. The same call earlier in the caller's current run (the same request
    ///    key) gets that same child, in whatever state it is now.
    /// 4. `child_cannot_delegate` (the caller has a parent link), `width_cap`
    ///    (it has three open children, of any agents), `wider_mode` (the mode
    ///    is wider than the caller's stored mode now: by its agent's order,
    ///    or for another agent by ``DelegationProviders/capabilities(provider:mode:)``), `agent_unavailable`
    ///    (another agent than the caller's whose CLI the app last found not
    ///    installed or signed out).
    /// 5. `not_git`, `detached_head`, `unborn_branch`, `low_disk`: the
    ///    caller's folder cannot host a child worktree.
    /// 6. `store_full`: even after pruning, the delegation file has no room
    ///    for the child's record and its report copy.
    ///
    /// Every refusal comes before anything is made and stores no key.
    /// Otherwise the child's record is saved in creating, with the call's
    /// request key and a copy of its TASK.md, and the answer names it while
    /// its start goes on in the background (``startChild(_:base:task:parentAgent:human:)``).
    func delegate(task: String, mode: String, agent: String? = nil, caller: PaneMCPBinding) async -> DelegationResponse {
        let childAgent: String
        if let named = agent, !named.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            guard let found = DelegationProviders.agent(named: named) else {
                return .refusal(.agentUnknown, detail: "\"\(named.prefix(40))\" is not an agent; agent must be claude, codex or antigravity, or left out for your own")
            }
            childAgent = found
        } else {
            childAgent = caller.provider
        }
        let modes = DelegationProviders.startingModes(provider: childAgent)
        let label = ProviderOptions.label(childAgent)
        guard modes.contains(mode) else {
            return .failure(childAgent == caller.provider ? "mode must be one of \(modes.joined(separator: ", "))." : "mode must be one of a \(label) child's: \(modes.joined(separator: ", ")).")
        }
        guard DelegationProviders.childCanWrite(provider: childAgent, mode: mode) else {
            let writing = modes.filter { DelegationProviders.childCanWrite(provider: childAgent, mode: $0) }
            return .failure("\(label.first == "A" ? "An" : "A") \(label) child in \(mode) mode cannot change files, so it could not do the task or leave its work for a merge. Ask for one of \(writing.joined(separator: ", ")), no wider than your own mode.")
        }
        guard !task.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .failure("delegate needs a task: what the child should do.") }
        guard !task.contains("\0") else { return .failure("A task may not contain a NUL character.") }
        guard task.utf8.count <= DelegationFileStore.maximumCopyBytes else { return .failure("A task may be at most 64 KiB.") }
        let parentId = caller.agentPaneId
        guard let parent = await host.paneState(sessionId: parentId) else { return .failure("The calling pane is closed.") }
        let otherAgent = childAgent == caller.provider ? nil : childAgent
        let key = DelegationRequestKey.make(parentSessionId: parentId, parentRunId: parent.runId ?? "", task: task, startingMode: mode, otherAgent: otherAgent)
        if let same = sameCall(key, from: parentId) { return .delegated(same) }
        if let reason = refusal(of: parentId, parent, mode: mode, child: childAgent) { return .refusal(reason) }
        if otherAgent != nil, let why = Self.unavailable(childAgent, await host.agentAvailability(childAgent)) { return .refusal(.agentUnavailable, detail: why) }
        let base: ChildWorktreeBase
        switch await worktrees.check(workspace: parent.folder) {
        case .refused(let reason): return .refusal(reason)
        case .ready(let ready): base = ready
        }
        let context = await pruneContext()
        // Nothing below suspends until the record is saved, so two calls at
        // once cannot both pass these checks.
        if let same = sameCall(key, from: parentId) { return .delegated(same) }
        if let reason = refusal(of: parentId, parent, mode: mode, child: childAgent) { return .refusal(reason) }
        let id = UUID().uuidString.lowercased()
        let child = ChildRecord(id: id, parentSessionId: parentId, worktreePath: worktrees.worktreePath(for: id), parentBranch: base.parentBranch,
                                baseCommit: base.baseCommit, startingMode: mode, requestKey: key, parentCheckout: base.repository, provider: childAgent)
        do {
            guard case .admitted(let next) = try file.admitting(child, task: DelegationCopy(childId: id, kind: .task, revision: 0, contents: Data(task.utf8)), context: context) else { return .refusal(.storeFull) }
            try commit(next, context: context)
        } catch DelegationFileError.full {
            return .refusal(.storeFull)
        } catch {
            return .failure("The delegation file could not be saved, so no child was made.")
        }
        starting.insert(id)
        Task { await self.startChild(id, base: base, task: task, parentAgent: parent.provider) }
        return .delegated(child)
    }

    /// Why the `provider` agent cannot run a child, naming it, or nil when it can.
    static func unavailable(_ provider: String, _ availability: DelegationAgentAvailability) -> String? {
        switch availability {
        case .ready: nil
        case .notInstalled: "\(ProviderOptions.label(provider)) is not installed on this Mac; the user can check it in Mighty Claude's Settings › CLI"
        case .signedOut: "\(ProviderOptions.label(provider)) is not signed in on this Mac; the user can check it in Mighty Claude's Settings › CLI"
        }
    }

    /// The child's start, after delegate (or the human's send) has answered:
    /// its worktree, then its pane of its own agent in the starting mode, then its first run,
    /// which `human` starts when the human's send made the child (with the
    /// send's attachments) and the host otherwise. The child moves to running
    /// once that run has started, as the run its parent awaits, and otherwise
    /// to failed, with one failed_to_start notice when the parent's delegate
    /// made it, or `human`'s failure call when the human's send did. Whatever
    /// a failed start made stays until a human discards the child; the app
    /// never removes it on its own.
    func startChild(_ id: String, base: ChildWorktreeBase, task: String, parentAgent: String, human: HumanChildStarter? = nil) async {
        defer { starting.remove(id) }
        // A human's child that will never start: their text needs another way (its queued row goes back, a quiet count ends).
        guard let child = file.children.first(where: { $0.id == id }), child.state == .creating else { await human?.failed(id); return }
        var runId: String?
        if case .created(let worktree) = await worktrees.make(sessionId: id, base: base, task: task) {
            let pane = DelegationChildPane(sessionId: id, parentSessionId: child.parentSessionId, mode: child.startingMode, folder: worktree.workingFolder,
                                           startedByHuman: !child.notifiesParent, provider: child.agent)
            if await host.createPane(pane) {
                if let human { runId = await human.start(id, Self.humanFirstRun(text: task, worktree: worktree, provider: child.agent, parentAgent: parentAgent)) }
                else { runId = await host.startRun(sessionId: id, input: Self.firstInput(task: task, worktree: worktree, provider: child.agent, parentAgent: parentAgent)) }
            }
        }
        let context = await pruneContext()
        var next = file
        // With no run, the human's text has not gone anywhere: it needs another way on every early return.
        guard let index = next.children.firstIndex(where: { $0.id == id }) else { if runId == nil { await human?.failed(id) }; return }
        if let runId {
            // Once the host has reported this run's start, or even its end, the record is past creating.
            guard next.children[index].state == .creating, next.children[index].noteRun(runId) else { return }
        } else {
            guard next.children[index].apply(.failStart) else { await human?.failed(id); return }
            if next.children[index].notifiesParent { next.notices.append(Notice(id: UUID().uuidString.lowercased(), childId: id, reportRevision: 0, kind: .failedToStart)) }
        }
        // The admitted record kept room for this child's report copy, far more
        // than this needs. Should the disk still refuse, the app goes on with
        // the move and the next save writes it.
        do { try commit(next, context: context) } catch { file = next }
        // A failed start's notice goes to the parent's pane while the child still counts as starting.
        if runId == nil { await deliverPending() }
        if runId == nil, let human { await human.failed(id) }
    }

    /// The first message of a child's first run: its task and how it
    /// reports. A Claude child commits its work itself; a child of any other
    /// agent leaves it for the app's commit (``ChildAutoCommit``). It names
    /// a Claude parent as one.
    static func firstInput(task: String, worktree: ChildWorktree, provider: String = "claude", parentAgent: String? = nil) -> String {
        let from = (parentAgent ?? provider) == "claude" ? "Another Claude pane" : "Another pane"
        let done = provider == "claude"
            ? "When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to \(worktree.reportFile)"
            : "When you are done, write your report to \(worktree.reportFile)"
        let leave = provider == "claude" ? "" : " " + appCommitNote(worktree: worktree)
        return """
        \(from) in Mighty Claude delegated this task to you. You work in your own git worktree on the branch \(worktree.branch); the task is also saved in \(worktree.taskFile).

        \(done): what you did, what is left and what your parent should check.\(leave) Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.

        Task:
        \(task)
        """
    }

    /// How a child of an agent other than Claude leaves its work: the app
    /// commits it on the child's branch when the child writes its report.
    static func appCommitNote(worktree: ChildWorktree) -> String {
        "Do not commit: leave your changes in this worktree. When your run ends with \(worktree.reportFile) changed, Mighty Claude commits every change .gitignore lets in on \(worktree.branch) for you."
    }

    /// The first run of a child the human started: their text as they typed
    /// it, then the app's note on where the child works and how it leaves its
    /// work for the human's merge from its card. A text that is a slash
    /// command (it starts with `/`) runs alone, as the human typed it, so the
    /// note never becomes the command's arguments; the note follows as the
    /// child's next request.
    public static func humanFirstRun(text: String, worktree: ChildWorktree, provider: String = "claude", parentAgent: String? = nil) -> HumanChildFirstRun {
        let note = humanNote(worktree: worktree, provider: provider, parentAgent: parentAgent)
        return text.hasPrefix("/") ? HumanChildFirstRun(input: text, followingNote: note) : HumanChildFirstRun(input: text + "\n\n" + note, followingNote: nil)
    }

    /// The app's note to a child the human started. A Claude child commits
    /// its work itself; a child of any other agent leaves it for the app. It
    /// names a Claude parent as one.
    static func humanNote(worktree: ChildWorktree, provider: String = "claude", parentAgent: String? = nil) -> String {
        let beside = (parentAgent ?? provider) == "claude" ? "another Claude pane" : "another pane"
        if provider == "claude" {
            return "[Mighty Claude] This request runs in its own git worktree on the branch \(worktree.branch), opened beside \(beside) so that pane keeps working undisturbed. When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write a short report to \(worktree.reportFile): what you did and what is left. The person who sent this merges your branch from the app. Do not merge, push or switch branches."
        }
        return "[Mighty Claude] This request runs in its own git worktree on the branch \(worktree.branch), opened beside \(beside) so that pane keeps working undisturbed. When you are done, write a short report to \(worktree.reportFile): what you did and what is left. \(appCommitNote(worktree: worktree)) The person who sent this merges your branch from the app. Do not merge, push or switch branches."
    }

    /// The human's run of `text` in a new child of the Claude, Codex or
    /// Antigravity pane `parentId`, from its composer's second send
    /// (``ComposerSecondSend``), or from its worktree button's menu, which
    /// names the child's `agent`. The child runs the parent's agent unless
    /// `agent` names another, and is made as delegate makes one, in
    /// ``DelegationProviders/humanChildMode(child:parent:parentMode:planFirst:)``:
    /// for the parent's agent, its permission mode now (so never wider), or
    /// plan mode when `planFirst` (the parent's style starts every request in
    /// plan mode) and the agent has one; for another agent, its widest mode
    /// no wider than the parent's in which it can change files. It is marked started
    /// by the human, so it sends the parent no notices and the parent's tools
    /// neither list it nor act on it; it has its own cap of 3 open children
    /// started by the human, of any agents, apart from the 3 the parent's delegate may have.
    /// The first rule that applies answers, and nothing is made by any but the last:
    ///
    /// 1. Delegation is switched off, or `text` is no task (empty, with a NUL
    ///    character or over 64 KiB): a failure in the app's language.
    /// 2. `parent_closed` (no such pane), `claude_only` (no agent pane),
    ///    `agent_unknown` (`agent` is no agent's provider id), another agent
    ///    whose CLI the app last found not installed or signed out, or no
    ///    mode in which the child could change files (a failure in the app's
    ///    language naming why), then delegate's own
    ///    refusals: `child_cannot_delegate`, `width_cap`, `wider_mode`;
    ///    `not_git`, `detached_head`, `unborn_branch`, `low_disk`; `store_full`.
    /// 3. A delegation file the disk refuses: a failure.
    ///
    /// Otherwise the child's record is saved in creating and the answer names
    /// it, while its worktree, pane and first run follow in the background:
    /// `starter` starts that run in the child's pane
    /// (``humanFirstRun(text:worktree:provider:parentAgent:)``), and hears when the worktree, the
    /// pane or that run could not be made after all.
    public func startHumanChild(_ text: String, parentId: String, planFirst: Bool = false, agent: String? = nil, starter: HumanChildStarter) async -> HumanChildStart {
        guard isSwitchOn() else { return .failed(L("delegation.human.error.switchedOff")) }
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !text.contains("\0"), text.utf8.count <= DelegationFileStore.maximumCopyBytes else {
            return .failed(L("delegation.human.error.invalidTask"))
        }
        guard let parent = await host.paneState(sessionId: parentId) else { return .refused(.parentClosed) }
        guard DelegationProviders.isAgentPane(kind: parent.kind, provider: parent.provider) else { return .refused(.claudeOnly) }
        let childAgent = agent ?? parent.provider
        guard DelegationProviders.agents.contains(childAgent) else { return .refused(.agentUnknown) }
        let label = ProviderOptions.label(childAgent)
        if childAgent != parent.provider {
            switch await host.agentAvailability(childAgent) {
            case .ready: break
            case .notInstalled: return .failed(L("delegation.human.error.agentNotInstalled", ["agent": label]))
            case .signedOut: return .failed(L("delegation.human.error.agentSignedOut", ["agent": label]))
            }
        }
        guard let mode = DelegationProviders.humanChildMode(child: childAgent, parent: parent.provider, parentMode: parent.permissionMode, planFirst: planFirst) else {
            return .failed(childAgent == parent.provider ? L("delegation.human.error.readOnlyMode") : L("delegation.human.error.agentNoMode", ["agent": label]))
        }
        if let reason = refusal(of: parentId, parent, mode: mode, child: childAgent, byHuman: true) { return .refused(reason) }
        let base: ChildWorktreeBase
        switch await worktrees.check(workspace: parent.folder) {
        case .refused(let reason): return .refused(reason)
        case .ready(let ready): base = ready
        }
        let context = await pruneContext()
        // Nothing below suspends until the record is saved, so two sends at
        // once cannot both pass the width cap.
        if let reason = refusal(of: parentId, parent, mode: mode, child: childAgent, byHuman: true) { return .refused(reason) }
        let id = UUID().uuidString.lowercased()
        // No repeat to recognise: each send is its own child.
        let key = DelegationRequestKey.make(parentSessionId: parentId, parentRunId: "human-" + id, task: text, startingMode: mode)
        let child = ChildRecord(id: id, parentSessionId: parentId, worktreePath: worktrees.worktreePath(for: id), parentBranch: base.parentBranch,
                                baseCommit: base.baseCommit, startingMode: mode, requestKey: key, parentCheckout: base.repository, startedByHuman: true, provider: childAgent)
        do {
            guard case .admitted(let next) = try file.admitting(child, task: DelegationCopy(childId: id, kind: .task, revision: 0, contents: Data(text.utf8)), context: context) else { return .refused(.storeFull) }
            try commit(next, context: context)
        } catch DelegationFileError.full {
            return .refused(.storeFull)
        } catch {
            return .failed(L("delegation.human.error.notSaved"))
        }
        starting.insert(id)
        Task { await self.startChild(id, base: base, task: text, parentAgent: parent.provider, human: starter) }
        return .started(child)
    }

    /// The parent's child made by the same call: the same request key.
    private func sameCall(_ key: String, from parentId: String) -> ChildRecord? {
        file.children.first { $0.parentSessionId == parentId && $0.requestKey == key }
    }

    /// Why the pane `parentId`, now `parent`, may not have a new child of the
    /// `child` agent in `mode`, or nil when it may. The cap counts the open
    /// children of the same kind, of every agent: those its delegate made, or
    /// (`byHuman`) those the human started from its composer, so neither kind
    /// blocks the other.
    private func refusal(of parentId: String, _ parent: DelegationPaneState, mode: String, child: String, byHuman: Bool = false) -> DelegationReasonCode? {
        // A pane with a parent link is already one layer down.
        let depth = (parent.parentSessionId != nil || file.children.contains(where: { $0.id == parentId })) ? 1 : 0
        if depth >= Self.maximumDepth { return .childCannotDelegate }
        if file.children.filter({ $0.parentSessionId == parentId && $0.state.isOpen && $0.notifiesParent != byHuman }).count >= Self.maximumOpenChildren { return .widthCap }
        if !DelegationProviders.isNoWider(mode, child: child, than: parent.permissionMode, parent: parent.provider) { return .widerMode }
        return nil
    }

    /// What pruning may assume now: which of the file's parents still have an
    /// open pane, and that any merge of a child not discarded may still be
    /// undone, so no save here ever drops one.
    func pruneContext() async -> DelegationPruneContext {
        var open = Set<String>()
        for parent in Set(file.children.map(\.parentSessionId)) {
            if await host.paneState(sessionId: parent) != nil { open.insert(parent) }
        }
        let discarded = Set(file.children.filter { $0.state == .discarded }.map(\.id))
        return DelegationPruneContext(openPaneIds: open, canUndo: { !discarded.contains($0.childId) })
    }

    /// Saves `next`, pruning first when it nears the cap, and holds what was
    /// written. Throws, holding the file as it was, when it cannot be saved.
    func commit(_ next: DelegationFile, context: DelegationPruneContext) throws {
        file = try store.save(next, pruning: context)
    }
}
