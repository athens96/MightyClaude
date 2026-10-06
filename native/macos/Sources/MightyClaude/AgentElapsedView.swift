import SwiftUI
import MightyCore

struct AgentElapsedView: View {
    let timing: AgentRunTiming

    var body: some View {
        if timing.finishedAt != nil { label(at: .now) }
        else { TimelineView(.periodic(from: .now, by: 1)) { context in label(at: context.date) } }
    }

    /// What the clock measures: a rebuilt estimate, a request still going, or a finished one.
    static func help(_ timing: AgentRunTiming) -> String {
        timing.isApproximate ? L("pane.hero.elapsed.approximate") : timing.finishedAt == nil ? L("pane.hero.elapsed.running") : L("pane.hero.elapsed.finished")
    }

    private func label(at date: Date) -> some View {
        Label(timing.label(at: date), systemImage: "clock")
            .font(.system(size: 10, design: .monospaced)).monospacedDigit()
            .foregroundStyle(.secondary).fixedSize()
            .help(Self.help(timing))
            .accessibilityLabel(L(timing.finishedAt == nil ? "pane.elapsed.runningAccessibility" : "pane.elapsed.finishedAccessibility", ["time": timing.label(at: date)]))
    }
}
