import Foundation

/// The tools the delegation MCP server lists (macOS only): exactly these six.
/// The server is attached only to a Claude pane's run while the hidden
/// ``DelegationSwitch`` is on, so with the switch on a Claude pane sees these
/// and with it off none. Every argument is a required string.
public enum DelegationToolManifest {
    public struct Argument: Sendable, Equatable {
        public let name: String
        public let description: String
    }

    public struct Tool: Sendable, Equatable {
        public let name: String
        public let description: String
        /// All strings, all required, no others.
        public let arguments: [Argument]
    }

    static let child = Argument(name: "child", description: "The child's id, as delegate or list_children gave it.")

    public static let delegate = Tool(
        name: "delegate",
        description: "Start a child Claude pane that works on a task in its own git worktree, on the flat branch mighty/<child id> made from your current branch head. The child starts in the permission mode you ask for, which must be no wider than your own (plan < manual < acceptEdits < auto < fullAccess). Answers as soon as the child exists; its run goes on in the background. The child writes its result to REPORT.md, and you get one notice per report. At most 3 open children per pane, and a child cannot delegate. The same call twice in one of your runs returns the same child.",
        arguments: [
            Argument(name: "task", description: "What the child should do. It is saved as the child's TASK.md."),
            Argument(name: "mode", description: "The child's starting permission mode: plan, manual (always ask), acceptEdits, auto or fullAccess. No wider than yours."),
        ]
    )

    public static let listChildren = Tool(
        name: "list_children",
        description: "List your children with their id, state and current report revision.",
        arguments: []
    )

    public static let childStatus = Tool(
        name: "child_status",
        description: "Read one of your children: its state, its head and the body of its latest REPORT.md.",
        arguments: [child]
    )

    public static let merge = Tool(
        name: "merge",
        description: "Fast-forward your recorded branch to a reported child's head. Refused, changing nothing, unless the child has reported at exactly expected_head with no tracked changes, your recorded branch is checked out with no tracked changes and it can fast-forward. Follows your permission mode.",
        arguments: [child, Argument(name: "expected_head", description: "The child's reported head commit, as child_status gave it.")]
    )

    public static let followUp = Tool(
        name: "follow_up",
        description: "Send one of your children a follow-up instruction, at most 2 per child. It reaches the child exactly once, as its next run, and clears its reported state.",
        arguments: [child, Argument(name: "text", description: "The instruction for the child.")]
    )

    public static let discard = Tool(
        name: "discard",
        description: "Discarding a child is for the human only, from the child's card in the app. This tool always refuses with discard_human_only and changes nothing.",
        arguments: [child]
    )

    /// All six in this order. Exactly this many; no more, no fewer.
    public static let all: [Tool] = [delegate, listChildren, childStatus, merge, followUp, discard]

    public static func tool(named name: String) -> Tool? { all.first { $0.name == name } }

    /// The `tools/list` entry for `tool`.
    static func definition(_ tool: Tool) -> [String: Any] {
        var properties: [String: Any] = [:]
        for argument in tool.arguments { properties[argument.name] = ["type": "string", "description": argument.description] }
        return [
            "name": tool.name,
            "description": tool.description,
            "inputSchema": [
                "type": "object",
                "properties": properties,
                "required": tool.arguments.map(\.name),
                "additionalProperties": false,
            ] as [String: Any],
        ]
    }
}

/// A child pane the coordinator asks the host to open.
public struct DelegationChildPane: Sendable, Equatable {
    /// Chosen by the coordinator, so the branch `mighty/<session id>` and the
    /// worktree exist before the pane does.
    public var sessionId: String
    public var parentSessionId: String
    /// The starting permission mode the parent asked for, written once.
    public var mode: String
    /// The folder the child works in, inside its worktree.
    public var folder: String

    public init(sessionId: String, parentSessionId: String, mode: String, folder: String) {
        self.sessionId = sessionId; self.parentSessionId = parentSessionId; self.mode = mode; self.folder = folder
    }
}

/// What a pane is doing, as delivery needs to know it.
public enum DelegationPaneActivity: String, Sendable, CaseIterable {
    /// A run is going.
    case running
    /// Idle after a run that finished normally since this launch.
    case finished
    /// Idle any other way: stopped, errored, or no run since this launch.
    case idle
}

