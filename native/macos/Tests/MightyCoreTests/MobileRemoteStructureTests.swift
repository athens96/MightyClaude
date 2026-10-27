import Foundation
import Testing
@testable import MightyCore

/// `MobileMighty.structure`: the Mac's structure view as the phone receives it — the same
/// columns, rows and cards `MightyStructure.make` builds, bounded for the wire.
struct MobileRemoteStructureTests {
    typealias Fixture = MightyStructureTests

    static func encoded(_ value: some Encodable) throws -> [String: Any] {
        let data = try JSONEncoder().encode(value)
        return try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    // MARK: The same structure

    @Test func aStyledPaneCarriesTheMacsColumns() throws {
        let style = try Fixture.style("ouroboros")
        var runs = [Fixture.run("r1", input: "ooo interview app"), Fixture.run("r2", input: "more questions"),
                    Fixture.run("r3", input: "ooo seed", status: "running", agents: [Fixture.agent("a1", status: "running"), Fixture.agent("a2", parent: "a1")])]
        runs[2].rootEntries = [LogEntry(id: "m", kind: "assistant", text: "writing the seed"), Fixture.tool("t", summary: "seed.yaml")]
        let phase = style.manifest.phase("seed")
        let shape = MightyStructure.make(runs: runs, style: style, currentPhase: phase)
        let wire = try #require(MobileStructure.make(shape, runCount: runs.count, title: { $0 == "r3" ? "Seed" : nil }))
        #expect(wire.v == 1)
        #expect(wire.shape == "phases")
        #expect(wire.rows == nil)
        #expect(wire.omitted == nil)
        let columns = try #require(wire.columns)
        let mac = Fixture.columns(shape)
        #expect(columns.map(\.id) == mac.map(\.id))
        #expect(columns.map(\.title) == mac.map(\.phase.title))
        #expect(columns.map(\.step) == mac.map(\.step.rawValue))
        #expect(columns.map { $0.items.map(\.runId) } == mac.map { $0.items.map(\.runID) })
        let card = try #require(columns.first { $0.id == "seed" }?.items.first)
        let source = try #require(mac.first { $0.id == "seed" }?.items.first)
        #expect(card.ordinal == 3)
        #expect(card.title == "Seed")
        #expect(card.status == source.status)
        #expect(card.finished == source.finished)
        #expect(card.request == source.request?.text)
        #expect(card.message == source.message?.text)
        #expect(card.activity.map(\.text) == source.activity.map(\.text))
        #expect(card.tally == MobileStructure.Tally(agents: source.tally.agents, tools: source.tally.tools))
        #expect(card.clusters.map { $0.nodes.map(\.id) } == [["a1", "a2"]])
        #expect(card.clusters[0].nodes.map(\.depth) == [0, 1])
        #expect(columns.first { $0.id == "interview" }?.items.first?.title == nil)
    }

    @Test func aFreePaneCarriesTheMacsRows() throws {
        let runs = [Fixture.run("done", final: "All done\n\n```swift\nlet x = 1\n```"), Fixture.run("failed", status: "error"),
                    Fixture.run("live", status: "running")]
        let shape = MightyStructure.make(runs: runs, style: nil, currentPhase: nil)
        let wire = try #require(MobileStructure.make(shape, runCount: runs.count))
        #expect(wire.shape == "free")
        #expect(wire.columns == nil)
        let rows = try #require(wire.rows)
        let mac = Fixture.rows(shape)
        #expect(rows.map(\.item.runId) == mac.map(\.item.runID))
        #expect(rows.map(\.request) == mac.map(\.request.rawValue))
        #expect(rows.map(\.execution) == mac.map(\.execution.rawValue))
        #expect(rows.map(\.result) == mac.map(\.result.rawValue))
        #expect(rows[0].item.answer == mac[0].item.answer?.text)
        #expect(rows.map(\.item.ordinal) == [1, 2, 3])
    }

    @Test func theWireShapeIsCompactAndVersioned() throws {
        let runs = [Fixture.run("r1")]
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: runs, style: nil, currentPhase: nil), runCount: 1))
        let json = try Self.encoded(wire)
        #expect(json["v"] as? Int == 1)
        #expect(json["shape"] as? String == "free")
        #expect(json["columns"] == nil)
        #expect(json["omitted"] == nil)
        let row = try #require((json["rows"] as? [[String: Any]])?.first)
        #expect(Set(row.keys) == ["item", "request", "execution", "result"])
        let item = try #require(row["item"] as? [String: Any])
        #expect(item["runId"] as? String == "r1")
        #expect(item["omittedNodes"] == nil)
        #expect(try JSONDecoder().decode(MobileStructure.self, from: JSONEncoder().encode(wire)) == wire)
    }

    /// `native/contracts/fixtures/mobile-structure.json`, which the phone's tests read too:
    /// every field the phone reads is one this type writes, and nothing more.
    @Test func theSharedFixtureIsTheWireShape() throws {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        let data = try Data(contentsOf: url.appendingPathComponent("native/contracts/fixtures/mobile-structure.json"))
        let fixture = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        for name in ["styled", "free"] {
            let raw = try #require(fixture[name] as? [String: Any])
            let decoded = try JSONDecoder().decode(MobileStructure.self, from: JSONSerialization.data(withJSONObject: raw))
            let again = try Self.encoded(decoded)
            #expect(NSDictionary(dictionary: again).isEqual(to: raw), "\(name) changed through the wire type")
        }
    }

    // MARK: Absent

    @Test func absentWithNothingToDraw() throws {
        #expect(MobileStructure.make(MightyStructure.make(runs: [], style: nil, currentPhase: nil), runCount: 0) == nil)
        let cache = MobileStructureCache()
        #expect(cache.structure(pane: "p", runs: [], style: nil, currentPhase: nil) == MobileStructureCache.Built(value: nil, tag: 0))
        // A style with phases still shows its bar before the first request.
        let style = try Fixture.style("ouroboros")
        let empty = try #require(cache.structure(pane: "q", runs: [], style: style, currentPhase: style.manifest.phase("goal")).value)
        #expect(empty.columns?.allSatisfy(\.items.isEmpty) == true)
        #expect(empty.columns?.first?.step == "current")
        // The field itself is left out of the Mighty payload, so an old phone never sees it.
        let mighty = try Self.encoded(MobileMighty(style: "cli", runs: []))
        #expect(mighty["structure"] == nil)
        #expect(mighty.keys.contains("runs"))
    }

    // MARK: Bounds

    @Test func textsAreCappedUnderTheMacsBounds() throws {
        let long = String(repeating: "가나다라 ", count: 2_000)
        var agent = Fixture.agent("a")
        agent.input = long
        agent.entries = [LogEntry(id: "o", kind: "assistant", text: long), Fixture.tool("t", summary: String(repeating: "x", count: 900))]
        var run = Fixture.run("r", input: long, status: "running", agents: [agent])
        run.rootEntries = [LogEntry(id: "m", kind: "assistant", text: long)]
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: [run], style: nil, currentPhase: nil), runCount: 1))
        let item = try #require(wire.rows?.first?.item)
        #expect(try #require(item.request).utf8.count <= MobileStructure.requestBytes)
        #expect(try #require(item.request).hasSuffix("…"))
        #expect(try #require(item.message).utf8.count <= MobileStructure.messageBytes)
        let node = try #require(item.clusters.first?.nodes.first)
        #expect(try #require(node.prompt).utf8.count <= MobileStructure.promptBytes)
        #expect(try #require(node.output).utf8.count <= MobileStructure.outputBytes)
        #expect(try #require(node.activity).text.utf8.count <= MobileStructure.activityBytes)
        // The answer is Markdown: a fence the cut leaves open is closed inside the cap.
        let answer = "intro\n```\n" + String(repeating: "code line\n", count: 1_000)
        let done = Fixture.run("d", final: answer)
        let finished = try #require(MobileStructure.make(MightyStructure.make(runs: [done], style: nil, currentPhase: nil), runCount: 1))
        let text = try #require(finished.rows?.first?.item.answer)
        #expect(text.utf8.count <= MobileStructure.answerBytes)
        #expect(text.hasSuffix("\n```"))
        #expect(MightyStructure.openFence(text) == nil)
    }

    @Test func onlyTheNewestRequestsTravel() throws {
        let runs = (1...25).map { Fixture.run("r\($0)") }
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: runs, style: nil, currentPhase: nil), runCount: runs.count))
        let rows = try #require(wire.rows)
        #expect(rows.count == MobileStructure.maximumItems)
        #expect(rows.first?.item.runId == "r6")
        #expect(rows.last?.item.runId == "r25")
        #expect(rows.first?.item.ordinal == 6)
        #expect(wire.omitted == 5)
    }

    @Test func aRequestCarriesAtMostSixtyNodesAndCapsTheirDepth() throws {
        // One chain eight deep, then wide siblings: 8 + 70 blocks.
        var agents = (0..<8).map { Fixture.agent("c\($0)", parent: $0 == 0 ? nil : "c\($0 - 1)") }
        agents += (0..<70).map { Fixture.agent("w\($0)") }
        let run = Fixture.run("r", agents: agents)
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: [run], style: nil, currentPhase: nil), runCount: 1))
        let item = try #require(wire.rows?.first?.item)
        let nodes = item.clusters.flatMap(\.nodes)
        #expect(nodes.count == MobileStructure.maximumNodes)
        #expect(item.omittedNodes == 78 - MobileStructure.maximumNodes)
        #expect(nodes.map(\.depth).max() == MobileStructure.maximumDepth)
        #expect(nodes.prefix(8).map(\.depth) == [0, 1, 2, 3, 4, 5, 6, 6])
        // The tally still counts the whole request.
        #expect(item.tally.agents == 78)
    }

    @Test func theBudgetDropsOlderRequestsFirst() throws {
        // Each request near the per-text caps with sixty full nodes: far over the budget together.
        let long = String(repeating: "\"quoted\" ", count: 600)
        func heavy(_ id: String) -> MightyGraphRun {
            let agents = (0..<60).map { index -> MightyGraphAgent in
                var agent = Fixture.agent("\(id)-\(index)")
                agent.input = long
                agent.entries = [LogEntry(id: "o\(index)", kind: "assistant", text: long)]
                return agent
            }
            return Fixture.run(id, input: long, agents: agents, final: long)
        }
        let runs = (1...6).map { heavy("r\($0)") }
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: runs, style: nil, currentPhase: nil), runCount: runs.count))
        let rows = try #require(wire.rows)
        #expect(try JSONEncoder().encode(wire).count <= MobileStructure.budgetBytes)
        #expect(rows.last?.item.runId == "r6")
        #expect(rows.count < runs.count)
        #expect(rows.map(\.item.runId) == runs.suffix(rows.count).map(\.id))
        #expect(wire.omitted == runs.count - rows.count)
    }

    @Test func aRequestOverTheBudgetAloneKeepsItsFirstNodes() throws {
        let run = Fixture.run("r", agents: [Fixture.agent("a"), Fixture.agent("b", parent: "a"), Fixture.agent("c")])
        let item = MobileStructure.wire(MightyStructure.item(run, index: 0), title: nil, model: { $0 })
        #expect(item.clusters.map { $0.nodes.map(\.id) } == [["a", "b"], ["c"]])
        let two = MobileStructure.keeping(item, nodes: 2)
        #expect(two.clusters.map { $0.nodes.map(\.id) } == [["a", "b"]])
        #expect(two.omittedNodes == 1)
        let none = MobileStructure.keeping(item, nodes: 0)
        #expect(none.clusters.isEmpty)
        #expect(none.omittedNodes == 3)
        #expect(MobileStructure.keeping(item, nodes: 9) == item)

        // One request whose sixty nodes alone are past the budget: it travels, trimmed to fit.
        let heavy = String(repeating: "\"\\", count: 2_000)
        let agents = (0..<60).map { index -> MightyGraphAgent in
            var agent = Fixture.agent("n\(index)")
            agent.title = heavy
            agent.input = heavy
            agent.entries = [LogEntry(id: "o\(index)", kind: "assistant", text: heavy)]
            agent.entries.append(Fixture.tool("t\(index)", summary: heavy))
            return agent
        }
        let big = Fixture.run("big", input: heavy, agents: agents, final: heavy)
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: [big], style: nil, currentPhase: nil), runCount: 1))
        let sent = try #require(wire.rows?.first?.item)
        #expect(try JSONEncoder().encode(wire).count <= MobileStructure.budgetBytes)
        let carried = sent.clusters.flatMap(\.nodes).count
        #expect(carried > 0 && carried < 60)
        #expect(sent.omittedNodes == 60 - carried)
    }

    @Test func aRepeatedRunIDTravelsOnce() throws {
        let runs = [Fixture.run("same"), Fixture.run("other"), Fixture.run("same", input: "again")]
        let wire = try #require(MobileStructure.make(MightyStructure.make(runs: runs, style: nil, currentPhase: nil), runCount: runs.count))
        let rows = try #require(wire.rows)
        #expect(rows.map(\.item.runId) == ["other", "same"])
        #expect(rows.last?.item.ordinal == 3)
        #expect(wire.omitted == nil)
    }

    @Test func cutsFallBetweenCharactersAndCloseTheFenceTheyLeaveOpen() {
        // A family emoji is one character of many scalars: never split.
        let family = "👨‍👩‍👧‍👦"
        let cut = MobileStructure.capped(String(repeating: family, count: 40), 100)
        #expect(cut.utf8.count <= 100)
        #expect(cut.hasSuffix("…"))
        #expect(cut.dropLast().allSatisfy { $0 == Character(family) })
        // A long fence early on: the shorter second cut still ends inside it, and its close fits.
        let long = String(repeating: "`", count: 40)
        let text = "intro\n" + long + "\n" + String(repeating: "code\n", count: 200)
        let answer = MobileStructure.capped(text, 300, markdown: true)
        #expect(answer.utf8.count <= 300)
        #expect(answer.hasSuffix("\n" + long))
        #expect(MightyStructure.openFence(answer) == nil)
    }

    // MARK: Rebuilt only when the structure moved

    @Test func theCacheRebuildsOnlyWhenTheStructureMoves() throws {
        let cache = MobileStructureCache()
        var run = Fixture.run("r", status: "running")
        run.rootEntries = [LogEntry(id: "m", kind: "assistant", text: "reading")]
        let first = cache.structure(pane: "p", runs: [run], style: nil, currentPhase: nil)
        #expect(cache.builds == 1)
        #expect(first.tag == MobileStructure.tag(first.value))
        #expect(first.tag > 0 && first.tag <= 999_999_999_999)
        // A streamed token that moves nothing drawn reuses the value.
        run.rootEntries[0].text += " the file"
        #expect(cache.structure(pane: "p", runs: [run], style: nil, currentPhase: nil) == first)
        #expect(cache.builds == 1)
        // A new block moves the structure.
        run.agents.append(Fixture.agent("a"))
        let grown = try #require(cache.structure(pane: "p", runs: [run], style: nil, currentPhase: nil).value)
        #expect(cache.builds == 2)
        #expect(grown.rows?.first?.item.clusters.count == 1)
        // So does the style's key (its request titles), and another pane has its own.
        _ = cache.structure(pane: "p", runs: [run], style: nil, currentPhase: nil, key: "other")
        #expect(cache.builds == 3)
        _ = cache.structure(pane: "q", runs: [run], style: nil, currentPhase: nil)
        #expect(cache.builds == 4)
        cache.retain(["q"])
        _ = cache.structure(pane: "p", runs: [run], style: nil, currentPhase: nil, key: "other")
        #expect(cache.builds == 5)
    }

    @Test func thePanesPhaseAndPlansMoveTheStructure() throws {
        let style = try Fixture.style("claude-plan")
        let cache = MobileStructureCache()
        let runs = [Fixture.run("r1", source: "s1")]
        let planning = try #require(cache.structure(pane: "p", runs: runs, style: style, currentPhase: style.manifest.phase("plan")).value)
        let approved = try #require(cache.structure(pane: "p", runs: runs, style: style, currentPhase: style.manifest.phase("execute"),
                                                    planHistory: [Fixture.record("p1", graphRunId: "s1", outcome: .approvedAuto)]).value)
        #expect(planning != approved)
        #expect(approved.columns?.first { $0.id == "execute" }?.items.map(\.runId) == ["r1"])
        #expect(cache.builds == 2)
    }

    // MARK: Sent only when asked for, and only when it changed

    static func detail(_ structure: MobileStructure?, tag: Int) -> MobileSessionDetail {
        let summary = MobileSessionSummary(id: "s", workspaceId: "w", title: "t", kind: "claude", provider: "claude", model: "default",
                                           status: "idle", revision: 3, updatedAt: "2026-10-13T00:00:00Z", pendingPermissions: 0)
        return MobileSessionDetail(revision: 3, session: summary, entries: [], permissions: [], queued: [],
                                   mighty: MobileMighty(style: "cli", runs: [], structure: structure, structureTag: tag))
    }

    @Test func theStructureTravelsOnlyWhenAskedAndChanged() throws {
        let value = try #require(MobileStructure.make(MightyStructure.make(runs: [Fixture.run("r")], style: nil, currentPhase: nil), runCount: 1))
        let tag = MobileStructure.tag(value)
        let full = Self.detail(value, tag: tag)
        // Not asked (an older phone, or the chat view): neither field.
        let plain = MobileRemoteSupport.outgoing(full, asked: nil)
        #expect(plain.mighty?.structure == nil && plain.mighty?.structureTag == nil)
        // Asked with another tag: the value and its tag.
        let sent = MobileRemoteSupport.outgoing(full, asked: 0)
        #expect(sent.mighty?.structure == value && sent.mighty?.structureTag == tag)
        // Asked with the tag it holds: the tag alone.
        let same = MobileRemoteSupport.outgoing(full, asked: tag)
        #expect(same.mighty?.structure == nil && same.mighty?.structureTag == tag)
        // Past the reply limit the structure goes first; the tag stays, so the phone knows.
        let shed = MobileRemoteSupport.outgoing(full, asked: 0, maximumBytes: 200)
        #expect(shed.mighty?.structure == nil && shed.mighty?.structureTag == tag)
        #expect(shed.mighty?.runs == full.mighty?.runs)
    }

    @Test func theSessionRouteTakesTheStructureTagAndOnlyThere() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-structure-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", appVersion: "9.9.9")
        let value = try #require(MobileStructure.make(MightyStructure.make(runs: [Fixture.run("r")], style: nil, currentPhase: nil), runCount: 1))
        let host = StructureHost(detail: Self.detail(value, tag: MobileStructure.tag(value)))
        await service.attach(host)
        func call(_ path: String) async -> (Int, [String: Any]) {
            let reply = await service.route(method: "GET", path: path, body: nil, deviceId: "cGhvbmUtb25lLTAwMDAwMDA")
            return (reply.status, (try? JSONSerialization.jsonObject(with: reply.body) as? [String: Any]) ?? [:])
        }
        let plain = await call("/m1/sessions/s?since=0&wait=0")
        #expect(plain.0 == 200)
        #expect((plain.1["mighty"] as? [String: Any])?["structureTag"] == nil)
        #expect(host.askedForStructure == [false])
        let asked = await call("/m1/sessions/s?since=0&wait=0&structure=0")
        let mighty = try #require(asked.1["mighty"] as? [String: Any])
        #expect(mighty["structure"] != nil)
        #expect(mighty["structureTag"] as? Int == MobileStructure.tag(value))
        #expect(host.askedForStructure == [false, true])
        let held = await call("/m1/sessions/s?since=0&wait=0&structure=\(MobileStructure.tag(value))")
        #expect((held.1["mighty"] as? [String: Any])?["structure"] == nil)
        // The state route has no structure.
        #expect(await call("/m1/state?since=0&wait=0&structure=0").0 == 400)
        #expect(await call("/m1/sessions/s?structure=x").0 == 400)
        #expect(MobileCapability.all.contains("structure"))
    }

    @Test func aBackgroundRunsCardCarriesItsTailAndReplyButNeverItsFile() throws {
        let task = MightyGraphAgent(id: "t1", title: "npm test", input: "npm test", status: "running", kind: "task",
                                    background: true, outputFile: "/tmp/example/tasks/t1.output", noticeReply: "통과")
        let runs = [Fixture.run("r1", status: "running", agents: [task, Fixture.agent("a1", status: "running")])]
        let shape = MightyStructure.make(runs: runs, style: nil, currentPhase: nil)
        let lines = (1...8).map { "line \($0)" }
        let wire = try #require(MobileStructure.make(shape, runCount: 1, tails: ["t1": lines, "a1": ["no"]]))
        let clusters = try #require(wire.rows?.first?.item.clusters)
        let nodes = clusters.flatMap(\.nodes)
        let card = try #require(nodes.first { $0.id == "t1" })
        #expect(card.background == true && card.outputTail == Array(lines.suffix(6)) && card.noticeReply == "통과")
        let agent = try #require(nodes.first { $0.id == "a1" })
        #expect(agent.background == nil && agent.outputTail == nil && agent.noticeReply == nil)
        let text = String(decoding: try JSONEncoder().encode(wire), as: UTF8.self)
        #expect(!text.contains("outputFile") && !text.contains("/tmp/example"))
        // A new line rebuilds the cached card.
        let cache = MobileStructureCache()
        let first = cache.structure(pane: "p", runs: runs, style: nil, currentPhase: nil, tails: ["t1": ["a"]])
        let second = cache.structure(pane: "p", runs: runs, style: nil, currentPhase: nil, tails: ["t1": ["a", "b"]])
        #expect(first.tag != second.tag)
        #expect(second.value?.rows?.first?.item.clusters.first?.nodes.first?.outputTail == ["a", "b"])
    }
}

