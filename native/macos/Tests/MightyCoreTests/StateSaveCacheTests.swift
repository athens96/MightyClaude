import Foundation
import Testing
@testable import MightyCore

struct StateSaveCacheTests {
    @Test func saveScheduleKeepsTheDebounceAndTheGapFromTheLastStart() {
        #expect(SaveSchedule.delay(sinceLastStart: nil) == 0.22)
        #expect(SaveSchedule.delay(sinceLastStart: 0) == 1.5)
        #expect(abs(SaveSchedule.delay(sinceLastStart: 1.0) - 0.5) < 1e-9)
        // Past the gap only the debounce is left.
        #expect(SaveSchedule.delay(sinceLastStart: 1.4) == 0.22)
        #expect(SaveSchedule.delay(sinceLastStart: 60) == 0.22)
        // A clock that went backwards or a broken value never shortens the wait.
        #expect(SaveSchedule.delay(sinceLastStart: -5) == 1.5)
        #expect(SaveSchedule.delay(sinceLastStart: .nan) == 0.22)
        #expect(SaveSchedule.delay(sinceLastStart: 0.3, debounce: 0.1, minimumGap: 2) == 1.7)
    }

    @Test func aWaitingSaveIsNotPushedBackPastTheGap() {
        // Inside the gap and freshly asked for: a new change may restart the debounce.
        #expect(!SaveSchedule.keepsPending(sinceLastStart: 0.5, sinceFirstRequest: 0.1))
        #expect(!SaveSchedule.keepsPending(sinceLastStart: nil, sinceFirstRequest: 1.49))
        // The gap since the last save began has passed: the waiting save fires.
        #expect(SaveSchedule.keepsPending(sinceLastStart: 1.5, sinceFirstRequest: 0))
        #expect(SaveSchedule.keepsPending(sinceLastStart: 60, sinceFirstRequest: 0.05))
        // Changes every 0.1 s with nothing saved yet: capped 1.5 s after the first request.
        #expect(SaveSchedule.keepsPending(sinceLastStart: nil, sinceFirstRequest: 1.5))
        #expect(SaveSchedule.keepsPending(sinceLastStart: 0.2, sinceFirstRequest: 3))
        // A clock that went backwards or a broken value never keeps it.
        #expect(!SaveSchedule.keepsPending(sinceLastStart: -5, sinceFirstRequest: -5))
        #expect(!SaveSchedule.keepsPending(sinceLastStart: .nan, sinceFirstRequest: .nan))
        #expect(SaveSchedule.keepsPending(sinceLastStart: 0.3, sinceFirstRequest: 2, minimumGap: 2))
    }

    private static let date = Date(timeIntervalSince1970: 1_790_000_000)

    private func logs(_ prefix: String, count: Int, size: Int) -> [LogEntry] {
        (0..<count).flatMap { index -> [LogEntry] in
            let activity = AgentActivity(id: "\(prefix)-act-\(index)", provider: "claude", kind: "command", state: "completed", toolName: "Bash",
                                         summary: "ls \(index)\nmore", output: String(repeating: "out \(index) ", count: size / 40) + "\u{1B}[0m end", durationMs: 12)
            return [LogEntry(id: "\(prefix)-user-\(index)", kind: "user", text: "Request \(index)", timestamp: "2026-09-21T10:00:0\(index % 10).000Z"),
                    LogEntry(id: activity.id, kind: "system", text: activity.summary, timestamp: "2026-09-21T10:00:0\(index % 10).100Z", provider: "claude", activity: activity),
                    LogEntry(id: "\(prefix)-reply-\(index)", kind: "assistant", text: String(repeating: "r\(index) ", count: size / 3), timestamp: "2026-09-21T10:00:0\(index % 10).500Z")]
        }
    }

    /// A run with a result whose request entries the budget clips gets a result entry
    /// stamped with the current time (`refreshResult`), in a fresh normalize as much
    /// as in a cached one; runs over budget therefore carry no result here.
    private func graph(_ prefix: String, runs: Int, size: Int, result: Bool = true) -> [MightyGraphRun] {
        (0..<runs).map { index in
            let agents = (0..<4).map { MightyGraphAgent(id: "\(prefix)-agent-\(index)-\($0)", input: String(repeating: "p", count: size / 4), status: "completed", entries: [LogEntry(id: "\(prefix)-entry-\(index)-\($0)", kind: "assistant", text: String(repeating: "x", count: size))]) }
            let root = [LogEntry(id: "\(prefix)-root-\(index)", kind: "user", text: "Request \(index)", timestamp: "2026-09-21T10:00:00.000Z")]
            return MightyGraphRun(id: "\(prefix)-run-\(index)", input: "Request \(index)", status: "completed", rootEntries: root, agents: agents, finalOutput: result ? String(repeating: "f", count: size / 2) : nil)
        }
    }

    private func session(_ id: String, status: String = "completed", logCount: Int, logSize: Int, runs: Int = 0, runSize: Int = 0, results: Bool = true) -> RunSession {
        var value = RunSession(id: id, workspaceId: "ws", title: "Claude \(id)", status: status, logs: logs(id, count: logCount, size: logSize), createdAt: "2026-09-21T09:00:00.000Z")
        if runs > 0 { value.graphRuns = graph(id, runs: runs, size: runSize, result: results) }
        value.agentViewMode = "mighty"
        return value
    }

