import Foundation
import Testing
@testable import MightyCore

/// The 파이프라인 view's state colours (spec 2026-10-25): a status, a step, a column and a card
/// take one of concept D's tones, a current step takes its newest request's (amber while it
/// waits, green only once it and its sub-agents finished), and a step still to come takes none. Windows (`MightyStructureToneVerification`) and the phone
/// (`pipeline-tone.test.ts`) check the same cases.
struct MightyStructureToneTests {
    typealias T = MightyStructureTests
    typealias Tone = MightyStructure.Tone

    @Test func statusWordsTakeTheBlockBucketsDoneItsOwnAndPendingNone() {
        let words = ["running", "starting", "queued", "waiting", "completed", "done", "error", "failed", "stopped", "cancelled", "interrupted"]
        #expect(words.map(Tone.status) == [.run, .run, .run, .wait, .done, .done, .err, .err, .stop, .stop, .stop])
        #expect(Tone.status("pending") == nil)
    }

    @Test func stepsTakeTheirToneAndTheCurrentOneWaitsWithItsRequest() {
        #expect(Tone.step(.done) == .done)
        #expect(Tone.step(.current) == .run)
        #expect(Tone.step(.current, waiting: true) == .wait)
        #expect(Tone.step(.error) == .err)
        #expect(Tone.step(.stopped) == .stop)
        #expect(Tone.step(.pending) == nil)
        #expect(Tone.step(.pending, waiting: true) == nil)
        #expect(Tone.step(.done, waiting: true) == .done)
    }

    @Test func aFreeBoardTintsItsColumnsAndCards() throws {
        let board = try #require(MightyStructureBoardTests.boards([T.run("r1", status: "waiting", agents: [T.agent("a", status: "running")])]).first)
        // 요청 done, 실행 waits with its request, 결과 still to come.
        #expect(board.columns.map(Tone.column) == [.done, .wait, nil])
        let execution = board.columns[1]
        #expect(execution.cards.map { Tone.card($0, in: execution) } == [.wait, .run])
        // The 요청 and 결과 cards stand for their step.
        #expect(Tone.card(board.columns[0].cards[0], in: board.columns[0]) == .done)
        #expect(Tone.card(board.columns[2].cards[0], in: board.columns[2]) == nil)
        let ended = MightyStructureBoardTests.boards([T.run("r1", final: "끝"), T.run("r2", status: "failed"), T.run("r3", status: "cancelled"), T.run("r4", status: "running")])
        #expect(ended.map { $0.columns.map(Tone.column) } == [[.done, .done, .done], [.done, .err, .err], [.done, .stop, .stop], [.done, .run, nil]])
        #expect(ended.map { Tone.card($0.columns[2].cards[0], in: $0.columns[2]) } == [.done, .err, .stop, nil])
    }

    @Test func aPipelineTintsItsPhasesAndEachCardByItsOwnStatus() throws {
        let style = try T.style("ouroboros")
        let waiting = try #require(MightyStructureBoardTests.boards([
            T.run("r1", input: "ooo interview 시작", agents: [T.agent("x"), T.agent("y", parent: "x", status: "failed")]),
            T.run("r2", input: "ooo seed", status: "waiting"),
        ], style: style).first)
        #expect(waiting.columns.map(Tone.column) == [.done, .done, .wait, nil, nil, nil])
        let interview = waiting.columns[1]
        #expect(interview.cards.map { Tone.card($0, in: interview) } == [.done, .done, .err])
        let running = try #require(MightyStructureBoardTests.boards([T.run("r1", input: "ooo interview 시작"), T.run("r2", input: "ooo seed", status: "running")], style: style).first)
        #expect(running.columns.map(Tone.column) == [.done, .done, .run, nil, nil, nil])
    }

    @Test func aCurrentColumnTakesItsNewestRequestsStatus() throws {
        let style = try T.style("ouroboros")
        func seedColumn(_ status: String) throws -> DesignTone? {
            let board = try #require(MightyStructureBoardTests.boards([T.run("r1", input: "ooo interview 시작"), T.run("r2", input: "ooo seed", status: status)], style: style).first)
            #expect(board.columns[2].step.step == .current)
            return Tone.column(board.columns[2])
        }
        // The pipeline stands at 시드 with its request over: the step reads as that request ended.
        #expect(try seedColumn("completed") == .done)
        #expect(try seedColumn("stopped") == .stop)
        #expect(try seedColumn("cancelled") == .stop)
        #expect(try seedColumn("failed") == .err)
        // The pane stands at 실행 for the next request, which no request reached yet: the current step runs.
        let moved = try #require(MightyStructureBoardTests.boards([T.run("r1", input: "ooo interview 시작"), T.run("r2", input: "ooo seed")], style: style,
                                                                  phase: "run", startingNew: true).first)
        #expect(moved.columns[3].step.step == .current && moved.columns[3].cards.isEmpty)
        #expect(Tone.column(moved.columns[3]) == .run)
    }

    @Test func aCurrentColumnRunsUntilItsNewestRequestReallyFinished() throws {
        // The request reports completed, but a sub-agent still runs: not finished, so the step runs.
        let style = try T.style("ouroboros")
        let seed = try #require(MightyStructureBoardTests.boards([T.run("r1", input: "ooo interview 시작"),
                                                                  T.run("r2", input: "ooo seed", agents: [T.agent("a", status: "running")])], style: style).first)
        #expect(seed.columns[2].step.step == .current)
        #expect(Tone.column(seed.columns[2]) == .run)
        let free = try #require(MightyStructureBoardTests.boards([T.run("r1", agents: [T.agent("bg", status: "running", kind: "task")])]).first)
        #expect(free.columns.map(Tone.column) == [.done, .run, nil])
    }
}
