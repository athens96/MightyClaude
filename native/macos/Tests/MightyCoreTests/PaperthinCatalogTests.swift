import Foundation
import Testing
@testable import MightyCore

struct PaperthinCatalogTests {
    @Test func catalogFollowsPaperthinsIndexAndInvocationSplit() {
        #expect(PaperthinCatalog.skills.count == 28 && Set(PaperthinCatalog.skills.map(\.name)).count == 28)
        #expect(PaperthinDomain.allCases.map { PaperthinCatalog.skills(in: $0).count } == [19, 2, 6, 1])
        // The twelve skills only the human can fire (docs/invocation.md).
        let userOnly = Set(PaperthinCatalog.skills.filter(\.userInvoked).map(\.name))
        #expect(userOnly == ["hate", "macrothink", "feynman", "reorder", "dedash", "debloat", "re0-git", "re0-release", "re0-merge", "re0-upgrade", "re0-plan", "prism"])
        #expect(PaperthinCatalog.skill("nba")?.readOnly == true && PaperthinCatalog.skill("re0")?.readOnly == false && PaperthinCatalog.skill("re0-loop")?.domain == .coil)
        #expect(PaperthinCatalog.skills.allSatisfy { !$0.summary.isEmpty && !$0.emoji.isEmpty && !$0.scope.isEmpty })
        #expect(PaperthinDomain.coil.question == "각 패스가 다음 패스를 가르쳤는가?" && PaperthinDomain.depth.axis == "하나 · 지금")
    }

    @Test func promptsAndRequestTitlesNameTheSkill() {
        #expect(PaperthinCatalog.prompt(skill: "re0", text: "  docs/spec.md ") == "/re0 docs/spec.md")
        #expect(PaperthinCatalog.prompt(skill: "nba") == "/nba" && PaperthinCatalog.prompt(skill: "unknown") == nil)
        #expect(PaperthinCatalog.skill(inPrompt: "/re0-loop 결제 모듈")?.name == "re0-loop")
        #expect(PaperthinCatalog.skill(inPrompt: "/re0x") == nil && PaperthinCatalog.skill(inPrompt: "re0 please") == nil && PaperthinCatalog.skill(inPrompt: "/archify x") == nil)
        #expect(PaperthinCatalog.requestTitle(forInput: "/prism README.md") == "🔺 prism")
        // One entry point names request blocks for both guided styles.
        #expect(MightyStyles.requestTitle(forInput: "/ouroboros:seed", style: "paperthin") == "시드" && MightyStyles.requestTitle(forInput: "/nba", style: "paperthin") == "🎯 nba")
        #expect(MightyStyles.requestTitle(forInput: "hello", style: "paperthin") == nil)
        // A plain CLI pane (or an unknown style) never relabels what the user typed.
        #expect(MightyStyles.requestTitle(forInput: "/nba", style: nil) == nil && MightyStyles.requestTitle(forInput: "/nba", style: "other") == nil)
        #expect(PaperthinCatalog.prompt(skill: "re0", text: " docs/spec.md\n\n  tighten it \n") == "/re0 docs/spec.md tighten it")
        #expect(PaperthinCatalog.installCommand.hasSuffix("--agent claude-code"))
        #expect(MightyStyles.normalized("paperthin") == "paperthin" && MightyStyles.normalized("ouroboros") == "ouroboros" && MightyStyles.normalized("other") == nil && MightyStyles.normalized(nil) == nil)
    }

