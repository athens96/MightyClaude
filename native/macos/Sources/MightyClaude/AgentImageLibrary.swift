import AppKit
import CryptoKit
import ImageIO
import MightyCore
import SwiftUI

/// A picture the transcript or the graph shows: one a tool returned (kept in
/// the image cache) or one a Markdown image names under the path rule.
enum AgentImageKey: Hashable {
    case stored(AgentImageRef)
    /// `stamp` is the file's size and modification time when the key was
    /// made, so a picture rewritten under the same name is decoded again.
    case file(URL, root: URL, stamp: String)
    case inline(mediaType: String, base64: String)

    init?(_ location: AgentImageLocation) {
        switch location {
        case .file(let url, let root): self = .file(url, root: root, stamp: Self.stamp(url))
        case .inline(let type, let base64): self = .inline(mediaType: type, base64: base64)
        case .remote, .refused: return nil
        }
    }

    static func stamp(_ url: URL) -> String {
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        let size = (attributes?[.size] as? NSNumber)?.int64Value ?? -1
        let modified = (attributes?[.modificationDate] as? Date)?.timeIntervalSinceReferenceDate ?? 0
        return "\(size):\(modified)"
    }

    /// Stable identity; an inline picture is named by its payload's hash.
    var id: String {
        switch self {
        case .stored(let ref): return "hash:" + ref.hash
        case .file(let url, _, let stamp): return "file:" + url.path + "#" + stamp
        case .inline(_, let base64): return "data:" + AgentImageSupport.sha256(Data(base64.utf8))
        }
    }
    static func == (lhs: Self, rhs: Self) -> Bool { lhs.id == rhs.id }
    func hash(into hasher: inout Hasher) { hasher.combine(id) }

    var label: String {
        switch self {
        case .stored(let ref): return ref.source
        case .file(let url, _, _): return url.lastPathComponent
        case .inline: return L("images.source.agent")
        }
    }
    /// Pixel size known before decoding, for a placeholder of the right shape.
    var knownSize: CGSize? {
        guard case .stored(let ref) = self, ref.width > 0, ref.height > 0 else { return nil }
        return CGSize(width: ref.width, height: ref.height)
    }
}

/// A decoded picture: fitted for display, with the file it can be opened,
/// copied or revealed from.
final class AgentImageThumbnail: @unchecked Sendable {
    let image: NSImage
    /// Pixels (orientation applied); a vector picture's points.
    let pixelSize: CGSize
    /// The bytes on disk with a picture extension: the cache file or the source file.
    let fileURL: URL?
    /// The file the agent named, when it still exists; Finder reveals this one.
    let originalURL: URL?
    init(image: NSImage, pixelSize: CGSize, fileURL: URL?, originalURL: URL?) {
        self.image = image; self.pixelSize = pixelSize; self.fileURL = fileURL; self.originalURL = originalURL
    }
}

enum AgentImageState {
    case loading
    case ready(AgentImageThumbnail)
    /// The cache no longer holds it, or its file is gone.
    case missing
}

/// At most `limit` pictures decode at once; the rest wait their turn.
actor AgentImageDecodeGate {
    private let limit: Int
    private var running = 0
    private var waiting: [CheckedContinuation<Void, Never>] = []
    init(limit: Int) { self.limit = limit }
    func acquire() async {
        guard running >= limit else { running += 1; return }
        await withCheckedContinuation { waiting.append($0) }
    }
    func release() { if waiting.isEmpty { running -= 1 } else { waiting.removeFirst().resume() } }
}

