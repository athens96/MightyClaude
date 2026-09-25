namespace MightyClaude.Core;

/// 페이즈별 모델 화면이 무엇을 그릴지 정하는 곳. WinUI는 여기서 나온 줄만 그린다.
///
/// 화면은 두 층이다.
///   1. 페이즈 묶음 줄 넷(계획·실행·검토·서브에이전트). 한 줄을 고르면 그 페이즈에
///      매인 Claude·Codex·omc·Ouroboros의 단일 모델 손잡이가 모두 같은 값이 된다.
///      매인 손잡이들의 값이 서로 다르면 줄은 "혼합"으로 보인다.
///   2. 도구별 자세히 줄. 손잡이 하나를 따로 바꾸는 자리이고, 페이즈에 매이지 않는
///      손잡이(Codex plan_mode_reasoning_effort, Ouroboros 목록 키)도 여기서만 바꾼다.
///
/// omc 묶음은 installed_plugins.json이 omc 설치를 보일 때만, Ouroboros 묶음은
/// ~/.ouroboros/config.yaml이 있을 때만 나온다. 없으면 그 묶음은 통째로 빠진다.
public static class PhaseModelSection
{
    /// 섞인 줄이 고른 값으로 쓰는 표. 사용자가 이 값을 고를 수는 없다.
    public const string MixedSentinel = "__mixed__";

    public const string ClaudeTool = "claude";
    public const string CodexTool = "codex";
    public const string OmcTool = "omc";
    public const string OuroborosTool = "ouroboros";

    /// 화면이 보여 주는 페이즈 줄, macOS와 같은 차례.
    public static IReadOnlyList<Phase> Phases { get; } =
        [Phase.Planning, Phase.Execution, Phase.Review, Phase.Subagents];

    public static string SectionTitle => Locale.Get("settings.phaseModels.sectionTitle");
    public static string Description => Locale.Get("settings.phaseModels.description");
    public static string DefaultOption => Locale.Get("settings.phaseModels.defaultOption");
    public static string MixedLabel => Locale.Get("settings.phaseModels.mixed");

    public static string PhaseLabel(Phase phase) => phase switch
    {
        Phase.Planning => Locale.Get("settings.phaseModels.phase.planning"),
        Phase.Execution => Locale.Get("settings.phaseModels.phase.execution"),
        Phase.Review => Locale.Get("settings.phaseModels.phase.review"),
        _ => Locale.Get("settings.phaseModels.phase.subagents"),
    };

    public static string ToolLabel(string tool) => tool switch
    {
        OmcTool => Locale.Get("settings.phaseModels.tool.omc"),
        OuroborosTool => Locale.Get("settings.phaseModels.tool.ouroboros"),
        _ => ProviderCatalog.Name(tool),
    };

    public static string FileError(string path) =>
        Locale.Get("settings.phaseModels.fileError", new Dictionary<string, string> { ["path"] = path });

    // MARK: - 읽기

    /// 두 바깥 도구의 현재 값을 읽는다. 설치되지 않았으면 null이고, 읽을 수 없으면
    /// Error에 보여 줄 수 있는 말을 담아 돌려준다 — 파일은 그대로 둔다.
    public static PhaseModelTools LoadTools(string? homeDirectory = null)
    {
        var store = new ModelSettingsFileStore(homeDirectory);
        var catalog = new OmcAgentCatalog(homeDirectory);
        string? error = null;

        // 에이전트 목록은 설치본에서, 값은 config.jsonc에서만 온다. frontmatter
        // `model:`은 보여 주는 기본값일 뿐이라 값으로 쓰지 않는다 — 쓰면 한 번의
        // 고름이 모든 에이전트의 기본값을 config.jsonc에 박아 버린다.
        var defaults = catalog.Scan();
        Dictionary<string, string>? omc = null;
        if (defaults is not null)
        {
            omc = defaults.Keys.ToDictionary(key => key, _ => "default");
            try
            {
                foreach (var (key, model) in store.LoadOmcAgents() ?? [])
                    if (omc.ContainsKey(key)) omc[key] = model;
            }
            catch (Exception) { error ??= FileError(store.OmcConfigPath); }
        }

        Dictionary<string, string>? ouroboros = null;
        try { ouroboros = store.LoadOuroborosKeys(); }
        catch (Exception) { error ??= FileError(store.OuroborosConfigPath); }

        return new(omc, ouroboros, error, defaults);
    }

    /// 스모크가 쓰는 붙박이 값. 스모크는 실제 사용자 파일을 읽지도 쓰지도 않으면서도
    /// 네 묶음의 글자를 모두 그려 보여야 하므로, 설치된 척하는 값을 여기서 준다.
    public static PhaseModelTools SmokeFixtureTools { get; } = new(
        new()
        {
            ["planner"] = "default",
            ["executor"] = "default",
            ["codeReviewer"] = "default",
            ["verifier"] = "default",
        },
        new()
        {
            ["clarification.default_model"] = "default",
            ["evaluation.semantic_model"] = "default",
            ["consensus.judge_model"] = "default",
            ["llm.qa_model"] = "default",
        },
        OmcDefaults: new Dictionary<string, string>
        {
            ["planner"] = "opus",
            ["executor"] = "sonnet",
            ["codeReviewer"] = "opus",
            ["verifier"] = "sonnet",
        });

