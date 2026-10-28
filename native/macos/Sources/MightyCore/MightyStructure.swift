import CoreGraphics
import Foundation

/// The Mighty graph drawn as its structure (design "A. 단계별 열"): a styled pane's
/// requests under the phase each belongs to, a free pane's as one 요청 → 실행 → 결과 row
/// each, and under every request its sub-agent trees as clusters. The Mac view shows it
/// one board at a time (`boards`); the phone reads the shape as it is.
///
/// It reads the very `[MightyGraphRun]` the diagram and the timeline draw, and the
/// pane's style through the frozen evaluator's public rules only. Nothing here is
/// stored: a request's phase is worked out again from its input on every read, because
/// the pane keeps no request → phase record.
public enum MightyStructure {
    /// Where one phase, or one step of a free request, stands.
    public enum Step: String, Sendable, Equatable, CaseIterable {
        case done, current, pending, error, stopped
    }

    /// One block of a cluster, flattened depth-first.
    public struct Node: Equatable, Sendable, Identifiable {
        public var agentID: String
        /// Index into the agents of the run as views draw it (`MightyGraphRun.shown`).
        public var agentIndex: Int
        /// The diagram node this row stands for (`MightyGraphLayout.Node.id`).
        public var nodeID: String
        /// 0 for the cluster's root.
        public var depth: Int
        public var title: String
        /// Bucketed as a block's status is (`MobileMightySupport.status`).
        public var status: String
        /// `MightyGraphSupport.blockKind`: "agent", "task", or "steer" for a task notice (the other kinds are left out).
        public var kind: String
        /// What the block was asked: the agent's prompt or the task's command.
        public var prompt: Excerpt?
        /// Its latest reply, which is its result once it settled (`recordGraph` appends
        /// the node's output as its last assistant record).
        public var output: Excerpt?
        /// Its last tool calls, oldest first.
        public var activity: [ActivityLine]
        /// The model its first response reported (an id, shortened by the view), if any.
        public var model: String?
        /// A background sub-agent or a task (`MightyGraphAgent.background`).
        public var background = false
        /// Claude's reply to a task's notice (`MightyGraphAgent.noticeReply`).
        public var noticeReply: Excerpt? = nil
        public var id: String { nodeID }
    }

    /// A text a card shows in place: bounded, with the part a folded card shows.
    public struct Excerpt: Equatable, Sendable {
        /// The text up to its cap; a cut text ends with "…".
        public var text: String
        /// The folded card's text: the first lines, bounded, so a folded card never lays
        /// out the whole text. Markdown is cut at whole lines, an open code fence closed.
        public var preview: String

        /// The preview leaves some of the text out.
        public var cut: Bool { preview != text }

        /// Whether a folded card showing `lines` lines of `charactersPerLine` characters
        /// (`MightyStructure.charactersPerLine`) may leave some of the text out: it is
        /// cut, or its lines wrap past the limit. A text that may be cut always gets the
        /// toggle that opens it.
        public func folds(lines: Int, charactersPerLine: Int) -> Bool {
            cut || MightyStructure.visualLines(preview, charactersPerLine: charactersPerLine) > lines
        }
    }

    /// One tool call, as a diagram block's activity row words it.
    public struct ActivityLine: Equatable, Sendable, Identifiable {
        /// The record's id.
        public var id: String
        /// `AgentActivity.kind`, for the row's glyph.
        public var kind: String
        public var state: String
        /// Its summary on one line, else its tool's name (`MobileMightySupport.step`).
        public var text: String
    }

    /// One root sub-agent's tree.
    public struct Cluster: Equatable, Sendable, Identifiable {
        /// The root first, then its descendants depth-first.
        public var nodes: [Node]
        public var id: String { nodes.first?.nodeID ?? "" }
    }

    /// "에이전트 n · 도구 m": what the request holds now, never what is to come.
    public struct Tally: Equatable, Sendable {
        public var agents: Int
        public var tools: Int
    }

