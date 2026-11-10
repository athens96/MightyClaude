import CoreGraphics
import Foundation

/// The line on the branch from a block to the sub-agents it handed work to: how many, on which model, and how many
/// of them still run. It stands between the block and its fan-out row (`MightyGraphLayout.make(fanOut:)`).
public enum FlowDelegation {
    /// The line's node in the diagram: as wide as its longest wording needs, one line tall.
    public static let size = CGSize(width: 340, height: 24)
    /// The key of the block every request starts from: its 실행 block (the main session). A sub-agent's key is its id.
    public static let mainKey = "main"

    /// What the line says.
    public struct Line: Equatable, Sendable {
        /// The sub-agents the block delegated to, and how many of them have not ended.
        public var count: Int
        public var running: Int
        /// "하위 에이전트 3개에 위임".
        public var head: String
        /// The one model all of them share ("Haiku 5.5", or "Haiku · 요청" while none has answered), or how many models
        /// they are on ("모델 2종"); nil when none names a model.
        public var model: String?
        /// "2/3 실행 중" while any of them runs.
        public var progress: String?
        /// The whole line, as VoiceOver reads it.
        public var text: String { ([head, model, progress].compactMap { $0 }).joined(separator: " · ") }
    }

    /// The sub-agents block `key` of `run` delegated to: the ones the flow draws right under it, in launch order. A
    /// copy in a notice run is no delegation, and neither is a block that is no sub-agent.
    public static func children(of key: String, in run: MightyGraphRun) -> [MightyGraphAgent] {
        guard run.noticeRun != true else { return [] }
        let agents = run.flowAgents
        let ids = Set(agents.map(\.id))
        let order = agents.indices.sorted { run.flowOrder($0) < run.flowOrder($1) }
        return order.map { agents[$0] }.filter { agent in
            guard agent.isSubagent, agent.sourceBlockId == nil else { return false }
            // The layout's own rule for what hangs off the main block: no parent, an unknown one, or itself.
            if key == mainKey { return agent.parentID.map { !ids.contains($0) || $0 == agent.id } ?? true }
            return agent.parentID == key && agent.id != key
        }
    }

    /// The line for `children`, nil when there are none. `definitions` are the custom agents their types name
    /// (`AgentDefinitions`), whose model stands in for a call that asked for none.
    public static func line(_ children: [MightyGraphAgent], definitions: [String: AgentDefinition] = [:]) -> Line? {
        guard !children.isEmpty else { return nil }
        let running = children.filter { !MightyGraphLayout.terminal($0.status) }.count
        let head = children.count == 1 ? L("graph.delegate.one") : L("graph.delegate.many", ["count": "\(children.count)"])
        return Line(count: children.count, running: running, head: head, model: model(children, definitions: definitions),
                    progress: running > 0 ? L("graph.delegate.running", ["running": "\(running)", "total": "\(children.count)"]) : nil)
    }

    /// The model the sub-agents share, or how many they are on. A model the CLI named (a block's own responses, the
    /// launch's resolved model) is read as it is; an alias that was only asked for counts as the same model as a named
    /// one of its family ("haiku" beside "Haiku 5.5"), and stands marked as requested when nothing was named yet.
    static func model(_ children: [MightyGraphAgent], definitions: [String: AgentDefinition]) -> String? {
        var named: [String] = [], asked: [String] = []
        for agent in children {
            if let id = agent.responseRecords?.lazy.compactMap(\.model).first(where: { !$0.isEmpty }) ?? agent.identity?.resolvedModel {
                let label = ModelUsageFormat.shortName(id, versioned: true)
                if !named.contains(label) { named.append(label) }
            } else if let alias = agent.identity?.requestedModel ?? agent.identity?.type.flatMap({ definitions[$0]?.model }), alias != "inherit" {
                let label = ModelLabel.text(alias)
                if !asked.contains(label) { asked.append(label) }
            }
        }
        let unmatched = asked.filter { family in !named.contains { $0 == family || $0.hasPrefix(family + " ") } }
        switch named.count + unmatched.count {
        case 0: return nil
        case 1: return named.first ?? unmatched.first.map { $0 + " " + L("graph.nodeModel.requestedSuffix") }
        default: return L("graph.delegate.models", ["count": "\(named.count + unmatched.count)"])
        }
    }
}

/// The node at a request's result position before its result is there: what is true of the way back to the main
/// session at that moment. Once the result block exists (the 알림 대기 block included) the branches end in it and
/// this node is gone.
public enum FlowReturn {
    public static let size = CGSize(width: 340, height: 34)

    public struct State: Equatable, Sendable {
        /// The run's sub-agents, at any depth, and how many of them have not ended.
        public var total: Int
        public var running: Int
        /// "하위 에이전트 2/3 실행 중 · 보고를 기다리는 중" while any runs; "메인으로 복귀 · 검토 중" once all ended and the
        /// main session's turn goes on.
        public var text: String
        public var waiting: Bool { running > 0 }
    }

    /// The state of `run`'s way back, nil for a run that delegated to no sub-agent and for a notice run.
    public static func state(_ run: MightyGraphRun) -> State? {
        guard run.noticeRun != true else { return nil }
        let agents = run.flowAgents.filter { $0.isSubagent && $0.sourceBlockId == nil }
        guard !agents.isEmpty else { return nil }
        let running = agents.filter { !MightyGraphLayout.terminal($0.status) }.count
        let text = running > 0 ? L("graph.return.waiting", ["running": "\(running)", "total": "\(agents.count)"]) : L("graph.return.reviewing")
        return State(total: agents.count, running: running, text: text)
    }
}
