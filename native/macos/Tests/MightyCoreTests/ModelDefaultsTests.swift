import Foundation
import Testing
@testable import MightyCore

struct ModelDefaultsTests {
    // MARK: - Resolution rule: explicit model > "default" → mode default

    @Test func explicitModelWinsOverModeDefault() {
        let appDefaults = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "claude-sonnet-5"]))
        let result = ModelDefaultsResolution.resolve(
            sessionModel: "claude-opus-5",
            provider: "claude",
            permissionMode: "manual",
            workspaceDefaults: nil,
            appDefaults: appDefaults
        )
        #expect(result == "claude-opus-5")
    }

    @Test func explicitModelIgnoresWorkspaceAndAppDefaults() {
        let workspace = ModelDefaultsConfig(claude: .init(modeDefaults: ["auto": "claude-sonnet-5"]))
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: ["auto": "claude-fable-5-1"]))
        let result = ModelDefaultsResolution.resolve(
            sessionModel: "my-company/claude-custom",
            provider: "claude",
            permissionMode: "auto",
            workspaceDefaults: workspace,
            appDefaults: app
        )
        #expect(result == "my-company/claude-custom")
    }

    // MARK: - Resolution rule: "default" session → app mode default

    @Test func defaultSessionUsesAppModeDefault() {
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: [
            "manual": "claude-sonnet-5",
            "auto": "claude-opus-5",
            "plan": "claude-fable-5-1"
        ]))
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "manual", workspaceDefaults: nil, appDefaults: app) == "claude-sonnet-5")
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "auto", workspaceDefaults: nil, appDefaults: app) == "claude-opus-5")
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "plan", workspaceDefaults: nil, appDefaults: app) == "claude-fable-5-1")
    }

    @Test func noDefaultsReturnDefault() {
        for mode in ["manual", "plan", "acceptEdits", "auto", "fullAccess"] {
            #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: mode, workspaceDefaults: nil, appDefaults: nil) == "default")
        }
        for mode in ["manual", "acceptEdits", "onRequest", "fullAccess"] {
            #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "codex", permissionMode: mode, workspaceDefaults: nil, appDefaults: nil) == "default")
        }
    }

    // MARK: - Resolution rule: workspace override > app default

    @Test func workspaceOverrideBeatsAppDefault() {
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "claude-sonnet-5"]))
        let workspace = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "claude-opus-5"]))
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "manual", workspaceDefaults: workspace, appDefaults: app) == "claude-opus-5")
    }

    @Test func workspaceDefaultValueFallsThroughToAppDefault() {
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "claude-sonnet-5"]))
        // workspace explicitly records "default" for "manual" — falls through
        let workspace = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "default"]))
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "manual", workspaceDefaults: workspace, appDefaults: app) == "claude-sonnet-5")
    }

    @Test func workspaceOverrideOnlyAppliesToItsMode() {
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: ["plan": "claude-sonnet-5"]))
        let workspace = ModelDefaultsConfig(claude: .init(modeDefaults: ["auto": "claude-opus-5"]))
        // workspace sets "auto", app sets "plan"
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "auto", workspaceDefaults: workspace, appDefaults: app) == "claude-opus-5")
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "plan", workspaceDefaults: workspace, appDefaults: app) == "claude-sonnet-5")
        // neither sets "manual"
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "manual", workspaceDefaults: workspace, appDefaults: app) == "default")
    }

    // MARK: - Resolution rule: permission mode menu label = that mode's resolution result

    @Test func modeMenuLabelIsResolvedForMode() {
        let app = ModelDefaultsConfig(
            claude: .init(modeDefaults: ["manual": "claude-sonnet-5", "auto": "claude-opus-5"]),
            codex: .init(modeDefaults: ["manual": "gpt-6-astra"])
        )
        #expect(ModelDefaultsResolution.modeMenuLabel(provider: "claude", permissionMode: "manual", workspaceDefaults: nil, appDefaults: app) == "claude-sonnet-5")
        #expect(ModelDefaultsResolution.modeMenuLabel(provider: "claude", permissionMode: "auto", workspaceDefaults: nil, appDefaults: app) == "claude-opus-5")
        #expect(ModelDefaultsResolution.modeMenuLabel(provider: "codex", permissionMode: "manual", workspaceDefaults: nil, appDefaults: app) == "gpt-6-astra")
        // unset mode label is "default"
        #expect(ModelDefaultsResolution.modeMenuLabel(provider: "claude", permissionMode: "plan", workspaceDefaults: nil, appDefaults: app) == "default")
    }

    @Test func modeMenuLabelRespectsWorkspaceOverApp() {
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "claude-sonnet-5"]))
        let workspace = ModelDefaultsConfig(claude: .init(modeDefaults: ["manual": "claude-opus-5"]))
        #expect(ModelDefaultsResolution.modeMenuLabel(provider: "claude", permissionMode: "manual", workspaceDefaults: workspace, appDefaults: app) == "claude-opus-5")
    }

    @Test func explicitSelectionSurvivesPermissionModeChanges() {
        let app = ModelDefaultsConfig(claude: .init(modeDefaults: [
            "manual": "claude-sonnet-5",
            "plan": "claude-fable-5-1",
            "acceptEdits": "claude-opus-5",
            "auto": "claude-opus-5",
            "fullAccess": "claude-sonnet-5"
        ]))
        // /model picked an explicit name: every permission mode keeps resolving to it,
        // while the mode menu still advertises that mode's own default.
        for mode in ["manual", "plan", "acceptEdits", "auto", "fullAccess"] {
            #expect(ModelDefaultsResolution.resolve(sessionModel: "claude-haiku-4-5-20251001", provider: "claude", permissionMode: mode, workspaceDefaults: nil, appDefaults: app) == "claude-haiku-4-5-20251001")
            #expect(ModelDefaultsResolution.modeMenuLabel(provider: "claude", permissionMode: mode, workspaceDefaults: nil, appDefaults: app) == app.claude.modeDefaults[mode])
        }
    }

    // MARK: - Provider isolation: Codex defaults don't bleed into Claude and vice versa

    @Test func codexProviderUsesCodexDefaults() {
        let app = ModelDefaultsConfig(
            claude: .init(modeDefaults: ["manual": "claude-sonnet-5"]),
            codex: .init(modeDefaults: ["manual": "gpt-6-astra"])
        )
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "codex", permissionMode: "manual", workspaceDefaults: nil, appDefaults: app) == "gpt-6-astra")
        #expect(ModelDefaultsResolution.resolve(sessionModel: "default", provider: "claude", permissionMode: "manual", workspaceDefaults: nil, appDefaults: app) == "claude-sonnet-5")
    }

    // MARK: - AppSnapshot carries modelDefaults field and round-trips

    @Test func appSnapshotModelDefaultsRoundTrips() throws {
        let config = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["manual": "claude-sonnet-5", "auto": "claude-opus-5"],
                registeredModels: [.init(name: "my-claude", supportsEffort: true, supportedEffortLevels: ["high", "max"])]
            ),
            codex: .init(modeDefaults: ["manual": "gpt-6-astra"])
        )
        var snapshot = AppSnapshot()
        snapshot.modelDefaults = config
        let encoded = try JSONEncoder().encode(snapshot)
        let decoded = try JSONDecoder().decode(AppSnapshot.self, from: encoded)
        #expect(decoded.modelDefaults == config)
    }

    @Test func oldSnapshotWithoutModelDefaultsDecodesAsNil() throws {
        let json = Data("""
        {"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":252}
        """.utf8)
        let snapshot = try JSONDecoder().decode(AppSnapshot.self, from: json)
        #expect(snapshot.modelDefaults == nil)
    }

    @Test func workspaceModelDefaultsRoundTrips() throws {
        let config = ModelDefaultsConfig(claude: .init(modeDefaults: ["plan": "claude-fable-5-1"]))
        let workspace = Workspace(name: "test", path: "/tmp/test", modelDefaults: config)
        let encoded = try JSONEncoder().encode(workspace)
        let decoded = try JSONDecoder().decode(Workspace.self, from: encoded)
        #expect(decoded.modelDefaults == config)
    }

    @Test func oldWorkspaceWithoutModelDefaultsDecodesAsNil() throws {
        let json = Data("""
        {"id":"abc","name":"test","path":"/tmp/test","createdAt":"2026-09-22T00:00:00Z"}
        """.utf8)
        let workspace = try JSONDecoder().decode(Workspace.self, from: json)
        #expect(workspace.modelDefaults == nil)
    }
}