/// One open pane as the host sees it now.
public struct DelegationPaneState: Sendable, Equatable {
    public var sessionId: String
    public var kind: String
    public var provider: String
    /// The pane's stored permission mode.
    public var permissionMode: String
    /// The folder its runs work in.
    public var folder: String
    /// The parent's session id when this pane is a delegated child.
    public var parentSessionId: String?
    /// The run going now, or the last one; nil before the first.
    public var runId: String?
    public var activity: DelegationPaneActivity

    public init(sessionId: String, kind: String = SessionKind.claude, provider: String = "claude", permissionMode: String, folder: String, parentSessionId: String? = nil, runId: String? = nil, activity: DelegationPaneActivity = .idle) {
        self.sessionId = sessionId; self.kind = kind; self.provider = provider; self.permissionMode = permissionMode
        self.folder = folder; self.parentSessionId = parentSessionId; self.runId = runId; self.activity = activity
    }
}

/// The app side of delegation: ``AppStore`` in the app, a fake in tests. The
/// coordinator reaches panes only through this.
public protocol DelegationHost: Sendable {
    /// Opens the child's Claude pane in `pane.mode`, working in `pane.folder`
    /// and linked to its parent. False when no pane could be made.
    func createPane(_ pane: DelegationChildPane) async -> Bool
    /// Starts a run with `input` in the pane: the new run's id, or nil when
    /// none started.
    func startRun(sessionId: String, input: String) async -> String?
    /// Hands `input` to the pane by `route`: steered into its running Claude
    /// run, or as its next run. The id of the run that got it, or nil when it
    /// did not get it.
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String?
    /// The pane now, or nil when it is closed.
    func paneState(sessionId: String) async -> DelegationPaneState?
    /// Stops the pane's run, if one is going, and returns once it has ended.
    /// Does nothing for an idle or closed pane.
    func stopRun(sessionId: String) async
}

