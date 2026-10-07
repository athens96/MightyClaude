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
        return VStack(alignment: .leading, spacing: 0) {
            if let current = progress.current(in: questionnaire) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        PaneWaitBadge(systemImage: "questionmark")
                        Text(current.header).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink)
                        if questionnaire.questions.count > 1 {
                            Text(L("phone.questionnaire.progress", ["current": "\(progress.index + 1)", "total": "\(questionnaire.questions.count)"]))
                                .font(.system(size: 11)).monospacedDigit().foregroundStyle(Palette.ink2)
                        }
                        Spacer()
                        if progress.index > 0 { Button(L("phone.questionnaire.back")) { store.guidedBack(sessionId) }.buttonStyle(PaneCardButtonStyle()).disabled(busy) }
                        Button(L("question.panel.skip")) { Task { await store.answerPermission(sessionId: sessionId, request: request, allow: false) } }.buttonStyle(PaneCardButtonStyle()).disabled(busy)
                    }
                    Text(current.question).font(.system(size: 13.5, weight: .bold)).foregroundStyle(Palette.ink).lineSpacing(3).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("agent-question-\(sessionId)")
                    // Two tiles a row where the composer is wide enough, one where it is not.
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 200), spacing: DesignMetrics.Spacing.sm, alignment: .top)], alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                        ForEach(current.options, id: \.label) { option in
                            let picked = progress.selected.contains(option.label)
                            let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
                            Button { onPrepare(); store.guidedChoose(sessionId, option: option.label) } label: {
                                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                                    Image(systemName: current.multiSelect ? (picked ? "checkmark.square.fill" : "square") : (picked ? "largecircle.fill.circle" : "circle")).font(.system(size: 12)).foregroundStyle(picked ? Palette.waitText : Palette.ink2).padding(.top, 1)
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(option.label).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink).multilineTextAlignment(.leading)
                                        if !option.description.isEmpty { Text(option.description).font(.system(size: 10.5)).foregroundStyle(Palette.ink2).multilineTextAlignment(.leading).fixedSize(horizontal: false, vertical: true) }
                                    }
                                    Spacer(minLength: 0)
                                }
                                .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
                                .frame(maxWidth: .infinity, alignment: .topLeading)
                                .background(picked ? Palette.waitSoft : Palette.panel, in: shape)
                                .overlay { shape.strokeBorder(picked ? Palette.wait : Palette.border, lineWidth: picked ? 2 : 1) }
                                .contentShape(shape)
                            }
                            .buttonStyle(.plain).disabled(busy)
                        }
                    }
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(current.multiSelect ? L("question.panel.hintMultiple") : L("question.panel.hintSingle")).font(.system(size: 10.5)).foregroundStyle(Palette.ink2)
                        Spacer(minLength: 0)
                        if current.multiSelect, !progress.selected.isEmpty { Button(L("question.panel.doneSelecting")) { onPrepare(); store.guidedAnswer(sessionId, text: "") }.buttonStyle(PaneCardButtonStyle(prominent: true)).disabled(busy) }
                        if busy { ProgressView().controlSize(.mini) }
                    }
                    if let error = store.permissionErrors[sessionId] { Label(error, systemImage: "exclamationmark.triangle").font(.system(size: 10)).foregroundStyle(Palette.waitText) }
                }
                // Concept D: the amber edge says this waits on the user.
                .padding(DesignMetrics.Spacing.md)
                .background(Palette.panel, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                .overlay { RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Palette.wait, lineWidth: 2).allowsHitTesting(false) }
            }
        }
    }
}
