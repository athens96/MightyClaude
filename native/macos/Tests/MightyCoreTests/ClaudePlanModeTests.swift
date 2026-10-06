import Foundation
import Darwin
import Testing
@testable import MightyCore

/// The same file Windows Core's `ClaudePlanModeVerification` reads: both
/// clients must recognise, answer and track Claude's plan mode alike.
struct ClaudePlanModeTests {
    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/claude-plan-mode.json")
    }()
    static func load() throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: fixture)) as? [String: Any])
    }
    static func canonical(_ value: Any) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
    }
    static func decision(_ row: [String: Any]) -> PlanDecision {
        switch row["decision"] as? String {
        case "approveAutoEdit": return .approveAutoEdit
        case "approveConfirmEach": return .approveConfirmEach
        case "revise": return .revise(feedback: row["feedback"] as? String ?? "")
        default: return .cancel
        }
    }
    private func channel(writes: PlanBox<[Data]>, displays: PlanBox<[ToolPermissionRequest]>, records: PlanBox<[PlanRecord]>, warnings: PlanBox<[String]> = PlanBox([])) -> ClaudePermissionChannel {
        ClaudePermissionChannel(runId: "run", prompt: Data(), write: { writes.value.append($0) }, emit: { displays.value.append($0) }, activity: { _, _ in },
                                warning: { warnings.value.append($0) }, fail: { _ in }, plan: { records.value.append($0) },
                                clock: { Date(timeIntervalSince1970: 1_800_000_000) })
    }
    private func response(_ data: Data) throws -> [String: Any] {
        let envelope = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let outer = try #require(envelope["response"] as? [String: Any])
        #expect(envelope["type"] as? String == "control_response" && outer["subtype"] as? String == "success")
        return try #require(outer["response"] as? [String: Any])
    }

    @Test func everyFixtureRequestIsRecognisedAsCommitted() throws {
        let rows = try #require(try Self.load()["planRequests"] as? [[String: Any]])
        #expect(rows.count >= 3)
        for row in rows {
            let writes = PlanBox<[Data]>([]), displays = PlanBox<[ToolPermissionRequest]>([]), records = PlanBox<[PlanRecord]>([])
            let channel = channel(writes: writes, displays: displays, records: records)
            channel.receive(try JSONSerialization.data(withJSONObject: row["request"]!))
            let expected = try #require(row["expected"] as? [String: Any])
            let display = try #require(displays.value.first, "\(row["name"]!)")
            #expect(display.canAnswerPlan == (expected["canAnswerPlan"] as? Bool), "\(row["name"]!)")
            #expect(display.canAllow == (expected["canAllow"] as? Bool), "\(row["name"]!)")
            #expect(display.plan == expected["plan"] as? String, "\(row["name"]!)")
            #expect(display.receivedAt == "2027-01-15T08:00:00.000Z")
            let approval = PlanApprovalRequest(sessionId: "pane-1", permission: display)
            #expect((approval != nil) == display.canAnswerPlan)
            if let approval {
                #expect(approval.sessionId == "pane-1" && approval.requestId == display.id && approval.runId == "run" && approval.plan == display.plan && approval.receivedAt == display.receivedAt)
            }
        }
    }

    @Test func everyFixtureDecisionAnswersAsCommitted() throws {
        let rows = try #require(try Self.load()["planResponses"] as? [[String: Any]])
        #expect(rows.count >= 10)
        for row in rows {
            let tool = try #require(row["toolUseId"] as? String), pane = row["paneMode"] as? String ?? "plan"
            if row["error"] as? Bool == true {
                #expect(throws: MightyError.self) { try ClaudePlanMode.response(for: Self.decision(row), toolUseId: tool, paneMode: pane) }
                continue
            }
            let value = try ClaudePlanMode.response(for: Self.decision(row), toolUseId: tool, paneMode: pane)
            #expect(try Self.canonical(value) == Self.canonical(row["expected"]!), "\(row["name"]!)")
        }
    }

    @Test func channelAnswersAPlanOnceAndRecordsIt() throws {
        let writes = PlanBox<[Data]>([]), displays = PlanBox<[ToolPermissionRequest]>([]), records = PlanBox<[PlanRecord]>([])
        let channel = channel(writes: writes, displays: displays, records: records)
        let request = try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": "p1", "request": ["subtype": "can_use_tool", "tool_name": "ExitPlanMode", "tool_use_id": "toolu_p1", "input": ["plan": "# Plan\n- one", "planFilePath": "/tmp/p.md"], "requires_user_interaction": true]])
        channel.receive(request)
        #expect(displays.value.count == 1 && displays.value[0].canAnswerPlan && !displays.value[0].canAllow)
        // The plan card answers it, so no "needs its own input screen" notice.
        #expect(displays.value[0].reason == nil)
        // The generic allow stays closed; a plan is answered with a plan decision.
        #expect(throws: MightyError.self) { try channel.respond(requestId: "p1", allow: true) }
        // An empty revise leaves the plan answerable and writes nothing.
        #expect(throws: MightyError.self) { try channel.answerPlan(requestId: "p1", decision: .revise(feedback: "  ")) }
        #expect(writes.value.isEmpty && records.value.isEmpty)
        let record = try channel.answerPlan(requestId: "p1", decision: .approveAutoEdit)
        let body = try response(try #require(writes.value.last))
        #expect(body["behavior"] as? String == "allow" && (body["updatedInput"] as? [String: Any])?.isEmpty == true)
        #expect(try Self.canonical(body["updatedPermissions"]!) == #"[{"destination":"session","mode":"acceptEdits","type":"setMode"}]"#)
        #expect(record.outcome == .approvedAuto && record.plan == "# Plan\n- one" && record.id == "p1" && record.runId == "run" && records.value == [record])
        #expect(displays.value.last?.state == "allowed")
        #expect(throws: MightyError.self) { try channel.answerPlan(requestId: "p1", decision: .cancel) }
        // Another tool is never answered as a plan.
        channel.receive(try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": "b1", "request": ["subtype": "can_use_tool", "tool_name": "Bash", "tool_use_id": "toolu_b1", "input": ["command": "ls"]]]))
        #expect(throws: MightyError.self) { try channel.answerPlan(requestId: "b1", decision: .approveAutoEdit) }
        // A generic deny (an older card, the phone) still keeps the plan, as cancelled.
        channel.receive(try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": "p2", "request": ["subtype": "can_use_tool", "tool_name": "ExitPlanMode", "tool_use_id": "toolu_p2", "input": ["plan": "# Two"], "requires_user_interaction": true]]))
        try channel.respond(requestId: "p2", allow: false)
        #expect(records.value.last?.id == "p2" && records.value.last?.outcome == .cancelled && records.value.last?.plan == "# Two")
        try channel.respond(requestId: "b1", allow: false)
        #expect(records.value.count == 2)
    }

    @Test func anApprovalNeverLowersTheModeThePaneRunsIn() throws {
        func mode(_ decision: PlanDecision, _ pane: String) -> String? { ClaudePlanMode.approvedCLIMode(decision, paneMode: pane) }
        #expect(mode(.approveConfirmEach, "plan") == "default" && mode(.approveAutoEdit, "plan") == "acceptEdits")
        #expect(mode(.approveConfirmEach, "manual") == "default" && mode(.approveAutoEdit, "manual") == "acceptEdits")
        #expect(mode(.approveConfirmEach, "acceptEdits") == "acceptEdits" && mode(.approveAutoEdit, "auto") == "auto" && mode(.approveConfirmEach, "fullAccess") == "bypassPermissions")
        #expect(mode(.revise(feedback: "x"), "fullAccess") == nil && mode(.cancel, "plan") == nil)
        // The channel uses the pane's stored mode.
        let writes = PlanBox<[Data]>([])
        let channel = ClaudePermissionChannel(runId: "run", prompt: Data(), write: { writes.value.append($0) }, emit: { _ in }, activity: { _, _ in }, warning: { _ in }, fail: { _ in }, paneMode: "auto")
        channel.receive(try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": "a1", "request": ["subtype": "can_use_tool", "tool_name": "ExitPlanMode", "tool_use_id": "toolu_a1", "input": ["plan": "# P"], "requires_user_interaction": true]]))
        try channel.answerPlan(requestId: "a1", decision: .approveConfirmEach)
        #expect((try response(try #require(writes.value.last))["updatedPermissions"] as? [[String: Any]])?.first?["mode"] as? String == "auto")
    }

    @Test func aPerRunPlanOverrideLaunchesInPlanWithoutTouchingThePane() throws {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "ws", input: "plan it", settings: RunSettings(permissionMode: "manual"))
        request.permissionModeOverride = "plan"
        let arguments = try ProviderService.arguments(request, pluginDirectory: URL(fileURLWithPath: "/tmp/plugin"), allowPermissionPrompts: true)
        let index = try #require(arguments.firstIndex(of: "--permission-mode"))
        #expect(arguments[index + 1] == "plan" && request.settings.permissionMode == "manual")
        // Never persisted with the request.
        #expect(!String(decoding: try JSONEncoder().encode(request), as: UTF8.self).contains("Override"))
        request.permissionModeOverride = "auto"
        #expect(throws: MightyError.self) { try CoreValidation.validate(request) }
        var codex = StartRunRequest(sessionId: "pane", workspaceId: "ws", input: "x", provider: "codex")
        codex.permissionModeOverride = "plan"
        #expect(throws: MightyError.self) { try CoreValidation.validate(codex) }
    }

    @Test func reviseCancelAndWithdrawnPlansAreKept() throws {
        let writes = PlanBox<[Data]>([]), displays = PlanBox<[ToolPermissionRequest]>([]), records = PlanBox<[PlanRecord]>([])
        let channel = channel(writes: writes, displays: displays, records: records)
        func plan(_ id: String) throws -> Data {
            try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": id, "request": ["subtype": "can_use_tool", "tool_name": "ExitPlanMode", "tool_use_id": "toolu_" + id, "input": ["plan": "Plan " + id], "requires_user_interaction": true]])
        }
        channel.receive(try plan("r1"))
        try channel.answerPlan(requestId: "r1", decision: .revise(feedback: " Split step 2 "))
        let revise = try response(try #require(writes.value.last))
        #expect(revise["behavior"] as? String == "deny" && (revise["message"] as? String)?.hasSuffix("The user said:\nSplit step 2") == true && revise["interrupt"] == nil)
        #expect(records.value.last?.outcome == .revised && records.value.last?.feedback == "Split step 2")
        channel.receive(try plan("r2"))
        try channel.answerPlan(requestId: "r2", decision: .cancel)
        #expect(try response(try #require(writes.value.last))["interrupt"] as? Bool == true)
        #expect(records.value.last?.outcome == .cancelled && displays.value.last?.state == "denied")
        // A plan the CLI withdrew, or one still open when the run ends, is kept as cancelled.
        channel.receive(try plan("r3"))
        channel.receive(try JSONSerialization.data(withJSONObject: ["type": "control_cancel_request", "request_id": "r3"]))
        channel.receive(try plan("r4"))
        channel.cancelAll()
        #expect(records.value.map(\.id) == ["r1", "r2", "r3", "r4"] && records.value.suffix(2).allSatisfy { $0.outcome == .cancelled })
    }

    @Test func aPlanTooLongToShowIsSentBackForAShorterOne() throws {
        let writes = PlanBox<[Data]>([]), displays = PlanBox<[ToolPermissionRequest]>([]), records = PlanBox<[PlanRecord]>([]), warnings = PlanBox<[String]>([])
        let channel = channel(writes: writes, displays: displays, records: records, warnings: warnings)
        channel.receive(try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": "big", "request": ["subtype": "can_use_tool", "tool_name": "ExitPlanMode", "tool_use_id": "toolu_big", "input": ["plan": String(repeating: "x", count: 70_000)]]]))
        let body = try response(try #require(writes.value.last))
        #expect(body["behavior"] as? String == "deny" && body["message"] as? String == ClaudePlanMode.tooLongMessage)
        #expect(displays.value.isEmpty && warnings.value == [L("plan.warning.tooLong")])
        // It is still kept, as a cancelled plan with a bounded copy of its text.
        #expect(records.value.count == 1 && records.value[0].outcome == .cancelled && records.value[0].planTruncated == true)
    }

    @Test func everyFixtureChecklistIsTrackedAsCommitted() throws {
        let rows = try #require(try Self.load()["todos"] as? [[String: Any]])
        #expect(rows.count >= 4)
        for row in rows {
            let saved = (row["saved"] as? [[String: Any]])?.map { TodoItem(id: $0["id"] as? String, content: $0["content"] as! String, activeForm: $0["activeForm"] as? String, status: $0["status"] as! String) }
            let tracker = TodoProgressTracker(progress: saved.map { TodoProgress(items: $0) })
            for case let frame as [String: Any] in row["frames"] as? [Any] ?? [] { _ = tracker.consume(frame) }
            let expected = try #require(row["expected"] as? [String: Any])
            let progress = try #require(tracker.progress, "\(row["name"]!)")
            #expect(progress.total == expected["total"] as? Int && progress.completed == expected["completed"] as? Int, "\(row["name"]!)")
            #expect(progress.currentText == expected["current"] as? String, "\(row["name"]!)")
            let items = try #require(expected["items"] as? [[String: Any]])
            #expect(progress.items == items.map { TodoItem(id: $0["id"] as? String, content: $0["content"] as! String, activeForm: $0["activeForm"] as? String, status: $0["status"] as! String) }, "\(row["name"]!)")
        }
    }

    @Test func everyFixtureBackgroundRunIsTrackedAsCommitted() throws {
        let rows = try #require(try Self.load()["background"] as? [[String: Any]])
        #expect(rows.count >= 3)
        for row in rows {
            let tracker = BackgroundTaskTracker(clock: { Date(timeIntervalSince1970: 1_800_000_000) })
            for case let step as [String: Any] in row["steps"] as? [Any] ?? [] {
                switch step["step"] as? String {
                case "turnEnded": _ = tracker.turnEnded()
                case "finish": _ = tracker.finish()
                default: _ = tracker.consume(step)
                }
            }
            let expected = try #require(row["expected"] as? [String: Any])
            #expect(tracker.work.turnEnded == expected["turnEnded"] as? Bool, "\(row["name"]!)")
            let tasks = try #require(expected["tasks"] as? [[String: Any]])
            #expect(tracker.work.tasks.count == tasks.count, "\(row["name"]!)")
            for (task, want) in zip(tracker.work.tasks, tasks) {
                #expect(task.id == want["id"] as? String && task.toolUseId == want["toolUseId"] as? String && task.kind == want["kind"] as? String, "\(row["name"]!) \(task.id)")
                #expect(task.description == want["description"] as? String && task.status == want["status"] as? String && task.summary == want["summary"] as? String, "\(row["name"]!) \(task.id)")
                #expect((task.endedAt != nil) == (want["ended"] as? Bool), "\(row["name"]!) \(task.id)")
                #expect(task.startedAt == "2027-01-15T08:00:00.000Z")
            }
        }
    }

    @Test func parserReportsTheChecklistAndBackgroundWorkOfTheMainAgent() throws {
        let todos = PlanBox<[TodoProgress]>([]), background = PlanBox<[BackgroundWork]>([])
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, todos: { todos.value.append($0) }, background: { background.value.append($0) })
        func line(_ value: [String: Any]) throws -> String { String(decoding: try JSONSerialization.data(withJSONObject: value), as: UTF8.self) + "\n" }
        parser.push(try line(["type": "assistant", "message": ["content": [["type": "tool_use", "id": "t1", "name": "TodoWrite", "input": ["todos": [["content": "Step", "status": "in_progress"]]]]]]]))
        parser.push(try line(["type": "system", "subtype": "task_started", "task_id": "bg1", "description": "Agent", "is_backgrounded": true, "task_type": "local_agent"]))
        // A task notification's own result is not the request's.
        parser.push(try line(["type": "result", "subtype": "success", "result": "x", "origin": ["kind": "task-notification"]]))
        #expect(background.value.last?.turnEnded == false)
        parser.push(try line(["type": "result", "subtype": "success", "result": "done"]))
        #expect(todos.value.map(\.currentText) == ["Step"])
        #expect(background.value.last?.turnEnded == true && background.value.last?.running.map(\.id) == ["bg1"])
        parser.finishBackground()
        #expect(background.value.last?.tasks.first?.status == "unknown")
        // Codex and Gemini panes never report either.
        let codex = PlanBox<Int>(0)
        let other = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, todos: { _ in codex.value += 1 }, background: { _ in codex.value += 1 })
        other.push(try line(["type": "system", "subtype": "task_started", "task_id": "bg1", "task_type": "local_agent"]))
        other.finishBackground()
        #expect(codex.value == 0)
    }

    @Test func anApprovedPlanSetsThePaneModeAndKeepsABoundedHistory() throws {
        var session = RunSession(id: "pane", workspaceId: "ws", title: "Claude", settings: RunSettings(permissionMode: "plan"))
        func record(_ id: String, _ outcome: PlanOutcome, plan: String = "# Plan") -> RunEvent {
            RunEvent(sessionId: "pane", type: "plan", plan: PlanRecord(id: id, runId: "run", plan: plan, receivedAt: "2026-10-06T01:00:00.000Z", decidedAt: "2026-10-06T01:01:00.000Z", outcome: outcome, feedback: outcome == .revised ? "more" : nil))
        }
        session.recordPlanMode(record("a", .revised))
        #expect(session.settings.permissionMode == "plan" && session.planHistory?.count == 1)
        session.recordPlanMode(record("b", .approvedAuto))
        #expect(session.settings.permissionMode == "acceptEdits")
        // Claude planned by itself in a pane not stored in plan: the pane keeps its mode.
        session.recordPlanMode(record("c", .approvedConfirm, plan: String(repeating: "가", count: 20_000)))
        #expect(session.settings.permissionMode == "acceptEdits")
        session.settings.permissionMode = "plan"
        session.recordPlanMode(record("c2", .approvedConfirm))
        #expect(session.settings.permissionMode == "manual")
        #expect(session.planHistory?.first { $0.id == "c" }?.planTruncated == true && (session.planHistory?.first { $0.id == "c" }?.plan.utf8.count ?? 0) <= ClaudePlanMode.maximumStoredPlanBytes)
        for index in 0..<12 { session.recordPlanMode(record("n\(index)", .cancelled)) }
        #expect(session.planHistory?.count == ClaudePlanMode.maximumHistory && session.planHistory?.last?.id == "n11" && session.settings.permissionMode == "manual")
        // A shell pane, or a Codex one, keeps none of it.
        var codex = RunSession(id: "codex", workspaceId: "ws", title: "Codex", provider: "codex")
        codex.recordPlanMode(record("x", .approvedAuto))
        #expect(codex.planHistory == nil && codex.settings.permissionMode == "manual")
        // Background work belongs to the latest run.
        session.recordPlanMode(RunEvent(sessionId: "pane", type: "background", background: BackgroundWork(tasks: [BackgroundTask(id: "t", kind: "agent", description: "d", startedAt: "2026-10-06T01:00:00.000Z")])))
        #expect(session.backgroundWork?.running.count == 1)
        session.recordPlanMode(RunEvent(sessionId: "pane", type: "status", status: "running"))
        #expect(session.backgroundWork == nil)
        session.recordPlanMode(RunEvent(sessionId: "pane", type: "todos", todos: TodoProgress(items: [TodoItem(content: "a", status: "weird")])))
        #expect(session.todoProgress?.items.first?.status == "pending")
    }

    @Test func theHistoryKeepsNewPlansWholeWithinItsBudget() {
        let lines = (1...400).map { "Step \($0): " + String(repeating: "x", count: 60) }.joined(separator: "\n")
        var history: [PlanRecord]?
        for index in 0..<10 {
            history = ClaudePlanMode.appending(PlanRecord(id: "h\(index)", runId: "run", plan: lines, receivedAt: "2026-10-06T01:00:00.000Z", decidedAt: "2026-10-06T01:01:00.000Z", outcome: .revised, feedback: String(repeating: "f", count: 3_000)), to: history)
        }
        let list = history ?? []
        let bytes = list.reduce(0) { $0 + $1.plan.utf8.count + ($1.feedback?.utf8.count ?? 0) }
        #expect(list.count == 10 && bytes <= ClaudePlanMode.maximumHistoryBytes + 10 * 1_536)
        #expect(list.last?.plan.utf8.count == ClaudePlanMode.maximumStoredPlanBytes || list.last?.plan.hasPrefix("Step 1:") == true)
        #expect(list.suffix(2).allSatisfy { $0.plan.utf8.count > 1_024 })
        let oldest = list[0]
        #expect(oldest.planTruncated == true && oldest.plan.split(separator: "\n").count == 8 && oldest.plan.hasPrefix("Step 1:") && (oldest.feedback?.utf8.count ?? 0) <= 512)
        #expect(ClaudePlanMode.normalizedHistory(list) == list)
    }

    @Test func savedPlanStateRoundTripsAndOldSnapshotsStillDecode() throws {
        let old = #"{"id":"pane","workspaceId":"ws","title":"Claude","kind":"claude","provider":"claude","model":"default","settings":{"effort":"default","permissionMode":"plan"},"status":"completed","logs":[],"createdAt":"2026-10-01T00:00:00Z"}"#
        let decoded = try JSONDecoder().decode(RunSession.self, from: Data(old.utf8))
        #expect(decoded.planHistory == nil && decoded.todoProgress == nil && decoded.backgroundWork == nil)
        var session = decoded
        session.planHistory = [PlanRecord(id: "p", runId: "r", plan: "# P", receivedAt: "2026-10-06T01:00:00.000Z", decidedAt: "2026-10-06T01:01:00.000Z", outcome: .approvedConfirm)]
        session.todoProgress = TodoProgress(items: [TodoItem(id: "1", content: "a", status: "completed")])
        session.backgroundWork = BackgroundWork(tasks: [BackgroundTask(id: "t", kind: "shell", description: "d", startedAt: "2026-10-06T01:00:00.000Z")], turnEnded: true)
        let again = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(again == session)
        // Damage in an optional part keeps the conversation.
        let damaged = old.replacingOccurrences(of: #""logs":[]"#, with: ##""logs":[],"planHistory":[{"id":1},{"id":"ok","runId":"r","plan":"# P","receivedAt":"2026-10-06T01:00:00.000Z","decidedAt":"2026-10-06T01:01:00.000Z","outcome":"revised"},{"id":"bad","outcome":"unknown"}],"backgroundWork":"x""##)
        let kept = try JSONDecoder().decode(RunSession.self, from: Data(damaged.utf8))
        // Only the damaged records are dropped.
        #expect(kept.id == "pane" && kept.planHistory?.map(\.id) == ["ok"] && kept.backgroundWork == nil)
        // Restoring: a task still running when the app quit did not report an end.
        let workspace = Workspace(id: "ws", name: "Repo", path: "/tmp/repo")
        var running = session; running.status = "running"
        running.backgroundWork = BackgroundWork(tasks: [BackgroundTask(id: "t", kind: "agent", description: "d", startedAt: "2026-10-06T01:00:00.000Z")])
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [running]), restoring: true).sessions[0]
        #expect(restored.backgroundWork?.tasks.first?.status == "unknown" && restored.planHistory?.count == 1 && restored.todoProgress?.completed == 1)
        let saved = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [running]), restoring: false).sessions[0]
        #expect(saved.backgroundWork?.tasks.first?.status == "running")
        var shell = session; shell.id = "sh"; shell.kind = "shell"
        let shellRestored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [shell]), restoring: true).sessions[0]
        #expect(shellRestored.planHistory == nil && shellRestored.todoProgress == nil && shellRestored.backgroundWork == nil)
    }
}

