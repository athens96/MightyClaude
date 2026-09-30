import CoreGraphics
import Darwin
import Foundation
import ImageIO

/// One row of a phone's folder listing (`GET /m1/workspaces/{id}/files`).
public struct MobileFileEntry: Codable, Sendable, Equatable {
    public var name: String
    public var relativePath: String
    /// `folder`, `file`, `symlink-folder` or `symlink-file`.
    public var kind: String
    /// Bytes, files only.
    public var size: Int64?
    /// ISO-8601.
    public var modified: String?
    /// A heavy or generated folder the Mac pane dims (`WorkspaceFiles.noiseFolders`).
    public var noise: Bool
}

public struct MobileFileListing: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var workspaceId: String
    public var path: String
    public var entries: [MobileFileEntry]
    /// More entries existed than the phone is sent.
    public var truncated: Bool
}

/// One file's preview (`GET /m1/workspaces/{id}/file`). Which optional fields
/// are present follows `type`: text for `source`/`markdown`, pixels for
/// `image`, a `reason` for `unsupported`.
public struct MobileFilePreview: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var workspaceId: String
    public var path: String
    public var name: String
    public var size: Int64
    public var modified: String?
    /// `source`, `markdown`, `image` or `unsupported`.
    public var type: String
    /// `SourceLanguage` raw value; `source` only.
    public var language: String?
    /// `TextEncoding.displayName`.
    public var encoding: String?
    public var text: String?
    /// Only the start of the file is in `text`.
    public var truncated: Bool?
    public var lineCount: Int?
    /// `image/jpeg` or `image/png`: the thumbnail's own format.
    public var mime: String?
    /// The image's size: pixels (orientation applied), points for svg and pdf.
    public var width: Int?
    public var height: Int?
    public var thumbnailWidth: Int?
    public var thumbnailHeight: Int?
    /// The thumbnail, base64.
    public var data: String?
    /// `binary`, `notRegularFile`, `tooLarge` or `undecodable`.
    public var reason: String?

    init(workspaceId: String, path: String, name: String, size: Int64, modified: String?, type: String) {
        self.workspaceId = workspaceId; self.path = path; self.name = name; self.size = size; self.modified = modified; self.type = type
    }
}

/// Why a phone's file request was refused. `code` travels beside the message.
public struct MobileFileError: Error, Equatable, Sendable {
    public let status: Int
    public let code: String
    public let message: String

    static let workspaceNotFound = MobileFileError(status: 404, code: "workspaceNotFound", message: "워크스페이스를 찾을 수 없습니다.")
    static let notFound = MobileFileError(status: 404, code: "notFound", message: "파일이나 폴더를 찾을 수 없습니다.")
    static let outsideWorkspace = MobileFileError(status: 403, code: "outsideWorkspace", message: "워크스페이스 밖의 경로입니다.")
    static let notReadable = MobileFileError(status: 403, code: "notReadable", message: "읽을 수 없습니다.")
    static let notDirectory = MobileFileError(status: 400, code: "notDirectory", message: "폴더가 아닙니다.")
    static let badPath = MobileFileError(status: 400, code: "badPath", message: "경로가 올바르지 않습니다.")
    /// A newer preview from the same phone came in while this one waited for a slot.
    static let superseded = MobileFileError(status: 409, code: "superseded", message: "더 새 미리보기 요청이 있어 건너뛰었습니다.")
}

/// The phone's read-only view of a workspace folder: the Mac files pane's
/// listing and preview (`WorkspaceFiles`, `FilePreviewClassifier`) cut to
/// what fits one relay frame. Containment is exactly the pane's: every path
/// goes through `WorkspaceFiles.resolve` / `openFile`.
public enum MobileWorkspaceFiles {
    /// Entries sent for one folder.
    public static let maximumEntries = 2_000
    /// Text read from a file for the phone.
    public static let maximumTextBytes = 512 * 1_024
    /// The encoded reply body. The relay closes a socket on a frame over
    /// 1 MiB (docs/relay.md), so a reply stays well below it; text and
    /// entries are cut further when escaping would take them over.
    public static let maximumReplyBytes = 768 * 1_024
    /// A thumbnail's long side.
    public static let maximumThumbnailPixels = 2_048
    /// A thumbnail's encoded bytes (before base64).
    public static let maximumThumbnailBytes = 512 * 1_024
    /// Long sides tried, largest first, until the thumbnail fits its bytes.
    static let thumbnailLadder = [2_048, 1_600, 1_280, 1_024, 768, 512, 256]

