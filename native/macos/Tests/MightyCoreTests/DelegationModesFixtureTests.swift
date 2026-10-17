import Foundation
import Testing
@testable import MightyCore

/// The file the Windows Core checks read too (`DelegationCrossAgentVerification`):
/// what each agent's mode may do, and the mode a child of each agent starts in
/// under each parent agent's mode. The Mac is the reference: these tables were
/// written from its rules, and its rules must still give them.
@Suite(.delegationLane) struct DelegationModesFixtureTests {
    struct Fixture: Decodable {
        let agents: [String]
        let capabilities: [String: [String: [String]]]
        let widestChildMode: [String: [String: [String: String?]]]
    }

    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/delegation-modes.json")
    }()

    static func load() throws -> Fixture { try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url)) }

    @Test func everyAgentsModesHaveTheFixturesCapabilities() throws {
        let fixture = try Self.load()
        #expect(fixture.agents == ProviderOptions.ids)
        #expect(Set(fixture.capabilities.keys) == Set(ProviderOptions.ids))
        for (provider, modes) in fixture.capabilities {
            #expect(Set(modes.keys) == Set(DelegationProviders.startingModes(provider: provider)), "\(provider)")
            for (mode, names) in modes {
                let actual = Set(DelegationProviders.capabilities(provider: provider, mode: mode).map { String(describing: $0) })
                #expect(actual == Set(names), "\(provider) \(mode)")
            }
        }
    }

    @Test func everyParentModesWidestChildModeIsTheFixtures() throws {
        let fixture = try Self.load()
        #expect(Set(fixture.widestChildMode.keys) == Set(ProviderOptions.ids))
        for (parent, modes) in fixture.widestChildMode {
            #expect(Set(modes.keys) == Set(DelegationProviders.startingModes(provider: parent)), "\(parent)")
            for (parentMode, children) in modes {
                #expect(Set(children.keys) == Set(ProviderOptions.ids), "\(parent) \(parentMode)")
                for (child, mode) in children {
                    #expect(DelegationProviders.widestChildMode(child: child, parent: parent, parentMode: parentMode) == mode, "\(child) under \(parent) \(parentMode)")
                }
            }
        }
    }
}
