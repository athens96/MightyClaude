import AppKit
import MightyCore
import UniformTypeIdentifiers

struct PaneDragPayload: Codable, Sendable, Equatable {
    static let contentType = UTType(exportedAs: "dev.mightyclaude.pane-tab", conformingTo: .data)
    let token: String
    let workspaceId: String
    let sessionId: String
    let dragId: String
    init(token: String, workspaceId: String, sessionId: String, dragId: String = UUID().uuidString) {
        self.token = token; self.workspaceId = workspaceId; self.sessionId = sessionId; self.dragId = dragId
    }
}

extension AppStore {
    func layoutForWorkspace(_ workspaceId: String) -> PaneLayoutNode? { snapshot.paneLayouts?[workspaceId] }
    var activePaneLayoutMode: String { snapshot.activeWorkspaceId.map(paneLayoutMode) ?? "tabs" }

    func paneLayoutMode(_ workspaceId: String) -> String {
        if let mode = snapshot.paneLayoutModes?[workspaceId], PaneLayouts.viewModes.contains(mode) { return mode }
        return PaneLayouts.workspaceModes(workspaceIds: [workspaceId], activeWorkspaceId: snapshot.activeWorkspaceId, legacyMode: snapshot.layout, layouts: snapshot.paneLayouts, savedModes: snapshot.paneLayoutModes)[workspaceId] ?? "tabs"
    }

    /// Publish workspace, active tab, tab-group selection and remembered
    /// selection as one value. Observers never see a new workspace paired with
    /// the previous workspace's pane, or the old tab while focus is changing.
    func paneSelectionSnapshot(workspaceId: String, sessionId: String?, layout: PaneLayoutNode? = nil, mode: String? = nil) -> AppSnapshot {
        var next = snapshot
        let ids = snapshot.sessions.filter { $0.workspaceId == workspaceId }.map(\.id)
        let selected = sessionId.flatMap { ids.contains($0) ? $0 : nil }
        let mode = mode ?? paneLayoutMode(workspaceId)
        let initial = layout ?? layoutForWorkspace(workspaceId) ?? PaneLayouts.preset(sessionIds: ids, activeId: selected, mode: mode)
        let root = PaneLayouts.normalized(root: initial, sessionIds: ids, activeId: selected)
        let active = selected ?? root?.firstSelectedSessionId
        next.activeWorkspaceId = workspaceId
        next.activeSessionId = active
        var layouts = next.paneLayouts ?? [:]
        if let root { layouts[workspaceId] = root } else { layouts.removeValue(forKey: workspaceId) }
        next.paneLayouts = layouts
        var modes = next.paneLayoutModes ?? [:]; modes[workspaceId] = mode; next.paneLayoutModes = modes
        var selections = next.paneLayoutActiveSessionIds ?? [:]
        if let active { selections[workspaceId] = active } else { selections.removeValue(forKey: workspaceId) }
        next.paneLayoutActiveSessionIds = selections
        return next
    }

    func setPaneLayoutMode(_ mode: String, workspaceId: String) {
        guard PaneLayouts.viewModes.contains(mode), snapshot.workspaces.contains(where: { $0.id == workspaceId }) else { return }
        var modes = snapshot.paneLayoutModes ?? [:]; modes[workspaceId] = mode
        if snapshot.paneLayoutModes != modes { snapshot.paneLayoutModes = modes }
    }

    func setPaneFocus(_ focused: Bool, sessionId: String? = nil) {
        guard !hasModal else { return }
        if let sessionId { selectSession(sessionId) }
        guard let workspace = activeWorkspace else { return }
        setPaneLayoutMode(focused ? "focus" : (layoutForWorkspace(workspace.id)?.kind == "split" ? "custom" : "tabs"), workspaceId: workspace.id)
    }

