import Darwin
import Foundation

/// The Mac-only, read-only files pane. It is held in the pane list like an
/// agent's terminal pane: a `RunSession` of its own kind that is never saved
/// (`SessionKind.stored` leaves it out) and never listed for the phone.
public enum FilePaneKind {
    public static let kind = "files"

    /// One files pane per workspace, so its id follows from the workspace id.
    public static func paneId(workspaceId: String) -> String { kind + ":" + workspaceId }
    public static func isFilePane(_ kind: String) -> Bool { kind == Self.kind }

    /// The panes the relay pane list may carry: everything but files panes,
    /// which the phone cannot show and has no command for.
    public static func phoneVisible(_ sessions: [RunSession]) -> [RunSession] {
        sessions.filter { !isFilePane($0.kind) }
    }
}

/// One row of the workspace tree. `relativePath` is the path under the
/// workspace root as the user sees it (a symlink keeps its own name).
public struct WorkspaceFileEntry: Equatable, Sendable, Identifiable {
    public let name: String
    public let relativePath: String
    public let isDirectory: Bool
    public let isSymlink: Bool
    public var id: String { relativePath }
    /// Heavy or generated folders stay collapsed until the user opens them.
    public var isNoise: Bool { isDirectory && WorkspaceFiles.isNoiseFolder(name) }

    public init(name: String, relativePath: String, isDirectory: Bool, isSymlink: Bool = false) {
        self.name = name; self.relativePath = relativePath; self.isDirectory = isDirectory; self.isSymlink = isSymlink
    }
}

public struct WorkspaceDirectoryListing: Equatable, Sendable {
    public let entries: [WorkspaceFileEntry]
    /// More entries existed than `WorkspaceFiles.maximumEntriesPerFolder`.
    public let truncated: Bool
}

public enum WorkspaceFileError: Error, Equatable, Sendable {
    /// Missing, or its real path is outside the workspace root.
    case outsideRoot
    case notDirectory
    case unreadable(String)
}

/// Why a file could not be opened for a preview.
public enum WorkspaceFileOpenError: Error, Equatable, Sendable {
    /// Gone, swapped for a symlink, or its real path leaves the root.
    case missing
    /// A folder, FIFO, socket or device: never read.
    case notRegularFile
    case unreadable
}

/// A regular file opened read-only under the workspace root. Read through
/// `handle`, never by path again, so a later swap cannot redirect the read.
public struct WorkspaceOpenFile {
    public let handle: FileHandle
    /// The resolved URL the file was opened at.
    public let url: URL
    public let size: Int64
    public let modified: Date?
}

/// Read-only access to the files under a workspace root. Every path is
/// resolved through symlinks and refused unless its real path is the root or
/// inside it, so neither a `..` nor a symlink can reach other files.
public enum WorkspaceFiles {
    public static let noiseFolders: Set<String> = [".git", "node_modules", ".build", "build", "dist", "DerivedData", ".next", "Pods", ".venv", "__pycache__"]
    public static let maximumEntriesPerFolder = 5_000
    /// Names read from one folder before the rest are left unread (and the
    /// listing marked truncated): a folder of hundreds of thousands of files
    /// is never checked and sorted whole, however often a phone asks for it.
    public static let maximumEnumeratedNames = 20_000
    public static let maximumPathBytes = 4_096

    public static func isNoiseFolder(_ name: String) -> Bool { noiseFolders.contains(name) }

    /// Folders first, then files; each group in Finder's case-insensitive
    /// natural order ("file2" before "file10").
    public static func sorted(_ entries: [WorkspaceFileEntry]) -> [WorkspaceFileEntry] {
        entries.sorted { lhs, rhs in
            if lhs.isDirectory != rhs.isDirectory { return lhs.isDirectory }
            let order = lhs.name.localizedStandardCompare(rhs.name)
            return order == .orderedSame ? lhs.name < rhs.name : order == .orderedAscending
        }
    }

    /// The root's real path; every containment check compares against it.
    public static func realRoot(_ root: URL) -> URL { root.standardizedFileURL.resolvingSymlinksInPath() }

