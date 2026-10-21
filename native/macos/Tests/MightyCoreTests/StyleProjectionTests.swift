import Foundation
import Testing
@testable import MightyCore

struct StyleProjectionTests {
    private func panel(_ style: RegisteredStyle, prompts: [String] = [], group: String? = nil,
                       states: [String: String] = [:],
                       prerequisites: StylePrerequisiteResult = StylePrerequisiteResult(ready: true),
                       session: RunSession? = nil) -> StylePanel {
        StylePanelProjection.make(style: style, prompts: prompts, selectedGroupId: group,
                                  capabilityStates: states, prerequisites: prerequisites,
                                  session: session)
    }

    private func jobSession(open: Bool) -> RunSession {
        let toolName = open ? "ouroboros_start_execute_seed" : "ouroboros_job_result"
        let act = AgentActivity(id: "j1", provider: "claude", kind: "tool", state: "completed",
                                toolName: toolName, summary: toolName)
        var s = RunSession(workspaceId: "ws", title: "T")
        s.logs = [LogEntry(kind: "system", text: toolName, activity: act)]
        return s
    }

    @Test func theProjectionCarriesTheWholeCatalogueAndTheSource() throws {
        let style = StyleFixtures.bundled("ouroboros")
        let empty = panel(style)
        #expect(empty.style.id == "ouroboros" && empty.style.source == .bundled && empty.style.icon == "point.3.connected.trianglepath.dotted")
        #expect(empty.actions.count == 10 && empty.groups.count == 1 && empty.groups[0].selected)
        #expect(empty.phase?.id == "goal" && empty.phase?.index == 0 && empty.phase?.count == 6)
        // The entry phase shows the start rule's buttons; the phone need not know.
        #expect(empty.next == ["interview", "auto"] && empty.actions.first(where: { $0.id == "interview" })?.prominent == true)
        #expect(empty.presentation.headerTitle == "Ouroboros \u{00B7} 목표" && empty.presentation.source == .bundled)
        #expect(empty.setup.ready && empty.setup.installCommand?.hasPrefix("claude plugin marketplace") == true)
        #expect(empty.guidance?.hasPrefix("무엇을 만들까요?") == true && empty.recommended == nil)

        let running = panel(style, prompts: ["/ouroboros:seed"])
        #expect(running.phase?.id == "seed" && running.phase?.index == 2)
        #expect(running.next == ["run", "evaluate", "status"] && running.actions.first(where: { $0.id == "run" })?.prominent == true)
        #expect(running.guidance == "시드 단계가 끝났습니다. 다음 단계를 고르거나, 아래에 적어 같은 대화를 이어가세요.")
        #expect(running.presentation.headerTitle == "Ouroboros \u{00B7} 시드")

        let flags = try #require(running.actions.first { $0.id == "interview" })
        #expect(flags.takesText && flags.requiresText && flags.flags.isEmpty && flags.icon == "questionmark.bubble")
        let notReady = panel(style, prerequisites: StylePrerequisiteResult(ready: false, missing: ["없음"], hint: "힌트", canInstall: true))
        #expect(!notReady.setup.ready && notReady.setup.missing == ["없음"] && notReady.setup.hint == "힌트")
    }

    @Test func groupsAndTheRecommendationTravel() throws {
        // The feature exists only in the test vocabulary since v7 (§1.8).
        let style = StyleFixtures.catalogueStyle
        let coil = panel(style, group: "coil", states: ["acme.thing": "open"])
        #expect(coil.phase == nil && coil.groups.count == 2)
        #expect(coil.groups.first { $0.selected }?.id == "coil" && coil.groups.first { $0.id == "depth" }?.axis == "하나 \u{00B7} 지금")
        #expect(coil.next == ["plan", "loop", "memo", "work", "nba"])
        #expect(coil.recommended == "loop")
        #expect(coil.actions.count == 7 && coil.actions.first { $0.id == "nba" }?.flags == ["userInvoked", "readOnly"])
        #expect(coil.actions.first { $0.id == "hate" }?.flags == ["userInvoked"] && coil.actions.first { $0.id == "hate" }?.glyph == "😈")
        #expect(coil.presentation.headerTitle == "Deck")
        // Nothing offers an attachment any more, but the field still travels
        // for a phone that predates v7.
        #expect(coil.attachments.isEmpty)
        let encoded = String(data: try StylePanelProjection.serialise(coil), encoding: .utf8) ?? ""
        #expect(encoded.contains("\"attachments\" : ["))
        // The initial group comes from the capability when none was chosen yet.
        #expect(panel(style, states: ["acme.thing": "absent"]).groups.first { $0.selected }?.id == "depth")
    }

