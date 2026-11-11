import Foundation
import Testing
@testable import MightyCore

/// The role view a phone that asked for it gets (`MobileFlow`, `roles=1`). native/contracts/fixtures/mobile-roles.json holds, for the
/// same saved runs, what the Mac sends; Windows Core (native/windows/MightyCode.Core.Tests/MobileFlowVerification.cs) sends the same,
/// and the phone (mobile/src/__tests__/flow-roles.test.ts) renders it.
struct MobileFlowTests {
    private static let url = GraphAgentIdentityTests.root.appendingPathComponent("native/contracts/fixtures/mobile-roles.json")

    private static func cases() throws -> [[String: Any]] {
        let document = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        return try #require(document["cases"] as? [[String: Any]])
    }

    private static func runs(_ item: [String: Any]) throws -> [MightyGraphRun] {
        try JSONDecoder().decode([MightyGraphRun].self, from: JSONSerialization.data(withJSONObject: item["runs"] ?? []))
    }

    /// The Mighty payload a host sends for a case, the role view in it.
    static func payload(_ item: [String: Any]) throws -> MobileMighty {
        let runs = try runs(item)
        let provider = item["provider"] as? String ?? "claude"
        let roles = MobileFlow.Roles(fanOut: provider == "claude")
        let wire = MobileMightySupport.runs(runs, outputTails: item["outputTails"] as? [String: [String]] ?? [:], roles: roles)
        return MobileMighty(style: "cli", runs: wire, fanOut: roles.fanOut, log: roles.fanOut ? MobileFlow.log(runs) : nil)
    }

