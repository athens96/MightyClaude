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
        #expect(try Self.roundTrips("start.json", RemoteWireStart.self))
        #expect(try Self.roundTrips("start-accepted.json", RemoteWireJob.self))
        #expect(try Self.roundTrips("poll.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-gap.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-malformed.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("steer.json", RemoteWireSteer.self))
        #expect(try Self.roundTrips("permission.json", RemoteWirePermission.self))
        #expect(try Self.roundTrips("questions.json", RemoteWireQuestions.self))
        #expect(try Self.roundTrips("plan-approved.json", RemoteWirePlan.self))
        #expect(try Self.roundTrips("plan-revised.json", RemoteWirePlan.self))
        #expect(try Self.roundTrips("answer.json", RemoteWireAnswer.self))
        let error = try JSONSerialization.jsonObject(with: Self.data("error.json")) as? [String: Any]
        #expect(error?["protocol"] as? Int == RemoteProtocol.version && error?["error"] is String)
        #expect((try JSONSerialization.jsonObject(with: Self.data("stop.json")) as? [String: Any])?.isEmpty == true)
    }

    @Test func theIndexNamesEveryRouteAndSample() throws {
        let index = try #require(JSONSerialization.jsonObject(with: Self.data("endpoints.json")) as? [[String: Any]])
        #expect(Set(index.compactMap { $0["name"] as? String }) == ["info", "start", "events", "stop", "steer", "permission", "questions", "plan", "refused"])
        for entry in index {
            #expect((entry["path"] as? String)?.hasPrefix(RemoteProtocol.pathPrefix) == true)
            for key in ["request", "response"] { if let name = entry[key] as? String { #expect(FileManager.default.fileExists(atPath: Self.folder.appendingPathComponent(name).path), "\(name)") } }
        }
    }

    @Test func theMacAcceptsEverySampleEventAndDropsOnlyTheMalformedOne() throws {
        let info = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.data("info.json"))
        try RemoteWorkspaceValidation.info(info)
        #expect(info.runtime.platform == "win32")
        for name in ["poll.json", "poll-gap.json"] {
            let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data(name))
            for item in poll.events { #expect(RemoteWorkspaceValidation.event(item.event, sessionId: "job-1"), "\(name) cursor \(item.cursor)") }
        }
        let malformed = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll-malformed.json"))
        #expect(malformed.events.map { RemoteWorkspaceValidation.event($0.event, sessionId: "job-1") } == [true, false, true])
        // The Windows-only graph_run event is never one a Mac client takes; Windows hosts send it only on request.
        let graphRun = try #require(JSONSerialization.jsonObject(with: Self.data("graph-run-event.json")) as? [String: Any])
        let event = try JSONDecoder().decode(RunEvent.self, from: JSONSerialization.data(withJSONObject: graphRun["event"]!))
        #expect(!RemoteWorkspaceValidation.event(event, sessionId: "job-1"))
    }

    @Test func savedReferencesAreKeptOnlyAtVersionTwo() throws {
        let file = try #require(JSONSerialization.jsonObject(with: Self.data("state-references.json")) as? [String: Any])
        let cases = try #require(file["cases"] as? [[String: Any]])
        #expect(cases.count == 9)
        for item in cases {
            #expect(StateRepository.keepsWorkspace(item["workspace"]!) == (item["kept"] as? Bool), "\(item["name"] ?? "")")
        }
    }
}
