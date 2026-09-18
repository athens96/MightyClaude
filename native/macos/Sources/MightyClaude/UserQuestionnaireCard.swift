import SwiftUI
import MightyCore

/// One question at a time: 다음 moves on once the current one is answered,
/// 이전 goes back with the earlier picks intact. Selection is a local draft;
/// only the explicit submit on the last question sends an answer.
struct UserQuestionnaireCard: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let request: ToolPermissionRequest
    let questionnaire: UserQuestionnaire
    let count: Int
    @ViewState private var selections: [Int: Set<String>] = [:]
    @ViewState private var customQuestions = Set<Int>()
    @ViewState private var customText: [Int: String] = [:]
    @ViewState private var contentHeight: CGFloat = 300
    @ViewState private var step = 0

    private var busy: Bool {
        store.permissionResponses.contains(store.permissionResponseKey(sessionId: sessionId, request: request))
    }

    private var answers: [String: UserQuestionAnswer] {
        // Question texts are unique: `UserQuestionnaire` refuses to decode duplicates.
        Dictionary(uniqueKeysWithValues: questionnaire.questions.enumerated().map { index, question in
            // Retain the displayed option order, regardless of the order of clicks.
            let labels = question.options.map(\.label).filter { selections[index, default: []].contains($0) }
            return (question.question, UserQuestionAnswer(selectedOptions: labels,
                customText: customQuestions.contains(index) ? customText[index, default: ""] : nil))
        })
    }

    private func answered(_ index: Int) -> Bool {
        !selections[index, default: []].isEmpty ||
        (customQuestions.contains(index) && !customText[index, default: ""].trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    }
    private var answeredCount: Int { questionnaire.questions.indices.filter(answered).count }
    private var lastStep: Int { max(0, questionnaire.questions.count - 1) }
    /// Clamped: a replaced request may have fewer questions than the one before it.
    private var currentStep: Int { min(max(0, step), lastStep) }

    private var progressText: String {
        let total = questionnaire.questions.count
        return total > 1 ? "질문 \(currentStep + 1)/\(total)" : "질문 1개"
    }
    private func dotState(_ index: Int) -> String {
        if index == currentStep { return "현재 질문" }
        return answered(index) ? "답변함" : "답변 안 함"
    }
    /// Back is always allowed; forward only over questions that already have an answer.
    private func canJump(to index: Int) -> Bool { index <= currentStep || (0..<index).allSatisfy(answered) }

    private var canAnswer: Bool { request.canAnswerQuestions && request.state == "pending" }
    private var canSubmit: Bool { canAnswer && (try? questionnaire.validatedAnswers(answers)) != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 7) {
                Image(systemName: "questionmark.bubble.fill").foregroundStyle(Palette.accent)
                Text("선택 요청").fontWeight(.semibold)
                Text(progressText)
                    .foregroundStyle(.secondary).monospacedDigit()
                    .accessibilityLabel("질문 \(questionnaire.questions.count)개 중 \(currentStep + 1)번째, \(answeredCount)개 답변함")
                    .accessibilityIdentifier("questionnaire-progress")
                if questionnaire.questions.count > 1 { stepDots }
                Spacer(minLength: 0)
                if count > 1 { Text("\(count)개 대기").foregroundStyle(.secondary) }
            }.font(.system(size: 11))

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if questionnaire.questions.indices.contains(currentStep) {
                        questionSection(questionnaire.questions[currentStep], index: currentStep).id(currentStep)
                    }
                }.padding(.trailing, 5).padding(.vertical, 2)
                    .background { GeometryReader { proxy in
                        Color.clear.preference(key: QuestionContentHeight.self, value: proxy.size.height)
                    } }
            }
            // The question viewport must yield space to the footer and composer
            // when this session is in a short split pane.
            .frame(minHeight: 56, idealHeight: max(56, min(contentHeight, 300)), maxHeight: max(56, min(contentHeight, 300)))
            .onPreferenceChange(QuestionContentHeight.self) { contentHeight = max(1, $0) }
            .disabled(busy || !canAnswer)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("questionnaire-viewport")

            if !canAnswer {
                Text("이 요청에는 답변을 보낼 수 없습니다. 요청을 취소하고 새 작업에서 다시 시도하세요.")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let error = store.permissionErrors[sessionId] {
                Text(error).font(.system(size: 11)).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            }

            HStack(spacing: 8) {
                Text(currentStep < lastStep ? "답을 고르고 다음으로 넘어가세요." : "답변을 보내면 작업이 계속됩니다.")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                if busy { ProgressView().controlSize(.mini).accessibilityIdentifier("questionnaire-sending") }
                Button("취소") {
                    Task { await store.answerPermission(sessionId: sessionId, request: request, allow: false) }
                }.accessibilityIdentifier("questionnaire-cancel")
                if currentStep > 0 {
                    Button("이전") { step = currentStep - 1 }.accessibilityIdentifier("questionnaire-back")
                }
                if currentStep < lastStep {
                    Button("다음") { step = currentStep + 1 }
                        .buttonStyle(.borderedProminent)
                        .disabled(!canAnswer || !answered(currentStep))
                        .accessibilityIdentifier("questionnaire-next")
                } else {
                    Button("답변 보내기") {
                        let submittedAnswers = answers
                        Task { await store.answerQuestionnaire(sessionId: sessionId, request: request, answers: submittedAnswers) }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(!canSubmit)
                    .accessibilityIdentifier("questionnaire-submit")
                }
            }.controlSize(.small).disabled(busy)
        }
        .padding(12)
        .background(Palette.accent.opacity(0.045))
        .overlay(alignment: .top) { Divider() }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("questionnaire-\(request.id)")
    }

    /// Filled for answered questions, ringed for the one on screen; a dot jumps
    /// back to a question already reached.
    private var stepDots: some View {
        HStack(spacing: 4) {
            ForEach(questionnaire.questions.indices, id: \.self) { index in
                Button { if canJump(to: index) { step = index } } label: {
                    Circle().fill(answered(index) ? Palette.accent : Color.primary.opacity(0.2)).frame(width: 6, height: 6)
                        .overlay { Circle().stroke(Palette.accent, lineWidth: index == currentStep ? 1.5 : 0).frame(width: 10, height: 10) }
                        .frame(width: 12, height: 12).contentShape(Rectangle())
                }
                .buttonStyle(.plain).disabled(!canJump(to: index))
                .accessibilityLabel("질문 \(index + 1)로 이동").accessibilityValue(dotState(index))
                .accessibilityIdentifier("questionnaire-step-\(index)")
            }
        }.disabled(busy)
    }

    private func questionSection(_ question: UserQuestionnaire.Question, index: Int) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Text("\(index + 1)").font(.system(size: 10, weight: .semibold, design: .rounded))
                    .frame(width: 19, height: 19).background(Palette.accent.opacity(0.12), in: Circle())
                Text(question.header).font(.system(size: 11, weight: .semibold))
                Spacer(minLength: 0)
                Text(question.multiSelect ? "여러 개 선택 가능" : "하나 선택")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
            }
            Text(question.question).font(.system(size: 12, weight: .medium))
                .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)

            ForEach(Array(question.options.enumerated()), id: \.offset) { optionIndex, option in
                choiceRow(label: option.label, description: option.description,
                          selected: selections[index, default: []].contains(option.label), multiple: question.multiSelect,
                          identifier: "questionnaire-option-\(index)-\(optionIndex)") {
                    if question.multiSelect {
                        if selections[index, default: []].contains(option.label) { selections[index, default: []].remove(option.label) }
                        else { selections[index, default: []].insert(option.label) }
                    } else {
                        selections[index] = [option.label]
                        customQuestions.remove(index)
                    }
                }
            }
            choiceRow(label: "직접 입력", description: nil, selected: customQuestions.contains(index),
                      multiple: question.multiSelect, identifier: "questionnaire-custom-\(index)") {
                if customQuestions.contains(index) { customQuestions.remove(index) }
                else {
                    customQuestions.insert(index)
                    if !question.multiSelect { selections[index] = [] }
                }
            }
            if customQuestions.contains(index) {
                TextField("답변을 입력하세요", text: Binding(get: { customText[index, default: ""] },
                    set: { customText[index] = $0 }), axis: .vertical)
                    .textFieldStyle(.roundedBorder).font(.system(size: 12))
                    .lineLimit(1...4)
                    .accessibilityLabel("\(question.header) 직접 입력 답변")
                    .accessibilityIdentifier("questionnaire-custom-text-\(index)")
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("질문 \(index + 1): \(question.header)")
        .accessibilityIdentifier("questionnaire-question-\(index)")
    }

    private func choiceRow(label: String, description: String?, selected: Bool, multiple: Bool,
                           identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: multiple ? (selected ? "checkmark.square.fill" : "square") :
                        (selected ? "largecircle.fill.circle" : "circle"))
                    .foregroundStyle(selected ? Palette.accent : Color.secondary)
                    .font(.system(size: 14)).frame(width: 17).padding(.top, 1)
                VStack(alignment: .leading, spacing: 3) {
                    Text(label).font(.system(size: 12, weight: selected ? .semibold : .medium))
                    if let description, !description.isEmpty {
                        Text(description).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                }.fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 9).padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Palette.accent.opacity(0.09) : Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
            .overlay { RoundedRectangle(cornerRadius: 7).stroke(selected ? Palette.accent.opacity(0.5) : Palette.border, lineWidth: 1) }
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityValue(selected ? "선택됨" : "선택 안 됨")
        .accessibilityHint(description ?? "직접 입력할 답변 선택")
        .accessibilityIdentifier(identifier)
    }
}

private struct QuestionContentHeight: PreferenceKey {
    static var defaultValue: CGFloat = 300
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}