    private static let timestamps = ISO8601DateFormatter()
    static func timestamp(_ date: Date?) -> String? { date.map { timestamps.string(from: $0) } }

    /// The relative path a phone asked for: "" (the root) only where allowed,
    /// no absolute path, no `..`, no empty or `.` component, no NUL.
    public static func validatedPath(_ raw: String?, allowRoot: Bool) throws -> String {
        let path = raw ?? ""
        if path.isEmpty {
            guard allowRoot else { throw MobileFileError.badPath }
            return ""
        }
        guard !path.contains("\0"), path.utf8.count <= WorkspaceFiles.maximumPathBytes else { throw MobileFileError.badPath }
        guard !path.hasPrefix("/") else { throw MobileFileError.outsideWorkspace }
        let components = path.split(separator: "/", omittingEmptySubsequences: false)
        guard !components.contains("..") else { throw MobileFileError.outsideWorkspace }
        guard !components.contains(where: { $0.isEmpty || $0 == "." }) else { throw MobileFileError.badPath }
        return path
    }

    /// A path `WorkspaceFiles.resolve` refused. Only an item directly inside a
    /// folder that itself resolves inside the root is looked at: it exists (a
    /// link out of the root, or a dangling one) or it does not. Under a folder
    /// that does not resolve inside the root — say, through a link that leaves
    /// it — every path is `notFound` alike, so the answer never tells whether
    /// something exists out there.
    static func refusal(_ relativePath: String, root: URL) -> MobileFileError {
        let slash = relativePath.lastIndex(of: "/")
        let parent = slash.map { String(relativePath[..<$0]) } ?? ""
        let name = slash.map { String(relativePath[relativePath.index(after: $0)...]) } ?? relativePath
        guard !name.isEmpty, let folder = WorkspaceFiles.resolve(parent, root: root) else { return .notFound }
        var status = stat()
        return lstat(folder.appendingPathComponent(name).path, &status) == 0 ? .outsideWorkspace : .notFound
    }

    // MARK: Listing

    public static func listing(workspaceId: String, path: String, root: URL) throws -> MobileFileListing {
        let listing: WorkspaceDirectoryListing
        do { listing = try WorkspaceFiles.list(path, root: root) }
        catch WorkspaceFileError.outsideRoot { throw refusal(path, root: root) }
        catch WorkspaceFileError.notDirectory { throw MobileFileError.notDirectory }
        catch { throw MobileFileError.notReadable }
        let entries = listing.entries.prefix(maximumEntries).map { entry(for: $0, root: root) }
        return MobileFileListing(workspaceId: workspaceId, path: path, entries: Array(entries),
                                 truncated: listing.truncated || listing.entries.count > maximumEntries)
    }

    /// Size and date come from `lstat`, never through a link: a link's are read
    /// at the real path `resolve` checked against the root.
    static func entry(for entry: WorkspaceFileEntry, root: URL) -> MobileFileEntry {
        let kind = (entry.isSymlink ? "symlink-" : "") + (entry.isDirectory ? "folder" : "file")
        let target = entry.isSymlink ? WorkspaceFiles.resolve(entry.relativePath, root: root)
                                     : WorkspaceFiles.realRoot(root).appendingPathComponent(entry.relativePath)
        var status = stat()
        guard let target, lstat(target.path, &status) == 0 else {
            return MobileFileEntry(name: entry.name, relativePath: entry.relativePath, kind: kind, noise: entry.isNoise)
        }
        return MobileFileEntry(name: entry.name, relativePath: entry.relativePath, kind: kind,
                               size: entry.isDirectory ? nil : Int64(status.st_size),
                               modified: timestamp(modified(status)), noise: entry.isNoise)
    }

