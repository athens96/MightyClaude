import Foundation

/// A background Ouroboros execution an agent started from a pane. It outlives
/// the request that started it, so the graph keeps a live block for it after
/// that request settled. Only the four start tools' results are read.
public struct OuroborosExecutionLink: Sendable, Hashable, Identifiable {
    /// The graph run whose tool result named the execution.
    public var runID: String
    public var tool: String
    public var jobID: String?
    public var sessionID: String?
    public var executionID: String?
    /// Loopback only; anything else in the text is dropped.
    public var dashboardURL: URL?
    public var timestamp: String
    public init(runID: String, tool: String, jobID: String? = nil, sessionID: String? = nil, executionID: String? = nil,
                dashboardURL: URL? = nil, timestamp: String = "") {
        self.runID = runID; self.tool = tool; self.jobID = jobID; self.sessionID = sessionID
        self.executionID = executionID; self.dashboardURL = dashboardURL; self.timestamp = timestamp
    }
    /// One block per execution; a start that named only a job keys on the job.
    public var key: String { executionID ?? "job-" + (jobID ?? "") }
    public var id: String { key }
}

public enum OuroborosExecutionLinks {
    /// The style manifest's `job.open` tools.
    public static let startTools: Set<String> = [
        "ouroboros_start_execute_seed", "ouroboros_start_auto", "ouroboros_start_evolve_step", "ouroboros_start_ralph",
    ]

    /// Claude names an MCP tool `mcp__<server>__<tool>`, Codex `<server>.<tool>`.
    static func shortName(_ wireName: String) -> String {
        var name = wireName
        if let range = name.range(of: "__", options: .backwards) { name = String(name[range.upperBound...]) }
        if let range = name.range(of: ".", options: .backwards) { name = String(name[range.upperBound...]) }
        return name
    }

    /// Ids go into URLs (as a query item) and a process argument of their
    /// own: letters, digits, `_`, `-` and `:` (an evolve generation is
    /// `evolve:<lineage>:generation:<n>`) only, never led by `-` or `:`, so
    /// none can read as an option or a path.
    public static func validID(_ value: String) -> Bool {
        value.range(of: "\\A[A-Za-z0-9][A-Za-z0-9_:-]{0,199}\\z", options: .regularExpression) != nil
    }

    /// What a start prints while it has no id yet (`Execution ID: None` from
    /// an evolve step whose generation is claimed later).
    static let placeholders: Set<String> = ["pending", "none", "null"]

    /// The labelled lines of one start tool's result text. A placeholder and
    /// anything that is not a strict id count as absent.
    public static func parse(result text: String, tool: String, runID: String, timestamp: String = "") -> OuroborosExecutionLink? {
        var values: [String: String] = [:]
        for raw in text.split(whereSeparator: \.isNewline).prefix(400) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let colon = line.firstIndex(of: ":") else { continue }
            let label = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard !value.isEmpty, !value.contains(" "), values[label] == nil else { continue }
            values[label] = value
        }
        func id(_ labels: String...) -> String? {
            labels.lazy.compactMap { values[$0] }.first { !placeholders.contains($0.lowercased()) && validID($0) }
        }
        let dashboard = values["live dashboard"].flatMap(URL.init(string:)).flatMap { OuroborosDashboard.endpoint(dashboardURL: $0) == nil ? nil : $0 }
        // A run URL names the execution even when the text still says pending.
        let runParameter = dashboard.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "run" }?.value }
        let execution = id("execution id") ?? runParameter.flatMap { validID($0) && !placeholders.contains($0.lowercased()) ? $0 : nil }
        let job = id("job id", "job_id")
        guard execution != nil || job != nil else { return nil }
        return OuroborosExecutionLink(runID: runID, tool: tool, jobID: job, sessionID: id("session id", "auto_session_id"),
                                      executionID: execution, dashboardURL: dashboard, timestamp: timestamp)
    }

    /// Every execution a start tool reported in these runs, once each, on the
    /// first run that reported it. A failed start named nothing that runs.
    public static func extract(from runs: [MightyGraphRun]) -> [OuroborosExecutionLink] {
        var seen = Set<String>()
        var links: [OuroborosExecutionLink] = []
        // Called on every graph render: the cheap name test runs first.
        func scan(_ entries: [LogEntry], runID: String) {
            for entry in entries {
                guard let activity = entry.activity, activity.state == "completed", let tool = activity.toolName,
                      tool.contains("ouroboros_start_"), let output = activity.output else { continue }
                let short = shortName(tool)
                guard startTools.contains(short),
                      let link = parse(result: output, tool: short, runID: runID, timestamp: entry.timestamp),
                      seen.insert(link.key).inserted else { continue }
                links.append(link)
            }
        }
        for run in runs {
            scan(run.rootEntries, runID: run.id)
            for agent in run.agents { scan(agent.entries, runID: run.id) }
        }
        return links
    }
}