// MARK: - Registered model name validation and selection tests

struct RegisteredModelTests {

    // MARK: - CoreValidation.validateRegistration

    @Test func registrationTrimsWhitespace() throws {
        let trimmed = try CoreValidation.validateRegistration(name: "  my-model  ", provider: "claude", existingEntries: [])
        #expect(trimmed == "my-model")
    }

    @Test func registrationRejectsEmpty() {
        var threw = false
        do { _ = try CoreValidation.validateRegistration(name: "", provider: "claude", existingEntries: []) } catch { threw = true }
        #expect(threw)

        threw = false
        do { _ = try CoreValidation.validateRegistration(name: "   ", provider: "claude", existingEntries: []) } catch { threw = true }
        #expect(threw)
    }

    @Test func registrationRejectsDefaultReservedName() {
        var threw = false
        do { _ = try CoreValidation.validateRegistration(name: "default", provider: "claude", existingEntries: []) } catch { threw = true }
        #expect(threw)
    }

    @Test func registrationRejectsDuplicateWithinProvider() {
        let existing = [RegisteredModelEntry(name: "my-model")]
        var threw = false
        do { _ = try CoreValidation.validateRegistration(name: "my-model", provider: "claude", existingEntries: existing) } catch { threw = true }
        #expect(threw)
    }

