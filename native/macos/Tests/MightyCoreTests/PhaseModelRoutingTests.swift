import Foundation
import Testing
@testable import MightyCore

struct PhaseModelRoutingTests {

    // MARK: - Claude planning row

    @Test func claudePlanningRowWritesOpusAlias() {
        var config = PhaseModelConfig()
        PhaseModelRouting.applyClaudeRow(phase: .planning, value: "claude-opus-5-5", to: &config)
        #expect(config.claudeOpusAlias == "claude-opus-5-5")
        #expect(config.claudeMain == "default")
        #expect(config.claudeSonnetAlias == "default")
        #expect(config.claudeSubagentDefault == "default")
    }

    // MARK: - Claude execution row

    @Test func claudeExecutionRowWritesMainAndSonnetAlias() {
        var config = PhaseModelConfig()
        PhaseModelRouting.applyClaudeRow(phase: .execution, value: "claude-sonnet-5", to: &config)
        #expect(config.claudeMain == "claude-sonnet-5")
        #expect(config.claudeSonnetAlias == "claude-sonnet-5")
        #expect(config.claudeOpusAlias == "default")
        #expect(config.claudeSubagentDefault == "default")
    }

    // MARK: - Claude subagents row

    @Test func claudeSubagentsRowWritesSubagentDefault() {
        var config = PhaseModelConfig()
        PhaseModelRouting.applyClaudeRow(phase: .subagents, value: "claude-haiku-4-5-20251001", to: &config)
        #expect(config.claudeSubagentDefault == "claude-haiku-4-5-20251001")
        #expect(config.claudeMain == "default")
        #expect(config.claudeOpusAlias == "default")
        #expect(config.claudeSonnetAlias == "default")
    }

    // MARK: - claudeHaikuAlias never touched by any row

    @Test func claudeHaikuAliasUntouchedByAllRows() {
        var config = PhaseModelConfig(claudeHaikuAlias: "claude-haiku-4-5-20251001")
        for phase in PhaseModelRouting.Phase.allCases {
            PhaseModelRouting.applyClaudeRow(phase: phase, value: "claude-sonnet-5", to: &config)
        }
        #expect(config.claudeHaikuAlias == "claude-haiku-4-5-20251001")
    }

    // MARK: - Claude row state

    @Test func claudeExecutionRowStateMixedWhenKnobsDiffer() {
        let config = PhaseModelConfig(claudeMain: "claude-sonnet-5", claudeSonnetAlias: "claude-opus-5-5")
        #expect(PhaseModelRouting.claudeRowState(phase: .execution, config: config) == .mixed)
    }

    @Test func claudeExecutionRowStateUniformWhenKnobsMatch() {
        let config = PhaseModelConfig(claudeMain: "claude-sonnet-5", claudeSonnetAlias: "claude-sonnet-5")
        #expect(PhaseModelRouting.claudeRowState(phase: .execution, config: config) == .uniform("claude-sonnet-5"))
    }

    @Test func claudePlanningRowStateUniform() {
        let config = PhaseModelConfig(claudeOpusAlias: "claude-opus-5-5")
        #expect(PhaseModelRouting.claudeRowState(phase: .planning, config: config) == .uniform("claude-opus-5-5"))
    }

    @Test func claudeReviewRowStateIsNil() {
        #expect(PhaseModelRouting.claudeRowState(phase: .review, config: PhaseModelConfig()) == nil)
    }

    // MARK: - Codex review row

    @Test func codexReviewRowWritesReviewModel() {
        var config = PhaseModelConfig()
        PhaseModelRouting.applyCodexRow(phase: .review, value: "o4", to: &config)
        #expect(config.codexReviewModel == "o4")
        #expect(config.codexSubagentDefault == "default")
    }

    // MARK: - Codex subagents row

    @Test func codexSubagentsRowWritesSubagentDefault() {
        var config = PhaseModelConfig()
        PhaseModelRouting.applyCodexRow(phase: .subagents, value: "o4-mini", to: &config)
        #expect(config.codexSubagentDefault == "o4-mini")
        #expect(config.codexReviewModel == "default")
    }

    // MARK: - Codex non-model knob untouched by all rows

    @Test func codexPlanModeReasoningEffortUntouchedByAllRows() {
        var config = PhaseModelConfig(codexPlanModeReasoningEffort: "high")
        for phase in PhaseModelRouting.Phase.allCases {
            PhaseModelRouting.applyCodexRow(phase: phase, value: "o4", to: &config)
        }
        #expect(config.codexPlanModeReasoningEffort == "high")
    }

