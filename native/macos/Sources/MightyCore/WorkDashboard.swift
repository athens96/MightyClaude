import Foundation

/// What the "작업 현황" view and the sidebar's pane rows count and show, read only
/// from what the app already holds: the pane list, the pending tool requests, each
/// pane's own run clock and usage reading. Nothing is estimated — a number the app
/// does not have is left out (nil) rather than filled with zero. Mirrors the phone's
/// `mobile/src/lib/dashboard.ts`.
public enum WorkDashboard {
    /// Requests a pane holds that wait on the user.
    public struct Attention: Equatable, Sendable {
        public var questions: Int
        public var permissions: Int
        public init(questions: Int = 0, permissions: Int = 0) { self.questions = questions; self.permissions = permissions }
        public var total: Int { questions + permissions }
    }

    /// The three tiles: panes running now, requests waiting on the user, panes finished now.
    public struct Stats: Equatable, Sendable {
        /// Panes whose status is `running`, whether or not they also wait on a request.
        public var running: Int
        /// Pending questions plus pending permission requests — requests, not panes.
        public var waiting: Int
        /// Panes whose status is `completed` right now — not a count of runs finished today.
        public var done: Int
        public init(running: Int = 0, waiting: Int = 0, done: Int = 0) { self.running = running; self.waiting = waiting; self.done = done }
    }

    /// A workspace's counts by state: the sidebar shows what wants a look (requests,
    /// running, errors); the workspace header also what has settled.
    public struct Badges: Equatable, Sendable {
        public var questions: Int
        public var permissions: Int
        /// Panes stopped by an error.
        public var errors: Int
        /// Panes running and not waiting on the user (those show as waiting instead).
        public var running: Int
        /// Panes finished, stopped by the user, and idle (not waiting on the user).
        public var done: Int
        public var stopped: Int
        public var idle: Int
        public init(questions: Int = 0, permissions: Int = 0, errors: Int = 0, running: Int = 0, done: Int = 0, stopped: Int = 0, idle: Int = 0) {
            self.questions = questions; self.permissions = permissions; self.errors = errors; self.running = running
            self.done = done; self.stopped = stopped; self.idle = idle
        }
    }

    /// A pane the user opened and the counts include: not an agent's own terminal or
    /// browser (the phone skips those too), not the read-only files pane, which never runs.
    public static func isCounted(_ kind: String) -> Bool {
        !AgentIOPaneKind.isAgentIOPane(kind) && !FilePaneKind.isFilePane(kind)
    }

    /// Only requests still `pending` count; a question is one the user answers in a
    /// questionnaire (`canAnswerQuestions`), everything else is a permission request.
    public static func attention(_ requests: [ToolPermissionRequest]?) -> Attention {
        var value = Attention()
        for request in requests ?? [] where request.state == "pending" {
            if request.canAnswerQuestions { value.questions += 1 } else { value.permissions += 1 }
        }
        return value
    }

    /// The status a card draws: a pane holding a request waits on the user whatever
    /// its own status says, since that is what the user has to act on.
    public static func displayStatus(_ status: String, attention: Attention) -> String {
        attention.total > 0 ? "waiting" : status
    }

    public static func stats(sessions: [RunSession], permissions: [String: [ToolPermissionRequest]]) -> Stats {
        var stats = Stats()
        for session in sessions where isCounted(session.kind) {
            stats.waiting += attention(permissions[session.id]).total
            if session.status == "running" { stats.running += 1 }
            else if session.status == "completed" { stats.done += 1 }
        }
        return stats
    }

    public static func badges(sessions: [RunSession], permissions: [String: [ToolPermissionRequest]]) -> Badges {
        var badges = Badges()
        for session in sessions where isCounted(session.kind) {
            let pending = attention(permissions[session.id])
            badges.questions += pending.questions
            badges.permissions += pending.permissions
            switch DesignTone(status: displayStatus(session.status, attention: pending)) {
            case .err: badges.errors += 1
            case .run: badges.running += 1
            case .done: badges.done += 1
            case .stop: badges.stopped += 1
            case .idle: badges.idle += 1
            case .wait: break
            }
        }
        return badges
    }

