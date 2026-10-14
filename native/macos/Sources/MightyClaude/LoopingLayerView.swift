import AppKit
import SwiftUI

/// One endless animation on one layer.
struct LayerLoop {
    let layer: CALayer
    let key: String
    let animation: CAAnimation
    /// Starts where the wall clock is in the animation's period instead of at its start,
    /// so every copy on screen moves in step (as `MightyGraphActivityStyle.phase` does).
    var clockAligned = false
}

/// A layer-backed view whose endless animations run in Core Animation, in the render
/// server: a running mark (the spark, the bars, a ripple) then never dirties SwiftUI's
/// graph, so the window is not re-rendered every display frame while an agent runs.
/// Subclasses build their layers in `canvas`, lay them out in `layoutCanvas(_:)`, colour
/// them in `paint()` and list their animations in `loops()`. The loops are added while
/// `animating` is on and the view is in a window, added again if they went missing when
/// the app comes back, and removed when the view leaves its window or SwiftUI dismantles
/// it. A view-drawn capture (`cacheDisplay`: help shots, smoke captures) renders the
/// canvas's model layers, so it shows the still pose set in `layoutCanvas(_:)`, never a
/// moment of the animation.
class LoopingLayerView: NSView {
    /// Sublayers go here, in SwiftUI's y-down coordinates whatever the backing layer's flip.
    let canvas = CALayer()
    var animating = false { didSet { if animating != oldValue { restartLoops() } } }
    private var added: [(layer: CALayer, key: String)] = []

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.addSublayer(canvas)
        NotificationCenter.default.addObserver(self, selector: #selector(applicationDidBecomeActive), name: NSApplication.didBecomeActiveNotification, object: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    deinit { NotificationCenter.default.removeObserver(self) }

    override var isFlipped: Bool { true }
    /// Decoration only: clicks reach the row or button under it.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        needsLayout = true
    }

    override func layout() {
        super.layout()
        guard let backing = layer else { return }
        Self.withoutActions {
            // Flip the canvas only when the backing layer is not already y-down on screen.
            canvas.isGeometryFlipped = !backing.contentsAreFlipped()
            canvas.frame = backing.bounds
            layoutCanvas(canvas.bounds.size)
        }
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        repaint(scheme: nil)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        needsLayout = true
        if window == nil { removeLoops() } else { addMissingLoops() }
    }

    /// Lays the canvas's layers out for its size, in y-down points.
    func layoutCanvas(_ size: CGSize) {}
    /// Sets the layers' colours from the view's dynamic NSColors (`resolved(_:opacity:)`),
    /// while the appearance being painted for is the current drawing appearance.
    func paint() {}

    /// Paints the layers for `scheme` (SwiftUI's colour scheme, from `updateNSView`), or
    /// for the view's own appearance when the window's appearance changes under it.
    func repaint(scheme: ColorScheme?) {
        let appearance = scheme.flatMap { NSAppearance(named: $0 == .dark ? .darkAqua : .aqua) } ?? effectiveAppearance
        Self.withoutActions { appearance.performAsCurrentDrawingAppearance { paint() } }
    }
    /// The endless animations, built from the current layout.
    func loops() -> [LayerLoop] { [] }

    /// Removes every loop and adds them again, after what they animate between changed.
    func restartLoops() {
        removeLoops()
        addMissingLoops()
    }

    func removeLoops() {
        for entry in added { entry.layer.removeAnimation(forKey: entry.key) }
        added.removeAll()
    }

    private func addMissingLoops() {
        guard animating, window != nil else { return }
        let now = CACurrentMediaTime()
        let clock = Date().timeIntervalSinceReferenceDate
        for loop in loops() where loop.layer.animation(forKey: loop.key) == nil {
            let animation = loop.animation
            animation.repeatCount = .infinity
            animation.isRemovedOnCompletion = false
            // Decoration: 24 fps is as smooth as the SwiftUI timelines were; never 120 Hz.
            animation.preferredFrameRateRange = CAFrameRateRange(minimum: 15, maximum: 30, preferred: 24)
            if loop.clockAligned, animation.duration > 0 {
                animation.beginTime = loop.layer.convertTime(now, from: nil) - clock.truncatingRemainder(dividingBy: animation.duration)
            }
            loop.layer.add(animation, forKey: loop.key)
            added.removeAll { $0.layer === loop.layer && $0.key == loop.key }
            added.append((loop.layer, loop.key))
        }
    }

