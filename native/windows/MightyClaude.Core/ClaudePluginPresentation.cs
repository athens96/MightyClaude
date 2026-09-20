using System.Text.Json.Serialization;

namespace MightyClaude.Core;

// Everything the plugin window shows, decided here and proven on the Mac.
// WinUI draws these rows and forwards the tab, the search text, the marketplace
// filter and the reload click. It contains no control that would change a
// plugin or a marketplace, because this feature only reads.

/// One drawn row, installed or available. Subtitle is already composed, so the
/// screen never joins Korean of its own.
public sealed record ClaudePluginRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? Version { get; init; }
    /// 활성 / 비활성 / 상태 미확인 on an installed row, null on a catalog row.
    public string? State { get; init; }
    public string? ProjectPath { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class ClaudePluginBrowser(string provider, Workspace workspace)
{
    public const string InstalledTab = "installed";
    public const string MarketplaceTab = "marketplace";

    public string Provider { get; } = provider;
    public Workspace Workspace { get; } = workspace;
    public string Tab { get; set; } = InstalledTab;
    public string Search { get; set; } = "";
    public string MarketplaceFilter { get; set; } = "";
    public ClaudePluginSnapshot? Snapshot { get; private set; }
    public bool Loading { get; private set; }

    public bool IsRemote => Workspace.Remote is not null;
    public string Title => PluginStrings.TitleTemplate.Replace("{provider}", CliUpdateService.ProviderLabel(Provider));
    public bool IsReady => Snapshot?.Status == ClaudePluginStatus.Ready;

    public void BeginLoad() => Loading = true;

    /// The one place a finished read reaches the screen state. A filter that the
    /// new list no longer offers falls back to 전체, as on macOS.
    public void Apply(ClaudePluginSnapshot snapshot)
    {
        Snapshot = snapshot;
        Loading = false;
        if (MarketplaceFilter.Length > 0 && !Marketplaces.Contains(MarketplaceFilter)) MarketplaceFilter = "";
    }

    public int InstalledCount => Snapshot?.Installed.Count ?? 0;
    public int AvailableCount => Snapshot?.Available.Count ?? 0;

    public string TabLabel(string tab) => PluginStrings.TabCountTemplate
        .Replace("{title}", tab == MarketplaceTab ? PluginStrings.TabMarketplace : PluginStrings.TabInstalled)
        .Replace("{count}", (tab == MarketplaceTab ? AvailableCount : InstalledCount).ToString());

    /// Every marketplace name the list mentions, registered or referenced.
    public IReadOnlyList<string> Marketplaces =>
    [
        .. (Snapshot is null ? Enumerable.Empty<string>()
            : Snapshot.Marketplaces.Select(m => m.Name)
                .Concat(Snapshot.Available.Select(p => p.Marketplace))
                .Concat(Snapshot.Installed.Select(p => p.Marketplace).Where(n => n is { Length: > 0 }).Select(n => n!)))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
    ];

    private bool Matches(string name, string description, string? marketplace)
    {
        var query = Search.Trim();
        if (MarketplaceFilter.Length > 0 && MarketplaceFilter != marketplace) return false;
        return query.Length == 0
            || name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static string ScopeLabel(string scope) => scope switch
    {
        "local" => PluginStrings.ScopeLocal,
        "project" => PluginStrings.ScopeProject,
        "user" => PluginStrings.ScopeUser,
        "managed" => PluginStrings.ScopeManaged,
        _ => scope,
    };

    private static string Subtitle(string left, string right) =>
        PluginStrings.SubtitleTemplate.Replace("{left}", left).Replace("{right}", right);

    /// The rows of the selected tab, filtered and ordered as macOS orders them.
    public IReadOnlyList<ClaudePluginRow> Rows() => Tab == MarketplaceTab ? AvailableRows() : InstalledRows();

    public IReadOnlyList<ClaudePluginRow> InstalledRows() =>
    [
        .. (Snapshot?.Installed ?? [])
            .Where(p => Matches(p.Name, p.Description, p.Marketplace))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Scope, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new ClaudePluginRow
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Subtitle = Subtitle(p.Marketplace is { Length: > 0 } m ? m : PluginStrings.DirectInstall, ScopeLabel(p.Scope)),
                Version = p.Version,
                State = p.Enabled is null ? PluginStrings.StateUnknown : p.Enabled.Value ? PluginStrings.StateEnabled : PluginStrings.StateDisabled,
                ProjectPath = p.ProjectPath,
                Errors = p.Errors,
                Notes = p.Notes,
            })
    ];

    public IReadOnlyList<ClaudePluginRow> AvailableRows() =>
    [
        .. (Snapshot?.Available ?? [])
            .Where(p => Matches(p.Name, p.Description, p.Marketplace))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Marketplace, StringComparer.Ordinal)
            .Select(p => new ClaudePluginRow
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description.Length > 0 ? p.Description : PluginStrings.NoDescription,
                Subtitle = Subtitle(p.Marketplace, p.SourceKind),
                Version = p.Version,
            })
    ];

    /// What stands in for an empty list (ClaudePluginView.emptyMessage).
    public string EmptyMessage
    {
        get
        {
            if (Loading) return PluginStrings.EmptyLoading;
            if (!IsReady) return PluginStrings.EmptyFailed;
            if (Search.Trim().Length > 0 || MarketplaceFilter.Length > 0) return PluginStrings.EmptyFiltered;
            return Tab == MarketplaceTab ? PluginStrings.EmptyAvailable : PluginStrings.EmptyInstalled;
        }
    }

    /// The sentence under the filters: the snapshot detail whenever the read did
    /// not end ready, and nothing while the list is good.
    public string? StatusText => Snapshot is { } snapshot && snapshot.Status != ClaudePluginStatus.Ready ? snapshot.Detail : null;

    /// Shown only when the marketplace tab is empty because nothing is registered.
    public bool ShowsMarketplaceHelp =>
        IsReady && Tab == MarketplaceTab && Snapshot!.Marketplaces.Count == 0;
}

/// What the GUI smoke run records under the key "claudePluginList" after it has
/// driven the real plugin window with a fixture snapshot. No CLI is started.
public sealed record ClaudePluginSmokeOutcome
{
    public const string ResultKey = "claudePluginList";

    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("installedTab")] public string InstalledTab { get; init; } = "";
    [JsonPropertyName("marketplaceTab")] public string MarketplaceTab { get; init; } = "";
    [JsonPropertyName("installedRows")] public int InstalledRows { get; init; }
    [JsonPropertyName("availableRows")] public int AvailableRows { get; init; }
    [JsonPropertyName("filteredRows")] public int FilteredRows { get; init; }
    [JsonPropertyName("searchedRows")] public int SearchedRows { get; init; }
    [JsonPropertyName("installedSubtitle")] public string InstalledSubtitle { get; init; } = "";
    [JsonPropertyName("availableSubtitle")] public string AvailableSubtitle { get; init; } = "";
    [JsonPropertyName("reloadedFromStatus")] public string ReloadedFromStatus { get; init; } = "";
    [JsonPropertyName("remoteSentences")] public IReadOnlyList<string> RemoteSentences { get; init; } = [];
    [JsonPropertyName("reads")] public int Reads { get; init; }
    [JsonPropertyName("mutatingControls")] public int MutatingControls { get; init; }
    [JsonPropertyName("restored")] public bool Restored { get; init; }
}
