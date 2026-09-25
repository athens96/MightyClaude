import Foundation
import Testing
@testable import MightyCore

@Suite struct ToolkitManifestTests {

    // MARK: - Five templates decode

    @Test func pluginEntryDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "my-plugin", "displayName": "My Plugin",
            "install": ["kind": "plugin", "source": "athens96/mighty-styles", "pluginID": "mighty-styles@mighty-styles"] as [String: Any],
        ])
        guard case .plugin(let source, let pluginID) = entry.install else { Issue.record("Expected plugin"); return }
        #expect(source == "athens96/mighty-styles")
        #expect(pluginID == "mighty-styles@mighty-styles")
        #expect(entry.source == .user)
    }

    @Test func pluginEntryWithHttpsSourceDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "remote-plugin", "displayName": "Remote Plugin",
            "install": ["kind": "plugin", "source": "https://github.com/example/plugin.git", "pluginID": "plugin@plugin"] as [String: Any],
        ])
        guard case .plugin(let source, _) = entry.install else { Issue.record("Expected plugin"); return }
        #expect(source.hasPrefix("https://"))
    }

    @Test func mcpEntryDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "my-mcp", "displayName": "My MCP Server",
            "install": ["kind": "mcp", "name": "my-server", "executable": "/usr/local/bin/python3", "args": ["--port", "3000"]] as [String: Any],
        ])
        guard case .mcp(let name, let executable, let args) = entry.install else { Issue.record("Expected mcp"); return }
        #expect(name == "my-server")
        #expect(executable == "/usr/local/bin/python3")
        #expect(args == ["--port", "3000"])
    }

    @Test func mcpEntryWithPathExecutableDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "node-mcp", "displayName": "Node MCP",
            "install": ["kind": "mcp", "name": "node-server", "executable": "node", "args": []] as [String: Any],
        ])
        guard case .mcp(_, let executable, _) = entry.install else { Issue.record("Expected mcp"); return }
        #expect(executable == "node")
    }

    @Test func skillEntryDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "my-skill", "displayName": "My Skill",
            "install": ["kind": "skill", "url": "https://github.com/athens96/my-skill.git"] as [String: Any],
        ])
        guard case .skill(let url) = entry.install else { Issue.record("Expected skill"); return }
        #expect(url == "https://github.com/athens96/my-skill.git")
    }

    @Test func packageBrewEntryDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "my-brew", "displayName": "My Brew Package",
            "install": ["kind": "package", "manager": "brew", "name": "ripgrep"] as [String: Any],
        ])
        guard case .package(let manager, let name, _) = entry.install else { Issue.record("Expected package"); return }
        #expect(manager == .brew)
        #expect(name == "ripgrep")
    }

    @Test func packageNpmEntryDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "my-npm", "displayName": "My npm Package",
            "install": ["kind": "package", "manager": "npm", "name": "typescript"] as [String: Any],
        ])
        guard case .package(let manager, let name, _) = entry.install else { Issue.record("Expected package"); return }
        #expect(manager == .npm)
        #expect(name == "typescript")
    }

    @Test func repoScriptEntryDecodes() throws {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        let entry = try ToolkitEntryDecoder.decode([
            "id": "my-script", "displayName": "My Script",
            "install": ["kind": "repoScript", "url": "https://github.com/athens96/setup.git",
                        "ref": sha, "scriptPath": "scripts/install.sh"] as [String: Any],
        ])
        guard case .repoScript(let url, let ref, let scriptPath) = entry.install else { Issue.record("Expected repoScript"); return }
        #expect(url == "https://github.com/athens96/setup.git")
        #expect(ref == sha)
        #expect(scriptPath == "scripts/install.sh")
    }

    @Test func repoScriptWithTagRefDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "tagged-script", "displayName": "Tagged Script",
            "install": ["kind": "repoScript", "url": "https://github.com/example/setup.git",
                        "ref": "v1.2.3", "scriptPath": "install.sh"] as [String: Any],
        ])
        guard case .repoScript(_, let ref, _) = entry.install else { Issue.record("Expected repoScript"); return }
        #expect(ref == "v1.2.3")
    }

    // MARK: - Shell strings and unknown kinds rejected

    @Test func unknownInstallKindIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "shellCommand", "command": "brew install foo"] as [String: Any],
            ])
        }
    }

    @Test func unknownTopLevelFieldIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad", "command": "rm -rf /",
                "install": ["kind": "package", "manager": "brew", "name": "ripgrep"] as [String: Any],
            ])
        }
    }

    @Test func unknownInstallFieldIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "plugin", "source": "owner/repo", "pluginID": "p@m", "extra": "evil"] as [String: Any],
            ])
        }
    }

    @Test func mcpWithUnknownFieldIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "mcp", "name": "srv", "executable": "node", "args": [], "shellString": "echo hi"] as [String: Any],
            ])
        }
    }

    @Test func skillWithUnknownFieldIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "skill", "url": "https://github.com/x/y.git", "postInstall": "echo done"] as [String: Any],
            ])
        }
    }

    // MARK: - repoScript-specific rejections

    @Test func repoScriptWithArgumentsFieldIsRejected() {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "repoScript", "url": "https://github.com/x/y.git",
                            "ref": sha, "scriptPath": "install.sh", "arguments": ["--flag"]] as [String: Any],
            ])
        }
    }

    @Test func repoScriptWithDotDotInPathIsRejected() {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        for path in ["../escape.sh", "scripts/../../escape.sh", "a/../b/../c.sh"] {
            #expect(throws: (any Error).self, "path '\(path)' should be rejected") {
                try ToolkitEntryDecoder.decode([
                    "id": "bad", "displayName": "Bad",
                    "install": ["kind": "repoScript", "url": "https://github.com/x/y.git",
                                "ref": sha, "scriptPath": path] as [String: Any],
                ])
            }
        }
    }

    @Test func repoScriptWithAbsolutePathIsRejected() {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "repoScript", "url": "https://github.com/x/y.git",
                            "ref": sha, "scriptPath": "/absolute/path.sh"] as [String: Any],
            ])
        }
    }

    @Test func repoScriptWithBranchRefIsRejected() {
        // refs with slashes (like origin/main) are not a valid 40-hex SHA or simple tag
        for badRef in ["", "origin/main", "refs/heads/main", "  "] {
            let result = try? ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "repoScript", "url": "https://github.com/x/y.git",
                            "ref": badRef, "scriptPath": "install.sh"] as [String: Any],
            ])
            #expect(result == nil, "ref '\(badRef)' should be rejected")
        }
    }

    @Test func repoScriptWithNonHttpsUrlIsRejected() {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        for bad in ["http://github.com/x/y.git", "git://github.com/x/y.git", "ssh://git@github.com/x/y.git"] {
            #expect(throws: (any Error).self, "url '\(bad)' should be rejected") {
                try ToolkitEntryDecoder.decode([
                    "id": "bad", "displayName": "Bad",
                    "install": ["kind": "repoScript", "url": bad,
                                "ref": sha, "scriptPath": "install.sh"] as [String: Any],
                ])
            }
        }
    }

    // MARK: - Build commands have valid executables

    /// Commands exactly as the runner builds them. A repoScript gets an approval
    /// so it builds; its last command is the script inside the clone.
    private func commands(for spec: ToolkitInstallSpec, sha: String) -> [[String]] {
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user, install: spec)
        let context = ToolkitProbeContext(home: URL(fileURLWithPath: "/tmp/toolkit-home"), appDataDir: URL(fileURLWithPath: "/tmp/toolkit-data"))
        return ToolkitRunner.installCommands(for: entry, approval: ToolkitApproval(contentHash: "h", resolvedCommit: sha), context: context)
    }

    @Test func buildCommandsHaveValidExecutables() throws {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        let specs: [ToolkitInstallSpec] = [
            .plugin(source: "owner/repo", pluginID: "tool@repo"),
            .mcp(name: "server", executable: "/usr/local/bin/python3", args: []),
            .skill(url: "https://github.com/x/skill.git"),
            .package(manager: .brew, name: "ripgrep"),
            .package(manager: .npm, name: "typescript"),
            .repoScript(url: "https://github.com/x/setup.git", ref: sha, scriptPath: "scripts/run.sh"),
        ]
        let allowed: Set<String> = ["claude", "git", "brew", "npm"]
        for spec in specs {
            let commands = commands(for: spec, sha: sha)
            #expect(!commands.isEmpty, "\(spec) produced no commands")
            for (index, argv) in commands.enumerated() {
                #expect(!argv.isEmpty, "Empty argv in \(spec)")
                let executable = argv[0]
                if case .repoScript = spec, index == commands.count - 1 {
                    #expect(executable == "/tmp/toolkit-data/toolkit-clones/\(sha)/scripts/run.sh")
                } else {
                    #expect(allowed.contains(executable),
                        "Executable '\(executable)' not in allowed set for \(spec)")
                }
            }
        }
    }

    @Test func buildCommandsAreArgvArraysWithNoShellString() throws {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        let specs: [ToolkitInstallSpec] = [
            .plugin(source: "athens96/mighty-styles", pluginID: "mighty-styles@mighty-styles"),
            .mcp(name: "my-server", executable: "node", args: ["--port", "8080"]),
            .skill(url: "https://github.com/x/skill.git"),
            .package(manager: .brew, name: "gh"),
            .package(manager: .npm, name: "pnpm"),
            .repoScript(url: "https://github.com/x/setup.git", ref: sha, scriptPath: "install.sh"),
        ]
        let shellMetachars = CharacterSet(charactersIn: ";|&$`'\"\\<>()")
        for spec in specs {
            for argv in commands(for: spec, sha: sha) {
                for element in argv {
                    #expect(element.unicodeScalars.allSatisfy { !shellMetachars.contains($0) } || element.hasPrefix("/"),
                        "Shell metachar found in argv element '\(element)' for \(spec)")
                }
            }
        }
    }

    @Test func pluginCommandsMatchTheClaudeCLI() {
        let commands = commands(for: .plugin(source: "athens96/mighty-styles", pluginID: "mighty-styles@mighty-styles"), sha: "")
        #expect(commands == [
            ["claude", "plugin", "marketplace", "add", "--scope", "user", "athens96/mighty-styles"],
            ["claude", "plugin", "install", "mighty-styles@mighty-styles", "--scope", "user", "--json"],
        ])
    }

    // MARK: - Bundled list

    @Test func bundledListContainsExactlyOneMightyStylesEntry() {
        let entries = ToolkitBundled.entries
        #expect(entries.count == 1)
        let entry = entries[0]
        #expect(entry.entryId == "mighty-styles")
        #expect(entry.source == .bundled)
        guard case .plugin(let source, let pluginID) = entry.install else {
            Issue.record("Bundled entry must be a plugin"); return
        }
        #expect(source == "athens96/mighty-styles")
        #expect(pluginID == "mighty-styles@mighty-styles")
    }

    @Test func bundledEntryHasValidPluginID() {
        let entry = ToolkitBundled.entries[0]
        guard case .plugin(_, let pluginID) = entry.install else { return }
        #expect(ToolkitEntryDecoder.validIdentifier(entry.entryId))
        let parts = pluginID.split(separator: "@")
        #expect(parts.count == 2)
        #expect(parts.allSatisfy { ToolkitEntryDecoder.validIdentifier(String($0)) })
    }
}
