import Foundation
import Testing
@testable import MightyCore

struct OuroborosFlowTests {
    @Test func promptsPhasesAndNextStepsFollowTheOuroborosLoop() {
        #expect(OuroborosFlow.prompt(skill: "interview", text: "  결제 모듈 리팩터링 ") == "/ouroboros:interview 결제 모듈 리팩터링")
        #expect(OuroborosFlow.prompt(skill: "seed") == "/ouroboros:seed" && OuroborosFlow.prompt(skill: "nope") == nil)
        #expect(OuroborosFlow.skill(inPrompt: "/ouroboros:run seed.yaml") == "run" && OuroborosFlow.skill(inPrompt: "ooo evolve") == "evolve")
        #expect(OuroborosFlow.skill(inPrompt: "/archify x") == nil && OuroborosFlow.skill(inPrompt: "hello") == nil && OuroborosFlow.skill(inPrompt: "/ouroboros:") == nil)
        #expect(OuroborosFlow.phase(forSkill: "auto") == .interview && OuroborosFlow.phase(forSkill: "ralph") == .evolve && OuroborosFlow.phase(forSkill: "status") == nil)
        let logs = [LogEntry(kind: "user", text: "/ouroboros:interview 목표"), LogEntry(kind: "assistant", text: "질문…"),
                    LogEntry(kind: "user", text: "/ouroboros:seed"), LogEntry(kind: "user", text: "/ouroboros:status"), LogEntry(kind: "user", text: "조금 더 보완해줘")]
        #expect(OuroborosFlow.currentPhase(prompts: logs.filter { $0.kind == "user" }.map(\.text)) == .seed)       // status and free text do not move the flow
        #expect(OuroborosFlow.currentPhase(prompts: []) == .goal)
        // The pane's phase comes from its request history, which survives the log being trimmed by tool activity.
        var session = RunSession(workspaceId: "ws", title: "Claude", logs: [LogEntry(kind: "system", text: "도구 실행")])
        session.beginGraphRun(input: "/ouroboros:interview 목표", id: "r1"); session.beginGraphRun(input: "/ouroboros:run", id: "r2")
        #expect(OuroborosFlow.currentPhase(session: session) == .run)
        #expect(OuroborosFlow.currentPhase(session: RunSession(workspaceId: "ws", title: "Claude", logs: logs)) == .seed)
        #expect(OuroborosFlow.takesText("interview") && OuroborosFlow.takesText("unstuck") && !OuroborosFlow.takesText("status") && !OuroborosFlow.takesText("seed"))
        #expect(OuroborosFlow.nextActions(after: .interview).first?.skill == "seed")
        #expect(OuroborosFlow.nextActions(after: .seed).first?.skill == "run" && OuroborosFlow.nextActions(after: .run).first?.skill == "evaluate")
        #expect(OuroborosFlow.nextActions(after: .evaluate).first?.skill == "evolve" && OuroborosFlow.nextActions(after: .goal).isEmpty)
        #expect(OuroborosFlow.requestTitle(forInput: "/ouroboros:evaluate") == "평가" && OuroborosFlow.requestTitle(forInput: "/ouroboros:unstuck") == "막힘 풀기")
        #expect(OuroborosFlow.requestTitle(forInput: "plain request") == nil && OuroborosFlow.requestTitle(forInput: "ooo 이거 해줘") == nil)
    }

    @Test func onlyOuroborosStateToolsAndToolDiscoveryAreAutoAllowed() {
        #expect(OuroborosFlow.autoAllowed(toolName: "mcp__plugin_ouroboros_ouroboros__ouroboros_interview"))
        #expect(OuroborosFlow.autoAllowed(toolName: "ToolSearch"))
        #expect(OuroborosFlow.autoAllowed(toolName: "mcp__plugin_ouroboros_ouroboros__ouroboros_generate_seed"))
        // Tools that start work keep their prompt, and the prefix cannot be borrowed by another server.
        for name in ["Bash", "Write", "Edit", "mcp__other__ouroboros_interview", "mcp__plugin_ouroboros_evil__x", "AskUserQuestion", "",
                     "mcp__plugin_ouroboros_ouroboros__ouroboros_execute_seed", "mcp__plugin_ouroboros_ouroboros__ouroboros_start_auto",
                     "mcp__plugin_ouroboros_ouroboros__ouroboros_ralph", "mcp__plugin_ouroboros_ouroboros__evil__ouroboros_interview", "mcp__plugin_ouroboros_ouroboros__"] {
            #expect(!OuroborosFlow.autoAllowed(toolName: name))
        }
    }

