import AppKit
import ImageIO
import MightyCore
import SwiftUI

/// What the files pane knows about the selected file.
struct FilePaneFileInfo: Equatable, Sendable {
    let name: String
    let relativePath: String
    let url: URL
    let size: Int64
    let modified: Date?
}

/// Text ready for the source view. The attributed string and the line starts
/// are built off the main thread, so showing a 1 MB file only swaps storage.
struct FilePaneText: @unchecked Sendable {
    let text: String
    let attributed: NSAttributedString
    let lineStarts: [Int]
    /// A line is longer than `SourceLines.wrapThreshold`: the view wraps.
    let wraps: Bool
    let truncated: Bool
    /// The text is longer than the highlighter scans.
    let highlightCapped: Bool
    let encoding: TextEncoding
}

/// A decoded image. A vector image (svg, pdf) is shown as is; a bitmap is
/// shown fitted from a thumbnail, and at 1:1 or zoom from its full-size
/// decode, made only when asked for and only under the pixel cap.
struct FilePaneImage: @unchecked Sendable {
    let display: NSImage
    var full: NSImage?
    /// Pixels (orientation applied) for a bitmap; points for a vector image.
    let size: CGSize
    let isVector: Bool
    let fullSizeAllowed: Bool
    /// The file's bytes, kept to decode the full size later.
    let data: Data
}

enum FilePanePreview {
    case none
    case loading(FilePaneFileInfo)
    case source(FilePaneFileInfo, FilePaneText)
    /// `renderable` is false above the Markdown renderer's limit; the source is shown instead.
    case markdown(FilePaneFileInfo, FilePaneText, renderable: Bool)
    case image(FilePaneFileInfo, FilePaneImage)
    /// Binary, unknown, not a regular file or too large: shown with its details and a Finder button.
    case unsupported(FilePaneFileInfo, reason: String?)
    case failed(String)

    var info: FilePaneFileInfo? {
        switch self {
        case .none, .failed: return nil
        case .loading(let info), .source(let info, _), .markdown(let info, _, _), .image(let info, _), .unsupported(let info, _): return info
        }
    }
}

/// The read-only tree and preview of one workspace. The store keeps one per
/// workspace for the app's lifetime, so closing and reopening the pane keeps
/// the opened folders and the selection. Nothing here writes to disk.
@MainActor
final class FilePaneModel: ObservableObject {
    struct Row: Identifiable, Equatable {
        let entry: WorkspaceFileEntry
        let depth: Int
        let isExpanded: Bool
        /// Under an opened folder: its error, "empty" or "truncated".
        let caption: String?
        var id: String { entry.relativePath }
    }

    static let maximumFilterResults = 2_000

    let workspaceId: String
    let root: URL
    @Published private(set) var children: [String: [WorkspaceFileEntry]] = [:]
    @Published private(set) var truncated = Set<String>()
    @Published private(set) var folderErrors: [String: String] = [:]
    @Published private(set) var expanded = Set<String>()
    /// The visible tree, rebuilt only when a listing, the opened folders or the filter change.
    @Published private(set) var rows: [Row] = []
    /// The filter found more than `maximumFilterResults` names.
    @Published private(set) var filterHitCap = false
    /// The tree's cursor: a folder or a file.
    @Published private(set) var selectedPath: String?
    @Published private(set) var preview: FilePanePreview = .none
    @Published var filter = "" { didSet { if filter != oldValue { rebuildRows() } } }
    @Published var showsMarkdownSource = false
    /// nil fits the image to the pane; otherwise the scale of its points.
    @Published private(set) var imageZoom: CGFloat?
    private var loads: [String: Task<Void, Never>] = [:]
    private var previewTask: Task<Void, Never>?
    private var fullImageTask: Task<Void, Never>?
    /// Bumped for every preview request; a result for an older one is dropped.
    private var previewRequest = 0
    /// The file last asked for, shown again when a closed pane reopens.
    private var previewTarget: (path: String, name: String)?

    init(workspaceId: String, root: URL) {
        self.workspaceId = workspaceId
        self.root = root
    }

    /// Loads the root the first time the pane is shown, and the selected file
    /// again when the pane was closed.
    func start() {
        if children[""] == nil, loads[""] == nil { load("") }
        if case .none = preview, let target = previewTarget { showPreview(target.path, name: target.name, debounce: false) }
    }

    /// The pane closed: drop the preview (a big text or image) but keep the
    /// opened folders and the selection for the next open.
    func releasePreview() {
        previewTask?.cancel(); previewTask = nil
        fullImageTask?.cancel(); fullImageTask = nil
        previewRequest &+= 1
        preview = .none
    }

