import MightyCore
import SwiftUI

/// The 변경사항 view of a source preview (Windows web DiffView.tsx, the phone's
/// DiffView): a caption naming the base with the counts, the truncated note,
/// then a unified diff in the code font: per hunk its `@@` head, then its lines
/// with the old and new line numbers in two gutters, added lines on the done
/// wash and removed ones on the error wash.
struct FileDiffView: View {
    let changes: FileChangeSet
    /// Room under the last row (the bubble's corner grip).
    var bottomClearance: CGFloat = 0
    /// The change review: a click on a line with a number picks it for a note; nil keeps the rows inert.
    var onLine: ((Row) -> Void)? = nil
    /// Rows with a note on them (marked in the gutter) and the row picked now.
    var notedRows: Set<Int> = []
    var pickedRow: Int? = nil

    /// One drawn row: a hunk head, a context, added or removed line with its
    /// line numbers (nil on the side it is not on), or the "no newline at end
    /// of file" mark under the line it belongs to.
    struct Row: Identifiable, Equatable {
        enum Kind: Equatable { case hunk, context, added, removed, noNewline }
        let id: Int
        let kind: Kind
        let old: Int?
        let new: Int?
        let text: String
    }

    /// The rows hunk after hunk, each hunk numbered from its own starts (a side
    /// with no lines starts at the line before it, so it numbers nothing).
    static func rows(_ changes: FileChangeSet) -> [Row] {
        var rows: [Row] = []
        for hunk in changes.hunks {
            rows.append(Row(id: rows.count, kind: .hunk, old: nil, new: nil, text: "@@ -\(hunk.oldStart),\(hunk.oldLines) +\(hunk.newStart),\(hunk.newLines) @@"))
            var old = hunk.oldStart, new = hunk.newStart
            for line in hunk.lines {
                switch line.op {
                case "-": rows.append(Row(id: rows.count, kind: .removed, old: old, new: nil, text: line.text)); old += 1
                case "+": rows.append(Row(id: rows.count, kind: .added, old: nil, new: new, text: line.text)); new += 1
                default: rows.append(Row(id: rows.count, kind: .context, old: old, new: new, text: line.text)); old += 1; new += 1
                }
                if line.noNewline == true { rows.append(Row(id: rows.count, kind: .noNewline, old: nil, new: nil, text: "")) }
            }
        }
        return rows
    }

    private var caption: String {
        if changes.created == true { return L("files.changes.created") }
        if changes.base == "run" { return L("files.changes.baseRun") }
        if let sha = changes.headSha { return L("files.changes.baseHead", ["sha": sha]) }
        return L("files.changes.baseHeadPlain")
    }

    var body: some View {
        let rows = Self.rows(changes)
        let digits = String(rows.reduce(0) { max($0, $1.old ?? 0, $1.new ?? 0) }).count
        VStack(spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(caption).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.tail)
                Spacer(minLength: 0)
                Text(verbatim: "+\(changes.added)").foregroundStyle(Palette.doneText)
                Text(verbatim: "−\(changes.removed)").foregroundStyle(Palette.errText)
            }
            .font(.ui(size: 10)).monospacedDigit()
            .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.xs)
            .accessibilityElement(children: .combine)
            Divider()
            if changes.truncated {
                Text(L("files.changes.truncated")).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
                    .background(Palette.waitSoft)
            }
            ScrollView([.vertical, .horizontal]) {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(rows) { row in line(row, digits: digits) }
                }
                .textSelection(.enabled)
                .padding(.vertical, DesignMetrics.Spacing.xs).padding(.bottom, bottomClearance)
            }
        }
    }

    private func line(_ row: Row, digits: Int) -> some View {
        let tinted = row.kind == .added ? Palette.doneText : row.kind == .removed ? Palette.errText : Palette.ink2
        return HStack(spacing: 0) {
            Text(verbatim: Self.gutter(row.old, digits: digits)).foregroundStyle(tinted)
                .padding(.horizontal, DesignMetrics.Spacing.xs)
            Text(verbatim: Self.gutter(row.new, digits: digits)).foregroundStyle(tinted)
                .padding(.horizontal, DesignMetrics.Spacing.xs)
            Text(verbatim: row.kind == .added ? "+ " : row.kind == .removed ? "− " : "  ").foregroundStyle(tinted)
            Group {
                if row.kind == .noNewline {
                    Text(verbatim: "\\ " + L("files.changes.noNewline")).italic().foregroundStyle(Palette.ink2)
                } else {
                    Text(verbatim: row.text).foregroundStyle(row.kind == .hunk ? Palette.ink2 : Palette.ink)
                }
            }
            .fixedSize(horizontal: true, vertical: false)
            .padding(.trailing, DesignMetrics.Spacing.md)
        }
        .font(.code(size: 11))
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(row.kind == .added ? Palette.doneSoft : row.kind == .removed ? Palette.errSoft : row.kind == .hunk ? Palette.raised : Color.clear)
        .overlay(alignment: .leading) {
            if notedRows.contains(row.id) || pickedRow == row.id {
                Rectangle().fill(Palette.accent).frame(width: pickedRow == row.id ? 3 : 2)
            }
        }
        .modifier(LinePick(action: onLine.flatMap { pick in row.old != nil || row.new != nil ? { pick(row) } : nil }))
    }

    /// A tap that picks the row, only in the change review: elsewhere the rows keep plain text selection.
    private struct LinePick: ViewModifier {
        let action: (() -> Void)?
        func body(content: Content) -> some View {
            if let action { content.contentShape(Rectangle()).onTapGesture(perform: action) } else { content }
        }
    }

    /// A line number right-aligned in the code font's cells, blank on the side
    /// the row is not on.
    private static func gutter(_ number: Int?, digits: Int) -> String {
        let text = number.map(String.init) ?? ""
        return String(repeating: " ", count: max(0, digits - text.count)) + text
    }
}
