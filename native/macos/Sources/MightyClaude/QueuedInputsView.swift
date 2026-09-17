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

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "clock.arrow.circlepath").font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.accent)
                Text(running ? "대기 중 \(items.count)개 · 현재 작업이 끝나면 순서대로 실행" : "대기 중 \(items.count)개").font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if !running {
                    Button(action: onRunNext) { Label("다음 실행", systemImage: "play.fill").font(.system(size: 10, weight: .medium)) }
                        .buttonStyle(.plain).foregroundStyle(Palette.accent)
                        .help("대기 중인 첫 요청을 지금 실행").accessibilityIdentifier("queue-run-\(sessionID)")
                }
            }
            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                HStack(alignment: .top, spacing: 7) {
                    Text("\(index + 1)").font(.system(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(.secondary).frame(width: 14, alignment: .trailing).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.text.isEmpty ? "첨부 파일만 전송" : item.text).font(.system(size: 11)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                        if !item.attachments.isEmpty {
                            Text(item.attachments.map(\.name).joined(separator: ", ")).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    Button { onRemove(item.id) } label: { Image(systemName: "xmark").font(.system(size: 9, weight: .semibold)).frame(width: 18, height: 16) }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help("대기열에서 제거").accessibilityLabel("대기 요청 제거")
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
