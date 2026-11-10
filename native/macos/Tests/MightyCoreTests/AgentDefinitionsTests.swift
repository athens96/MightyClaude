import Foundation
import Testing
@testable import MightyCore

/// Custom agents' colour and model, read from their definition files (`AgentDefinitions`): the frontmatter read
/// line by line, the project's file over the user's, a plugin's agent from its own folder, and nothing read twice
/// while the files stay as they are.
struct AgentDefinitionsTests {
    private func folder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("agent-definitions-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    private func write(_ text: String, _ path: String, in root: URL) throws {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    @Test func theFrontmatterGivesNameColourAndModel() throws {
        let text = "---\nname: reviewer\ndescription: Reviews a diff\ncolor: green\nmodel: sonnet\ntools: Read, Grep\n---\n\nYou review code.\n"
        #expect(AgentDefinitions.parse(text, fileName: "code-reviewer.md") == AgentDefinition(name: "reviewer", color: "green", model: "sonnet"))
        // Without a name the file's own stands in.
        #expect(AgentDefinitions.parse("---\ncolor: Blue\n---\n", fileName: "scout.md") == AgentDefinition(name: "scout", color: "blue"))
    }

    @Test func quotesCommentsAndABomAreReadThrough() throws {
        let text = "\u{FEFF}---\nname: \"deep scout\"   # the long one\ncolor: 'purple'\nmodel: claude-opus-5-5 # pinned\n---\n"
        #expect(AgentDefinitions.parse(text, fileName: "x.md") == AgentDefinition(name: "deep scout", color: "purple", model: "claude-opus-5-5"))
        #expect(AgentDefinitions.scalar("  'a # b'  ") == "a # b")
        #expect(AgentDefinitions.scalar("plain # note") == "plain")
        #expect(AgentDefinitions.scalar("   ") == nil)
        #expect(AgentDefinitions.scalar("[a, b]") == nil)
        #expect(AgentDefinitions.scalar("|") == nil)
        #expect(AgentDefinitions.scalar("\"unclosed") == nil)
    }

    @Test func whatYAMLWouldRejectStillGivesWhatItCan() throws {
        // A tab-indented list, a broken mapping and a block scalar around the two lines that matter.
        let text = "---\nname: fixer\ntools:\n\t- Read\n  - : broken\ndescription: >\n  folded\n  text: not a key\ncolor: orange\nmodel: [haiku\n---\nbody\n"
        #expect(AgentDefinitions.parse(text, fileName: "fixer.md") == AgentDefinition(name: "fixer", color: "orange"))
        // A colour Claude Code lacks and a model no CLI could name are left out, not passed on.
        #expect(AgentDefinitions.parse("---\nname: odd\ncolor: teal\nmodel: two words\n---\n", fileName: "odd.md") == AgentDefinition(name: "odd"))
        // The first of a repeated key is the one read.
        #expect(AgentDefinitions.parse("---\ncolor: red\ncolor: blue\n---\n", fileName: "twice.md")?.color == "red")
    }

    @Test func aFileWithoutAClosedFrontmatterIsNoDefinition() {
        #expect(AgentDefinitions.parse("# Just notes\n\ncolor: red\n", fileName: "notes.md") == nil)
        #expect(AgentDefinitions.parse("---\nname: open\ncolor: red\n", fileName: "open.md") == nil)
        #expect(AgentDefinitions.parse("", fileName: "empty.md") == nil)
        // The block ends within its first lines or it is not read as one.
        let long = "---\n" + String(repeating: "x: y\n", count: AgentDefinitions.maximumLines + 5) + "color: red\n---\n"
        #expect(AgentDefinitions.parse(long, fileName: "long.md") == nil)
    }

    @Test func theProjectsAgentWinsOverTheUsersAndOnlyAskedTypesAreReturned() throws {
        let root = try folder(); defer { try? FileManager.default.removeItem(at: root) }
        let config = root.appendingPathComponent("config"), workspace = root.appendingPathComponent("project")
        try write("---\nname: reviewer\ncolor: red\nmodel: opus\n---\n", "agents/reviewer.md", in: config)
        try write("---\nname: scout\ncolor: cyan\n---\n", "agents/scout.md", in: config)
        try write("---\nname: reviewer\ncolor: green\n---\n", ".claude/agents/reviewer.md", in: workspace)
        try write("not an agent", ".claude/agents/README.txt", in: workspace)
        let cache = AgentDefinitions.Cache()
        let found = AgentDefinitions.load(types: ["reviewer", "scout", "Explore"], workspace: workspace, configDirectory: config, cache: cache)
        #expect(found == ["reviewer": AgentDefinition(name: "reviewer", color: "green"), "scout": AgentDefinition(name: "scout", color: "cyan")])
        // Without a project folder the user's agent is the one.
        #expect(AgentDefinitions.load(types: ["reviewer"], workspace: nil, configDirectory: config, cache: cache)["reviewer"]?.color == "red")
        #expect(AgentDefinitions.load(types: ["scout"], workspace: workspace, configDirectory: config, cache: cache).keys.sorted() == ["scout"])
        #expect(AgentDefinitions.load(types: [], workspace: workspace, configDirectory: config, cache: cache).isEmpty)
        // A folder that is not there is no error.
        #expect(AgentDefinitions.load(types: ["reviewer"], workspace: root.appendingPathComponent("missing"), configDirectory: root.appendingPathComponent("none"), cache: cache).isEmpty)
    }

