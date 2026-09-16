import Foundation

/// A workspace's split tree. Leaves contain ordered tabs; split nodes contain
/// exactly two children after normalization. The wire schema is shared with Windows.
public struct PaneLayoutNode: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var kind: String
    public var sessionIds: [String]
    public var selectedSessionId: String?
    public var axis: String?
    public var ratio: Double
    public var children: [PaneLayoutNode]

    public init(id: String = UUID().uuidString, kind: String, sessionIds: [String] = [], selectedSessionId: String? = nil, axis: String? = nil, ratio: Double = 0.5, children: [PaneLayoutNode] = []) {
        self.id = id; self.kind = kind; self.sessionIds = sessionIds; self.selectedSessionId = selectedSessionId
        self.axis = axis; self.ratio = ratio; self.children = children
    }

    enum CodingKeys: String, CodingKey { case id, kind, sessionIds, selectedSessionId, axis, ratio, children }
    public init(from decoder: Decoder) throws {
        let path = decoder.codingPath
        let depth = path.indices.dropFirst().filter { path[$0].intValue != nil && path[$0 - 1].stringValue == "children" }.count
        guard depth < PaneLayouts.maximumDepth else {
            throw DecodingError.dataCorrupted(.init(codingPath: path, debugDescription: "Pane layout is too deeply nested."))
        }
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id); kind = try c.decode(String.self, forKey: .kind)
        sessionIds = try c.decodeIfPresent([String].self, forKey: .sessionIds) ?? []
        selectedSessionId = try c.decodeIfPresent(String.self, forKey: .selectedSessionId)
        axis = try c.decodeIfPresent(String.self, forKey: .axis)
        ratio = try c.decodeIfPresent(Double.self, forKey: .ratio) ?? 0.5
        children = try c.decodeIfPresent([PaneLayoutNode].self, forKey: .children) ?? []
    }
}

public enum PaneLayouts {
    public static let maximumSessions = 128
    public static let maximumNodes = 255
    public static let maximumDepth = 16
    public static let minimumRatio = 0.15
    public static let maximumRatio = 0.85
    public static let viewModes = ["grid", "columns", "tabs", "custom", "focus"]

    /// One-time migration of the old global display mode. Focus applies only
    /// to the workspace that was active; subsequent workspaces never inherit it.
    public static func workspaceModes(workspaceIds: [String], activeWorkspaceId: String?, legacyMode: String, layouts: [String: PaneLayoutNode]?, savedModes: [String: String]?) -> [String: String] {
        var result: [String: String] = [:]
        for id in workspaceIds.prefix(64) where CoreValidation.identifier(id) {
            if let mode = savedModes?[id], viewModes.contains(mode) { result[id] = mode; continue }
            if savedModes == nil, legacyMode == "focus", id == activeWorkspaceId { result[id] = "focus"; continue }
            if let root = layouts?[id] { result[id] = root.kind == "tabs" ? "tabs" : "custom" }
            else if savedModes == nil, ["grid", "columns", "tabs"].contains(legacyMode) { result[id] = legacyMode }
            else { result[id] = "tabs" }
        }
        return result
    }

    public static func firstSelectedSession(in root: PaneLayoutNode?) -> String? {
        guard let root else { return nil }
        return find(root, depth: 0, where: { $0.kind == "tabs" && !$0.sessionIds.isEmpty }).flatMap { node in
            node.selectedSessionId.flatMap { node.sessionIds.contains($0) ? $0 : nil } ?? node.sessionIds.first
        }
    }

    public static func preset(sessionIds: [String], activeId: String? = nil, mode: String) -> PaneLayoutNode? {
        let ids = validSessionIds(sessionIds)
        guard !ids.isEmpty else { return nil }
        if mode == "tabs" || mode == "focus" || mode == "custom" {
            return PaneLayoutNode(kind: "tabs", sessionIds: ids, selectedSessionId: activeId.flatMap { ids.contains($0) ? $0 : nil } ?? ids[0])
        }
        func build(_ slice: ArraySlice<String>, depth: Int) -> PaneLayoutNode {
            if slice.count == 1 { return PaneLayoutNode(kind: "tabs", sessionIds: Array(slice), selectedSessionId: slice.first) }
            let middle = slice.startIndex + (slice.count + 1) / 2
            let left = slice[..<middle], right = slice[middle...]
            return PaneLayoutNode(kind: "split", axis: mode == "columns" || depth.isMultiple(of: 2) ? "horizontal" : "vertical",
                                  ratio: clamp(Double(left.count) / Double(slice.count)), children: [build(left, depth: depth + 1), build(right, depth: depth + 1)])
        }
        return build(ids[...], depth: 0)
    }

