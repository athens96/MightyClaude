import Foundation
import Testing
@testable import MightyCore

struct StyleProjectionTests {
    private func panel(_ style: RegisteredStyle, prompts: [String] = [], group: String? = nil,
                       states: [String: String] = [:], attachments: [StyleAttachmentItem] = [],
                       prerequisites: StylePrerequisiteResult = StylePrerequisiteResult(ready: true)) -> StylePanel {
        StylePanelProjection.make(style: style, prompts: prompts, selectedGroupId: group,
                                  capabilityStates: states, attachments: attachments, prerequisites: prerequisites)
    }

    @Test func theProjectionCarriesTheWholeCatalogueAndTheSource() throws {
        let style = StyleFixtures.bundled("ouroboros")
        let empty = panel(style)
        #expect(empty.style.id == "ouroboros" && empty.style.source == .bundled && empty.style.icon == "point.3.connected.trianglepath.dotted")
        #expect(empty.actions.count == 9 && empty.groups.count == 1 && empty.groups[0].selected)
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

    @Test func groupsRecommendationAndAttachmentsTravel() throws {
        let style = StyleFixtures.bundled("paperthin")
        let items = [StyleAttachmentItem(id: "DESIGN.local.md", title: "DESIGN", detail: "0.1.0 \u{00B7} full", readOnly: true, openPath: "/repo/x")]
        let coil = panel(style, group: "coil", states: [StyleCapabilityID.casebook: "open"], attachments: items)
        #expect(coil.phase == nil && coil.groups.count == 4)
        #expect(coil.groups.first { $0.selected }?.id == "coil" && coil.groups.first { $0.id == "depth" }?.axis == "하나 \u{00B7} 지금")
        #expect(coil.next == ["re0-plan", "re0-loop", "re0-memo", "re0-work", "catchup", "nba"])
        #expect(coil.recommended == "re0-loop")
        #expect(coil.actions.count == 28 && coil.actions.first { $0.id == "prism" }?.flags == ["userInvoked", "readOnly"])
        #expect(coil.attachments == [StylePanel.Attachment(id: "DESIGN.local.md", title: "DESIGN", detail: "0.1.0 \u{00B7} full", readOnly: true)])
        #expect(coil.presentation.headerTitle == "Paperthin")
        // The initial group comes from the capability when none was chosen yet.
        #expect(panel(style, states: [StyleCapabilityID.casebook: "absent"]).groups.first { $0.selected }?.id == "depth")
        // A path never rides to the phone.
        let encoded = try StylePanelProjection.serialise(coil)
        #expect(!(String(data: encoded, encoding: .utf8) ?? "").contains("/repo/x"))
    }

    @Test func aNonBundledStyleNamesItsSourceEverywhere() throws {
        let style = try StyleFixtures.registered(StyleFixtures.data(), source: .workspace, path: "/repo/.claude/mighty-styles/flow.json", workspacePath: "/repo")
        let made = panel(style)
        #expect(made.style.source == .workspace && made.presentation.source == .workspace)
        let text = try #require(String(data: StylePanelProjection.serialise(made), encoding: .utf8))
        #expect(text.contains("\"source\" : \"workspace\""))
    }

    @Test func theLegacyAdapterRebuildsTodaysPayloads() throws {
        let ouroboros = StyleFixtures.bundled("ouroboros")
        let seeded = panel(ouroboros, prompts: ["/ouroboros:seed"])
        let legacy = MobileLegacyStyleAdapter.payloads(style: ouroboros, panel: seeded, casebook: nil)
        let flow = try #require(legacy.ouroboros)
        #expect(legacy.paperthin == nil)
        #expect(flow.phase == "seed" && flow.ready)
        #expect(flow.takesText == ["interview", "auto", "unstuck"])
        #expect(flow.next.map(\.skill) == ["run", "evaluate", "status"])
        #expect(flow.all.count == 9 && flow.all.first?.skill == "interview" && flow.all.first?.title == "인터뷰 시작")
        #expect(flow.all.first?.help == "소크라테스식 질문으로 요구를 또렷하게 만듭니다 (모호도 0.2 이하까지)")
        // A phase-less pane still reports the entry phase, as today — and the
        // old `next` field is the phase map alone, which is empty at `goal`,
        // so an older phone falls back to `all` exactly as it used to (§7.4).
        let fresh = MobileLegacyStyleAdapter.payloads(style: ouroboros, panel: panel(ouroboros), casebook: nil).ouroboros
        #expect(fresh?.phase == "goal" && fresh?.next.isEmpty == true)
        // The new panel still offers the start rule's buttons in the same state.
        #expect(panel(ouroboros).next == ["interview", "auto"])

        let paperthin = StyleFixtures.bundled("paperthin")
        let casebook = StyleCasebook(name: "0.1.0-first", path: "/repo/.re0/iteration/0.1.0-first", files: ["DESIGN.local.md"], modifiedAt: Date())
        let map = panel(paperthin, group: "coil", states: [StyleCapabilityID.casebook: "complete"])
        let thin = try #require(MobileLegacyStyleAdapter.payloads(style: paperthin, panel: map, casebook: casebook).paperthin)
        #expect(thin.installed && thin.recommended == "re0-work" && thin.domains.count == 4)
        #expect(thin.domains.map(\.id) == ["depth", "breadth", "coil", "mesh"])
        #expect(thin.domains.map { $0.skills.count } == [19, 2, 6, 1])
        #expect(thin.domains[0].axis == "하나 \u{00B7} 지금" && thin.domains[2].question == "각 패스가 다음 패스를 가르쳤는가?")
        let re0 = try #require(thin.domains[0].skills.first)
        #expect(re0.name == "re0" && re0.emoji == "♻️" && re0.scope == "아티팩트 하나" && !re0.userInvoked && !re0.readOnly)
        #expect(thin.domains[3].skills.first?.userInvoked == true && thin.domains[3].skills.first?.readOnly == true)
        #expect(thin.casebook == MobilePaperthinCasebook(name: "0.1.0-first", weight: "full", files: ["DESIGN.local.md"]))
        // The adapter belongs to the two bundled ids and nothing else.
        let third = try StyleFixtures.registered(StyleFixtures.data())
        let none = MobileLegacyStyleAdapter.payloads(style: third, panel: panel(third), casebook: nil)
        #expect(none.ouroboros == nil && none.paperthin == nil)
    }
}