    @Test func aNonBundledStyleNamesItsSourceEverywhere() throws {
        let style = try StyleFixtures.registered(StyleFixtures.data(), source: .workspace, path: "/repo/.claude/mighty-styles/flow.json", workspacePath: "/repo")
        let made = panel(style)
        #expect(made.style.source == .workspace && made.presentation.source == .workspace)
        let text = try #require(String(data: StylePanelProjection.serialise(made), encoding: .utf8))
        #expect(text.contains("\"source\" : \"workspace\""))
    }

    @Test func jobOpenPanelShowsWhileOpenActionsAndGuidance() {
        let style = StyleFixtures.bundled("ouroboros")
        let openPanel = panel(style, prompts: ["/ouroboros:run"], session: jobSession(open: true))
        // While open: only whileOpen actions in declared order; first is prominent.
        #expect(openPanel.next == ["status", "cancel", "unstuck"])
        #expect(openPanel.actions.first(where: { $0.id == "status" })?.prominent == true)
        #expect(openPanel.actions.first(where: { $0.id == "evaluate" })?.prominent == false)
        #expect(openPanel.guidance?.contains("백그라운드에서 실행 중입니다") == true)
        // After close: normal next returns; a manifest without declaration is unaffected.
        let closedPanel = panel(style, prompts: ["/ouroboros:run"], session: jobSession(open: false))
        #expect(closedPanel.next.first == "evaluate")
        #expect(closedPanel.guidance?.contains("실행 단계가 끝났습니다") == true)
        // No session → no job open (backward-compatible default).
        let noSession = panel(style, prompts: ["/ouroboros:run"])
        #expect(noSession.next.first == "evaluate")
    }

    @Test func theLegacyAdapterRebuildsTodaysPayloads() throws {
        let ouroboros = StyleFixtures.bundled("ouroboros")
        let seeded = panel(ouroboros, prompts: ["/ouroboros:seed"])
        let flow = try #require(MobileLegacyStyleAdapter.payload(style: ouroboros, panel: seeded))
        #expect(flow.phase == "seed" && flow.ready)
        #expect(flow.takesText == ["interview", "auto", "unstuck"])
        #expect(flow.next.map(\.skill) == ["run", "evaluate", "status"])
        #expect(flow.all.count == 10 && flow.all.first?.skill == "interview" && flow.all.first?.title == "인터뷰 시작")
        #expect(flow.all.first?.help == "소크라테스식 질문으로 요구를 또렷하게 만듭니다 (모호도 0.2 이하까지)")
        // A phase-less pane still reports the entry phase, as today — and the
        // old `next` field is the phase map alone, which is empty at `goal`,
        // so an older phone falls back to `all` exactly as it used to (§7.4).
        let fresh = MobileLegacyStyleAdapter.payload(style: ouroboros, panel: panel(ouroboros))
        #expect(fresh?.phase == "goal" && fresh?.next.isEmpty == true)
        // The new panel still offers the start rule's buttons in the same state.
        #expect(panel(ouroboros).next == ["interview", "auto"])

        // The adapter belongs to the bundled Ouroboros and nothing else.
        let third = try StyleFixtures.registered(StyleFixtures.data())
        #expect(MobileLegacyStyleAdapter.payload(style: third, panel: panel(third)) == nil)
        let plan = StyleFixtures.bundled("claude-plan")
        #expect(MobileLegacyStyleAdapter.payload(style: plan, panel: panel(plan)) == nil)
    }
}