/// The app's delegation service (macOS only). It owns the child records (the
/// delegation file is their authority), the six tools, delivery and the git
/// actions, and reaches panes only through its ``DelegationHost``.
///
/// Each call is answered by the first rule that applies:
///
/// 1. The gates of ``DelegationIOHandler``: switched off or a run without the
///    server reaches nothing; a pane that is not a Claude pane is refused
///    with `claude_only`.
/// 2. A name that is not one of the six tools reaches nothing.
/// 3. `discard` is always refused with `discard_human_only`, whatever it
///    names: only a human discards, from the child's card.
/// 4. A call without exactly the tool's string arguments is answered with an
///    error and changes nothing.
/// 5. Otherwise the tool runs, and its answer comes within ``answerSeconds``,
///    well inside the socket's 60 s limit.
public actor DelegationCoordinator: DelegationRequestHandler {
    /// The longest any call takes to answer. The socket gives up after
    /// ``AgentIOSocketClient/responseTimeoutSeconds`` (60 s).
    public static let answerSeconds: TimeInterval = 45

    public nonisolated let store: DelegationFileStore
    let host: any DelegationHost
    /// Makes each child's worktree: under `~/.mightyclaude/worktrees` in the
    /// app, under a temp folder in tests.
    nonisolated let worktrees: ChildWorktreeMaker
    private nonisolated let isSwitchOn: @Sendable () -> Bool
    private nonisolated let answerSeconds: TimeInterval
    /// The delegation file as last loaded or saved. The one exception: a
    /// child's start that the disk refused to save is held here until the
    /// next save writes it.
    public internal(set) var file: DelegationFile
    /// Children whose start is still going: delegate saved their record and
    /// ``startChild(_:base:task:)`` has not finished. A discard waits for it.
    var starting: Set<String> = []
    /// Whether a tool merge is going. Merges take turns, so two never run git
    /// in a parent's checkout at once; the others wait here, first come first.
    var isMerging = false
    var mergeTurns: [CheckedContinuation<Void, Never>] = []
    /// Whether a run event of a child's pane (its start, its end, the parent
    /// awaiting it, or the launch's sweep) is being recorded. They take turns
    /// in the order they came, so each is recorded whole before the next.
    var isRecordingRun = false
    var runTurns: [CheckedContinuation<Void, Never>] = []
    /// Whether a delivery pass is offering pending items to their panes, and
    /// whether another pass was asked for meanwhile.
    var isDelivering = false
    var wantsDelivery = false

    /// Loads the delegation file from `store`; throws when it is unreadable.
    public init(store: DelegationFileStore, host: any DelegationHost, worktrees: ChildWorktreeMaker = ChildWorktreeMaker(), isSwitchOn: @escaping @Sendable () -> Bool = { DelegationSwitch.isOn() }, answerSeconds: TimeInterval = DelegationCoordinator.answerSeconds) throws {
        self.store = store; self.host = host; self.worktrees = worktrees; self.isSwitchOn = isSwitchOn
        self.answerSeconds = min(answerSeconds, Self.answerSeconds)
        file = try store.load()
        // What the last launch left pending is held, never delivered on its
        // own. The move only shortens the file, so it always fits.
        if file.holdPending() { try? store.save(file) }
    }

    public nonisolated func handle(_ request: DelegationRequest, binding: PaneMCPBinding) async -> DelegationResponse {
        if let gated = DelegationIOHandler.gate(binding, isSwitchOn: isSwitchOn()) { return gated }
        guard let tool = DelegationToolManifest.tool(named: request.tool) else { return .failure(DelegationIOHandler.unknownToolMessage(request.tool)) }
        if tool == DelegationToolManifest.discard { return .refusal(.discardHumanOnly) }
        if let problem = Self.argumentProblem(request.arguments, for: tool) { return .failure(problem) }
        let late = DelegationResponse.failure(Self.lateMessage(tool.name, seconds: answerSeconds))
        return await Self.answer(within: answerSeconds, late: late) { await self.perform(tool, request.arguments, caller: binding) }
    }

    /// Runs one checked call from the pane `caller`.
    private func perform(_ tool: DelegationToolManifest.Tool, _ arguments: [String: String], caller: PaneMCPBinding) async -> DelegationResponse {
        switch tool {
        case DelegationToolManifest.delegate:
            return await delegate(task: arguments["task"] ?? "", mode: arguments["mode"] ?? "", caller: caller)
        case DelegationToolManifest.listChildren:
            return listChildren(caller: caller)
        case DelegationToolManifest.childStatus:
            return childStatus(arguments["child"] ?? "", caller: caller)
        case DelegationToolManifest.merge:
            return await merge(arguments["child"] ?? "", expectedHead: arguments["expected_head"] ?? "", caller: caller)
        case DelegationToolManifest.followUp:
            return await followUp(arguments["child"] ?? "", text: arguments["text"] ?? "", caller: caller)
        default:
            // discard, which handle(_:binding:) already refused: only a human discards.
            return .refusal(.discardHumanOnly)
        }
    }

    static func lateMessage(_ tool: String, seconds: TimeInterval) -> String {
        "\(tool) did not finish within \(Int(seconds)) seconds. Its work goes on in Mighty Claude; call list_children to see where your children stand."
    }

    /// Why `arguments` are not exactly `tool`'s, or nil when they are.
    static func argumentProblem(_ arguments: [String: String], for tool: DelegationToolManifest.Tool) -> String? {
        let names = tool.arguments.map(\.name)
        guard Set(arguments.keys) == Set(names) else {
            return names.isEmpty ? "\(tool.name) takes no arguments." : "\(tool.name) takes exactly these string arguments: \(names.joined(separator: ", "))."
        }
        return nil
    }

    /// `work`'s answer, or `late` once `seconds` pass first. The work is not
    /// cancelled: it finishes in the background and keeps what it records,
    /// but its answer is dropped, so a call is answered exactly once.
    static func answer(within seconds: TimeInterval, late: DelegationResponse, _ work: @escaping @Sendable () async -> DelegationResponse) async -> DelegationResponse {
        let once = OnceAnswer()
        return await withCheckedContinuation { continuation in
            once.arm(continuation)
            let timer = Task {
                do { try await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000)) } catch { return }
                once.resume(late)
            }
            Task {
                once.resume(await work())
                timer.cancel()
            }
        }
    }
}

/// A continuation resumed by whichever answer comes first; later ones are dropped.
private final class OnceAnswer: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<DelegationResponse, Never>?

    func arm(_ continuation: CheckedContinuation<DelegationResponse, Never>) { lock.withLock { self.continuation = continuation } }

    func resume(_ answer: DelegationResponse) {
        let taken: CheckedContinuation<DelegationResponse, Never>? = lock.withLock { defer { continuation = nil }; return continuation }
        taken?.resume(returning: answer)
    }
}
