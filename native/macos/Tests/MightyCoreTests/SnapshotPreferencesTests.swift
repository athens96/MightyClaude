import Foundation
import Testing
@testable import MightyCore

struct SnapshotPreferencesTests {
    @Test func expandedWorkspacesAndLegacyNumberedTitlesNormalize() throws {
        let a = Workspace(id: "a", name: "A", path: "/tmp/a"), b = Workspace(id: "b", name: "B", path: "/tmp/b")
        let sessions = [
            RunSession(id: "s1", workspaceId: "a", title: "Claude 1"),
            RunSession(id: "s2", workspaceId: "a", title: "Codex 3", provider: "codex"),
            RunSession(id: "s3", workspaceId: "b", title: "터미널 2", kind: "shell"),
            RunSession(id: "s5", workspaceId: "b", title: "My notes 7"),
            RunSession(id: "s6", workspaceId: "b", title: "Claude"),
        ]
        let snapshot = AppSnapshot(workspaces: [a, b], sessions: sessions, activeWorkspaceId: "a", expandedWorkspaceIds: ["b", "missing", "b"])
        let normalized = StateRepository.normalize(snapshot, restoring: false)
        #expect(normalized.expandedWorkspaceIds == ["b"])
        #expect(normalized.sessions.map(\.title) == ["Claude", "Codex", "터미널", "My notes 7", "Claude"])
        // Older state has no list: only the active workspace is open, as before.
        #expect(StateRepository.normalize(AppSnapshot(workspaces: [a], activeWorkspaceId: "a"), restoring: false).expandedWorkspaceIds == nil)
        let data = try JSONEncoder().encode(normalized)
        #expect(StateRepository.decodeSnapshot(data).expandedWorkspaceIds == ["b"])
        #expect(StateRepository.decodeSnapshot(data).sessions.map(\.title).prefix(2) == ["Claude", "Codex"])
    }

    @Test func aRestoredShellStartsIdleWhileAnAgentRunIsStopped() {
        let a = Workspace(id: "a", name: "A", path: "/tmp/a")
        let sessions = [
            RunSession(id: "shell", workspaceId: "a", title: "Terminal", kind: SessionKind.shell, status: "running"),
            RunSession(id: "agent", workspaceId: "a", title: "Claude", status: "running"),
            RunSession(id: "done", workspaceId: "a", title: "Terminal", kind: SessionKind.shell, status: "completed"),
        ]
        let snapshot = AppSnapshot(workspaces: [a], sessions: sessions, activeWorkspaceId: "a")
        #expect(StateRepository.normalize(snapshot, restoring: true).sessions.map(\.status) == ["idle", "stopped", "completed"])
        // A save while the app runs keeps the status as it is.
        #expect(StateRepository.normalize(snapshot, restoring: false).sessions.map(\.status) == ["running", "running", "completed"])
    }