    /// One request as a card.
    public struct Item: Equatable, Sendable, Identifiable {
        /// Index into the runs the diagram draws.
        public var runIndex: Int
        public var runID: String
        /// The request's number (`MightyGraphSupport.requestOrdinals`): a notice run adds none and carries the one before it.
        public var ordinal: Int
        /// The request's own diagram node, which holds its input alone.
        public var nodeID: String
        /// The diagram's 실행 block under it, which holds the main agent's work: the
        /// latest reply, the tool calls and the status this card shows as its 실행 part.
        public var executionNodeID: String
        /// The request on one line.
        public var input: String
        /// The request's status, bucketed as a block's is.
        public var status: String
        public var finished: Bool
        /// The final answer on one line, once the request finished with one.
        public var result: String?
        /// The whole request.
        public var request: Excerpt?
        /// The main block's latest reply. Left out once the request finished with that
        /// very reply as its answer, which `answer` shows.
        public var message: Excerpt?
        /// The main block's last tool calls, oldest first.
        public var activity: [ActivityLine]
        /// The final answer, from the diagram's result card's source, once the request finished with one.
        public var answer: Excerpt?
        public var tally: Tally
        public var clusters: [Cluster]
        /// Claude's reply to finished background work (`MightyGraphRun.noticeRun`), titled as such, not a request.
        public var noticeRun = false
        /// A notice run taken into a running turn, without a reply (`MightyGraphRun.quietNotice`): never followed.
        public var quiet = false
        public var id: String { runID }
    }

    /// One phase of a styled pane and the requests under it.
    public struct Column: Equatable, Sendable, Identifiable {
        public var phase: StylePhase
        /// done, current or pending.
        public var step: Step
        /// Oldest first.
        public var items: [Item]
        public var id: String { phase.id }
    }

    /// One request of a free pane as 요청 → 실행 → 결과.
    public struct Row: Equatable, Sendable, Identifiable {
        public var item: Item
        public var request: Step
        public var execution: Step
        public var result: Step
        public var id: String { item.id }
    }

    public enum Shape: Equatable, Sendable {
        /// A style with phases: one column per phase, in order.
        case phases([Column])
        /// No style, or one without phases: one row per request, oldest first.
        case free([Row])
    }

    /// What `make` reads of the pane, cheap to compare: a view keeps the shape it built
    /// while this stays equal, so a streamed line that changes nothing drawn here does
    /// not rebuild it. Text is followed by its length (requests and answers only grow
    /// or appear), records by their count and a digest of their tail — the records a
    /// card reads (its latest reply, its last tool calls) are among the newest, and a
    /// record replaced in place (a reply that grew, a tool call that finished) keeps the
    /// count; a block's title and status as they are.
    public struct Signature: Equatable, Sendable {
        struct Block: Equatable, Sendable {
            var id, title, status: String
            var parentID, kind: String?
            var input, entries, tail, records: Int
            /// Claude's reply to a task's notice, which can land with no new record.
            var notice = 0
        }
        /// One request's part, which is all its card reads (`ItemCache` keys on it).
        struct Request: Equatable, Sendable {
            var id, status: String
            var sourceRunID: String?
            var input, rootEntries, rootTail, resultEntries, result: Int
            var finalOutput: Int?
            var blocks: [Block]
        }
        var requests: [Request]
        var phases: [String]
        var recognition: String?
        var currentPhase: String?
        var startingNew: Bool
        var plans: [String]
        var pendingPlanRunID: String?

        public init(runs: [MightyGraphRun], style: StyleEvaluator?, currentPhase: StylePhase?, startingNew: Bool = false,
                    planHistory: [PlanRecord] = [], pendingPlanRunID: String? = nil) {
            requests = runs.map { run in
                Request(id: run.id, status: run.status, sourceRunID: run.sourceRunID, input: run.input.utf8.count,
                        rootEntries: run.rootEntries.count, rootTail: MightyStructure.tail(run.rootEntries), resultEntries: run.resultEntries.count,
                        result: run.resultEntries.last?.text.utf8.count ?? 0, finalOutput: run.finalOutput?.utf8.count,
                        blocks: run.agents.map {
                            Block(id: $0.id, title: $0.title, status: $0.status, parentID: $0.parentID, kind: $0.kind, input: $0.input.utf8.count,
                                  entries: $0.entries.count, tail: MightyStructure.tail($0.entries), records: $0.responseRecords?.count ?? 0,
                                  notice: $0.noticeReply?.utf8.count ?? 0)
                        })
            }
            phases = style?.manifest.orderedPhases.map(\.id) ?? []
            recognition = style?.manifest.id
            self.currentPhase = currentPhase?.id
            self.startingNew = startingNew
            plans = planHistory.map { [$0.id, $0.outcome.rawValue, $0.graphRunId ?? "", $0.runId].joined(separator: "\u{1F}") }
            self.pendingPlanRunID = pendingPlanRunID
        }
    }

