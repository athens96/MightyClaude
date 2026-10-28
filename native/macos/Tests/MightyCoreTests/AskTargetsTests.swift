import Foundation
import Testing
@testable import MightyCore

/// The composer "send to" rules the Windows Core and the phone read from the same file:
/// the target list, which targets a pane offers, which one a send uses, and the text it sends.
struct AskTargetsTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/ask-targets.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    static func cases(_ key: String) throws -> [[String: Any]] {
        let cases = try #require(try load()[key] as? [[String: Any]], "\(key)")
        #expect(!cases.isEmpty, "\(key) has no cases")
        return cases
    }

    static func optional<T>(_ value: Any?, as type: T.Type) -> T? { value is NSNull ? nil : value as? T }

    static func target(_ id: String?) throws -> AskTarget? {
        guard let id else { return nil }
        return try #require(AskTargets.all.first { $0.id == id }, "\(id)")
    }

    @Test func targetsAndSkillMatchTheFixture() throws {
        let fixture = try Self.load()
        #expect(fixture["skill"] as? String == AskTargets.skill)
        let targets = try #require(fixture["targets"] as? [[String: String]])
        #expect(targets.map { $0["id"] } == AskTargets.all.map(\.id))
        #expect(targets.map { $0["label"] } == AskTargets.all.map(\.label))
        #expect(targets.map { $0["executable"] } == AskTargets.all.map(\.executable))
    }

    @Test func everyAvailableCase() throws {
        for entry in try Self.cases("available") {
            let name = entry["name"] as? String ?? "unnamed"
            let slash = Set(try #require(entry["slash"] as? [String], "\(name)"))
            let installed = Set(try #require(entry["installed"] as? [String], "\(name)"))
            let result = AskTargets.available(isClaudeAgent: entry["isClaudeAgent"] as? Bool ?? false, slashInvocations: slash, installed: installed)
            #expect(result.map(\.id) == (entry["expect"] as? [String]), "\(name)")
        }
    }

    @Test func everyEffectiveCase() throws {
        for entry in try Self.cases("effective") {
            let name = entry["name"] as? String ?? "unnamed"
            let ids = try #require(entry["available"] as? [String], "\(name)")
            let available = AskTargets.all.filter { ids.contains($0.id) }
            let result = AskTargets.effective(saved: Self.optional(entry["saved"], as: String.self), available: available)
            #expect(result?.id == Self.optional(entry["expect"], as: String.self), "\(name)")
        }
    }

    @Test func everyWrapCase() throws {
        for entry in try Self.cases("wrap") {
            let name = entry["name"] as? String ?? "unnamed"
            let target = try Self.target(Self.optional(entry["target"], as: String.self))
            let text = try #require(entry["text"] as? String, "\(name)")
            #expect(AskTargets.wrap(text, target: target) == Self.optional(entry["expect"], as: String.self), "\(name)")
        }
    }

    @Test func askTargetRoundTripsAndOldSavesReadNil() throws {
        let session = RunSession(workspaceId: "w", title: "t", askTarget: "codex")
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(decoded.askTarget == "codex")
        var object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        object.removeValue(forKey: "askTarget")
        let old = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(old.askTarget == nil)
    }
}
