import Darwin
import Foundation

/// A workspace's agent pane that works in its own git worktree instead of the workspace folder (macOS only), so two agents
/// of one workspace can work at once without writing over each other's files. It has no parent: it is made with the same
/// rules and in the same place as a delegated child's worktree (``ChildWorktreeMaker/makePane(sessionId:base:timeout:)``,
/// branch `mighty/<session id>` at the workspace branch's head), and its run folder is the session's `workingFolder` like a
/// child's (``DelegationPanes/runWorkspace(for:in:)``). What it adds is kept here, on the session (`RunSession.paneWorktree`).
public struct PaneWorktree: Codable, Sendable, Equatable {
    /// The worktree's top folder, `<root>/<session id>`.
    public var worktreePath: String
    /// `mighty/<session id>`.
    public var branch: String
    /// The workspace folder's branch and head when the worktree was made: the branch a merge goes into.
    public var baseBranch: String
    public var baseCommit: String
    /// The top folder of the workspace's checkout, where the branch lives and where merge and discard run git.
    public var checkout: String
    /// The `PORT` every run of the pane gets, so two panes' dev servers do not collide.
    public var port: Int
    /// The workspace's setup command run once in the new worktree (`Workspace.worktreeSetupCommand`), and how it went.
    public var setupCommand: String?
    public var setupStatus: SetupStatus?
    public var setupExitCode: Int32?
    /// The commit the workspace branch moved to by the last merge from the pane; nil before one.
    public var mergedCommit: String?
    /// The workspace branch's head before that merge: where an undo puts it back (``PaneWorktreeGit/undoMerge(_:)``).
    public var preMergeCommit: String?
    /// The folder the pane's runs work in: the worktree, or the folder in it that matches the workspace when the workspace
    /// is a subfolder of its repository. nil in records from before it was kept (the worktree itself).
    public var workingFolder: String?
    /// Whether the pane's agent was told it works in this worktree (``PaneWorktreeNote``): its first request carries the note.
    public var noteGiven: Bool?

    public enum SetupStatus: String, Codable, Sendable { case running, done, failed }

    public init(worktreePath: String, branch: String, baseBranch: String, baseCommit: String, checkout: String, port: Int,
                setupCommand: String? = nil, setupStatus: SetupStatus? = nil, setupExitCode: Int32? = nil, mergedCommit: String? = nil,
                preMergeCommit: String? = nil, workingFolder: String? = nil, noteGiven: Bool? = nil) {
        self.worktreePath = worktreePath; self.branch = branch; self.baseBranch = baseBranch; self.baseCommit = baseCommit; self.checkout = checkout
        self.port = port; self.setupCommand = setupCommand; self.setupStatus = setupStatus; self.setupExitCode = setupExitCode; self.mergedCommit = mergedCommit
        self.preMergeCommit = preMergeCommit; self.workingFolder = workingFolder; self.noteGiven = noteGiven
    }

    /// The record of `made`, a pane worktree in the checkout `checkout`.
    public init(_ made: ChildWorktree, checkout: String, port: Int, setupCommand: String?) {
        self.init(worktreePath: made.worktreePath, branch: made.branch, baseBranch: made.parentBranch, baseCommit: made.baseCommit, checkout: checkout,
                  port: port, setupCommand: setupCommand, setupStatus: setupCommand == nil ? nil : .running, workingFolder: made.workingFolder)
    }

    /// The folder the pane's runs work in.
    public var runFolder: String { workingFolder ?? worktreePath }

    /// The values every run of the pane (and its setup command) gets: its `PORT`, the workspace folder it was made from
    /// (where git-ignored files such as `.env` live) and its own worktree.
    public func environment(workspaceFolder: String) -> [String: String] {
        ["PORT": String(port), "MIGHTY_WORKSPACE_FOLDER": workspaceFolder, "MIGHTY_WORKTREE": runFolder]
    }

    /// A saved record, or nil when it is not one this app made: absolute paths, the pane's own branch, a port a server can
    /// bind. A setup still running when the app quit reads as failed.
    public func normalized(sessionId: String, restoring: Bool) -> PaneWorktree? {
        guard worktreePath.hasPrefix("/"), checkout.hasPrefix("/"), !worktreePath.contains("\0"), !checkout.contains("\0"),
              branch == ChildRecord.branchName(for: sessionId), !baseBranch.isEmpty, !baseBranch.contains("\0"),
              PaneWorktreePorts.range.contains(port) else { return nil }
        var copy = self
        if let command = setupCommand { copy.setupCommand = PaneWorktreeSetup.normalizedCommand(command) }
        if copy.setupCommand == nil { copy.setupStatus = nil; copy.setupExitCode = nil }
        if restoring, copy.setupStatus == .running { copy.setupStatus = .failed }
        if let merged = mergedCommit, !DelegationGit.isObjectName(merged) { copy.mergedCommit = nil }
        if let pre = preMergeCommit, copy.mergedCommit == nil || !DelegationGit.isObjectName(pre) { copy.preMergeCommit = nil }
        if let folder = workingFolder, !(folder == worktreePath || folder.hasPrefix(worktreePath + "/")) || folder.contains("\0") { copy.workingFolder = nil }
        if copy.noteGiven != true { copy.noteGiven = nil }
        return copy
    }
}

