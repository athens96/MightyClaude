import CoreGraphics
import Foundation

/// One step of a request's checklist as the role view draws it (`FlowSteps`): the item Claude's task list holds (its
/// TodoWrite list, or its TaskCreate and TaskUpdate tasks) and the times the app saw it start and end. A time is when the
/// app read the change from the stream, never a guess.
public struct FlowStep: Codable, Sendable, Equatable {
    /// The item's task id, else its words: how one list change finds the step of the one before.
    public var key: String
    public var content: String
    public var activeForm: String?
    /// pending, in_progress or completed, as the list says.
    public var status: String
    /// ISO 8601: when the item went in progress, or, for one marked done without that, when the step before it ended.
    public var startedAt: String?
    public var endedAt: String?

    public init(key: String, content: String, activeForm: String? = nil, status: String = "pending", startedAt: String? = nil, endedAt: String? = nil) {
        self.key = key; self.content = content; self.activeForm = activeForm; self.status = status; self.startedAt = startedAt; self.endedAt = endedAt
    }

    public var started: Date? { startedAt.flatMap(AgentRunTiming.parseTimestamp) }
    public var ended: Date? { endedAt.flatMap(AgentRunTiming.parseTimestamp) }
    public var isPending: Bool { status == "pending" }
}

/// A request's steps in the role view of a Claude pane: the checklist Claude keeps while it works, each item a card on the
/// request's spine (요청 → 실행 → 1단계 → 2단계 … → 결과), with the sub-agents and background commands it launched during a
/// step beside that step. What a step holds is what the main session recorded between the step's start and its end.
public enum FlowSteps {
    /// Claude Code's task tools. Run as the app runs it (`--input-format stream-json`, prompts over stdio) it loads them only
    /// when `--allowedTools` names them (2.1.322): without them there is no list and so no plan.
    public static let taskTools = ["TaskCreate", "TaskUpdate", "TaskList", "TaskGet"]

    /// What a Claude run is told on top of its system prompt (`--append-system-prompt`), so a request that does work has a
    /// plan for the role view to draw: its task list, kept up to date. A reply that needs no tool keeps no list and is drawn as
    /// request → result.
    public static let planInstruction = """
    Mighty Code draws your task list as this request's plan. Before any other tool call, create the task list of the steps \
    you will take with TaskCreate (load it with ToolSearch first if it is not loaded yet; use TodoWrite only where the task \
    tools are unavailable): one task per step, in the user's language, even when there are only one or two steps. Then mark \
    each task in_progress with TaskUpdate when you start it and completed as soon as it is done, and add or remove tasks \
    when the plan changes. Only a reply that needs no tool call at all needs no task list.
    """

    /// A step block's node suffix: `step:` and a hash of its key, so a node id never carries the item's words.
    public static let suffix = "step:"
    /// From a spine block's trailing edge to the column of what it launched.
    public static let sideGap: CGFloat = 44
    /// The 실행 card and a step's card: as wide as the request above them, so their one line reads.
    public static let cardSize = CGSize(width: MightyGraphCamera.requestWidth, height: FlowCard.size.height)

    /// The newest thing the main session did among `entries`, as a card's one line: what it last said (prose), or the
    /// tool call it made after that (a command, as the transcript words it).
    static func latest(_ entries: [LogEntry]) -> (text: String, prose: Bool)? {
        for entry in entries.reversed() {
            if let activity = entry.activity {
                guard activity.kind != "turn" else { continue }
                if let line = FlowCard.line(activity.summary.isEmpty ? activity.toolName ?? "" : activity.summary) { return (line, false) }
            } else if entry.kind == "assistant", let line = prose(FlowCard.lastLine(entry.text)) {
                return (line, true)
            }
        }
        return nil
    }
    /// A line of Claude's reply without the markdown marks it is written in.
    static func prose(_ raw: String) -> String? {
        var text = raw.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
        while let first = text.first, "#>-* ".contains(first) { text.removeFirst() }
        return FlowCard.line(text)
    }

    public static func key(_ item: TodoItem) -> String { item.id.map { "id:" + $0 } ?? "text:" + item.content }
    public static func nodeSuffix(_ step: FlowStep) -> String { suffix + stableHash(step.key) }

    /// FNV-1a, 64 bits, in hex: the same on every run of the app and on every platform.
    static func stableHash(_ text: String) -> String {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in text.utf8 { hash ^= UInt64(byte); hash = hash &* 0x0000_0100_0000_01b3 }
        return String(hash, radix: 16)
    }

    private static let formatter: ISO8601DateFormatter = {
        let value = ISO8601DateFormatter()
        value.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return value
    }()
    static func stamp(_ date: Date) -> String { formatter.string(from: date) }