    /// The last thing a pane did, for the mono line at the foot of a card.
    public struct LastActivity: Equatable, Sendable {
        public var text: String
        public var isError: Bool
        /// The tool the entry was a call to, when it was one.
        public var tool: String?
        public init(text: String, isError: Bool, tool: String? = nil) { self.text = text; self.isError = isError; self.tool = tool }
    }

    /// One pane as a status row. Every optional is absent when the app does not have it.
    public struct Card: Equatable, Sendable, Identifiable {
        public var id: String
        public var workspaceId: String
        public var title: String
        public var kind: String
        public var provider: String
        /// The model the pane asks for; nil for the CLI's own default.
        public var model: String?
        public var status: String
        public var displayStatus: String
        public var attention: Attention
        /// `SessionUsage.contextPercent`, only when the reading is from the pane's own provider.
        public var contextPercent: Double?
        /// The pane's own clock for its latest request (`RunSession.runTiming`), when valid.
        public var timing: AgentRunTiming?
        /// When the pane last wrote to its record (its last log entry, else its creation).
        public var updatedAt: Date?
        public var lastActivity: LastActivity?

        public var tone: DesignTone { DesignTone(status: displayStatus) }
        public var isAgent: Bool { kind == SessionKind.claude }
        public var isRunning: Bool { status == "running" }
    }

    /// `activity: false` skips the log scan for `lastActivity` (left nil), for a caller
    /// that redraws on every streamed line and never shows it (the pane's header).
    public static func card(_ session: RunSession, permissions: [ToolPermissionRequest]?, activity: Bool = true) -> Card {
        let pending = attention(permissions)
        let usage = session.sessionUsage?.provider == session.provider ? session.sessionUsage : nil
        let percent = usage?.contextPercent.flatMap { $0.isFinite ? min(100, max(0, $0)) : nil }
        let timing = session.runTiming.flatMap { $0.isValid ? $0 : nil }
        let model = session.model.trimmingCharacters(in: .whitespacesAndNewlines)
        return Card(
            id: session.id, workspaceId: session.workspaceId, title: session.title, kind: session.kind, provider: session.provider,
            model: model.isEmpty || model == "default" ? nil : model,
            status: session.status, displayStatus: displayStatus(session.status, attention: pending), attention: pending,
            contextPercent: percent, timing: timing,
            updatedAt: AgentRunTiming.parseTimestamp(session.logs.last?.timestamp ?? session.createdAt),
            lastActivity: activity ? lastActivity(session.logs) : nil)
    }

    /// The newest log entry with something to say: a tool call as "tool · summary", or
    /// the first line of a message. An `error` entry or a failed tool call is an error.
    public static func lastActivity(_ logs: [LogEntry]) -> LastActivity? {
        for entry in logs.reversed() {
            if let activity = entry.activity {
                let summary = oneLine(activity.summary)
                let tool = activity.toolName.map(oneLine) ?? ""
                let text = tool.isEmpty ? summary : summary.isEmpty ? tool : tool + " · " + summary
                guard !text.isEmpty else { continue }
                return LastActivity(text: text, isError: activity.state == "error", tool: tool.isEmpty ? nil : tool)
            }
            let text = oneLine(entry.text)
            guard !text.isEmpty, entry.kind != "image" else { continue }
            return LastActivity(text: text, isError: entry.kind == "error")
        }
        return nil
    }

    /// The first non-blank line, trimmed, at most 200 characters. Only the leading blank
    /// run and the next 201 characters are read, so a long message is never split whole.
    private static func oneLine(_ text: String) -> String {
        let blank = CharacterSet.whitespacesAndNewlines
        guard let start = text.firstIndex(where: { !$0.unicodeScalars.allSatisfy(blank.contains) }) else { return "" }
        let window = text[start...].prefix(201)
        let line = window.firstIndex(where: \.isNewline).map { window[..<$0] } ?? window
        if line.count > 200 { return String(line.prefix(200)) }
        return line.trimmingCharacters(in: .whitespaces)
    }

