import Foundation
import Testing
@testable import MightyCore

// MARK: - Helpers

private func tempDir(_ label: String) -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("toolkit-probe-\(label)-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func writeJSON(_ value: Any, to url: URL) throws {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try JSONSerialization.data(withJSONObject: value).write(to: url)
}

private func touch(_ url: URL) throws {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data().write(to: url)
}

private func fakeExecutable(_ dir: URL, name: String) throws -> String {
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let url = dir.appendingPathComponent(name)
    try Data("#!/bin/sh\n".utf8).write(to: url)
    try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
    return dir.path
}

private func context(home: URL, env: [String: String] = [:], appData: URL? = nil,
                     brewPrefixes: [String] = []) -> ToolkitProbeContext {
    ToolkitProbeContext(home: home, environment: env,
                        appDataDir: appData ?? home.appendingPathComponent("appdata"),
                        brewPrefixes: brewPrefixes)
}

private let fakeSHA = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"

// MARK: - Suite

@Suite struct ToolkitProbeTests {

    // MARK: – Plugin probe

    @Test func pluginMissingWhenFileAbsent() {
        let home = tempDir("plugin-absent")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "x", displayName: "X", source: .user,
                                 install: .plugin(source: "o/r", pluginID: "x@m"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func pluginInstalledWhenUserScopeRecordPresent() throws {
        let home = tempDir("plugin-user")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["version": 2, "plugins": ["mighty-styles@mighty-styles": [["installPath": "/x", "scope": "user"]]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let entry = ToolkitEntry(entryId: "mighty-styles", displayName: "Mighty Styles", source: .bundled,
                                 install: .plugin(source: "athens96/mighty-styles", pluginID: "mighty-styles@mighty-styles"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .installed)
    }

    @Test func pluginMissingWhenOnlyProjectScopeRecord() throws {
        let home = tempDir("plugin-project")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["version": 2, "plugins": ["my-plugin@m": [["installPath": "/x", "scope": "project"]]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .plugin(source: "o/r", pluginID: "my-plugin@m"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func pluginMissingWhenOnlyLocalScopeRecord() throws {
        let home = tempDir("plugin-local")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["version": 2, "plugins": ["my-plugin@m": [["installPath": "/x", "scope": "local"]]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .plugin(source: "o/r", pluginID: "my-plugin@m"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func pluginMissingWhenKeyDoesNotMatchExactPluginID() throws {
        let home = tempDir("plugin-mismatch")
        defer { try? FileManager.default.removeItem(at: home) }
        // key is different plugin
        try writeJSON(
            ["version": 2, "plugins": ["other@m": [["installPath": "/x", "scope": "user"]]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .plugin(source: "o/r", pluginID: "my-plugin@m"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func pluginInstalledWhenMixedScopesAndUserPresent() throws {
        let home = tempDir("plugin-mixed")
        defer { try? FileManager.default.removeItem(at: home) }
        // Two records: project scope then user scope — installed because user exists
        try writeJSON(
            ["version": 2, "plugins": ["tool@m": [
                ["installPath": "/x", "scope": "project"],
                ["installPath": "/x", "scope": "user"],
            ]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .plugin(source: "o/r", pluginID: "tool@m"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .installed)
    }

    // MARK: – MCP probe

    @Test func mcpMissingWhenFileAbsent() {
        let home = tempDir("mcp-absent")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .mcp(name: "my-server", executable: "node", args: []))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func mcpInstalledWhenNameInClaudeJson() throws {
        let home = tempDir("mcp-present")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["mcpServers": ["my-server": ["command": "node", "args": []]]],
            to: home.appendingPathComponent(".claude.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .mcp(name: "my-server", executable: "node", args: []))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .installed)
    }

    @Test func mcpMissingWhenNameAbsentFromClaudeJson() throws {
        let home = tempDir("mcp-missing-key")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["mcpServers": ["other-server": ["command": "node"]]],
            to: home.appendingPathComponent(".claude.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .mcp(name: "my-server", executable: "node", args: []))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func mcpMissingWhenClaudeJsonHasNoMcpServersKey() throws {
        let home = tempDir("mcp-no-mcpServers")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(["version": 1], to: home.appendingPathComponent(".claude.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .mcp(name: "my-server", executable: "node", args: []))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    // MARK: – Skill probe

    @Test func skillMissingWhenSkillMdAbsent() {
        let home = tempDir("skill-absent")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .skill(url: "https://github.com/x/my-skill.git"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func skillInstalledWhenSkillMdExists() throws {
        let home = tempDir("skill-present")
        defer { try? FileManager.default.removeItem(at: home) }
        try touch(home.appendingPathComponent(".claude/skills/my-skill/SKILL.md"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .skill(url: "https://github.com/x/my-skill.git"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .installed)
    }

    @Test func skillNameDerivedByStrippingDotGit() throws {
        let home = tempDir("skill-name-derive")
        defer { try? FileManager.default.removeItem(at: home) }
        // URL: https://github.com/athens96/setup-toolkit.git → dir: setup-toolkit
        try touch(home.appendingPathComponent(".claude/skills/setup-toolkit/SKILL.md"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .skill(url: "https://github.com/athens96/setup-toolkit.git"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .installed)
    }

    @Test func skillNameWithoutDotGitUsedAsIs() throws {
        let home = tempDir("skill-no-git-ext")
        defer { try? FileManager.default.removeItem(at: home) }
        try touch(home.appendingPathComponent(".claude/skills/my-skill/SKILL.md"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .skill(url: "https://github.com/x/my-skill"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .installed)
    }

    // MARK: – Package probe

    @Test func brewMissingWhenPrefixListEmpty() {
        let home = tempDir("brew-no-prefix")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .package(manager: .brew, name: "ripgrep"))
        // Empty prefix list → no path to check → always missing
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home, brewPrefixes: [])) == .missing)
    }

    @Test func brewInstalledWhenOptNameDirExistsUnderFakePrefix() throws {
        let home = tempDir("brew-present")
        defer { try? FileManager.default.removeItem(at: home) }
        // Create a fake brew prefix in the temp dir
        let fakePrefix = home.appendingPathComponent("fake-homebrew")
        try FileManager.default.createDirectory(at: fakePrefix.appendingPathComponent("opt/ripgrep"), withIntermediateDirectories: true)
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .package(manager: .brew, name: "ripgrep"))
        let ctx = ToolkitProbeContext(home: home, environment: [:], appDataDir: home,
                                     brewPrefixes: [fakePrefix.path])
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx) == .installed)
    }

    @Test func brewMissingWhenOptNameDirAbsent() throws {
        let home = tempDir("brew-absent-name")
        defer { try? FileManager.default.removeItem(at: home) }
        let fakePrefix = home.appendingPathComponent("fake-homebrew")
        // Create prefix dir but not the opt/name subdir
        try FileManager.default.createDirectory(at: fakePrefix.appendingPathComponent("opt"), withIntermediateDirectories: true)
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .package(manager: .brew, name: "ripgrep"))
        let ctx = ToolkitProbeContext(home: home, environment: [:], appDataDir: home,
                                     brewPrefixes: [fakePrefix.path])
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx) == .missing)
    }

    @Test func npmInstalledWhenModuleDirExistsOnPath() throws {
        let home = tempDir("npm-present")
        defer { try? FileManager.default.removeItem(at: home) }
        // Create a fake npm bin structure in temp: <binDir>/npm and <binDir>/../lib/node_modules/typescript
        let binDir = home.appendingPathComponent("fake-npm-bin")
        let _ = try fakeExecutable(binDir, name: "npm")
        let moduleDir = binDir.deletingLastPathComponent().appendingPathComponent("lib/node_modules/typescript")
        try FileManager.default.createDirectory(at: moduleDir, withIntermediateDirectories: true)

        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .package(manager: .npm, name: "typescript"))
        let ctx = ToolkitProbeContext(home: home, environment: ["PATH": binDir.path], appDataDir: home)
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx) == .installed)
    }

    @Test func npmMissingWhenModuleDirAbsent() throws {
        let home = tempDir("npm-absent")
        defer { try? FileManager.default.removeItem(at: home) }
        let binDir = home.appendingPathComponent("fake-npm-bin")
        let _ = try fakeExecutable(binDir, name: "npm")
        // No node_modules directory created

        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .package(manager: .npm, name: "typescript"))
        let ctx = ToolkitProbeContext(home: home, environment: ["PATH": binDir.path], appDataDir: home)
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx) == .missing)
    }

    @Test func npmMissingWhenNpmNotOnPath() {
        let home = tempDir("npm-no-path")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .package(manager: .npm, name: "typescript"))
        let ctx = ToolkitProbeContext(home: home, environment: ["PATH": "/nonexistent-path"], appDataDir: home)
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx) == .missing)
    }

    // MARK: – RepoScript probe

    @Test func repoScriptMissingWhenNoApproval() {
        let home = tempDir("repo-no-approval")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .repoScript(url: "https://github.com/x/y.git",
                                                      ref: fakeSHA, scriptPath: "install.sh"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: context(home: home)) == .missing)
    }

    @Test func repoScriptMissingWhenApprovalHasNoResolvedCommit() {
        let home = tempDir("repo-no-commit")
        defer { try? FileManager.default.removeItem(at: home) }
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .repoScript(url: "https://github.com/x/y.git",
                                                      ref: "v1.0", scriptPath: "install.sh"))
        let approval = ToolkitApproval(contentHash: "abc", resolvedCommit: nil)
        #expect(ToolkitProbe.probe(entry: entry, approval: approval, context: context(home: home)) == .missing)
    }

    @Test func repoScriptMissingWhenMarkerAbsent() {
        let home = tempDir("repo-no-marker")
        defer { try? FileManager.default.removeItem(at: home) }
        let appData = home.appendingPathComponent("appdata")
        let approval = ToolkitApproval(contentHash: "abc", resolvedCommit: fakeSHA)
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .repoScript(url: "https://github.com/x/y.git",
                                                      ref: fakeSHA, scriptPath: "install.sh"))
        #expect(ToolkitProbe.probe(entry: entry, approval: approval, context: context(home: home, appData: appData)) == .missing)
    }

    @Test func repoScriptInstalledWhenMarkerExists() throws {
        let home = tempDir("repo-marker")
        defer { try? FileManager.default.removeItem(at: home) }
        let appData = home.appendingPathComponent("appdata")
        let approval = ToolkitApproval(contentHash: "abc", resolvedCommit: fakeSHA)
        // Write the marker
        let marker = ToolkitProbe.repoScriptMarker(appDataDir: appData, resolvedCommit: fakeSHA)
        try touch(marker)
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .repoScript(url: "https://github.com/x/y.git",
                                                      ref: fakeSHA, scriptPath: "install.sh"))
        #expect(ToolkitProbe.probe(entry: entry, approval: approval, context: context(home: home, appData: appData)) == .installed)
    }

    @Test func repoScriptMarkerPathEmbedsSHA() {
        let appData = URL(fileURLWithPath: "/tmp/appdata")
        let marker = ToolkitProbe.repoScriptMarker(appDataDir: appData, resolvedCommit: fakeSHA)
        #expect(marker.path.contains(fakeSHA))
        #expect(marker.lastPathComponent == ".toolkit-script-complete")
    }

    // MARK: – Workspace isolation

    @Test func pluginProbeIsWorkspaceIndependent() throws {
        let home = tempDir("plugin-ws-iso")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["version": 2, "plugins": ["tool@m": [["installPath": "/x", "scope": "user"]]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .plugin(source: "o/r", pluginID: "tool@m"))
        // Two different appDataDir values give the same result
        let ctx1 = ToolkitProbeContext(home: home, environment: [:], appDataDir: home.appendingPathComponent("ws1"))
        let ctx2 = ToolkitProbeContext(home: home, environment: [:], appDataDir: home.appendingPathComponent("ws2"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx1) == .installed)
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx2) == .installed)
    }

    @Test func mcpProbeIsWorkspaceIndependent() throws {
        let home = tempDir("mcp-ws-iso")
        defer { try? FileManager.default.removeItem(at: home) }
        try writeJSON(
            ["mcpServers": ["my-server": ["command": "node"]]],
            to: home.appendingPathComponent(".claude.json"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .mcp(name: "my-server", executable: "node", args: []))
        let ctx1 = ToolkitProbeContext(home: home, environment: [:], appDataDir: home.appendingPathComponent("ws1"))
        let ctx2 = ToolkitProbeContext(home: home, environment: [:], appDataDir: home.appendingPathComponent("ws2"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx1) == .installed)
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx2) == .installed)
    }

    @Test func skillProbeIsWorkspaceIndependent() throws {
        let home = tempDir("skill-ws-iso")
        defer { try? FileManager.default.removeItem(at: home) }
        try touch(home.appendingPathComponent(".claude/skills/my-skill/SKILL.md"))
        let entry = ToolkitEntry(entryId: "e", displayName: "E", source: .user,
                                 install: .skill(url: "https://github.com/x/my-skill.git"))
        let ctx1 = ToolkitProbeContext(home: home, environment: [:], appDataDir: home.appendingPathComponent("ws1"))
        let ctx2 = ToolkitProbeContext(home: home, environment: [:], appDataDir: home.appendingPathComponent("ws2"))
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx1) == .installed)
        #expect(ToolkitProbe.probe(entry: entry, approval: nil, context: ctx2) == .installed)
    }
}