    /// omc 자세히 줄의 이름: 설치본이 정한 기본 모델이 있으면 함께 보여 준다.
    public static string OmcAgentLabel(string key, IReadOnlyDictionary<string, string>? defaults) =>
        defaults is not null && defaults.TryGetValue(key, out var model) && model != "default"
            ? Locale.Get("settings.phaseModels.omcAgentDefault",
                new Dictionary<string, string> { ["agent"] = key, ["model"] = model })
            : key;

    // MARK: - 줄

    /// 페이즈 줄 넷. 매인 손잡이들이 한 값이면 그 값, 다르면 MixedSentinel.
    public static IReadOnlyList<PhaseModelSummaryRow> SummaryRows(
        PhaseModelsSnapshot config, PhaseModelTools tools) =>
        [.. Phases.Select(phase =>
        {
            var state = SummaryRowState(phase, config, tools);
            return new PhaseModelSummaryRow(
                phase,
                PhaseLabel(phase),
                state is null or { IsUniform: false } ? MixedSentinel : state.Value!,
                state is not null && !state.IsUniform);
        })];

    /// 한 페이즈에 매인 네 도구의 모든 단일 모델 손잡이를 하나의 상태로 모은다.
    public static RowState? SummaryRowState(Phase phase, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        List<RowState> states = [];
        foreach (var state in new[]
                 {
                     PhaseModelRouting.ClaudeRowState(phase, config),
                     PhaseModelRouting.CodexRowState(phase, config),
                     PhaseModelRouting.OmcRowState(phase, tools.OmcAgents),
                     PhaseModelRouting.OuroborosRowState(phase, tools.OuroborosKeys),
                 })
            if (state is not null) states.Add(state);

        if (states.Count == 0) return null;
        if (states.Any(state => !state.IsUniform)) return RowState.Mixed;
        var values = states.Select(state => state.Value!).Distinct().ToArray();
        return values.Length == 1 ? RowState.Uniform(values[0]) : RowState.Mixed;
    }

    /// 도구별 자세히 묶음. 설치되지 않은 도구는 아예 빠진다.
    public static IReadOnlyList<PhaseModelToolBlock> ToolBlocks(
        PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        List<PhaseModelToolBlock> blocks =
        [
            new(ClaudeTool, ToolLabel(ClaudeTool),
            [
                new("claude.claudeMain", Locale.Get("settings.phaseModels.knob.claudeMain"), config.ClaudeMain),
                new("claude.claudeOpusAlias", Locale.Get("settings.phaseModels.knob.claudeOpusAlias"), config.ClaudeOpusAlias),
                new("claude.claudeSonnetAlias", Locale.Get("settings.phaseModels.knob.claudeSonnetAlias"), config.ClaudeSonnetAlias),
                new("claude.claudeHaikuAlias", Locale.Get("settings.phaseModels.knob.claudeHaikuAlias"), config.ClaudeHaikuAlias),
                new("claude.claudeSubagentDefault", Locale.Get("settings.phaseModels.knob.claudeSubagent"), config.ClaudeSubagentDefault),
            ]),
            new(CodexTool, ToolLabel(CodexTool),
            [
                new("codex.codexReviewModel", Locale.Get("settings.phaseModels.knob.codexReview"), config.CodexReviewModel),
                new("codex.codexSubagentDefault", Locale.Get("settings.phaseModels.knob.codexSubagent"), config.CodexSubagentDefault),
                new("codex.codexPlanModeReasoningEffort", Locale.Get("settings.phaseModels.knob.codexPlanEffort"), config.CodexPlanModeReasoningEffort, IsEffort: true),
            ]),
        ];

        if (tools.OmcAgents is { } agents)
            blocks.Add(new(OmcTool, ToolLabel(OmcTool),
                [.. agents.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new PhaseModelKnobRow(
                        "omc.agent." + pair.Key, OmcAgentLabel(pair.Key, tools.OmcDefaults), pair.Value))]));

