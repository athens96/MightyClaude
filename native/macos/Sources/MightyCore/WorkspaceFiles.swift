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
    /// real path leaves the root (or dangling symlinks) are left out.
    public static func list(_ relativePath: String, root: URL) throws -> WorkspaceDirectoryListing {
        guard let directory = resolve(relativePath, root: root) else { throw WorkspaceFileError.outsideRoot }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDirectory), isDirectory.boolValue else { throw WorkspaceFileError.notDirectory }
        let base = realRoot(root)
        let names: [String]
        do { names = try FileManager.default.contentsOfDirectory(atPath: directory.path) }
        catch { throw WorkspaceFileError.unreadable(error.localizedDescription) }
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
        return WorkspaceDirectoryListing(entries: Array(ordered.prefix(maximumEntriesPerFolder)), truncated: ordered.count > maximumEntriesPerFolder)
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
