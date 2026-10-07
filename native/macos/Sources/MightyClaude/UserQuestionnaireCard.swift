import SwiftUI
import MightyCore

/// One question at a time: "Next" moves on once the current one is answered,
/// "Back" goes back with the earlier picks intact. Selection is a local draft;
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
        return total > 1 ? L("phone.questionnaire.progress", ["current": "\(currentStep + 1)", "total": "\(total)"]) : L("questionnaire.oneQuestion")
    }
    private func dotState(_ index: Int) -> String {
        if index == currentStep { return L("questionnaire.dot.current") }
        return answered(index) ? L("questionnaire.dot.answered") : L("questionnaire.dot.unanswered")
    }
    /// Back is always allowed; forward only over questions that already have an answer.
    private func canJump(to index: Int) -> Bool { index <= currentStep || (0..<index).allSatisfy(answered) }

    private var canAnswer: Bool { request.canAnswerQuestions && request.state == "pending" }
    private var canSubmit: Bool { canAnswer && (try? questionnaire.validatedAnswers(answers)) != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                PaneWaitBadge(systemImage: "questionmark")
                Text(L("phone.questionnaire.title")).font(.system(size: 13, weight: .bold)).foregroundStyle(Palette.ink)
                Text(progressText)
                    .foregroundStyle(Palette.ink2).monospacedDigit()
                    .accessibilityLabel(L("questionnaire.progressAccessibility", ["total": "\(questionnaire.questions.count)", "current": "\(currentStep + 1)", "answered": "\(answeredCount)"]))
                    .accessibilityIdentifier("questionnaire-progress")
                if questionnaire.questions.count > 1 { stepDots }
                Spacer(minLength: 0)
                if count > 1 { Text(L("phone.questionnaire.waiting", ["count": "\(count)"])).foregroundStyle(Palette.ink2) }
            }.font(.system(size: 12))

            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
                    if questionnaire.questions.indices.contains(currentStep) {
                        questionSection(questionnaire.questions[currentStep], index: currentStep).id(currentStep)
                    }
                }.padding(.trailing, DesignMetrics.Spacing.xs).padding(.vertical, DesignMetrics.Spacing.xxs)
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
                Text(L("questionnaire.cannotAnswer"))
                    .font(.system(size: 10)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let error = store.permissionErrors[sessionId] {
                Text(error).font(.system(size: 11)).foregroundStyle(Palette.errText)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            }

            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(currentStep < lastStep ? L("phone.questionnaire.hintNext") : L("phone.questionnaire.hintSubmit"))
                    .font(.system(size: 11)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                if busy { ProgressView().controlSize(.mini).accessibilityIdentifier("questionnaire-sending") }
                Button(L("phone.questionnaire.cancel")) {
                    Task { await store.answerPermission(sessionId: sessionId, request: request, allow: false) }
                }.buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("questionnaire-cancel")
                if currentStep > 0 {
                    Button(L("phone.questionnaire.back")) { step = currentStep - 1 }
                        .buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("questionnaire-back")
                }
                if currentStep < lastStep {
                    Button(L("phone.questionnaire.next")) { step = currentStep + 1 }
                        .buttonStyle(PaneCardButtonStyle(prominent: true))
                        .disabled(!canAnswer || !answered(currentStep))
                        .accessibilityIdentifier("questionnaire-next")
                } else {
                    Button(L("phone.questionnaire.submit")) {
                        let submittedAnswers = answers
                        Task { await store.answerQuestionnaire(sessionId: sessionId, request: request, answers: submittedAnswers) }
                    }
                    .buttonStyle(PaneCardButtonStyle(prominent: true))
                    .disabled(!canSubmit)
                    .accessibilityIdentifier("questionnaire-submit")
                }
            }.disabled(busy)
        }
        // Concept D: a white card with the amber edge, docked right above the composer.
        .paneWaitCard()
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("questionnaire-\(request.id)")
    }

    /// Filled for answered questions, ringed for the one on screen; a dot jumps
    /// back to a question already reached.
    private var stepDots: some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            ForEach(questionnaire.questions.indices, id: \.self) { index in
                Button { if canJump(to: index) { step = index } } label: {
                    // The question on screen is the amber dot; one answered keeps a soft amber.
                    Text("\(index + 1)").font(.system(size: 10, weight: .bold)).monospacedDigit()
                        .foregroundStyle(index == currentStep ? Palette.onWait : answered(index) ? Palette.waitText : Palette.ink2)
                        .frame(width: 18, height: 18)
                        .background(index == currentStep ? Palette.wait : answered(index) ? Palette.waitSoft : Palette.track, in: Circle())
                        .contentShape(Circle())
                }
                .buttonStyle(.plain).disabled(!canJump(to: index))
                .accessibilityLabel(L("phone.questionnaire.jump", ["index": "\(index + 1)"])).accessibilityValue(dotState(index))
                .accessibilityIdentifier("questionnaire-step-\(index)")
            }
        }.disabled(busy)
    }

    private func questionSection(_ question: UserQuestionnaire.Question, index: Int) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(question.header).font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.waitText)
                Spacer(minLength: 0)
                Text(question.multiSelect ? L("phone.questionnaire.multiple") : L("phone.questionnaire.single"))
                    .font(.system(size: 10.5)).foregroundStyle(Palette.ink2)
            }
            Text(question.question).font(.system(size: 14.5, weight: .bold)).foregroundStyle(Palette.ink)
                .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)

            // Two tiles a row where the pane is wide enough, one where it is not.
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 210), spacing: DesignMetrics.Spacing.sm, alignment: .top)], alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
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
            choiceRow(label: L("phone.questionnaire.custom"), description: nil, selected: customQuestions.contains(index),
                      multiple: question.multiSelect, identifier: "questionnaire-custom-\(index)") {
                if customQuestions.contains(index) { customQuestions.remove(index) }
                else {
                    customQuestions.insert(index)
                    if !question.multiSelect { selections[index] = [] }
                }
            }
            }
            if customQuestions.contains(index) {
                TextField(L("phone.questionnaire.customPlaceholder"), text: Binding(get: { customText[index, default: ""] },
                    set: { customText[index] = $0 }), axis: .vertical)
                    .textFieldStyle(.roundedBorder).font(.system(size: 12))
                    .lineLimit(1...4)
                    .accessibilityLabel(L("phone.questionnaire.customLabel", ["header": question.header]))
                    .accessibilityIdentifier("questionnaire-custom-text-\(index)")
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("questionnaire.questionAccessibility", ["index": "\(index + 1)", "header": question.header]))
        .accessibilityIdentifier("questionnaire-question-\(index)")
    }

    private func choiceRow(label: String, description: String?, selected: Bool, multiple: Bool,
                           identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            let shape = RoundedRectangle(cornerRadius: 11, style: .continuous)
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: multiple ? (selected ? "checkmark.square.fill" : "square") :
                        (selected ? "largecircle.fill.circle" : "circle"))
                    .foregroundStyle(selected ? Palette.waitText : Palette.ink2)
                    .font(.system(size: 14)).frame(width: 17).padding(.top, 1)
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text(label).font(.system(size: 13, weight: .bold)).foregroundStyle(Palette.ink)
                    if let description, !description.isEmpty {
                        Text(description).font(.system(size: 11.5)).foregroundStyle(Palette.ink2)
                    }
                }.fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, minHeight: 50, alignment: .topLeading)
            .background(selected ? Palette.waitSoft : Palette.panel, in: shape)
            .overlay { shape.strokeBorder(selected ? Palette.wait : Palette.border, lineWidth: selected ? 2 : 1) }
            .contentShape(shape)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityValue(selected ? L("accessibility.selected") : L("accessibility.notSelected"))
        .accessibilityHint(description ?? L("phone.questionnaire.customHint"))
        .accessibilityIdentifier(identifier)
    }
}

private struct QuestionContentHeight: PreferenceKey {
    static var defaultValue: CGFloat = 300
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}
