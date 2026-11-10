import Foundation

/// One line of a hunk: `" "` context, `"+"` added, `"-"` removed. `noNewline`
/// marks the file's last line when it has no line break.
public struct FileDiffLine: Codable, Equatable, Sendable {
    public var op: String
    public var text: String
    public var noNewline: Bool?
    public init(op: String, text: String, noNewline: Bool? = nil) { self.op = op; self.text = text; self.noNewline = noNewline }
}

/// A unified-diff hunk. A side with no lines starts at the line before it
/// (0 at the top of the file), as git writes it.
public struct FileDiffHunk: Codable, Equatable, Sendable {
    public var oldStart: Int
    public var oldLines: Int
    public var newStart: Int
    public var newLines: Int
    public var lines: [FileDiffLine]
}

/// What `FileDiff.compute` found. `added` and `removed` count the whole
/// change even when `truncated` cut the hunks; `unchanged` is true (and the
/// hunks empty) when both sides are the same text once CRLF reads as LF.
public struct FileDiffResult: Equatable, Sendable {
    public var hunks: [FileDiffHunk]
    public var added: Int
    public var removed: Int
    public var truncated: Bool
    public var unchanged: Bool
}

/// The line diff of the source preview's 변경사항 view (Windows
/// `MightyCode.Core/FileDiff.cs`, native/contracts/fixtures/file-diff.json):
/// CRLF compared as LF; lines compared by their exact UTF-8 bytes (no Unicode
/// normalization, unlike `String ==`) and their line break; Myers' shortest
/// edit script after the common start and end are set aside, each run of
/// changes written removals first; 3 lines of context, hunks whose changes
/// stand at most 6 equal lines apart merged. Each side is at most 1 MiB
/// (UTF-8) and 20,000 lines and holds no NUL, else there is no diff (nil); an
/// edit script longer than `maximumEditDistance` reads as the whole middle
/// replaced. At most 5,000 hunk lines are written, then `truncated`.
public enum FileDiff {
    public static let context = 3
    public static let maximumSideBytes = 1_048_576
    public static let maximumSideLines = 20_000
    public static let maximumOutputLines = 5_000
    /// Past this many edits Myers' trace (its square in memory) is not kept:
    /// the middle is shown as removed then added.
    public static let maximumEditDistance = 2_000

    /// A line as compared: its UTF-8 bytes and whether a line break ends it.
    struct Line: Hashable {
        let bytes: [UInt8]
        let terminated: Bool
        var text: String { String(decoding: bytes, as: UTF8.self) }
    }

    /// The lines of `text` after CRLF becomes LF; a final line break opens no
    /// further line. Read on UTF-8 bytes: Swift keeps "\r\n" as one Character.
    static func lines(_ text: String) -> [Line] {
        let bytes = Array(text.utf8)
        var normalized: [UInt8] = []
        normalized.reserveCapacity(bytes.count)
        var index = 0
        while index < bytes.count {
            if bytes[index] == 13, index + 1 < bytes.count, bytes[index + 1] == 10 { normalized.append(10); index += 2; continue }
            normalized.append(bytes[index]); index += 1
        }
        var lines: [Line] = []
        var start = 0
        while start < normalized.count {
            guard let end = normalized[start...].firstIndex(of: 10) else {
                lines.append(Line(bytes: Array(normalized[start...]), terminated: false)); break
            }
            lines.append(Line(bytes: Array(normalized[start..<end]), terminated: true))
            start = end + 1
        }
        return lines
    }

    static func fits(_ text: String) -> Bool { text.utf8.count <= maximumSideBytes && !text.utf8.contains(0) }

