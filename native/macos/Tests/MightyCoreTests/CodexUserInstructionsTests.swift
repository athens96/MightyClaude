import Foundation
import Testing
@testable import MightyCore

/// Reading the user's own Codex `developer_instructions` so a run adds ours
/// after them instead of replacing them.
struct CodexUserInstructionsParserTests {
    @Test func eachStringFormReadsItsValue() {
        #expect(CodexUserInstructions.parse(#"developer_instructions = "a \"q\" \\ b\nc\td \u00e9 \U0001F600""#) == .value("a \"q\" \\ b\nc\td \u{e9} \u{1F600}"))
        #expect(CodexUserInstructions.parse(#"developer_instructions = 'C:\path "raw"'"#) == .value(#"C:\path "raw""#))
        #expect(CodexUserInstructions.parse("developer_instructions = \"\"\"\nline one\nline \"two\"\\n\"\"\"") == .value("line one\nline \"two\"\n"))
        #expect(CodexUserInstructions.parse("developer_instructions = \"\"\"\\\n    joined \\\n    here\"\"\"") == .value("joined here"))
        #expect(CodexUserInstructions.parse("developer_instructions = '''\nraw \\n\n'kept'\n'''") == .value("raw \\n\n'kept'\n"))
        #expect(CodexUserInstructions.parse("developer_instructions = \"\"\"ends with quote\"\"\"\"") == .value("ends with quote\""))
        #expect(CodexUserInstructions.parse("developer_instructions = \"한글 지시\"") == .value("한글 지시"))
        #expect(CodexUserInstructions.parse("\"developer_instructions\" = \"quoted key\"") == .value("quoted key"))
    }

    @Test func commentsTablesAndOtherKeysAreSkipped() {
        let text = """
        # developer_instructions = "not this"
        model = "gpt-5" # trailing
        approval_policy = "never"
        developer_instructions = "top" # comment after the value
        count = 3
        flag = true

        [mcp_servers.x]
        command = "npx"
        args = [
          "-y", # a comment inside an array
          "pkg]{",
        ]
        env = { A = "1", B = { C = "2" } }
        developer_instructions = "not a codex key here"

        [projects."/Users/me/repo"]
        trust_level = "trusted"
        """
        #expect(CodexUserInstructions.parse(text) == .value("top"))
        #expect(CodexUserInstructions.parse("model = \"x\"\n[tui]\nnotifications = true\n") == .absent)
        #expect(CodexUserInstructions.parse("") == .absent)
        #expect(CodexUserInstructions.parse("[profiles.p]\nmodel = \"x\"\n# developer_instructions only in a comment\n") == .absent)
    }

    @Test func aTopLevelKeyAfterATableIsNotTopLevel() {
        #expect(CodexUserInstructions.parse("[tui]\ndeveloper_instructions = \"in a table\"\n") == .absent)
    }

    @Test func theActiveProfileWins() {
        let base = """
        developer_instructions = "top"
        profile = "work"

        [profiles.work]
        developer_instructions = '''
        profile text'''

        [profiles.other]
        developer_instructions = "other"
        """
        #expect(CodexUserInstructions.parse(base) == .value("profile text"))
        #expect(CodexUserInstructions.parse("developer_instructions = \"top\"\nprofile = \"none\"\n[profiles.work]\ndeveloper_instructions = \"w\"\n") == .value("top"))
        #expect(CodexUserInstructions.parse("developer_instructions = \"top\"\n[profiles.work]\ndeveloper_instructions = \"w\"\n") == .value("top"))
        #expect(CodexUserInstructions.parse("profile = \"work\"\nprofiles.work.developer_instructions = \"dotted\"\n") == .value("dotted"))
    }

    @Test func formsItDoesNotConfidentlyParseAreUnreadable() {
        #expect(CodexUserInstructions.parse("developer_instructions = [\"a\"]") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = { text = \"a\" }") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = 3") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = \"unterminated") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = \"bad \\q escape\"") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = \"a\"\ndeveloper_instructions = \"b\"") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = \"a\" trailing") == .unreadable)
        #expect(CodexUserInstructions.parse("profile = \"p\"\nprofiles = { p = { developer_instructions = \"x\" } }") == .unreadable)
        #expect(CodexUserInstructions.parse("[profiles]\np = { developer_instructions = \"x\" }") == .unreadable)
        #expect(CodexUserInstructions.parse("[[profiles]]\ndeveloper_instructions = \"x\"") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = \"a\"\nprofile = 1") == .unreadable)
        #expect(CodexUserInstructions.parse("developer_instructions = \"a\"\n[broken") == .unreadable)
    }
}

