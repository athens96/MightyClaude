import AppKit
import SwiftUI
import MightyCore

struct AgentStatusControls: View {
    @ObservedObject var companion: AgentCompanion
    var body: some View {
        HStack(spacing: 10) {
            Button { companion.preferences.enabled.toggle() } label: {
                Image(systemName: companion.preferences.enabled ? "pawprint.fill" : "pawprint")
                    .foregroundStyle(companion.preferences.enabled ? Palette.accent : .secondary)
            }.buttonStyle(.plain).help(companion.preferences.enabled ? "펫 숨기기" : "펫 보기").accessibilityLabel("펫 표시 전환")
            Button { companion.showsStatus.toggle() } label: {
                HStack(spacing: 5) {
                    Image(systemName: companion.runningCount > 0 ? "waveform.path" : "circle.grid.2x2")
                    if companion.runningCount > 0 { Text("\(companion.runningCount)").monospacedDigit() }
                }.padding(.horizontal, 8).padding(.vertical, 4)
                    .background(Palette.subtle, in: Capsule())
            }.buttonStyle(.plain).help("에이전트 상태").accessibilityLabel("에이전트 상태")
                .popover(isPresented: $companion.showsStatus, arrowEdge: .top) {
                    AgentStatusPopover(companion: companion)
                }
        }
    }
}

struct AgentStatusPopover: View {
    @ObservedObject var companion: AgentCompanion
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("에이전트 상태").font(.system(size: 14, weight: .semibold))
                Spacer()
                Text("\(companion.runningCount)개 작업 중").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            if companion.agents.isEmpty {
                Text("에이전트를 추가하면 현재 작업이 여기에 표시됩니다.").font(.system(size: 12)).foregroundStyle(.secondary).padding(.vertical, 18)
            } else {
                ScrollView {
                    LazyVStack(spacing: 8) {
                        ForEach(companion.agents) { agent in
                            Button { companion.focus(agent.id) } label: {
                                HStack(alignment: .top, spacing: 10) {
                                    PresenceIndicator(status: agent.status).padding(.top, 2)
                                    VStack(alignment: .leading, spacing: 4) {
                                        HStack { Text(agent.title).font(.system(size: 12, weight: .medium)); Spacer(); Text(presenceLabel(agent.status)).font(.system(size: 10)).foregroundStyle(.secondary) }
                                        HStack {
                                            Text(agent.workspace).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                                            Spacer(minLength: 0)
                                            if let timing = agent.timing { AgentElapsedView(timing: timing) }
                                        }
                                        if let input = agent.input, !input.isEmpty {
                                            Label(input, systemImage: "arrow.up.right").font(.system(size: 11)).lineLimit(2)
                                        }
                                        Text(agent.summary).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(2)
                                    }
                                    Image(systemName: "arrow.up.forward").font(.system(size: 9)).foregroundStyle(.tertiary)
                                }.padding(10).frame(maxWidth: .infinity, alignment: .leading).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 10))
                            }.buttonStyle(.plain).accessibilityLabel("\(agent.title), \(presenceLabel(agent.status)), 열기")
                        }
                    }
                }.frame(maxHeight: 330)
            }
            Divider()
            Toggle("데스크톱 펫", isOn: $companion.preferences.enabled).toggleStyle(.switch).controlSize(.mini)
        }.padding(18).frame(width: 350)
    }
}