    /// Reconciles a persisted tree with this workspace's current sessions. New
    /// sessions join the first group; UI may then move them to its previous focus.
    public static func normalized(root: PaneLayoutNode?, sessionIds: [String], activeId: String? = nil) -> PaneLayoutNode? {
        let ids = validSessionIds(sessionIds)
        guard !ids.isEmpty else { return nil }
        let allowed = Set(ids)
        var seenSessions = Set<String>(), seenNodes = Set<String>(), count = 0
        func visit(_ source: PaneLayoutNode, depth: Int) -> PaneLayoutNode? {
            guard depth < maximumDepth, count < maximumNodes else { return nil }
            count += 1
            var id = source.id
            if !CoreValidation.identifier(id) || !seenNodes.insert(id).inserted { id = freshId(excluding: &seenNodes) }
            if source.kind == "tabs" {
                let tabs = source.sessionIds.prefix(maximumSessions).filter { allowed.contains($0) && seenSessions.insert($0).inserted }
                guard !tabs.isEmpty else { return nil }
                let selected = source.selectedSessionId.flatMap { tabs.contains($0) ? $0 : nil } ?? tabs[0]
                return PaneLayoutNode(id: id, kind: "tabs", sessionIds: tabs, selectedSessionId: selected)
            }
            guard source.kind == "split" else { return nil }
            let children = source.children.prefix(2).compactMap { visit($0, depth: depth + 1) }
            if children.count == 1 { return children[0] }
            guard children.count == 2 else { return nil }
            return PaneLayoutNode(id: id, kind: "split", axis: source.axis == "vertical" ? "vertical" : "horizontal", ratio: clamp(source.ratio), children: children)
        }
        var result = root.flatMap { visit($0, depth: 0) }
        let missing = ids.filter { !seenSessions.contains($0) }
        if result == nil { result = PaneLayoutNode(id: freshId(excluding: &seenNodes), kind: "tabs", sessionIds: ids, selectedSessionId: ids[0]) }
        else if !missing.isEmpty {
            var appended = false
            result = map(result!, depth: 0) { node in
                guard node.kind == "tabs", !appended else { return node }
                appended = true; var copy = node; copy.sessionIds.append(contentsOf: missing); return copy
            }
        }
        if let activeId, allowed.contains(activeId) { result = selecting(root: result, id: activeId) }
        return result
    }

    public static func selecting(root: PaneLayoutNode?, id: String) -> PaneLayoutNode? {
        guard let root, CoreValidation.identifier(id) else { return root }
        return map(root, depth: 0) { node in
            guard node.kind == "tabs", node.sessionIds.contains(id) else { return node }
            var copy = node; copy.selectedSessionId = id; return copy
        }
    }

    /// Add a new tab directly to its intended group. A stale target or a split
    /// beyond the depth limit changes nothing, including existing ratios/IDs.
    public static func inserting(root: PaneLayoutNode?, sessionId: String, targetGroupId: String? = nil, placement: String = "tab") -> PaneLayoutNode? {
        guard CoreValidation.identifier(sessionId), ["tab", "left", "right", "top", "bottom"].contains(placement) else { return root }
        guard let root else {
            guard targetGroupId == nil, placement == "tab" else { return nil }
            return PaneLayoutNode(kind: "tabs", sessionIds: [sessionId], selectedSessionId: sessionId)
        }
        let ids = allSessionIds(root)
        guard ids.count < maximumSessions, !ids.contains(sessionId), withinLimits(root),
              let target = find(root, depth: 0, where: { $0.kind == "tabs" && (targetGroupId == nil || $0.id == targetGroupId) }) else { return root }
        var nodeIds = Set(allNodeIds(root))
        let result = map(root, depth: 0) { node in
            guard node.id == target.id, node.kind == "tabs" else { return node }
            if placement == "tab" {
                var copy = node; copy.sessionIds.append(sessionId); copy.selectedSessionId = sessionId; return copy
            }
            let newGroup = PaneLayoutNode(id: freshId(excluding: &nodeIds), kind: "tabs", sessionIds: [sessionId], selectedSessionId: sessionId)
            return PaneLayoutNode(id: freshId(excluding: &nodeIds), kind: "split", axis: ["left", "right"].contains(placement) ? "horizontal" : "vertical", children: ["left", "top"].contains(placement) ? [newGroup, node] : [node, newGroup])
        }
        return withinLimits(result) ? result : root
    }