    /// The cards of the last build, each built again only when its own request's part of
    /// the signature moved: while one request streams, the other cards are kept as they
    /// are rather than read again from their records.
    public struct ItemCache: Sendable {
        private var built: [String: (request: Signature.Request, item: Item)] = [:]
        public init() {}

        /// The cards of `runs`, which `signature` was taken of.
        public mutating func items(_ runs: [MightyGraphRun], signature: Signature) -> [Item] {
            var next: [String: (request: Signature.Request, item: Item)] = [:]
            next.reserveCapacity(runs.count)
            let ordinals = MightyGraphSupport.requestOrdinals(runs)
            let items = runs.enumerated().map { index, run in
                let request = index < signature.requests.count ? signature.requests[index] : nil
                var item: Item
                if let request, let kept = built[run.id], kept.request == request {
                    // A trim of older requests moves a kept card's number, nothing else.
                    item = kept.item
                    item.runIndex = index
                    item.ordinal = ordinals[index]
                } else {
                    item = MightyStructure.item(run, index: index, ordinal: ordinals[index])
                }
                if let request { next[run.id] = (request, item) }
                return item
            }
            built = next
            return items
        }
    }

    /// The records a card reads, as one number: how many there are and how many of
    /// them are tool calls that settled (an older call finishing in place moves it),
    /// and the id, kind, text length and tool state and summary length of the last
    /// `tailEntries`. A text's length counts in `tailTextStep` steps, so a reply that
    /// streams in moves it every few lines, not on every word.
    static func tail(_ entries: [LogEntry]) -> Int {
        var hasher = Hasher()
        hasher.combine(entries.count)
        var settled = 0
        for entry in entries where entry.activity.map({ MightyGraphSupport.terminal($0.state) }) == true { settled += 1 }
        hasher.combine(settled)
        for entry in entries.suffix(tailEntries) {
            hasher.combine(entry.id); hasher.combine(entry.kind); hasher.combine(entry.text.utf8.count / tailTextStep)
            hasher.combine(entry.activity?.state); hasher.combine(entry.activity?.summary.utf8.count)
        }
        return hasher.finalize()
    }

    /// The deepest tree level a cluster follows, as the diagram's layout does.
    public static let maximumDepth = 64
    /// The bound of an input or result preview.
    static let previewBytes = 400
    /// The bounds of the texts a card shows in place, and the lines each shows folded.
    public static let requestBytes = 4_096, requestLines = 10
    public static let messageBytes = 4_096, messageLines = 8
    public static let answerBytes = 16_384, answerLines = 15
    public static let promptBytes = 2_048, promptLines = 2
    public static let outputBytes = 4_096, outputLines = 6
    /// The tool calls a request's run card and a sub-agent's card list.
    public static let requestActivity = 5, nodeActivity = 3
    /// The narrowest kanban column; wider panes share their width out between the columns.
    public static let minimumColumnWidth: CGFloat = 280
    /// A folded line is cut at this many characters, so a preview stays small however
    /// long the text's lines run; a Markdown preview holds this many per line it shows.
    static let previewLineCharacters = 200
    /// The newest records the signature follows one by one.
    static let tailEntries = 12
    /// The bytes a streamed text grows by before the signature moves: a few lines.
    static let tailTextStep = 256
    /// A tree's indent stops growing past this depth, so a deep chain stays inside its column.
    public static let maximumIndent = 6
    /// How full a wrapped line is counted: words wrap whole, so a line holds less than
    /// its width's worth of em-wide glyphs (Hangul, kana and CJK are about one em).
    static let wrapFill: CGFloat = 0.75
    /// Activity kinds that are a tool call, not the turn or a sub-agent's own line.
    static let toolKinds: Set<String> = ["tool", "command", "read", "edit", "search", "web"]

