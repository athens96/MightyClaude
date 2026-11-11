import Foundation
import Testing
@testable import MightyCore

/// Each CLI's model-list read against what the real CLI on the developer's Mac printed
/// (native/contracts/fixtures/model-list-*), and the arguments each read may use.
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
        // The probe's environment lets Claude fetch the account's current models (DISABLE_TELEMETRY made it answer with its built-in list).
        #expect(ProviderService.quietClaudeEnvironment["DISABLE_TELEMETRY"] == nil)
    }

    // MARK: The real CLIs' output

    @Test func agysListHasItsNewModels() throws {
        let text = try Self.fixture("model-list-agy.txt")
        let catalog = ProviderService.normalizeAntigravityCatalog(text)
        #expect(catalog.source == "cli" && catalog.models.count == 17)
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
        #expect(catalog.source == "cli" && catalog.models.count == 13)
        #expect(catalog.models.dropFirst().allSatisfy { $0.value.hasPrefix("opencode/") })
        #expect(catalog.models.map(\.value).contains("opencode/big-pickle") && catalog.models.last?.value == "opencode/nemotron-3.5-super-vl-free")
    }

    @Test func pisTableHasEveryRow() throws {
        let catalog = ProviderService.normalizePiCatalog(try Self.fixture("model-list-pi.txt"))
        #expect(catalog.source == "cli" && catalog.models.count == 19)
        for model in ["anthropic/claude-fable-5-2", "anthropic/claude-haiku-5-5", "anthropic/claude-opus-5-5", "anthropic/claude-sonnet-5-5"] {
            #expect(catalog.models.contains { $0.value == model }, "\(model)")
        }
        #expect(catalog.models.dropFirst().allSatisfy { $0.supportsEffort == true })
    }

    @Test func codexsModelListAnswerIsReadWithItsDefault() throws {
        let lines = try Self.fixture("model-list-codex.jsonl").split(separator: "\n").map { try #require(JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]) }
        let answer = try #require(lines.first { $0["id"] as? Int == 2 }?["result"] as? [String: Any])
        let catalog = ProviderService.normalizeCodexCatalog(try #require(answer["data"] as? [[String: Any]]))
        #expect(catalog.source == "cli")
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
        #expect(catalog.source == "cli" && catalog.models.count == 14)
        #expect(catalog.models[0].resolvedModel == "claude-opus-5-5")
        let fable = try #require(catalog.models.first { $0.value == "fable" })
        #expect(fable.displayName == "Fable 5.2" && fable.resolvedModel == "claude-fable-5-2")
        #expect(catalog.models.contains { $0.value == "claude-fable-5-1" } && catalog.models.contains { $0.value == "haiku" && $0.resolvedModel == "claude-haiku-5-5" })
    }

    // MARK: Each read, end to end, with a fake CLI that refuses other arguments

    @Test(arguments: ["claude", "codex", "gemini", "pi", "opencode"])
    func aFakeCLIGivesItsFixtureList(provider: String) async throws {
        let fake = try FakeListCLIs(); defer { fake.remove() }
        let service = try await fake.service()
        let catalog = await service.modelCatalog(provider: provider)
        #expect(catalog.source == "cli", "\(provider)")
        #expect(try fake.arguments(provider) == Self.verified[provider], "\(provider)")
        let expected: [String: String] = ["claude": "fable", "codex": "gpt-6.1-sol", "gemini": "gemini-4-carbon-medium", "pi": "anthropic/claude-fable-5-2", "opencode": "opencode/big-pickle"]
        #expect(catalog.models.contains { $0.value == expected[provider] }, "\(provider)")
        if provider == "claude" {
            let environment = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: fake.state.appendingPathComponent("claude-env.json"))) as? [String: Any])
            #expect(environment["CLAUDE_CODE_SAFE_MODE"] as? String == "1" && environment["DISABLE_TELEMETRY"] is NSNull)
        }
        await service.shutdown()
    }

    @Test func agyRefusesTheOpencodeFlagAndItsOwnReadLists() async throws {
        let fake = try FakeListCLIs(); defer { fake.remove() }
        let service = try await fake.service()
        // What the app ran before this fix, answered as the real agy 1.5.5 answers it.
        let refused = try await ProcessCapture.run(executable: fake.binary("gemini"), arguments: ["models", "--standalone"], environment: fake.environment)
        #expect(refused.exitCode == 1 && String(decoding: refused.stderr, as: UTF8.self).contains("flags provided but not defined: -standalone"))
        #expect(await service.modelCatalog(provider: "gemini") == ProviderService.normalizeAntigravityCatalog(try Self.fixture("model-list-agy.txt")))
        await service.shutdown()
    }
}

/// Stand-ins for the five CLIs that list models: each answers its verified arguments with the real CLI's recorded
/// output (the fixtures) and refuses any other, as the real ones do (agy with its own usage error). Each writes the
/// arguments it got.
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
    func service() async throws -> ProviderService {
        try await PythonFixture.warmUp(environment: environment)
        let overrides = Dictionary(uniqueKeysWithValues: ProviderOptions.ids.map { ($0, binary($0)) })
        return ProviderService(binaryOverrides: overrides, environment: environment)
    }
    func arguments(_ provider: String) throws -> [String]? {
        try JSONSerialization.jsonObject(with: Data(contentsOf: state.appendingPathComponent(ProviderOptions.executable(provider) + "-argv.json"))) as? [String]
    }
    func remove() { try? FileManager.default.removeItem(at: state) }
}