// MARK: - Dashboard payloads

public enum OuroborosExecutionStatus: String, Sendable, Equatable {
    case running, paused, completed, failed, cancelled, unknown
    public init(wire: String?) { self = wire.flatMap { Self(rawValue: $0.lowercased()) } ?? .unknown }
    public var isTerminal: Bool { [.completed, .failed, .cancelled].contains(self) }
}

public struct OuroborosACItem: Sendable, Equatable, Identifiable {
    public enum Status: String, Sendable { case pending, executing, completed, failed }
    public var id: String
    public var title: String
    public var status: Status
    public var depth: Int
    public init(id: String, title: String, status: Status, depth: Int = 0) {
        self.id = id; self.title = title; self.status = status; self.depth = depth
    }
}

/// One row of `/api/runs`. Every field but the id may be missing.
public struct OuroborosRunSummary: Sendable, Equatable {
    public var executionID: String
    public var status: OuroborosExecutionStatus
    public var goal: String?
    public var phase: String?
    public var activity: String?
    public var completed: Int?
    public var total: Int?
    public var pending: Int?
    public var executing: Int?
    public var failed: Int?
}

/// The Kanban board one `/events` frame carries.
public struct OuroborosBoard: Sendable, Equatable {
    public var goal: String?
    public var phase: String?
    public var activity: String?
    public var completed: Int?
    public var total: Int?
    public var items: [OuroborosACItem]
    public func count(_ status: OuroborosACItem.Status) -> Int { items.filter { $0.status == status }.count }
}

public struct OuroborosDashboardEndpoint: Sendable, Equatable {
    public var host: String
    public var port: Int
    /// `path` starts with "/"; `run` is only set when it is a strict id.
    public func url(_ path: String, run: String? = nil) -> URL? {
        var components = URLComponents(string: "http://" + (host.contains(":") ? "[" + host + "]" : host) + ":\(port)")
        components?.path = path
        if let run { components?.queryItems = [URLQueryItem(name: "run", value: run)] }
        return components?.url
    }
}

/// What a graph block shows for one execution.
public struct OuroborosExecutionSnapshot: Sendable, Equatable {
    public enum Source: Sendable, Equatable {
        /// The dashboard answered.
        case live
        /// Nothing answered on loopback.
        case unreachable
        /// The dashboard answered but could not read its runs (a non-200 such
        /// as 503 `picker_index_contract_unavailable`). It is up: never
        /// started again for this.
        case unreadable
        /// The start named only a job: there is no execution to look up yet.
        case noExecution
    }
    public var source: Source
    public var status: OuroborosExecutionStatus
    public var goal: String?
    public var phase: String?
    public var activity: String?
    public var completed: Int?
    public var total: Int?
    public var pending: Int?
    public var executing: Int?
    public var failed: Int?
    /// nil until a board was read; the list is only read while it is open.
    public var items: [OuroborosACItem]?
    public var dashboardURL: URL?
    public init(source: Source, status: OuroborosExecutionStatus = .unknown, goal: String? = nil, phase: String? = nil, activity: String? = nil,
                completed: Int? = nil, total: Int? = nil, pending: Int? = nil, executing: Int? = nil, failed: Int? = nil,
                items: [OuroborosACItem]? = nil, dashboardURL: URL? = nil) {
        self.source = source; self.status = status; self.goal = goal; self.phase = phase; self.activity = activity
        self.completed = completed; self.total = total; self.pending = pending; self.executing = executing; self.failed = failed
        self.items = items; self.dashboardURL = dashboardURL
    }

