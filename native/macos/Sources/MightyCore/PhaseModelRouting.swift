import Foundation

// MARK: - PhaseModelConfig

/// Machine-wide per-phase / per-role model knobs.
///
/// Claude and Codex knobs are hardcoded and delivered as per-run arguments.
/// omc knobs are scanned from ~/.config/claude-omc/config.jsonc agents section;
/// nil means omc is not installed and the section is hidden.
/// Ouroboros knobs are scanned from ~/.ouroboros/config.yaml model keys;
/// nil means Ouroboros is not installed and the section is hidden.
public struct PhaseModelConfig: Sendable, Equatable {
    // Claude (hardcoded, per-run via --model / --settings env)
    public var claudeMain: String            // --model  →  execution
    public var claudeOpusAlias: String       // ANTHROPIC_DEFAULT_OPUS_MODEL  →  planning
    public var claudeSonnetAlias: String     // ANTHROPIC_DEFAULT_SONNET_MODEL  →  execution
    public var claudeHaikuAlias: String      // ANTHROPIC_DEFAULT_HAIKU_MODEL  →  not phase-mapped
    public var claudeSubagentDefault: String // CLAUDE_CODE_SUBAGENT_MODEL  →  subagents

    // Codex (hardcoded, per-run via -c)
    public var codexReviewModel: String              // review_model  →  review
    public var codexSubagentDefault: String          // agents.default_subagent_model  →  subagents
    public var codexPlanModeReasoningEffort: String  // plan_mode_reasoning_effort  →  non-model / details-only

    // omc (scanned; nil = not installed)
    public var omcAgents: [String: String]?     // camelCase agent key → model

    // Ouroboros (scanned; nil = not installed)
    public var ouroborosKeys: [String: String]? // dotted config path → model

    public init(
        claudeMain: String = "default",
        claudeOpusAlias: String = "default",
        claudeSonnetAlias: String = "default",
        claudeHaikuAlias: String = "default",
        claudeSubagentDefault: String = "default",
        codexReviewModel: String = "default",
        codexSubagentDefault: String = "default",
        codexPlanModeReasoningEffort: String = "default",
        omcAgents: [String: String]? = nil,
        ouroborosKeys: [String: String]? = nil
    ) {
        self.claudeMain = claudeMain
        self.claudeOpusAlias = claudeOpusAlias
        self.claudeSonnetAlias = claudeSonnetAlias
        self.claudeHaikuAlias = claudeHaikuAlias
        self.claudeSubagentDefault = claudeSubagentDefault
        self.codexReviewModel = codexReviewModel
        self.codexSubagentDefault = codexSubagentDefault
        self.codexPlanModeReasoningEffort = codexPlanModeReasoningEffort
        self.omcAgents = omcAgents
        self.ouroborosKeys = ouroborosKeys
    }
}

// MARK: - PhaseModelRouting

public enum PhaseModelRouting {

    // MARK: Phase

    public enum Phase: String, CaseIterable, Sendable, Equatable {
        case planning, execution, review, subagents
    }

    // MARK: Row state

    /// The display state of a phase summary row.
    public enum RowState: Equatable, Sendable {
        /// All mapped single-model knobs share this value.
        case uniform(String)
        /// Mapped knobs differ; shown as "혼합".
        case mixed
    }

    // MARK: Phase mapping: omc

    /// Phase for an omc agent key (plain name or camelCase), or nil for details-only.
    public static func omcAgentPhase(_ key: String) -> Phase? {
        switch key {
        case "planner", "architect", "critic": return .planning
        case "executor":                        return .execution
        case "codeReviewer", "verifier":       return .review
        default:                                return nil
        }
    }

    /// omc agent keys that map to the given phase.
    public static func omcPhaseKeys(_ phase: Phase) -> [String] {
        switch phase {
        case .planning:  return ["planner", "architect", "critic"]
        case .execution: return ["executor"]
        case .review:    return ["codeReviewer", "verifier"]
        case .subagents: return []
        }
    }

    // MARK: Phase mapping: Ouroboros

