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

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "clock.arrow.circlepath").font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.accent)
                Text(running ? L("queue.waitingBusy", ["count": "\(held.count + items.count)"]) : L("queue.waiting", ["count": "\(held.count + items.count)"])).font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if DelegationQueueRows.offersRunNext(busy: running, held: held.count) {
                    Button(action: onRunNext) { Label(L("queue.runNext"), systemImage: "play.fill").font(.system(size: 10, weight: .medium)) }
                        .buttonStyle(.plain).foregroundStyle(Palette.accent)
                        .help(L("queue.runNextHelp")).accessibilityIdentifier("queue-run-\(sessionID)")
                }
            }
            if let notice {
                Text(verbatim: notice).font(.system(size: 10)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("queue-background-\(sessionID)")
            }
            ForEach(Array(held.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Text("\(index + 1)").font(.system(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    Text(item.text).font(.system(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                    // Held, not the human's request: no remove button.
                    Image(systemName: "pin.fill").font(.system(size: 9, weight: .semibold)).frame(width: 18, height: 16)
                        .foregroundStyle(.secondary).help(L("queue.heldHelp")).accessibilityLabel(L("queue.held"))
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .combine).accessibilityIdentifier("queue-held-\(item.id)")
            }
            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Text("\(held.count + index + 1)").font(.system(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        Text(item.text.isEmpty ? L("queue.attachmentsOnly") : item.text).font(.system(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                        if !item.attachments.isEmpty {
                            Text(item.attachments.map(\.name).joined(separator: ", ")).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    Button { onRemove(item.id) } label: { Image(systemName: "xmark").font(.system(size: 9, weight: .semibold)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help(L("queue.removeHelp")).accessibilityLabel(L("queue.remove"))
                        .accessibilityIdentifier("queue-remove-\(item.id)")
                }
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .combine).accessibilityIdentifier("queue-item-\(item.id)")
            }
        }
        .accessibilityIdentifier("queue-\(sessionID)")
    }
}