    /// The pane as the structure view draws it.
    ///
    /// - `currentPhase`: the pane's own phase, as its phase bar shows it.
    /// - `startingNew`: the user chose to start anew, so `currentPhase` is the phase the
    ///   next request starts in and says nothing of the requests already there.
    /// - `planHistory`: the pane's answered plans; `pendingPlanRunID`: the run id of the
    ///   plan waiting for an answer (`ToolPermissionRequest.runId`), if any.
    /// - `items`: the runs' cards already built (`ItemCache`), in run order.
    ///
    /// The current column is the pane's phase, which is where the live or newest request
    /// sits (or will, once a request that has just started draws its block). A column
    /// that holds a card is never pending: any card outside the current column is a
    /// request that is over with that phase. An empty column is done before the current
    /// one and pending after it.
    public static func make(runs: [MightyGraphRun], style: StyleEvaluator?, currentPhase: StylePhase?, startingNew: Bool = false,
                            planHistory: [PlanRecord] = [], pendingPlanRunID: String? = nil, items built: [Item]? = nil) -> Shape {
        let items = built.flatMap { $0.count == runs.count ? $0 : nil } ?? {
            let ordinals = MightyGraphSupport.requestOrdinals(runs)
            return runs.enumerated().map { item($1, index: $0, ordinal: ordinals[$0]) }
        }()
        guard let style, !style.manifest.phases.isEmpty else { return .free(items.map(row)) }
        let ordered = style.manifest.orderedPhases
        let pane = currentPhase.flatMap { phase in ordered.contains { $0.id == phase.id } ? phase : nil }
        let assigned = phaseIDs(runs: runs, style: style, currentPhase: startingNew ? nil : pane,
                                planHistory: planHistory, pendingPlanRunID: pendingPlanRunID)
        // A pane that names no phase stands where its newest request does.
        let currentIndex = (pane?.id ?? assigned.last).flatMap { id in ordered.firstIndex { $0.id == id } }
        let held = Set(assigned)
        return .phases(ordered.enumerated().map { index, phase in
            let step: Step
            if index == currentIndex { step = .current }
            else if held.contains(phase.id) { step = .done }
            else { step = currentIndex.map { index < $0 ? .done : .pending } ?? .pending }
            return Column(phase: phase, step: step, items: items.indices.filter { assigned[$0] == phase.id }.map { items[$0] })
        })
    }

    /// Each run's phase id, in run order.
    ///
    /// Plan-state styles (§1.17) read each run as `StyleStateEngine.planStage` reads the
    /// pane's latest one: the run whose plan waits for an answer is awaiting approval,
    /// a run whose latest answered plan was approved is executing, and any other run is
    /// planning — the style starts every new request in plan mode. A request that is over
    /// keeps the stage it ended in. The pane's phase is not copied onto the newest run
    /// here: it can describe a run that has not drawn its block yet.
    ///
    /// Every other style, by the first rule that holds:
    /// 1. The newest run takes `currentPhase`, the pane's own phase, so the card and the
    ///    phase bar agree (a file source may have moved the pane on, §1.16).
    /// 2. The phase the evaluator recognises from the run's input (an action with a
    ///    phase, or an alias), as the phase walk reads a prompt.
    /// 3. The previous run's phase.
    /// 4. For the first run, the style's default phase, else its first phase.
    public static func phaseIDs(runs: [MightyGraphRun], style: StyleEvaluator, currentPhase: StylePhase? = nil,
                                planHistory: [PlanRecord] = [], pendingPlanRunID: String? = nil) -> [String] {
        let manifest = style.manifest
        guard let first = manifest.orderedPhases.first else { return [] }
        if case .planState = manifest.rules.phase {
            func phase(_ stage: StylePlanStage) -> String { style.currentPhase(planStage: stage)?.id ?? first.id }
            // The diagram's own match for the pending plan's request (`PlanCardSupport.graphRunID`).
            let waiting = pendingPlanRunID.flatMap { id in runs.lastIndex { $0.sourceRunID == id || $0.id == id } }
            var stages: [String] = []
            for (index, run) in runs.enumerated() {
                // A notice run is no request of its own: it stays in the phase of the run before it.
                if run.noticeRun == true { stages.append(stages.last ?? phase(.planning)); continue }
                if index == waiting { stages.append(phase(.awaitingApproval)); continue }
                switch planHistory.last(where: { planRecord($0, belongsTo: run) })?.outcome {
                case .approvedAuto?, .approvedConfirm?: stages.append(phase(.executing))
                case .revised?, .cancelled?, nil: stages.append(phase(.planning))
                }
            }
            return stages
        }
        // An empty history reads as the style's `default` phase.
        let initial = style.currentPhase(prompts: [])?.id ?? first.id
        let pane = currentPhase.flatMap { manifest.phase($0.id)?.id }
        var result: [String] = []
        // The pane's phase is its newest request's; a notice run after it is not one.
        let newestRequest = MightyGraphSupport.newestRequestIndex(runs)
        for (index, run) in runs.enumerated() {
            // A notice run is no request of its own: it stays in the phase of the run before it.
            if run.noticeRun == true { result.append(result.last ?? initial); continue }
            let newest = index == newestRequest ? pane : nil
            result.append(newest ?? recognisedPhase(run.input, style: style) ?? result.last ?? initial)
        }
        return result
    }