    /// The summary owns status and counts; a board fills what it lacks and
    /// alone supplies the AC list. Neither is a contract, so either may be nil.
    public static func merge(summary: OuroborosRunSummary?, board: OuroborosBoard?, previousItems: [OuroborosACItem]?,
                             dashboardURL: URL?) -> Self {
        func text(_ value: String?) -> String? {
            guard let value else { return nil }
            let clean = ActivitySupport.clean(value, maximumBytes: 600, singleLine: true)
            return clean.isEmpty ? nil : clean
        }
        return Self(source: .live, status: summary?.status ?? board.map(settledStatus) ?? .unknown,
                    goal: text(summary?.goal ?? board?.goal), phase: text(summary?.phase ?? board?.phase),
                    activity: text(summary?.activity ?? board?.activity),
                    completed: summary?.completed ?? board?.completed ?? board.map { $0.count(.completed) },
                    total: summary?.total ?? board.map { ($0.total ?? 0) > 0 ? $0.total! : $0.items.count },
                    pending: summary?.pending ?? board.map { $0.count(.pending) },
                    executing: summary?.executing ?? board.map { $0.count(.executing) },
                    failed: summary?.failed ?? board.map { $0.count(.failed) },
                    items: board?.items ?? previousItems, dashboardURL: dashboardURL)
    }

    /// A run `/api/runs` no longer lists (it keeps only the newest) has no
    /// status but its board: with nothing pending or executing and every AC
    /// settled, it failed when any AC failed and completed otherwise — the
    /// dashboard's own fallback. Anything else stays unknown.
    static func settledStatus(_ board: OuroborosBoard) -> OuroborosExecutionStatus {
        let completed = board.count(.completed), failed = board.count(.failed)
        guard board.count(.pending) == 0, board.count(.executing) == 0, completed + failed > 0,
              completed + failed >= (board.total ?? 0) else { return .unknown }
        return failed > 0 ? .failed : .completed
    }
}

public enum OuroborosDashboard {
    public static func isLoopback(host: String) -> Bool {
        ["127.0.0.1", "localhost", "::1", "[::1]"].contains(host.lowercased())
    }

    /// A `Live Dashboard:` URL is agent-influenced text: only plain http on a
    /// loopback host with a real port is ever connected to.
    public static func endpoint(dashboardURL url: URL) -> OuroborosDashboardEndpoint? {
        guard url.scheme?.lowercased() == "http", url.user == nil, url.password == nil,
              let host = url.host, isLoopback(host: host), let port = url.port, (1...65_535).contains(port) else { return nil }
        return OuroborosDashboardEndpoint(host: host.hasPrefix("[") ? String(host.dropFirst().dropLast()) : host, port: port)
    }

