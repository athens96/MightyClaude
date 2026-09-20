import Foundation
import Testing
@testable import MightyCore

struct StylesV3BundleTests {
    @Test func ouroborosDeclaresSixPhasesInOrder() {
        #expect(StyleFixtures.bundled("ouroboros").manifest.phases.map(\.id) == ["goal", "interview", "seed", "run", "evaluate", "evolve"])
    }

    @Test func hundredAndFirstActionIsRejectedWithELimit() {
        let actions = "[" + (0..<101).map { "{\"id\":\"a\($0)\",\"title\":\"A\($0)\",\"help\":\"h\",\"prompt\":\"/a\($0)\",\"takesText\":false}" }.joined(separator: ",") + "]"
        #expect(StyleFixtures.code(StyleFixtures.data(StyleFixtures.flat, ["actions": actions])) == "E_LIMIT")
    }

    @Test func styleLaunchWiringBindsRunWindowAfterApproval() throws {
        let approved = try StyleFixtures.registered(StyleFixtures.data(), approval: .approved)
        let pending = try StyleFixtures.registered(StyleFixtures.data(), approval: .pending)
        #expect(StyleLaunchWiring.canBindRunWindow(to: approved) == true)
        #expect(StyleLaunchWiring.canBindRunWindow(to: pending) == false)
    }

    @Test func styleLaunchWiringDoesNotCopyWorkspaceManifests() {
        #expect(StyleLaunchWiring.shouldCopyOnApproval(source: .user) == true)
        #expect(StyleLaunchWiring.shouldCopyOnApproval(source: .workspace) == false)
        #expect(StyleLaunchWiring.shouldCopyOnApproval(source: .bundled) == false)
    }

    @Test func styleLaunchWiringBlocksTitlePrefixForOrdinaryRunWindows() throws {
        let ouroboros = StyleFixtures.bundled("ouroboros")
        let input = try #require(ouroboros.manifest.actions.first?.prompt)
        let title = try #require(ouroboros.evaluator.requestTitle(forInput: input))

        // Ordinary run window: the style is runnable and recognises the input, yet no prefix appears.
        let ordinary = StyleLaunchWiring.requestTitles(guidedStyle: nil, runnable: [ouroboros])
        #expect(ordinary.prefix(input) == nil)
        #expect(StyleChrome.requestTitle(prefix: ordinary.prefix(input), ordinal: 1, providerLabel: "Claude")
                == "요청 1 " + StyleChrome.separator + " Claude")

        // Guided run window: the same input carries the style's own title.
        let guided = StyleLaunchWiring.requestTitles(guidedStyle: ouroboros, runnable: [ouroboros])
        #expect(guided.prefix(input) == title)
    }
}