    @Test func codexPlanningAndExecutionRowStatesAreNil() {
        let config = PhaseModelConfig()
        #expect(PhaseModelRouting.codexRowState(phase: .planning, config: config) == nil)
        #expect(PhaseModelRouting.codexRowState(phase: .execution, config: config) == nil)
    }

    // MARK: - omc phase mapping

    @Test func omcPlanningPhaseKeysArePlannerArchitectCritic() {
        let keys = PhaseModelRouting.omcPhaseKeys(.planning)
        #expect(Set(keys) == Set(["planner", "architect", "critic"]))
    }

    @Test func omcExecutionPhaseKeyIsExecutor() {
        #expect(PhaseModelRouting.omcPhaseKeys(.execution) == ["executor"])
    }

    @Test func omcReviewPhaseKeysAreCodeReviewerVerifier() {
        let keys = PhaseModelRouting.omcPhaseKeys(.review)
        #expect(Set(keys) == Set(["codeReviewer", "verifier"]))
    }

    @Test func omcSubagentsPhaseKeysEmpty() {
        #expect(PhaseModelRouting.omcPhaseKeys(.subagents).isEmpty)
    }

    @Test func omcAgentPhaseHelperMapping() {
        #expect(PhaseModelRouting.omcAgentPhase("planner")      == .planning)
        #expect(PhaseModelRouting.omcAgentPhase("architect")    == .planning)
        #expect(PhaseModelRouting.omcAgentPhase("critic")       == .planning)
        #expect(PhaseModelRouting.omcAgentPhase("executor")     == .execution)
        #expect(PhaseModelRouting.omcAgentPhase("codeReviewer") == .review)
        #expect(PhaseModelRouting.omcAgentPhase("verifier")     == .review)
        // details-only
        #expect(PhaseModelRouting.omcAgentPhase("securityReviewer")    == nil)
        #expect(PhaseModelRouting.omcAgentPhase("testEngineer")        == nil)
        #expect(PhaseModelRouting.omcAgentPhase("qaTester")            == nil)
        #expect(PhaseModelRouting.omcAgentPhase("gitMaster")           == nil)
        #expect(PhaseModelRouting.omcAgentPhase("codeSimplifier")      == nil)
        #expect(PhaseModelRouting.omcAgentPhase("documentSpecialist")  == nil)
    }

    @Test func omcRowAppliesValueToAllPhaseAgents() {
        var config = PhaseModelConfig(omcAgents: [
            "planner": "default", "architect": "default", "critic": "default",
            "executor": "default", "codeReviewer": "default", "verifier": "default"
        ])
        PhaseModelRouting.applyOmcRow(phase: .planning, value: "claude-opus-5-5", to: &config)
        #expect(config.omcAgents?["planner"]      == "claude-opus-5-5")
        #expect(config.omcAgents?["architect"]    == "claude-opus-5-5")
        #expect(config.omcAgents?["critic"]       == "claude-opus-5-5")
        #expect(config.omcAgents?["executor"]     == "default")
        #expect(config.omcAgents?["codeReviewer"] == "default")
        #expect(config.omcAgents?["verifier"]     == "default")
    }

    @Test func omcRowMixedWhenAgentsDiffer() {
        let config = PhaseModelConfig(omcAgents: [
            "planner": "claude-opus-5-5", "architect": "claude-sonnet-5", "critic": "claude-sonnet-5"
        ])
        #expect(PhaseModelRouting.omcRowState(phase: .planning, config: config) == .mixed)
    }

    @Test func omcRowUniformWhenAgentsMatch() {
        let config = PhaseModelConfig(omcAgents: [
            "planner": "claude-opus-5-5", "architect": "claude-opus-5-5", "critic": "claude-opus-5-5"
        ])
        #expect(PhaseModelRouting.omcRowState(phase: .planning, config: config) == .uniform("claude-opus-5-5"))
    }

    // MARK: - omc section absent when not installed

    @Test func omcRowStateNilWhenNotInstalled() {
        let config = PhaseModelConfig(omcAgents: nil)
        for phase in PhaseModelRouting.Phase.allCases {
            #expect(PhaseModelRouting.omcRowState(phase: phase, config: config) == nil)
        }
    }

    @Test func omcApplyRowIsNoopWhenNotInstalled() {
        var config = PhaseModelConfig(omcAgents: nil)
        PhaseModelRouting.applyOmcRow(phase: .planning, value: "x", to: &config)
        #expect(config.omcAgents == nil)
    }

