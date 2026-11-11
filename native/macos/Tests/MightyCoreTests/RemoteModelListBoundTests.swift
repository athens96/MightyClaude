import Foundation
import Testing
@testable import MightyCore

/// A remote workspace's host info (`/v2/info`) carries each provider's model list. A Mac host sent each list as its CLI
/// read gave it (opencode up to 1,024 models), and a Mac client refused the host's whole info once any list passed 128
/// models. Now the host sends, and the client keeps, each list's first models up to what this app's own read of that
/// CLI keeps (`ProviderOptions.listedModelLimit`), so one long list never drops the host.
struct RemoteModelListBoundTests {
    private static func catalog(_ prefix: String, _ count: Int) -> [String: Any] {
        let models: [[String: Any]] = [["value": "default", "displayName": "Default", "description": ""]]
            + (1..<count).map { ["value": "\(prefix)\($0)", "displayName": "\(prefix)\($0)", "description": "row \($0)"] }
        return ["source": "cli", "models": models, "detail": prefix + " list"]
    }
    private static func provider(_ id: String, _ catalog: [String: Any]) -> [String: Any] {
        ["id": id, "name": id, "available": true, "detail": "", "modelCatalog": catalog,
         "capabilities": ["effort": true, "permissionModes": ["manual", "fullAccess"], "maxTurns": false, "maxBudgetUsd": false, "resume": true]]
    }
    /// info-darwin.json as an older Mac host sends it with long lists: Claude's (in both fields) 140 models, Codex's 200,
    /// pi's 600 and opencode's `opencode` (1,500 is more than any read keeps); Antigravity's short list as it is.
    static func olderHostInfo(opencode: Int = 1_500) throws -> Data {
        var object = try #require(JSONSerialization.jsonObject(with: RemoteWireFixtureTests.data("info-darwin.json")) as? [String: Any])
        var runtime = try #require(object["runtime"] as? [String: Any])
        var providers = try #require(runtime["providers"] as? [[String: Any]])
        let claude = catalog("claude-m", 140)
        runtime["modelCatalog"] = claude
        providers[0]["modelCatalog"] = claude
        providers[1]["modelCatalog"] = catalog("gpt-m", 200)
        providers += [provider("pi", catalog("anthropic/m", 600)), provider("opencode", catalog("opencode/m", opencode))]
        runtime["providers"] = providers; object["runtime"] = runtime
        return try JSONSerialization.data(withJSONObject: object)
    }
    private static func counts(_ runtime: RuntimeInfo?) -> [String: Int] {
        Dictionary(uniqueKeysWithValues: (runtime?.providers ?? []).map { ($0.id, $0.modelCatalog.models.count) })
    }

