import Foundation

/// The switch for parent → child delegation between agent panes (macOS
/// only). It is on unless the human turned it off, in Settings › General or
/// with its defaults key:
///
///     defaults write dev.mightyclaude.native delegation.enabled -bool false
///
/// While it is on, each new run in a Claude pane, or a Codex pane in
/// acceptEdits or fullAccess, also gets the delegation MCP server (``DelegationMCPServer``) beside mighty-terminal, and
/// the Mac composer's second send on a busy Claude, Codex or Antigravity pane
/// runs the text in a new child worktree (``ComposerSecondSend``); which panes
/// take part is ``DelegationProviders``'s. While it is off, no run gets the
/// server, delegation calls reach nothing and the second send steers or
/// queues as before. Turning it off only detaches the server: existing
/// children keep working.
public enum DelegationSwitch {
    public static let defaultsKey = "delegation.enabled"

    /// Read at each run start, at each delegation call and by the composer.
    /// A missing key is on; a stored one reads as defaults reads a Bool.
    public static func isOn(_ defaults: UserDefaults = .standard) -> Bool { defaults.object(forKey: defaultsKey) == nil || defaults.bool(forKey: defaultsKey) }
}

/// What the Mac composer's second send (⌘Enter, and its round button) does
/// with the text while the pane is busy. Enter always queues it as the next
/// request; the phone's send modes do not go through this.
public enum ComposerSecondSend: String, Sendable, CaseIterable {
    /// A new child pane of the same agent in its own worktree, beside this
    /// one, runs the text now (``DelegationCoordinator/startHumanChild(_:parentId:planFirst:agent:starter:)``).
    /// The round button's menu runs it in a child of another agent instead.
    case worktree
    /// The text joins the running Claude turn.
    case steer
    /// The text waits for the next request, as Enter does.
    case queue

    /// The first rule that applies: a Claude, Codex or Antigravity pane that
    /// may have children (delegation on, not a child itself, and for Codex or
    /// Antigravity a `mode` in which its child can change files) in a folder
    /// that can host a child worktree (``ChildWorktreeCheck/canHostWorktree``)
    /// opens a new worktree; any other pane whose running turn takes text
    /// steers (a delegated Claude child, a Claude pane with delegation off or
    /// outside a usable git checkout); every other pane queues.
    public static func choose(kind: String, provider: String, mode: String, isChild: Bool, delegationOn: Bool, canHostWorktree: Bool, steerable: Bool) -> ComposerSecondSend {
        if delegationOn, !isChild, canHostWorktree, DelegationProviders.canParentByHuman(kind: kind, provider: provider, mode: mode) { return .worktree }
        return steerable ? .steer : .queue
    }

    /// What a text does when its new worktree could not be made or its child
    /// could not start: it goes to the running turn when that can take it,
    /// and is otherwise the pane's next request, as Enter would make it. It
    /// is never dropped. A text the human meant for another agent never goes
    /// to the pane (``returnsToComposer(childAgent:parentAgent:)``).
    public static func fallback(steerable: Bool) -> ComposerSecondSend { steerable ? .steer : .queue }

    /// Whether a text whose child could not be made or started goes back to
    /// the composer instead of to the pane: the human picked another agent
    /// than the pane's from the worktree button's menu.
    public static func returnsToComposer(childAgent: String?, parentAgent: String) -> Bool { childAgent.map { $0 != parentAgent } ?? false }

    /// Whether the worktree button shows for its menu alone: where ``choose``
    /// does not open a new worktree because the pane's own agent's child
    /// could not change files in `mode`, yet the pane may have a child of
    /// another agent, and one of `choices` can run now. Its click then opens
    /// the menu, and ⌘Enter does what it did before.
    public static func menuOnly(kind: String, provider: String, mode: String, isChild: Bool, delegationOn: Bool, canHostWorktree: Bool, choices: [DelegationChildChoice]) -> Bool {
        delegationOn && !isChild && canHostWorktree && DelegationProviders.isAgentPane(kind: kind, provider: provider)
            && !DelegationProviders.canParentByHuman(kind: kind, provider: provider, mode: mode) && choices.contains { $0.blocked == nil }
    }
}

