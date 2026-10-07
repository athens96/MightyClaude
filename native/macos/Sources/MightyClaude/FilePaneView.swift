import AppKit
import MightyCore
import SwiftUI

/// The read-only files pane: the workspace tree on the left, a preview of the
/// selected file on the right. See docs/file-pane.md.
struct FilePaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    var body: some View {
        if let model = store.filePaneModel(for: session.workspaceId) { FilePaneContent(model: model) }
        else { Color.clear }
    }
}

private struct FilePaneContent: View {
    @ObservedObject var model: FilePaneModel
    @FocusState private var treeFocused: Bool

    var body: some View {
        HSplitView {
            tree.frame(minWidth: 160, idealWidth: 240, maxWidth: 520, maxHeight: .infinity)
            FilePanePreviewView(model: model).frame(minWidth: 160, maxWidth: .infinity, maxHeight: .infinity)
        }
        .onAppear { model.start() }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("files-pane-\(model.workspaceId)")
    }

    private var tree: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "line.3.horizontal.decrease").font(.system(size: 10)).foregroundStyle(.tertiary)
                TextField(L("files.tree.filter"), text: $model.filter)
                    .textFieldStyle(.plain).font(.system(size: 11))
                    .accessibilityLabel(L("files.tree.filter"))
                Button { model.refresh() } label: { Image(systemName: "arrow.clockwise").font(.system(size: 11)) }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
                    .help(L("files.tree.refresh")).accessibilityLabel(L("files.tree.refresh"))
                    .accessibilityIdentifier("files-refresh")
            }
            .padding(.horizontal, 10).padding(.vertical, 7)
            .background(Palette.subtle)
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        let filtering = model.isFiltering
                        ForEach(model.rows) { row in
                            FilePaneRow(row: row, isSelected: model.selectedPath == row.id, filtering: filtering) {
                                treeFocused = true
                                model.select(row.entry)
                                if row.entry.isDirectory, !filtering { model.toggle(row.entry) }
                            }
                            .equatable().id(row.id)
                        }
                        if let error = model.folderErrors[""] { note(error) }
                        else if model.truncated.contains("") { note(L("files.tree.truncated", ["count": "\(WorkspaceFiles.maximumEntriesPerFolder)"])) }
                        if filtering, model.rows.isEmpty { note(L("files.tree.noMatches")) }
                        if filtering, model.filterHitCap { note(L("files.tree.moreResults", ["count": FilePaneModel.maximumFilterResults.formatted()])) }
                    }
                    .padding(.vertical, 4)
                }
                .onChange(of: model.selectedPath) { _, path in if let path { proxy.scrollTo(path) } }
                // A folder revealed from the filter may already be the selection.
                .onChange(of: model.isFiltering) { _, filtering in if !filtering, let path = model.selectedPath { proxy.scrollTo(path) } }
            }
            .focusable()
            .focused($treeFocused)
            .focusEffectDisabled()
            .onKeyPress(.upArrow) { model.moveSelection(by: -1); return .handled }
            .onKeyPress(.downArrow) { model.moveSelection(by: 1); return .handled }
            .onKeyPress(.leftArrow) { model.collapseOrParent(); return .handled }
            .onKeyPress(.rightArrow) { model.expandOrChild(); return .handled }
            .onKeyPress(.return) { model.activateSelection(); return .handled }
            .accessibilityIdentifier("files-tree")
        }
        .background(Palette.panel)
    }

    private func note(_ text: String) -> some View {
        Text(text).font(.system(size: 10)).foregroundStyle(.secondary).padding(.horizontal, 12).padding(.vertical, 6)
    }
}

/// One tree row. It takes plain values rather than the model, so a change
/// elsewhere in the tree does not redraw every row.
private struct FilePaneRow: View, Equatable {
    let row: FilePaneModel.Row
    let isSelected: Bool
    let filtering: Bool
    let onTap: () -> Void

    static func == (lhs: FilePaneRow, rhs: FilePaneRow) -> Bool {
        lhs.row == rhs.row && lhs.isSelected == rhs.isSelected && lhs.filtering == rhs.filtering
    }

