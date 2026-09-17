import AppKit
import SwiftUI

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
            if let approval = companion.approval {
                CompanionApprovalBubble(companion: companion, approval: approval)
            } else if companion.preferences.showsTask, bubble.isVisible, let current = companion.current {
                TimelineView(.periodic(from: .now, by: 1)) { timeline in
                Button { companion.focus(current.id) } label: {
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
                                Text(input).font(.system(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                            }
                            Divider()
                        }
                        HStack(alignment: .top, spacing: 6) {
                            Text("작업").font(.system(size: 9, weight: .medium)).foregroundStyle(.secondary).padding(.top, 2)
                            Text(current.summary).font(.system(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }.padding(12).foregroundStyle(.primary)
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18))
                        .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(Color.primary.opacity(0.10)))
                }.buttonStyle(.plain).frame(width: 258)
                    .accessibilityLabel("\(current.workspace.isEmpty ? "" : current.workspace + " 워크스페이스의 ")\(current.title) 열기. \(current.timing.map { "실행 시간 " + $0.label(at: timeline.date) + ". " } ?? "")요청: \(current.input ?? "없음"). 작업: \(current.summary)")
                    .accessibilityIdentifier("pet-task-bubble")
                }
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
                .contextMenu { Button("펫 숨기기") { companion.preferences.enabled = false }; Button("에이전트 열기") { companion.focus(companion.current?.id) } }
        }.padding(8).frame(width: 282, height: companion.approval == nil ? 306 : CompanionPanel.tallHeight, alignment: .bottom)
            .onChange(of: CompanionBubbleIdentity(companion.current), initial: true) { _, identity in bubble.synchronize(identity) }
            .onChange(of: animationRow) { _, _ in epoch = Date() }
            .onDisappear { motion.endAfterTeardown() }
            .task(id: CompanionBubbleIdentity(companion.current)) {
                celebrating = companion.current?.status == "completed"
                if celebrating {
                    do { try await Task.sleep(for: .seconds(6)) } catch { return }
                    celebrating = false
                }
            }
    }
    private func toggleBubble() {
        guard !motion.isDragging else { return }
        if !companion.preferences.showsTask { companion.preferences.showsTask = true; bubble.show() }
        else { bubble.toggle() }
    }
    private var animationRow: Int {
        (motion.direction ?? CompanionPetAnimationPolicy.task(companion.current, celebrating: celebrating)).rawValue
    }
}

/// Approvals and single-choice questions can be answered from the pet without
/// activating the main window. Everything else opens the pane.
struct CompanionApprovalBubble: View {
    @ObservedObject var companion: AgentCompanion
    let approval: CompanionApproval
    var body: some View {
        let presentation = approval.presentation
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Image(systemName: approval.quickChoices == nil ? "hand.raised.fill" : "questionmark.bubble.fill").foregroundStyle(.orange)
                Text(approval.quickChoices == nil ? "승인 요청" : "선택 요청").font(.system(size: 11, weight: .semibold))
                Spacer(minLength: 0)
                Text(approval.workspaceName.isEmpty ? approval.sessionTitle : approval.workspaceName + " · " + approval.sessionTitle)
                    .font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
            }
            if let question = approval.quickChoices {
                Text(question.question).font(.system(size: 11, weight: .medium)).lineLimit(3).fixedSize(horizontal: false, vertical: true)
                ForEach(Array(question.options.enumerated()), id: \.offset) { index, option in
                    Button { companion.answerApprovalChoice(option.label) } label: {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(option.label).font(.system(size: 11, weight: .medium)).lineLimit(1)
                            if !option.description.isEmpty { Text(option.description).font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1) }
                        }.frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 8).padding(.vertical, 5)
                            .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                    }.buttonStyle(.plain).accessibilityIdentifier("pet-choice-\(index)")
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
                Button("열기") { companion.openApproval() }.accessibilityIdentifier("pet-approval-open")
                Spacer(minLength: 0)
                if companion.approvalBusy { ProgressView().controlSize(.mini) }
                if approval.quickChoices == nil {
                    Button("거부") { companion.answerApproval(allow: false) }.accessibilityIdentifier("pet-approval-deny")
                    Button("이번만 허용") { companion.answerApproval(allow: true) }
                        .buttonStyle(.borderedProminent).disabled(!approval.request.canAllow)
                        .accessibilityIdentifier("pet-approval-allow")
                } else {
                    Button("취소") { companion.answerApproval(allow: false) }.accessibilityIdentifier("pet-approval-cancel")
                }
            }.controlSize(.small).disabled(companion.approvalBusy)
        }
        .padding(12).frame(width: 258)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18))
        .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(Color.orange.opacity(0.45)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(approval.workspaceName.isEmpty ? "" : approval.workspaceName + " 워크스페이스의 ")\(approval.sessionTitle) 승인 요청: \(presentation.headline ?? approval.request.summary)")
        .accessibilityIdentifier("pet-approval-bubble")
    }
}

@MainActor
final class CompanionPanel {
    static let baseHeight: CGFloat = 306
    static let tallHeight: CGFloat = 470
    private let panel: NSPanel
    init(companion: AgentCompanion) {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 282, height: 306), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
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
    }
    func setVisible(_ visible: Bool) {
        if visible { panel.orderFrontRegardless() }
        else { CompanionPetInteraction.cancel(in: panel.contentView); panel.orderOut(nil) }
    }
    /// Grow upward for an approval bubble; the pet keeps its bottom-left origin.
    func setTall(_ tall: Bool) {
        let height = tall ? Self.tallHeight : Self.baseHeight
        guard abs(panel.frame.height - height) > 0.5 else { return }
        panel.setFrame(NSRect(x: panel.frame.minX, y: panel.frame.minY, width: panel.frame.width, height: height), display: true)
    }
    func close() { CompanionPetInteraction.cancel(in: panel.contentView); panel.close() }
}
