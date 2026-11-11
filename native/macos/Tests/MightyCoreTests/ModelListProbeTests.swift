import Foundation
import Testing
@testable import MightyCore

/// Each CLI's model-list read against what the real CLI on the developer's Mac printed
/// (native/contracts/fixtures/model-list-*), the arguments each read may use, and what the
/// menu gets when a read fails: the last list the CLI gave, kept and marked, or the
/// built-in list with the reason.
struct ModelListProbeTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static let fixtures = root.appendingPathComponent("native/contracts/fixtures")
    static func fixture(_ name: String) throws -> String { try String(contentsOf: fixtures.appendingPathComponent(name), encoding: .utf8) }

    /// What each real CLI accepted, run by hand with exactly these arguments (versions beside them). A flag one CLI
    /// takes is not another's: agy 1.5.5 refuses `models --standalone` ("flags provided but not defined"), which
    /// once left Antigravity's menu on the built-in list, while opencode 2.2.7 lists the same models with it and
    /// without starting OpenCode 2's background service. The fake CLIs below refuse anything else, as the real ones do.
    static let verified: [String: [String]] = [
        // claude 2.1.322: a headless stream-json session, sent only `initialize`.
        "claude": ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--safe-mode"],
        // codex-cli 0.167.0: the app-server over stdio, `initialize` then `model/list`.
        "codex": ["app-server", "--listen", "stdio://"],
        // agy 1.5.5
        "gemini": ["models"],
        // pi 1.5.2
        "pi": ["--list-models"],
        // opencode v2.2.7
        "opencode": ["models", "--standalone"],
    ]

    @Test func everyReadRunsTheArgumentsItsCLIWasCheckedWith() {
        for (provider, arguments) in Self.verified { #expect(ProviderService.modelListArguments(provider) == arguments, "\(provider)") }
        // Hermes 0.23.1 has no list command: the picker its last session offered is read instead.
        #expect(ProviderService.modelListArguments("hermes") == nil)
        #expect(ProviderService.modelListCommand("gemini") == "agy models" && ProviderService.modelListCommand("opencode") == "opencode models --standalone")
        #expect(ProviderService.modelListCommand("claude") == "claude" && ProviderService.modelListCommand("pi") == "pi --list-models")
        // The probe's environment lets Claude fetch the account's current models (DISABLE_TELEMETRY made it answer with its built-in list).
        #expect(ProviderService.quietClaudeEnvironment["DISABLE_TELEMETRY"] == nil)
    }

    // MARK: The real CLIs' output

    @Test func agysListHasItsNewModels() throws {
        let text = try Self.fixture("model-list-agy.txt")
        let catalog = ProviderService.normalizeAntigravityCatalog(text)
        #expect(catalog.isFresh && catalog.models.count == 17)
        #expect(catalog.models.map(\.value).prefix(4) == ["default", "gemini-4-carbon-medium", "gemini-4-carbon-high", "gemini-4-carbon-low"])
        #expect(catalog.models.first { $0.value == "gemini-4-carbon-medium" }?.displayName == "Gemini 4 Carbon (Medium)")
        #expect(catalog.models.last?.value == "gpt-oss-120b-medium")
        // Not in the built-in list: only the CLI's own list brings it.
        #expect(!ProviderOptions.fallbackCatalog("gemini").models.contains { $0.value == "gemini-4-carbon-medium" })
        // agy 1.5.5 writes "Fetching available models..." to standard error; an older one wrote it first on standard output.
        #expect(ProviderService.normalizeAntigravityCatalog("Fetching available models...\n" + text) == catalog)
    }

    @Test func opencodesListIsItsProviderSlashModelLines() throws {
        let catalog = ProviderService.normalizeOpencodeCatalog(try Self.fixture("model-list-opencode.txt"))
        #expect(catalog.isFresh && catalog.models.count == 13)
        #expect(catalog.models.dropFirst().allSatisfy { $0.value.hasPrefix("opencode/") })
        #expect(catalog.models.map(\.value).contains("opencode/big-pickle") && catalog.models.last?.value == "opencode/nemotron-3.5-super-vl-free")
    }

    @Test func pisTableHasEveryRow() throws {
        let catalog = ProviderService.normalizePiCatalog(try Self.fixture("model-list-pi.txt"))
        #expect(catalog.isFresh && catalog.models.count == 19)
        for model in ["anthropic/claude-fable-5-2", "anthropic/claude-haiku-5-5", "anthropic/claude-opus-5-5", "anthropic/claude-sonnet-5-5"] {
            #expect(catalog.models.contains { $0.value == model }, "\(model)")
        }
        #expect(catalog.models.dropFirst().allSatisfy { $0.supportsEffort == true })
    }

    @Test func codexsModelListAnswerIsReadWithItsDefault() throws {
        let lines = try Self.fixture("model-list-codex.jsonl").split(separator: "\n").map { try #require(JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]) }
        let answer = try #require(lines.first { $0["id"] as? Int == 2 }?["result"] as? [String: Any])
        let catalog = ProviderService.normalizeCodexCatalog(try #require(answer["data"] as? [[String: Any]]))
        #expect(catalog.isFresh)
        #expect(catalog.models.map(\.value) == ["default", "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-6.1-terra", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5"])
        #expect(catalog.models[0].resolvedModel == "gpt-6.1-sol")
        // codex lists an `ultra` effort the app has no setting for; the others stay.
        #expect(catalog.models[1].supportedEffortLevels == ["low", "medium", "high", "xhigh", "max"])
    }

    @Test func claudesInitializeAnswerNamesFable52() throws {
        let lines = try Self.fixture("model-list-claude.jsonl").split(separator: "\n").map { try #require(JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]) }
        let response = try #require(lines.compactMap { $0["response"] as? [String: Any] }.first)
        let models = try #require((response["response"] as? [String: Any])?["models"] as? [[String: Any]])
        let catalog = ProviderService.normalizeClaudeCatalog(models)
        #expect(catalog.isFresh && catalog.models.count == 14)
        #expect(catalog.models[0].resolvedModel == "claude-opus-5-5")
        let fable = try #require(catalog.models.first { $0.value == "fable" })
        #expect(fable.displayName == "Fable 5.2" && fable.resolvedModel == "claude-fable-5-2")
        #expect(catalog.models.contains { $0.value == "claude-fable-5-1" } && catalog.models.contains { $0.value == "haiku" && $0.resolvedModel == "claude-haiku-5-5" })
    }

    @Test func aFailedReadNamesTheCommandAndTheLineThatSaysWhy() throws {
        try LocaleOverride.$language.withValue(.ko) {
            let usage = try Self.fixture("model-list-agy-unknown-flag.txt")
            #expect(ProviderService.readFailure("gemini", problem: L("provider.catalog.failure.exit", ["code": "1"]), output: usage)
                    == "agy models: 종료 코드 1 · Error: flags provided but not defined: -standalone")
            // No line naming an error: the first that says anything (pi without a sign-in).
            #expect(ProviderService.readFailure("pi", problem: L("provider.catalog.failure.noModels"), output: "No models available. Use /login to log in.\n  /x/docs/providers.md\n")
                    == "pi --list-models: 출력에서 모델을 찾지 못했습니다 · No models available. Use /login to log in.")
            #expect(ProviderService.readFailure("codex", problem: L("process.error.timeout")) == "codex app-server --listen stdio://: " + L("process.error.timeout"))
            let home = FileManager.default.homeDirectoryForCurrentUser.path
            #expect(!ProviderService.readFailure("pi", problem: "x", output: "error: \(home)/.pi/agent/auth.json unreadable").contains(home))
            #expect(ProviderService.readFailure("pi", problem: "x", output: String(repeating: "error ", count: 400)).utf8.count <= 400)
        }
    }

    // MARK: Each read, end to end, with a fake CLI that refuses other arguments

    @Test(arguments: ["claude", "codex", "gemini", "pi", "opencode"])
    func aFakeCLIGivesItsFixtureList(provider: String) async throws {
        try await LocaleOverride.$language.withValue(.ko) { () async throws in
            let fake = try FakeListCLIs(); defer { fake.remove() }
            let service = try await fake.service()
            let catalog = await service.modelCatalog(provider: provider)
            #expect(catalog.isFresh, "\(provider): \(catalog.readFailure ?? "")")
            #expect(try fake.arguments(provider) == Self.verified[provider], "\(provider)")
            let expected: [String: String] = ["claude": "fable", "codex": "gpt-6.1-sol", "gemini": "gemini-4-carbon-medium", "pi": "anthropic/claude-fable-5-2", "opencode": "opencode/big-pickle"]
            #expect(catalog.models.contains { $0.value == expected[provider] }, "\(provider)")
            if provider == "claude" {
                let environment = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: fake.state.appendingPathComponent("claude-env.json"))) as? [String: Any])
                #expect(environment["CLAUDE_CODE_SAFE_MODE"] as? String == "1" && environment["DISABLE_TELEMETRY"] is NSNull)
            }
            await service.shutdown()
        }
    }

    @Test func agyRefusesTheOpencodeFlagAndItsOwnReadLists() async throws {
        let fake = try FakeListCLIs(); defer { fake.remove() }
        try await PythonFixture.warmUp(environment: fake.environment)
        // What the app ran before this fix, answered as the real agy 1.5.5 answers it.
        let refused = try await ProcessCapture.run(executable: fake.binary("gemini"), arguments: ["models", "--standalone"], environment: fake.environment)
        #expect(refused.exitCode == 1 && String(decoding: refused.stderr, as: UTF8.self).contains("flags provided but not defined: -standalone"))
        let read = await ProviderService.readListCommand(provider: "gemini", command: ProviderCommand(provider: "gemini", executable: fake.binary("gemini"), version: "1.5.5"),
                                                         environment: fake.environment, workspace: nil)
        #expect(read == .listed(ProviderService.normalizeAntigravityCatalog(try Self.fixture("model-list-agy.txt"))))
    }

    // MARK: A read that fails keeps the last list

    @Test func aFailedReadKeepsTheLastListMarkedAndARestartStillHasIt() async throws {
        try await LocaleOverride.$language.withValue(.ko) { () async throws in
            let fake = try FakeListCLIs(); defer { fake.remove() }
            let storage = fake.state.appendingPathComponent("storage")
            let workspace = fake.state.appendingPathComponent("site"); try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
            var service = try await fake.service(storage: storage)
            let fresh = await service.modelCatalog(provider: "gemini", workspacePath: workspace.path)
            #expect(fresh.isFresh && fresh.notice() == nil)
            let file = storage.appendingPathComponent(KeptModelLists.fileName)
            #expect(KeptModelLists.load(from: file).list(provider: "gemini", workspace: workspace.path)?.models == fresh.models)
            #expect(try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? Int == 0o600)

            try fake.fail("gemini", with: "Error: authentication required. Run 'agy' to log in, then retry.\n")
            let kept = await service.modelCatalog(provider: "gemini", workspacePath: workspace.path, forceRefresh: true)
            #expect(kept.source == "cli" && !kept.isFresh && kept.models == fresh.models && kept.detail == fresh.detail)
            #expect(kept.readFailure == "agy models: 종료 코드 1 · Error: authentication required. Run 'agy' to log in, then retry.")
            #expect(kept.listedAt.flatMap(KeptModelLists.date) != nil)
            #expect(kept.notice() == ModelListNotice(line: L("composer.model.listKept", ["age": L("resume.time.now")]), help: kept.readFailure ?? ""))
            // Another workspace that never read a list gets this provider's newest, marked the same way.
            let other = await service.modelCatalog(provider: "gemini", workspacePath: fake.state.path, forceRefresh: true)
            #expect(other.models == fresh.models && other.readFailure != nil)
            await service.shutdown()

            // After a restart the read still fails: the list comes back from the file, not the built-in one.
            service = try await fake.service(storage: storage)
            let restored = await service.modelCatalog(provider: "gemini", workspacePath: workspace.path)
            #expect(restored.source == "cli" && restored.models == fresh.models && restored.readFailure == kept.readFailure)
            await service.shutdown()

            // With nothing kept, the built-in list, and why.
            service = try await fake.service(storage: fake.state.appendingPathComponent("empty"))
            let builtIn = await service.modelCatalog(provider: "gemini", workspacePath: workspace.path)
            #expect(builtIn.source == "fallback" && builtIn.models == ProviderOptions.fallbackCatalog("gemini").models && builtIn.readFailure == kept.readFailure)
            #expect(builtIn.notice() == ModelListNotice(line: L("composer.model.listFallback"), help: kept.readFailure ?? ""))
            await service.shutdown()
        }
    }

    @Test func aMissingCLIOrNoModelsIsAFailedReadToo() async throws {
        try await LocaleOverride.$language.withValue(.ko) { () async throws in
            let fake = try FakeListCLIs(); defer { fake.remove() }
            let missing = ProviderService(binaryOverrides: ["pi": fake.state.appendingPathComponent("absent")], environment: fake.environment)
            let none = await missing.modelCatalog(provider: "pi")
            #expect(none.source == "fallback" && none.readFailure == L("run.error.cliExecutableMissing", ["name": "pi"]))
            await missing.shutdown()
            let service = try await fake.service()
            try Data("No models available. Use /login to log into a provider via OAuth or API key. See:\n  /x/docs/providers.md\n".utf8).write(to: fake.state.appendingPathComponent("pi-output"))
            let empty = await service.modelCatalog(provider: "pi")
            #expect(empty.source == "fallback" && empty.readFailure == "pi --list-models: 출력에서 모델을 찾지 못했습니다 · No models available. Use /login to log into a provider via OAuth or API key. See:")
            try fake.fail("codex", with: "Error: not logged in\n")
            let codex = await service.modelCatalog(provider: "codex")
            #expect(codex.source == "fallback" && codex.readFailure == "codex app-server --listen stdio://: 종료 코드 1 · Error: not logged in")
            await service.shutdown()
        }
    }

    @Test func hermesKeepsItsOwnPickerAndATestServiceHasNone() async throws {
        let fake = try FakeListCLIs(); defer { fake.remove() }
        // Without a storage folder (every test's service) there is no picker file to write over.
        #expect(ProviderService().hermesModelCacheURL == nil)
        let storage = fake.state.appendingPathComponent("storage")
        let service = try await fake.service(storage: storage)
        let cache = try #require(service.hermesModelCacheURL)
        #expect(cache == storage.appendingPathComponent("hermes-models.json"))
        try FileManager.default.createDirectory(at: storage, withIntermediateDirectories: true)
        try JSONEncoder().encode([ModelOption(value: "anthropic:claude-fable-5-2", displayName: "Fable")]).write(to: cache)
        let picker = await service.modelCatalog(provider: "hermes")
        #expect(picker.isFresh && picker.models.map(\.value) == ["default", "anthropic:claude-fable-5-2"])
        // Signed out, Hermes's picker goes (`forgetModels`): the menu does not bring it back as a kept list.
        HermesAgent.forgetModels(at: cache)
        let empty = await service.modelCatalog(provider: "hermes", forceRefresh: true)
        #expect(empty.source == "fallback" && empty.readFailure == nil && empty.notice() == nil)
        #expect(KeptModelLists.load(from: storage.appendingPathComponent(KeptModelLists.fileName)).entries.isEmpty)
        await service.shutdown()
    }

    // MARK: The kept lists file

    @Test func theKeptListsFileIsBoundedAndReadTolerantly() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("kept-lists-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent(KeptModelLists.fileName)
        #expect(KeptModelLists.load(from: file).entries.isEmpty)
        try Data("not json".utf8).write(to: file)
        #expect(KeptModelLists.load(from: file).entries.isEmpty)
        try Data(#"{"version":2,"lists":[]}"#.utf8).write(to: file)
        #expect(KeptModelLists.load(from: file).entries.isEmpty)
        let now = KeptModelLists.timestamp(Date())
        let good: [String: Any] = ["provider": "gemini", "workspace": "/w", "listedAt": now, "detail": "d", "models": [["value": "gemini-4-carbon-medium", "displayName": "Carbon", "description": ""]]]
        var badModels = good; badModels["models"] = [["value": "--bad", "displayName": "x", "description": ""]]
        var hermes = good; hermes["provider"] = "hermes"
        var unknown = good; unknown["provider"] = "gemini-cli"
        var undated = good; undated["listedAt"] = "yesterday"; undated["workspace"] = "/x"
        var relative = good; relative["workspace"] = "w"
        let rows: [Any] = [good, badModels, hermes, unknown, undated, relative, "text", ["provider": "pi"]]
        try JSONSerialization.data(withJSONObject: ["version": 1, "lists": rows]).write(to: file)
        let loaded = KeptModelLists.load(from: file)
        #expect(loaded.entries.count == 1 && loaded.entries[0].models.map(\.value) == ["gemini-4-carbon-medium"])
        // Over the size bound the file is not read at all.
        try Data(count: KeptModelLists.maximumBytes + 1).write(to: file)
        #expect(KeptModelLists.load(from: file).entries.isEmpty)

        var lists = KeptModelLists()
        let start = Date()
        let catalog = ModelCatalog(source: "cli", models: [ModelOption(value: "m1", displayName: "M1")], detail: "d")
        let first = lists.remember(catalog, provider: "pi", workspace: "/w", at: start, writtenAt: nil)
        // The same list again soon after: kept in memory with its new time, not written again.
        let again = lists.remember(catalog, provider: "pi", workspace: "/w", at: start.addingTimeInterval(10), writtenAt: start)
        #expect(first && !again)
        #expect(lists.list(provider: "pi", workspace: "/w")?.listedAt == KeptModelLists.timestamp(start.addingTimeInterval(10)))
        let later = lists.remember(catalog, provider: "pi", workspace: "/w", at: start.addingTimeInterval(KeptModelLists.rewriteAge + 11), writtenAt: start)
        var changed = catalog; changed.models.append(ModelOption(value: "m2", displayName: "M2"))
        let different = lists.remember(changed, provider: "pi", workspace: "/w", at: start.addingTimeInterval(400), writtenAt: start.addingTimeInterval(390))
        #expect(later && different)
        // Neither a kept nor a built-in list is ever kept.
        let keptAgain = lists.remember(KeptModelLists.kept(lists.entries[0], reason: "r"), provider: "pi", workspace: "/w", at: start, writtenAt: nil)
        let builtIn = lists.remember(ProviderOptions.fallbackCatalog("pi"), provider: "pi", workspace: "/v", at: start, writtenAt: nil)
        #expect(!keptAgain && !builtIn && lists.entries.count == 1)
        for index in 0..<40 { lists.remember(catalog, provider: "codex", workspace: "/w\(index)", at: start.addingTimeInterval(500 + Double(index)), writtenAt: nil) }
        #expect(lists.entries.count == KeptModelLists.maximumEntries && lists.entries.first?.workspace == "/w39")
        // The newest of a provider stands in for a workspace that never read one.
        #expect(lists.list(provider: "codex", workspace: "/elsewhere")?.workspace == "/w39" && lists.list(provider: "claude", workspace: nil) == nil)
        let data = try #require(lists.encoded())
        try data.write(to: file)
        #expect(KeptModelLists.load(from: file) == lists)
    }

    // MARK: A kept list is shown, never trusted

    @Test func aKeptListRetiresNothing() {
        let fresh = ModelCatalog(source: "cli", models: [ModelOption(value: "default", displayName: "Default"), ModelOption(value: "claude-fable-5-2", displayName: "Fable 5.2")])
        var kept = ModelCatalog(source: "cli", models: [ModelOption(value: "default", displayName: "Default")], readFailure: "claude: 종료 코드 1", listedAt: KeptModelLists.timestamp(Date()))
        #expect(!kept.isFresh && fresh.isFresh)
        #expect(ModelSelectionSupport.reconcile(model: "claude-fable-5-2", effort: "high", previous: fresh, refreshed: kept, provider: "claude") == .init(model: "claude-fable-5-2", effort: "high"))
        #expect(ModelSelectionSupport.reconcile(model: "claude-fable-5-2", effort: "high", previous: fresh, refreshed: ModelCatalog(source: "cli", models: kept.models), provider: "claude") == .init(model: "default", effort: "default"))
        #expect(!AntigravityMigration.modelUnavailable("gemini-4-carbon-medium", catalog: kept))
        kept.readFailure = nil
        #expect(AntigravityMigration.modelUnavailable("gemini-4-carbon-medium", catalog: kept))
    }

    @Test func theMenuLineSaysWhichListItIs() {
        LocaleOverride.$language.withValue(.ko) {
            let now = Date()
            #expect(ModelCatalog(source: "cli", models: []).notice() == nil)
            // Not read yet (or a read another one replaced): no line.
            #expect(ProviderOptions.fallbackCatalog("claude").notice() == nil)
            var builtIn = ProviderOptions.fallbackCatalog("claude"); builtIn.readFailure = "claude: 종료 코드 1"
            #expect(builtIn.notice(now: now) == ModelListNotice(line: "기본 목록 · CLI에서 목록을 읽지 못했습니다", help: "claude: 종료 코드 1"))
            let kept = ModelCatalog(source: "cli", models: [], readFailure: "agy models: 종료 코드 1", listedAt: KeptModelLists.timestamp(now.addingTimeInterval(-300)))
            #expect(kept.notice(now: now) == ModelListNotice(line: "이전 목록 · 새로 읽지 못했습니다 (5분 전 목록)", help: "agy models: 종료 코드 1"))
        }
    }

    // MARK: The wire

    @Test func theNewFieldsAreAdditiveOnTheWire() throws {
        let encoder = JSONEncoder()
        let fresh = try #require(JSONSerialization.jsonObject(with: encoder.encode(ModelCatalog(source: "cli", models: [ModelOption(value: "opus", displayName: "Opus")]))) as? [String: Any])
        #expect(fresh["readFailure"] == nil && fresh["listedAt"] == nil, "a fresh list is encoded as before")
        let kept = ModelCatalog(source: "cli", models: [ModelOption(value: "opus", displayName: "Opus")], detail: "d", readFailure: "claude: 종료 코드 1", listedAt: "2026-11-11T02:00:00Z")
        let data = try encoder.encode(kept)
        #expect(try JSONDecoder().decode(ModelCatalog.self, from: data) == kept)
        // An older peer's catalogue (no new keys) still decodes what a newer host sends, and the reverse reads nil.
        struct OlderCatalog: Codable { var source: String; var models: [ModelOption]; var detail: String }
        let older = try JSONDecoder().decode(OlderCatalog.self, from: data)
        #expect(older.source == "cli" && older.models.map(\.value) == ["opus"] && older.detail == "d")
        let fromOlder = try JSONDecoder().decode(ModelCatalog.self, from: encoder.encode(older))
        #expect(fromOlder.readFailure == nil && fromOlder.listedAt == nil && fromOlder.isFresh)

        // A newer Mac host's /v2/info with a kept list: a client reads and accepts it; the fixture itself (an older host) has neither field.
        let raw = try Data(contentsOf: Self.fixtures.appendingPathComponent("remote-v2/info-darwin.json"))
        let plain = try JSONDecoder().decode(RemoteWireInfo.self, from: raw)
        #expect(plain.runtime.providers?.allSatisfy { $0.modelCatalog.readFailure == nil && $0.modelCatalog.listedAt == nil } == true)
        var object = try #require(JSONSerialization.jsonObject(with: raw) as? [String: Any])
        var runtime = try #require(object["runtime"] as? [String: Any])
        var providers = try #require(runtime["providers"] as? [[String: Any]])
        var catalog = try #require(providers[0]["modelCatalog"] as? [String: Any])
        catalog["readFailure"] = "claude: 종료 코드 1"; catalog["listedAt"] = "2026-11-11T02:00:00Z"
        providers[0]["modelCatalog"] = catalog; runtime["providers"] = providers; object["runtime"] = runtime
        let info = try JSONDecoder().decode(RemoteWireInfo.self, from: JSONSerialization.data(withJSONObject: object))
        try RemoteWorkspaceValidation.info(info)
        #expect(info.runtime.providers?[0].modelCatalog.readFailure == "claude: 종료 코드 1" && info.runtime.providers?[0].modelCatalog.notice() != nil)
        catalog["readFailure"] = String(repeating: "x", count: 2_001); providers[0]["modelCatalog"] = catalog; runtime["providers"] = providers; object["runtime"] = runtime
        #expect(throws: (any Error).self) { try RemoteWorkspaceValidation.info(try JSONDecoder().decode(RemoteWireInfo.self, from: JSONSerialization.data(withJSONObject: object))) }
    }
}

