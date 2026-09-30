import Foundation

/// A coloured span in a source preview, in UTF-16 offsets so the view can
/// apply it to an NSAttributedString directly.
public struct SourceToken: Equatable, Sendable {
    public enum Kind: String, Sendable { case keyword, string, comment, number }
    public let kind: Kind
    public let location: Int
    public let length: Int
    public init(kind: Kind, location: Int, length: Int) { self.kind = kind; self.location = location; self.length = length }
}

/// Where lines start in a text, counted the way NSTextView breaks them.
public enum SourceLines {
    /// Longer lines make the source view wrap instead of scrolling sideways.
    public static let wrapThreshold = 5_000

    /// The UTF-16 offset of each line start and the longest line's length in
    /// UTF-16 units. A line ends at \n, \r, \r\n (one break), U+2028 or U+2029.
    public static func scan(_ text: String) -> (starts: [Int], longest: Int) {
        var starts = [0], longest = 0, offset = 0, lineStart = 0, previous: UInt16 = 0
        for unit in text.utf16 {
            offset += 1
            switch unit {
            case 0x0A where previous == 0x0D:
                // \r\n: the \r already ended the line; move its start past the \n.
                starts[starts.count - 1] = offset
                lineStart = offset
            case 0x0A, 0x0D, 0x2028, 0x2029:
                longest = max(longest, offset - 1 - lineStart)
                starts.append(offset)
                lineStart = offset
            default:
                break
            }
            previous = unit
        }
        return (starts, max(longest, offset - lineStart))
    }
}

/// A small, single-pass highlighter for the files pane: comments, strings,
/// numbers and keywords. It never fails; text it does not understand stays
/// plain. Only the first `maximumUnits` UTF-16 units (about 400,000
/// characters) are scanned; the preview says so for longer text.
public enum SourceHighlighter {
    public static let maximumUnits = 400_000

    struct Rules {
        var lineComments: [String] = []
        var blockComment: (open: String, close: String)?
        /// Quote characters; a string ends at the line end unless multi-line.
        var quotes: [Character] = ["\"", "'"]
        var multilineQuotes: [Character] = []
        var tripleQuotes = false
        var keywords: Set<String> = []
        var caseInsensitive = false
    }

    public static func tokens(_ text: String, language: SourceLanguage, maximumUnits: Int = maximumUnits) -> [SourceToken] {
        guard language != .plain else { return [] }
        let rules = rules(for: language)
        let units = Array(text.utf16.prefix(maximumUnits))
        let count = units.count
        let lineComments = rules.lineComments.map { Array($0.utf16) }
        let block = rules.blockComment.map { (Array($0.open.utf16), Array($0.close.utf16)) }
        let quotes = Set(rules.quotes.compactMap { $0.utf16.first })
        let multiline = Set(rules.multilineQuotes.compactMap { $0.utf16.first })
        let newline = UInt16(0x0A), backslash = UInt16(0x5C), doubleQuote = UInt16(0x22)
        var result: [SourceToken] = []
        var index = 0

        func matches(_ pattern: [UInt16], at position: Int) -> Bool {
            guard position + pattern.count <= count else { return false }
            for offset in pattern.indices where units[position + offset] != pattern[offset] { return false }
            return true
        }
        func find(_ pattern: [UInt16], from position: Int) -> Int? {
            var cursor = position
            while cursor + pattern.count <= count {
                if matches(pattern, at: cursor) { return cursor }
                cursor += 1
            }
            return nil
        }
        func lineEnd(from position: Int) -> Int {
            var cursor = position
            while cursor < count, units[cursor] != newline { cursor += 1 }
            return cursor
        }
        func add(_ kind: SourceToken.Kind, _ start: Int, _ end: Int) {
            if end > start { result.append(SourceToken(kind: kind, location: start, length: end - start)) }
        }

        scan: while index < count {
            let unit = units[index]
            if let (open, close) = block, matches(open, at: index) {
                let end = find(close, from: index + open.count).map { $0 + close.count } ?? count
                add(.comment, index, end); index = end; continue
            }
            for marker in lineComments where matches(marker, at: index) {
                // "#" starts a comment only at a word boundary ("$#", "a#b" do not).
                if marker == [0x23], index > 0, !isSpace(units[index - 1]) { break }
                let end = lineEnd(from: index)
                add(.comment, index, end); index = end; continue scan
            }
            if rules.tripleQuotes, unit == doubleQuote, matches([doubleQuote, doubleQuote, doubleQuote], at: index) {
                let end = find([doubleQuote, doubleQuote, doubleQuote], from: index + 3).map { $0 + 3 } ?? count
                add(.string, index, end); index = end; continue
            }
            if quotes.contains(unit) {
                var cursor = index + 1
                while cursor < count {
                    let current = units[cursor]
                    if current == backslash { cursor += 2; continue }
                    if current == unit { cursor += 1; break }
                    if current == newline, !multiline.contains(unit) { break }
                    cursor += 1
                }
                let end = min(cursor, count)
                add(.string, index, end); index = end; continue
            }
            if isDigit(unit), index == 0 || !isIdentifier(units[index - 1]) {
                var cursor = index + 1
                while cursor < count, isIdentifier(units[cursor]) || (units[cursor] == 0x2E && cursor + 1 < count && isDigit(units[cursor + 1])) { cursor += 1 }
                add(.number, index, cursor); index = cursor; continue
            }
            if isIdentifierStart(unit) {
                var cursor = index + 1
                while cursor < count, isIdentifier(units[cursor]) { cursor += 1 }
                let word = String(decoding: units[index..<cursor], as: UTF16.self)
                if rules.keywords.contains(rules.caseInsensitive ? word.lowercased() : word) { add(.keyword, index, cursor) }
                index = cursor; continue
            }
            index += 1
        }
        return result
    }