struct CompanionSettingsSection: View {
    @ObservedObject var companion: AgentCompanion
    var body: some View {
        Section("펫과 작업 알림") {
            Toggle("데스크톱 펫 표시", isOn: $companion.preferences.enabled)
            HStack {
                if let pet = companion.selectedPet, let image = pet.frames.first?.first {
                    Image(nsImage: image).resizable().scaledToFit().frame(width: 58, height: 64)
                }
                VStack(alignment: .leading, spacing: 7) {
                    Picker("펫", selection: $companion.preferences.selectedPet) {
                        ForEach(companion.pets) { pet in Text(pet.name).tag(pet.id) }
                    }
                    HStack {
                        Button("Codex 펫 가져오기…") { companion.importPet() }
                        Button("새로고침") { companion.reloadPets() }
                    }.controlSize(.small)
                }
            }
            Text("설치된 Codex 펫과 PNG/WebP 스프라이트를 사용할 수 있습니다. v1–v3의 기본 9개 동작을 재생합니다.")
                .font(.system(size: 10)).foregroundStyle(.secondary)
            Toggle("펫 말풍선에 요청과 현재 작업 표시", isOn: $companion.preferences.showsTask)
            Toggle("펫 애니메이션 줄이기", isOn: $companion.preferences.reducedMotion)
            Toggle("작업 완료 시 Mac 알림", isOn: $companion.preferences.notifications)
                .onChange(of: companion.preferences.notifications) { _, enabled in Task { await companion.refreshNotificationStatus(request: enabled) } }
            HStack {
                Text(companion.notificationStatus).font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button("알림 설정") {
                    if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") { NSWorkspace.shared.open(url) }
                }.controlSize(.small)
            }
            if let message = companion.message { Text(message).font(.system(size: 11)).foregroundStyle(.secondary) }
        }
    }
}

struct PresenceIndicator: View {
    let status: String
    var body: some View {
        Group {
            if status == "running" { ProgressView().controlSize(.mini).scaleEffect(0.75).frame(width: 15, height: 15) }
            else { Image(systemName: status == "waiting" ? "hand.raised.fill" : status == "completed" ? "checkmark.circle.fill" : status == "error" ? "exclamationmark.circle.fill" : "circle").foregroundStyle(status == "error" ? Color.red : status == "waiting" ? Color.orange : status == "completed" ? Color.green : Color.secondary).frame(width: 15, height: 15) }
        }.accessibilityLabel(presenceLabel(status))
    }
}

func presenceLabel(_ state: String) -> String {
    state == "waiting" ? "대기 중" : Palette.status(state)
}

struct CompanionOverlayView: View {
    @ObservedObject var companion: AgentCompanion
    @StateObject private var bubble = CompanionBubbleController()
    @StateObject private var motion = CompanionPetMotion()
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @ViewState private var epoch = Date()
    @ViewState private var celebrating = false
    var body: some View {
        VStack(spacing: 2) {
            if let approval = companion.visibleApproval {
                CompanionApprovalBubble(companion: companion, approval: approval)
            } else if companion.preferences.showsTask, bubble.isVisible, let current = companion.shown {
                CompanionTaskBubble(companion: companion, current: current)
            } else { Color.clear.frame(height: 86) }
            Button(action: toggleBubble) {
                TimelineView(.animation(minimumInterval: 1.0 / 12.0, paused: companion.preferences.reducedMotion || systemReduceMotion)) { timeline in
                    if let pet = companion.selectedPet,
                       let image = pet.frame(row: animationRow, elapsed: timeline.date.timeIntervalSince(epoch), reducedMotion: companion.preferences.reducedMotion || systemReduceMotion) {
                        Image(nsImage: image).resizable().interpolation(.high).scaledToFit()
                    } else { Image(systemName: "pawprint.fill").resizable().scaledToFit().foregroundStyle(Palette.accent).padding(35) }
                }.frame(width: 125, height: 135)
            }.buttonStyle(.plain).accessibilityLabel(companion.preferences.showsTask && bubble.isVisible ? "작업 말풍선 숨기기" : "작업 말풍선 보기").accessibilityIdentifier("pet-toggle-bubble")
                .background(CompanionPetInteraction(motion: motion, row: animationRow, onClick: toggleBubble).allowsHitTesting(false))
                .contextMenu { Button("펫 숨기기") { companion.preferences.enabled = false }; Button("에이전트 열기") { companion.focus(companion.shown?.id) } }
        }.padding(8).frame(width: panelSize.width, height: panelSize.height, alignment: .bottom)
            .onChange(of: CompanionBubbleIdentity(companion.shown), initial: true) { _, identity in bubble.synchronize(identity) }
            .onChange(of: animationRow) { _, _ in epoch = Date() }
            .onDisappear { motion.endAfterTeardown() }
            .task(id: CompanionBubbleIdentity(companion.shown)) {
                celebrating = companion.shown?.status == "completed"
                if celebrating {
                    do { try await Task.sleep(for: .seconds(6)) } catch { return }
                    celebrating = false
                }
            }
    }
    private var panelSize: CGSize {
        let size = companion.bubbleSize
        return CompanionBubbleLayout.panelSize(width: size.width, height: size.height, tall: companion.visibleApproval != nil)
    }
    private func toggleBubble() {
        guard !motion.isDragging else { return }
        if !companion.preferences.showsTask { companion.preferences.showsTask = true; bubble.show() }
        else { bubble.toggle() }
    }
    private var animationRow: Int {
        (motion.direction ?? CompanionPetAnimationPolicy.task(companion.shown, celebrating: celebrating)).rawValue
    }
}

