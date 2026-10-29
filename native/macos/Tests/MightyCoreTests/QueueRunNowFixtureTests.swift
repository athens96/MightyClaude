import Foundation
import Testing
@testable import MightyCore

/// The file Windows Core and the phone read too: what 바로 실행 on a queued row does.
@Suite struct QueueRunNowFixtureTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/queue-run-now.json")
    }()

    @Test func everyCaseDecidesAsTheFixtureSays() throws {
        let root = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: Self.url)) as? [String: Any])
        let cases = try #require(root["cases"] as? [[String: Any]])
        #expect(cases.count >= 18)
        var seen = Set<String>()
        for item in cases {
            let name = item["name"] as? String ?? ""
            let row = try #require(item["row"] as? [String: Any]), p = try #require(item["pane"] as? [String: Any])
            // Every case lists every field, so no reader ever falls back on a default.
            #expect(Set(row.keys) == ["heldIndex", "text", "attachments"], "\(name): row fields")
            #expect(Set(p.keys) == ["busy", "heldForUpdate", "kind", "provider", "mode", "isChild", "delegationOn", "canHostWorktree", "otherAgentReady",
                                    "waitsOnBackgroundOnly", "held"], "\(name): pane fields")
            #expect(item.keys.contains("wire"), "\(name): wire")
            func flag(_ key: String) -> Bool { p[key] as? Bool ?? false }
            let kind = p["kind"] as? String ?? "", provider = p["provider"] as? String ?? "", mode = p["mode"] as? String ?? ""
            let second = ComposerSecondSend.choose(kind: kind, provider: provider, mode: mode, isChild: flag("isChild"), delegationOn: flag("delegationOn"),
                                                   canHostWorktree: flag("canHostWorktree"))
            let choices = flag("otherAgentReady") ? [DelegationChildChoice(agent: provider == "claude" ? "codex" : "claude", mode: "acceptEdits", blocked: nil)] : []
            let menuOnly = ComposerSecondSend.menuOnly(kind: kind, provider: provider, mode: mode, isChild: flag("isChild"), delegationOn: flag("delegationOn"),
                                                       canHostWorktree: flag("canHostWorktree"), choices: choices)
            let pane = QueueRunNow.Pane(busy: flag("busy"), heldForUpdate: flag("heldForUpdate"), secondSend: second, menuOnly: menuOnly,
                                        waitsOnBackgroundOnly: flag("waitsOnBackgroundOnly"), held: p["held"] as? Int ?? 0)
            let path = QueueRunNow.decide(heldIndex: row["heldIndex"] as? Int, text: row["text"] as? String ?? "", attachments: row["attachments"] as? Int ?? 0, pane: pane)
            #expect(path.rawValue == item["expect"] as? String, "\(name)")
            seen.insert(path.rawValue)
            let wire = item["wire"] as? [String: String]
            #expect(path.wire?.runNow == wire?["runNow"] && path.wire?.reason == wire?["runNowReason"], "\(name): wire")
            // Only an unavailable row says why.
            #expect((wire?["runNowReason"] != nil) == (wire?["runNow"] == "unavailable"), "\(name): reason")
        }
        // Every answer has a case.
        #expect(seen == Set(QueueRunNow.allCases.map(\.rawValue)))
    }

    @Test func onlyTheWorktreeAgentMenuHereSteerAndHeldAnswersRun() {
        #expect(QueueRunNow.allCases.filter(\.runs) == [.worktree, .agentMenu, .here, .steer, .held])
        #expect(QueueRunNow.hidden.wire == nil)
    }

    @Test func thePhonesRowsCarryTheirRunNowAndReasonAndLeaveThemOutWhenHidden() throws {
        let held = [DelegationDeliveryItem(id: "n-1", kind: .notice, childId: "c1", sessionId: "parent", text: "Notice 1"),
                    DelegationDeliveryItem(id: "n-2", kind: .notice, childId: "c2", sessionId: "parent", text: "Notice 2")]
        let queued = [QueuedInput(id: "q-1", text: "Human row")]
        let items = DelegationQueueRows.mobileItems(held: held, queued: queued) { $0 == "n-1" ? .held : $0 == "n-2" ? .hidden : .waitsForHeld }
        #expect(items.map(\.id) == ["n-1", "n-2", "q-1"])
        #expect(items.map(\.runNow) == ["here", nil, "unavailable"])
        #expect(items.map(\.runNowReason) == [nil, nil, "held"])
        let hidden = try #require(try JSONSerialization.jsonObject(with: JSONEncoder().encode(items[1])) as? [String: Any])
        #expect(Set(hidden.keys) == ["id", "text"])
        let unavailable = try #require(try JSONSerialization.jsonObject(with: JSONEncoder().encode(items[2])) as? [String: Any])
        #expect(unavailable["runNow"] as? String == "unavailable" && unavailable["runNowReason"] as? String == "held")
        let here = try #require(try JSONSerialization.jsonObject(with: JSONEncoder().encode(items[0])) as? [String: Any])
        #expect(Set(here.keys) == ["id", "text", "runNow"])
    }
}