    /// The steps a run draws: its own, never a notice run's (a notice turn's changes are its request's).
    public static func steps(_ run: MightyGraphRun) -> [FlowStep] { run.noticeRun == true ? [] : run.steps ?? [] }

    // MARK: The progress panel

    /// The progress panel over the role view's top-left corner (`MightyGraphProgressPanel`): the request the diagram shows,
    /// in brief, and how far its plan got. It stays where it is while the diagram scrolls under it, and reads the request
    /// in view.
    public static let progressWidth: CGFloat = 280
    public static let progressRowHeight: CGFloat = 24
    /// The plan's lines at most: every step when they fit, else one fewer steps and a last line that counts the rest.
    public static let progressVisibleRows = 12

    /// What the progress panel says.
    public struct Progress: Equatable, Sendable {
        public enum State: Equatable, Sendable { case done, current, pending, stopped, failed }
        public struct Row: Equatable, Sendable, Identifiable {
            /// The step's index among the request's steps (`Content.step`).
            public var index: Int
            public var id: String
            /// "3. 버전 커밋"; the step in progress reads its activeForm while it runs.
            public var text: String
            public var state: State
            public var startedAt: Date?
            public var endedAt: Date?
        }
        /// "진행 상황".
        public var title: String
        /// "요청 27": which request the panel is about.
        public var heading: String
        /// The request's first line, cut.
        public var request: String
        /// The request's state, in the diagram's words, and its bucket (`MobileMightySupport.status`).
        public var stateWord: String
        public var status: String
        /// "3/9": the steps done of all of them; nil for a request without a plan.
        public var count: String?
        public var rows: [Row]
        /// Steps the rows leave out, which the panel's last line counts.
        public var hidden: Int
        /// The activity still goes on: the time of the step in progress runs.
        public var running: Bool
    }

    /// The progress of request `runs[index]` (a request, never a notice run), headed `heading` ("요청 27"): its request in
    /// brief, its state, and every step of its plan with its state, the one in progress kept in view when there are more
    /// than the panel holds. A request without a plan has no rows.
    public static func progress(_ index: Int, in runs: [MightyGraphRun], heading: String) -> Progress {
        let request = runs[index]
        let steps = Self.steps(request)
        let activity = activityRuns(index, in: runs)
        let awaiting = BackgroundNoticeRuns.awaitingNotice(request, in: activity) != nil
        let running = activity.contains { !MightyGraphLayout.terminal($0.status) } || awaiting
        let rows = steps.enumerated().map { offset, step -> Progress.Row in
            let state: Progress.State
            switch step.status {
            case "completed": state = .done
            case "in_progress":
                switch self.state(step, in: activity) {
                case "running": state = .current
                case "error": state = .failed
                case "completed": state = .done
                default: state = .stopped
                }
            default: state = .pending
            }
            let words = state == .current ? step.activeForm ?? step.content : step.content
            return Progress.Row(index: offset, id: step.key, text: L("graph.step.title", ["n": "\(offset + 1)", "text": words]), state: state,
                                startedAt: step.started, endedAt: step.ended ?? (state == .stopped || state == .failed ? activityEnd(index, in: runs) : nil))
        }
        let room = rows.count <= progressVisibleRows ? progressVisibleRows : progressVisibleRows - 1
        let focus = rows.firstIndex { $0.state == .current } ?? rows.lastIndex { $0.state == .done } ?? 0
        let start = rows.count <= room ? 0 : max(0, min(focus - room / 3, rows.count - room))
        let shown = Array(rows[start..<min(rows.count, start + room)])
        let done = rows.filter { $0.state == .done }.count
        let status = running ? "running" : MobileMightySupport.status(request.status)
        return Progress(title: L("graph.progress.title"), heading: heading, request: FlowCard.line(FlowCard.firstLine(request.input)) ?? "",
                        stateWord: awaiting ? BackgroundNoticeRuns.stateWord : FlowCard.stateWord(status), status: awaiting ? "waiting" : status,
                        count: rows.isEmpty ? nil : L("graph.steps.count", ["done": "\(done)", "total": "\(rows.count)"]),
                        rows: shown, hidden: rows.count - shown.count, running: running)
    }

    /// The step a block launched from the main session belongs to, as its card says it ("2. 노트 작성"): the step whose span
    /// its launch time falls in. Nil for one launched outside every step, one under another block, and in a run without steps.
    public static func stepLabel(_ agent: MightyGraphAgent, in run: MightyGraphRun) -> String? {
        if let parent = agent.parentID, parent != agent.id, run.flowAgents.contains(where: { $0.id == parent }) { return nil }
        let started = steps(run).enumerated().filter { !$0.element.isPending }
        guard !started.isEmpty else { return nil }
        let slot = Self.slot(of: agent, steps: started.map(\.element))
        guard slot > 0 else { return nil }
        let (index, step) = started[slot - 1]
        return L("graph.step.title", ["n": "\(index + 1)", "text": step.content])
    }

