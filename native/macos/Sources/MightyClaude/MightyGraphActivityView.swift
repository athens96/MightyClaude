import MightyCore
import SwiftUI

/// Keeps animation inside tiny decoration subtrees. Transcript contents and
/// graph coordinates do not receive frame-by-frame state changes: the bars and the
/// marching border run in Core Animation, sampled from these values over one period.
enum MightyGraphActivityStyle {
    static func isActive(_ status: String) -> Bool {
        ["running", "starting", "queued"].contains(status)
    }

    /// One cycle of the bars and of the border's march, in seconds.
    static let period: Double = 1.6

    static func phase(at date: Date, reducedMotion: Bool) -> Double {
        guard !reducedMotion else { return 0 }
        return date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: period) / period
    }

    static func barHeight(_ index: Int, phase: Double) -> CGFloat {
        4 + 8 * CGFloat((sin((phase + Double(index) / 5) * 2 * .pi) + 1) / 2)
    }

    /// A running block's border: 9pt dashes, 7pt gaps, drawn with butt caps so the
    /// dashes are as long as they say.
    static let dash: [CGFloat] = [9, 7]

    /// The length of a rounded rectangle's outline with circular corners.
    static func perimeter(_ size: CGSize, cornerRadius: CGFloat) -> CGFloat {
        let radius = max(0, min(cornerRadius, size.width / 2, size.height / 2))
        return 2 * (size.width + size.height) - 8 * radius + 2 * .pi * radius
    }

    /// `dash` stretched or squeezed so a whole number of dash-and-gap periods fits the
    /// outline: where the path starts and ends there is no short dash or double gap.
    static func dash(fitting perimeter: CGFloat) -> [CGFloat] {
        let period = dash.reduce(0, +)
        guard perimeter > 0 else { return dash }
        let fitted = perimeter / max(1, (perimeter / period).rounded())
        return dash.map { $0 * fitted / period }
    }

    /// One period per cycle, so the line moves on without a jump when the cycle
    /// wraps; a negative phase walks the dashes forward along the outline.
    static func dashPhase(_ phase: Double, dash: [CGFloat]) -> CGFloat {
        -CGFloat(phase) * dash.reduce(0, +)
    }
}

struct MightyGraphActivityIndicator: View {
    let status: String
    let tint: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if MightyGraphActivityStyle.isActive(status) {
                if reduceMotion {
                    Image(systemName: "bolt.fill").font(.ui(size: 10)).foregroundStyle(tint)
                        .frame(width: 18, height: 14)
                } else {
                    MightyGraphActivityBars(tint: tint).frame(width: 18, height: 14)
                }
            } else if status == "waiting" {
                Image(systemName: "pause.circle").font(.ui(size: 12)).foregroundStyle(Palette.waitText)
                    .frame(width: 18, height: 14)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true) // The adjacent status text carries the state.
    }
}

/// Four capsules rising and falling as `MightyGraphActivityStyle.barHeight` says, in
/// step with the wall clock; each bar's height is a keyframe animation sampled over
/// one period.
private struct MightyGraphActivityBars: NSViewRepresentable {
    let tint: Color

    func makeNSView(context: Context) -> MightyGraphActivityBarsView { MightyGraphActivityBarsView(frame: .zero) }
    func updateNSView(_ view: MightyGraphActivityBarsView, context: Context) {
        view.tint = NSColor(tint)
        view.repaint(scheme: context.environment.colorScheme)
        view.animating = true
    }
    static func dismantleNSView(_ view: MightyGraphActivityBarsView, coordinator: ()) { view.animating = false }
}

private final class MightyGraphActivityBarsView: LoopingLayerView {
    static let barWidth: CGFloat = 3
    /// Keyframes per period: the bars' sine, smooth at 120 Hz.
    static let samples = 64
    let bars = (0..<4).map { _ in CALayer() }
    var tint = NSColor.clear

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        for bar in bars {
            bar.cornerRadius = Self.barWidth / 2
            canvas.addSublayer(bar)
        }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func paint() {
        let fill = Self.resolved(tint)
        for bar in bars { bar.backgroundColor = fill }
    }

    /// The bars as the HStack laid them out: centred in the frame, `Spacing.xxs` apart.
    override func layoutCanvas(_ size: CGSize) {
        let gap = DesignMetrics.Spacing.xxs
        let width = CGFloat(bars.count) * Self.barWidth + CGFloat(bars.count - 1) * gap
        for (index, bar) in bars.enumerated() {
            // Still (and in a view-drawn capture), a bar holds its height at phase 0.
            bar.bounds = CGRect(x: 0, y: 0, width: Self.barWidth, height: MightyGraphActivityStyle.barHeight(index, phase: 0))
            bar.position = CGPoint(x: (size.width - width) / 2 + CGFloat(index) * (Self.barWidth + gap) + Self.barWidth / 2, y: size.height / 2)
        }
    }