    /// Whether `realPath` (already resolved) is the real root or inside it.
    public static func contains(realPath: URL, realRoot: URL) -> Bool {
        let base = realRoot.path, path = realPath.path
        return path == base || path.hasPrefix(base.hasSuffix("/") ? base : base + "/")
    }

    /// The real URL of an existing item at `relativePath` under `root`
    /// ("" is the root itself), or nil when it is missing or its real path
    /// leaves the root.
    public static func resolve(_ relativePath: String, root: URL) -> URL? {
        guard root.isFileURL, !relativePath.hasPrefix("/"), !relativePath.contains("\0"),
              relativePath.utf8.count <= maximumPathBytes else { return nil }
        let base = realRoot(root)
        let candidate = (relativePath.isEmpty ? base : base.appendingPathComponent(relativePath)).standardizedFileURL.resolvingSymlinksInPath()
        guard contains(realPath: candidate, realRoot: base), FileManager.default.fileExists(atPath: candidate.path) else { return nil }
        return candidate
    }

    /// The folder's children, sorted, hidden files included. Children whose
    /// real path leaves the root (or dangling symlinks) are left out. Only the
    /// first `enumerationLimit` names on disk are looked at; past it the
    /// listing is truncated.
    public static func list(_ relativePath: String, root: URL, enumerationLimit: Int = maximumEnumeratedNames) throws -> WorkspaceDirectoryListing {
        guard let directory = resolve(relativePath, root: root) else { throw WorkspaceFileError.outsideRoot }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDirectory), isDirectory.boolValue else { throw WorkspaceFileError.notDirectory }
        let base = realRoot(root)
        var names: [String] = [], unread = false
        guard let stream = opendir(directory.path) else { throw WorkspaceFileError.unreadable(String(cString: strerror(errno))) }
        defer { closedir(stream) }
        while let item = readdir(stream) {
            let length = Int(item.pointee.d_namlen)
            let name = withUnsafeBytes(of: item.pointee.d_name) { String(decoding: $0.prefix(length), as: UTF8.self) }
            if name == "." || name == ".." { continue }
            if names.count == enumerationLimit { unread = true; break }
            names.append(name)
        }
        var entries: [WorkspaceFileEntry] = []
        for name in names where !name.isEmpty && !name.contains("/") {
            let item = directory.appendingPathComponent(name)
            let isLink = (try? FileManager.default.destinationOfSymbolicLink(atPath: item.path)) != nil
            let real = isLink ? item.resolvingSymlinksInPath() : item
            var childIsDirectory: ObjCBool = false
            guard contains(realPath: real, realRoot: base),
                  FileManager.default.fileExists(atPath: real.path, isDirectory: &childIsDirectory) else { continue }
            entries.append(WorkspaceFileEntry(name: name, relativePath: relativePath.isEmpty ? name : relativePath + "/" + name,
                                              isDirectory: childIsDirectory.boolValue, isSymlink: isLink))
        }
        let ordered = sorted(entries)
        return WorkspaceDirectoryListing(entries: Array(ordered.prefix(maximumEntriesPerFolder)), truncated: unread || ordered.count > maximumEntriesPerFolder)
    }

    /// Opens the file at `relativePath` for reading: resolved under the root,
    /// opened without following a final symlink and without blocking, required
    /// to be a regular file, and its real path (from the open descriptor)
    /// checked against the root once more.
    public static func openFile(_ relativePath: String, root: URL) throws -> WorkspaceOpenFile {
        guard let url = resolve(relativePath, root: root) else { throw WorkspaceFileOpenError.missing }
        return try openResolved(url, root: root)
    }

    /// `openFile` after resolution: the checks that hold on the descriptor.
    static func openResolved(_ url: URL, root: URL) throws -> WorkspaceOpenFile {
        let descriptor = Darwin.open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard descriptor >= 0 else {
            switch errno {
            case ENOENT, ENOTDIR, ELOOP: throw WorkspaceFileOpenError.missing
            default: throw WorkspaceFileOpenError.unreadable
            }
        }
        var status = stat()
        guard fstat(descriptor, &status) == 0 else { Darwin.close(descriptor); throw WorkspaceFileOpenError.unreadable }
        guard status.st_mode & S_IFMT == S_IFREG else { Darwin.close(descriptor); throw WorkspaceFileOpenError.notRegularFile }
        var buffer = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard fcntl(descriptor, F_GETPATH, &buffer) != -1, let canonicalRoot = canonicalPath(root) else {
            Darwin.close(descriptor); throw WorkspaceFileOpenError.unreadable
        }
        guard contains(realPath: URL(fileURLWithPath: String(cString: buffer)), realRoot: canonicalRoot) else {
            Darwin.close(descriptor); throw WorkspaceFileOpenError.missing
        }
        let modified = Date(timeIntervalSince1970: TimeInterval(status.st_mtimespec.tv_sec) + TimeInterval(status.st_mtimespec.tv_nsec) / 1_000_000_000)
        return WorkspaceOpenFile(handle: FileHandle(fileDescriptor: descriptor, closeOnDealloc: true), url: url, size: Int64(status.st_size), modified: modified)
    }

    /// realpath(3), which (unlike URL's symlink resolution) keeps `/private`
    /// the way the kernel reports a descriptor's path.
    static func canonicalPath(_ url: URL) -> URL? {
        guard let resolved = realpath(url.path, nil) else { return nil }
        defer { free(resolved) }
        return URL(fileURLWithPath: String(cString: resolved))
    }
}