/// Which files make up the value and what reaches Codex's argv.
struct CodexUserInstructionsArgumentTests {
    private func write(_ text: String, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    private func developerInstructions(home: URL, workspace: URL) throws -> String? {
        let binding = testPaneBinding(token: "codex-fixture-token-0123456789", workspacePath: workspace.path, provider: "codex")
        let request = StartRunRequest(sessionId: "a", workspaceId: "w", input: "hi", provider: "codex")
        let args = try ProviderService.arguments(request, pluginDirectory: URL(fileURLWithPath: "/tmp", isDirectory: true), paneMCPBinding: binding, codexHome: home)
        #expect(args.filter { $0.hasPrefix("developer_instructions=") }.count <= 1)
        guard let setting = args.first(where: { $0.hasPrefix("developer_instructions=") }) else { return nil }
        return try JSONDecoder().decode(String.self, from: Data(setting.dropFirst("developer_instructions=".count).utf8))
    }

    @Test func theUsersValueComesFirstThenOurs() throws {
        let root = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("ws")
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        try write("developer_instructions = \"Always answer in Korean.\"\n", to: home.appendingPathComponent("config.toml"))
        let value = try #require(try developerInstructions(home: home, workspace: workspace))
        #expect(value == "Always answer in Korean.\n\n" + PaneMCPToolManifest.codexDeveloperInstructions)
    }

    @Test func noUserValueSendsOursAlone() throws {
        let root = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("ws")
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        #expect(try developerInstructions(home: root.appendingPathComponent("missing"), workspace: workspace) == PaneMCPToolManifest.codexDeveloperInstructions)
    }

    @Test func anUnreadableUserValueLeavesTheFlagOut() throws {
        let root = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("ws")
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        try write("developer_instructions = [\"a\"]\n", to: home.appendingPathComponent("config.toml"))
        #expect(try developerInstructions(home: home, workspace: workspace) == nil)
        try Data([0x64, 0xFF, 0xFE]).write(to: home.appendingPathComponent("config.toml"))
        #expect(try developerInstructions(home: home, workspace: workspace) == nil)
    }

    @Test func aTrustedProjectOverridesTheHomeValue() throws {
        let root = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), repo = root.appendingPathComponent("repo"), sub = repo.appendingPathComponent("sub")
        try FileManager.default.createDirectory(at: repo.appendingPathComponent(".git"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        try write("developer_instructions = \"project\"\n", to: repo.appendingPathComponent(".codex/config.toml"))
        let homeConfig = home.appendingPathComponent("config.toml")
        try write("developer_instructions = \"home\"\n[projects.\"\(repo.path)\"]\ntrust_level = \"trusted\"\n", to: homeConfig)
        // The git root's layer applies to a run in a subfolder too.
        #expect(CodexUserInstructions.effective(codexHome: home, workingDirectory: sub) == .value("project"))
        #expect(try developerInstructions(home: home, workspace: sub) == "project\n\n" + PaneMCPToolManifest.codexDeveloperInstructions)
        try write("developer_instructions = \"deeper\"\n", to: sub.appendingPathComponent(".codex/config.toml"))
        #expect(CodexUserInstructions.effective(codexHome: home, workingDirectory: sub) == .value("deeper"))
    }

    @Test func anUntrustedProjectValueIsNeitherMergedNorClobbered() throws {
        let root = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), repo = root.appendingPathComponent("repo")
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try write("developer_instructions = \"from the repo\"\n", to: repo.appendingPathComponent(".codex/config.toml"))
        try write("developer_instructions = \"home\"\n", to: home.appendingPathComponent("config.toml"))
        #expect(CodexUserInstructions.effective(codexHome: home, workingDirectory: repo) == .unreadable)
        #expect(try developerInstructions(home: home, workspace: repo) == nil)
        try write("developer_instructions = \"home\"\n[projects.\"\(repo.path)\"]\ntrust_level = \"untrusted\"\n", to: home.appendingPathComponent("config.toml"))
        #expect(try developerInstructions(home: home, workspace: repo) == nil)
        // A project config without the key leaves the home value in charge.
        try write("model = \"x\"\n", to: repo.appendingPathComponent(".codex/config.toml"))
        #expect(try developerInstructions(home: home, workspace: repo) == "home\n\n" + PaneMCPToolManifest.codexDeveloperInstructions)
    }

    @Test func noBindingMeansNoFlag() throws {
        let request = StartRunRequest(sessionId: "a", workspaceId: "w", input: "hi", provider: "codex")
        let args = try ProviderService.arguments(request, pluginDirectory: URL(fileURLWithPath: "/tmp", isDirectory: true))
        #expect(!args.contains { $0.hasPrefix("developer_instructions=") })
    }

    /// `-c` values are TOML; a JSON string literal must read back unchanged as a TOML basic string.
    @Test func theTomlLiteralRoundTripsQuotesBackslashesNewlinesAndKorean() {
        let original = "say \"hi\" \\ C:\\path\nline two\t탭 한글 지시 😀 \u{1}"
        let literal = PaneMCPBinding.tomlLiteral(original)
        #expect(!literal.contains("\n"))
        #expect(CodexUserInstructions.parse("developer_instructions = " + literal) == .value(original))
        #expect((try? JSONDecoder().decode(String.self, from: Data(literal.utf8))) == original)
    }
}