private final class StructureHost: MobileHostDelegate, @unchecked Sendable {
    let detail: MobileSessionDetail
    private let lock = NSLock()
    private var asked: [Bool] = []
    var askedForStructure: [Bool] { lock.lock(); defer { lock.unlock() }; return asked }
    init(detail: MobileSessionDetail) { self.detail = detail }
    func mobileState() async -> MobileState { MobileState(revision: 1, hostName: "Test Mac", workspaces: [], sessions: []) }
    func mobileSession(id: String) async -> MobileSessionDetail? { await mobileSession(id: id, structure: false) }
    func mobileSession(id: String, structure: Bool) async -> MobileSessionDetail? {
        lock.lock(); asked.append(structure); lock.unlock()
        guard id == "s" else { return nil }
        guard !structure else { return detail }
        var plain = detail
        plain.mighty?.structure = nil
        plain.mighty?.structureTag = nil
        return plain
    }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String { "started" }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String { "started" }
    func mobileStop(sessionId: String) async throws -> Bool { false }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {}
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {}
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { "new" }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {}
    func mobileRunNextQueued(sessionId: String) async throws {}
    func mobileRename(sessionId: String, title: String, titleMode: String?) async throws {}
    func mobileClose(sessionId: String) async throws {}
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage { MobileEntriesPage(entries: [], hasMore: false) }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {}
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] { [] }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? { nil }
}