    /// `~/.ouroboros/dashboard.json`. A daemon bound to every interface is
    /// still reached on loopback, as its own health probe does.
    public static func endpoint(stateJSON data: Data) -> OuroborosDashboardEndpoint? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let port = object["port"] as? Int, (1...65_535).contains(port) else { return nil }
        let raw = object["host"] as? String ?? "127.0.0.1"
        let host = ["", "0.0.0.0"].contains(raw) ? "127.0.0.1" : raw
        guard isLoopback(host: host) else { return nil }
        return OuroborosDashboardEndpoint(host: host, port: port)
    }

    public static func runsURL(_ endpoint: OuroborosDashboardEndpoint) -> URL? { endpoint.url("/api/runs") }
    public static func eventsURL(_ endpoint: OuroborosDashboardEndpoint, executionID: String) -> URL? {
        guard OuroborosExecutionLinks.validID(executionID) else { return nil }
        return endpoint.url("/events", run: executionID)
    }
    /// The page the "open dashboard" button shows: the run's board, or the
    /// run picker when the start named no execution.
    public static func pageURL(_ endpoint: OuroborosDashboardEndpoint, executionID: String?) -> URL? {
        endpoint.url("/", run: executionID.flatMap { OuroborosExecutionLinks.validID($0) ? $0 : nil })
    }

    static func count(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double.isFinite, double >= 0, double <= 1_000_000 else { return nil }
        return Int(double)
    }
    static func string(_ value: Any?) -> String? {
        guard let text = value as? String, !text.isEmpty else { return nil }
        return String(text.prefix(2_000))
    }

    /// `/api/runs` keyed by execution id; nil when the body is not that shape.
    public static func summaries(_ data: Data) -> [String: OuroborosRunSummary]? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let runs = object["runs"] as? [Any] else { return nil }
        var result: [String: OuroborosRunSummary] = [:]
        for case let run as [String: Any] in runs.prefix(500) {
            guard let id = run["execution_id"] as? String, OuroborosExecutionLinks.validID(id), result[id] == nil else { continue }
            result[id] = OuroborosRunSummary(executionID: id, status: OuroborosExecutionStatus(wire: run["status"] as? String),
                                             goal: string(run["goal"]), phase: string(run["phase"]), activity: string(run["activity"]),
                                             completed: count(run["completed_count"]), total: count(run["total_count"]),
                                             pending: count(run["pending_count"]), executing: count(run["executing_count"]),
                                             failed: count(run["failed_count"]))
        }
        return result
    }

    /// Complete SSE events' `data` in order. Comments and other fields are
    /// skipped; a trailing event without its blank line is not complete yet.
    public static func sseData(_ text: String) -> [String] {
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        var events = normalized.components(separatedBy: "\n\n")
        events.removeLast()
        return events.compactMap { event in
            let lines = event.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line -> Substring? in
                guard line.hasPrefix("data:") else { return nil }
                let value = line.dropFirst(5)
                return value.hasPrefix(" ") ? value.dropFirst() : value
            }
            return lines.isEmpty ? nil : lines.joined(separator: "\n")
        }
    }

    /// One board frame. Cards the reducer put in no known column are kept as
    /// executing, as the dashboard itself shows them.
    public static func board(_ data: Data) -> OuroborosBoard? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let columns = object["columns"] as? [String: Any] else { return nil }
        let meta = object["meta"] as? [String: Any] ?? [:]
        var items: [(index: Int, item: OuroborosACItem)] = []
        var ids = Set<String>()
        for (column, value) in columns {
            let status = OuroborosACItem.Status(rawValue: column) ?? .executing
            for case let card as [String: Any] in (value as? [Any] ?? []).prefix(1_000) {
                guard let id = string(card["id"]), ids.insert(id).inserted else { continue }
                let title = ActivitySupport.clean(string(card["title"]) ?? id, maximumBytes: 400, singleLine: true)
                items.append((count(card["ac_index"]) ?? Int.max,
                              OuroborosACItem(id: id, title: title.isEmpty ? id : title, status: status, depth: min(4, count(card["depth"]) ?? 0))))
            }
        }
        items.sort { ($0.index, $0.item.depth, $0.item.id) < ($1.index, $1.item.depth, $1.item.id) }
        return OuroborosBoard(goal: string(meta["goal"]), phase: string(meta["phase"]), activity: string(meta["activity"]),
                              completed: count(meta["completed"]), total: count(meta["total"]), items: items.map(\.item))
    }
}

// MARK: - Polling