    /// Most pressing first: waiting on the user, running, stopped by an error, finished
    /// (an outcome to look at, as on the phone's list), stopped by the user, then idle.
    /// Within a rank the most recently active pane comes first; the order is otherwise stable.
    public static func rank(_ tone: DesignTone) -> Int {
        switch tone {
        case .wait: 0
        case .run: 1
        case .err: 2
        case .done: 3
        case .stop: 4
        case .idle: 5
        }
    }

    public static func ordered(_ cards: [Card]) -> [Card] {
        cards.enumerated().sorted { a, b in
            let ra = rank(a.element.tone), rb = rank(b.element.tone)
            if ra != rb { return ra < rb }
            let da = a.element.updatedAt ?? .distantPast, db = b.element.updatedAt ?? .distantPast
            if da != db { return da > db }
            return a.offset < b.offset
        }.map(\.element)
    }

    /// `02:14`, `1:02:09` — the same stopwatch reading as `AgentRunTiming.label`, without
    /// its approximate prefix, which the view words through the locale.
    public static func clock(_ seconds: TimeInterval) -> String {
        let total = seconds.isFinite ? Int(min(max(0, seconds), Double(Int.max / 2))) : 0
        if total >= 3_600 { return "\(total / 3_600):" + String(format: "%02d:%02d", total / 60 % 60, total % 60) }
        return String(format: "%02d:%02d", total / 60, total % 60)
    }

    public enum Age: Equatable, Sendable {
        case now
        case minutes(Int)
        case hours(Int)
        case days(Int)
    }

    /// How long ago `date` was, in the largest whole unit.
    public static func age(of date: Date, now: Date) -> Age {
        let minutes = Int(max(0, now.timeIntervalSince(date)) / 60)
        if minutes < 1 { return .now }
        if minutes < 60 { return .minutes(minutes) }
        let hours = minutes / 60
        if hours < 24 { return .hours(hours) }
        return .days(hours / 24)
    }

    /// What the sidebar row's second line holds, in order.
    public enum MetaPart: Equatable, Sendable {
        /// The agent's provider (Claude, Codex, Gemini).
        case provider
        case elapsed
        case context(Int)
        /// Why a pane stopped on an error: the tool that failed (worded "Bash 실패"),
        /// else the error's own first line, cut to `reasonLimit` characters.
        case reason(String, isTool: Bool)
        case age(Age)
    }

    /// The longest error line the sidebar row's second line carries.
    public static let reasonLimit = 40

    /// An agent pane names its provider first. A running pane (one waiting on the user
    /// included) then shows its clock and context; one at rest how long ago it was
    /// active, after why it failed for a pane stopped by an error ("Claude · Bash 실패 ·
    /// 3분 전"). A part the app has no number for is left out; an empty result means the
    /// view names the pane's kind instead (a shell, a browser, the files pane).
    public static func sidebarMeta(_ card: Card, now: Date) -> [MetaPart] {
        guard card.isAgent else { return [] }
        var parts: [MetaPart] = [.provider]
        if card.isRunning {
            if card.timing != nil { parts.append(.elapsed) }
            if let percent = card.contextPercent { parts.append(.context(Int(percent.rounded()))) }
            return parts
        }
        if card.tone == .err, let last = card.lastActivity, last.isError {
            if let tool = last.tool { parts.append(.reason(tool, isTool: true)) }
            else { parts.append(.reason(last.text.count > reasonLimit ? String(last.text.prefix(reasonLimit - 1)) + "…" : last.text, isTool: false)) }
        }
        if let date = card.updatedAt { parts.append(.age(age(of: date, now: now))) }
        return parts
    }
}
