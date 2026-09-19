import Foundation
import Testing
@testable import MightyCore

/// The equivalence oracle for the Ouroboros bundle: every value the old
/// `OuroborosFlowTests` asserted, now read out of the manifest (§5.9).
struct StylesOuroborosTests {
    private var style: RegisteredStyle { StyleFixtures.bundled("ouroboros") }
    private var evaluator: StyleEvaluator { style.evaluator }
    private func phase(_ id: String) -> StylePhase? { style.manifest.phase(id) }

    @Test func promptsPhasesAndNextSteps() {
        #expect(evaluator.prompt(actionId: "interview", text: "  결제 모듈 리팩터링 ") == "/ouroboros:interview 결제 모듈 리팩터링")
        #expect(evaluator.prompt(actionId: "seed", text: "") == "/ouroboros:seed" && evaluator.prompt(actionId: "nope", text: "") == nil)
        #expect(evaluator.recognised(inPrompt: "/ouroboros:run seed.yaml") == .action("run") && evaluator.recognised(inPrompt: "ooo evolve") == .action("evolve"))
        #expect(evaluator.recognised(inPrompt: "/archify x") == nil && evaluator.recognised(inPrompt: "hello") == nil && evaluator.recognised(inPrompt: "/ouroboros:") == nil)
        #expect(style.manifest.action("auto")?.phase == "interview" && style.manifest.action("ralph")?.phase == "evolve" && style.manifest.action("status")?.phase == nil)
        let logs = [LogEntry(kind: "user", text: "/ouroboros:interview 목표"), LogEntry(kind: "assistant", text: "질문…"),
                    LogEntry(kind: "user", text: "/ouroboros:seed"), LogEntry(kind: "user", text: "/ouroboros:status"), LogEntry(kind: "user", text: "조금 더 보완해줘")]
        // Status and free text do not move the flow.
        #expect(evaluator.currentPhase(prompts: logs.filter { $0.kind == "user" }.map(\.text))?.id == "seed")
        #expect(evaluator.currentPhase(prompts: [])?.id == "goal")
        // The pane's phase comes from its request history, which survives the log being trimmed by tool activity.
        var session = RunSession(workspaceId: "ws", title: "Claude", logs: [LogEntry(kind: "system", text: "도구 실행")])
        session.beginGraphRun(input: "/ouroboros:interview 목표", id: "r1"); session.beginGraphRun(input: "/ouroboros:run", id: "r2")
        #expect(evaluator.currentPhase(session: session)?.id == "run")
        #expect(evaluator.currentPhase(session: RunSession(workspaceId: "ws", title: "Claude", logs: logs))?.id == "seed")
        let takesText = Set(style.manifest.actions.filter(\.takesText).map(\.id))
        #expect(takesText == ["interview", "auto", "unstuck"])
        #expect(evaluator.nextActions(phase: phase("interview"), group: nil).first?.id == "seed")
        #expect(evaluator.nextActions(phase: phase("seed"), group: nil).first?.id == "run")
        #expect(evaluator.nextActions(phase: phase("run"), group: nil).first?.id == "evaluate")
        #expect(evaluator.nextActions(phase: phase("evaluate"), group: nil).first?.id == "evolve")
        #expect(evaluator.nextActions(phase: phase("goal"), group: nil).isEmpty)
        #expect(evaluator.requestTitle(forInput: "/ouroboros:evaluate") == "평가" && evaluator.requestTitle(forInput: "/ouroboros:unstuck") == "막힘 풀기")
        #expect(evaluator.requestTitle(forInput: "plain request") == nil && evaluator.requestTitle(forInput: "ooo 이거 해줘") == nil)
        // The entry phase has its buttons from the start rule, not from `next`.
        #expect(evaluator.startActions(phase: phase("goal")).map(\.id) == ["interview", "auto"] && evaluator.resetTitle == "새 목표")
        #expect(evaluator.startActions(phase: phase("seed")).isEmpty)
    }