/// Where a workspace's agent panes work. Saved on the workspace; a value this build does not know reads as `.ask`.
public enum PaneWorktreeMode: String, Codable, Sendable, CaseIterable {
    /// The first agent pane works in the workspace folder; a further one asks (the default).
    case ask
    /// The first works in the workspace folder; every further one opens in its own worktree without asking.
    case further
    /// Every agent pane, the first too, opens in its own worktree: the workspace folder only receives merges.
    case every

    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = PaneWorktreeMode(rawValue: raw) ?? .ask
    }
}

/// A pane worktree kept after its pane closed with work still unmerged (or children still on its branch): what the
/// workspace's worktree list needs to reopen it as a pane, merge it or delete it.
public struct KeptPaneWorktree: Codable, Sendable, Equatable, Identifiable {
    /// The closed pane's session id: the branch is `mighty/<id>`, and a reopened pane takes the id again.
    public var sessionId: String
    public var title: String
    public var provider: String
    public var worktree: PaneWorktree
    public var keptAt: String
    public var id: String { sessionId }

    public init(sessionId: String, title: String, provider: String, worktree: PaneWorktree, keptAt: String = mightyTimestamp()) {
        self.sessionId = sessionId; self.title = title; self.provider = provider; self.worktree = worktree; self.keptAt = keptAt
    }

    /// The records worth keeping from a saved list: valid ids and worktrees, one per session, the newest 50.
    public static func normalized(_ list: [KeptPaneWorktree]?, restoring: Bool) -> [KeptPaneWorktree]? {
        guard let list else { return nil }
        var seen = Set<String>()
        let kept = list.reversed().compactMap { record -> KeptPaneWorktree? in
            guard CoreValidation.identifier(record.sessionId), seen.insert(record.sessionId).inserted,
                  let worktree = record.worktree.normalized(sessionId: record.sessionId, restoring: restoring) else { return nil }
            var copy = record
            copy.worktree = worktree
            copy.title = String(record.title.prefix(200))
            copy.provider = ProviderOptions.normalizeProvider(record.provider)
            return copy
        }.prefix(50).reversed()
        return kept.isEmpty ? nil : Array(kept)
    }
}

/// Where a new agent pane of a workspace works.
public enum PaneWorktreeDecision: Sendable, Equatable {
    /// In the workspace folder, as every pane did before: the workspace's first agent pane, a remote workspace, or a pane
    /// that continues an earlier session (whose records belong to the workspace folder).
    case sameFolder
    /// In a new worktree, without asking: the workspace's ``PaneWorktreeMode`` says so.
    case worktree
    /// The user chooses: another agent pane already works in the workspace folder.
    case ask
}

public enum PaneWorktreePolicy {
    /// Whether `session` is an agent pane of `workspaceId` that works in the workspace folder itself: not a delegated
    /// child, not a pane in its own worktree.
    public static func worksInWorkspaceFolder(_ session: RunSession, workspaceId: String) -> Bool {
        session.workspaceId == workspaceId && session.kind == SessionKind.claude && session.parentSessionId == nil && session.workingFolder == nil
    }

    /// Where a new agent pane of `workspace` works. The rule is "open", not "running": a pane open in the workspace folder
    /// may run whenever its user sends, so a second one there is asked about as soon as it is added. `closing` are the
    /// panes going away now, which no longer count.
    public static func decide(workspace: Workspace, sessions: [RunSession], closing: Set<String> = []) -> PaneWorktreeDecision {
        guard workspace.remote == nil else { return .sameFolder }
        let mode = workspace.paneWorktreeMode ?? .ask
        if mode == .every { return .worktree }
        let others = sessions.filter { !closing.contains($0.id) && worksInWorkspaceFolder($0, workspaceId: workspace.id) }
        guard !others.isEmpty else { return .sameFolder }
        return mode == .further ? .worktree : .ask
    }

    /// The same decision where nobody can be asked (a pane added from the phone): `.ask` becomes the workspace folder.
    public static func decideWithoutAsking(workspace: Workspace, sessions: [RunSession], closing: Set<String> = []) -> PaneWorktreeDecision {
        let decision = decide(workspace: workspace, sessions: sessions, closing: closing)
        return decision == .ask ? .sameFolder : decision
    }