    private var entry: WorkspaceFileEntry { row.entry }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 5) {
                Group {
                    if entry.isDirectory, !filtering { Image(systemName: row.isExpanded ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .semibold)) }
                    else { Color.clear }
                }.frame(width: 10).foregroundStyle(.secondary)
                Image(systemName: entry.isDirectory ? "folder" : FilePaneIcons.symbol(for: entry.name))
                    .font(.system(size: 11)).frame(width: 14)
                    .foregroundStyle(entry.isDirectory ? Palette.accent : Color.secondary)
                Text(entry.name).font(.system(size: 11)).lineLimit(1).truncationMode(.middle)
                if entry.isSymlink { Image(systemName: "arrow.turn.up.right").font(.system(size: 8)).foregroundStyle(.tertiary) }
                if filtering, entry.relativePath != entry.name {
                    Text(entry.relativePath).font(.system(size: 9, design: .monospaced)).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.head)
                }
                Spacer(minLength: 0)
            }
            .padding(.leading, 8 + CGFloat(row.depth) * 14).padding(.trailing, 8).padding(.vertical, 4)
            .opacity(entry.isNoise ? 0.55 : 1)
            .background(isSelected ? Palette.accent.opacity(0.18) : Color.clear, in: RoundedRectangle(cornerRadius: 5))
            .padding(.horizontal, 4)
            .contentShape(Rectangle())
            .onTapGesture(perform: onTap)
            .help(entry.relativePath)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(entry.name)
            .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
            .accessibilityIdentifier("files-row-\(entry.relativePath)")
            if let caption = row.caption, !filtering { self.caption(caption) }
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(.system(size: 10)).foregroundStyle(.tertiary)
            .padding(.leading, 8 + CGFloat(row.depth + 1) * 14 + 29).padding(.vertical, 3)
    }
}

enum FilePaneIcons {
    static func symbol(for name: String) -> String {
        switch FilePreviewClassifier.kind(forName: name) {
        case .markdown?: return "doc.richtext"
        case .image?: return "photo"
        case .source(.plain)?: return "doc.text"
        case .source?: return "chevron.left.forwardslash.chevron.right"
        default: return "doc"
        }
    }
}

// MARK: - Preview

private struct FilePanePreviewView: View {
    @ObservedObject var model: FilePaneModel
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        VStack(spacing: 0) {
            if let info = model.preview.info {
                header(info)
                Divider()
            }
            content.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(Palette.panel)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("files-preview")
    }

    private func header(_ info: FilePaneFileInfo) -> some View {
        HStack(spacing: 8) {
            Image(systemName: FilePaneIcons.symbol(for: info.name)).foregroundStyle(Palette.accent)
            VStack(alignment: .leading, spacing: 1) {
                Text(info.name).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Text(info.relativePath).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 6)
            controls
            Button { NSWorkspace.shared.activateFileViewerSelecting([info.url]) } label: { Image(systemName: "folder") }
                .help(L("menu.showInFinder")).accessibilityLabel(L("menu.showInFinder"))
        }
        .buttonStyle(.plain).font(.system(size: 12))
        .padding(.horizontal, 12).frame(height: DesignMetrics.Layout.previewHead)
        .background(Palette.subtle)
    }

