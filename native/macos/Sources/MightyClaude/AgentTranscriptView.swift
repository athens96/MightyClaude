import AppKit
import MightyCore
import SwiftUI

struct AgentTranscriptView: NSViewRepresentable {
    let sessionId: String
    let provider: String
    let running: Bool
    let entries: [LogEntry]
    let onFocus: () -> Void
    /// When set, file paths and addresses in the transcript become links and
    /// file references are delivered here instead of being opened by AppKit.
    var onReference: ((String, Int?) -> Void)? = nil
    @Environment(\.colorScheme) private var colorScheme

    func makeCoordinator() -> AgentTranscriptCoordinator { AgentTranscriptCoordinator() }
    func makeNSView(context: Context) -> NSScrollView {
        context.coordinator.makeScrollView(sessionId: sessionId)
    }
    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        context.coordinator.textView?.onFocus = onFocus
        context.coordinator.onReference = onReference
        context.coordinator.update(entries: entries, provider: provider, running: running, dark: colorScheme == .dark, references: onReference != nil)
    }
    static func dismantleNSView(_ scrollView: NSScrollView, coordinator: AgentTranscriptCoordinator) {
        coordinator.onReference = nil
        coordinator.textView?.onFocus = nil
        coordinator.textView?.onSelectionFinished = nil
        coordinator.textView?.delegate = nil
    }
}

struct AgentTranscriptDocument {
    struct Segment {
        let id: String
        let range: NSRange
        let value: NSAttributedString
    }
    let value: NSAttributedString
    let segments: [Segment]
    static let empty = AgentTranscriptDocument(value: NSAttributedString(string: ""), segments: [])

    struct Anchor { let id: String; let offset: Int; let segmentIndex: Int }
    func anchor(at position: Int, upper: Bool = false) -> Anchor? {
        let position = min(max(0, position), value.length)
        for (index, segment) in segments.enumerated() {
            if position < NSMaxRange(segment.range) || (upper && position == NSMaxRange(segment.range)) {
                return Anchor(id: segment.id, offset: max(0, position - segment.range.location), segmentIndex: index)
            }
        }
        guard let last = segments.last else { return nil }
        return Anchor(id: last.id, offset: last.range.length, segmentIndex: segments.count - 1)
    }

    func position(of anchor: Anchor?, from previous: AgentTranscriptDocument, upper: Bool = false) -> Int {
        guard let anchor else { return 0 }
        if let segment = segments.first(where: { $0.id == anchor.id }),
           let old = previous.segments.first(where: { $0.id == anchor.id }) {
            let offset = Self.mapOffset(anchor.offset, old: old.value.string, new: segment.value.string, upper: upper)
            let text = segment.value.string as NSString
            if offset > 0, offset < text.length {
                let cluster = text.rangeOfComposedCharacterSequence(at: offset)
                if cluster.location < offset { return segment.range.location + (upper ? NSMaxRange(cluster) : cluster.location) }
            }
            return segment.range.location + offset
        }
        // Log bounds may prune old entries. Keep the remaining selection near
        // its surviving neighbor rather than moving it to unrelated content.
        for old in previous.segments.dropFirst(anchor.segmentIndex) {
            if let segment = segments.first(where: { $0.id == old.id }) { return segment.range.location }
        }
        return value.length
    }

    private static func mapOffset(_ offset: Int, old: String, new: String, upper: Bool) -> Int {
        if old == new { return min(offset, new.utf16.count) }
        let a = Array(old.utf16), b = Array(new.utf16)
        var prefix = 0
        while prefix < min(a.count, b.count), a[prefix] == b[prefix] { prefix += 1 }
        var suffix = 0
        while suffix < min(a.count, b.count) - prefix, a[a.count - 1 - suffix] == b[b.count - 1 - suffix] { suffix += 1 }
        if offset <= prefix { return offset }
        if suffix > 0, offset >= a.count - suffix { return max(prefix, b.count - (a.count - offset)) }
        return upper ? b.count - suffix : prefix
    }
}

