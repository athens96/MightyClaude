import Foundation

extension MightyStructure {
    /// One page of the 구조 view's kanban: one request of a free pane, or one pipeline of
    /// a styled pane. The view shows one board at a time, with the steps it went through
    /// as its header and one column of cards per step under it.
    public struct Board: Equatable, Sendable, Identifiable {
        public enum Kind: String, Sendable {
            /// A free pane's request: 요청 → 실행 → 결과.
            case request
            /// A styled pane's run of requests through the style's phases.
            case pipeline
        }
        /// The first request's run id. Requests only join a board's end, so the id holds
        /// while new runs arrive; a trim that drops a pipeline's first request names the
        /// pipeline by its next one (`Navigation` follows it by the runs it remembers).
        public var id: String
        public var kind: Kind
        /// 1-based place among the boards.
        public var ordinal: Int
        /// The board's requests, oldest first.
        public var items: [Item]
        /// The first request's first line, nil when it has none.
        public var headline: String?
        /// The board's status, bucketed as a block's is: running or waiting while any
        /// request still is, else the newest request's.
        public var status: String
        /// The header's steps, in order.
        public var steps: [BoardStep]
        /// One column per step, in the same order.
        public var columns: [BoardColumn]

        public var runIDs: [String] { items.map(\.runID) }
        /// Only notice runs taken into a running turn (`Item.quiet`): the view never moves on to it by itself.
        public var quiet: Bool { !items.isEmpty && items.allSatisfy(\.quiet) }
        /// The current step's place, else the last step that is over.
        public var currentStepIndex: Int? {
            steps.firstIndex { $0.step == .current } ?? steps.lastIndex { $0.step != .pending }
        }
    }

    /// One step of a board's header: a free request's 요청, 실행 or 결과, or a phase.
    public struct BoardStep: Equatable, Sendable, Identifiable {
        /// "request", "execution" or "result" for a free board, else the phase id.
        public var id: String
        /// The phase's title; nil for a free board's steps, which the view names.
        public var title: String?
        public var step: Step
    }

    /// One kanban column: a step and its cards, top to bottom.
    public struct BoardColumn: Equatable, Sendable, Identifiable {
        public var step: BoardStep
        public var cards: [Card]
        /// The column header's count: a styled column's requests, a free column's cards
        /// (요청 1, 실행 the main card and its sub-agents, 결과 1 once there is a result).
        public var count: Int
        public var id: String { step.id }
    }

    /// One card of a column.
    public enum Card: Equatable, Sendable, Identifiable {
        /// A free board's 요청: what was asked.
        case request(Item)
        /// A free board's 실행: the main agent's status, tally, latest reply and tool calls.
        case execution(Item)
        /// A free board's 결과: the answer, or how the request ended, or a placeholder.
        case result(Item)
        /// A styled board's request: what was asked, its 실행 part and its result line.
        case summary(Item)
        /// A sub-agent or task of request `runID`; `parent` is the title of the block it
        /// runs under, nil for a tree's root.
        case agent(Node, runID: String, parent: String?)

        public var id: String {
            switch self {
            case .request(let item): "request:" + item.runID
            case .execution(let item): "execution:" + item.runID
            case .result(let item): "result:" + item.runID
            case .summary(let item): "summary:" + item.runID
            case .agent(let node, _, _): "agent:" + node.nodeID
            }
        }
    }

    /// The boards of `shape`, oldest first.
    ///
    /// A free pane has one board per request. A styled pane has one board per pipeline:
    /// a run of consecutive requests whose phase never goes back. The first request starts
    /// a pipeline, and so does every request whose phase comes earlier in the style's order
    /// than the request before it (Ouroboros going back to 목표 or 인터뷰, a Claude plan
    /// request that plans again after the last one executed). Requests read their phase as
    /// `make` gives it (`phaseIDs`, the plan-state reading included), so a request that
    /// names no phase stays in the pipeline of the one before it.
    ///
    /// A pipeline's steps are the style's phases, judged within that pipeline: phases
    /// before its current one are done, later ones pending. The newest pipeline's current
    /// phase is the pane's (the shape's current column) unless the pane's phase comes
    /// before the pipeline's last request, which means the next request starts anew; an
    /// older pipeline's current phase is its last request's, done once its requests are.
    public static func boards(_ shape: Shape) -> [Board] {
        switch shape {
        case .free(let rows):
            return rows.enumerated().map { index, row in freeBoard(row, ordinal: index + 1) }
        case .phases(let columns):
            return pipelineBoards(columns)
        }
    }

    static func freeBoard(_ row: Row, ordinal: Int) -> Board {
        let item = row.item
        let steps = [BoardStep(id: "request", step: row.request), BoardStep(id: "execution", step: row.execution),
                     BoardStep(id: "result", step: row.result)]
        let agents = agentCards(item)
        let columns = [
            BoardColumn(step: steps[0], cards: [.request(item)], count: 1),
            BoardColumn(step: steps[1], cards: [.execution(item)] + agents, count: 1 + agents.count),
            BoardColumn(step: steps[2], cards: [.result(item)], count: item.finished ? 1 : 0),
        ]
        return Board(id: item.runID, kind: .request, ordinal: ordinal, items: [item], headline: firstLine(item),
                     status: item.status, steps: steps, columns: columns)
    }