    @ViewBuilder private var controls: some View {
        switch model.preview {
        case .source(_, let text):
            encoding(text.encoding)
        case .markdown(_, let text, let renderable):
            encoding(text.encoding)
            if renderable {
                Picker("", selection: $model.showsMarkdownSource) {
                    Text(L("files.markdown.rendered")).tag(false)
                    Text(L("files.markdown.source")).tag(true)
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
                .accessibilityIdentifier("files-markdown-mode")
            }
        case .image(_, let image):
            let limit = FileImagePreview.zoomLimit(image, scale: displayScale)
            let zoomable = image.isVector || image.fullSizeAllowed
            Text(L("files.image.pixels", ["width": "\(Int(image.size.width))", "height": "\(Int(image.size.height))"]))
                .font(.system(size: 10)).foregroundStyle(.secondary).monospacedDigit()
            Group {
                Button { model.setImageZoom(min(limit, max(0.1, (model.imageZoom ?? 1) / 1.25))) } label: { Image(systemName: "minus.magnifyingglass") }
                    .help(L("files.image.zoomOut")).accessibilityLabel(L("files.image.zoomOut"))
                Button { model.setImageZoom(nil) } label: { Image(systemName: "arrow.up.left.and.down.right.and.arrow.up.right.and.down.left") }
                    .help(L("files.image.fit")).accessibilityLabel(L("files.image.fit"))
                Button { model.setImageZoom(min(1, limit)) } label: { Text("1:1").font(.system(size: 10, weight: .medium)) }
                    .help(L("files.image.actualSize")).accessibilityLabel(L("files.image.actualSize"))
                Button { model.setImageZoom(min(limit, (model.imageZoom ?? 1) * 1.25)) } label: { Image(systemName: "plus.magnifyingglass") }
                    .help(L("files.image.zoomIn")).accessibilityLabel(L("files.image.zoomIn"))
            }
            .disabled(!zoomable)
        default:
            EmptyView()
        }
    }

    private func encoding(_ encoding: TextEncoding) -> some View {
        Text(encoding.displayName).font(.system(size: 10)).foregroundStyle(.secondary)
            .help(L("files.preview.encoding")).accessibilityLabel(L("files.preview.encoding") + " " + encoding.displayName)
            .accessibilityIdentifier("files-encoding")
    }

    @ViewBuilder private var content: some View {
        switch model.preview {
        case .none:
            notice(symbol: "sidebar.left", title: L("files.preview.placeholder"), detail: nil)
        case .loading:
            ProgressView().controlSize(.small)
        case .source(let info, let text):
            VStack(spacing: 0) {
                banners(text)
                FileSourceTextView(content: text, identity: identity(info, "source"))
            }
        case .markdown(let info, let text, let renderable):
            VStack(spacing: 0) {
                if !renderable { banner(L("files.markdown.tooLarge")) }
                banners(text)
                if model.showsMarkdownSource || !renderable {
                    FileSourceTextView(content: text, identity: identity(info, "markdown-source"))
                } else {
                    ScrollView { AgentMarkdownView(source: text.text).padding(18).frame(maxWidth: 860, alignment: .leading) }
                        .accessibilityIdentifier("files-markdown")
                }
            }
        case .image(_, let image):
            VStack(spacing: 0) {
                if !image.isVector, !image.fullSizeAllowed {
                    banner(L("files.image.fitOnly", ["count": (FilePreviewClassifier.maximumFullPixels / 1_000_000).formatted()]))
                }
                FileImagePreview(image: image, zoom: model.imageZoom)
            }
        case .unsupported(let info, let reason):
            VStack(spacing: 8) {
                Image(systemName: "doc.questionmark").font(.system(size: 30, weight: .light)).foregroundStyle(.secondary)
                Text(L("files.preview.unsupported")).font(.system(size: 13, weight: .semibold))
                Text(info.name).font(.system(size: 12)).lineLimit(2).multilineTextAlignment(.center)
                Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 3) {
                    GridRow {
                        Text(L("files.preview.size")).foregroundStyle(.secondary)
                        Text(ByteCountFormatter.string(fromByteCount: info.size, countStyle: .file)).monospacedDigit()
                    }
                    if let modified = info.modified {
                        GridRow {
                            Text(L("files.preview.modified")).foregroundStyle(.secondary)
                            Text(modified.formatted(date: .abbreviated, time: .shortened))
                        }
                    }
                }.font(.system(size: 11))
                if let reason { Text(reason).font(.system(size: 11)).foregroundStyle(.secondary).multilineTextAlignment(.center) }
                Button(L("menu.showInFinder")) { NSWorkspace.shared.activateFileViewerSelecting([info.url]) }
                    .padding(.top, 4).accessibilityIdentifier("files-show-in-finder")
            }
            .padding(24)
            .accessibilityIdentifier("files-unsupported")
        case .failed(let message):
            notice(symbol: "exclamationmark.triangle", title: message, detail: nil)
        }
    }

    @ViewBuilder private func banners(_ text: FilePaneText) -> some View {
        if text.truncated { banner(L("files.preview.truncated")) }
        if text.highlightCapped { banner(L("files.preview.highlightCapped", ["count": SourceHighlighter.maximumUnits.formatted()])) }
        if text.wraps { banner(L("files.preview.wrapped", ["count": SourceLines.wrapThreshold.formatted()])) }
    }

    private func identity(_ info: FilePaneFileInfo, _ mode: String) -> String {
        "\(mode)|\(info.relativePath)|\(info.size)|\(info.modified?.timeIntervalSince1970 ?? 0)"
    }