    /// The diff of `old` to `new`, or nil when either side is over the caps or holds a NUL.
    public static func compute(old oldText: String, new newText: String) -> FileDiffResult? {
        guard fits(oldText), fits(newText) else { return nil }
        let a = lines(oldText), b = lines(newText)
        guard a.count <= maximumSideLines, b.count <= maximumSideLines else { return nil }
        // Every distinct line (bytes and break) gets a number; Myers compares numbers.
        var ids: [Line: Int] = [:]
        func id(_ line: Line) -> Int {
            if let known = ids[line] { return known }
            let value = ids.count; ids[line] = value; return value
        }
        let x = a.map(id), y = b.map(id)
        if x == y { return FileDiffResult(hunks: [], added: 0, removed: 0, truncated: false, unchanged: true) }
        let ops = script(x, y)
        // Removals before additions in each run of changes.
        var ordered: [Op] = []
        ordered.reserveCapacity(ops.count)
        var i = 0
        while i < ops.count {
            if ops[i].op == " " { ordered.append(ops[i]); i += 1; continue }
            var end = i
            while end < ops.count, ops[end].op != " " { end += 1 }
            ordered += ops[i..<end].filter { $0.op == "-" }
            ordered += ops[i..<end].filter { $0.op == "+" }
            i = end
        }
        let added = ordered.filter { $0.op == "+" }.count, removed = ordered.filter { $0.op == "-" }.count
        // Change runs grouped into hunks: a gap of at most 2 × context equal lines joins two.
        var groups: [(start: Int, end: Int)] = []
        i = 0
        while i < ordered.count {
            if ordered[i].op == " " { i += 1; continue }
            var end = i
            while end + 1 < ordered.count, ordered[end + 1].op != " " { end += 1 }
            if let last = groups.last, i - last.end - 1 <= 2 * context { groups[groups.count - 1] = (last.start, end) }
            else { groups.append((i, end)) }
            i = end + 1
        }
        var hunks: [FileDiffHunk] = []
        var written = 0, truncated = false
        // Lines of each side before the next hunk, carried from one hunk to the next.
        var scanned = 0, oldBefore = 0, newBefore = 0
        for group in groups {
            let start = max(0, group.start - context), stop = min(ordered.count - 1, group.end + context)
            var count = stop - start + 1
            if written + count > maximumOutputLines { count = maximumOutputLines - written; truncated = true }
            if count <= 0 { break }
            while scanned < start {
                if ordered[scanned].op != "+" { oldBefore += 1 }
                if ordered[scanned].op != "-" { newBefore += 1 }
                scanned += 1
            }
            var lines: [FileDiffLine] = []
            lines.reserveCapacity(count)
            var oldLines = 0, newLines = 0
            for op in ordered[start..<(start + count)] {
                let line = op.op == "+" ? b[op.new] : a[op.old]
                if op.op != "+" { oldLines += 1 }
                if op.op != "-" { newLines += 1 }
                lines.append(FileDiffLine(op: String(op.op), text: line.text, noNewline: line.terminated ? nil : true))
            }
            hunks.append(FileDiffHunk(oldStart: oldLines == 0 ? oldBefore : oldBefore + 1, oldLines: oldLines,
                                      newStart: newLines == 0 ? newBefore : newBefore + 1, newLines: newLines, lines: lines))
            written += count
            if truncated { break }
        }
        return FileDiffResult(hunks: hunks, added: added, removed: removed, truncated: truncated, unchanged: false)
    }

    /// The first `lines` hunk lines of `hunks`, a hunk cut short counting only
    /// the lines it keeps (a reply too big for its frame).
    public static func cut(_ hunks: [FileDiffHunk], lines: Int) -> [FileDiffHunk] {
        var left = lines
        var kept: [FileDiffHunk] = []
        for hunk in hunks {
            guard left > 0 else { break }
            if hunk.lines.count <= left { kept.append(hunk); left -= hunk.lines.count; continue }
            let part = Array(hunk.lines.prefix(left))
            let oldLines = part.filter { $0.op != "+" }.count, newLines = part.filter { $0.op != "-" }.count
            // A start of a side with no lines is the line before it, as for a whole hunk.
            let oldStart = hunk.oldLines == 0 ? hunk.oldStart : hunk.oldStart - 1, newStart = hunk.newLines == 0 ? hunk.newStart : hunk.newStart - 1
            kept.append(FileDiffHunk(oldStart: oldLines == 0 ? oldStart : oldStart + 1, oldLines: oldLines,
                                     newStart: newLines == 0 ? newStart : newStart + 1, newLines: newLines, lines: part))
            break
        }
        return kept
    }

    /// One step of the edit script: `" "` (both indexes), `"-"` (old index), `"+"` (new index).
    struct Op { let op: Character; let old: Int; let new: Int }