    // MARK: - Ouroboros phase mapping

    @Test func ouroborosPlanningPhaseKeyIsClarificationDefaultModel() {
        #expect(PhaseModelRouting.ouroborosPhaseKeys(.planning) == ["clarification.default_model"])
    }

    @Test func ouroborosReviewPhaseKeysAreSemanticJudgeQA() {
        let keys = PhaseModelRouting.ouroborosPhaseKeys(.review)
        #expect(Set(keys) == Set(["evaluation.semantic_model", "consensus.judge_model", "llm.qa_model"]))
    }

    @Test func ouroborosExecutionAndSubagentsPhaseKeysEmpty() {
        #expect(PhaseModelRouting.ouroborosPhaseKeys(.execution).isEmpty)
        #expect(PhaseModelRouting.ouroborosPhaseKeys(.subagents).isEmpty)
    }

    @Test func ouroborosKeyPhaseHelperMapping() {
        #expect(PhaseModelRouting.ouroborosKeyPhase("clarification.default_model")          == .planning)
        #expect(PhaseModelRouting.ouroborosKeyPhase("evaluation.semantic_model")             == .review)
        #expect(PhaseModelRouting.ouroborosKeyPhase("consensus.judge_model")                 == .review)
        #expect(PhaseModelRouting.ouroborosKeyPhase("llm.qa_model")                          == .review)
        // details-only
        #expect(PhaseModelRouting.ouroborosKeyPhase("llm.dependency_analysis_model")         == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("llm.ontology_analysis_model")           == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("llm.context_compression_model")         == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("resilience.wonder_model")               == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("resilience.reflect_model")              == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("evaluation.assertion_extraction_model") == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("consensus.advocate_model")              == nil)
        #expect(PhaseModelRouting.ouroborosKeyPhase("consensus.devil_model")                 == nil)
    }

    @Test func ouroborosRowAppliesValueToAllPhaseKeys() {
        var config = PhaseModelConfig(ouroborosKeys: [
            "clarification.default_model": "default",
            "evaluation.semantic_model": "default",
            "consensus.judge_model": "default",
            "llm.qa_model": "default",
            "llm.dependency_analysis_model": "claude-sonnet-5"  // details-only, must not change
        ])
        PhaseModelRouting.applyOuroborosRow(phase: .review, value: "claude-opus-5-5", to: &config)
        #expect(config.ouroborosKeys?["evaluation.semantic_model"]  == "claude-opus-5-5")
        #expect(config.ouroborosKeys?["consensus.judge_model"]      == "claude-opus-5-5")
        #expect(config.ouroborosKeys?["llm.qa_model"]               == "claude-opus-5-5")
        // planning key untouched by review row
        #expect(config.ouroborosKeys?["clarification.default_model"] == "default")
        // details-only key untouched
        #expect(config.ouroborosKeys?["llm.dependency_analysis_model"] == "claude-sonnet-5")
    }

    @Test func ouroborosRowMixedWhenKeysDiffer() {
        let config = PhaseModelConfig(ouroborosKeys: [
            "evaluation.semantic_model": "claude-opus-5-5",
            "consensus.judge_model":     "claude-sonnet-5",
            "llm.qa_model":              "claude-sonnet-5"
        ])
        #expect(PhaseModelRouting.ouroborosRowState(phase: .review, config: config) == .mixed)
    }

    @Test func ouroborosRowUniformWhenKeysMatch() {
        let config = PhaseModelConfig(ouroborosKeys: [
            "evaluation.semantic_model": "claude-opus-5-5",
            "consensus.judge_model":     "claude-opus-5-5",
            "llm.qa_model":              "claude-opus-5-5"
        ])
        #expect(PhaseModelRouting.ouroborosRowState(phase: .review, config: config) == .uniform("claude-opus-5-5"))
    }

    // MARK: - Ouroboros section absent when not installed

    @Test func ouroborosRowStateNilWhenNotInstalled() {
        let config = PhaseModelConfig(ouroborosKeys: nil)
        for phase in PhaseModelRouting.Phase.allCases {
            #expect(PhaseModelRouting.ouroborosRowState(phase: phase, config: config) == nil)
        }
    }

    @Test func ouroborosApplyRowIsNoopWhenNotInstalled() {
        var config = PhaseModelConfig(ouroborosKeys: nil)
        PhaseModelRouting.applyOuroborosRow(phase: .review, value: "x", to: &config)
        #expect(config.ouroborosKeys == nil)
    }
}