    /// The phase a prompt names, read as `StyleEvaluator.currentPhase(prompts:)` reads
    /// one: phase-less actions and free text name none.
    public static func recognisedPhase(_ input: String, style: StyleEvaluator) -> String? {
        switch style.recognised(inPrompt: input) {
        case .action(let id): return style.action(id)?.phase.flatMap { style.manifest.phase($0)?.id }
        case .alias(_, let phase): return style.manifest.phase(phase)?.id
        case .none: return nil
        }
    }

    /// The match `StyleStateEngine.planStage` uses: a record's `graphRunId` is its run's
    /// `sourceRunID` (or the block id), and an older Mac record names it as its `runId`.
    static func planRecord(_ record: PlanRecord, belongsTo run: MightyGraphRun) -> Bool {
        record.graphRunId == run.id || (run.sourceRunID != nil && (record.graphRunId == run.sourceRunID || record.runId == run.sourceRunID))
    }

    /// A column's width: the columns share what the pane has, but never go narrower
    /// than `minimumColumnWidth` (the view scrolls sideways then).
    public static func columnWidth(available: CGFloat, count: Int, gap: CGFloat) -> CGFloat {
        guard count > 0, available.isFinite else { return minimumColumnWidth }
        return max(minimumColumnWidth, ((available - gap * CGFloat(count - 1)) / CGFloat(count)).rounded(.down))
    }

    /// The fewest characters a wrapped line of `width` points holds at `fontSize`,
    /// counting every glyph an em wide and lines as only partly filled, so a fold is
    /// never missed: Latin text holds more and only shows the toggle sooner.
    public static func charactersPerLine(width: CGFloat, fontSize: CGFloat) -> Int {
        guard width.isFinite, fontSize > 0 else { return 1 }
        return max(1, Int((width / fontSize * wrapFill).rounded(.down)))
    }

    /// The width a sub-agent card's text has in a column: the tree's indent at `depth`
    /// and the card's own padding taken off, as `MightyStructureView` lays them out.
    public static func nodeTextWidth(column: CGFloat, depth: Int) -> CGFloat {
        let S = DesignMetrics.Spacing.self
        return column - CGFloat(min(max(0, depth), maximumIndent)) * S.lg - (S.md + S.xs)
    }

    /// How many lines `text` takes wrapped at `charactersPerLine`.
    static func visualLines(_ text: String, charactersPerLine: Int) -> Int {
        let width = max(1, charactersPerLine)
        var count = 0
        for line in text.split(separator: "\n", omittingEmptySubsequences: false) { count += max(1, (line.count + width - 1) / width) }
        return count
    }

    /// `ordinal`: the request's number (`MightyGraphSupport.requestOrdinals`); nil counts every run before it.
    /// The card reads the run as views draw it (`MightyGraphRun.shown`): background blocks only in a notice run.
    public static func item(_ raw: MightyGraphRun, index: Int, ordinal: Int? = nil) -> Item {
        let run = raw.shown
        let finished = MightyGraphLayout.finished(run)
        let answer = run.resultEntries.last(where: { $0.kind == "assistant" && !$0.text.isEmpty })?.text ?? run.finalOutput
        let result = finished ? answer.map(preview).flatMap { $0.isEmpty ? nil : $0 } : nil
        let full = finished ? answer.flatMap { excerpt($0, maximumBytes: answerBytes, lines: answerLines, markdown: true) } : nil
        // `refreshResult` keeps the final answer in the main block too: shown once, as the answer.
        let latest = reply(run.rootEntries)
        let message = full != nil && latest == answer ? nil : latest.flatMap { excerpt($0, maximumBytes: messageBytes, lines: messageLines) }
        // Counted over the blocks the clusters draw, so a steer's or a question's records add nothing.
        let blocks = kept(run).map { run.agents[$0] }
        let entries = run.rootEntries + blocks.flatMap(\.entries)
        let tally = Tally(agents: blocks.filter { MightyGraphSupport.blockKind($0) == "agent" }.count,
                          tools: entries.filter { $0.activity.map { toolKinds.contains($0.kind) } ?? false }.count)
        return Item(runIndex: index, runID: run.id, ordinal: ordinal ?? index + 1, nodeID: MightyGraphLayout.nodeID(run, suffix: "request"),
                    executionNodeID: MightyGraphLayout.nodeID(run, suffix: MightyGraphLayout.workSuffix),
                    input: preview(run.input), status: MobileMightySupport.status(run.status), finished: finished,
                    result: result, request: excerpt(run.input, maximumBytes: requestBytes, lines: requestLines), message: message,
                    activity: activity(run.rootEntries, limit: requestActivity), answer: full, tally: tally, clusters: clusters(run),
                    noticeRun: run.noticeRun == true, quiet: run.quietNotice)
    }

