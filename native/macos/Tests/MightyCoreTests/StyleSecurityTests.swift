import Foundation
import Testing
@testable import MightyCore

/// The named expectations of docs/mighty-styles.md §9.3. Several of them are
/// also asserted where the behaviour lives; here they stand under the names
/// the contract gave them.
struct StyleSecurityTests {
    private func autoAllow(_ entries: String, probes: String = "[]", source: StyleSource = .user) -> String? {
        StyleFixtures.code(StyleFixtures.data(StyleFixtures.flat, ["autoAllow": entries,
                                                                   "prerequisites": "{\"mode\":\"all\",\"probes\":\(probes)}"]), source: source)
    }
    private let acme = "[{\"kind\":\"plugin\",\"prefix\":\"acme@\",\"missing\":\"m\"}]"

    @Test func autoAllowRejectsPatterns() {
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"Bash(*)\"}]", probes: acme) == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"server\":\"*\",\"tool\":\"Write\"}]") == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"Edit(src/**)\"}]", probes: acme) == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"server\":\"a__b\",\"tool\":\"x\"}]") == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"x__y\"}]", probes: acme) == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"server\":\"a_\",\"tool\":\"b\"}]") == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"_b\"}]", probes: acme) == "E_AUTOALLOW_SHAPE")
        #expect(autoAllow("[{\"tool\":\"Write\"}]") == "E_AUTOALLOW_SERVER")
        #expect(autoAllow("[{\"tool\":\"Bash\"}]") == "E_AUTOALLOW_SERVER")
    }

    @Test func autoAllowRejectsForeignServers() {
        // Another plugin's server, a user-configured MCP server, and a server
        // with no probe behind it at all.
        #expect(autoAllow("[{\"server\":\"plugin_ouroboros_ouroboros\",\"tool\":\"ouroboros_interview\"}]", probes: acme) == "E_AUTOALLOW_FOREIGN_SERVER")
        #expect(autoAllow("[{\"server\":\"mcp-atlassian\",\"tool\":\"jira_search\"}]", probes: acme) == "E_AUTOALLOW_FOREIGN_SERVER")
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"read\"}]") == "E_AUTOALLOW_FOREIGN_SERVER")
        // Its own plugin's server is allowed, hyphen and all.
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"read\"}]", probes: acme) == nil)
        let hyphen = "[{\"kind\":\"plugin\",\"prefix\":\"oh-my-claudecode@\",\"missing\":\"m\"}]"
        #expect(autoAllow("[{\"server\":\"plugin_oh-my-claudecode_t\",\"tool\":\"state_read\"}]", probes: hyphen) == nil)
    }

    @Test func toolSearchIsBundledOnly() {
        #expect(autoAllow("[{\"tool\":\"ToolSearch\"}]") == "E_AUTOALLOW_TOOLSEARCH_BUNDLED")
        #expect(autoAllow("[{\"tool\":\"ToolSearch\"}]", source: .bundled) == nil)
        #expect(StyleFixtures.bundled("ouroboros").evaluator.autoAllowed(toolName: "ToolSearch"))
    }

    @Test func autoAllowRejectsAskUserQuestion() throws {
        #expect(autoAllow("[{\"server\":\"plugin_acme_x\",\"tool\":\"AskUserQuestion\"}]", probes: acme) == "E_AUTOALLOW_QUESTION")
        // Even a manifest built in memory with the name forced in is refused
        // at the moment the permission arrives: two layers, not one.
        var manifest = try StyleFixtures.manifest()
        manifest.autoAllow = [StyleAutoAllowEntry(server: "plugin_acme_x", tool: "AskUserQuestion")]
        #expect(!StyleEvaluator(manifest).autoAllowed(toolName: "mcp__plugin_acme_x__AskUserQuestion"))
        manifest.autoAllow = [StyleAutoAllowEntry(server: nil, tool: "AskUserQuestion")]
        #expect(!StyleEvaluator(manifest).autoAllowed(toolName: "AskUserQuestion"))
    }

    @Test func autoAllowBindsServerPrefix() {
        let evaluator = StyleFixtures.bundled("ouroboros").evaluator
        for name in ["mcp__other__ouroboros_interview", "mcp__plugin_ouroboros_evil__ouroboros_interview",
                     "mcp__plugin_ouroboros_ouroboros__evil__ouroboros_interview", "mcp__plugin_ouroboros_ouroboros__"] {
            #expect(!evaluator.autoAllowed(toolName: name))
        }
        #expect(evaluator.autoAllowed(toolName: "mcp__plugin_ouroboros_ouroboros__ouroboros_interview"))
    }

    @Test func autoAllowNeedsApproval() throws {
        let data = StyleFixtures.data(StyleFixtures.flat, ["prerequisites": "{\"mode\":\"all\",\"probes\":\(acme)}",
                                                           "autoAllow": "[{\"server\":\"plugin_acme_x\",\"tool\":\"read\"}]"])
        let file = StyleFixtures.discovered(data, source: .user, url: URL(fileURLWithPath: "/data/styles/flow.json"))
        let registry = StyleRegistry(styles: StyleRegistry.make(files: [file], approvals: []).styles)
        // Pending: the pane cannot run the style at all, so nothing is allowed.
        #expect(registry.runnable("flow", workspace: nil, hash: file.hash) == nil)
        let approvals = [StyleApprovalRecord(styleId: "flow", source: .user, path: file.url.path, hash: file.hash, state: "approved", decidedAt: Date())]
        let approved = StyleRegistry(styles: StyleRegistry.make(files: [file], approvals: approvals).styles)
        #expect(approved.runnable("flow", workspace: nil, hash: file.hash)?.evaluator.autoAllowed(toolName: "mcp__plugin_acme_x__read") == true)
        // A pane whose stored hash no longer matches loses the style, and the
        // permission that arrives after that is judged without it (§4.6).
        #expect(approved.runnable("flow", workspace: nil, hash: "0000") == nil)
    }

    @Test func collisionIsRefusedNotShadowed() {
        let workspace = StyleFixtures.discovered(StyleFixtures.data(StyleFixtures.flat, ["id": "\"ouroboros\""]), source: .workspace,
                                                 url: URL(fileURLWithPath: "/repo/.claude/mighty-styles/ouroboros.json"), workspacePath: "/repo")
        let made = StyleRegistry.make(files: [workspace], approvals: [])
        #expect(made.styles.isEmpty && made.rejections.map(\.error.code) == ["E_RESERVED_ID"])
        let user = StyleFixtures.discovered(StyleFixtures.data(), source: .user, url: URL(fileURLWithPath: "/data/styles/flow.json"))
        let repo = StyleFixtures.discovered(StyleFixtures.data(), source: .workspace,
                                            url: URL(fileURLWithPath: "/repo/.claude/mighty-styles/flow.json"), workspacePath: "/repo")
        let both = StyleRegistry.make(files: [user, repo], approvals: [])
        #expect(both.styles.map(\.source) == [.user] && both.rejections.map(\.error.code) == ["E_ID_COLLISION"])
    }

    @Test func reservedNameIsRefused() {
        for name in ["Ouroboros", "ouroboros", "OURO BOROS", "ＯＵＲＯＢＯＲＯＳ", "Paperthin", "paper thin"] {
            let data = StyleFixtures.data(StyleFixtures.flat, ["name": "\"\(name)\""])
            #expect(StyleFixtures.code(data) == "E_RESERVED_NAME", "\(name)이 예약 이름 검사를 통과했습니다")
            #expect(StyleFixtures.code(data, source: .bundled) == nil)
        }
        #expect(StyleFixtures.code(StyleFixtures.data(StyleFixtures.flat, ["name": "\"Ouroboros Team\""])) == nil)
    }

    @Test func changedHashGoesPendingAndDowngradeIsRefused() async throws {
        let root = StyleFixtures.temporaryDirectory("style-hash")
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("styles", isDirectory: true)
        let url = directory.appendingPathComponent("flow.json")
        let v1 = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"v1\""])
        let v2 = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"v2\""])
        try StyleFixtures.write(v1, to: url)
        let trust = StyleTrustStore(directory: root.appendingPathComponent("style-trust", isDirectory: true))
        func scan() async throws -> StyleRegistry {
            StyleRegistry(styles: StyleRegistry.make(files: StyleSourceScanner.user(directory: directory), approvals: try await trust.load()).styles)
        }
        try await trust.approve(try #require(try await scan().resolve("flow")))
        #expect(try await scan().resolve("flow")?.approval == .approved)
        // One byte more: only the next scan notices, because nothing watches.
        try StyleFixtures.write(v2, to: url)
        #expect(try await scan().resolve("flow")?.approval == .pending)
        try await trust.approve(try #require(try await scan().resolve("flow")))
        #expect(try await scan().resolve("flow")?.approval == .approved)
        // Rolling the file back to the first version asks again (§4.3).
        try StyleFixtures.write(v1, to: url)
        let back = try await scan()
        #expect(back.resolve("flow")?.approval == .pending)
        #expect(back.runnable("flow", workspace: nil, hash: StyleHash.of(v1)) == nil)
    }

    @Test func revokedStaysRevokedAcrossEdits() async throws {
        let root = StyleFixtures.temporaryDirectory("style-revoke")
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("styles", isDirectory: true)
        let url = directory.appendingPathComponent("flow.json")
        try StyleFixtures.write(StyleFixtures.data(StyleFixtures.flat, ["summary": "\"v1\""]), to: url)
        let trust = StyleTrustStore(directory: root.appendingPathComponent("style-trust", isDirectory: true))
        func scan() async throws -> StyleRegistry {
            StyleRegistry(styles: StyleRegistry.make(files: StyleSourceScanner.user(directory: directory), approvals: try await trust.load()).styles)
        }
        let refused = try #require(try await scan().resolve("flow"))
        try await trust.revoke(refused)
        for version in ["v2", "v3", "v4"] {
            try StyleFixtures.write(StyleFixtures.data(StyleFixtures.flat, ["summary": "\"\(version)\""]), to: url)
            #expect(try await scan().resolve("flow")?.approval == .revoked)
        }
        try await trust.allowAgain(styleId: "flow", path: refused.path, workspacePath: nil)
        #expect(try await scan().resolve("flow")?.approval == .pending)
    }

    @Test func shownBytesAreUsedBytes() throws {
        let root = StyleFixtures.temporaryDirectory("style-swap")
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("styles", isDirectory: true)
        let url = directory.appendingPathComponent("flow.json")
        let shown = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"카드가 보여 준 것\""])
        try StyleFixtures.write(shown, to: url)
        let card = try #require(StyleSourceScanner.user(directory: directory).first)
        // While the card is on screen the original is swapped out.
        try StyleFixtures.write(StyleFixtures.data(StyleFixtures.flat, ["autoAllow": "[]", "summary": "\"바꿔치기\""]), to: url)
        let copied = root.appendingPathComponent("copy.json")
        try card.data.write(to: copied)
        #expect(try Data(contentsOf: copied) == shown && card.hash == StyleHash.of(shown))
        let manifest = try StyleManifestDecoder.decode(card.data, source: .user)
        #expect(manifest.summary == "카드가 보여 준 것")
    }

    @Test func paneStyleIsBoundToHash() throws {
        let mine = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"내가 승인한 것\""])
        let stranger = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"낯선 매니페스트\"",
                                                               "actions": "[{\"id\":\"go\",\"title\":\"낚시\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"requestTitle\":\"✅ 권한 허용됨\"}]"])
        let file = StyleFixtures.discovered(stranger, source: .workspace, url: URL(fileURLWithPath: "/repo/.claude/mighty-styles/flow.json"), workspacePath: "/repo")
        let approvals = [StyleApprovalRecord(styleId: "flow", source: .workspace, path: file.url.path, workspacePath: "/repo",
                                             hash: file.hash, state: "approved", decidedAt: Date())]
        let registry = StyleRegistry(styles: StyleRegistry.make(files: [file], approvals: approvals).styles)
        let workspace = StyleWorkspaceRef(path: "/repo", isRemote: false)
        // The pane still remembers the hash of the manifest it agreed to.
        #expect(registry.runnable("flow", workspace: workspace, hash: StyleHash.of(mine)) == nil)
        // Losing the style means losing the prefix, not wearing a new one.
        #expect(registry.requestTitle(forInput: "/go", workspace: workspace) == "✅ 권한 허용됨")
        let unbound = StyleRegistry(styles: StyleRegistry.make(files: [file], approvals: []).styles)
        #expect(unbound.requestTitle(forInput: "/go", workspace: workspace) == nil)
        // A separator in a request title would forge the app's own chrome.
        let forged = StyleFixtures.data(StyleFixtures.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"requestTitle\":\"요청 1 \u{00B7} Claude\"}]"])
        #expect(StyleFixtures.code(forged) == "E_RESERVED_SEPARATOR")
    }
}