/// Keeps the same native text view and text storage across streaming updates.
/// Only changed entry ranges are replaced; selection endpoints are anchored to
/// entry IDs, so a tool changing above selected text does not displace it.
@MainActor
final class AgentTranscriptCoordinator: NSObject, NSTextViewDelegate {
    private struct Input: Equatable {
        var entries: [LogEntry]
        var provider: String
        var running: Bool
        var dark: Bool
        var references: Bool
    }
    private struct Cached {
        let entry: LogEntry
        let provider: String
        let running: Bool
        let dark: Bool
        let expanded: Bool
        let references: Bool
        let value: NSAttributedString
    }
    var onReference: ((String, Int?) -> Void)?
    private var latest: Input?
    private var applied: Input?
    private var cache: [String: Cached] = [:]
    private var expanded = Set<String>()
    private(set) weak var textView: AgentTranscriptTextView?
    private weak var scrollView: NSScrollView?

    func makeScrollView(sessionId: String) -> NSScrollView {
        let storage = NSTextStorage()
        let manager = NSLayoutManager()
        let container = NSTextContainer(containerSize: NSSize(width: 500, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        container.lineFragmentPadding = 0
        manager.addTextContainer(container)
        storage.addLayoutManager(manager)
        let editor = AgentTranscriptTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 300), textContainer: container)
        editor.isEditable = false; editor.isSelectable = true; editor.isRichText = true
        editor.importsGraphics = false; editor.allowsUndo = false
        editor.drawsBackground = false; editor.usesAdaptiveColorMappingForDarkAppearance = false
        editor.isVerticallyResizable = true; editor.isHorizontallyResizable = false
        editor.minSize = .zero; editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        editor.autoresizingMask = [.width]
        editor.textContainerInset = NSSize(width: 15, height: 15)
        editor.linkTextAttributes = [.foregroundColor: AgentTranscriptFormat.accent, .cursor: NSCursor.pointingHand]
        editor.selectedTextAttributes = [.backgroundColor: NSColor.selectedTextBackgroundColor, .foregroundColor: NSColor.selectedTextColor]
        editor.setAccessibilityIdentifier("transcript-\(sessionId)")
        editor.setAccessibilityLabel("실행 기록 · 드래그하여 여러 문단 선택")
        editor.delegate = self
        editor.onSelectionFinished = { [weak self] in self?.applyLatest() }
        let scroll = AgentTranscriptScrollView(frame: NSRect(x: 0, y: 0, width: 500, height: 300))
        scroll.drawsBackground = false; scroll.borderType = .noBorder
        scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = false
        scroll.autohidesScrollers = true
        scroll.documentView = editor
        textView = editor; scrollView = scroll
        return scroll
    }

    func update(entries: [LogEntry], provider: String, running: Bool, dark: Bool, references: Bool = false) {
        latest = Input(entries: entries, provider: provider, running: running, dark: dark, references: references)
        applyLatest()
    }

    private func applyLatest(force: Bool = false) {
        guard let input = latest, let editor = textView, !editor.isTrackingSelection,
              force || input != applied else { return }
        let liveIds = Set(input.entries.map(\.id))
        expanded.formIntersection(liveIds)
        cache = cache.filter { liveIds.contains($0.key) }
        let output = NSMutableAttributedString(string: "")
        var segments: [AgentTranscriptDocument.Segment] = []
        for entry in input.entries {
            let isExpanded = expanded.contains(entry.id)
            let value: NSAttributedString
            if let old = cache[entry.id], old.entry == entry, old.provider == input.provider,
               old.running == input.running, old.dark == input.dark, old.expanded == isExpanded, old.references == input.references { value = old.value }
            else {
                value = AgentTranscriptFormat.entry(entry, provider: input.provider, running: input.running, expanded: isExpanded, references: input.references)
                cache[entry.id] = Cached(entry: entry, provider: input.provider, running: input.running, dark: input.dark, expanded: isExpanded, references: input.references, value: value)
            }
            segments.append(.init(id: entry.id, range: NSRange(location: output.length, length: value.length), value: value))
            output.append(value)
        }
        let document = AgentTranscriptDocument(value: output, segments: segments)
        editor.replaceDocument(document)
        applied = input
    }

