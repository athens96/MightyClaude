import Foundation

/// The Mighty graph drawn as its structure (design "A. 단계별 열"): a styled pane's
/// requests under the phase each belongs to, a free pane's as one 요청 → 실행 → 결과 row
/// each, and under every request its sub-agent trees as clusters.
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
        /// Index into the run's `agents`.
        public var agentIndex: Int
        /// The diagram node this row stands for (`MightyGraphLayout.Node.id`).
        public var nodeID: String
        /// 0 for the cluster's root.
        public var depth: Int
        public var title: String
        /// Bucketed as a block's status is (`MobileMightySupport.status`).
        public var status: String
        /// `MightyGraphSupport.blockKind`: "agent" or "task" (the other kinds are left out).
        public var kind: String
        public var id: String { nodeID }
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
        /// Index into the runs the diagram draws, so `ordinal == runIndex + 1`.
        public var runIndex: Int
        public var runID: String
        public var ordinal: Int
        /// The request's own diagram node.
        public var nodeID: String
        /// The request on one line.
        public var input: String
        /// The request's status, bucketed as a block's is.
        public var status: String
        public var finished: Bool
        /// The final answer on one line, once the request finished with one.
        public var result: String?
        public var tally: Tally
        public var clusters: [Cluster]
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
    /// or appear), records by their count; a block's title and status as they are.
    public struct Signature: Equatable, Sendable {
        struct Block: Equatable, Sendable {
            var id, title, status: String
            var parentID, kind: String?
            var entries: Int
        }
        struct Request: Equatable, Sendable {
            var id, status: String
            var sourceRunID: String?
            var input, rootEntries, resultEntries, result: Int
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
                        rootEntries: run.rootEntries.count, resultEntries: run.resultEntries.count,
                        result: run.resultEntries.last?.text.utf8.count ?? 0, finalOutput: run.finalOutput?.utf8.count,
                        blocks: run.agents.map { Block(id: $0.id, title: $0.title, status: $0.status, parentID: $0.parentID, kind: $0.kind, entries: $0.entries.count) })
            }
            phases = style?.manifest.orderedPhases.map(\.id) ?? []
            recognition = style?.manifest.id
            self.currentPhase = currentPhase?.id
            self.startingNew = startingNew
            plans = planHistory.map { [$0.id, $0.outcome.rawValue, $0.graphRunId ?? "", $0.runId].joined(separator: "\u{1F}") }
            self.pendingPlanRunID = pendingPlanRunID
        }
    }

    /// The deepest tree level a cluster follows, as the diagram's layout does.
    public static let maximumDepth = 64
    /// The bound of an input or result preview.
    static let previewBytes = 400
    /// Activity kinds that are a tool call, not the turn or a sub-agent's own line.
    static let toolKinds: Set<String> = ["tool", "command", "read", "edit", "search", "web"]

    /// The pane as the structure view draws it.
    ///
    /// - `currentPhase`: the pane's own phase, as its phase bar shows it.
    /// - `startingNew`: the user chose to start anew, so `currentPhase` is the phase the
    ///   next request starts in and says nothing of the requests already there.
    /// - `planHistory`: the pane's answered plans; `pendingPlanRunID`: the run id of the
    ///   plan waiting for an answer (`ToolPermissionRequest.runId`), if any.
    ///
    /// The current column is the pane's phase, which is where the live or newest request
    /// sits (or will, once a request that has just started draws its block). A column
    /// that holds a card is never pending: any card outside the current column is a
    /// request that is over with that phase. An empty column is done before the current
    /// one and pending after it.
    public static func make(runs: [MightyGraphRun], style: StyleEvaluator?, currentPhase: StylePhase?, startingNew: Bool = false,
                            planHistory: [PlanRecord] = [], pendingPlanRunID: String? = nil) -> Shape {
        let items = runs.enumerated().map { item($1, index: $0) }
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
            return runs.enumerated().map { index, run in
                if index == waiting { return phase(.awaitingApproval) }
                switch planHistory.last(where: { planRecord($0, belongsTo: run) })?.outcome {
                case .approvedAuto?, .approvedConfirm?: return phase(.executing)
                case .revised?, .cancelled?, nil: return phase(.planning)
                }
            }
        }
        // An empty history reads as the style's `default` phase.
        let initial = style.currentPhase(prompts: [])?.id ?? first.id
        let pane = currentPhase.flatMap { manifest.phase($0.id)?.id }
        var result: [String] = []
        for (index, run) in runs.enumerated() {
            let newest = index == runs.count - 1 ? pane : nil
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

    public static func item(_ run: MightyGraphRun, index: Int) -> Item {
        let finished = MightyGraphLayout.finished(run)
        let answer = run.resultEntries.last(where: { $0.kind == "assistant" && !$0.text.isEmpty })?.text ?? run.finalOutput
        let result = finished ? answer.map(preview).flatMap { $0.isEmpty ? nil : $0 } : nil
        // Counted over the blocks the clusters draw, so a steer's or a question's records add nothing.
        let blocks = kept(run).map { run.agents[$0] }
        let entries = run.rootEntries + blocks.flatMap(\.entries)
        let tally = Tally(agents: blocks.filter { MightyGraphSupport.blockKind($0) == "agent" }.count,
                          tools: entries.filter { $0.activity.map { toolKinds.contains($0.kind) } ?? false }.count)
        return Item(runIndex: index, runID: run.id, ordinal: index + 1, nodeID: MightyGraphLayout.nodeID(run, suffix: "request"),
                    input: preview(run.input), status: MobileMightySupport.status(run.status), finished: finished,
                    result: result, tally: tally, clusters: clusters(run))
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
    /// commands) are work, so they stay.
    public static func clusters(_ run: MightyGraphRun) -> [Cluster] {
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
                              kind: MightyGraphSupport.blockKind(agent)))
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

    /// The run's sub-agent and task blocks, by index. A block id the run repeats (a
    /// damaged save) is kept at its first place only, so every node id is unique.
    static func kept(_ run: MightyGraphRun) -> [Int] {
        var seen = Set<String>()
        return run.agents.indices.filter { index in
            ["agent", "task"].contains(MightyGraphSupport.blockKind(run.agents[index])) && seen.insert(run.agents[index].id).inserted
        }
    }

    static func preview(_ text: String) -> String {
        ActivitySupport.clean(text, maximumBytes: previewBytes, singleLine: true)
    }
}