/// Thumbnails are decoded off the main thread, two at a time, at most
/// `AgentImageSupport.thumbnailPixels` on the long side, and kept in a
/// bounded memory cache. Views ask for a state when they draw; loads that
/// finish together post one `didLoad` (the transcript, with their ids under
/// `idsKey`) and bump `revision` once (SwiftUI galleries).
@MainActor
final class AgentImageLibrary: ObservableObject {
    static let shared = AgentImageLibrary()
    static let didLoad = Notification.Name("MightyAgentImageDidLoad")
    static let idsKey = "ids"
    static let costLimit = 192 * 1_048_576
    /// Set once by the app store; nil (tests, diagnostics) shows placeholders.
    var cache: AgentImageCache?
    @Published private(set) var revision = 0
    /// Thumbnails by key id; above the cost limit the least recently drawn go
    /// first. Asking for one marks it used, so pictures on screen are never
    /// the ones evicted and reloaded in a loop.
    private var thumbnails: [String: (thumbnail: AgentImageThumbnail, cost: Int, used: UInt64)] = [:]
    private var thumbnailCost = 0
    private var clock: UInt64 = 0
    /// Proportions of pictures decoded before, kept after their thumbnail is
    /// evicted so a placeholder keeps the picture's shape.
    private var sizes: [String: CGSize] = [:]
    private var missing = Set<String>()
    private var loading = Set<String>()
    private var finished = Set<String>()
    private var publishing = false
    private let decodes = AgentImageDecodeGate(limit: 2)

    /// The state to draw; starts a load when there is none yet.
    func state(_ key: AgentImageKey) -> AgentImageState {
        let id = key.id
        if var entry = thumbnails[id] {
            clock &+= 1; entry.used = clock; thumbnails[id] = entry
            return .ready(entry.thumbnail)
        }
        if missing.contains(id) { return .missing }
        load(key, id: id)
        return .loading
    }

    /// The shape to lay a picture out with, without loading anything.
    func layoutSize(_ key: AgentImageKey) -> CGSize? {
        let id = key.id
        if missing.contains(id) { return nil }
        return key.knownSize ?? sizes[id]
    }
    func isMissing(_ key: AgentImageKey) -> Bool { missing.contains(key.id) }

    /// A run ended: files it wrote may now be readable pictures.
    func forgetMissingFiles() { missing = missing.filter { !$0.hasPrefix("file:") } }

    private func load(_ key: AgentImageKey, id: String) {
        guard loading.insert(id).inserted else { return }
        let cache = cache, decodes = decodes
        Task.detached(priority: .userInitiated) {
            await decodes.acquire()
            let result = Self.decode(key, cache: cache, maximumPixels: AgentImageSupport.thumbnailPixels)
            await decodes.release()
            await AgentImageLibrary.shared.finish(id, result)
        }
    }

