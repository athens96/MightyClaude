import Foundation

/// How the app's ``DelegationHost`` makes and runs a delegated child's pane
/// (macOS only). AppStore applies these rules; tests check them without the app.
public enum DelegationPanes {
    /// A new Claude pane for `pane` in the workspace `workspaceId`. Its mode is
    /// the starting mode the parent asked for, written here once and never
    /// copied from the last-used pane; it is linked to its parent and works in
    /// its worktree folder.
    public static func childSession(_ pane: DelegationChildPane, workspaceId: String, title: String) -> RunSession {
        var session = RunSession(id: pane.sessionId, workspaceId: workspaceId, title: title, kind: SessionKind.claude, provider: "claude",
                                 settings: RunSettings(permissionMode: pane.mode))
        session.parentSessionId = pane.parentSessionId
        session.workingFolder = pane.folder
        return session
    }

    /// The workspace a run of `session` starts with. A child's run, its
    /// terminal tool and its MCP binding all work in the child's worktree
    /// folder; any other pane's in the workspace folder.
    public static func runWorkspace(for session: RunSession, in workspace: Workspace) -> Workspace {
        guard let folder = session.workingFolder else { return workspace }
        var child = workspace
        child.path = folder
        return child
    }

    /// `worktree_missing` when `session` is a child whose worktree folder is
    /// gone (or was never saved), so it may not run; nil otherwise. A child
    /// never runs in its parent's checkout instead.
    public static func runRefusal(_ session: RunSession, folderExists: (String) -> Bool = DelegationPanes.isDirectory) -> DelegationReasonCode? {
        guard session.parentSessionId != nil || session.workingFolder != nil else { return nil }
        guard let folder = session.workingFolder, folderExists(folder) else { return .worktreeMissing }
        return nil
    }

    public static func isDirectory(_ path: String) -> Bool {
        var directory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &directory) && directory.boolValue
    }

    /// The pane `session` of `workspace` as the coordinator reads it.
    /// `startRefused` says the app's start would refuse a run in the pane now:
    /// a Claude model reset or a CLI update is going, the pane is blocked (a
    /// child without its worktree among others), or its request does not
    /// validate. A pane idle after a normal finish then reads as idle, so
    /// what comes for it is held as a row of its queued list instead of being
    /// handed a run the app would refuse. A running pane still reads as running.
    public static func paneState(of session: RunSession, in workspace: Workspace, runId: String?, activity: DelegationPaneActivity, startRefused: Bool = false) -> DelegationPaneState {
        DelegationPaneState(sessionId: session.id, kind: session.kind, provider: session.provider, permissionMode: session.settings.permissionMode,
                            folder: runWorkspace(for: session, in: workspace).path, parentSessionId: session.parentSessionId, runId: runId,
                            activity: activity == .finished && startRefused ? .idle : activity)
    }
}

/// The runs of the app's panes as delegation names them (macOS only): each
/// run the app starts gets an id, and a pane that finished a run normally
/// since this launch is told apart from one idle any other way.
public struct DelegationRunLedger: Sendable {
    private var runIds: [String: String] = [:]
    private var going: Set<String> = []
    private var finished: Set<String> = []

    public init() {}

    /// A new run starts in the pane `sessionId`: its id.
    public mutating func begin(_ sessionId: String) -> String {
        let runId = "run-" + UUID().uuidString.lowercased()
        runIds[sessionId] = runId
        going.insert(sessionId)
        finished.remove(sessionId)
        return runId
    }

    /// The pane's run going now, or its last since this launch.
    public func runId(_ sessionId: String) -> String? { runIds[sessionId] }

    /// The pane's run ended with the app status `status` ("completed",
    /// "stopped" or "error"); `quitting` while the app quits. The run and how
    /// it ended, or nil when no run of this launch was going: a second end of
    /// the same run is not another end.
    public mutating func end(_ sessionId: String, status: String, quitting: Bool) -> (runId: String, end: ChildRunEnd)? {
        guard let runId = runIds[sessionId], going.remove(sessionId) != nil else { return nil }
        let end: ChildRunEnd = quitting ? .quit : status == "completed" ? .finished : status == "stopped" ? .stopped : .errored
        if end == .finished { finished.insert(sessionId) }
        return (runId, end)
    }

    /// What the pane is doing: `running` when a run is going or starting.
    public func activity(_ sessionId: String, running: Bool) -> DelegationPaneActivity {
        running ? .running : finished.contains(sessionId) ? .finished : .idle
    }

    /// The pane closed.
    public mutating func forget(_ sessionId: String) {
        runIds.removeValue(forKey: sessionId)
        going.remove(sessionId)
        finished.remove(sessionId)
    }
}

/// A run event of a child's pane, as the host reports it.
public enum DelegationRunEvent: Sendable, Equatable {
    case started(childId: String, runId: String)
    case ended(childId: String, runId: String, end: ChildRunEnd)
}

/// Hands a host's child run events on one at a time, in the order they
/// happened (macOS only), so a run's end never overtakes its start.
public final class DelegationRunEventPump: Sendable {
    private let continuation: AsyncStream<DelegationRunEvent>.Continuation
    private let task: Task<Void, Never>

    public init(_ deliver: @escaping @Sendable (DelegationRunEvent) async -> Void) {
        let (events, continuation) = AsyncStream<DelegationRunEvent>.makeStream()
        self.continuation = continuation
        task = Task { for await event in events { await deliver(event) } }
    }

    /// Each event reaches the coordinator's record of its child's runs.
    public convenience init(coordinator: DelegationCoordinator) {
        self.init { event in
            switch event {
            case .started(let id, let runId): await coordinator.childRunStarted(id, runId: runId)
            case .ended(let id, let runId, let end): await coordinator.childRunEnded(id, runId: runId, end: end)
            }
        }
    }

    public func send(_ event: DelegationRunEvent) { continuation.yield(event) }

    /// Takes no more events and returns once every one sent is handed on.
    public func finish() async {
        continuation.finish()
        await task.value
    }
}
