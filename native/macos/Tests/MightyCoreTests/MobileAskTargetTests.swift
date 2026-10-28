import Foundation
import Testing
@testable import MightyCore

/// The phone's view of the composer "send to" switch: the session detail carries
/// the pane's targets and its current one, and neither field when it offers none.
struct MobileAskTargetTests {
    private let summary = MobileSessionSummary(id: "s1", workspaceId: "w1", title: "Claude", kind: "claude", provider: "claude", model: "default",
                                               status: "idle", revision: 1, updatedAt: "2026-10-28T00:00:00Z")

    private func encoded(_ detail: MobileSessionDetail) throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(detail)) as? [String: Any])
    }

    @Test func aPaneWithTargetsSendsThemInOrderWithItsChoice() throws {
        let targets = AskTargets.all.prefix(2).map { MobileAskTarget(id: $0.id, label: $0.label) }
        let json = try encoded(MobileSessionDetail(revision: 1, session: summary, entries: [], askTargets: targets, askTarget: "antigravity"))
        let wire = try #require(json["askTargets"] as? [[String: String]])
        #expect(wire == [["id": "codex", "label": "Codex"], ["id": "antigravity", "label": "Antigravity"]])
        #expect(json["askTarget"] as? String == "antigravity")
        let decoded = try JSONDecoder().decode(MobileSessionDetail.self, from: JSONEncoder().encode(MobileSessionDetail(revision: 1, session: summary, entries: [], askTargets: targets, askTarget: "codex")))
        #expect(decoded.askTargets == targets && decoded.askTarget == "codex")
    }

    @Test func claudeLeavesTheChoiceOutAndNoTargetsLeaveBothOut() throws {
        let targets = [MobileAskTarget(id: "codex", label: "Codex")]
        let claude = try encoded(MobileSessionDetail(revision: 1, session: summary, entries: [], askTargets: targets, askTarget: nil))
        #expect(claude["askTargets"] != nil && claude["askTarget"] == nil)
        for detail in [MobileSessionDetail(revision: 1, session: summary, entries: []),
                       MobileSessionDetail(revision: 1, session: summary, entries: [], askTargets: [], askTarget: "codex")] {
            let json = try encoded(detail)
            #expect(json["askTargets"] == nil && json["askTarget"] == nil)
        }
        // An older host's detail reads as no switch.
        let old = try JSONDecoder().decode(MobileSessionDetail.self, from: JSONEncoder().encode(MobileSessionDetail(revision: 1, session: summary, entries: [])))
        #expect(old.askTargets == nil && old.askTarget == nil)
    }

    @Test func theSetAskTargetBodyReadsATargetOrNull() throws {
        #expect(try JSONDecoder().decode(MobileSetAskTargetRequest.self, from: Data(#"{"target":"codex"}"#.utf8)).target == "codex")
        #expect(try JSONDecoder().decode(MobileSetAskTargetRequest.self, from: Data(#"{"target":null}"#.utf8)).target == nil)
    }
}
