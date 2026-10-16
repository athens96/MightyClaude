import Foundation

/// Which agent panes take part in delegation, and how (macOS only). A child
/// always runs its parent's agent CLI.
///
/// - Claude and Codex panes may be parents by agent: their runs get the
///   delegation server, and their delegate call makes children. A Codex run
///   gets it only where its tool calls can work: acceptEdits or fullAccess.
///   Manual is read-only and refuses every MCP call it cannot ask about, and
///   onRequest declines the MCP prompts the app cannot show.
/// - Claude, Codex and Antigravity panes may be parents by human: ⌘Enter on a
///   busy one runs the text in a new child worktree, in the parent's mode. A
///   Codex or Antigravity parent offers it only in a mode where its child can
///   change files (``childCanWrite(provider:mode:)``). Antigravity has no
///   delegation tools, so its children come only from ⌘Enter.
/// - Only a Claude run takes text while it runs (steer).
/// - A Claude child commits its own work. A Codex or Antigravity child
///   cannot commit outside fullAccess (the Codex sandbox keeps a worktree's
///   git folder read-only, and Antigravity declines `git commit`), so the app
///   commits what it leaves when its report changes (``ChildAutoCommit``).
/// - A Claude parent's merge tool call follows its CLI's permission mode; any
///   other parent may merge by tool only when its run started in fullAccess,
///   and is otherwise refused with `merge_human_only`: the human merges from
///   the child's card.
public enum DelegationProviders {
    /// An agent pane (kind claude) running one of these may delegate by tool.
    static let agentParents: Set<String> = ["claude", "codex"]
    /// The agents of agent panes that take part at all.
    static let agents: Set<String> = ["claude", "codex", "gemini"]

    /// Every permission mode of any provider, narrowest first: how freely the
    /// agent acts without asking. Modes are compared within one provider.
    static let modeRank = ["plan", "manual", "acceptEdits", "onRequest", "auto", "fullAccess"]

    /// An agent pane (kind claude) running Claude, Codex or Antigravity.
    public static func isAgentPane(kind: String, provider: String) -> Bool { kind == SessionKind.claude && agents.contains(provider) }

    public static func canParentByAgent(kind: String, provider: String) -> Bool { kind == SessionKind.claude && agentParents.contains(provider) }

    /// Whether a run in `mode` gets the delegation tools: every Claude run,
    /// and a Codex run in acceptEdits or fullAccess. ⌘Enter does not depend on it.
    public static func attachesDelegation(kind: String, provider: String, mode: String) -> Bool {
        canParentByAgent(kind: kind, provider: provider) && (provider != "codex" || codexApprovesTools(mode: mode))
    }

    /// Whether a Codex run in `mode` approves the delegation server's tools
    /// ahead: where it may write and never asks (acceptEdits, fullAccess).
    /// mighty-terminal's run_in_terminal is approved ahead only in fullAccess
    /// (``PaneMCPBinding/codexMCPArgs(userInstructions:mode:)``).
    public static func codexApprovesTools(mode: String) -> Bool { mode == "acceptEdits" || mode == "fullAccess" }

    /// Whether a `provider` child in `mode` can change files: any Claude mode
    /// (Claude asks the human), Codex acceptEdits, onRequest or fullAccess,
    /// Antigravity acceptEdits or fullAccess.
    public static func childCanWrite(provider: String, mode: String) -> Bool {
        switch provider {
        case "claude": true
        case "codex": ["acceptEdits", "onRequest", "fullAccess"].contains(mode)
        case "gemini": ["acceptEdits", "fullAccess"].contains(mode)
        default: false
        }
    }

    /// Whether the pane, in `mode` now, may have a child the human starts:
    /// an agent pane whose child, in the same mode, can change files.
    public static func canParentByHuman(kind: String, provider: String, mode: String) -> Bool {
        isAgentPane(kind: kind, provider: provider) && childCanWrite(provider: provider, mode: mode)
    }

    /// Whether a running turn of this pane takes text (steer): Claude only.
    public static func canSteer(kind: String, provider: String) -> Bool { kind == SessionKind.claude && provider == "claude" }

