import Foundation
import Testing
@testable import MightyCore

/// The pet's progress card (`CompanionProgress`): a few steps around the one in progress, a few pieces of background
/// work, and the height the pet's window grows by.
struct CompanionProgressTests {
    private let base = Date(timeIntervalSince1970: 1_800_000_000)
    private func stamp(_ seconds: Double) -> String { FlowSteps.stamp(base.addingTimeInterval(seconds)) }

    private func run(steps: Int, current: Int, background: Int = 0) -> MightyGraphRun {
        var run = MightyGraphRun(id: "r", input: "배포해 줘", status: "running", provider: "claude")
        run.steps = (0..<steps).map { index in
            FlowStep(key: "text:\(index)", content: "단계 \(index + 1)", status: index < current ? "completed" : index == current ? "in_progress" : "pending",
                     startedAt: index <= current ? stamp(Double(index * 10)) : nil, endedAt: index < current ? stamp(Double(index * 10 + 9)) : nil)
        }
        run.agents = (0..<background).map { MightyGraphAgent(id: "t\($0)", title: "작업 \($0)", status: "running", kind: "task", background: true, figures: GraphAgentFigures(startedAt: stamp(1))) }
        return run
    }

    @Test func aRequestWithoutStepsOrBackgroundWorkHasNoCard() {
        #expect(CompanionProgress.newest(in: [run(steps: 0, current: 0)]) == nil)
        #expect(CompanionProgress.newest(in: []) == nil)
    }

    @Test func theCardKeepsTheStepInProgressInViewAndCountsTheRest() throws {
        let card = try #require(CompanionProgress.newest(in: [run(steps: 9, current: 6)]))
        #expect(card.rows.count == CompanionProgress.maximumRows)
        #expect(card.rows.map(\.index) == [4, 5, 6, 7, 8])
        #expect(card.rows.contains { $0.state == .current && $0.index == 6 })
        #expect(card.hidden == 4)
        #expect(card.count == L("graph.steps.count", ["done": "6", "total": "9"]))
        #expect(card.heading == L("graph.timeline.requestOrdinal", ["n": "1"]))
    }

    @Test func backgroundWorkAloneMakesACardAndFoldingShrinksIt() throws {
        let card = try #require(CompanionProgress.newest(in: MightyGraphSupport.shownRuns([run(steps: 0, current: 0, background: 5)])))
        #expect(card.rows.isEmpty && card.count == nil)
        #expect(card.background.count == CompanionProgress.maximumBackground)
        #expect(card.backgroundHidden == 2)
        #expect(card.backgroundCount == "5/5")
        let open = card.height(collapsed: false)
        let folded = card.height(collapsed: true)
        #expect(folded == CompanionProgress.headerHeight + CompanionProgress.gap)
        #expect(open == folded + CompanionProgress.sectionHeight + 3 * CompanionProgress.rowHeight + CompanionProgress.sectionHeight + CompanionProgress.padding)
        // The pet's window grows by the card.
        let plain = CompanionBubbleLayout.panelSize(width: nil, height: nil, tall: false)
        #expect(CompanionBubbleLayout.panelSize(width: nil, height: nil, tall: false, above: open).height == plain.height + open)
        #expect(CompanionBubbleLayout.panelSize(width: nil, height: nil, tall: true, above: open).height == CompanionBubbleLayout.tallHeight)
    }
}
