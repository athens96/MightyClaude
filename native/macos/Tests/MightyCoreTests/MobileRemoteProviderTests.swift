import Foundation
import Testing
@testable import MightyCore

/// What the Mac host tells a phone about a pane's provider: the same rules the Mac's own
/// composer and model chip use, so an Antigravity or Codex pane is not offered what the
/// host cannot do with it.
struct MobileRemoteProviderTests {
    private func summary(_ pane: RunSession, catalog: ModelCatalog) throws -> [String: Any] {
        let value = MobileSessionSummary(id: pane.id, workspaceId: pane.workspaceId, title: pane.title, kind: pane.kind, provider: pane.provider, model: pane.model,
                                         status: "running", revision: 1, updatedAt: pane.createdAt,
                                         resolvedModel: MobileRemoteSupport.resolvedModel(pane, catalog: catalog), canSteer: MobileRemoteSupport.canSteer(pane))
        return try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(value)) as? [String: Any])
    }

    private func usage(_ provider: String, model: String, selected: String) -> SessionUsage {
        var usage = SessionUsage(provider: provider, source: "test", model: model)
        usage.selectedModel = selected
        return usage
    }

    @Test func anAntigravityPaneOnDefaultCarriesNoModelAndCannotSteer() throws {
        let catalog = ProviderOptions.fallbackCatalog("gemini")
        // agy reported a model for this very selection: still no version for `default`, which may mean another model next run.
        let pane = RunSession(workspaceId: "w", title: "agy", provider: "gemini", model: "default", sessionUsage: usage("gemini", model: "gemini-4-argon-medium", selected: "default"))
        #expect(ProviderOptions.label(pane.provider) == "Antigravity" && ProviderOptions.executable(pane.provider) == "agy")
        let capabilities = ProviderService.capabilities(provider: "gemini", version: "1.2.3")
        #expect(!capabilities.effort, "no effort picker: each agy model slug carries its own effort")
        #expect(capabilities.permissionModes == ["manual", "plan", "acceptEdits", "fullAccess"])
        #expect(ModelLabel.selection(pane, catalog: catalog) == L("provider.fallback.defaultLabel", ["name": "Antigravity"]))

        let wire = try summary(pane, catalog: catalog)
        #expect(wire["provider"] as? String == "gemini" && wire["model"] as? String == "default")
        #expect(wire["resolvedModel"] == nil, "the phone reads just the Antigravity default label")
        #expect(wire["canSteer"] as? Bool == false, "an agy turn takes no follow-up, so the phone only queues")
    }

    @Test func onlyAClaudeAgentPaneSteers() throws {
        let claude = RunSession(workspaceId: "w", title: "c", provider: "claude", model: "opus", sessionUsage: usage("claude", model: "claude-opus-5-5", selected: "opus"))
        let codex = RunSession(workspaceId: "w", title: "x", provider: "codex", model: "default")
        let shell = RunSession(workspaceId: "w", title: "s", kind: SessionKind.shell, provider: "claude")
        #expect(MobileRemoteSupport.canSteer(claude) && !MobileRemoteSupport.canSteer(codex) && !MobileRemoteSupport.canSteer(shell))
        #expect(MobileRemoteSupport.canSteer(claude) == DelegationProviders.canSteer(kind: claude.kind, provider: claude.provider), "one rule with the Mac composer")
        #expect(try summary(claude, catalog: ProviderOptions.fallbackCatalog("claude"))["canSteer"] as? Bool == true)
        #expect(try summary(codex, catalog: ProviderOptions.fallbackCatalog("codex"))["canSteer"] as? Bool == false)
        // A family alias takes the model reported for it; a command pane has no model to name.
        #expect(MobileRemoteSupport.resolvedModel(claude, catalog: ProviderOptions.fallbackCatalog("claude")) == "claude-opus-5-5")
        #expect(MobileRemoteSupport.resolvedModel(shell, catalog: ProviderOptions.fallbackCatalog("claude")) == nil)
        // Reported for an earlier selection: stale, so nothing travels.
        var switched = claude; switched.model = "sonnet"
        #expect(MobileRemoteSupport.resolvedModel(switched, catalog: ProviderOptions.fallbackCatalog("claude")) == nil)
    }

    @Test func anOlderPhoneReadsTheSummaryWithoutTheNewField() throws {
        let data = Data(#"{"id":"s","workspaceId":"w","title":"t","kind":"claude","provider":"gemini","model":"default","status":"idle","revision":1,"updatedAt":"2026-10-22T00:00:00Z","pendingPermissions":0,"pendingQuestions":0,"queued":0,"terminal":false}"#.utf8)
        let decoded = try JSONDecoder().decode(MobileSessionSummary.self, from: data)
        #expect(decoded.canSteer == nil && decoded.resolvedModel == nil)
    }
}