/// The text encodings the preview reads. A byte order mark decides first;
/// otherwise UTF-8, then CP949 (Windows Korean, a superset of EUC-KR) when
/// the bytes decode cleanly.
public enum TextEncoding: String, Sendable, CaseIterable {
    case utf8, utf8BOM, utf16LE, utf16BE, utf32LE, utf32BE, cp949

    public static let cp949Encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.dosKorean.rawValue)))

    /// Shown in the preview header.
    public var displayName: String {
        switch self {
        case .utf8: return "UTF-8"
        case .utf8BOM: return "UTF-8 BOM"
        case .utf16LE: return "UTF-16 LE"
        case .utf16BE: return "UTF-16 BE"
        case .utf32LE: return "UTF-32 LE"
        case .utf32BE: return "UTF-32 BE"
        case .cp949: return "CP949 (EUC-KR)"
        }
    }

    public var stringEncoding: String.Encoding {
        switch self {
        case .utf8, .utf8BOM: return .utf8
        case .utf16LE: return .utf16LittleEndian
        case .utf16BE: return .utf16BigEndian
        case .utf32LE: return .utf32LittleEndian
        case .utf32BE: return .utf32BigEndian
        case .cp949: return Self.cp949Encoding
        }
    }

    public var byteOrderMark: [UInt8] {
        switch self {
        case .utf8, .cp949: return []
        case .utf8BOM: return [0xEF, 0xBB, 0xBF]
        case .utf16LE: return [0xFF, 0xFE]
        case .utf16BE: return [0xFE, 0xFF]
        case .utf32LE: return [0xFF, 0xFE, 0x00, 0x00]
        case .utf32BE: return [0x00, 0x00, 0xFE, 0xFF]
        }
    }

    /// Bytes a cut sample may leave of a character at its end.
    var tailSlack: Int { self == .cp949 ? 1 : 3 }

    /// The encoding named by the data's byte order mark (UTF-32 before
    /// UTF-16, whose LE mark it starts with).
    public static func fromByteOrderMark(_ data: Data) -> TextEncoding? {
        let head = [UInt8](data.prefix(4))
        return [.utf32LE, .utf32BE, .utf8BOM, .utf16LE, .utf16BE].first { head.starts(with: $0.byteOrderMark) }
    }
}

/// The source languages the preview highlights. `plain` is text without
/// highlighting (logs, CSV, unknown text found by sniffing).
public enum SourceLanguage: String, Sendable, CaseIterable {
    case swift, c, javascript, python, kotlin, java, go, rust, shell, json, yaml, toml, xml, html, css, sql, gradle, dockerfile, makefile, plain
}

public enum FilePreviewKind: Equatable, Sendable {
    case markdown
    case source(SourceLanguage)
    case image
    case unsupported
}

