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

    /// The six fixed inputs of §8.4, in one file so the golden stays readable.
    /// `lastGroup` and `capabilityOpen` exist because the first four collapse
    /// onto the default group and never carry a recommendation or an
    /// attachment: without them the `byGroup` rule for a non-default group,
    /// the whole recommend rule and attachment projection go unrecorded.
    struct Projection: Codable, Equatable {
        var empty: StylePanel
        var afterFirstAction: StylePanel
        var notReady: StylePanel
        var firstGroup: StylePanel
        var lastGroup: StylePanel
        var capabilityOpen: StylePanel
        /// §1.16: only for a style that declares state sources, so every other
        /// golden keeps its bytes. The panel carries the reading's widgets as
        /// they are; see `stateReading(for:)` for what is recorded.
        var withState: StylePanel?
        /// §1.17 (v6): only for a style that reads the plan stage — the panel
        /// at each stage, with `stateRunState` read at that stage (a plan
        /// waiting for its answer is a running turn). Three top-level cases
        /// rather than one map, so every case in a golden is a panel.
        var planStagePlanning: StylePanel?
        var planStageAwaitingApproval: StylePanel?
        var planStageExecuting: StylePanel?

        /// The three stage cases by stage, for the readable assertions.
        var planStages: [StylePlanStage: StylePanel]? {
            guard let planStagePlanning, let planStageAwaitingApproval, let planStageExecuting else { return nil }
            return [.planning: planStagePlanning, .awaitingApproval: planStageAwaitingApproval, .executing: planStageExecuting]
        }
    }

    /// §1.17: the pane's fixed plan-mode state — three of seven steps done
    /// with the fourth under way, and two background tasks, one still running
    /// after the turn ended.
    static func stateRunState(_ stage: StylePlanStage) -> StyleRunStateInput {
        let items = (1...7).map { index in
            TodoItem(content: "step \(index)", activeForm: "doing step \(index)", status: index <= 3 ? "completed" : index == 4 ? "in_progress" : "pending")
        }
        let work = BackgroundWork(tasks: [
            BackgroundTask(id: "bg-1", kind: "agent", description: "review the diff", startedAt: "2026-10-06T01:00:00.000Z"),
            BackgroundTask(id: "bg-2", kind: "shell", description: "npm test", startedAt: "2026-10-06T01:00:05.000Z",
                           status: "completed", endedAt: "2026-10-06T01:01:10.000Z"),
        ], turnEnded: true)
        return StyleRunStateInput(planStage: stage, todos: TodoProgress(items: items), background: work)
    }

    /// A plan three of seven items done, read by the engine's own checklist parser.
    static let stateChecklist = "- [x] one\n- [x] two\n- [x] three\n- [ ] four\n- [ ] five\n- [ ] six\n- [ ] seven\n"

    /// The pane's fixed events: two sub-agents started and one ordinary tool
    /// call, so a `count` of `subagent.start` reads 2 and a `tool.call` 1.
    static let stateRunEvents = [
        StyleStateEngine.RunEventRecord(event: .subagentStart, value: "plan review"),
        StyleStateEngine.RunEventRecord(event: .toolCall, value: "Read"),
        StyleStateEngine.RunEventRecord(event: .subagentStart, value: "task 1"),
    ]

    /// The `withState` case, produced by the real engine: every declared file
    /// source parses `stateChecklist` as if it were the current file, and the
    /// run-event sources aggregate `stateRunEvents`. A label's text comes from
    /// the app's locale, so the reading is made in Korean — the copy the
    /// golden records and the phone test reads — whatever the machine runs.
    static func stateReading(for manifest: StyleManifest, stage: StylePlanStage = .executing) -> StyleStateReading? {
        guard let sources = manifest.stateSources else { return nil }
        let files = sources.files.map { StyleStateEngine.parse(stateChecklist, source: $0) }
        // §1.17: a style that reads the plan stage records it at `executing`.
        let runState = manifest.readsPlanState ? stateRunState(stage) : nil
        return withKoreanLocale {
            StyleStateEngine.reading(sources: sources, files: files, runEvents: stateRunEvents, runState: runState)
        }
    }

    /// §1.17: one panel per plan stage, for a style that reads it.
    static func planStageReadings(for manifest: StyleManifest) -> [StylePlanStage: StyleStateReading]? {
        guard manifest.readsPlanState else { return nil }
        var readings: [StylePlanStage: StyleStateReading] = [:]
        for stage in StylePlanStage.allCases {
            readings[stage] = stateReading(for: manifest, stage: stage) ?? StyleStateReading(planStage: stage)
        }
        return readings
    }

    /// Korean for this task only: the process-wide preference stays untouched,
    /// so suites that switch it in parallel (LocalizationTests) cannot race it.
    static func withKoreanLocale<T>(_ body: () throws -> T) rethrows -> T {
        try LocaleOverride.$language.withValue(.ko) { try body() }
    }

    /// Two items every style's golden carries, so the attachment shape is
    /// recorded even for a style that declares no built-in feature.
    static let attachments = [
        StyleAttachmentItem(id: "DESIGN.local.md", title: "DESIGN", detail: "0.1.0-golden \u{00B7} full", readOnly: true, openPath: "/repo/.re0"),
        StyleAttachmentItem(id: "RETRO.local.md", title: "RETRO", detail: "0.1.0-golden \u{00B7} full", readOnly: true, openPath: nil),
    ]

    static func projection(for style: RegisteredStyle, casebookStates: [String: String] = [:]) -> Projection {
        let manifest = style.manifest
        let firstPrompt = manifest.actions.first.map { $0.prompt(text: "") } ?? ""
        let ready = StylePrerequisiteResult(ready: true)
        let missing = StylePrerequisiteResult(ready: false, missing: manifest.prerequisites.probes.first.map { [$0.missing] } ?? [],
                                              hint: manifest.prerequisites.probes.first?.hint,
                                              canInstall: manifest.install != nil)
        let open = Dictionary(uniqueKeysWithValues: manifest.capabilities.map { ($0, "open") })
        func make(prompts: [String] = [], group: String? = nil, states: [String: String] = casebookStates,
                  items: [StyleAttachmentItem] = [], prerequisites: StylePrerequisiteResult = ready,
                  state: StyleStateReading = .empty, running: Bool = false) -> StylePanel {
            StylePanelProjection.make(style: style, prompts: prompts, selectedGroupId: group,
                                      capabilityStates: states, attachments: items, prerequisites: prerequisites,
                                      running: running, state: state)
        }
        let stages = planStageReadings(for: manifest)
        func stage(_ value: StylePlanStage) -> StylePanel? {
            stages?[value].map { make(state: $0, running: value == .awaitingApproval) }
        }
        return Projection(empty: make(),
                          afterFirstAction: make(prompts: [firstPrompt]),
                          notReady: make(prerequisites: missing),
                          firstGroup: make(group: manifest.groups.first?.id),
                          lastGroup: make(group: manifest.groups.last?.id),
                          capabilityOpen: make(group: manifest.groups.last?.id, states: open, items: attachments),
                          withState: stateReading(for: manifest).map { make(state: $0) },
                          planStagePlanning: stage(.planning), planStageAwaitingApproval: stage(.awaitingApproval),
                          planStageExecuting: stage(.executing))
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
