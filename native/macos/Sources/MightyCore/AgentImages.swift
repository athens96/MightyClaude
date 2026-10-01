import CryptoKit
import Foundation
import ImageIO

/// A picture an agent showed, kept by reference. The bytes live in the app's
/// image cache under their SHA-256; transcripts, graph runs, saved state and
/// the phone carry only this small record, never the picture itself.
public struct AgentImageRef: Codable, Sendable, Equatable, Hashable {
    /// SHA-256 of the decoded bytes, lower-case hex.
    public var hash: String
    public var mediaType: String
    /// Pixels with orientation applied; 0 when unknown (an svg).
    public var width: Int
    public var height: Int
    /// Decoded size in bytes.
    public var bytes: Int
    /// Where the picture came from: the tool or item that returned it.
    public var source: String
    /// The file the picture was read from, when the engine named one.
    public var path: String?

    public init(hash: String, mediaType: String, width: Int, height: Int, bytes: Int, source: String, path: String? = nil) {
        self.hash = hash; self.mediaType = mediaType; self.width = width; self.height = height
        self.bytes = bytes; self.source = source; self.path = path
    }
}

public enum AgentImageError: Error, Equatable, Sendable {
    case empty, tooLarge, unsupportedType, undecodable, externalSVG, tooManyPixels, invalidEncoding
}

public enum AgentImageSupport {
    /// Larger decoded pictures are refused, never written.
    public static let maximumImageBytes = 20 * 1_048_576
    /// The cache on disk; least recently used pictures go first above it.
    public static let maximumCacheBytes = 200 * 1_048_576
    /// One run stores at most this many pictures; later ones are noted once.
    public static let maximumImagesPerRun = 64
    public static let maximumImagesPerEntry = 16
    /// Thumbnails are decoded at most this many pixels on the long side: a
    /// 480 pt transcript picture at 2x.
    public static let thumbnailPixels = 960
    /// Agent pictures with more pixels, or a longer side, are refused before
    /// anything decodes them (the file pane's own cap is far higher).
    public static let maximumPixels = 64_000_000
    public static let maximumSide = 8_000
    static let maximumSourceBytes = 240

    /// The picture formats kept, with the file extension each is stored under.
    public static let extensions: [String: String] = [
        "image/png": "png", "image/jpeg": "jpg", "image/gif": "gif", "image/webp": "webp", "image/heic": "heic",
        "image/heif": "heif", "image/tiff": "tiff", "image/bmp": "bmp", "image/svg+xml": "svg",
    ]
    static let typeIdentifiers: [String: String] = [
        "public.png": "image/png", "public.jpeg": "image/jpeg", "com.compuserve.gif": "image/gif", "org.webmproject.webp": "image/webp",
        "public.heic": "image/heic", "public.heif": "image/heif", "public.tiff": "image/tiff", "com.microsoft.bmp": "image/bmp",
    ]

    /// A recognised picture media type, or nil.
    public static func mediaType(_ value: String) -> String? {
        let lower = value.trimmingCharacters(in: .whitespaces).lowercased()
        let canonical = ["image/jpg": "image/jpeg", "image/pjpeg": "image/jpeg", "image/x-png": "image/png", "image/svg": "image/svg+xml"][lower] ?? lower
        return extensions[canonical] == nil ? nil : canonical
    }

    /// The media type a picture file name implies, or nil for anything else.
    public static func mediaType(forFileName name: String) -> String? {
        let ext = FilePreviewClassifier.fileExtension(name)
        if ext == "jpeg" { return "image/jpeg" }
        if ext == "tif" { return "image/tiff" }
        return extensions.first(where: { $0.value == ext })?.key
    }

    public static func sha256(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }

