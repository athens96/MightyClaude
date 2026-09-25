namespace MightyClaude.Core;

/// One component row shown in the Settings components section.
public sealed class ComponentStatus
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// installed · missing · attention · checking · unsupported
    public required string State { get; init; }
    public string? Version { get; init; }
    public required string Detail { get; init; }
    public IReadOnlyList<ComponentAction> Actions { get; init; } = [];
}

public sealed class ComponentAction
{
    /// install · copy-command · update
    public required string Id { get; init; }
    public required string Title { get; init; }
}

/// Windows ComponentSection — CLI rows for claude, codex and gemini from the runtime.
public static class ComponentSection
{
    public static string SectionTitle => Locale.Get("settings.components.sectionTitle");

    /// One row per provider id (claude, codex, gemini) from runtime.Providers.
    /// Logic mirrors AppStore+Components.swift providerRow(_:), using the runtime
    /// that the Windows ProviderCatalog already built.
    public static IReadOnlyList<ComponentStatus> SectionRows(RuntimeInfo runtime)
    {
        var rows = new List<ComponentStatus>();
        foreach (var id in Wire.Providers)
        {
            var provider = runtime.Providers.FirstOrDefault(p => p.Id == id);
            rows.Add(ProviderRow(id, provider));
        }
        return rows;
    }

    private static ComponentStatus ProviderRow(string id, ProviderRuntime? provider)
    {
        var title = ProviderTitle(id);

        if (provider is null || !provider.Available)
        {
            var detail = provider?.Detail ?? Locale.Get("provider.notInstalled");
            var command = InstallCommand(id);
            return new ComponentStatus
            {
                Id = id,
                Title = title,
                State = "missing",
                Version = provider?.Version,
                Detail = detail,
                Actions = command is null ? [] : [new ComponentAction { Id = "copy-command", Title = Locale.Get("settings.components.statusMissing") }],
            };
        }

        // Claude below the Mods minimum → attention + update action.
        if (id == "claude" && !ProviderCatalog.SupportsMods(provider.Version))
        {
            return new ComponentStatus
            {
                Id = id,
                Title = title,
                State = "attention",
                Version = provider.Version,
                Detail = Locale.Get("provider.unsupportedVersion"),
                Actions = [new ComponentAction { Id = "update", Title = Locale.Get("settings.components.statusAttention") }],
            };
        }

        return new ComponentStatus
        {
            Id = id,
            Title = title,
            State = "installed",
            Version = provider.Version,
            Detail = Locale.Get("provider.available"),
            Actions = [],
        };
    }

    /// npm install commands, matching ComponentCatalog.installCommand() on macOS.
    public static string? InstallCommand(string provider) => provider switch
    {
        "claude" => "npm install -g @anthropic-ai/claude-code",
        "codex" => "npm install -g @openai/codex",
        "gemini" => "npm install -g @google/gemini-cli",
        _ => null,
    };

    private static string ProviderTitle(string id) => id switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "gemini" => "Gemini CLI",
        _ => id,
    };
}
