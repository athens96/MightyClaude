import AppKit
import SwiftUI
import MightyCore

/// A provider's own mark in its brand colour. `monochrome` draws it in the
/// surrounding foreground style instead, for places where colour would mislead.
struct ProviderIcon: View {
    let provider: String
    var size: CGFloat = 12
    var weight: Font.Weight = .medium   // kept for call sites; the marks have one weight
    var monochrome = false

    var body: some View {
        Group {
            if monochrome { ProviderMarkShape(provider: provider).fill(.foreground) }
            else { ProviderMarkShape(provider: provider).fill(ProviderBrand.gradient(provider)) }
        }
        .frame(width: size * 1.15, height: size * 1.15)
        .accessibilityLabel(Palette.name(provider))
    }
}

struct ProviderMarkShape: Shape {
    let provider: String
    func path(in rect: CGRect) -> Path { Path(ProviderMark.path(provider: provider, in: rect)) }
}

enum ProviderBrand {
    static func nsColors(_ provider: String) -> [NSColor] {
        ProviderMark.colors(provider: provider).map { hex in
            NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
        }
    }
    /// The first brand colour, for text and tints next to the mark.
    static func color(_ provider: String) -> Color { Color(nsColor: nsColors(provider).first ?? .labelColor) }
    /// One colour for Claude and Codex; Gemini's blue-to-rose sweep.
    static func gradient(_ provider: String) -> LinearGradient {
        LinearGradient(colors: nsColors(provider).map { Color(nsColor: $0) }, startPoint: .bottomLeading, endPoint: .topTrailing)
    }
}

enum ProviderIconImage {
    /// A rendered mark for places that need an NSImage (menus, transcripts).
    /// `color` nil draws the brand colours.
    static func image(provider: String, pointSize: CGFloat, color: NSColor? = nil) -> NSImage? {
        let side = pointSize * 1.2
        // Flipped: the outline data has y pointing down.
        return NSImage(size: NSSize(width: side, height: side), flipped: true) { rect in
            let path = NSBezierPath(cgPath: ProviderMark.path(provider: provider, in: rect))
            let brand = ProviderBrand.nsColors(provider)
            if let color { color.setFill(); path.fill() }
            else if brand.count > 1, let gradient = NSGradient(colors: brand) { gradient.draw(in: path, angle: -45) }
            else { (brand.first ?? .labelColor).setFill(); path.fill() }
            return true
        }
    }
}