/// Why a delegation call was refused. A refused call carries exactly one of
/// these and changes nothing. The raw values are the ontology's ReasonCode
/// names, which the agent sees.
public enum DelegationReasonCode: String, Codable, Sendable, CaseIterable {
    /// The pane cannot use delegation this way (the ontology's original name,
    /// from when only Claude panes could): its agent has no delegation tools,
    /// or it cannot have children.
    case claudeOnly = "claude_only"
    case childCannotDelegate = "child_cannot_delegate"
    case widthCap = "width_cap"
    case lowDisk = "low_disk"
    case storeFull = "store_full"
    case notGit = "not_git"
    case unbornBranch = "unborn_branch"
    case detachedHead = "detached_head"
    case widerMode = "wider_mode"
    case discardHumanOnly = "discard_human_only"
    case notReported = "not_reported"
    case trackedChanges = "tracked_changes"
    case headMoved = "head_moved"
    case diverged = "diverged"
    case branchNotCheckedOut = "branch_not_checked_out"
    case childClosed = "child_closed"
    case parentClosed = "parent_closed"
    case followUpLimit = "follow_up_limit"
    case worktreeMissing = "worktree_missing"
    case mergeConflict = "merge_conflict"
    case undoParentMoved = "undo_parent_moved"
    case parentBusy = "parent_busy"
    case workspaceHasChildren = "workspace_has_children"
    case heldNotRemovable = "held_not_removable"
    /// The child was started by the human from its parent's composer, not by
    /// the parent's delegate call: only the human merges it or sends it work.
    case humanStarted = "human_started"
    /// A parent whose agent's merge tool call is not gated by its CLI (any
    /// but Claude) may merge by tool only in fullAccess: the human merges
    /// from the child's card.
    case mergeHumanOnly = "merge_human_only"
    /// delegate's `agent` names no agent a child can run (macOS only).
    case agentUnknown = "agent_unknown"
    /// The agent delegate named is not installed or not signed in on this
    /// Mac, as the app last learnt it (macOS only).
    case agentUnavailable = "agent_unavailable"
}

/// One delegation tool call forwarded by the delegation MCP server. Like
/// ``AgentIORequest`` it carries no pane id: the caller is whatever pane the
/// token resolves to. Every delegation tool argument is a string.
public struct DelegationRequest: Codable, Sendable, Equatable {
    public var tool: String
    public var arguments: [String: String]

    public init(tool: String, arguments: [String: String] = [:]) { self.tool = tool; self.arguments = arguments }

    /// Lowercase letters, digits and underscores, starting with a letter, at most 64.
    public static func isToolName(_ name: String) -> Bool {
        let bytes = Array(name.utf8)
        let letters = UInt8(ascii: "a") ... UInt8(ascii: "z"), digits = UInt8(ascii: "0") ... UInt8(ascii: "9")
        guard let first = bytes.first, bytes.count <= 64, letters.contains(first) else { return false }
        return bytes.allSatisfy { letters.contains($0) || digits.contains($0) || $0 == UInt8(ascii: "_") }
    }
}

/// The app's answer to one delegation call.
public struct DelegationResponse: Codable, Sendable, Equatable {
    /// The call was refused with exactly this reason, and nothing changed.
    public var refused: DelegationReasonCode?
    /// The call reached no delegation tool (the pane is gone, the server is
    /// not attached to its run, or no tool has that name), or the tool could
    /// not take it as given. With `refused`, what the refusal is about (the
    /// agent it names), shown after its code.
    public var error: String?
    /// The child the call is about: the one delegate just made, or the same
    /// one again when that call is repeated in the same parent run.
    public var child: DelegationChildInfo?
    /// list_children's answer: the caller's children, possibly none.
    public var children: [DelegationChildSummary]?
    /// child_status's answer: one of the caller's children.
    public var status: DelegationChildStatus?
    /// merge's answer: the fast-forward it made, as the MergeRecord it wrote.
    public var merged: MergeRecord?
    /// follow_up's answer: the follow-up it recorded.
    public var followUp: DelegationFollowUpInfo?

