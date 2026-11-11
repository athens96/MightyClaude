import Foundation
import Testing
@testable import MightyCore

/// The efforts a model takes are what its CLI's own list says (native/contracts/fixtures/model-list-*): Claude Code
/// 2.1.322 names low to max for Haiku 5.5 and nothing for Haiku 4.5. A Haiku model no list speaks for keeps the old
/// rule, no effort.
struct ModelEffortTests {
    static func claudeCatalog() throws -> ModelCatalog {
        let lines = try ModelListProbeTests.fixture("model-list-claude.jsonl").split(separator: "\n").map { try #require(JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]) }
        let response = try #require(lines.compactMap { $0["response"] as? [String: Any] }.first)
        return ProviderService.normalizeClaudeCatalog(try #require((response["response"] as? [String: Any])?["models"] as? [[String: Any]]))
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
}
