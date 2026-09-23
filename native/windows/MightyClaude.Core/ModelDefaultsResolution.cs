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
        if (trimmed.Length == 0) return Locale.Get("settings.modelDefaults.error.empty");
        if (trimmed == "default") return Locale.Get("settings.modelDefaults.error.reserved");
        if (!Wire.Model(trimmed)) return Locale.Get("settings.modelDefaults.error.invalid");
        var pd = provider == "codex" ? config.Codex : config.Claude;
        if (pd.RegisteredModels.Any(m => m.Name == trimmed)) return Locale.Get("settings.modelDefaults.error.duplicate");
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

    /// Builds a StartRunRequest for a pane run: resolves the effective model and
    /// attaches the provider's registered models from workspace and app config.
    public static StartRunRequest BuildPaneRequest(
        RunSession pane,
        Workspace workspace,
        ModelDefaultsConfig? appDefaults,
        string input,
        IReadOnlyList<RunAttachment>? attachments = null)
    {
        var resolvedModel = Resolve(
            pane.Model, pane.Provider, pane.Settings.PermissionMode,
            workspace.ModelDefaults, appDefaults);
        var registeredModels = GetProviderRegisteredModels(
            pane.Provider, workspace.ModelDefaults, appDefaults);
        return new StartRunRequest(
            pane.Id, pane.WorkspaceId, pane.Kind, input,
            resolvedModel, pane.Provider, pane.Settings, pane.ResumeId,
            attachments, registeredModels);
    }

    /// Returns the combined registered model entries for a provider from
    /// workspace and app config. Workspace entries come first; app entries
    /// with duplicate names are skipped. Returns null when both are empty.
    public static IReadOnlyList<RegisteredModelEntry>? GetProviderRegisteredModels(
        string provider,
        ModelDefaultsConfig? workspaceDefaults,
        ModelDefaultsConfig? appDefaults)
    {
        var list1 = ProviderRegisteredList(provider, workspaceDefaults);
        var list2 = ProviderRegisteredList(provider, appDefaults);
        if (list1.Count == 0 && list2.Count == 0) return null;
        if (list2.Count == 0) return list1;
        if (list1.Count == 0) return list2;
        var names = list1.Select(r => r.Name).ToHashSet();
        var combined = list1.ToList();
        foreach (var e in list2) if (!names.Contains(e.Name)) combined.Add(e);
        return combined.AsReadOnly();
    }

    private static IReadOnlyList<RegisteredModelEntry> ProviderRegisteredList(
        string provider, ModelDefaultsConfig? config)
    {
        if (config is null) return [];
        return provider == "codex" ? config.Codex.RegisteredModels : config.Claude.RegisteredModels;
    }

    private static string? ModeLookup(ModelDefaultsConfig? config, string provider, string mode)
    {
        if (config is null) return null;
        var defaults = provider == "codex" ? config.Codex.ModeDefaults : config.Claude.ModeDefaults;
        return defaults.TryGetValue(mode, out var name) ? name : null;
    }
}
