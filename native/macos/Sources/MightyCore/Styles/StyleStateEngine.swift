import Foundation

/// §1.16: what one declared file source read, in declaration order. The state
/// drives `stateOverrides`; the widget is what the panel draws for the source.
public struct StyleFileReading: Sendable, Equatable, Hashable {
    public var state: StyleFileSourceState
    public var widget: StylePanel.Widget
    public init(state: StyleFileSourceState, widget: StylePanel.Widget) { self.state = state; self.widget = widget }
}

/// §1.16: everything the declared sources produced for one pane — the phase
/// signals keyed by file-source index and one widget per source (files first,
/// then run events, each in declaration order).
public struct StyleStateReading: Sendable, Equatable, Hashable {
    public var fileSourceStates: [Int: StyleFileSourceState]
    public var widgets: [StylePanel.Widget]
    public init(fileSourceStates: [Int: StyleFileSourceState] = [:], widgets: [StylePanel.Widget] = []) {
        self.fileSourceStates = fileSourceStates; self.widgets = widgets
    }
    public static let empty = StyleStateReading()
}

/// §1.16: the closed state-reading engine. It knows the two parsers, the two
/// aggregates, the three event kinds and the three widgets, and nothing else:
/// a manifest names them, it cannot describe new ones.
///
/// Files are read only inside the workspace. The decoder already refused
/// absolute paths, `~` and `..`; here every directory walked and every file
/// matched is resolved with realpath(3): a link that resolves inside the
/// workspace is followed to its target, one that resolves outside is dropped,
/// and so is a file with a second name (hard link).
public enum StyleStateEngine {

    /// One in-session event the aggregates read.
    public struct RunEventRecord: Sendable, Equatable {
        public var event: StyleStateRunEvent
        /// What `lastValue` shows: the tool name, or a sub-agent's description.
        public var value: String
        public init(event: StyleStateRunEvent, value: String) { self.event = event; self.value = value }
    }

    /// A list widget shows at most this many lines (§1.16.4).
    public static let maximumListItems = 8

    // MARK: - The one path

    /// Disk reads plus in-session aggregates, combined. The Mac pane and the
    /// phone payload both come through `reading(sources:files:runEvents:)`;
    /// this is the same thing done in one call.
    public static func read(_ sources: StyleStateSources, workspacePath: String, since: Date?,
                            session: RunSession?) -> StyleStateReading {
        reading(sources: sources,
                files: readFiles(sources, workspacePath: workspacePath, since: since),
                runEvents: session.map { runEvents(from: $0, since: since) } ?? [])
    }

    /// Pure: combines file readings (from disk, possibly cached) with the
    /// pane's events. A missing reading draws as the source's empty widget.
    public static func reading(sources: StyleStateSources, files: [StyleFileReading],
                               runEvents: [RunEventRecord]) -> StyleStateReading {
        var states: [Int: StyleFileSourceState] = [:]
        var widgets: [StylePanel.Widget] = []
        for (index, source) in sources.files.enumerated() {
            let value = index < files.count ? files[index] : unread(source)
            states[index] = value.state
            widgets.append(value.widget)
        }
        for source in sources.runEvents {
            widgets.append(aggregate(source, events: runEvents.filter { $0.event == source.event }))
        }
        return StyleStateReading(fileSourceStates: states, widgets: widgets)
    }

    // MARK: - File sources

    /// Reads every file source. `since` is the pane's first request in this
    /// style: only a file modified after it is current, so a plan left from
    /// earlier work never drives a new session, and with no request yet
    /// nothing is current at all (the pane opens on its default phase).
    public static func readFiles(_ sources: StyleStateSources, workspacePath: String, since: Date?) -> [StyleFileReading] {
        guard let workspace = realPath(URL(fileURLWithPath: workspacePath, isDirectory: true)),
              StylePathBoundary.isPlainDirectory(workspace) else { return sources.files.map(unread) }
        return sources.files.map { readFile($0, workspace: workspace, since: since) }
    }

    /// The empty widget a source draws when nothing qualifies: never hidden,
    /// so the panel keeps one widget per declared source (§1.16.1).
    public static func unread(_ source: StyleStateFileSource) -> StyleFileReading {
        StyleFileReading(state: StyleFileSourceState(exists: false, allChecked: false), widget: emptyWidget(source.widget))
    }