/// When one execution is read next. Pure: the caller supplies the clock and
/// what the last read saw.
public struct OuroborosExecutionPoll: Sendable, Equatable {
    public static let fastInterval: TimeInterval = 3
    public static let slowInterval: TimeInterval = 15
    /// No change for this long moves to the slow interval.
    public static let quietAfter: TimeInterval = 60
    /// A start older than this is history: the daemon is not started for it
    /// and, when nothing answers, it is read once and left.
    public static let staleAfter: TimeInterval = 12 * 60 * 60
    /// This many reads in a row that could not tell the status (a run the
    /// dashboard neither lists nor has a settled board for, or no answer)
    /// move to `unknownInterval`; `unknownStopAfter` of them stop the block.
    public static let unknownBackoffAfter = 10
    public static let unknownInterval: TimeInterval = 180
    public static let unknownStopAfter = 40

    public private(set) var nextAt: Date?
    public private(set) var lastChange: Date
    public private(set) var last: OuroborosExecutionSnapshot?
    /// Reads in a row whose status was unknown.
    public private(set) var unknownReads = 0
    public let stale: Bool

    public init(now: Date, stale: Bool) { nextAt = now; lastChange = now; self.stale = stale }

    public var stopped: Bool { nextAt == nil }
    public func isDue(_ now: Date) -> Bool { nextAt.map { $0 <= now } ?? false }

    /// A terminal status ends polling; so does a job-only start (nothing to
    /// look up), a stale start that nothing answers for, and a long run of
    /// reads that never told the status.
    public mutating func record(_ snapshot: OuroborosExecutionSnapshot, now: Date) {
        if snapshot != last { lastChange = now; last = snapshot }
        unknownReads = snapshot.status == .unknown ? unknownReads + 1 : 0
        if snapshot.status.isTerminal || snapshot.source == .noExecution || (stale && snapshot.status == .unknown)
            || unknownReads >= Self.unknownStopAfter {
            nextAt = nil
            return
        }
        let interval = unknownReads >= Self.unknownBackoffAfter ? Self.unknownInterval
            : now.timeIntervalSince(lastChange) >= Self.quietAfter ? Self.slowInterval : Self.fastInterval
        nextAt = now.addingTimeInterval(interval)
    }

    /// Whether this block may ask for the daemon at all: a live execution
    /// still being read, never history. How often is `OuroborosDaemonStarts`'.
    public var mayEnsureDaemon: Bool { !stale && !stopped && last?.status.isTerminal != true }
    /// One more read for a stopped or waiting block, such as an AC list
    /// opened with nothing read yet; `record` stops it again if it was over.
    public mutating func wake(_ now: Date) { nextAt = now }

    public static func isStale(timestamp: String, now: Date) -> Bool {
        let fractional = ISO8601DateFormatter(), plain = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let date = fractional.date(from: timestamp) ?? plain.date(from: timestamp) else { return true }
        return now.timeIntervalSince(date) > staleAfter
    }
}

/// Daemon starts the graph made on its own, process-wide and keyed by
/// execution id, so a graph that leaves the screen and comes back (a tab
/// switch makes a new view model) cannot start it again sooner.
public actor OuroborosDaemonStarts {
    public static let shared = OuroborosDaemonStarts()
    public static let cooldown: TimeInterval = 300
    public static let maximum = 3
    private var starts: [String: (at: Date, count: Int)] = [:]

    public init() {}

    /// Takes one start for `executionID` when its last was at least
    /// `cooldown` ago and it has had fewer than `maximum`; false otherwise.
    public func claim(_ executionID: String, now: Date) -> Bool {
        let previous = starts[executionID]
        guard (previous?.count ?? 0) < Self.maximum, previous.map({ now.timeIntervalSince($0.at) >= Self.cooldown }) ?? true else { return false }
        starts[executionID] = (now, (previous?.count ?? 0) + 1)
        return true
    }
}

// MARK: - Client