    /// What the bytes really are: a bitmap ImageIO can read within the pixel
    /// and side caps, or an svg document that references nothing outside
    /// itself. The declared type only decides between the two; a bitmap is
    /// stored as what it is. ImageIO itself reports no size for some
    /// oversized bitmaps; those are refused as undecodable before the caps
    /// are even consulted.
    public static func inspect(_ data: Data, mediaType declared: String, maximumPixels: Int = AgentImageSupport.maximumPixels, maximumSide: Int = AgentImageSupport.maximumSide) throws -> (mediaType: String, width: Int, height: Int) {
        guard !data.isEmpty else { throw AgentImageError.empty }
        guard data.count <= maximumImageBytes else { throw AgentImageError.tooLarge }
        if mediaType(declared) == "image/svg+xml" {
            guard svgDocument(data) else { throw AgentImageError.undecodable }
            guard !FilePreviewClassifier.svgLoadsExternalContent(data) else { throw AgentImageError.externalSVG }
            return ("image/svg+xml", 0, 0)
        }
        guard mediaType(declared) != nil else { throw AgentImageError.unsupportedType }
        guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetCount(source) > 0, let type = CGImageSourceGetType(source) as String? else { throw AgentImageError.undecodable }
        guard let actual = typeIdentifiers[type] else { throw AgentImageError.unsupportedType }
        guard let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
              let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue else { throw AgentImageError.undecodable }
        guard let pixels = FilePreviewClassifier.pixelCount(width: width, height: height) else { throw AgentImageError.undecodable }
        guard pixels <= maximumPixels, max(width, height) <= maximumSide else { throw AgentImageError.tooManyPixels }
        let orientation = (properties[kCGImagePropertyOrientation] as? NSNumber)?.intValue ?? 1
        return (actual, (5...8).contains(orientation) ? height : width, (5...8).contains(orientation) ? width : height)
    }

    /// Whether the text is an svg document: after an optional byte order mark,
    /// XML declaration, processing instructions, comments and doctype, the
    /// first element is `<svg`. Anything else declared as svg is refused.
    static func svgDocument(_ data: Data) -> Bool {
        guard let decoded = FilePreviewClassifier.decodeText(data.prefix(65_536), sample: true) else { return false }
        var rest = Substring(decoded.text.lowercased())
        while true {
            rest = rest.drop(while: { $0.isWhitespace || $0 == "\u{FEFF}" })
            if rest.hasPrefix("<?") {
                guard let end = rest.range(of: "?>") else { return false }
                rest = rest[end.upperBound...]
            } else if rest.hasPrefix("<!--") {
                guard let end = rest.range(of: "-->") else { return false }
                rest = rest[end.upperBound...]
            } else if rest.hasPrefix("<!doctype") {
                guard let end = rest.firstIndex(of: ">") else { return false }
                rest = rest[rest.index(after: end)...]
            } else { break }
        }
        guard rest.hasPrefix("<svg") else { return false }
        let next = rest.dropFirst(4).first
        return next.map { $0.isWhitespace || $0 == ">" || $0 == "/" } ?? false
    }

    /// A saved or received record, or nil when any field is out of shape.
    public static func normalized(_ ref: AgentImageRef) -> AgentImageRef? {
        guard ref.hash.utf8.count == 64, ref.hash.allSatisfy({ $0.isHexDigit && !$0.isUppercase }),
              let mediaType = mediaType(ref.mediaType), (0...1_000_000).contains(ref.width), (0...1_000_000).contains(ref.height),
              (1...maximumImageBytes).contains(ref.bytes) else { return nil }
        var result = ref
        result.mediaType = mediaType
        result.source = ActivitySupport.clean(ref.source, maximumBytes: maximumSourceBytes, singleLine: true)
        result.path = ref.path.flatMap { $0.hasPrefix("/") && $0.utf8.count <= WorkspaceFiles.maximumPathBytes && !$0.contains("\0") ? $0 : nil }
        return result
    }

    public static func normalized(_ refs: [AgentImageRef]?) -> [AgentImageRef]? {
        guard let refs else { return nil }
        let result = Array(refs.prefix(maximumImagesPerEntry).compactMap(normalized))
        return result.isEmpty ? nil : result
    }

