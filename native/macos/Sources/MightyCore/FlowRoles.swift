import CoreGraphics
import Foundation

/// Who works in a flow diagram: the main session that took the request, the sub-agents it delegates to, the commands
/// it runs in the background, and the advisor a Claude session may consult. One ink per role, on every block's header,
/// on the delegate line, in the session log.
public enum FlowRole: String, Sendable, CaseIterable {
    /// The pane's own agent: a request's 실행 block.
    case lead
    /// An agent another agent started (Claude's Agent tool, a Codex or Antigravity sub-agent).
    case subagent
    /// A shell command a Claude session runs in the background (`run_in_background` on Bash): a task block.
    case task
    /// The model a Claude session asks for advice (`claude --advisor`).
    case advisor
}

/// An ink of the design palette a role, or a custom agent's own colour, is drawn in. The flow has no colour of its
/// own: every case is one of concept D's text-safe inks (`DesignPalette`), held to AA by `DesignTokenContrastTests`.
public enum FlowInk: String, Sendable, CaseIterable {
    case task, agent, steer, accent, done, err, wait, question
    /// The secondary ink: a background command's, which is no agent's and no state's.
    case neutral

    public var token: KeyPath<DesignPalette, DesignColor> {
        switch self {
        case .neutral: \.ink2
        case .task: \.taskText
        case .agent: \.agentText
        case .steer: \.steerText
        case .accent: \.accent
        case .done: \.doneText
        case .err: \.errText
        case .wait: \.waitText
        case .question: \.questionText
        }
    }

    /// The design token's name (`native/contracts/fixtures/design-tokens.json`), as a client that keeps its own palette
    /// reads the ink: the phone's role view, Windows' page (`FlowRoles.Token`).
    public var tokenName: String {
        switch self {
        case .neutral: "ink2"
        case .task: "taskText"
        case .agent: "agentText"
        case .steer: "steerText"
        case .accent: "accent"
        case .done: "doneText"
        case .err: "errText"
        case .wait: "waitText"
        case .question: "questionText"
        }
    }
}

/// A small mark on a sub-agent's header: how it runs, beside what it is.
public enum FlowMark: String, Sendable, CaseIterable {
    /// Started in the background (`run_in_background`, or the CLI's default).
    case background
    /// A fork of the calling session: its whole conversation and its model.
    case fork
    /// Works in a git worktree of its own (`isolation: "worktree"`).
    case worktree

    /// The SF Symbol the Mac draws it with.
    public var symbol: String {
        switch self {
        case .background: "square.stack.3d.up"
        case .fork: "square.on.square"
        case .worktree: "arrow.triangle.branch"
        }
    }
    /// What it says on hover, and to VoiceOver.
    public var help: String {
        switch self {
        case .background: L("graph.identity.mark.background")
        case .fork: L("graph.identity.mark.fork")
        case .worktree: L("graph.identity.mark.worktree")
        }
    }
    /// One word for the identity sentence.
    public var word: String {
        switch self {
        case .background: L("graph.identity.word.background")
        case .fork: L("graph.identity.word.fork")
        case .worktree: L("graph.identity.word.worktree")
        }
    }
}

/// What a flow block's header says about who it is.
public struct FlowIdentity: Equatable, Sendable {
    public var role: FlowRole
    public var ink: FlowInk
    /// The sub-agent's type as the CLI named it (`subagent_type`); nil for the lead and for a block that reported none.
    public var type: String?
    /// The type as the header's chip words it: nil when there is nothing to add to the title (no type, a fork, whose
    /// mark says it, or a title that is the type already).
    public var typeChip: String?
    /// The model before the block's own responses name the one that answered: the model the CLI resolved at launch,
    /// else the requested alias marked as requested (`GraphModelLabel.agentModelLabel`). Nil for the lead, whose label
    /// the run carries (`MightyGraphRun.nodeModelLabel`).
    public var modelLabel: String?
    /// The lead's effort when the pane named one ("high"); nil while the CLI decides and for sub-agents.
    public var effort: String?
    public var marks: [FlowMark]
    /// The whole identity in one line, for the header's tooltip and for VoiceOver.
    public var summary: String

    public init(role: FlowRole, ink: FlowInk, type: String? = nil, typeChip: String? = nil, modelLabel: String? = nil, effort: String? = nil,
                marks: [FlowMark] = [], summary: String) {
        self.role = role; self.ink = ink; self.type = type; self.typeChip = typeChip; self.modelLabel = modelLabel
        self.effort = effort; self.marks = marks; self.summary = summary
    }
}