    /// Why the pane `sessionId` may not be discarded now, nil when it may: ⌘Enter or delegated children still branch from
    /// its branch (an open child pane, or a child record that is still open), and their merges go into it.
    public static func discardBlocker(sessionId: String, sessions: [RunSession], children: [DelegationChildRow]) -> String? {
        let openChildPanes = sessions.filter { $0.parentSessionId == sessionId }.count
        let openRecords = children.filter { $0.parentSessionId == sessionId && $0.state.isOpen }.count
        let count = max(openChildPanes, openRecords)
        return count > 0 ? L("paneWorktree.refused.hasChildren", ["count": String(count)]) : nil
    }
}

/// A port for each worktree pane's `PORT`: from the pane's id, so the same pane asks for the same one, then the next one
/// that no other pane holds and nothing on this Mac listens on.
public enum PaneWorktreePorts {
    public static let range = 4100...4899

    public static func port(for sessionId: String, taken: Set<Int>, isFree: (Int) -> Bool = PaneWorktreePorts.canBind) -> Int {
        let span = range.count
        // FNV-1a: the same id gives the same start on every launch (Swift's own hash does not).
        var hash: UInt32 = 2_166_136_261
        for byte in sessionId.utf8 { hash = (hash ^ UInt32(byte)) &* 16_777_619 }
        let start = Int(hash % UInt32(span))
        for step in 0..<span {
            let port = range.lowerBound + (start + step) % span
            if !taken.contains(port), isFree(port) { return port }
        }
        return range.lowerBound + start
    }

    /// Whether a TCP server could listen on `port` on this Mac's loopback now.
    public static func canBind(_ port: Int) -> Bool {
        let socket = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard socket >= 0 else { return false }
        defer { close(socket) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(socket, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0 }
        }
    }
}

/// The workspace's setup command for a new worktree (`npm ci`, …).
public enum PaneWorktreeSetup {
    /// One line, at most 1,000 characters; empty is none.
    public static func normalizedCommand(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let line = raw.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces)
        guard !line.isEmpty, !line.contains("\0") else { return nil }
        return String(line.prefix(1_000))
    }
}

/// How a merge from a worktree pane went.
public enum PaneWorktreeMergeOutcome: Sendable, Equatable {
    /// The workspace branch now holds the pane's branch: by fast-forward or one merge commit. `preMerge` is where it was.
    case merged(commit: String, preMerge: String)
    /// Refused before any git write; nothing changed.
    case refused(Refusal)
    /// Git was missing or failed (the words are in the app's language, with git's own output kept as it is).
    case failed(String)

    public enum Refusal: String, Sendable, Equatable {
        /// The worktree is gone.
        case worktreeMissing
        /// The worktree has changes not committed yet, new files included: they would not go with the merge.
        /// ``PaneWorktreeGit/commitAll(_:)`` commits them ("커밋하고 병합").
        case uncommittedChanges
        /// The pane's branch has nothing the workspace branch lacks.
        case nothingToMerge
        /// The workspace folder no longer has the branch the pane was made from checked out.
        case branchNotCheckedOut
        /// The workspace folder has changes to tracked files.
        case trackedChanges
        /// The two branches change the same lines.
        case mergeConflict
    }
}

/// Merges a worktree pane's branch into the workspace branch, and discards the worktree. The same git as a delegated
/// child's card merge (``ChildMerge/cardMerge(_:parentCheckout:)``) and discard (``ChildCleanup``).
public enum PaneWorktreeGit {
    static let timeout: TimeInterval = 60

