import Foundation
import Testing
@testable import MightyCore

/// The same file `mobile/src/__tests__/next-actions.test.ts` reads: both clients
/// must turn every breadcrumb into the same buttons.
struct NextActionsTests {
    struct Case: Decodable { let line: String; let options: [Option] }
    struct Option: Decodable { let label: String; let fill: String }

    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/next-actions.json")
    }()

    static func cases() throws -> [Case] { try JSONDecoder().decode([Case].self, from: Data(contentsOf: fixture)) }

    @Test func everyFixtureLineParsesAsCommitted() throws {
        let cases = try Self.cases()
        #expect(cases.count >= 20)
        #expect(cases.contains { $0.options.isEmpty })
        #expect(cases.contains { $0.options.count == NextActions.maximum })
        for item in cases {
            let parsed = NextActions.parse(item.line)
            #expect(parsed == item.options.map { NextAction(label: $0.label, fill: $0.fill) }, "\(item.line)")
        }
    }

    @Test func offersTheLastReplyOnlyWhileNoUserMessageFollows() {
        let reply = LogEntry(id: "a1", kind: "assistant", text: "끝\n◆ 완료 → next: `ooo run` 또는 다듬기")
        let tool = LogEntry(id: "s1", kind: "system", text: "Read")
        let latest = NextActions.latest(in: [reply, tool])
        #expect(latest?.entryId == "a1")
        #expect(latest?.actions == [NextAction(label: "`ooo run`", fill: "ooo run"), NextAction(label: "다듬기", fill: "다듬기")])
        #expect(NextActions.latest(in: [reply, LogEntry(id: "u1", kind: "user", text: "ooo run")]) == nil)
        #expect(NextActions.latest(in: [reply, LogEntry(id: "a2", kind: "assistant", text: "다른 답")]) == nil)
        #expect(NextActions.latest(in: []) == nil)
    }

    @Test func showsLabelsWithoutBackticksKeepingOneThatWouldBeBlank() {
        #expect(NextAction(label: "완료 후 `ooo run`", fill: "ooo run").displayLabel == "완료 후 ooo run")
        #expect(NextAction(label: "``", fill: "``").displayLabel == "``")
    }

    @Test func fillingNeverLosesTheDraft() {
        #expect(NextActions.insertion(into: "", fill: "ooo run") == (true, "ooo run"))
        #expect(NextActions.insertion(into: " \n\u{3000}", fill: "ooo run") == (true, "ooo run"))
        #expect(NextActions.insertion(into: "먼저 확인", fill: "ooo run") == (false, "\nooo run"))
        #expect(NextActions.insertion(into: "먼저 확인\n", fill: "ooo run") == (false, "ooo run"))
        #expect(NextActions.insertion(into: "먼저 확인\r\n", fill: "ooo run") == (false, "ooo run"))
        #expect(NextActions.insertion(into: "먼저 확인\n\n", fill: "ooo run") == (false, "ooo run"))
        #expect(NextActions.insertion(into: "먼저 확인 ", fill: "ooo run") == (false, "\nooo run"))
    }
}
