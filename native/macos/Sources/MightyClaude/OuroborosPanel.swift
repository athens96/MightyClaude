import SwiftUI
import MightyCore

/// CLI | Ouroboros, per pane, inside Mighty mode.
struct MightyStylePicker: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    var body: some View {
        Picker("요청 스타일", selection: Binding(get: { session.mightyStyle ?? "cli" }, set: { store.setMightyStyle(session.id, style: $0 == "cli" ? nil : $0) })) {
            Text("CLI").tag("cli")
            Text("Ouroboros").tag(OuroborosFlow.style)
        }
        .pickerStyle(.segmented).labelsHidden().controlSize(.small).fixedSize()
        .disabled(session.status == "running")
        .help("CLI: 지금처럼 자유롭게 요청 · Ouroboros: 인터뷰로 요구를 또렷하게 만든 뒤 시드 → 실행 → 평가 → 진화")
        .accessibilityIdentifier("mighty-style-\(session.id)")
    }
}

/// The composer's Ouroboros chrome: where the flow stands, the agent's
/// current question with its choices, or the next steps to take.
struct OuroborosPanel: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    let running: Bool
    @Binding var startingNew: Bool
    /// Commits a syllable the input method is still composing before the
    /// panel reads or replaces the draft.
    var onPrepare: () -> Void = {}

    private var phase: OuroborosPhase { startingNew ? .goal : OuroborosFlow.currentPhase(session: session) }
    private var draft: String { (store.drafts[session.id] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            stepper
            // A waiting question always wins: nothing else may hide it.
            if let (request, questionnaire) = store.ouroborosQuestion(for: session.id) { question(request, questionnaire) }
            else {
                if let prerequisites = store.ouroborosPrerequisites, !prerequisites.ready { setup(prerequisites) }
                if running { progress } else if phase == .goal { start } else { next }
            }
        }
        .padding(.horizontal, 12).padding(.top, 8)
        .accessibilityElement(children: .contain).accessibilityIdentifier("ouroboros-panel-\(session.id)")
    }

    private var stepper: some View {
        HStack(spacing: 4) {
            ForEach(Array(OuroborosPhase.allCases.enumerated()), id: \.element) { index, item in
                let reached = OuroborosPhase.allCases.firstIndex(of: phase).map { index <= $0 } ?? false
                HStack(spacing: 4) {
                    Text(item.title).font(.system(size: 10, weight: item == phase ? .semibold : .regular))
                        .foregroundStyle(item == phase ? Palette.accent : reached ? Color.primary.opacity(0.75) : Color.secondary.opacity(0.6))
                    if index < OuroborosPhase.allCases.count - 1 { Image(systemName: "chevron.right").font(.system(size: 7)).foregroundStyle(.tertiary) }
                }
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .ignore).accessibilityLabel("진행 단계").accessibilityValue(phase.title)
    }

    private var start: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("무엇을 만들까요? 목표를 아래에 적으면 질문을 주고받으며 요구를 또렷하게 만듭니다.").font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 6) {
                actionButton("interview", prominent: true, text: draft).disabled(draft.isEmpty)
                actionButton("auto", prominent: false, text: draft).disabled(draft.isEmpty)
                if startingNew { Button("취소") { startingNew = false }.controlSize(.small) }
            }
        }
    }

    private var next: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("\(phase.title) 단계가 끝났습니다. 다음 단계를 고르거나, 아래에 적어 같은 대화를 이어가세요.").font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ScrollView(.horizontal) {
                HStack(spacing: 6) {
                    ForEach(Array(OuroborosFlow.nextActions(after: phase).enumerated()), id: \.element.id) { index, action in
                        actionButton(action.skill, prominent: index == 0, text: draft)
                    }
                    Button { startingNew = true } label: { Label("새 목표", systemImage: "plus") }.controlSize(.small).help("새 목표로 인터뷰를 다시 시작합니다")
                }
            }.scrollIndicators(.hidden)
        }
    }

    private var progress: some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.mini)
            Text("\(phase.title) 진행 중 · 질문이 오면 여기에 표시됩니다").font(.system(size: 11)).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private func actionButton(_ skill: String, prominent: Bool, text: String) -> some View {
        if let action = OuroborosFlow.action(skill) {
            let button = Button {
                onPrepare()
                startingNew = false
                store.sendOuroboros(session.id, skill: skill, text: (store.drafts[session.id] ?? "").trimmingCharacters(in: .whitespacesAndNewlines))
            } label: { Label(action.title, systemImage: action.systemImage) }
                .controlSize(.small).help(action.help).accessibilityIdentifier("ouroboros-\(skill)-\(session.id)")
            if prominent { button.buttonStyle(.borderedProminent).tint(Palette.accent) } else { button }
        }
    }

    private func setup(_ prerequisites: OuroborosFlow.Prerequisites) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(prerequisites.pluginInstalled ? "uvx가 필요합니다 (Ouroboros MCP 서버 실행용)" : "Ouroboros 플러그인이 설치되어 있지 않습니다", systemImage: "shippingbox")
                .font(.system(size: 11, weight: .medium))
            Text(prerequisites.pluginInstalled ? "터미널에서 `brew install uv` 또는 https://docs.astral.sh/uv 의 안내로 설치한 뒤 다시 확인하세요." : "설치는 Claude CLI의 플러그인 명령으로 진행합니다. 터미널 실행 창이 열리고 명령이 실행됩니다.")
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 6) {
                if !prerequisites.pluginInstalled { Button("플러그인 설치") { store.startOuroborosInstall(from: session) }.controlSize(.small) }
                Button("다시 확인") { store.refreshOuroborosPrerequisites() }.controlSize(.small)
            }
        }
    }

    private func question(_ request: ToolPermissionRequest, _ questionnaire: UserQuestionnaire) -> some View {
        let progress = store.ouroborosProgress(for: session.id, request: request)
        let busy = store.permissionResponses.contains(store.permissionResponseKey(sessionId: session.id, request: request))
        return VStack(alignment: .leading, spacing: 7) {
            if let current = progress.current(in: questionnaire) {
                HStack(spacing: 6) {
                    Image(systemName: "questionmark.bubble.fill").foregroundStyle(Palette.accent)
                    Text(current.header).font(.system(size: 11, weight: .semibold))
                    if questionnaire.questions.count > 1 { Text("\(progress.index + 1)/\(questionnaire.questions.count)").font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
                    Spacer()
                    if progress.index > 0 { Button("이전") { store.ouroborosBack(session.id) }.controlSize(.mini).disabled(busy) }
                    Button("답하지 않기") { Task { await store.answerPermission(sessionId: session.id, request: request, allow: false) } }.controlSize(.mini).disabled(busy)
                }
                Text(current.question).font(.system(size: 13)).lineSpacing(3).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("ouroboros-question-\(session.id)")
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(current.options, id: \.label) { option in
                        let picked = progress.selected.contains(option.label)
                        Button { onPrepare(); store.ouroborosChoose(session.id, option: option.label) } label: {
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
                    if current.multiSelect, !progress.selected.isEmpty { Button("선택 완료") { onPrepare(); store.ouroborosAnswer(session.id, text: "") }.controlSize(.mini).disabled(busy) }
                    if busy { ProgressView().controlSize(.mini) }
                }
                if let error = store.permissionErrors[session.id] { Label(error, systemImage: "exclamationmark.triangle").font(.system(size: 10)).foregroundStyle(.orange) }
            }
        }
    }
}
