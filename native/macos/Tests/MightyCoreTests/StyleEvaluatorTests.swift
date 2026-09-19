import Foundation
import Testing
@testable import MightyCore

struct StyleEvaluatorTests {
    /// A style with both folds, an alias, a start rule and an armed Enter.
    private func evaluator() throws -> StyleEvaluator {
        let actions = "[{\"id\":\"plan\",\"title\":\"계획\",\"help\":\"h\",\"prompt\":\"/x:plan {text}\",\"takesText\":true,\"foldText\":\"trimOnly\",\"phase\":\"one\"}," +
            "{\"id\":\"ship\",\"title\":\"출시\",\"help\":\"h\",\"prompt\":\"/x:ship {text}\",\"takesText\":true,\"foldText\":\"oneLine\",\"phase\":\"two\",\"glyph\":\"🚀\"}," +
            "{\"id\":\"note\",\"title\":\"메모\",\"help\":\"h\",\"prompt\":\"/x:note\",\"takesText\":false,\"requestTitle\":\"쪽지\"}," +
            "{\"id\":\"gstack-review\",\"title\":\"리뷰\",\"help\":\"h\",\"prompt\":\"/x:review\",\"takesText\":false,\"match\":\"review\",\"phase\":\"two\"}]"
        let rules = "{\"start\":{\"kind\":\"actions\",\"phase\":\"one\",\"actions\":[\"plan\",\"note\"],\"resetTitle\":\"새 목표\"}," +
            "\"phase\":{\"kind\":\"lastRecognisedAction\",\"default\":\"one\"}," +
            "\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[\"ship\",\"note\"],\"two\":[\"note\"]}}," +
            "\"enter\":{\"kind\":\"rewriteBareDraftTo\",\"action\":\"plan\",\"phase\":\"one\"}," +
            "\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"
        let manifest = try StyleFixtures.manifest(StyleFixtures.phased, [
            "actions": actions,
            "groups": "[{\"id\":\"g\",\"title\":\"G\",\"actions\":[\"plan\",\"ship\",\"note\",\"gstack-review\"]}]",
            "aliases": "[{\"name\":\"crystallize\",\"phase\":\"two\"}]",
            "recognition": "{\"prefixes\":[\"/x:\",\"xx \"],\"lowercase\":true}",
            "placeholders": "{\"idle\":\"i\",\"answering\":\"a\",\"initial\":\"목표를 적으세요\"}",
            "guidance": "{\"start\":\"시작\",\"next\":\"{phase} 끝\",\"running\":\"{phase} 진행 중\"}",
            "rules": rules,
        ])
        return StyleEvaluator(manifest)
    }

    @Test func promptSubstitutionCoversBothFoldsAndTheEmptyCase() throws {
        let style = try evaluator()
        #expect(style.prompt(actionId: "plan", text: "  두 줄\n둘째 ") == "/x:plan 두 줄\n둘째")
        #expect(style.prompt(actionId: "plan", text: "   ") == "/x:plan")
        #expect(style.prompt(actionId: "ship", text: " a\n\n  b \n") == "/x:ship a b")
        #expect(style.prompt(actionId: "ship", text: "") == "/x:ship")
        #expect(style.prompt(actionId: "note", text: "버려짐") == "/x:note")
    }

    @Test func recognisedAndNamesSomethingAnswerDifferentQuestions() throws {
        let style = try evaluator()
        #expect(style.recognised(inPrompt: "/x:plan 목표") == .action("plan"))
        #expect(style.recognised(inPrompt: "/x:review") == .action("gstack-review"))
        #expect(style.recognised(inPrompt: "xx crystallize") == .alias(name: "crystallize", phase: "two"))
        // The Enter rule's question: a prefix and a name, whatever it means.
        #expect(style.recognised(inPrompt: "xx 이거 해줘") == nil && style.namesSomething(inPrompt: "xx 이거 해줘"))
        #expect(!style.namesSomething(inPrompt: "xx ") && !style.namesSomething(inPrompt: "그냥 문장"))
        #expect(style.requestTitle(forInput: "/x:ship") == "🚀 출시")
        #expect(style.requestTitle(forInput: "/x:note") == "쪽지")
        #expect(style.requestTitle(forInput: "/x:plan") == "하나")
        #expect(style.requestTitle(forInput: "xx crystallize") == "둘")
        #expect(style.requestTitle(forInput: "xx 이거 해줘") == nil)
    }