    var isFiltering: Bool { !filterNeedle.isEmpty }
    private var filterNeedle: String { filter.trimmingCharacters(in: .whitespacesAndNewlines) }

    private func rebuildRows() {
        let needle = filterNeedle
        if !needle.isEmpty {
            // Only the root and opened folders are searched (each re-read on
            // refresh and on opening); the filter never walks the disk.
            var found: [Row] = [], more = false
            search: for path in [""] + expanded.sorted() {
                for entry in children[path] ?? [] where entry.name.localizedCaseInsensitiveContains(needle) {
                    if found.count == Self.maximumFilterResults { more = true; break search }
                    found.append(Row(entry: entry, depth: 0, isExpanded: false, caption: nil))
                }
            }
            filterHitCap = more
            rows = found
            return
        }
        filterHitCap = false
        var result: [Row] = []
        func visit(_ path: String, depth: Int) {
            for entry in children[path] ?? [] {
                let open = entry.isDirectory && expanded.contains(entry.relativePath)
                result.append(Row(entry: entry, depth: depth, isExpanded: open, caption: open ? caption(entry.relativePath) : nil))
                if open, depth < 64 { visit(entry.relativePath, depth: depth + 1) }
            }
        }
        visit("", depth: 0)
        rows = result
    }

    private func caption(_ path: String) -> String? {
        if let error = folderErrors[path] { return error }
        if children[path]?.isEmpty == true { return L("files.tree.empty") }
        if truncated.contains(path) { return L("files.tree.truncated", ["count": "\(WorkspaceFiles.maximumEntriesPerFolder)"]) }
        return nil
    }

    func toggle(_ entry: WorkspaceFileEntry) {
        guard entry.isDirectory else { return }
        if expanded.contains(entry.relativePath) { expanded.remove(entry.relativePath); rebuildRows() }
        else { expand(entry.relativePath) }
    }

    /// Opens a folder: a listing read earlier shows at once and is re-read.
    private func expand(_ path: String) {
        expanded.insert(path)
        load(path)
        rebuildRows()
    }

    /// Re-reads the root and every opened folder, then the selected file.
    /// Listings of closed folders are dropped so they are read fresh when opened.
    func refresh() {
        let kept = Set([""]).union(expanded)
        children = children.filter { kept.contains($0.key) }
        truncated = truncated.filter { kept.contains($0) }
        folderErrors = folderErrors.filter { kept.contains($0.key) }
        rebuildRows()
        for path in kept.sorted() { load(path) }
        if let target = previewTarget { showPreview(target.path, name: target.name, debounce: false) }
    }

    private func load(_ path: String) {
        let root = root
        loads[path]?.cancel()
        loads[path] = Task { [weak self] in
            let result = await Task.detached(priority: .userInitiated) { () -> Result<WorkspaceDirectoryListing, WorkspaceFileError> in
                do { return .success(try WorkspaceFiles.list(path, root: root)) }
                catch let error as WorkspaceFileError { return .failure(error) }
                catch { return .failure(.unreadable(error.localizedDescription)) }
            }.value
            guard let self, !Task.isCancelled else { return }
            self.loads[path] = nil
            switch result {
            case .success(let listing):
                self.children[path] = listing.entries
                self.folderErrors[path] = nil
                if listing.truncated { self.truncated.insert(path) } else { self.truncated.remove(path) }
            case .failure(let error):
                self.children[path] = []
                self.truncated.remove(path)
                self.folderErrors[path] = error == .outsideRoot ? L("files.preview.missing") : L("files.tree.unreadable")
                if error == .outsideRoot, !path.isEmpty { self.expanded.remove(path) }
            }
            self.rebuildRows()
        }
    }

    // MARK: Selection and keyboard

    func select(_ entry: WorkspaceFileEntry) {
        selectedPath = entry.relativePath
        if !entry.isDirectory { showPreview(entry.relativePath, name: entry.name) }
    }

    func moveSelection(by offset: Int) {
        guard !rows.isEmpty else { return }
        let current = selectedPath.flatMap { path in rows.firstIndex { $0.id == path } }
        let next = current.map { min(max(0, $0 + offset), rows.count - 1) } ?? (offset < 0 ? rows.count - 1 : 0)
        select(rows[next].entry)
    }

    /// Left: close an open folder, otherwise go to the parent folder. Does
    /// nothing while filtering, where folders are not shown open.
    func collapseOrParent() {
        guard !isFiltering, let path = selectedPath else { return }
        if expanded.contains(path) { expanded.remove(path); rebuildRows(); return }
        guard let slash = path.lastIndex(of: "/") else { return }
        let parent = String(path[..<slash])
        if let entry = rows.first(where: { $0.id == parent })?.entry { select(entry) }
    }