    /// 요청 is always there; 실행 runs until every block settled, unless the request
    /// failed or was stopped; 결과 waits until the request finished.
    public static func row(_ item: Item) -> Row {
        let ended: Step? = switch item.status {
        case "error": .error
        case "stopped": .stopped
        default: nil
        }
        return Row(item: item, request: .done, execution: ended ?? (item.finished ? .done : .current),
                   result: item.finished ? ended ?? .done : .pending)
    }

    /// One cluster per root of the run's sub-agent trees, by the diagram's rule: an
    /// agent whose parent is missing, unknown or itself is a root, a tree stops at
    /// `maximumDepth`, and whatever a cycle or the cap left out starts a cluster of its
    /// own, so every block shows once.
    ///
    /// Steer, compaction and question blocks are left out. The diagram draws them as
    /// blocks under the request, but they are the conversation's own moments, not work
    /// handed to a sub-agent, and no block is ever their child; tasks (backgrounded
    /// commands) are work, so they stay, and so does a task's notice (a 작업 알림
    /// steer): it and its reply are the run's work on that task's outcome.
    /// `Node.agentIndex` indexes the agents of `run.shown`, which views draw.
    public static func clusters(_ raw: MightyGraphRun) -> [Cluster] {
        let run = raw.shown
        let kept = kept(run)
        let indexes = Dictionary(kept.map { (run.agents[$0].id, $0) }, uniquingKeysWith: { first, _ in first })
        var children: [String: [Int]] = [:]
        for index in kept { if let parent = run.agents[index].parentID { children[parent, default: []].append(index) } }
        var visited = Set<Int>()
        func walk(_ index: Int, depth: Int, into nodes: inout [Node]) {
            guard depth < maximumDepth, visited.insert(index).inserted else { return }
            let agent = run.agents[index]
            nodes.append(Node(agentID: agent.id, agentIndex: index, nodeID: MightyGraphLayout.nodeID(run, suffix: "agent:" + agent.id),
                              depth: depth, title: MightyGraphSupport.blockTitle(agent), status: MobileMightySupport.status(agent.status),
                              kind: MightyGraphSupport.blockKind(agent),
                              prompt: excerpt(agent.shownInput, maximumBytes: promptBytes, lines: promptLines),
                              output: reply(agent.entries).flatMap { excerpt($0, maximumBytes: outputBytes, lines: outputLines) },
                              activity: activity(agent.entries, limit: nodeActivity),
                              model: agent.responseRecords?.lazy.compactMap(\.model).first { !$0.isEmpty },
                              background: agent.background == true,
                              noticeReply: agent.noticeReply.flatMap { excerpt($0, maximumBytes: outputBytes, lines: outputLines) }))
            for child in children[agent.id] ?? [] where child != index { walk(child, depth: depth + 1, into: &nodes) }
        }
        var result: [Cluster] = []
        func start(_ index: Int) {
            var nodes: [Node] = []
            walk(index, depth: 0, into: &nodes)
            if !nodes.isEmpty { result.append(Cluster(nodes: nodes)) }
        }
        for index in kept {
            let agent = run.agents[index]
            if agent.parentID == nil || indexes[agent.parentID!] == nil || agent.parentID == agent.id { start(index) }
        }
        for index in kept where !visited.contains(index) { start(index) }
        return result
    }