/// The runner path, against a fake `claude` that asks ExitPlanMode, keeps
/// working after its result while a background agent runs, then reports it.
@Suite(.serialized)
struct ClaudePlanModeRunnerTests {
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Plan fixture timed out") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    @Test func aFakeCLIGetsThePlanAnswersAndReportsItsChecklistAndBackgroundWork() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-plan-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("claude")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '2.1.289\n'; exit 0; fi
        metadata=false
        for argument in "$@"; do if [ "$argument" = "--safe-mode" ]; then metadata=true; fi; done
        IFS= read -r initialize || exit 21
        request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
        if [ "$metadata" = true ]; then /bin/cat >/dev/null; exit 0; fi
        printf '%s\n' "$@" > arguments.txt
        env > environment.txt
        IFS= read -r prompt || exit 22
        printf '%s\n' '{"type":"control_request","request_id":"plan-1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","tool_use_id":"toolu_plan","input":{"plan":"# Plan\n1. Do it","planFilePath":"/tmp/plan.md"},"requires_user_interaction":true}}'
        IFS= read -r response || exit 23
        printf '%s\n' "$response" > response.json
        case "$response" in *'"interrupt":true'*)
          printf '%s\n' '{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["[Request interrupted by user]"]}'
          while IFS= read -r extra; do :; done; exit 0;;
        esac
        printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_todo","name":"TodoWrite","input":{"todos":[{"content":"Do it","status":"in_progress","activeForm":"Doing it"}]}}]}}'
        printf '%s\n' '{"type":"system","subtype":"task_started","task_id":"bg1","tool_use_id":"toolu_bg","description":"Review","is_backgrounded":true,"task_type":"local_agent"}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"turn done"}'
        if [ -f quiet ]; then
          # The agent ends without a follow-up turn or an idle event.
          printf '%s\n' '{"type":"system","subtype":"task_notification","task_id":"bg1","status":"completed","summary":"quiet"}'
          while IFS= read -r extra; do :; done; exit 0
        fi
        # stdin stays open while the background agent runs: new input joins this process.
        IFS= read -r follow || exit 24
        printf '%s\n' "$follow" > follow.json
        printf '%s\n' '{"type":"system","subtype":"task_notification","task_id":"bg1","tool_use_id":"toolu_bg","status":"completed","summary":"All good"}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"noted","origin":{"kind":"task-notification"}}'
        while IFS= read -r extra; do printf '%s\n' "$extra" >> extra.jsonl; done
        """#
        try Data(source.utf8).write(to: binary); try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let plugin = root.appendingPathComponent("plugin")
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let providers = ProviderService(binaryOverrides: ["claude": binary])
        let events = PlanBox<[RunEvent]>([]); let lock = NSLock()
        func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return events.value }
        let runner = ProcessRunner(providerService: providers, pluginDirectory: plugin, backgroundIdleClose: 0.5) { event in lock.lock(); events.value.append(event); lock.unlock() }
        let workspace = Workspace(id: "workspace", name: "Fixture", path: root.path)
        // The pane is stored in manual; this request alone starts in plan (a guided style's override).
        var request = StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "plan it", settings: RunSettings(permissionMode: "manual"))
        request.permissionModeOverride = "plan"
        do {
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { values().contains { $0.permission?.state == "pending" } }
            let pending = try #require(values().compactMap(\.permission).first)
            let approval = try #require(PlanApprovalRequest(sessionId: "pane", permission: pending))
            #expect(approval.plan == "# Plan\n1. Do it" && !pending.canAllow)
            await #expect(throws: MightyError.self) { try await runner.answerPlan(sessionId: "pane", runId: "other", requestId: pending.id, decision: .approveAutoEdit) }
            try await runner.answerPlan(sessionId: "pane", runId: pending.runId, requestId: pending.id, decision: .approveAutoEdit)
            // The turn is over but its background agent runs: input stays open and new text joins.
            try await wait { values().compactMap(\.background).contains { $0.waitingOnBackground } }
            #expect(!values().contains { $0.status == "completed" })
            #expect(await runner.steer(sessionId: "pane", text: "and one more thing"))
            try await wait { values().contains { $0.status == "completed" } }
            #expect(try String(contentsOf: root.appendingPathComponent("follow.json"), encoding: .utf8).contains("and one more thing"))
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("extra.jsonl").path))
            #expect(await !runner.steer(sessionId: "pane", text: "too late"))
            let body = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("response.json"))) as? [String: Any])
            let answer = try #require((body["response"] as? [String: Any])?["response"] as? [String: Any])
            #expect(answer["behavior"] as? String == "allow" && (answer["updatedPermissions"] as? [[String: Any]])?.first?["mode"] as? String == "acceptEdits")
            #expect(try String(contentsOf: root.appendingPathComponent("arguments.txt"), encoding: .utf8).contains("--permission-mode\nplan\n"))
            #expect(try String(contentsOf: root.appendingPathComponent("environment.txt"), encoding: .utf8).contains("CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS=1"))
            #expect(values().compactMap(\.plan).map(\.outcome) == [.approvedAuto])
            #expect(values().compactMap(\.todos).last?.currentText == "Doing it")
            let backgrounds = values().compactMap(\.background)
            #expect(backgrounds.last?.tasks.first?.status == "completed" && backgrounds.last?.tasks.first?.summary == "All good" && backgrounds.last?.waitingOnBackground == false)
            // The pane was not stored in plan, so the approval does not change it.
            var session = RunSession(id: "pane", workspaceId: workspace.id, title: "Claude", settings: RunSettings(permissionMode: "manual"))
            for event in values() { session.recordPlanMode(event) }
            #expect(session.settings.permissionMode == "manual" && session.planHistory?.count == 1 && session.todoProgress?.total == 1)

            // Cancel interrupts the turn: the run ends stopped, without an error line.
            try FileManager.default.removeItem(at: root.appendingPathComponent("response.json"))
            let before = values().count
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { values().filter { $0.permission?.state == "pending" }.count == 2 }
            let second = try #require(values().compactMap(\.permission).last { $0.state == "pending" })
            try await runner.answerPlan(sessionId: "pane", runId: second.runId, requestId: second.id, decision: .cancel)
            try await wait { values().last?.status == "stopped" }
            let cancelled = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("response.json"))) as? [String: Any])
            #expect(((cancelled["response"] as? [String: Any])?["response"] as? [String: Any])?["interrupt"] as? Bool == true)
            #expect(values().compactMap(\.plan).map(\.outcome) == [.approvedAuto, .cancelled])
            #expect(!values()[before...].contains { $0.entry?.kind == "error" })

            // A background agent that ends with no follow-up turn: the idle close lets the CLI exit.
            try Data().write(to: root.appendingPathComponent("quiet"))
            let completed = values().filter { $0.status == "completed" }.count
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { values().filter { $0.permission?.state == "pending" }.count == 3 }
            let third = try #require(values().compactMap(\.permission).last { $0.state == "pending" })
            try await runner.answerPlan(sessionId: "pane", runId: third.runId, requestId: third.id, decision: .approveConfirmEach)
            try await wait { values().filter { $0.status == "completed" }.count == completed + 1 }
        } catch { await runner.shutdown(); await providers.shutdown(); throw error }
        await runner.shutdown(); await providers.shutdown()
    }

    @Test func stoppingAPaneWaitingOnBackgroundWorkEndsEverything() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-plan-stop-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("claude")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '2.1.289\n'; exit 0; fi
        metadata=false
        for argument in "$@"; do if [ "$argument" = "--safe-mode" ]; then metadata=true; fi; done
        IFS= read -r initialize || exit 21
        request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
        if [ "$metadata" = true ]; then /bin/cat >/dev/null; exit 0; fi
        IFS= read -r prompt || exit 22
        printf '%s\n' "$$" > child-pid.txt
        printf '%s\n' '{"type":"system","subtype":"task_started","task_id":"sh1","description":"npm run dev","task_type":"local_bash"}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"started"}'
        while IFS= read -r extra; do :; done
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
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "serve"), workspace: workspace, allowPermissionPrompts: true)
            try await wait { values().compactMap(\.background).contains { $0.waitingOnBackground } }
            await runner.stop(id: "pane")
            #expect(values().last?.status == "stopped" && values().compactMap(\.background).last?.tasks.first?.status == "unknown")
            let pid = try #require(Int32(String(contentsOf: root.appendingPathComponent("child-pid.txt"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
            #expect(Darwin.kill(pid, 0) != 0)
        } catch { await runner.shutdown(); await providers.shutdown(); throw error }
        await runner.shutdown(); await providers.shutdown()
    }
}

/// A reference cell the channel callbacks can append to.
final class PlanBox<Value>: @unchecked Sendable {
    var value: Value
    init(_ value: Value) { self.value = value }
}
