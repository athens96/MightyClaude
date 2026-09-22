import Foundation
import Testing
@testable import MightyCore

@Suite struct CodexApprovalSettingsTests {
    private let plugin = URL(fileURLWithPath: "/tmp/codex-approval-fixture")
    private func request(mode: String = "onRequest", network: Bool = false) -> StartRunRequest {
        StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "fixture prompt", provider: "codex", settings: RunSettings(permissionMode: mode, networkAccess: network))
    }

    @Test func explicitChoicePersistsWithoutMigratingSavedDefaults() throws {
        #expect(RunSettings().permissionMode == "manual")
        #expect(!RunSettings().networkAccess)
        #expect(try JSONDecoder().decode(RunSettings.self, from: Data("{}".utf8)) == RunSettings())
        for mode in ["manual", "acceptEdits", "onRequest", "fullAccess"] {
            let settings = RunSettings(permissionMode: mode)
            #expect(try JSONDecoder().decode(RunSettings.self, from: JSONEncoder().encode(settings)) == settings)
            #expect(ProviderOptions.normalizedSettings(provider: "codex", settings: settings) == settings)
        }
        for mode in ["acceptEdits", "onRequest"] {
            let settings = RunSettings(permissionMode: mode, networkAccess: true)
            #expect(ProviderOptions.normalizedSettings(provider: "codex", settings: settings) == settings)
            try CoreValidation.validate(request(mode: mode, network: true))
        }
        for mode in ["manual", "fullAccess"] {
            #expect(!ProviderOptions.normalizedSettings(provider: "codex", settings: RunSettings(permissionMode: mode, networkAccess: true)).networkAccess)
            #expect(throws: MightyError.self) { try CoreValidation.validate(request(mode: mode, network: true)) }
        }
    }

    @Test func onlySupportedCodexRuntimeAdvertisesTheMode() throws {
        let request = request()
        try CoreValidation.validate(request)
        #expect(!ProviderOptions.fallbackRuntime("codex").capabilities.permissionModes.contains("onRequest"))
        for version in [nil, "unknown", "0.152.0", "0.153.3", "0.153.4-preview", "0.154.0-alpha"] as [String?] {
            let caps = ProviderService.capabilities(provider: "codex", version: version)
            #expect(!caps.permissionModes.contains("onRequest"))
            #expect(throws: MightyError.self) { try CoreValidation.validateCapabilities(request, capabilities: caps) }
        }
        for version in ["codex-cli 0.153.4", "0.153.5", "0.154.0", "1.0.0"] {
            let caps = ProviderService.capabilities(provider: "codex", version: version)
            #expect(caps.permissionModes.contains("onRequest"))
            try CoreValidation.validateCapabilities(request, capabilities: caps)
        }
        for provider in ["claude", "gemini"] {
            var other = request; other.provider = provider
            #expect(throws: MightyError.self) { try CoreValidation.validate(other) }
            #expect(ProviderOptions.normalizedSettings(provider: provider, settings: other.settings).permissionMode == "manual")
        }
        var shell = request; shell.kind = "shell"
        #expect(throws: MightyError.self) { try CoreValidation.validate(shell) }
        let old = ProviderCapabilities(permissionModes: ["manual", "acceptEdits", "fullAccess"])
        #expect(throws: MightyError.self) { try CoreValidation.validateCapabilities(request, capabilities: old) }
    }

    @Test func approvalModeRequiresHostChannelAndDoesNotUsePromptArgumentsOrExec() throws {
        var request = request(network: true)
        request.model = "gpt-6-astra"; request.resumeId = "previous-thread"
        request.settings.effort = "high"; request.settings.fastMode = true; request.settings.webSearch = "live"
        #expect(throws: MightyError.self) { try ProviderService.arguments(request, pluginDirectory: plugin) }
        let args = try ProviderService.arguments(request, pluginDirectory: plugin, allowPermissionPrompts: true)
        #expect(args == ["-c", "approval_policy=\"on-request\"", "-c", "sandbox_mode=\"workspace-write\"", "-c", "sandbox_workspace_write.network_access=true", "-c", "features.fast_mode=true", "-c", "service_tier=\"fast\"", "-c", "web_search=\"live\"", "-c", "approvals_reviewer=\"user\"", "-c", "model=\"gpt-6-astra\"", "-c", "model_reasoning_effort=\"high\"", "app-server", "--listen", "stdio://"])
        #expect(!args.contains(request.input)); #expect(!args.contains("previous-thread")); #expect(!args.contains("exec"))
        let defaultArgs = try ProviderService.arguments(self.request(), pluginDirectory: plugin, allowPermissionPrompts: true)
        #expect(defaultArgs.contains("sandbox_workspace_write.network_access=false"))
        #expect(!defaultArgs.contains { $0.hasPrefix("model=") || $0.hasPrefix("model_reasoning_effort=") })
        let attachments = try AttachmentPreparation([])
        #expect(throws: MightyError.self) { try ProviderInput.prepare(request, pluginDirectory: plugin, attachments: attachments, allowPermissionPrompts: true) }
        request.attachments = [try AttachmentSupport.make(name: "fixture.txt", data: Data("fixture".utf8))]
        let staged = try AttachmentPreparation(request.attachments); defer { staged.cleanup() }
        #expect(throws: MightyError.self) { try ProviderInput.prepare(request, pluginDirectory: plugin, attachments: staged, allowPermissionPrompts: true) }
    }

    @Test func legacyModesKeepTheirExactNoninteractiveCommands() throws {
        for (mode, sandbox) in [("manual", "read-only"), ("acceptEdits", "workspace-write"), ("fullAccess", "danger-full-access")] {
            let request = request(mode: mode)
            let args = try ProviderService.arguments(request, pluginDirectory: plugin)
            #expect(args == ["-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"\(sandbox)\"", "-c", "sandbox_workspace_write.network_access=false", "-c", "features.fast_mode=false", "-c", "service_tier=\"default\"", "exec", "--json", "--skip-git-repo-check", "-"])
            #expect(try ProviderService.arguments(request, pluginDirectory: plugin, allowPermissionPrompts: true) == args)
        }
    }
}