/// One agent's request and current work. With several agents busy, the pager
/// row (or a sideways drag or two-finger swipe) turns to the others.
struct CompanionTaskBubble: View {
    @ObservedObject var companion: AgentCompanion
    let current: AgentPresence
    /// Set while a sideways drag is turning the page, so its mouse-up does not also open the agent.
    @ViewState private var turning = false
    @ViewState private var measuredHeight: CGFloat = 0
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            TimelineView(.periodic(from: .now, by: 1)) { timeline in
                Button { if turning { turning = false } else { companion.focus(current.id) } } label: { content }
                    .simultaneousGesture(pageDrag)
                    .buttonStyle(.plain)
                    .accessibilityLabel(Self.spokenLabel(current, at: timeline.date))
                    .accessibilityIdentifier("pet-task-bubble")
            }
            CompanionPager(companion: companion)
        }
        .padding(12).foregroundStyle(.primary)
        .frame(width: size.resolvedWidth, height: size.resolvedHeight, alignment: .top)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18))
        .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(Color.primary.opacity(0.10)))
        .overlay(CompanionBubbleResizer(companion: companion, measuredHeight: measuredHeight, allowsHeight: true))
        .background(GeometryReader { geo in Color.clear.onAppear { measuredHeight = geo.size.height }.onChange(of: geo.size.height) { _, new in measuredHeight = new } })
    }
    private var size: CompanionBubbleSize { companion.bubbleSize }
    /// A taller bubble shows more of the request and the work instead of padding.
    private var lines: Int? { size.resolvedHeight == nil ? 2 : nil }

    /// On the bubble's own button only; the pager's chevrons stay plain clicks.
    private var pageDrag: some Gesture {
        DragGesture(minimumDistance: 18)
            .onChanged { _ in if companion.page != nil { turning = true } }
            .onEnded { drag in
                let width = drag.translation.width
                if companion.page != nil, abs(width) > 36, abs(width) > abs(drag.translation.height) { companion.showNeighbour(width < 0 ? 1 : -1) }
                // The button's action may run before or after this; either way the flag is gone by the next click.
                DispatchQueue.main.async { turning = false }
            }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 7) {
                PresenceIndicator(status: current.status)
                // Several agents can be busy at once; the workspace says which one this is.
                VStack(alignment: .leading, spacing: 1) {
                    if !current.workspace.isEmpty {
                        Text(current.workspace).font(.system(size: 9, weight: .medium)).foregroundStyle(.secondary).lineLimit(1)
                            .accessibilityIdentifier("pet-workspace-\(current.id)")
                    }
                    Text(current.title).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                }
                Spacer(minLength: 0)
                VStack(alignment: .trailing, spacing: 2) {
                    Text(presenceLabel(current.status)).font(.system(size: 9)).foregroundStyle(.secondary)
                    if let timing = current.timing { AgentElapsedView(timing: timing).accessibilityIdentifier("pet-elapsed-\(current.id)") }
                }
            }
            if let input = current.input, !input.isEmpty {
                HStack(alignment: .top, spacing: 6) {
                    Text("요청").font(.system(size: 9, weight: .medium)).foregroundStyle(.secondary).padding(.top, 2)
                    Text(input).font(.system(size: 11)).lineLimit(lines).frame(maxWidth: .infinity, alignment: .leading)
                }
                Divider()
            }
            HStack(alignment: .top, spacing: 6) {
                Text("작업").font(.system(size: 9, weight: .medium)).foregroundStyle(.secondary).padding(.top, 2)
                Text(current.summary).font(.system(size: 11)).lineLimit(lines).frame(maxWidth: .infinity, alignment: .leading)
                    .layoutPriority(1)
            }
        }
        .contentShape(Rectangle())
    }

    static func spokenLabel(_ agent: AgentPresence, at date: Date) -> String {
        let place = agent.workspace.isEmpty ? "" : agent.workspace + " 워크스페이스의 "
        let elapsed = agent.timing.map { "실행 시간 " + $0.label(at: date) + ". " } ?? ""
        return place + agent.title + " 열기. " + elapsed + "요청: " + (agent.input ?? "없음") + ". 작업: " + agent.summary
    }
}

