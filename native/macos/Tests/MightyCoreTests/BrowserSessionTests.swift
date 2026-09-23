import Foundation
import Testing
@testable import MightyCore

struct BrowserSessionTests {
    @Test func browserSessionKindRoundTrips() throws {
        let session = RunSession(workspaceId: "ws-1", title: "Browser", kind: "browser")
        let data = try JSONEncoder().encode(session)
        let decoded = try JSONDecoder().decode(RunSession.self, from: data)
        #expect(decoded.kind == "browser")
        #expect(decoded.workspaceId == "ws-1")
    }

    @Test func browserSessionKeepsWorkspaceProfileKey() throws {
        var session = RunSession(workspaceId: "ws-1", title: "Browser", kind: "browser")
        session.workspaceProfileKey = "my-workspace-profile-key"
        let data = try JSONEncoder().encode(session)
        let decoded = try JSONDecoder().decode(RunSession.self, from: data)
        #expect(decoded.workspaceProfileKey == "my-workspace-profile-key")
        #expect(decoded.kind == "browser")
    }

    @Test func browserSessionOwnerIsOptional() throws {
        var session = RunSession(workspaceId: "ws-1", title: "Browser", kind: "browser")
        #expect(session.ownerSessionId == nil)
        session.ownerSessionId = "owner-session-abc"
        let data = try JSONEncoder().encode(session)
        let decoded = try JSONDecoder().decode(RunSession.self, from: data)
        #expect(decoded.ownerSessionId == "owner-session-abc")
        // Stage 1: owner stays nil when not set
        var noOwner = RunSession(workspaceId: "ws-1", title: "Browser", kind: "browser")
        noOwner.workspaceProfileKey = "key"
        let noOwnerData = try JSONEncoder().encode(noOwner)
        let noOwnerDecoded = try JSONDecoder().decode(RunSession.self, from: noOwnerData)
        #expect(noOwnerDecoded.ownerSessionId == nil)
    }

    @Test func stateWithBrowserSessionLoads() throws {
        let json = Data("""
        {"version":1,"workspaces":[{"id":"ws-browser","name":"Test","path":"/private/tmp","createdAt":"2026-01-01T00:00:00Z"}],"sessions":[{"id":"sess-browser","workspaceId":"ws-browser","title":"Browser","kind":"browser","provider":"claude","model":"default","settings":{"effort":"default","permissionMode":"manual"},"status":"idle","logs":[],"createdAt":"2026-01-01T00:00:00Z","workspaceProfileKey":"ws-browser"}],"layout":"grid","theme":"dark","sidebarWidth":252}
        """.utf8)
        let snapshot = StateRepository.decodeSnapshot(json)
        #expect(snapshot.sessions.contains { $0.id == "sess-browser" && $0.kind == "browser" })
        #expect(snapshot.sessions.first { $0.id == "sess-browser" }?.workspaceProfileKey == "ws-browser")
    }
}
