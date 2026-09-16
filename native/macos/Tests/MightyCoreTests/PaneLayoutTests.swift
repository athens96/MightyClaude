import Foundation
import Testing
@testable import MightyCore

struct PaneLayoutTests {
    private func tabs(_ id: String, _ sessions: [String], selected: String? = nil) -> PaneLayoutNode {
        PaneLayoutNode(id: id, kind: "tabs", sessionIds: sessions, selectedSessionId: selected ?? sessions.first)
    }
    private func split(_ first: PaneLayoutNode, _ second: PaneLayoutNode, id: String = "root") -> PaneLayoutNode {
        PaneLayoutNode(id: id, kind: "split", axis: "horizontal", children: [first, second])
    }
    private func nodes(_ root: PaneLayoutNode) -> [PaneLayoutNode] { [root] + root.children.flatMap(nodes) }
    private func sessions(_ root: PaneLayoutNode) -> [String] { nodes(root).filter { $0.kind == "tabs" }.flatMap(\.sessionIds) }
    private func depth(_ root: PaneLayoutNode) -> Int { 1 + (root.children.map(depth).max() ?? 0) }

    @Test func oldSnapshotsAndMinimalNodesKeepWireCompatibility() throws {
        let legacy = Data(#"{"version":1,"workspaces":[],"sessions":[],"layout":"columns","theme":"dark","sidebarWidth":252}"#.utf8)
        let snapshot = try JSONDecoder().decode(AppSnapshot.self, from: legacy)
        #expect(snapshot.paneLayouts == nil)
        #expect(snapshot.paneLayoutModes == nil && snapshot.paneLayoutActiveSessionIds == nil)
        let encoded = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(snapshot)) as? [String: Any])
        #expect(encoded["paneLayouts"] == nil)
        #expect(encoded["paneLayoutModes"] == nil && encoded["paneLayoutActiveSessionIds"] == nil)
        let minimal = try JSONDecoder().decode(PaneLayoutNode.self, from: Data(#"{"id":"group","kind":"tabs","sessionIds":["a"]}"#.utf8))
        #expect(minimal.children.isEmpty && minimal.ratio == 0.5 && minimal.selectedSessionId == nil)
        #expect(PaneLayouts.normalized(root: minimal, sessionIds: ["a"])?.selectedSessionId == "a")
        for mode in ["tabs", "custom"] { #expect(StateRepository.normalize(AppSnapshot(layout: mode), restoring: false).layout == mode) }
    }

    @Test func presetsContainEachSessionOnceAndUseBoundedBinarySplits() throws {
        for mode in ["grid", "columns", "focus", "tabs", "custom"] {
            let root = try #require(PaneLayouts.preset(sessionIds: ["a", "b", "a", "c", "d", "invalid id"], activeId: "c", mode: mode))
            #expect(sessions(root) == ["a", "b", "c", "d"])
            #expect(Set(nodes(root).map(\.id)).count == nodes(root).count)
            if ["tabs", "focus", "custom"].contains(mode) { #expect(root.kind == "tabs" && root.selectedSessionId == "c") }
            else {
                #expect(nodes(root).filter { $0.kind == "split" }.allSatisfy { $0.children.count == 2 && (0.15...0.85).contains($0.ratio) })
                if mode == "columns" { #expect(nodes(root).filter { $0.kind == "split" }.allSatisfy { $0.axis == "horizontal" }) }
                else { #expect(nodes(root).contains { $0.axis == "vertical" }) }
            }
        }
        #expect(PaneLayouts.preset(sessionIds: [], mode: "grid") == nil)
    }

    @Test func normalizationRepairsDuplicateNodesAndRestrictsWorkspaceMembership() throws {
        let root = PaneLayoutNode(id: "root", kind: "split", sessionIds: ["unused"], axis: "invalid", ratio: -3, children: [
            tabs("duplicate", ["a", "a", "foreign"], selected: "foreign"),
            split(tabs("duplicate", ["b"]), tabs("empty", []), id: "nested")
        ])
        let repaired = try #require(PaneLayouts.normalized(root: root, sessionIds: ["a", "b", "c", "d"], activeId: "c"))
        #expect(repaired.kind == "split" && repaired.axis == "horizontal" && repaired.ratio == 0.15)
        #expect(repaired.sessionIds.isEmpty)
        #expect(repaired.children[0].sessionIds == ["a", "c", "d"])
        #expect(repaired.children[0].selectedSessionId == "c")
        #expect(Set(sessions(repaired)) == Set(["a", "b", "c", "d"]))
        #expect(Set(nodes(repaired).map(\.id)).count == nodes(repaired).count)
        #expect(PaneLayouts.normalized(root: repaired, sessionIds: ["a", "b", "c", "d"], activeId: "c") == repaired)
    }

    @Test func tabMovesReorderAndRejectStaleOrCrossWorkspaceDropsWithoutMutation() throws {
        let root = split(tabs("left", ["a", "b"]), tabs("right", ["c", "d"]))
        let moved = try #require(PaneLayouts.moving(root: root, sessionId: "a", targetGroupId: "right", placement: "tab", beforeSessionId: "d"))
        #expect(moved.children[0].sessionIds == ["b"])
        #expect(moved.children[1].sessionIds == ["c", "a", "d"])
        #expect(moved.children[1].selectedSessionId == "a")
        for (source, target, placement, before) in [("foreign", "right", "tab", nil), ("a", "missing", "tab", nil), ("a", "root", "tab", nil), ("a", "right", "diagonal", nil), ("a", "right", "tab", "b")] as [(String, String, String, String?)] {
            #expect(PaneLayouts.moving(root: root, sessionId: source, targetGroupId: target, placement: placement, beforeSessionId: before) == root)
        }
        let group = tabs("group", ["a", "b", "c"])
        #expect(PaneLayouts.moving(root: group, sessionId: "c", targetGroupId: "group", placement: "tab", beforeSessionId: "a")?.sessionIds == ["c", "a", "b"])
        #expect(PaneLayouts.moving(root: group, sessionId: "a", targetGroupId: "group", placement: "tab")?.sessionIds == ["b", "c", "a"])
        #expect(PaneLayouts.moving(root: group, sessionId: "b", targetGroupId: "group", placement: "tab", beforeSessionId: "b") == group)
    }

    @Test func edgeDropsSplitOnlyTheDraggedTabAndPruneVacatedGroups() throws {
        let group = tabs("group", ["a", "b"], selected: "b")
        for placement in ["left", "right", "top", "bottom"] {
            let moved = try #require(PaneLayouts.moving(root: group, sessionId: "b", targetGroupId: "group", placement: placement))
            #expect(moved.kind == "split")
            #expect(moved.axis == (["left", "right"].contains(placement) ? "horizontal" : "vertical"))
            #expect(moved.children[0].sessionIds == (["left", "top"].contains(placement) ? ["b"] : ["a"]))
            #expect(Set(sessions(moved)) == Set(["a", "b"]))
            #expect(nodes(moved).contains { $0.id == "group" && $0.sessionIds == ["a"] })
        }
        let singleton = tabs("group", ["a"])
        #expect(PaneLayouts.moving(root: singleton, sessionId: "a", targetGroupId: "group", placement: "left") == singleton)
        let root = split(tabs("left", ["a"]), tabs("right", ["b", "c"]))
        let merged = try #require(PaneLayouts.moving(root: root, sessionId: "a", targetGroupId: "right", placement: "tab"))
        #expect(merged.id == "right" && merged.kind == "tabs" && merged.sessionIds == ["b", "c", "a"])
        let closed = try #require(PaneLayouts.normalized(root: root, sessionIds: ["b"]))
        #expect(closed.id == "right" && closed.sessionIds == ["b"])
        #expect(PaneLayouts.normalized(root: root, sessionIds: []) == nil)
    }

    @Test func selectionResizeAndExcessiveDepthStayBounded() throws {
        let root = split(tabs("left", ["a", "b"]), tabs("right", ["c", "d"], selected: "d"))
        let selected = try #require(PaneLayouts.selecting(root: root, id: "b"))
        #expect(selected.children[0].selectedSessionId == "b" && selected.children[1].selectedSessionId == "d")
        #expect(PaneLayouts.selecting(root: root, id: "foreign") == root)
        #expect(PaneLayouts.resizing(root: root, splitId: "root", ratio: 7)?.ratio == 0.85)
        #expect(PaneLayouts.resizing(root: root, splitId: "root", ratio: -2)?.ratio == 0.15)
        #expect(PaneLayouts.resizing(root: root, splitId: "root", ratio: .nan) == root)
        #expect(PaneLayouts.resizing(root: root, splitId: "missing", ratio: 0.7) == root)
        var deep = tabs("deep", ["a", "b"])
        var ids = ["a", "b"]
        for index in 0..<(PaneLayouts.maximumDepth - 1) {
            let id = "extra-\(index)"; ids.append(id)
            deep = split(deep, tabs("group-\(index)", [id]), id: "split-\(index)")
        }
        #expect(depth(deep) == PaneLayouts.maximumDepth)
        #expect(PaneLayouts.moving(root: deep, sessionId: "a", targetGroupId: "deep", placement: "right") == deep)
        let tooDeep = split(deep, tabs("tail", ["last"]), id: "too-deep"); ids.append("last")
        let normalized = try #require(PaneLayouts.normalized(root: tooDeep, sessionIds: ids))
        #expect(depth(normalized) <= PaneLayouts.maximumDepth && nodes(normalized).count <= PaneLayouts.maximumNodes)
        #expect(Set(sessions(normalized)) == Set(ids) && sessions(normalized).count == ids.count)
        let encoded = try JSONEncoder().encode(tooDeep)
        #expect(throws: (any Error).self) { try JSONDecoder().decode(PaneLayoutNode.self, from: encoded) }
    }

    @Test func newTabsAndGroupsAreInsertedAtomicallyWithoutRearrangingOtherGroups() throws {
        var root = split(tabs("left", ["a", "b"], selected: "b"), tabs("right", ["c", "d"], selected: "d"))
        root.ratio = 0.63
        let added = try #require(PaneLayouts.inserting(root: root, sessionId: "new-tab", targetGroupId: "right"))
        #expect(added.id == root.id && added.ratio == 0.63)
        #expect(added.children[0] == root.children[0])
        #expect(added.children[1].id == "right" && added.children[1].sessionIds == ["c", "d", "new-tab"])
        #expect(added.children[1].selectedSessionId == "new-tab")
        let newGroup = try #require(PaneLayouts.inserting(root: added, sessionId: "new-split", targetGroupId: "right", placement: "bottom"))
        #expect(newGroup.children[0] == root.children[0]); #expect(newGroup.ratio == 0.63)
        #expect(newGroup.children[1].axis == "vertical")
        #expect(newGroup.children[1].children[0] == added.children[1])
        #expect(newGroup.children[1].children[1].sessionIds == ["new-split"])
        #expect(PaneLayouts.inserting(root: root, sessionId: "a", targetGroupId: "right") == root)
        #expect(PaneLayouts.inserting(root: root, sessionId: "new", targetGroupId: "stale") == root)
        #expect(PaneLayouts.inserting(root: root, sessionId: "new", targetGroupId: "root") == root)
        #expect(PaneLayouts.inserting(root: nil, sessionId: "first")?.sessionIds == ["first"])
        #expect(PaneLayouts.inserting(root: nil, sessionId: "new", targetGroupId: "stale") == nil)
        var deep = tabs("deep", ["a"])
        for index in 0..<(PaneLayouts.maximumDepth - 1) { deep = split(deep, tabs("g-\(index)", ["s-\(index)"]), id: "n-\(index)") }
        #expect(PaneLayouts.inserting(root: deep, sessionId: "too-deep", targetGroupId: "deep", placement: "right") == deep)
        #expect(sessions(try #require(PaneLayouts.inserting(root: deep, sessionId: "safe-tab", targetGroupId: "deep"))).contains("safe-tab"))
    }

    @Test func legacyFocusAndLastSelectionMigratePerWorkspaceWithoutCrossWorkspaceReferences() throws {
        let workspaces = [Workspace(id: "one", name: "One", path: "/tmp/one"), Workspace(id: "two", name: "Two", path: "/tmp/two")]
        let panes = [RunSession(id: "a", workspaceId: "one", title: "A"), RunSession(id: "b", workspaceId: "one", title: "B"), RunSession(id: "c", workspaceId: "two", title: "C")]
        let root = split(tabs("left", ["a"]), tabs("right", ["b"]))
        let legacy = AppSnapshot(workspaces: workspaces, sessions: panes, activeWorkspaceId: "one", activeSessionId: "b", layout: "focus", paneLayouts: ["one": root, "two": tabs("other", ["c"])])
        let migrated = StateRepository.decodeSnapshot(try JSONEncoder().encode(legacy))
        #expect(migrated.paneLayoutModes == ["one": "focus", "two": "tabs"])
        #expect(migrated.paneLayoutActiveSessionIds == ["one": "b", "two": "c"])
        var switched = migrated
        switched.activeWorkspaceId = "two"; switched.activeSessionId = "c"
        switched.paneLayoutModes?["two"] = "custom"
        let preserved = StateRepository.normalize(switched, restoring: false)
        #expect(preserved.paneLayoutModes == ["one": "focus", "two": "custom"])
        #expect(preserved.paneLayoutActiveSessionIds?["one"] == "b")
        #expect(preserved.paneLayouts?["one"] == migrated.paneLayouts?["one"])
        var damaged = switched
        damaged.paneLayoutModes = ["one": "invalid", "two": "tabs", "gone": "focus"]
        damaged.paneLayoutActiveSessionIds = ["one": "c", "two": "a", "gone": "b"]
        let repaired = StateRepository.normalize(damaged, restoring: false)
        #expect(repaired.paneLayoutModes == ["one": "custom", "two": "tabs"])
        #expect(repaired.paneLayoutActiveSessionIds == ["one": "a", "two": "c"])
        let newWorkspace = PaneLayouts.workspaceModes(workspaceIds: ["new"], activeWorkspaceId: "new", legacyMode: "focus", layouts: nil, savedModes: migrated.paneLayoutModes)
        #expect(newWorkspace["new"] == "tabs")
    }

    @Test func snapshotSaveReloadPreservesEachWorkspaceTreeAndDropsOrphanKeys() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-layout-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let project = directory.appendingPathComponent("Project"), other = directory.appendingPathComponent("Other")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        let repository = StateRepository(directory: directory.appendingPathComponent("Profile"), legacyStateURL: nil)
        let first = try await repository.approveWorkspace(Workspace(id: "workspace", name: "Project", path: project.path))
        let second = try await repository.approveWorkspace(Workspace(id: "other", name: "Other", path: other.path))
        let panes = [RunSession(id: "a", workspaceId: first.id, title: "A"), RunSession(id: "b", workspaceId: first.id, title: "B"), RunSession(id: "c", workspaceId: second.id, title: "C")]
        let root = split(tabs("group-a", ["a"]), tabs("group-b", ["b", "c"]))
        let snapshot = AppSnapshot(workspaces: [first, second], sessions: panes, activeWorkspaceId: first.id, activeSessionId: "b", layout: "custom", paneLayouts: [first.id: root, second.id: tabs("group-c", ["c"]), "removed-workspace": tabs("orphan", ["a"])], paneLayoutModes: [first.id: "focus", second.id: "tabs", "removed-workspace": "columns"], paneLayoutActiveSessionIds: [first.id: "b", second.id: "c", "removed-workspace": "a"])
        try await repository.save(snapshot)
        let restored = try await StateRepository(directory: directory.appendingPathComponent("Profile"), legacyStateURL: nil).load()
        #expect(restored.layout == "custom" && restored.sessions == panes)
        #expect(Set(restored.paneLayouts?.keys.map { $0 } ?? []) == Set([first.id, second.id]))
        let restoredRoot = try #require(restored.paneLayouts?[first.id])
        #expect(restoredRoot.id == root.id && sessions(restoredRoot) == ["a", "b"])
        #expect(restoredRoot.children[1].selectedSessionId == "b")
        #expect(restored.paneLayouts?[second.id]?.sessionIds == ["c"])
        #expect(restored.paneLayoutModes == [first.id: "focus", second.id: "tabs"])
        #expect(restored.paneLayoutActiveSessionIds == [first.id: "b", second.id: "c"])
        let decoded = StateRepository.decodeSnapshot(try JSONEncoder().encode(restored))
        #expect(decoded.paneLayouts == restored.paneLayouts)
        #expect(decoded.paneLayoutModes == restored.paneLayoutModes && decoded.paneLayoutActiveSessionIds == restored.paneLayoutActiveSessionIds)
    }
}