    /// Right: open a folder, or step into an open one. While filtering it
    /// clears the filter and reveals the folder in the tree.
    func expandOrChild() {
        guard let path = selectedPath, let entry = rows.first(where: { $0.id == path })?.entry, entry.isDirectory else { return }
        if isFiltering { reveal(entry); return }
        if !expanded.contains(path) { expand(path); return }
        if let first = children[path]?.first { select(first) }
    }

    /// Return: toggle a folder (while filtering: clear the filter and reveal
    /// it open); a file is already previewed on selection.
    func activateSelection() {
        guard let path = selectedPath, let entry = rows.first(where: { $0.id == path })?.entry else { return }
        if entry.isDirectory { if isFiltering { reveal(entry) } else { toggle(entry) } }
        else { showPreview(entry.relativePath, name: entry.name, debounce: false) }
    }

    /// Clears the filter and opens the folder with every folder above it.
    private func reveal(_ entry: WorkspaceFileEntry) {
        let components = entry.relativePath.split(separator: "/").map(String.init)
        for count in 1...components.count { expanded.insert(components.prefix(count).joined(separator: "/")) }
        for count in 1...components.count { load(components.prefix(count).joined(separator: "/")) }
        selectedPath = entry.relativePath
        filter = ""
    }

    // MARK: Image zoom

    /// Sets the zoom (nil fits). A bitmap is decoded at full size the first
    /// time it is shown at 1:1 or zoomed, unless it is over the pixel cap.
    func setImageZoom(_ zoom: CGFloat?) {
        guard case .image(let info, let image) = preview else { return }
        if zoom != nil, !image.isVector, !image.fullSizeAllowed { return }
        imageZoom = zoom
        guard zoom != nil, !image.isVector, image.full == nil, fullImageTask == nil else { return }
        let request = previewRequest, data = image.data, longest = Int(max(image.size.width, image.size.height))
        fullImageTask = Task.detached(priority: .userInitiated) { [weak self] in
            let full = CGImageSourceCreateWithData(data as CFData, nil).flatMap { Self.thumbnail($0, maximumPixels: longest) }
            guard !Task.isCancelled else { return }
            await self?.applyFullImage(full, info: info, request: request)
        }
    }

    private func applyFullImage(_ full: NSImage?, info: FilePaneFileInfo, request: Int) {
        guard request == previewRequest else { return }
        fullImageTask = nil
        guard let full, case .image(let current, var image) = preview, current == info else { return }
        image.full = full
        preview = .image(info, image)
    }

    // MARK: Preview

    private enum Loaded: @unchecked Sendable {
        case source(FilePaneFileInfo, FilePaneText)
        case markdown(FilePaneFileInfo, FilePaneText, renderable: Bool)
        case image(FilePaneFileInfo, FilePaneImage)
        case unsupported(FilePaneFileInfo)
        case tooLargeImage(FilePaneFileInfo)
        case missing
        case failed
        case cancelled
    }

    /// Shows the file. The read runs off the main thread after a short pause
    /// (so arrowing through the tree does not read every file) and stops
    /// between its steps once a newer request replaces it.
    func showPreview(_ relativePath: String, name: String, debounce: Bool = true) {
        previewTask?.cancel()
        fullImageTask?.cancel(); fullImageTask = nil
        previewRequest &+= 1
        let request = previewRequest, root = root
        previewTarget = (relativePath, name)
        imageZoom = nil
        preview = .loading(FilePaneFileInfo(name: name, relativePath: relativePath, url: root.appendingPathComponent(relativePath), size: 0, modified: nil))
        previewTask = Task.detached(priority: .userInitiated) { [weak self] in
            if debounce { try? await Task.sleep(for: .milliseconds(100)) }
            guard !Task.isCancelled else { return }
            let loaded = Self.load(relativePath, name: name, root: root)
            guard !Task.isCancelled else { return }
            await self?.apply(loaded, request: request)
        }
    }

    private func apply(_ loaded: Loaded, request: Int) {
        guard request == previewRequest else { return }
        previewTask = nil
        switch loaded {
        case .source(let info, let text): preview = .source(info, text)
        case .markdown(let info, let text, let renderable): preview = .markdown(info, text, renderable: renderable)
        case .image(let info, let image): preview = .image(info, image)
        case .unsupported(let info): preview = .unsupported(info, reason: nil)
        case .tooLargeImage(let info): preview = .unsupported(info, reason: L("files.preview.tooLargeImage"))
        case .missing: preview = .failed(L("files.preview.missing"))
        case .failed: preview = .failed(L("files.preview.failed"))
        case .cancelled: break
        }
    }

