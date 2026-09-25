namespace MightyClaude.Core;

public enum Phase { Planning, Execution, Review, Subagents }

public sealed class RowState
{
    public bool IsUniform => Value is not null;
    public string? Value { get; private init; }
    public static RowState Uniform(string value) => new() { Value = value };
    public static readonly RowState Mixed = new();
    public override bool Equals(object? obj) => obj is RowState other && Value == other.Value;
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}

public static class PhaseModelRouting
{
    public static Phase? OmcAgentPhase(string key) => key switch
    {
        "planner" or "architect" or "critic" => Phase.Planning,
        "executor" => Phase.Execution,
        "codeReviewer" or "verifier" => Phase.Review,
        _ => null
    };

    public static string[] OmcPhaseKeys(Phase phase) => phase switch
    {
        Phase.Planning => ["planner", "architect", "critic"],
        Phase.Execution => ["executor"],
        Phase.Review => ["codeReviewer", "verifier"],
        _ => []
    };

    public static Phase? OuroborosKeyPhase(string key) => key switch
    {
        "clarification.default_model" => Phase.Planning,
        "evaluation.semantic_model" or "consensus.judge_model" or "llm.qa_model" => Phase.Review,
        _ => null
    };

    public static string[] OuroborosPhaseKeys(Phase phase) => phase switch
    {
        Phase.Planning => ["clarification.default_model"],
        Phase.Review => ["evaluation.semantic_model", "consensus.judge_model", "llm.qa_model"],
        _ => []
    };

    public static PhaseModelsSnapshot ApplyClaudeRow(Phase phase, string value, PhaseModelsSnapshot config) =>
        phase switch
        {
            Phase.Planning => config with { ClaudeOpusAlias = value },
            Phase.Execution => config with { ClaudeMain = value, ClaudeSonnetAlias = value },
            Phase.Subagents => config with { ClaudeSubagentDefault = value },
            _ => config
        };

    public static PhaseModelsSnapshot ApplyCodexRow(Phase phase, string value, PhaseModelsSnapshot config) =>
        phase switch
        {
            Phase.Review => config with { CodexReviewModel = value },
            Phase.Subagents => config with { CodexSubagentDefault = value },
            _ => config
        };

    public static Dictionary<string, string> ApplyOmcRow(Phase phase, string value, Dictionary<string, string> agents)
    {
        var updated = new Dictionary<string, string>(agents);
        foreach (var key in OmcPhaseKeys(phase)) updated[key] = value;
        return updated;
    }

    public static Dictionary<string, string> ApplyOuroborosRow(Phase phase, string value, Dictionary<string, string> keys)
    {
        var updated = new Dictionary<string, string>(keys);
        foreach (var key in OuroborosPhaseKeys(phase)) updated[key] = value;
        return updated;
    }

    public static RowState? ClaudeRowState(Phase phase, PhaseModelsSnapshot config) => phase switch
    {
        Phase.Planning => RowStateFrom([config.ClaudeOpusAlias]),
        Phase.Execution => RowStateFrom([config.ClaudeMain, config.ClaudeSonnetAlias]),
        Phase.Subagents => RowStateFrom([config.ClaudeSubagentDefault]),
        _ => null
    };

    public static RowState? CodexRowState(Phase phase, PhaseModelsSnapshot config) => phase switch
    {
        Phase.Review => RowStateFrom([config.CodexReviewModel]),
        Phase.Subagents => RowStateFrom([config.CodexSubagentDefault]),
        _ => null
    };

    public static RowState? OmcRowState(Phase phase, Dictionary<string, string>? agents)
    {
        if (agents is null) return null;
        var vals = OmcPhaseKeys(phase).Where(agents.ContainsKey).Select(k => agents[k]).ToArray();
        return vals.Length == 0 ? null : RowStateFrom(vals);
    }

    public static RowState? OuroborosRowState(Phase phase, Dictionary<string, string>? keys)
    {
        if (keys is null) return null;
        var vals = OuroborosPhaseKeys(phase).Where(keys.ContainsKey).Select(k => keys[k]).ToArray();
        return vals.Length == 0 ? null : RowStateFrom(vals);
    }

    private static RowState RowStateFrom(string[] values)
    {
        var unique = values.Distinct().ToArray();
        return unique.Length == 1 ? RowState.Uniform(unique[0]) : RowState.Mixed;
    }
}
