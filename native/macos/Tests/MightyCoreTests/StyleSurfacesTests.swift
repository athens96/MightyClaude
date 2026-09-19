import Foundation
import Testing
@testable import MightyCore

/// The Mac's guided surfaces, asserted here because the app module has no
/// test target of its own (docs/mighty-styles.md §6).
struct StyleSurfacesTests {
    @Test func theAppWritesTheTailOfEveryTitleItDraws() throws {
        #expect(StyleChrome.requestTitle(prefix: "시드", ordinal: 3, providerLabel: "Claude") == "시드 \u{00B7} 요청 3 \u{00B7} Claude")
        // No style, or no recognised name: the app's own tail stands alone.
        #expect(StyleChrome.requestTitle(prefix: nil, ordinal: 1, providerLabel: "Claude") == "요청 1 \u{00B7} Claude")
        #expect(StyleChrome.requestTitle(prefix: "", ordinal: 1, providerLabel: "Claude") == "요청 1 \u{00B7} Claude")
        #expect(StyleChrome.graphHeader(styleName: nil, phaseTitle: nil) == "마이티")
        #expect(StyleChrome.graphHeader(styleName: "Paperthin", phaseTitle: nil) == "마이티 \u{00B7} Paperthin")
        #expect(StyleChrome.graphHeader(styleName: "Ouroboros", phaseTitle: "시드") == "마이티 \u{00B7} Ouroboros \u{00B7} 시드")
        #expect(StyleChrome.installPaneTitle("Ouroboros 설치", styleName: "Ouroboros") == "Ouroboros 설치 \u{00B7} Ouroboros")
        #expect(StyleChrome.sourceBadge(.bundled) == nil)
        #expect(StyleChrome.sourceBadge(.user) == "사용자 등록" && StyleChrome.sourceBadge(.workspace) == "저장소에서 발견됨")
        #expect(StyleChrome.hashPrefix(String(repeating: "a", count: 64)) == String(repeating: "a", count: 12))
    }

