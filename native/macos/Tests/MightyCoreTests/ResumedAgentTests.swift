import Foundation
import Testing
@testable import MightyCore

/// A background agent Claude resumes with SendMessage is background work of the pane like any other: the pane waits on it,
/// the queue holds, the strip lists it, and the run's input stays open until it ends. Claude Code (2.1.322) lists and starts
/// the resumed run under the agent's first task id with a run id of its own (`background_tasks_changed`, then `task_started`
/// naming the SendMessage call), and ends it as it ends any task (`task_updated`, `task_notification` of that run, an empty
/// `background_tasks_changed`); the turn its notification opens names that run (`origin.runId`).
///
/// The recording is native/contracts/fixtures/claude-stream-resumed-agent.jsonl: Claude Code 2.1.322 with the app's stream-json
/// flags, model haiku. The request launches one background agent, which ends before the request's own result (frames 0–17);
/// the notice turn its end opens resumes it with SendMessage (18–27), and its second end opens the next notice turn (28–35). A
/// second user message on stdin resumes it once more from an ordinary turn (36–52).
struct ResumedAgentTests {
    /// A pane recording the run as the app does: its graph, its background work, and at each point where the runner may close
    /// the run's input (the request's result, a notification's result, `idle`: `ProcessRunner.settleInput`) whether it would.
    private final class Replay {
        let parser: CLIStreamParser
        let pane: GraphAgentIdentityTests.Box<RunSession>
        /// True where the runner would close the input: no background task runs and the CLI is not busy.
        let closes: GraphAgentIdentityTests.Box<[Bool]>
        private let frames: [Data]
        private var next = 0
        init() throws {
            var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
            session.status = "running"
            session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
            let pane = GraphAgentIdentityTests.Box(session), closes = GraphAgentIdentityTests.Box<[Bool]>([])
            let reader = Weak()
            parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                     result: { closes.value.append(reader.closes) },
                                     graph: { pane.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe",
                                     background: { pane.value.recordPlanMode(RunEvent(sessionId: "pane", type: "background", background: $0)) },
                                     settled: { closes.value.append(reader.closes) })
            reader.parser = parser
            self.pane = pane; self.closes = closes
            frames = try GraphAgentIdentityTests.fixture("claude-stream-resumed-agent")
        }
        private final class Weak {
            weak var parser: CLIStreamParser?
            var closes: Bool { parser.map { !$0.backgroundRunning && !$0.cliBusy } ?? false }
        }
        func feed(through index: Int) {
            while next <= index { parser.push(frames[next] + Data([10])); next += 1 }
        }
        var work: BackgroundWork? { pane.value.backgroundWork }
        var runs: [MightyGraphRun] { pane.value.mightyGraphRuns }
    }

