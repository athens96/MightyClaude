import SwiftUI
import MightyCore

/// The agent's pending AskUserQuestion, answered from the composer: one
/// question at a time with option chips; typed text answers on Enter.
/// Shared by the guided Mighty styles.
struct AgentQuestionPanel: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let request: ToolPermissionRequest
    let questionnaire: UserQuestionnaire
    var onPrepare: () -> Void = {}

    var body: some View {
        let progress = store.guidedProgress(for: sessionId, request: request)
        let busy = store.permissionResponses.contains(store.permissionResponseKey(sessionId: sessionId, request: request))
        return VStack(alignment: .leading, spacing: 7) {
            if let current = progress.current(in: questionnaire) {
                HStack(spacing: 6) {
                    Image(systemName: "questionmark.bubble.fill").foregroundStyle(Palette.accent)
                    Text(current.header).font(.system(size: 11, weight: .semibold))
                    if questionnaire.questions.count > 1 { Text("\(progress.index + 1)/\(questionnaire.questions.count)").font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
                    Spacer()
                    if progress.index > 0 { Button("이전") { store.guidedBack(sessionId) }.controlSize(.mini).disabled(busy) }
                    Button("답하지 않기") { Task { await store.answerPermission(sessionId: sessionId, request: request, allow: false) } }.controlSize(.mini).disabled(busy)
                }
                Text(current.question).font(.system(size: 13)).lineSpacing(3).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("agent-question-\(sessionId)")
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(current.options, id: \.label) { option in
                        let picked = progress.selected.contains(option.label)
                        Button { onPrepare(); store.guidedChoose(sessionId, option: option.label) } label: {
                            HStack(alignment: .top, spacing: 7) {
                                Image(systemName: current.multiSelect ? (picked ? "checkmark.square.fill" : "square") : "circle").font(.system(size: 11)).foregroundStyle(picked ? Palette.accent : Color.secondary).padding(.top, 1)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(option.label).font(.system(size: 12, weight: .medium)).multilineTextAlignment(.leading)
                                    if !option.description.isEmpty { Text(option.description).font(.system(size: 10)).foregroundStyle(.secondary).multilineTextAlignment(.leading).fixedSize(horizontal: false, vertical: true) }
                                }
                                Spacer(minLength: 0)
                            }
                            .padding(.horizontal, 9).padding(.vertical, 6)
                            .background(picked ? Palette.accent.opacity(0.12) : Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                            .contentShape(RoundedRectangle(cornerRadius: 7))
                        }
                        .buttonStyle(.plain).disabled(busy)
                    }
                }
                HStack(spacing: 6) {
                    Text(current.multiSelect ? "여러 개를 고른 뒤 Enter, 또는 아래에 직접 적어 Enter" : "하나를 고르거나 아래에 직접 적어 Enter").font(.system(size: 10)).foregroundStyle(.tertiary)
                    if current.multiSelect, !progress.selected.isEmpty { Button("선택 완료") { onPrepare(); store.guidedAnswer(sessionId, text: "") }.controlSize(.mini).disabled(busy) }
                    if busy { ProgressView().controlSize(.mini) }
                }
                if let error = store.permissionErrors[sessionId] { Label(error, systemImage: "exclamationmark.triangle").font(.system(size: 10)).foregroundStyle(.orange) }
            }
        }
    }
}
