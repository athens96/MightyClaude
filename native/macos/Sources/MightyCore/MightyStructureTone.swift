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

        /// A column's: its step's, the current one waiting while the column's newest request waits.
        public static func column(_ column: BoardColumn) -> DesignTone? {
            step(column.step.step, waiting: newestRequest(column)?.status == "waiting")
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
