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

    @Test func styleLaunchWiringBlocksTitlePrefixForOrdinaryRunWindows() {
        let prefix = StyleLaunchWiring.requestTitlePrefix(guidedStyle: nil)
        let title = StyleChrome.requestTitle(prefix: prefix, ordinal: 1, providerLabel: "Claude")
        #expect(prefix == nil)
        #expect(title == "요청 1 " + StyleChrome.separator + " Claude")
    }
}
