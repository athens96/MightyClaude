import Foundation
import Testing
@testable import MightyCore

/// The equivalence oracle for the Paperthin bundle (§5.9).
struct StylesPaperthinTests {
    private var style: RegisteredStyle { StyleFixtures.bundled("paperthin") }
    private var evaluator: StyleEvaluator { style.evaluator }
    private var registry: StyleRegistry { StyleRegistry(styles: BundledStyles.shared.styles()) }

    @Test func catalog() {
        let manifest = style.manifest
        #expect(manifest.actions.count == 28 && Set(manifest.actions.map(\.id)).count == 28)
        #expect(manifest.groups.map(\.id) == ["depth", "breadth", "coil", "mesh"])
        #expect(manifest.groups.map { $0.actions.count } == [19, 2, 6, 1])
        // The counts sum to 28 even if one skill sat in two groups and another
        // in none, so the partition itself is what is asserted.
        let grouped = manifest.groups.flatMap(\.actions)
        #expect(Set(grouped) == Set(manifest.actions.map(\.id)) && grouped.count == manifest.actions.count)
        #expect(manifest.group("coil")?.actions == ["re0-plan", "re0-loop", "re0-memo", "re0-work", "catchup", "nba"])
        #expect(manifest.group("mesh")?.actions == ["prism"] && manifest.group("breadth")?.actions == ["ssotize", "re0-upgrade"])
        // The twelve skills only the human can fire (docs/invocation.md).
        let userOnly = Set(manifest.actions.filter { $0.flags.contains(.userInvoked) }.map(\.id))
        #expect(userOnly == ["hate", "macrothink", "feynman", "reorder", "dedash", "debloat", "re0-git", "re0-release", "re0-merge", "re0-upgrade", "re0-plan", "prism"])
        #expect(manifest.action("nba")?.flags.contains(.readOnly) == true && manifest.action("re0")?.flags.contains(.readOnly) == false)
        #expect(manifest.group("coil")?.actions.contains("re0-loop") == true)
        #expect(manifest.actions.allSatisfy { !$0.help.isEmpty && ($0.glyph ?? "").isEmpty == false && ($0.scope ?? "").isEmpty == false })
        #expect(manifest.group("coil")?.question == "각 패스가 다음 패스를 가르쳤는가?" && manifest.group("depth")?.axis == "하나 \u{00B7} 지금")
        #expect(manifest.phases.isEmpty && evaluator.drawsGroupMap())
    }

    @Test func promptsAndTitles() {
        #expect(evaluator.prompt(actionId: "re0", text: "  docs/spec.md ") == "/re0 docs/spec.md")
        #expect(evaluator.prompt(actionId: "nba", text: "") == "/nba" && evaluator.prompt(actionId: "unknown", text: "") == nil)
        #expect(evaluator.recognised(inPrompt: "/re0-loop 결제 모듈") == .action("re0-loop"))
        #expect(evaluator.recognised(inPrompt: "/re0x") == nil && evaluator.recognised(inPrompt: "re0 please") == nil && evaluator.recognised(inPrompt: "/archify x") == nil)
        #expect(evaluator.requestTitle(forInput: "/prism README.md") == "🔺 prism")
        // One entry point names request blocks for every runnable style, because
        // a pane keeps its earlier blocks when the style changes (§1.10).
        #expect(registry.requestTitle(forInput: "/ouroboros:seed", workspace: nil) == "시드")
        #expect(registry.requestTitle(forInput: "/nba", workspace: nil) == "🎯 nba")
        #expect(registry.requestTitle(forInput: "hello", workspace: nil) == nil)
        #expect(evaluator.prompt(actionId: "re0", text: " docs/spec.md\n\n  tighten it \n") == "/re0 docs/spec.md tighten it")
        #expect(style.manifest.install?.command.hasSuffix("--agent claude-code") == true)
    }