    @Test func registrationRejectsSpaceInMiddle() {
        var threw = false
        do { _ = try CoreValidation.validateRegistration(name: "my model", provider: "claude", existingEntries: []) } catch { threw = true }
        #expect(threw)
    }

    @Test func registrationRejectsOver200Chars() {
        let longName = String(repeating: "a", count: 201)
        var threw = false
        do { _ = try CoreValidation.validateRegistration(name: longName, provider: "claude", existingEntries: []) } catch { threw = true }
        #expect(threw)
    }

    @Test func registrationAcceptsValidNames() throws {
        let validNames = ["my-model", "acme/custom-claude", "claude.custom.v1", "MODEL123", "a-b:c"]
        for name in validNames {
            _ = try CoreValidation.validateRegistration(name: name, provider: "claude", existingEntries: [])
        }
    }

    // MARK: - validateSelection accepts registered models

    @Test func registeredModelPassesValidateSelection() throws {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let registered = [RegisteredModelEntry(name: "acme/special-claude")]
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "hello", model: "acme/special-claude", provider: "claude")
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: registered)
    }

    @Test func unregisteredUnknownModelFailsValidateSelection() {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "hello", model: "completely-unknown-xyz", provider: "claude")
        var threw = false
        do { try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: []) } catch { threw = true }
        #expect(threw)
    }

    @Test func registeredModelNamePassesThroughUnchanged() throws {
        // After validateSelection the model field is identical to the registered name —
        // no silent replacement occurs, so ['--model', name] receives the exact name.
        let registeredName = "my-org/custom-model-v2"
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let registered = [RegisteredModelEntry(name: registeredName)]
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "test", model: registeredName, provider: "claude")
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: registered)
        #expect(request.model == registeredName)
    }

    @Test func cliRejectionExposedNotSilentlyReplaced() {
        // validateSelection throws rather than falling back to a different model;
        // the caller sees the error unchanged.
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "test", model: "not-registered-abc", provider: "claude")
        var caughtError: MightyError?
        do {
            try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: [])
        } catch let e as MightyError {
            caughtError = e
        } catch {}
        #expect(caughtError != nil)
        // The model field is not silently replaced
        #expect(request.model == "not-registered-abc")
    }

    // MARK: - Effort levels for registered models

    @Test func registeredModelWithEffortLevels() {
        let registered = [RegisteredModelEntry(name: "acme/smart", supportsEffort: true, supportedEffortLevels: ["high", "max"])]
        let levels = ProviderOptions.effortLevels(provider: "claude", model: "acme/smart", catalog: nil, registeredModels: registered)
        #expect(levels == ["high", "max"])
    }

    @Test func registeredModelWithoutEffortSupport() {
        let registered = [RegisteredModelEntry(name: "acme/fast", supportsEffort: false)]
        let levels = ProviderOptions.effortLevels(provider: "claude", model: "acme/fast", catalog: nil, registeredModels: registered)
        #expect(levels.isEmpty)
    }

    @Test func registeredModelEffortValidatedInSelection() throws {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let registered = [RegisteredModelEntry(name: "acme/model", supportsEffort: true, supportedEffortLevels: ["high", "max"])]
        var settings = RunSettings(); settings.effort = "high"
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "test", model: "acme/model", provider: "claude", settings: settings)
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: registered)
    }

    @Test func registeredModelUnsupportedEffortFails() {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let registered = [RegisteredModelEntry(name: "acme/model", supportsEffort: true, supportedEffortLevels: ["high"])]
        var settings = RunSettings(); settings.effort = "max"
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "test", model: "acme/model", provider: "claude", settings: settings)
        var threw = false
        do { try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: registered) } catch { threw = true }
        #expect(threw)
    }

    @Test func catalogEntryTakesPrecedenceOverRegistered() throws {
        // When the catalog already has the model, catalog info wins over registered entry.
        let catalog = ProviderOptions.fallbackCatalog("claude")
        // "haiku" from catalog has supportsEffort == false → no effort levels
        let registered = [RegisteredModelEntry(name: "haiku", supportsEffort: true, supportedEffortLevels: ["high", "max"])]
        let levels = ProviderOptions.effortLevels(provider: "claude", model: "haiku", catalog: catalog, registeredModels: registered)
        #expect(levels.isEmpty)
    }

    // MARK: - The run path carries the registered names on the request

    // ProcessRunner validates with `registeredModels: request.registeredModels`, so a
    // registered name only survives the Claude official-name gate if the request the
    // store built actually carries the provider's names. These drive that same
    // expression instead of handing validateSelection a separate array.

    @Test func requestCarriedRegisteredModelsReachValidateSelection() throws {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "hello",
                                      model: "acme/run-path-model", provider: "claude",
                                      registeredModels: [RegisteredModelEntry(name: "acme/run-path-model")])
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: request.registeredModels)
    }

    @Test func requestWithoutRegisteredModelsIsRejectedOnTheRunPath() {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "hello",
                                      model: "acme/run-path-model", provider: "claude")
        var threw = false
        do { try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: request.registeredModels) } catch { threw = true }
        #expect(threw)
    }

    @Test func requestCarriedRegisteredEffortReachesValidateSelection() throws {
        let catalog = ProviderOptions.fallbackCatalog("claude")
        var settings = RunSettings(); settings.effort = "high"
        let request = StartRunRequest(sessionId: "sess-1", workspaceId: "ws-1", kind: "claude", input: "hello",
                                      model: "acme/effort-model", provider: "claude", settings: settings,
                                      registeredModels: [RegisteredModelEntry(name: "acme/effort-model", supportsEffort: true, supportedEffortLevels: ["high", "max"])])
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: request.registeredModels)
    }

    // MARK: - Registration rejection: reason must be exposed (not just thrown)

    @Test func registrationEmptyNameExposesMeaningfulReason() {
        do {
            _ = try CoreValidation.validateRegistration(name: "", provider: "claude", existingEntries: [])
            Issue.record("Expected error not thrown")
        } catch let e as MightyError {
            #expect(!e.message.isEmpty)
        } catch {
            Issue.record("Wrong error type: \(error)")
        }
    }

    @Test func registrationDefaultReservedNameExposesMeaningfulReason() {
        do {
            _ = try CoreValidation.validateRegistration(name: "default", provider: "claude", existingEntries: [])
            Issue.record("Expected error not thrown")
        } catch let e as MightyError {
            #expect(!e.message.isEmpty)
        } catch {
            Issue.record("Wrong error type: \(error)")
        }
    }

    @Test func registrationDuplicateExposesMeaningfulReason() {
        let existing = [RegisteredModelEntry(name: "my-model")]
        do {
            _ = try CoreValidation.validateRegistration(name: "my-model", provider: "claude", existingEntries: existing)
            Issue.record("Expected error not thrown")
        } catch let e as MightyError {
            #expect(!e.message.isEmpty)
        } catch {
            Issue.record("Wrong error type: \(error)")
        }
    }
}