/// Decides how a file is previewed: by name first, then by sniffing its
/// first bytes. A text kind still needs text bytes, so a binary `.plist` or
/// a mislabelled file falls back to `unsupported`.
public enum FilePreviewClassifier {
    /// Text beyond this is not shown; the preview says so.
    public static let maximumTextBytes = 1_048_576
    /// Images larger than this are not decoded.
    public static let maximumImageBytes = 50 * 1_048_576
    /// A bitmap is shown fitted from a thumbnail at most this many pixels on
    /// its long side.
    public static let maximumFitPixels = 4_096
    /// Bitmaps with more pixels than this are never decoded at full size, so
    /// they have no 1:1 or zoom.
    public static let maximumFullPixels = 100_000_000
    /// Bitmaps with more pixels than this are not decoded at all, not even to
    /// a thumbnail: a thumbnail of some formats still decodes every pixel.
    public static let maximumDecodePixels = 250_000_000
    /// An svg or pdf wider or taller than this many points is not drawn. Real
    /// documents stay far below it; a file can claim 1e30 or infinity.
    public static let maximumVectorPoints: Double = 10_000_000
    /// Zoom stops before the image is this many points on its long side.
    public static let maximumZoomPoints: Double = 16_384
    /// How much of a file is read to tell text from binary.
    public static let sniffBytes = 8_192

    static let markdownExtensions: Set<String> = ["md", "markdown", "mdx"]
    static let imageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tif", "tiff", "bmp", "ico", "svg", "pdf"]
    static let sourceExtensions: [String: SourceLanguage] = [
        "swift": .swift,
        "c": .c, "h": .c, "cc": .c, "cpp": .c, "cxx": .c, "hpp": .c, "hh": .c, "m": .c, "mm": .c,
        "js": .javascript, "jsx": .javascript, "mjs": .javascript, "cjs": .javascript, "ts": .javascript, "tsx": .javascript, "mts": .javascript, "cts": .javascript,
        "py": .python, "pyi": .python,
        "kt": .kotlin, "kts": .kotlin, "java": .java, "go": .go, "rs": .rust,
        "sh": .shell, "bash": .shell, "zsh": .shell, "fish": .shell,
        "json": .json, "jsonc": .json, "yaml": .yaml, "yml": .yaml, "toml": .toml,
        "xml": .xml, "plist": .xml, "xib": .xml, "storyboard": .xml, "entitlements": .xml,
        "html": .html, "htm": .html, "css": .css, "scss": .css, "less": .css, "sql": .sql, "gradle": .gradle,
        "txt": .plain, "log": .plain, "csv": .plain, "tsv": .plain, "ini": .plain, "cfg": .plain, "conf": .plain, "properties": .plain, "env": .shell,
    ]

