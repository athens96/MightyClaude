import SwiftUI
import MightyCore

extension Palette {
    /// The nine palette names of §1.10, each mapped to the colour the app
    /// already draws with. A manifest cannot name anything else.
    static func tint(_ value: StyleTint?) -> Color {
        switch value {
        case .purple: return .purple
        case .teal: return .teal
        case .indigo: return .indigo
        case .mint: return .mint
        case .orange: return .orange
        case .green: return .green
        case .red: return .red
        case .secondary: return .secondary
        case .accent, nil: return Palette.accent
        }
    }
}

/// One action of a guided style. Its own view keeps the row's builder cheap
/// to type-check, as the Paperthin grid's chip did before it.
struct GuidedActionChip: View {
    let action: StyleAction
    let prominent: Bool
    let recommended: Bool
    let disabled: Bool
    let sessionID: String
    let onTap: () -> Void

    private var emphasised: Bool { prominent || recommended }

    var body: some View {
        let label = HStack(spacing: 5) {
            if let glyph = action.glyph { Text(verbatim: glyph).font(.system(size: 11)) }
            else if let icon = action.icon { Image(systemName: icon.rawValue).font(.system(size: 10)) }
            Text(verbatim: action.title)
                .font(.system(size: 11, weight: emphasised ? .semibold : .regular))
                .lineLimit(1)
            if action.flags.contains(.userInvoked) { Image(systemName: "person.fill").font(.system(size: 8)).foregroundStyle(.secondary) }
            if action.flags.contains(.readOnly) { Image(systemName: "eye").font(.system(size: 8)).foregroundStyle(.secondary) }
        }
        .padding(.horizontal, 8).padding(.vertical, 5)
        .background(emphasised ? Palette.tint(action.tint).opacity(0.18) : Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(RoundedRectangle(cornerRadius: 6))

        Button(action: onTap) { label }
            .buttonStyle(.plain)
            .disabled(disabled)
            .opacity(disabled ? 0.45 : 1)
            .help(StyleChips.help(action))
            .accessibilityLabel(action.title)
            .accessibilityHint(action.help)
            .accessibilityIdentifier("mighty-action-\(action.id)-\(sessionID)")
    }
}

/// The one-line strip a pending style leaves in the panel: the card itself is
/// a sheet, because a hundred actions cannot fit above the composer (§4.5).
struct GuidedApprovalStrip: View {
    let name: String
    let source: StyleSource
    let sessionID: String
    let onOpen: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "questionmark.square.dashed").font(.system(size: 10)).foregroundStyle(.secondary)
            Text(verbatim: name).font(.system(size: 11, weight: .medium)).lineLimit(1)
            if let badge = StyleChrome.sourceBadge(source) { SourceBadge(text: badge) }
            Text("확인이 필요합니다").font(.system(size: 11)).foregroundStyle(.secondary)
            Button("내용 보기", action: onOpen).controlSize(.small)
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-approval-strip-\(sessionID)")
    }
}

/// The badge that follows a non-bundled name wherever it is drawn (§1.10).
struct SourceBadge: View {
    let text: String
    var body: some View {
        Text(verbatim: text)
            .font(.system(size: 9))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Palette.subtle, in: Capsule())
            .accessibilityLabel("출처: " + text)
    }
}