    static func script(_ a: [Int], _ b: [Int]) -> [Op] {
        var prefix = 0
        while prefix < a.count, prefix < b.count, a[prefix] == b[prefix] { prefix += 1 }
        var suffix = 0
        while suffix < a.count - prefix, suffix < b.count - prefix, a[a.count - 1 - suffix] == b[b.count - 1 - suffix] { suffix += 1 }
        var ops: [Op] = []
        ops.reserveCapacity(a.count + b.count)
        for i in 0..<prefix { ops.append(Op(op: " ", old: i, new: i)) }
        ops += middle(a, aStart: prefix, aEnd: a.count - suffix, b, bStart: prefix, bEnd: b.count - suffix)
        for i in stride(from: suffix, to: 0, by: -1) { ops.append(Op(op: " ", old: a.count - i, new: b.count - i)) }
        return ops
    }

    /// The furthest x reachable on diagonal k at step d from step d-1's row
    /// (`previous`, indexed k + d - 1, -1 where unreached): down from k+1 (an
    /// addition) or right from k-1 (a removal), the one that reaches further,
    /// down on a tie; never off the grid. nil when neither move is possible.
    static func step(_ previous: [Int], d: Int, k: Int, n: Int, m: Int) -> (x: Int, down: Bool)? {
        var down: Int?, right: Int?
        if k + 1 <= d - 1 { let above = previous[k + 1 + d - 1]; if above >= 0, above - k <= m { down = above } }
        if k - 1 >= -(d - 1) { let left = previous[k - 1 + d - 1]; if left >= 0, left + 1 <= n { right = left + 1 } }
        switch (down, right) {
        case (nil, nil): return nil
        case (nil, let r?): return (r, false)
        case (let dd?, nil): return (dd, true)
        case (let dd?, let r?): return r > dd ? (r, false) : (dd, true)
        }
    }

    static func middle(_ a: [Int], aStart: Int, aEnd: Int, _ b: [Int], bStart: Int, bEnd: Int) -> [Op] {
        let n = aEnd - aStart, m = bEnd - bStart
        func replaced() -> [Op] {
            (0..<n).map { Op(op: "-", old: aStart + $0, new: -1) } + (0..<m).map { Op(op: "+", old: -1, new: bStart + $0) }
        }
        if n == 0 || m == 0 { return replaced() }
        var trace: [[Int]] = []
        var endD = -1, endK = 0
        var d = 0
        while d <= min(n + m, maximumEditDistance), endD < 0 {
            var row = [Int](repeating: -1, count: 2 * d + 1)
            var k = -d
            while k <= d {
                defer { k += 2 }
                var x: Int
                if d == 0 { x = 0 }
                else if let next = step(trace[d - 1], d: d, k: k, n: n, m: m) { x = next.x }
                else { continue }
                var y = x - k
                if y < 0 || y > m { continue }
                while x < n, y < m, a[aStart + x] == b[bStart + y] { x += 1; y += 1 }
                row[k + d] = x
                if x == n, y == m { endD = d; endK = k; break }
            }
            trace.append(row)
            d += 1
        }
        if endD < 0 { return replaced() }
        // Walk back from (n, m): each step is a snake of equal lines after one edit.
        var reversed: [Op] = []
        reversed.reserveCapacity(n + m)
        var cx = n, cy = m, ck = endK
        var back = endD
        while back > 0 {
            let down = step(trace[back - 1], d: back, k: ck, n: n, m: m)!.down
            let pk = down ? ck + 1 : ck - 1
            let px = trace[back - 1][pk + back - 1], py = px - pk
            let sx = down ? px : px + 1, sy = down ? py + 1 : py
            while cx > sx, cy > sy { cx -= 1; cy -= 1; reversed.append(Op(op: " ", old: aStart + cx, new: bStart + cy)) }
            reversed.append(down ? Op(op: "+", old: -1, new: bStart + py) : Op(op: "-", old: aStart + px, new: -1))
            cx = px; cy = py; ck = pk
            back -= 1
        }
        while cx > 0, cy > 0 { cx -= 1; cy -= 1; reversed.append(Op(op: " ", old: aStart + cx, new: bStart + cy)) }
        return reversed.reversed()
    }
}
