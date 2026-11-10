import CoreGraphics
import Foundation

/// A sub-agent as the flow diagram's compact card: who is working, what it is doing right now and how far it is,
/// in four lines. A running sub-agent is a card; one that ran in the foreground becomes its full block when it
/// ends, as it always was, and one that ran in the background stays a card, since its full block is the copy in
/// its notice run (`BackgroundNoticeRuns`). Every figure on it is one the app observed: the time since it saw the
/// agent start, the tool calls it counted, the tokens the CLI reported. Nothing is estimated and nothing is a
/// percentage: an agent does not say how much of its work is left.
public enum FlowCard {
    /// The card's size in the diagram. It is never dragged: opened, the block has its own saved size.
    public static let size = CGSize(width: 236, height: 86)

    /// Whether the flow draws sub-agent `agent` as a card. `opened`: the user opened it into its full block, which
    /// stays open until they fold it back. A copy in a notice run is always the full block, and so is a block that
    /// is no sub-agent.
    public static func isCompact(_ agent: MightyGraphAgent, opened: Bool) -> Bool {
        guard foldsToCard(agent), !opened else { return false }
        return true
    }

    /// Whether the block has a card to fold back to: a request's own background sub-agent at any time, a foreground
    /// one while it runs.
    public static func foldsToCard(_ agent: MightyGraphAgent) -> Bool {
        guard agent.isSubagent, agent.sourceBlockId == nil else { return false }
        return agent.background == true || !MightyGraphLayout.terminal(agent.status)
    }

    /// What a card says.
    public struct Content: Equatable, Sendable {
        /// The block's title: the agent's name, else its description.
        public var title: String
        /// "Explore · Haiku 5.5": its type and its model, whichever of the two is known.
        public var identity: String?
        /// One line: what it is doing now while it runs, what it answered once it ended.
        public var activity: String?
        /// The status bucket (`MobileMightySupport.status`) and its word.
        public var status: String
        public var stateWord: String
        public var startedAt: Date?
        public var endedAt: Date?
        public var toolCalls: Int?
        /// The tokens its own messages reported so far (`GraphTokenUsage.total`).
        public var tokens: Int?
        /// Sub-agents under it, which a card counts instead of drawing.
        public var nested: Int
        public var marks: [FlowMark]
        public var ink: FlowInk
        /// The whole identity in one line, for the tooltip (`FlowIdentity.summary`).
        public var summary: String

        public var running: Bool { !MightyGraphLayout.terminal(status) }
    }

    /// The card of `agent`, a block of `run`. `definition` is the custom agent its type names (`AgentDefinitions`).
    public static func content(_ agent: MightyGraphAgent, in run: MightyGraphRun, definition: AgentDefinition? = nil) -> Content {
        let who = FlowRoles.subagent(agent, definition: definition)
        let status = MobileMightySupport.status(agent.status)
        let running = !MightyGraphLayout.terminal(status)
        return Content(title: MightyGraphSupport.blockTitle(agent), identity: identityLine(who, agent),
                       activity: running ? doing(agent) : outcome(agent), status: status, stateWord: stateWord(status),
                       startedAt: agent.figures?.started, endedAt: agent.figures?.ended, toolCalls: agent.figures?.toolCalls,
                       tokens: agent.usage.flatMap { $0.isEmpty ? nil : $0.total }, nested: nested(under: agent, in: run),
                       marks: who?.marks ?? [], ink: who?.ink ?? FlowRoles.ink(.subagent), summary: who?.summary ?? "")
    }