    @Test func theRecordingIsTheResumeAsClaudeCodeReportsIt() throws {
        let frames = try GraphAgentIdentityTests.fixture("claude-stream-resumed-agent").map { try #require(JSONSerialization.jsonObject(with: $0) as? [String: Any]) }
        #expect(frames.count == 53)
        // The resumed run: listed and started under the first run's task id, with a run id of its own, naming the SendMessage call.
        #expect(frames[23]["subtype"] as? String == "background_tasks_changed")
        #expect(((frames[23]["tasks"] as? [[String: Any]])?.first).map { [$0["task_id"] as? String, $0["run_id"] as? String] } == ["agent1", "taskrun-2"])
        #expect([frames[24]["subtype"], frames[24]["task_id"], frames[24]["run_id"], frames[24]["tool_use_id"]].map { $0 as? String } == ["task_started", "agent1", "taskrun-2", "toolu_3"])
        #expect(frames[24]["description"] as? String == "Count one to three" && frames[24]["is_backgrounded"] as? Bool == true)
        #expect((frames[25]["tool_use_result"] as? [String: Any])?["resumedAgentId"] as? String == "agent1")
        // Its end, and the notice turn that names the run.
        #expect([frames[29]["subtype"], frames[29]["run_id"]].map { $0 as? String } == ["task_updated", "taskrun-2"])
        #expect([frames[30]["subtype"], frames[30]["run_id"], frames[30]["tool_use_id"]].map { $0 as? String } == ["task_notification", "taskrun-2", "toolu_3"])
        #expect((frames[34]["origin"] as? [String: Any])?["runId"] as? String == "taskrun-2")
        // The resumed agent's own messages name the first launch's call.
        #expect(frames[28]["parent_tool_use_id"] as? String == "toolu_1")
        // `idle` waits for the resumed agent: it comes only after its notice turn, never after the turn that resumed it.
        let idle = frames.indices.filter { frames[$0]["subtype"] as? String == "session_state_changed" && frames[$0]["state"] as? String == "idle" }
        #expect(idle == [35, 52])
    }

    /// The user's case: a notice turn resumes the agent its own notice was about, and Claude ends that turn while the agent works.
    @Test func anAgentResumedFromANoticeTurnKeepsThePaneWaitingUntilItsNotification() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let replay = try Replay()
            // The request's own turn: the agent ended before its result, and its notice turn is still to come, so the CLI
            // is busy and the runner keeps the input open (closed, Claude Code would stop the resumed agent 10 minutes on).
            replay.feed(through: 17)
            var work = try #require(replay.work)
            #expect(work.turnEnded && work.tasks.map(\.status) == ["completed"] && !work.waitingOnBackground)
            #expect(replay.closes.value == [false] && replay.parser.cliBusy)

            // The notice turn resumes it: listed again under a new run, then started by the SendMessage call.
            replay.feed(through: 24)
            work = try #require(replay.work)
            let task = try #require(work.tasks.first)
            #expect(work.tasks.count == 1 && task.id == "agent1" && task.status == "running" && task.toolUseId == "toolu_3")
            #expect(task.endedAt == nil && task.summary == nil && task.description == "Count one to three" && task.kind == "agent")

            // The notice turn's result: the agent still runs, so the input stays open and the pane waits on it.
            replay.feed(through: 27)
            #expect(replay.closes.value == [false, false] && replay.parser.backgroundRunning)
            work = try #require(replay.work)
            #expect(work.waitingOnBackground && PlanCardSupport.backgroundStatus(work) == "턴 완료 · 백그라운드 1개 실행 중")
            // New input waits for it, 바로 실행 takes the row into the process as a new turn, and Stop keeps the queue.
            let runs = replay.runs
            #expect(BackgroundQueuePolicy.waitsOnBackground(work: work, queued: 1))
            #expect(BackgroundQueuePolicy.steersRunNext(work: work, localClaude: true, stopping: false, queued: 1, firstHasAttachments: false))
            #expect(BackgroundQueuePolicy.stopKeepsQueue(work))
            let queue = QueueRunNow.Pane(busy: true, heldForUpdate: false, secondSend: .queue, menuOnly: false, waitsOnBackgroundOnly: work.waitingOnBackground,
                                         held: 0, noticeTurnRunning: MightyGraphSupport.noticeTurnRunning(runs))
            #expect(QueueRunNow.decide(heldIndex: nil, text: "next", attachments: 0, pane: queue) == .steer)
            // The strip lists it as a running sub-agent, opened to the message it was sent; the phone lists it too.
            let request = try #require(MightyGraphSupport.newestRequestRun(runs))
            #expect(PlanCardSupport.showsBackgroundStrip(work, mighty: false, styleDrawsTasks: false))
            #expect(PlanCardSupport.backgroundCounts(work) == "끝남 0개 · 작업 중 1개")
            let rows = PlanCardSupport.backgroundStripRows(work, run: request)
            #expect(rows.count == 1 && rows[0].id == "agent1" && rows[0].run == false && rows[0].task.running)
            #expect(rows[0].command == "Reply with exactly: four five six. Use no tools.")
            #expect(PlanCardSupport.mobileBackgroundWork(work, run: request)?.map(\.kind) == ["agent"])

            // Its block is the request's, as every block a notice turn makes is (a notice run holds only copies): the request
            // awaits its notice, the pet and the header read 알림 대기, the role view draws it as a running card in the request's
            // row, and the log strip's status line counts it.
            let resumed = try #require(request.agents.first { $0.title == "Count four to six" })
            #expect(resumed.background == true && resumed.status == "running" && resumed.sourceBlockId == nil)
            #expect(resumed.id == ExecutionGraphSupport.agentNodeID(runId: "recorded", toolUseId: "toolu_3"))
            #expect(runs.filter { $0.noticeRun == true }.allSatisfy { $0.agents.allSatisfy { $0.sourceBlockId != nil } })
            #expect(BackgroundNoticeRuns.awaitingNotice(request, in: runs)?.tasks.map(\.status) == ["completed", "running"])
            #expect(BackgroundNoticeRuns.awaitsNotice(replay.pane.value))
            let shown = MightyGraphSupport.shownRuns(runs)
            #expect(shown[0].flowAgents.map(\.title) == ["Count one to three", "Count four to six"])
            #expect(shown[0].flowAgents.map(\.status) == ["completed", "running"])
            let status = try #require(FlowLog.status(runs))
            #expect(status.lead.hasSuffix("알림 대기") && status.subagents == "하위 에이전트 1/2 실행 중")

            // Its end: the run's own notification ends it, its copy opens the next notice run, nothing is left to wait on.
            replay.feed(through: 30)
            work = try #require(replay.work)
            #expect(work.tasks.map(\.status) == ["completed"] && work.tasks[0].endedAt != nil && !work.waitingOnBackground && !replay.parser.backgroundRunning)
            #expect(PlanCardSupport.backgroundCounts(work) == "끝남 1개 · 작업 중 0개")
            let ended = replay.runs
            #expect(ended[0].agents.first { $0.id == resumed.id }?.status == "completed")
            #expect(ended.last?.noticeRun == true && ended.last?.agents.map(\.sourceBlockId) == [resumed.id])
            // That notice turn's result, then `idle`: only then may the input close and the process end.
            replay.feed(through: 35)
            #expect(replay.closes.value == [false, false, false, true])
            #expect(replay.runs.last?.finalOutput == "done")
        }
    }

    /// The same agent resumed from an ordinary turn: 바로 실행's steer reopens the turn, so the pane does not read as waiting
    /// on background work until that turn's own result; then it does, until the agent's notification.
    @Test func anAgentResumedFromASteeredTurnWaitsOnceThatTurnEnds() throws {
        let replay = try Replay()
        replay.feed(through: 35)
        // The second message reached the process as a steer does (`ProcessRunner.steer`). The app would have closed this run's
        // input at `idle` (frame 35); here the frames stand for a run kept open by other background work.
        replay.parser.reopenTurn()
        replay.parser.steer(id: "second", text: "Call the SendMessage tool exactly once to continue the same agent again.")
        replay.feed(through: 40)
        var work = try #require(replay.work)
        #expect(work.tasks.map(\.status) == ["running"] && work.tasks[0].toolUseId == "toolu_4")
        #expect(!work.turnEnded && !work.waitingOnBackground && replay.parser.backgroundRunning)
        // The steered turn's result is the request's own kind of result: the turn is over and the agent runs on.
        replay.feed(through: 43)
        work = try #require(replay.work)
        #expect(work.turnEnded && work.waitingOnBackground)
        #expect(replay.closes.value.last == false)
        replay.feed(through: 47)
        work = try #require(replay.work)
        #expect(work.tasks.map(\.status) == ["completed"] && !work.waitingOnBackground)
        replay.feed(through: 52)
        #expect(replay.closes.value.last == true)
    }
}

