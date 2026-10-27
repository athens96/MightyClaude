import Foundation
import Testing
@testable import MightyCore

/// The natural submit rules the Windows Core, the Windows page (vitest) and the phone (jest)
/// read from the same file: when the pending area shows (`display`), how a submit's answer
/// reads (`submitResponses`), and where a view moves when a request block appears
/// (`scrollTargets`).
struct PendingTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/pending.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    static func cases(_ key: String) throws -> [[String: Any]] {
        let cases = try #require(try load()[key] as? [[String: Any]], "\(key)")
        #expect(!cases.isEmpty, "\(key) has no cases")
        return cases
    }

    /// A fixture value that may be JSON null.
    static func optional<T>(_ value: Any?, as type: T.Type) -> T? { value is NSNull ? nil : value as? T }

    // MARK: display

    @Test func everyDisplayCaseInMilliseconds() throws {
        for entry in try Self.cases("display") {
            let name = entry["name"] as? String ?? "unnamed"
            let startedAt = try #require(entry["startedAt"] as? Double, "\(name)")
            let now = try #require(entry["now"] as? Double, "\(name)")
            let endedAt = Self.optional(entry["endedAt"], as: Double.self)
            let failed = entry["failed"] as? Bool ?? false
            let expected = try #require(entry["expectVisible"] as? Bool, "\(name)")
            #expect(PendingDisplay.visible(startedAt: startedAt, endedAt: endedAt, now: now, failed: failed) == expected, "\(name)")
        }
    }

    /// The same cases through `PendingState` on real dates, start times far from zero
    /// included: what the views call.
    @Test(arguments: [0.0, 815_000_000.123])
    func everyDisplayCaseAsAState(offset: Double) throws {
        for entry in try Self.cases("display") {
            let name = entry["name"] as? String ?? "unnamed"
            func date(_ milliseconds: Double) -> Date { Date(timeIntervalSinceReferenceDate: offset + milliseconds / 1000) }
            let since = date(try #require(entry["startedAt"] as? Double, "\(name)"))
            let now = date(try #require(entry["now"] as? Double, "\(name)"))
            let at = Self.optional(entry["endedAt"], as: Double.self).map(date)
            let failed = entry["failed"] as? Bool ?? false
            let expected = try #require(entry["expectVisible"] as? Bool, "\(name)")
            let state: PendingState = if failed {
                .failed("오류", since: since, at: at)
            } else if let at {
                .done(since: since, at: at)
            } else {
                .pending(since: since)
            }
            let indicator = PendingDisplay.indicator(state, now: now)
            #expect(PendingDisplay.visible(state, now: now) == expected, "\(name) at \(offset)")
            #expect((indicator != .hidden) == expected, "\(name) at \(offset)")
            // A failure shows its message from its end on, loading before.
            if failed, let at {
                let shown: PendingIndicator = now >= at ? .failed("오류") : (expected ? .loading : .hidden)
                #expect(indicator == shown, "\(name) at \(offset)")
            } else {
                #expect(indicator != PendingIndicator.failed("오류"), "\(name) at \(offset)")
            }
        }
    }

    @Test func idleShowsNothingAndAFailureWithoutAnEndFailsNow() {
        let since = Date(timeIntervalSinceReferenceDate: 1000)
        #expect(PendingDisplay.indicator(.idle, now: since.addingTimeInterval(5)) == .hidden)
        #expect(PendingDisplay.indicator(.failed("끊김", since: since, at: nil), now: since.addingTimeInterval(0.01)) == .failed("끊김"))
        #expect(PendingDisplay.visible(startedAt: 0, endedAt: nil, now: 10, failed: true))
    }

    /// The rule reads the motion tokens, the numbers the fixture's cases were written for.
    @Test func theRuleUsesTheMotionTokens() throws {
        let motion = try #require((try JSONSerialization.jsonObject(with: Data(contentsOf: DesignTokenParityTests.fixture)) as? [String: Any])?["motion"] as? [String: Any])
        #expect(motion["pendingShowAfter"] as? Int == DesignMetrics.Motion.pendingShowAfter)
        #expect(motion["pendingMinVisible"] as? Int == DesignMetrics.Motion.pendingMinVisible)
        // Other numbers move the boundaries with them.
        #expect(!PendingDisplay.visible(startedAt: 0, endedAt: nil, now: 150, showAfter: 200))
        #expect(PendingDisplay.visible(startedAt: 0, endedAt: 250, now: 500, showAfter: 200, minVisible: 300))
        #expect(!PendingDisplay.visible(startedAt: 0, endedAt: 250, now: 501, showAfter: 200, minVisible: 300))
    }

    // MARK: submitResponses

    @Test func everySubmitResponseReadsAsTheFixtureSays() throws {
        for entry in try Self.cases("submitResponses") {
            let name = entry["name"] as? String ?? "unnamed"
            let parsed = try #require(entry["parsed"] as? [String: Any], "\(name)")
            let raw: Any? = entry["raw"] is NSNull ? nil : entry["raw"]
            let accepted = try #require(parsed["accepted"] as? Bool, "\(name)")
            let word = Self.optional(parsed["outcome"], as: String.self)
            let outcome = word.flatMap(SubmitOutcome.init(rawValue:))
            #expect(word == nil || outcome != nil, "\(name): \(word ?? "")")
            let expected = SubmitResponse(accepted: accepted, entryId: Self.optional(parsed["entryId"], as: String.self),
                                          runId: Self.optional(parsed["runId"], as: String.self), outcome: outcome)
            #expect(SubmitResponse.parse(raw) == expected, "\(name)")
            // The same body as bytes, as it comes off the wire.
            let data = try JSONSerialization.data(withJSONObject: entry["raw"] ?? NSNull(), options: [.fragmentsAllowed])
            #expect(SubmitResponse.parse(data: data) == expected, "\(name) as data")
        }
    }

    @Test func aNumberIsNotTheBooleanTrueAndGarbageIsNotAccepted() {
        #expect(SubmitResponse.parse(["accepted": 1]) == SubmitResponse(accepted: false))
        #expect(SubmitResponse.parse(["accepted": "dropped"]) == SubmitResponse(accepted: false))
        #expect(SubmitResponse.parse(data: Data("not json".utf8)) == SubmitResponse(accepted: false))
        #expect(SubmitResponse.parse(["accepted": true]).accepted)
    }

    /// What the Mac host now answers reads back as its receipt, and the old word stays for
    /// older phones.
    @Test func theMacHostsAnswerReadsBack() throws {
        let receipt = MobileSubmitReceipt(outcome: "started", entryId: "entry-1", runId: "entry-1")
        let body = try JSONSerialization.jsonObject(with: JSONEncoder().encode(MobileSubmitResult(receipt))) as? [String: Any]
        #expect(body?["protocol"] as? Int == 1)
        #expect(body?["accepted"] as? String == "started")
        #expect(SubmitResponse.parse(body) == SubmitResponse(accepted: true, entryId: "entry-1", runId: "entry-1", outcome: .started))
        // Queued: no ids, and no null fields on the wire.
        let queued = try JSONSerialization.jsonObject(with: JSONEncoder().encode(MobileSubmitResult(MobileSubmitReceipt(outcome: "queued")))) as? [String: Any]
        #expect(Set(queued?.keys.map { $0 } ?? []) == ["protocol", "accepted", "outcome"])
        #expect(SubmitResponse.parse(queued) == SubmitResponse(accepted: true, outcome: .queued))
        // The old shape still encodes as before.
        let old = try JSONSerialization.jsonObject(with: JSONEncoder().encode(MobileSubmitResult(accepted: "queued"))) as? [String: Any]
        #expect(Set(old?.keys.map { $0 } ?? []) == ["protocol", "accepted"])
    }

    // MARK: scrollTargets

    @Test func everyScrollTargetIsTheFixtures() throws {
        for entry in try Self.cases("scrollTargets") {
            let name = entry["name"] as? String ?? "unnamed"
            let input = try #require(entry["input"] as? [String: Any], "\(name)")
            let expect = try #require(entry["expect"] as? [String: Any], "\(name)")
            let view = try #require((input["view"] as? String).flatMap(SubmitScroll.View.init(rawValue:)), "\(name)")
            let board = try #require(input["chosenBoard"] as? String, "\(name)")
            #expect(["newest", "older"].contains(board), "\(name)")
            let decision = SubmitScroll.target(view: view, own: try #require(input["own"] as? Bool, "\(name)"),
                                               chosenBoardIsNewest: board == "newest", nearBottom: try #require(input["nearBottom"] as? Bool, "\(name)"))
            let scroll = try #require((expect["scroll"] as? String).flatMap(SubmitScroll.Target.init(rawValue:)), "\(name)")
            #expect(decision == SubmitScroll.Decision(scroll: scroll, resetBoard: try #require(expect["resetBoard"] as? Bool, "\(name)")), "\(name)")
        }
    }

    @Test func theFixtureCoversEveryView() throws {
        let views = Set(try Self.cases("scrollTargets").compactMap { ($0["input"] as? [String: Any])?["view"] as? String })
        #expect(views == Set(SubmitScroll.View.allCases.map(\.rawValue)))
    }

    @Test func aPanesViewModeIsItsScrollView() {
        // Not showing the Mighty view is the default transcript, whatever mode it saved.
        for mode in MightyGraphViewMode.allCases {
            #expect(SubmitScroll.View(showsMighty: false, mightyViewMode: mode) == .basic)
        }
        #expect(SubmitScroll.View(showsMighty: true, mightyViewMode: .diagram) == .diagram)
        #expect(SubmitScroll.View(showsMighty: true, mightyViewMode: .timeline) == .timeline)
        #expect(SubmitScroll.View(showsMighty: true, mightyViewMode: .structure) == .pipeline)
        // An older board means nothing outside the 파이프라인.
        #expect(SubmitScroll.target(view: .basic, own: false, chosenBoardIsNewest: false, nearBottom: true) == SubmitScroll.Decision(scroll: .follow, resetBoard: false))
        #expect(SubmitScroll.target(view: .timeline, own: true, chosenBoardIsNewest: false, nearBottom: false) == SubmitScroll.Decision(scroll: .toRequest, resetBoard: false))
    }

    @Test func aQueuedRequestRemembersWhetherItIsThisMacsOwn() {
        // Every existing caller queues the Mac's own request; the phone's says so, and keeps it when copied.
        #expect(QueuedInput(text: "next").own)
        let phone = QueuedInput(text: "from the phone", own: false)
        #expect(!phone.own)
        var moved = phone
        moved.text = "edited"
        #expect(!moved.own)
        #expect(QueuedInput(id: "a", text: "x", createdAt: "t") != QueuedInput(id: "a", text: "x", createdAt: "t", own: false))
    }
}