    @Test func theChipRowFollowsTheStartAndNextRules() throws {
        let ouroboros = StyleFixtures.bundled("ouroboros")
        let evaluator = ouroboros.evaluator
        let goal = evaluator.currentPhase(prompts: [])
        let start = StyleChips.make(evaluator, phase: goal, group: nil, startingNew: false)
        // The entry phase draws the start rule's row, first chip prominent.
        #expect(start.actions.map(\.id) == ["interview", "auto"] && start.prominentId == "interview")
        #expect(start.reset == .none && start.phaseId == "goal")

        let seed = evaluator.currentPhase(prompts: ["/ouroboros:seed"])
        let next = StyleChips.make(evaluator, phase: seed, group: nil, startingNew: false)
        #expect(next.actions.map(\.id) == ["run", "evaluate", "status"] && next.prominentId == "run")
        // Away from the entry phase the reset chip appears at the row's end.
        #expect(next.reset == .reset("새 목표") && next.phaseId == "seed")

        // Pressing it shows the start row again, with a cancel chip instead.
        let restarted = StyleChips.make(evaluator, phase: seed, group: nil, startingNew: true)
        #expect(restarted.actions.map(\.id) == ["interview", "auto"] && restarted.reset == .cancel && restarted.phaseId == "goal")

        // `byGroup` has no prominent chip; the recommendation is the emphasis.
        let paperthin = StyleFixtures.bundled("paperthin")
        let coil = paperthin.manifest.group("coil")
        let group = StyleChips.make(paperthin.evaluator, phase: nil, group: coil, startingNew: false,
                                    capabilityStates: [StyleCapabilityID.casebook: "open"])
        #expect(group.actions.map(\.id) == ["re0-plan", "re0-loop", "re0-memo", "re0-work", "catchup", "nba"])
        #expect(group.prominentId == nil && group.recommendedId == "re0-loop" && group.reset == .none)
        #expect(StyleChips.make(paperthin.evaluator, phase: nil, group: paperthin.manifest.group("depth"), startingNew: false,
                                capabilityStates: [StyleCapabilityID.casebook: "open"]).recommendedId == nil)
    }

    @Test func aChipsTooltipNamesItsScopeAndItsFlags() throws {
        let paperthin = StyleFixtures.bundled("paperthin")
        let hate = try #require(paperthin.manifest.action("hate"))
        #expect(StyleChips.help(hate) == "친절하기를 거부합니다. 계획을 죽일 수 있는 반론 하나와 가장 싼 테스트를 냅니다 \u{00B7} 범위: 계획 하나 \u{00B7} 사람만 부를 수 있는 스킬")
        let nba = try #require(paperthin.manifest.action("nba"))
        #expect(StyleChips.help(nba).hasSuffix("범위: 현재 사이클 \u{00B7} 읽기 전용"))
        // No scope and no flags: the help line stands by itself, as today.
        let seed = try #require(StyleFixtures.bundled("ouroboros").manifest.action("seed"))
        #expect(StyleChips.help(seed) == "인터뷰 결과를 불변 명세(시드)로 굳힙니다")
    }

    @Test func thePickerShowsPendingStylesWithoutLettingThemBeChosen() throws {
        let bundled = StyleFixtures.bundled("ouroboros")
        let pending = try StyleFixtures.registered(StyleFixtures.data(), source: .workspace,
                                                   path: "/repo/.claude/mighty-styles/flow.json", workspacePath: "/repo", approval: .pending)
        let revoked = try StyleFixtures.registered(StyleFixtures.data(StyleFixtures.flat, ["id": "\"blocked\"", "name": "\"Blocked\""]),
                                                   source: .user, path: "/data/styles/blocked.json", approval: .revoked)
        let rows = StyleMenu.rows([pending, revoked, bundled])
        #expect(rows.map(\.id) == ["cli", "ouroboros", "blocked", "flow"])
        #expect(rows[0].label == "CLI" && rows[0].selectable && rows[0].source == nil && rows[0].badge == nil)
        #expect(rows[1].selectable && !rows[1].opensApproval && rows[1].badge == nil && rows[1].detail == nil)
        let repo = rows[3]
        #expect(!repo.selectable && repo.opensApproval && repo.badge == "저장소에서 발견됨")
        #expect(repo.detail == "확인 필요 \u{00B7} 행동 1 \u{00B7} 자동 허용 0")
        // A refused place stays refused: the card does not even open (§4.2).
        #expect(!rows[2].selectable && !rows[2].opensApproval && rows[2].detail?.hasPrefix("차단됨") == true)
    }

    @Test func settingsRowsOfferOnlyTheDecisionsTheStoreCanWrite() throws {
        let bundled = StyleFixtures.bundled("paperthin")
        let approved = try StyleFixtures.registered(StyleFixtures.data(), source: .user, path: "/data/styles/flow.json")
        let pending = try StyleFixtures.registered(StyleFixtures.data(StyleFixtures.flat, ["id": "\"repo\"", "name": "\"Repo\""]),
                                                   source: .workspace, path: "/repo/.claude/mighty-styles/repo.json",
                                                   workspacePath: "/repo", approval: .pending)
        let rows = StyleSettingsList.rows([pending, approved, bundled], locked: false)
        #expect(rows.map(\.styleId) == ["paperthin", "flow", "repo"])
        #expect(rows[0].stateLabel == "내장" && !rows[0].canApprove && !rows[0].canRevoke && !rows[0].canRemove)
        #expect(rows[1].stateLabel == "허용됨" && rows[1].canRevoke && rows[1].canRemove && !rows[1].canApprove)
        #expect(rows[2].stateLabel == "확인 필요" && rows[2].canApprove && !rows[2].canRemove && rows[2].badge == "저장소에서 발견됨")
        #expect(rows[1].hashPrefix.count == 12 && rows[1].path == "/data/styles/flow.json")
        // A locked store answers no decision at all, so nothing is offered.
        let locked = StyleSettingsList.rows([pending, approved, bundled], locked: true)
        #expect(locked.allSatisfy { !$0.canApprove && !$0.canRevoke && !$0.canAllowAgain && !$0.canRemove })
        #expect(StyleSettingsList.lockedMessage("/data/style-trust/approvals.json").hasSuffix("approvals.json"))
        #expect(StyleSettingsList.stateLabel(.revoked) == "차단됨")
    }

    @Test func theApprovalCardOpensWithTheAutoAllowListInRiskOrder() throws {
        let ouroboros = StyleFixtures.bundled("ouroboros")
        let sections = StyleApprovalCard.sections(ouroboros)
        #expect(sections.map(\.id) == ["origin", "autoAllow", "install", "enter", "identity", "structure", "rules", "actions", "presentation"])
        // The riskiest block is second and never folds away (§4.4).
        let auto = sections[1]
        #expect(auto.title == "자동 허용" && !auto.foldable && auto.lines.count == 17)
        #expect(auto.lines.first == "ToolSearch" && auto.lines.contains("mcp__plugin_ouroboros_ouroboros__ouroboros_interview"))
        #expect(StyleApprovalCard.requiresSecondConfirmation(ouroboros))
        #expect(StyleApprovalCard.secondConfirmation(count: 17) == "이 스타일은 도구 17개를 권한 창 없이 실행할 수 있게 됩니다.")
        #expect(sections[2].lines.first?.hasPrefix("claude plugin marketplace") == true)
        #expect(sections[2].lines.last == StyleApprovalCard.installNotice)
        // The first Enter's bytes are quoted, not summarised.
        #expect(sections[3].lines.last == "/ouroboros:interview {text}")
        #expect(sections[3].lines.first?.contains("목표") == true)
        // Every action is listed whole, with its prompt template verbatim.
        #expect(sections[7].title == "행동 9개" && sections[7].lines.contains("/ouroboros:unstuck {text}"))
        #expect(sections[8].lines.first == "아이콘 point.3.connected.trianglepath.dotted")

        let paperthin = StyleApprovalCard.sections(StyleFixtures.bundled("paperthin"))
        #expect(paperthin[1].lines == ["자동 허용 없음"] && !StyleApprovalCard.requiresSecondConfirmation(StyleFixtures.bundled("paperthin")))
        #expect(paperthin[3].lines == ["입력창의 글을 그대로 요청으로 보냅니다."])
        #expect(paperthin.first { $0.id == "rules" }?.lines.contains("다음 \u{00B7} 고른 그룹의 행동") == true)

        // A workspace style names its clone as well as its file.
        let repo = try StyleFixtures.registered(StyleFixtures.data(), source: .workspace,
                                                path: "/repo/.claude/mighty-styles/flow.json", workspacePath: "/repo", approval: .pending)
        let origin = StyleApprovalCard.sections(repo)[0]
        #expect(origin.lines == ["저장소에서 발견됨", "워크스페이스 /repo", "/repo/.claude/mighty-styles/flow.json", "해시 " + StyleChrome.hashPrefix(repo.hash)])
        // No install block at all when the manifest declares no command.
        #expect(!StyleApprovalCard.sections(repo).contains { $0.id == "install" })
    }
}