    static func modified(_ status: stat) -> Date {
        Date(timeIntervalSince1970: TimeInterval(status.st_mtimespec.tv_sec) + TimeInterval(status.st_mtimespec.tv_nsec) / 1_000_000_000)
    }

    /// The listing as JSON under `maximumReplyBytes`, dropping entries from the
    /// end (and saying so) when long paths would take it over.
    public static func encoded(_ listing: MobileFileListing) -> Data {
        var listing = listing
        while true {
            let data = (try? JSONEncoder().encode(listing)) ?? Data("{}".utf8)
            guard data.count > maximumReplyBytes, !listing.entries.isEmpty else { return data }
            listing.entries.removeLast(max(1, listing.entries.count / 4))
            listing.truncated = true
        }
    }

    // MARK: Preview

    /// A preview read as far as it goes without decoding an image. An image's
    /// file is left open, unread, for `finish` to decode once the caller holds
    /// a preview slot. The file moves from one task to the next, never shared.
    public enum PreviewStage: @unchecked Sendable {
        case done(MobileFilePreview)
        case image(MobileFilePreview, WorkspaceOpenFile)
    }

    public static func preview(workspaceId: String, path: String, root: URL, svg: SVGRasterizer? = nil) throws -> MobileFilePreview {
        switch try prepare(workspaceId: workspaceId, path: path, root: root) {
        case .done(let preview): return preview
        case .image(let preview, let file): return try finish(preview, file: file, svg: svg, isCancelled: { false }) ?? preview
        }
    }

    /// Opens and classifies the file; text is read here, an image is not.
    public static func prepare(workspaceId: String, path: String, root: URL) throws -> PreviewStage {
        let name = String(path.split(separator: "/").last ?? "")
        let file: WorkspaceOpenFile
        do { file = try WorkspaceFiles.openFile(path, root: root) }
        catch WorkspaceFileOpenError.missing { throw refusal(path, root: root) }
        catch WorkspaceFileOpenError.notRegularFile {
            // A folder, FIFO, socket or device: its details only, as the Mac shows it.
            guard let url = WorkspaceFiles.resolve(path, root: root) else { throw refusal(path, root: root) }
            var status = stat()
            let known = lstat(url.path, &status) == 0
            var preview = MobileFilePreview(workspaceId: workspaceId, path: path, name: name, size: known ? Int64(status.st_size) : 0,
                                            modified: known ? timestamp(modified(status)) : nil, type: "unsupported")
            preview.reason = "notRegularFile"
            return .done(preview)
        }
        catch { throw MobileFileError.notReadable }

        var preview = MobileFilePreview(workspaceId: workspaceId, path: path, name: name, size: file.size, modified: timestamp(file.modified), type: "unsupported")
        guard let head = try? FilePreviewClassifier.readHead(file.handle) else { throw MobileFileError.notReadable }
        let kind = FilePreviewClassifier.classify(name: name, head: head)
        switch kind {
        case .image:
            guard file.size <= FilePreviewClassifier.maximumImageBytes else { preview.reason = "tooLarge"; return .done(preview) }
            return .image(preview, file)
        case .markdown, .source:
            guard let read = try? FilePreviewClassifier.readText(file.handle, maximumBytes: maximumTextBytes) else { throw MobileFileError.notReadable }
            preview.type = kind == .markdown ? "markdown" : "source"
            if case .source(let language) = kind { preview.language = language.rawValue }
            preview.encoding = read.encoding.displayName
            preview.text = read.text
            preview.truncated = read.truncated
            preview.lineCount = SourceLines.scan(read.text).starts.count
        case .unsupported:
            preview.reason = "binary"
        }
        return .done(preview)
    }

