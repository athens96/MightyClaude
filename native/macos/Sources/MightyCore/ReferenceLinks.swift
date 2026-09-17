import Foundation

/// A loadable file mentioned in a final result. Its canonical workspace-relative
/// path can be passed through the transcript's existing reference viewer.
public struct ReferenceFile: Equatable, Sendable, Identifiable {
    public let url: URL
    public let path: String
    public let line: Int?
    public var id: String { url.path }

    public init(url: URL, path: String, line: Int? = nil) {
        self.url = url; self.path = path; self.line = line
    }
}

/// File paths and web addresses mentioned in agent text. Detection is purely
/// textual; resolution only ever yields an existing regular file inside the
/// given workspace root, so a model-written path cannot reach other files.
public enum ReferenceLinkSupport {
    public struct Match: Equatable, Sendable {
        public let range: Range<String.Index>
        /// The matched path text for files, or the address text for the web.
        public let path: String
        public let line: Int?
        public let url: URL?
        public var isWeb: Bool { url != nil }
    }
    public static let maximumFileBytes = 2 * 1024 * 1024
    public static let maximumMatchesPerText = 200
    public static let maximumPathBytes = 1_024
    public static let maximumResultFiles = 100
    public static let maximumResultTextBytes = 512 * 1024
    public static let maximumResultTexts = 200