    /// Bytes a record costs in a saved profile, for the shared budgets.
    static func approximateBytes(_ refs: [AgentImageRef]?) -> Int {
        (refs ?? []).reduce(0) { $0 + 160 + $1.mediaType.utf8.count + $1.source.utf8.count + ($1.path?.utf8.count ?? 0) }
    }

    /// The text an image entry carries. The phone and any reader without
    /// picture support show it in place of the pictures.
    public static func entryText(_ refs: [AgentImageRef], source: String) -> String {
        let label = ActivitySupport.clean(source, maximumBytes: maximumSourceBytes, singleLine: true)
        return refs.count == 1 ? L("images.entry.one", ["source": label]) : L("images.entry.many", ["count": "\(refs.count)", "source": label])
    }

    /// Base64 picture payloads in a tool result or message content: Claude's
    /// `{"type":"image","source":{"type":"base64",…}}` blocks and MCP's
    /// `{"type":"image","data":…,"mimeType":…}` blocks, in order, also inside
    /// a `content` array (Codex `mcp_tool_call` results). Text is never read.
    public static func payloads(in value: Any?, depth: Int = 0) -> [(mediaType: String, base64: String)] {
        guard depth < 4 else { return [] }
        if let record = value as? [String: Any] {
            if record["type"] as? String == "image" {
                if let source = record["source"] as? [String: Any], source["type"] as? String == "base64",
                   let data = source["data"] as? String, let type = source["media_type"] as? String { return [(type, data)] }
                if let data = record["data"] as? String, let type = (record["mimeType"] ?? record["mime_type"] ?? record["media_type"]) as? String { return [(type, data)] }
                return []
            }
            return payloads(in: record["content"], depth: depth + 1)
        }
        guard let blocks = value as? [Any] else { return [] }
        return Array(blocks.prefix(64).flatMap { payloads(in: $0, depth: depth + 1) })
    }

    /// Strict base64 (whitespace allowed), decoded only within the size cap.
    public static func decodeBase64(_ text: String) throws -> Data {
        guard text.utf8.count <= (maximumImageBytes / 3 + 2) * 4 + 4_096 else { throw AgentImageError.tooLarge }
        guard let data = Data(base64Encoded: text, options: .ignoreUnknownCharacters), !data.isEmpty else { throw AgentImageError.invalidEncoding }
        guard data.count <= maximumImageBytes else { throw AgentImageError.tooLarge }
        return data
    }
}

/// A picture checked and written to the cache, not yet attributed to a tool.
/// The runner prepares the large ones off its actor (`AgentOutputLines`).
public struct AgentImagePrepared: Sendable, Equatable {
    public var hash: String
    public var mediaType: String
    public var width: Int
    public var height: Int
    public var bytes: Int

    public func ref(source: String, path: String? = nil) -> AgentImageRef {
        let ref = AgentImageRef(hash: hash, mediaType: mediaType, width: width, height: height, bytes: bytes, source: source, path: path)
        return AgentImageSupport.normalized(ref) ?? ref
    }
}

/// The content-addressed picture store under the app's data directory.
/// Files are named `<sha256>.<ext>`; reading one marks it recently used, and
/// writing past the cap removes the least recently used others.
public final class AgentImageCache: @unchecked Sendable {
    public let directory: URL
    public let maximumBytes: Int
    private let lock = NSLock()

    public init(directory: URL, maximumBytes: Int = AgentImageSupport.maximumCacheBytes) {
        self.directory = directory; self.maximumBytes = maximumBytes
    }

    func fileURL(hash: String, mediaType: String) -> URL? {
        guard hash.utf8.count == 64, hash.allSatisfy({ $0.isHexDigit }), let type = AgentImageSupport.mediaType(mediaType),
              let ext = AgentImageSupport.extensions[type] else { return nil }
        return directory.appendingPathComponent(hash.lowercased() + "." + ext)
    }