    /// The modes a child of `provider` may start in, narrowest first.
    public static func startingModes(provider: String) -> [String] {
        let modes = Set(ProviderOptions.permissionModes(provider: provider))
        return modeRank.filter(modes.contains)
    }

    /// Whether `mode` lets a `provider` agent act no more freely than
    /// `stored` does. A mode that is not one of the provider's, on either
    /// side, is never no wider.
    public static func isNoWider(_ mode: String, than stored: String, provider: String) -> Bool {
        let modes = startingModes(provider: provider)
        guard let asked = modes.firstIndex(of: mode), let own = modes.firstIndex(of: stored) else { return false }
        return asked <= own
    }

    /// Whether a `provider` child in `mode` can commit on its own branch.
    public static func canCommit(provider: String, mode: String) -> Bool { provider == "claude" || mode == "fullAccess" }

    /// Whether a `provider` parent whose run started in `mode` may merge a
    /// child by its merge tool.
    public static func agentMayMerge(provider: String, mode: String) -> Bool { provider == "claude" || mode == "fullAccess" }
}

/// The app's commit of a Codex or Antigravity child's work (macOS only): when
/// a run of the child finishes normally with REPORT.md changed and the
/// worktree changed, what it changed is committed on the child's branch before
/// its head is read for the report. Tracked files go in as changed; new files
/// go in as .gitignore lets them (the notes folder ignores itself, so TASK.md
/// and REPORT.md never go in), except new files that look like secrets or are
/// over ``maximumNewFileBytes``, which stay uncommitted and are named to the
/// human. Never with an unresolved conflict, and never in a folder that is
/// not the child's own worktree.
public enum ChildAutoCommit {
    /// The largest new file the app commits for a child.
    public static let maximumNewFileBytes = 5_000_000
    /// The longest one git step of the commit may take.
    static let timeout: TimeInterval = 300

    /// Whether the app commits for the child now. A Claude child commits
    /// itself, so never; any other child whose report changed, with changes
    /// left and no unmerged path. (A child that could commit and did leaves
    /// no changes, so its permission mode adds nothing here.)
    public static func shouldCommit(provider: String, reportChanged: Bool, hasChanges: Bool, unmerged: Bool) -> Bool {
        provider != "claude" && reportChanged && hasChanges && !unmerged
    }

    /// The message of the commit for report revision `revision`.
    public static func message(childId: String, revision: Int) -> String { "mighty: \(childId) report r\(revision)" }

    /// Whether a new file at `path` (relative to the worktree) looks like a
    /// secret: `.env*`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.ppk`, an SSH key
    /// (`id_rsa*`, `id_ed25519*`, `id_ecdsa*`, `id_dsa*`), `credentials*`,
    /// `secrets.*`, `.npmrc` or `.pypirc`.
    public static func looksSecret(_ path: String) -> Bool {
        let name = (path as NSString).lastPathComponent.lowercased()
        return [".env", "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", "credentials", "secrets."].contains { name.hasPrefix($0) }
            || [".pem", ".key", ".pfx", ".p12", ".ppk"].contains { name.hasSuffix($0) }
            || [".npmrc", ".pypirc"].contains(name)
    }

    /// What the app found or did in the child's worktree.
    public enum Outcome: Sendable, Equatable {
        /// Nothing to commit, or the child is one that commits itself.
        case nothing
        case committed
        /// `git ls-files -u` lists paths: a conflict is unresolved, so nothing was committed.
        case unmerged
        /// The run stopped or errored: nothing is committed until a run of
        /// the child finishes normally.
        case afterNormalFinish
        /// Git could not read or commit the worktree, or it is not the child's
        /// own (why). Nothing was committed.
        case failed(String)
    }

    public struct Result: Sendable, Equatable {
        public var outcome: Outcome
        /// New files left uncommitted: secret-looking or too large.
        public var leftOut: [String] = []
    }