    private func finish(_ id: String, _ result: AgentImageThumbnail?) {
        loading.remove(id)
        if let result {
            let pixels = result.image.representations.first.map { $0.pixelsWide * $0.pixelsHigh } ?? Int(result.image.size.width * result.image.size.height)
            let cost = max(1, pixels * 4)
            if let old = thumbnails[id] { thumbnailCost -= old.cost }
            clock &+= 1
            thumbnails[id] = (result, cost, clock); thumbnailCost += cost
            sizes[id] = result.pixelSize.width > 0 ? result.pixelSize : result.image.size
            while thumbnailCost > Self.costLimit, thumbnails.count > 1,
                  let oldest = thumbnails.filter({ $0.key != id }).min(by: { $0.value.used < $1.value.used }) {
                thumbnailCost -= oldest.value.cost; thumbnails.removeValue(forKey: oldest.key)
            }
        } else { missing.insert(id) }
        finished.insert(id)
        guard !publishing else { return }
        publishing = true
        // Loads finishing close together redraw once.
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(50))
            AgentImageLibrary.shared.publish()
        }
    }

    private func publish() {
        publishing = false
        let ids = finished; finished.removeAll()
        guard !ids.isEmpty else { return }
        revision &+= 1
        NotificationCenter.default.post(name: Self.didLoad, object: nil, userInfo: [Self.idsKey: ids])
    }

    /// The picture at viewer size (long side up to `FilePreviewClassifier.maximumFitPixels`).
    func full(_ key: AgentImageKey) async -> AgentImageThumbnail? {
        let cache = cache, decodes = decodes
        return await Task.detached(priority: .userInitiated) {
            await decodes.acquire()
            let result = Self.decode(key, cache: cache, maximumPixels: FilePreviewClassifier.maximumFitPixels)
            await decodes.release()
            return result
        }.value
    }

    /// Reads the bytes for a key, re-validated (`AgentImageSupport.inspect`)
    /// before anything decodes them.
    nonisolated static func bytes(_ key: AgentImageKey, cache: AgentImageCache?) -> (data: Data, mediaType: String, fileURL: URL?, originalURL: URL?)? {
        switch key {
        case .stored(let ref):
            guard let cache, let data = cache.data(for: ref) else { return nil }
            let original = ref.path.flatMap { FileManager.default.fileExists(atPath: $0) ? URL(fileURLWithPath: $0) : nil }
            return (data, ref.mediaType, cache.url(for: ref), original)
        case .file(let url, let root, _):
            guard let data = try? AgentImagePaths.read(url, root: root), let type = AgentImageSupport.mediaType(forFileName: url.lastPathComponent) else { return nil }
            return (data, type, url, url)
        case .inline(let type, let base64):
            guard let data = try? AgentImageSupport.decodeBase64(base64) else { return nil }
            // Kept in the cache so it can be opened in Preview or revealed.
            let stored = cache.flatMap { try? $0.store(data, mediaType: type, source: L("images.source.agent")) }
            return (data, stored?.mediaType ?? type, stored.flatMap { cache?.url(for: $0) }, nil)
        }
    }

    nonisolated static func decode(_ key: AgentImageKey, cache: AgentImageCache?, maximumPixels: Int) -> AgentImageThumbnail? {
        guard let source = bytes(key, cache: cache),
              let inspected = try? AgentImageSupport.inspect(source.data, mediaType: source.mediaType) else { return nil }
        if inspected.mediaType == "image/svg+xml" {
            // Checked above: this svg references nothing outside its own bytes.
            // Drawn once, here, into a bitmap (at most twice its size and
            // `maximumPixels` on the long side); views never redraw the vector.
            guard let vector = NSImage(data: source.data), FilePreviewClassifier.isDrawable(width: vector.size.width, height: vector.size.height) else { return nil }
            let scale = min(2, CGFloat(maximumPixels) / max(vector.size.width, vector.size.height))
            let width = max(1, Int((vector.size.width * scale).rounded())), height = max(1, Int((vector.size.height * scale).rounded()))
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            vector.draw(in: NSRect(x: 0, y: 0, width: width, height: height), from: .zero, operation: .copy, fraction: 1)
            NSGraphicsContext.restoreGraphicsState()
            guard let raster = context.makeImage() else { return nil }
            return AgentImageThumbnail(image: NSImage(cgImage: raster, size: NSSize(width: width, height: height)), pixelSize: vector.size,
                                       fileURL: source.fileURL, originalURL: source.originalURL)
        }
        guard let imageSource = CGImageSourceCreateWithData(source.data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let image = CGImageSourceCreateThumbnailAtIndex(imageSource, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
                kCGImageSourceThumbnailMaxPixelSize: maximumPixels,
              ] as CFDictionary) else { return nil }
        return AgentImageThumbnail(image: NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height)),
                                   pixelSize: CGSize(width: inspected.width, height: inspected.height),
                                   fileURL: source.fileURL, originalURL: source.originalURL)
    }
}

/// Copy, reveal and Preview, shared by the transcript's menu, the graph's
/// gallery and the viewer.
@MainActor
enum AgentImageActions {
    static func open(_ key: AgentImageKey) { AgentImageViewer.open(key) }

