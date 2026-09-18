import Foundation
import Testing
@testable import MightyCore

/// The m1 extension's pure rules: what a value may be, which page answers a
/// cursor, and how the app's own types reach the wire.
struct MobileRemoteExtensionTests {
    private func encoded<T: Encodable>(_ value: T) throws -> [String: Any] {
        let data = try JSONEncoder().encode(value)
        return try JSONSerialization.jsonObject(with: data) as? [String: Any] ?? [:]
    }

    @Test func renameTitleTrimsAndHoldsItsBounds() {
        #expect(MobileRemoteSupport.renameTitle("  릴리스 준비 ") == "릴리스 준비")
        #expect(MobileRemoteSupport.renameTitle("a") == "a")
        #expect(MobileRemoteSupport.renameTitle(String(repeating: "가", count: 80))?.count == 80)
        #expect(MobileRemoteSupport.renameTitle("") == nil)
        #expect(MobileRemoteSupport.renameTitle("   \n ") == nil)
        #expect(MobileRemoteSupport.renameTitle(String(repeating: "가", count: 81)) == nil)
        #expect(MobileRemoteSupport.renameTitle("한\u{0007}줄") == nil)
    }

    @Test func pagingWalksBackwardsAndStopsAtAnEvictedCursor() {
        let entries = (1...10).map { LogEntry(id: "e\($0)", kind: "assistant", text: "\($0)") }
        let middle = MobileRemoteSupport.page(entries: entries, before: "e6", limit: 3)
        #expect(middle.entries.map(\.id) == ["e3", "e4", "e5"] && middle.hasMore)
        let head = MobileRemoteSupport.page(entries: entries, before: "e3", limit: 5)
        #expect(head.entries.map(\.id) == ["e1", "e2"] && !head.hasMore)
        let oldest = MobileRemoteSupport.page(entries: entries, before: "e1", limit: 5)
        #expect(oldest.entries.isEmpty && !oldest.hasMore)
        // A cursor the host no longer holds answers empty, not the oldest page.
        let evicted = MobileRemoteSupport.page(entries: entries, before: "gone", limit: 5)
        #expect(evicted.entries.isEmpty && !evicted.hasMore)
        #expect(MobileRemoteSupport.page(entries: [], before: "e1", limit: 5).entries.isEmpty)
        #expect(MobileRemoteSupport.hasOlder(entryCount: 80) == false && MobileRemoteSupport.hasOlder(entryCount: 81))
    }

