import SwiftUI
import MightyCore

/// Requests waiting behind the current run, shown above the composer. Each row
/// can be removed; when the pane is idle (a run ended in error) the first row
/// can be started by hand.
struct QueuedInputsView: View {
    let sessionID: String
    let items: [QueuedInput]
    let running: Bool
    let onRemove: (String) -> Void
    let onRunNext: () -> Void
    /// Why the queue waits beyond the current turn: its background work (§1.17.4).
    var notice: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "clock.arrow.circlepath").font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.accent)
                Text(running ? L("queue.waitingBusy", ["count": "\(items.count)"]) : L("queue.waiting", ["count": "\(items.count)"])).font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if !running {
                    Button(action: onRunNext) { Label(L("queue.runNext"), systemImage: "play.fill").font(.system(size: 10, weight: .medium)) }
                        .buttonStyle(.plain).foregroundStyle(Palette.accent)
                        .help(L("queue.runNextHelp")).accessibilityIdentifier("queue-run-\(sessionID)")
                }
            }
            if let notice {
                Text(verbatim: notice).font(.system(size: 10)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("queue-background-\(sessionID)")
            }
            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: 7) {
                    Text("\(index + 1)").font(.system(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.text.isEmpty ? L("queue.attachmentsOnly") : item.text).font(.system(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                        if !item.attachments.isEmpty {
                            Text(item.attachments.map(\.name).joined(separator: ", ")).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    Button { onRemove(item.id) } label: { Image(systemName: "xmark").font(.system(size: 9, weight: .semibold)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help(L("queue.removeHelp")).accessibilityLabel(L("queue.remove"))
                        .accessibilityIdentifier("queue-remove-\(item.id)")
                }
                .padding(.horizontal, 8).padding(.vertical, 5)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .combine).accessibilityIdentifier("queue-item-\(item.id)")
            }
        }
        .accessibilityIdentifier("queue-\(sessionID)")
    }
}
