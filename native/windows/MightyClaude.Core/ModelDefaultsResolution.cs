namespace MightyClaude.Core;

/// Resolution rules for the effective model name — identical contract to the
/// macOS ModelDefaultsResolution.swift (docs/model-defaults.md).
public static class ModelDefaultsResolution
{
    /// Returns the effective model name for a run.
    ///
    /// Priority:
    /// 1. If sessionModel is not "default", return it directly.
    /// 2. Look up permissionMode in workspace-level defaults.
    /// 3. Look up permissionMode in app-level defaults.
    /// 4. Return "default" (CLI decides).
    public static string Resolve(
        string sessionModel,
        string provider,
        string permissionMode,
        ModelDefaultsConfig? workspaceDefaults,
        ModelDefaultsConfig? appDefaults)
    {
        if (sessionModel != "default") return sessionModel;
        foreach (var source in new[] { workspaceDefaults, appDefaults })
        {
            var name = ModeLookup(source, provider, permissionMode);
            if (name is not null && name != "default") return name;
        }
        return "default";
    }

    /// The model name shown in the permission-mode menu for a given mode.
    /// Equivalent to resolving with sessionModel = "default".
    public static string ModeMenuLabel(
        string provider,
        string permissionMode,
        ModelDefaultsConfig? workspaceDefaults,
        ModelDefaultsConfig? appDefaults)
        => Resolve("default", provider, permissionMode, workspaceDefaults, appDefaults);

    /// Returns one row per (provider, mode) pair reflecting current config.
    /// Choices are ["default"] plus any registered names for that provider.
    public static IReadOnlyList<ModelDefaultsRow> SectionRows(ModelDefaultsConfig? config)
    {
        var rows = new List<ModelDefaultsRow>();
        foreach (var provider in new[] { "claude", "codex" })
        {
            var pd = provider == "codex" ? config?.Codex : config?.Claude;
            var registered = (IReadOnlyList<RegisteredModelEntry>)(pd?.RegisteredModels ?? []);
            var choices = new List<string> { "default" };
            foreach (var e in registered) choices.Add(e.Name);
            foreach (var mode in ProviderCatalog.PermissionModes(provider))
            {
                var current = pd?.ModeDefaults.TryGetValue(mode, out var val) == true ? val! : "default";
                rows.Add(new(provider, mode, current, choices.AsReadOnly()));
            }
        }
        return rows;
    }

    /// Adds a registered model name to config for provider.
    /// Returns an error string when the name is invalid, already exists, or effort params are invalid; null on success.
    public static string? AddRegisteredModel(string name, string provider, ref ModelDefaultsConfig config,
        bool supportsEffort = false, string[]? supportedEffortLevels = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "모델 이름은 비어 있을 수 없습니다.";
        if (trimmed == "default") return "'default'는 예약된 이름이므로 사용할 수 없습니다.";
        if (!Wire.Model(trimmed)) return "모델 이름에 허용되지 않는 문자가 포함되어 있거나 길이가 초과되었습니다.";
        var pd = provider == "codex" ? config.Codex : config.Claude;
        if (pd.RegisteredModels.Any(m => m.Name == trimmed)) return "같은 제공자에 이미 등록된 이름입니다.";
        var levels = supportedEffortLevels ?? [];
        if (supportsEffort && levels.Length == 0)
            return Locale.Get("settings.modelDefaults.error.effortWithoutLevels");
        foreach (var level in levels)
            if (!Wire.Efforts.Contains(level))
                return Locale.Get("settings.modelDefaults.error.unknownLevel");
        var updated = pd with { RegisteredModels = [.. pd.RegisteredModels, new(trimmed, supportsEffort, levels.Length > 0 ? levels : null)] };
        config = provider == "codex" ? config with { Codex = updated } : config with { Claude = updated };
        return null;
    }

    /// Removes a registered model name from config for provider, reverting any mode rows
    /// that referenced that name to "default". Returns the number of mode rows reverted.
    public static int RemoveRegisteredModel(string name, string provider, ref ModelDefaultsConfig config)
    {
        var pd = provider == "codex" ? config.Codex : config.Claude;
        var newRegistered = pd.RegisteredModels.Where(m => m.Name != name).ToList();
        var newModeDefaults = new Dictionary<string, string>(pd.ModeDefaults);
        var reverted = 0;
        foreach (var mode in newModeDefaults.Keys.ToList())
        {
            if (newModeDefaults[mode] == name) { newModeDefaults[mode] = "default"; reverted++; }
        }
        var updated = pd with { RegisteredModels = newRegistered, ModeDefaults = newModeDefaults };
        config = provider == "codex" ? config with { Codex = updated } : config with { Claude = updated };
        return reverted;
    }

    private static string? ModeLookup(ModelDefaultsConfig? config, string provider, string mode)
    {
        if (config is null) return null;
        var defaults = provider == "codex" ? config.Codex.ModeDefaults : config.Claude.ModeDefaults;
        return defaults.TryGetValue(mode, out var name) ? name : null;
    }
}
