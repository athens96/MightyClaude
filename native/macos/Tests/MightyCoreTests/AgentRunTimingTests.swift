import Foundation
import Testing
@testable import MightyCore

struct AgentRunTimingTests {
    @Test func elapsedIncludesWaitingAndFreezesAtFirstTerminalEvent() {
        let start = Date(timeIntervalSince1970: 1_000)
        var timing = AgentRunTiming(startedAt: start)
        #expect(timing.label(at: start.addingTimeInterval(65)) == "01:05")
        #expect(timing.label(at: start.addingTimeInterval(3_723)) == "1:02:03")
        timing.finish(at: start.addingTimeInterval(73))
        timing.finish(at: start.addingTimeInterval(90))
        #expect(timing.elapsed(at: start.addingTimeInterval(5_000)) == 73)
        #expect(timing.label(at: start.addingTimeInterval(5_000)) == "01:13")
        let next = AgentRunTiming(startedAt: start.addingTimeInterval(100))
        #expect(next.label(at: start.addingTimeInterval(102)) == "00:02")
    }

    @Test func backwardsClockCannotDisplayNegativeTime() {
        let start = Date(timeIntervalSince1970: 1_000)
        var timing = AgentRunTiming(startedAt: start)
        #expect(timing.label(at: start.addingTimeInterval(-5)) == "00:00")
        timing.finish(at: start.addingTimeInterval(-5))
        #expect(timing.elapsed(at: start.addingTimeInterval(50)) == 0)
    }

    @Test func durableSessionClockUsesWholeRunLifecycleForEveryProvider() {
        let start = Date(timeIntervalSince1970: 1_000)
        for provider in ProviderOptions.ids {
            var session = RunSession(workspaceId: "local-or-remote", title: provider, provider: provider)
            session.beginRunTiming(at: start); session.status = "running"
            session.recordRunTiming(RunEvent(sessionId: session.id, type: "status", status: "running"), at: start.addingTimeInterval(3))
            let tool = AgentActivity(provider: provider, kind: "tool", state: "completed", summary: "fixture")
            session.recordRunTiming(RunEvent(sessionId: session.id, type: "activity", activity: tool), at: start.addingTimeInterval(8))
            #expect(session.runTiming?.elapsed(at: start.addingTimeInterval(10)) == 10)
            #expect(session.runTiming?.finishedAt == nil)
            session.recordRunTiming(RunEvent(sessionId: session.id, type: "status", status: "stopped"), at: start.addingTimeInterval(12))
            session.recordRunTiming(RunEvent(sessionId: session.id, type: "status", status: "completed"), at: start.addingTimeInterval(20))
            #expect(session.runTiming?.elapsed(at: start.addingTimeInterval(1_000)) == 12)
            session.beginRunTiming(at: start.addingTimeInterval(100))
            #expect(session.runTiming?.elapsed(at: start.addingTimeInterval(102)) == 2)
            #expect(session.runTiming?.isApproximate == false)
        }
    }

    @Test func repositoryRestoresCompletedAndInterruptedClocksWithoutDowntime() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-timing-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Timing", path: directory.path))
        var completed = RunSession(workspaceId: workspace.id, title: "Completed", status: "completed")
        completed.beginRunTiming(at: Date(timeIntervalSince1970: 1_000)); completed.runTiming?.finish(at: Date(timeIntervalSince1970: 1_073))
        var interrupted = RunSession(workspaceId: workspace.id, title: "Interrupted", provider: "codex", status: "running")
        interrupted.beginRunTiming(at: Date().addingTimeInterval(-30))
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [completed, interrupted]))
        let bytes = try Data(contentsOf: directory.appendingPathComponent("workspace-state.json"))
        let saved = try JSONDecoder().decode(AppSnapshot.self, from: bytes)
        let checkpoint = try #require(saved.sessions.last?.runTiming?.lastObservedAt)
        let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
        #expect(restored.sessions.first?.runTiming == completed.runTiming)
        #expect(restored.sessions.last?.status == "stopped")
        #expect(restored.sessions.last?.runTiming?.finishedAt == checkpoint)
        #expect(restored.sessions.last?.runTiming?.isApproximate == true)
        let frozen = try #require(restored.sessions.last?.runTiming)
        #expect(frozen.elapsed(at: checkpoint.addingTimeInterval(86_400)) == frozen.elapsed(at: checkpoint))
        #expect(frozen.label(at: checkpoint).hasPrefix("약 "))
        #expect(frozen.elapsed(at: checkpoint) >= 29)
    }

    @Test func legacyInferenceNeedsRealResponseAndOnlyUsesLatestRequest() {
        let workspace = Workspace(id: "workspace", name: "Legacy", path: "/tmp")
        var session = RunSession(id: "legacy", workspaceId: workspace.id, title: "Legacy", status: "completed", logs: [
            LogEntry(kind: "user", text: "older", timestamp: "2026-09-16T01:00:00Z"),
            LogEntry(kind: "assistant", text: "older response", timestamp: "2026-09-16T01:01:00Z"),
            LogEntry(kind: "user", text: "latest", timestamp: "2026-09-16T02:00:00.000Z"),
            LogEntry(kind: "assistant", text: "latest response", timestamp: "2026-09-16T02:01:13.000Z"),
            LogEntry(kind: "system", text: "settings changed", timestamp: "2026-09-16T05:00:00Z")
        ])
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: true)
        #expect(restored.sessions.first?.runTiming?.label() == "약 01:13")
        session.logs.remove(at: 3)
        #expect(AgentRunTiming.inferred(from: session.logs) == nil)
        session.logs = [LogEntry(kind: "assistant", text: "missing request", timestamp: "2026-09-16T02:01:13Z")]
        #expect(AgentRunTiming.inferred(from: session.logs) == nil)
    }

    @Test func damagedOptionalTimingPreservesConversationAndNonFiniteClockIsSafe() throws {
        let data = try JSONEncoder().encode(RunSession(workspaceId: "workspace", title: "Keep me", logs: [LogEntry(kind: "assistant", text: "retained")]))
        var object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        object["runTiming"] = ["startedAt": "not-a-date", "lastObservedAt": "2026-09-16T00:00:00Z"]
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(decoded.title == "Keep me"); #expect(decoded.logs.first?.text == "retained"); #expect(decoded.runTiming == nil)
        let invalid = AgentRunTiming(startedAt: Date(timeIntervalSince1970: .infinity))
        #expect(!invalid.isValid); #expect(invalid.label() == "00:00")
        let valid = AgentRunTiming(startedAt: Date(timeIntervalSince1970: 0))
        #expect(valid.label(at: Date(timeIntervalSince1970: .nan)) == "00:00")
    }
}