    @Test func prerequisitesAndCasebook() throws {
        let root = StyleFixtures.temporaryDirectory("paperthin")
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("repo")
        func write(_ text: String, _ url: URL, modified: Date? = nil) throws {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(text.utf8).write(to: url)
            if let modified { try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path) }
        }
        let manifest = style.manifest
        func ready(_ home: URL, _ workspacePath: String? = nil) -> Bool {
            StylePrerequisiteProbe.evaluate(manifest.prerequisites, install: manifest.install, home: home, workspacePath: workspacePath, environment: [:]).ready
        }
        #expect(!ready(home))
        try write(#"{"version":2,"plugins":{"paperthin@somewhere":[{"installPath":"/x","scope":"user"}]}}"#, home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        #expect(ready(home))
        let skillsHome = root.appendingPathComponent("home2")
        try write("---\nname: nba\n---\n", skillsHome.appendingPathComponent(".claude/skills/nba/SKILL.md"))
        #expect(ready(skillsHome))

        func recommended(_ path: String) -> String? {
            let states = StyleCapabilities.evaluate(manifest.capabilities, workspacePath: path).states
            return evaluator.recommendedAction(capabilityStates: states)
        }
        func initialGroup(_ path: String) -> String? {
            evaluator.initialGroup(capabilityStates: StyleCapabilities.evaluate(manifest.capabilities, workspacePath: path).states)?.id
        }
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        #expect(StyleCasebook.latest(workspacePath: workspace.path) == nil)
        #expect(recommended(workspace.path) == "re0-plan" && initialGroup(workspace.path) == "depth")
        let old = workspace.appendingPathComponent(".re0/iteration/0.1.0-first")
        try write("retro", old.appendingPathComponent("RETRO.local.md"), modified: Date(timeIntervalSinceNow: -3600))
        try FileManager.default.createDirectory(at: workspace.appendingPathComponent(".re0/iteration/0.0.9-empty"), withIntermediateDirectories: true)
        let lightweight = try #require(StyleCasebook.latest(workspacePath: workspace.path))
        #expect(lightweight.name == "0.1.0-first" && lightweight.weight == "lightweight" && lightweight.files == ["RETRO.local.md"])
        #expect(recommended(workspace.path) == "re0-loop" && initialGroup(workspace.path) == "coil")
        let current = workspace.appendingPathComponent(".re0/iteration/0.2.0-second")
        for name in ["REF-api.local.md", "EVIDENCE.local.md", "DESIGN.local.md", "WORKFLOW.local.md"] { try write(name, current.appendingPathComponent(name)) }
        try write("not a casebook file", current.appendingPathComponent("notes.txt"))
        let full = try #require(StyleCasebook.latest(workspacePath: workspace.path))
        #expect(full.name == "0.2.0-second" && full.weight == "full")
        #expect(full.files == ["DESIGN.local.md", "WORKFLOW.local.md", "EVIDENCE.local.md", "REF-api.local.md"])
        #expect(recommended(workspace.path) == "re0-loop")
        try write("retro", current.appendingPathComponent("RETRO.local.md"))
        #expect(recommended(workspace.path) == "re0-work" && initialGroup(workspace.path) == "coil")
        // A project-scope install counts for that workspace only.
        let bare = root.appendingPathComponent("home3")
        try write("---\nname: re0\n---\n", workspace.appendingPathComponent(".claude/skills/re0/SKILL.md"))
        #expect(ready(bare, workspace.path) && !ready(bare))
        // A linked folder is never followed, however new what it points at is.
        let outside = root.appendingPathComponent("outside/9.9.9-elsewhere")
        try write("design", outside.appendingPathComponent("DESIGN.local.md"), modified: Date(timeIntervalSinceNow: 3600))
        try FileManager.default.createSymbolicLink(at: workspace.appendingPathComponent(".re0/iteration/9.9.9-link"), withDestinationURL: outside)
        #expect(StyleCasebook.latest(workspacePath: workspace.path)?.name == "0.2.0-second")
        // Only the most recently touched folders are scanned, and the newest is among them.
        for index in 0..<40 {
            let folder = workspace.appendingPathComponent(".re0/iteration/0.0.\(index)-archived")
            try write("retro", folder.appendingPathComponent("RETRO.local.md"), modified: Date(timeIntervalSinceNow: -86_400))
            try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -86_400)], ofItemAtPath: folder.path)
        }
        #expect(StyleCasebook.latest(workspacePath: workspace.path)?.name == "0.2.0-second")
    }
}
