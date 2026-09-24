import Foundation
import Testing
@testable import MightyCore

@Suite(.serialized)
struct OmcAgentCatalogTests {

    private func makeHome() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("omc-catalog-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func cleanup(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }

    private func writeInstalledPlugins(_ plugins: [String: Any], in home: URL) throws {
        let dir = home.appendingPathComponent(".claude/plugins")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let json: [String: Any] = ["version": 2, "plugins": plugins]
        let data = try JSONSerialization.data(withJSONObject: json)
        try data.write(to: dir.appendingPathComponent("installed_plugins.json"))
    }

    private func makeAgentsDir(installBase: URL) throws -> URL {
        let dir = installBase.appendingPathComponent("agents")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private func writeAgentFile(name: String, model: String, in agentsDir: URL) throws {
        let content = "---\nname: \(name)\nmodel: \(model)\n---\n# \(name)\n"
        try Data(content.utf8).write(to: agentsDir.appendingPathComponent("\(name).md"))
    }

    // MARK: - Hidden when not installed

    @Test func returnsNilWhenNoInstalledPluginsFile() throws {
        let home = try makeHome(); defer { cleanup(home) }
        #expect(OmcAgentCatalog(homeDirectory: home).scan() == nil)
    }

    @Test func returnsNilWhenOmcNotInPlugins() throws {
        let home = try makeHome(); defer { cleanup(home) }
        try writeInstalledPlugins(
            ["other-plugin@registry": [["scope": "user", "installPath": "/some/path"]]],
            in: home
        )
        #expect(OmcAgentCatalog(homeDirectory: home).scan() == nil)
    }

    @Test func returnsNilWhenOnlyProjectScopeRecord() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let installBase = home.appendingPathComponent("cache/omc/5.4.0")
        let agentsDir = try makeAgentsDir(installBase: installBase)
        try writeAgentFile(name: "planner", model: "sonnet", in: agentsDir)
        try writeInstalledPlugins(
            ["oh-my-claudecode@omc": [["scope": "project", "installPath": installBase.path]]],
            in: home
        )
        #expect(OmcAgentCatalog(homeDirectory: home).scan() == nil)
    }

    @Test func returnsNilWhenAgentsDirAbsent() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let installBase = home.appendingPathComponent("cache/omc/5.4.0")
        try FileManager.default.createDirectory(at: installBase, withIntermediateDirectories: true)
        try writeInstalledPlugins(
            ["oh-my-claudecode@omc": [["scope": "user", "installPath": installBase.path]]],
            in: home
        )
        #expect(OmcAgentCatalog(homeDirectory: home).scan() == nil)
    }

    @Test func returnsNilWhenAgentsDirHasNoMdFiles() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let installBase = home.appendingPathComponent("cache/omc/5.4.0")
        let agentsDir = try makeAgentsDir(installBase: installBase)
        // Write a non-.md file
        try Data("{}".utf8).write(to: agentsDir.appendingPathComponent("readme.txt"))
        try writeInstalledPlugins(
            ["oh-my-claudecode@omc": [["scope": "user", "installPath": installBase.path]]],
            in: home
        )
        #expect(OmcAgentCatalog(homeDirectory: home).scan() == nil)
    }

    // MARK: - Agent list from fixture install

    @Test func agentListFromFixtureInstall() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let installBase = home.appendingPathComponent("cache/omc/5.4.0")
        let agentsDir = try makeAgentsDir(installBase: installBase)
        try writeAgentFile(name: "planner", model: "sonnet", in: agentsDir)
        try writeAgentFile(name: "code-reviewer", model: "opus", in: agentsDir)
        try writeAgentFile(name: "executor", model: "haiku", in: agentsDir)
        try writeInstalledPlugins(
            ["oh-my-claudecode@omc": [["scope": "user", "installPath": installBase.path]]],
            in: home
        )

        let result = OmcAgentCatalog(homeDirectory: home).scan()
        #expect(result != nil)
        #expect(result?["planner"] == "sonnet")
        #expect(result?["codeReviewer"] == "opus")
        #expect(result?["executor"] == "haiku")
    }

    @Test func frontmatterModelFallsBackToDefaultWhenAbsent() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let installBase = home.appendingPathComponent("cache/omc/5.4.0")
        let agentsDir = try makeAgentsDir(installBase: installBase)
        // File with no model: in frontmatter
        let noModel = "---\nname: architect\ndescription: Some agent\n---\nBody\n"
        try Data(noModel.utf8).write(to: agentsDir.appendingPathComponent("architect.md"))
        try writeInstalledPlugins(
            ["oh-my-claudecode@omc": [["scope": "user", "installPath": installBase.path]]],
            in: home
        )

        let result = OmcAgentCatalog(homeDirectory: home).scan()
        #expect(result?["architect"] == "default")
    }

    @Test func userScopeWinsOverProjectScope() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let installBase = home.appendingPathComponent("cache/omc/5.4.0")
        let agentsDir = try makeAgentsDir(installBase: installBase)
        try writeAgentFile(name: "planner", model: "opus", in: agentsDir)
        try writeInstalledPlugins([
            "oh-my-claudecode@omc": [
                ["scope": "project", "installPath": "/no/agents/here"],
                ["scope": "user", "installPath": installBase.path]
            ]
        ], in: home)

        let result = OmcAgentCatalog(homeDirectory: home).scan()
        #expect(result?["planner"] == "opus")
    }

    // MARK: - CamelCase key conversion

    @Test func camelCaseKeyConversion() {
        #expect(OmcAgentCatalog.kebabToCamelCase("planner")             == "planner")
        #expect(OmcAgentCatalog.kebabToCamelCase("executor")            == "executor")
        #expect(OmcAgentCatalog.kebabToCamelCase("code-reviewer")       == "codeReviewer")
        #expect(OmcAgentCatalog.kebabToCamelCase("security-reviewer")   == "securityReviewer")
        #expect(OmcAgentCatalog.kebabToCamelCase("test-engineer")       == "testEngineer")
        #expect(OmcAgentCatalog.kebabToCamelCase("qa-tester")           == "qaTester")
        #expect(OmcAgentCatalog.kebabToCamelCase("git-master")          == "gitMaster")
        #expect(OmcAgentCatalog.kebabToCamelCase("code-simplifier")     == "codeSimplifier")
        #expect(OmcAgentCatalog.kebabToCamelCase("document-specialist") == "documentSpecialist")
    }

    // MARK: - Suite marker

    @Test func markerOmcCatalogOK() {
        print("Suite OmcAgentCatalogTests passed")
    }
}