    /// The lower-cased extension; a leading dot (".env") is not one.
    public static func fileExtension(_ name: String) -> String {
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return "" }
        return String(name[name.index(after: dot)...]).lowercased()
    }

    /// The kind from the file name alone, or nil when only the bytes can tell.
    public static func kind(forName name: String) -> FilePreviewKind? {
        let ext = fileExtension(name)
        if markdownExtensions.contains(ext) { return .markdown }
        if imageExtensions.contains(ext) { return .image }
        let lower = name.lowercased()
        if lower == "dockerfile" || lower.hasPrefix("dockerfile.") || ext == "dockerfile" { return .source(.dockerfile) }
        if ["makefile", "gnumakefile"].contains(lower) || ext == "mk" { return .source(.makefile) }
        if lower == ".env" || lower.hasPrefix(".env.") { return .source(.shell) }
        if let language = sourceExtensions[ext] { return .source(language) }
        return nil
    }

    /// The preview kind for `name` given its first bytes (up to `sniffBytes`).
    public static func classify(name: String, head: Data) -> FilePreviewKind {
        switch kind(forName: name) {
        case .markdown?: return looksLikeText(head) ? .markdown : .unsupported
        case .image?: return .image
        case .source(let language)?: return looksLikeText(head) ? .source(language) : .unsupported
        case .unsupported?, nil: return looksLikeText(head) ? .source(.plain) : .unsupported
        }
    }

    /// Text in one of the `TextEncoding`s. Without a UTF-16/32 byte order
    /// mark a NUL byte means binary. The sample may end inside a character.
    public static func looksLikeText(_ data: Data) -> Bool {
        decodeText(data, sample: true) != nil
    }

    /// Decodes UTF-8, dropping at most three bytes of a character the sample
    /// cut in half; nil when the bytes are not UTF-8.
    public static func decodedPrefix(_ data: Data) -> String? {
        decode(data, as: .utf8, sample: true)
    }

    /// The text and its encoding: the byte order mark's encoding, else UTF-8,
    /// else CP949; nil when none decodes cleanly (or a NUL shows binary).
    /// With `sample` the data may end inside a character.
    public static func decodeText(_ data: Data, sample: Bool) -> (text: String, encoding: TextEncoding)? {
        if let marked = TextEncoding.fromByteOrderMark(data) {
            return decode(data.dropFirst(marked.byteOrderMark.count), as: marked, sample: sample).map { ($0, marked) }
        }
        guard !data.contains(0) else { return nil }
        for encoding in [TextEncoding.utf8, .cp949] {
            if let text = decode(data, as: encoding, sample: sample) { return (text, encoding) }
        }
        return nil
    }

    static func decode(_ data: Data, as encoding: TextEncoding, sample: Bool) -> String? {
        for drop in 0...(sample ? min(encoding.tailSlack, data.count) : 0) {
            if let text = String(data: data.dropLast(drop), encoding: encoding.stringEncoding) { return text }
        }
        return nil
    }

    /// Decodes whatever is there, replacing bad sequences: used when the
    /// whole read no longer decodes the way its first bytes did.
    static func decodeLossily(_ data: Data, as encoding: TextEncoding) -> String {
        let bytes = [UInt8](data)
        switch encoding {
        case .utf16LE, .utf16BE:
            let units = stride(from: 0, to: bytes.count - 1, by: 2).map { index -> UInt16 in
                let first = UInt16(bytes[index]), second = UInt16(bytes[index + 1])
                return encoding == .utf16LE ? first | second << 8 : first << 8 | second
            }
            return String(decoding: units, as: UTF16.self)
        case .utf32LE, .utf32BE:
            let units = stride(from: 0, to: bytes.count - 3, by: 4).map { index -> UInt32 in
                let value = bytes[index..<(index + 4)].reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
                return encoding == .utf32LE ? value.byteSwapped : value
            }
            return String(decoding: units, as: UTF32.self)
        case .utf8, .utf8BOM, .cp949:
            return String(decoding: bytes, as: UTF8.self)
        }
    }

    /// The first `maximumBytes` of a text file, read from its start through
    /// `handle`, in the encoding `decodeText` finds. Bytes that no longer
    /// decode are shown as replacement characters rather than refusing it.
    public static func readText(_ handle: FileHandle, maximumBytes: Int = maximumTextBytes) throws -> (text: String, truncated: Bool, encoding: TextEncoding) {
        try handle.seek(toOffset: 0)
        let data = try handle.read(upToCount: maximumBytes + 1) ?? Data()
        let truncated = data.count > maximumBytes
        let body = truncated ? data.prefix(maximumBytes) : data
        if let decoded = decodeText(body, sample: true) { return (decoded.text, truncated, decoded.encoding) }
        let fallback = TextEncoding.fromByteOrderMark(body) ?? .utf8
        return (decodeLossily(body.dropFirst(fallback.byteOrderMark.count), as: fallback), truncated, fallback)
    }

    /// The first `sniffBytes` of a file, read from its start through `handle`.
    public static func readHead(_ handle: FileHandle) throws -> Data {
        try handle.seek(toOffset: 0)
        return try handle.read(upToCount: sniffBytes) ?? Data()
    }
}

// MARK: Image sizes and svg references

public extension FilePreviewClassifier {
    /// width × height, or nil when either is not positive or the product overflows.
    static func pixelCount(width: Int, height: Int) -> Int? {
        guard width > 0, height > 0 else { return nil }
        let (product, overflow) = width.multipliedReportingOverflow(by: height)
        return overflow ? nil : product
    }

