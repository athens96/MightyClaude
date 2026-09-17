import SwiftUI
import AppKit

struct ComposerPill: View {
    let title: String
    var systemImage: String?
    /// Draws the provider glyph instead of an SF Symbol when set.
    var provider: String?
    var active = false
    var chevron = false
    var maximumTextWidth: CGFloat?
    var compact = false

    var body: some View {
        HStack(spacing: 5) {
            if let provider { ProviderIcon(provider: provider, size: 12).frame(width: 14, height: 14) }
            else if let systemImage { Image(systemName: systemImage).font(.system(size: 12, weight: .medium)).frame(width: 14, height: 14) }
            if !compact { Text(title).font(.system(size: 11, weight: .medium)).lineLimit(1).truncationMode(.tail).frame(width: min(ComposerToolbarMetrics.textWidth(title), maximumTextWidth ?? .greatestFiniteMagnitude)) }
            if chevron && !compact { Image(systemName: "chevron.down").font(.system(size: 7, weight: .semibold)).opacity(0.6).frame(width: 7) }
        }
        .foregroundStyle(active ? Palette.accent : Color.primary.opacity(0.8))
        .padding(.horizontal, compact ? 0 : 8)
        .frame(width: compact ? ComposerToolbarMetrics.height : nil, height: ComposerToolbarMetrics.height)
        .background(active ? Palette.accent.opacity(0.12) : Color.primary.opacity(0.045), in: Capsule())
        .overlay { Capsule().stroke(active ? Palette.accent.opacity(0.25) : Palette.border.opacity(0.7), lineWidth: 0.5).allowsHitTesting(false) }
        .contentShape(Capsule())
        // A menu's custom label must remain one accessibility element. Without
        // grouping, its identifier can land on the first SF Symbol instead of
        // the complete padded control, giving VoiceOver a tiny hit target.
        .accessibilityElement(children: .combine)
    }
}

enum ComposerToolbarStyle: Equatable { case full, compact, overflow }

/// All controls share one height. Labels progressively collapse, then secondary
/// choices move into a menu; the composer never grows a second controls row.
enum ComposerToolbarMetrics {
    static let height: CGFloat = 32
    static let spacing: CGFloat = 6
    static func textWidth(_ title: String) -> CGFloat {
        ceil((title as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 11, weight: .medium)]).width)
    }
    static func pillWidth(_ title: String, cap: CGFloat, chevron: Bool = true) -> CGFloat {
        min(textWidth(title), cap) + 16 + 14 + 5 + (chevron ? 12 : 0)
    }
    static func style(width: CGFloat, model: String, effort: String?, permission: String, fast: Bool) -> ComposerToolbarStyle {
        let count = 4 + (effort == nil ? 0 : 1) + (fast ? 1 : 0)
        let gaps = CGFloat(count - 1) * spacing
        let full = height * 2 + pillWidth(model, cap: 155) + pillWidth(permission, cap: 90) + (effort.map { pillWidth($0, cap: 48) } ?? 0) + (fast ? pillWidth("Fast", cap: 40, chevron: false) : 0) + gaps
        if width >= full + 4 { return .full }
        let compact = height * CGFloat(count - 1) + pillWidth(model, cap: 90) + gaps
        return width >= compact + 4 ? .compact : .overflow
    }
    static func modelTextWidth(style: ComposerToolbarStyle, width: CGFloat) -> CGFloat {
        switch style {
        case .full: return 155
        case .compact: return 90
        case .overflow: return max(0, min(110, width - height * 2 - spacing * 2 - 47))
        }
    }
}