    /// "Explore · Haiku 5.5": a sub-agent's type and its model, whichever is known. The model is the one its own
    /// responses name, else the one the CLI resolved at launch, else the alias that was asked for, marked as requested. A
    /// fork has no type to name: its mark says it.
    public static func identityLine(_ agent: MightyGraphAgent, definition: AgentDefinition? = nil) -> String? {
        identityLine(FlowRoles.subagent(agent, definition: definition), agent)
    }
    static func identityLine(_ who: FlowIdentity?, _ agent: MightyGraphAgent) -> String? {
        let model = agent.responseRecords?.lazy.compactMap(\.model).first { !$0.isEmpty }.map { ModelUsageFormat.shortName($0, versioned: true) }
            ?? ModelUsageFormat.versionedNodeLabel(who?.modelLabel)
        let parts = [who?.type.map(FlowRoles.typeName).flatMap { who?.marks.contains(.fork) == true ? nil : $0 }, model].compactMap { $0 }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// The most a card's line holds; the view cuts what does not fit its width.
    static let lineBytes = 240

    /// What a running agent is doing right now: its latest tool call as the transcript words it (its summary, else its
    /// tool's name), else the last line it wrote, else the first line of what it was asked. The run's own lifecycle
    /// rows are not tool calls.
    public static func doing(_ agent: MightyGraphAgent) -> String? {
        for entry in agent.entries.reversed() {
            guard let activity = entry.activity, activity.kind != "turn" else { continue }
            if let line = line(activity.summary.isEmpty ? activity.toolName ?? "" : activity.summary) { return line }
        }
        for entry in agent.entries.reversed() where entry.kind == "assistant" {
            if let line = line(lastLine(entry.text)) { return line }
        }
        return line(firstLine(agent.shownInput))
    }

    /// What an ended agent has to say in one line: the first line of its answer (the last reply its block holds), else
    /// of the last thing it recorded.
    public static func outcome(_ agent: MightyGraphAgent) -> String? {
        if let answer = MobileMightySupport.answer(agent.entries), let line = line(firstLine(answer)) { return line }
        for entry in agent.entries.reversed() where entry.activity == nil {
            if let line = line(firstLine(entry.text)) { return line }
        }
        return nil
    }

    static func line(_ raw: String) -> String? {
        let clean = ActivitySupport.clean(raw, maximumBytes: lineBytes, singleLine: true)
        return clean.isEmpty ? nil : clean
    }
    static func firstLine(_ text: String) -> String {
        text.split(whereSeparator: \.isNewline).first { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.map(String.init) ?? ""
    }
    static func lastLine(_ text: String) -> String {
        text.split(whereSeparator: \.isNewline).last { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.map(String.init) ?? ""
    }

    /// A status bucket in the diagram's own words (`graph.state.*`).
    public static func stateWord(_ status: String) -> String {
        switch status {
        case "completed": L("graph.state.completed")
        case "error", "failed": L("graph.state.error")
        case "stopped", "cancelled", "interrupted": L("graph.state.stopped")
        case "waiting": L("graph.state.waiting")
        default: L("graph.state.running")
        }
    }

    /// The sub-agents under `agent`, at any depth: the "(+N)" a card shows in their place.
    public static func nested(under agent: MightyGraphAgent, in run: MightyGraphRun) -> Int {
        let agents = run.flowAgents
        var under: Set<String> = [agent.id]
        var count = 0
        var grew = true
        while grew {
            grew = false
            for candidate in agents where !under.contains(candidate.id) {
                guard let parent = candidate.parentID, under.contains(parent) else { continue }
                under.insert(candidate.id); grew = true
                if candidate.isSubagent { count += 1 }
            }
        }
        return count
    }

    /// The card's last line: its state, then the figures it has. While it runs the time is the time since it started,
    /// read against `now`; once it ended, how long it ran. A figure the app does not have is left out.
    public static func figures(_ content: Content, now: Date) -> String {
        var parts = [content.stateWord]
        if let start = content.startedAt {
            if content.running { parts.append(StyleWidgetPresentation.elapsed(from: start, to: now)) }
            else if let end = content.endedAt, end >= start { parts.append(StyleWidgetPresentation.elapsed(from: start, to: end)) }
        }
        if let tools = content.toolCalls, tools > 0 { parts.append(L("graph.card.tools", ["count": "\(tools)"])) }
        if let tokens = content.tokens { parts.append(L("graph.usage.summary", ["total": GraphTokenUsage.compact(tokens)])) }
        return parts.joined(separator: " · ")
    }

    /// The node of `agent`'s full copy in a notice run (`ExecutionGraphNode.sourceBlockId`), the newest when a later
    /// turn copied it again: where a finished background agent's card leads. Nil while no notice run holds one.
    public static func copyNodeID(of agent: MightyGraphAgent, in runs: [MightyGraphRun]) -> String? {
        for run in runs.reversed() where run.noticeRun == true {
            if let copy = run.agents.last(where: { $0.sourceBlockId == agent.id }) {
                return MightyGraphLayout.nodeID(run, suffix: "agent:" + copy.id)
            }
        }
        return nil
    }
}
