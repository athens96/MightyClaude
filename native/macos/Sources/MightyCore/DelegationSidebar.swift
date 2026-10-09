import Foundation

/// One delegated child as the sidebar lists it (macOS only): its record's
/// state and parent link, and its task in one line for a row whose pane is gone.
public struct DelegationChildRow: Sendable, Equatable, Identifiable {
    /// The child pane's session id.
    public var id: String
    public var parentSessionId: String
    public var state: ChildState
    /// TASK.md shortened to one line, from its stored copy; nil without one.
    public var task: String?
    /// The parent's branch when the child was made.
    public var parentBranch: String
    /// The top folder of the parent's checkout when the child was made.
    public var parentCheckout: String?

    public init(id: String, parentSessionId: String, state: ChildState, task: String? = nil, parentBranch: String = "", parentCheckout: String? = nil) {
        self.id = id; self.parentSessionId = parentSessionId; self.state = state; self.task = task
        self.parentBranch = parentBranch; self.parentCheckout = parentCheckout
    }

    /// The row of `record`, its task read from `file`'s stored copy.
    public init(_ record: ChildRecord, in file: DelegationFile) {
        self.init(id: record.id, parentSessionId: record.parentSessionId, state: record.state,
                  task: file.copy(childId: record.id, kind: .task).flatMap { PaneTitle.shortened($0.text) },
                  parentBranch: record.parentBranch, parentCheckout: record.parentCheckout)
    }
}

/// How the Mac sidebar lists delegated children (macOS only): each under its
/// parent pane with its state, and the children of a closed parent under a
/// 'parent closed' node. The delegation file is the authority for each
/// child's link and state; the open panes come from the snapshot. The tree
/// does not depend on the hidden switch, so existing children stay listed
/// with it off.
public enum DelegationSidebar {
    /// Every child the file holds except discarded ones, in the order they were made.
    public static func rows(_ file: DelegationFile) -> [DelegationChildRow] {
        file.children.filter { $0.state != .discarded }.map { DelegationChildRow($0, in: file) }
    }

    /// The state a child's row shows: waiting while a running child's pane
    /// has a request only a human can answer, otherwise its record's state.
    public static func shownState(_ state: ChildState, asksHuman: Bool) -> ChildState {
        state == .running && asksHuman ? .waiting : state
    }

    /// The tone of a row's state word and glyph.
    public static func tone(_ state: ChildState) -> DesignTone {
        switch state {
        case .creating, .running: .run
        case .waiting: .wait
        case .reported, .merged: .done
        case .failed: .err
        case .interrupted: .stop
        case .ended, .closed, .discarded: .idle
        }
    }

    /// One workspace's panes as the sidebar lists them.
    public struct Tree: Sendable, Equatable {
        /// The panes at the top level, in the snapshot's order: every pane of
        /// the workspace that is not listed as a child.
        public var top: [RunSession] = []
        /// Each open parent pane's children, by the parent's session id, in
        /// the order they were made.
        public var children: [String: [DelegationChildRow]] = [:]
        /// A 'parent closed' node for each closed parent with children listed
        /// here, in the order its first child was made.
        public var parentClosed: [ParentClosed] = []

        public init() {}
    }

    /// The children of one closed parent pane.
    public struct ParentClosed: Sendable, Equatable, Identifiable {
        /// The closed parent's session id.
        public var id: String
        /// The parent's branch when its first child here was made.
        public var parentBranch: String
        public var children: [DelegationChildRow]
    }

    /// The tree of the workspace `workspaceId`. A child is listed once, under
    /// its parent pane while that is open, otherwise under its closed
    /// parent's node in the workspace of its own pane or, with that closed
    /// too, of its parent's checkout. A pane that is not listed as a child
    /// stays at the top level, so no pane ever drops out of the sidebar.
    public static func tree(workspaceId: String, workspaces: [Workspace], sessions: [RunSession], children: [DelegationChildRow]) -> Tree {
        let panes = Dictionary(sessions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        var tree = Tree()
        var listed: Set<String> = []
        for child in children {
            if let parent = panes[child.parentSessionId] {
                guard parent.workspaceId == workspaceId else { continue }
                tree.children[parent.id, default: []].append(child)
            } else {
                guard listedWorkspaceId(of: child, workspaces: workspaces, panes: panes) == workspaceId else { continue }
                if let index = tree.parentClosed.firstIndex(where: { $0.id == child.parentSessionId }) {
                    tree.parentClosed[index].children.append(child)
                } else {
                    tree.parentClosed.append(ParentClosed(id: child.parentSessionId, parentBranch: child.parentBranch, children: [child]))
                }
            }
            listed.insert(child.id)
        }
        tree.top = sessions.filter { $0.workspaceId == workspaceId && !listed.contains($0.id) }
        return tree
    }

    /// The workspace a child of a closed parent is listed in: its own pane's;
    /// with that closed, the workspace at its parent's checkout, or else the
    /// first one inside it; nil when there is none.
    static func listedWorkspaceId(of child: DelegationChildRow, workspaces: [Workspace], panes: [String: RunSession]) -> String? {
        if let pane = panes[child.id] { return pane.workspaceId }
        guard let checkout = child.parentCheckout.map(standardized) else { return nil }
        let inside = checkout.hasSuffix("/") ? checkout : checkout + "/"
        let paths = workspaces.map { (id: $0.id, path: standardized($0.path)) }
        return (paths.first { $0.path == checkout } ?? paths.first { $0.path.hasPrefix(inside) })?.id
    }

    static func standardized(_ path: String) -> String { URL(fileURLWithPath: path).standardizedFileURL.path }
}
