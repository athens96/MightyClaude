import CoreGraphics
import Foundation

/// A sub-agent, or a command the session runs in the background, as the flow diagram's compact card: who is working,
/// what it is doing right now and how far it is, in four lines. Every sub-agent and command is a card, running or
/// ended, and so is a finished background one's copy in its notice run (`BackgroundNoticeRuns`): only the result is drawn
/// whole, and a click opens any card into its block. The 실행 block and a request's steps (`FlowSteps`) are cards
/// the same way. Every figure on it is one the app observed: the time since it saw the agent
/// start, the tool calls it counted, the tokens the CLI reported, the exit code the CLI named. Nothing is estimated
/// and nothing is a percentage: an agent does not say how much of its work is left.
public enum FlowCard {
    /// The card's size in the diagram. It is never dragged: opened, the block has its own saved size.
    public static let size = CGSize(width: 236, height: 86)
    /// A card beside a request's step: one more line, the step it was launched in (`FlowSteps.stepLabel`).
    public static let stepSize = CGSize(width: 236, height: 104)

    /// Whether the flow draws sub-agent `agent` as a card. `opened`: the user opened it into its full block, which
    /// stays open until they fold it back. A block that is no sub-agent or command (a steer, a question, a compaction) is
    /// always its block.
    public static func isCompact(_ agent: MightyGraphAgent, opened: Bool) -> Bool {
        guard foldsToCard(agent), !opened else { return false }
        return true
    }

    /// Whether the block has a card to fold back to: every sub-agent and background command, running or ended, a copy
    /// in a notice run too.
    public static func foldsToCard(_ agent: MightyGraphAgent) -> Bool { agent.isTask || agent.isSubagent }

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
        /// A background command's card: its second line is the command (the view sets it as code) and its figures are
        /// its exit code and time.
        public var isTask = false
        public var exitCode: Int? = nil
        /// A step not begun (`FlowSteps`): no time runs and no outline moves.
        public var isPending = false
        /// The one line is what the main session said (`FlowSteps`), set as text rather than as a command.
        public var proseActivity = false
        /// The step of the request's plan it was launched in ("2. 노트 작성"), on a line of its own (`FlowSteps.stepLabel`).
        public var step: String? = nil

        public var running: Bool { !isPending && !MightyGraphLayout.terminal(status) }
    }

    /// The card of `agent`, a block of `run`. `definition` is the custom agent its type names (`AgentDefinitions`);
    /// `output` the last lines a background command wrote (`BackgroundOutputTail`), which the app reads apart.
    public static func content(_ agent: MightyGraphAgent, in run: MightyGraphRun, definition: AgentDefinition? = nil, output: [String] = []) -> Content {
        if let who = FlowRoles.task(agent) { return task(agent, who: who, output: output) }
        let who = FlowRoles.subagent(agent, definition: definition)
        let status = MobileMightySupport.status(agent.status)
        let running = !MightyGraphLayout.terminal(status)
        return Content(title: MightyGraphSupport.blockTitle(agent), identity: identityLine(who, agent),
                       activity: running ? doing(agent) : outcome(agent), status: status, stateWord: stateWord(status),
                       startedAt: agent.figures?.started, endedAt: agent.figures?.ended, toolCalls: agent.figures?.toolCalls,
                       tokens: agent.usage.flatMap { $0.isEmpty ? nil : $0.total }, nested: nested(under: agent, in: run),
                       marks: who?.marks ?? [], ink: who?.ink ?? FlowRoles.ink(.subagent), summary: who?.summary ?? "")
    }

    /// A background command's card: its description (else its command), `작업 · <the command's first line>`, the last
    /// line it wrote, and its state with its exit code and time.
    static func task(_ agent: MightyGraphAgent, who: FlowIdentity, output: [String]) -> Content {
        let status = MobileMightySupport.status(agent.status)
        let command = line(firstLine(agent.shownInput)).map { L("graph.card.task", ["command": $0]) } ?? FlowRoles.roleName(.task)
        return Content(title: MightyGraphSupport.blockTitle(agent), identity: command, activity: lastOutput(output), status: status,
                       stateWord: stateWord(status), startedAt: agent.figures?.started, endedAt: agent.figures?.ended, toolCalls: nil, tokens: nil,
                       nested: 0, marks: who.marks, ink: who.ink, summary: who.summary, isTask: true, exitCode: agent.figures?.exitCode)
    }

    /// The last line a background command wrote, without the line Claude Code adds once it exits
    /// (`[exited with code 0]`): its exit code is the card's figure.
    public static func lastOutput(_ lines: [String]) -> String? {
        for raw in lines.reversed() {
            let text = raw.trimmingCharacters(in: .whitespaces)
            if text.isEmpty || text.wholeMatch(of: #/\[exited with code -?[0-9]+\]/#) != nil { continue }
            return line(text)
        }
        return nil
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
        let (before, after) = figureParts(content)
        return (before + [elapsed(content, now: now)].compactMap { $0 } + after).joined(separator: " · ")
    }

    /// The card's last line before and after its time: its state (and a command's exit code), then the tools and the
    /// tokens. A view that keeps its own clock (the phone, Windows' page) puts the time between them, read once a second.
    public static func figureParts(_ content: Content) -> (before: [String], after: [String]) {
        var before = [content.stateWord]
        // A command that ended says how it exited, before how long it took.
        if content.isTask, !content.running, let code = content.exitCode { before.append(L("graph.card.exitCode", ["code": "\(code)"])) }
        var after: [String] = []
        if let tools = content.toolCalls, tools > 0 { after.append(L("graph.card.tools", ["count": "\(tools)"])) }
        if let tokens = content.tokens { after.append(L("graph.usage.summary", ["total": GraphTokenUsage.compact(tokens)])) }
        return (before, after)
    }

    /// The card's time: while it runs the time since it started, read against `now`; once it ended, how long it ran.
    /// Nil when the app does not have it.
    public static func elapsed(_ content: Content, now: Date) -> String? {
        guard let start = content.startedAt else { return nil }
        if content.running { return StyleWidgetPresentation.elapsed(from: start, to: now) }
        guard let end = content.endedAt, end >= start else { return nil }
        return StyleWidgetPresentation.elapsed(from: start, to: end)
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
