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

    /// Every golden belongs to something that is compared: a repository
    /// manifest of `styles/*.json`, a bundled style or a fixture of
    /// `styles/fixtures/` — no golden is left that nothing produces.
    @Test func noGoldenIsLeftUnused() throws {
        let goldens = try FileManager.default.contentsOfDirectory(at: StyleGolden.goldenDirectory,
                                                                  includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
            .map(\.lastPathComponent).filter { $0.hasSuffix(".panel.json") }
        let fixtures = try FileManager.default.contentsOfDirectory(at: StyleGolden.fixturesDirectory,
                                                                   includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
            .filter { $0.pathExtension == "json" }
        let repository = try manifests().map { try StyleManifestDecoder.decode(Data(contentsOf: $0), source: .user).id }
        let bundled = BundledStyles.shared.styles().map(\.manifest.id)
        let fixtureIds = try fixtures.map { try StyleFixtures.fixtureStyle($0.deletingPathExtension().lastPathComponent).manifest.id }
        #expect(!bundled.isEmpty && !fixtureIds.isEmpty && !repository.isEmpty)
        #expect(Set(goldens) == Set((repository + bundled + fixtureIds).map { $0 + ".panel.json" }))
    }

    /// The bundled styles are the phone lane's real fixtures, recorded the
    /// same way a third-party style will be. (`claude-plan` has its own test
    /// below.)
    @Test func theBundledStylesHaveGoldens() throws {
        let style = StyleFixtures.bundled("ouroboros")
        let projection = StyleGolden.projection(for: style)
        // No state source, no plan stage: those cases are not recorded at all.
        #expect(projection.withState == nil && projection.planStages == nil)
        try StyleGolden.check(projection, id: "ouroboros")
    }

    /// The test-only manifests of `styles/fixtures/` (§8.4) are recorded and
    /// compared whole like the bundled styles, so what no bundled style
    /// exercises since v7 — state sources, a group map, an action with both
    /// flags — stays in the cross-platform goldens Windows Core and the phone
    /// read too. Each is decoded as an approved user style under
    /// `StyleFixtures.testCapabilities`, the only vocabulary `deck` decodes
    /// under; Windows Core loads them the same way. The fixed inputs read no
    /// feature state, so `deck`'s golden records its first group as the
    /// fallback and no recommendation: the feature-chosen first group and the
    /// recommendation are the engine tests' (`StyleEvaluatorTests`
    /// `groupRulesReadTheCapabilityStateOnce`, `StyleCapabilityTests`
    /// `aKnownFeatureStillDrivesTheRecommendationAndTheFirstGroup`,
    /// `StyleProjectionTests.groupsAndTheRecommendationTravel`).
    @Test func theFixtureManifestsHaveGoldens() throws {
        let found = try FileManager.default.contentsOfDirectory(at: StyleGolden.fixturesDirectory,
                                                                includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
        let names = found.filter { $0.pathExtension == "json" }.map(\.lastPathComponent).sorted()
        #expect(names == ["deck.json", "loop.json"])
        var projections: [StyleGolden.Projection] = []
        for name in names {
            let id = String(name.dropLast(".json".count))
            let style = try StyleFixtures.fixtureStyle(id)
            #expect(style.manifest.id == id)
            let projection = StyleGolden.projection(for: style)
            projections.append(projection)
            try StyleGolden.check(projection, id: id)
        }
        // `lastGroup` is not `firstGroup` again for at least one golden, so
        // the `byGroup` input is not vacuous.
        #expect(projections.contains { $0.lastGroup != $0.firstGroup })
        let deck = StyleGolden.projection(for: try StyleFixtures.catalogueStyle)
        #expect(deck.firstGroup.next == ["sweep", "probe"] && deck.lastGroup.next == ["draft", "cycle", "note", "reset", "peek"])
        #expect(deck.empty.actions.first { $0.id == "peek" }?.flags == ["userInvoked", "readOnly"])
    }

    /// §1.16: a style with file and run-event sources records what the engine
    /// makes of its own declarations — the plan file as a bar and the
    /// `subagent.start` count as a label — and the file state moves it to the
    /// phase a current plan means. No bundled or repository style declares
    /// either kind since v7, so the `loop` fixture stands in; its golden is
    /// compared in `theFixtureManifestsHaveGoldens`.
    @Test func aStyleWithFileAndRunEventSourcesRecordsItsReading() throws {
        let projection = StyleGolden.projection(for: try StyleFixtures.statefulStyle)
        let withState = try #require(projection.withState)
        #expect(withState.widgets == [.progressBar(value: 3, total: 7), .label(text: "서브에이전트 2회 시작")])
        #expect(withState.phase?.id == "build")
        #expect(projection.empty.phase?.id == "sketch" && projection.empty.widgets == nil)
        #expect(projection.planStages == nil)
        // The case survives its own serialisation, widgets and all.
        let decoded = try JSONDecoder().decode(StyleGolden.Projection.self, from: StyleGolden.serialise(projection))
        #expect(decoded == projection)
    }

    /// §1.17 (v6): the plan-mode style records its panel at every plan stage
    /// and, executing, the checklist bar with its current step and the
    /// background work — the same file the phone lane reads.
    @Test func theClaudePlanStyleRecordsEveryPlanStage() throws {
        let style = StyleFixtures.bundled("claude-plan")
        let projection = StyleGolden.projection(for: style)
        let stages = try #require(projection.planStages)
        #expect(stages[.planning]?.phase?.id == "plan")
        #expect(stages[.awaitingApproval]?.phase?.id == "approve" && stages[.awaitingApproval]?.next == [])
        #expect(stages[.executing]?.phase?.id == "execute")
        // A new plan does not show the last one's progress.
        #expect(stages[.planning]?.widgets?.first == .progressBar(value: 0, total: 0))
        let withState = try #require(projection.withState)
        #expect(withState.phase?.id == "execute")
        #expect(withState.widgets?.prefix(3) == [.progressBar(value: 3, total: 7), .label(text: "진행 중: doing step 4"),
                                                 .label(text: "턴 완료 · 백그라운드 1개 실행 중")])
        #expect(withState.next == ["new-plan", "verify"])
        #expect(projection.empty.phase?.id == "plan" && projection.empty.next == ["new-plan"])
        try StyleGolden.check(projection, id: "claude-plan")
    }

    @Test func theGoldenSerialisationRulesAreFixed() throws {
        let style = StyleFixtures.bundled("ouroboros")
        let panel = StylePanelProjection.make(style: style, prompts: [], selectedGroupId: nil,
                                              capabilityStates: [:], prerequisites: StylePrerequisiteResult(ready: true))
        let data = try StylePanelProjection.serialise(panel)
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.hasSuffix("}\n") && !text.hasSuffix("}\n\n"))
        #expect(!text.contains("\\/") && text.contains("add Q00/ouroboros"))
        // Keys sorted, two-space indent.
        #expect(text.hasPrefix("{\n  \"actions\" : ["))
        #expect(try StylePanelProjection.serialise(panel) == data)
        let decoded = try JSONDecoder().decode(StylePanel.self, from: data)
        #expect(decoded == panel)
    }
}
