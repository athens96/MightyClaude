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

/// The delegate tool (macOS only): a parent Claude pane starts a child Claude
/// pane on a task, in the child's own worktree, one layer deep.
extension DelegationCoordinator {
    /// The open children one parent may have at once.
    public static let maximumOpenChildren = 3
    /// How deep delegation goes: a parent's children cannot delegate.
    public static let maximumDepth = 1
    /// The starting modes a parent may ask for, narrowest first.
    public static let startingModes = ["plan", "manual", "acceptEdits", "auto", "fullAccess"]

    /// Whether `mode` lets Claude act no more freely than `stored` does, by
    /// rank (plan < manual < acceptEdits < auto < fullAccess). An unknown mode
    /// on either side is never no wider.
    public static func isNoWider(_ mode: String, than stored: String) -> Bool {
        guard let asked = startingModes.firstIndex(of: mode), let own = startingModes.firstIndex(of: stored) else { return false }
        return asked <= own
    }

    /// delegate(task, mode) from `caller`. The first rule that applies answers:
    ///
    /// 1. A mode that is not a starting mode, or an empty task, one with a NUL
    ///    character or one over 64 KiB, is answered with an error. The child's
    ///    first run input holds the whole task, and no run input may hold a
    ///    NUL; 64 KiB is its TASK.md copy's cap, which keeps that input well
    ///    under a run input's 100,000 characters.
    /// 2. The same call earlier in the caller's current run (the same request
    ///    key) gets that same child, in whatever state it is now.
    /// 3. `child_cannot_delegate` (the caller has a parent link), `width_cap`
    ///    (it has three open children), `wider_mode` (the mode is wider than
    ///    the caller's stored mode now).
    /// 4. `not_git`, `detached_head`, `unborn_branch`, `low_disk`: the
    ///    caller's folder cannot host a child worktree.
    /// 5. `store_full`: even after pruning, the delegation file has no room
    ///    for the child's record and its report copy.
    ///
    /// Every refusal comes before anything is made and stores no key.
    /// Otherwise the child's record is saved in creating, with the call's
    /// request key and a copy of its TASK.md, and the answer names it while
    /// its start goes on in the background (``startChild(_:base:task:)``).
    func delegate(task: String, mode: String, caller: PaneMCPBinding) async -> DelegationResponse {
        guard Self.startingModes.contains(mode) else { return .failure("mode must be one of \(Self.startingModes.joined(separator: ", ")).") }
        guard !task.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .failure("delegate needs a task: what the child should do.") }
        guard !task.contains("\0") else { return .failure("A task may not contain a NUL character.") }
        guard task.utf8.count <= DelegationFileStore.maximumCopyBytes else { return .failure("A task may be at most 64 KiB.") }
        let parentId = caller.agentPaneId
        guard let parent = await host.paneState(sessionId: parentId) else { return .failure("The calling pane is closed.") }
        let key = DelegationRequestKey.make(parentSessionId: parentId, parentRunId: parent.runId ?? "", task: task, startingMode: mode)
        if let same = sameCall(key, from: parentId) { return .delegated(same) }
        if let reason = refusal(of: parentId, parent, mode: mode) { return .refusal(reason) }
        let base: ChildWorktreeBase
        switch await worktrees.check(workspace: parent.folder) {
        case .refused(let reason): return .refusal(reason)
        case .ready(let ready): base = ready
        }
        let context = await pruneContext()
        // Nothing below suspends until the record is saved, so two calls at
        // once cannot both pass these checks.
        if let same = sameCall(key, from: parentId) { return .delegated(same) }
        if let reason = refusal(of: parentId, parent, mode: mode) { return .refusal(reason) }
        let id = UUID().uuidString.lowercased()
        let child = ChildRecord(id: id, parentSessionId: parentId, worktreePath: worktrees.worktreePath(for: id), parentBranch: base.parentBranch,
                                baseCommit: base.baseCommit, startingMode: mode, requestKey: key, parentCheckout: base.repository)
        do {
            guard case .admitted(let next) = try file.admitting(child, task: DelegationCopy(childId: id, kind: .task, revision: 0, contents: Data(task.utf8)), context: context) else { return .refusal(.storeFull) }
            try commit(next, context: context)
        } catch DelegationFileError.full {
            return .refusal(.storeFull)
        } catch {
            return .failure("The delegation file could not be saved, so no child was made.")
        }
        starting.insert(id)
        Task { await self.startChild(id, base: base, task: task) }
        return .delegated(child)
    }

    /// The child's start, after delegate (or the human's send) has answered:
    /// its worktree, then its pane in the starting mode, then its first run,
    /// which `human` starts when the human's send made the child (with the
    /// send's attachments) and the host otherwise. The child moves to running
    /// once that run has started, as the run its parent awaits, and otherwise
    /// to failed, with one failed_to_start notice when the parent's delegate
    /// made it, or `human`'s failure call when the human's send did. Whatever
    /// a failed start made stays until a human discards the child; the app
    /// never removes it on its own.
    func startChild(_ id: String, base: ChildWorktreeBase, task: String, human: HumanChildStarter? = nil) async {
        defer { starting.remove(id) }
        guard let child = file.children.first(where: { $0.id == id }), child.state == .creating else { return }
        var runId: String?
        if case .created(let worktree) = await worktrees.make(sessionId: id, base: base, task: task) {
            let pane = DelegationChildPane(sessionId: id, parentSessionId: child.parentSessionId, mode: child.startingMode, folder: worktree.workingFolder, startedByHuman: !child.notifiesParent)
            if await host.createPane(pane) {
                if let human { runId = await human.start(id, Self.humanFirstRun(text: task, worktree: worktree)) }
                else { runId = await host.startRun(sessionId: id, input: Self.firstInput(task: task, worktree: worktree)) }
            }
        }
        let context = await pruneContext()
        var next = file
        guard let index = next.children.firstIndex(where: { $0.id == id }) else { return }
        if let runId {
            // Once the host has reported this run's start, or even its end, the record is past creating.
            guard next.children[index].state == .creating, next.children[index].noteRun(runId) else { return }
        } else {
            guard next.children[index].apply(.failStart) else { return }
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

    /// The first message of a child's first run: its task and how it reports.
    static func firstInput(task: String, worktree: ChildWorktree) -> String {
        """
        Another Claude pane in Mighty Claude delegated this task to you. You work in your own git worktree on the branch \(worktree.branch); the task is also saved in \(worktree.taskFile).

        When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to \(worktree.reportFile): what you did, what is left and what your parent should check. Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.

        Task:
        \(task)
        """
    }

    /// The first run of a child the human started: their text as they typed
    /// it, then the app's note on where the child works and how it leaves its
    /// work for the human's merge from its card. A text that is a slash
    /// command (it starts with `/`) runs alone, as the human typed it, so the
    /// note never becomes the command's arguments; the note follows as the
    /// child's next request.
    public static func humanFirstRun(text: String, worktree: ChildWorktree) -> HumanChildFirstRun {
        let note = humanNote(worktree: worktree)
        return text.hasPrefix("/") ? HumanChildFirstRun(input: text, followingNote: note) : HumanChildFirstRun(input: text + "\n\n" + note, followingNote: nil)
    }

    /// The app's note to a child the human started.
    static func humanNote(worktree: ChildWorktree) -> String {
        "[Mighty Claude] This request runs in its own git worktree on the branch \(worktree.branch), opened beside another Claude pane so that pane keeps working undisturbed. When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write a short report to \(worktree.reportFile): what you did and what is left. The person who sent this merges your branch from the app. Do not merge, push or switch branches."
    }

    /// The human's run of `text` in a new child of the Claude pane
    /// `parentId`, from its composer's second send (``ComposerSecondSend``).
    /// The child is made as delegate makes one, in the parent's permission
    /// mode now (so never wider), or in plan mode when `planFirst` (the
    /// parent's style starts every request in plan mode). It is marked started
    /// by the human, so it sends the parent no notices and the parent's tools
    /// neither list it nor act on it; it has its own cap of 3 open children
    /// started by the human, apart from the 3 the parent's delegate may have.
    /// The first rule that applies answers, and nothing is made by any but the last:
    ///
    /// 1. Delegation is switched off, or `text` is no task (empty, with a NUL
    ///    character or over 64 KiB): a failure in the app's language.
    /// 2. `parent_closed` (no such pane), `claude_only`, then delegate's own
    ///    refusals: `child_cannot_delegate`, `width_cap`, `wider_mode`;
    ///    `not_git`, `detached_head`, `unborn_branch`, `low_disk`; `store_full`.
    /// 3. A delegation file the disk refuses: a failure.
    ///
    /// Otherwise the child's record is saved in creating and the answer names
    /// it, while its worktree, pane and first run follow in the background:
    /// `starter` starts that run in the child's pane
    /// (``humanFirstRun(text:worktree:)``), and hears when the worktree, the
    /// pane or that run could not be made after all.
    public func startHumanChild(_ text: String, parentId: String, planFirst: Bool = false, starter: HumanChildStarter) async -> HumanChildStart {
        guard isSwitchOn() else { return .failed(L("delegation.human.error.switchedOff")) }
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !text.contains("\0"), text.utf8.count <= DelegationFileStore.maximumCopyBytes else {
            return .failed(L("delegation.human.error.invalidTask"))
        }
        guard let parent = await host.paneState(sessionId: parentId) else { return .refused(.parentClosed) }
        guard DelegationSwitch.isClaudePane(kind: parent.kind, provider: parent.provider) else { return .refused(.claudeOnly) }
        let mode = planFirst ? "plan" : parent.permissionMode
        if let reason = refusal(of: parentId, parent, mode: mode, byHuman: true) { return .refused(reason) }
        let base: ChildWorktreeBase
        switch await worktrees.check(workspace: parent.folder) {
        case .refused(let reason): return .refused(reason)
        case .ready(let ready): base = ready
        }
        let context = await pruneContext()
        // Nothing below suspends until the record is saved, so two sends at
        // once cannot both pass the width cap.
        if let reason = refusal(of: parentId, parent, mode: mode, byHuman: true) { return .refused(reason) }
        let id = UUID().uuidString.lowercased()
        // No repeat to recognise: each send is its own child.
        let key = DelegationRequestKey.make(parentSessionId: parentId, parentRunId: "human-" + id, task: text, startingMode: mode)
        let child = ChildRecord(id: id, parentSessionId: parentId, worktreePath: worktrees.worktreePath(for: id), parentBranch: base.parentBranch,
                                baseCommit: base.baseCommit, startingMode: mode, requestKey: key, parentCheckout: base.repository, startedByHuman: true)
        do {
            guard case .admitted(let next) = try file.admitting(child, task: DelegationCopy(childId: id, kind: .task, revision: 0, contents: Data(text.utf8)), context: context) else { return .refused(.storeFull) }
            try commit(next, context: context)
        } catch DelegationFileError.full {
            return .refused(.storeFull)
        } catch {
            return .failed(L("delegation.human.error.notSaved"))
        }
        starting.insert(id)
        Task { await self.startChild(id, base: base, task: text, human: starter) }
        return .started(child)
    }

    /// The parent's child made by the same call: the same request key.
    private func sameCall(_ key: String, from parentId: String) -> ChildRecord? {
        file.children.first { $0.parentSessionId == parentId && $0.requestKey == key }
    }

    /// Why the pane `parentId`, now `parent`, may not have a new child in
    /// `mode`, or nil when it may. The cap counts the open children of the
    /// same kind: those its delegate made, or (`byHuman`) those the human
    /// started from its composer, so neither kind blocks the other.
    private func refusal(of parentId: String, _ parent: DelegationPaneState, mode: String, byHuman: Bool = false) -> DelegationReasonCode? {
        // A pane with a parent link is already one layer down.
        let depth = (parent.parentSessionId != nil || file.children.contains(where: { $0.id == parentId })) ? 1 : 0
        if depth >= Self.maximumDepth { return .childCannotDelegate }
        if file.children.filter({ $0.parentSessionId == parentId && $0.state.isOpen && $0.notifiesParent != byHuman }).count >= Self.maximumOpenChildren { return .widthCap }
        if !Self.isNoWider(mode, than: parent.permissionMode) { return .widerMode }
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