    public init(refused: DelegationReasonCode? = nil, error: String? = nil, child: DelegationChildInfo? = nil, children: [DelegationChildSummary]? = nil, status: DelegationChildStatus? = nil,
                merged: MergeRecord? = nil, followUp: DelegationFollowUpInfo? = nil) {
        self.refused = refused; self.error = error; self.child = child; self.children = children; self.status = status
        self.merged = merged; self.followUp = followUp
    }

    public static func refusal(_ reason: DelegationReasonCode) -> DelegationResponse { DelegationResponse(refused: reason) }
    public static func refusal(_ reason: DelegationReasonCode, detail: String) -> DelegationResponse { DelegationResponse(refused: reason, error: detail) }
    public static func failure(_ message: String) -> DelegationResponse { DelegationResponse(error: message) }
    public static func delegated(_ child: ChildRecord) -> DelegationResponse { DelegationResponse(child: DelegationChildInfo(child)) }
}

/// One child as its parent's tools show it.
public struct DelegationChildInfo: Codable, Sendable, Equatable {
    public var id: String
    public var state: ChildState
    /// Always `mighty/<id>`.
    public var branch: String
    /// The permission mode the parent asked for in delegate.
    public var startingMode: String
    /// The agent the child runs, as delegate's `agent` names it.
    public var agent: String

    public init(_ child: ChildRecord) {
        id = child.id; state = child.state; branch = child.branch; startingMode = child.startingMode; agent = DelegationProviders.agentName(child.agent)
    }
}

/// Serves one delegation call, already resolved to the pane its token belongs to.
public protocol DelegationRequestHandler: Sendable {
    func handle(_ request: DelegationRequest, binding: PaneMCPBinding) async -> DelegationResponse
}

/// The app's side of the delegation server. Each call is answered by the
/// first rule that applies, and none of these answers changes anything:
///
/// 1. The switch is off: the server is detached, so the call reaches nothing.
/// 2. The caller's agent has no delegation tools (an agent pane running
///    neither Claude nor Codex, or no agent pane): refused with `claude_only`.
/// 3. The caller's run started without the delegation server (the switch was
///    off then, or it is a Codex run in manual or onRequest): the call reaches nothing.
/// 4. Otherwise the call goes to the delegation tool it names; there is no
///    tool by any other name.
public struct DelegationIOHandler: DelegationRequestHandler {
    public static let detachedMessage = "The delegation tools are not attached to this pane's run."

    private let isSwitchOn: @Sendable () -> Bool

    public init(isSwitchOn: @escaping @Sendable () -> Bool = { DelegationSwitch.isOn() }) { self.isSwitchOn = isSwitchOn }

    public func handle(_ request: DelegationRequest, binding: PaneMCPBinding) async -> DelegationResponse {
        Self.gate(binding, isSwitchOn: isSwitchOn()) ?? .failure(Self.unknownToolMessage(request.tool))
    }

    /// The answer of rules 1 to 3, or nil when the call may go on to a tool.
    /// ``DelegationCoordinator`` applies the same gates.
    static func gate(_ binding: PaneMCPBinding, isSwitchOn: Bool) -> DelegationResponse? {
        guard isSwitchOn else { return .failure(detachedMessage) }
        guard DelegationProviders.canParentByAgent(kind: binding.kind, provider: binding.provider) else { return .refusal(.claudeOnly) }
        guard binding.delegation else { return .failure(detachedMessage) }
        return nil
    }

    static func unknownToolMessage(_ tool: String) -> String { "Unknown delegation tool \(tool.prefix(80))." }
}
