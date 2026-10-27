import SwiftUI
import MightyCore

/// Requests waiting behind the current run, shown above the composer. Each row
/// can be removed; when the pane is idle (a run ended in error) the first row
/// can be started by hand. Delegation's held notices and follow-ups come
/// first: they cannot be removed, and run next stays on offer while any are
/// held.
struct QueuedInputsView: View {
    let sessionID: String
    var held: [DelegationDeliveryItem] = []
    let items: [QueuedInput]
    let running: Bool
    let onRemove: (String) -> Void
    let onRunNext: () -> Void
    /// Why the queue waits beyond the current turn: its background work (§1.17.4).
    var notice: String? = nil
    /// Run next can hand the first request into the run that only waits on background work.
    var steersRunNext = false
    /// When a request last joined the queue (`AppStore.queueAddedAt`): the queue flashes the
    /// accent wash for `Motion.highlight` from then (natural submit B3).
    var addedAt: Date? = nil
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
                if DelegationQueueRows.offersRunNext(busy: running, held: held.count, steers: steersRunNext) {
                    Button(action: onRunNext) { Label(L("queue.runNext"), systemImage: "play.fill").font(.ui(size: 10, weight: .medium)) }
                        .buttonStyle(.plain).foregroundStyle(Palette.accent)
                        .help(L("queue.runNextHelp")).accessibilityIdentifier("queue-run-\(sessionID)")
                }
            }
            if let notice {
                Text(verbatim: notice).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("queue-background-\(sessionID)")
            }
            ForEach(Array(held.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Text("\(index + 1)").font(.ui(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    Text(item.text).font(.ui(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                    // Held, not the human's request: no remove button.
                    Image(systemName: "pin.fill").font(.ui(size: 9, weight: .semibold)).frame(width: 18, height: 16)
                        .foregroundStyle(.secondary).help(L("queue.heldHelp")).accessibilityLabel(L("queue.held"))
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .combine).accessibilityIdentifier("queue-held-\(item.id)")
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
                    Button { onRemove(item.id) } label: { Image(systemName: "xmark").font(.ui(size: 9, weight: .semibold)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help(L("queue.removeHelp")).accessibilityLabel(L("queue.remove"))
                        .accessibilityIdentifier("queue-remove-\(item.id)")
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .combine).accessibilityIdentifier("queue-item-\(item.id)")
                .transition(rowTransition)
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