    private func banner(_ text: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: "info.circle")
            Text(text)
            Spacer(minLength: 0)
        }
        .font(.system(size: 10)).foregroundStyle(.secondary)
        .padding(.horizontal, 12).padding(.vertical, 6)
        .background(Palette.waitSoft)
    }

    private func notice(symbol: String, title: String, detail: String?) -> some View {
        VStack(spacing: 6) {
            Image(systemName: symbol).font(.system(size: 24, weight: .light)).foregroundStyle(.secondary)
            Text(title).font(.system(size: 12)).foregroundStyle(.secondary).multilineTextAlignment(.center)
            if let detail { Text(detail).font(.system(size: 11)).foregroundStyle(.tertiary) }
        }
        .padding(20)
    }
}

private struct FileImagePreview: View {
    let image: FilePaneImage
    let zoom: CGFloat?
    @Environment(\.displayScale) private var displayScale

    /// The image's size in points at actual size: a bitmap's pixels over the
    /// screen's backing scale (one image pixel per screen pixel), a vector
    /// image's own points.
    static func baseSize(_ image: FilePaneImage, scale: CGFloat) -> CGSize {
        let divisor = image.isVector ? 1 : max(1, scale)
        return CGSize(width: max(1, image.size.width / divisor), height: max(1, image.size.height / divisor))
    }

    /// The largest zoom that keeps the image under the point limit.
    static func zoomLimit(_ image: FilePaneImage, scale: CGFloat) -> CGFloat {
        let base = baseSize(image, scale: scale)
        return CGFloat(FilePreviewClassifier.maximumZoomPoints) / max(base.width, base.height)
    }

    var body: some View {
        GeometryReader { geometry in
            let base = Self.baseSize(image, scale: displayScale)
            if let zoom {
                ScrollView([.horizontal, .vertical]) {
                    Image(nsImage: image.full ?? image.display).resizable().interpolation(zoom >= 2 ? .none : .high)
                        .frame(width: base.width * zoom, height: base.height * zoom)
                        .padding(12)
                        .frame(minWidth: geometry.size.width, minHeight: geometry.size.height)
                }
            } else {
                // Fit: shrink to the pane, never enlarge past actual size.
                Image(nsImage: image.display).resizable().interpolation(.high).scaledToFit()
                    .frame(maxWidth: min(base.width, max(1, geometry.size.width - 24)), maxHeight: min(base.height, max(1, geometry.size.height - 24)))
                    .frame(width: geometry.size.width, height: geometry.size.height)
            }
        }
        .background(Palette.canvas)
        .accessibilityElement()
        .accessibilityLabel(L("files.image.pixels", ["width": "\(Int(image.size.width))", "height": "\(Int(image.size.height))"]))
        .accessibilityIdentifier("files-image")
    }
}

// MARK: - Source text

/// A read-only, selectable, monospaced text view with line numbers. TextKit 1
/// with non-contiguous layout keeps a 1 MB file responsive. The text arrives
/// already attributed (built off the main thread by the model); a text with
/// a line over `SourceLines.wrapThreshold` wraps instead of scrolling sideways.
struct FileSourceTextView: NSViewRepresentable {
    let content: FilePaneText
    /// Changes whenever different text is shown; the view reloads only then.
    let identity: String