/// "‹ ● ○ ○ 1 / 3 ›" under a bubble while more than one agent is busy.
struct CompanionPager: View {
    @ObservedObject var companion: AgentCompanion
    var body: some View {
        if let page = companion.page { pager(page.position, of: page.count) }
    }

    private func pager(_ position: Int, of count: Int) -> some View {
        HStack(spacing: 6) {
            pageButton("chevron.left", offset: -1, label: "이전 에이전트")
            Spacer(minLength: 0)
            if count <= 8 {
                HStack(spacing: 4) {
                    ForEach(1...count, id: \.self) { index in
                        Circle().fill(index == position ? Palette.accent : Color.primary.opacity(0.22)).frame(width: 5, height: 5)
                    }
                }.accessibilityHidden(true)
            }
            Text("\(position) / \(count)").font(.system(size: 9, weight: .medium)).monospacedDigit().foregroundStyle(.secondary)
                .accessibilityLabel("작업 중인 에이전트 \(count)개 중 \(position)번째")
                .accessibilityIdentifier("pet-page-label")
            if companion.hiddenApproval != nil {
                Button { companion.showApprovalAgent() } label: {
                    Image(systemName: "hand.raised.fill").font(.system(size: 9)).foregroundStyle(.orange).frame(width: 16, height: 16).contentShape(Rectangle())
                }
                .buttonStyle(.plain).help("다른 에이전트가 승인을 기다립니다 · 눌러서 보기").accessibilityLabel("승인을 기다리는 에이전트 보기")
                .accessibilityIdentifier("pet-page-approval")
            }
            Spacer(minLength: 0)
            pageButton("chevron.right", offset: 1, label: "다음 에이전트")
        }
    }

    private func pageButton(_ symbol: String, offset: Int, label: String) -> some View {
        Button { companion.showNeighbour(offset) } label: {
            Image(systemName: symbol).font(.system(size: 9, weight: .semibold)).frame(width: 22, height: 16)
                .background(Palette.subtle, in: Capsule()).contentShape(Capsule())
        }
        .buttonStyle(.plain).help(label).accessibilityLabel(label)
        .accessibilityIdentifier(offset < 0 ? "pet-page-previous" : "pet-page-next")
    }
}