    @Test func prerequisitesReadThePluginRegistryAndPath() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("ouroboros-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let bin = home.appendingPathComponent("bin", isDirectory: true)
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        #expect(OuroborosFlow.prerequisites(home: home, environment: ["PATH": bin.path]) == .init(pluginInstalled: false, uvxAvailable: false))
        try FileManager.default.createDirectory(at: home.appendingPathComponent(".claude/plugins"), withIntermediateDirectories: true)
        try Data(#"{"version":2,"plugins":{"ouroboros@ouroboros":[{"installPath":"/x"}]}}"#.utf8).write(to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let uvx = bin.appendingPathComponent("uvx"); try Data("#!/bin/sh\n".utf8).write(to: uvx)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: uvx.path)
        let ready = OuroborosFlow.prerequisites(home: home, environment: ["PATH": "/nowhere:" + bin.path])
        #expect(ready.pluginInstalled && ready.uvxAvailable && ready.ready)
    }

    private func questionnaire(_ json: String) throws -> UserQuestionnaire { try #require(UserQuestionnaire.parse(inputJSON: json)) }

    @Test func questionnaireProgressWalksQuestionsAndProducesValidAnswers() throws {
        let form = try questionnaire(#"{"questions":[{"header":"범위","question":"어디까지 할까요?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":"b"}]},{"header":"대상","question":"무엇을 포함할까요?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""},{"label":"Z","description":""}]}]}"#)
        var progress = QuestionnaireProgress(requestKey: "k")
        #expect(progress.current(in: form)?.header == "범위")
        #expect(progress.commit(customText: "   ", in: form) == nil)                  // nothing to answer with
        #expect(progress.choose("missing", in: form) == nil && progress.index == 0)
        #expect(progress.choose("B", in: form) == .next && progress.index == 1)
        // Multi-select chips toggle; Enter (or 선택 완료) commits them with optional text, in option order.
        #expect(progress.choose("Z", in: form) == nil && progress.choose("X", in: form) == nil && progress.choose("Y", in: form) == nil)
        #expect(progress.choose("Y", in: form) == nil && progress.selected == ["Z", "X"])
        guard case .complete(let answers)? = progress.commit(customText: "그리고 W", in: form) else { Issue.record("expected completion"); return }
        #expect(answers["어디까지 할까요?"] == UserQuestionAnswer(selectedOptions: ["B"]))
        #expect(answers["무엇을 포함할까요?"] == UserQuestionAnswer(selectedOptions: ["X", "Z"], customText: "그리고 W"))
        #expect(try form.validatedAnswers(answers) == ["어디까지 할까요?": "B", "무엇을 포함할까요?": "X, Z, 그리고 W"])
        // Free text alone answers a single-select question; back returns to it.
        var typed = QuestionnaireProgress(requestKey: "k2")
        #expect(typed.commit(customText: "직접 입력", in: form) == .next)
        typed.back(in: form)
        #expect(typed.index == 0 && typed.selected.isEmpty)
        // Going back to a multi-select question shows its earlier picks again.
        var revisit = QuestionnaireProgress(requestKey: "k3")
        let multiFirst = try questionnaire(#"{"questions":[{"header":"대상","question":"무엇을?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""}]},{"header":"범위","question":"어디까지?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}"#)
        _ = revisit.choose("Y", in: multiFirst)
        #expect(revisit.commit(customText: "", in: multiFirst) == .next)
        revisit.back(in: multiFirst)
        #expect(revisit.index == 0 && revisit.selected == ["Y"])
        #expect(typed.choose("A", in: form) == .next && typed.answers["어디까지 할까요?"] == UserQuestionAnswer(selectedOptions: ["A"]))
    }

    @Test func askUserQuestionBecomesAQuestionBlockSettledByTheAnswer() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-question", graph: { nodes.append($0) }, graphInput: "/ouroboros:interview 목표")
        func send(_ value: [String: Any]) throws { var data = try JSONSerialization.data(withJSONObject: value); data.append(10); parser.push(data) }
        let option: [[String: Any]] = [["label": "CLI", "description": ""], ["label": "라이브러리", "description": ""]]
        let input: [String: Any] = ["questions": [["header": "형태", "question": "어떤 형태로 제공할까요?", "multiSelect": false, "options": option]]]
        let use: [String: Any] = ["type": "tool_use", "id": "ask-1", "name": "AskUserQuestion", "input": input]
        try send(["type": "assistant", "uuid": "m1", "session_id": "s", "message": ["id": "m1", "content": [use]] as [String: Any]])
        let asked = try #require(nodes.last(where: { $0.kind == "question" }))
        #expect(asked.state == "waiting" && asked.title == "질문 · 형태" && asked.parentId == ExecutionGraphSupport.mainNodeID(runId: "run-question"))
        #expect(asked.input?.contains("어떤 형태로 제공할까요?") == true && asked.input?.contains("○ 라이브러리") == true)
        let result: [String: Any] = ["type": "tool_result", "tool_use_id": "ask-1", "content": "User has answered: \"어떤 형태로 제공할까요?\"=\"CLI\""]
        try send(["type": "user", "message": ["content": [result]] as [String: Any]])
        let answered = try #require(nodes.last(where: { $0.id == asked.id }))
        #expect(answered.state == "completed" && answered.output?.contains("CLI") == true)
        // Sessions keep the kind; the style survives normalization only for local Claude panes.
        var session = RunSession(workspaceId: "ws", title: "Claude"); session.mightyStyle = "ouroboros"; session.agentViewMode = "mighty"
        var codex = RunSession(workspaceId: "ws", title: "Codex", provider: "codex"); codex.mightyStyle = "ouroboros"
        var odd = RunSession(workspaceId: "ws", title: "Claude"); odd.mightyStyle = "something"
        let state = StateRepository.normalize(AppSnapshot(workspaces: [Workspace(id: "ws", name: "R", path: "/tmp/r")], sessions: [session, codex, odd]), restoring: true)
        #expect(state.sessions.map(\.mightyStyle) == ["ouroboros", nil, nil])
        let restored = try JSONDecoder().decode(RunSession.self, from: try JSONEncoder().encode(state.sessions[0]))
        #expect(restored.mightyStyle == "ouroboros")
    }
}