public enum FlowRoles {
    /// The role's ink: teal for the lead, violet for sub-agents (as their blocks always were), the palette's secondary
    /// grey for background commands (which teal would make read as the main session), pink for the advisor. Inks far
    /// apart in both appearances, and none of them a state's colour (blue runs, amber waits).
    public static func ink(_ role: FlowRole) -> FlowInk {
        switch role {
        case .lead: .task
        case .subagent: .agent
        case .task: .neutral
        case .advisor: .steer
        }
    }

    /// The colours a Claude Code agent definition may name (`color:` in its frontmatter).
    public static let colors = ["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"]

    /// A custom agent's colour as a palette ink; nil for a colour Claude Code does not have. Yellow and orange are the
    /// palette's two ambers (`waitText`, `questionText`), which hold the same value today.
    public static func ink(color: String?) -> FlowInk? {
        switch color?.trimmingCharacters(in: .whitespaces).lowercased() {
        case "red": .err
        case "blue": .accent
        case "green": .done
        case "yellow": .wait
        case "purple": .agent
        case "orange": .question
        case "pink": .steer
        case "cyan": .task
        default: nil
        }
    }

    /// A custom agent's own colour takes the place of the sub-agent ink; the lead and the advisor keep theirs.
    public static func ink(_ role: FlowRole, color: String?) -> FlowInk {
        role == .subagent ? ink(color: color) ?? ink(role) : ink(role)
    }

    public static func roleName(_ role: FlowRole) -> String {
        switch role {
        case .lead: L("graph.identity.role.lead")
        case .subagent: L("graph.block.agent")
        case .task: L("graph.identity.role.task")
        case .advisor: L("graph.identity.role.advisor")
        }
    }

    /// An effort level as the composer names it; nil for "default" (the CLI decides) and for a word that is no level.
    public static func effortName(_ effort: String?) -> String? {
        switch effort {
        case "off": "Off"
        case "minimal": "Minimal"
        case "low": "Low"
        case "medium": "Medium"
        case "high": "High"
        case "xhigh": "XHigh"
        case "max": "Max"
        // Codex's: short as the others in a block's chip; the composer's menu says what it does (`composer.effort.ultra`).
        case "ultra": "Ultra"
        default: nil
        }
    }

    /// A type as a chip words it: a plugin's agent (`plugin:agent`) by its own name, any other as it is.
    public static func typeName(_ type: String) -> String {
        guard let colon = type.lastIndex(of: ":"), type.index(after: colon) < type.endIndex else { return type }
        return String(type[type.index(after: colon)...])
    }

    /// The marks of a sub-agent block, in a fixed order.
    public static func marks(_ agent: MightyGraphAgent) -> [FlowMark] {
        var marks: [FlowMark] = []
        if agent.background == true { marks.append(.background) }
        if agent.identity?.isFork == true { marks.append(.fork) }
        if agent.identity?.isolation == GraphAgentIdentity.isolations[0] { marks.append(.worktree) }
        return marks
    }

    /// The main session's identity on a run's 실행 block: the lead's ink, the pane's effort when it named one. Its
    /// model stays the run's own label and capsule.
    public static func lead(_ run: MightyGraphRun) -> FlowIdentity {
        let effort = effortName(run.configuredEffort)
        var parts = [roleName(.lead)]
        if let effort { parts.append(L("composer.effort.menu", ["effort": effort])) }
        return FlowIdentity(role: .lead, ink: ink(.lead), effort: effort, summary: parts.joined(separator: " · "))
    }

    /// A sub-agent block's identity; nil for a block that is no sub-agent (a background run, a steer, a context
    /// compaction, a question). `definition` is the custom agent its type names, when one was found
    /// (`AgentDefinitions`): its colour takes the sub-agent ink's place and its model stands in for a call that asked
    /// for none.
    public static func subagent(_ agent: MightyGraphAgent, definition: AgentDefinition? = nil) -> FlowIdentity? {
        guard agent.isSubagent else { return nil }
        let identity = agent.identity
        let type = identity?.type
        let marks = marks(agent)
        let title = MightyGraphSupport.blockTitle(agent)
        let chip: String? = type.flatMap { type in
            let name = typeName(type)
            return identity?.isFork == true || type == title || name == title ? nil : name
        }
        let model = GraphModelLabel.agentModelLabel(resolved: identity?.resolvedModel, requested: identity?.requestedModel ?? definition?.model)
        var parts = [roleName(.subagent)]
        if let type, identity?.isFork != true { parts.append(type) }
        if let resolved = identity?.resolvedModel { parts.append(ModelLabel.text(resolved)) }
        else if let requested = identity?.requestedModel ?? definition?.model, requested != "inherit" {
            parts.append(L("graph.identity.requestedModel", ["model": ModelLabel.text(requested)]))
        }
        parts += marks.map(\.word)
        return FlowIdentity(role: .subagent, ink: ink(.subagent, color: definition?.color), type: type, typeChip: chip, modelLabel: model,
                            marks: marks, summary: parts.joined(separator: " · "))
    }