    @Test func settingsValidationRefusesEveryValueOutsideTheOptions() throws {
        let options = MobileSettingsOptions(
            models: [MobileOption(id: "default", label: "CLI 기본값"), MobileOption(id: "opus", label: "opus")],
            permissionModes: [MobileOption(id: "manual", label: "Always ask")],
            efforts: [MobileOption(id: "default", label: "Auto"), MobileOption(id: "high", label: "High")],
            mightyStyles: [MobileOption(id: "cli", label: "CLI")])
        #expect(throws: Never.self) { try MobileRemoteSupport.validate(MobileSettingsRequest(model: "opus", effort: "high"), options: options) }
        #expect(throws: MobileHostError.badRequest("바꿀 설정을 하나 이상 보내세요.")) {
            try MobileRemoteSupport.validate(MobileSettingsRequest(), options: options)
        }
        for request in [MobileSettingsRequest(model: "sonnet"), MobileSettingsRequest(permissionMode: "fullAccess"),
                        MobileSettingsRequest(effort: "max"), MobileSettingsRequest(mightyStyle: "ouroboros"),
                        MobileSettingsRequest(agentViewMode: "graph")] {
            var refused = false
            do { try MobileRemoteSupport.validate(request, options: options) } catch let failure as MobileHostError { refused = failure.status == 400 } catch { refused = false }
            #expect(refused)
        }
        #expect(throws: Never.self) { try MobileRemoteSupport.validate(MobileSettingsRequest(agentViewMode: "mighty"), options: options) }
        // No effort picker at all: any effort the phone sends is outside the options.
        var refusedEffort = false
        do { try MobileRemoteSupport.validate(MobileSettingsRequest(effort: "high"), options: MobileSettingsOptions(models: options.models)) }
        catch let failure as MobileHostError { refusedEffort = failure.status == 400 }
        #expect(refusedEffort)
    }

    @Test func submitOutcomesNameWhatHappenedAndDroppedIsNotAnAcceptance() {
        #expect(SubmitOutcome.started.accepted == "started")
        #expect(SubmitOutcome.steered.accepted == "steered")
        #expect(SubmitOutcome.queued.accepted == "queued")
        // Nothing was accepted, so there is no `accepted` to send: the route 409s.
        #expect(SubmitOutcome.dropped.accepted == nil)
        let named = SubmitOutcome.allCases.compactMap(\.accepted)
        #expect(Set(named) == Set(["started", "steered", "queued"]))
    }

    @Test func viewModeAndStyleNormaliseAndGuidedStylesNeedMightyView() {
        #expect(MobileRemoteSupport.viewMode("mighty") == "mighty")
        #expect(MobileRemoteSupport.viewMode("default") == "plain" && MobileRemoteSupport.viewMode(nil) == "plain")
        #expect(MobileRemoteSupport.viewMode("graph") == "plain")
        #expect(MobileRemoteSupport.style(nil) == "cli" && MobileRemoteSupport.style("ouroboros") == "ouroboros")
        // An unknown saved style is the plain CLI, never invented.
        #expect(MobileRemoteSupport.style("zzz") == "cli")

        let mighty = MobileRemoteSupport.guidedStylesAvailable(kind: "claude", provider: "claude", localWorkspace: true, viewMode: "mighty")
        let plain = MobileRemoteSupport.guidedStylesAvailable(kind: "claude", provider: "claude", localWorkspace: true, viewMode: "plain")
        let remote = MobileRemoteSupport.guidedStylesAvailable(kind: "claude", provider: "claude", localWorkspace: false, viewMode: "mighty")
        let codex = MobileRemoteSupport.guidedStylesAvailable(kind: "claude", provider: "codex", localWorkspace: true, viewMode: "mighty")
        #expect(mighty && !plain && !remote && !codex)
        #expect(MobileRemoteSupport.styleOptionIds(guided: false) == ["cli"])
        #expect(Set(MobileRemoteSupport.styleOptionIds(guided: true)) == Set(["cli"] + MightyStyles.all))
    }

    @Test func settingsAreEditableOnlyWhileNothingIsRunningOrPending() {
        #expect(MobileRemoteSupport.editable(status: "idle", pendingRun: false))
        #expect(MobileRemoteSupport.editable(status: "completed", pendingRun: false))
        #expect(!MobileRemoteSupport.editable(status: "running", pendingRun: false))
        // A run accepted but not yet started disables the pickers too.
        #expect(!MobileRemoteSupport.editable(status: "idle", pendingRun: true))
    }

    @Test func statusLineSegmentsCarryHexColoursAndWeight() {
        let parsed = StatusLineSupport.lines(from: "\u{1B}[1;31m경고\u{1B}[0m 보통\n\u{1B}[38;2;18;52;86m파랑\u{1B}[0m")
        let wire = MobileRemoteSupport.statusLine(parsed)
        #expect(wire?.lines.count == 2)
        let first = wire?.lines.first ?? []
        #expect(first.first?.text == "경고" && first.first?.bold == true && first.first?.fg == "#DB4D4D")
        #expect(first.last?.text == " 보통" && first.last?.bold == nil && first.last?.fg == nil)
        #expect(wire?.lines.last?.first?.fg == "#123456")
        #expect(MobileRemoteSupport.statusLine([]) == nil)
        #expect(MobileRemoteSupport.statusLine(StatusLineSupport.lines(from: "")) == nil)
        // Never more than six lines reach the phone.
        let many = StatusLineSupport.lines(from: (1...12).map(String.init).joined(separator: "\n"))
        #expect(MobileRemoteSupport.statusLine(many)?.lines.count == 6)
        // Default, black and white follow the phone's own theme.
        #expect(ANSIWireColor.hex(.standard(0)) == nil && ANSIWireColor.hex(.standard(7)) == nil)
        #expect(ANSIWireColor.hex(.palette(4)) == "#5C8CE6" && ANSIWireColor.hex(.rgb(0, 255, 300)) == "#00FFFF")
    }

    @Test func rateLimitsKeepOnlyTheMeasurableWindows() {
        let limits = [SessionRateLimit(kind: "five_hour", percentUsed: 42.5, resetsAt: "2026-09-18T00:00:00Z"),
                      SessionRateLimit(kind: "seven_day", percentUsed: nil),
                      SessionRateLimit(kind: "weekly", percentUsed: 180)]
        let wire = MobileRemoteSupport.rateLimits(limits)
        #expect(wire?.count == 2)
        #expect(wire?.first?.label == RateLimitWindowLabel.label("five_hour") && wire?.first?.usedPercent == 42.5)
        #expect(wire?.first?.resetsAt == "2026-09-18T00:00:00Z" && wire?.last?.usedPercent == 100)
        #expect(MobileRemoteSupport.rateLimits([]) == nil)
        #expect(MobileRemoteSupport.rateLimits([SessionRateLimit(kind: "weekly", percentUsed: nil)]) == nil)
    }

    @Test func slashCommandsBecomeWireActionsAndDropMacOnlyEntries() {
        let wire = MobileCommandSupport.wire(SlashCommandCatalog.builtins(provider: "claude"))
        let names = wire.map(\.name)
        #expect(!names.contains("plugin") && !names.contains("config"))
        #expect(wire.first { $0.name == "model" }?.action == "model")
        #expect(wire.first { $0.name == "model" }?.argumentHint == "모델 이름")
        #expect(wire.first { $0.name == "permissions" }?.action == "permission")
        #expect(wire.first { $0.name == "clear" }?.action == "clear")
        #expect(wire.first { $0.name == "usage" }?.action == "usage")
        #expect(wire.first { $0.name == "rename" }?.action == "rename")
        #expect(wire.first { $0.name == "help" }?.action == "help")
        #expect(wire.allSatisfy { $0.source == "app" })
        // The wire source is the structured origin, never the Korean badge text:
        // prose that changes must not silently reclassify a command.
        let scanned = [SlashCommand(invocation: "spec", description: "명세", source: "프로젝트 스킬", origin: .project),
                       SlashCommand(invocation: "note", description: "메모", source: "사용자 명령", origin: .user),
                       SlashCommand(invocation: "omc:plan", description: "계획", source: "플러그인 omc", origin: .plugin),
                       SlashCommand(invocation: "codex", description: "스킬", source: "Codex 스킬", origin: .user)]
        #expect(MobileCommandSupport.wire(scanned).map(\.source) == ["project", "user", "plugin", "user"])
        #expect(MobileCommandSupport.wire(scanned).allSatisfy { $0.action == nil && $0.argumentHint == nil })
        // Renamed prose keeps the origin the discovery site set.
        let renamed = SlashCommand(invocation: "spec", description: "", source: "무엇이든", origin: .project)
        #expect(MobileCommandSupport.wire([renamed]).first?.source == "project")
        #expect(MobileCommandSupport.wire(scanned).allSatisfy { SlashCommandOrigin(rawValue: $0.source) != nil })
    }

    @Test func newDetailFieldsAreAbsentFromTheJSONWhenTheHostHasNone() throws {
        let summary = MobileSessionSummary(id: "s1", workspaceId: "w1", title: "Claude", kind: "claude", provider: "claude", model: "default",
                                           status: "idle", revision: 1, updatedAt: "2026-09-17T00:00:00Z")
        let bare = try encoded(MobileSessionDetail(revision: 1, session: summary, entries: []))
        for key in ["hasOlder", "settings", "statusLine", "rateLimits", "usage", "elapsedSeconds"] { #expect(bare[key] == nil) }
        // A command pane has no agent view and no style, so neither key is sent.
        #expect((bare["session"] as? [String: Any])?["agentViewMode"] == nil)
        #expect((bare["session"] as? [String: Any])?["mightyStyle"] == nil)
        // No effort capability: neither the value nor the picker reaches the phone.
        let effortless = try encoded(MobileSettings(editable: true, model: "default", permissionMode: "manual", agentViewMode: "plain", mightyStyle: "cli",
                                                    options: MobileSettingsOptions(models: [MobileOption(id: "default", label: "CLI 기본값")])))
        #expect(effortless["effort"] == nil && (effortless["options"] as? [String: Any])?["efforts"] == nil)

        var filled = summary
        filled.agentViewMode = "mighty"; filled.mightyStyle = "ouroboros"
        let settings = MobileSettings(editable: false, model: "opus", permissionMode: "manual", effort: "default", agentViewMode: "mighty", mightyStyle: "ouroboros",
                                      options: MobileSettingsOptions(models: [MobileOption(id: "opus", label: "opus")]))
        let detail = try encoded(MobileSessionDetail(revision: 2, session: filled, entries: [], hasOlder: true, settings: settings,
                                                     statusLine: MobileStatusLine(lines: [[MobileStatusSegment(text: "ok")]]),
                                                     rateLimits: [MobileRateLimit(label: "세션", usedPercent: 10)]))
        #expect(detail["hasOlder"] as? Bool == true)
        #expect((detail["settings"] as? [String: Any])?["editable"] as? Bool == false)
        #expect((detail["session"] as? [String: Any])?["mightyStyle"] as? String == "ouroboros")
        // A segment with no colour and no weight sends neither key.
        let segment = ((detail["statusLine"] as? [String: Any])?["lines"] as? [[[String: Any]]])?.first?.first
        #expect(segment?["text"] as? String == "ok" && segment?["fg"] == nil && segment?["bold"] == nil)
        let command = try encoded(MobileCommandResult())
        #expect(command["ok"] as? Bool == true && command["message"] == nil && command["protocol"] as? Int == 1)
        let listed = try encoded(MobileCommandList(commands: [MobileCommand(name: "help", description: "도움말", source: "app", action: "help")]))
        #expect((listed["commands"] as? [[String: Any]])?.first?["argumentHint"] == nil)
    }

    @Test func usageTextReportsWhatWasMeasuredAndNothingElse() {
        let empty = MobileUsageText.text(usage: nil, model: "opus")
        #expect(empty.contains("아직 측정된"))
        let usage = MobileUsage(model: "claude-opus-5", contextUsedTokens: 12_000, contextWindowTokens: 200_000, contextPercent: 6, totalTokens: 41_000, costUSD: 0.1234)
        let text = MobileUsageText.text(usage: usage, model: "opus", elapsedSeconds: 12.4)
        #expect(text.contains("claude-opus-5") && text.contains("6.0%") && text.contains("$0.1234") && text.contains("12초"))
        // Nothing measured but the model name: no invented numbers.
        let modelOnly = MobileUsageText.text(usage: MobileUsage(model: "opus"), model: "opus")
        #expect(!modelOnly.contains("$") && !modelOnly.contains("컨텍스트"))
    }
}
