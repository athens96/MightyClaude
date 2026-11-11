import Foundation
import Testing
@testable import MightyCore

/// The efforts a model takes are what its CLI's own list says (native/contracts/fixtures/model-list-*): Claude Code
/// 2.1.322 names low to max for Haiku 5.5 and nothing for Haiku 4.5. A Haiku model no list speaks for keeps the old
/// rule, no effort. Codex 0.167.0 names `ultra` for six of its nine models, which only Codex is ever offered.
struct ModelEffortTests {
    static func claudeCatalog() throws -> ModelCatalog {
        let lines = try ModelListProbeTests.fixture("model-list-claude.jsonl").split(separator: "\n").map { try #require(JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]) }
        let response = try #require(lines.compactMap { $0["response"] as? [String: Any] }.first)
        return ProviderService.normalizeClaudeCatalog(try #require((response["response"] as? [String: Any])?["models"] as? [[String: Any]]))
    }
    static func codexCatalog() throws -> ModelCatalog {
        let lines = try ModelListProbeTests.fixture("model-list-codex.jsonl").split(separator: "\n").map { try #require(JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]) }
        let answer = try #require(lines.first { $0["id"] as? Int == 2 }?["result"] as? [String: Any])
        return ProviderService.normalizeCodexCatalog(try #require(answer["data"] as? [[String: Any]]))
    }

    private func request(_ model: String, effort: String) -> StartRunRequest {
        StartRunRequest(sessionId: "pane", workspaceId: "space", input: "Reply with ok.", model: model, settings: RunSettings(effort: effort))
    }

    // MARK: Haiku

    @Test func haiku55TakesTheLevelsClaudeCodeNamesAndHaiku45None() throws {
        let catalog = try Self.claudeCatalog()
        let haiku = try #require(catalog.models.first { $0.value == "haiku" })
        #expect(haiku.resolvedModel == "claude-haiku-5-5" && haiku.supportsEffort == true && haiku.supportedEffortLevels == ProviderOptions.efforts)
        // The alias and the version it stands for today.
        for model in ["haiku", "claude-haiku-5-5"] {
            #expect(ProviderOptions.effortLevels(provider: "claude", model: model, catalog: catalog, registeredModels: []) == ProviderOptions.efforts, "\(model)")
        }
        // Haiku 4.5 is listed without a word on effort: the old rule.
        let older = try #require(catalog.models.first { $0.value == "claude-haiku-4-5-20251001" })
        #expect(older.supportsEffort == false && older.supportedEffortLevels == [])
        #expect(ProviderOptions.effortLevels(provider: "claude", model: older.value, catalog: catalog, registeredModels: []).isEmpty)
        // No list, the built-in one, or a list that does not name it: the old rule.
        let elsewhere = ModelCatalog(source: "cli", models: [ModelOption(value: "opus", displayName: "Opus")])
        for list in [nil, ProviderOptions.fallbackCatalog("claude"), elsewhere] as [ModelCatalog?] {
            #expect(ProviderOptions.effortLevels(provider: "claude", model: "haiku", catalog: list, registeredModels: []).isEmpty)
        }
        // A list that says a Haiku model takes an effort without naming the levels is believed; one that says no is too.
        let unnamed = ProviderService.normalizeClaudeCatalog([["value": "claude-haiku-6", "supportsEffort": true]])
        #expect(unnamed.models.last?.supportsEffort == true)
        #expect(ProviderOptions.effortLevels(provider: "claude", model: "claude-haiku-6", catalog: unnamed, registeredModels: []) == ["low", "medium", "high"])
        let refused = ProviderService.normalizeClaudeCatalog([["value": "claude-haiku-6", "supportsEffort": false]])
        #expect(ProviderOptions.effortLevels(provider: "claude", model: "claude-haiku-6", catalog: refused, registeredModels: []).isEmpty)
        // A model the user registered says for itself, Haiku or not.
        let registered = [RegisteredModelEntry(name: "claude-haiku-7", supportsEffort: true, supportedEffortLevels: ["low", "high"])]
        #expect(ProviderOptions.effortLevels(provider: "claude", model: "claude-haiku-7", catalog: elsewhere, registeredModels: registered) == ["low", "high"])
    }

    @Test func aHaikuRunWithAnEffortIsCheckedAgainstTheCLIsList() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let catalog = try Self.claudeCatalog()
            // Without a list, no rule by name: the run's list decides.
            try CoreValidation.validate(request("haiku", effort: "max"))
            for model in ["haiku", "claude-haiku-5-5"] {
                try CoreValidation.validateSelection(request(model, effort: "max"), catalog: catalog, registeredModels: [])
            }
            // A Haiku model the CLI says nothing about takes none, refused in Haiku's words.
            for (model, list) in [("claude-haiku-4-5-20251001", catalog), ("haiku", ProviderOptions.fallbackCatalog("claude"))] {
                do {
                    try CoreValidation.validateSelection(request(model, effort: "high"), catalog: list, registeredModels: [])
                    Issue.record("\(model) took an effort")
                } catch { #expect(error.localizedDescription == L("validation.run.haikuEffort")) }
            }
            // A level its list does not name is an unknown one, not a Haiku refusal.
            let low = ProviderService.normalizeClaudeCatalog([["value": "haiku", "supportsEffort": true, "supportedEffortLevels": ["low"]]])
            do {
                try CoreValidation.validateSelection(request("haiku", effort: "max"), catalog: low, registeredModels: [])
                Issue.record("max passed a list of low")
            } catch { #expect(error.localizedDescription == L("validation.run.effortUnknown")) }
            // The run passes the effort as it does any Claude model's.
            let arguments = try ProviderService.arguments(request("haiku", effort: "max"), pluginDirectory: URL(fileURLWithPath: "/tmp/plugin"))
            let effort = try #require(arguments.firstIndex(of: "--effort")), model = try #require(arguments.firstIndex(of: "--model"))
            #expect(arguments[effort + 1] == "max" && arguments[model + 1] == "haiku")
            let settings = try #require(arguments.firstIndex(of: "--settings"))
            let environment = try JSONSerialization.jsonObject(with: Data(arguments[settings + 1].utf8)) as? [String: [String: String]]
            #expect(environment?["env"]?["CLAUDE_CODE_EFFORT_LEVEL"] == "max")
        }
    }

    @Test func thePhaseEffortASavedSelectionAndADelegatedChildKeepHaikusEffort() throws {
        let catalog = try Self.claudeCatalog()
        // Settings › Models: the execution effort reaches a Haiku main model (named, or the pane's default standing for it).
        let config = PhaseModelConfig(claudeMain: "haiku", claudeMainEffort: "high")
        #expect(config.runEffort(provider: "claude", paneEffort: "default", paneModel: "haiku", catalog: catalog, registeredModels: []) == "high")
        #expect(config.runEffort(provider: "claude", paneEffort: "default", paneModel: "default", catalog: catalog, registeredModels: []) == "high")
        #expect(PhaseModelConfig(claudeMain: "claude-haiku-4-5-20251001", claudeMainEffort: "high")
            .runEffort(provider: "claude", paneEffort: "default", paneModel: "default", catalog: catalog, registeredModels: []) == "default")
        // A fresh list keeps a Haiku pane's effort; Haiku 4.5's goes back to the CLI's default.
        #expect(ModelSelectionSupport.reconcile(model: "haiku", effort: "max", previous: nil, refreshed: catalog, provider: "claude") == .init(model: "haiku", effort: "max"))
        #expect(ModelSelectionSupport.reconcile(model: "claude-haiku-4-5-20251001", effort: "max", previous: nil, refreshed: catalog, provider: "claude")
                == .init(model: "claude-haiku-4-5-20251001", effort: "default"))
        // A child the human starts from a Haiku pane takes its model and effort, and its run checks out against the list.
        let parent = RunSession(id: "parent", workspaceId: "ws", title: "Claude", provider: "claude", model: "haiku", settings: RunSettings(effort: "xhigh"))
        let child = DelegationPanes.childSession(DelegationChildPane(sessionId: "c1", parentSessionId: "parent", mode: "acceptEdits", folder: "/w/c1", startedByHuman: true),
                                                 workspaceId: "ws", title: "Fix it", parent: parent)
        #expect(child.model == "haiku" && child.settings.effort == "xhigh")
        let run = StartRunRequest(sessionId: child.id, workspaceId: "ws", input: "Fix it", model: child.model, provider: child.provider, settings: child.settings)
        try CoreValidation.validateSelection(run, catalog: catalog, registeredModels: [])
    }

    // MARK: Codex's ultra

    @Test func codexsListKeepsUltraForTheModelsThatNameIt() throws {
        let catalog = try Self.codexCatalog()
        let named = catalog.models.dropFirst().filter { $0.supportedEffortLevels?.contains("ultra") == true }.map(\.value)
        #expect(named == ["gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-5.6-sol", "gpt-6.1-terra", "gpt-5.6-terra"])
        #expect(ProviderOptions.effortLevels(provider: "codex", model: "gpt-6.1-sol", catalog: catalog, registeredModels: []) == ["low", "medium", "high", "xhigh", "max", "ultra"])
        // The default stands for gpt-6.1-sol, and takes what it takes.
        #expect(catalog.models[0].resolvedModel == "gpt-6.1-sol" && ProviderOptions.effortLevels(provider: "codex", model: "default", catalog: catalog, registeredModels: []).last == "ultra")
        // A model that does not name it is not offered it.
        #expect(ProviderOptions.effortLevels(provider: "codex", model: "gpt-6-luna", catalog: catalog, registeredModels: []) == ProviderOptions.efforts)
        #expect(ProviderOptions.effortLevels(provider: "codex", model: "gpt-5.5", catalog: catalog, registeredModels: []) == ["low", "medium", "high", "xhigh"])
        // An effort no agent knows is still left out.
        let unknown = ProviderService.normalizeCodexCatalog([["model": "gpt-7", "supportedReasoningEfforts": [["reasoningEffort": "ultra"], ["reasoningEffort": "persistent"]]]])
        #expect(unknown.models.last?.supportedEffortLevels == ["ultra"])
    }

    @Test func noOtherAgentIsOfferedUltra() throws {
        // A Claude list that named it would still not bring it.
        let claude = ProviderService.normalizeClaudeCatalog([["value": "opus", "supportsEffort": true, "supportedEffortLevels": ["high", "max", "ultra"]]])
        #expect(claude.models.last?.supportedEffortLevels == ["high", "max"])
        let named = ModelCatalog(source: "cli", models: [ModelOption(value: "m", displayName: "M", supportsEffort: true, supportedEffortLevels: ProviderOptions.codexEfforts)])
        for provider in ["claude", "gemini", "pi", "opencode", "hermes"] {
            for model in ["default", "m", "opus"] {
                #expect(!ProviderOptions.effortLevels(provider: provider, model: model, catalog: named, registeredModels: []).contains("ultra"), "\(provider) \(model)")
            }
            #expect(!ProviderOptions.runEfforts(provider).contains("ultra"))
            // A run set to it is refused, and a pane saved with it goes back to the CLI's default.
            let request = StartRunRequest(sessionId: "pane", workspaceId: "space", input: "x", provider: provider, settings: RunSettings(effort: "ultra"))
            #expect(throws: (any Error).self, "\(provider)") { try CoreValidation.validate(request) }
            #expect(ProviderOptions.normalizedSettings(provider: provider, settings: RunSettings(effort: "ultra")).effort == "default")
        }
        #expect(ProviderOptions.runEfforts("codex") == ProviderOptions.codexEfforts && ProviderOptions.normalizedSettings(provider: "codex", settings: RunSettings(effort: "ultra")).effort == "ultra")
        // Settings › Models offers it on no knob (they reach every run whatever its model): a host refuses a run whose knobs
        // carry it, as an older Mac or Windows host would.
        for knob in [\PhaseModelHardcodedConfig.codexMainEffort, \.codexSubagentEffort, \.claudeMainEffort] {
            var phase = PhaseModelHardcodedConfig(); phase[keyPath: knob] = "ultra"
            #expect(!RemoteWorkspaceValidation.phaseModels(phase))
        }
        #expect(!RemoteWorkspaceValidation.phaseModels(PhaseModelHardcodedConfig(codexPlanModeReasoningEffort: "ultra")))
    }

    @Test func aCodexRunPassesUltraAsItPassesEveryEffort() throws {
        let catalog = try Self.codexCatalog()
        let plugin = URL(fileURLWithPath: "/tmp/plugin")
        var request = StartRunRequest(sessionId: "pane", workspaceId: "space", input: "Reply with ok.", model: "gpt-6.1-sol", provider: "codex", settings: RunSettings(effort: "ultra"))
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: [])
        // `codex exec`, as run once by hand against codex-cli 0.167.0 (read-only, this argv as it stands).
        let exec = try ProviderService.arguments(request, pluginDirectory: plugin)
        #expect(exec == ["-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"read-only\"", "-c", "sandbox_workspace_write.network_access=false", "-c", "features.fast_mode=false",
                         "-c", "service_tier=\"default\"", "exec", "--json", "--skip-git-repo-check", "--model", "gpt-6.1-sol", "-c", "model_reasoning_effort=\"ultra\"", "-"])
        // A model that does not name it refuses it before anything runs.
        var luna = request; luna.model = "gpt-6-luna"
        #expect(throws: (any Error).self) { try CoreValidation.validateSelection(luna, catalog: catalog, registeredModels: []) }
        // The app-server (approvals in the pane): in its config and in the turn it starts.
        request.settings.permissionMode = "onRequest"
        let server = try ProviderService.arguments(request, pluginDirectory: plugin, allowPermissionPrompts: true)
        #expect(server.suffix(7) == ["-c", "model=\"gpt-6.1-sol\"", "-c", "model_reasoning_effort=\"ultra\"", "app-server", "--listen", "stdio://"])
        final class Writes: @unchecked Sendable { var objects: [[String: Any]] = [] }
        let writes = Writes()
        let channel = CodexApprovalChannel(runId: "run", request: request, workspacePath: "/work", attachments: try AttachmentPreparation([]),
                                           write: { writes.objects.append((try? JSONSerialization.jsonObject(with: $0)) as? [String: Any] ?? [:]) }, event: { _ in },
                                           emit: { _ in }, activity: { _, _ in }, warning: { _ in }, fail: { _ in }, completed: {})
        func answer(_ result: [String: Any]) { channel.receive(try! JSONSerialization.data(withJSONObject: ["id": writes.objects.last!["id"]!, "result": result]) + Data([10])) }
        channel.start(); answer([:]); answer(["thread": ["id": "thread"]])
        let turn = try #require(writes.objects.last)
        #expect(turn["method"] as? String == "turn/start" && (turn["params"] as? [String: Any])?["effort"] as? String == "ultra")
    }

    @Test func savedSettingsDecodeAsBeforeAndKeepUltraOnlyOnACodexPane() async throws {
        // A save from before (no newer keys) reads as it did.
        let older = Data(#"{"effort":"xhigh","permissionMode":"acceptEdits","maxTurns":null,"maxBudgetUsd":null}"#.utf8)
        #expect(try JSONDecoder().decode(RunSettings.self, from: older) == RunSettings(effort: "xhigh", permissionMode: "acceptEdits"))
        let ultra = try JSONDecoder().decode(RunSettings.self, from: Data(#"{"effort":"ultra","permissionMode":"manual","maxTurns":null,"maxBudgetUsd":null}"#.utf8))
        #expect(ultra.effort == "ultra")
        // Through the state file: the Codex pane keeps it, the Claude pane goes back to the CLI's default, the older one stays.
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ultra-state-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        _ = try await repository.load()
        let workspace = try await repository.approveWorkspace(Workspace(name: "Ultra", path: directory.path))
        let panes = [RunSession(id: "codex", workspaceId: workspace.id, title: "Codex", provider: "codex", model: "gpt-6.1-sol", settings: ultra),
                     RunSession(id: "claude", workspaceId: workspace.id, title: "Claude", provider: "claude", settings: ultra),
                     RunSession(id: "older", workspaceId: workspace.id, title: "Codex", provider: "codex", settings: try JSONDecoder().decode(RunSettings.self, from: older))]
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: panes))
        let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
        let efforts = Dictionary(uniqueKeysWithValues: restored.sessions.map { ($0.id, $0.settings.effort) })
        #expect(efforts == ["codex": "ultra", "claude": "default", "older": "xhigh"])
    }

    @Test func ultraIsNamedInEveryLanguageAndReachesTheStatusLine() throws {
        let names: [AppLanguage: String] = [.ko: "Ultra · 자동 위임", .en: "Ultra · auto-delegation", .ja: "Ultra · 自動委任", .zh: "Ultra · 自动委派"]
        for (language, name) in names {
            LocaleOverride.$language.withValue(language) { #expect(L("composer.effort.ultra") == name, "\(language)") }
        }
        // A block's chip names it as short as the other levels.
        #expect(FlowRoles.effortName("ultra") == "Ultra" && FlowRoles.effortName("bogus") == nil)
        var context = StatusLineContext(sessionId: "s", cwd: "/w", projectDir: "/w", modelId: "gpt-6.1-sol", modelName: "GPT-6.1-Sol", version: "0.167.0", effort: "ultra")
        #expect((StatusLineSupport.payload(context)["effort"] as? [String: Any])?["level"] as? String == "ultra")
        context.effort = "persistent"
        #expect(StatusLineSupport.payload(context)["effort"] == nil)
    }
}
