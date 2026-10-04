import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// Background Ouroboros executions in the Mighty graph: which tool results
/// name one, what the loopback dashboard's payloads become, when a block is
/// read again, and where its block sits.
@Suite struct OuroborosExecutionTests {
    static let claudeStart = "mcp__plugin_ouroboros_ouroboros__ouroboros_start_execute_seed"
    static let startText = """
    Started background execution.

    Job ID: job_6bb930bd8b98
    Session ID: orch_1234
    Execution ID: exec_abc123

    Runtime Backend: claude
    Live Dashboard: http://localhost:8123/?run=exec_abc123

    Use ouroboros_ac_tree_hud(session_id, cursor) for live progress.
    """

    private func entry(_ id: String, tool: String, output: String?, state: String = "completed",
                       timestamp: String = "2026-09-29T00:00:00Z") -> LogEntry {
        LogEntry(id: id, kind: "system", text: tool, timestamp: timestamp, provider: "claude",
                 activity: AgentActivity(id: "activity-" + id, provider: "claude", kind: "tool", state: state, toolName: tool, summary: tool, output: output))
    }

    // MARK: Links

    @Test func aStartToolResultNamesItsJobSessionExecutionAndLoopbackDashboard() throws {
        let run = MightyGraphRun(id: "run-1", rootEntries: [entry("a", tool: Self.claudeStart, output: Self.startText)])
        let link = try #require(OuroborosExecutionLinks.extract(from: [run]).first)
        #expect(link.runID == "run-1")
        #expect(link.tool == "ouroboros_start_execute_seed")
        #expect(link.jobID == "job_6bb930bd8b98")
        #expect(link.sessionID == "orch_1234")
        #expect(link.executionID == "exec_abc123")
        #expect(link.dashboardURL?.absoluteString == "http://localhost:8123/?run=exec_abc123")
        #expect(link.key == "exec_abc123")
    }

    @Test func onlyTheFourStartToolsAndOnlyTheirSettledResultsCount() {
        let status = entry("s", tool: "mcp__plugin_ouroboros_ouroboros__ouroboros_job_status", output: "**Execution ID**: exec_other\nExecution ID: exec_other")
        let failed = entry("f", tool: Self.claudeStart, output: Self.startText, state: "error")
        let running = entry("r", tool: Self.claudeStart, output: nil, state: "running")
        let lookalike = entry("l", tool: "mcp__x__not_ouroboros_start_execute_seed", output: Self.startText)
        let run = MightyGraphRun(id: "run-1", rootEntries: [status, failed, running, lookalike])
        #expect(OuroborosExecutionLinks.extract(from: [run]).isEmpty)
    }

    @Test func codexNamesSubagentStartsAndJobOnlyStartsAreFoundOncePerExecution() {
        let auto = entry("auto", tool: "ouroboros.ouroboros_start_auto",
                         output: "Started background auto session.\n\nStatus: queued\njob_id: job_auto1\nauto_session_id: auto_9\nLive Dashboard: http://127.0.0.1:9000\n")
        let agent = MightyGraphAgent(id: "child", entries: [entry("b", tool: Self.claudeStart, output: Self.startText)])
        let first = MightyGraphRun(id: "run-1", rootEntries: [auto], agents: [agent])
        // An idempotent replay in a later request names the same execution.
        let second = MightyGraphRun(id: "run-2", rootEntries: [entry("c", tool: Self.claudeStart, output: Self.startText)])
        let links = OuroborosExecutionLinks.extract(from: [first, second])
        #expect(links.map(\.key) == ["job-job_auto1", "exec_abc123"])
        #expect(links.allSatisfy { $0.runID == "run-1" })
        #expect(links[0].executionID == nil)
        #expect(links[0].sessionID == "auto_9")
        #expect(links[0].dashboardURL?.absoluteString == "http://127.0.0.1:9000")
    }

    @Test func aPendingExecutionIsTakenFromALoopbackRunURLOnly() {
        let pending = "Job ID: job_1\nExecution ID: pending\nLive Dashboard: http://localhost:8123/?run=exec_from_url\n"
        #expect(OuroborosExecutionLinks.parse(result: pending, tool: "t", runID: "r")?.executionID == "exec_from_url")
        let remote = "Job ID: job_1\nExecution ID: pending\nLive Dashboard: http://evil.example:8123/?run=exec_from_url\n"
        let link = OuroborosExecutionLinks.parse(result: remote, tool: "t", runID: "r")
        #expect(link?.executionID == nil)
        #expect(link?.dashboardURL == nil)
        #expect(link?.key == "job-job_1")
    }

