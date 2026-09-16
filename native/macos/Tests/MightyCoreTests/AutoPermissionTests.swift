import Foundation
import Testing
@testable import MightyCore

@Suite struct AutoPermissionTests {
    @Test func autoIsClaudeOnlyAndRequiresAnAdvertisedSupportedRuntime() throws {
        #expect(RunSettings().permissionMode == "manual")
        #expect(ProviderOptions.permissionModes(provider: "claude") == ["plan", "manual", "acceptEdits", "auto", "fullAccess"])
        let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "fixture only", settings: RunSettings(permissionMode: "auto"))
        try CoreValidation.validate(request)
        for version in [nil, "unknown", "2.1.82", "2.1.270", "2.1.273-preview"] as [String?] {
            let capabilities = ProviderService.capabilities(provider: "claude", version: version)
            #expect(!capabilities.permissionModes.contains("auto"))
            #expect(throws: MightyError.self) { try CoreValidation.validateCapabilities(request, capabilities: capabilities) }
        }
        for version in ["2.1.271", "2.1.273 (Claude Code)", "3.0.0"] {
            let capabilities = ProviderService.capabilities(provider: "claude", version: version)
            #expect(capabilities.permissionModes.contains("auto"))
            try CoreValidation.validateCapabilities(request, capabilities: capabilities)
        }
        #expect(!ProviderOptions.fallbackRuntime("claude").capabilities.permissionModes.contains("auto"))
        for provider in ["codex", "gemini"] {
            var other = request; other.provider = provider
            #expect(throws: MightyError.self) { try CoreValidation.validate(other) }
            #expect(ProviderOptions.normalizedSettings(provider: provider, settings: request.settings).permissionMode == "manual")
        }
        var shell = request; shell.kind = "shell"
        #expect(throws: MightyError.self) { try CoreValidation.validate(shell) }
        let oldCapabilities = try JSONDecoder().decode(ProviderCapabilities.self, from: Data(#"{"effort":true,"permissionModes":["manual","plan","acceptEdits"],"maxTurns":true,"maxBudgetUsd":true,"resume":true}"#.utf8))
        #expect(throws: MightyError.self) { try CoreValidation.validateCapabilities(request, capabilities: oldCapabilities) }
    }

    @Test func autoPersistsWithoutChangingLegacyDefaultOrAddingWireFields() async throws {
        let settings = RunSettings(permissionMode: "auto")
        let encoded = try JSONEncoder().encode(settings)
        let object = try #require(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        #expect(Set(object.keys) == ["effort", "permissionMode", "maxTurns", "maxBudgetUsd"])
        #expect(object["permissionMode"] as? String == "auto")
        #expect(try JSONDecoder().decode(RunSettings.self, from: encoded) == settings)
        #expect(try JSONDecoder().decode(RunSettings.self, from: Data("{}".utf8)).permissionMode == "manual")
        #expect(ProviderOptions.normalizedSettings(provider: "claude", settings: settings) == settings)
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-auto-settings-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        _ = try await repository.load()
        let workspace = try await repository.approveWorkspace(Workspace(name: "Auto fixture", path: directory.path))
        let session = RunSession(workspaceId: workspace.id, title: "Claude", settings: settings)
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [session]))
        let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
        #expect(restored.sessions.first?.settings == settings)
    }
}
