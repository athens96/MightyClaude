import Foundation

/// One hunk of Claude Code's `structuredPatch`: lines are `" ctx"`, `"-old"`,
/// `"+new"` (and `"\ No newline…"`).
public struct FilePatchHunk: Equatable, Sendable {
    public var oldStart: Int
    public var oldLines: Int
    public var newStart: Int
    public var newLines: Int
    public var lines: [String]
}

/// What one Edit, Write or MultiEdit result says about the file before it
/// (Windows `FileEdit`): its `original` text when the CLI sent it, `created`
/// when the tool made the file, else the `patch` the tool applied, to be
/// undone against the file on disk. None of the three: the original is unknown.
public struct FileEdit: Equatable, Sendable {
    public var original: String?
    public var created: Bool
    public var patch: [FilePatchHunk]?
    public init(original: String? = nil, created: Bool = false, patch: [FilePatchHunk]? = nil) {
        self.original = original; self.created = created; self.patch = patch
    }
    public static let unknown = FileEdit()
}

/// Undoing a `structuredPatch` on the file as it is just after the edit
/// (Windows `FilePatch`, native/contracts/fixtures/file-diff.json
/// `reversePatch`): the result is the file before the edit, or nil when the
/// patch does not fit the file exactly.
public enum FilePatch {
    public static let maximumHunks = 10_000

    /// The hunks of a `structuredPatch` array (as JSONSerialization reads it); nil when it is not one.
    public static func parse(_ value: Any?) -> [FilePatchHunk]? {
        guard let items = value as? [Any], items.count <= maximumHunks else { return nil }
        var hunks: [FilePatchHunk] = []
        for item in items {
            guard let hunk = item as? [String: Any],
                  let oldStart = number(hunk["oldStart"]), let oldLines = number(hunk["oldLines"]),
                  let newStart = number(hunk["newStart"]), let newLines = number(hunk["newLines"]),
                  let lines = hunk["lines"] as? [Any] else { return nil }
            var texts: [String] = []
            for line in lines {
                guard let text = line as? String else { return nil }
                texts.append(text)
            }
            hunks.append(FilePatchHunk(oldStart: oldStart, oldLines: oldLines, newStart: newStart, newLines: newLines, lines: texts))
        }
        return hunks
    }

    /// A non-negative whole number; a JSON boolean (also an NSNumber) is not one.
    private static func number(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let int = number.intValue
        return int >= 0 && Double(int) == number.doubleValue ? int : nil
    }

    /// The bytes after the patch line's first character, a trailing CR aside.
    private static func body(_ line: String) -> [UInt8] {
        var bytes = Array(line.utf8.dropFirst())
        if bytes.last == 13 { bytes.removeLast() }
        return bytes
    }

    /// The file before the patch, from `current` (the file after it). Each
    /// hunk's new side (its context and added lines) must stand exactly at
    /// its `newStart`, in order, a trailing CR aside; its removed lines come
    /// back with the file's own line break (CRLF when the file has one). A
    /// `\` line (no newline at end of file), a count that disagrees with the
    /// lines, or any mismatch: nil. Read on UTF-8 bytes, as Windows reads code units.
    public static func reverseApply(_ current: String, hunks: [FilePatchHunk]) -> String? {
        let bytes = Array(current.utf8)
        var lines: [(body: ArraySlice<UInt8>, ending: [UInt8])] = []
        var start = 0
        var crlfSeen = false
        while start < bytes.count {
            guard let end = bytes[start...].firstIndex(of: 10) else { lines.append((bytes[start...], [])); break }
            let crlf = end > start && bytes[end - 1] == 13
            if crlf { crlfSeen = true }
            lines.append((bytes[start..<(crlf ? end - 1 : end)], crlf ? [13, 10] : [10]))
            start = end + 1
        }
        let ending: [UInt8] = crlfSeen ? [13, 10] : [10]
        var output: [UInt8] = []
        output.reserveCapacity(bytes.count)
        var cursor = 0
        for hunk in hunks {
            var newSide = 0, oldSide = 0
            for line in hunk.lines {
                guard let first = line.utf8.first, [32, 43, 45].contains(first) else { return nil }
                if first != 45 { newSide += 1 }
                if first != 43 { oldSide += 1 }
            }
            guard newSide == hunk.newLines, oldSide == hunk.oldLines else { return nil }
            // A hunk with no new lines has no context either: the file was emptied, so it stands at the end of what is left.
            var at = hunk.newLines == 0 ? min(hunk.newStart, lines.count) : hunk.newStart - 1
            guard at >= cursor, at + hunk.newLines <= lines.count else { return nil }
            for index in cursor..<at { output += lines[index].body; output += lines[index].ending }
            for line in hunk.lines {
                let text = body(line)
                if line.utf8.first == 45 { output += text; output += ending; continue }
                guard Array(lines[at].body) == text else { return nil }
                if line.utf8.first == 32 { output += lines[at].body; output += lines[at].ending }
                at += 1
            }
            cursor = at
        }
        for index in cursor..<lines.count { output += lines[index].body; output += lines[index].ending }
        return String(decoding: output, as: UTF8.self)
    }
}
