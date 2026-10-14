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
            rows.Add(ProviderRow(id, provider, runtime.Mods?.MinimumVersion ?? "2.1.271"));
        }
        return rows;
    }

    /// <summary>
    /// The row's detail says what the Mac's does in the same case (M/AppStore+Components.swift:20-40): a missing CLI its
    /// runtime's words and the install command to run in a terminal (a CLI not yet looked for the Mac's fallback words), an
    /// old Claude the Mods version it needs, a ready CLI its own ready sentence.
    /// </summary>
    private static ComponentStatus ProviderRow(string id, ProviderRuntime? provider, string modsVersion)
    {
        var title = ProviderTitle(id);

        if (provider is null || !provider.Available)
        {
            var command = InstallCommand(id);
            var detail = (provider?.Detail ?? Locale.Get("provider.fallback.checkInstall")) + (command is null ? "" : Locale.Get("settings.components.installInTerminal", new Dictionary<string, string> { ["command"] = command }));
            return new ComponentStatus
            {
                Id = id,
                Title = title,
                State = "missing",
                Version = provider?.Version,
                Detail = detail,
                Actions = command is null ? [] : [new ComponentAction { Id = "copy-command", Title = Locale.Get("settings.components.copyInstallCommand") }],
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
                Detail = Locale.Get("settings.components.modsNeedsVersion", new Dictionary<string, string> { ["version"] = modsVersion }),
                Actions = [new ComponentAction { Id = "update", Title = Locale.Get("settings.components.updateCLI") }],
            };
        }

        return new ComponentStatus
        {
            Id = id,
            Title = title,
            State = "installed",
            Version = provider.Version,
            Detail = Locale.Get(id switch { "claude" => "settings.components.readyClaude", "codex" => "settings.components.readyCodex", _ => "settings.components.ready" }),
            Actions = [],
        };
    }

    /// What the section says once a row's install command is on the clipboard (M/AppStore+Components.swift:57-62).
    public static string CopiedMessage(string command) => Locale.Get("settings.components.copied", new Dictionary<string, string> { ["command"] = command });

    /// How often the open section looks again, and whether it has to: only while a row still needs the
    /// user, since an install lands outside the app (M/ComponentsSettingsView.swift:28-38).
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);
    public static bool NeedsRecheck(IEnumerable<ComponentStatus> rows) => rows.Any(row => row.State != "installed");

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