    /// The repository's stored snapshot and the written file both match a fresh,
    /// uncached normalize of the same input at the same time.
    private func expectSavedMatchesFresh(_ repository: StateRepository, _ value: AppSnapshot, file: URL) async throws {
        try await repository.save(value, at: Self.date)
        let expected = StateRepository.normalize(value, restoring: false, at: Self.date)
        let stored = try await repository.load()
        #expect(stored == expected)
        let data = try Data(contentsOf: file)
        #expect(data.count <= StateRepository.maximumStateBytes)
        #expect(try JSONDecoder().decode(AppSnapshot.self, from: data) == expected)
        #expect(StateRepository.decodeSnapshot(data, restoring: true) == StateRepository.decodeSnapshot(try JSONEncoder().encode(expected), restoring: true))
        let permissions = try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? Int
        #expect(permissions == 0o600)
    }

    @Test func cachedSavesMatchAFullNormalizeAfterEveryChange() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-save-cache-\(UUID().uuidString)")
        let folder = root.appendingPathComponent("repo")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
        let repository = StateRepository(directory: root.appendingPathComponent("state"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(id: "ws", name: "Repo", path: folder.path))
        let file = root.appendingPathComponent("state/workspace-state.json")
        // "huge" alone is over both budgets, so its shares move whenever another session grows.
        var value = AppSnapshot(workspaces: [workspace], sessions: [
            session("huge", logCount: 30, logSize: 150_000, runs: 5, runSize: 120_000, results: false),
            session("live", status: "running", logCount: 3, logSize: 2_000, runs: 1, runSize: 1_000),
            session("small", logCount: 4, logSize: 3_000, runs: 1, runSize: 2_000),
            session("other", logCount: 2, logSize: 1_000),
            RunSession(id: "shell", workspaceId: "ws", title: "터미널 2", kind: "shell"),
        ], activeWorkspaceId: "ws", activeSessionId: "live")
        #expect(StateRepository.approximateLogBytes(value.sessions[0].logs) > StateRepository.totalLogBudget)
        #expect(StateRepository.normalize(value, restoring: false, at: Self.date).sessions[2].logs.contains { $0.activity?.output?.isEmpty == false })
        #expect(StateRepository.approximateGraphBytes(value.sessions[0].graphRuns ?? []) > StateRepository.totalGraphBudget)
        try await expectSavedMatchesFresh(repository, value, file: file)
        // Nothing changed: every session but the running one comes from the cache.
        try await expectSavedMatchesFresh(repository, value, file: file)

        // The running session streams.
        value.sessions[1].logs.append(LogEntry(id: "live-more", kind: "assistant", text: "streamed", timestamp: "2026-09-21T10:01:00.000Z"))
        try await expectSavedMatchesFresh(repository, value, file: file)

        // A standing session changes status; the running one finishes.
        value.sessions[2].status = "error"
        value.sessions[1].status = "completed"
        try await expectSavedMatchesFresh(repository, value, file: file)
        #expect(try await repository.load().sessions.first { $0.id == "small" }?.status == "error")

        // A standing session grows, which takes budget from "huge".
        value.sessions[3].logs += logs("other-more", count: 6, size: 60_000)
        value.sessions[3].graphRuns = graph("other-more", runs: 2, size: 60_000)
        try await expectSavedMatchesFresh(repository, value, file: file)

        // A session goes, which gives budget back; an invalid status falls back to idle.
        value.sessions.remove(at: 2)
        value.sessions[0].status = "weird"
        try await expectSavedMatchesFresh(repository, value, file: file)
        #expect(try await repository.load().sessions.map(\.id) == ["huge", "live", "other", "shell"])

        // Cross-session state still follows the current input.
        value.activeSessionId = "other"
        value.sessions.append(session("late", status: "running", logCount: 1, logSize: 500))
        try await expectSavedMatchesFresh(repository, value, file: file)
        #expect(try await repository.load().activeSessionId == "other")
    }

    @Test func splicedSessionsDecodeLikeAFullEncode() throws {
        let workspace = Workspace(id: "ws", name: "Repo \"sessions\":[] {[", path: "/tmp/repo")
        var trap = session("trap", logCount: 2, logSize: 400)
        trap.title = #"x","sessions":[],"y":"#
        let value = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [trap, session("b", logCount: 3, logSize: 900, runs: 1, runSize: 500)],
                                                          paneLayouts: ["ws": PaneLayoutNode(id: "l", kind: "tabs", sessionIds: ["trap", "b"], selectedSessionId: "b")], expandedWorkspaceIds: ["ws"]), restoring: false, at: Self.date)
        let encoder = JSONEncoder()
        let spliced = try StateRepository.encodedSnapshot(value, sessions: value.sessions.map { try encoder.encode($0) })
        let full = try encoder.encode(value)
        #expect(try JSONSerialization.jsonObject(with: spliced) as? NSDictionary == JSONSerialization.jsonObject(with: full) as? NSDictionary)
        #expect(try JSONDecoder().decode(AppSnapshot.self, from: spliced) == value)
        #expect(StateRepository.decodeSnapshot(spliced) == StateRepository.decodeSnapshot(full))
        // No sessions, and a shell with the key only inside strings.
        var empty = value; empty.sessions = []
        #expect(try JSONDecoder().decode(AppSnapshot.self, from: StateRepository.encodedSnapshot(empty, sessions: [])) == empty)
        #expect(StateRepository.emptySessionsClose(in: Data(#"{"a":"\"sessions\":[]","b":{"sessions":[]}}"#.utf8)) == nil)
        #expect(StateRepository.emptySessionsClose(in: Data(#"{"a":"\\","sessions":[]}"#.utf8)) == 22)
        #expect(StateRepository.emptySessionsClose(in: Data(#"{"sessions":[],"sessions":[]}"#.utf8)) == nil)
    }
}
