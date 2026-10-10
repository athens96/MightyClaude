import Foundation
import Testing
@testable import MightyCore

/// A delegated child pane's two optional snapshot keys, `parentSessionId` and
/// `workingFolder` (macOS only; the delegation file is the authority).
@Suite(.delegationLane) struct DelegationSnapshotTests {
    private let workspace = Workspace(id: "w", name: "W", path: "/tmp/w")

    private func child(_ id: String, kind: String = SessionKind.claude, provider: String = "claude", parent: String?, folder: String?) -> RunSession {
        var session = RunSession(id: id, workspaceId: "w", title: "Claude", kind: kind, provider: provider, createdAt: "2026-10-09T00:00:00Z")
        session.parentSessionId = parent; session.workingFolder = folder
        return session
    }

    @Test func decodeKeepsBothKeysAndADamagedValueKeepsTheConversation() throws {
        let json = """
        {"version":1,"workspaces":[{"id":"w","name":"W","path":"/tmp/w","createdAt":"2026-10-01T00:00:00Z"}],"sessions":[
          {"id":"parent","workspaceId":"w","title":"Claude","createdAt":"2026-10-09T00:00:00Z"},
          {"id":"child","workspaceId":"w","title":"Claude","createdAt":"2026-10-09T00:00:00Z","parentSessionId":"parent","workingFolder":"/tmp/worktrees/child"},
          {"id":"damaged","workspaceId":"w","title":"Claude","createdAt":"2026-10-09T00:00:00Z","parentSessionId":7,"workingFolder":["x"],
           "logs":[{"id":"l1","kind":"user","text":"keep me","timestamp":"2026-10-09T00:00:01Z"}]}
        ],"activeWorkspaceId":"w"}
        """
        let snapshot = StateRepository.decodeSnapshot(Data(json.utf8))
        #expect(snapshot.sessions.map(\.id) == ["parent", "child", "damaged"])
        let child = try #require(snapshot.sessions.first { $0.id == "child" })
        #expect(child.parentSessionId == "parent"); #expect(child.workingFolder == "/tmp/worktrees/child")
        let damaged = try #require(snapshot.sessions.first { $0.id == "damaged" })
        #expect(damaged.parentSessionId == nil); #expect(damaged.workingFolder == nil)
        #expect(damaged.logs.map(\.text) == ["keep me"])
        // Encoding writes both keys back; a decode of that restores them exactly.
        let again = StateRepository.decodeSnapshot(try JSONEncoder().encode(snapshot))
        #expect(again.sessions.first { $0.id == "child" }?.parentSessionId == "parent")
        #expect(again.sessions.first { $0.id == "child" }?.workingFolder == "/tmp/worktrees/child")
        #expect(again == snapshot)
    }