    /// The run's sub-agent, task and task-notice blocks, by index. A block id the run repeats (a
    /// damaged save) is kept at its first place only, so every node id is unique.
    static func kept(_ run: MightyGraphRun) -> [Int] {
        var seen = Set<String>()
        return run.agents.indices.filter { index in
            (["agent", "task"].contains(MightyGraphSupport.blockKind(run.agents[index])) || run.agents[index].isTaskNotice) && seen.insert(run.agents[index].id).inserted
        }
    }

    static func preview(_ text: String) -> String {
        ActivitySupport.clean(text, maximumBytes: previewBytes, singleLine: true)
    }

    /// `raw` cleaned as every shown text is (`ActivitySupport.clean`) and cut at
    /// `maximumBytes`, with its first `lines` lines as the folded preview; nil when
    /// nothing is left. A plain preview cuts each line at `previewLineCharacters`; a
    /// `markdown` one keeps whole lines within `lines × previewLineCharacters`
    /// characters and closes a code fence left open, so the folded card renders what
    /// the opened one does, only shorter.
    public static func excerpt(_ raw: String, maximumBytes: Int, lines: Int, markdown: Bool = false) -> Excerpt? {
        // Cleaning reads the whole string; a text far over its cap is cut first.
        let cleaned = ActivitySupport.clean(ActivitySupport.prefixUTF8(raw, maximumBytes: 2 * maximumBytes + 64), maximumBytes: maximumBytes + 4)
        guard !cleaned.isEmpty else { return nil }
        let text = cleaned.utf8.count > maximumBytes
            ? ActivitySupport.prefixUTF8(cleaned, maximumBytes: maximumBytes - 3).trimmingCharacters(in: .whitespacesAndNewlines) + "…"
            : cleaned
        let all = text.split(separator: "\n", omittingEmptySubsequences: false)
        var kept: [Substring] = []
        if markdown {
            var budget = lines * previewLineCharacters
            for line in all.prefix(lines) {
                if line.count > budget {
                    // Only a first line longer than the whole budget is cut.
                    if kept.isEmpty { kept.append(line.prefix(budget)) }
                    break
                }
                kept.append(line)
                budget -= line.count
            }
        } else {
            kept = all.prefix(lines).map { $0.prefix(previewLineCharacters) }
        }
        var shown = kept.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        if markdown, shown != text, let fence = openFence(shown) { shown += "\n" + fence }
        return Excerpt(text: text, preview: shown)
    }

    /// The fence a Markdown text ends inside of (a line opening with ``` or ~~~ up to
    /// its matching close), nil when every code block is closed.
    static func openFence(_ markdown: String) -> String? {
        var open: String?
        for line in markdown.split(separator: "\n", omittingEmptySubsequences: false) {
            let lead = line.drop { $0 == " " }
            guard line.count - lead.count <= 3, let mark = lead.first, mark == "`" || mark == "~" else { continue }
            let run = String(lead.prefix { $0 == mark })
            guard run.count >= 3 else { continue }
            if let fence = open {
                // A close is the same mark, at least as long, with nothing after it.
                if run.first == fence.first, run.count >= fence.count, lead.dropFirst(run.count).allSatisfy(\.isWhitespace) { open = nil }
            } else {
                open = run
            }
        }
        return open
    }

    /// The block's latest reply: its last assistant record with words in it, a
    /// question's raw form left out as the phone's steps leave it out (`MobileMightySupport.step`).
    static func reply(_ entries: [LogEntry]) -> String? {
        entries.last { entry in
            entry.kind == "assistant" && !entry.text.isEmpty
                && !(entry.text.first == "{" && UserQuestionnaire.parse(inputJSON: entry.text) != nil)
        }?.text
    }

    /// The last `limit` tool calls of a block, oldest first, worded as the phone's steps
    /// word them (`MobileMightySupport.step`): its summary, else its tool's name. The
    /// run's own lifecycle row ("turn") is not a tool call.
    static func activity(_ entries: [LogEntry], limit: Int) -> [ActivityLine] {
        var lines: [ActivityLine] = []
        for entry in entries.reversed() {
            guard lines.count < limit else { break }
            guard let activity = entry.activity, activity.kind != "turn", let text = MobileMightySupport.step(entry) else { continue }
            lines.append(ActivityLine(id: entry.id, kind: activity.kind, state: activity.state, text: text))
        }
        return lines.reversed()
    }
}
