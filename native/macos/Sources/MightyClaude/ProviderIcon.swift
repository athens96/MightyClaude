import AppKit
import SwiftUI

/// Provider glyphs. Claude keeps its asterisk, Gemini uses the four-point
/// spark, and Codex draws OpenAI's hexagonal knot. The same shapes serve
/// SwiftUI views, menus and NSAttributedString attachments.
struct ProviderIcon: View {
    let provider: String
    var size: CGFloat = 12
    var weight: Font.Weight = .medium

    var body: some View {
        switch provider {
        case "codex":
            CodexKnotShape().fill(.foreground)
                .frame(width: size * 1.15, height: size * 1.15)
                .accessibilityLabel("Codex")
        case "gemini":
            Image(systemName: "sparkle").font(.system(size: size, weight: weight)).accessibilityLabel("Gemini")
        default:
            Image(systemName: "asterisk").font(.system(size: size, weight: weight)).accessibilityLabel("Claude")
        }
    }
}

/// Six rounded bars, each offset from the centre and rotated 60° apart, form
/// the interlocking knot that reads as the OpenAI / Codex mark at small sizes.
struct CodexKnotShape: Shape {
    func path(in rect: CGRect) -> Path {
        let side = min(rect.width, rect.height)
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let length = side * 0.62, width = side * 0.17, radius = side * 0.2
        var path = Path()
        for index in 0..<6 {
            let angle = CGFloat(index) * .pi / 3
            let origin = CGPoint(x: center.x + cos(angle + .pi / 6) * radius, y: center.y + sin(angle + .pi / 6) * radius)
            let bar = Path(roundedRect: CGRect(x: -length / 2, y: -width / 2, width: length, height: width), cornerRadius: width / 2)
            path.addPath(bar.applying(CGAffineTransform(translationX: origin.x, y: origin.y).rotated(by: angle)))
        }
        return path
    }
}

enum ProviderIconImage {
    /// A rendered glyph for places that need an NSImage (menus, transcripts).
    static func image(provider: String, pointSize: CGFloat, color: NSColor) -> NSImage? {
        if provider == "codex" {
            let side = pointSize * 1.2
            return NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
                color.setFill()
                NSBezierPath(cgPath: CodexKnotShape().path(in: rect).cgPath).fill()
                return true
            }
        }
        let name = provider == "gemini" ? "sparkle" : "asterisk"
        return NSImage(systemSymbolName: name, accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(pointSize: pointSize, weight: .medium).applying(.init(paletteColors: [color])))
    }
}