/// Approvals and single-choice questions can be answered from the pet without
/// activating the main window. Everything else opens the pane.
struct CompanionApprovalBubble: View {
    @ObservedObject var companion: AgentCompanion
    let approval: CompanionApproval
    var body: some View {
        let presentation = approval.presentation
        let questionnaire = approval.quickQuestionnaire
        let question = questionnaire.flatMap { companion.questionProgress.current(in: $0) }
        return VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Image(systemName: questionnaire == nil ? "hand.raised.fill" : "questionmark.bubble.fill").foregroundStyle(.orange)
                Text(Self.title(questionnaire, progress: companion.questionProgress)).font(.system(size: 11, weight: .semibold)).monospacedDigit()
                    .accessibilityIdentifier("pet-question-progress")
                Spacer(minLength: 0)
                Text(approval.workspaceName.isEmpty ? approval.sessionTitle : approval.workspaceName + " · " + approval.sessionTitle)
                    .font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
            }
            if let question {
                Text(question.question).font(.system(size: 11, weight: .medium)).lineLimit(3).fixedSize(horizontal: false, vertical: true)
                ForEach(Array(question.options.enumerated()), id: \.offset) { index, option in
                    CompanionOptionRow(option: option, multiple: question.multiSelect, picked: Self.picked(option.label, question: question, progress: companion.questionProgress)) {
                        companion.chooseOption(option.label)
                    }.accessibilityIdentifier("pet-choice-\(index)")
                }
            } else {
                Text(presentation.title + " · " + approval.request.toolName).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                if let headline = presentation.headline {
                    Text(headline).font(.system(size: 11, weight: .medium)).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                }
                if let code = presentation.primaryCode {
                    Text(code.value).font(.system(size: 10, design: .monospaced)).lineLimit(3)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 7).padding(.vertical, 5)
                        .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
                } else if presentation.headline == nil {
                    Text(approval.request.summary).font(.system(size: 10, design: .monospaced)).lineLimit(3)
                }
            }
            if let error = companion.approvalError { Text(error).font(.system(size: 9)).foregroundStyle(.red).lineLimit(2) }
            HStack(spacing: 6) {
                // Four buttons share 234pt while a questionnaire is up, so 열기 shrinks to its icon there.
                Button { companion.openApproval() } label: {
                    if questionnaire == nil { Text("열기") } else { Image(systemName: "arrow.up.forward.app") }
                }
                .help("실행 창에서 열기").accessibilityLabel("실행 창에서 열기").accessibilityIdentifier("pet-approval-open")
                Spacer(minLength: 0)
                if companion.approvalBusy { ProgressView().controlSize(.mini) }
                if let question, let questionnaire {
                    if companion.questionProgress.index > 0 {
                        Button("이전") { companion.previousQuestion() }.accessibilityIdentifier("pet-question-back")
                    }
                    if companion.needsCommitButton(question, in: questionnaire) {
                        Button(Self.commitTitle(questionnaire, progress: companion.questionProgress)) { companion.commitQuestion() }
                            .buttonStyle(.borderedProminent).disabled(!companion.canCommit(question))
                            .accessibilityIdentifier("pet-question-next")
                    }
                    Button("취소") { companion.answerApproval(allow: false) }.accessibilityIdentifier("pet-approval-cancel")
                } else if questionnaire == nil {
                    Button("거부") { companion.answerApproval(allow: false) }.accessibilityIdentifier("pet-approval-deny")
                    Button("이번만 허용") { companion.answerApproval(allow: true) }
                        .buttonStyle(.borderedProminent).disabled(!approval.request.canAllow)
                        .accessibilityIdentifier("pet-approval-allow")
                } else {
                    Button("취소") { companion.answerApproval(allow: false) }.accessibilityIdentifier("pet-approval-cancel")
                }
            }.controlSize(.small).disabled(companion.approvalBusy)
            CompanionPager(companion: companion)
        }
        .padding(12).frame(width: CompanionBubbleLayout.approvalWidth(companion.bubbleSize.width))
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18))
        .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(Color.orange.opacity(0.45)))
        // The approval keeps its own height; its sides still set the shared width.
        .overlay(CompanionBubbleResizer(companion: companion, measuredHeight: nil, allowsHeight: false))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(approval.workspaceName.isEmpty ? "" : approval.workspaceName + " 워크스페이스의 ")\(approval.sessionTitle) 승인 요청: \(presentation.headline ?? approval.request.summary)")
        .accessibilityIdentifier("pet-approval-bubble")
    }

    /// "선택 요청 2/3" while stepping through several questions.
    static func title(_ questionnaire: UserQuestionnaire?, progress: QuestionnaireProgress) -> String {
        guard let questionnaire else { return "승인 요청" }
        let total = questionnaire.questions.count
        return total > 1 ? "선택 요청 \(progress.index + 1)/\(total)" : "선택 요청"
    }
    static func commitTitle(_ questionnaire: UserQuestionnaire, progress: QuestionnaireProgress) -> String {
        progress.index + 1 < questionnaire.questions.count ? "다음" : "보내기"
    }
    /// Toggled picks for a multi-choice question; the recorded answer when the user came back to one.
    static func picked(_ label: String, question: UserQuestionnaire.Question, progress: QuestionnaireProgress) -> Bool {
        if question.multiSelect { return progress.selected.contains(label) }
        return progress.answers[question.question]?.selectedOptions.contains(label) == true
    }
}

