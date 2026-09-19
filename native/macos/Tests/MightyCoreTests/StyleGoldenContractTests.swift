import Foundation
import Testing
@testable import MightyCore

/// Frozen before the tag and living outside the allow-list, so adding a
/// manifest to `styles/` is by itself enough to make frozen code demand and
/// check its golden (§8.4). Zero manifests is a valid state today.
struct StyleGoldenContractTests {
    private func manifests() -> [URL] {
        let found = (try? FileManager.default.contentsOfDirectory(at: StyleGolden.stylesDirectory,
                                                                  includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
        return found.filter { $0.pathExtension == "json" }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    @Test func everyManifestInTheRepositoryDecodesAndMatchesItsGolden() throws {
        for url in manifests() {
            let data = try Data(contentsOf: url)
            let manifest = try StyleManifestDecoder.decode(data, source: .user)
            #expect(url.lastPathComponent == manifest.id + ".json")
            let style = RegisteredStyle(manifest: manifest, source: .user, path: url.path, workspacePath: nil,
                                        hash: StyleHash.of(data), approval: .approved)
            try StyleGolden.check(StyleGolden.projection(for: style), id: manifest.id)
        }
    }

    /// The two bundled styles are the phone lane's real fixtures, recorded the
    /// same way a third-party style will be.
    @Test func theBundledStylesHaveGoldens() throws {
        for id in ["ouroboros", "paperthin"] {
            let style = StyleFixtures.bundled(id)
            let states = StyleCapabilityID.all.isEmpty ? [:] : [StyleCapabilityID.casebook: "absent"]
            try StyleGolden.check(StyleGolden.projection(for: style, casebookStates: states), id: id)
        }
    }

    @Test func theGoldenSerialisationRulesAreFixed() throws {
        let style = StyleFixtures.bundled("paperthin")
        let panel = StylePanelProjection.make(style: style, prompts: [], selectedGroupId: nil,
                                              capabilityStates: [StyleCapabilityID.casebook: "absent"],
                                              attachments: [], prerequisites: StylePrerequisiteResult(ready: true))
        let data = try StylePanelProjection.serialise(panel)
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.hasSuffix("}\n") && !text.hasSuffix("}\n\n"))
        #expect(!text.contains("\\/") && text.contains("npx skills@latest"))
        // Keys sorted, two-space indent.
        #expect(text.hasPrefix("{\n  \"actions\" : ["))
        #expect(try StylePanelProjection.serialise(panel) == data)
        let decoded = try JSONDecoder().decode(StylePanel.self, from: data)
        #expect(decoded == panel)
    }
}