    static func pipelineBoards(_ columns: [Column]) -> [Board] {
        var phaseOf: [Int: Int] = [:]
        var items: [Item] = []
        for (phase, column) in columns.enumerated() {
            for item in column.items {
                phaseOf[item.runIndex] = phase
                items.append(item)
            }
        }
        items.sort { $0.runIndex < $1.runIndex }
        // Split where the phase goes back.
        var groups: [[Item]] = []
        var previous: Int?
        for item in items {
            let phase = phaseOf[item.runIndex] ?? 0
            if let previous, phase >= previous { groups[groups.count - 1].append(item) } else { groups.append([item]) }
            previous = phase
        }
        let pane = columns.firstIndex { $0.step == .current }
        return groups.enumerated().map { index, group in
            let last = phaseOf[group[group.count - 1].runIndex] ?? 0
            let newest = index == groups.count - 1
            let current = newest ? pane.flatMap { $0 >= last ? $0 : nil } ?? last : last
            let over = !newest && group.allSatisfy(\.finished)
            let boardColumns = columns.enumerated().map { phase, column in
                let step: Step = phase < current ? .done : phase > current ? .pending : over ? .done : .current
                let held = group.filter { phaseOf[$0.runIndex] == phase }
                return BoardColumn(step: BoardStep(id: column.phase.id, title: column.phase.title, step: step),
                                   cards: held.flatMap { [Card.summary($0)] + agentCards($0) }, count: held.count)
            }
            let status = group.contains { $0.status == "running" } ? "running"
                : group.contains { $0.status == "waiting" } ? "waiting" : group[group.count - 1].status
            return Board(id: group[0].runID, kind: .pipeline, ordinal: index + 1, items: group, headline: firstLine(group[0]),
                         status: status, steps: boardColumns.map(\.step), columns: boardColumns)
        }
    }

    /// A request's sub-agent cards, each tree depth-first, each card naming the block it
    /// runs under.
    static func agentCards(_ item: Item) -> [Card] {
        var cards: [Card] = []
        for cluster in item.clusters {
            var titles: [String] = []
            for node in cluster.nodes {
                let depth = min(node.depth, titles.count)
                titles.removeSubrange(depth...)
                cards.append(.agent(node, runID: item.runID, parent: titles.last))
                titles.append(node.title)
            }
        }
        return cards
    }

    /// The request's first line with words in it.
    static func firstLine(_ item: Item) -> String? {
        let text = item.request?.text ?? item.input
        return text.split(whereSeparator: \.isNewline).lazy.map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty }
    }

    /// Which board the 구조 view shows. While the user is on the newest board the view
    /// moves on with it: a new request (or pipeline) is shown as it arrives. A board the
    /// user picks that is not the newest is kept by its id, and by the requests it held
    /// when picked, so new runs never move it; a newer board that arrives meanwhile is
    /// unseen until the user goes to the newest board again.
    public struct Navigation: Equatable, Sendable {
        /// The board the user picked, nil while the view shows the newest one.
        public private(set) var chosen: String?
        private var chosenRuns: [String] = []
        /// The newest board when the user left it.
        private var seenNewest: String?
        public init() {}

        /// The newest board: the last one, but a quiet notice's board after it (`Board.quiet`), which the
        /// view never moves on to by itself (`MightyGraphSupport.followRunIndex`).
        public static func newestIndex(_ boards: [Board]) -> Int? { boards.lastIndex { !$0.quiet } ?? boards.indices.last }

        /// The place of the board shown in `boards`, nil when there is none. A picked
        /// board that is gone is found by a request it held, else the oldest board is shown.
        public func index(in boards: [Board]) -> Int? {
            guard !boards.isEmpty else { return nil }
            guard let chosen else { return Self.newestIndex(boards) }
            if let index = boards.firstIndex(where: { $0.id == chosen }) { return index }
            let runs = Set(chosenRuns)
            return boards.firstIndex { $0.runIDs.contains(where: runs.contains) } ?? 0
        }

        /// The shown board is the newest one.
        public func onNewest(in boards: [Board]) -> Bool {
            index(in: boards).map { $0 == Self.newestIndex(boards) } ?? true
        }

        /// A board newer than the one shown arrived since the user left the newest board.
        public func hasUnseen(in boards: [Board]) -> Bool {
            !onNewest(in: boards) && Self.newestIndex(boards).map { boards[$0].id } != seenNewest
        }

        /// Shows board `id`. The newest board is shown as "the newest", so the view moves
        /// on with new ones again.
        public mutating func select(_ id: String, in boards: [Board]) {
            guard let target = boards.first(where: { $0.id == id }) else { return }
            let newest = Self.newestIndex(boards).map { boards[$0].id }
            if target.id == newest {
                chosen = nil
                chosenRuns = []
                seenNewest = nil
                return
            }
            if onNewest(in: boards) { seenNewest = newest }
            chosen = target.id
            chosenRuns = target.runIDs
        }

        /// Shows the board `offset` places from the shown one, stopping at the ends.
        public mutating func step(_ offset: Int, in boards: [Board]) {
            guard let index = index(in: boards) else { return }
            select(boards[min(max(0, index + offset), boards.count - 1)].id, in: boards)
        }

        /// Shows the newest board, and moves on with new ones from now on.
        public mutating func showNewest(in boards: [Board]) {
            guard let newest = Self.newestIndex(boards) else { chosen = nil; return }
            select(boards[newest].id, in: boards)
        }
    }
}
