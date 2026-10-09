import Foundation

/// One child as list_children shows it (macOS only).
public struct DelegationChildSummary: Codable, Sendable, Equatable {
    public var id: String
    public var state: ChildState
    /// The current report revision: 0 until the first report.
    public var revision: Int

    public init(_ child: ChildRecord) { id = child.id; state = child.state; revision = child.reportRevision }
}

/// One child as child_status shows it: where it stands and what it last
/// reported (macOS only).
public struct DelegationChildStatus: Codable, Sendable, Equatable {
    public var id: String
    public var state: ChildState
    /// The current report revision: 0 until the first report.
    public var revision: Int
    /// The head the current report was made at, which merge takes as its
    /// expected_head. nil until the first report.
    public var head: String?
    /// The stored copy of the current revision's REPORT.md, cut at 64 KiB with
    /// a visible marker. nil until the first report, or once pruning dropped it.
    public var report: String?
    /// The report is longer than its copy; the whole file stays in the child's
    /// worktree until cleanup.
    public var reportTruncated: Bool

    /// `child` with `report`, its stored REPORT.md copy, when that copy holds
    /// the current revision.
    public init(_ child: ChildRecord, report: DelegationCopy?) {
        id = child.id; state = child.state; revision = child.reportRevision; head = child.reportHead
        let current = report.flatMap { $0.childId == child.id && $0.kind == .report && $0.revision == child.reportRevision && child.reportRevision > 0 ? $0 : nil }
        self.report = current?.text; reportTruncated = current?.truncated ?? false
    }
}

/// The status tools (macOS only): a parent reads where its children stand.
/// Both only read the delegation file, so neither changes anything.
extension DelegationCoordinator {
    /// list_children from `caller`: its children in the order they were made,
    /// each with its id, state and current report revision. None is an empty list.
    func listChildren(caller: PaneMCPBinding) -> DelegationResponse {
        DelegationResponse(children: file.children.filter { $0.parentSessionId == caller.agentPaneId }.map(DelegationChildSummary.init))
    }

    /// child_status(child) from `caller`: that child's state, the head of its
    /// current report and that report's body. An id that is not one of the
    /// caller's children is answered with an error.
    func childStatus(_ id: String, caller: PaneMCPBinding) -> DelegationResponse {
        let id = id.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let child = file.children.first(where: { $0.id == id && $0.parentSessionId == caller.agentPaneId }) else { return .failure(Self.notYourChildMessage(id)) }
        return DelegationResponse(status: DelegationChildStatus(child, report: file.copy(childId: child.id, kind: .report)))
    }

    static func notYourChildMessage(_ id: String) -> String { "No child \"\(id.prefix(80))\" of yours. list_children names your children." }
}

extension DelegationMCPServer {
    /// list_children's answer as text and as structured content.
    static func result(_ children: [DelegationChildSummary]) -> [String: Any] {
        let lines = children.map { "- \($0.id): \($0.state.rawValue), report revision \($0.revision)" }
        let text = children.isEmpty ? "You have no children." : (["Your children:"] + lines).joined(separator: "\n")
        let data: [[String: Any]] = children.map { ["id": $0.id, "state": $0.state.rawValue, "revision": $0.revision] }
        return ["content": [["type": "text", "text": text]], "structuredContent": ["children": data]]
    }

    /// child_status's answer as text and as structured content; the report
    /// body is in both.
    static func result(_ status: DelegationChildStatus) -> [String: Any] {
        var text = "Child \(status.id) is \(status.state.rawValue)."
        if status.revision == 0 {
            text += " It has not reported yet."
        } else {
            text += " Its report revision \(status.revision) was made at head \(status.head ?? "unknown")."
            text += status.report.map { "\n\nREPORT.md:\n\($0)" } ?? " Its REPORT.md copy is no longer kept."
        }
        let data: [String: Any] = ["id": status.id, "state": status.state.rawValue, "revision": status.revision, "head": status.head ?? NSNull(),
                                   "report": status.report ?? NSNull(), "reportTruncated": status.reportTruncated]
        return ["content": [["type": "text", "text": text]], "structuredContent": ["status": data]]
    }
}
