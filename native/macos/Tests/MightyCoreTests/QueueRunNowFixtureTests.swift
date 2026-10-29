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
            #expect(Set(row.keys) == ["heldIndex", "text", "attachments", "continuation"], "\(name): row fields")
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
            let path = QueueRunNow.decide(heldIndex: row["heldIndex"] as? Int, text: row["text"] as? String ?? "", attachments: row["attachments"] as? Int ?? 0,
                                          continuation: row["continuation"] as? Bool ?? false, pane: pane)
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

    /// An allowed Antigravity card's follow-up belongs to this pane's next run: never a worktree child, the agent menu or a
    /// steer, whatever the pane could do for another row; here once the pane is idle.
    @Test func anAntigravityCardContinuationNeverLeavesItsPane() {
        let continuation = QueuedInput(text: "Allowed: go on", antigravityGrant: AntigravityAllowGrant(requestId: "agy-1", rules: ["command(date)"], temporary: true))
        #expect(continuation.isContinuation && !QueuedInput(text: "Plain").isContinuation)
        func pane(busy: Bool = true, second: ComposerSecondSend = .queue, menuOnly: Bool = false, waits: Bool = false) -> QueueRunNow.Pane {
            QueueRunNow.Pane(busy: busy, heldForUpdate: false, secondSend: second, menuOnly: menuOnly, waitsOnBackgroundOnly: waits, held: 0)
        }
        for busyPane in [pane(second: .worktree), pane(menuOnly: true), pane(waits: true), pane()] {
            #expect(QueueRunNow.decide(heldIndex: nil, text: continuation.text, attachments: 0, continuation: true, pane: busyPane) == .waitsForTurn)
        }
        // The same words without the card take the worktree, the menu and the steer.
        #expect(QueueRunNow.decide(heldIndex: nil, text: continuation.text, attachments: 0, pane: pane(second: .worktree)) == .worktree)
        #expect(QueueRunNow.decide(heldIndex: nil, text: continuation.text, attachments: 0, pane: pane(menuOnly: true)) == .agentMenu)
        #expect(QueueRunNow.decide(heldIndex: nil, text: continuation.text, attachments: 0, pane: pane(waits: true)) == .steer)
        #expect(QueueRunNow.decide(heldIndex: nil, text: continuation.text, attachments: 0, continuation: true, pane: pane(busy: false, second: .worktree)) == .here)
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

    /// A steer into a running notice turn would lose its answer: that turn's result ends the steer's turn too, and the steer's own
    /// result then replaces the request's. While a notice run after the newest request runs, its rows wait for their turn.
    @Test func aRunningNoticeTurnTakesNoSteer() {
        let request = MightyGraphRun(id: "r", status: "completed")
        let running = MightyGraphRun(id: "r" + BackgroundNoticeRuns.marker + "1", status: "running", noticeRun: true)
        let answered = MightyGraphRun(id: "r" + BackgroundNoticeRuns.marker + "1", status: "completed", finalOutput: "Done.", noticeRun: true)
        #expect(MightyGraphSupport.noticeTurnRunning([request, running]))
        #expect(!MightyGraphSupport.noticeTurnRunning([request, answered]))
        #expect(!MightyGraphSupport.noticeTurnRunning([request]) && !MightyGraphSupport.noticeTurnRunning([]))
        // An older request's notice run left running never holds the newest request's queue.
        #expect(!MightyGraphSupport.noticeTurnRunning([request, running, MightyGraphRun(id: "r2", status: "completed")]))
        // The pane reads it as not only waiting on background work: the row waits for its turn instead of steering.
        let pane = QueueRunNow.Pane(busy: true, heldForUpdate: false, secondSend: .queue, menuOnly: false,
                                    waitsOnBackgroundOnly: !MightyGraphSupport.noticeTurnRunning([request, running]), held: 0)
        #expect(QueueRunNow.decide(heldIndex: nil, text: "Next", attachments: 0, pane: pane) == .waitsForTurn)
    }
}