    func preparePaneLayouts() {
        snapshot.paneLayoutModes = PaneLayouts.workspaceModes(workspaceIds: snapshot.workspaces.map(\.id), activeWorkspaceId: snapshot.activeWorkspaceId, legacyMode: snapshot.layout, layouts: snapshot.paneLayouts, savedModes: snapshot.paneLayoutModes)
        for workspace in snapshot.workspaces { reconcilePaneLayout(workspace.id) }
        let valid = Set(snapshot.workspaces.map(\.id))
        let layouts = (snapshot.paneLayouts ?? [:]).filter { valid.contains($0.key) }
        if snapshot.paneLayouts != layouts { snapshot.paneLayouts = layouts }
        snapshot.paneLayoutActiveSessionIds = (snapshot.paneLayoutActiveSessionIds ?? [:]).filter { valid.contains($0.key) }
    }

    func reconcilePaneLayout(_ workspaceId: String) {
        let ids = snapshot.sessions.filter { $0.workspaceId == workspaceId }.map(\.id)
        let active = snapshot.activeWorkspaceId == workspaceId ? snapshot.activeSessionId : snapshot.paneLayoutActiveSessionIds?[workspaceId]
        let existing = layoutForWorkspace(workspaceId)
        let mode = paneLayoutMode(workspaceId)
        let initial = existing ?? PaneLayouts.preset(sessionIds: ids, activeId: active, mode: mode)
        savePaneLayout(PaneLayouts.normalized(root: initial, sessionIds: ids, activeId: active), workspaceId: workspaceId)
        setPaneLayoutMode(mode, workspaceId: workspaceId)
        let selected = active.flatMap { ids.contains($0) ? $0 : nil } ?? layoutForWorkspace(workspaceId)?.firstSelectedSessionId
        rememberPaneSelection(selected, workspaceId: workspaceId)
    }

    func setPaneLayoutPreset(_ mode: String) {
        guard ["grid", "columns", "tabs"].contains(mode), !hasModal, let workspace = activeWorkspace else { return }
        savePaneLayout(PaneLayouts.preset(sessionIds: activeSessions.map(\.id), activeId: snapshot.activeSessionId, mode: mode), workspaceId: workspace.id)
        setPaneLayoutMode(mode, workspaceId: workspace.id)
    }

    func togglePaneFocus(_ sessionId: String? = nil) {
        if let sessionId { selectSession(sessionId) }
        setPaneFocus(activePaneLayoutMode != "focus")
    }

    func movePane(sessionId: String, targetGroupId: String, placement: String, beforeSessionId: String? = nil) {
        let placement = placement == "center" ? "tab" : placement
        guard ["tab", "left", "right", "top", "bottom"].contains(placement), !hasModal, let workspace = activeWorkspace,
              snapshot.sessions.contains(where: { $0.id == sessionId && $0.workspaceId == workspace.id }),
              let root = layoutForWorkspace(workspace.id),
              let target = root.node(withId: targetGroupId), target.kind == "tabs" else { return }
        if let beforeSessionId, !target.sessionIds.contains(beforeSessionId) { return }
        let moved = PaneLayouts.moving(root: root, sessionId: sessionId, targetGroupId: targetGroupId, placement: placement, beforeSessionId: beforeSessionId)
        let next = paneSelectionSnapshot(workspaceId: workspace.id, sessionId: sessionId, layout: moved, mode: "custom")
        if next != snapshot { snapshot = next }
    }

    func resizePaneSplit(_ id: String, ratio: Double) {
        guard !hasModal, ratio.isFinite, let workspace = activeWorkspace, let root = layoutForWorkspace(workspace.id) else { return }
        savePaneLayout(PaneLayouts.resizing(root: root, splitId: id, ratio: ratio), workspaceId: workspace.id)
    }

    func selectPaneInLayout(_ sessionId: String, workspaceId: String) {
        if layoutForWorkspace(workspaceId) == nil { reconcilePaneLayout(workspaceId) }
        savePaneLayout(PaneLayouts.selecting(root: layoutForWorkspace(workspaceId), id: sessionId), workspaceId: workspaceId)
        rememberPaneSelection(sessionId, workspaceId: workspaceId)
    }

    func rememberPaneSelection(_ sessionId: String?, workspaceId: String) {
        var selected = snapshot.paneLayoutActiveSessionIds ?? [:]
        if let sessionId, snapshot.sessions.contains(where: { $0.id == sessionId && $0.workspaceId == workspaceId }) { selected[workspaceId] = sessionId }
        else { selected.removeValue(forKey: workspaceId) }
        if selected != snapshot.paneLayoutActiveSessionIds { snapshot.paneLayoutActiveSessionIds = selected }
    }

