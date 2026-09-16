import AppKit
import MightyCore

/// Camera geometry follows Amon's top-edge, mirrored-wing layout. The camera
/// gap is obtained from the display's auxiliary areas, never from visibleFrame.
struct CameraIslandGeometry {
    let frame: NSRect
    let gap: CGFloat
    let wing: CGFloat
    var cameraRect: NSRect { NSRect(x: wing, y: 0, width: gap, height: frame.height) }
    var leftRect: NSRect { NSRect(x: 0, y: 0, width: wing, height: frame.height) }
    var rightRect: NSRect { NSRect(x: wing + gap, y: 0, width: wing, height: frame.height) }

    static func make(screen: NSRect, safeTop: CGFloat, left: NSRect?, right: NSRect?, wing: CGFloat = 112) -> Self {
        guard [screen.minX, screen.minY, screen.width, screen.height].allSatisfy(\.isFinite),
              screen.width > 0, screen.height > 0 else { return Self(frame: .zero, gap: 0, wing: 0) }
        let top = safeTop.isFinite ? min(screen.height, max(0, safeTop)) : 0
        let valid = left.map { screen.contains($0) && !$0.isEmpty && abs($0.maxY - screen.maxY) < 1 } == true
            && right.map { screen.contains($0) && !$0.isEmpty && abs($0.maxY - screen.maxY) < 1 } == true
            && (right?.minX ?? 0) > (left?.maxX ?? 0)
        let gap = top > 0 ? (valid ? right!.minX - left!.maxX : min(200, max(0, screen.width - 16))) : min(24, max(0, screen.width - 16))
        let center = top > 0 && valid ? (left!.maxX + right!.minX) / 2 : screen.midX
        let room = max(0, min(center - gap / 2 - screen.minX - 8, screen.maxX - center - gap / 2 - 8))
        let width = min(room, max(44, wing.isFinite ? wing : 112))
        let height = min(screen.height, max(32, top))
        return Self(frame: NSRect(x: center - gap / 2 - width, y: screen.maxY - height, width: width * 2 + gap, height: height), gap: gap, wing: width)
    }
}

/// Native drawing keeps both text columns out of the physical camera cutout.
@MainActor
final class CameraIslandView: NSView {
    var geometry = CameraIslandGeometry.make(screen: .zero, safeTop: 0, left: nil, right: nil) { didSet { needsDisplay = true } }
    var accounts: [IslandAccount] = [] {
        didSet {
            let descriptions = accounts.map { account in
                ProviderOptions.label(account.provider) + " · " + account.host + " · " + lines(account).joined(separator: ", ")
            }
            let label = "계정 사용 한도 아일랜드 · " + descriptions.joined(separator: " / ")
            setAccessibilityLabel(label); toolTip = label + "\n사용률이 높은 한도 최대 2개 · 클릭하여 모든 계정 보기"
            needsDisplay = true
        }
    }
    var onPress: (() -> Void)?
    private var hovered = false { didSet { needsDisplay = true } }
    private var tracking: NSTrackingArea?
    private let font = NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .medium)

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setAccessibilityElement(true); setAccessibilityRole(.button)
        setAccessibilityIdentifier("account-island-floating")
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    var desiredWing: CGFloat {
        let width = accounts.prefix(2).flatMap { lines($0) }.map { ($0 as NSString).size(withAttributes: [.font: font]).width }.max() ?? 60
        return min(164, max(90, ceil(width) + 48))
    }
    override func updateTrackingAreas() {
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: .zero, options: [.activeAlways, .inVisibleRect, .mouseEnteredAndExited], owner: self)
        addTrackingArea(area); tracking = area
        super.updateTrackingAreas()
    }
    private var shape: NSBezierPath { NSBezierPath(roundedRect: bounds, xRadius: bounds.height / 2, yRadius: bounds.height / 2) }
    override func hitTest(_ point: NSPoint) -> NSView? { shape.contains(point) ? self : nil }
    override func mouseEntered(with event: NSEvent) { hovered = true }
    override func mouseExited(with event: NSEvent) { hovered = false }
    override func mouseDown(with event: NSEvent) {}
    override func mouseUp(with event: NSEvent) {
        if shape.contains(convert(event.locationInWindow, from: nil)) { onPress?() }
    }
    override func accessibilityPerformPress() -> Bool { onPress?(); return true }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.setFill(); shape.fill()
        if hovered {
            NSColor.white.withAlphaComponent(0.25).setStroke()
            let border = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: bounds.height / 2, yRadius: bounds.height / 2)
            border.lineWidth = 1; border.stroke()
        }
        guard let first = accounts.first else { return }
        draw(first, rect: geometry.leftRect, left: true, selectedLines: lines(first))
        if accounts.count > 1 {
            draw(accounts[1], rect: geometry.rightRect, left: false, selectedLines: lines(accounts[1]))
        }

    }
    private func draw(_ account: IslandAccount, rect: NSRect, left: Bool, selectedLines: [String]) {
        guard rect.width >= 44 else { return }
        let area = rect.insetBy(dx: 12, dy: 2)
        let icon = NSRect(x: left ? area.maxX - 15 : area.minX, y: area.midY - 7.5, width: 15, height: 15)
        let color: NSColor = account.provider == "claude" ? NSColor(calibratedRed: 0.8, green: 0.57, blue: 0.44, alpha: 1) : account.provider == "codex" ? .systemGreen : .systemBlue
        NSImage(systemSymbolName: Palette.symbol(account.provider), accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(paletteColors: [color]))?.draw(in: icon)
        let textRect = NSRect(x: left ? area.minX : icon.maxX + 6, y: area.minY, width: max(0, area.width - 21), height: area.height)
        let style = NSMutableParagraphStyle(); style.alignment = left ? .right : .left; style.lineBreakMode = .byTruncatingTail
        let faded = ["stale", "error"].contains(account.usage?.status ?? "")
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white.withAlphaComponent(faded ? 0.65 : 0.95), .paragraphStyle: style]
        let lineHeight: CGFloat = 12
        let total = CGFloat(selectedLines.count) * lineHeight
        for (index, text) in selectedLines.enumerated() {
            (text as NSString).draw(in: NSRect(x: textRect.minX, y: textRect.midY + total / 2 - CGFloat(index + 1) * lineHeight, width: textRect.width, height: lineHeight), withAttributes: attributes)
        }
    }
    private func lines(_ account: IslandAccount) -> [String] { IslandSummaryPolicy.lines(account) }
}