    private nonisolated static func load(_ relativePath: String, name: String, root: URL) -> Loaded {
        let file: WorkspaceOpenFile
        do { file = try WorkspaceFiles.openFile(relativePath, root: root) }
        catch WorkspaceFileOpenError.notRegularFile {
            guard let url = WorkspaceFiles.resolve(relativePath, root: root) else { return .missing }
            let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
            return .unsupported(FilePaneFileInfo(name: name, relativePath: relativePath, url: url, size: Int64(values?.fileSize ?? 0), modified: values?.contentModificationDate))
        }
        catch WorkspaceFileOpenError.missing { return .missing }
        catch { return .failed }
        let info = FilePaneFileInfo(name: name, relativePath: relativePath, url: file.url, size: file.size, modified: file.modified)
        guard let head = try? FilePreviewClassifier.readHead(file.handle) else { return .failed }
        guard !Task.isCancelled else { return .cancelled }
        let kind = FilePreviewClassifier.classify(name: name, head: head)
        switch kind {
        case .image:
            guard info.size <= FilePreviewClassifier.maximumImageBytes else { return .tooLargeImage(info) }
            guard (try? file.handle.seek(toOffset: 0)) != nil,
                  let data = try? file.handle.read(upToCount: FilePreviewClassifier.maximumImageBytes + 1) else { return .failed }
            guard data.count <= FilePreviewClassifier.maximumImageBytes else { return .tooLargeImage(info) }
            guard !Task.isCancelled else { return .cancelled }
            return decodeImage(data, name: name).map { .image(info, $0) } ?? .unsupported(info)
        case .markdown, .source:
            guard let read = try? FilePreviewClassifier.readText(file.handle) else { return .failed }
            guard !Task.isCancelled else { return .cancelled }
            if kind == .markdown {
                let renderable = read.text.utf8.count <= AgentMarkdownDocument.maximumRenderBytes
                // Parse here so the renderer finds the document in its cache.
                if renderable { _ = AgentMarkdownDocument.parse(read.text) }
                guard !Task.isCancelled else { return .cancelled }
                return .markdown(info, text(read, tokens: [], highlightCapped: false), renderable: renderable)
            }
            var tokens: [SourceToken] = [], capped = false
            if case .source(let language) = kind, language != .plain {
                tokens = SourceHighlighter.tokens(read.text, language: language)
                capped = read.text.utf16.count > SourceHighlighter.maximumUnits
            }
            guard !Task.isCancelled else { return .cancelled }
            return .source(info, text(read, tokens: tokens, highlightCapped: capped))
        case .unsupported:
            return .unsupported(info)
        }
    }

    private nonisolated static func text(_ read: (text: String, truncated: Bool, encoding: TextEncoding), tokens: [SourceToken], highlightCapped: Bool) -> FilePaneText {
        let lines = SourceLines.scan(read.text)
        return FilePaneText(text: read.text, attributed: FileSourceTextView.attributed(read.text, tokens: tokens), lineStarts: lines.starts,
                            wraps: lines.longest > SourceLines.wrapThreshold, truncated: read.truncated, highlightCapped: highlightCapped, encoding: read.encoding)
    }

    private nonisolated static func decodeImage(_ data: Data, name: String) -> FilePaneImage? {
        if ["svg", "pdf"].contains(FilePreviewClassifier.fileExtension(name)) {
            guard let image = NSImage(data: data), image.size.width > 0, image.size.height > 0 else { return nil }
            return FilePaneImage(display: image, full: image, size: image.size, isVector: true, fullSizeAllowed: true, data: Data())
        }
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
              let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue, width > 0, height > 0 else { return nil }
        let orientation = (properties[kCGImagePropertyOrientation] as? NSNumber)?.intValue ?? 1
        let longest = max(width, height), small = longest <= FilePreviewClassifier.maximumFitPixels
        guard let display = thumbnail(source, maximumPixels: min(longest, FilePreviewClassifier.maximumFitPixels)) else { return nil }
        // A small bitmap's thumbnail is already its full size; only a large one keeps its bytes.
        return FilePaneImage(display: display, full: small ? display : nil,
                             size: orientation >= 5 ? CGSize(width: height, height: width) : CGSize(width: width, height: height),
                             isVector: false, fullSizeAllowed: width * height <= FilePreviewClassifier.maximumFullPixels, data: small ? Data() : data)
    }

    /// The first frame, orientation applied, at most `maximumPixels` on its long side.
    private nonisolated static func thumbnail(_ source: CGImageSource, maximumPixels: Int) -> NSImage? {
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: maximumPixels,
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
        return NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
    }
}