/// Stand-ins for the five CLIs that list models: each answers its verified arguments with the real CLI's recorded
/// output (the fixtures) and refuses any other, as the real ones do (agy with its own usage error). Each writes the
/// arguments it got; a `<name>-fail` file makes it fail with that text on standard error; a `pi-output` file is
/// printed instead of pi's table.
struct FakeListCLIs {
    let state: URL
    let environment: [String: String]

    init() throws {
        state = FileManager.default.temporaryDirectory.appendingPathComponent("model-lists-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: state, withIntermediateDirectories: true)
        var environment = ["PATH": "/usr/bin:/bin", "MODEL_LIST_STATE": state.path, "MODEL_LIST_FIXTURES": ModelListProbeTests.fixtures.path]
        let verified = Dictionary(uniqueKeysWithValues: ModelListProbeTests.verified.map { (ProviderOptions.executable($0.key), $0.value) })
        environment["MODEL_LIST_VERIFIED"] = String(decoding: try JSONSerialization.data(withJSONObject: verified), as: UTF8.self)
        if let developerDirectory = ProcessInfo.processInfo.environment["DEVELOPER_DIR"] { environment["DEVELOPER_DIR"] = developerDirectory }
        self.environment = environment
        let script = #"""
        #!/usr/bin/python3
        import json, os, pathlib, sys
        name = pathlib.Path(sys.argv[0]).name
        args = sys.argv[1:]
        state = pathlib.Path(os.environ["MODEL_LIST_STATE"])
        fixtures = pathlib.Path(os.environ["MODEL_LIST_FIXTURES"])
        versions = {"agy": "1.5.5", "pi": "1.5.2", "opencode": "opencode v2.2.7", "codex": "codex-cli 0.167.0", "claude": "2.1.322 (Claude Code)", "hermes": "Hermes Agent v0.23.1 (2026.9.24)"}
        if args == ["--version"]:
            print(versions[name]); sys.exit(0)
        (state / (name + "-argv.json")).write_text(json.dumps(args))
        if name == "claude":
            (state / "claude-env.json").write_text(json.dumps({key: os.environ.get(key) for key in ["CLAUDE_CODE_SAFE_MODE", "DISABLE_TELEMETRY"]}))
        failure = state / (name + "-fail")
        if failure.exists():
            sys.stderr.write(failure.read_text()); sys.exit(1)
        if args != json.loads(os.environ["MODEL_LIST_VERIFIED"]).get(name):
            if name == "agy":
                sys.stderr.write((fixtures / "model-list-agy-unknown-flag.txt").read_text()); sys.exit(1)
            sys.stderr.write("error: unexpected arguments: " + " ".join(args) + "\n"); sys.exit(2)
        if name in ("agy", "pi", "opencode"):
            if name == "agy":
                sys.stderr.write("Fetching available models...\n")
            replaced = state / (name + "-output")
            sys.stdout.write(replaced.read_text() if replaced.exists() else (fixtures / ("model-list-" + name + ".txt")).read_text())
            sys.exit(0)
        lines = (fixtures / ("model-list-" + name + ".jsonl")).read_text().splitlines()
        for line in sys.stdin:
            request = json.loads(line)
            if name == "claude":
                if request.get("type") != "control_request":
                    continue
                print(lines[0], flush=True)
                print(lines[1].replace("REQUEST_ID", request["request_id"]), flush=True)
            elif request.get("method") == "initialize":
                answer = json.loads(lines[0]); answer["id"] = request["id"]
                print(json.dumps(answer), flush=True); print(lines[1], flush=True)
            elif request.get("method") == "model/list":
                answer = json.loads(lines[2]); answer["id"] = request["id"]
                print(json.dumps(answer), flush=True)
        """#
        for name in ["claude", "codex", "agy", "pi", "opencode", "hermes"] {
            let file = state.appendingPathComponent(name)
            try Data(script.utf8).write(to: file)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path)
        }
    }

    func binary(_ provider: String) -> URL { state.appendingPathComponent(ProviderOptions.executable(provider)) }
    func service(storage: URL? = nil) async throws -> ProviderService {
        try await PythonFixture.warmUp(environment: environment)
        let overrides = Dictionary(uniqueKeysWithValues: ProviderOptions.ids.map { ($0, binary($0)) })
        return ProviderService(binaryOverrides: overrides, environment: environment, storageDirectory: storage)
    }
    func arguments(_ provider: String) throws -> [String]? {
        try JSONSerialization.jsonObject(with: Data(contentsOf: state.appendingPathComponent(ProviderOptions.executable(provider) + "-argv.json"))) as? [String]
    }
    func fail(_ provider: String, with text: String) throws { try Data(text.utf8).write(to: state.appendingPathComponent(ProviderOptions.executable(provider) + "-fail")) }
    func remove() { try? FileManager.default.removeItem(at: state) }
}
