import AppKit
import MightyCore
import SwiftUI

/// Text the user may select but not edit. AppKit hands ⌘C to the first
/// responder alone, and inside the Mighty graph the first responder moves away
/// from a card constantly: the canvas monitor takes it for every pan, every
/// block resize and every click that is not on a native text view, while the
/// selection stays painted where it was made. So these views take focus back
/// as soon as a drag actually selected something, and — for the times focus
/// legitimately belongs elsewhere — claim the Copy key equivalent for the view
/// the user selected in last, but only when whatever is focused cannot copy for
/// itself.
@MainActor
class SelectableTextView: NSTextView {
    /// The view the user most recently finished a selection in. Weak, so an
    /// unmounted card stops competing for ⌘C by simply going away.
    private static weak var mostRecentlySelected: SelectableTextView?
    /// Diagnostics hand in their own pasteboard; a check must never overwrite
    /// what the user has on the clipboard.
    var copyPasteboard: NSPasteboard = .general
    var selectionLength: Int { selectedRanges.reduce(0) { $0 + $1.rangeValue.length } }

    override func mouseDown(with event: NSEvent) {
        super.mouseDown(with: event)
        claimSelectionFocus()
    }

    /// Mouse tracking has ended. A non-empty selection makes this view both the
    /// ⌘C owner and, when the monitor moved focus during the click, the first
    /// responder again; an empty one gives up the claim it may have held.
    private func claimSelectionFocus() {
        guard let window else { return }
        guard selectionLength > 0 else {
            if Self.mostRecentlySelected === self { Self.mostRecentlySelected = nil }
            return
        }
        Self.mostRecentlySelected = self
        if window.firstResponder !== self { window.makeFirstResponder(self) }
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard event.type == .keyDown,
              TranscriptCopyClaim.isCopyKeyEquivalent(modifiers: event.modifierFlags.rawValue,
                                                      characters: event.charactersIgnoringModifiers,
                                                      keyCode: event.keyCode),
              let window, window === event.window, window.firstResponder !== self,
              TranscriptCopyClaim.claims(copyKeyEquivalent: true, selectionLength: selectionLength,
                                         isMostRecentlySelected: Self.mostRecentlySelected === self,
                                         isVisible: !isHiddenOrHasHiddenAncestor && !visibleRect.isEmpty,
                                         isSelectionOnScreen: isSelectionOnScreen,
                                         isBlocked: window.attachedSheet != nil,
                                         responder: Self.responderState(window.firstResponder))
        else { return super.performKeyEquivalent(with: event) }
        // The same flavours the Copy menu item writes, so a claimed ⌘C and an
        // ordinary one leave the pasteboard in the same shape. Declaring the
        // types is what makes the per-type writes below stick.
        let types = writablePasteboardTypes
        copyPasteboard.declareTypes(types, owner: nil)
        return writeSelection(to: copyPasteboard, types: types)
    }

    /// Whether any of the drawn selection is really on screen. A card preview
    /// clips its text, and a selection scrolled out of that clip is not
    /// something the user can point at — ⌘C must not silently mean it.
    private var isSelectionOnScreen: Bool {
        guard let manager = layoutManager, let container = textContainer else { return false }
        let visible = visibleRect
        guard !visible.isEmpty else { return false }
        let origin = textContainerOrigin
        for range in selectedRanges.map(\.rangeValue) where range.length > 0 {
            let glyphs = manager.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            var box = manager.boundingRect(forGlyphRange: glyphs, in: container)
            box.origin.x += origin.x
            box.origin.y += origin.y
            if box.intersects(visible) { return true }
        }
        return false
    }

