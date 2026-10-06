import Foundation

/// §1.16.4: what one computed state widget draws, worked out in one place so
/// the Mac panel reads the payload exactly as the phone does
/// (`mobile/src/lib/styles.ts` — `parseWidgets` and `progressBarDisplay`).
/// Pure: the widgets come from the cached reading, never from disk.
public enum StyleWidgetPresentation: Sendable, Equatable {
    /// `fraction` is how full the track is (0…1); `text` is `done/total`, or
    /// the bare count when the bar has no total.
    case progressBar(fraction: Double, text: String)
    /// At most `maximumListItems` non-empty lines.
    case list(items: [String])
    /// One line; empty when the source has nothing to say.
    case label(text: String)
    /// §1.17 (v6): background tasks, running ones first.
    case taskList(items: [Task])

    /// One background task as both platforms draw it: the description (or
    /// the kind when it has none), the kind and the status in words, and the
    /// times the elapsed text is worked out from against the viewer's clock.
    public struct Task: Sendable, Equatable {
        public var text: String
        public var kind: String
        public var kindTitle: String
        public var status: String
        public var statusTitle: String
        public var running: Bool
        public var startedAt: Date?
        public var endedAt: Date?

        /// `3분 12초` — to now while it runs, to its end once it ended.
        public func elapsed(now: Date) -> String {
            guard let startedAt else { return "" }
            return StyleWidgetPresentation.elapsed(from: startedAt, to: running ? now : (endedAt ?? now))
        }
    }

    /// The phone draws at most this many list lines, and so does the Mac.
    public static let maximumListItems = StyleStateEngine.maximumListItems
    /// The phone cuts a list line or a label at this many Unicode code points
    /// (`Array.from` there, `unicodeScalars` here), so both cut at one place.
    public static let maximumTextLength = 200

    /// Every widget in payload order. A bar with a negative count is dropped,
    /// as the phone drops it; everything else keeps its place, so an empty
    /// source still stands in the list (`0/0`, an empty list, an empty label).
    public static func make(_ widgets: [StylePanel.Widget]) -> [StyleWidgetPresentation] {
        widgets.compactMap(make)
    }

    public static func make(_ widget: StylePanel.Widget) -> StyleWidgetPresentation? {
        switch widget {
        case .progressBar(let value, let total):
            guard value >= 0 else { return nil }
            // A negative total is no total, as the phone reads it.
            guard let total, total >= 0 else { return .progressBar(fraction: 0, text: String(value)) }
            // More done than there are items never draws past a full bar.
            let done = min(value, total)
            let fraction = total > 0 ? Double(done) / Double(total) : 0
            return .progressBar(fraction: fraction, text: "\(done)/\(total)")
        case .list(let items):
            let lines = items.map(line).filter { !$0.isEmpty }
            return .list(items: Array(lines.prefix(maximumListItems)))
        case .label(let text):
            return .label(text: line(text))
        case .taskList(let items):
            return .taskList(items: items.prefix(StyleLimits.maximumTaskListItems).map(task))
        }
    }

    static func task(_ item: StylePanel.TaskItem) -> Task {
        let kind = ["agent", "shell"].contains(item.kind) ? item.kind : "other"
        let status = ["running", "completed", "failed", "stopped"].contains(item.status) ? item.status : "unknown"
        let kindTitle = taskKindTitle(kind)
        let text = line(item.text)
        return Task(text: text.isEmpty ? kindTitle : text, kind: kind, kindTitle: kindTitle, status: status,
                    statusTitle: taskStatusTitle(status), running: status == "running",
                    startedAt: AgentRunTiming.parseTimestamp(item.startedAt), endedAt: item.endedAt.flatMap(AgentRunTiming.parseTimestamp))
    }

    public static func taskKindTitle(_ kind: String) -> String {
        switch kind {
        case "agent": return L("styles.state.taskKind.agent")
        case "shell": return L("styles.state.taskKind.shell")
        default: return L("styles.state.taskKind.other")
        }
    }

    public static func taskStatusTitle(_ status: String) -> String {
        switch status {
        case "running": return L("styles.state.taskStatus.running")
        case "completed": return L("styles.state.taskStatus.completed")
        case "failed": return L("styles.state.taskStatus.failed")
        case "stopped": return L("styles.state.taskStatus.stopped")
        default: return L("styles.state.taskStatus.unknown")
        }
    }

    /// `45초`, `3분 12초`, `1시간 2분`; a clock that went back reads as 0.
    public static func elapsed(from start: Date, to end: Date) -> String {
        let seconds = max(0, Int(end.timeIntervalSince(start)))
        if seconds < 60 { return L("styles.state.elapsedSeconds", ["seconds": String(seconds)]) }
        if seconds < 3600 { return L("styles.state.elapsedMinutes", ["minutes": String(seconds / 60), "seconds": String(seconds % 60)]) }
        return L("styles.state.elapsedHours", ["hours": String(seconds / 3600), "minutes": String(seconds % 3600 / 60)])
    }

    /// Whether there is anything to draw: an empty bar still draws its track
    /// and `0/0`; an empty list or label draws nothing.
    public var isEmpty: Bool {
        switch self {
        case .progressBar: return false
        case .list(let items): return items.isEmpty
        case .label(let text): return text.isEmpty
        case .taskList(let items): return items.isEmpty
        }
    }

    /// One line as the phone's `inlineText` makes it: every stripped
    /// character removed wherever it stands (not only at the ends), then
    /// trimmed, then cut to `maximumTextLength` code points.
    static func line(_ text: String) -> String {
        var kept = String.UnicodeScalarView()
        kept.append(contentsOf: text.unicodeScalars.filter { !isStrippedFromLine($0) })
        let trimmed = String(kept).trimmingCharacters(in: .whitespacesAndNewlines)
        return String(String.UnicodeScalarView(trimmed.unicodeScalars.prefix(maximumTextLength)))
    }

    /// The phone's `UNSAFE_INLINE`: §1.11's banned set (`StyleText`) less
    /// U+200D (ZWJ), which joins a multi-scalar emoji into one glyph and so
    /// may stand on one line. Newline and tab are C0 controls: a line never
    /// breaks, it closes up.
    static func isStrippedFromLine(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value != 0x200D && StyleText.isBanned(scalar)
    }
}