/// GETs against the loopback dashboard. Tests inject a fake.
public protocol OuroborosDashboardTransport: Sendable {
    /// Status and body, or nil when nothing answered in time.
    func get(_ url: URL, timeout: TimeInterval) async -> (status: Int, body: Data)?
    /// Read an SSE response until it has been quiet for `quiet` after its
    /// first event, no event came within `firstEvent`, `deadline` passed or
    /// `maximumBytes` arrived; then close it.
    func readEvents(_ url: URL, firstEvent: TimeInterval, quiet: TimeInterval, deadline: TimeInterval, maximumBytes: Int) async -> Data?
}

/// What `/api/runs` said.
public enum OuroborosRunsAnswer: Sendable, Equatable {
    /// Nothing answered on loopback: the daemon is not up.
    case noAnswer
    /// Something answered, but not with runs (a non-200 such as 503, or
    /// another body). The daemon is up; starting it again cannot help.
    case unreadable(status: Int)
    case runs([String: OuroborosRunSummary])

    public var summaries: [String: OuroborosRunSummary]? {
        if case .runs(let summaries) = self { return summaries }
        return nil
    }
}

public struct OuroborosDashboardClient: Sendable {
    /// One daemon start: a fixed executable, argument array, environment and
    /// working directory, and no shell.
    public struct Launch: Sendable, Equatable {
        public var executable: URL
        public var arguments: [String]
        public var environment: [String: String]
        public var cwd: URL
        public init(executable: URL, arguments: [String], environment: [String: String], cwd: URL) {
            self.executable = executable; self.arguments = arguments; self.environment = environment; self.cwd = cwd
        }
    }
    public typealias Launcher = @Sendable (Launch) async -> Bool
    /// Board streams one tick reads at once.
    public static let boardConcurrency = 3
    /// A stream that sent no event by then has nothing to show yet.
    public static let firstEventDeadline: TimeInterval = 2
    /// The daemon runs from a directory no agent can write: `python -m`
    /// would otherwise put the working directory first on `sys.path`, and the
    /// detached daemon inherits it.
    public static let daemonDirectory = URL(fileURLWithPath: "/", isDirectory: true)

    public let transport: any OuroborosDashboardTransport
    public let home: URL
    public let stateURL: URL
    public let pythonCandidates: [URL]
    public let launcher: Launcher

    public init(transport: any OuroborosDashboardTransport = LoopbackDashboardTransport(),
                home: URL = FileManager.default.homeDirectoryForCurrentUser,
                launcher: @escaping Launcher = OuroborosDashboardClient.capture) {
        self.transport = transport
        self.home = home
        stateURL = home.appendingPathComponent(".ouroboros/dashboard.json")
        // The tool environment Ouroboros is installed into (uv, then pipx).
        pythonCandidates = [".local/share/uv/tools/ouroboros-ai/bin/python", ".local/pipx/venvs/ouroboros-ai/bin/python"]
            .map { home.appendingPathComponent($0) }
        self.launcher = launcher
    }

    public static let capture: Launcher = { launch in
        (try? await ProcessCapture.run(executable: launch.executable, arguments: launch.arguments, environment: launch.environment,
                                       cwd: launch.cwd, timeout: 20, maximumBytes: 64 * 1024))?.exitCode == 0
    }

    /// The running daemon's own record first; a link's URL is only a hint.
    /// Async so the file is read off the caller's actor.
    public func endpoint(hint: URL?) async -> OuroborosDashboardEndpoint? {
        if let data = try? Data(contentsOf: stateURL, options: .uncached), data.count < 64 * 1024,
           let endpoint = OuroborosDashboard.endpoint(stateJSON: data) { return endpoint }
        return hint.flatMap { OuroborosDashboard.endpoint(dashboardURL: $0) }
    }

    public func summaries(_ endpoint: OuroborosDashboardEndpoint) async -> OuroborosRunsAnswer {
        guard let url = OuroborosDashboard.runsURL(endpoint), let response = await transport.get(url, timeout: 4) else { return .noAnswer }
        guard response.status == 200, let summaries = OuroborosDashboard.summaries(response.body) else { return .unreadable(status: response.status) }
        return .runs(summaries)
    }