    /// The pane's commits into the branch the workspace folder was on when the pane was made. The first rule that applies
    /// answers, and every refusal comes before any git write. The merge commit, when the workspace branch moved meanwhile,
    /// holds exactly the tree `git merge-tree` checked; the branch, index and files then move by one `git merge --ff-only`,
    /// which never overwrites an untracked file. The pane's branch is only read.
    public static func merge(_ pane: PaneWorktree) async -> PaneWorktreeMergeOutcome {
        guard DelegationGit.executable != nil else { return .failed(L("delegation.card.error.gitMissing")) }
        guard ChildMerge.isFolder(pane.worktreePath) else { return .refused(.worktreeMissing) }
        // New files count too: a merge never leaves something the agent made behind.
        switch await PaneWorktreeGit.hasAnyChanges(pane.worktreePath) {
        case nil: return .failed(L("delegation.card.error.gitUnfinished"))
        case true?: return .refused(.uncommittedChanges)
        case false?: break
        }
        guard let found = await DelegationGit.run(["rev-parse", "-q", "--verify", "refs/heads/\(pane.branch)^{commit}"], in: pane.checkout), found.exitCode == 0 else {
            return .refused(.worktreeMissing)
        }
        let head = DelegationGit.line(found.stdout)
        guard DelegationGit.isObjectName(head) else { return .refused(.worktreeMissing) }
        guard let preMerge = await ChildMerge.checkedOutHead(of: pane.baseBranch, in: pane.checkout) else { return .refused(.branchNotCheckedOut) }
        guard let contained = await DelegationGit.run(["merge-base", "--is-ancestor", head, preMerge], in: pane.checkout) else { return .failed(L("delegation.card.error.gitUnfinished")) }
        if contained.exitCode == 0 { return .refused(.nothingToMerge) }
        switch await ChildMerge.hasTrackedChanges(pane.checkout) {
        case nil: return .failed(L("delegation.card.error.parentChangesUnread"))
        case true?: return .refused(.trackedChanges)
        case false?: break
        }
        guard let preview = await DelegationGit.run(["merge-tree", "--write-tree", preMerge, head], in: pane.checkout, timeout: timeout) else { return .failed(L("delegation.card.error.gitUnfinished")) }
        guard preview.exitCode != 1 else { return .refused(.mergeConflict) }
        let tree = String(DelegationGit.line(preview.stdout).prefix { $0 != "\n" })
        guard preview.exitCode == 0, DelegationGit.isObjectName(tree) else { return .failed(DelegationGit.message(preview)) }
        guard let ancestor = await DelegationGit.run(["merge-base", "--is-ancestor", preMerge, head], in: pane.checkout) else { return .failed(L("delegation.card.error.gitUnfinished")) }
        let target: String
        switch ancestor.exitCode {
        case 0: target = head
        case 1:
            let message = "Merge branch '\(pane.branch)' into \(pane.baseBranch)"
            guard let made = await DelegationGit.run(["commit-tree", tree, "-p", preMerge, "-p", head, "-m", message], in: pane.checkout, timeout: timeout) else { return .failed(L("delegation.card.error.gitUnfinished")) }
            let commit = DelegationGit.line(made.stdout)
            guard made.exitCode == 0, DelegationGit.isObjectName(commit) else { return .failed(DelegationGit.message(made)) }
            target = commit
        default: return .failed(DelegationGit.message(ancestor))
        }
        guard let moved = await DelegationGit.run(["merge", "--ff-only", "--no-autostash", "--no-stat", "-q", target], in: pane.checkout, timeout: timeout) else { return .failed(L("delegation.card.error.gitUnfinished")) }
        guard moved.exitCode == 0 else { return .failed(DelegationGit.message(moved)) }
        guard await ChildMerge.head(of: pane.checkout) == target else { return .failed(L("delegation.card.error.parentNotAtMerge")) }
        return .merged(commit: target, preMerge: preMerge)
    }

    /// Whether `folder` has changes git would show: tracked or new files, ignored ones aside. nil when git could not tell.
    static func hasAnyChanges(_ folder: String) async -> Bool? {
        guard let status = await DelegationGit.run(["status", "--porcelain", "--untracked-files=normal"], in: folder), status.exitCode == 0 else { return nil }
        return !status.stdout.isEmpty
    }

    /// Whether the pane holds work the workspace branch does not: commits on its branch the branch lacks, or changes in
    /// its worktree, tracked or not. nil when git could not tell (counts as work: nothing is thrown away on a guess).
    public static func hasUnmergedWork(_ pane: PaneWorktree) async -> Bool? {
        guard ChildMerge.isFolder(pane.worktreePath) else { return false }
        guard let status = await DelegationGit.run(["status", "--porcelain"], in: pane.worktreePath), status.exitCode == 0 else { return nil }
        if !status.stdout.isEmpty { return true }
        guard let found = await DelegationGit.run(["rev-parse", "-q", "--verify", "refs/heads/\(pane.branch)^{commit}"], in: pane.checkout) else { return nil }
        guard found.exitCode == 0 else { return false }
        let head = DelegationGit.line(found.stdout)
        guard let contained = await DelegationGit.run(["merge-base", "--is-ancestor", head, "refs/heads/\(pane.baseBranch)"], in: pane.checkout) else { return nil }
        switch contained.exitCode {
        case 0: return false
        case 1: return true
        default: return nil
        }
    }

    /// The worktree, every worktree nested in it, a leftover folder at its place and its branch, whichever exist; only the
    /// pane's own place under `root` is ever forced away.
    public static func discard(_ pane: PaneWorktree, sessionId: String, root: URL) async -> ChildDiscardOutcome {
        await ChildCleanup.removeOwnWorktree(id: sessionId, worktreePath: pane.worktreePath, branch: pane.branch, checkout: pane.checkout, worktreeRoot: root)
    }
}