    // MARK: Recording

    /// A checklist change (`TodoProgress`, named by the request whose stream sent it) applied to that request's run:
    /// each item keeps the times of the step it was, an item that goes in progress starts now, one that is done ends now.
    /// `previous` is the pane's list before the change: the items it already had done when this request first sent a
    /// list are the earlier request's, which Claude's task list keeps, and never this request's steps.
    public static func record(_ progress: TodoProgress, into runs: inout [MightyGraphRun], previous: TodoProgress?, at now: Date) {
        guard let runID = progress.runId,
              let index = runs.lastIndex(where: { $0.noticeRun != true && ($0.sourceRunID == runID || $0.id == runID) }) else { return }
        var run = runs[index]
        if run.steps == nil {
            let carried = previous.flatMap { $0.runId == runID ? nil : $0.items.filter { $0.status == "completed" }.map(key) } ?? []
            run.stepsCarried = carried.isEmpty ? nil : carried
        }
        let carried = Set(run.stepsCarried ?? [])
        let old = Dictionary((run.steps ?? []).map { ($0.key, $0) }, uniquingKeysWith: { first, _ in first })
        let time = stamp(now)
        // The latest a step ended before this change: where a step marked done without going in progress began.
        var lastEnd = (run.steps ?? []).compactMap(\.endedAt).max { (AgentRunTiming.parseTimestamp($0) ?? .distantPast) < (AgentRunTiming.parseTimestamp($1) ?? .distantPast) }
        var steps: [FlowStep] = []
        for item in progress.items where !carried.contains(key(item)) {
            var step = old[key(item)] ?? FlowStep(key: key(item), content: item.content)
            step.content = item.content
            step.activeForm = item.activeForm
            switch item.status {
            case "in_progress":
                if step.startedAt == nil || step.status == "pending" { step.startedAt = time }
                step.endedAt = nil
            case "completed":
                if step.startedAt == nil { step.startedAt = lastEnd ?? run.rootEntries.first?.timestamp ?? time }
                if step.endedAt == nil { step.endedAt = time; lastEnd = time }
            default:
                step.startedAt = nil; step.endedAt = nil
            }
            step.status = item.status
            steps.append(step)
        }
        run.steps = steps
        runs[index] = run
    }

    // MARK: What a step holds

    /// The step of `steps` whose span holds `time`, the one that started last when spans meet; nil for the main
    /// session's own time between steps. A step's start counts from its whole second, as the records' times do.
    public static func stepIndex(at time: Date, in steps: [FlowStep]) -> Int? {
        var best: (index: Int, start: Date)?
        for (index, step) in steps.enumerated() {
            guard let start = step.started, start.timeIntervalSince1970.rounded(.down) <= time.timeIntervalSince1970 else { continue }
            if let end = step.ended, end < time { continue }
            if best == nil || start >= best!.start { best = (index, start) }
        }
        return best?.index
    }

    /// Where on the spine a block launched from the main session stands: 0 beside the 실행 block, k beside step k.
    public static func slot(of agent: MightyGraphAgent, steps: [FlowStep]) -> Int {
        guard let start = agent.figures?.started, let index = stepIndex(at: start, in: steps) else { return 0 }
        return index + 1
    }

    /// The runs of the activity `runs[index]` heads: the request and the notice runs that answer for its work.
    public static func activityRuns(_ index: Int, in runs: [MightyGraphRun]) -> [MightyGraphRun] {
        let request = runs[index]
        guard let source = request.sourceRunID else { return [request] }
        return [request] + runs.filter { $0.noticeRun == true && $0.sourceRunID.flatMap(BackgroundNoticeRuns.originalRunID) == source }
    }

    /// What the main session recorded during step `step` of `runs[index]`: in the request and in the notice turns after it.
    public static func entries(_ step: Int, of index: Int, in runs: [MightyGraphRun]) -> [LogEntry] {
        let steps = Self.steps(runs[index])
        guard steps.indices.contains(step) else { return [] }
        return activityRuns(index, in: runs).flatMap(\.rootEntries).filter { entry in
            guard let time = AgentRunTiming.parseTimestamp(entry.timestamp) else { return false }
            return stepIndex(at: time, in: steps) == step
        }
    }

    // MARK: Cards