    func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
        guard let url = (link as? URL) ?? (link as? String).flatMap(URL.init(string:)) else { return true }
        if url.scheme == "mighty-transcript", url.host == "activity",
           let id = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "id" })?.value,
           latest?.entries.contains(where: { $0.id == id && $0.activity != nil }) == true {
            if !expanded.insert(id).inserted { expanded.remove(id) }
            // Native link activation happens inside mouseDown's tracking loop.
            // Apply after tracking ends, with the same selection preservation.
            applied = nil
            applyLatest(force: true)
            return true
        }
        if let reference = ReferenceLinkSupport.parseReferenceURL(url) {
            onReference?(reference.path, reference.line)
            return true
        }
        if AgentMarkdownDocument.safeLink(url) { NSWorkspace.shared.open(url) }
        return true // Never let AppKit open an unvalidated model-provided URL.
    }
}

@MainActor
private final class AgentTranscriptScrollView: NSScrollView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        for name in [NSScrollView.willStartLiveScrollNotification, NSScrollView.didLiveScrollNotification] {
            NotificationCenter.default.addObserver(self, selector: #selector(userDidScroll), name: name, object: self)
        }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    deinit { NotificationCenter.default.removeObserver(self) }

    override func layout() {
        super.layout()
        // NSScrollView has now tiled its final clip viewport. Position the
        // initial document before display, while keeping the deferred fallback.
        (documentView as? AgentTranscriptTextView)?.positionInitialScrollBeforeDisplay()
        (documentView as? AgentTranscriptTextView)?.scheduleInitialScrollToBottom()
    }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        (documentView as? AgentTranscriptTextView)?.scheduleInitialScrollToBottom()
    }
    override func scrollWheel(with event: NSEvent) {
        (documentView as? AgentTranscriptTextView)?.cancelInitialScroll()
        super.scrollWheel(with: event)
    }
    @objc private func userDidScroll(_ notification: Notification) {
        (documentView as? AgentTranscriptTextView)?.cancelInitialScroll()
    }
}

@MainActor
final class AgentTranscriptTextView: SelectableTextView {
    var onFocus: (() -> Void)?
    var onSelectionFinished: (() -> Void)?
    private(set) var isTrackingSelection = false
    private(set) var document = AgentTranscriptDocument.empty
    private var contextCode: String?
    private var synchronizingLayout = false
    private var initialScrollPending = true
    private var initialScrollScheduled = false
    private var positioningInitialScroll = false

    override func setFrameSize(_ newSize: NSSize) {
        let widthChanged = abs(newSize.width - frame.width) > 0.5
        super.setFrameSize(newSize)
        if widthChanged { synchronizeDocumentLayout(invalidatingFrom: 0) }
        scheduleInitialScrollToBottom()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        scheduleInitialScrollToBottom()
    }

    func cancelInitialScroll() { initialScrollPending = false }

    /// No SwiftUI state is published here. Repeated native layouts may refine
    /// the viewport before the deferred one-shot consumes the initial decision.
    @discardableResult
    func positionInitialScrollBeforeDisplay() -> Bool {
        guard initialScrollPending, !positioningInitialScroll, !synchronizingLayout,
              document.value.length > 0, window != nil, let scroll = enclosingScrollView,
              scroll.contentView.bounds.width > 1, scroll.contentView.bounds.height > 1 else { return false }
        guard !isTrackingSelection, selectedRanges.allSatisfy({ $0.rangeValue.length == 0 }) else {
            cancelInitialScroll(); return false
        }
        positioningInitialScroll = true
        defer { positioningInitialScroll = false }
        synchronizeDocumentLayout(invalidatingFrom: 0)
        let clip = scroll.contentView
        clip.scroll(to: NSPoint(x: clip.bounds.minX, y: max(0, bounds.height - clip.bounds.height)))
        scroll.reflectScrolledClipView(clip)
        return true
    }