private struct CompanionOptionRow: View {
    let option: UserQuestionnaire.Option
    let multiple: Bool
    let picked: Bool
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 6) {
                if multiple { Image(systemName: picked ? "checkmark.square.fill" : "square").font(.system(size: 10)).foregroundStyle(picked ? Palette.accent : Color.secondary).padding(.top, 1) }
                VStack(alignment: .leading, spacing: 1) {
                    Text(option.label).font(.system(size: 11, weight: .medium)).lineLimit(1)
                    if !option.description.isEmpty { Text(option.description).font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1) }
                }
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 8).padding(.vertical, 5)
            .background(picked ? Palette.accent.opacity(0.16) : Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain).accessibilityLabel(option.label).accessibilityValue(picked ? "선택됨" : "선택 안 됨")
    }
}

@MainActor
final class CompanionPanel {
    // Room for the pager row under either bubble; the extra band is transparent.
    static let baseHeight = CompanionBubbleLayout.baseHeight
    static let tallHeight = CompanionBubbleLayout.tallHeight
    /// The pet and its padding; swipes below this line are over the pet, not a bubble.
    static let bubbleFloor: CGFloat = 143
    private let panel: NSPanel
    private var pager = SwipePager()
    nonisolated(unsafe) private var scrollMonitor: Any?
    init(companion: AgentCompanion) {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 282, height: Self.baseHeight), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "MightyClaude Pet"
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.isMovableByWindowBackground = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: CompanionOverlayView(companion: companion))
        let visible = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        panel.setFrameOrigin(NSPoint(x: visible.maxX - 300, y: visible.minY + 24))
        panel.setFrameAutosaveName("MightyClaudeCompanion")
        if !NSScreen.screens.contains(where: { $0.visibleFrame.intersects(panel.frame) }) {
            panel.setFrameOrigin(NSPoint(x: visible.maxX - 300, y: visible.minY + 24))
        }
        // A two-finger sideways swipe over the pet turns to the next busy agent.
        scrollMonitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self, weak companion] event in
            // Only over a bubble: a stray scroll on the pet must not reopen one the user closed.
            guard let self, event.window === self.panel, event.momentumPhase.isEmpty, event.locationInWindow.y > Self.bubbleFloor,
                  companion?.page != nil else { return event }
            let phase: SwipePager.Phase = event.phase.contains(.began) ? .began : event.phase.contains(.ended) || event.phase.contains(.cancelled) ? .ended : event.phase.isEmpty ? .none : .changed
            // A wheel without precise deltas reports lines, not points.
            let scale: CGFloat = event.hasPreciseScrollingDeltas ? 1 : 12
            let turn = self.pager.feed(deltaX: event.scrollingDeltaX * scale, deltaY: event.scrollingDeltaY * scale, phase: phase, at: event.timestamp)
            if turn != 0 { companion?.showNeighbour(turn) }
            return event
        }
    }
    func setVisible(_ visible: Bool) {
        if visible { panel.orderFrontRegardless() }
        else { CompanionPetInteraction.cancel(in: panel.contentView); panel.orderOut(nil) }
    }
    /// Which side of the window stays where it is when its width changes.
    enum Side { case left, right, center }
    /// Sizes the window around the bubble. It always grows upward; sideways it
    /// keeps `side` where it is, so a dragged edge follows the pointer.
    func fit(bubble: CompanionBubbleSize, tall: Bool, keeping side: Side) {
        let size = CompanionBubbleLayout.panelSize(width: bubble.width, height: bubble.height, tall: tall)
        let frame = panel.frame
        guard abs(frame.width - size.width) > 0.5 || abs(frame.height - size.height) > 0.5 else { return }
        let x = switch side {
        case .left: frame.minX
        case .right: frame.maxX - size.width
        case .center: frame.midX - size.width / 2
        }
        panel.setFrame(NSRect(x: x, y: frame.minY, width: size.width, height: size.height), display: true)
    }
    deinit { if let scrollMonitor { NSEvent.removeMonitor(scrollMonitor) } }
    func close() {
        if let scrollMonitor { NSEvent.removeMonitor(scrollMonitor) }
        scrollMonitor = nil
        CompanionPetInteraction.cancel(in: panel.contentView); panel.close()
    }
}

