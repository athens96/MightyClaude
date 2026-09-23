import Foundation
import Testing
@testable import MightyCore

struct ModelRefreshTests {
    @Test func resumedClaudeDefaultExplicitlyClearsSavedModel() throws {
        let plugin = URL(fileURLWithPath: "/fixture/plugin")
        var request = StartRunRequest(sessionId: "session", workspaceId: "workspace", input: "fixture", resumeId: "old-conversation")
        let args = try ProviderService.arguments(request, pluginDirectory: plugin)
        let modelIndex = try #require(args.firstIndex(of: "--model"))
        #expect(args[modelIndex + 1] == "default")
        #expect(args.contains("--resume") && args.contains("old-conversation"))
        request.resumeId = nil
        #expect(try !ProviderService.arguments(request, pluginDirectory: plugin).contains("--model"))
        for provider in ["codex", "gemini"] {
            request.provider = provider; request.resumeId = "old-conversation"
            #expect(try !ProviderService.arguments(request, pluginDirectory: plugin).contains("--model"))
        }
    }

    @Test func providerWideDiscardLeavesOtherProviderCached() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let folder = try workspace(root, "workspace", model: "old-model")
        let service = service(root)
        for provider in ["claude", "codex"] { _ = await service.modelCatalog(provider: provider, workspacePath: folder.path) }
        try configure(folder, model: "new-model")
        await service.discardModelCatalogs(provider: "claude")
        #expect(await service.modelCatalog(provider: "claude", workspacePath: folder.path).models.last?.value == "new-model")
        #expect(await service.modelCatalog(provider: "codex", workspacePath: folder.path).models.last?.value == "old-model")
        #expect(calls(folder, "claude") == 2 && calls(folder, "codex") == 1)
        await service.shutdown()
    }

    private func fixture() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("model-refresh-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let script = #"""
        #!/usr/bin/python3
        import json, os, pathlib, sys, time
        provider = pathlib.Path(sys.argv[0]).name
        if '--version' in sys.argv:
            print('2.1.278' if provider == 'claude' else '0.153.4', flush=True)
            sys.exit(0)
        cwd = pathlib.Path.cwd()
        with (cwd / (provider + '-calls')).open('a') as f: f.write('probe\n')
        config = json.loads((cwd / 'catalog.json').read_text())
        (cwd / (provider + '-started')).write_text(config['model'])
        time.sleep(config.get('delay', 0))
        for line in sys.stdin:
            request = json.loads(line)
            with (cwd / (provider + '-requests')).open('a') as f: f.write(line)
            if provider == 'claude':
                assert '--safe-mode' in sys.argv
                assert os.environ['CLAUDE_CODE_SAFE_MODE'] == '1'
                result = {'type':'control_response','response':{'subtype':'success','request_id':request['request_id'],'response':{'models':[{'value':config['model'],'displayName':'Fixture'}]}}}
            elif request.get('method') == 'initialize':
                result = {'id':request['id'],'result':{}}
            elif request.get('method') == 'model/list':
                result = {'id':request['id'],'result':{'data':[{'model':config['model'],'displayName':'Fixture'}]}}
            else:
                continue
            print(json.dumps(result), flush=True)
        """#
        for provider in ["claude", "codex"] {
            let file = root.appendingPathComponent(provider)
            try Data(script.utf8).write(to: file)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path)
        }
        return root
    }
    private func workspace(_ root: URL, _ name: String, model: String, delay: Double = 0) throws -> URL {
        let folder = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try configure(folder, model: model, delay: delay)
        return folder
    }
    private func configure(_ folder: URL, model: String, delay: Double = 0) throws {
        try JSONSerialization.data(withJSONObject: ["model": model, "delay": delay]).write(to: folder.appendingPathComponent("catalog.json"), options: .atomic)
    }
    private func service(_ root: URL) -> ProviderService {
        var environment = ["PATH": "/usr/bin:/bin"]
        if let developerDirectory = ProcessInfo.processInfo.environment["DEVELOPER_DIR"] { environment["DEVELOPER_DIR"] = developerDirectory }
        return ProviderService(binaryOverrides: ["claude": root.appendingPathComponent("claude"), "codex": root.appendingPathComponent("codex")], environment: environment)
    }
    private func calls(_ folder: URL, _ provider: String) -> Int {
        ((try? String(contentsOf: folder.appendingPathComponent(provider + "-calls"), encoding: .utf8)) ?? "").split(separator: "\n").count
    }
    private func waitForStart(_ folder: URL, provider: String, model: String) async throws {
        for _ in 0..<3000 {
            if (try? String(contentsOf: folder.appendingPathComponent(provider + "-started"), encoding: .utf8)) == model { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        throw MightyError("Fixture probe did not start")
    }

    @Test func workspaceCatalogsAndProviderRefreshesAreIsolated() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let a = try workspace(root, "a", model: "company/one"), b = try workspace(root, "b", model: "company/two")
        let service = service(root)
        for provider in ["claude", "codex"] {
            let first = await service.modelCatalog(provider: provider, workspacePath: a.path)
            let second = await service.modelCatalog(provider: provider, workspacePath: b.path)
            #expect(first.source == "cli" && first.models.last?.value == "company/one")
            #expect(second.models.last?.value == "company/two")
        }
        try configure(a, model: "company/new")
        let cached = await service.modelCatalog(provider: "claude", workspacePath: a.path + "/../a")
        #expect(cached.models.last?.value == "company/one" && calls(a, "claude") == 1)
        let fresh = await service.modelCatalog(provider: "claude", workspacePath: a.path, forceRefresh: true)
        #expect(fresh.models.last?.value == "company/new" && calls(a, "claude") == 2)
        #expect(await service.modelCatalog(provider: "codex", workspacePath: a.path).models.last?.value == "company/one")
        #expect(calls(a, "codex") == 1 && calls(b, "claude") == 1)
        for folder in [a, b] {
            for provider in ["claude", "codex"] {
                let requests = try String(contentsOf: folder.appendingPathComponent(provider + "-requests"), encoding: .utf8)
                #expect(!requests.contains("turn/start") && !requests.contains("thread/start") && !requests.contains("\"type\":\"user\""))
            }
        }
        await service.shutdown()
    }

    @Test func forcedDiscoverySupersedesOldWorkAndConcurrentRefreshesShareAProbe() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let folder = try workspace(root, "workspace", model: "old-model", delay: 0.5)
        let service = service(root)
        let old = Task { await service.modelCatalog(provider: "codex", workspacePath: folder.path) }
        try await waitForStart(folder, provider: "codex", model: "old-model")
        try configure(folder, model: "new-model", delay: 0.2)
        let first = Task { await service.modelCatalog(provider: "codex", workspacePath: folder.path, forceRefresh: true) }
        try await waitForStart(folder, provider: "codex", model: "new-model")
        let second = Task { await service.modelCatalog(provider: "codex", workspacePath: folder.path, forceRefresh: true) }
        #expect(await old.value.source == "fallback")
        #expect(await first.value.models.last?.value == "new-model")
        #expect(await second.value.models.last?.value == "new-model")
        #expect(calls(folder, "codex") == 2)
        #expect(await service.modelCatalog(provider: "codex", workspacePath: folder.path).models.last?.value == "new-model")
        await service.shutdown()
    }

    @Test func invalidationAndProbeFailureCannotReturnOldModels() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let folder = try workspace(root, "workspace", model: "old-model", delay: 0.5)
        let service = service(root)
        let old = Task { await service.modelCatalog(provider: "claude", workspacePath: folder.path) }
        try await waitForStart(folder, provider: "claude", model: "old-model")
        await service.invalidateCaches()
        #expect(await old.value.source == "fallback")
        try FileManager.default.removeItem(at: folder.appendingPathComponent("catalog.json"))
        #expect(await service.modelCatalog(provider: "claude", workspacePath: folder.path, forceRefresh: true).source == "fallback")
        await service.shutdown()
    }

    @Test func authenticationInvalidationSupersedesEvenAnAlreadyForcedProbe() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let folder = try workspace(root, "workspace", model: "account-one", delay: 0.5)
        let service = service(root)
        let old = Task { await service.providerRuntime(provider: "codex", workspacePath: folder.path, forceRefresh: true) }
        try await waitForStart(folder, provider: "codex", model: "account-one")
        try configure(folder, model: "account-two")
        await service.invalidateModelCatalog(provider: "codex", workspacePath: folder.path)
        let fresh = await service.providerRuntime(provider: "codex", workspacePath: folder.path, forceRefresh: true)
        #expect(fresh.available && fresh.modelCatalog.models.last?.value == "account-two")
        #expect(await old.value.modelCatalog.source == "fallback")
        #expect(calls(folder, "codex") == 2 && calls(folder, "claude") == 0)
        await service.shutdown()
    }

    @Test func selectionReconciliationPreservesAliasesDefaultsAndCustomModels() {
        let old = ModelCatalog(source: "cli", models: [.init(value: "old-alias", displayName: "Old", resolvedModel: "provider/model"), .init(value: "retired", displayName: "Retired")])
        let fresh = ModelCatalog(source: "cli", models: [.init(value: "new-alias", displayName: "New", resolvedModel: "provider/model", supportedEffortLevels: ["high"]), .init(value: "no-effort", displayName: "Small", supportsEffort: false)])
        #expect(ModelSelectionSupport.reconcile(model: "old-alias", effort: "high", previous: old, refreshed: fresh) == .init(model: "new-alias", effort: "high"))
        #expect(ModelSelectionSupport.reconcile(model: "provider/model", effort: "low", previous: nil, refreshed: fresh) == .init(model: "new-alias", effort: "default"))
        #expect(ModelSelectionSupport.reconcile(model: "retired", effort: "high", previous: old, refreshed: fresh) == .init(model: "default", effort: "default"))
        #expect(ModelSelectionSupport.reconcile(model: "custom/private", effort: "high", previous: old, refreshed: fresh) == .init(model: "custom/private", effort: "high"))
        #expect(ModelSelectionSupport.reconcile(model: "default", effort: "high", previous: old, refreshed: fresh) == .init(model: "default", effort: "high"))
        #expect(ModelSelectionSupport.reconcile(model: "retired", effort: "high", previous: old, refreshed: .init()) == .init(model: "retired", effort: "high"))
        #expect(ModelSelectionSupport.reconcile(model: "no-effort", effort: "high", previous: nil, refreshed: fresh) == .init(model: "no-effort", effort: "default"))
    }

    @Test func restoredClaudeSelectionRecoversWithoutAnOldCatalogAndDefaultsKeepMetadata() {
        let fresh = ProviderService.normalizeClaudeCatalog([
            ["value": "default", "resolvedModel": "apac.anthropic.claude-opus-5", "supportedEffortLevels": ["high"]],
            ["value": "apac.anthropic.claude-fable-5-1", "displayName": "Fable"]
        ])
        #expect(fresh.models[0].resolvedModel == "apac.anthropic.claude-opus-5")
        for previous in [nil, ProviderOptions.fallbackCatalog("claude")] {
            #expect(ModelSelectionSupport.reconcile(model: "claude-fable-5-1[1m]", effort: "high", previous: previous, refreshed: fresh, provider: "claude") == .init(model: "default", effort: "default"))
        }
        #expect(ModelSelectionSupport.reconcile(model: "apac.anthropic.claude-fable-5-1", effort: "high", previous: nil, refreshed: fresh, provider: "claude").model == "apac.anthropic.claude-fable-5-1")
        #expect(ModelSelectionSupport.reconcile(model: "company/claude-fable-5-1", effort: "high", previous: nil, refreshed: fresh, provider: "claude").model == "company/claude-fable-5-1")
        #expect(ModelSelectionSupport.reconcile(model: "default", effort: "max", previous: nil, refreshed: fresh, provider: "claude").effort == "default")
        #expect(ProviderService.normalizeClaudeCatalog([["value": "default", "resolvedModel": "provider/default"]]).source == "cli")
        let codex = ProviderService.normalizeCodexCatalog([["model": "gpt-6-astra", "isDefault": true, "supportedReasoningEfforts": [["reasoningEffort": "high"]]]])
        #expect(codex.models.first?.resolvedModel == "gpt-6-astra" && codex.models.first?.supportedEffortLevels == ["high"])
    }

    @Test func stableClaudeAliasesSurviveConcreteBedrockCatalogs() {
        let fresh = ProviderService.normalizeClaudeCatalog([
            ["value": "default", "resolvedModel": "apac.anthropic.claude-opus-5"],
            ["value": "apac.anthropic.claude-sonnet-5", "supportsEffort": true, "supportedEffortLevels": ["high"]]
        ])
        for alias in ["sonnet", "opus", "fable", "haiku", "opusplan", "best", "sonnet[1m]", "opus[1m]", "fable[1m]"] {
            let previousCLI = ModelCatalog(source: "cli", models: [.init(value: alias, displayName: alias, resolvedModel: "apac.anthropic.claude-sonnet-5")])
            for previous in [nil, ProviderOptions.fallbackCatalog("claude"), previousCLI] {
                #expect(ModelSelectionSupport.reconcile(model: alias, effort: "high", previous: previous, refreshed: fresh, provider: "claude") == .init(model: alias, effort: "high"))
            }
        }
        let explicitAlias = ModelCatalog(source: "cli", models: [.init(value: "sonnet", displayName: "Sonnet", supportedEffortLevels: ["high"])])
        #expect(ModelSelectionSupport.reconcile(model: "sonnet", effort: "max", previous: nil, refreshed: explicitAlias, provider: "claude") == .init(model: "sonnet", effort: "default"))
    }
}
