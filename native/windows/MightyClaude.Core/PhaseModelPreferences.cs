namespace MightyClaude.Core;

public sealed record PhaseModelVersion(string Value, string Label);

/// Provider-specific settings shared by the native settings UI and every run entry point.
public static class PhaseModelPreferences
{
    public static PhaseModelsSnapshot? Normalize(PhaseModelsSnapshot? value)
    {
        if (value is null) return null;
        static string Model(string? name) => Wire.Model(name) ? name! : "default";
        static string? Effort(string? effort) => Wire.Efforts.Contains(effort) ? effort : null;
        return value with { ClaudeMain = Model(value.ClaudeMain), ClaudeOpusAlias = Model(value.ClaudeOpusAlias), ClaudeSonnetAlias = Model(value.ClaudeSonnetAlias), ClaudeHaikuAlias = Model(value.ClaudeHaikuAlias), ClaudeSubagentDefault = Model(value.ClaudeSubagentDefault), CodexReviewModel = Model(value.CodexReviewModel), CodexSubagentDefault = Model(value.CodexSubagentDefault), CodexPlanModeReasoningEffort = Effort(value.CodexPlanModeReasoningEffort) ?? "default", ClaudeMainEffort = Effort(value.ClaudeMainEffort), CodexMainEffort = Effort(value.CodexMainEffort), CodexSubagentEffort = Effort(value.CodexSubagentEffort) };
    }

    public static string EffectiveModel(StartRunRequest request) => request.Provider == "claude" && request.Model == "default" ? Normalize(request.PhaseModels)?.ClaudeMain ?? "default" : request.Model;
    public static string? MainEffort(StartRunRequest request) => request.Provider switch { "claude" => Normalize(request.PhaseModels)?.ClaudeMainEffort, "codex" => Normalize(request.PhaseModels)?.CodexMainEffort, _ => null };
    public static StartRunRequest ResolveEffort(StartRunRequest request, ModelCatalog catalog)
    {
        var settings = request.Settings ?? new();
        if (settings.Effort != "default" || MainEffort(request) is not { } effort) return request;
        return ProviderCatalog.Efforts(request.Provider, EffectiveModel(request), catalog, request.RegisteredModels).Contains(effort)
            ? request with { Settings = settings with { Effort = effort } } : request;
    }

    public static PhaseModelEdit ApplyRow(string provider, Phase phase, string value, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        if (!Wire.Model(value) || provider is not ("claude" or "codex")) throw new ArgumentException("Invalid phase model.");
        var supported = provider == "claude" ? PhaseModelRouting.ClaudeRowState(phase, config) : PhaseModelRouting.CodexRowState(phase, config);
        if (supported is null) throw new ArgumentException("This phase uses the pane model.");
        return new(provider == "claude" ? PhaseModelRouting.ApplyClaudeRow(phase, value, config) : PhaseModelRouting.ApplyCodexRow(phase, value, config),
            tools.OmcAgents is null ? null : PhaseModelRouting.ApplyOmcRow(phase, value, tools.OmcAgents),
            tools.OuroborosKeys is null ? null : PhaseModelRouting.ApplyOuroborosRow(phase, value, tools.OuroborosKeys));
    }

    public static PhaseModelsSnapshot SetEffort(PhaseModelsSnapshot config, string provider, Phase phase, string value)
    {
        if (value != "default" && !Wire.Efforts.Contains(value)) throw new ArgumentException("Invalid phase effort.");
        var effort = value == "default" ? null : value;
        return (provider, phase) switch {
            ("claude", Phase.Execution) => config with { ClaudeMainEffort = effort },
            ("codex", Phase.Execution) => config with { CodexMainEffort = effort },
            ("codex", Phase.Planning) => config with { CodexPlanModeReasoningEffort = value },
            ("codex", Phase.Subagents) => config with { CodexSubagentEffort = effort },
            _ => throw new ArgumentException("This phase has no effort setting.") };
    }

    public static IReadOnlyList<PhaseModelVersion> Versions(ModelCatalog catalog, IReadOnlyList<RegisteredModelEntry> registered, string current)
    {
        var seen = new HashSet<string>(registered.Select(r => r.Name), StringComparer.Ordinal) { "default", PhaseModelSection.MixedSentinel };
        var values = new List<PhaseModelVersion>();
        foreach (var option in catalog.Models)
        {
            var resolved = option.ResolvedModel is { Length: > 0 } id && id != option.Value ? id : null;
            if (seen.Add(option.Value)) values.Add(new(option.Value, resolved is null ? ModelLabel.Option(option) : Locale.Get("settings.phaseModels.latestTemplate", new Dictionary<string, string> { ["name"] = ModelLabel.Option(option) })));
            if (resolved is not null && seen.Add(resolved)) values.Add(new(resolved, ModelLabel.Text(resolved)));
        }
        if (seen.Add(current)) values.Add(new(current, ModelLabel.Text(current)));
        values.AddRange(registered.DistinctBy(r => r.Name).Select(r => new PhaseModelVersion(r.Name, ModelLabel.Text(r.Name))));
        return values;
    }

    public static string[] Efforts(string provider, ModelCatalog catalog, string current)
    {
        var advertised = catalog.Models.SelectMany(m => m.SupportedEffortLevels ?? []).ToHashSet();
        var values = provider == "codex" ? advertised.Count == 0 ? ["low", "medium", "high"] : Wire.Efforts.Where(advertised.Contains).ToArray() : Wire.Efforts;
        return current != "default" && !values.Contains(current) ? [.. values, current] : values;
    }

    public static RegisteredModelEntry Registration(string name, IReadOnlyList<RegisteredModelEntry> existing, bool supportsEffort, IEnumerable<string> levels)
    {
        var trimmed = name.Trim(); var effort = levels.Distinct().ToArray();
        string? key = trimmed.Length == 0 ? "settings.modelDefaults.error.empty" : trimmed == "default" ? "settings.modelDefaults.error.reserved" : !Wire.Model(trimmed) ? "settings.modelDefaults.error.invalid" : existing.Any(e => e.Name == trimmed) ? "settings.modelDefaults.error.duplicate" : supportsEffort && effort.Length == 0 ? "settings.modelDefaults.error.effortWithoutLevels" : effort.Any(e => !Wire.Efforts.Contains(e)) ? "settings.modelDefaults.error.unknownLevel" : null;
        if (key is not null) throw new ArgumentException(Locale.Get(key));
        return new(trimmed, supportsEffort, supportsEffort ? Wire.Efforts.Where(effort.Contains).ToArray() : []);
    }
}