    /// An allowlist, not a guess at what the focused view is: anything that
    /// implements `copy(_:)` already has a ⌘C of its own. That covers every
    /// NSTextView, an NSTextField's field editor and the Ghostty terminal view,
    /// and it keeps a focused editor with an empty selection the no-op macOS
    /// users expect instead of copying a card behind it.
    private static func responderState(_ responder: NSResponder?) -> TranscriptCopyClaim.Responder {
        guard let responder else { return .plain }
        return TranscriptCopyClaim.Responder(canCopyItself: responder.responds(to: #selector(NSText.copy(_:))))
    }

    /// Attachment placeholders are layout, not text the user asked for.
    override func writeSelection(to pasteboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool {
        if type == .string {
            let source = string as NSString
            let selected = selectedRanges.map(\.rangeValue).filter { NSMaxRange($0) <= source.length }
                .map { source.substring(with: $0).replacingOccurrences(of: "\u{FFFC}", with: "") }.joined(separator: "\n")
            return pasteboard.setString(selected, forType: .string)
        }
        return super.writeSelection(to: pasteboard, type: type)
    }
}

/// The plain request and draft previews inside a graph card. `Text(...)
/// .textSelection(.enabled)` cannot be used here: the canvas monitor only
/// leaves the first responder alone when the click landed on a native text
/// view, and a SwiftUI selection lives in the hosting view it just defocused —
/// drawn, but unreachable by ⌘C.
@MainActor
final class MightyGraphPreviewTextView: SelectableTextView {
    struct Input: Equatable {
        var text: String
        var fontSize: CGFloat
        var secondary: Bool
        /// 0 wraps without limit; a positive value truncates like `lineLimit`.
        var maximumLines: Int
        var width: CGFloat
    }
    private var applied: Input?
    private var reportedHeight: CGFloat?

    /// Lays the text out at `width` and returns the height it needs, never more
    /// than `maximumLines` laid-out lines of this very text.
    @discardableResult
    func apply(_ input: Input) -> CGFloat {
        guard let storage = textStorage, let manager = layoutManager, let container = textContainer else { return frame.height }
        let width = max(1, input.width)
        if applied != input {
            applied = input
            // NSTextView.setFrameSize resizes the text container along with the
            // view; NSView's implementation would leave the container behind at
            // the old width and lay the text out for a box that is not there.
            if abs(frame.width - width) > 0.5 { setFrameSize(NSSize(width: width, height: max(1, frame.height))) }
            container.maximumNumberOfLines = max(0, input.maximumLines)
            let paragraph = NSMutableParagraphStyle()
            paragraph.lineBreakMode = input.maximumLines > 0 ? .byTruncatingTail : .byWordWrapping
            let color = input.secondary ? NSColor.secondaryLabelColor : NSColor.labelColor
            storage.setAttributedString(NSAttributedString(string: input.text, attributes: [
                .font: NSFont.systemFont(ofSize: input.fontSize), .foregroundColor: color, .paragraphStyle: paragraph,
            ]))
        }
        manager.ensureLayout(for: container)
        var used = max(manager.usedRect(for: container).maxY, manager.extraLineFragmentRect.maxY)
        if input.maximumLines > 0 { used = min(used, cap(lines: input.maximumLines, manager: manager)) }
        let height = max(1, ceil(used))
        if abs(frame.height - height) > 0.5 { setFrameSize(NSSize(width: width, height: height)) }
        return height
    }

    /// The bottom of the `limit`-th laid-out line fragment of the text that is
    /// actually there. Taken from the layout manager rather than from the Latin
    /// system font's default line height, so a Hangul or emoji fallback that
    /// draws taller is reserved for as it will really be drawn.
    private func cap(lines limit: Int, manager: NSLayoutManager) -> CGFloat {
        var bottom: CGFloat = 0
        var glyph = 0
        var counted = 0
        while glyph < manager.numberOfGlyphs, counted < limit {
            var effective = NSRange(location: 0, length: 0)
            bottom = max(bottom, manager.lineFragmentRect(forGlyphAt: glyph, effectiveRange: &effective).maxY)
            counted += 1
            guard effective.length > 0 else { break }
            glyph = NSMaxRange(effective)
        }
        return bottom > 0 ? bottom : manager.extraLineFragmentRect.maxY
    }

    /// True when this height differs from the one SwiftUI was last told about,
    /// so an update pass that changed nothing schedules nothing.
    func noteReportedHeight(_ height: CGFloat) -> Bool {
        if let reportedHeight, abs(reportedHeight - height) <= 0.5 { return false }
        reportedHeight = height
        return true
    }
}

/// Selectable plain text in a graph card, sized by its caller.
struct MightyGraphSelectableText: NSViewRepresentable {
    let text: String
    /// Width the text wraps at; the caller owns the surrounding frame.
    let width: CGFloat
    var fontSize: CGFloat = 11
    var secondary = false
    var maximumLines = 0
    var identifier: String? = nil
    var accessibilityLabel: String? = nil
    /// The height the text needed, reported after the layout pass.
    var onHeight: (CGFloat) -> Void = { _ in }

    func makeNSView(context: Context) -> NSScrollView {
        let storage = NSTextStorage()
        let manager = NSLayoutManager()
        let container = NSTextContainer(containerSize: NSSize(width: max(1, width), height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        container.lineFragmentPadding = 0
        manager.addTextContainer(container)
        storage.addLayoutManager(manager)
        let view = MightyGraphPreviewTextView(frame: NSRect(x: 0, y: 0, width: max(1, width), height: 14), textContainer: container)
        view.isEditable = false; view.isSelectable = true; view.isRichText = false
        view.importsGraphics = false; view.allowsUndo = false
        view.drawsBackground = false; view.usesAdaptiveColorMappingForDarkAppearance = false
        view.isVerticallyResizable = true; view.isHorizontallyResizable = false
        view.textContainerInset = .zero
        view.minSize = .zero
        view.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        view.selectedTextAttributes = [.backgroundColor: NSColor.selectedTextBackgroundColor, .foregroundColor: NSColor.selectedTextColor]
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: max(1, width), height: 14))
        scroll.drawsBackground = false; scroll.borderType = .noBorder
        // A line-limited preview truncates at its cap instead of scrolling, so
        // there is nothing past the bottom for a scroller to reach.
        scroll.hasVerticalScroller = maximumLines == 0
        scroll.hasHorizontalScroller = false
        scroll.autohidesScrollers = true
        scroll.documentView = view
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let view = scroll.documentView as? MightyGraphPreviewTextView else { return }
        scroll.hasVerticalScroller = maximumLines == 0
        if let identifier { view.setAccessibilityIdentifier(identifier) }
        // Always assigned: a label left over from an earlier draft would
        // describe text that is no longer there.
        view.setAccessibilityLabel(accessibilityLabel)
        let height = view.apply(.init(text: text, fontSize: fontSize, secondary: secondary, maximumLines: maximumLines, width: width))
        // Publishing a SwiftUI value from inside updateNSView is undefined;
        // report the measured height after this layout pass instead, and only
        // when it actually moved.
        guard view.noteReportedHeight(height) else { return }
        let report = onHeight
        DispatchQueue.main.async { report(height) }
    }
}
