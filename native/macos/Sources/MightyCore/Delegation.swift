import Foundation

/// The hidden switch for parent → child delegation between Claude panes
/// (macOS only). It is a defaults key with no settings UI and is off by
/// default:
///
///     defaults write dev.mightyclaude.native delegation.enabled -bool true
///
/// While it is on, each new run in a Claude pane also gets the delegation MCP
/// server (``DelegationMCPServer``) beside mighty-terminal. While it is off, no
/// run gets it and delegation calls reach nothing. Turning it off only detaches
/// the server.
public enum DelegationSwitch {
    public static let defaultsKey = "delegation.enabled"

    /// Read at each run start and at each delegation call. A missing key is off.
    public static func isOn(_ defaults: UserDefaults = .standard) -> Bool { defaults.bool(forKey: defaultsKey) }

    /// A Claude pane: an agent pane (kind claude) running the Claude CLI. Only
    /// these get the delegation server or may call it.
    public static func isClaudePane(kind: String, provider: String) -> Bool { kind == SessionKind.claude && provider == "claude" }
}

/// Why a delegation call was refused. A refused call carries exactly one of
/// these and changes nothing. The raw values are the ontology's ReasonCode
/// names, which the agent sees.
public enum DelegationReasonCode: String, Codable, Sendable, CaseIterable {
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
    /// not take it as given.
    public var error: String?
    /// The child the call is about: the one delegate just made, or the same
    /// one again when that call is repeated in the same parent run.
    public var child: DelegationChildInfo?

    public init(refused: DelegationReasonCode? = nil, error: String? = nil, child: DelegationChildInfo? = nil) { self.refused = refused; self.error = error; self.child = child }

    public static func refusal(_ reason: DelegationReasonCode) -> DelegationResponse { DelegationResponse(refused: reason) }
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

    public init(_ child: ChildRecord) { id = child.id; state = child.state; branch = child.branch; startingMode = child.startingMode }
}

/// Serves one delegation call, already resolved to the pane its token belongs to.
public protocol DelegationRequestHandler: Sendable {
    func handle(_ request: DelegationRequest, binding: PaneMCPBinding) async -> DelegationResponse
}

/// The app's side of the delegation server. Each call is answered by the
/// first rule that applies, and none of these answers changes anything:
///
/// 1. The switch is off: the server is detached, so the call reaches nothing.
/// 2. The caller is not a Claude pane: refused with `claude_only`.
/// 3. The caller's run started without the delegation server (the switch was
///    off then): the call reaches nothing.
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
        guard DelegationSwitch.isClaudePane(kind: binding.kind, provider: binding.provider) else { return .refusal(.claudeOnly) }
        guard binding.delegation else { return .failure(detachedMessage) }
        return nil
    }

    static func unknownToolMessage(_ tool: String) -> String { "Unknown delegation tool \(tool.prefix(80))." }
}