    /// Commits the worktree at `worktreePath` for report revision `revision`
    /// when ``shouldCommit(provider:reportChanged:hasChanges:unmerged:)`` says
    /// so. The report is known to have changed. A lock file left by a git of
    /// the app's own that did not finish in time is removed; one there before
    /// stops the commit.
    static func run(worktreePath: String, childId: String, revision: Int, provider: String) async -> Result {
        guard provider != "claude" else { return Result(outcome: .nothing) }
        guard let top = await DelegationGit.run(["rev-parse", "--show-toplevel", "--absolute-git-dir"], in: worktreePath), top.exitCode == 0 else {
            return Result(outcome: .failed("Git could not read the worktree."))
        }
        let lines = String(decoding: top.stdout, as: UTF8.self).split(separator: "\n").map(String.init)
        guard lines.count == 2, sameFolder(lines[0], worktreePath) else { return Result(outcome: .failed("The child's folder is not its own git worktree.")) }
        let lock = URL(fileURLWithPath: lines[1]).appendingPathComponent("index.lock").path
        guard let unmerged = await DelegationGit.run(["ls-files", "-u"], in: worktreePath, timeout: timeout), unmerged.exitCode == 0,
              let status = await DelegationGit.run(["status", "--porcelain", "--untracked-files=normal"], in: worktreePath, timeout: timeout), status.exitCode == 0 else {
            return Result(outcome: .failed("Git could not read the worktree."))
        }
        if !unmerged.stdout.isEmpty { return Result(outcome: .unmerged) }
        guard shouldCommit(provider: provider, reportChanged: true, hasChanges: !status.stdout.isEmpty, unmerged: false) else { return Result(outcome: .nothing) }
        guard !FileManager.default.fileExists(atPath: lock) else { return Result(outcome: .failed("Another git is working in the worktree (index.lock).")) }
        guard let others = await DelegationGit.run(["ls-files", "-z", "--others", "--exclude-standard"], in: worktreePath, timeout: timeout), others.exitCode == 0 else {
            return Result(outcome: .failed("Git could not read the worktree."))
        }
        let new = others.stdout.split(separator: 0).map { String(decoding: $0, as: UTF8.self) }
        let leftOut = new.filter { looksSecret($0) || size(of: URL(fileURLWithPath: worktreePath).appendingPathComponent($0).path) > maximumNewFileBytes }
        let taken = new.filter { !leftOut.contains($0) }
        /// One git write; a run the app gave up on leaves its lock removed.
        func write(_ arguments: [String], input: Data? = nil) async -> String? {
            guard let result = await DelegationGit.run(arguments, in: worktreePath, timeout: timeout, input: input) else {
                try? FileManager.default.removeItem(atPath: lock)
                return "Git did not finish."
            }
            return result.exitCode == 0 ? nil : DelegationGit.message(result)
        }
        if let why = await write(["add", "-u"]) { return Result(outcome: .failed(why), leftOut: leftOut) }
        if !taken.isEmpty, let why = await write(["--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul"], input: Data(taken.joined(separator: "\0").utf8)) {
            return Result(outcome: .failed(why), leftOut: leftOut)
        }
        guard let staged = await DelegationGit.run(["diff", "--cached", "--quiet"], in: worktreePath, timeout: timeout), staged.exitCode <= 1 else {
            return Result(outcome: .failed("Git could not read the worktree."), leftOut: leftOut)
        }
        // Only what was left out changed: nothing to commit.
        if staged.exitCode == 0 { return Result(outcome: .nothing, leftOut: leftOut) }
        // An app commit never waits on a signing prompt or a hook.
        if let why = await write(["-c", "commit.gpgsign=false", "commit", "-q", "--no-verify", "-m", message(childId: childId, revision: revision)]) {
            return Result(outcome: .failed(why), leftOut: leftOut)
        }
        return Result(outcome: .committed, leftOut: leftOut)
    }

    static func sameFolder(_ a: String, _ b: String) -> Bool {
        URL(fileURLWithPath: a).resolvingSymlinksInPath().standardizedFileURL.path == URL(fileURLWithPath: b).resolvingSymlinksInPath().standardizedFileURL.path
    }

    /// The size of the file at `path`, not following a link; 0 when unknown.
    static func size(of path: String) -> Int { ((try? FileManager.default.attributesOfItem(atPath: path))?[.size] as? NSNumber)?.intValue ?? 0 }
}