    @Test func aPluginsAgentIsFoundInItsInstallFolder() throws {
        let root = try folder(); defer { try? FileManager.default.removeItem(at: root) }
        let config = root.appendingPathComponent("config"), install = root.appendingPathComponent("cache/tools/1.2.0")
        try write("---\nname: executor\ncolor: pink\nmodel: sonnet\n---\n", "agents/executor.md", in: install)
        let index: [String: Any] = ["version": 2, "plugins": ["tools@market": [["scope": "user", "installPath": install.path]], "empty@market": []]]
        try write(String(decoding: try JSONSerialization.data(withJSONObject: index), as: UTF8.self), "plugins/installed_plugins.json", in: config)
        let cache = AgentDefinitions.Cache()
        let found = AgentDefinitions.load(types: ["tools:executor", "tools:missing", "other:executor", "tools:../escape"], workspace: nil, configDirectory: config, cache: cache)
        #expect(found == ["tools:executor": AgentDefinition(name: "tools:executor", color: "pink", model: "sonnet")])
        #expect(cache.pluginFolders(configDirectory: config).keys.sorted() == ["tools"])
    }

    @Test func aFolderIsReadAgainOnlyWhenItsFilesChange() throws {
        let root = try folder(); defer { try? FileManager.default.removeItem(at: root) }
        let agents = root.appendingPathComponent("agents")
        try write("---\nname: scout\ncolor: cyan\n---\n", "agents/scout.md", in: root)
        let cache = AgentDefinitions.Cache()
        #expect(cache.definitions(in: agents).map(\.color) == ["cyan"])
        // The same bytes at the same time: what was read is what is given.
        #expect(cache.definitions(in: agents) == [AgentDefinition(name: "scout", color: "cyan")])
        // A file that grew is read again.
        try write("---\nname: scout\ncolor: yellow\nmodel: haiku\n---\n", "agents/scout.md", in: root)
        #expect(cache.definitions(in: agents) == [AgentDefinition(name: "scout", color: "yellow", model: "haiku")])
        try write("---\nname: second\n---\n", "agents/second.md", in: root)
        #expect(cache.definitions(in: agents).map(\.name) == ["scout", "second"])
        try FileManager.default.removeItem(at: agents)
        #expect(cache.definitions(in: agents).isEmpty)
    }

    @Test func onlyAFilesHeadIsRead() throws {
        let root = try folder(); defer { try? FileManager.default.removeItem(at: root) }
        // The closing line lies past what is read: no definition, and no megabyte in memory.
        let padding = String(repeating: "# " + String(repeating: "x", count: 98) + "\n", count: AgentDefinitions.maximumBytes / 100 + 10)
        try write("---\nname: huge\n" + padding + "color: red\n---\n", "agents/huge.md", in: root)
        try write("---\nname: small\ncolor: red\n---\n" + String(repeating: "body\n", count: 50_000), "agents/small.md", in: root)
        let found = AgentDefinitions.Cache().definitions(in: root.appendingPathComponent("agents"))
        #expect(found == [AgentDefinition(name: "small", color: "red")])
    }

    @Test func theConfigFolderFollowsTheEnvironment() {
        let home = URL(fileURLWithPath: "/Users/someone", isDirectory: true)
        #expect(AgentDefinitions.configDirectory(home: home, environment: [:]).path == "/Users/someone/.claude")
        #expect(AgentDefinitions.configDirectory(home: home, environment: ["CLAUDE_CONFIG_DIR": ""]).path == "/Users/someone/.claude")
        #expect(AgentDefinitions.configDirectory(home: home, environment: ["CLAUDE_CONFIG_DIR": "/tmp/claude-config"]).path == "/tmp/claude-config")
    }
}