    @Test func normalizeKeepsWellFormedLinksEvenForAClosedParentAndDropsTheRest() {
        let sessions = [
            // The parent pane was closed: its child keeps the link.
            child("child", parent: "closed-parent", folder: "/tmp/worktrees/child"),
            // A pane switched to another provider stays a child; the delegation file decides.
            child("codex", provider: "codex", parent: "closed-parent", folder: "/tmp/worktrees/codex"),
            child("self", parent: "self", folder: "relative/folder"),
            child("bad", parent: "not an id", folder: "/tmp/a\u{0}b"),
            child("long", parent: "p", folder: "/" + String(repeating: "a", count: 4096)),
            child("shell", kind: SessionKind.shell, parent: "child", folder: "/tmp/worktrees/shell"),
            child("browser", kind: SessionKind.browser, parent: "child", folder: "/tmp/worktrees/browser"),
            RunSession(id: "plain", workspaceId: "w", title: "Claude", createdAt: "2026-10-09T00:00:00Z"),
        ]
        for restoring in [true, false] {
            let output = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: sessions, activeWorkspaceId: "w"), restoring: restoring)
            func links(_ id: String) -> [String?] {
                let session = output.sessions.first { $0.id == id }
                return [session?.parentSessionId, session?.workingFolder]
            }
            #expect(output.sessions.map(\.id) == sessions.map(\.id))
            #expect(links("child") == ["closed-parent", "/tmp/worktrees/child"])
            #expect(links("codex") == ["closed-parent", "/tmp/worktrees/codex"])
            #expect(links("self") == [nil, nil])
            #expect(links("bad") == [nil, nil])
            #expect(links("long") == ["p", nil])
            #expect(links("shell") == [nil, nil])
            #expect(links("browser") == [nil, nil])
            #expect(links("plain") == [nil, nil])
        }
    }

    @Test func aNewPaneNeverInheritsTheLinksFromItsTemplate() throws {
        let tuned = RunSettings(effort: "high", permissionMode: "acceptEdits")
        let parent = RunSession(id: "parent", workspaceId: "w", title: "Claude", createdAt: "2026-10-01T00:00:00Z")
        var recent = child("child", parent: "parent", folder: "/tmp/worktrees/child")
        recent.model = "claude-opus-5"; recent.settings = tuned
        recent.logs = [LogEntry(id: "c1", kind: "user", text: "hi", timestamp: "2026-10-09T10:00:00Z")]
        // The child is the most recently used Claude pane, so it is the template.
        let template = try #require(RunSession.template(kind: SessionKind.claude, provider: "claude", in: [parent, recent]))
        #expect(template.id == "child")
        var fresh = RunSession(workspaceId: "w", title: "Claude")
        fresh.inheritSettings(from: template)
        #expect(fresh.model == "claude-opus-5"); #expect(fresh.settings == tuned)
        #expect(fresh.parentSessionId == nil); #expect(fresh.workingFolder == nil)
        // Run settings never carry them, from the child or the new pane.
        for settings in [template.settings, fresh.settings] {
            let text = String(decoding: try JSONEncoder().encode(settings), as: UTF8.self)
            #expect(!text.contains("parentSessionId")); #expect(!text.contains("workingFolder"))
        }
        let saved = String(decoding: try JSONEncoder().encode(fresh), as: UTF8.self)
        #expect(!saved.contains("parentSessionId")); #expect(!saved.contains("workingFolder"))
    }

    @Test func aStateFileWrittenBeforeTheKeysLoadsUnchanged() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-delegation-snapshot-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let old = """
        {"version":1,"workspaces":[{"id":"w","name":"W","path":"/tmp/w","createdAt":"2026-10-01T00:00:00Z"}],"sessions":[
          {"id":"a","workspaceId":"w","title":"Review","kind":"claude","provider":"claude","model":"claude-opus-5",
           "settings":{"effort":"high","permissionMode":"acceptEdits"},"status":"completed","titleMode":"fixed",
           "resumeId":"resume-a","createdAt":"2026-10-01T00:00:00Z",
           "logs":[{"id":"l1","kind":"user","text":"check the parser","timestamp":"2026-10-01T00:00:01Z"},
                   {"id":"l2","kind":"assistant","text":"done","timestamp":"2026-10-01T00:00:05Z"}]},
          {"id":"t","workspaceId":"w","title":"Terminal","kind":"shell","status":"idle","createdAt":"2026-10-01T00:00:00Z"}
        ],"activeWorkspaceId":"w","activeSessionId":"a","layout":"grid","theme":"dark","sidebarWidth":260}
        """
        let data = Data(old.utf8)
        let url = root.appendingPathComponent("workspace-state.json")
        try data.write(to: url)
        let loaded = try await StateRepository(directory: root, legacyStateURL: nil).load()
        #expect(loaded == StateRepository.decodeSnapshot(data))
        // Loading leaves the file as it was written.
        #expect(try Data(contentsOf: url) == data)
        #expect(loaded.sessions.map(\.id) == ["a", "t"])
        let agent = try #require(loaded.sessions.first)
        #expect(agent.title == "Review"); #expect(agent.model == "claude-opus-5"); #expect(agent.resumeId == "resume-a")
        #expect(agent.settings.effort == "high"); #expect(agent.settings.permissionMode == "acceptEdits"); #expect(agent.status == "completed")
        #expect(agent.logs.map(\.text) == ["check the parser", "done"])
        #expect(loaded.sessions.allSatisfy { $0.parentSessionId == nil && $0.workingFolder == nil })
        // Saving it again adds neither key.
        let resaved = String(decoding: try JSONEncoder().encode(loaded), as: UTF8.self)
        #expect(!resaved.contains("parentSessionId")); #expect(!resaved.contains("workingFolder"))
        #expect(StateRepository.decodeSnapshot(Data(resaved.utf8)) == loaded)
    }
}