    /// Invalid drops are no-ops. In particular, validate the target before
    /// removing the source so stale drag payloads cannot lose a live session.
    public static func moving(root: PaneLayoutNode?, sessionId: String, targetGroupId: String, placement: String, beforeSessionId: String? = nil) -> PaneLayoutNode? {
        guard let root, CoreValidation.identifier(sessionId), ["tab", "left", "right", "top", "bottom"].contains(placement),
              let source = find(root, depth: 0, where: { $0.kind == "tabs" && $0.sessionIds.contains(sessionId) }),
              let target = find(root, depth: 0, where: { $0.kind == "tabs" && $0.id == targetGroupId }),
              beforeSessionId == nil || CoreValidation.identifier(beforeSessionId!) && target.sessionIds.contains(beforeSessionId!) else { return root }
        if source.id == target.id && (source.sessionIds.count == 1 || placement == "tab" && beforeSessionId == sessionId) { return root }
        let ids = allSessionIds(root)
        guard let clean = normalized(root: root, sessionIds: ids), allSessionIds(clean).contains(sessionId),
              find(clean, depth: 0, where: { $0.kind == "tabs" && $0.id == targetGroupId }) != nil,
              var removed = removing(clean, sessionId: sessionId) else { return root }
        var nodeIds = Set(allNodeIds(removed)), inserted = false
        removed = map(removed, depth: 0) { node in
            guard node.kind == "tabs", node.id == targetGroupId else { return node }
            inserted = true
            if placement == "tab" {
                var copy = node
                let index = beforeSessionId.flatMap { copy.sessionIds.firstIndex(of: $0) } ?? copy.sessionIds.count
                copy.sessionIds.insert(sessionId, at: index); copy.selectedSessionId = sessionId
                return copy
            }
            let tab = PaneLayoutNode(id: freshId(excluding: &nodeIds), kind: "tabs", sessionIds: [sessionId], selectedSessionId: sessionId)
            return PaneLayoutNode(id: freshId(excluding: &nodeIds), kind: "split", axis: placement == "left" || placement == "right" ? "horizontal" : "vertical",
                                  children: placement == "left" || placement == "top" ? [tab, node] : [node, tab])
        }
        guard inserted, withinLimits(removed) else { return root }
        return removed
    }

    public static func resizing(root: PaneLayoutNode?, splitId: String, ratio: Double) -> PaneLayoutNode? {
        guard let root, ratio.isFinite else { return root }
        return map(root, depth: 0) { node in
            guard node.kind == "split", node.id == splitId else { return node }
            var copy = node; copy.ratio = clamp(ratio); return copy
        }
    }

    private static func validSessionIds(_ ids: [String]) -> [String] {
        var seen = Set<String>()
        return ids.prefix(maximumSessions).filter { CoreValidation.identifier($0) && seen.insert($0).inserted }
    }
    private static func clamp(_ ratio: Double) -> Double { ratio.isFinite ? min(maximumRatio, max(minimumRatio, ratio)) : 0.5 }
    private static func freshId(excluding used: inout Set<String>) -> String {
        var id: String
        repeat { id = UUID().uuidString } while !used.insert(id).inserted
        return id
    }
    private static func map(_ node: PaneLayoutNode, depth: Int, _ transform: (PaneLayoutNode) -> PaneLayoutNode) -> PaneLayoutNode {
        guard depth < maximumDepth else { return node }
        var copy = node
        if node.kind == "split" { copy.children = node.children.prefix(2).map { map($0, depth: depth + 1, transform) } }
        return transform(copy)
    }
    private static func find(_ node: PaneLayoutNode, depth: Int, where predicate: (PaneLayoutNode) -> Bool) -> PaneLayoutNode? {
        guard depth < maximumDepth else { return nil }
        if predicate(node) { return node }
        for child in node.children.prefix(2) { if let found = find(child, depth: depth + 1, where: predicate) { return found } }
        return nil
    }
    private static func allSessionIds(_ root: PaneLayoutNode) -> [String] {
        var ids: [String] = []
        _ = map(root, depth: 0) { node in if node.kind == "tabs", ids.count < maximumSessions { ids.append(contentsOf: node.sessionIds.prefix(maximumSessions - ids.count)) }; return node }
        return ids
    }
    private static func allNodeIds(_ root: PaneLayoutNode) -> [String] {
        var ids: [String] = []
        _ = map(root, depth: 0) { node in ids.append(node.id); return node }
        return ids
    }
    private static func removing(_ node: PaneLayoutNode, sessionId: String) -> PaneLayoutNode? {
        if node.kind == "tabs" {
            var copy = node; copy.sessionIds.removeAll { $0 == sessionId }
            guard !copy.sessionIds.isEmpty else { return nil }
            if copy.selectedSessionId == sessionId { copy.selectedSessionId = copy.sessionIds[0] }
            return copy
        }
        let children = node.children.compactMap { removing($0, sessionId: sessionId) }
        if children.count == 1 { return children[0] }
        guard children.count == 2 else { return nil }
        var copy = node; copy.children = children; return copy
    }
    private static func withinLimits(_ root: PaneLayoutNode) -> Bool {
        var count = 0
        func visit(_ node: PaneLayoutNode, depth: Int) -> Bool {
            count += 1
            guard count <= maximumNodes, depth < maximumDepth else { return false }
            return node.children.allSatisfy { visit($0, depth: depth + 1) }
        }
        return visit(root, depth: 0)
    }
}