    @Test func anOlderHostsLongListsAreCutToWhatALocalReadKeeps() throws {
        let info = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.olderHostInfo())
        // Unbounded, the info still does not pass: the client cuts first.
        #expect(throws: (any Error).self) { try RemoteWorkspaceValidation.info(info) }
        var kept = info
        kept.runtime = RemoteWorkspaceValidation.bounded(info.runtime)
        try RemoteWorkspaceValidation.info(kept)
        #expect(Self.counts(kept.runtime) == ["claude": 129, "codex": 129, "gemini": 1, "pi": 513, "opencode": 1_025])
        #expect(kept.runtime.modelCatalog?.models.count == 129)
        // The first models in the host's order, and the rest of the list as it was.
        let opencode = try #require(kept.runtime.providers?.first { $0.id == "opencode" }?.modelCatalog)
        #expect(opencode.models.map(\.value) == ["default"] + (1..<1_025).map { "opencode/m\($0)" })
        #expect(opencode.detail == "opencode/m list" && opencode.source == "cli" && opencode.models.last?.description == "row 1024")
        // A list within its bound is left whole: opencode's 300 (refused before), Antigravity's one.
        let shorter = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.olderHostInfo(opencode: 300))
        let whole = RemoteWorkspaceValidation.bounded(shorter.runtime)
        #expect(whole.providers?.first { $0.id == "opencode" }?.modelCatalog == shorter.runtime.providers?.first { $0.id == "opencode" }?.modelCatalog)
        #expect(whole.providers?.first { $0.id == "gemini" }?.modelCatalog == shorter.runtime.providers?.first { $0.id == "gemini" }?.modelCatalog)
        var withinBounds = shorter; withinBounds.runtime = whole
        try RemoteWorkspaceValidation.info(withinBounds)
    }

    @Test func theBoundIsWhatEachReadOfItsCLIKeeps() throws {
        let bound = RemoteWorkspaceValidation.maximumModels
        #expect(ProviderService.normalizeClaudeCatalog((1...200).map { ["value": "claude-m\($0)"] }).models.count == bound("claude"))
        #expect(ProviderService.normalizeCodexCatalog((1...200).map { ["model": "gpt-m\($0)"] }).models.count == bound("codex"))
        #expect(ProviderService.normalizeAntigravityCatalog((1...200).map { "gemini-m\($0)\tGemini \($0)" }.joined(separator: "\n")).models.count == bound("gemini"))
        #expect(ProviderService.normalizePiCatalog((1...600).map { "anthropic m\($0) 200K 64K yes yes" }.joined(separator: "\n")).models.count == bound("pi"))
        #expect(ProviderService.normalizeOpencodeCatalog((1...1_500).map { "opencode/m\($0)" }.joined(separator: "\n")).models.count == bound("opencode"))
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("hermes-bound-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let picker = directory.appendingPathComponent("hermes-models.json")
        try JSONEncoder().encode((1...1_500).map { ModelOption(value: "anthropic:m\($0)", displayName: "m\($0)") }).write(to: picker)
        #expect(HermesAgent.cachedCatalog(url: picker).models.count == bound("hermes"))
        // A kept list is read back to the longest of them, whatever its provider: the host cuts it to its own.
        #expect(KeptModelLists.maximumModels == bound("opencode"))
    }

    @Test func aClientConnectsToAnOlderHostWithLongListsAndKeepsTheirFirstModels() async throws {
        let info = try Self.olderHostInfo()
        let fake = HTTPServer(address: "127.0.0.1", port: 0) { request in
            HTTPResponse(status: request.target.hasSuffix("/info") ? 200 : 404, body: request.target.hasSuffix("/info") ? info : Data("{}".utf8),
                         headers: [RemoteProtocol.header: "\(RemoteProtocol.version)"])
        }
        let port = Int(try await fake.start())
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-remote-lists-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let client = RemoteClientService(repository: StateRepository(directory: root, legacyStateURL: nil), dataDirectory: root, allowLoopbackForTests: true) { _ in }
        do {
            let connection = try await client.connect(name: "", address: "127.0.0.1:\(port)", key: String(repeating: "k", count: 43))
            #expect(connection.status == .connected && connection.workspaces.map(\.id) == ["ws-site"])
            #expect(Self.counts(connection.runtime) == ["claude": 129, "codex": 129, "gemini": 1, "pi": 513, "opencode": 1_025])
            #expect(connection.runtime?.providers?.first { $0.id == "codex" }?.modelCatalog.models.last?.value == "gpt-m128")
        } catch { await client.shutdown(); await fake.stop(); throw error }
        await client.shutdown(); await fake.stop()
    }

    @Test func aHostSharesOpencodesLongListAndAClientTakesIt() async throws {
        let fake = try FakeListCLIs(); defer { fake.remove() }
        // opencode lists more than its read keeps; the other CLIs answer with their recorded lists.
        try Data((1...1_500).map { "opencode/m\($0)" }.joined(separator: "\n").utf8).write(to: fake.state.appendingPathComponent("opencode-output"))
        let providers = try await fake.service()
        let root = fake.state.appendingPathComponent("remote")
        let folder = root.appendingPathComponent("project"), plugin = root.appendingPathComponent("plugin")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let repository = StateRepository(directory: root.appendingPathComponent("host"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Project", path: folder.path))
        try await repository.save(AppSnapshot(workspaces: [workspace]))
        let host = RemoteHostService(repository: repository, providers: providers, pluginDirectory: plugin, appVersion: "9.9.9", hostName: "Test host", allowLoopbackForTests: true)
        let client = RemoteClientService(repository: StateRepository(directory: root.appendingPathComponent("client"), legacyStateURL: nil),
                                         dataDirectory: root.appendingPathComponent("client"), allowLoopbackForTests: true) { _ in }
        func cleanup() async { await client.shutdown(); _ = await host.stop(); await providers.shutdown() }
        do {
            let state = try await host.start(workspaceIds: [workspace.id], port: 0)
            let key = try #require(state.key), port = try #require(state.port)
            // Before, this connection failed: the client refused the host for opencode's list of 1,025.
            let connection = try await client.connect(name: "", address: "127.0.0.1:\(port)", key: key)
            #expect(connection.status == .connected && connection.workspaces.map(\.id) == [workspace.id])
            let opencode = try #require(connection.runtime?.providers?.first { $0.id == "opencode" }?.modelCatalog)
            #expect(opencode.isFresh && opencode.models.count == 1_025 && opencode.models[1].value == "opencode/m1" && opencode.models.last?.value == "opencode/m1024")
            // The other lists came whole: Codex's recorded nine and its default.
            #expect(connection.runtime?.providers?.first { $0.id == "codex" }?.modelCatalog.models.count == 10)
            await cleanup()
        } catch { await cleanup(); throw error }
    }
}