    /// Reads and decodes the image `prepare` left open. nil once `isCancelled`
    /// answers true, which is asked between the read and every step of the
    /// thumbnail ladder.
    public static func finish(_ preview: MobileFilePreview, file: WorkspaceOpenFile, svg: SVGRasterizer?, isCancelled: () -> Bool) throws -> MobileFilePreview? {
        var preview = preview
        guard (try? file.handle.seek(toOffset: 0)) != nil,
              let data = try? file.handle.read(upToCount: FilePreviewClassifier.maximumImageBytes + 1) else { throw MobileFileError.notReadable }
        guard data.count <= FilePreviewClassifier.maximumImageBytes else { preview.reason = "tooLarge"; return preview }
        guard !isCancelled() else { return nil }
        switch thumbnail(data, name: preview.name, svg: svg, isCancelled: isCancelled) {
        case .cancelled: return nil
        case .tooLarge: preview.reason = "tooLarge"
        case .undecodable: preview.reason = "undecodable"
        case .image(let image):
            preview.type = "image"
            preview.mime = image.mime; preview.width = image.width; preview.height = image.height
            preview.thumbnailWidth = image.thumbnailWidth; preview.thumbnailHeight = image.thumbnailHeight
            preview.data = image.data.base64EncodedString()
        }
        return preview
    }

    /// The preview as JSON under `maximumReplyBytes`: text that escaping would
    /// take over is cut by a quarter at a time (and marked truncated).
    public static func encoded(_ preview: MobileFilePreview) -> Data {
        var preview = preview
        while true {
            let data = (try? JSONEncoder().encode(preview)) ?? Data("{}".utf8)
            guard data.count > maximumReplyBytes, let text = preview.text, !text.isEmpty else { return data }
            let kept = String(text.prefix(text.count * 3 / 4))
            preview.text = kept
            preview.truncated = true
            preview.lineCount = SourceLines.scan(kept).starts.count
        }
    }

    // MARK: Thumbnails

    struct Thumbnail: Equatable {
        let mime: String
        let width: Int
        let height: Int
        let thumbnailWidth: Int
        let thumbnailHeight: Int
        let data: Data
    }

    /// Draws an svg file into a bitmap with its long side at most the given
    /// pixels. AppKit does this, and MightyCore stays free of AppKit, so the
    /// app hands the drawing in (`MobileHostDelegate.mobileSVGRasterizer`);
    /// without it an svg is `undecodable`. Returns the image and its size in points.
    public typealias SVGRasterizer = @Sendable (_ data: Data, _ maximumPixels: Int) -> (image: CGImage, points: CGSize)?

    enum Decoded: Equatable {
        case image(Thumbnail)
        /// More pixels than `FilePreviewClassifier.maximumFullPixels`.
        case tooLarge
        /// Not an image ImageIO, CoreGraphics or the app can draw, an svg that
        /// would load something besides itself, or a size past any sense.
        case undecodable
        case cancelled
    }