    /// The newest complete board the stream sent before it went quiet.
    public func board(_ endpoint: OuroborosDashboardEndpoint, executionID: String) async -> OuroborosBoard? {
        guard let url = OuroborosDashboard.eventsURL(endpoint, executionID: executionID),
              let data = await transport.readEvents(url, firstEvent: Self.firstEventDeadline, quiet: 1.2, deadline: 8,
                                                    maximumBytes: 8 * 1024 * 1024) else { return nil }
        return OuroborosDashboard.sseData(String(decoding: data, as: UTF8.self)).last.flatMap { OuroborosDashboard.board(Data($0.utf8)) }
    }

    /// Boards for several executions, at most `boardConcurrency` streams open
    /// at once, so one quiet run does not hold up the others.
    public func boards(_ endpoint: OuroborosDashboardEndpoint, executionIDs: [String]) async -> [String: OuroborosBoard] {
        var seen = Set<String>()
        var remaining = executionIDs.filter { seen.insert($0).inserted }.makeIterator()
        var result: [String: OuroborosBoard] = [:]
        await withTaskGroup(of: (String, OuroborosBoard?).self) { group in
            func add() -> Bool {
                guard let id = remaining.next() else { return false }
                group.addTask { (id, await self.board(endpoint, executionID: id)) }
                return true
            }
            for _ in 0..<Self.boardConcurrency { guard add() else { break } }
            while let (id, board) = await group.next() {
                if let board { result[id] = board }
                _ = add()
            }
        }
        return result
    }

    /// Argument array for the tool python; nil for an id that is not strict.
    /// `-I` keeps Python from reading `PYTHON*` variables, the user site and
    /// the working directory; the id is its own argument after `--run`.
    public static func ensureArguments(executionID: String?) -> [String]? {
        guard let executionID else { return ["-I", "-m", "ouroboros.dashboard_web"] }
        guard OuroborosExecutionLinks.validID(executionID) else { return nil }
        return ["-I", "-m", "ouroboros.dashboard_web", "--run", executionID]
    }

    /// Only what Python and Ouroboros need, never the app's whole environment
    /// (an agent's `PYTHONPATH` and the like). The two `PYTHON*` flags reach
    /// the detached daemon, which Ouroboros starts without `-I`.
    public static func daemonEnvironment(home: URL, inherited: [String: String]) -> [String: String] {
        var environment = ["HOME": home.path, "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "PYTHONSAFEPATH": "1", "PYTHONNOUSERSITE": "1"]
        for key in ["LANG", "LC_ALL", "TMPDIR"] { if let value = inherited[key], !value.isEmpty { environment[key] = value } }
        if environment["LANG"] == nil, environment["LC_ALL"] == nil { environment["LANG"] = "en_US.UTF-8" }
        return environment
    }

    /// Ouroboros' own singleton election brings the daemon up (or reuses it);
    /// no shell, a fixed argument array and environment, and a timeout.
    public func ensureDaemon(executionID: String?) async -> Bool {
        guard let arguments = Self.ensureArguments(executionID: executionID),
              let python = pythonCandidates.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) else { return false }
        return await launcher(Launch(executable: python, arguments: arguments,
                                     environment: Self.daemonEnvironment(home: home, inherited: ProcessInfo.processInfo.environment),
                                     cwd: Self.daemonDirectory))
    }
}

/// URLSession against loopback only: no proxy, no cache, no cookies and no
/// redirects, so a response can never send the app anywhere else.
public struct LoopbackDashboardTransport: OuroborosDashboardTransport {
    public init() {}
    public func get(_ url: URL, timeout: TimeInterval) async -> (status: Int, body: Data)? {
        await LoopbackFetch(url: url, timeout: timeout, quiet: nil, firstEvent: nil, maximumBytes: 4 * 1024 * 1024).run()
    }
    public func readEvents(_ url: URL, firstEvent: TimeInterval, quiet: TimeInterval, deadline: TimeInterval, maximumBytes: Int) async -> Data? {
        guard let response = await LoopbackFetch(url: url, timeout: deadline, quiet: quiet, firstEvent: firstEvent, maximumBytes: maximumBytes).run(),
              response.status == 200 else { return nil }
        return response.body
    }
}