    @Test func idsThatCouldBreakOutOfAURLOrAnArgumentAreRejected() {
        for bad in ["-rf", "../etc", "exec/1", "exec 1", "exec?x=1", "exec;rm", "", String(repeating: "a", count: 201), "exec%2F", "pending.1",
                    ":evolve", "evolve:a/b", "exec#1", "exec&run=x"] {
            #expect(!OuroborosExecutionLinks.validID(bad), "\(bad)")
        }
        for good in ["exec_abc123", "job_6bb930bd8b98", "orch-1", "A", "evolve:lin_seed_x:generation:3", String(repeating: "a", count: 200)] {
            #expect(OuroborosExecutionLinks.validID(good), "\(good)")
        }
        #expect(OuroborosExecutionLinks.parse(result: "Job ID: ../x\nExecution ID: -v\n", tool: "t", runID: "r") == nil)
        #expect(OuroborosDashboardClient.ensureArguments(executionID: "--serve-daemon") == nil)
        #expect(OuroborosDashboardClient.ensureArguments(executionID: "exec_1") == ["-I", "-m", "ouroboros.dashboard_web", "--run", "exec_1"])
        #expect(OuroborosDashboardClient.ensureArguments(executionID: nil) == ["-I", "-m", "ouroboros.dashboard_web"])
        // An evolve id is one argument after `--run`, and a percent-encoded query item.
        #expect(OuroborosDashboardClient.ensureArguments(executionID: "evolve:lin:generation:2")?.suffix(2) == ["--run", "evolve:lin:generation:2"])
        let url = OuroborosDashboard.eventsURL(OuroborosDashboardEndpoint(host: "127.0.0.1", port: 9), executionID: "evolve:lin:generation:2")
        #expect(URLComponents(url: url!, resolvingAgainstBaseURL: false)?.queryItems == [URLQueryItem(name: "run", value: "evolve:lin:generation:2")])
        #expect(url?.path == "/events")
    }

    /// `ouroboros_start_evolve_step`'s result as Ouroboros writes it (the
    /// observer hand-off abridged): `None` while the generation is claimed
    /// later, `evolve:<lineage>:generation:<n>` when it was planned.
    static func evolveText(_ execution: String) -> String {
        """
        Started background evolve_step.

        Job ID: job_7f3a2b1c9d0e
        Lineage ID: lin_seed_mac_resources_regression
        Execution ID: \(execution)

        Use ouroboros_job_status, ouroboros_job_wait, or ouroboros_job_result to monitor it.

        <!-- ouroboros-job-observer-v1 base64
        eyJqb2Jfb2JzZXJ2ZXIiOnt9fQ==
        -->
        """
    }

    @Test func anEvolveStepWithoutAnExecutionIsItsJobAndAPlannedGenerationIsItsOwnBlock() {
        let evolve = "mcp__plugin_ouroboros_ouroboros__ouroboros_start_evolve_step"
        for placeholder in ["None", "none", "NULL", "Pending"] {
            let link = OuroborosExecutionLinks.parse(result: Self.evolveText(placeholder), tool: "ouroboros_start_evolve_step", runID: "r")
            #expect(link?.executionID == nil, "\(placeholder)")
            #expect(link?.key == "job-job_7f3a2b1c9d0e")
        }
        // Two steps of one lineage: two blocks, never one "None" block.
        let first = MightyGraphRun(id: "run-1", rootEntries: [entry("a", tool: evolve, output: Self.evolveText("None").replacingOccurrences(of: "job_7f3a2b1c9d0e", with: "job_1"))])
        let second = MightyGraphRun(id: "run-2", rootEntries: [entry("b", tool: evolve, output: Self.evolveText("evolve:lin_seed_mac_resources_regression:generation:2"))])
        let links = OuroborosExecutionLinks.extract(from: [first, second])
        #expect(links.map(\.key) == ["job-job_1", "evolve:lin_seed_mac_resources_regression:generation:2"])
        #expect(links[1].executionID == "evolve:lin_seed_mac_resources_regression:generation:2")
        #expect(links[1].dashboardURL == nil)
        // A dashboard run parameter that is only a placeholder names nothing either.
        #expect(OuroborosExecutionLinks.parse(result: "Job ID: job_1\nLive Dashboard: http://localhost:1/?run=None\n", tool: "t", runID: "r")?.executionID == nil)
    }

    // MARK: Loopback

