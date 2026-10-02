import Foundation
import Testing
@testable import MightyCore

/// The measurement scene's clock: 3 s count-in, 60 s of motion, 30 s still.
struct ScreenShareReferenceSceneTests {
    typealias Scene = ScreenShareReferenceScene

    @Test func thePhasesFollowEachOtherOnTheDocumentedSchedule() {
        #expect(Scene.totalSeconds == 93)
        #expect(Scene.start(of: .preroll) == 0)
        #expect(Scene.start(of: .motion) == 3)
        #expect(Scene.start(of: .still) == 63)
        #expect(Scene.start(of: .done) == 93)
        #expect(Scene.moment(at: 0).phase == .preroll)
        #expect(Scene.moment(at: 2.99).phase == .preroll)
        #expect(Scene.moment(at: 3).phase == .motion)
        #expect(Scene.moment(at: 62.99).phase == .motion)
        #expect(Scene.moment(at: 63).phase == .still)
        #expect(Scene.moment(at: 92.99).phase == .still)
        #expect(Scene.moment(at: 93).phase == .done)
        #expect(Scene.moment(at: 500).phase == .done)
    }

    @Test func theCountdownShowsWholeSecondsLeftInThePhase() {
        #expect(Scene.moment(at: 0).secondsLeft == 3)
        #expect(Scene.moment(at: 0.2).secondsLeft == 3)
        #expect(Scene.moment(at: 2.1).secondsLeft == 1)
        #expect(Scene.moment(at: 3).secondsLeft == 60)
        #expect(Scene.moment(at: 62.5).secondsLeft == 1)
        #expect(Scene.moment(at: 63).secondsLeft == 30)
        #expect(Scene.moment(at: 93).secondsLeft == 0)
    }

    @Test func motionStopsAtTheStillPhaseAndNeverRunsBackwards() {
        #expect(Scene.moment(at: 1).motionElapsed == 0)
        #expect(Scene.moment(at: 13).motionElapsed == 10)
        #expect(Scene.moment(at: 63).motionElapsed == 60)
        #expect(Scene.moment(at: 80).motionElapsed == 60)
        #expect(Scene.moment(at: -5).phase == .preroll)
        #expect(Scene.moment(at: .nan).phase == .preroll)
        // The still phase is still: scroll and typing are where motion left them.
        let atEnd = Scene.moment(at: 63).motionElapsed
        let later = Scene.moment(at: 90).motionElapsed
        #expect(Scene.scrollOffset(motionElapsed: atEnd, contentHeight: 1e9)
                == Scene.scrollOffset(motionElapsed: later, contentHeight: 1e9))
        #expect(Scene.typedCount(motionElapsed: atEnd) == Scene.typedCount(motionElapsed: later))
    }

    @Test func scrollAndTypingMoveAtFixedRates() {
        #expect(Scene.scrollOffset(motionElapsed: 2, contentHeight: 10_000) == 2 * Scene.scrollPointsPerSecond)
        // A page shorter than the motion wraps instead of running off.
        #expect(Scene.scrollOffset(motionElapsed: 10, contentHeight: 500) == (10 * Scene.scrollPointsPerSecond).truncatingRemainder(dividingBy: 500))
        #expect(Scene.scrollOffset(motionElapsed: 10, contentHeight: 0) == 0)
        #expect(Scene.typedCount(motionElapsed: 0) == 0)
        #expect(Scene.typedCount(motionElapsed: 10) == Int(10 * Scene.typedCharactersPerSecond))
    }

    @Test func eachPhaseIsAnnouncedAsADataChannelNote() throws {
        for phase in Scene.Phase.allCases {
            let note = Scene.announcement(phase)
            #expect(note["t"] as? String == "scene")
            #expect(note["phase"] as? String == phase.rawValue)
            let data = try JSONSerialization.data(withJSONObject: note)
            #expect(data.count < ScreenShareDataChannel.maximumMessageBytes)
        }
        #expect(Scene.Phase.allCases.map(\.rawValue) == ["preroll", "motion", "still", "done"])
    }
}
