import Foundation
import Testing
@testable import MightyCore

struct SlashCommandTests {
    private func write(_ text: String, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    @Test func scansSkillsCommandsPluginsAndCodexWithProjectShadowing() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("slash-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("repo")
        try write("---\nname: gstack\ndescription: \"Router for the suite\"\n---\n# body\n", to: home.appendingPathComponent(".claude/skills/_gstack-command/SKILL.md"))
        try write("---\ndescription: 'Make diagrams'\n---\n", to: home.appendingPathComponent(".claude/skills/archify/SKILL.md"))
        try write("no frontmatter here\n", to: home.appendingPathComponent(".claude/skills/plain/SKILL.md"))
        try write("---\nname: bad name!\n---\n", to: home.appendingPathComponent(".claude/skills/weird dir/SKILL.md"))
        try write("---\nallowed-tools: [Read]\ndescription: \"Analyze code\"\n---\n", to: home.appendingPathComponent(".claude/commands/sc/analyze.md"))
        try write("Just a prompt body line\nmore\n", to: home.appendingPathComponent(".claude/commands/deploy.md"))
        let plugin = root.appendingPathComponent("cache/official/ralph/1.0.0")
        try write("---\nname: ralph-loop\ndescription: Loop\n---\n", to: plugin.appendingPathComponent("skills/ralph-loop/SKILL.md"))
        try write("---\ndescription: Cancel\n---\n", to: plugin.appendingPathComponent("commands/cancel-ralph.md"))
        try write("""
        {"version": 2, "plugins": {"ralph@official": [{"scope": "user", "installPath": "\(plugin.path)"}], "ghost@official": [{"installPath": "\(root.path)/missing"}], "../x@y": [{"installPath": "\(plugin.path)"}]}}
        """, to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        try write("---\nname: archify\ndescription: Project override\n---\n", to: workspace.appendingPathComponent(".claude/skills/archify/SKILL.md"))
        try write("---\nname: hatch-pet\ndescription: Pets\n---\n", to: home.appendingPathComponent(".codex/skills/hatch-pet/SKILL.md"))

        let claude = SlashCommandCatalog.commands(provider: "claude", workspacePath: workspace.path, home: home)
        #expect(claude.map(\.invocation) == ["archify", "deploy", "gstack", "plain", "ralph:cancel-ralph", "ralph:ralph-loop", "sc:analyze"])
        #expect(claude.first { $0.invocation == "archify" }?.description == "Project override")
        #expect(claude.first { $0.invocation == "archify" }?.source == "프로젝트 스킬")
        #expect(claude.first { $0.invocation == "gstack" }?.description == "Router for the suite")
        #expect(claude.first { $0.invocation == "deploy" }?.description == "Just a prompt body line")
        #expect(claude.first { $0.invocation == "ralph:ralph-loop" }?.source == "플러그인 ralph")
        #expect(claude.first { $0.invocation == "sc:analyze" }?.description == "Analyze code")
        let codex = SlashCommandCatalog.commands(provider: "codex", workspacePath: workspace.path, home: home)
        #expect(codex.map(\.invocation) == ["hatch-pet"] && codex[0].source == "Codex 스킬")
        #expect(SlashCommandCatalog.commands(provider: "gemini", workspacePath: nil, home: home).isEmpty)
        #expect(SlashCommandCatalog.commands(provider: "claude", workspacePath: nil, home: root.appendingPathComponent("nowhere")).isEmpty)
    }

    @Test func queryAndFilterFollowTheComposerRules() {
        #expect(SlashCommandCatalog.query(from: "/") == "")
        #expect(SlashCommandCatalog.query(from: "/ar") == "ar")
        #expect(SlashCommandCatalog.query(from: "/archify make a diagram") == nil)
        #expect(SlashCommandCatalog.query(from: "hello /ar") == nil)
        #expect(SlashCommandCatalog.query(from: "/" + String(repeating: "a", count: 81)) == nil)
        let commands = ["archify", "sc:analyze", "sc:build", "oh-my-claudecode:autopilot", "review"].map { SlashCommand(invocation: $0, description: $0 == "review" ? "Analyze a PR" : "", source: "x") }
        #expect(SlashCommandCatalog.filter(commands, query: "").map(\.invocation) == commands.map(\.invocation))
        #expect(SlashCommandCatalog.filter(commands, query: "a").map(\.invocation) == ["archify", "sc:analyze", "oh-my-claudecode:autopilot", "review"])
        #expect(SlashCommandCatalog.filter(commands, query: "auto").map(\.invocation) == ["oh-my-claudecode:autopilot"])
        #expect(SlashCommandCatalog.filter(commands, query: "SC:").map(\.invocation) == ["sc:analyze", "sc:build"])
        #expect(SlashCommandCatalog.filter(commands, query: "zzz").isEmpty)
        #expect(SlashCommandCatalog.frontmatter("---\nname: x\ndescription: >\n  folded\n---") == ["name": "x"])
        #expect(SlashCommandCatalog.frontmatter("# no frontmatter") == [:])
    }

    @Test func builtinsUseEachCLIsOwnNamesAndRunInTheApp() {
        let claude = SlashCommandCatalog.builtins(provider: "claude")
        #expect(claude.map(\.invocation) == ["plugin", "model", "permissions", "clear", "cost", "usage", "config", "rename", "help"])
        #expect(claude.allSatisfy { $0.source == "앱 기능" && ($0.action != nil) != ($0.argument != nil) })
        #expect(claude.first { $0.invocation == "plugin" }?.action == .openPlugins)
        #expect(claude.first { $0.invocation == "model" }?.argument == .model)
        #expect(claude.first { $0.invocation == "permissions" }?.argument == .permission)
        #expect(SlashCommandCatalog.builtins(provider: "codex").map(\.invocation) == ["plugins", "model", "approvals", "new", "status", "settings", "rename", "help"])
        let gemini = SlashCommandCatalog.builtins(provider: "gemini")
        #expect(gemini.map(\.invocation) == ["model", "approval-mode", "clear", "stats", "settings", "rename", "help"])
        #expect(!gemini.contains { $0.action == .openPlugins })
        #expect(SlashCommandCatalog.builtins(provider: "shell").isEmpty)
        let help = SlashCommandCatalog.helpText(provider: "claude")
        #expect(help.hasPrefix("앱 명령 · Claude 실행 창\n/plugin · ") && help.contains("\n/clear · ") && help.contains("CLI에 전달"))
        // Built-ins sort into the same prefix-first order as scanned commands.
        let mixed = SlashCommandCatalog.filter(claude + [SlashCommand(invocation: "plan-review", description: "", source: "x")], query: "pl")
        #expect(mixed.map(\.invocation) == ["plugin", "plan-review"])
    }

    @Test func argumentQueryFollowsASingleSpaceAfterABuiltinName() {
        #expect(SlashCommandCatalog.argumentQuery(from: "/model ")?.command == "model")
        #expect(SlashCommandCatalog.argumentQuery(from: "/model ")?.query == "")
        #expect(SlashCommandCatalog.argumentQuery(from: "/model cla")?.query == "cla")
        #expect(SlashCommandCatalog.argumentQuery(from: "/approval-mode plan")?.command == "approval-mode")
        #expect(SlashCommandCatalog.argumentQuery(from: "/model") == nil)
        #expect(SlashCommandCatalog.argumentQuery(from: "/model a b") == nil)
        #expect(SlashCommandCatalog.argumentQuery(from: "/model  ") == nil)
        #expect(SlashCommandCatalog.argumentQuery(from: "/bad name! x") == nil)
        #expect(SlashCommandCatalog.argumentQuery(from: "model x") == nil)
        let choices = ["default", "claude-opus-5", "claude-sonnet-5"].map { SlashCommand(invocation: "model " + $0, description: $0, source: "모델", action: .setModel($0)) }
        #expect(SlashCommandCatalog.filter(choices, query: "model cla").map(\.invocation) == ["model claude-opus-5", "model claude-sonnet-5"])
        #expect(SlashCommandCatalog.filter(choices, query: "model ").count == 3)
    }

    @Test func roParticleFollowsTheLastSyllable() {
        #expect(KoreanParticle.ro("전체 접근") == "으로"); #expect(KoreanParticle.ro("읽기 전용") == "으로")
        #expect(KoreanParticle.ro("계획") == "으로"); #expect(KoreanParticle.ro("작업 폴더") == "로"); #expect(KoreanParticle.ro("CLI 기본값") == "으로")
        #expect(KoreanParticle.ro("Bypass") == "로"); #expect(KoreanParticle.ro("opus") == "로"); #expect(KoreanParticle.ro("하늘") == "로"); #expect(KoreanParticle.ro("") == "로")
    }
}
