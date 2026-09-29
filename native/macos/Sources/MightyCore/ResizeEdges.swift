import CoreGraphics

/// Which sides of a box a drag is moving. Coordinates are y-down.
public struct ResizeEdges: OptionSet, Hashable, Sendable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }
    public static let left = ResizeEdges(rawValue: 1)
    public static let right = ResizeEdges(rawValue: 2)
    public static let top = ResizeEdges(rawValue: 4)
    public static let bottom = ResizeEdges(rawValue: 8)
    public static let bottomRight: ResizeEdges = [.right, .bottom]

    public var horizontal: Bool { contains(.left) || contains(.right) }
    public var vertical: Bool { contains(.top) || contains(.bottom) }

    /// The sides whose band holds `point`: up to `outside` points beyond the
    /// border and `inside` points within it, so a thin inner strip leaves the
    /// box's own scrollers and text alone. Near a corner that is both sides.
    public static func at(_ point: CGPoint, frame: CGRect, outside: CGFloat, inside: CGFloat) -> ResizeEdges {
        guard frame.insetBy(dx: -outside, dy: -outside).contains(point) else { return [] }
        let band = -outside...inside
        var edges: ResizeEdges = []
        if band.contains(point.x - frame.minX) { edges.insert(.left) } else if band.contains(frame.maxX - point.x) { edges.insert(.right) }
        if band.contains(point.y - frame.minY) { edges.insert(.top) } else if band.contains(frame.maxY - point.y) { edges.insert(.bottom) }
        return edges
    }

    /// The size after the pointer moved `delta` screen points since the drag
    /// began at `initial`. A left or top side grows as the pointer moves away
    /// from the box, the same as a right or bottom one.
    public func size(from initial: CGSize, delta: CGSize, zoom: CGFloat = 1, minimum: CGSize, maximum: CGSize) -> CGSize {
        let scale = zoom.isFinite && zoom > 0 ? zoom : 1
        var width = initial.width, height = initial.height
        if contains(.right) { width += delta.width / scale } else if contains(.left) { width -= delta.width / scale }
        if contains(.bottom) { height += delta.height / scale } else if contains(.top) { height -= delta.height / scale }
        return CGSize(width: min(maximum.width, max(minimum.width, width)), height: min(maximum.height, max(minimum.height, height)))
    }

    /// The corner that stays put, as a fraction of the box: the one opposite
    /// the dragged sides.
    public var pinnedUnit: CGPoint {
        CGPoint(x: contains(.left) ? 1 : 0, y: contains(.top) ? 1 : 0)
    }
}

/// The pet's floating window, sized around its bubble and the pet beneath.
public enum CompanionBubbleLayout {
    public static let defaultWidth: CGFloat = 258
    public static let minimum = CGSize(width: 220, height: 100)
    public static let maximum = CGSize(width: 640, height: 480)
    /// The window's padding on each side of the bubble.
    public static let sidePadding: CGFloat = 12
    /// Top padding, the gap under the bubble, the pet and the bottom padding.
    public static let chrome: CGFloat = 8 + 2 + 135 + 8
    public static let baseHeight: CGFloat = 330
    public static let tallHeight: CGFloat = 494

    /// `height` nil keeps the bubble as tall as its content; `tall` is the
    /// approval bubble, which is never given a fixed height nor made narrower
    /// than the default, so its options still fit its fixed window.
    public static func panelSize(width: CGFloat?, height: CGFloat?, tall: Bool) -> CGSize {
        let bubbleWidth = tall ? approvalWidth(width) : clampedWidth(width)
        let panelWidth = bubbleWidth + sidePadding * 2
        if tall { return CGSize(width: panelWidth, height: tallHeight) }
        let fixed = height.map { clampedHeight($0) + chrome } ?? 0
        return CGSize(width: panelWidth, height: max(baseHeight, fixed))
    }
    public static func clampedWidth(_ width: CGFloat?) -> CGFloat {
        min(maximum.width, max(minimum.width, width ?? defaultWidth))
    }
    public static func approvalWidth(_ width: CGFloat?) -> CGFloat { max(defaultWidth, clampedWidth(width)) }
    public static func clampedHeight(_ height: CGFloat) -> CGFloat {
        min(maximum.height, max(minimum.height, height))
    }
}
