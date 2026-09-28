import Foundation
import Testing
@testable import MightyCore

/// §1.16: the phase a pane shows comes from its request history plus what its
/// state sources read, and only a plan modified after the pane's first request
/// in the style is the current plan. Real files, real mtimes, the bundled
/// Superpowers manifest.
struct StylePhaseFromStateTests {
    private let fm = FileManager.default
    private var style: RegisteredStyle { StyleFixtures.bundled("superpowers") }
    private var sources: StyleStateSources { style.manifest.stateSources! }

    private func workspace(_ label: String) -> URL { StyleFixtures.temporaryDirectory("phase-state-" + label) }

    private func plan(_ text: String, named name: String = "2026-09-28-feature.md", in root: URL, modified: Date) throws {
        let url = root.appendingPathComponent("docs/superpowers/plans/" + name)
        try StyleFixtures.write(Data(text.utf8), to: url)
        try fm.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
    }

    /// A pane in the style whose first request in it was at `since`.
    private func pane(since: Date?, prompts: [String] = []) -> RunSession {
        var session = RunSession(workspaceId: "w", title: "t")
        session.mightyStyle = "superpowers"
        session.mightyStyleSince = since.map { ISO8601DateFormatter().string(from: $0) }
        session.logs = prompts.map { LogEntry(kind: "user", text: $0, timestamp: session.mightyStyleSince ?? mightyTimestamp()) }
        return session
    }

    /// The Mac pane's phase and the phone panel's phase for the same pane,
    /// both from one engine reading.
    private func phases(_ session: RunSession, root: URL) -> (mac: String?, phone: String?, reading: StyleStateReading) {
        let since = session.mightyStyleSince.flatMap(AgentRunTiming.parseTimestamp)
        let reading = StyleStateEngine.read(sources, workspacePath: root.path, since: since, session: session)
        let mac = style.evaluator.currentPhase(session: session, fileSourceStates: reading.fileSourceStates)?.id
        let prompts = session.logs.filter { $0.kind == "user" }.map(\.text)
        let phone = StylePanelProjection.make(style: style, prompts: prompts, selectedGroupId: nil, capabilityStates: [:],
                                              attachments: [], prerequisites: StylePrerequisiteResult(ready: true),
                                              session: session, state: reading).phase?.id
        return (mac, phone, reading)
    }

    @Test func aNewSessionOpensInBrainstormWhateverPlansAreLeftOver() throws {
        let root = workspace("new-session")
        defer { try? fm.removeItem(at: root) }
        // A finished plan from yesterday's work.
        try plan("- [x] a\n- [x] b\n", in: root, modified: Date(timeIntervalSinceNow: -86_400))

        // No request in the style yet: nothing is current.
        let fresh = phases(pane(since: nil), root: root)
        #expect(fresh.mac == "brainstorm" && fresh.phone == "brainstorm")
        #expect(fresh.reading.fileSourceStates[0] == StyleFileSourceState(exists: false, allChecked: false))
        #expect(fresh.reading.widgets.first == .progressBar(value: 0, total: 0))

        // The first request was just made: the old plan is still stale.
        let started = phases(pane(since: Date(timeIntervalSinceNow: -60), prompts: ["/superpowers:brainstorming 결제"]), root: root)
        #expect(started.mac == "brainstorm" && started.phone == "brainstorm")
        #expect(started.reading.widgets.first == .progressBar(value: 0, total: 0))
    }

    @Test func aStalePlanIsIgnoredAndAFreshOneAdvancesToExecute() throws {
        let root = workspace("fresh-plan")
        defer { try? fm.removeItem(at: root) }
        let since = Date(timeIntervalSinceNow: -600)
        // Newer by name but older than the first request: not current.
        try plan("- [x] a\n", named: "2026-09-29-stale.md", in: root, modified: since.addingTimeInterval(-5))
        let before = phases(pane(since: since, prompts: ["/superpowers:brainstorming 결제"]), root: root)
        #expect(before.mac == "brainstorm" && before.phone == "brainstorm")

        // writing-plans saved a plan after the first request.
        try plan("- [x] one\n- [ ] two\n- [ ] three\n", named: "2026-09-28-fresh.md", in: root, modified: since.addingTimeInterval(120))
        let after = phases(pane(since: since, prompts: ["/superpowers:brainstorming 결제"]), root: root)
        #expect(after.mac == "execute" && after.phone == "execute")
        #expect(after.reading.widgets.first == .progressBar(value: 1, total: 3))
        // History alone would have said brainstorm: the state is what moved it.
        #expect(style.evaluator.currentPhase(session: pane(since: since, prompts: ["/superpowers:brainstorming 결제"]))?.id == "brainstorm")
    }

    @Test func theNewestCurrentPlanIsTheOneRead() throws {
        let root = workspace("newest")
        defer { try? fm.removeItem(at: root) }
        let since = Date(timeIntervalSinceNow: -600)
        try plan("- [x] a\n- [x] b\n", named: "first.md", in: root, modified: since.addingTimeInterval(60))
        try plan("- [x] a\n- [ ] b\n- [ ] c\n- [ ] d\n", named: "second.md", in: root, modified: since.addingTimeInterval(120))
        let result = phases(pane(since: since), root: root)
        #expect(result.reading.widgets.first == .progressBar(value: 1, total: 4))
        #expect(result.mac == "execute" && result.phone == "execute")
    }

