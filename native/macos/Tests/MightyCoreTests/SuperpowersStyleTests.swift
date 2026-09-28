import Foundation
import Testing
@testable import MightyCore

/// The equivalence oracle for the Superpowers bundle: the style is found,
/// decoded, validated, offered by the picker, and its four phases each carry
/// buttons that send a skill command the installed plugin actually ships.
struct SuperpowersStyleTests {
    private var style: RegisteredStyle { StyleFixtures.bundled("superpowers") }
    private var evaluator: StyleEvaluator { style.evaluator }
    private var registry: StyleRegistry { StyleRegistry(styles: BundledStyles.shared.styles()) }
    private func phase(_ id: String) -> StylePhase? { style.manifest.phase(id) }

    /// The skill folders `obra/superpowers` ships. Every button's command is
    /// checked against this list rather than against a guessed name, so a
    /// renamed skill upstream fails here instead of in a live pane.
    private static let pluginSkills: Set<String> = [
        "brainstorming", "diagnosing-superpowers", "dispatching-parallel-agents", "executing-plans",
        "finishing-a-development-branch", "receiving-code-review", "requesting-code-review",
        "subagent-driven-development", "systematic-debugging", "test-driven-development",
        "using-git-worktrees", "using-superpowers", "verification-before-completion",
        "writing-plans", "writing-skills",
    ]

    @Test func decodesAndValidatesAsABundledStyle() throws {
        let files = StyleSourceScanner.bundled().filter { $0.url.lastPathComponent == "superpowers.json" }
        #expect(files.count == 1)
        let file = try #require(files.first)
        let manifest = try StyleManifestDecoder.decode(file.data, source: .bundled)
        try StyleManifestValidator.validate(manifest, source: .bundled, knownCapabilities: StyleCapabilityID.all)
        #expect(manifest.id == "superpowers" && manifest.name == "Superpowers" && manifest.schema == 1)
        #expect(file.hash.count == 64)
    }

    @Test func appearsInTheStylePicker() {
        // A bundled manifest is pre-approved, so it is runnable without a card.
        #expect(registry.resolve("superpowers")?.approval == .preApproved)
        #expect(registry.runnableInPrecedence(workspace: nil).map(\.id) == ["ouroboros", "paperthin", "superpowers"])
        #expect(registry.resolve("superpowers")?.source == .bundled)
        #expect(style.manifest.presentation.icon?.rawValue == "sparkles" && style.manifest.presentation.tint == .teal)
    }

    @Test func fourPhasesInLoopOrder() {
        let phases = style.manifest.phases
        #expect(phases.map(\.id) == ["brainstorm", "plan", "execute", "finish"])
        #expect(phases.map(\.order) == [0, 1, 2, 3])
        #expect(phases.map(\.title) == ["브레인스토밍", "계획", "실행", "완료"])
        #expect(evaluator.drawsPhaseProgress && !evaluator.drawsGroupMap())
        // Every loop phase owns at least one button.
        for id in ["brainstorm", "plan", "execute", "finish"] {
            #expect(style.manifest.actions.contains { $0.phase == id }, "\(id) 단계에 버튼이 없습니다")
        }
    }

    @Test func buttonsSendInstalledPluginSkillCommands() {
        for action in style.manifest.actions {
            let prompt = action.prompt(text: "")
            #expect(prompt.hasPrefix("/superpowers:"), "\(action.id): \(prompt)")
            let skill = prompt.dropFirst("/superpowers:".count).split(separator: " ").first.map(String.init) ?? ""
            #expect(Self.pluginSkills.contains(skill), "설치된 플러그인에 없는 스킬: \(skill)")
        }
        #expect(evaluator.prompt(actionId: "brainstorming", text: "  결제 모듈 \n") == "/superpowers:brainstorming 결제 모듈")
        #expect(evaluator.prompt(actionId: "writing-plans", text: "") == "/superpowers:writing-plans")
        #expect(evaluator.prompt(actionId: "executing-plans", text: "") == "/superpowers:executing-plans")
        #expect(evaluator.prompt(actionId: "verification-before-completion", text: "") == "/superpowers:verification-before-completion")
        #expect(evaluator.prompt(actionId: "nowhere", text: "") == nil)
        #expect(evaluator.recognised(inPrompt: "/superpowers:writing-plans") == .action("writing-plans"))
        #expect(evaluator.recognised(inPrompt: "/superpowers:nope") == nil)
        #expect(evaluator.recognised(inPrompt: "brainstorming 해줘") == nil)
    }

    @Test func phaseWalksTheLoopAndNamesTheNextButtons() {
        // A pane with no Superpowers request yet opens on brainstorm.
        #expect(evaluator.currentPhase(prompts: [])?.id == "brainstorm")
        #expect(evaluator.currentPhase(prompts: ["/superpowers:writing-plans"])?.id == "plan")
        #expect(evaluator.currentPhase(prompts: ["/superpowers:writing-plans", "/superpowers:executing-plans"])?.id == "execute")
        #expect(evaluator.currentPhase(prompts: ["/superpowers:verification-before-completion"])?.id == "finish")
        // An unrecognised request leaves the phase where it was.
        #expect(evaluator.currentPhase(prompts: ["안녕하세요"])?.id == "brainstorm")

        #expect(evaluator.startActions(phase: phase("brainstorm")).map(\.id) == ["brainstorming", "writing-plans"])
        #expect(evaluator.resetTitle == "새 아이디어")
        #expect(evaluator.nextActions(phase: phase("brainstorm"), group: nil).first?.id == "writing-plans")
        #expect(evaluator.nextActions(phase: phase("plan"), group: nil).map(\.id) == ["executing-plans", "subagent-driven-development"])
        #expect(evaluator.nextActions(phase: phase("execute"), group: nil).first?.id == "verification-before-completion")
        #expect(evaluator.nextActions(phase: phase("finish"), group: nil).first?.id == "verification-before-completion")
    }

