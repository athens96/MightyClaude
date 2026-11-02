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
/// when the tool made the file, and the `patch` the tool applied (with no
/// original, to be undone against the file on disk; with one, to follow the
/// run's later edits of the file). No original, not created and no patch: the
/// original is unknown.
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

    private static let byteOrderMark: [UInt8] = [0xEF, 0xBB, 0xBF]

    /// A text without the byte order mark it may start with (the files are read without one).
    public static func withoutByteOrderMark(_ text: String) -> String {
        text.hasPrefix("\u{FEFF}") ? String(text.unicodeScalars.dropFirst()) : text
    }

    /// The same hunks the other way round: undoing them on the file before the patch gives the file after it.
    public static func inverted(_ hunks: [FilePatchHunk]) -> [FilePatchHunk] {
        hunks.map { hunk in
            FilePatchHunk(oldStart: hunk.newStart, oldLines: hunk.newLines, newStart: hunk.oldStart, newLines: hunk.oldLines,
                          lines: hunk.lines.map { line -> String in
                              if line.hasPrefix("+") { return "-" + line.dropFirst() }
                              if line.hasPrefix("-") { return "+" + line.dropFirst() }
                              return line
                          })
        }
    }

    /// A `\` line right after a line of a side (not first, not after another marker).
    private static func marker(_ lines: [String], _ index: Int) -> Bool {
        guard index > 0, lines[index].utf8.first == 92, let previous = lines[index - 1].utf8.first else { return false }
        return [32, 43, 45].contains(previous)
    }

    /// A patch line's bytes after its first character, a trailing CR aside;
    /// line 1's without the byte order mark the file is read past.
    private static func text(_ line: String, number: Int) -> [UInt8] {
        var bytes = Array(line.utf8.dropFirst())
        if bytes.last == 13 { bytes.removeLast() }
        if number == 1, bytes.starts(with: byteOrderMark) { bytes.removeFirst(3) }
        return bytes
    }

    /// The file before the patch, from `current` (the file after it). Each
    /// hunk's new side (its context and added lines) must stand exactly at
    /// its `newStart`, in order, a trailing CR aside, and its old side must
    /// then start at its `oldStart` in the result. A removed line comes back
    /// with the line break of the hunk's nearest context line (before it on a
    /// tie; with none, the file's: CRLF when it has one). A `\` line (jsdiff's
    /// "No newline at end of file") after a removed line restores it without a
    /// break; after a context or added line, that line on disk must have none.
    /// A byte order mark before line 1 is read past, on disk and in the patch,
    /// and kept. A count that disagrees with the lines, a line without a break
    /// before the end, or any mismatch: nil. Read on UTF-8 bytes, as Windows
    /// reads code units.
    public static func reverseApply(_ current: String, hunks: [FilePatchHunk]) -> String? {
        var bytes = Array(current.utf8)
        let mark = bytes.starts(with: byteOrderMark)
        if mark { bytes.removeFirst(3) }
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
        var output: [UInt8] = mark ? byteOrderMark : []
        output.reserveCapacity(bytes.count + 3)
        var written = 0, closed = false
        // Only a file's last line goes without a break.
        func emit(_ body: ArraySlice<UInt8>, _ end: [UInt8]) -> Bool {
            guard !closed else { return false }
            output += body; output += end; written += 1; closed = end.isEmpty
            return true
        }
        var cursor = 0
        for hunk in hunks {
            let count = hunk.lines.count
            var newSide = 0, oldSide = 0
            for index in 0..<count {
                if marker(hunk.lines, index) { continue }
                guard let first = hunk.lines[index].utf8.first, [32, 43, 45].contains(first) else { return nil }
                if first != 45 { newSide += 1 }
                if first != 43 { oldSide += 1 }
            }
            guard newSide == hunk.newLines, oldSide == hunk.oldLines else { return nil }
            // A hunk with no new lines has no context either: the file was emptied, so it stands at the end of what is left.
            let at = hunk.newLines == 0 ? min(hunk.newStart, lines.count) : hunk.newStart - 1
            guard at >= cursor, at + hunk.newLines <= lines.count else { return nil }
            for index in cursor..<at { guard emit(lines[index].body, lines[index].ending) else { return nil } }
            // The old side starts at oldStart (with no old lines: after line oldStart, or at the top written as 0 or 1).
            guard hunk.oldLines > 0 ? written == hunk.oldStart - 1 : (written == hunk.oldStart || written == hunk.oldStart - 1) else { return nil }
            // Where each new-side line stands on disk, and the nearest context line (with a break) before and after each line.
            let kinds = hunk.lines.map { $0.utf8.first ?? 0 }
            var disk = [Int](repeating: -1, count: count), before = [Int](repeating: -1, count: count), after = [Int](repeating: -1, count: count)
            var position = at, seen = -1
            for index in 0..<count {
                if kinds[index] == 32 || kinds[index] == 43 { disk[index] = position; position += 1 }
                before[index] = seen
                if kinds[index] == 32, !lines[disk[index]].ending.isEmpty { seen = index }
            }
            seen = -1
            for index in stride(from: count - 1, through: 0, by: -1) {
                after[index] = seen
                if kinds[index] == 32, !lines[disk[index]].ending.isEmpty { seen = index }
            }
            var oldNumber = hunk.oldStart
            for index in 0..<count where kinds[index] != 92 {
                let bare = index + 1 < count && marker(hunk.lines, index + 1)
                if kinds[index] == 45 {
                    let near = before[index] >= 0 && (after[index] < 0 || index - before[index] <= after[index] - index) ? before[index] : after[index]
                    let restored = text(hunk.lines[index], number: oldNumber)
                    guard emit(restored[...], bare ? [] : near >= 0 ? lines[disk[near]].ending : ending) else { return nil }
                    oldNumber += 1
                    continue
                }
                let line = lines[disk[index]]
                guard Array(line.body) == text(hunk.lines[index], number: disk[index] + 1), !(bare && !line.ending.isEmpty) else { return nil }
                if kinds[index] == 32 {
                    guard emit(line.body, line.ending) else { return nil }
                    oldNumber += 1
                }
            }
            cursor = at + hunk.newLines
        }
        for index in cursor..<lines.count { guard emit(lines[index].body, lines[index].ending) else { return nil } }
        return String(decoding: output, as: UTF8.self)
    }
}
