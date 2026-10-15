import SwiftUI
import MightyCore

/// The status mark in front of a pane's title in the sidebar, the pane header, the
/// tab strip and the dashboard (status v2, concept A). Which glyph a state gets is
/// `StatusGlyphKind` in MightyCore; its colours are `DesignPalette.glyph(_:)`,
/// `discFill(_:)` and `discInk(_:)`. Drawn on a 16-unit grid, as in the mockup's SVG.
struct StatusGlyph: View {
    let tone: DesignTone
    var kind: String = SessionKind.claude
    var size: CGFloat = 14
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let glyph = StatusGlyphKind(tone: tone, kind: kind)
        mark(glyph)
            .frame(width: size, height: size)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(Palette.word(tone))
            .accessibilityAddTraits(.isImage)
    }

    @ViewBuilder private func mark(_ glyph: StatusGlyphKind) -> some View {
        let ink = Palette.glyph(tone)
        switch glyph {
        case .spark:
            StatusSpark(color: ink, unit: size / 16, turning: !reduceMotion)
        case .question:
            ZStack {
                Circle().fill(Palette.discFill(tone)).padding(size * 0.8 / 16)
                QuestionHook().stroke(Palette.discInk(tone), style: Self.line(1.7, size))
                GlyphDot(center: CGPoint(x: 8, y: 11.7), radius: 1.05).fill(Palette.discInk(tone))
            }
        case .check:
            GlyphLines(segments: [[CGPoint(x: 3.2, y: 8.5), CGPoint(x: 6.2, y: 11.5), CGPoint(x: 12.8, y: 4.5)]])
                .stroke(ink, style: Self.line(1.8, size))
        case .slashedRing:
            ZStack {
                Circle().stroke(ink, lineWidth: 1.8 * size / 16).padding(2 * size / 16)
                GlyphLines(segments: [[CGPoint(x: 3.9, y: 12.1), CGPoint(x: 12.1, y: 3.9)]]).stroke(ink, style: Self.line(1.8, size))
            }
        case .exclamation:
            ZStack {
                Circle().fill(Palette.discFill(tone)).padding(size * 0.8 / 16)
                GlyphLines(segments: [[CGPoint(x: 8, y: 4.3), CGPoint(x: 8, y: 8.9)]]).stroke(Palette.discInk(tone), style: Self.line(1.9, size))
                GlyphDot(center: CGPoint(x: 8, y: 11.6), radius: 1.05).fill(Palette.discInk(tone))
            }
        case .ring:
            Circle().stroke(ink, lineWidth: 1.8 * size / 16).padding(4.4 * size / 16)
        case .pane:
            Image(systemName: paneSymbol(kind)).font(.ui(size: size * 0.78, weight: .medium)).foregroundStyle(ink)
        }
    }

    /// A stroke given in the 16-unit grid, scaled to the glyph's size.
    static func line(_ width: CGFloat, _ size: CGFloat) -> StrokeStyle {
        StrokeStyle(lineWidth: width * size / 16, lineCap: .round, lineJoin: .round)
    }
}

/// The running spark: four arms and four fainter diagonals, turning clockwise once
/// every 3.2 s; still when the user asks for reduced motion. It turns in Core Animation,
/// so a running pane's mark never re-renders the window frame by frame.
private struct StatusSpark: NSViewRepresentable {
    let color: Color
    let unit: CGFloat
    let turning: Bool

    static let arms: [[CGPoint]] = [
        [CGPoint(x: 8, y: 1.6), CGPoint(x: 8, y: 5.6)], [CGPoint(x: 8, y: 10.4), CGPoint(x: 8, y: 14.4)],
        [CGPoint(x: 1.6, y: 8), CGPoint(x: 5.6, y: 8)], [CGPoint(x: 10.4, y: 8), CGPoint(x: 14.4, y: 8)],
    ]
    static let diagonals: [[CGPoint]] = [
        [CGPoint(x: 4.6, y: 4.6), CGPoint(x: 6.1, y: 6.1)], [CGPoint(x: 9.9, y: 9.9), CGPoint(x: 11.4, y: 11.4)],
        [CGPoint(x: 4.6, y: 11.4), CGPoint(x: 6.1, y: 9.9)], [CGPoint(x: 9.9, y: 6.1), CGPoint(x: 11.4, y: 4.6)],
    ]

    func makeNSView(context: Context) -> StatusSparkView { StatusSparkView(frame: .zero) }
    func updateNSView(_ view: StatusSparkView, context: Context) {
        view.ink = NSColor(color)
        view.repaint(scheme: context.environment.colorScheme)
        LoopingLayerView.withoutActions { for line in [view.arms, view.diagonals] { line.lineWidth = 2 * unit } }
        view.animating = turning
    }
    static func dismantleNSView(_ view: StatusSparkView, coordinator: ()) { view.animating = false }
}

