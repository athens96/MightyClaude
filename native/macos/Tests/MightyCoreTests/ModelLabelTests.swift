import Foundation
import Testing
@testable import MightyCore

/// The same file `mobile/src/__tests__/model-label.test.ts` reads: the Mac and
/// the phone must give every model id the same label.
struct ModelLabelTests {
    struct Case: Decodable { let model: String; let resolved: String?; let fallback: String?; let expected: String }

    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/model-labels.json")
    }()

    @Test func everyFixtureCaseLabelsAsCommitted() throws {
        let cases = try JSONDecoder().decode([Case].self, from: Data(contentsOf: Self.fixture))
        #expect(cases.count >= 40)
        for item in cases {
            #expect(ModelLabel.text(item.model, resolved: item.resolved, fallback: item.fallback) == item.expected, "\(item.model)")
        }
    }

    /// The catalogue the installed Claude Code reported on 2026-10-03.
    static let claudeRows: [[String: Any]] = [
        ["value": "default", "resolvedModel": "claude-opus-5-5", "displayName": "Default (recommended)", "description": "Opus 5.5 · Best for everyday, complex tasks"],
        ["value": "opus", "resolvedModel": "claude-opus-5-5", "displayName": "Opus"],
        ["value": "claude-fable-5-1[1m]", "resolvedModel": "claude-fable-5-1", "displayName": "Fable"],
        ["value": "sonnet", "resolvedModel": "claude-sonnet-5-5", "displayName": "Sonnet"],
        ["value": "haiku", "resolvedModel": "claude-haiku-4-5-20251001", "displayName": "Haiku"],
    ]

    @Test func cliCatalogueRowsCarryTheirVersion() {
        let catalog = ProviderService.normalizeClaudeCatalog(Self.claudeRows)
        #expect(catalog.source == "cli")
        #expect(catalog.models.map { ModelLabel.option($0) } == ["Claude 설정 따름 · Opus 5.5", "Opus 5.5", "Fable 5.1 (1M)", "Sonnet 5.5", "Haiku 4.5"])
        // The chip reads the same row the picker drew.
        for option in catalog.models { #expect(ModelLabel.text(option.value, catalog: catalog) == ModelLabel.option(option)) }
        // Only the label changes: the values sent to the CLI stay the aliases.
        #expect(catalog.models.map(\.value) == ["default", "opus", "claude-fable-5-1[1m]", "sonnet", "haiku"])
    }

    @Test func codexCatalogueLabelsItsIds() {
        let catalog = ProviderService.normalizeCodexCatalog([
            ["model": "gpt-6.1-sol", "displayName": "GPT-6.1-Sol", "isDefault": true],
            ["model": "gpt-5.2-codex", "displayName": "GPT-5.2-Codex"],
        ])
        #expect(catalog.models.map { ModelLabel.option($0) } == ["Codex 설정 따름 · GPT-6.1 Sol", "GPT-6.1 Sol", "GPT-5.2 Codex"])
    }

    @Test func fallbackCatalogueNeverInventsAVersion() {
        let fallback = ProviderOptions.fallbackCatalog("claude")
        #expect(ModelLabel.text("opus", catalog: fallback) == "Opus")
        #expect(ModelLabel.text("default", catalog: fallback) == "Claude 설정 따름")
        #expect(ModelLabel.text("opusplan", catalog: fallback) == "opusplan")
        // The pane's own reported model may give a family alias its version …
        #expect(ModelLabel.text("opus", catalog: fallback, hint: "claude-opus-5-5") == "Opus 5.5")
        // … but never another family's, and never `default`/`best` theirs.
        #expect(ModelLabel.text("sonnet", catalog: fallback, hint: "claude-opus-5-5") == "Sonnet")
        #expect(ModelLabel.text("default", catalog: fallback, hint: "claude-opus-5-5") == "Claude 설정 따름")
        #expect(ModelLabel.text("best", catalog: fallback, hint: "claude-opus-5-5") == "best")
        #expect(ModelLabel.text("gemini-3-pro-preview", catalog: ProviderOptions.fallbackCatalog("gemini")) == "Gemini 3 Pro")
        #expect(ModelLabel.text("gpt-6-astra", catalog: nil) == "GPT-6 Astra")
        // An id stored after a reconcile reads directly.
        #expect(ModelLabel.text("claude-sonnet-5-5", catalog: fallback) == "Sonnet 5.5")
    }

    @Test func graphCapsulesReadVersionsOnlyWhenAsked() {
        let catalog = ProviderService.normalizeClaudeCatalog(Self.claudeRows).models
        let records = [GraphResponseRecord(responseId: "r1", model: "claude-opus-5-5", usage: GraphTokenUsage(inputTokens: 1000, outputTokens: 50), activityIds: ["a1"])]
        let usage = GraphTokenUsage(inputTokens: 1500, outputTokens: 80)
        #expect(ModelUsageFormat.blockCapsule(usage: usage, records: records, nodeModelLabel: nil, catalog: catalog, versioned: true) == "1.6K · Opus 5.5")
        #expect(ModelUsageFormat.shortName("claude-sonnet-4-5", versioned: true) == "Sonnet 4.5")
        // The shared graph vectors keep the catalogue-only name.
        #expect(ModelUsageFormat.shortName("claude-sonnet-4-5") == "claude-sonnet-4-5")
        // A saved node label keeps its "configured" marker after the label.
        let marker = " " + L("graph.nodeModel.configuredSuffix")
        // Today's catalogue does not say which version an older run used.
        #expect(ModelUsageFormat.versionedNodeLabel("opus" + marker) == "Opus" + marker)
        #expect(ModelUsageFormat.blockCapsule(usage: usage, records: [], nodeModelLabel: "opus" + marker, catalog: catalog, versioned: true) == "Opus" + marker)
        #expect(ModelUsageFormat.versionedNodeLabel("claude-sonnet-4-5" + marker) == "Sonnet 4.5" + marker)
        #expect(ModelUsageFormat.versionedNodeLabel("claude-fable-5-1") == "Fable 5.1")
        #expect(ModelUsageFormat.versionedNodeLabel(nil) == nil)
    }

    @Test func thePhoneReceivesTheIdAnAliasStandsFor() {
        let catalog = ProviderService.normalizeClaudeCatalog(Self.claudeRows)
        #expect(ModelLabel.resolution("opus", catalog: catalog) == "claude-opus-5-5")
        #expect(ModelLabel.resolution("default", catalog: catalog) == "claude-opus-5-5")
        let fallback = ProviderOptions.fallbackCatalog("claude")
        #expect(ModelLabel.resolution("opus", catalog: fallback) == nil)
        #expect(ModelLabel.resolution("opus", catalog: fallback, hint: "claude-opus-5-5") == "claude-opus-5-5")
        #expect(ModelLabel.resolution("best", catalog: fallback, hint: "claude-opus-5-5") == nil)
        #expect(ModelLabel.resolution("default", catalog: fallback, hint: "claude-opus-5-5") == nil)
    }

    // MARK: A pane's model

    static func usage(model: String?, selected: String?) -> SessionUsage {
        var usage = SessionUsage(provider: "claude", source: "claude.stream-json", model: model)
        usage.selectedModel = selected
        return usage
    }

    @Test func aReportedModelLabelsOnlyTheSelectionItWasReportedFor() {
        let fallback = ProviderOptions.fallbackCatalog("claude")
        let session = RunSession(workspaceId: "w", title: "t", model: "opus", sessionUsage: Self.usage(model: "claude-opus-5-5", selected: "opus"))
        let rows = ModelLabel.pickerOptions(session, catalog: fallback)
        // Values stay the catalogue's; only the selected row borrows the reported version.
        #expect(rows.map(\.value) == fallback.models.map(\.value))
        #expect(rows.first { $0.value == "opus" }?.displayName == "Opus 5.5")
        #expect(rows.first { $0.value == "sonnet" }?.displayName == "Sonnet")
        #expect(rows.first { $0.value == "best" }?.displayName == "best")
        #expect(ModelLabel.selection(session, catalog: fallback) == "Opus 5.5")
        #expect(ModelLabel.resolution(session.model, catalog: fallback, hint: ModelLabel.reportedModel(session)) == "claude-opus-5-5")

        // Reported for another selection: stale, so no version for it.
        var switched = session; switched.model = "sonnet"
        #expect(ModelLabel.reportedModel(switched) == nil)
        #expect(ModelLabel.selection(switched, catalog: fallback) == "Sonnet")
        // The same family chosen again after a switch away does not revive an old report either.
        let stale = RunSession(workspaceId: "w", title: "t", model: "opus", sessionUsage: Self.usage(model: "claude-opus-4-5", selected: "default"))
        #expect(ModelLabel.selection(stale, catalog: fallback) == "Opus")
        // Usage saved before the selection was recorded gives no hint.
        let legacy = RunSession(workspaceId: "w", title: "t", model: "opus", sessionUsage: Self.usage(model: "claude-opus-5-5", selected: nil))
        #expect(ModelLabel.reportedModel(legacy) == nil)
        #expect(ModelLabel.selection(legacy, catalog: fallback) == "Opus")
    }

    @Test func recordingUsageRemembersTheSelection() {
        var session = RunSession(id: "s1", workspaceId: "w", title: "t", model: "opus")
        session.recordSessionUsage(RunEvent(sessionId: "s1", type: "usage", usage: SessionUsage(provider: "claude", source: "claude.stream-json", model: "claude-opus-5-5")))
        #expect(session.sessionUsage?.selectedModel == "opus")
        #expect(ModelLabel.reportedModel(session) == "claude-opus-5-5")
    }

    @Test func picksAndDisplaysDifferOnlyByPickerMarks() {
        let catalog = ProviderService.normalizeClaudeCatalog(Self.claudeRows)
        let saved = RunSession(workspaceId: "w", title: "t", model: "claude-opus-4-1")
        #expect(ModelLabel.pickerOptions(saved, catalog: catalog).last?.displayName == "Opus 4.1 · 저장된 모델")
        #expect(ModelLabel.selection(saved, catalog: catalog) == "Opus 4.1")
        let bare = RunSession(workspaceId: "w", title: "t", model: "default")
        #expect(ModelLabel.selection(bare, catalog: ModelCatalog(models: [])) == "CLI 기본값")
        #expect(ModelLabel.pickerOptions(bare, catalog: ModelCatalog(models: [])).map(\.displayName) == ["CLI 기본값"])
    }

    @Test func statusLineNamesTheCurrentSelection() {
        let catalog = ProviderService.normalizeClaudeCatalog(Self.claudeRows)
        let current = RunSession(workspaceId: "w", title: "t", model: "opus", sessionUsage: Self.usage(model: "claude-opus-5-5", selected: "opus"))
        #expect(ModelLabel.statusLine(current, catalog: catalog) == ("claude-opus-5-5", "Opus 5.5"))
        // Switched to Haiku after the run: the reported Opus gives way.
        var switched = current; switched.model = "haiku"
        #expect(ModelLabel.statusLine(switched, catalog: catalog) == ("claude-haiku-4-5-20251001", "Haiku 4.5"))
        // Older usage without a recorded selection keeps the reported model.
        let legacy = RunSession(workspaceId: "w", title: "t", model: "sonnet", sessionUsage: Self.usage(model: "claude-sonnet-4-5", selected: nil))
        #expect(ModelLabel.statusLine(legacy, catalog: catalog) == ("claude-sonnet-4-5", "Sonnet 4.5"))
        let fallbackDefault = RunSession(workspaceId: "w", title: "t", model: "default")
        #expect(ModelLabel.statusLine(fallbackDefault, catalog: ProviderOptions.fallbackCatalog("claude")) == ("default", "Claude 설정 따름"))
    }

    @Test func theSummaryCarriesTheResolvedModelOnlyWhenKnown() throws {
        let known = MobileSessionSummary(id: "s", workspaceId: "w", title: "t", kind: "claude", provider: "claude", model: "opus", status: "idle", revision: 1, updatedAt: "2026-10-03T00:00:00Z", resolvedModel: "claude-opus-5-5")
        let data = try JSONEncoder().encode(known)
        #expect(try JSONSerialization.jsonObject(with: data) as? [String: Any] != nil)
        #expect(String(decoding: data, as: UTF8.self).contains("\"resolvedModel\":\"claude-opus-5-5\""))
        #expect(try JSONDecoder().decode(MobileSessionSummary.self, from: data) == known)
        var unknown = known; unknown.resolvedModel = nil
        let bare = try JSONEncoder().encode(unknown)
        #expect(!String(decoding: bare, as: UTF8.self).contains("resolvedModel"))
        #expect(try JSONDecoder().decode(MobileSessionSummary.self, from: bare).resolvedModel == nil)
    }

    @Test func savedUsageKeepsItsSelectionAcrossNormalisation() throws {
        let usage = Self.usage(model: "claude-opus-5-5", selected: "opus")
        let decoded = try JSONDecoder().decode(SessionUsage.self, from: JSONEncoder().encode(usage))
        #expect(SessionUsageSupport.normalized(decoded)?.selectedModel == "opus")
        #expect(SessionUsageSupport.normalized(Self.usage(model: "x", selected: "bad model"))?.selectedModel == nil)
        // Older saves have no selection.
        let old = try JSONDecoder().decode(SessionUsage.self, from: Data(#"{"provider":"claude","source":"s","tokenScope":"run","updatedAt":"2026-10-03T00:00:00Z"}"#.utf8))
        #expect(old.selectedModel == nil)
    }
}