/// The run's input as `ProcessRunner` keeps it: open after the request's result while the CLI says it is not idle, so a notice
/// turn that comes after a result with nothing running can resume an agent and still take new input; closed once it is idle.
struct ResumedAgentRunnerTests {
    private func wait(_ timeout: Duration = .seconds(10), until condition: () async -> Bool) async throws {
        let clock = ContinuousClock(), deadline = clock.now + timeout
        while clock.now < deadline {
            if await condition() { return }
            try await Task.sleep(for: .milliseconds(20))
        }
        Issue.record("timed out")
    }

    @Test func aNoticeTurnAfterTheResultResumesAnAgentWhileTheInputStaysOpen() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-resumed-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("claude")
        // The recording's order: the agent ends before the request's result, the notice turn resumes it, and `idle` comes only
        // after the resumed agent's own notice turn. Input closed before that read fails the run (exit 24).
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '2.1.322\n'; exit 0; fi
        metadata=false
        for argument in "$@"; do if [ "$argument" = "--safe-mode" ]; then metadata=true; fi; done
        IFS= read -r initialize || exit 21
        request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
        if [ "$metadata" = true ]; then /bin/cat >/dev/null; exit 0; fi
        IFS= read -r prompt || exit 22
        printf '%s\n' '{"type":"system","subtype":"session_state_changed","state":"running"}'
        printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Agent","input":{"description":"Count","prompt":"Count.","run_in_background":true}}]}}'
        printf '%s\n' '{"type":"system","subtype":"task_started","task_id":"agent1","run_id":"run-1","tool_use_id":"toolu_1","description":"Count","is_backgrounded":true,"task_type":"local_agent"}'
        printf '%s\n' '{"type":"system","subtype":"task_updated","task_id":"agent1","run_id":"run-1","patch":{"status":"completed"}}'
        printf '%s\n' '{"type":"system","subtype":"task_notification","task_id":"agent1","run_id":"run-1","tool_use_id":"toolu_1","status":"completed","summary":"one"}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"launched"}'
        sleep 0.4
        printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"SendMessage","input":{"to":"agent1","summary":"Count on","message":"Count on."}}]}}'
        printf '%s\n' '{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"agent1","run_id":"run-2","task_type":"local_agent","description":"Count"}]}'
        printf '%s\n' '{"type":"system","subtype":"task_started","task_id":"agent1","run_id":"run-2","tool_use_id":"toolu_2","description":"Count","is_backgrounded":true,"task_type":"local_agent"}'
        printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2","content":"{\"success\":true,\"resumedAgentId\":\"agent1\"}"}]}}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"resumed","origin":{"kind":"task-notification","runId":"run-1"}}'
        IFS= read -r follow || exit 24
        printf '%s\n' "$follow" > follow.json
        printf '%s\n' '{"type":"system","subtype":"task_updated","task_id":"agent1","run_id":"run-2","patch":{"status":"completed"}}'
        printf '%s\n' '{"type":"system","subtype":"task_notification","task_id":"agent1","run_id":"run-2","tool_use_id":"toolu_2","status":"completed","summary":"two"}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"done","origin":{"kind":"task-notification","runId":"run-2"}}'
        printf '%s\n' '{"type":"system","subtype":"session_state_changed","state":"idle"}'
        while IFS= read -r extra; do printf '%s\n' "$extra" >> extra.jsonl; done
        """#
        try Data(source.utf8).write(to: binary); try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let plugin = root.appendingPathComponent("plugin")
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let providers = ProviderService(binaryOverrides: ["claude": binary])
        let events = PlanBox<[RunEvent]>([]); let lock = NSLock()
        func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return events.value }
        let runner = ProcessRunner(providerService: providers, pluginDirectory: plugin) { event in lock.lock(); events.value.append(event); lock.unlock() }
        let workspace = Workspace(id: "workspace", name: "Fixture", path: root.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "count"), workspace: workspace, allowPermissionPrompts: true)
            // After the notice turn's result the resumed agent runs, the pane waits on it, and new input still reaches the process.
            try await wait { values().compactMap(\.background).last.map { $0.waitingOnBackground && $0.tasks.first?.toolUseId == "toolu_2" } == true }
            #expect(!values().contains { $0.status != nil && $0.status != "running" })
            #expect(await runner.steer(sessionId: "pane", text: "and one more thing"))
            try await wait { values().contains { $0.status == "completed" } }
            #expect(try String(contentsOf: root.appendingPathComponent("follow.json"), encoding: .utf8).contains("and one more thing"))
            #expect(values().compactMap(\.background).last.map { $0.tasks.map(\.status) == ["completed"] && !$0.waitingOnBackground } == true)
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("extra.jsonl").path))
        } catch { await runner.shutdown(); await providers.shutdown(); throw error }
        await runner.shutdown(); await providers.shutdown()
    }
}