    /// Phase for an Ouroboros dotted-key, or nil for details-only.
    public static func ouroborosKeyPhase(_ key: String) -> Phase? {
        switch key {
        case "clarification.default_model":                         return .planning
        case "evaluation.semantic_model",
             "consensus.judge_model",
             "llm.qa_model":                                        return .review
        default:                                                     return nil
        }
    }

    /// Ouroboros config keys that map to the given phase.
    public static func ouroborosPhaseKeys(_ phase: Phase) -> [String] {
        switch phase {
        case .planning:  return ["clarification.default_model"]
        case .execution: return []
        case .review:    return ["evaluation.semantic_model", "consensus.judge_model", "llm.qa_model"]
        case .subagents: return []
        }
    }

    // MARK: Apply row

    /// Writes `value` to every Claude single-model knob mapped to `phase`.
    public static func applyClaudeRow(phase: Phase, value: String, to config: inout PhaseModelConfig) {
        switch phase {
        case .planning:
            config.claudeOpusAlias = value
        case .execution:
            config.claudeMain = value
            config.claudeSonnetAlias = value
        case .review:
            break
        case .subagents:
            config.claudeSubagentDefault = value
        }
    }

    /// Writes `value` to every Codex single-model knob mapped to `phase`.
    /// The non-model knob `codexPlanModeReasoningEffort` is never touched.
    public static func applyCodexRow(phase: Phase, value: String, to config: inout PhaseModelConfig) {
        switch phase {
        case .review:
            config.codexReviewModel = value
        case .subagents:
            config.codexSubagentDefault = value
        case .planning, .execution:
            break
        }
    }

    /// Writes `value` to every omc agent key mapped to `phase`.
    /// No-op when `config.omcAgents` is nil (omc not installed).
    public static func applyOmcRow(phase: Phase, value: String, to config: inout PhaseModelConfig) {
        guard config.omcAgents != nil else { return }
        for key in omcPhaseKeys(phase) { config.omcAgents![key] = value }
    }

    /// Writes `value` to every Ouroboros key mapped to `phase`.
    /// No-op when `config.ouroborosKeys` is nil (Ouroboros not installed).
    public static func applyOuroborosRow(phase: Phase, value: String, to config: inout PhaseModelConfig) {
        guard config.ouroborosKeys != nil else { return }
        for key in ouroborosPhaseKeys(phase) { config.ouroborosKeys![key] = value }
    }

    // MARK: Row state

    /// Row state for Claude at `phase`, or nil when no single-model knobs map to it.
    public static func claudeRowState(phase: Phase, config: PhaseModelConfig) -> RowState? {
        switch phase {
        case .planning:
            return rowState(from: [config.claudeOpusAlias])
        case .execution:
            return rowState(from: [config.claudeMain, config.claudeSonnetAlias])
        case .review:
            return nil
        case .subagents:
            return rowState(from: [config.claudeSubagentDefault])
        }
    }

    /// Row state for Codex at `phase`, or nil when no single-model knobs map to it.
    public static func codexRowState(phase: Phase, config: PhaseModelConfig) -> RowState? {
        switch phase {
        case .planning, .execution:
            return nil
        case .review:
            return rowState(from: [config.codexReviewModel])
        case .subagents:
            return rowState(from: [config.codexSubagentDefault])
        }
    }

    /// Row state for omc at `phase`, or nil when omc is not installed or no agents map to it.
    public static func omcRowState(phase: Phase, config: PhaseModelConfig) -> RowState? {
        guard let agents = config.omcAgents else { return nil }
        let vals = omcPhaseKeys(phase).compactMap { agents[$0] }
        return vals.isEmpty ? nil : rowState(from: vals)
    }

    /// Row state for Ouroboros at `phase`, or nil when Ouroboros is not installed or no keys map to it.
    public static func ouroborosRowState(phase: Phase, config: PhaseModelConfig) -> RowState? {
        guard let keys = config.ouroborosKeys else { return nil }
        let vals = ouroborosPhaseKeys(phase).compactMap { keys[$0] }
        return vals.isEmpty ? nil : rowState(from: vals)
    }

    // MARK: Private

    private static func rowState(from values: [String]) -> RowState {
        let unique = Set(values)
        return unique.count == 1 ? .uniform(unique.first!) : .mixed
    }
}