private final class LoopbackFetch: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let url: URL
    private let timeout: TimeInterval
    private let quiet: TimeInterval?
    /// Event streams only: no event by then ends the read.
    private let firstEvent: TimeInterval?
    private let maximumBytes: Int
    private let lock = NSLock()
    private var body = Data()
    private var status = 0
    private var lastEventAt: Date?
    private var done = false
    private var continuation: CheckedContinuation<(status: Int, body: Data)?, Never>?
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var watchdog: Task<Void, Never>?

    init(url: URL, timeout: TimeInterval, quiet: TimeInterval?, firstEvent: TimeInterval?, maximumBytes: Int) {
        self.url = url; self.timeout = timeout; self.quiet = quiet; self.firstEvent = firstEvent; self.maximumBytes = maximumBytes
    }

    func run() async -> (status: Int, body: Data)? {
        guard OuroborosDashboard.endpoint(dashboardURL: url) != nil else { return nil }
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                let configuration = URLSessionConfiguration.ephemeral
                configuration.connectionProxyDictionary = [:]
                configuration.urlCache = nil
                configuration.httpCookieStorage = nil
                configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
                configuration.timeoutIntervalForRequest = timeout
                configuration.timeoutIntervalForResource = timeout + 1
                let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
                var request = URLRequest(url: url, timeoutInterval: timeout)
                request.httpMethod = "GET"
                // The task exists before the session is published, so a
                // cancel can only ever invalidate a session whose task was
                // already made; both are published and started under one lock.
                let task = session.dataTask(with: request)
                lock.lock()
                // Cancelled before it started: nothing was opened.
                guard !done else { lock.unlock(); session.invalidateAndCancel(); continuation.resume(returning: nil); return }
                self.continuation = continuation; self.session = session; self.task = task
                task.resume()
                lock.unlock()
                let started = Date(), timeout = timeout, quiet = quiet, firstEvent = firstEvent
                let watchdog = Task { [weak self] in
                    while !Task.isCancelled {
                        try? await Task.sleep(nanoseconds: 150_000_000)
                        guard let self else { return }
                        let now = Date(), elapsed = now.timeIntervalSince(started), last = self.lastEvent
                        if elapsed >= timeout { self.finish(complete: quiet != nil); return }
                        if let firstEvent, last == nil, elapsed >= firstEvent { self.finish(complete: true); return }
                        if let quiet, let last, now.timeIntervalSince(last) >= quiet { self.finish(complete: true); return }
                    }
                }
                lock.lock(); self.watchdog = watchdog; let ended = done; lock.unlock()
                if ended { watchdog.cancel() }
            }
        } onCancel: { self.finish(complete: false) }
    }

    private var lastEvent: Date? { lock.lock(); defer { lock.unlock() }; return lastEventAt }

    /// `complete` is false when what arrived must not be used.
    private func finish(complete: Bool) {
        lock.lock()
        guard !done else { lock.unlock(); return }
        done = true
        let continuation = self.continuation, session = self.session, task = self.task, watchdog = self.watchdog
        let value = (status: status, body: body)
        self.continuation = nil; self.session = nil; self.task = nil; self.watchdog = nil
        lock.unlock()
        watchdog?.cancel()
        task?.cancel()
        session?.invalidateAndCancel()
        continuation?.resume(returning: complete && value.status != 0 ? value : nil)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        lock.lock(); status = (response as? HTTPURLResponse)?.statusCode ?? 0; lock.unlock()
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        body.append(data)
        let full = body.count >= maximumBytes
        if quiet != nil, body.range(of: Data("\n\n".utf8), in: max(0, body.count - data.count - 1)..<body.count) != nil { lastEventAt = Date() }
        lock.unlock()
        if full { finish(complete: quiet != nil) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        finish(complete: error == nil || quiet != nil)
    }
}
