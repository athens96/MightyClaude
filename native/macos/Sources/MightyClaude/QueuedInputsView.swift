import SwiftUI
import MightyCore

/// Requests waiting behind the current run, shown above the composer. Each row
/// can be removed, and each has 바로 실행 before its X (``QueueRunNow``): a new
/// worktree child where one can be made, otherwise this pane now when it can
/// take the row, drawn disabled with the reason when the row must wait.
/// Delegation's held notices and follow-ups come first: they cannot be
/// removed, and the first one's 바로 실행 sends them all.
struct QueuedInputsView: View {
    let sessionID: String
    var held: [DelegationDeliveryItem] = []
    let items: [QueuedInput]
    /// The pane is busy (running, starting, or held for an update).
    let running: Bool
    let onRemove: (String) -> Void
    /// Why the queue waits beyond the current turn: its background work (§1.17.4).
    var notice: String? = nil
    /// When a request last joined the queue (`AppStore.queueAddedAt`): the queue flashes the
    /// accent wash for `Motion.highlight` from then (natural submit B3).
    var addedAt: Date? = nil
    /// What 바로 실행 does on each row, by row id (`AppStore.queueRunNow`); a row not in it has no button.
    var runNow: [String: QueueRunNow] = [:]
    /// 바로 실행 on a row: its id, and the agent picked from its menu (nil: the pane's own, or here).
    var onRunNow: (String, String?) -> Void = { _, _ in }
    /// The agent menu's rows (`AppStore.worktreeChildChoices`), empty when the pane offers no worktree menu.
    var agentChoices: [DelegationChildChoice] = []
    /// The pane's own agent, which the menu names without its mode's ⌘Enter mark.
    var parentProvider: String = ""
    /// A modal is up: every 바로 실행 is disabled, without a reason.
    var disabled = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        // Drawn again only when the flash starts and ends, never frame by frame.
        TimelineView(QueueFlashSchedule(addedAt: addedAt)) { context in
            let flashing = addedAt.map { context.date >= $0 && context.date < $0 + DesignMetrics.Motion.seconds(DesignMetrics.Motion.highlight) } ?? false
            rows
                .background {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(flashing ? Palette.accentSoft : Color.clear)
                        .padding(-DesignMetrics.Spacing.xs)
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                }
                .animation(PendingMotion.animation(DesignMetrics.Motion.quick, reduceMotion: false), value: flashing)
        }
        // A new row comes in over `Motion.standard`; under Reduce Motion it fades in without moving.
        .animation(PendingMotion.animation(DesignMetrics.Motion.standard, reduceMotion: false), value: items.map(\.id))
        .accessibilityIdentifier("queue-\(sessionID)")
    }

    private var rowTransition: AnyTransition {
        reduceMotion ? .opacity : .move(edge: .bottom).combined(with: .opacity)
    }

    private var rows: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "clock.arrow.circlepath").font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.accent)
                Text(running ? L("queue.waitingBusy", ["count": "\(held.count + items.count)"]) : L("queue.waiting", ["count": "\(held.count + items.count)"])).font(.ui(size: 10, weight: .medium)).foregroundStyle(.secondary)
                Spacer(minLength: 0)
            }
            if let notice {
                Text(verbatim: notice).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("queue-background-\(sessionID)")
            }
            ForEach(Array(held.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Text("\(index + 1)").font(.ui(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    Text(item.text).font(.ui(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                    // The first held row's 바로 실행 sends them all; the others draw nothing (`.hidden`).
                    runNowButton(item.id)
                    // Held, not the human's request: no remove button.
                    Image(systemName: "pin.fill").font(.ui(size: 9, weight: .semibold)).frame(width: 18, height: 16)
                        .foregroundStyle(.secondary).help(L("queue.heldHelp")).accessibilityLabel(L("queue.held"))
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .contain).accessibilityIdentifier("queue-held-\(item.id)")
            }
            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Text("\(held.count + index + 1)").font(.ui(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        Text(item.text.isEmpty ? L("queue.attachmentsOnly") : item.text).font(.ui(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                        if !item.attachments.isEmpty {
                            Text(item.attachments.map(\.name).joined(separator: ", ")).font(.ui(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    runNowButton(item.id)
                    Button { onRemove(item.id) } label: { Image(systemName: "xmark").font(.ui(size: 9, weight: .semibold)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help(L("queue.removeHelp")).accessibilityLabel(L("queue.remove"))
                        .accessibilityIdentifier("queue-remove-\(item.id)")
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .contain).accessibilityIdentifier("queue-item-\(item.id)")
                .transition(rowTransition)
            }
        }
    }

    /// 바로 실행 beside a row's X (or the first held row's pin): what `QueueRunNow` decided for the row, in the old 다음 실행's
    /// look. A row that cannot run now keeps its slot with the button disabled; SwiftUI shows no help on a disabled control,
    /// so the reason sits on a wrapper.
    @ViewBuilder private func runNowButton(_ itemId: String) -> some View {
        let path = runNow[itemId] ?? .hidden
        let branch = path == .worktree || path == .agentMenu
        let label = Label(L("queue.runNow"), systemImage: branch ? "arrow.triangle.branch" : "play.fill").font(.ui(size: 10, weight: .medium))
        switch path {
        case .hidden:
            EmptyView()
        case .waitsForTurn, .waitsForHeld, .waitsForUpdate:
            HStack(spacing: 0) {
                Button {} label: { label }.buttonStyle(.plain).foregroundStyle(Palette.ink2).disabled(true)
            }
            .contentShape(Rectangle()).help(help(path))
            .accessibilityElement(children: .combine).accessibilityHint(help(path))
            .accessibilityIdentifier("queue-run-now-\(itemId)")
        case .agentMenu:
            Menu { agentMenu(itemId) } label: { label }
                .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
                .foregroundStyle(Palette.accent).disabled(disabled).help(L("queue.worktreeMenuHintMac"))
                .accessibilityIdentifier("queue-run-now-\(itemId)")
        case .worktree:
            // A right-click or Control-click picks the child's agent, as on the composer's worktree button.
            Button { onRunNow(itemId, nil) } label: { label }
                .buttonStyle(.plain).foregroundStyle(Palette.accent).disabled(disabled).help(help(path))
                .contextMenu { agentMenu(itemId) }
                .accessibilityIdentifier("queue-run-now-\(itemId)")
        case .here, .steer, .held:
            Button { onRunNow(itemId, nil) } label: { label }
                .buttonStyle(.plain).foregroundStyle(Palette.accent).disabled(disabled).help(help(path))
                .accessibilityIdentifier("queue-run-now-\(itemId)")
        }
    }

    private func help(_ path: QueueRunNow) -> String {
        switch path {
        case .worktree: return L("queue.runNowWorktreeHelpMac")
        case .agentMenu: return L("queue.worktreeMenuHintMac")
        case .steer: return L("queue.runNowSteerHelp")
        case .held: return L("queue.runNowHeldHelp", ["count": "\(held.count)"])
        case .waitsForTurn: return L("queue.runNowUnavailableHelp")
        case .waitsForHeld: return L("queue.runNowAfterHeldHelp")
        case .waitsForUpdate: return L("queue.runNowUpdateHelp")
        case .here, .hidden: return L("queue.runNowHereHelp")
        }
    }

    /// The composer worktree menu's rows without the ⌘Enter mark: a pick runs the row in a new child of that agent.
    @ViewBuilder private func agentMenu(_ itemId: String) -> some View {
        Section(L("composer.worktreeMenu.title")) {
            ForEach(agentChoices) { choice in
                Button { onRunNow(itemId, choice.agent) } label: {
                    Label { Text(ProviderOptions.betaTitle(choice.agent, SessionPaneView.worktreeChoiceTitle(choice, parent: parentProvider, marksShortcut: false))) } icon: {
                        if let image = ProviderIconImage.image(provider: choice.agent, pointSize: 12) { Image(nsImage: image) }
                    }
                }
                .disabled(choice.blocked != nil || disabled)
                .accessibilityIdentifier("queue-run-now-\(choice.agent)-\(itemId)")
            }
        }
    }
}

/// The queue's flash (`QueuedInputsView.addedAt`): drawn when it starts and when it is over.
private struct QueueFlashSchedule: TimelineSchedule {
    let addedAt: Date?
    func entries(from startDate: Date, mode: TimelineScheduleMode) -> [Date] {
        guard let addedAt else { return [startDate] }
        return [startDate] + [addedAt, addedAt + DesignMetrics.Motion.seconds(DesignMetrics.Motion.highlight)].filter { $0 > startDate }
    }
}
