import Darwin
import Foundation

/// An earlier Claude or Codex session recorded for a workspace folder that a
/// new pane can continue with `--resume` / `exec resume`.
public struct ResumableSession: Sendable, Equatable, Identifiable {
    public var provider: String
    /// The provider's own session id: the pane's `resumeId`.
    public var sessionID: String
    /// The first request the user typed, on one line; nil when none was read.
    public var title: String?
    /// When the record was last written.
    public var modified: Date
    /// Requests in the record, counted only when the record is small.
    public var requests: Int?
    /// The model the record names last.
    public var model: String?
    public var url: URL
    /// Started by another program's non-interactive run (a nested
    /// `claude --print` of an Ouroboros step): listed only when every session
    /// is shown.
    public var automated: Bool
    public var id: String { provider + ":" + sessionID }
    public init(provider: String, sessionID: String, title: String?, modified: Date, requests: Int?, model: String?, url: URL, automated: Bool = false) {
        self.provider = provider; self.sessionID = sessionID; self.title = title; self.modified = modified
        self.requests = requests; self.model = model; self.url = url; self.automated = automated
    }
}

/// One listing: what to show, and how many automated sessions were left out.
public struct ResumableSessionListing: Sendable, Equatable {
    public var items: [ResumableSession]
    public var hidden: Int
    public init(items: [ResumableSession] = [], hidden: Int = 0) { self.items = items; self.hidden = hidden }
}

public struct ResumableSessionQuery: Sendable {
    public var workspacePath: String
    public var environment: [String: String]
    public var home: URL
    /// Session ids open panes already use (any provider, any case).
    public var excluding: Set<String>
    /// Session ids the app's own panes started or resumed: never hidden.
    public var known: Set<String>
    /// Lists automated sessions too ("모든 세션 보기").
    public var includeAutomated: Bool
    public var now: Date
    public var maximumAge: TimeInterval
    /// Records read for a row per provider, newest first by modification
    /// time. Excluded and hidden records do not use a slot: they are decided
    /// from their first request alone.
    public var maximumCandidates: Int
    /// Records looked at per provider at all, hidden ones included.
    public var maximumScanned: Int
    /// Sessions listed per provider.
    public var maximumSessions: Int
    /// Reads only what decides whether a record is listed (its first working folder
    /// and first request): no request count, no model from the record's end. For a
    /// yes/no look-up such as "창 추가"'s.
    public var headOnly = false
    public init(workspacePath: String, environment: [String: String] = [:], home: URL = FileManager.default.homeDirectoryForCurrentUser,
                excluding: Set<String> = [], known: Set<String> = [], includeAutomated: Bool = false,
                now: Date = Date(), maximumAge: TimeInterval = ResumableSessions.maximumAge,
                maximumCandidates: Int = ResumableSessions.maximumCandidates, maximumScanned: Int = ResumableSessions.maximumScanned,
                maximumSessions: Int = ResumableSessions.maximumSessions) {
        self.workspacePath = workspacePath; self.environment = environment; self.home = home
        self.excluding = Set(excluding.map { $0.lowercased() }); self.known = Set(known.map { $0.lowercased() })
        self.includeAutomated = includeAutomated; self.now = now; self.maximumAge = maximumAge
        self.maximumCandidates = maximumCandidates; self.maximumScanned = maximumScanned; self.maximumSessions = maximumSessions
    }
}