    @Test func savedRemoteWorkspacesAreDroppedWithEverythingKeyedByThem() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-remote-drop-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
        let local = root.path
        let legacy = """
        {"version":1,
         "workspaces":[
           {"id":"local","name":"Local","path":"\(local)","createdAt":"2026-01-01T00:00:00Z"},
           {"id":"far","name":"Far","path":"/home/ubuntu/proj","createdAt":"2026-01-01T00:00:00Z",
            "remote":{"connectionId":"conn1","workspaceId":"w1","hostName":"ubuntu-box"}}],
         "sessions":[
           {"id":"s1","workspaceId":"local","title":"Claude"},
           {"id":"s2","workspaceId":"local","title":"Codex","provider":"codex"},
           {"id":"r1","workspaceId":"far","title":"Remote pane"},
           {"id":"r2","workspaceId":"far","title":"Remote shell","kind":"shell"}],
         "activeWorkspaceId":"far","activeSessionId":"r1",
         "layout":"grid","theme":"dark","sidebarWidth":252,
         "paneLayouts":{
           "local":{"id":"l","kind":"tabs","sessionIds":["s1","s2"],"selectedSessionId":"s2"},
           "far":{"id":"f","kind":"tabs","sessionIds":["r1","r2"],"selectedSessionId":"r1"}},
         "paneLayoutModes":{"local":"tabs","far":"custom"},
         "paneLayoutActiveSessionIds":{"local":"s2","far":"r1"},
         "expandedWorkspaceIds":["local","far"]}
        """
        try Data(legacy.utf8).write(to: root.appendingPathComponent("workspace-state.json"))
        let repository = StateRepository(directory: root, legacyStateURL: nil)
        let restored = try await repository.load()
        #expect(restored.workspaces.map(\.id) == ["local"])
        #expect(restored.workspaces.first?.path == local)
        #expect(restored.sessions.map(\.id) == ["s1", "s2"])
        #expect(restored.activeWorkspaceId == "local")
        #expect(restored.activeSessionId == "s1")
        #expect(restored.paneLayouts.map { Array($0.keys) } == ["local"])
        #expect(restored.paneLayoutModes.map { Array($0.keys) } == ["local"])
        #expect(restored.paneLayoutActiveSessionIds.map { Array($0.keys) } == ["local"])
        #expect(restored.expandedWorkspaceIds == ["local"])
        await #expect(throws: MightyError.self) { try await repository.workspace(id: "far") }
        try await repository.save(restored)
        let saved = try String(contentsOf: root.appendingPathComponent("workspace-state.json"), encoding: .utf8)
        #expect(!saved.contains("far") && !saved.contains("\"remote\""))
        // An explicit null was never a reference to another computer; that workspace stays.
        let nullRemote = #"{"version":1,"workspaces":[{"id":"n","name":"N","path":"/tmp/n","createdAt":"2026-01-01T00:00:00Z","remote":null}],"sessions":[]}"#
        #expect(StateRepository.decodeSnapshot(Data(nullRemote.utf8)).workspaces.map(\.id) == ["n"])
    }

    @Test func foldedSidebarPersistsAndKeepsItsWidth() async throws {
        // Older state has no field: the sidebar is open.
        let legacy = Data(#"{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300}"#.utf8)
        let old = StateRepository.decodeSnapshot(legacy)
        #expect(old.sidebarCollapsed == nil)
        #expect(old.sidebarWidth == 300)
        #expect(StateRepository.normalize(AppSnapshot(sidebarWidth: 280, sidebarCollapsed: true), restoring: false).sidebarCollapsed == true)
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-sidebar-fold-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        for collapsed in [true, false] {
            try await StateRepository(directory: directory, legacyStateURL: nil).save(AppSnapshot(sidebarWidth: 310, sidebarCollapsed: collapsed))
            let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
            #expect(restored.sidebarCollapsed == collapsed)
            #expect(restored.sidebarWidth == 310)
        }
        // A drag's width is saved inside the bounds, and not for a move of a point or less.
        #expect(SidebarFold.widthToSave(dragged: 280, saved: 252) == 280)
        #expect(SidebarFold.widthToSave(dragged: 300.5, saved: 300) == nil)
        #expect(SidebarFold.widthToSave(dragged: 500, saved: 300) == SidebarFold.maximumWidth)
        #expect(SidebarFold.widthToSave(dragged: 180, saved: 300) == SidebarFold.minimumWidth)
        #expect(SidebarFold.widthToSave(dragged: 180, saved: SidebarFold.minimumWidth) == nil)
        // Only a JSON boolean counts; anything else reads as open.
        var object = try #require(JSONSerialization.jsonObject(with: legacy) as? [String: Any])
        for invalid: Any in [1, "true", NSNull()] {
            object["sidebarCollapsed"] = invalid
            #expect(StateRepository.decodeSnapshot(try JSONSerialization.data(withJSONObject: object)).sidebarCollapsed == nil)
        }
    }

    @Test func sidebarWidthNormalizesIntoTheContractBounds() throws {
        func normalized(_ width: Double) -> Double { StateRepository.normalize(AppSnapshot(sidebarWidth: width), restoring: false).sidebarWidth }
        #expect(normalized(205) == 210)
        #expect(normalized(200) == 210)
        #expect(normalized(380) == 360)
        #expect(normalized(400) == 360)
        #expect(normalized(300) == 300)
        #expect(normalized(.nan) == 252)
        #expect(normalized(.infinity) == 252)
        // A state without the field starts at the default.
        #expect(StateRepository.decodeSnapshot(Data(#"{"version":1,"workspaces":[],"sessions":[]}"#.utf8)).sidebarWidth == SidebarFold.defaultWidth)
        #expect(AppSnapshot().sidebarWidth == SidebarFold.defaultWidth)
        // The bounds and the fold threshold are the design contract's, which Windows reads too.
        let layout = try #require((try DesignTokenParityTests.load()["metrics"] as? [String: Any])?["layout"] as? [String: Double])
        #expect(layout["sidebarMin"] == SidebarFold.minimumWidth)
        #expect(layout["sidebarDefault"] == SidebarFold.defaultWidth)
        #expect(layout["sidebarMax"] == SidebarFold.maximumWidth)
        #expect(layout["sidebarFoldThreshold"] == SidebarFold.foldThreshold)
    }

    @Test func sidebarBorderDragClampsOrFolds() {
        // Inside the bounds the border follows the pointer from where the drag began.
        #expect(SidebarFold.drag(startWidth: 252, translation: 40) == .width(292))
        #expect(SidebarFold.drag(startWidth: 300, translation: -30) == .width(270))
        // Past the bounds it stops at them.
        #expect(SidebarFold.drag(startWidth: 300, translation: 200) == .width(SidebarFold.maximumWidth))
        #expect(SidebarFold.drag(startWidth: 252, translation: -60) == .width(SidebarFold.minimumWidth))
        // Narrower than the fold threshold folds; the threshold itself still stays open at the minimum.
        #expect(SidebarFold.foldThreshold < SidebarFold.minimumWidth)
        #expect(SidebarFold.drag(startWidth: 252, translation: SidebarFold.foldThreshold - 252) == .width(SidebarFold.minimumWidth))
        #expect(SidebarFold.drag(startWidth: 252, translation: SidebarFold.foldThreshold - 252 - 0.5) == .fold)
        #expect(SidebarFold.drag(startWidth: 360, translation: -360) == .fold)
        // The double-click width and the clamp.
        #expect(SidebarFold.defaultWidth == 252)
        #expect(SidebarFold.clamp(SidebarFold.defaultWidth) == 252)
        #expect(SidebarFold.clamp(-.infinity) == SidebarFold.defaultWidth)
        #expect(SidebarFold.clamp(SidebarFold.minimumWidth - SidebarFold.step) == SidebarFold.minimumWidth)
    }

    @Test func numberedTitleStrippingOnlyTouchesGeneratedNames() {
        #expect(StateRepository.legacyNumberedTitle("Gemini 4") == "Gemini")
        #expect(StateRepository.legacyNumberedTitle("Claude 10") == "Claude")
        #expect(StateRepository.legacyNumberedTitle("Claude") == "Claude")
        #expect(StateRepository.legacyNumberedTitle("Claude 1 review") == "Claude 1 review")
        #expect(StateRepository.legacyNumberedTitle("Release 2") == "Release 2")
        #expect(StateRepository.legacyNumberedTitle("claude 2") == "claude 2")
    }
}
