import Foundation

/// Which agent panes take part in delegation, and how (macOS only). A child
/// runs the agent its parent chose: its own by default, or (on the Mac) any
/// of Claude, Codex and Antigravity.
///
/// - Claude and Codex panes may be parents by agent: their runs get the
///   delegation server, and their delegate call makes children, of their own
///   agent unless the call names another (`agent`). A Codex run gets it only
///   where its tool calls can work: acceptEdits or fullAccess. Manual is
///   read-only and refuses every MCP call it cannot ask about, and onRequest
///   declines the MCP prompts the app cannot show.
/// - Claude, Codex and Antigravity panes may be parents by human: ⌘Enter on a
///   busy one runs the text in a new child worktree of the same agent, in the
///   parent's mode; the worktree button's menu picks any agent, in
///   ``widestChildMode(child:parent:parentMode:)``. A Codex or Antigravity parent
///   offers it only in a mode where its own child can change files
///   (``childCanWrite(provider:mode:)``). Antigravity has no delegation
///   tools (agy takes MCP servers only from its global config, which the app
///   never touches), so its children come only from the human.
/// - A child of the parent's agent may start in a mode no wider than the
///   parent's by ``modeRank``. A child of another agent may start in a mode
///   that may do nothing the parent's mode may not
///   (``capabilities(provider:mode:)``). A child's mode is always one of its
///   own agent's.
/// - Only a Claude run takes text while it runs (steer).
/// - A Claude child commits its own work. A Codex or Antigravity child
///   cannot commit outside fullAccess (the Codex sandbox keeps a worktree's
///   git folder read-only, and Antigravity declines `git commit`), so the app
///   commits what it leaves when its report changes (``ChildAutoCommit``).
///   Everything that runs or reads the child follows the child's agent;
///   notices and merges follow the parent's.
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
    /// agent acts without asking. Modes are compared by it within one agent.
    static let modeRank = ["plan", "manual", "acceptEdits", "onRequest", "auto", "fullAccess"]

    /// What a run of `provider` in `mode` may do, as the app starts it
    /// (``ProviderService/arguments(_:pluginDirectory:allowPermissionPrompts:phaseModels:paneMCPBinding:codexHome:)``):
    ///
    /// - Claude (`--permission-mode` as stored, fullAccess as
    ///   bypassPermissions): plan edits nothing until its plan is approved;
    ///   manual asks the human before each edit; acceptEdits edits unasked and
    ///   still asks before Bash; auto also runs commands unasked; fullAccess
    ///   is full access.
    /// - Codex: manual (read-only sandbox, approval never) runs commands
    ///   unasked but edits nothing; onRequest (workspace-write, on-request)
    ///   edits in its sandbox unasked and asks to go beyond it; acceptEdits
    ///   (workspace-write, approval never) edits and runs commands unasked;
    ///   fullAccess (danger-full-access) is full access.
    /// - Antigravity: plan and manual edit nothing (manual declines what it
    ///   would ask about, headless); acceptEdits (`--mode accept-edits`)
    ///   edits unasked and declines the shell commands its own permission
    ///   rules do not allow; fullAccess (`--dangerously-skip-permissions`) is
    ///   full access.
    public static func capabilities(provider: String, mode: String) -> Set<ModeCapability> {
        switch (provider, mode) {
        case ("claude", "manual"): [.writesAsking]
        case ("claude", "acceptEdits"), ("codex", "onRequest"), ("gemini", "acceptEdits"): [.writesUnasked]
        case ("claude", "auto"), ("codex", "acceptEdits"): [.writesUnasked, .runsUnasked]
        case ("codex", "manual"): [.runsUnasked]
        case (_, "fullAccess"): Set(ModeCapability.allCases)
        default: []
        }
    }

    /// Whether a mode with `child` capabilities does nothing a mode with
    /// `parent` capabilities may not: each is the parent's too, except that
    /// editing after asking is covered by editing unasked.
    public static func isCovered(_ child: Set<ModeCapability>, by parent: Set<ModeCapability>) -> Bool {
        child.allSatisfy { parent.contains($0) || ($0 == .writesAsking && parent.contains(.writesUnasked)) }
    }

    /// The provider id the delegate tool's `agent` argument names: claude,
    /// codex or antigravity (or Antigravity's provider id, gemini), in any
    /// case; nil for any other word.
    public static func agent(named name: String) -> String? {
        switch name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
        case "claude": "claude"
        case "codex": "codex"
        case "antigravity", "gemini": "gemini"
        default: nil
        }
    }

    /// The name the delegation tools give the agent `provider`: its `agent`
    /// argument's word (antigravity for gemini).
    public static func agentName(_ provider: String) -> String { provider == "gemini" ? "antigravity" : provider }

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

    /// Whether the pane, in `mode` now, may have a child the human starts of
    /// any agent: its own (``canParentByHuman(kind:provider:mode:)``), or
    /// another with a mode in which its child can change files.
    public static func mayHaveHumanChild(kind: String, provider: String, mode: String) -> Bool {
        isAgentPane(kind: kind, provider: provider) && ProviderOptions.ids.contains { humanChildMode(child: $0, parent: provider, parentMode: mode, planFirst: false) != nil }
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
        isNoWider(mode, child: provider, than: stored, parent: provider)
    }

    /// Whether `mode`, one of the `child` agent's modes, lets it act no more
    /// freely than the `parent` agent's `stored` mode. For one agent this is
    /// its own modes' order (``modeRank``); for another, the child's mode may
    /// do nothing the parent's may not (``capabilities(provider:mode:)``). A
    /// mode that is not its own agent's, on either side, is never no wider.
    public static func isNoWider(_ mode: String, child: String, than stored: String, parent: String) -> Bool {
        guard startingModes(provider: child).contains(mode), startingModes(provider: parent).contains(stored),
              let asked = modeRank.firstIndex(of: mode), let own = modeRank.firstIndex(of: stored) else { return false }
        guard child != parent else { return asked <= own }
        return isCovered(capabilities(provider: child, mode: mode), by: capabilities(provider: parent, mode: stored))
    }

    /// The widest of the `child` agent's modes that is no wider than the
    /// `parent` agent's `parentMode` (``isNoWider(_:child:than:parent:)``) and
    /// in which that child can change files, or nil when it has none: the one
    /// with the most capabilities, then the later in its own order. For the
    /// parent's own agent it is the last by its own order: the parent's mode
    /// whenever its child can write in it.
    public static func widestChildMode(child: String, parent: String, parentMode: String) -> String? {
        let fitting = startingModes(provider: child).filter { isNoWider($0, child: child, than: parentMode, parent: parent) && childCanWrite(provider: child, mode: $0) }
        guard child != parent else { return fitting.last }
        return fitting.enumerated().max { (capabilities(provider: child, mode: $0.element).count, $0.offset) < (capabilities(provider: child, mode: $1.element).count, $1.offset) }?.element
    }

    /// The mode a child of the `child` agent that the human starts from a
    /// `parent` pane in `parentMode` begins in, or nil when there is none in
    /// which it could change files. A child of the parent's own agent starts
    /// in the parent's mode, or in plan mode when `planFirst` (the parent's
    /// style starts every request so) and the agent has one, as ⌘Enter
    /// always did; any other agent's in ``widestChildMode(child:parent:parentMode:)``.
    public static func humanChildMode(child: String, parent: String, parentMode: String, planFirst: Bool) -> String? {
        guard child == parent else { return widestChildMode(child: child, parent: parent, parentMode: parentMode) }
        let mode = planFirst && startingModes(provider: child).contains("plan") ? "plan" : parentMode
        return childCanWrite(provider: child, mode: mode) ? mode : nil
    }

    /// Whether an agent's CLI can run a child, as the app last learnt it:
    /// `installed` from its CLI search, `account` from its sign-in status.
    /// Either one unknown (nil) does not hold a child back; the app never asks
    /// a CLI for this.
    public static func availability(installed: Bool?, account: CLIAccountStatus?) -> DelegationAgentAvailability {
        if installed == false || account?.installed == false { return .notInstalled }
        if account?.loggedIn == false { return .signedOut }
        return .ready
    }

    /// What the worktree button's menu offers on a `parent` pane in
    /// `parentMode` with `openHumanChildren` open children the human started:
    /// one row per agent, Claude, Codex, Antigravity. A row says why it cannot
    /// run: the agent's CLI (not for the parent's own agent, whose run is
    /// going), no mode in which its child could change files, or the cap.
    public static func humanChildChoices(parent: String, parentMode: String, planFirst: Bool, openHumanChildren: Int,
                                         availability: (String) -> DelegationAgentAvailability) -> [DelegationChildChoice] {
        ProviderOptions.ids.map { agent in
            let mode = humanChildMode(child: agent, parent: parent, parentMode: parentMode, planFirst: planFirst)
            let blocked: DelegationChildChoice.Blocked?
            switch agent == parent ? .ready : availability(agent) {
            case .notInstalled: blocked = .notInstalled
            case .signedOut: blocked = .signedOut
            case .ready: blocked = mode == nil ? .noMode : openHumanChildren >= DelegationCoordinator.maximumOpenChildren ? .cap : nil
            }
            return DelegationChildChoice(agent: agent, mode: mode, blocked: blocked)
        }
    }

    /// Whether a `provider` child in `mode` can commit on its own branch.
    public static func canCommit(provider: String, mode: String) -> Bool { provider == "claude" || mode == "fullAccess" }

    /// Whether a `provider` parent whose run started in `mode` may merge a
    /// child by its merge tool.
    public static func agentMayMerge(provider: String, mode: String) -> Bool { provider == "claude" || mode == "fullAccess" }
}