/// Lists the sessions a workspace folder's CLIs recorded, read-only and
/// bounded: only the head of a record (and its tail for the model) unless it
/// is small enough to read whole for a request count. Synchronous and
/// file-bound: call it off the main actor. Gemini is not listed: the app does
/// not read its records.
public enum ResumableSessions {
    public static let providers = ["claude", "codex"]
    public static let maximumAge: TimeInterval = 60 * 86_400
    public static let maximumCandidates = 600
    public static let maximumScanned = 6_000
    public static let maximumSessions = 200
    /// A record written this recently may belong to a CLI still running.
    public static let busyInterval: TimeInterval = 120
    /// How far into a record its first request is looked for (session-start
    /// hooks can write hundreds of KiB before it).
    static let headBytes = 2 * 1_048_576
    /// A Codex record's first line (`session_meta`) is looked for within this.
    static let metaBytes = 512 * 1_024
    /// Records up to this size are read whole and their requests counted.
    static let countBytes = 1_048_576
    /// Bytes all whole reads of one listing may spend together.
    static let countBudget = 96 * 1_048_576
    /// The end of a record read back for its model.
    static let tailBytes = 256 * 1_024
    /// A title kept for the list; the pane title is shortened further.
    static let titleCharacters = 200

    public static func list(_ query: ResumableSessionQuery) -> [ResumableSession] { listing(query).items }

    public static func listing(_ query: ResumableSessionQuery) -> ResumableSessionListing {
        var budget = countBudget
        let claude = claude(query, budget: &budget), codex = codex(query, budget: &budget)
        let items = (claude.items + codex.items).sorted { $0.modified != $1.modified ? $0.modified > $1.modified : $0.id < $1.id }
        return ResumableSessionListing(items: items, hidden: claude.hidden + codex.hidden)
    }

    /// One agent's sessions alone: the other agent's records are not read. An agent
    /// the app cannot resume lists nothing.
    public static func listing(_ query: ResumableSessionQuery, provider: String) -> ResumableSessionListing {
        var budget = countBudget
        switch provider {
        case "claude": return claude(query, budget: &budget)
        case "codex": return codex(query, budget: &budget)
        default: return ResumableSessionListing()
        }
    }

    /// A nested non-interactive run sends a flattened transcript as its first
    /// prompt ("User: …" / "Assistant: …"); a person types no such thing. Codex
    /// `exec` records carry no marker that tells such a run from the app's own
    /// panes (both are `originator` `codex_exec`, `source` `exec`), so the
    /// same text rule decides there too.
    public static func automatedPrompt(_ prompt: String?) -> Bool {
        guard let prompt else { return false }
        return prompt.hasPrefix("User: ") || prompt.hasPrefix("Assistant: ") || prompt.hasPrefix("User:\n") || prompt.hasPrefix("Assistant:\n")
    }

    /// Whether the record was written so recently that a CLI elsewhere may
    /// still be running the session.
    public static func mayBeRunning(_ item: ResumableSession, now: Date = Date()) -> Bool {
        now.timeIntervalSince(item.modified) < busyInterval
    }

    /// Ids of sessions open panes continue, lowercased.
    public static func inUse(_ sessions: [RunSession]) -> Set<String> {
        Set(sessions.compactMap { $0.kind == SessionKind.claude ? $0.resumeId?.lowercased() : nil })
    }