    @Test func onlyPlainHTTPOnALoopbackHostWithAPortIsEverAnEndpoint() {
        func endpoint(_ text: String) -> OuroborosDashboardEndpoint? { URL(string: text).flatMap(OuroborosDashboard.endpoint(dashboardURL:)) }
        #expect(endpoint("http://localhost:8123/?run=x") == OuroborosDashboardEndpoint(host: "localhost", port: 8123))
        #expect(endpoint("http://127.0.0.1:1") != nil)
        #expect(endpoint("http://[::1]:8123/")?.host == "::1")
        for bad in ["https://localhost:8123", "http://localhost", "http://example.com:8123", "http://127.0.0.2:8123",
                    "http://user:pw@localhost:8123", "file:///tmp/x", "http://0.0.0.0:8123", "http://localhost.evil.com:80"] {
            #expect(endpoint(bad) == nil, "\(bad)")
        }
        func state(_ json: String) -> OuroborosDashboardEndpoint? { OuroborosDashboard.endpoint(stateJSON: Data(json.utf8)) }
        #expect(state(#"{"host":"127.0.0.1","port":5000,"pid":1,"db_path":"/x"}"#) == OuroborosDashboardEndpoint(host: "127.0.0.1", port: 5000))
        #expect(state(#"{"host":"0.0.0.0","port":5000}"#)?.host == "127.0.0.1")
        #expect(state(#"{"port":5000}"#)?.host == "127.0.0.1")
        #expect(state(#"{"host":"10.0.0.5","port":5000}"#) == nil)
        #expect(state(#"{"host":"127.0.0.1","port":"5000"}"#) == nil)
        #expect(state(#"{"host":"127.0.0.1","port":70000}"#) == nil)
        #expect(state("not json") == nil)
        let v6 = OuroborosDashboardEndpoint(host: "::1", port: 9)
        #expect(OuroborosDashboard.eventsURL(v6, executionID: "exec_1")?.absoluteString == "http://[::1]:9/events?run=exec_1")
        #expect(OuroborosDashboard.eventsURL(v6, executionID: "a/b") == nil)
        #expect(OuroborosDashboard.pageURL(v6, executionID: nil)?.absoluteString == "http://[::1]:9/")
    }

    // MARK: Payloads