// MARK: - Registered model deletion: rows revert to "default", count reported

struct RegisteredModelDeletionTests {

    @Test func deleteRevertsAppModeRows() {
        var app = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["manual": "acme/custom", "plan": "acme/custom", "auto": "claude-sonnet-5"],
                registeredModels: [.init(name: "acme/custom")]
            )
        )
        let reverted = ModelDefaultsResolution.removeRegisteredModel(name: "acme/custom", provider: "claude", from: &app)
        #expect(reverted == 2)
        #expect(app.claude.modeDefaults["manual"] == "default")
        #expect(app.claude.modeDefaults["plan"] == "default")
        #expect(app.claude.modeDefaults["auto"] == "claude-sonnet-5")
        #expect(app.claude.registeredModels.isEmpty)
    }

    @Test func deleteRevertsWorkspaceModeRows() {
        var workspace = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["acceptEdits": "acme/custom"],
                registeredModels: [.init(name: "acme/custom")]
            )
        )
        let reverted = ModelDefaultsResolution.removeRegisteredModel(name: "acme/custom", provider: "claude", from: &workspace)
        #expect(reverted == 1)
        #expect(workspace.claude.modeDefaults["acceptEdits"] == "default")
        #expect(workspace.claude.registeredModels.isEmpty)
    }

    @Test func deleteReportsZeroWhenNoRowsReferenceDeletedName() {
        var app = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["manual": "claude-sonnet-5"],
                registeredModels: [.init(name: "acme/custom")]
            )
        )
        let reverted = ModelDefaultsResolution.removeRegisteredModel(name: "acme/custom", provider: "claude", from: &app)
        #expect(reverted == 0)
        #expect(app.claude.registeredModels.isEmpty)
        #expect(app.claude.modeDefaults["manual"] == "claude-sonnet-5")
    }

    @Test func deleteCombinedCountAcrossAppAndWorkspace() {
        var app = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["manual": "acme/custom"],
                registeredModels: [.init(name: "acme/custom")]
            )
        )
        var workspace = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["plan": "acme/custom", "fullAccess": "acme/custom"],
                registeredModels: [.init(name: "acme/custom")]
            )
        )
        let appReverted = ModelDefaultsResolution.removeRegisteredModel(name: "acme/custom", provider: "claude", from: &app)
        let wsReverted = ModelDefaultsResolution.removeRegisteredModel(name: "acme/custom", provider: "claude", from: &workspace)
        #expect(appReverted + wsReverted == 3)
        #expect(app.claude.modeDefaults["manual"] == "default")
        #expect(workspace.claude.modeDefaults["plan"] == "default")
        #expect(workspace.claude.modeDefaults["fullAccess"] == "default")
    }

    @Test func deleteCodexProviderRevertsCodexRows() {
        var app = ModelDefaultsConfig(
            claude: .init(modeDefaults: ["manual": "gpt-extra"], registeredModels: [.init(name: "gpt-extra")]),
            codex: .init(modeDefaults: ["manual": "gpt-extra"], registeredModels: [.init(name: "gpt-extra")])
        )
        let reverted = ModelDefaultsResolution.removeRegisteredModel(name: "gpt-extra", provider: "codex", from: &app)
        #expect(reverted == 1)
        #expect(app.codex.modeDefaults["manual"] == "default")
        #expect(app.codex.registeredModels.isEmpty)
        // Claude section is untouched
        #expect(app.claude.modeDefaults["manual"] == "gpt-extra")
        #expect(app.claude.registeredModels.count == 1)
    }

    @Test func deletePreservesOtherRegisteredModels() {
        var app = ModelDefaultsConfig(
            claude: .init(
                modeDefaults: ["manual": "acme/a"],
                registeredModels: [.init(name: "acme/a"), .init(name: "acme/b")]
            )
        )
        ModelDefaultsResolution.removeRegisteredModel(name: "acme/a", provider: "claude", from: &app)
        #expect(app.claude.registeredModels.count == 1)
        #expect(app.claude.registeredModels[0].name == "acme/b")
    }

    @Test func deleteNonExistentNameIsIdempotent() {
        var app = ModelDefaultsConfig(
            claude: .init(modeDefaults: ["manual": "claude-sonnet-5"], registeredModels: [])
        )
        let reverted = ModelDefaultsResolution.removeRegisteredModel(name: "never-existed", provider: "claude", from: &app)
        #expect(reverted == 0)
        #expect(app.claude.modeDefaults["manual"] == "claude-sonnet-5")
    }
}

