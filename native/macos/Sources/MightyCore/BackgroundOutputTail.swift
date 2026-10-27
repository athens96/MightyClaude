import Foundation

/// The lines a host shows for a background run: the end of its output file
/// (native/contracts/fixtures/background-output.json).
public enum BackgroundOutputTail {
    public static let maxBytes = 8192
    public static let maxLines = 6
    public static let maxLineChars = 200

    public static func lines(_ data: Data, truncated: Bool) -> [String] {
        let text = String(decoding: data, as: UTF8.self)
        // ICU spells a code point \x{…}: CSI sequences, OSC strings (ended by BEL or ST), then the other two-byte escapes.
        let clean = text.replacingOccurrences(of: #"\x{1B}\[[0-9;?]*[ -/]*[@-~]|\x{1B}\][^\x{07}\x{1B}]*(\x{07}|\x{1B}\\)|\x{1B}[@-Z\\-_]"#, with: "", options: .regularExpression)
        var rows = clean.components(separatedBy: "\n").map { row -> String in
            let line = row.hasSuffix("\r") ? String(row.dropLast()) : row
            return line.components(separatedBy: "\r").last ?? ""
        }
        if truncated, !rows.isEmpty { rows.removeFirst() }
        while let last = rows.last, last.isEmpty { rows.removeLast() }
        // The cut counts Unicode scalars, as Windows and the phone do (not grapheme clusters, not UTF-16 units).
        return rows.suffix(maxLines).map { row in
            row.unicodeScalars.count > maxLineChars ? String(String.UnicodeScalarView(row.unicodeScalars.prefix(maxLineChars))) + "…" : row
        }
    }

    /// nil when the file cannot be read (missing, or on another computer).
    public static func read(path: String) -> [String]? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd() else { return nil }
        let start = size > UInt64(maxBytes) ? size - UInt64(maxBytes) : 0
        // At most maxBytes, even when the file grows while it is read.
        guard (try? handle.seek(toOffset: start)) != nil, let data = try? handle.read(upToCount: maxBytes) ?? Data() else { return nil }
        return lines(data, truncated: start > 0)
    }
}