    @Test func runSummariesAreReadTolerantly() throws {
        let body = #"""
        {"runs":[
          {"execution_id":"exec_1","session_id":"orch_1","goal":"Build it","status":"running","completed_count":2,"total_count":5,
           "pending_count":1,"executing_count":2,"failed_count":0,"phase":"Execute","activity":"AC 3","extra":{"x":1}},
          {"execution_id":"exec_2","status":"exploded","completed_count":true,"total_count":-3},
          {"execution_id":"../bad","status":"completed"},
          {"status":"completed"},
          "noise"
        ]}
        """#
        let runs = try #require(OuroborosDashboard.summaries(Data(body.utf8)))
        #expect(runs.count == 2)
        let first = try #require(runs["exec_1"])
        #expect(first.status == .running && first.goal == "Build it" && first.phase == "Execute" && first.activity == "AC 3")
        #expect(first.completed == 2 && first.total == 5 && first.pending == 1 && first.executing == 2 && first.failed == 0)
        let second = try #require(runs["exec_2"])
        #expect(second.status == .unknown)
        #expect(second.completed == nil && second.total == nil && second.goal == nil)
        #expect(OuroborosDashboard.summaries(Data(#"{"error":"x"}"#.utf8)) == nil)
        #expect(OuroborosDashboard.summaries(Data("<html>".utf8)) == nil)
        #expect(OuroborosExecutionStatus(wire: "CANCELLED") == .cancelled)
        #expect(OuroborosExecutionStatus(wire: nil) == .unknown)
        #expect(!OuroborosExecutionStatus.paused.isTerminal && OuroborosExecutionStatus.failed.isTerminal)
    }

    @Test func sseKeepsOnlyCompleteDataEvents() {
        let text = "data: {\"a\":1}\n\n: keep-alive\n\nevent: x\r\ndata: one\r\ndata: two\r\n\r\ndata: partial"
        #expect(OuroborosDashboard.sseData(text) == ["{\"a\":1}", "one\ntwo"])
        #expect(OuroborosDashboard.sseData("").isEmpty)
    }

    @Test func aBoardBecomesAnOrderedACListWithUnknownColumnsKeptAsExecuting() throws {
        let body = #"""
        {"meta":{"goal":"G","phase":"P","activity":"A","completed":1,"total":4},
         "columns":{
          "completed":[{"id":"ac_1","title":"First","status":"completed","ac_index":1}],
          "pending":[{"id":"ac_3","status":"pending","ac_index":3}],
          "executing":[{"id":"ac_2","title":"Second","ac_index":2},{"id":"ac_2_1","title":"Sub","ac_index":2,"depth":1}],
          "failed":[{"title":"no id"}],
          "mystery":[{"id":"ac_4","title":"Fourth","ac_index":4}]},
         "providers":[]}
        """#
        let board = try #require(OuroborosDashboard.board(Data(body.utf8)))
        #expect(board.items.map(\.id) == ["ac_1", "ac_2", "ac_2_1", "ac_3", "ac_4"])
        #expect(board.items.map(\.status) == [.completed, .executing, .executing, .pending, .executing])
        #expect(board.items[3].title == "ac_3")
        #expect(board.items[2].depth == 1)
        #expect(board.goal == "G" && board.completed == 1 && board.total == 4)
        #expect(OuroborosDashboard.board(Data(#"{"meta":{}}"#.utf8)) == nil)
    }

    @Test func theSummaryOwnsStatusAndCountsAndTheBoardFillsTheRest() {
        let board = OuroborosBoard(goal: "board goal", phase: "p", activity: nil, completed: nil, total: nil,
                                   items: [OuroborosACItem(id: "1", title: "a", status: .completed), OuroborosACItem(id: "2", title: "b", status: .failed)])
        let summary = OuroborosRunSummary(executionID: "exec_1", status: .running, goal: nil, phase: nil, activity: "act",
                                          completed: 5, total: 9, pending: 1, executing: 3, failed: 0)
        let both = OuroborosExecutionSnapshot.merge(summary: summary, board: board, previousItems: nil, dashboardURL: nil)
        #expect(both.status == .running && both.completed == 5 && both.total == 9 && both.failed == 0)
        #expect(both.goal == "board goal" && both.phase == "p" && both.activity == "act")
        #expect(both.items?.count == 2)
        let boardOnly = OuroborosExecutionSnapshot.merge(summary: nil, board: board, previousItems: nil, dashboardURL: nil)
        #expect(boardOnly.completed == 1 && boardOnly.total == 2 && boardOnly.failed == 1 && boardOnly.pending == 0)
        let kept = [OuroborosACItem(id: "x", title: "x", status: .pending)]
        #expect(OuroborosExecutionSnapshot.merge(summary: summary, board: nil, previousItems: kept, dashboardURL: nil).items == kept)
    }

    @Test func aRunTheRunListDroppedTakesASettledStatusFromItsBoard() {
        func board(_ statuses: [OuroborosACItem.Status], total: Int? = nil) -> OuroborosBoard {
            OuroborosBoard(goal: nil, phase: nil, activity: nil, completed: nil, total: total,
                           items: statuses.enumerated().map { OuroborosACItem(id: "ac_\($0.offset)", title: "t", status: $0.element) })
        }
        func status(_ board: OuroborosBoard) -> OuroborosExecutionStatus {
            OuroborosExecutionSnapshot.merge(summary: nil, board: board, previousItems: nil, dashboardURL: nil).status
        }
        #expect(status(board([.completed, .completed])) == .completed)
        #expect(status(board([.completed, .failed])) == .failed)
        // Work still in flight, nothing settled, or ACs the board has not got yet: not known.
        #expect(status(board([.completed, .executing])) == .unknown)
        #expect(status(board([.completed, .pending])) == .unknown)
        #expect(status(board([])) == .unknown)
        #expect(status(board([.completed], total: 3)) == .unknown)
        // The summary, when there is one, still owns the status.
        let running = OuroborosRunSummary(executionID: "e", status: .running)
        #expect(OuroborosExecutionSnapshot.merge(summary: running, board: board([.completed]), previousItems: nil, dashboardURL: nil).status == .running)
        // A settled board ends polling like a terminal summary does.
        var poll = OuroborosExecutionPoll(now: Date(), stale: false)
        poll.record(OuroborosExecutionSnapshot.merge(summary: nil, board: board([.completed]), previousItems: nil, dashboardURL: nil), now: Date())
        #expect(poll.stopped)
    }

    // MARK: Polling

    @Test func pollingIsFastWhileThingsChangeSlowWhenQuietAndStopsWhenTerminal() {
        let start = Date(timeIntervalSince1970: 1_000)
        var poll = OuroborosExecutionPoll(now: start, stale: false)
        #expect(poll.isDue(start))
        var snapshot = OuroborosExecutionSnapshot(source: .live, status: .running, completed: 1, total: 3)
        poll.record(snapshot, now: start)
        #expect(poll.nextAt == start.addingTimeInterval(OuroborosExecutionPoll.fastInterval))
        // No change for a minute: back off.
        let quiet = start.addingTimeInterval(OuroborosExecutionPoll.quietAfter)
        poll.record(snapshot, now: quiet)
        #expect(poll.nextAt == quiet.addingTimeInterval(OuroborosExecutionPoll.slowInterval))
        // A change speeds it up again.
        snapshot.completed = 2
        poll.record(snapshot, now: quiet.addingTimeInterval(15))
        #expect(poll.nextAt == quiet.addingTimeInterval(15 + OuroborosExecutionPoll.fastInterval))
        snapshot.status = .completed
        poll.record(snapshot, now: quiet.addingTimeInterval(20))
        #expect(poll.stopped && !poll.isDue(quiet.addingTimeInterval(1_000)))
        // An AC list opened afterwards reads once more, then stops again.
        poll.wake(quiet.addingTimeInterval(30))
        #expect(poll.isDue(quiet.addingTimeInterval(30)))
        poll.record(snapshot, now: quiet.addingTimeInterval(31))
        #expect(poll.stopped)
    }

    @Test func jobOnlyStartsAndUnansweredHistoryAreReadOnce() {
        let now = Date()
        var job = OuroborosExecutionPoll(now: now, stale: false)
        job.record(OuroborosExecutionSnapshot(source: .noExecution), now: now)
        #expect(job.stopped)
        var history = OuroborosExecutionPoll(now: now, stale: true)
        #expect(!history.mayEnsureDaemon)
        history.record(OuroborosExecutionSnapshot(source: .unreachable), now: now)
        #expect(history.stopped)
        // A live but unreachable execution keeps being read.
        var live = OuroborosExecutionPoll(now: now, stale: false)
        live.record(OuroborosExecutionSnapshot(source: .unreachable), now: now)
        #expect(!live.stopped)
        #expect(OuroborosExecutionPoll.isStale(timestamp: "2026-09-29T00:00:00Z", now: ISO8601DateFormatter().date(from: "2026-09-29T11:00:00Z")!) == false)
        #expect(OuroborosExecutionPoll.isStale(timestamp: "2026-09-29T00:00:00.123Z", now: ISO8601DateFormatter().date(from: "2026-09-30T00:00:00Z")!))
        #expect(OuroborosExecutionPoll.isStale(timestamp: "garbage", now: now))
    }

    @Test func readsThatNeverTellTheStatusBackOffToMinutesAndThenStop() {
        let start = Date(timeIntervalSince1970: 0)
        var poll = OuroborosExecutionPoll(now: start, stale: false)
        let unknown = OuroborosExecutionSnapshot(source: .live)
        var at = start
        for read in 1...OuroborosExecutionPoll.unknownStopAfter {
            poll.record(unknown, now: at)
            if read < OuroborosExecutionPoll.unknownBackoffAfter {
                #expect(poll.nextAt.map { $0.timeIntervalSince(at) }! <= OuroborosExecutionPoll.slowInterval)
            } else if read < OuroborosExecutionPoll.unknownStopAfter {
                #expect(poll.nextAt == at.addingTimeInterval(OuroborosExecutionPoll.unknownInterval))
            }
            at = poll.nextAt ?? at
        }
        #expect(poll.stopped)
        // A read that tells the status starts the count again.
        var known = OuroborosExecutionPoll(now: start, stale: false)
        for _ in 1..<OuroborosExecutionPoll.unknownBackoffAfter { known.record(unknown, now: start) }
        known.record(OuroborosExecutionSnapshot(source: .live, status: .running), now: start)
        #expect(known.unknownReads == 0)
        known.record(unknown, now: start)
        #expect(known.nextAt == start.addingTimeInterval(OuroborosExecutionPoll.fastInterval))
        // So does an answer that cannot be read: it is not a status either.
        var unreadable = OuroborosExecutionPoll(now: start, stale: false)
        for _ in 1...OuroborosExecutionPoll.unknownStopAfter { unreadable.record(OuroborosExecutionSnapshot(source: .unreadable), now: start) }
        #expect(unreadable.stopped)
    }

    @Test func theDaemonIsStartedAtMostEveryFewMinutesAndAFewTimesInAllPerExecutionProcessWide() async {
        let start = Date(timeIntervalSince1970: 0)
        let starts = OuroborosDaemonStarts()
        #expect(await starts.claim("exec_1", now: start))
        #expect(!(await starts.claim("exec_1", now: start.addingTimeInterval(OuroborosDaemonStarts.cooldown - 1))))
        // Keyed by execution: another one is not held up by the first.
        #expect(await starts.claim("exec_2", now: start))
        var at = start
        for _ in 1..<OuroborosDaemonStarts.maximum {
            at = at.addingTimeInterval(OuroborosDaemonStarts.cooldown)
            #expect(await starts.claim("exec_1", now: at))
        }
        #expect(!(await starts.claim("exec_1", now: at.addingTimeInterval(OuroborosDaemonStarts.cooldown * 10))))
        // Whether a block may ask at all: live and still read, never history or a finished run.
        let poll = OuroborosExecutionPoll(now: start, stale: false)
        #expect(poll.mayEnsureDaemon)
        #expect(!OuroborosExecutionPoll(now: start, stale: true).mayEnsureDaemon)
        var finished = OuroborosExecutionPoll(now: start, stale: false)
        finished.record(OuroborosExecutionSnapshot(source: .live, status: .failed), now: start)
        #expect(!finished.mayEnsureDaemon)
    }

    // MARK: Client

    private final class Recorder: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [String] = []
        func add(_ value: String) { lock.lock(); values.append(value); lock.unlock() }
        var all: [String] { lock.lock(); defer { lock.unlock() }; return values }
    }

    private struct FakeTransport: OuroborosDashboardTransport {
        let recorder: Recorder
        let runs: Data?
        let events: Data?
        var status = 200
        func get(_ url: URL, timeout: TimeInterval) async -> (status: Int, body: Data)? {
            recorder.add("GET " + url.absoluteString)
            return runs.map { (status, $0) }
        }
        func readEvents(_ url: URL, firstEvent: TimeInterval, quiet: TimeInterval, deadline: TimeInterval, maximumBytes: Int) async -> Data? {
            recorder.add("SSE " + url.absoluteString)
            return events
        }
    }

    private func temporaryHome() throws -> URL {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("ouroboros-home-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: home.appendingPathComponent(".ouroboros"), withIntermediateDirectories: true)
        return home
    }

    @Test func theClientReadsTheDaemonsOwnRecordAndOnlyGETsTheTwoEndpoints() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        try Data(#"{"host":"127.0.0.1","port":4321,"pid":7,"db_path":"/db"}"#.utf8).write(to: home.appendingPathComponent(".ouroboros/dashboard.json"))
        let recorder = Recorder()
        let frame = #"{"meta":{"total":1},"columns":{"completed":[{"id":"ac_1","title":"One"}]}}"#
        let client = OuroborosDashboardClient(transport: FakeTransport(recorder: recorder, runs: Data(#"{"runs":[{"execution_id":"exec_1","status":"completed"}]}"#.utf8),
                                                                       events: Data("data: {}\n\ndata: \(frame)\n\n".utf8)),
                                              home: home, launcher: { _ in Issue.record("nothing to start"); return false })
        // The record wins over a hint from tool output.
        let endpoint = try #require(await client.endpoint(hint: URL(string: "http://localhost:9999/")))
        #expect(endpoint == OuroborosDashboardEndpoint(host: "127.0.0.1", port: 4321))
        #expect(await client.summaries(endpoint).summaries?["exec_1"]?.status == .completed)
        #expect(await client.board(endpoint, executionID: "exec_1")?.items.map(\.title) == ["One"])
        #expect(await client.board(endpoint, executionID: "exec/1") == nil)
        #expect(recorder.all == ["GET http://127.0.0.1:4321/api/runs", "SSE http://127.0.0.1:4321/events?run=exec_1"])
    }

    private final class Launches: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [OuroborosDashboardClient.Launch] = []
        func add(_ value: OuroborosDashboardClient.Launch) { lock.lock(); values.append(value); lock.unlock() }
        var all: [OuroborosDashboardClient.Launch] { lock.lock(); defer { lock.unlock() }; return values }
    }

    @Test func withoutARecordOnlyALoopbackHintIsUsedAndTheDaemonIsEnsuredWithoutAShell() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let recorder = Recorder()
        let launches = Launches()
        let client = OuroborosDashboardClient(transport: FakeTransport(recorder: recorder, runs: nil, events: nil), home: home,
                                              launcher: { launch in launches.add(launch); return true })
        #expect(await client.endpoint(hint: nil) == nil)
        #expect(await client.endpoint(hint: URL(string: "http://example.com:80/")) == nil)
        #expect(await client.endpoint(hint: URL(string: "http://localhost:8123/?run=x")) == OuroborosDashboardEndpoint(host: "localhost", port: 8123))
        #expect(await client.summaries(OuroborosDashboardEndpoint(host: "localhost", port: 8123)) == .noAnswer)
        // No tool python installed: nothing is launched.
        #expect(await client.ensureDaemon(executionID: "exec_1") == false)
        let python = home.appendingPathComponent(".local/share/uv/tools/ouroboros-ai/bin/python")
        try FileManager.default.createDirectory(at: python.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\n".utf8).write(to: python)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: python.path)
        #expect(await client.ensureDaemon(executionID: "exec_1"))
        #expect(await client.ensureDaemon(executionID: "-x") == false)
        let launch = try #require(launches.all.first)
        #expect(launches.all.count == 1)
        #expect(launch.executable == python)
        // Isolated python, from a directory an agent cannot write, with only the environment it needs.
        #expect(launch.arguments == ["-I", "-m", "ouroboros.dashboard_web", "--run", "exec_1"])
        #expect(launch.cwd.path == "/")
        #expect(launch.environment["HOME"] == home.path)
        #expect(launch.environment["PATH"] == "/usr/bin:/bin:/usr/sbin:/sbin")
        #expect(launch.environment["PYTHONSAFEPATH"] == "1" && launch.environment["PYTHONNOUSERSITE"] == "1")
        #expect(Set(launch.environment.keys).isSubset(of: ["HOME", "PATH", "LANG", "LC_ALL", "TMPDIR", "PYTHONSAFEPATH", "PYTHONNOUSERSITE"]))
        let inherited = ["PYTHONPATH": "/tmp/evil", "PYTHONSTARTUP": "/tmp/x.py", "LANG": "ko_KR.UTF-8", "TMPDIR": "/var/tmp/", "OUROBOROS_DASHBOARD": "1",
                         "HOME": "/elsewhere", "PATH": "/tmp/bin:/usr/bin", "DYLD_INSERT_LIBRARIES": "/tmp/x.dylib"]
        #expect(OuroborosDashboardClient.daemonEnvironment(home: home, inherited: inherited)
                == ["HOME": home.path, "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "LANG": "ko_KR.UTF-8", "TMPDIR": "/var/tmp/",
                    "PYTHONSAFEPATH": "1", "PYTHONNOUSERSITE": "1"])
        #expect(OuroborosDashboardClient.daemonEnvironment(home: home, inherited: [:])["LANG"] == "en_US.UTF-8")
    }

    @Test func aDashboardThatAnswersWithoutRunsIsUpButUnreadable() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let endpoint = OuroborosDashboardEndpoint(host: "127.0.0.1", port: 4321)
        func answer(_ status: Int, _ body: String) async -> OuroborosRunsAnswer {
            let client = OuroborosDashboardClient(transport: FakeTransport(recorder: Recorder(), runs: Data(body.utf8), events: nil, status: status),
                                                  home: home, launcher: { _ in Issue.record("nothing to start"); return false })
            return await client.summaries(endpoint)
        }
        #expect(await answer(503, #"{"runs":[],"error":"picker_index_contract_unavailable"}"#) == .unreadable(status: 503))
        #expect(await answer(200, "<html>") == .unreadable(status: 200))
        #expect(await answer(200, #"{"runs":[]}"#) == .runs([:]))
        #expect(OuroborosRunsAnswer.unreadable(status: 503).summaries == nil)
    }

    /// Counts how many event streams are open at once.
    private final class SlowEvents: OuroborosDashboardTransport, @unchecked Sendable {
        private let lock = NSLock()
        private var open = 0
        private(set) var mostOpen = 0
        func get(_ url: URL, timeout: TimeInterval) async -> (status: Int, body: Data)? { nil }
        func readEvents(_ url: URL, firstEvent: TimeInterval, quiet: TimeInterval, deadline: TimeInterval, maximumBytes: Int) async -> Data? {
            #expect(firstEvent == OuroborosDashboardClient.firstEventDeadline && firstEvent < deadline)
            lock.withLock { open += 1; mostOpen = max(mostOpen, open) }
            try? await Task.sleep(nanoseconds: 80_000_000)
            lock.withLock { open -= 1 }
            let run = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first?.value ?? ""
            return Data("data: {\"meta\":{},\"columns\":{\"completed\":[{\"id\":\"\(run)\"}]}}\n\n".utf8)
        }
    }

    @Test func boardsAreReadConcurrentlyButBounded() async throws {
        let transport = SlowEvents()
        let client = OuroborosDashboardClient(transport: transport, home: try temporaryHome(), launcher: { _ in false })
        let ids = (0..<7).map { "exec_\($0)" }
        let boards = await client.boards(OuroborosDashboardEndpoint(host: "127.0.0.1", port: 1), executionIDs: ids + ["exec_0"])
        #expect(Set(boards.keys) == Set(ids))
        #expect(boards["exec_3"]?.items.map(\.id) == ["exec_3"])
        #expect(transport.mostOpen > 1 && transport.mostOpen <= OuroborosDashboardClient.boardConcurrency)
    }

    /// The real loopback transport with every wait stretched: the test below is
    /// about what it reads, and a loaded CI runner can hold the fake dashboard's
    /// answer past the product's seconds. The waits themselves have their own
    /// tests (the first-event deadline, a cancelled read).
    private struct PatientLoopbackTransport: OuroborosDashboardTransport {
        let base = LoopbackDashboardTransport()
        func get(_ url: URL, timeout: TimeInterval) async -> (status: Int, body: Data)? {
            await base.get(url, timeout: max(timeout, 60))
        }
        func readEvents(_ url: URL, firstEvent: TimeInterval, quiet: TimeInterval, deadline: TimeInterval, maximumBytes: Int) async -> Data? {
            await base.readEvents(url, firstEvent: max(firstEvent, 60), quiet: max(quiet, 60), deadline: max(deadline, 61), maximumBytes: maximumBytes)
        }
    }

    @Test func theLoopbackTransportReadsJSONAndAClosedEventStreamFromAFakeDashboard() async throws {
        let frame = #"{"meta":{},"columns":{"executing":[{"id":"ac_1","title":"Doing"}]}}"#
        let server = HTTPServer(address: "127.0.0.1", port: 0) { request in
            if request.target.hasPrefix("/api/runs") { return .json(200, ["runs": [["execution_id": "exec_1", "status": "running"]]]) }
            if request.target == "/events?run=exec_1" {
                return HTTPResponse(status: 200, body: Data(": hello\n\ndata: \(frame)\n\n".utf8))
            }
            return .json(404, [:])
        }
        do {
            let port = try await server.start()
            let client = OuroborosDashboardClient(transport: PatientLoopbackTransport(), home: try temporaryHome(), launcher: { _ in false })
            let endpoint = OuroborosDashboardEndpoint(host: "127.0.0.1", port: Int(port))
            #expect(await client.summaries(endpoint).summaries?["exec_1"]?.status == .running)
            #expect(await client.board(endpoint, executionID: "exec_1")?.items.map(\.title) == ["Doing"])
            // Not an event stream it asked for: nothing.
            #expect(await client.board(endpoint, executionID: "exec_2") == nil)
        } catch { await server.stop(); throw error }
        await server.stop()
        // Never anything but loopback, before any connection is made.
        #expect(await LoopbackDashboardTransport().get(URL(string: "http://example.com:80/api/runs")!, timeout: 1) == nil)
    }

    @Test func anEventStreamThatSendsNothingIsLeftAfterTheFirstEventDeadlineNotTheWholeOne() async throws {
        let server = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            return HTTPResponse(status: 200, body: Data())
        }
        let port = try await server.start()
        let started = Date()
        let data = await LoopbackDashboardTransport().readEvents(URL(string: "http://127.0.0.1:\(port)/events?run=exec_1")!,
                                                                 firstEvent: 0.5, quiet: 1.2, deadline: 8, maximumBytes: 1024)
        let elapsed = Date().timeIntervalSince(started)
        await server.stop()
        #expect(data == nil)
        #expect(elapsed < 4, "\(elapsed)")
    }

    @Test func aCancelledReadEndsAtOnceWhateverPointItReached() async throws {
        let server = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            return HTTPResponse(status: 200, body: Data("data: {}\n\n".utf8))
        }
        let port = try await server.start()
        let url = URL(string: "http://127.0.0.1:\(port)/events?run=exec_1")!
        // Cancelled before, while and just after the session and its task are made.
        for delay: UInt64 in [0, 1_000, 100_000, 2_000_000, 20_000_000] {
            let read = Task { await LoopbackDashboardTransport().readEvents(url, firstEvent: 2, quiet: 1.2, deadline: 8, maximumBytes: 1024) }
            if delay > 0 { try? await Task.sleep(nanoseconds: delay) }
            read.cancel()
            #expect(await read.value == nil)
        }
        await server.stop()
    }

    // MARK: Layout

    private func run(_ id: String, agents: [MightyGraphAgent] = [], status: String = "completed") -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: status, agents: agents, finalOutput: status == "completed" ? "결과 " + id : nil)
    }

    @Test func anExecutionBlockHangsBesideItsRequestWithoutMovingAnyCard() throws {
        let wide = (0..<5).map { MightyGraphAgent(id: "branch-\($0)", title: "b", input: "x", status: "completed") }
        let runs = [run("one", agents: wide), run("two")]
        let executions = [MightyGraphLayout.Execution(runID: "one", key: "exec_1"), MightyGraphLayout.Execution(runID: "one", key: "job-job_2"),
                          MightyGraphLayout.Execution(runID: "gone", key: "exec_9")]
        let plain = MightyGraphLayout.make(runs: runs, draft: "", running: false, expanded: [])
        let firstID = MightyGraphBlockSize.nodeID(runID: "one", suffix: MightyGraphLayout.executionSuffix + "exec_1")
        let secondID = MightyGraphBlockSize.nodeID(runID: "one", suffix: MightyGraphLayout.executionSuffix + "job-job_2")
        let layout = MightyGraphLayout.make(runs: runs, draft: "", running: false, expanded: [firstID], executions: executions)
        // Attachments only: every flow card and edge is exactly where it was.
        for card in plain.nodes { #expect(layout.nodes.first { $0.id == card.id }?.frame == card.frame) }
        #expect(layout.edges.map(\.id) == plain.edges.map(\.id))
        #expect(layout.originX == plain.originX)
        let request = try #require(layout.nodes.first { $0.content == .request(0) })
        let first = try #require(layout.nodes.first { $0.id == firstID })
        let second = try #require(layout.nodes.first { $0.id == secondID })
        #expect(first.content == .execution(0, "exec_1") && first.isAuxiliary && !first.isResultFiles)
        #expect(first.frame.minY == request.frame.minY)
        #expect(first.frame.height == MightyGraphLayout.executionHeight(expanded: true))
        #expect(second.frame.height == MightyGraphLayout.executionHeight(expanded: false))
        #expect(second.frame.minY > first.frame.maxY)
        #expect(layout.nodes.filter { $0.isAuxiliary }.count == 2)
        // Clear of every card it shares rows with, and inside the canvas.
        for block in [first, second] {
            for card in layout.nodes where card.id != block.id { #expect(!card.frame.intersects(block.frame), "\(card.id)") }
            #expect(block.frame.maxX - layout.originX <= layout.size.width)
            #expect(block.frame.maxY <= layout.size.height)
        }
    }
}