    /// Validates, then writes the picture once under its hash. A file already
    /// there whose bytes no longer match the hash is written again.
    public func prepare(_ data: Data, mediaType: String) throws -> AgentImagePrepared {
        let inspected = try AgentImageSupport.inspect(data, mediaType: mediaType)
        let hash = AgentImageSupport.sha256(data)
        guard let url = fileURL(hash: hash, mediaType: inspected.mediaType) else { throw AgentImageError.unsupportedType }
        lock.lock(); defer { lock.unlock() }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if intact(url, bytes: data.count, hash: hash) { touch(url) }
        else {
            try data.write(to: url, options: [.atomic])
            evict(keeping: url)
        }
        return AgentImagePrepared(hash: hash, mediaType: inspected.mediaType, width: inspected.width, height: inspected.height, bytes: data.count)
    }

    public func prepare(base64: String, mediaType: String) throws -> AgentImagePrepared {
        try prepare(AgentImageSupport.decodeBase64(base64), mediaType: mediaType)
    }

    public func store(_ data: Data, mediaType: String, source: String, path: String? = nil) throws -> AgentImageRef {
        try prepare(data, mediaType: mediaType).ref(source: source, path: path)
    }

    public func store(base64: String, mediaType: String, source: String, path: String? = nil) throws -> AgentImageRef {
        try prepare(base64: base64, mediaType: mediaType).ref(source: source, path: path)
    }

    /// The cached file has the expected size and hash; a damaged or truncated
    /// one is not trusted (callers hold the lock).
    private func intact(_ url: URL, bytes: Int, hash: String) -> Bool {
        guard let size = (try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber, size.intValue == bytes,
              let data = try? Data(contentsOf: url) else { return false }
        return AgentImageSupport.sha256(data) == hash
    }

    /// The cached file, marked as just used; nil once it was evicted or removed.
    public func url(for ref: AgentImageRef) -> URL? {
        guard let url = fileURL(hash: ref.hash, mediaType: ref.mediaType) else { return nil }
        lock.lock(); defer { lock.unlock() }
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        touch(url)
        return url
    }

    /// The cached bytes, re-checked against the hash they are named by.
    public func data(for ref: AgentImageRef) -> Data? {
        guard let url = url(for: ref), let data = try? Data(contentsOf: url), data.count <= AgentImageSupport.maximumImageBytes,
              AgentImageSupport.sha256(data) == ref.hash.lowercased() else { return nil }
        return data
    }

    public var totalBytes: Int { lock.lock(); defer { lock.unlock() }; return entries().reduce(0) { $0 + $1.bytes } }

    private func touch(_ url: URL) { try? FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: url.path) }

    private func entries() -> [(url: URL, bytes: Int, used: Date)] {
        let keys: [URLResourceKey] = [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey]
        let urls = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
        return urls.compactMap { url in
            guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { return nil }
            return (url, values.fileSize ?? 0, values.contentModificationDate ?? .distantPast)
        }
    }

    private func evict(keeping kept: URL) {
        var files = entries().sorted { $0.used < $1.used }
        var total = files.reduce(0) { $0 + $1.bytes }
        while total > maximumBytes, !files.isEmpty {
            let oldest = files.removeFirst()
            guard oldest.url.lastPathComponent != kept.lastPathComponent else { continue }
            if (try? FileManager.default.removeItem(at: oldest.url)) != nil { total -= oldest.bytes }
        }
    }
}

/// Where a picture an agent named may be read from.
public enum AgentImageLocation: Equatable, Sendable {
    /// An existing picture file whose real path is inside `root`.
    case file(URL, root: URL)
    /// A `data:image/…;base64,` URI, still encoded.
    case inline(mediaType: String, base64: String)
    /// An http(s) address. It is shown as a link and never fetched.
    case remote(URL)
    /// Anything else: outside the allowed folders, not a picture, malformed.
    case refused
}

/// The path rule for pictures named in agent text (Markdown images) or by a
/// tool item that reports only a path: the file must be a picture whose real
/// path, symlinks resolved, lies inside the workspace root or the system
/// temporary folder. Relative paths resolve against the workspace only.
public enum AgentImagePaths {
    /// The temporary folders an agent's tools write screenshots to. Worked
    /// out once: resolving them touches the file system.
    public static let temporaryRoots: [URL] = {
        var roots = [FileManager.default.temporaryDirectory]
        let tmp = URL(fileURLWithPath: "/tmp", isDirectory: true)
        if !roots.contains(where: { WorkspaceFiles.realRoot($0) == WorkspaceFiles.realRoot(tmp) }) { roots.append(tmp) }
        return roots
    }()