// MARK: - Graph request node / phone block model label tests

struct NodeModelLabelTests {

    // MARK: - CLI-reported model takes priority

    @Test func cliReportedModelTakesPriority() {
        // CLI says it actually used "claude-sonnet-5"; that wins over any configured name.
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: "claude-sonnet-5", configuredModel: "claude-opus-5")
        #expect(label == "claude-sonnet-5")
    }

    @Test func cliReportedModelWinsOverRegisteredConfigured() {
        // Even a registered custom name yields to the CLI-reported actual model.
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: "claude-opus-5", configuredModel: "acme/custom-v2")
        #expect(label == "claude-opus-5")
    }

    @Test func cliReportedModelUsedWhenBothPresent() {
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: "gpt-6-astra", configuredModel: "gpt-5.6-sol")
        #expect(label == "gpt-6-astra")
    }

    // MARK: - Configured name + '설정' indicator when no CLI report

    @Test func configuredNameWithSettingsIndicatorWhenNoCLIReport() {
        // No CLI report; the configured name carries '설정' to show it is a setting, not confirmed.
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configuredModel: "claude-sonnet-5")
        #expect(label == "claude-sonnet-5 · 설정")
    }

    @Test func registeredModelWithSettingsIndicator() {
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configuredModel: "my-org/custom-model-v2")
        #expect(label == "my-org/custom-model-v2 · 설정")
    }

    @Test func codexConfiguredNameWithSettingsIndicator() {
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configuredModel: "gpt-6-astra")
        #expect(label == "gpt-6-astra · 설정")
    }

    // MARK: - No label when model is 'default' and CLI did not report

    @Test func nilWhenDefaultAndNoCLIReport() {
        // "default" means the CLI decides; without a CLI report we cannot show a label.
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configuredModel: "default")
        #expect(label == nil)
    }

    @Test func emptyCliReportFallsBackToConfigured() {
        // An empty string is treated as no report; the configured name with marker is shown.
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: "", configuredModel: "acme/custom")
        #expect(label == "acme/custom · 설정")
    }

    @Test func emptyCliReportWithDefaultConfiguredYieldsNil() {
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: "", configuredModel: "default")
        #expect(label == nil)
    }

    // MARK: - nodeModelLabel field on MightyGraphRun

    @Test func graphRunNodeModelLabelDefaultsToNil() {
        let run = MightyGraphRun(id: "r1", input: "Hello", provider: "claude")
        #expect(run.nodeModelLabel == nil)
    }

    @Test func graphRunNodeModelLabelIsSet() {
        let run = MightyGraphRun(id: "r1", input: "Hello", provider: "claude", nodeModelLabel: "claude-sonnet-5")
        #expect(run.nodeModelLabel == "claude-sonnet-5")
    }

    @Test func graphRunNodeModelLabelWithConfiguredSuffix() {
        let label = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configuredModel: "claude-opus-5")
        let run = MightyGraphRun(id: "r1", input: "Hello", provider: "claude", nodeModelLabel: label)
        #expect(run.nodeModelLabel == "claude-opus-5 · 설정")
    }

    @Test func graphRunCLIReportedModelUpdatesLabel() {
        // Start with a configured label; CLI then reports the actual model it used.
        var run = MightyGraphRun(id: "r1", input: "Hello", provider: "claude",
                                 nodeModelLabel: ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configuredModel: "claude-opus-5"))
        #expect(run.nodeModelLabel == "claude-opus-5 · 설정")
        // CLI report arrives: update the label to the actual model (no '설정' marker).
        run.nodeModelLabel = ModelDefaultsResolution.nodeModelLabel(cliReportedModel: "claude-sonnet-5", configuredModel: "claude-opus-5")
        #expect(run.nodeModelLabel == "claude-sonnet-5")
    }
}
