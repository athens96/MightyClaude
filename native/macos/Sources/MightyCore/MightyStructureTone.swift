import Foundation

extension MightyStructure {
    /// The 파이프라인 view's colours by state (spec 2026-10-25): every column takes its step's
    /// tone as a wash under its cards, and every card its own as a tint and a bar down its
    /// leading edge (`DesignMetrics.Pipeline`). The tones are concept D's; there is no colour of
    /// their own. A step still to come takes none and stays on the page's ground. Windows
    /// (`MightyStructure.Tone` in MightyClaude.Core) and the phone (`pipelineTone`, `stepTone`,
    /// `columnTone`, `cardTone` in mobile/src/lib) keep the same rules.
    public enum Tone {
        /// A card's status, in the block buckets (`DesignTone(blockStatus:)`): failed is an error,
        /// cancelled and interrupted are stops, anything still in motion runs; `done` is done and
        /// `pending`, a step still to come, takes none.
        public static func status(_ status: String) -> DesignTone? {
            switch status {
            case "pending": return nil
            case "done": return .done
            default: return DesignTone(blockStatus: status)
            }
        }

        /// A step's: done, error and stopped in their own colours, the current step running, or
        /// waiting while its request waits (on a permission, a question or a plan); pending none.
        public static func step(_ step: Step, waiting: Bool = false) -> DesignTone? {
            switch step {
            case .done: return .done
            case .current: return waiting ? .wait : .run
            case .pending: return nil
            case .error: return .err
            case .stopped: return .stop
            }
        }

        /// A column's: a done, error, stopped or pending step's own; the current step's follows
        /// its newest request: once that request finished (`Item.finished`: it and every
        /// sub-agent and task ended) its status's (done, an error or a stop), until then
        /// running, or waiting while its status waits; it runs while the column holds no
        /// request yet. A free board's 결과 whose request awaits its background work's notices
        /// (`Item.awaiting`) waits, as its 알림 대기 중 title does.
        public static func column(_ column: BoardColumn) -> DesignTone? {
            if column.step.step == .done, column.cards.contains(where: { if case .result(let item) = $0 { item.awaiting } else { false } }) { return .wait }
            guard column.step.step == .current else { return step(column.step.step) }
            guard let newest = newestRequest(column) else { return .run }
            if newest.finished { return status(newest.status) ?? .run }
            return status(newest.status) == .wait ? .wait : .run
        }

        /// A card's: a free board's 요청 and 결과 stand for their step and take their column's;
        /// the 실행 card, a styled board's request and every sub-agent, task and 작업 알림 card
        /// take their own status's.
        public static func card(_ card: Card, in column: BoardColumn) -> DesignTone? {
            switch card {
            case .request, .result: return self.column(column)
            case .execution(let item), .summary(let item): return status(item.status)
            case .agent(let node, _, _): return status(node.status)
            }
        }

        /// The request of the column's last request card (its newest), nil for an empty column.
        static func newestRequest(_ column: BoardColumn) -> Item? {
            for card in column.cards.reversed() {
                switch card {
                case .request(let item), .execution(let item), .result(let item), .summary(let item): return item
                case .agent: continue
                }
            }
            return nil
        }
    }
}
