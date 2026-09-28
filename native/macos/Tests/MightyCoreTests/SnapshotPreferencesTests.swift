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

    @Test func numberedTitleStrippingOnlyTouchesGeneratedNames() {
        #expect(StateRepository.legacyNumberedTitle("Gemini 4") == "Gemini")
        #expect(StateRepository.legacyNumberedTitle("Claude 10") == "Claude")
        #expect(StateRepository.legacyNumberedTitle("Claude") == "Claude")
        #expect(StateRepository.legacyNumberedTitle("Claude 1 review") == "Claude 1 review")
        #expect(StateRepository.legacyNumberedTitle("Release 2") == "Release 2")
        #expect(StateRepository.legacyNumberedTitle("claude 2") == "claude 2")
    }
}