    /// Whether an svg's or pdf's size in points may be drawn and turned into
    /// whole numbers: finite, positive and at most `maximumVectorPoints`.
    static func isDrawable(width: Double, height: Double) -> Bool {
        width.isFinite && height.isFinite && width > 0 && height > 0 && width <= maximumVectorPoints && height <= maximumVectorPoints
    }

    /// Whether drawing this svg could read anything but its own bytes. CoreSVG
    /// (behind `NSImage`) follows `<image href>` to `file:` URLs, absolute
    /// paths and bare relative ones (against the process's working folder),
    /// so an svg is only drawn when this is false. Deliberately conservative,
    /// on the text with numeric character references decoded and case folded:
    /// any `href`/`src` value or CSS `url(` that is neither a `#fragment` nor a
    /// non-svg `data:` URL, any `@import`, `image-set(`, `xml:base`,
    /// `<!ENTITY`, DOCTYPE with an identifier or internal subset, CSS
    /// backslash escape, declared encoding the check cannot read as ASCII, or
    /// bytes that do not decode as text all count as external.
    static func svgLoadsExternalContent(_ data: Data) -> Bool {
        guard let decoded = decodeText(data, sample: false) else { return true }
        let text = Array(SVGReferences.decodingCharacterReferences(decoded.text).lowercased().utf8)
        if text.contains(UInt8(ascii: "\\")) { return true }
        for token in ["<!entity", "@import", "image-set(", "xml:base"] where !SVGReferences.find(token, in: text).isEmpty { return true }
        for start in SVGReferences.find("<!doctype", in: text) {
            guard let end = text[start...].firstIndex(of: UInt8(ascii: ">")) else { return true }
            let doctype = Array(text[start..<end])
            if ["system", "public", "["].contains(where: { !SVGReferences.find($0, in: doctype).isEmpty }) { return true }
        }
        if !SVGReferences.readableDeclaration(text, bom: decoded.encoding) { return true }
        for name in ["href", "src", "srcset"] {
            for start in SVGReferences.find(name, in: text) {
                var index = SVGReferences.skipSpace(text, start + name.utf8.count)
                // Not followed by `=`: the word in running text, not an attribute.
                guard index < text.count, text[index] == UInt8(ascii: "=") else { continue }
                index += 1
                if !SVGReferences.isLocal(text, from: index) { return true }
            }
        }
        for start in SVGReferences.find("url(", in: text) where !SVGReferences.isLocal(text, from: start + 4) { return true }
        return false
    }
}

/// The byte-level pieces of `FilePreviewClassifier.svgLoadsExternalContent`.
enum SVGReferences {
    /// Every offset of `needle` in `text`.
    static func find(_ needle: String, in text: [UInt8]) -> [Int] {
        let pattern = Array(needle.utf8)
        guard !pattern.isEmpty, text.count >= pattern.count else { return [] }
        var found: [Int] = []
        text.withUnsafeBytes { haystack in
            pattern.withUnsafeBytes { wanted in
                guard let base = haystack.baseAddress, let wantedBase = wanted.baseAddress else { return }
                var offset = 0
                while offset <= haystack.count - wanted.count,
                      let hit = memmem(base + offset, haystack.count - offset, wantedBase, wanted.count) {
                    let position = base.distance(to: UnsafeRawPointer(hit))
                    found.append(position)
                    offset = position + 1
                }
            }
        }
        return found
    }

    static func isSpace(_ byte: UInt8) -> Bool { byte == 0x20 || byte == 0x09 || byte == 0x0A || byte == 0x0D || byte == 0x0C }

    static func skipSpace(_ text: [UInt8], _ start: Int) -> Int {
        var index = start
        while index < text.count, isSpace(text[index]) { index += 1 }
        return index
    }