    @Test func phasesStartActionsAndNextFollowTheRules() throws {
        let style = try evaluator()
        #expect(style.currentPhase(prompts: [])?.id == "one")
        #expect(style.currentPhase(prompts: ["/x:ship 배포", "/x:note", "자유 문장"])?.id == "two")
        #expect(style.currentPhase(prompts: ["/x:ship", "xx 이거"])?.id == "two")
        let one = style.manifest.phase("one"), two = style.manifest.phase("two")
        #expect(style.startActions(phase: one).map(\.id) == ["plan", "note"] && style.startActions(phase: two).isEmpty)
        #expect(style.resetTitle == "새 목표")
        #expect(style.nextActions(phase: one, group: nil).map(\.id) == ["ship", "note"])
        #expect(style.nextActions(phase: two, group: nil).map(\.id) == ["note"])
        #expect(style.nextActions(phase: nil, group: nil).isEmpty)
    }

    @Test func enterRewritesOnlyWhenAllSixConditionsHold() throws {
        let style = try evaluator()
        let one = style.manifest.phase("one"), two = style.manifest.phase("two")
        func behaviour(draft: String = "결제 모듈", phase: StylePhase? = nil, attachments: Bool = false, running: Bool = false, requests: Bool = false) -> StyleEnterBehaviour {
            style.enterBehaviour(draft: draft, phase: phase ?? one, hasAttachments: attachments, running: running, hasRequests: requests)
        }
        #expect(behaviour() == .rewrite(actionId: "plan"))
        #expect(behaviour(running: true) == .verbatim)                       // 1
        #expect(behaviour(attachments: true) == .verbatim)                   // 2
        #expect(behaviour(draft: "  /x:note") == .verbatim)                  // 3
        #expect(behaviour(draft: "xx 이거 해줘") == .verbatim)                 // 4
        #expect(behaviour(phase: two) == .verbatim)                          // 5
        #expect(behaviour(requests: true) == .verbatim)                      // 6
        #expect(style.enterArmedPrefix(phase: one, running: false, hasRequests: false) == "/x:plan")
        #expect(style.enterArmedPrefix(phase: two, running: false, hasRequests: false) == nil)
        #expect(style.enterArmedPrefix(phase: one, running: true, hasRequests: false) == nil)
        // A verbatim style never rewrites, whatever the draft is.
        let paperthin = StyleFixtures.bundled("paperthin").evaluator
        #expect(paperthin.enterBehaviour(draft: "무엇이든", phase: nil, hasAttachments: false, running: false, hasRequests: false) == .verbatim)
        #expect(paperthin.enterArmedPrefix(phase: nil, running: false, hasRequests: false) == nil)
    }

    @Test func placeholdersAndGuidanceSubstituteThePhase() throws {
        let style = try evaluator()
        let one = style.manifest.phase("one"), two = style.manifest.phase("two")
        #expect(style.placeholder(phase: one, running: false, answering: false) == "목표를 적으세요")
        #expect(style.placeholder(phase: two, running: false, answering: false) == "i")
        #expect(style.placeholder(phase: two, running: false, answering: true) == "a")
        // No `running` placeholder in this manifest: the app's own wording wins.
        #expect(style.placeholder(phase: two, running: true, answering: false) == "")
        #expect(style.guidanceLine(phase: one, running: false) == "시작")
        #expect(style.guidanceLine(phase: two, running: false) == "둘 끝")
        #expect(style.guidanceLine(phase: two, running: true) == "둘 진행 중")
        // With no phase the token and the space after it both go (§1.7).
        #expect(StyleGuidanceTemplate.render("{phase} 단계가 끝났습니다.", phaseTitle: nil) == "단계가 끝났습니다.")
        #expect(StyleGuidanceTemplate.render("{phase} 단계", phaseTitle: "시드") == "시드 단계")
        #expect(StyleFixtures.bundled("paperthin").evaluator.guidanceLine(phase: nil, running: false) == "대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.")
    }

    @Test func groupRulesReadTheCapabilityStateOnce() throws {
        let paperthin = StyleFixtures.bundled("paperthin").evaluator
        #expect(paperthin.initialGroup(capabilityStates: ["paperthin.casebook": "absent"])?.id == "depth")
        #expect(paperthin.initialGroup(capabilityStates: ["paperthin.casebook": "open"])?.id == "coil")
        #expect(paperthin.initialGroup(capabilityStates: ["paperthin.casebook": "complete"])?.id == "coil")
        let coil = paperthin.manifest.group("coil"), depth = paperthin.manifest.group("depth")
        #expect(paperthin.recommendedAction(capabilityStates: ["paperthin.casebook": "open"], group: coil) == "re0-loop")
        // The recommendation belongs to one group only.
        #expect(paperthin.recommendedAction(capabilityStates: ["paperthin.casebook": "open"], group: depth) == nil)
        #expect(paperthin.nextActions(phase: nil, group: coil).map(\.id) == ["re0-plan", "re0-loop", "re0-memo", "re0-work", "catchup", "nba"])
        #expect(paperthin.nextActions(phase: nil, group: nil).isEmpty)
    }
}