    /// The payload as JSON, cut to the keys the fixture lists.
    static func projection(_ mighty: MobileMighty) throws -> [String: Any] {
        let json = try #require(try JSONSerialization.jsonObject(with: JSONEncoder().encode(mighty)) as? [String: Any])
        var out: [String: Any] = [:]
        for key in ["fanOut", "log"] { if let value = json[key] { out[key] = value } }
        out["runs"] = try #require(json["runs"] as? [[String: Any]]).map { run -> [String: Any] in
            var kept: [String: Any] = [:]
            for key in ["id", "status", "returning", "progress"] { if let value = run[key] { kept[key] = value } }
            kept["blocks"] = (run["blocks"] as? [[String: Any]] ?? []).map { block -> [String: Any] in
                var shown: [String: Any] = [:]
                for key in ["id", "kind", "title", "status", "background", "role", "card", "delegate"] { if let value = block[key] { shown[key] = value } }
                return shown
            }
            return kept
        }
        return out
    }

    private static func pretty(_ value: Any) -> String {
        (try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys])).flatMap { String(data: $0, encoding: .utf8) } ?? "?"
    }

    /// `MIGHTY_WRITE_MOBILE_ROLES=<path>` writes what the Mac sends for every case, a JSON array in the cases' order, for when the role
    /// view's payload changes on purpose.
    @Test func writesTheExpectationsWhenAsked() throws {
        guard let path = ProcessInfo.processInfo.environment["MIGHTY_WRITE_MOBILE_ROLES"], !path.isEmpty else { return }
        let made = try LocaleOverride.$language.withValue(.ko) { try Self.cases().map { try Self.projection(Self.payload($0)) } }
        try JSONSerialization.data(withJSONObject: made, options: [.sortedKeys]).write(to: URL(fileURLWithPath: path))
    }

    @Test func theSharedFixtureIsWhatTheMacSends() throws {
        try LocaleOverride.$language.withValue(.ko) {
            for item in try Self.cases() {
                let name = item["name"] as? String ?? "?"
                let made = try Self.projection(Self.payload(item))
                guard let expect = item["expect"] as? [String: Any] else {
                    Issue.record("\(name) has no expectation; the Mac sends:\n\(Self.pretty(made))")
                    continue
                }
                #expect(NSDictionary(dictionary: made).isEqual(to: expect), "\(name) differs; the Mac sends:\n\(Self.pretty(made))")
            }
        }
    }

    /// A phone that does not ask (an older one) gets the payload it always got: no role, no row, no line, no log, and a request
    /// without its background blocks, which wait for their notice run.
    @Test func withoutRolesThePayloadIsTheOneAnOlderPhoneReads() throws {
        try LocaleOverride.$language.withValue(.ko) {
            let item = try #require(try Self.cases().first)
            let plain = MobileMightySupport.runs(try Self.runs(item), outputTails: item["outputTails"] as? [String: [String]] ?? [:])
            let json = String(decoding: try JSONEncoder().encode(MobileMighty(style: "cli", runs: plain)), as: UTF8.self)
            for key in ["role", "card", "delegate", "returning", "progress", "fanOut", "log"] { #expect(!json.contains("\"\(key)\""), "\(key) went to an older phone") }
            #expect(plain.map { $0.blocks.map(\.id) } == [["r1:main"], ["r1:notice:1:main", "r1:notice:1:bg"], ["r2:main", "scout", "deep"]])
        }
    }

    /// The fan-out is the request's blocks as they were recorded, background ones in their place; another agent's pane keeps
    /// the rule every view has (`shownAgents`) and says only who each block is.
    @Test func onlyAClaudePaneFansOut() throws {
        try LocaleOverride.$language.withValue(.ko) {
            let item = try #require(try Self.cases().first)
            let runs = try Self.runs(item)
            let claude = MobileMightySupport.runs(runs, roles: MobileFlow.Roles(fanOut: true))
            #expect(claude[2].blocks.map(\.id) == ["r2:main", "scout", "ci", "wt", "deep"])
            let other = MobileMightySupport.runs(runs, roles: MobileFlow.Roles(fanOut: false))
            #expect(other[2].blocks.map(\.id) == ["r2:main", "scout", "deep"])
            #expect(other[2].blocks.allSatisfy { $0.card == nil && $0.delegate == nil } && other[2].returning == nil)
            #expect(other[2].blocks.map { $0.role?.name } == ["lead", "subagent", "subagent"])
        }
    }

    /// The long poll wakes for what the role view draws: a background block's new step and figures, and a consultation.
    @Test func theDigestMovesWithTheFanOutAndTheAdvisor() throws {
        var runs = try Self.runs(try #require(try Self.cases().first))
        let start = MobileMightySupport.digest(runs)
        var stepped = runs
        stepped[2].agents[2].entries.append(LogEntry(id: "wt-step", kind: "assistant", text: "초안", timestamp: "2026-11-10T10:06:00Z"))
        #expect(MobileMightySupport.digest(stepped) != start)
        var counted = runs
        counted[2].agents[2].figures = GraphAgentFigures(startedAt: "2026-11-10T10:05:00Z", toolCalls: 2)
        #expect(MobileMightySupport.digest(counted) != start)
        runs[2].advisor?.calls.append(GraphAdvisorCall(id: "srvtoolu_2", startedAt: "2026-11-10T10:06:00Z", state: "consulting"))
        #expect(MobileMightySupport.digest(runs) != start)
    }

    /// `roles=1` reaches the host on the session detail alone; an older phone's poll (no `roles`) asks for none, and the list
    /// route refuses the name as it refuses any it does not know.
    @Test func theSessionRouteTakesRolesAndOnlyThere() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-roles-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", appVersion: "9.9.9")
        let host = RolesHost()
        await service.attach(host)
        func call(_ path: String) async -> Int {
            await service.route(method: "GET", path: path, body: nil, deviceId: "cGhvbmUtb25lLTAwMDAwMDA").status
        }
        #expect(await call("/m1/sessions/s?since=0&wait=0") == 200)
        #expect(await call("/m1/sessions/s?since=0&wait=0&roles=1") == 200)
        #expect(await call("/m1/sessions/s?since=0&wait=0&roles=0&structure=0") == 200)
        #expect(host.askedForRoles == [false, true, false])
        #expect(await call("/m1/state?since=0&wait=0&roles=1") == 400)
        #expect(await call("/m1/sessions/s?roles=yes") == 400)
        #expect(MobileCapability.all.contains("roles"))
    }
}

private final class RolesHost: MobileHostDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var asked: [Bool] = []
    var askedForRoles: [Bool] { lock.lock(); defer { lock.unlock() }; return asked }
    func mobileState() async -> MobileState { MobileState(revision: 1, hostName: "Test Mac", workspaces: [], sessions: []) }
    func mobileSession(id: String) async -> MobileSessionDetail? { await mobileSession(id: id, structure: false, roles: false) }
    private func record(_ roles: Bool) { lock.lock(); asked.append(roles); lock.unlock() }
    func mobileSession(id: String, structure: Bool, roles: Bool) async -> MobileSessionDetail? {
        record(roles)
        guard id == "s" else { return nil }
        let summary = MobileSessionSummary(id: "s", workspaceId: "w", title: "t", kind: "claude", provider: "claude", model: "default",
                                           status: "idle", revision: 3, updatedAt: "2026-10-13T00:00:00Z", pendingPermissions: 0)
        return MobileSessionDetail(revision: 3, session: summary, entries: [], permissions: [], queued: [], mighty: MobileMighty(style: "cli", runs: [], fanOut: roles))
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
