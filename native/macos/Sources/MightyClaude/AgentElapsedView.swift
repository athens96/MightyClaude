import SwiftUI
import MightyCore

struct AgentElapsedView: View {
    let timing: AgentRunTiming

    var body: some View {
        if timing.finishedAt != nil { label(at: .now) }
        else { TimelineView(.periodic(from: .now, by: 1)) { context in label(at: context.date) } }
    }

    private func label(at date: Date) -> some View {
        Label(timing.label(at: date), systemImage: "clock")
            .font(.system(size: 10, design: .monospaced)).monospacedDigit()
            .foregroundStyle(.secondary).fixedSize()
            .help(timing.isApproximate ? "마지막 요청과 응답·활동 또는 저장 시각으로 복원한 추정 시간입니다. 앱이 종료된 동안의 시간은 포함하지 않습니다." : timing.finishedAt == nil ? "이번 요청의 경과 시간 · 승인 대기 포함" : "이번 요청의 실행 시간")
            .accessibilityLabel("\(timing.finishedAt == nil ? "진행 시간" : "실행 시간") \(timing.label(at: date))")
    }
}

struct AgentSessionElapsedView: View {
    @ObservedObject var companion: AgentCompanion
    let sessionID: String
    var body: some View {
        if let timing = companion.agents.first(where: { $0.id == sessionID })?.timing {
            AgentElapsedView(timing: timing).accessibilityIdentifier("agent-elapsed-\(sessionID)")
        }
    }
}