    @Test func installationAndCasebookAreReadFromDisk() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("paperthin-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("repo")
        func write(_ text: String, _ url: URL, modified: Date? = nil) throws {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(text.utf8).write(to: url)
            if let modified { try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path) }
        }
        #expect(!PaperthinCatalog.installed(home: home))
        try write(#"{"version":2,"plugins":{"paperthin@somewhere":[{"installPath":"/x"}]}}"#, home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        #expect(PaperthinCatalog.installed(home: home))
        let skillsHome = root.appendingPathComponent("home2")
        try write("---\nname: nba\n---\n", skillsHome.appendingPathComponent(".claude/skills/nba/SKILL.md"))
        #expect(PaperthinCatalog.installed(home: skillsHome))

        #expect(PaperthinCasebook.latest(workspacePath: workspace.path) == nil)
        #expect(PaperthinCatalog.recommendedCoilSkill(casebook: nil) == "re0-plan")
        let old = workspace.appendingPathComponent(".re0/iteration/0.1.0-first")
        try write("retro", old.appendingPathComponent("RETRO.local.md"), modified: Date(timeIntervalSinceNow: -3600))
        try FileManager.default.createDirectory(at: workspace.appendingPathComponent(".re0/iteration/0.0.9-empty"), withIntermediateDirectories: true)
        let lightweight = try #require(PaperthinCasebook.latest(workspacePath: workspace.path))
        #expect(lightweight.name == "0.1.0-first" && lightweight.weight == "lightweight" && lightweight.files == ["RETRO.local.md"])
        #expect(PaperthinCatalog.recommendedCoilSkill(casebook: lightweight) == "re0-loop")
        let current = workspace.appendingPathComponent(".re0/iteration/0.2.0-second")
        for name in ["REF-api.local.md", "EVIDENCE.local.md", "DESIGN.local.md", "WORKFLOW.local.md"] { try write(name, current.appendingPathComponent(name)) }
        try write("not a casebook file", current.appendingPathComponent("notes.txt"))
        let full = try #require(PaperthinCasebook.latest(workspacePath: workspace.path))
        #expect(full.name == "0.2.0-second" && full.weight == "full")
        #expect(full.files == ["DESIGN.local.md", "WORKFLOW.local.md", "EVIDENCE.local.md", "REF-api.local.md"])
        #expect(PaperthinCatalog.recommendedCoilSkill(casebook: full) == "re0-loop")
        try write("retro", current.appendingPathComponent("RETRO.local.md"))
        #expect(PaperthinCatalog.recommendedCoilSkill(casebook: PaperthinCasebook.latest(workspacePath: workspace.path)) == "re0-work")
        // A project-scope install counts for that workspace only.
        let bare = root.appendingPathComponent("home3")
        try write("---\nname: re0\n---\n", workspace.appendingPathComponent(".claude/skills/re0/SKILL.md"))
        #expect(PaperthinCatalog.installed(home: bare, workspacePath: workspace.path) && !PaperthinCatalog.installed(home: bare))
        // A linked folder is never followed, however new what it points at is.
        let outside = root.appendingPathComponent("outside/9.9.9-elsewhere")
        try write("design", outside.appendingPathComponent("DESIGN.local.md"), modified: Date(timeIntervalSinceNow: 3600))
        try FileManager.default.createSymbolicLink(at: workspace.appendingPathComponent(".re0/iteration/9.9.9-link"), withDestinationURL: outside)
        #expect(PaperthinCasebook.latest(workspacePath: workspace.path)?.name == "0.2.0-second")
        // Only the most recently touched folders are scanned, and the newest is among them.
        for index in 0..<40 {
            let folder = workspace.appendingPathComponent(".re0/iteration/0.0.\(index)-archived")
            try write("retro", folder.appendingPathComponent("RETRO.local.md"), modified: Date(timeIntervalSinceNow: -86_400))
            try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -86_400)], ofItemAtPath: folder.path)
        }
        #expect(PaperthinCasebook.latest(workspacePath: workspace.path)?.name == "0.2.0-second")
        // The style survives normalization for local Claude panes only.
        var session = RunSession(workspaceId: "ws", title: "Claude"); session.mightyStyle = "paperthin"
        var codex = RunSession(workspaceId: "ws", title: "Codex", provider: "codex"); codex.mightyStyle = "paperthin"
        let state = StateRepository.normalize(AppSnapshot(workspaces: [Workspace(id: "ws", name: "R", path: "/tmp/r")], sessions: [session, codex]), restoring: true)
        #expect(state.sessions.map(\.mightyStyle) == ["paperthin", nil])
    }
}
