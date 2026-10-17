import Testing
import Foundation
@testable import MightyCore

/// native/contracts/fixtures/remote-v2/: the remote-workspace wire both apps speak.
/// The Mac is the reference: every sample decodes into the Mac's own type and
/// encodes back to itself (null values aside). The Windows Core reads the same
/// files in RemoteWireFixtureVerification.
struct RemoteWireFixtureTests {
    static let folder: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/remote-v2")
    }()
    static func data(_ name: String) throws -> Data { try Data(contentsOf: folder.appendingPathComponent(name)) }

    /// Nulls removed, so an explicit null and a missing key read the same.
    static func plain(_ value: Any) -> Any {
        if let object = value as? [String: Any] { return object.filter { !($0.value is NSNull) }.mapValues(plain) }
        if let array = value as? [Any] { return array.map(plain) }
        return value
    }
    static func same(_ lhs: Data, _ rhs: Data) throws -> Bool {
        let a = plain(try JSONSerialization.jsonObject(with: lhs)), b = plain(try JSONSerialization.jsonObject(with: rhs))
        return NSDictionary(dictionary: ["v": a]).isEqual(to: ["v": b])
    }
    static func roundTrips<T: Codable>(_ name: String, _ type: T.Type) throws -> Bool {
        let raw = try data(name)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        return try same(raw, encoder.encode(JSONDecoder().decode(T.self, from: raw)))
    }

    @Test func everyBodyIsTheMacsOwnEncoding() throws {
        #expect(try Self.roundTrips("info.json", RemoteWireInfo.self))
        #expect(try Self.roundTrips("info-darwin.json", RemoteWireInfo.self))
        #expect(try Self.roundTrips("start.json", RemoteWireStart.self))
        #expect(try Self.roundTrips("start-codex.json", RemoteWireStart.self))
        #expect(try Self.roundTrips("start-accepted.json", RemoteWireJob.self))
        #expect(try Self.roundTrips("poll.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-gap.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-malformed.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-requests.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("steer.json", RemoteWireSteer.self))
        #expect(try Self.roundTrips("permission.json", RemoteWirePermission.self))
        #expect(try Self.roundTrips("questions.json", RemoteWireQuestions.self))
        #expect(try Self.roundTrips("plan-approved.json", RemoteWirePlan.self))
        #expect(try Self.roundTrips("plan-revised.json", RemoteWirePlan.self))
        #expect(try Self.roundTrips("answer.json", RemoteWireAnswer.self))
        #expect(try Self.roundTrips("answer-declined.json", RemoteWireAnswer.self))
        #expect(try JSONDecoder().decode(RemoteWireAnswer.self, from: Self.data("answer-declined.json")).accepted == false)
        let error = try JSONSerialization.jsonObject(with: Self.data("error.json")) as? [String: Any]
        #expect(error?["protocol"] as? Int == RemoteProtocol.version && error?["error"] is String)
        #expect((try JSONSerialization.jsonObject(with: Self.data("stop.json")) as? [String: Any])?.isEmpty == true)
    }

    /// The comparison above reads a null and a missing key alike, so what the Mac
    /// writes as an explicit null, or leaves out, is pinned here.
    @Test func aStartIsEncodedWithItsNullsAndOmissions() throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        for name in ["start.json", "start-codex.json"] {
            let wire = try JSONDecoder().decode(RemoteWireStart.self, from: Self.data(name))
            let encoded = try #require(JSONSerialization.jsonObject(with: encoder.encode(wire)) as? [String: Any])
            let request = try #require(encoded["request"] as? [String: Any]), settings = try #require(request["settings"] as? [String: Any])
            #expect(settings["maxTurns"] is NSNull && settings["maxBudgetUsd"] is NSNull, "\(name): maxTurns and maxBudgetUsd are explicit nulls")
            #expect(encoded["registeredModels"] is [Any], "\(name): registeredModels is always there")
            #expect(request["registeredModels"] == nil, "\(name): the request itself never carries registeredModels")
        }
        let codex = try JSONDecoder().decode(RemoteWireStart.self, from: Self.data("start-codex.json"))
        #expect(codex.request.provider == "codex" && codex.request.settings.permissionMode == "onRequest" && codex.request.settings.fastMode
                && codex.request.settings.webSearch == "live" && codex.request.settings.networkAccess && codex.registeredModels.isEmpty)
        let encoded = try #require(JSONSerialization.jsonObject(with: encoder.encode(codex)) as? [String: Any])
        let request = try #require(encoded["request"] as? [String: Any])
        #expect(request["attachments"] == nil && request["resumeId"] == nil, "an empty attachment list and no resume id are left out")
        #expect((encoded["registeredModels"] as? [Any])?.isEmpty == true, "no registered models is an empty array")
    }

    /// Files the index does not name as a route's body: other bodies of the same routes.
    static let auxiliary: Set<String> = ["endpoints.json", "info-darwin.json", "start-codex.json", "poll-gap.json", "poll-malformed.json", "poll-requests.json",
                                         "plan-approved.json", "answer-declined.json", "graph-run-event.json", "state-references.json"]

    @Test func theIndexNamesEveryRouteAndEveryFileIsAccountedFor() throws {
        let index = try #require(JSONSerialization.jsonObject(with: Self.data("endpoints.json")) as? [[String: Any]])
        #expect(Set(index.compactMap { $0["name"] as? String }) == ["info", "start", "events", "stop", "steer", "permission", "questions", "plan", "refused"])
        var named = Set<String>()
        for entry in index {
            #expect((entry["path"] as? String)?.hasPrefix(RemoteProtocol.pathPrefix) == true)
            for key in ["request", "response"] { if let name = entry[key] as? String { named.insert(name); #expect(FileManager.default.fileExists(atPath: Self.folder.appendingPathComponent(name).path), "\(name)") } }
        }
        let files = Set(try FileManager.default.contentsOfDirectory(atPath: Self.folder.path))
        #expect(named.isDisjoint(with: Self.auxiliary))
        #expect(files == named.union(Self.auxiliary), "unaccounted: \(files.symmetricDifference(named.union(Self.auxiliary)).sorted())")
    }

    @Test func theMacAcceptsEverySampleEventAndDropsOnlyTheMalformedOne() throws {
        let info = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.data("info.json"))
        try RemoteWorkspaceValidation.info(info)
        #expect(info.runtime.platform == "win32")
        let darwin = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.data("info-darwin.json"))
        try RemoteWorkspaceValidation.info(darwin)
        #expect(darwin.runtime.platform == "darwin")
        for name in ["poll.json", "poll-gap.json", "poll-requests.json"] {
            let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data(name))
            for item in poll.events {
                #expect(RemoteWorkspaceValidation.event(item.event, sessionId: "job-1"), "\(name) cursor \(item.cursor)")
                // Usage and graph pass validation by default; the client's own normalizing is what keeps or drops them.
                #expect(RemoteClientService.local(item.event, pane: "pane-1")?.sessionId == "pane-1", "\(name) cursor \(item.cursor) reaches the pane")
            }
        }
        let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll.json"))
        #expect(poll.events.filter { ["usage", "graph"].contains($0.event.type) }.count == 2)
        let malformed = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll-malformed.json"))
        #expect(malformed.events.map { RemoteWorkspaceValidation.event($0.event, sessionId: "job-1") } == [true, false, true])
        // The Windows-only graph_run event is never one a Mac client takes; Windows hosts send it only on request.
        let graphRun = try #require(JSONSerialization.jsonObject(with: Self.data("graph-run-event.json")) as? [String: Any])
        let event = try JSONDecoder().decode(RunEvent.self, from: JSONSerialization.data(withJSONObject: graphRun["event"]!))
        #expect(!RemoteWorkspaceValidation.event(event, sessionId: "job-1"))
    }

    /// The Mac writes log, usage and graph times in whole seconds and a request's
    /// receivedAt with milliseconds; the samples carry both, and both are read.
    @Test func timestampsComeInBothForms() throws {
        let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll.json"))
        let gap = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll-gap.json"))
        let usage = try #require(poll.events.compactMap(\.event.usage).first), graph = try #require(poll.events.compactMap(\.event.graph).first)
        let log = try #require(poll.events.compactMap(\.event.entry).first), gapLog = try #require(gap.events.compactMap(\.event.entry).first)
        #expect(usage.updatedAt == "2026-10-17T01:02:04Z" && graph.updatedAt == "2026-10-17T01:02:10Z" && gapLog.timestamp == "2026-10-17T01:03:00Z")
        #expect(log.timestamp == "2026-10-17T01:02:03.456Z")
        for text in [usage.updatedAt, graph.updatedAt, log.timestamp, gapLog.timestamp] { #expect(AgentRunTiming.parseTimestamp(text) != nil, "\(text)") }
        #expect(SessionUsageSupport.normalized(usage)?.updatedAt == usage.updatedAt && ExecutionGraphSupport.normalized(graph)?.updatedAt == graph.updatedAt)
    }

    /// The requests the answer endpoints settle: questions.json answers perm-2,
    /// the plan samples answer perm-3, and perm-4 carries a reason and a blocked path.
    @Test func theAnswerSamplesAnswerTheSampleRequests() throws {
        let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll-requests.json"))
        let requests = poll.events.compactMap(\.event.permission)
        #expect(requests.map(\.id) == ["perm-2", "perm-3", "perm-4"])
        let ask = requests[0], plan = requests[1], blocked = requests[2]
        let answers = try JSONDecoder().decode(RemoteWireQuestions.self, from: Self.data("questions.json"))
        #expect(ask.toolName == "AskUserQuestion" && ask.canAnswerQuestions && !ask.canAllow && answers.requestId == ask.id && answers.runId == ask.runId)
        let questionnaire = try #require(ask.questionnaire)
        #expect(try questionnaire.validatedAnswers(answers.answers) == ["Which file?": "README.md, and the docs"])
        #expect(plan.toolName == ClaudePlanMode.toolName && plan.canAnswerPlan && plan.plan == "# Plan\n1. Read")
        for name in ["plan-approved.json", "plan-revised.json"] {
            let answer = try JSONDecoder().decode(RemoteWirePlan.self, from: Self.data(name))
            #expect(answer.requestId == plan.id && answer.runId == plan.runId && answer.decision != nil, "\(name)")
        }
        #expect(blocked.reason?.isEmpty == false && blocked.blockedPath?.isEmpty == false && !blocked.canAnswerQuestions && !blocked.canAnswerPlan)
        let permission = try JSONDecoder().decode(RemoteWirePermission.self, from: Self.data("permission.json"))
        #expect(permission.requestId == "perm-1", "permission.json answers poll.json's tool request")
    }

    @Test func savedReferencesAreKeptOnlyAtVersionTwo() throws {
        let file = try #require(JSONSerialization.jsonObject(with: Self.data("state-references.json")) as? [String: Any])
        let cases = try #require(file["cases"] as? [[String: Any]])
        #expect(cases.count == 12)
        let kept = Dictionary(uniqueKeysWithValues: cases.compactMap { item in (item["name"] as? String).map { ($0, item["kept"] as? Bool) } })
        #expect(kept["version 2.0 is the number 2"] == true && kept["version null"] == false && kept["a remote that is an array"] == false)
        for item in cases {
            #expect(StateRepository.keepsWorkspace(item["workspace"]!) == (item["kept"] as? Bool), "\(item["name"] ?? "")")
        }
    }
}