    /// Copies a decoded picture as is (the viewer's is already full size).
    static func copy(_ thumbnail: AgentImageThumbnail) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([thumbnail.image])
    }

    /// Copies the picture at viewer size, decoded again through the same
    /// checks as its thumbnail rather than from whatever the path holds now.
    static func copy(_ key: AgentImageKey) {
        Task { @MainActor in
            if let full = await AgentImageLibrary.shared.full(key) { copy(full) }
        }
    }

    static func reveal(_ thumbnail: AgentImageThumbnail) {
        guard let url = thumbnail.originalURL ?? thumbnail.fileURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    static func openInPreview(_ thumbnail: AgentImageThumbnail) {
        guard let url = thumbnail.fileURL else { return }
        if let preview = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Preview") {
            NSWorkspace.shared.open([url], withApplicationAt: preview, configuration: NSWorkspace.OpenConfiguration())
        } else { NSWorkspace.shared.open(url) }
    }

    /// Native menu rows for a picture under the pointer. Each row keeps its
    /// own handler alive: a menu item's target is weak.
    static func menuItems(_ key: AgentImageKey) -> [NSMenuItem] {
        var items = [AgentImageMenuHandler.item(L("images.menu.open")) { open(key) }]
        guard case .ready(let thumbnail) = AgentImageLibrary.shared.state(key) else { return items }
        items.append(AgentImageMenuHandler.item(L("images.menu.copy")) { copy(key) })
        if thumbnail.originalURL != nil || thumbnail.fileURL != nil {
            items.append(AgentImageMenuHandler.item(L("images.menu.reveal")) { reveal(thumbnail) })
        }
        if thumbnail.fileURL != nil { items.append(AgentImageMenuHandler.item(L("images.menu.preview")) { openInPreview(thumbnail) }) }
        return items
    }
}

@MainActor
private final class AgentImageMenuHandler: NSObject {
    let action: @MainActor () -> Void
    init(_ action: @escaping @MainActor () -> Void) { self.action = action }
    @objc func run() { action() }
    static func item(_ title: String, _ action: @escaping @MainActor () -> Void) -> NSMenuItem {
        let handler = AgentImageMenuHandler(action)
        let item = NSMenuItem(title: title, action: #selector(run), keyEquivalent: "")
        item.target = handler
        item.representedObject = handler
        return item
    }
}

/// A window showing one picture fitted, with the same actions as the menu.
@MainActor
enum AgentImageViewer {
    private static var windows: [NSWindow] = []
    private static var observers: [ObjectIdentifier: NSObjectProtocol] = [:]

    static func open(_ key: AgentImageKey) {
        if let existing = windows.first(where: { $0.identifier?.rawValue == "agent-image-" + key.id }) {
            existing.makeKeyAndOrderFront(nil); return
        }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 880, height: 680), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.identifier = NSUserInterfaceItemIdentifier("agent-image-" + key.id)
        window.title = key.label
        window.contentView = NSHostingView(rootView: AgentImageViewerView(key: key))
        window.center()
        windows.append(window)
        let id = ObjectIdentifier(window)
        observers[id] = NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { _ in
            MainActor.assumeIsolated {
                windows.removeAll { ObjectIdentifier($0) == id }
                if let token = observers.removeValue(forKey: id) { NotificationCenter.default.removeObserver(token) }
            }
        }
        window.makeKeyAndOrderFront(nil)
    }
}

private struct AgentImageViewerView: View {
    let key: AgentImageKey
    @ViewState private var picture: AgentImageThumbnail?
    @ViewState private var finished = false

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                if let picture {
                    Text(L("files.image.pixels", ["width": "\(Int(picture.pixelSize.width))", "height": "\(Int(picture.pixelSize.height))"]))
                        .font(.system(size: 11)).foregroundStyle(.secondary).monospacedDigit()
                }
                Spacer()
                if let picture {
                    Button(L("images.menu.copy")) { AgentImageActions.copy(picture) }
                    if picture.originalURL != nil || picture.fileURL != nil { Button(L("images.menu.reveal")) { AgentImageActions.reveal(picture) } }
                    if picture.fileURL != nil { Button(L("images.menu.preview")) { AgentImageActions.openInPreview(picture) } }
                }
            }
            .controlSize(.small).padding(.horizontal, 12).padding(.vertical, 8)
            Divider()
            Group {
                if let picture {
                    Image(nsImage: picture.image).resizable().interpolation(.high).scaledToFit().padding(12)
                        .accessibilityLabel(key.label)
                } else {
                    Text(finished ? L("images.missing") : L("images.loading")).font(.system(size: 12)).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Palette.subtle)
        }
        .frame(minWidth: 360, minHeight: 280)
        .task {
            picture = await AgentImageLibrary.shared.full(key)
            finished = true
        }
    }
}