/// Thin strips along the bubble's top, left and right edges that resize it.
/// The bottom meets the pet, so it has none. They cover only the edges, so
/// the bubble's own buttons keep every click. A double click restores the
/// default size.
struct CompanionBubbleResizer: View {
    @ObservedObject var companion: AgentCompanion
    /// The bubble's drawn height, the start of a first vertical drag.
    let measuredHeight: CGFloat?
    let allowsHeight: Bool
    static let band: CGFloat = 6
    @ViewState private var drag: (edges: ResizeEdges, start: NSPoint, size: CGSize)?

    var body: some View {
        ZStack {
            strip(.left).frame(width: Self.band).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            strip(.right).frame(width: Self.band).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .trailing)
            if allowsHeight {
                strip(.top).frame(height: Self.band).padding(.horizontal, Self.band * 2).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                strip([.left, .top]).frame(width: Self.band * 2, height: Self.band * 2).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                strip([.right, .top]).frame(width: Self.band * 2, height: Self.band * 2).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
            }
        }
        .onDisappear { if drag != nil { drag = nil; companion.cancelBubbleResize(keeping: .left) } }
    }

    private func strip(_ edges: ResizeEdges) -> some View {
        Color.clear.contentShape(Rectangle())
            .onHover { inside in (inside ? MightyGraphInteractionProbe.cursor(for: edges) : NSCursor.arrow).set() }
            .onTapGesture(count: 2) { companion.resizeBubble(to: CompanionBubbleSize(), keeping: .center, finished: true) }
            .gesture(DragGesture(minimumDistance: 1)
                .onChanged { _ in resize(edges, finished: false) }
                .onEnded { _ in resize(edges, finished: true) })
    }

    /// Measured in screen points from the pointer itself: the window moves
    /// under it while it resizes, so view coordinates would feed back.
    private func resize(_ edges: ResizeEdges, finished: Bool) {
        let side: CompanionPanel.Side = edges.contains(.left) ? .right : .left
        if drag == nil {
            let size = companion.bubbleSize
            drag = (edges, NSEvent.mouseLocation, CGSize(width: size.resolvedWidth, height: size.resolvedHeight ?? measuredHeight ?? CompanionBubbleLayout.minimum.height))
        }
        guard let current = drag else { return }
        let now = NSEvent.mouseLocation
        let delta = CGSize(width: now.x - current.start.x, height: current.start.y - now.y)
        let size = edges.size(from: current.size, delta: delta, minimum: CompanionBubbleLayout.minimum, maximum: CompanionBubbleLayout.maximum)
        // A sideways drag leaves a content-following height as it was.
        let next = CompanionBubbleSize(width: size.width, height: edges.vertical ? size.height : companion.bubbleSize.height)
        guard finished else { companion.resizeBubble(to: next, keeping: side, finished: false); return }
        drag = nil
        if size == current.size { companion.cancelBubbleResize(keeping: side) }
        else { companion.resizeBubble(to: next, keeping: side, finished: true) }
    }
}