        if (tools.OuroborosKeys is { } keys)
            blocks.Add(new(OuroborosTool, ToolLabel(OuroborosTool),
                [.. keys.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new PhaseModelKnobRow("ouroboros." + pair.Key, pair.Key, pair.Value))]));

        return blocks;
    }

    // MARK: - 쓰기

    /// 페이즈 줄 하나를 네 도구에 모두 적용한다. 설치되지 않은 도구는 건드리지 않는다.
    public static PhaseModelEdit ApplyPhaseRow(
        Phase phase, string value, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        var updated = PhaseModelRouting.ApplyCodexRow(
            phase, value, PhaseModelRouting.ApplyClaudeRow(phase, value, config));
        // 설치본에 없는 에이전트·파일에 없는 키는 새로 만들지 않는다.
        return new(
            updated,
            tools.OmcAgents is null ? null : KeepKnown(PhaseModelRouting.ApplyOmcRow(phase, value, tools.OmcAgents), tools.OmcAgents),
            tools.OuroborosKeys is null ? null : KeepKnown(PhaseModelRouting.ApplyOuroborosRow(phase, value, tools.OuroborosKeys), tools.OuroborosKeys));
    }

    private static Dictionary<string, string> KeepKnown(Dictionary<string, string> updated, Dictionary<string, string> known) =>
        updated.Where(pair => known.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);

    /// 자세히 줄 하나를 바꾼다. knobId는 ToolBlocks가 준 그 값이다.
    public static PhaseModelEdit ApplyKnob(
        string knobId, string value, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        if (knobId.StartsWith("omc.agent.", StringComparison.Ordinal))
        {
            if (tools.OmcAgents is null) return new(config, null, tools.OuroborosKeys);
            var agents = new Dictionary<string, string>(tools.OmcAgents)
            {
                [knobId["omc.agent.".Length..]] = value,
            };
            return new(config, agents, tools.OuroborosKeys);
        }
        if (knobId.StartsWith("ouroboros.", StringComparison.Ordinal))
        {
            if (tools.OuroborosKeys is null) return new(config, tools.OmcAgents, null);
            var keys = new Dictionary<string, string>(tools.OuroborosKeys)
            {
                [knobId["ouroboros.".Length..]] = value,
            };
            return new(config, tools.OmcAgents, keys);
        }
        var updated = knobId switch
        {
            "claude.claudeMain" => config with { ClaudeMain = value },
            "claude.claudeOpusAlias" => config with { ClaudeOpusAlias = value },
            "claude.claudeSonnetAlias" => config with { ClaudeSonnetAlias = value },
            "claude.claudeHaikuAlias" => config with { ClaudeHaikuAlias = value },
            "claude.claudeSubagentDefault" => config with { ClaudeSubagentDefault = value },
            "codex.codexReviewModel" => config with { CodexReviewModel = value },
            "codex.codexSubagentDefault" => config with { CodexSubagentDefault = value },
            "codex.codexPlanModeReasoningEffort" => config with { CodexPlanModeReasoningEffort = value },
            _ => config,
        };
        return new(updated, tools.OmcAgents, tools.OuroborosKeys);
    }

    /// 이번 고침이 실제로 바꾼 바깥 도구 값만 파일에 쓰고, 두 파일을 다시 읽어
    /// 돌려준다. 쓸 수 없으면 Error에 보여 줄 수 있는 말을 담고 그 파일은 그대로 둔다.
    public static PhaseModelTools SaveTools(PhaseModelTools before, PhaseModelEdit edit, string? homeDirectory = null)
    {
        var store = new ModelSettingsFileStore(homeDirectory);
        string? error = null;
        if (Changed(before.OmcAgents, edit.OmcAgents) is { Count: > 0 } agents)
        {
            try { store.SaveOmcAgents(agents); }
            catch (Exception) { error = FileError(store.OmcConfigPath); }
        }
        if (Changed(before.OuroborosKeys, edit.OuroborosKeys) is { Count: > 0 } keys)
        {
            try { store.SaveOuroborosKeys(keys); }
            catch (Exception) { error ??= FileError(store.OuroborosConfigPath); }
        }
        var reloaded = LoadTools(homeDirectory);
        return reloaded with { Error = error ?? reloaded.Error };
    }

    /// after 가운데 before와 값이 다른 키만.
    public static Dictionary<string, string> Changed(
        Dictionary<string, string>? before, Dictionary<string, string>? after) =>
        after is null
            ? []
            : after.Where(pair => before is null || !before.TryGetValue(pair.Key, out var old) || old != pair.Value)
                   .ToDictionary(pair => pair.Key, pair => pair.Value);
}

/// 바깥 두 도구의 현재 값. null은 그 도구가 설치되지 않았다는 뜻이다.
public sealed record PhaseModelTools(
    Dictionary<string, string>? OmcAgents,
    Dictionary<string, string>? OuroborosKeys,
    string? Error = null,
    IReadOnlyDictionary<string, string>? OmcDefaults = null);

/// 한 번의 고침이 만든 새 값. 파일 주인은 그대로다.
public sealed record PhaseModelEdit(
    PhaseModelsSnapshot Config,
    Dictionary<string, string>? OmcAgents,
    Dictionary<string, string>? OuroborosKeys);

public sealed record PhaseModelSummaryRow(Phase Phase, string Label, string Value, bool Mixed);

public sealed record PhaseModelKnobRow(string KnobId, string Label, string Value, bool IsEffort = false);

public sealed record PhaseModelToolBlock(string Tool, string Label, IReadOnlyList<PhaseModelKnobRow> Knobs);
