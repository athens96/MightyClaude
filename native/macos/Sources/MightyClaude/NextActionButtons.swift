import SwiftUI
import MightyCore

/// The options after `next:` in the last finished reply's `◆` breadcrumb,
/// drawn right under the transcript. A tap fills the composer; nothing is sent.
/// Concept D: white rows, each with its label and a blue arrow, stacked under a
/// small "다음 작업 제안" heading, as on the phone.
struct NextActionButtons: View {
    let sessionID: String
    let entryID: String
    let actions: [NextAction]
    let onFill: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(L("pane.nextActions.label"))
                .font(.system(size: 11.5, weight: .bold)).foregroundStyle(Palette.ink2)
                .padding(.horizontal, 3)
                .accessibilityHidden(true)
            ForEach(Array(actions.enumerated()), id: \.offset) { index, action in
                Button { onFill(action.fill) } label: {
                    HStack(spacing: 10) {
                        Text(verbatim: action.displayLabel)
                            .font(.system(size: 13, weight: .semibold)).foregroundStyle(Palette.ink)
                            .lineLimit(2).truncationMode(.tail).multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        Image(systemName: "arrow.right").font(.system(size: 13, weight: .bold)).foregroundStyle(Palette.accent)
                            .accessibilityHidden(true)
                    }
                    .padding(.horizontal, 14).padding(.vertical, 9)
                    .background(Palette.panel, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                    .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Palette.border, lineWidth: 1) }
                    .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                }
                .buttonStyle(.plain)
                .help(action.displayLabel)
                .accessibilityLabel(action.displayLabel)
                .accessibilityHint(L("pane.nextActions.fillHint"))
                .accessibilityIdentifier("next-action-\(sessionID)-\(index)")
            }
        }
        .padding(.horizontal, 12).padding(.top, 8).padding(.bottom, 2)
        .frame(maxWidth: .infinity, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
        .id(entryID)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("pane.nextActions.label"))
        .accessibilityIdentifier("next-actions-\(sessionID)")
    }
}
