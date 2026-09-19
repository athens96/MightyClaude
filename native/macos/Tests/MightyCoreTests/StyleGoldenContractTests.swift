import Foundation
import Testing
@testable import MightyCore

/// Frozen before the tag and living outside the allow-list, so adding a
/// manifest to `styles/` is by itself enough to make frozen code demand and
/// check its golden (§8.4). Zero manifests is a valid state today.
struct StyleGoldenContractTests {
    private func manifests() throws -> [URL] {
        // A wrong `stylesDirectory` and an empty one look identical from a
        // glob, and the empty answer is the one that passes: say so loudly
        // rather than let the whole gate become a no-op (§8.4).
        var isDirectory: ObjCBool = false
        let path = StyleGolden.stylesDirectory.path
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else {
            Issue.record("styles 폴더를 찾지 못했습니다: \(path). StyleGolden.repositoryRoot를 확인하세요.")
            return []
        }
        let found = try FileManager.default.contentsOfDirectory(at: StyleGolden.stylesDirectory,
                                                                includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
        return found.filter { $0.pathExtension == "json" }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    @Test func everyManifestInTheRepositoryDecodesAndMatchesItsGolden() throws {
        // The goldens the bundled pair records live here too, so the directory
        // is never empty and the glob is never silently vacuous.
        #expect(FileManager.default.fileExists(atPath: StyleGolden.goldenDirectory.path))
        for url in try manifests() {
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
            try StyleGolden.check(StyleGolden.projection(for: style, casebookStates: [StyleCapabilityID.casebook: "absent"]), id: id)
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
