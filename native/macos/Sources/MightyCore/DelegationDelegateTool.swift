import Foundation

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
    /// 1. A mode that is not a starting mode, or an empty task, is answered
    ///    with an error.
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

    /// The child's start, after delegate has answered: its worktree, then its
    /// pane in the starting mode, then its first run. The child moves to
    /// running once that run has started, as the run its parent awaits, and
    /// otherwise to failed with one failed_to_start notice. Whatever a failed
    /// start made stays until a human discards the child; the app never
    /// removes it on its own.
    func startChild(_ id: String, base: ChildWorktreeBase, task: String) async {
        defer { starting.remove(id) }
        guard let child = file.children.first(where: { $0.id == id }), child.state == .creating else { return }
        var runId: String?
        if case .created(let worktree) = await worktrees.make(sessionId: id, base: base, task: task) {
            let pane = DelegationChildPane(sessionId: id, parentSessionId: child.parentSessionId, mode: child.startingMode, folder: worktree.workingFolder)
            if await host.createPane(pane) {
                runId = await host.startRun(sessionId: id, input: Self.firstInput(task: task, worktree: worktree))
            }
        }
        let context = await pruneContext()
        var next = file
        guard let index = next.children.firstIndex(where: { $0.id == id }) else { return }
        if let runId {
            // The host may have reported this run's start, or even its end, already.
            guard next.children[index].runId == runId || next.children[index].noteRun(runId) else { return }
        } else {
            guard next.children[index].apply(.failStart) else { return }
            next.notices.append(Notice(id: UUID().uuidString.lowercased(), childId: id, reportRevision: 0, kind: .failedToStart))
        }
        // The admitted record kept room for this child's report copy, far more
        // than this needs. Should the disk still refuse, the app goes on with
        // the move and the next save writes it.
        do { try commit(next, context: context) } catch { file = next }
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

    /// The parent's child made by the same call: the same request key.
    private func sameCall(_ key: String, from parentId: String) -> ChildRecord? {
        file.children.first { $0.parentSessionId == parentId && $0.requestKey == key }
    }

    /// Why the pane `parentId`, now `parent`, may not delegate in `mode`, or
    /// nil when it may.
    private func refusal(of parentId: String, _ parent: DelegationPaneState, mode: String) -> DelegationReasonCode? {
        // A pane with a parent link is already one layer down.
        let depth = (parent.parentSessionId != nil || file.children.contains(where: { $0.id == parentId })) ? 1 : 0
        if depth >= Self.maximumDepth { return .childCannotDelegate }
        if file.children.filter({ $0.parentSessionId == parentId && $0.state.isOpen }).count >= Self.maximumOpenChildren { return .widthCap }
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