    public static func locate(_ reference: String, workspaceRoot: URL?, temporaryRoots: [URL] = AgentImagePaths.temporaryRoots) -> AgentImageLocation {
        let value = reference.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, !value.contains("\0") else { return .refused }
        if value.lowercased().hasPrefix("data:") { return inline(value) }
        if let url = URL(string: value), let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme) {
            return (url.host ?? "").isEmpty ? .refused : .remote(url)
        }
        // An absolute path is tried as written, then percent-decoded: Markdown
        // hands over `/tmp/%EC%8A%A4….png` for a Korean or spaced file name.
        let paths: [String]
        if value.hasPrefix("/") { paths = [value] + (value.removingPercentEncoding.map { $0 == value ? [] : [$0] } ?? []) }
        else if let url = URL(string: value), let local = ReferenceLinkSupport.localPath(url) { paths = [local] }
        else { return .refused }
        for path in paths {
            guard !path.hasPrefix("~"), !path.contains("\0"), AgentImageSupport.mediaType(forFileName: path) != nil else { continue }
            let roots = (workspaceRoot.map { [$0] } ?? []) + (path.hasPrefix("/") ? temporaryRoots : [])
            for root in roots {
                guard let resolved = ReferenceLinkSupport.resolve(path, root: root),
                      AgentImageSupport.mediaType(forFileName: resolved.lastPathComponent) != nil else { continue }
                return .file(resolved, root: root)
            }
        }
        return .refused
    }

    private static func inline(_ value: String) -> AgentImageLocation {
        guard let comma = value.firstIndex(of: ",") else { return .refused }
        let header = value[value.index(value.startIndex, offsetBy: 5)..<comma].lowercased()
        let parts = header.split(separator: ";").map { $0.trimmingCharacters(in: .whitespaces) }
        guard parts.last == "base64", let type = parts.first.flatMap({ AgentImageSupport.mediaType($0) }) else { return .refused }
        return .inline(mediaType: type, base64: String(value[value.index(after: comma)...]))
    }

    /// Reads a located file through the workspace safe-open (no final
    /// symlink, regular file, descriptor path re-checked against the root).
    public static func read(_ url: URL, root: URL) throws -> Data {
        let file = try WorkspaceFiles.openResolved(url, root: root)
        guard file.size <= Int64(AgentImageSupport.maximumImageBytes) else { throw AgentImageError.tooLarge }
        let data = try file.handle.read(upToCount: AgentImageSupport.maximumImageBytes + 1) ?? Data()
        guard data.count <= AgentImageSupport.maximumImageBytes else { throw AgentImageError.tooLarge }
        return data
    }
}

/// `![alt](target)` images in agent Markdown, outside fenced code blocks.
public struct AgentMarkdownImage: Equatable, Sendable {
    public var alt: String
    public var source: String
}

public enum AgentMarkdownImages {
    public static let maximumImages = 32
    private static let pattern = #/!\[([^\]\n]{0,500})\]\(\s*(?:<([^>\n]+)>|([^)\s]+))(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)/#

    public static func extract(_ text: String) -> [AgentMarkdownImage] {
        guard text.contains("![") else { return [] }
        var result: [AgentMarkdownImage] = []
        var fenced = false
        var outside = ""
        for line in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let trimmed = line.drop(while: { $0 == " " })
            if trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") { fenced.toggle(); outside += "\n"; continue }
            outside += fenced ? "\n" : line + "\n"
        }
        for match in outside.matches(of: pattern) {
            guard let source = match.output.2 ?? match.output.3 else { continue }
            result.append(AgentMarkdownImage(alt: String(match.output.1), source: String(source)))
            if result.count == maximumImages { break }
        }
        return result
    }
}