    func beginPaneDrag(_ sessionId: String) -> PaneDragPayload? {
        guard let session = snapshot.sessions.first(where: { $0.id == sessionId }), !hasModal,
              snapshot.activeWorkspaceId == session.workspaceId else { return nil }
        let payload = PaneDragPayload(token: paneDragToken, workspaceId: session.workspaceId, sessionId: sessionId)
        draggedPane = payload
        return payload
    }

    func cancelPaneDrag(_ payload: PaneDragPayload) {
        if draggedPane == payload { draggedPane = nil }
    }

    @discardableResult
    func finishPaneDrag(_ payload: PaneDragPayload, workspaceId: String, groupId: String, placement: String, beforeSessionId: String? = nil) -> Bool {
        guard draggedPane == payload else { return false }
        defer { cancelPaneDrag(payload) }
        guard canDropPane(in: workspaceId), payload.token == paneDragToken, payload.workspaceId == workspaceId,
              let group = layoutForWorkspace(workspaceId)?.node(withId: groupId), group.kind == "tabs",
              beforeSessionId == nil || group.sessionIds.contains(beforeSessionId!) else { return false }
        movePane(sessionId: payload.sessionId, targetGroupId: groupId, placement: placement, beforeSessionId: beforeSessionId)
        return true
    }

    func paneDragProvider(_ sessionId: String) -> NSItemProvider {
        let provider = NSItemProvider()
        guard let payload = beginPaneDrag(sessionId), let data = try? JSONEncoder().encode(payload) else { return provider }
        provider.suggestedName = snapshot.sessions.first { $0.id == sessionId }?.title
        // A separate, process-local data type leaves file drops and clipboard
        // data on the composer attachment path.
        provider.registerDataRepresentation(forTypeIdentifier: PaneDragPayload.contentType.identifier, visibility: .ownProcess) { completion in
            completion(data, nil)
            return nil
        }
        return provider
    }

    func canDropPane(in workspaceId: String) -> Bool {
        guard !hasModal, let draggedPane, draggedPane.token == paneDragToken,
              draggedPane.workspaceId == workspaceId, snapshot.activeWorkspaceId == workspaceId else { return false }
        return snapshot.sessions.contains { $0.id == draggedPane.sessionId && $0.workspaceId == workspaceId }
    }

    func receivePaneDrop(_ provider: NSItemProvider, workspaceId: String, groupId: String, placement: String, beforeSessionId: String? = nil) {
        guard let expected = draggedPane else { return }
        provider.loadDataRepresentation(forTypeIdentifier: PaneDragPayload.contentType.identifier) { [weak self] data, _ in
            DispatchQueue.main.async {
                guard let self else { return }
                guard let data, data.count <= 2048, let payload = try? JSONDecoder().decode(PaneDragPayload.self, from: data), payload == expected else { return }
                self.finishPaneDrag(payload, workspaceId: workspaceId, groupId: groupId, placement: placement, beforeSessionId: beforeSessionId)
            }
        }
    }

    func savePaneLayout(_ root: PaneLayoutNode?, workspaceId: String) {
        var layouts = snapshot.paneLayouts ?? [:]
        if let root { layouts[workspaceId] = root }
        else { layouts.removeValue(forKey: workspaceId) }
        if layouts != snapshot.paneLayouts { snapshot.paneLayouts = layouts }
    }
}

extension PaneLayoutNode {
    func node(withId id: String) -> PaneLayoutNode? {
        if self.id == id { return self }
        return children.lazy.compactMap { $0.node(withId: id) }.first
    }

    func group(containing sessionId: String) -> PaneLayoutNode? {
        if kind == "tabs", sessionIds.contains(sessionId) { return self }
        return children.lazy.compactMap { $0.group(containing: sessionId) }.first
    }

    var firstSelectedSessionId: String? {
        if kind == "tabs" { return selectedSessionId ?? sessionIds.first }
        return children.lazy.compactMap(\.firstSelectedSessionId).first
    }
}