    static func readFile(_ source: StyleStateFileSource, workspace: URL, since: Date?) -> StyleFileReading {
        guard let since else { return unread(source) }
        // The current file: the newest match modified after `since`. Ties go
        // to the path so the answer does not depend on directory order.
        let current = matches(source.path, in: workspace)
            .compactMap { url -> (url: URL, modified: Date)? in
                guard let modified = modificationDate(url), modified > since else { return nil }
                return (url, modified)
            }
            .max { $0.modified != $1.modified ? $0.modified < $1.modified : $0.url.path < $1.url.path }
        guard let current else { return unread(source) }
        guard let data = boundedRead(current.url, workspace: workspace, maximumBytes: StyleLimits.maximumBytes),
              let text = String(data: data, encoding: .utf8) else {
            // It exists and is current, but cannot be read as text.
            return StyleFileReading(state: StyleFileSourceState(exists: true, allChecked: false), widget: emptyWidget(source.widget))
        }
        return parse(text, source: source)
    }

    /// Reads one matched file without a check-then-use gap: the checks are
    /// made on the descriptor that is then read. The last component may not be
    /// a link (`O_NOFOLLOW`), the open never waits (`O_NONBLOCK`), and the
    /// opened file must be a regular file with one name, no larger than the
    /// cap, whose kernel path is still inside the workspace. At most cap + 1
    /// bytes are read, so a file that grew meanwhile is refused, not cut.
    static func boundedRead(_ url: URL, workspace: URL, maximumBytes: Int) -> Data? {
        let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1,
              info.st_size >= 0, info.st_size <= off_t(maximumBytes) else { return nil }
        var path = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard fcntl(fd, F_GETPATH, &path) == 0,
              StylePathBoundary.contains(parent: workspace, child: URL(fileURLWithPath: String(cString: path))) else { return nil }
        let limit = maximumBytes + 1
        var buffer = [UInt8](repeating: 0, count: limit)
        var total = 0
        while total < limit {
            let count = buffer.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress! + total, limit - total) }
            if count < 0 { if errno == EINTR { continue }; return nil }
            if count == 0 { break }
            total += count
        }
        guard total <= maximumBytes else { return nil }
        return Data(buffer.prefix(total))
    }

    /// Workspace-relative glob: `*` and `?` inside one path component, `**`
    /// for any number of directories. Hidden entries match only a component
    /// that itself starts with `.`. At most `maximumFilesPerSource` files and
    /// `maximumDirectoryEntries` directory entries are looked at.
    public static func matches(_ pattern: String, in workspace: URL) -> [URL] {
        guard !pattern.hasPrefix("/"), !pattern.hasPrefix("~") else { return [] }
        let parts = pattern.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard !parts.isEmpty, !parts.contains(".."), let root = realPath(workspace) else { return [] }
        var found: [URL] = []
        var budget = StyleLimits.maximumDirectoryEntries
        walk(parts[...], at: root, workspace: root, depth: 0, found: &found, budget: &budget)
        return found
    }

    private static func walk(_ parts: ArraySlice<String>, at directory: URL, workspace: URL, depth: Int,
                             found: inout [URL], budget: inout Int) {
        guard let part = parts.first, found.count < StyleLimits.maximumFilesPerSource,
              depth <= StyleLimits.maximumDepth else { return }
        let rest = parts.dropFirst()
        if part == "**" {
            // Zero directories, then each subdirectory in turn.
            walk(rest, at: directory, workspace: workspace, depth: depth, found: &found, budget: &budget)
            for child in entries(of: directory, budget: &budget) where !child.lastPathComponent.hasPrefix(".") {
                guard let resolved = inside(child, workspace: workspace), StylePathBoundary.isPlainDirectory(resolved) else { continue }
                walk(parts, at: resolved, workspace: workspace, depth: depth + 1, found: &found, budget: &budget)
            }
            return
        }
        let candidates: [URL]
        if part.contains("*") || part.contains("?") {
            candidates = entries(of: directory, budget: &budget).filter {
                let name = $0.lastPathComponent
                return (!name.hasPrefix(".") || part.hasPrefix(".")) && wildcard(name, matches: part)
            }.sorted { $0.lastPathComponent < $1.lastPathComponent }
        } else {
            candidates = [directory.appendingPathComponent(part)]
        }
        for candidate in candidates {
            guard found.count < StyleLimits.maximumFilesPerSource, let resolved = inside(candidate, workspace: workspace) else { continue }
            if rest.isEmpty {
                if StylePathBoundary.isPlainFile(resolved), !found.contains(resolved) { found.append(resolved) }
            } else if StylePathBoundary.isPlainDirectory(resolved) {
                walk(rest, at: resolved, workspace: workspace, depth: depth + 1, found: &found, budget: &budget)
            }
        }
    }

    private static func entries(of directory: URL, budget: inout Int) -> [URL] {
        guard budget > 0, let items = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else { return [] }
        let taken = Array(items.prefix(budget))
        budget -= taken.count
        return taken
    }

    /// The real path of `url` when it exists and stays inside the workspace.
    private static func inside(_ url: URL, workspace: URL) -> URL? {
        guard let resolved = realPath(url), StylePathBoundary.contains(parent: workspace, child: resolved) else { return nil }
        return resolved
    }

    /// realpath(3): every link resolved, nil when the path does not exist.
    static func realPath(_ url: URL) -> URL? {
        guard let resolved = realpath(url.path, nil) else { return nil }
        defer { free(resolved) }
        return URL(fileURLWithPath: String(cString: resolved))
    }

    private static func modificationDate(_ url: URL) -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
    }

    /// `*` is any run of characters, `?` exactly one; nothing else is special.
    static func wildcard(_ name: String, matches pattern: String) -> Bool {
        let s = Array(name), p = Array(pattern)
        var si = 0, pi = 0, star: Int?, mark = 0
        while si < s.count {
            if pi < p.count, p[pi] == "?" || (p[pi] != "*" && p[pi] == s[si]) {
                si += 1; pi += 1
            } else if pi < p.count, p[pi] == "*" {
                star = pi; mark = si; pi += 1
            } else if let star {
                pi = star + 1; mark += 1; si = mark
            } else { return false }
        }
        while pi < p.count, p[pi] == "*" { pi += 1 }
        return pi == p.count
    }

    // MARK: - Parsers

    /// One markdown checklist item: `- [ ] text`, `- [x] text` (also `*` and
    /// `+` bullets, `X`, any indent). Lines inside fenced code are not items.
    public struct ChecklistItem: Sendable, Equatable {
        public var text: String
        public var checked: Bool
    }

    public static func checklistItems(_ text: String) -> [ChecklistItem] {
        var items: [ChecklistItem] = []
        var fenced = false
        for raw in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.hasPrefix("```") || line.hasPrefix("~~~") { fenced.toggle(); continue }
            guard !fenced, line.count >= 5, let bullet = line.first, "-*+".contains(bullet) else { continue }
            let rest = line.dropFirst()
            guard rest.first == " " else { continue }
            let body = rest.drop { $0 == " " }
            guard body.count >= 3, body.hasPrefix("["), body.dropFirst(2).first == "]" else { continue }
            let mark = body.dropFirst().first!
            guard mark == " " || mark == "x" || mark == "X" else { continue }
            let after = body.dropFirst(3)
            guard after.isEmpty || after.first == " " else { continue }
            let item = String(after.trimmingCharacters(in: .whitespaces).prefix(StyleLimits.maximumString))
            items.append(ChecklistItem(text: StyleText.replacingBanned(item), checked: mark != " "))
        }
        return items
    }

    static func parse(_ text: String, source: StyleStateFileSource) -> StyleFileReading {
        switch source.parser {
        case .markdownChecklist:
            let items = checklistItems(text)
            let checked = items.filter(\.checked).count
            let state = StyleFileSourceState(exists: true, allChecked: !items.isEmpty && checked == items.count)
            let widget: StylePanel.Widget
            switch source.widget {
            case .progressBar: widget = .progressBar(value: checked, total: items.count)
            case .list: widget = .list(items: Array(items.filter { !$0.checked }.map(\.text).prefix(maximumListItems)))
            case .label: widget = .label(text: L("styles.state.checklistLabel", ["checked": String(checked), "total": String(items.count)]))
            }
            return StyleFileReading(state: state, widget: widget)
        case .json:
            // `allChecked` has no meaning for JSON and is always false (§1.16.5).
            let state = StyleFileSourceState(exists: true, allChecked: false)
            return StyleFileReading(state: state, widget: json(text, widget: source.widget) ?? emptyWidget(source.widget))
        }
    }

    /// The fixed JSON shapes: a list is a top-level array or `items`; a label
    /// is a top-level scalar or `text`; a bar is `{"value": n, "total": m}`.
    static func json(_ text: String, widget: StyleStateWidget) -> StylePanel.Widget? {
        guard let data = text.data(using: .utf8),
              let value = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else { return nil }
        let object = value as? [String: Any]
        switch widget {
        case .list:
            guard let array = (value as? [Any]) ?? (object?["items"] as? [Any]) else { return nil }
            return .list(items: Array(array.compactMap(scalar).prefix(maximumListItems)))
        case .label:
            guard let text = scalar(object?["text"] ?? value) else { return nil }
            return .label(text: text)
        case .progressBar:
            guard let done = count(object?["value"]) else { return nil }
            let total = count(object?["total"])
            return .progressBar(value: total.map { min(done, $0) } ?? done, total: total)
        }
    }

    private static func scalar(_ value: Any?) -> String? {
        switch value {
        case let text as String: return StyleText.replacingBanned(String(text.prefix(StyleLimits.maximumString)))
        case let number as NSNumber: return number.stringValue
        default: return nil
        }
    }

    private static func count(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double.isFinite, double >= 0, double <= Double(Int32.max) else { return nil }
        return Int(double)
    }

    static func emptyWidget(_ kind: StyleStateWidget) -> StylePanel.Widget {
        switch kind {
        case .progressBar: return .progressBar(value: 0, total: 0)
        case .list: return .list(items: [])
        case .label: return .label(text: "")
        }
    }

    // MARK: - Run events

    /// The tools that start a sub-agent; `send_input`, `wait` and `close_agent`
    /// talk to one that already exists.
    static let subagentTools: Set<String> = ["agent", "task", "spawn_agent", "delegate_to_agent"]
    static let toolKinds: Set<String> = ["tool", "command", "read", "edit", "search", "web"]

    /// This pane's own events since its first request in the style — never
    /// another pane's and never an earlier style's. A log entry is one
    /// activity whose state is updated in place, so a sub-agent counts once
    /// as started and once more as finished when it reaches a terminal state.
    public static func runEvents(from session: RunSession, since: Date?) -> [RunEventRecord] {
        guard let since else { return [] }
        // Two formatters per call rather than two per entry: the phone's digest
        // asks for this on every publish.
        let fractional = ISO8601DateFormatter(), plain = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var records: [RunEventRecord] = []
        for entry in session.logs {
            guard let activity = entry.activity, let tool = activity.toolName,
                  let at = fractional.date(from: entry.timestamp) ?? plain.date(from: entry.timestamp), at >= since else { continue }
            if activity.kind == "agent" {
                guard subagentTools.contains(tool.lowercased()) else { continue }
                let summary = eventValue(activity.summary)
                records.append(RunEventRecord(event: .subagentStart, value: summary))
                if MightyGraphSupport.terminal(activity.state) {
                    records.append(RunEventRecord(event: .subagentFinish, value: summary))
                }
            } else if toolKinds.contains(activity.kind) {
                records.append(RunEventRecord(event: .toolCall, value: eventValue(tool)))
            }
        }
        return records
    }

    /// An event's value came from the agent, not through the approval card: it
    /// is cut and cleaned exactly as a file source's text is (§1.8, §1.16.3).
    private static func eventValue(_ value: String) -> String {
        StyleText.replacingBanned(String(value.prefix(StyleLimits.maximumString)))
    }

    /// `count` and `lastValue`, each drawn by the widget the source names. A
    /// bar from events has no total: it shows how many there were.
    static func aggregate(_ source: StyleStateRunEventSource, events: [RunEventRecord]) -> StylePanel.Widget {
        switch (source.aggregate, source.widget) {
        case (.count, .label): return .label(text: countLabel(source.event, count: events.count))
        case (.count, .list): return .list(items: Array(events.suffix(maximumListItems).map(\.value)))
        case (_, .progressBar): return .progressBar(value: events.count, total: nil)
        case (.lastValue, .label): return .label(text: events.last?.value ?? "")
        case (.lastValue, .list): return .list(items: events.last.map { [$0.value] } ?? [])
        }
    }

    static func countLabel(_ event: StyleStateRunEvent, count: Int) -> String {
        let key: String
        switch event {
        case .subagentStart: key = "styles.state.subagentStartCount"
        case .subagentFinish: key = "styles.state.subagentFinishCount"
        case .toolCall: key = "styles.state.toolCallCount"
        }
        return L(key, ["count": String(count)])
    }

    // MARK: - Change detection

    /// Whether a change reported at `changedPath` (a directory or a file,
    /// already real) can move any file source: it is under a source's fixed
    /// leading directory, or is one of that directory's ancestors (a folder
    /// being created on the way). The watcher asks this; nothing is read.
    public static func affects(_ sources: StyleStateSources, workspacePath: String, changedPath: String) -> Bool {
        guard let workspace = realPath(URL(fileURLWithPath: workspacePath, isDirectory: true)) else { return false }
        let changed = URL(fileURLWithPath: changedPath).standardizedFileURL.path
        for source in sources.files {
            let fixed = source.path.split(separator: "/").prefix { !$0.contains("*") && !$0.contains("?") }
            // A literal path's last part is the file; its folder is what changes.
            let folders = fixed.count == source.path.split(separator: "/").count ? fixed.dropLast() : fixed
            let base = folders.reduce(workspace) { $0.appendingPathComponent(String($1)) }.standardizedFileURL.path
            if changed == base || changed.hasPrefix(base + "/") || base.hasPrefix(changed + "/") { return true }
        }
        return false
    }
}