    private static func isDigit(_ unit: UInt16) -> Bool { unit >= 0x30 && unit <= 0x39 }
    private static func isIdentifierStart(_ unit: UInt16) -> Bool { (unit >= 0x41 && unit <= 0x5A) || (unit >= 0x61 && unit <= 0x7A) || unit == 0x5F }
    private static func isIdentifier(_ unit: UInt16) -> Bool { isIdentifierStart(unit) || isDigit(unit) }
    private static func isSpace(_ unit: UInt16) -> Bool { unit == 0x20 || unit == 0x09 || unit == 0x0A || unit == 0x0D }

    private static func words(_ text: String) -> Set<String> { Set(text.split(separator: " ").map(String.init)) }

    static func rules(for language: SourceLanguage) -> Rules {
        let slash = ["//"], cBlock = ("/*", "*/")
        switch language {
        case .swift:
            return Rules(lineComments: slash, blockComment: cBlock, quotes: ["\""], tripleQuotes: true, keywords: words(
                "actor as associatedtype async await break case catch class continue default defer deinit do else enum extension fallthrough false fileprivate final for func guard if import in init inout internal is lazy let mutating nil nonisolated open operator override private protocol public repeat rethrows return self Self some static struct subscript super switch throw throws true try typealias var weak where while any"))
        case .c:
            return Rules(lineComments: slash, blockComment: cBlock, keywords: words(
                "auto bool break case char class const constexpr continue default delete do double else enum explicit extern false float for friend goto if inline int long namespace new nullptr operator private protected public register return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while NULL nil YES NO self super id"))
        case .javascript:
            return Rules(lineComments: slash, blockComment: cBlock, quotes: ["\"", "'", "`"], multilineQuotes: ["`"], keywords: words(
                "abstract as async await break case catch class const continue debugger declare default delete do else enum export extends false finally for from function get if implements import in instanceof interface keyof let new null of private protected public readonly return set static super switch this throw true try type typeof undefined var void while yield"))
        case .python:
            return Rules(lineComments: ["#"], tripleQuotes: true, keywords: words(
                "False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return self try while with yield match case"))
        case .kotlin, .gradle:
            return Rules(lineComments: slash, blockComment: cBlock, tripleQuotes: true, keywords: words(
                "abstract annotation as break by catch class companion const constructor continue data do else enum false final finally for fun if import in init inline interface internal is lateinit null object open operator override package private protected public return sealed super suspend this throw true try typealias val var when while def apply plugins dependencies implementation"))
        case .java:
            return Rules(lineComments: slash, blockComment: cBlock, tripleQuotes: true, keywords: words(
                "abstract boolean break byte case catch char class const continue default do double else enum extends false final finally float for if implements import instanceof int interface long native new null package private protected public record return short static super switch synchronized this throw throws transient true try var void volatile while"))
        case .go:
            return Rules(lineComments: slash, blockComment: cBlock, quotes: ["\"", "'", "`"], multilineQuotes: ["`"], keywords: words(
                "break case chan const continue default defer else fallthrough false for func go goto if import interface iota map nil package range return select struct switch true type var"))
        case .rust:
            // '\'' is also a lifetime marker, so only double-quoted strings.
            return Rules(lineComments: slash, blockComment: cBlock, quotes: ["\""], multilineQuotes: ["\""], keywords: words(
                "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while"))
        case .shell, .dockerfile, .makefile:
            var keywords = words("if then else elif fi for while until do done case esac in function return export local readonly source echo exit set unset")
            if language == .dockerfile { keywords.formUnion(words("from run cmd label expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell as")) }
            if language == .makefile { keywords.formUnion(words("ifeq ifneq ifdef ifndef endif include define endef override")) }
            return Rules(lineComments: ["#"], quotes: ["\"", "'"], multilineQuotes: ["\"", "'"], keywords: keywords, caseInsensitive: language == .dockerfile)
        case .json:
            return Rules(quotes: ["\""], keywords: words("true false null"))
        case .yaml:
            return Rules(lineComments: ["#"], keywords: words("true false null yes no on off"))
        case .toml:
            return Rules(lineComments: ["#"], tripleQuotes: true, keywords: words("true false"))
        case .xml, .html:
            return Rules(blockComment: ("<!--", "-->"), quotes: ["\""])
        case .css:
            return Rules(blockComment: cBlock, keywords: words("important inherit initial unset none auto"))
        case .sql:
            return Rules(lineComments: ["--"], blockComment: cBlock, quotes: ["'", "\""], keywords: words(
                "add all alter and as asc begin between by case check column commit constraint create database default delete desc distinct drop else end exists foreign from group having if in index inner insert into is join key left like limit not null on or order outer primary references right rollback select set table then union unique update values view when where with"), caseInsensitive: true)
        case .plain:
            return Rules(quotes: [])
        }
    }
}