/// What a run in a permission mode may do on its own, to compare the modes
/// of different agents (``DelegationProviders/capabilities(provider:mode:)``).
public enum ModeCapability: Sendable, Hashable, CaseIterable {
    /// It changes files only after the human approves each change.
    case writesAsking
    /// It changes files without asking, sandboxed or not.
    case writesUnasked
    /// It runs shell commands without asking the human, sandboxed or not.
    case runsUnasked
    /// No sandbox and no asking.
    case fullAccess
}

/// Whether an agent's CLI can run a child now, as the app last learnt it.
public enum DelegationAgentAvailability: Sendable, Equatable {
    case ready
    /// The CLI was not found, or says it is not installed.
    case notInstalled
    /// The CLI says it is signed out.
    case signedOut
}

/// One agent the composer's worktree button offers in its menu.
public struct DelegationChildChoice: Sendable, Equatable, Identifiable {
    /// Why the agent cannot run the child now.
    public enum Blocked: Sendable, Equatable {
        case notInstalled
        case signedOut
        /// No mode of the agent, no wider than the parent's, lets its child change files.
        case noMode
        /// The parent has its 3 open children the human started.
        case cap
    }

    /// The provider id.
    public var agent: String
    /// The mode the child would start in; nil when none would do.
    public var mode: String?
    /// nil when the child can start.
    public var blocked: Blocked?

    public var id: String { agent }

    public init(agent: String, mode: String?, blocked: Blocked?) { self.agent = agent; self.mode = mode; self.blocked = blocked }
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