    /// Titles containing every word of the query, ignoring case and width.
    public static func filter(_ items: [ResumableSession], query: String) -> [ResumableSession] {
        let words = query.split(whereSeparator: \.isWhitespace).map(String.init)
        guard !words.isEmpty else { return items }
        return items.filter { item in
            let title = item.title ?? ""
            return words.allSatisfy { title.range(of: $0, options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive]) != nil }
        }
    }

    /// Makes a freshly added agent pane continue `item`: it resumes that
    /// session and is titled after its first request. The title stays
    /// automatic, so the next request retitles the pane as usual.
    public static func apply(_ item: ResumableSession, to session: inout RunSession) {
        session.provider = item.provider
        session.resumeId = item.sessionID
        if let title = item.title.flatMap(PaneTitle.shortened) { session.title = title }
        session.titleMode = "auto"
    }

    /// "방금", "5분 전", "3시간 전", "12일 전".
    public static func relativeTime(_ date: Date, now: Date = Date()) -> String {
        let seconds = max(0, now.timeIntervalSince(date))
        if seconds < 60 { return L("resume.time.now") }
        if seconds < 3_600 { return L("resume.time.minutes", ["count": "\(Int(seconds / 60))"]) }
        if seconds < 86_400 { return L("resume.time.hours", ["count": "\(Int(seconds / 3_600))"]) }
        return L("resume.time.days", ["count": "\(Int(seconds / 86_400))"])
    }

    // MARK: Claude

    private static let uuid = #/^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/#

    /// `<config>/projects/<escaped path>/<session>.jsonl` for the workspace
    /// path and its resolved path. Sub-agent records live in sub-folders and
    /// are never listed; a record whose first working folder is another one
    /// (two paths can escape to the same name) is left out.
    static func claude(_ query: ResumableSessionQuery, budget: inout Int) -> ResumableSessionListing {
        let config = query.environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) }
            ?? query.home.appendingPathComponent(".claude", isDirectory: true)
        let projects = config.appendingPathComponent("projects", isDirectory: true)
        let paths = workspacePaths(query.workspacePath)
        var folders: [String] = []
        for path in paths where !folders.contains(SessionHistory.claudeProjectFolder(path)) { folders.append(SessionHistory.claudeProjectFolder(path)) }
        var candidates: [(url: URL, id: String, modified: Date)] = []
        var seen = Set<String>()
        for folder in folders {
            for (url, modified) in files(in: projects.appendingPathComponent(folder, isDirectory: true), extension: "jsonl") {
                let id = String(url.lastPathComponent.dropLast(".jsonl".count))
                guard (try? uuid.wholeMatch(in: id)) != nil, seen.insert(id.lowercased()).inserted else { continue }
                candidates.append((url, id, modified))
            }
        }
        var listing = ResumableSessionListing(), read = 0
        for candidate in recent(candidates, query: query) {
            guard listing.items.count < query.maximumSessions, read < query.maximumCandidates, !Task.isCancelled else { break }
            guard !query.excluding.contains(candidate.id.lowercased()) else { continue }
            let known = query.known.contains(candidate.id.lowercased())
            switch claudeSession(candidate.url, id: candidate.id, modified: candidate.modified, paths: paths, known: known,
                                 includeAutomated: query.includeAutomated, headOnly: query.headOnly, budget: &budget) {
            case .listed(let item): read += 1; listing.items.append(item)
            case .hidden: listing.hidden += 1
            case .skipped: read += 1
            }
        }
        return listing
    }

    enum Outcome { case listed(ResumableSession), hidden, skipped }

    /// The head alone decides whether a record is listed: its first working
    /// folder and its first request. Only a listed record is read further.
    static func claudeSession(_ url: URL, id: String, modified: Date, paths: Set<String>, known: Bool = false,
                              includeAutomated: Bool = true, headOnly: Bool = false, budget: inout Int) -> Outcome {
        guard let fd = CodexSessionFiles.open(url) else { return .skipped }
        defer { Darwin.close(fd) }
        guard let size = CodexSessionFiles.size(fd), size > 0 else { return .skipped }
        var title: String?, cwdChecked = false, foreign = false
        let headComplete = forEachLine(fd, size: size, limit: headBytes) { line in
            if !cwdChecked, let object = HistoryScan.contains(line, #""cwd":"#) ? json(line) : nil, let cwd = object["cwd"] as? String {
                cwdChecked = true
                guard object["isSidechain"] as? Bool != true, matches(cwd, paths) else { foreign = true; return false }
            }
            if title == nil, let opening = HistoryScan.opening(line, format: .claude) { title = opening.prompt }
            return title == nil || !cwdChecked
        }
        // A finished record without a single request has nothing to continue.
        if foreign || (title == nil && headComplete) { return .skipped }
        let automated = !known && automatedPrompt(title)
        if automated && !includeAutomated { return .hidden }
        var requests: Int?
        if !headOnly && size <= countBytes && budget >= size {
            budget -= size
            var count = 0
            let complete = forEachLine(fd, size: size, limit: size) { line in
                if HistoryScan.opening(line, format: .claude) != nil { count += 1 }
                return true
            }
            if complete { requests = count }
        }
        let model = headOnly ? nil : tailModel(fd, size: size, claudeModel)
        return .listed(ResumableSession(provider: "claude", sessionID: id, title: title.map(oneLine), modified: modified,
                                        requests: requests, model: model, url: url, automated: automated))
    }

    static func claudeModel(_ line: Data) -> String? {
        guard HistoryScan.contains(line, #""type":"assistant""#), HistoryScan.contains(line, #""model":"#),
              let object = json(line), object["type"] as? String == "assistant", object["isSidechain"] as? Bool != true,
              let model = (object["message"] as? [String: Any])?["model"] as? String,
              !model.hasPrefix("<"), CoreValidation.model(model) else { return nil }
        return model
    }

    // MARK: Codex

    /// `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-…-<thread>.jsonl` whose
    /// `session_meta` names the workspace as its working folder. Sub-agent
    /// threads (`source.subagent`: spawned children, guardian reviews) are
    /// left out.
    static func codex(_ query: ResumableSessionQuery, budget: inout Int) -> ResumableSessionListing {
        guard let sessions = CodexSessionFiles.sessions(codexHome: CLIAccountSupport.codexHome(home: query.home, environment: query.environment)) else { return ResumableSessionListing() }
        let paths = workspacePaths(query.workspacePath)
        var candidates: [(url: URL, id: String, modified: Date)] = []
        for day in dayFolders(sessions) {
            for rollout in CodexSessionFiles.rollouts(in: day) { candidates.append((rollout.url, rollout.thread, rollout.modified)) }
        }
        var listing = ResumableSessionListing(), read = 0
        var seen = Set<String>()
        for candidate in recent(candidates, query: query) {
            guard listing.items.count < query.maximumSessions, read < query.maximumCandidates, !Task.isCancelled else { break }
            let lowered = candidate.id.lowercased()
            guard CoreValidation.identifier(candidate.id), !query.excluding.contains(lowered), !seen.contains(lowered) else { continue }
            switch codexSession(candidate.url, id: candidate.id, modified: candidate.modified, paths: paths, known: query.known.contains(lowered),
                                includeAutomated: query.includeAutomated, headOnly: query.headOnly, budget: &budget) {
            case .listed(let item): read += 1; seen.insert(lowered); listing.items.append(item)
            case .hidden: seen.insert(lowered); listing.hidden += 1
            case .skipped: read += 1
            }
        }
        return listing
    }

    static func codexSession(_ url: URL, id: String, modified: Date, paths: Set<String>, known: Bool = false,
                             includeAutomated: Bool = true, headOnly: Bool = false, budget: inout Int) -> Outcome {
        guard let fd = CodexSessionFiles.open(url) else { return .skipped }
        defer { Darwin.close(fd) }
        guard let size = CodexSessionFiles.size(fd), size > 0 else { return .skipped }
        // The first line alone decides; most of a long line is instructions.
        var first: Data?
        _ = forEachLine(fd, size: size, limit: metaBytes) { first = $0; return false }
        guard let first, let meta = json(first), meta["type"] as? String == "session_meta",
              let payload = meta["payload"] as? [String: Any], let cwd = payload["cwd"] as? String, matches(cwd, paths) else { return .skipped }
        if let source = payload["source"] as? [String: Any], source["subagent"] != nil { return .skipped }
        // The first turn's lines that can carry its user text, read until the
        // turn's text is certain or the next turn starts.
        var title: String?, headModel: String?, turn: [Data] = [], inTurn = false
        _ = forEachLine(fd, size: size, limit: headBytes) { line in
            if headModel == nil { headModel = codexModel(line) }
            if HistoryScan.opening(line, format: .codex(thread: id)) != nil {
                if inTurn, let prompt = HistoryScan.codexPrompt(turn) { title = prompt; return false }
                inTurn = true; turn = []
                return true
            }
            guard HistoryScan.codexUserLine(line) else { return true }
            turn.append(line)
            // The item or the event is the turn's own text; nothing can beat it.
            if HistoryScan.codexUserItem(line) != nil || HistoryScan.codexUserEvent(line) != nil {
                title = HistoryScan.codexPrompt(turn); return false
            }
            return true
        }
        if title == nil { title = HistoryScan.codexPrompt(turn) }
        let automated = !known && automatedPrompt(title)
        if automated && !includeAutomated { return .hidden }
        var requests: Int?
        if !headOnly && size <= countBytes && budget >= size {
            budget -= size
            var count = 0
            let complete = forEachLine(fd, size: size, limit: size) { line in
                if HistoryScan.opening(line, format: .codex(thread: id)) != nil { count += 1 }
                if headModel == nil { headModel = codexModel(line) }
                return true
            }
            if complete { requests = count }
        }
        let model = headOnly ? headModel : tailModel(fd, size: size, codexModel) ?? headModel
        return .listed(ResumableSession(provider: "codex", sessionID: id, title: title.map(oneLine), modified: modified,
                                        requests: requests, model: model, url: url, automated: automated))
    }

    static func codexModel(_ line: Data) -> String? { HistoryReplay.codexModel([line]) }

    /// Every day folder, newest first, at most 4 000; links are not followed.
    static func dayFolders(_ sessions: URL) -> [URL] {
        func folders(_ url: URL) -> [URL] {
            let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey]
            let items = (try? FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
            return items.filter { item in
                let values = try? item.resourceValues(forKeys: Set(keys))
                return values?.isDirectory == true && values?.isSymbolicLink != true && item.lastPathComponent.allSatisfy(\.isNumber)
            }.sorted { $0.lastPathComponent > $1.lastPathComponent }
        }
        var days: [URL] = []
        for year in folders(sessions) {
            for month in folders(year) {
                for day in folders(month) {
                    days.append(day)
                    if days.count >= 4_000 { return days }
                }
            }
        }
        return days
    }

    // MARK: Reading

    /// The workspace path as given and resolved, without a trailing slash.
    static func workspacePaths(_ path: String) -> Set<String> {
        var paths: Set<String> = [normalized(path)]
        if let real = realpath(path, nil) { paths.insert(normalized(String(cString: real))); free(real) }
        return paths
    }
    private static func normalized(_ path: String) -> String {
        var path = path
        while path.count > 1, path.hasSuffix("/") { path.removeLast() }
        return path
    }
    static func matches(_ cwd: String, _ paths: Set<String>) -> Bool {
        if paths.contains(normalized(cwd)) { return true }
        guard let real = realpath(cwd, nil) else { return false }
        defer { free(real) }
        return paths.contains(normalized(String(cString: real)))
    }

    /// Regular files (not links) with the extension directly in `folder`.
    static func files(in folder: URL, extension ext: String) -> [(URL, Date)] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey]
        guard let urls = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        return urls.compactMap { url in
            guard url.pathExtension == ext, let values = try? url.resourceValues(forKeys: Set(keys)), values.isSymbolicLink != true,
                  values.isRegularFile == true, let modified = values.contentModificationDate else { return nil }
            return (url, modified)
        }
    }

    /// Within the age limit, newest first, at most the scan limit.
    private static func recent(_ candidates: [(url: URL, id: String, modified: Date)], query: ResumableSessionQuery) -> [(url: URL, id: String, modified: Date)] {
        let oldest = query.now.addingTimeInterval(-query.maximumAge)
        return Array(candidates.filter { $0.modified >= oldest }
            .sorted { $0.modified != $1.modified ? $0.modified > $1.modified : $0.id < $1.id }
            .prefix(max(0, query.maximumScanned)))
    }

    /// Visits the complete lines of the first `limit` bytes in order, read
    /// in blocks, until `visit` returns false. True when every line of the
    /// file was visited; a line still being written is never visited.
    static func forEachLine(_ fd: Int32, size: Int, limit: Int, block: Int = 64 * 1_024, _ visit: (Data) -> Bool) -> Bool {
        let total = min(size, max(0, limit))
        var pending = Data(), offset = 0
        while offset < total {
            let wanted = min(block, total - offset), kept = pending.count
            pending.count = kept + wanted
            let read = pending.withUnsafeMutableBytes { pread(fd, $0.baseAddress! + kept, wanted, off_t(offset)) }
            guard read > 0 else { return false }
            pending.count = kept + read
            offset += read
            var stopped = false
            let consumed = pending.withUnsafeBytes { buffer -> Int in
                let base = buffer.baseAddress!
                var start = 0
                while start < buffer.count, let found = memchr(base + start, 10, buffer.count - start) {
                    let end = base.distance(to: found)
                    defer { start = end + 1 }
                    if end > start, !visit(Data(bytes: base + start, count: end - start)) { stopped = true; return end + 1 }
                }
                return start
            }
            if stopped { return false }
            if consumed > 0 { pending = pending.subdata(in: consumed..<pending.count) }
        }
        return offset == size && pending.isEmpty
    }

    /// The newest value `pick` finds in the record's last lines.
    static func tailModel(_ fd: Int32, size: Int, _ pick: (Data) -> String?) -> String? {
        for window in [64 * 1_024, tailBytes] {
            let start = max(0, size - window), count = size - start
            var data = Data(count: count)
            let read = data.withUnsafeMutableBytes { pread(fd, $0.baseAddress!, count, off_t(start)) }
            guard read == count else { return nil }
            var lines: [Data] = []
            data.withUnsafeBytes { buffer in
                let base = buffer.baseAddress!
                var from = 0, first = true
                while from < buffer.count, let found = memchr(base + from, 10, buffer.count - from) {
                    let end = base.distance(to: found)
                    // The window's first piece is the end of a longer line.
                    if end > from, !(first && start > 0) { lines.append(Data(bytes: base + from, count: end - from)) }
                    first = false; from = end + 1
                }
            }
            for line in lines.reversed() { if let value = pick(line) { return value } }
            if start == 0 { break }
        }
        return nil
    }

    private static func json(_ line: Data) -> [String: Any]? { (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] }

    static func oneLine(_ text: String) -> String {
        // A pasted prompt can be very long; only its start can show.
        let start = text.prefix(titleCharacters * 8)
        let collapsed = start.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).joined(separator: " ")
        let cut = collapsed.count > titleCharacters || start.endIndex < text.endIndex
        return cut ? String(collapsed.prefix(titleCharacters)) + "…" : collapsed
    }
}

/// Session ids the app's own panes started or resumed, oldest first, kept in
/// a small file in the app's data folder: the session list never hides them
/// as automated runs.
public enum KnownSessionIDs {
    public static let maximum = 2_000
    static let maximumFileBytes = 256 * 1_024

    public static func load(_ url: URL) -> [String] {
        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]), values.isRegularFile == true,
              (values.fileSize ?? .max) <= maximumFileBytes, let data = try? Data(contentsOf: url),
              let ids = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return Array(ids.filter(CoreValidation.identifier).suffix(maximum))
    }
    /// The list with `id` added as its newest entry, dropping the oldest past
    /// the limit; nil when nothing changes.
    public static func adding(_ id: String, to ids: [String]) -> [String]? {
        guard CoreValidation.identifier(id), !ids.contains(where: { $0.caseInsensitiveCompare(id) == .orderedSame }) else { return nil }
        return Array((ids + [id]).suffix(maximum))
    }
    public static func save(_ ids: [String], to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(Array(ids.suffix(maximum))).write(to: url, options: [.atomic])
    }
}
