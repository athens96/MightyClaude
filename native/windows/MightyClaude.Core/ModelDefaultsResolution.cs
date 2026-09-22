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

    private static string? ModeLookup(ModelDefaultsConfig? config, string provider, string mode)
    {
        if (config is null) return null;
        var defaults = provider == "codex" ? config.Codex.ModeDefaults : config.Claude.ModeDefaults;
        return defaults.TryGetValue(mode, out var name) ? name : null;
    }
}