    /// Restored text can arrive before the native view has a window or its
    /// final SwiftUI viewport. Resolve that layout once, after mounting, rather
    /// than consuming the initial follow decision against the temporary frame.
    func scheduleInitialScrollToBottom() {
        guard initialScrollPending, !initialScrollScheduled, document.value.length > 0,
              window != nil, enclosingScrollView != nil else { return }
        initialScrollScheduled = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            defer { self.initialScrollScheduled = false }
            guard self.initialScrollPending, let window = self.window,
                  let scroll = self.enclosingScrollView else { return }
            guard !self.isTrackingSelection, self.selectedRanges.allSatisfy({ $0.rangeValue.length == 0 }) else {
                self.cancelInitialScroll(); return
            }
            window.contentView?.layoutSubtreeIfNeeded()
            scroll.layoutSubtreeIfNeeded()
            let clip = scroll.contentView
            guard clip.bounds.width > 1, clip.bounds.height > 1 else { return }
            if self.positionInitialScrollBeforeDisplay() { self.initialScrollPending = false }
        }
    }

    override func mouseDown(with event: NSEvent) {
        cancelInitialScroll()
        onFocus?()
        isTrackingSelection = true
        // super is SelectableTextView: it runs AppKit's tracking loop and then
        // takes the first responder back if the drag selected anything.
        defer { isTrackingSelection = false; onSelectionFinished?() }
        super.mouseDown(with: event)
    }

    override func keyDown(with event: NSEvent) {
        cancelInitialScroll()
        super.keyDown(with: event)
    }

    func replaceDocument(_ next: AgentTranscriptDocument) {
        guard let storage = textStorage else { return }
        let previous = document
        let ranges = selectedRanges.map(\.rangeValue)
        let selection = ranges.map { (previous.anchor(at: $0.location), previous.anchor(at: NSMaxRange($0), upper: true)) }
        let clip = enclosingScrollView?.contentView
        let origin = clip?.bounds.origin ?? .zero
        let visibleHeight = clip?.bounds.height ?? 0
        let follow = previous.value.length == 0 || (ranges.allSatisfy { $0.length == 0 } && origin.y + visibleHeight >= bounds.height - 36)
        let topIndex = characterIndexForInsertion(at: NSPoint(x: textContainerInset.width + 2, y: origin.y + 2))
        let top = previous.anchor(at: topIndex)
        let offset = origin.y - lineY(at: topIndex)

        var prefix = 0
        while prefix < min(previous.segments.count, next.segments.count),
              previous.segments[prefix].id == next.segments[prefix].id,
              previous.segments[prefix].value.isEqual(to: next.segments[prefix].value) { prefix += 1 }
        var suffix = 0
        while suffix < min(previous.segments.count, next.segments.count) - prefix {
            let old = previous.segments[previous.segments.count - 1 - suffix]
            let new = next.segments[next.segments.count - 1 - suffix]
            guard old.id == new.id, old.value.isEqual(to: new.value) else { break }
            suffix += 1
        }
        let start = prefix < previous.segments.count ? previous.segments[prefix].range.location : previous.value.length
        let oldEnd = suffix == 0 ? previous.value.length : previous.segments[previous.segments.count - suffix].range.location
        let newStart = prefix < next.segments.count ? next.segments[prefix].range.location : next.value.length
        let newEnd = suffix == 0 ? next.value.length : next.segments[next.segments.count - suffix].range.location
        document = next
        if oldEnd != start || newEnd != newStart {
            storage.beginEditing()
            storage.replaceCharacters(in: NSRange(location: start, length: oldEnd - start), with: next.value.attributedSubstring(from: NSRange(location: newStart, length: newEnd - newStart)))
            storage.endEditing()
            // A disclosure changes a text block's height while the following
            // attributed segments remain cached. Reflow AND redraw that suffix;
            // ensureLayout alone can retain valid glyph/display caches below it.
            synchronizeDocumentLayout(invalidatingFrom: start)
        }
        let restored = selection.map { lower, upper -> NSValue in
            let start = next.position(of: lower, from: previous)
            let end = max(start, next.position(of: upper, from: previous, upper: true))
            return NSValue(range: NSRange(location: start, length: end - start))
        }
        setSelectedRanges(restored.isEmpty ? [NSValue(range: NSRange(location: 0, length: 0))] : restored, affinity: selectionAffinity, stillSelecting: false)
        if let container = textContainer { layoutManager?.ensureLayout(for: container) }
        if follow { scrollRangeToVisible(NSRange(location: next.value.length, length: 0)) }
        else if let clip {
            let index = next.position(of: top, from: previous)
            let y = min(max(0, lineY(at: index) + offset), max(0, bounds.height - clip.bounds.height))
            clip.scroll(to: NSPoint(x: origin.x, y: y))
            enclosingScrollView?.reflectScrolledClipView(clip)
        }
        scheduleInitialScrollToBottom()
    }

    private func synchronizeDocumentLayout(invalidatingFrom start: Int) {
        guard !synchronizingLayout, let storage = textStorage,
              let manager = layoutManager, let container = textContainer else { return }
        synchronizingLayout = true
        defer { synchronizingLayout = false }
        let width = max(1, frame.width - textContainerInset.width * 2)
        if abs(container.containerSize.width - width) > 0.5 {
            container.containerSize = NSSize(width: width, height: CGFloat.greatestFiniteMagnitude)
        }
        let location = min(max(0, start), storage.length)
        let suffix = NSRange(location: location, length: storage.length - location)
        manager.invalidateLayout(forCharacterRange: suffix, actualCharacterRange: nil)
        manager.invalidateDisplay(forCharacterRange: suffix)
        manager.ensureLayout(for: container)
        // The scroll view owns the viewport, while this native document owns
        // its entire laid-out height, including content outside the viewport.
        let bottom = max(manager.usedRect(for: container).maxY, manager.extraLineFragmentRect.maxY)
        let height = max(enclosingScrollView?.contentView.bounds.height ?? 0,
                         ceil(bottom + textContainerInset.height * 2))
        if abs(frame.height - height) > 0.5 { super.setFrameSize(NSSize(width: frame.width, height: height)) }
        // Clear old glyph pixels as well as their new positions after collapse
        // or expansion, including a cached suffix already visible on screen.
        needsDisplay = true
        enclosingScrollView?.contentView.needsDisplay = true
    }

    private func lineY(at index: Int) -> CGFloat {
        guard let manager = layoutManager, manager.numberOfGlyphs > 0, document.value.length > 0 else { return 0 }
        let glyph = manager.glyphIndexForCharacter(at: min(max(0, index), document.value.length - 1))
        return manager.lineFragmentRect(forGlyphAt: glyph, effectiveRange: nil).minY + textContainerOrigin.y
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let menu = super.menu(for: event) ?? NSMenu()
        let point = convert(event.locationInWindow, from: nil)
        let index = characterIndexForInsertion(at: point)
        contextCode = index < (textStorage?.length ?? 0) ? textStorage?.attribute(AgentTranscriptFormat.codeAttribute, at: index, effectiveRange: nil) as? String : nil
        if contextCode != nil {
            menu.addItem(.separator())
            let item = NSMenuItem(title: "코드 블록 복사", action: #selector(copyCode), keyEquivalent: "")
            item.target = self; menu.addItem(item)
        }
        return menu
    }

    @objc private func copyCode() {
        guard let contextCode else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(contextCode, forType: .string)
    }
}
