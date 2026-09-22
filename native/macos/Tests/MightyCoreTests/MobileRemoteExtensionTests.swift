import Foundation
import Testing
@testable import MightyCore

/// A clock the expiry and `lastSeen` rules can be driven with, so neither test
/// has to wait ten minutes or a minute for the behaviour it checks.
private final class TestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Date(timeIntervalSince1970: 1_800_000_000)
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: Double) { lock.lock(); value = value.addingTimeInterval(seconds); lock.unlock() }
}

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
        // `options.styles` is the open list: the CLI plus what may be run.
        let options = MobileRemoteSupport.styleOptions(BundledStyles.shared.styles())
        #expect(options.map(\.id) == ["cli", "ouroboros", "paperthin"])
        #expect(options.map(\.label) == ["cli", "Ouroboros", "Paperthin"])
        #expect(options[0].source == nil && options[1].source == .bundled)
        #expect(MobileRemoteSupport.styleOptions([]).map(\.id) == ["cli"])

        // Only a pane the Mac draws as a graph carries a Mighty payload; every
        // other pane leaves the field off the wire entirely.
        #expect(MobileRemoteSupport.sendsMighty(kind: "claude", agentViewMode: "mighty"))
        #expect(!MobileRemoteSupport.sendsMighty(kind: "claude", agentViewMode: "default"))
        #expect(!MobileRemoteSupport.sendsMighty(kind: "claude", agentViewMode: nil))
        #expect(!MobileRemoteSupport.sendsMighty(kind: "shell", agentViewMode: "mighty"))
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
        for key in ["hasOlder", "settings", "mighty", "statusLine", "rateLimits", "usage", "elapsedSeconds"] { #expect(bare[key] == nil) }
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

    // MARK: Mighty

    private func agent(_ id: String, kind: String?, status: String, title: String = "", input: String = "", answer: String? = nil) -> MightyGraphAgent {
        var entries: [LogEntry] = []
        if let answer { entries = [LogEntry(id: id + "-answer", kind: "assistant", text: answer, timestamp: "2026-09-17T00:00:00Z")] }
        return MightyGraphAgent(id: id, title: title, input: input, status: status, entries: entries, kind: kind)
    }

    @Test func everyBlockKindAndStatusLandsOnAContractValue() {
        let agents = [agent("a1", kind: nil, status: "running"),
                      agent("a2", kind: "task", status: "waiting"),
                      agent("a3", kind: "steer", status: "completed"),
                      agent("a4", kind: "compact", status: "error"),
                      agent("a5", kind: "question", status: "stopped"),
                      // A kind this build has never heard of is a sub-agent, not
                      // a new word invented on the wire.
                      agent("a6", kind: "wormhole", status: "idle")]
        let run = MightyGraphRun(id: "run-1", input: "정리해줘", status: "completed", agents: agents)
        let blocks = MobileMightySupport.blocks(run, ordinal: 3)
        #expect(blocks.map(\.kind) == ["main", "agent", "task", "steer", "compact", "question", "agent"])
        #expect(blocks.map(\.status) == ["completed", "running", "waiting", "completed", "error", "stopped", "running"])
        #expect(blocks.allSatisfy { MobileWire.blockKinds.contains($0.kind) && MobileWire.blockStatuses.contains($0.status) })
        #expect(blocks.first?.id == "run-1:main" && blocks.first?.title == "요청 3")
        // The titles are the Mac's own; a named block keeps its name.
        #expect(blocks.map(\.title).dropFirst() == ["하위 에이전트", "백그라운드 작업", "중간 요청", ContextCompaction.title, "질문", "하위 에이전트"])
        let named = MobileMightySupport.blocks(MightyGraphRun(id: "r", agents: [agent("a", kind: "task", status: "running", title: "테스트 실행")]), ordinal: 1)
        #expect(named.last?.title == "테스트 실행")
    }

    @Test func blockStatusesFollowTheMacsOwnBuckets() {
        for raw in ["failed"] { #expect(MobileMightySupport.status(raw) == "error") }
        for raw in ["cancelled", "interrupted"] { #expect(MobileMightySupport.status(raw) == "stopped") }
        // Still in motion, or a word from a newer engine: the phone sees running.
        for raw in ["idle", "starting", "queued", "running", "무엇이든"] { #expect(MobileMightySupport.status(raw) == "running") }
        #expect(MobileMightySupport.status("waiting") == "waiting" && MobileMightySupport.status("completed") == "completed")
        #expect(MobileWire.blockStatuses.allSatisfy { MobileMightySupport.status($0) == $0 })
    }

    @Test func blockOutputIsCutOnACharacterBoundary() {
        let long = String(repeating: "가", count: 3_000)
        let cut = MobileMightySupport.output(long)
        #expect(cut?.count == 2_000 && cut?.hasSuffix("가") == true)
        // Cut by characters, not bytes: a three-byte glyph is never halved.
        #expect(cut.map { String(decoding: Array($0.utf8), as: UTF8.self) } == cut)
        #expect(MobileMightySupport.output(nil) == nil && MobileMightySupport.output("   ") == nil)
        #expect(MobileMightySupport.output("답\u{0007}변") == "답변")
    }

    @Test func runsCarryTheNewestTwentyWithTheirGuidedTitles() {
        let runs = (1...25).map { MightyGraphRun(id: "run-\($0)", input: $0 == 25 ? "/ouroboros:seed" : "요청 \($0)", status: "completed") }
        let registry = StyleRegistry(styles: BundledStyles.shared.styles())
        let wire = MobileMightySupport.runs(runs) { registry.requestTitle(forInput: $0, workspace: nil) }
        #expect(wire.count == 20 && wire.first?.id == "run-6" && wire.last?.id == "run-25")
        // The ordinal keeps counting from the pane's own history, not from 1.
        #expect(wire.first?.blocks.first?.title == "요청 6" && wire.last?.blocks.first?.title == "요청 25")
        #expect(wire.last?.title == "시드")
        #expect(wire.first?.title == nil)
        // A plain CLI pane has no guided titles at all.
        #expect(MobileMightySupport.runs(runs).last?.title == nil)
        #expect(MobileMightySupport.runs([]).isEmpty)
    }

    @Test func theMightyDigestMovesOnStructureAndIgnoresStreamedText() {
        var run = MightyGraphRun(id: "run-1", input: "안녕", status: "running", rootEntries: [LogEntry(id: "e1", kind: "assistant", text: "부")])
        let first = MobileMightySupport.digest([run])
        run.rootEntries[0].text = "부분 응답이 계속 자라난다"
        run.finalOutput = "중간 결과"
        #expect(MobileMightySupport.digest([run]) == first)
        run.agents = [agent("a1", kind: nil, status: "running")]
        let withAgent = MobileMightySupport.digest([run])
        #expect(withAgent != first)
        run.agents[0].status = "completed"
        #expect(MobileMightySupport.digest([run]) != withAgent)
        run.status = "completed"
        #expect(MobileMightySupport.digest([run]) != withAgent)
    }

    @Test func aLegacyGraphPaneStillMovesItsDigestWhenABlockStatusChanges() {
        // No saved graph: the payload groups the transcript, so the digest has
        // to read that same grouping or the phone's blocks change in silence.
        var session = RunSession(workspaceId: "w1", title: "Claude", status: "running",
                                 logs: [LogEntry(id: "u1", kind: "user", text: "정리해줘"),
                                        LogEntry(id: "a1", kind: "assistant", text: "부분"),
                                        LogEntry(id: "u2", kind: "user", text: "계속")])
        #expect(session.graphRuns == nil)
        let running = MobileMightySupport.digest(session: session)
        #expect(running == MobileMightySupport.digest(MightyGraphSupport.legacyRuns(session)))
        // The block the phone is watching finished: the digest must move.
        session.status = "completed"
        let finished = MobileMightySupport.digest(session: session)
        #expect(finished != running && finished == MobileMightySupport.digest(MightyGraphSupport.legacyRuns(session)))
        // A new request is a new block, and streamed text alone is not.
        session.logs.append(LogEntry(id: "a2", kind: "assistant", text: "자라나는 답"))
        #expect(MobileMightySupport.digest(session: session) == finished)
        session.logs.append(LogEntry(id: "u3", kind: "user", text: "또"))
        #expect(MobileMightySupport.digest(session: session) != finished)
        // A transcript that opens with replies groups them under one run, and
        // an empty pane has nothing to hash at all.
        let orphan = RunSession(workspaceId: "w1", title: "Claude", status: "idle", logs: [LogEntry(id: "a0", kind: "assistant", text: "이전 답")])
        #expect(MobileMightySupport.digest(session: orphan) == MobileMightySupport.digest(MightyGraphSupport.legacyRuns(orphan)))
        let empty = RunSession(workspaceId: "w1", title: "Claude")
        #expect(MobileMightySupport.digest(session: empty) == MobileMightySupport.digest([]))
        // A saved graph wins: the transcript is not consulted at all.
        var saved = session
        saved.graphRuns = [MightyGraphRun(id: "run-1", input: "정리해줘", status: "running")]
        #expect(MobileMightySupport.digest(session: saved) == MobileMightySupport.digest(saved.graphRuns ?? []))
    }

    @Test func onlyTheNewestRunsOfALongLegacyTranscriptAreHashed() {
        // The payload sends the newest twenty runs, so the digest walks back
        // exactly that far rather than the whole saved transcript.
        var logs: [LogEntry] = []
        for index in 1...40 { logs.append(LogEntry(id: "u\(index)", kind: "user", text: "요청 \(index)")) }
        var session = RunSession(workspaceId: "w1", title: "Claude", status: "completed", logs: logs)
        let identities = MobileMightySupport.legacyRunIdentities(session)
        #expect(identities.count == MobileWire.mightyRuns && identities.first?.id == "u21" && identities.last?.id == "u40")
        let before = MobileMightySupport.digest(session: session)
        #expect(before == MobileMightySupport.digest(MightyGraphSupport.legacyRuns(session)))
        // Rewriting a request that fell out of the window changes nothing.
        session.logs[0] = LogEntry(id: "u1-renamed", kind: "user", text: "다른 요청")
        #expect(MobileMightySupport.digest(session: session) == before)
    }

    @Test func theGuidedPanelsCarryWhatTheMacsOwnButtonsOffer() throws {
        // Built from the one projection both surfaces read, then folded back
        // into the shapes an older phone knows (§7.4).
        let flow = StyleFixtures.bundled("ouroboros")
        let seeded = StylePanelProjection.make(style: flow, prompts: ["/ouroboros:seed"], selectedGroupId: nil,
                                               capabilityStates: [:], attachments: [],
                                               prerequisites: StylePrerequisiteResult(ready: false))
        let ouroboros = try #require(MobileLegacyStyleAdapter.payloads(style: flow, panel: seeded, casebook: nil).ouroboros)
        #expect(ouroboros.phase == "seed" && !ouroboros.ready)
        #expect(ouroboros.next.map(\.skill) == ["run", "evaluate", "status"])
        // The literal catalogue the deleted `OuroborosFlowTests` pinned.
        #expect(ouroboros.all.map(\.skill) == ["interview", "auto", "seed", "run", "evaluate", "evolve", "ralph", "status", "unstuck", "cancel"])
        #expect(ouroboros.takesText == ["interview", "auto", "unstuck"])
        #expect(ouroboros.all.first { $0.skill == "seed" }?.title == "시드 생성")
        // The literal phase walk, as the old payload reported it.
        var phases: [String] = []
        for phase in flow.manifest.orderedPhases {
            let panel = StylePanelProjection.make(style: flow, prompts: ["/ouroboros:" + phase.id], selectedGroupId: nil,
                                                  capabilityStates: [:], attachments: [],
                                                  prerequisites: StylePrerequisiteResult(ready: true))
            phases.append(MobileLegacyStyleAdapter.payloads(style: flow, panel: panel, casebook: nil).ouroboros?.phase ?? "")
        }
        // `goal` has no action of its own, so a pane there reports the entry.
        #expect(flow.manifest.orderedPhases.map(\.id) == ["goal", "interview", "seed", "run", "evaluate", "evolve"])
        #expect(phases == ["goal", "interview", "seed", "run", "evaluate", "evolve"])

        let thin = StyleFixtures.bundled("paperthin")
        let casebook = StyleCasebook(name: "v3-relay", path: "/tmp/x", files: ["DESIGN.local.md", "RETRO.local.md"], modifiedAt: Date())
        // The phone has no group of its own, and production never passes one:
        // the recommendation has to survive that, or it disappears exactly
        // where the old payload said `re0-plan` (§7.4).
        let map = StylePanelProjection.make(style: thin, prompts: [], selectedGroupId: nil,
                                            capabilityStates: [StyleCapabilityID.casebook: "complete"], attachments: [],
                                            prerequisites: StylePrerequisiteResult(ready: true))
        let paperthin = try #require(MobileLegacyStyleAdapter.payloads(style: thin, panel: map, casebook: casebook).paperthin)
        #expect(paperthin.installed && paperthin.domains.map(\.id) == ["depth", "breadth", "coil", "mesh"])
        #expect(paperthin.casebook?.weight == "full" && paperthin.casebook?.files.count == 2)
        #expect(paperthin.recommended == "re0-work")
        let depth = try #require(paperthin.domains.first { $0.id == "depth" })
        #expect(depth.axis == "하나 \u{00B7} 지금" && depth.question == "이 하나가 깨끗하고 참인가?")
        // The literal first four of the domain the deleted tests pinned.
        #expect(depth.skills.prefix(4).map(\.name) == ["re0", "readchk", "aim", "modelchk"])
        #expect(depth.skills.count == 19 && depth.skills.first { $0.name == "hate" }?.userInvoked == true)
        #expect(paperthin.domains.map { $0.skills.count } == [19, 2, 6, 1])
        // Nothing on disk yet: the first step of a cycle is what is recommended,
        // and that is the value an older phone read without any group at all.
        let absent = StylePanelProjection.make(style: thin, prompts: [], selectedGroupId: nil,
                                               capabilityStates: [StyleCapabilityID.casebook: "absent"], attachments: [],
                                               prerequisites: StylePrerequisiteResult(ready: false))
        let none = try #require(MobileLegacyStyleAdapter.payloads(style: thin, panel: absent, casebook: nil).paperthin)
        #expect(!none.installed && none.recommended == "re0-plan" && none.casebook == nil)
        // A workspace whose feature has not been read yet has no answer at all.
        let unread = StylePanelProjection.make(style: thin, prompts: [], selectedGroupId: nil, capabilityStates: [:],
                                               attachments: [], prerequisites: StylePrerequisiteResult(ready: true))
        #expect(MobileLegacyStyleAdapter.payloads(style: thin, panel: unread, casebook: nil).paperthin?.recommended == nil)
    }

    /// The `/guided` gate, decided from the registry in memory alone: neither
    /// an unregistered nor an unapproved style reads the disk, so the two
    /// cannot be told apart by timing either (§4.5).
    @Test func theGuidedRouteAnswersUnknownAndUnapprovedAlike() throws {
        let flow = StyleFixtures.bundled("ouroboros")
        let data = StyleFixtures.data()
        let file = StyleFixtures.discovered(data, source: .user, url: URL(fileURLWithPath: "/data/styles/flow.json"))
        let pending = StyleRegistry(styles: StyleRegistry.make(files: [file], approvals: []).styles)
        func decide(_ registry: StyleRegistry, pane: RegisteredStyle?, styleId: String, actionId: String, text: String = "") -> MobileRemoteSupport.GuidedDecision {
            MobileRemoteSupport.guidedDecision(registry: registry, workspace: nil, pane: pane, styleId: styleId, actionId: actionId, text: text)
        }
        #expect(decide(pending, pane: nil, styleId: "flow", actionId: "go") == .unknownStyle)
        #expect(decide(pending, pane: nil, styleId: "nothing-here", actionId: "go") == .unknownStyle)
        let approvals = [StyleApprovalRecord(styleId: "flow", source: .user, path: file.url.path, hash: file.hash, state: "approved", decidedAt: Date())]
        let approved = StyleRegistry(styles: StyleRegistry.make(files: [file], approvals: approvals).styles)
        let style = try #require(approved.resolve("flow"))
        #expect(decide(approved, pane: nil, styleId: "flow", actionId: "go") == .otherPane(styleId: "flow"))
        #expect(decide(approved, pane: flow, styleId: "flow", actionId: "go") == .otherPane(styleId: "flow"))
        #expect(decide(approved, pane: style, styleId: "flow", actionId: "nope") == .unknownAction)
        #expect(decide(approved, pane: style, styleId: "flow", actionId: "go") == .send(prompt: "/go"))
        // A bundled style needs no record at all, and its text still folds.
        let bundles = StyleRegistry(styles: BundledStyles.shared.styles())
        #expect(decide(bundles, pane: flow, styleId: "ouroboros", actionId: "interview", text: "결제 흐름\n둘째")
                    == .send(prompt: "/ouroboros:interview 결제 흐름 둘째"))
    }

    /// The pane's phase comes from its request history, and that history is a
    /// 128-run window: if the only phase-carrying request falls out of it the
    /// flow reads as the entry phase again, never as a stale one (§1.6).
    @Test func theRunWindowIsWhatThePhaseIsReadFrom() throws {
        let flow = StyleFixtures.bundled("ouroboros")
        var session = RunSession(workspaceId: "ws", title: "Claude")
        session.beginGraphRun(input: "/ouroboros:seed", id: "r0")
        #expect(flow.evaluator.currentPhase(session: session)?.id == "seed")
        for index in 1...128 { session.beginGraphRun(input: "그냥 요청 \(index)", id: "r\(index)") }
        #expect(session.graphRuns?.count == 128 && session.graphRuns?.contains { $0.id == "r0" } == false)
        #expect(flow.evaluator.currentPhase(session: session)?.id == "goal")
        // The logs are not consulted while the window still holds requests, so
        // a trimmed run does not resurrect its phase through them either.
        session.logs = [LogEntry(kind: "user", text: "/ouroboros:seed")]
        #expect(flow.evaluator.currentPhase(session: session)?.id == "goal")
        // With no runs at all the logs are the fallback, as they always were.
        var logsOnly = RunSession(workspaceId: "ws", title: "Claude", logs: [LogEntry(kind: "user", text: "/ouroboros:seed")])
        logsOnly.graphRuns = []
        #expect(flow.evaluator.currentPhase(session: logsOnly)?.id == "seed")
    }

    @Test func guidedPromptsAreTheVeryStringsTheMacButtonsSend() throws {
        let flow = StyleFixtures.bundled("ouroboros")
        let thin = StyleFixtures.bundled("paperthin")
        #expect(MobileMightySupport.guidedPrompt(flow, actionId: "interview", text: " 결제 흐름 ") == flow.evaluator.prompt(actionId: "interview", text: "결제 흐름"))
        // An action that takes no text drops it, exactly as the Mac's chip does.
        #expect(MobileMightySupport.guidedPrompt(flow, actionId: "seed", text: "무시됨") == flow.evaluator.prompt(actionId: "seed", text: ""))
        #expect(MobileMightySupport.guidedPrompt(thin, actionId: "re0", text: "docs/spec.md") == thin.evaluator.prompt(actionId: "re0", text: "docs/spec.md"))
        #expect(MobileMightySupport.guidedPrompt(thin, actionId: "re0", text: "") == "/re0")
        // Wrong catalogue, unknown action: nothing is invented.
        #expect(MobileMightySupport.guidedPrompt(thin, actionId: "interview", text: "") == nil)
        #expect(MobileMightySupport.guidedPrompt(flow, actionId: "re0", text: "") == nil)
    }

    @Test func aPhonesMultilineTextBecomesOneLineForBothGuidedStyles() throws {
        // An action reads its argument up to the first line break, so a pasted
        // paragraph must not reach one catalogue whole and the other cut off.
        let flow = StyleFixtures.bundled("ouroboros")
        let thin = StyleFixtures.bundled("paperthin")
        let pasted = "결제 흐름 정리\n\n  두 번째 줄  \n세 번째 줄"
        #expect(MobileMightySupport.guidedPrompt(flow, actionId: "interview", text: pasted) == "/ouroboros:interview 결제 흐름 정리 두 번째 줄 세 번째 줄")
        #expect(MobileMightySupport.guidedPrompt(thin, actionId: "re0", text: pasted) == "/re0 결제 흐름 정리 두 번째 줄 세 번째 줄")
        #expect(MobileMightySupport.guidedPrompt(flow, actionId: "interview", text: "\n \n") == "/ouroboros:interview")
        // The Mac's own buttons are untouched: only the phone's route folds.
        #expect(flow.evaluator.prompt(actionId: "interview", text: "첫 줄\n둘째 줄") == "/ouroboros:interview 첫 줄\n둘째 줄")
        #expect(thin.evaluator.prompt(actionId: "re0", text: "첫 줄\n둘째 줄") == "/re0 첫 줄 둘째 줄")
        #expect(flow.evaluator.prompt(actionId: "interview", text: " 결제 흐름 ") == "/ouroboros:interview 결제 흐름")
    }

    @Test func theMightyPayloadKeepsItsOptionalFieldsOffTheWire() throws {
        let plain = try encoded(MobileMighty(style: "cli", runs: []))
        #expect(plain["style"] as? String == "cli" && plain["ouroboros"] == nil && plain["paperthin"] == nil)
        let block = try encoded(MobileBlock(id: "b", kind: "main", title: "요청 1", status: "running"))
        for key in ["summary", "output", "durationMs"] { #expect(block[key] == nil) }
        let run = try encoded(MobileMightyRun(id: "r", input: "안녕", status: "running", blocks: []))
        #expect(run["title"] == nil)
    }

    // MARK: Uploads

    @Test func uploadNamesLoseEveryPathAndLeadingDot() {
        #expect(MobileUploadStore.sanitize("../../etc/passwd") == "passwd")
        #expect(MobileUploadStore.sanitize("C:\\temp\\shot.png") == "shot.png")
        #expect(MobileUploadStore.sanitize(".hidden") == "hidden")
        #expect(MobileUploadStore.sanitize("....") == nil)
        #expect(MobileUploadStore.sanitize("/") == nil)
        #expect(MobileUploadStore.sanitize("   ") == nil)
        #expect(MobileUploadStore.sanitize("보고\u{0007}서.pdf") == "보고서.pdf")
        #expect(MobileUploadStore.sanitize(String(repeating: "가", count: 200))?.count == 120)
        #expect(MobileUploadStore.sanitize("report.pdf") == "report.pdf")
    }

    @Test func invisibleScalarsCannotDisguiseAName() {
        // A right-to-left override makes "invoice\u{202E}gnp.exe" read as
        // "invoicexe.png" on screen while still ending in .exe.
        #expect(MobileUploadStore.sanitize("invoice\u{202E}gnp.exe") == "invoicegnp.exe")
        #expect(MobileDeviceRegistry.deviceName("invoice\u{202E}gnp.exe") == "invoicegnp.exe")
        for scalar in ["\u{200B}", "\u{200E}", "\u{202A}", "\u{2066}", "\u{2069}", "\u{FEFF}"] {
            #expect(MobileUploadStore.sanitize("a\(scalar)b.txt") == "ab.txt")
            #expect(MobileDeviceRegistry.deviceName("A\(scalar)B") == "AB")
        }
        // A name that is nothing but invisible scalars leaves nothing behind.
        #expect(MobileUploadStore.sanitize("\u{202E}\u{200B}") == nil)
        #expect(MobileDeviceRegistry.deviceName("\u{202E}\u{200B}") == "휴대폰")
        // Ordinary text is untouched.
        #expect(MobileUploadStore.sanitize("보고서.pdf") == "보고서.pdf")
    }

    private func store(_ clock: TestClock) -> (MobileUploadStore, URL) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-uploads-" + UUID().uuidString, isDirectory: true)
        return (MobileUploadStore(directory: directory, now: { clock.now() }), directory)
    }
    /// The device most of these tests upload as; the device rules have their own.
    private static let phone = "cGhvbmUtb25lLTAwMDAwMDA"
    private static let other = "cGhvbmUtdHdvLTAwMDAwMDA"

    @Test func uploadsAcceptChunksInOrderAndBecomeComposerAttachments() async throws {
        let clock = TestClock()
        let (uploads, directory) = store(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let bytes = Data(String(repeating: "문서 ", count: 4).utf8)
        let ticket = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "note.txt", size: bytes.count, mimeType: "text/plain")
        #expect(ticket.chunkSize == 196_608)
        // The bytes land under the upload's own id, never under the phone's name.
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(files == [ticket.uploadId + ".part"])
        #expect((try FileManager.default.attributesOfItem(atPath: directory.appendingPathComponent(files[0]).path)[.posixPermissions] as? Int) == 0o600)
        #expect((try FileManager.default.attributesOfItem(atPath: directory.path)[.posixPermissions] as? Int) == 0o700)
        let received = try await uploads.append(id: ticket.uploadId, deviceId: Self.phone, index: 0, data: bytes)
        #expect(received == bytes.count)
        let attachment = try await uploads.complete(id: ticket.uploadId, deviceId: Self.phone)
        #expect(attachment.name == "note.txt" && attachment.size == bytes.count)
        let consumed = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        #expect(consumed.attachments.count == 1 && consumed.attachments[0].name == "note.txt" && consumed.attachments[0].mediaType == "text/plain")
        #expect(Data(base64Encoded: consumed.attachments[0].dataBase64) == bytes)
        // Claimed, not spent: a refused submit hands the file back to retry.
        await uploads.release(consumed.claim)
        let waiting = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        #expect(waiting.attachments.map(\.name) == consumed.attachments.map(\.name))
        #expect(waiting.attachments.map(\.dataBase64) == consumed.attachments.map(\.dataBase64))
        await uploads.spend(waiting.claim)
        // One use: the file is gone and the id no longer resolves.
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty)
        await #expect(throws: MobileHostError.badRequest("끝나지 않았거나 이 실행 창의 것이 아닌 업로드입니다.")) {
            _ = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        }
    }

    @Test func oneUploadCanBeClaimedByOnlyOneSubmitAtATime() async throws {
        let clock = TestClock()
        let (uploads, directory) = store(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let bytes = Data("문서".utf8)
        let ticket = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "note.txt", size: bytes.count, mimeType: nil)
        _ = try await uploads.append(id: ticket.uploadId, deviceId: Self.phone, index: 0, data: bytes)
        _ = try await uploads.complete(id: ticket.uploadId, deviceId: Self.phone)
        // Two submits naming the same upload: the second finds it taken, so a
        // pane can never be handed the same file twice.
        let first = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        await #expect(throws: MobileHostError.badRequest("이미 전송 중인 업로드입니다.")) {
            _ = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        }
        // The first submit failed: the claim goes back and the id works again.
        await uploads.release(first.claim)
        let second = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        #expect(second.attachments.count == 1)
        // A released claim is spent by nobody: only the live one may spend.
        await uploads.spend(first.claim)
        let stillThere = await uploads.count()
        #expect(stillThere == 1)
        await uploads.spend(second.claim)
        let afterSpend = await uploads.count()
        let onDisk = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(afterSpend == 0 && onDisk.isEmpty)
    }

    @Test func anotherPhonesUploadIsNotFoundAtAll() async throws {
        let clock = TestClock()
        let (uploads, directory) = store(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let ticket = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "note.txt", size: 4, mimeType: nil)
        // 404 rather than 403: whether the id exists is not the other phone's
        // business either.
        await #expect(throws: MobileHostError.notFound("업로드를 찾을 수 없습니다.")) {
            _ = try await uploads.append(id: ticket.uploadId, deviceId: Self.other, index: 0, data: Data(repeating: 65, count: 4))
        }
        await #expect(throws: MobileHostError.notFound("업로드를 찾을 수 없습니다.")) { _ = try await uploads.complete(id: ticket.uploadId, deviceId: Self.other) }
        await #expect(throws: MobileHostError.notFound("업로드를 찾을 수 없습니다.")) { try await uploads.cancel(id: ticket.uploadId, deviceId: Self.other) }
        // The owner still has it, all of it.
        _ = try await uploads.append(id: ticket.uploadId, deviceId: Self.phone, index: 0, data: Data(repeating: 65, count: 4))
        _ = try await uploads.complete(id: ticket.uploadId, deviceId: Self.phone)
        await #expect(throws: MobileHostError.badRequest("끝나지 않았거나 이 실행 창의 것이 아닌 업로드입니다.")) {
            _ = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.other)
        }
        let owned = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s1", deviceId: Self.phone)
        #expect(owned.attachments.count == 1)
        // Unpairing a phone takes everything it was holding with it.
        await uploads.discard(device: Self.phone)
        let left = await uploads.count()
        let onDisk = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(left == 0 && onDisk.isEmpty)
    }

    @Test func aSymlinkedUploadsFolderIsRefusedRatherThanFollowed() async throws {
        let clock = TestClock()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-uploads-link-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let elsewhere = root.appendingPathComponent("elsewhere", isDirectory: true)
        try FileManager.default.createDirectory(at: elsewhere, withIntermediateDirectories: true)
        let bystander = elsewhere.appendingPathComponent("keep.txt")
        try Data("소중한 파일".utf8).write(to: bystander)
        let directory = root.appendingPathComponent("uploads", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: directory, withDestinationURL: elsewhere)
        let uploads = MobileUploadStore(directory: directory, now: { clock.now() })
        // Following the link would put the phone's bytes — and the folder
        // sweep — somewhere whoever placed it chose.
        await #expect(throws: MightyError.self) {
            _ = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "note.txt", size: 4, mimeType: nil)
        }
        #expect(FileManager.default.fileExists(atPath: bystander.path))
    }

    @Test func uploadsRefuseSizeMismatchesForeignPanesAndTooManyOpenSlots() async throws {
        let clock = TestClock()
        let (uploads, directory) = store(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let ticket = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "a.txt", size: 10, mimeType: nil)
        let received = try await uploads.append(id: ticket.uploadId, deviceId: Self.phone, index: 0, data: Data(repeating: 65, count: 10))
        #expect(received == 10)
        // Every chunk of the declared size has arrived: there is no index 1.
        await #expect(throws: MobileHostError.self) { _ = try await uploads.append(id: ticket.uploadId, deviceId: Self.phone, index: 1, data: Data([65])) }
        _ = try await uploads.complete(id: ticket.uploadId, deviceId: Self.phone)
        // Another pane's upload is not this pane's to attach.
        await #expect(throws: MobileHostError.self) { _ = try await uploads.attachments(ids: [ticket.uploadId], sessionId: "s2", deviceId: Self.phone) }
        // A chunk is exactly a chunk, or exactly what is left of the file.
        let short = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "b.txt", size: 10, mimeType: nil)
        await #expect(throws: MobileHostError.badRequest("chunk 크기가 선언과 다릅니다.")) {
            _ = try await uploads.append(id: short.uploadId, deviceId: Self.phone, index: 0, data: Data(repeating: 66, count: 5))
        }
        // Nothing arrived, so the received size cannot match the declared one.
        await #expect(throws: MobileHostError.badRequest("받은 크기가 선언한 크기와 다릅니다.")) { _ = try await uploads.complete(id: short.uploadId, deviceId: Self.phone) }
        // An unfinished upload can never be attached.
        await #expect(throws: MobileHostError.self) { _ = try await uploads.attachments(ids: [short.uploadId], sessionId: "s1", deviceId: Self.phone) }
        try await uploads.cancel(id: short.uploadId, deviceId: Self.phone)
        await uploads.shutdown()
        #expect(!FileManager.default.fileExists(atPath: directory.path))
    }

    @Test func openUploadsAreCappedPerPaneAndOverallAndFreedWithThePane() async throws {
        let clock = TestClock()
        let (uploads, directory) = store(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        for index in 0..<MobileUploadStore.maximumOpenPerSession {
            _ = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "f\(index).txt", size: 4, mimeType: nil)
        }
        let mine = await uploads.open(sessionId: "s1")
        #expect(mine == MobileUploadStore.maximumOpenPerSession)
        // Too many at once, not too big: 429, and the pane next door is fine.
        await #expect(throws: MobileHostError.tooMany("이 실행 창에서 동시에 올릴 수 있는 파일은 16개입니다.")) {
            _ = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "over.txt", size: 4, mimeType: nil)
        }
        let neighbour = try await uploads.begin(sessionId: "s2", deviceId: Self.phone, name: "ok.txt", size: 4, mimeType: nil)
        #expect(!neighbour.uploadId.isEmpty)
        // Fill the host's own ceiling from further panes.
        let panes = MobileUploadStore.maximumOpen / MobileUploadStore.maximumOpenPerSession
        for pane in 2...panes {
            let session = "s\(pane)"
            let already = await uploads.open(sessionId: session)
            for index in already..<MobileUploadStore.maximumOpenPerSession {
                _ = try await uploads.begin(sessionId: session, deviceId: Self.phone, name: "f\(index).txt", size: 4, mimeType: nil)
            }
        }
        let total = await uploads.count()
        #expect(total == MobileUploadStore.maximumOpen)
        await #expect(throws: MobileHostError.tooMany("동시에 올릴 수 있는 파일 수를 넘었습니다.")) {
            _ = try await uploads.begin(sessionId: "fresh", deviceId: Self.phone, name: "over.txt", size: 4, mimeType: nil)
        }
        // A closed pane can never take a submit, so its slots and bytes go now.
        await uploads.discard(sessionId: "s1")
        let freed = await uploads.open(sessionId: "s1")
        let afterClose = await uploads.count()
        #expect(freed == 0 && afterClose == MobileUploadStore.maximumOpen - MobileUploadStore.maximumOpenPerSession)
        _ = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "room.txt", size: 4, mimeType: nil)
        await uploads.shutdown()
    }

    @Test func unfinishedUploadsExpireAndFreeTheirSlotAndBytes() async throws {
        let clock = TestClock()
        let (uploads, directory) = store(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let ticket = try await uploads.begin(sessionId: "s1", deviceId: Self.phone, name: "a.txt", size: 8, mimeType: nil)
        clock.advance(599)
        // Just inside the window: the chunk lands and pushes the deadline out.
        let received = try await uploads.append(id: ticket.uploadId, deviceId: Self.phone, index: 0, data: Data(repeating: 65, count: 8))
        #expect(received == 8)
        clock.advance(601)
        // Ten minutes without a chunk: the upload and its bytes are gone.
        await #expect(throws: MobileHostError.notFound("업로드를 찾을 수 없습니다.")) { _ = try await uploads.complete(id: ticket.uploadId, deviceId: Self.phone) }
        let remaining = await uploads.count()
        #expect(remaining == 0)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty)
    }

    // MARK: Devices

    private func registry(_ clock: TestClock) -> (MobileDeviceRegistry, URL) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-devices-" + UUID().uuidString, isDirectory: true)
        return (MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json"), now: { clock.now() }), directory)
    }
    /// The token of a successful first pairing, or nil for every refusal.
    private func issuedToken(_ result: MobileTokenIssue) -> String? {
        guard case .issued(let token) = result else { return nil }
        return token
    }
    private func deviceId(_ index: Int) -> String { String(format: "device%013d", index) }

    @Test func aDeviceTokenIsIssuedOnceAndOnlyItsHashIsKept() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let token = try #require(issuedToken(devices.issueToken(clientId: clientId, name: "Young의 iPhone")))
        #expect(MobileDeviceRegistry.validToken(token) && devices.all().count == 1)
        #expect(devices.all().first?.name == "Young의 iPhone" && devices.all().first?.legacy == false)
        #expect(devices.authenticate(clientId: clientId, token: token))
        // A different token of the same length, and the right token on another
        // device, are both refused.
        #expect(!devices.authenticate(clientId: clientId, token: String(token.dropLast()) + (token.hasSuffix("A") ? "B" : "A")))
        #expect(!devices.authenticate(clientId: "bm90LXJlZ2lzdGVyZWQ", token: token))
        #expect(!devices.authenticate(clientId: clientId, token: "short"))
        #expect(!devices.authenticate(clientId: "../etc", token: token))

        let url = directory.appendingPathComponent("devices.json")
        #expect((try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int) == 0o600)
        #expect((try FileManager.default.attributesOfItem(atPath: directory.path)[.posixPermissions] as? Int) == 0o700)
        let saved = try String(contentsOf: url, encoding: .utf8)
        // The token itself never touches the disk; its hash does.
        #expect(!saved.contains(token) && saved.contains(MobileDeviceRegistry.hash(token)))
        // A fresh registry over the same file authenticates the same phone.
        let reopened = MobileDeviceRegistry(url: url, now: { clock.now() })
        #expect(reopened.authenticate(clientId: clientId, token: token))
        let removed = try reopened.remove(clientId)
        let again = try reopened.remove(clientId)
        #expect(removed && !again)
        #expect(!MobileDeviceRegistry(url: url, now: { clock.now() }).authenticate(clientId: clientId, token: token))
    }

    @Test func aTakenClientIdIsNeverWrittenOverEvenWithTheRightKey() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let key = try #require(MobilePairing.generateKey())
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let token = try #require(issuedToken(devices.issueToken(clientId: clientId, name: "Young의 iPhone")))
        let before = try #require(devices.all().first)
        clock.advance(3_600)
        // Whoever knows the key would otherwise only have to name this id to
        // take the phone's place in the list — and its name, and its history.
        #expect(devices.issueToken(clientId: clientId, name: "침입자") == .conflict)
        let frame: [String: Any] = ["type": "auth", "pairingKey": key, "clientId": clientId, "clientName": "침입자"]
        #expect(MobileAuthSupport.decide(frame: frame, pairingKey: key, devices: devices) == .refused(reason: "device-conflict"))
        // Not one field of the row moved, and the real phone still gets in.
        #expect(devices.all() == [before])
        #expect(devices.all().first?.name == "Young의 iPhone" && devices.all().first?.firstSeen == before.firstSeen)
        #expect(devices.authenticate(clientId: clientId, token: token))
    }

    @Test func aFailedWriteIssuesNoTokenAndMakesARevokeFail() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
            try? FileManager.default.removeItem(at: directory)
        }
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let token = try #require(issuedToken(devices.issueToken(clientId: clientId, name: "iPhone")))
        let before = devices.all()
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: directory.path)
        // A token the host cannot remember is worse than no token: the phone
        // keeps its pairing key and the list is left exactly as it was.
        #expect(devices.issueToken(clientId: "bmV3LWRldmljZS0wMDAwMA", name: "새 폰") == .unsaved)
        #expect(devices.all() == before)
        // A revoke that cannot be written is reported as failed, with the row
        // still in place rather than shown as gone while the file admits it.
        #expect(throws: MightyError.self) { _ = try devices.remove(clientId) }
        #expect(devices.all() == before && devices.authenticate(clientId: clientId, token: token))
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        let removed = try devices.remove(clientId)
        #expect(removed && devices.all().isEmpty)
    }

    @Test func anUnreadableListStartsOverAndKeepsTheOldBytesAside() throws {
        let clock = TestClock()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-devices-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("devices.json")
        try Data("{ not json".utf8).write(to: url)
        let devices = MobileDeviceRegistry(url: url, now: { clock.now() })
        #expect(devices.all().isEmpty)
        let warning = try #require(devices.warning())
        #expect(warning.contains("devices.json"))
        #expect(issuedToken(devices.issueToken(clientId: "Zm9vYmFyYmF6cXV4MDA", name: "iPhone")) != nil)
        // The unreadable bytes were kept before the first overwrite.
        let kept = try FileManager.default.contentsOfDirectory(atPath: directory.path).filter { $0.contains(".corrupt-") }
        #expect(kept.count == 1)
        let backup = try String(contentsOf: directory.appendingPathComponent(kept[0]), encoding: .utf8)
        #expect(backup == "{ not json")
        // A readable list says nothing, and repeated ids collapse to the first.
        let repeated = """
        [{"id":"Zm9vYmFyYmF6cXV4MDA","name":"진짜","tokenHash":"\(MobileDeviceRegistry.hash("a"))","firstSeen":"2026-09-17T00:00:00.000Z","lastSeen":"2026-09-17T00:00:00.000Z"},\
        {"id":"Zm9vYmFyYmF6cXV4MDA","name":"덮어쓰기","tokenHash":"\(MobileDeviceRegistry.hash("b"))","firstSeen":"2026-09-17T00:00:00.000Z","lastSeen":"2026-09-17T00:00:00.000Z"}]
        """
        let second = directory.appendingPathComponent("repeated.json")
        try Data(repeated.utf8).write(to: second)
        let reloaded = MobileDeviceRegistry(url: second, now: { clock.now() })
        #expect(reloaded.warning() == nil)
        #expect(reloaded.all().map(\.name) == ["진짜"])
    }

    @Test func constantTimeComparisonAnswersOnContentAlone() {
        let hash = MobileDeviceRegistry.hash("a")
        #expect(MobileDeviceRegistry.constantTimeEquals(hash, MobileDeviceRegistry.hash("a")))
        #expect(!MobileDeviceRegistry.constantTimeEquals(hash, MobileDeviceRegistry.hash("b")))
        // Length differences are answered no, never by reading past the end.
        #expect(!MobileDeviceRegistry.constantTimeEquals(hash, String(hash.dropLast())))
        #expect(!MobileDeviceRegistry.constantTimeEquals("", hash))
        #expect(MobileDeviceRegistry.constantTimeEquals("", ""))
    }

    @Test func lastSeenIsThrottledAndDoesNotMoveFirstSeen() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let token = try #require(issuedToken(devices.issueToken(clientId: clientId, name: "iPhone")))
        let first = try #require(devices.all().first?.lastSeen)
        clock.advance(30)
        #expect(devices.authenticate(clientId: clientId, token: token))
        #expect(devices.all().first?.lastSeen == first)
        clock.advance(31)
        #expect(devices.authenticate(clientId: clientId, token: token))
        let later = try #require(devices.all().first?.lastSeen)
        #expect(later != first && devices.all().first?.firstSeen == first)
    }

    @Test func aFloodOfRegistrationsCannotPushALiveDeviceOutOfTheList() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let token = try #require(issuedToken(devices.issueToken(clientId: clientId, name: "Young의 iPhone")))
        // Forty registrations, spaced out so the rate limit never bites: the
        // list fills up and then refuses, and the phone the user actually uses
        // is still in it. Evicting the coldest row on demand would have made a
        // leaked key into a way of unpairing that phone.
        var refusals = 0
        for index in 0..<40 {
            clock.advance(MobileDeviceRegistry.registrationWindow)
            if devices.issueToken(clientId: deviceId(index), name: "phone \(index)") == .refused { refusals += 1 }
        }
        #expect(refusals > 0)
        #expect(devices.all().count == MobileDeviceRegistry.maximum)
        #expect(devices.all().contains { $0.id == clientId })
        #expect(devices.authenticate(clientId: clientId, token: token))
        // The full list refuses rather than evicting; the phone hears "later".
        #expect(devices.issueToken(clientId: "YW5vdGhlci1uZXctZGV2aWNl", name: "또 다른 폰") == .refused)

        // Ninety days on, the rows nobody has used are fair game again.
        clock.advance(MobileDeviceRegistry.staleAfter + 60)
        #expect(devices.authenticate(clientId: clientId, token: token))
        let fresh = try #require(issuedToken(devices.issueToken(clientId: "YW5vdGhlci1uZXctZGV2aWNl", name: "또 다른 폰")))
        #expect(MobileDeviceRegistry.validToken(fresh))
        // The phone that connected a moment ago survived the eviction.
        #expect(devices.all().contains { $0.id == clientId } && devices.all().count == MobileDeviceRegistry.maximum)
    }

    @Test func registrationsAreRateLimitedPerHourAcrossTheWholeHost() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        for index in 0..<MobileDeviceRegistry.maximumRegistrations {
            #expect(issuedToken(devices.issueToken(clientId: deviceId(index), name: "phone \(index)")) != nil)
        }
        // Nine in one hour is one too many, whatever the ids are.
        #expect(devices.issueToken(clientId: deviceId(100), name: "phone 100") == .refused)
        clock.advance(1_800)
        #expect(devices.issueToken(clientId: deviceId(101), name: "phone 101") == .refused)
        // The window is rolling, so the hour after the first pairing frees one.
        clock.advance(1_801)
        #expect(issuedToken(devices.issueToken(clientId: deviceId(102), name: "phone 102")) != nil)
        #expect(devices.all().count == MobileDeviceRegistry.maximumRegistrations + 1)
    }

    @Test func arrivalsAreMarkedForADayAndTheListKnowsWhoIsConnected() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        #expect(issuedToken(devices.issueToken(clientId: clientId, name: "iPhone")) != nil)
        let fresh = try #require(devices.infos(connected: [clientId]).first)
        #expect(fresh.isNew && fresh.connected && !fresh.legacy)
        #expect(devices.infos(connected: []).first?.connected == false)
        clock.advance(MobileDeviceRegistry.newDeviceWindow + 60)
        #expect(devices.infos(connected: []).first?.isNew == false)
        #expect(!MobileDeviceRegistry.isNew(firstSeen: "쓰레기", now: clock.now()))
    }

    @Test func authFramesAdmitTokensPairingKeysAndOldAppsApart() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let key = try #require(MobilePairing.generateKey())
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        // First pairing: the key is right and the app knows about tokens.
        let paired = MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": key, "clientId": clientId, "clientName": "Young의 iPhone"], pairingKey: key, devices: devices)
        guard case .paired(let device, let issued) = paired, let token = issued else { Issue.record("첫 페어링이 토큰을 발급하지 않았습니다."); return }
        #expect(device == clientId && MobileDeviceRegistry.validToken(token))
        // Later: the token alone, no key.
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "clientId": clientId, "deviceToken": token], pairingKey: key, devices: devices) == .token(deviceId: clientId))
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "clientId": clientId, "deviceToken": "0123456789012345678901234567890123"], pairingKey: key, devices: devices) == .refused(reason: "device-revoked"))
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "deviceToken": token], pairingKey: key, devices: devices) == .refused(reason: "device-revoked"))
        // The old frame — pairing key only — still works and groups as one row.
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": key, "clientName": "옛 폰"], pairingKey: key, devices: devices) == .legacy)
        #expect(devices.all().contains { $0.id == MobileDeviceRegistry.legacyId && $0.legacy && $0.name == MobileDeviceRegistry.legacyName })
        // A second old phone joins the same row rather than making another.
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": key, "clientName": "다른 옛 폰"], pairingKey: key, devices: devices) == .legacy)
        #expect(devices.all().filter(\.legacy).count == 1)
        // A clientId the app did not mint properly is a malformed frame, never
        // a quiet seat in the shared "구버전 앱" row.
        for bad in ["x", "", String(repeating: "a", count: 65), "not/valid/id!!!!"] {
            #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": key, "clientId": bad], pairingKey: key, devices: devices) == .refused(reason: "malformed"))
        }
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "clientId": "x", "deviceToken": token], pairingKey: key, devices: devices) == .refused(reason: "malformed"))
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": "wrong-but-long-enough-key-value"], pairingKey: key, devices: devices) == .refused(reason: "pairing-key"))
        #expect(MobileAuthSupport.decide(frame: ["type": "auth"], pairingKey: key, devices: devices) == .refused(reason: "malformed"))
        #expect(MobileAuthSupport.decide(frame: ["type": "hello"], pairingKey: key, devices: devices) == .refused(reason: "malformed"))
        // Revoking one phone leaves the other rows untouched.
        let revoked = try devices.remove(clientId)
        #expect(revoked)
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "clientId": clientId, "deviceToken": token], pairingKey: key, devices: devices) == .refused(reason: "device-revoked"))
        #expect(devices.all().map(\.id) == [MobileDeviceRegistry.legacyId])
    }

    @Test func legacyFramesAreRefusedWhenTheHostAsksForTokensOnly() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let key = try #require(MobilePairing.generateKey())
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let legacyFrame: [String: Any] = ["type": "auth", "pairingKey": key, "clientName": "옛 폰"]
        #expect(MobileAuthSupport.decide(frame: legacyFrame, pairingKey: key, devices: devices, allowLegacy: false) == .refused(reason: "legacy-refused"))
        // Nothing was written: the group row is not created by a refusal.
        #expect(devices.all().isEmpty)
        // An app that carries a clientId is unaffected by the switch.
        let paired = MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": key, "clientId": clientId], pairingKey: key, devices: devices, allowLegacy: false)
        guard case .paired(let device, let issued) = paired, let token = issued else { Issue.record("토큰을 발급하지 않았습니다."); return }
        #expect(device == clientId)
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "clientId": clientId, "deviceToken": token], pairingKey: key, devices: devices, allowLegacy: false) == .token(deviceId: clientId))
        // A wrong key is still a wrong key, and rubbish is still malformed.
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": String(repeating: "z", count: 43)], pairingKey: key, devices: devices, allowLegacy: false) == .refused(reason: "pairing-key"))
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": key, "clientId": "x"], pairingKey: key, devices: devices, allowLegacy: false) == .refused(reason: "malformed"))
        // On again, the same old frame is admitted as the group row.
        #expect(MobileAuthSupport.decide(frame: legacyFrame, pairingKey: key, devices: devices, allowLegacy: true) == .legacy)
        #expect(devices.all().contains { $0.id == MobileDeviceRegistry.legacyId })
    }

    @Test func aRotatedKeyRefusesTheOldLegacyFrameButNotATokenOne() throws {
        let clock = TestClock()
        let (devices, directory) = registry(clock)
        defer { try? FileManager.default.removeItem(at: directory) }
        let old = try #require(MobilePairing.generateKey())
        let rotated = try #require(MobilePairing.generateKey())
        let clientId = "Zm9vYmFyYmF6cXV4MDA"
        let paired = MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": old, "clientId": clientId], pairingKey: old, devices: devices)
        guard case .paired(_, let issued) = paired, let token = issued else { Issue.record("토큰을 발급하지 않았습니다."); return }
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": old, "clientName": "옛 폰"], pairingKey: old, devices: devices) == .legacy)
        // After the rotation the old QR is dead for every key-carrying phone…
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": old, "clientName": "옛 폰"], pairingKey: rotated, devices: devices) == .refused(reason: "pairing-key"))
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "pairingKey": old, "clientId": clientId], pairingKey: rotated, devices: devices) == .refused(reason: "pairing-key"))
        // …and untouched for the one that shows a token instead.
        #expect(MobileAuthSupport.decide(frame: ["type": "auth", "clientId": clientId, "deviceToken": token], pairingKey: rotated, devices: devices) == .token(deviceId: clientId))
    }

    @Test func rotatingTheKeyClosesOnlyTheConnectionsThatLiveByIt() {
        let holder = "Zm9vYmFyYmF6cXV4MDA"
        let unsaved = "dW5zYXZlZC1kZXZpY2UtMDA"
        let connections = ["c-token", "c-legacy", "c-handshake", "c-unsaved"]
        let devices = ["c-token": holder, "c-legacy": MobileDeviceRegistry.legacyId, "c-unsaved": unsaved]
        // A phone holding a token never presents the key, so rotating it must
        // leave that phone connected; everyone else has to come back with the
        // new QR — including a socket still inside the handshake and a pairing
        // the host could not write down.
        let doomed = Set(MobileAuthSupport.keyDependent(connections: connections, devices: devices, tokenHolders: [holder]))
        #expect(doomed == Set(["c-legacy", "c-handshake", "c-unsaved"]))
        // A revoked device's token is gone from the registry, so its socket
        // stops being exempt the moment the row does.
        let afterRevoke = Set(MobileAuthSupport.keyDependent(connections: connections, devices: devices, tokenHolders: []))
        #expect(afterRevoke == Set(connections))
        #expect(MobileAuthSupport.keyDependent(connections: [], devices: devices, tokenHolders: [holder]).isEmpty)
    }

    @Test func deviceNamesAreBoundedAndNeverBlank() {
        #expect(MobileDeviceRegistry.deviceName(nil) == "휴대폰")
        #expect(MobileDeviceRegistry.deviceName("   ") == "휴대폰")
        #expect(MobileDeviceRegistry.deviceName("Young의\u{0007}iPhone") == "Young의iPhone")
        #expect(MobileDeviceRegistry.deviceName("줄\n바꿈") == "줄 바꿈")
        #expect(MobileDeviceRegistry.deviceName(String(repeating: "가", count: 100)).count == 40)
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

    @Test func theStyleFieldsRideBesideTheFixedVocabulary() throws {
        // `mightyStyle` keeps its three words; the truth travels in `styleId`,
        // so an old phone sees a plain CLI pane rather than a wrong one (§7.2).
        #expect(MobileWire.mightyStyles == ["cli", "ouroboros", "paperthin"])
        #expect(MobileCapability.all.contains("style"))
        let summary = try encoded(MobileSessionSummary(id: "s", workspaceId: "w", title: "t", kind: "claude", provider: "claude",
                                                       model: "default", status: "idle", revision: 1, updatedAt: "now",
                                                       mightyStyle: "cli", styleId: "oh-my-claudecode"))
        #expect(summary["mightyStyle"] as? String == "cli" && summary["styleId"] as? String == "oh-my-claudecode")
        let plainSummary = try encoded(MobileSessionSummary(id: "s", workspaceId: "w", title: "t", kind: "claude", provider: "claude",
                                                            model: "default", status: "idle", revision: 1, updatedAt: "now"))
        #expect(plainSummary["styleId"] == nil)

        let style = StyleFixtures.bundled("paperthin")
        let panel = StylePanelProjection.make(style: style, prompts: [], selectedGroupId: "coil",
                                              capabilityStates: [StyleCapabilityID.casebook: "absent"], attachments: [],
                                              prerequisites: StylePrerequisiteResult(ready: true))
        let legacy = MobileLegacyStyleAdapter.payloads(style: style, panel: panel, casebook: nil)
        let mighty = try encoded(MobileMighty(style: "paperthin", styleId: "paperthin", runs: [], panel: panel, paperthin: legacy.paperthin))
        #expect(mighty["styleId"] as? String == "paperthin")
        let wire = try #require(mighty["panel"] as? [String: Any])
        #expect((wire["actions"] as? [Any])?.count == 28 && (wire["groups"] as? [Any])?.count == 4)
        #expect((wire["presentation"] as? [String: Any])?["source"] as? String == "bundled")
        #expect(mighty["paperthin"] != nil)
        // A plain pane carries neither.
        let plain = try encoded(MobileMighty(style: "cli", runs: []))
        #expect(plain["panel"] == nil && plain["styleId"] as? String == "cli")
    }

    @Test func settingsCarryTheOpenStyleListAndAcceptOnlyItsMembers() throws {
        let styles = MobileRemoteSupport.styleOptions(BundledStyles.shared.styles())
        #expect(styles.map(\.id) == ["cli", "ouroboros", "paperthin"])
        #expect(styles[1].label == "Ouroboros" && styles[1].source == .bundled && styles[0].source == nil)
        let options = MobileSettingsOptions(models: [MobileOption(id: "default", label: "기본")],
                                            permissionModes: [MobileOption(id: "default", label: "기본")],
                                            mightyStyles: MobileWire.mightyStyles.map { MobileOption(id: $0, label: $0) },
                                            styles: styles)
        try MobileRemoteSupport.validate(MobileSettingsRequest(styleId: "paperthin"), options: options)
        // Unregistered and unapproved answer with the very same string (§4.5).
        #expect(throws: MobileHostError.badRequest(MobileRemoteSupport.unknownStyleMessage)) {
            try MobileRemoteSupport.validate(MobileSettingsRequest(styleId: "secret-style"), options: options)
        }
        // The host sends `mightyStyle: "cli"` beside an open id, so the phone
        // must be able to hand that very pair back.
        try MobileRemoteSupport.validate(MobileSettingsRequest(mightyStyle: "cli", styleId: "ouroboros"), options: options)
        let unapproved = MobileSettingsOptions(models: [], permissionModes: [], mightyStyles: [], styles: MobileRemoteSupport.styleOptions([
            try StyleFixtures.registered(StyleFixtures.data(), approval: .pending),
        ]))
        #expect(unapproved.styles.map(\.id) == ["cli"])
    }

    @Test func guidedAcceptsBothShapesAndPrefersTheNewOne() throws {
        let new = MobileGuidedRequest(styleId: "gstack", actionId: "ship", text: "지금")
        #expect(new.resolvedStyle == "gstack" && new.resolvedAction == "ship")
        let old = MobileGuidedRequest(style: "ouroboros", skill: "interview")
        #expect(old.resolvedStyle == "ouroboros" && old.resolvedAction == "interview")
        // Both together: the new shape wins outright, skill included (§7.5).
        let both = MobileGuidedRequest(styleId: "gstack", actionId: "ship", style: "ouroboros", skill: "interview")
        #expect(both.resolvedStyle == "gstack" && both.resolvedAction == "ship")
        #expect(MobileGuidedRequest().resolvedStyle == nil)
        let decoded = try JSONDecoder().decode(MobileGuidedRequest.self, from: Data(#"{"styleId":"gstack","actionId":"ship"}"#.utf8))
        #expect(decoded.resolvedStyle == "gstack" && decoded.style == nil)
    }
}