    /// A JPEG or PNG of at most `maximumThumbnailPixels` on its long side and
    /// `maximumThumbnailBytes`, stepping down the ladder until it fits. svg is
    /// drawn to a bitmap only when it references nothing outside itself; pdf
    /// shows its first page on white. A bitmap over the pixel budget is never
    /// decoded. Sizes are checked before any becomes an `Int`.
    static func thumbnail(_ data: Data, name: String, svg: SVGRasterizer?, isCancelled: () -> Bool = { false },
                          maximumPixels: Int = FilePreviewClassifier.maximumFullPixels) -> Decoded {
        let ext = FilePreviewClassifier.fileExtension(name)
        let base: CGImage, size: (width: Int, height: Int)
        switch ext {
        case "svg":
            guard let svg, !FilePreviewClassifier.svgLoadsExternalContent(data) else { return .undecodable }
            guard !isCancelled() else { return .cancelled }
            guard let drawn = svg(data, maximumThumbnailPixels),
                  FilePreviewClassifier.isDrawable(width: drawn.points.width, height: drawn.points.height) else { return .undecodable }
            base = drawn.image; size = (Int(drawn.points.width.rounded()), Int(drawn.points.height.rounded()))
        case "pdf":
            guard let drawn = firstPage(data) else { return .undecodable }
            base = drawn.image; size = drawn.points
        default:
            guard let source = CGImageSourceCreateWithData(data as CFData, nil),
                  let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                  let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
                  let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue, width > 0, height > 0 else { return .undecodable }
            guard let pixels = FilePreviewClassifier.pixelCount(width: width, height: height),
                  pixels <= maximumPixels else { return .tooLarge }
            let orientation = (properties[kCGImagePropertyOrientation] as? NSNumber)?.intValue ?? 1
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
                kCGImageSourceThumbnailMaxPixelSize: min(max(width, height), maximumThumbnailPixels),
            ]
            guard let decoded = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return .undecodable }
            base = decoded; size = orientation >= 5 ? (height, width) : (width, height)
        }
        let transparent = ![.none, .noneSkipFirst, .noneSkipLast].contains(base.alphaInfo)
        let longest = max(base.width, base.height)
        for step in [longest] + thumbnailLadder.filter({ $0 < longest }) {
            guard !isCancelled() else { return .cancelled }
            if transparent, let image = scaled(base, longest: step, opaque: false), let png = encode(image, type: "public.png"), png.count <= maximumThumbnailBytes {
                return .image(Thumbnail(mime: "image/png", width: size.width, height: size.height, thumbnailWidth: image.width, thumbnailHeight: image.height, data: png))
            }
            if let image = scaled(base, longest: step, opaque: true), let jpeg = encode(image, type: "public.jpeg"), jpeg.count <= maximumThumbnailBytes {
                return .image(Thumbnail(mime: "image/jpeg", width: size.width, height: size.height, thumbnailWidth: image.width, thumbnailHeight: image.height, data: jpeg))
            }
        }
        return .undecodable
    }

    /// A pdf's first page on white, at twice its point size (sharp on a phone)
    /// but no more than `maximumThumbnailPixels` on its long side. A page box
    /// under a point or past `maximumVectorPoints` is not drawn.
    static func firstPage(_ data: Data) -> (image: CGImage, points: (width: Int, height: Int))? {
        guard let provider = CGDataProvider(data: data as CFData), let document = CGPDFDocument(provider), let page = document.page(at: 1) else { return nil }
        var box = page.getBoxRect(.mediaBox)
        guard FilePreviewClassifier.isDrawable(width: box.width, height: box.height), box.width >= 1, box.height >= 1 else { return nil }
        if page.rotationAngle % 180 != 0 { box = CGRect(x: 0, y: 0, width: box.height, height: box.width) }
        let scale = min(2, Double(maximumThumbnailPixels) / Double(max(box.width, box.height)))
        let width = max(1, Int((box.width * scale).rounded())), height = max(1, Int((box.height * scale).rounded()))
        guard let context = context(width: width, height: height, opaque: true) else { return nil }
        context.scaleBy(x: scale, y: scale)
        context.concatenate(page.getDrawingTransform(.mediaBox, rect: CGRect(origin: .zero, size: box.size), rotate: 0, preserveAspectRatio: true))
        context.drawPDFPage(page)
        guard let image = context.makeImage() else { return nil }
        return (image, (Int(box.width.rounded()), Int(box.height.rounded())))
    }

    /// An sRGB bitmap context `width` × `height`, white first when `opaque`.
    public static func context(width: Int, height: Int, opaque: Bool) -> CGContext? {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                      bitmapInfo: opaque ? CGImageAlphaInfo.noneSkipLast.rawValue : CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        if opaque {
            context.setFillColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        }
        context.interpolationQuality = .high
        return context
    }

    private static func fitted(width: Int, height: Int, longest: Int) -> (Int, Int) {
        let scale = min(1, Double(longest) / Double(max(width, height)))
        return (max(1, Int((Double(width) * scale).rounded())), max(1, Int((Double(height) * scale).rounded())))
    }

    static func scaled(_ image: CGImage, longest: Int, opaque: Bool) -> CGImage? {
        let (width, height) = fitted(width: image.width, height: image.height, longest: longest)
        guard let context = context(width: width, height: height, opaque: opaque) else { return nil }
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }

    private static func encode(_ image: CGImage, type: String) -> Data? {
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, type as CFString, 1, nil) else { return nil }
        let options: [CFString: Any] = type == "public.jpeg" ? [kCGImageDestinationLossyCompressionQuality: 0.8] : [:]
        CGImageDestinationAddImage(destination, image, options as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }
}
