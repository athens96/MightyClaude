import SwiftUI
import MightyCore

/// The options after `next:` in the last finished reply's `◆` breadcrumb,
/// drawn right under the transcript. A tap fills the composer; nothing is sent.
struct NextActionButtons: View {
    let sessionID: String
    let entryID: String
    let actions: [NextAction]
    let onFill: (String) -> Void

    var body: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 6) {
                Image(systemName: "arrow.turn.down.right").font(.system(size: 10)).foregroundStyle(.tertiary)
                    .accessibilityHidden(true)
                ForEach(Array(actions.enumerated()), id: \.offset) { index, action in
                    Button { onFill(action.fill) } label: {
                        Text(verbatim: action.displayLabel)
                            .font(.system(size: 11)).lineLimit(1).truncationMode(.tail)
                            .frame(maxWidth: 280, alignment: .leading)
                            .fixedSize(horizontal: true, vertical: false)
                            .padding(.horizontal, 8).padding(.vertical, 5)
                            .background(Palette.accent.opacity(0.10), in: RoundedRectangle(cornerRadius: 6))
                            .overlay { RoundedRectangle(cornerRadius: 6).stroke(Palette.accent.opacity(0.35), lineWidth: 1) }
                            .contentShape(RoundedRectangle(cornerRadius: 6))
                    }
                    .buttonStyle(.plain)
                    .help(action.displayLabel)
                    .accessibilityLabel(action.displayLabel)
                    .accessibilityHint(L("pane.nextActions.fillHint"))
                    .accessibilityIdentifier("next-action-\(sessionID)-\(index)")
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
        }
        .scrollIndicators(.hidden)
        .fixedSize(horizontal: false, vertical: true)
        .id(entryID)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("pane.nextActions.label"))
        .accessibilityIdentifier("next-actions-\(sessionID)")
    }
}
