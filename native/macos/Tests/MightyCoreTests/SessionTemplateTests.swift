import Foundation
import Testing
@testable import MightyCore

struct SessionTemplateTests {
    private func session(_ id: String, kind: String = "claude", provider: String = "claude", createdAt: String, logAt: String? = nil,
                         model: String = "default", settings: RunSettings = .init(), mode: String? = nil) -> RunSession {
        var value = RunSession(id: id, workspaceId: "w", title: id, kind: kind, provider: provider, model: model, settings: settings, createdAt: createdAt)
        value.agentViewMode = mode
        value.resumeId = "resume-" + id
        if let logAt { value.logs = [LogEntry(id: id + "-log", kind: "user", text: "hi", timestamp: logAt)] }
        return value
    }

    @Test func mostRecentlyUsedPaneOfTheSameProviderIsTheTemplate() {
        let tuned = RunSettings(effort: "high", permissionMode: "acceptEdits", maxTurns: 40, maxBudgetUsd: 3, fastMode: true, webSearch: "on", networkAccess: true)
        let sessions = [
            session("old-used", createdAt: "2026-09-01T00:00:00Z", logAt: "2026-09-17T10:00:00Z", model: "claude-opus-5", settings: tuned, mode: "mighty"),
            session("new-unused", createdAt: "2026-09-17T09:00:00Z", model: "claude-sonnet-5"),
            session("codex", provider: "codex", createdAt: "2026-09-17T11:00:00Z", model: "gpt-5.6", settings: RunSettings(effort: "medium", permissionMode: "acceptEdits")),
            session("shell", kind: "shell", createdAt: "2026-09-17T12:00:00Z"),
            session("broken", createdAt: "not-a-date", logAt: "also-not-a-date", model: "ignored"),
        ]
        // A pane used at 10:00 outranks one merely created at 09:00.
        let claude = RunSession.template(kind: "claude", provider: "claude", in: sessions)
        #expect(claude?.id == "old-used")
        #expect(RunSession.template(kind: "claude", provider: "codex", in: sessions)?.id == "codex")
        #expect(RunSession.template(kind: "claude", provider: "gemini", in: sessions) == nil)
        #expect(RunSession.template(kind: "shell", provider: "claude", in: sessions) == nil)
        #expect(RunSession.template(kind: "claude", provider: "claude", in: sessions, excluding: "old-used")?.id == "new-unused")

        var fresh = RunSession(workspaceId: "w2", title: "Claude 3")
        fresh.inheritSettings(from: claude!)
        #expect(fresh.model == "claude-opus-5"); #expect(fresh.settings == tuned); #expect(fresh.agentViewMode == "mighty")
        #expect(fresh.resumeId == nil); #expect(fresh.logs.isEmpty); #expect(fresh.title == "Claude 3"); #expect(fresh.workspaceId == "w2")
        #expect(fresh.status == "idle"); #expect(fresh.graphRuns == nil)
    }

    @Test func creationTimeBreaksTiesAndInvalidTimestampsSortLast() {
        let sessions = [
            session("first", createdAt: "2026-09-17T08:00:00Z", model: "a"),
            session("second", createdAt: "2026-09-17T08:30:00Z", model: "b"),
            session("broken", createdAt: "bad", model: "c"),
        ]
        #expect(RunSession.template(kind: "claude", provider: "claude", in: sessions)?.id == "second")
        #expect(session("broken", createdAt: "bad").lastUsedAt == .distantPast)
        #expect(RunSession.template(kind: "claude", provider: "claude", in: []) == nil)
    }
}