    // A path needs at least one directory segment and an ASCII extension, so
    // version numbers, "and/or" and bare file names are left alone. Text
    // adjacent to a URL is excluded by the look-behind on "/" ":" ".".
    private static let pathPattern = try! NSRegularExpression(pattern: #"(?<![A-Za-z0-9_/.:@~-])((?:\.{1,2}/|/)?(?:[\p{L}\p{N}_.@-]+/)+[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,8})(?::(\d{1,6}))?(?![A-Za-z0-9_/])"#)
    private static let webPattern = try! NSRegularExpression(pattern: #"https?://[^\s<>"'`)\]]+"#)

    public static func matches(in text: String) -> [Match] {
        let nsText = text as NSString
        let full = NSRange(location: 0, length: nsText.length)
        var result: [Match] = []
        var taken: [NSRange] = []
        for match in webPattern.matches(in: text, range: full).prefix(maximumMatchesPerText) {
            var range = match.range
            // Sentence punctuation directly after an address is rarely part of it.
            while range.length > 1, ".,;:!?".utf16.contains(nsText.character(at: NSMaxRange(range) - 1)) { range.length -= 1 }
            guard let swiftRange = Range(range, in: text), let url = URL(string: nsText.substring(with: range)), !(url.host ?? "").isEmpty else { continue }
            taken.append(range)
            result.append(Match(range: swiftRange, path: nsText.substring(with: range), line: nil, url: url))
        }
        for match in pathPattern.matches(in: text, range: full).prefix(maximumMatchesPerText) {
            guard !taken.contains(where: { NSIntersectionRange($0, match.range).length > 0 }),
                  let swiftRange = Range(match.range, in: text) else { continue }
            let path = nsText.substring(with: match.range(at: 1))
            guard path.utf8.count <= maximumPathBytes else { continue }
            let lineRange = match.range(at: 2)
            let line = lineRange.location == NSNotFound ? nil : Int(nsText.substring(with: lineRange))
            result.append(Match(range: swiftRange, path: path, line: line, url: nil))
        }
        return result.sorted { $0.range.lowerBound < $1.range.lowerBound }
    }

    /// A relative or file URL from Markdown link syntax, as a path string.
    public static func localPath(_ url: URL) -> String? {
        guard url.scheme == nil || url.scheme?.lowercased() == "file", (url.host ?? "").isEmpty else { return nil }
        let path = url.scheme == nil ? (url.path.isEmpty ? url.absoluteString : url.path) : url.path
        let clean = path.removingPercentEncoding ?? path
        guard !clean.isEmpty, !clean.contains("://"), clean.utf8.count <= maximumPathBytes, !clean.contains("\0") else { return nil }
        return clean
    }

    /// nil unless the reference is an existing regular file inside root.
    public static func resolve(_ path: String, root: URL?) -> URL? {
        guard let root, root.isFileURL, !path.isEmpty, !path.contains("\0"), path.utf8.count <= maximumPathBytes else { return nil }
        let base = root.standardizedFileURL.resolvingSymlinksInPath()
        let candidate = (path.hasPrefix("/") ? URL(fileURLWithPath: path) : base.appendingPathComponent(path)).standardizedFileURL.resolvingSymlinksInPath()
        let prefix = base.path.hasSuffix("/") ? base.path : base.path + "/"
        guard candidate.path.hasPrefix(prefix) else { return nil }
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: candidate.path),
              attributes[.type] as? FileAttributeType == .typeRegular,
              FileManager.default.isReadableFile(atPath: candidate.path) else { return nil }
        return candidate
    }

    /// Read-only extraction for the final-result file list. Markdown links use
    /// the same Foundation parser and options as AgentMarkdownDocument; plain
    /// text uses the same path detector as transcript links. No directory scan
    /// or file content read is performed, and remote panes pass a nil root.
    public static func resultFiles(in texts: [String], root: URL?) -> [ReferenceFile] {
        guard let root, root.isFileURL else { return [] }
        let base = root.standardizedFileURL.resolvingSymlinksInPath()
        let prefix = base.path.hasSuffix("/") ? base.path : base.path + "/"
        var files: [ReferenceFile] = []; var seen = Set<String>()
        var remainingBytes = maximumResultTextBytes
        var remainingCandidates = 1_000

        func add(_ path: String, line: Int? = nil) {
            guard remainingCandidates > 0, files.count < maximumResultFiles else { return }
            remainingCandidates -= 1
            var line = line
            var resolved = resolve(path, root: base)
            // Markdown destinations can carry the same :line convention as
            // plain transcript paths. A real filename containing ':' wins.
            if resolved == nil, let colon = path.lastIndex(of: ":") {
                let suffix = path[path.index(after: colon)...]
                if !suffix.isEmpty, suffix.count <= 6, suffix.allSatisfy({ $0.isASCII && $0.isNumber }), let number = Int(suffix), number > 0 {
                    resolved = resolve(String(path[..<colon]), root: base); line = number
                }
            }
            guard let url = resolved, seen.insert(url.path).inserted else { return }
            let displayPath = String(url.path.dropFirst(prefix.count))
            files.append(ReferenceFile(url: url, path: displayPath, line: line.flatMap { $0 > 0 ? $0 : nil }))
        }
        func plain(_ text: String) {
            for match in matches(in: text) where !match.isWeb {
                add(match.path, line: match.line)
                if remainingCandidates == 0 || files.count == maximumResultFiles { break }
            }
        }
        for source in texts.prefix(maximumResultTexts) {
            guard remainingBytes > 0, remainingCandidates > 0, files.count < maximumResultFiles else { break }
            let text = ActivitySupport.prefixUTF8(source, maximumBytes: remainingBytes)
            remainingBytes -= text.utf8.count
            // Keep the transcript renderer's 128 KiB Markdown parsing limit.
            if text.utf8.count <= 131_072,
               let parsed = try? AttributedString(markdown: text, options: .init(interpretedSyntax: .full, failurePolicy: .returnPartiallyParsedIfPossible)) {
                for run in parsed.runs {
                    guard remainingCandidates > 0, files.count < maximumResultFiles else { break }
                    if let link = run.link {
                        if let path = localPath(link) { add(path) }
                    } else {
                        plain(String(parsed[run.range].characters))
                    }
                }
            } else { plain(text) }
        }
        return files
    }

    /// markdown, html, image or text — how the desktop previews the file.
    public static func kind(of url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "md", "markdown", "mdx": return "markdown"
        case "html", "htm", "svg": return "html"
        case "png", "jpg", "jpeg", "gif", "webp", "heic", "tiff", "bmp": return "image"
        default: return "text"
        }
    }

    /// The in-app link carried by transcript text. It is never opened by AppKit.
    public static func referenceURL(path: String, line: Int?) -> URL? {
        var components = URLComponents()
        components.scheme = "mighty-transcript"; components.host = "reference"
        components.queryItems = [URLQueryItem(name: "path", value: path)] + (line.map { [URLQueryItem(name: "line", value: String($0))] } ?? [])
        return components.url
    }

    public static func parseReferenceURL(_ url: URL) -> (path: String, line: Int?)? {
        guard url.scheme == "mighty-transcript", url.host == "reference",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let path = items.first(where: { $0.name == "path" })?.value, !path.isEmpty, path.utf8.count <= maximumPathBytes else { return nil }
        return (path, items.first(where: { $0.name == "line" })?.value.flatMap(Int.init))
    }
}