    @Test func phaseStateOverridesAdvanceByFileSourceSignals() {
        // No commands, no state → brainstorm (default)
        #expect(evaluator.currentPhase(prompts: [], fileSourceStates: [:])?.id == "brainstorm")
        // Plan file qualifies (engine applied mtime rule before this call), no commands → execute
        let planExists = StyleFileSourceState(exists: true, allChecked: false)
        #expect(evaluator.currentPhase(prompts: [], fileSourceStates: [0: planExists])?.id == "execute")
        // Plan file exists and all items checked → finish
        let planDone = StyleFileSourceState(exists: true, allChecked: true)
        #expect(evaluator.currentPhase(prompts: [], fileSourceStates: [0: planDone])?.id == "finish")
        // Command history says finish, plan not fully checked → finish (command history wins, no retreat)
        #expect(evaluator.currentPhase(prompts: ["/superpowers:verification-before-completion"],
                                       fileSourceStates: [0: planExists])?.id == "finish")
        // Command history says execute, plan all checked → finish (state advances)
        #expect(evaluator.currentPhase(prompts: ["/superpowers:executing-plans"],
                                       fileSourceStates: [0: planDone])?.id == "finish")
        // Plan file stale / not present → no state advance
        let noPlan = StyleFileSourceState(exists: false, allChecked: false)
        #expect(evaluator.currentPhase(prompts: [], fileSourceStates: [0: noPlan])?.id == "brainstorm")
        // §1.16 precedence case 1: plan command run, but no qualifying plan file → command phase wins
        #expect(evaluator.currentPhase(prompts: ["/superpowers:writing-plans"],
                                       fileSourceStates: [0: noPlan])?.id == "plan")
        // §1.16 precedence case 2: qualifying plan present, but last command was brainstorm → state advances to execute
        #expect(evaluator.currentPhase(prompts: ["/superpowers:brainstorming"],
                                       fileSourceStates: [0: planExists])?.id == "execute")
        // Unknown sourceIndex key → no advance (silently ignored)
        #expect(evaluator.currentPhase(prompts: [], fileSourceStates: [99: planDone])?.id == "brainstorm")
        // Enter is still verbatim regardless of state
        #expect(style.manifest.rules.enter == .verbatim)
    }

    @Test func enterStaysVerbatimAndTheStyleOnlyAdvises() {
        #expect(style.manifest.rules.enter == .verbatim)
        // Unlike Ouroboros, a bare draft is never turned into a command.
        #expect(evaluator.enterBehaviour(draft: "결제 모듈", phase: phase("brainstorm"), hasAttachments: false,
                                         running: false, hasRequests: false) == .verbatim)
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈", phase: phase("brainstorm"), running: false, hasRequests: false) == nil)
        #expect(style.manifest.autoAllow.isEmpty)
    }

    @Test func panelCarriesTheStateItWasGiven() throws {
        // A current plan with 2 of 4 items checked → execute phase + a 2/4 bar.
        let planExists = StyleFileSourceState(exists: true, allChecked: false)
        let progress = StylePanel.Widget.progressBar(value: 2, total: 4)
        let label = StylePanel.Widget.label(text: "서브에이전트 3회 시작")
        func panel(_ state: StyleStateReading) -> StylePanel {
            StylePanelProjection.make(style: style, prompts: [], selectedGroupId: nil, capabilityStates: [:], attachments: [],
                                      prerequisites: StylePrerequisiteResult(ready: true), state: state)
        }
        let executing = panel(StyleStateReading(fileSourceStates: [0: planExists], widgets: [progress, label]))
        #expect(executing.phase?.id == "execute")
        #expect(executing.widgets == [progress, label])
        // All items checked → finish.
        let done = panel(StyleStateReading(fileSourceStates: [0: StyleFileSourceState(exists: true, allChecked: true)],
                                           widgets: [.progressBar(value: 4, total: 4)]))
        #expect(done.phase?.id == "finish")
        // No state at all → brainstorm, and no widgets field.
        let bare = panel(.empty)
        #expect(bare.phase?.id == "brainstorm" && bare.widgets == nil)

        // The payload round-trips, and the bar travels as two counts.
        let data = try StylePanelProjection.serialise(executing)
        #expect(try JSONDecoder().decode(StylePanel.self, from: data).widgets == [progress, label])
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.contains("\"kind\" : \"progressBar\"") && text.contains("\"total\" : 4") && text.contains("\"value\" : 2"))
    }

    @Test func prerequisiteNamesTheInstalledPlugin() throws {
        let root = StyleFixtures.temporaryDirectory("superpowers")
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home")
        let manifest = style.manifest
        func ready(_ home: URL) -> Bool {
            StylePrerequisiteProbe.evaluate(manifest.prerequisites, install: manifest.install, home: home,
                                            workspacePath: nil, environment: [:]).ready
        }
        #expect(!ready(home))
        let installed = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        try FileManager.default.createDirectory(at: installed.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"version":2,"plugins":{"superpowers@claude-community":[{"installPath":"/x","scope":"user"}]}}"#.utf8).write(to: installed)
        #expect(ready(home))
        // A fresh Mac has neither the marketplace nor the plugin: `enable` alone
        // only switches on a plugin already installed. The marketplace source is
        // the one `~/.claude/plugins/known_marketplaces.json` records for
        // `claude-community`, the same shape as the Ouroboros manifest.
        #expect(manifest.install?.command
                == "claude plugin marketplace add anthropics/claude-plugins-community && claude plugin install superpowers@claude-community")
    }
}