    @objc private func applicationDidBecomeActive() { addMissingLoops() }

    /// Changes to the canvas's layers land at once instead of easing in over 0.25 s.
    static func withoutActions(_ body: () -> Void) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        body()
        CATransaction.commit()
    }

    /// A dynamic colour as the current drawing appearance shows it, with `opacity` on top
    /// of its own alpha; call it from `paint()`.
    static func resolved(_ color: NSColor, opacity: CGFloat = 1) -> CGColor {
        let resolved = color.cgColor
        return opacity < 1 ? resolved.copy(alpha: resolved.alpha * opacity) ?? resolved : resolved
    }

    /// The animation of `keyPath` from `from` to `to` over `duration`, once per period.
    static func basic(_ keyPath: String, from: Any, to: Any, duration: CFTimeInterval, timing: CAMediaTimingFunctionName) -> CABasicAnimation {
        let animation = CABasicAnimation(keyPath: keyPath)
        animation.fromValue = from
        animation.toValue = to
        animation.duration = duration
        animation.timingFunction = CAMediaTimingFunction(name: timing)
        return animation
    }
}

/// A disc that grows and fades, over and over: `PulseDot`'s breath and the timeline's
/// running ring. The view's frame is the disc at its largest; the disc starts at
/// `fromScale` of the diameter that fills the frame at `toScale`.
struct RippleLayer: NSViewRepresentable {
    let color: Color
    var colorOpacity: CGFloat = 1
    let fromScale: CGFloat
    let toScale: CGFloat
    let fromOpacity: Float
    let duration: CFTimeInterval

    func makeNSView(context: Context) -> RippleLayerView { RippleLayerView(frame: .zero) }
    func updateNSView(_ view: RippleLayerView, context: Context) {
        let changed = view.fromScale != fromScale || view.toScale != toScale || view.fromOpacity != fromOpacity || view.duration != duration
        (view.fromScale, view.toScale, view.fromOpacity, view.duration) = (fromScale, toScale, fromOpacity, duration)
        (view.tint, view.tintOpacity) = (NSColor(color), colorOpacity)
        view.repaint(scheme: context.environment.colorScheme)
        if changed { view.needsLayout = true; view.restartLoops() }
        view.animating = true
    }
    static func dismantleNSView(_ view: RippleLayerView, coordinator: ()) { view.animating = false }
}

final class RippleLayerView: LoopingLayerView {
    let disc = CAShapeLayer()
    var tint = NSColor.clear
    var tintOpacity: CGFloat = 1
    var fromScale: CGFloat = 1
    var toScale: CGFloat = 1
    var fromOpacity: Float = 1
    var duration: CFTimeInterval = 1

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        canvas.addSublayer(disc)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func paint() { disc.fillColor = Self.resolved(tint, opacity: tintOpacity) }

    override func layoutCanvas(_ size: CGSize) {
        let diameter = min(size.width, size.height) / max(toScale, 0.01)
        disc.bounds = CGRect(x: 0, y: 0, width: diameter, height: diameter)
        disc.position = CGPoint(x: size.width / 2, y: size.height / 2)
        disc.path = CGPath(ellipseIn: disc.bounds, transform: nil)
        // Still (and in a view-drawn capture), it shows the breath's first frame.
        disc.transform = CATransform3DMakeScale(fromScale, fromScale, 1)
        disc.opacity = fromOpacity
    }

    override func loops() -> [LayerLoop] {
        let group = CAAnimationGroup()
        group.animations = [
            Self.basic("transform.scale", from: fromScale, to: toScale, duration: duration, timing: .easeOut),
            Self.basic("opacity", from: fromOpacity, to: 0, duration: duration, timing: .easeOut),
        ]
        group.duration = duration
        return [LayerLoop(layer: disc, key: "ripple", animation: group)]
    }
}