    /// A step as its card: its number and words, the main session's model, what it is doing (or did) last, its state,
    /// time and tool calls. A step not begun says so and has no figures.
    public static func card(_ step: Int, of index: Int, in runs: [MightyGraphRun]) -> FlowCard.Content? {
        let request = runs[index], steps = Self.steps(request)
        guard steps.indices.contains(step) else { return nil }
        let item = steps[step]
        let entries = Self.entries(step, of: index, in: runs)
        let calls = entries.filter { $0.activity.map { $0.kind != "turn" } ?? false }
        let status = state(item, in: Self.activityRuns(index, in: runs))
        let running = !MightyGraphLayout.terminal(status) && !item.isPending
        let newest = latest(entries) ?? (running ? item.activeForm.flatMap(FlowCard.line).map { ($0, true) } : nil)
        let activity = item.isPending ? nil : newest
        let ended = item.ended ?? (MightyGraphLayout.terminal(status) && !item.isPending ? activityEnd(index, in: runs) : nil)
        var content = FlowCard.Content(title: L("graph.step.title", ["n": "\(step + 1)", "text": item.content]),
                                       identity: FlowLog.leadName(request), activity: activity?.text, status: item.isPending ? "pending" : status,
                                       stateWord: item.isPending ? L("graph.state.pending") : FlowCard.stateWord(status),
                                       startedAt: item.started, endedAt: ended, toolCalls: calls.count, tokens: nil, nested: 0, marks: [],
                                       ink: FlowRoles.ink(.lead), summary: item.content)
        content.isPending = item.isPending
        content.proseActivity = activity?.prose ?? false
        return content
    }

    /// The state a step's card shows: done as the list says; in progress, the state of its activity (still at work,
    /// else how it ended).
    static func state(_ step: FlowStep, in activity: [MightyGraphRun]) -> String {
        if step.status == "completed" { return "completed" }
        if activity.contains(where: { !MightyGraphLayout.terminal($0.status) }) { return "running" }
        if let request = activity.first, BackgroundNoticeRuns.awaitingNotice(request, in: activity) != nil { return "running" }
        return MobileMightySupport.status(activity.last?.status ?? "completed")
    }

    /// When the activity's last turn ended: the last thing it recorded.
    static func activityEnd(_ index: Int, in runs: [MightyGraphRun]) -> Date? {
        activityRuns(index, in: runs).compactMap { ($0.resultEntries.last ?? $0.rootEntries.last).flatMap { AgentRunTiming.parseTimestamp($0.timestamp) } }.max()
    }

    /// The 실행 block as its card: the main session's model, what it is doing (or answered), its state, time, tool calls
    /// and tokens.
    public static func leadCard(_ run: MightyGraphRun, title: String) -> FlowCard.Content {
        let calls = run.rootEntries.filter { $0.activity.map { $0.kind != "turn" } ?? false }
        let status = MobileMightySupport.status(run.status)
        let running = !MightyGraphLayout.terminal(status)
        let current = steps(run).first { $0.status == "in_progress" }.flatMap { ($0.activeForm ?? $0.content).isEmpty ? nil : FlowCard.line($0.activeForm ?? $0.content) }
        // Running: the newest thing it said or did. Ended: the first line of its answer.
        let answer = run.finalOutput.flatMap { prose(FlowCard.firstLine($0)) }.map { (text: $0, prose: true) }
        let line = running ? latest(run.rootEntries) ?? current.map { (text: $0, prose: true) } : answer ?? latest(run.rootEntries)
        let started = run.rootEntries.first.flatMap { AgentRunTiming.parseTimestamp($0.timestamp) }
        let ended = running ? nil : (run.resultEntries.last ?? run.rootEntries.last).flatMap { AgentRunTiming.parseTimestamp($0.timestamp) }
        var content = FlowCard.Content(title: title, identity: FlowLog.leadName(run), activity: line?.text,
                                       status: status, stateWord: FlowCard.stateWord(status), startedAt: started, endedAt: ended, toolCalls: calls.count,
                                       tokens: run.usage.flatMap { $0.isEmpty ? nil : $0.total }, nested: 0, marks: [], ink: FlowRoles.ink(.lead),
                                       summary: FlowLog.leadName(run))
        content.proseActivity = line?.prose ?? false
        return content
    }
}

extension RunSession {
    /// A checklist change of the main session's, kept on the request it belongs to with the time it came (`FlowSteps.record`).
    /// Read before `recordPlanMode`, which replaces the pane's list with it.
    public mutating func recordSteps(_ event: RunEvent, at now: Date) {
        guard event.type == "todos", kind == "claude", provider == "claude", let progress = TodoProgress.normalized(event.todos),
              var runs = graphRuns, !runs.isEmpty else { return }
        FlowSteps.record(progress, into: &runs, previous: todoProgress, at: now)
        graphRuns = runs
    }
}
