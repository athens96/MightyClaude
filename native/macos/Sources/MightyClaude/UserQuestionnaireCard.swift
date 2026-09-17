import SwiftUI
import MightyCore

/// Selection is a local draft. Only the explicit submit action sends an answer.
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

    private var busy: Bool {
        store.permissionResponses.contains(store.permissionResponseKey(sessionId: sessionId, request: request))
    }

    private var answers: [String: UserQuestionAnswer] {
        Dictionary(uniqueKeysWithValues: questionnaire.questions.enumerated().map { index, question in
            // Retain the displayed option order, regardless of the order of clicks.
            let labels = question.options.map(\.label).filter { selections[index, default: []].contains($0) }
            return (question.question, UserQuestionAnswer(selectedOptions: labels,
                customText: customQuestions.contains(index) ? customText[index, default: ""] : nil))
        })
    }

    private var answeredCount: Int {
        questionnaire.questions.indices.filter { index in
            !selections[index, default: []].isEmpty ||
            (customQuestions.contains(index) && !customText[index, default: ""].trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        }.count
    }

    private var canAnswer: Bool { request.canAnswerQuestions && request.state == "pending" }
    private var canSubmit: Bool { canAnswer && (try? questionnaire.validatedAnswers(answers)) != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 7) {
                Image(systemName: "questionmark.bubble.fill").foregroundStyle(Palette.accent)
                Text("선택 요청").fontWeight(.semibold)
                Text("\(answeredCount)/\(questionnaire.questions.count) 답변")
                    .foregroundStyle(.secondary).monospacedDigit()
                    .accessibilityIdentifier("questionnaire-progress")
                Spacer(minLength: 0)
                if count > 1 { Text("\(count)개 대기").foregroundStyle(.secondary) }
            }.font(.system(size: 11))

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    ForEach(Array(questionnaire.questions.enumerated()), id: \.offset) { index, question in
                        questionSection(question, index: index)
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
                Text("답변을 보내면 작업이 계속됩니다.")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                if busy { ProgressView().controlSize(.mini).accessibilityIdentifier("questionnaire-sending") }
                Button("취소") {
                    Task { await store.answerPermission(sessionId: sessionId, request: request, allow: false) }
                }.accessibilityIdentifier("questionnaire-cancel")
                Button("답변 보내기") {
                    let submittedAnswers = answers
                    Task { await store.answerQuestionnaire(sessionId: sessionId, request: request, answers: submittedAnswers) }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!canSubmit)
                .accessibilityIdentifier("questionnaire-submit")
            }.controlSize(.small).disabled(busy)
        }
        .padding(12)
        .background(Palette.accent.opacity(0.045))
        .overlay(alignment: .top) { Divider() }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("questionnaire-\(request.id)")
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