    @Test func everyItemCheckedMovesToFinishAndACommandStandsWithoutAPlan() throws {
        let root = workspace("finish")
        defer { try? fm.removeItem(at: root) }
        let since = Date(timeIntervalSinceNow: -600)
        try plan("- [x] one\n- [x] two\n", in: root, modified: since.addingTimeInterval(300))
        let done = phases(pane(since: since, prompts: ["/superpowers:executing-plans"]), root: root)
        #expect(done.mac == "finish" && done.phone == "finish")
        #expect(done.reading.widgets.first == .progressBar(value: 2, total: 2))

        // A plan command without a current plan file: the command's phase stands.
        let empty = workspace("finish-empty")
        defer { try? fm.removeItem(at: empty) }
        let planning = phases(pane(since: since, prompts: ["/superpowers:writing-plans"]), root: empty)
        #expect(planning.mac == "plan" && planning.phone == "plan")
    }

    @Test func enterStaysVerbatimWhateverTheStateSays() throws {
        let root = workspace("enter")
        defer { try? fm.removeItem(at: root) }
        let since = Date(timeIntervalSinceNow: -600)
        try plan("- [ ] one\n", in: root, modified: since.addingTimeInterval(60))
        let result = phases(pane(since: since), root: root)
        let phase = style.manifest.phase(try #require(result.mac))
        #expect(phase?.id == "execute")
        for draft in ["결제 모듈 계속", "/superpowers:executing-plans", ""] {
            #expect(style.evaluator.enterBehaviour(draft: draft, phase: phase, hasAttachments: false, running: false,
                                                   hasRequests: false, startingNew: true) == .verbatim)
        }
    }

    @Test func theFirstRequestMarkerSurvivesSaving() throws {
        let since = "2026-09-28T01:02:03Z"
        var session = pane(since: AgentRunTiming.parseTimestamp(since))
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(decoded.mightyStyleSince == since)
        // An older save without the field reads as "no request in the style yet".
        session.mightyStyleSince = nil
        let legacy = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(legacy.mightyStyleSince == nil)
    }

    @Test func savedMarkerIsDroppedWithTheStyleOrWhenMalformed() {
        let workspace = Workspace(id: "ws", name: "R", path: "/tmp/r")
        func restored(style: String?, since: String?) -> String? {
            var session = RunSession(id: "pane", workspaceId: "ws", title: "Claude")
            session.agentViewMode = "mighty"; session.mightyStyle = style; session.mightyStyleSince = since
            return StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: true)
                .sessions.first?.mightyStyleSince
        }
        #expect(restored(style: "superpowers", since: "2026-09-28T01:02:03Z") == "2026-09-28T01:02:03Z")
        #expect(restored(style: "superpowers", since: "2026-09-28T01:02:03.250Z") == "2026-09-28T01:02:03.250Z")
        // No style, no marker: a later style starts over at its own first request.
        #expect(restored(style: nil, since: "2026-09-28T01:02:03Z") == nil)
        #expect(restored(style: "Bad_Shape", since: "2026-09-28T01:02:03Z") == nil)
        // Not a timestamp, or far longer than one: dropped rather than trusted.
        #expect(restored(style: "superpowers", since: "yesterday") == nil)
        #expect(restored(style: "superpowers", since: "2026-09-28T01:02:03Z" + String(repeating: " ", count: 60)) == nil)
    }

    @Test func aSnapshotSavedBeforeTheMarkerExistedStillLoads() {
        // A pane saved by the previous app version: a style, no `mightyStyleSince`.
        let json = Data("""
        {"version":1,"workspaces":[{"id":"ws-old","name":"Old","path":"/private/tmp","createdAt":"2026-09-01T00:00:00Z"}],"sessions":[{"id":"pane-old","workspaceId":"ws-old","title":"Claude","kind":"claude","provider":"claude","model":"default","settings":{"effort":"default","permissionMode":"manual"},"status":"completed","logs":[{"id":"u1","kind":"user","text":"/superpowers:writing-plans","timestamp":"2026-09-01T00:00:05Z"}],"createdAt":"2026-09-01T00:00:00Z","agentViewMode":"mighty","mightyStyle":"superpowers","mightyStyleHash":"abc"}],"layout":"grid","theme":"dark","sidebarWidth":252}
        """.utf8)
        let snapshot = StateRepository.decodeSnapshot(json)
        let pane = snapshot.sessions.first { $0.id == "pane-old" }
        #expect(pane?.mightyStyle == "superpowers" && pane?.mightyStyleHash == "abc")
        #expect(pane != nil && pane?.mightyStyleSince == nil)
        // With no marker nothing on disk is current, so history alone decides.
        if let pane {
            #expect(style.evaluator.currentPhase(session: pane, fileSourceStates: [:])?.id == "plan")
            #expect(StyleStateEngine.runEvents(from: pane, since: nil).isEmpty)
        }
    }
}
