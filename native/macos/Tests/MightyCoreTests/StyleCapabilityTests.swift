import Foundation
import Testing
@testable import MightyCore

/// Built-in features (§1.8). The app's vocabulary is empty since v7, so every
/// declared name is unknown; the field and the two rules that name a feature
/// stay in the schema and are checked against a test vocabulary here.
struct StyleCapabilityTests {
    private let f = StyleFixtures.self

    private func rules(recommend: String = "{\"kind\":\"none\"}", initialGroup: String = "{\"kind\":\"fixed\",\"group\":\"g\"}") -> String {
        "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"none\"},\"next\":{\"kind\":\"byGroup\"},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":\(recommend),\"initialGroup\":\(initialGroup)}"
    }

    @Test func theAppKnowsNoFeatureAndRefusesEveryDeclaredName() {
        #expect(StyleCapabilityID.vocabulary.isEmpty && StyleCapabilityID.all.isEmpty)
        for name in ["acme.thing", "a"] {
            #expect(f.code(f.data(f.flat, ["capabilities": "[\"\(name)\"]"])) == "E_UNKNOWN_CAPABILITY")
            // A bundled manifest is held to the same vocabulary.
            #expect(f.code(f.data(f.flat, ["capabilities": "[\"\(name)\"]"]), source: .bundled) == "E_UNKNOWN_CAPABILITY")
        }
        // Declaring nothing is still the valid, ordinary case.
        #expect(f.code(f.data(f.flat, ["capabilities": "[]"])) == nil)
        // Every bundled style declares none.
        for style in BundledStyles.shared.styles() { #expect(style.manifest.capabilities.isEmpty, "\(style.id)") }
    }

    @Test func aRuleThatNamesAFeatureIsRefusedInAFixedOrder() {
        let recommend = "{\"kind\":\"capability\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"go\",\"open\":\"go\",\"complete\":\"go\"}}"
        let initial = "{\"kind\":\"capabilityState\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"g\",\"open\":\"g\",\"complete\":\"g\"}}"
        for undeclared in [["rules": rules(recommend: recommend)], ["rules": rules(initialGroup: initial)]] {
            // Not declared: refused for that, whatever the vocabulary.
            #expect(f.code(f.data(f.flat, undeclared)) == "E_CAPABILITY_UNDECLARED")
            #expect(f.code(f.data(f.flat, undeclared), capabilities: f.testCapabilities) == "E_CAPABILITY_UNDECLARED")
            // Declared: the vocabulary is read first, so the app refuses the
            // name and a vocabulary that knows it accepts the whole map.
            var declared = undeclared
            declared["capabilities"] = "[\"acme.thing\"]"
            #expect(f.code(f.data(f.flat, declared)) == "E_UNKNOWN_CAPABILITY")
            #expect(f.code(f.data(f.flat, declared), capabilities: f.testCapabilities) == nil)
        }
        // A map must cover every state and name nothing else.
        let partial = "{\"kind\":\"capability\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"go\",\"open\":\"go\"}}"
        let extra = "{\"kind\":\"capability\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"go\",\"open\":\"go\",\"complete\":\"go\",\"gone\":\"go\"}}"
        let declared = "[\"acme.thing\"]"
        #expect(f.code(f.data(f.flat, ["capabilities": declared, "rules": rules(recommend: partial)]), capabilities: f.testCapabilities) == "E_CAPABILITY_MAP")
        #expect(f.code(f.data(f.flat, ["capabilities": declared, "rules": rules(recommend: extra)]), capabilities: f.testCapabilities) == "E_UNKNOWN_REFERENCE")
    }

    /// The evaluator reads a feature's state as it always did, so a feature
    /// added later recommends and seeds a group without engine work.
    @Test func aKnownFeatureStillDrivesTheRecommendationAndTheFirstGroup() throws {
        let style = StyleFixtures.catalogueStyle
        let evaluator = style.evaluator
        #expect(evaluator.recommendedAction(capabilityStates: ["acme.thing": "complete"]) == "work")
        #expect(evaluator.recommendedAction(capabilityStates: [:]) == nil)
        #expect(evaluator.initialGroup(capabilityStates: ["acme.thing": "open"])?.id == "coil")
        // Not read yet: the first group, not a guess.
        #expect(evaluator.initialGroup(capabilityStates: [:])?.id == "depth")
        // The phone has no group of its own and production passes none, so
        // the recommendation has to survive that (§7.4).
        let panel = StylePanelProjection.make(style: style, prompts: [], selectedGroupId: nil,
                                              capabilityStates: ["acme.thing": "complete"], prerequisites: StylePrerequisiteResult(ready: true))
        #expect(panel.recommended == "work" && panel.groups.first { $0.selected }?.id == "coil")
        // Nothing offers a chip any more, but the field is still sent, empty,
        // for a phone that predates v7.
        #expect(panel.attachments.isEmpty)
        let wire = try #require(try JSONSerialization.jsonObject(with: StylePanelProjection.serialise(panel)) as? [String: Any])
        #expect((wire["attachments"] as? [Any])?.isEmpty == true)
    }
}
