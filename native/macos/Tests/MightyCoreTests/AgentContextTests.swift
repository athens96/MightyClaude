import Foundation
import Testing
@testable import MightyCore

/// 에이전트 맥락 (`AgentContext.swift`): instruction files, MCP servers of the three CLIs, Claude hooks.
struct AgentContextTests {
    private func folders() throws -> (root: URL, home: URL) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-context-" + UUID().uuidString, isDirectory: true)
        let root = base.appendingPathComponent("work"), home = base.appendingPathComponent("home")
        for folder in [root.appendingPathComponent(".claude"), root.appendingPathComponent(".gemini"), home.appendingPathComponent(".codex"), home.appendingPathComponent(".claude")] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        }
        return (root, home)
    }
    private func write(_ text: String, _ url: URL) throws { try Data(text.utf8).write(to: url) }

    @Test func instructionFilesAreReadComparedAndWritten() throws {
        let (root, _) = try folders()
        try write("rules\n", root.appendingPathComponent("CLAUDE.md"))
        try write("rules", root.appendingPathComponent("AGENTS.md"))
        var files = AgentContext.instructionFiles(root: root)
        #expect(files.map(\.name) == ["CLAUDE.md", "AGENTS.md", "GEMINI.md"] && files[2].text == nil)
        #expect(!AgentContext.diverged(files))
        try AgentContext.write("other rules", name: "GEMINI.md", root: root)
        files = AgentContext.instructionFiles(root: root)
        #expect(AgentContext.diverged(files) && files[2].text == "other rules")
        #expect(throws: (any Error).self) { try AgentContext.write("x", name: "../evil.md", root: root) }
    }

    @Test func mcpServersComeFromEachCLIsFiles() throws {
        let (root, home) = try folders()
        try write(#"{"mcpServers":{"db":{"command":"npx","args":["db-mcp","--ro"]}}}"#, root.appendingPathComponent(".mcp.json"))
        try write(#"{"mcpServers":{"docs":{"url":"https://example.com/mcp"}},"projects":{"\#(root.path)":{"mcpServers":{"local":{"command":"./serve"}}}}}"#, home.appendingPathComponent(".claude.json"))
        try write("""
        model = "gpt"
        [mcp_servers.github]
        command = "gh-mcp"
        args = ["--stdio", "-v"]
        [mcp_servers.github.env]
        TOKEN = "x"
        [mcp_servers."web"]
        url = "http://localhost:9000"
        [profiles.fast]
        command = "nope"
        """, home.appendingPathComponent(".codex/config.toml"))
        try write(#"{"mcpServers":{"fs":{"command":"fs-mcp"}}}"#, root.appendingPathComponent(".gemini/settings.json"))
        let servers = AgentContext.mcpServers(root: root, home: home)
        #expect(servers.map { "\($0.provider):\($0.scope):\($0.name)=\($0.target)" } == [
            "claude:project:db=npx db-mcp --ro", "claude:project:local=./serve", "claude:user:docs=https://example.com/mcp",
            "codex:user:github=gh-mcp --stdio -v", "codex:user:web=http://localhost:9000", "gemini:project:fs=fs-mcp",
        ])
    }

    @Test func claudeHooksAreListedPerEvent() throws {
        let (root, home) = try folders()
        try write(#"{"hooks":{"PostToolUse":[{"matcher":"Edit","hooks":[{"type":"command","command":"swiftformat ."}]}],"Stop":[]}}"#, root.appendingPathComponent(".claude/settings.json"))
        try write(#"{"hooks":{"Notification":[{"hooks":[{"type":"command","command":"say done"},{"type":"command","command":"afplay x"}]}]}}"#, home.appendingPathComponent(".claude/settings.json"))
        let hooks = AgentContext.claudeHooks(root: root, home: home)
        #expect(hooks.map(\.event) == ["PostToolUse", "Notification"])
        #expect(hooks[0].scope == "project" && hooks[0].commands == ["swiftformat ."])
        #expect(hooks[1].scope == "user" && hooks[1].commands.count == 2)
    }
}
