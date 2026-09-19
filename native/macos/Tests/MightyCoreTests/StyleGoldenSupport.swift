import Foundation
import Testing
@testable import MightyCore

/// The record mode of §8.4. `MIGHTY_STYLE_GOLDEN=record swift test --filter Style`
/// overwrites the expected files and fails, so a recording is never mistaken
/// for a passing run. This file is outside the freeze allow-list on purpose.
enum StyleGolden {
    static var isRecording: Bool { ProcessInfo.processInfo.environment["MIGHTY_STYLE_GOLDEN"] == "record" }

    /// The repository root, found from this source file rather than the
    /// working directory, which `swift test` does not promise.
    static var repositoryRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // MightyCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // macos
            .deletingLastPathComponent()   // native
            .deletingLastPathComponent()   // repository root
    }
    static var stylesDirectory: URL { repositoryRoot.appendingPathComponent("styles", isDirectory: true) }
    static var goldenDirectory: URL { stylesDirectory.appendingPathComponent("golden", isDirectory: true) }

    /// The four fixed inputs of §8.4, in one file so the golden stays readable.
    struct Projection: Codable, Equatable {
        var empty: StylePanel
        var afterFirstAction: StylePanel
        var notReady: StylePanel
        var firstGroup: StylePanel
    }

    static func projection(for style: RegisteredStyle, casebookStates: [String: String] = [:]) -> Projection {
        let manifest = style.manifest
        let firstAction = manifest.actions.first
        let firstPrompt = firstAction.map { $0.prompt(text: "") } ?? ""
        let firstGroup = manifest.groups.first?.id
        let ready = StylePrerequisiteResult(ready: true)
        let missing = StylePrerequisiteResult(ready: false, missing: manifest.prerequisites.probes.first.map { [$0.missing] } ?? [],
                                              hint: manifest.prerequisites.probes.first?.hint,
                                              canInstall: manifest.install != nil)
        func make(prompts: [String], group: String?, prerequisites: StylePrerequisiteResult) -> StylePanel {
            StylePanelProjection.make(style: style, prompts: prompts, selectedGroupId: group,
                                      capabilityStates: casebookStates, attachments: [], prerequisites: prerequisites)
        }
        return Projection(empty: make(prompts: [], group: nil, prerequisites: ready),
                          afterFirstAction: make(prompts: [firstPrompt], group: nil, prerequisites: ready),
                          notReady: make(prompts: [], group: nil, prerequisites: missing),
                          firstGroup: make(prompts: [], group: firstGroup, prerequisites: ready))
    }

    static func serialise(_ projection: Projection) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        var data = try encoder.encode(projection)
        data.append(0x0A)
        return data
    }

    /// Compares, or records and fails so the run is repeated on the new bytes.
    static func check(_ projection: Projection, id: String, sourceLocation: SourceLocation = #_sourceLocation) throws {
        let url = goldenDirectory.appendingPathComponent(id + ".panel.json")
        let produced = try serialise(projection)
        if isRecording {
            try FileManager.default.createDirectory(at: goldenDirectory, withIntermediateDirectories: true)
            try produced.write(to: url)
            Issue.record("골든을 기록했습니다: \(url.path). MIGHTY_STYLE_GOLDEN 없이 다시 실행하세요.", sourceLocation: sourceLocation)
            return
        }
        guard let expected = try? Data(contentsOf: url) else {
            Issue.record("골든이 없습니다: \(url.path). MIGHTY_STYLE_GOLDEN=record로 기록하세요.", sourceLocation: sourceLocation)
            return
        }
        guard produced == expected else {
            Issue.record("골든과 다릅니다: \(url.path)", sourceLocation: sourceLocation)
            return
        }
    }
}