    override func loops() -> [LayerLoop] {
        bars.enumerated().map { index, bar in
            let animation = CAKeyframeAnimation(keyPath: "bounds.size.height")
            animation.values = (0...Self.samples).map { MightyGraphActivityStyle.barHeight(index, phase: Double($0) / Double(Self.samples)) }
            animation.calculationMode = .linear
            animation.duration = MightyGraphActivityStyle.period
            return LayerLoop(layer: bar, key: "bar", animation: animation, clockAligned: true)
        }
    }
}

/// A running block's 2pt dashed border walking forward along the card's outline, one
/// dash period per `MightyGraphActivityStyle.period`, in step with the wall clock.
private struct MightyGraphMarchingBorder: NSViewRepresentable {
    let color: Color
    let cornerRadius: CGFloat

    func makeNSView(context: Context) -> MightyGraphMarchingBorderView { MightyGraphMarchingBorderView(frame: .zero) }
    func updateNSView(_ view: MightyGraphMarchingBorderView, context: Context) {
        view.ink = NSColor(color)
        view.repaint(scheme: context.environment.colorScheme)
        if view.cornerRadius != cornerRadius { view.cornerRadius = cornerRadius; view.needsLayout = true }
        view.animating = true
    }
    static func dismantleNSView(_ view: MightyGraphMarchingBorderView, coordinator: ()) { view.animating = false }
}

private final class MightyGraphMarchingBorderView: LoopingLayerView {
    let border = CAShapeLayer()
    var ink = NSColor.clear
    var cornerRadius: CGFloat = 12
    private var dash: [CGFloat] = []

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        border.fillColor = nil
        border.lineWidth = 2
        border.lineCap = .butt
        canvas.addSublayer(border)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func paint() { border.strokeColor = Self.resolved(ink) }

    override func layoutCanvas(_ size: CGSize) {
        border.frame = CGRect(origin: .zero, size: size)
        let rect = border.bounds.insetBy(dx: 1, dy: 1)
        let radius = max(0, cornerRadius - 1)
        // The same path SwiftUI stroked, so the dashes start and walk where they did.
        border.path = RoundedRectangle(cornerRadius: radius, style: .circular).path(in: rect).cgPath
        let fitted = MightyGraphActivityStyle.dash(fitting: MightyGraphActivityStyle.perimeter(rect.size, cornerRadius: radius))
        border.lineDashPattern = fitted.map { NSNumber(value: Double($0)) }
        guard fitted != dash else { return }
        dash = fitted
        restartLoops()
    }

    override func loops() -> [LayerLoop] {
        guard !dash.isEmpty else { return [] }
        let animation = Self.basic("lineDashPhase", from: 0, to: MightyGraphActivityStyle.dashPhase(1, dash: dash),
                                   duration: MightyGraphActivityStyle.period, timing: .linear)
        return [LayerLoop(layer: border, key: "march", animation: animation, clockAligned: true)]
    }
}

/// Concept D's edge on a block that is in motion: a 2pt run-blue border with the soft
/// run halo outside it while it runs, the amber one while it waits on the user. While
/// it runs, the border is a dashed line marching around the card (the look the diagram
/// had before the card dashboard); under Reduce Motion it holds still as a solid line.
struct MightyGraphActivityOutline: View {
    let tone: DesignTone
    var cornerRadius: CGFloat = 12
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius)
        Group {
            switch tone {
            case .run:
                Group {
                    if reduceMotion {
                        shape.inset(by: 1).stroke(Palette.run, lineWidth: 2)
                    } else {
                        // The march runs in Core Animation: the card under it, its transcript,
                        // the graph's layout and SwiftUI itself never see the clock.
                        MightyGraphMarchingBorder(color: Palette.run, cornerRadius: cornerRadius)
                    }
                }
                .background { shape.inset(by: -2).stroke(Palette.runSoft, lineWidth: 4) }
            case .wait:
                shape.inset(by: 1).stroke(Palette.wait, lineWidth: 2)
            default:
                EmptyView()
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// A block's status as a pill: the tone's soft tint under its text-safe ink.
struct MightyStatusPill: View {
    let text: String
    let tone: DesignTone
    var height: CGFloat = 18

    var body: some View {
        Text(text)
            .font(.ui(size: 10, weight: .bold)).lineLimit(1)
            .foregroundStyle(Palette.text(tone))
            .padding(.horizontal, DesignMetrics.Spacing.sm).frame(height: height)
            .background(Palette.soft(tone), in: Capsule())
            .fixedSize()
    }
}

extension View {
    /// The white D card every graph block sits on: the card fill and its hairline edge.
    func mightyBlockCard(cornerRadius: CGFloat = 12) -> some View {
        background(Palette.panel, in: RoundedRectangle(cornerRadius: cornerRadius))
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
            .overlay { RoundedRectangle(cornerRadius: cornerRadius).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
    }
}
