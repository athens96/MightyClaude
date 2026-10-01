import AppKit
import MightyCore

/// A picture inside the transcript's attributed document: a thumbnail at
/// most 480 pt wide (and 640 pt tall), aspect kept, in a rounded hairline
/// frame. The cell keeps only the picture's key: it asks the library for the
/// thumbnail each time it draws, so cached transcript text never holds one.
final class AgentImageAttachmentCell: NSTextAttachmentCell {
    static let maximumWidth: CGFloat = 480
    static let maximumHeight: CGFloat = 640
    let key: AgentImageKey
    let caption: String

    init(key: AgentImageKey, caption: String) {
        self.key = key; self.caption = caption
        super.init()
    }
    required init(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    /// The picture's own proportions once known; a placeholder's otherwise.
    /// Layout never starts a load: only drawing does.
    private var natural: CGSize {
        MainActor.assumeIsolated {
            let library = AgentImageLibrary.shared
            if library.isMissing(key) { return CGSize(width: 260, height: 56) }
            return library.layoutSize(key) ?? CGSize(width: 320, height: 180)
        }
    }

    func fitted(width available: CGFloat) -> NSSize {
        let natural = natural
        guard natural.width > 0, natural.height > 0 else { return NSSize(width: min(available, 260), height: 56) }
        var width = min(available, Self.maximumWidth, natural.width)
        var height = width * natural.height / natural.width
        if height > Self.maximumHeight { height = Self.maximumHeight; width = height * natural.width / natural.height }
        return NSSize(width: max(24, width.rounded()), height: max(24, height.rounded()))
    }

    override func cellSize() -> NSSize { fitted(width: Self.maximumWidth) }

    override func cellFrame(for textContainer: NSTextContainer, proposedLineFragment lineFrag: NSRect, glyphPosition position: NSPoint, characterIndex charIndex: Int) -> NSRect {
        let size = fitted(width: max(40, lineFrag.width - 4))
        return NSRect(x: 0, y: -3, width: size.width, height: size.height)
    }

    override func draw(withFrame cellFrame: NSRect, in controlView: NSView?) {
        let frame = cellFrame.insetBy(dx: 0.5, dy: 0.5)
        let path = NSBezierPath(roundedRect: frame, xRadius: 8, yRadius: 8)
        NSGraphicsContext.saveGraphicsState()
        path.addClip()
        let picture = MainActor.assumeIsolated { AgentImageLibrary.shared.state(key) }
        switch picture {
        case .ready(let thumbnail):
            thumbnail.image.draw(in: frame, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: [.interpolation: NSImageInterpolation.high.rawValue])
        case .loading, .missing:
            NSColor.labelColor.withAlphaComponent(0.05).setFill()
            frame.fill()
            let text: String
            if case .loading = picture { text = L("images.loading") } else { text = L("images.missing") }
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.secondaryLabelColor]
            let size = (text as NSString).size(withAttributes: attributes)
            (text as NSString).draw(at: NSPoint(x: frame.midX - size.width / 2, y: frame.midY - size.height / 2), withAttributes: attributes)
        }
        NSGraphicsContext.restoreGraphicsState()
        NSColor.separatorColor.setStroke()
        path.lineWidth = 1
        path.stroke()
    }

    /// The attachment character carrying this cell.
    @MainActor static func attachment(_ key: AgentImageKey, caption: String) -> NSAttributedString {
        let attachment = NSTextAttachment()
        attachment.attachmentCell = AgentImageAttachmentCell(key: key, caption: caption)
        let value = NSMutableAttributedString(attachment: attachment)
        let range = NSRange(location: 0, length: value.length)
        value.addAttribute(.cursor, value: NSCursor.pointingHand, range: range)
        value.addAttribute(.toolTip, value: caption.isEmpty ? key.label : caption, range: range)
        value.addAttribute(AgentTranscriptFormat.imageAttribute, value: key.id, range: range)
        return value
    }
}