    final class Coordinator { var identity: String? }
    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSScrollView {
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        layout.allowsNonContiguousLayout = true
        storage.addLayoutManager(layout)
        let container = NSTextContainer(size: NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = false
        layout.addTextContainer(container)
        let textView = NSTextView(frame: .zero, textContainer: container)
        textView.isEditable = false
        textView.isSelectable = true
        textView.usesFindBar = true
        textView.isIncrementalSearchingEnabled = true
        textView.drawsBackground = false
        textView.isHorizontallyResizable = true
        textView.isVerticallyResizable = true
        textView.minSize = .zero
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.autoresizingMask = [.width, .height]
        textView.textContainerInset = NSSize(width: 6, height: 8)
        textView.setAccessibilityIdentifier("files-source-text")
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        scroll.documentView = textView
        scroll.verticalRulerView = FileLineNumberRuler(textView: textView, scrollView: scroll)
        scroll.hasVerticalRuler = true
        scroll.rulersVisible = true
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard context.coordinator.identity != identity, let textView = scroll.documentView as? NSTextView, let container = textView.textContainer else { return }
        context.coordinator.identity = identity
        // Set the wrapping first so the new text is laid out only once.
        if content.wraps {
            scroll.hasHorizontalScroller = false
            textView.isHorizontallyResizable = false
            textView.autoresizingMask = [.width]
            textView.frame.size.width = scroll.contentSize.width
            container.containerSize = NSSize(width: scroll.contentSize.width, height: CGFloat.greatestFiniteMagnitude)
            container.widthTracksTextView = true
        } else {
            container.widthTracksTextView = false
            container.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
            textView.isHorizontallyResizable = true
            textView.autoresizingMask = [.width, .height]
            scroll.hasHorizontalScroller = true
        }
        textView.textStorage?.setAttributedString(content.attributed)
        (scroll.verticalRulerView as? FileLineNumberRuler)?.update(lineStarts: content.lineStarts)
        textView.setSelectedRange(NSRange(location: 0, length: 0))
        textView.scroll(.zero)
    }

    /// Safe off the main thread: the model builds it while loading.
    nonisolated static func attributed(_ text: String, tokens: [SourceToken]) -> NSAttributedString {
        let result = NSMutableAttributedString(string: text, attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
            .foregroundColor: NSColor.labelColor,
        ])
        let length = result.length
        result.beginEditing()
        for token in tokens where token.location >= 0 && token.length > 0 && token.location + token.length <= length {
            result.addAttribute(.foregroundColor, value: color(token.kind), range: NSRange(location: token.location, length: token.length))
        }
        result.endEditing()
        return result
    }

    private nonisolated static func color(_ kind: SourceToken.Kind) -> NSColor {
        switch kind {
        case .keyword: return .systemPink
        case .string: return .systemOrange
        case .comment: return .secondaryLabelColor
        case .number: return .systemPurple
        }
    }
}

/// Line numbers beside a FileSourceTextView, drawn only for visible lines.
final class FileLineNumberRuler: NSRulerView {
    private weak var textView: NSTextView?
    /// UTF-16 offset where each line starts.
    private var lineStarts: [Int] = [0]

    init(textView: NSTextView, scrollView: NSScrollView) {
        self.textView = textView
        super.init(scrollView: scrollView, orientation: .verticalRuler)
        clientView = textView
        ruleThickness = 34
        scrollView.contentView.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(self, selector: #selector(contentScrolled), name: NSView.boundsDidChangeNotification, object: scrollView.contentView)
    }

    required init(coder: NSCoder) { fatalError("init(coder:) is not used") }

    @objc private func contentScrolled() { needsDisplay = true }

    /// `lineStarts` from `SourceLines.scan`, which breaks lines as NSTextView does.
    func update(lineStarts starts: [Int]) {
        lineStarts = starts.isEmpty ? [0] : starts
        ruleThickness = CGFloat(max(2, String(lineStarts.count).count)) * 7 + 16
        needsDisplay = true
    }

    override func drawHashMarksAndLabels(in rect: NSRect) {
        guard let textView, let layout = textView.layoutManager, let container = textView.textContainer else { return }
        NSColor.separatorColor.setFill()
        NSRect(x: bounds.maxX - 1, y: rect.minY, width: 1, height: rect.height).fill()
        let length = textView.textStorage?.length ?? 0
        let glyphs = layout.glyphRange(forBoundingRect: textView.visibleRect, in: container)
        let characters = layout.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
        var low = 0, high = lineStarts.count - 1
        while low < high {
            let middle = (low + high + 1) / 2
            if lineStarts[middle] <= characters.location { low = middle } else { high = middle - 1 }
        }
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular), .foregroundColor: NSColor.tertiaryLabelColor]
        let origin = textView.textContainerOrigin.y
        var line = low
        while line < lineStarts.count, lineStarts[line] <= NSMaxRange(characters) {
            let start = lineStarts[line]
            let fragment: NSRect
            if start >= length {
                fragment = layout.extraLineFragmentRect
                if fragment.height == 0 { break }
            } else {
                fragment = layout.lineFragmentRect(forGlyphAt: layout.glyphIndexForCharacter(at: start), effectiveRange: nil)
            }
            let y = convert(NSPoint(x: 0, y: fragment.minY + origin), from: textView).y
            let label = "\(line + 1)" as NSString
            let size = label.size(withAttributes: attributes)
            label.draw(at: NSPoint(x: ruleThickness - size.width - 8, y: y + (fragment.height - size.height) / 2), withAttributes: attributes)
            line += 1
        }
    }
}