    /// A background command's identity in the role view: the neutral ink, its background mark, and in words its role
    /// and the first line of its command. Nil for a block that is no background command.
    public static func task(_ agent: MightyGraphAgent) -> FlowIdentity? {
        guard agent.isTask else { return nil }
        let marks = marks(agent)
        let parts = [roleName(.task)] + marks.map(\.word) + [FlowCard.line(FlowCard.firstLine(agent.shownInput))].compactMap { $0 }
        return FlowIdentity(role: .task, ink: ink(.task), marks: marks, summary: parts.joined(separator: " · "))
    }

    /// The types the blocks of `runs` name, for looking their definitions up (`AgentDefinitions.load`).
    public static func types(_ runs: [MightyGraphRun]) -> Set<String> {
        var types = Set<String>()
        for run in runs { for agent in run.flowAgents { if let type = agent.identity?.type { types.insert(type) } } }
        return types
    }

    // MARK: Header room

    /// Which of a header's identity chips fit beside what the header always showed.
    public struct HeaderFit: Equatable, Sendable {
        /// The type chip's most width; nil leaves the chip out.
        public var typeWidth: CGFloat?
        public var showsEffort: Bool
        public var showsMarks: Bool
        public static let none = HeaderFit(typeWidth: nil, showsEffort: false, showsMarks: false)
    }

    /// The width a short label takes at `size` points, near enough to decide whether a chip has room: a wide
    /// (Hangul, kana, Han) character a full em, any other a little over half.
    public static func estimatedWidth(_ text: String, size: CGFloat, monospaced: Bool = false) -> CGFloat {
        text.unicodeScalars.reduce(CGFloat(0)) { width, scalar in
            let wide = (0x1100...0x11FF).contains(scalar.value) || (0x2E80...0xA4CF).contains(scalar.value)
                || (0xAC00...0xD7A3).contains(scalar.value) || (0xF900...0xFAFF).contains(scalar.value) || (0xFF00...0xFFEF).contains(scalar.value)
            return width + size * (wide ? 1 : monospaced ? 0.61 : 0.56)
        }
    }

    /// The least a block's title keeps before any identity chip is drawn.
    public static let titleFloor: CGFloat = 72
    /// A type chip narrower than this says nothing.
    public static let typeFloor: CGFloat = 40
    public static let typeCeiling: CGFloat = 110
    static let markWidth: CGFloat = 14
    static let chipPadding: CGFloat = 2 * DesignMetrics.Spacing.sm

    /// What fits in a block header `width` wide. The title, the status mark, the token capsule and the buttons were there
    /// first and keep their room (the title at least `titleFloor`, or its own width when it is shorter); what is left
    /// goes to the marks, then the effort, then the type chip, which takes the width given here (its type cut inside it)
    /// or is left out. A header at the block's least width that holds a status and a capsule shows none of them: the
    /// identity stays on the icon's tooltip.
    public static func headerFit(width: CGFloat, title: String, status: String?, capsule: String?, buttons: Int, identity: FlowIdentity) -> HeaderFit {
        let gap = DesignMetrics.Spacing.sm
        var used = 2 * DesignMetrics.Inset.graphBlockBodyH + 16 + gap
        used += min(titleFloor, estimatedWidth(title, size: 12)) + gap
        // The activity bars, the status pill and the capsule, each with the gap after it.
        if let status { used += 18 + gap + estimatedWidth(status, size: 10) + chipPadding + gap }
        if let capsule { used += estimatedWidth(capsule, size: 10, monospaced: true) + chipPadding + gap }
        used += CGFloat(buttons) * (16 + gap)
        var free = width - used
        var fit = HeaderFit.none
        if !identity.marks.isEmpty {
            let marks = CGFloat(identity.marks.count) * markWidth + gap
            if free >= marks { fit.showsMarks = true; free -= marks }
        }
        if let effort = identity.effort {
            let chip = markWidth + estimatedWidth(effort, size: 10) + gap
            if free >= chip { fit.showsEffort = true; free -= chip }
        }
        if let type = identity.typeChip, free >= typeFloor + gap {
            // A little over the estimate, so a type of wide letters is not cut while there is room.
            let wanted = (estimatedWidth(type, size: 10) * 1.15 + chipPadding + 4).rounded(.up)
            fit.typeWidth = max(typeFloor, min(typeCeiling, free - gap, wanted))
        }
        return fit
    }
}