private final class StatusSparkView: LoopingLayerView {
    /// Turns about the glyph's centre, carrying both sets of strokes.
    let rotor = CALayer()
    let arms = CAShapeLayer()
    let diagonals = CAShapeLayer()
    var ink = NSColor.clear

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        for line in [arms, diagonals] {
            line.fillColor = nil
            line.lineCap = .round
            line.lineJoin = .round
            rotor.addSublayer(line)
        }
        canvas.addSublayer(rotor)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func paint() {
        arms.strokeColor = Self.resolved(ink)
        diagonals.strokeColor = Self.resolved(ink, opacity: 0.7)
    }

    override func layoutCanvas(_ size: CGSize) {
        rotor.frame = CGRect(origin: .zero, size: size)
        let scale = min(size.width, size.height) / 16
        for (line, segments) in [(arms, StatusSpark.arms), (diagonals, StatusSpark.diagonals)] {
            line.frame = rotor.bounds
            let path = CGMutablePath()
            for points in segments {
                guard let first = points.first else { continue }
                path.move(to: CGPoint(x: first.x * scale, y: first.y * scale))
                for point in points.dropFirst() { path.addLine(to: CGPoint(x: point.x * scale, y: point.y * scale)) }
            }
            line.path = path
        }
    }

    /// In the canvas's y-down space a positive angle turns clockwise on screen, as
    /// SwiftUI's positive `rotationEffect` did.
    override func loops() -> [LayerLoop] {
        [LayerLoop(layer: rotor, key: "turn", animation: Self.basic("transform.rotation.z", from: 0, to: 2 * Double.pi, duration: 3.2, timing: .linear))]
    }
}

/// Polylines on the glyph's 16-unit grid.
private struct GlyphLines: Shape {
    let segments: [[CGPoint]]

    func path(in rect: CGRect) -> Path {
        let scale = min(rect.width, rect.height) / 16
        var path = Path()
        for points in segments {
            guard let first = points.first else { continue }
            path.move(to: CGPoint(x: rect.minX + first.x * scale, y: rect.minY + first.y * scale))
            for point in points.dropFirst() { path.addLine(to: CGPoint(x: rect.minX + point.x * scale, y: rect.minY + point.y * scale)) }
        }
        return path
    }
}

/// A filled dot on the glyph's 16-unit grid.
private struct GlyphDot: Shape {
    let center: CGPoint
    let radius: CGFloat

    func path(in rect: CGRect) -> Path {
        let scale = min(rect.width, rect.height) / 16
        return Path(ellipseIn: CGRect(x: rect.minX + (center.x - radius) * scale, y: rect.minY + (center.y - radius) * scale,
                                      width: radius * 2 * scale, height: radius * 2 * scale))
    }
}

/// The hook of the "?": the mockup's `M5.9 6.1 a2.1 2.1 0 1 1 2.9 1.95 c-.5.22-.8.6-.8 1.15 v.35`.
private struct QuestionHook: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        // The arc from (5.9, 6.1) round the top to (8.8, 8.05): radius 2.1 about (8, 6.11).
        path.addRelativeArc(center: CGPoint(x: 8, y: 6.11), radius: 2.1, startAngle: .degrees(180), delta: .degrees(247.6))
        path.addCurve(to: CGPoint(x: 8, y: 9.2), control1: CGPoint(x: 8.3, y: 8.27), control2: CGPoint(x: 8, y: 8.65))
        path.addLine(to: CGPoint(x: 8, y: 9.55))
        let scale = min(rect.width, rect.height) / 16
        return path.applying(CGAffineTransform(scaleX: scale, y: scale).concatenating(CGAffineTransform(translationX: rect.minX, y: rect.minY)))
    }
}

extension Palette {
    /// A tone's status word, as the glyph's accessibility label and the header's word.
    static func word(_ tone: DesignTone) -> String {
        switch tone {
        case .run: L("session.state.running")
        case .wait: L("phone.dashboard.stat.waiting")
        case .done: L("session.state.completed")
        case .err: L("session.state.error")
        case .stop: L("session.state.stopped")
        case .idle: L("session.state.idle")
        }
    }

    static func glyph(_ tone: DesignTone) -> Color { glyphColors[tone] ?? ink2 }
    static func discFill(_ tone: DesignTone) -> Color { discFills[tone] ?? err }
    static func discInk(_ tone: DesignTone) -> Color { discInks[tone] ?? onStatus }

    private static let glyphColors = byTone { palette, tone in palette.glyph(tone) }
    private static let discFills = byTone { palette, tone in palette.discFill(tone) }
    private static let discInks = byTone { palette, tone in palette.discInk(tone) }

    private static func byTone(_ pick: (DesignPalette, DesignTone) -> DesignColor) -> [DesignTone: Color] {
        Dictionary(uniqueKeysWithValues: DesignTone.allCases.map { tone in (tone, dynamic { pick($0, tone) }) })
    }
}