    /// Whether the reference starting at `start` (after `=` or `url(`, before
    /// any space and quote) stays inside the document: `#fragment`, or a
    /// `data:` URL whose media type is not svg, xml or html.
    static func isLocal(_ text: [UInt8], from start: Int) -> Bool {
        var index = skipSpace(text, start)
        if index < text.count, text[index] == UInt8(ascii: "\"") || text[index] == UInt8(ascii: "'") { index = skipSpace(text, index + 1) }
        guard index < text.count else { return false }
        if text[index] == UInt8(ascii: "#") { return true }
        let scheme = Array("data:".utf8)
        guard text.count - index > scheme.count, Array(text[index..<(index + scheme.count)]) == scheme else { return false }
        let rest = text[(index + scheme.count)...].prefix(256)
        guard let comma = rest.firstIndex(of: UInt8(ascii: ",")) else { return false }
        let media = Array(text[(index + scheme.count)..<comma].filter { !isSpace($0) })
        return ["svg", "xml", "html"].allSatisfy { find($0, in: media).isEmpty }
    }

    /// An XML declaration's encoding is one whose markup is plain ASCII bytes
    /// (or the byte order mark's own), so the scan above reads what the parser will.
    static func readableDeclaration(_ text: [UInt8], bom: TextEncoding) -> Bool {
        let start = skipSpace(text, 0)
        let opening = Array("<?xml".utf8)
        guard text.count - start >= opening.count, Array(text[start..<(start + opening.count)]) == opening else { return true }
        let declaration = Array(text[start...].prefix(512))
        guard let close = find("?>", in: declaration).first else { return false }
        let head = Array(declaration[..<close])
        guard let key = find("encoding", in: head).first else { return true }
        var index = skipSpace(head, key + 8)
        guard index < head.count, head[index] == UInt8(ascii: "=") else { return false }
        index = skipSpace(head, index + 1)
        guard index < head.count, head[index] == UInt8(ascii: "\"") || head[index] == UInt8(ascii: "'") else { return false }
        let quote = head[index]
        guard let end = head[(index + 1)...].firstIndex(of: quote) else { return false }
        let name = String(decoding: head[(index + 1)..<end], as: UTF8.self)
        var readable: Set<String> = ["utf-8", "utf8", "us-ascii", "ascii", "iso-8859-1", "latin1"]
        switch bom {
        case .utf16LE, .utf16BE: readable.formUnion(["utf-16", "utf-16le", "utf-16be"])
        case .utf32LE, .utf32BE: readable.formUnion(["utf-32", "utf-32le", "utf-32be"])
        case .utf8, .utf8BOM, .cp949: break
        }
        return readable.contains(name)
    }

    /// The text with `&#NN;`, `&#xHH;` and the five predefined entities
    /// replaced by their characters, as the XML parser reads them.
    static func decodingCharacterReferences(_ text: String) -> String {
        guard text.contains("&") else { return text }
        let scalars = Array(text.unicodeScalars)
        var output = String.UnicodeScalarView()
        var index = 0
        while index < scalars.count {
            if scalars[index] == "&", let (scalar, next) = reference(scalars, at: index + 1) {
                output.append(scalar); index = next
            } else {
                output.append(scalars[index]); index += 1
            }
        }
        return String(output)
    }

    private static func reference(_ scalars: [Unicode.Scalar], at start: Int) -> (Unicode.Scalar, Int)? {
        var index = start
        if index < scalars.count, scalars[index] == "#" {
            index += 1
            var radix: UInt32 = 10
            if index < scalars.count, scalars[index] == "x" || scalars[index] == "X" { radix = 16; index += 1 }
            var value: UInt32 = 0, digits = 0
            while index < scalars.count, let digit = Character(scalars[index]).hexDigitValue, UInt32(digit) < radix {
                value = min(value * radix + UInt32(digit), 0x11_0000)
                digits += 1; index += 1
            }
            guard digits > 0, index < scalars.count, scalars[index] == ";", let scalar = Unicode.Scalar(value) else { return nil }
            return (scalar, index + 1)
        }
        var name = ""
        while index < scalars.count, name.count < 5, scalars[index].properties.isAlphabetic { name.unicodeScalars.append(scalars[index]); index += 1 }
        guard index < scalars.count, scalars[index] == ";" else { return nil }
        let named: [String: Unicode.Scalar] = ["amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'"]
        return named[name.lowercased()].map { ($0, index + 1) }
    }
}