    @Test func autoAllowIsExactNamesOnly() {
        let prefix = "mcp__plugin_ouroboros_ouroboros__"
        let stateTools = ["ouroboros_interview", "ouroboros_pm_interview", "ouroboros_lateral_think", "ouroboros_generate_seed", "ouroboros_brownfield",
                          "ouroboros_session_status", "ouroboros_job_status", "ouroboros_job_wait", "ouroboros_job_result",
                          "ouroboros_query_events", "ouroboros_query_projection", "ouroboros_lineage_status", "ouroboros_measure_drift",
                          "ouroboros_ac_dashboard", "ouroboros_ac_tree_hud", "ouroboros_session_signal_targets"]
        #expect(stateTools.count == 16 && style.manifest.autoAllow.count == 17)
        for name in stateTools { #expect(evaluator.autoAllowed(toolName: prefix + name)) }
        #expect(evaluator.autoAllowed(toolName: "ToolSearch"))
        // Tools that start work keep their prompt, and the prefix cannot be borrowed by another server.
        for name in ["Bash", "Write", "Edit", "mcp__other__ouroboros_interview", "mcp__plugin_ouroboros_evil__x", "AskUserQuestion", "",
                     prefix + "ouroboros_execute_seed", prefix + "ouroboros_start_auto", prefix + "ouroboros_ralph",
                     prefix + "evil__ouroboros_interview", prefix] {
            #expect(!evaluator.autoAllowed(toolName: name))
        }
    }

    @Test func prerequisites() throws {
        let home = StyleFixtures.temporaryDirectory("ouroboros")
        defer { try? FileManager.default.removeItem(at: home) }
        let bin = home.appendingPathComponent("bin", isDirectory: true)
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let manifest = style.manifest
        func result(_ path: String) -> StylePrerequisiteResult {
            StylePrerequisiteProbe.evaluate(manifest.prerequisites, install: manifest.install, home: home, workspacePath: nil, environment: ["PATH": path])
        }
        let empty = result(bin.path)
        // `report: "first"` shows one line, and the uvx probe is `install: false`.
        #expect(!empty.ready && empty.missing == ["Ouroboros 플러그인이 설치되어 있지 않습니다"] && empty.canInstall)
        #expect(empty.hint?.contains("Enter는 직접 누르세요.") == true)
        try FileManager.default.createDirectory(at: home.appendingPathComponent(".claude/plugins"), withIntermediateDirectories: true)
        try Data(#"{"version":2,"plugins":{"ouroboros@ouroboros":[{"installPath":"/x"}]}}"#.utf8).write(to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let noUvx = result(bin.path)
        #expect(!noUvx.ready && noUvx.missing == ["uvx가 필요합니다 (Ouroboros MCP 서버 실행용)"] && !noUvx.canInstall)
        let uvx = bin.appendingPathComponent("uvx"); try Data("#!/bin/sh\n".utf8).write(to: uvx)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: uvx.path)
        #expect(result("/nowhere:" + bin.path).ready)
    }

    @Test func placeholdersGuidanceAndEnterFollowTheManifest() {
        #expect(evaluator.placeholder(phase: phase("goal"), running: false, answering: false) == "무엇을 만들까요? 목표를 적고 Enter로 인터뷰를 시작하세요…")
        #expect(evaluator.placeholder(phase: phase("seed"), running: false, answering: false) == "이어서 요청하거나 위에서 다음 단계를 고르세요…")
        #expect(evaluator.placeholder(phase: phase("seed"), running: false, answering: true) == "직접 답하려면 여기에 적고 Enter…")
        #expect(evaluator.guidanceLine(phase: phase("seed"), running: false) == "시드 단계가 끝났습니다. 다음 단계를 고르거나, 아래에 적어 같은 대화를 이어가세요.")
        #expect(evaluator.guidanceLine(phase: phase("seed"), running: true) == "시드 진행 중 \u{00B7} 질문이 오면 여기에 표시됩니다")
        #expect(evaluator.guidanceLine(phase: phase("goal"), running: false)?.hasPrefix("무엇을 만들까요?") == true)
        #expect(evaluator.enterArmedPrefix(phase: phase("goal"), running: false, hasRequests: false) == "/ouroboros:interview")
        #expect(evaluator.enterArmedPrefix(phase: phase("goal"), running: false, hasRequests: true) == nil)
        #expect(!evaluator.drawsGroupMap())
    }
}
