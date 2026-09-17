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
            RunSession(id: "s4", workspaceId: "b", title: "원격 명령 12", kind: "shell"),
            RunSession(id: "s5", workspaceId: "b", title: "My notes 7"),
            RunSession(id: "s6", workspaceId: "b", title: "Claude"),
        ]
        let snapshot = AppSnapshot(workspaces: [a, b], sessions: sessions, activeWorkspaceId: "a", expandedWorkspaceIds: ["b", "missing", "b"])
        let normalized = StateRepository.normalize(snapshot, restoring: false)
        #expect(normalized.expandedWorkspaceIds == ["b"])
        #expect(normalized.sessions.map(\.title) == ["Claude", "Codex", "터미널", "원격 명령", "My notes 7", "Claude"])
        // Older state has no list: only the active workspace is open, as before.
        #expect(StateRepository.normalize(AppSnapshot(workspaces: [a], activeWorkspaceId: "a"), restoring: false).expandedWorkspaceIds == nil)
        let data = try JSONEncoder().encode(normalized)
        #expect(StateRepository.decodeSnapshot(data).expandedWorkspaceIds == ["b"])
        #expect(StateRepository.decodeSnapshot(data).sessions.map(\.title).prefix(2) == ["Claude", "Codex"])
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
