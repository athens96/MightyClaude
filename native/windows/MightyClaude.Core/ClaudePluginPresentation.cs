using System.Text.Json.Serialization;

namespace MightyClaude.Core;

// Everything the plugin window shows, decided here and proven on the Mac.
// WinUI draws these rows and forwards the tab, the search text, the marketplace
// filter, the scope choice, the reload click, the install click, the
// 마켓플레이스 새로고침 click and the cancel click. The only two changes it can
// ask for are the two the macOS app has: install one plugin and refresh one
// marketplace. There is no uninstall, enable, disable or marketplace add.

/// One entry of the scope picker (ClaudePluginView's 설치 범위 picker).
public sealed record PluginScopeOption(string Value, string Label);

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
    /// ClaudePluginView.providerLabel: the same header for both providers.
    public string Title => PluginStrings.TitleTemplate.Replace("{provider}", CliUpdateService.ProviderLabel(Provider));
    public bool IsReady => Snapshot?.Status == ClaudePluginStatus.Ready;

    public const string CodexProvider = "codex";
    private bool IsCodex => Provider == CodexProvider;

    /// Which installed scopes this provider's window lists. Claude shows every
    /// scope its CLI reports; Codex plugins are user level only
    /// (ClaudePluginView.supportedScopes is ["user"] for Codex), so a row
    /// claiming another scope is never drawn under the Codex title.
    public IReadOnlyList<string> SupportedScopes => IsCodex ? ["user"] : ["local", "project", "user", "managed", "session"];

    /// The sentence under the list (ClaudePluginView's footer text).
    public string FooterNote => IsCodex ? CodexPluginStrings.FooterNote : PluginStrings.FooterNote;

    // ---- The install and the marketplace refresh (AppStore+Plugins.swift) ----

    public const string PhaseIdle = "idle";
    public const string PhaseLoading = "loading";
    public const string PhaseInstalling = "installing";
    public const string PhaseRefreshing = "refreshing";

    /// The scope the install uses. Claude offers the three macOS scopes and
    /// starts on 로컬; Codex installs at user level and offers nothing else.
    public string Scope { get; set; } = provider == CodexProvider ? "user" : "local";
    public string Phase { get; private set; } = PhaseIdle;
    public string? ActivePluginId { get; private set; }
    public bool IsCancelling { get; private set; }
    /// The result line under the list. Never saved anywhere.
    public ClaudePluginOperationResult? LastResult { get; private set; }

    private CancellationTokenSource? operationCancel;

    /// Claude's three scopes; Codex's only one.
    public IReadOnlyList<string> InstallScopes => IsCodex ? ["user"] : ["local", "project", "user"];

    public IReadOnlyList<PluginScopeOption> ScopeOptions =>
    [
        .. InstallScopes.Select(scope => new PluginScopeOption(scope, scope switch
        {
            "local" => PluginStrings.ScopeLocalOption,
            "project" => PluginStrings.ScopeProjectOption,
            _ => PluginStrings.ScopeUserOption,
        }))
    ];

    /// The sentence under the picker explaining the selected scope.
    public string ScopeNote => Scope switch
    {
        "project" => PluginStrings.ScopeNoteProject,
        "user" => PluginStrings.ScopeNoteUser,
        _ => PluginStrings.ScopeNoteLocal,
    };

    public bool IsBusy => Phase != PhaseIdle || IsCancelling;
    public bool IsMutating => Phase is PhaseInstalling or PhaseRefreshing || IsCancelling;
    /// 닫기 is disabled while a change is running, as on macOS.
    public bool CanClose => !IsMutating;
    public bool CanCancel => IsMutating && !IsCancelling;

    public string? ProgressLabel => !IsBusy ? null
        : IsCancelling ? PluginStrings.ProgressCancelling
        : Phase switch
        {
            PhaseInstalling => PluginStrings.ProgressInstalling,
            PhaseRefreshing => PluginStrings.ProgressRefreshing,
            _ => PluginStrings.ProgressLoading,
        };

    public string CancelLabel => IsCancelling ? PluginStrings.ButtonCancelling : PluginStrings.ButtonCancelOperation;

    /// Why a change cannot be started right now, in the macOS words.
    public string? BlockedReason => IsRemote ? PluginStrings.RemoteNote : null;

    /// Refresh uses the registered sources; Codex can upgrade Git-backed ones only.
    public IReadOnlyList<string> RefreshableMarketplaces =>
    [
        .. (Snapshot?.Marketplaces ?? [])
            .Where(m => (!IsCodex || m.SourceKind == "git")
                && (MarketplaceFilter.Length == 0 || m.Name == MarketplaceFilter))
            .Select(m => m.Name)
    ];

    /// The Codex sentence saying only registered Git sources can be refreshed.
    public string? RefreshUnavailableNote =>
        IsCodex && RefreshableMarketplaces.Count == 0 ? CodexPluginStrings.RefreshGitOnly : null;

    public bool InstalledInSelectedScope(string pluginId) =>
        (Snapshot?.Installed ?? []).Any(p => p.PluginId == pluginId && p.Scope == Scope
            && (Scope == "user" || p.ProjectPath is null || ClaudePluginSupport.Applies(p.ProjectPath, Workspace.Path)));

    public string InstallButtonLabel(string pluginId) =>
        ActivePluginId == pluginId ? PluginStrings.ButtonInstalling
        : InstalledInSelectedScope(pluginId) ? PluginStrings.TabInstalled
        : PluginStrings.ButtonInstall;

    public bool CanInstall(string pluginId) =>
        !InstalledInSelectedScope(pluginId) && !IsBusy && BlockedReason is null && IsReady;

    public bool CanRefreshMarketplaces =>
        !IsBusy && BlockedReason is null && IsReady && RefreshableMarketplaces.Count > 0;

    /// The result line, and the bounded CLI output behind 명령 실행 상세.
    public string? ResultText => LastResult?.Detail;
    public string DiagnosticText
    {
        get
        {
            var snapshot = Snapshot is { } value && value.Status != ClaudePluginStatus.Ready ? value.DiagnosticOutput : "";
            var operation = LastResult?.Output ?? "";
            if (!IsCodex) return operation.Length > 0 ? operation : snapshot;
            return string.Join("\n\n", new[] { operation, snapshot }.Where(t => t.Length > 0));
        }
    }

    /// The window's cancel button. The running operation stops and the list is
    /// read again, because a change may already have reached the CLI.
    public void RequestCancel()
    {
        if (!IsMutating || IsCancelling) return;
        IsCancelling = true;
        operationCancel?.Cancel();
    }

    /// Install one plugin the catalog just offered, in the selected scope: the
    /// one method that ties the argument assembly, the run and the reading of
    /// the result together. Core.Tests drives it with a fake runner.
    public Task<ClaudePluginOperationResult> InstallAsync(IPluginReader gateway, string pluginId, CancellationToken cancellation = default)
    {
        if (Refuse() is { } refusal) return Task.FromResult(Remember(refusal));
        if (!InstallScopes.Contains(Scope) || !(Snapshot?.Available.Any(p => p.Id == pluginId) ?? false))
            return Task.FromResult(Remember(new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.SelectPluginAndScopeAgain)));
        if (InstalledInSelectedScope(pluginId))
            return Task.FromResult(Remember(new ClaudePluginOperationResult(ClaudePluginStatus.Skipped,
                IsCodex ? CodexPluginStrings.InstallSkipped : PluginStrings.InstallSkipped)));
        var scope = Scope;
        return RunAsync(PhaseInstalling, pluginId, gateway,
            token => gateway.InstallAsync(pluginId, scope, Workspace, token), cancellation);
    }

    /// Refresh the registered marketplaces the filter selects, one at a time,
    /// stopping at the first one that does not succeed.
    public Task<ClaudePluginOperationResult> RefreshMarketplacesAsync(IPluginReader gateway, CancellationToken cancellation = default)
    {
        if (Refuse() is { } refusal) return Task.FromResult(Remember(refusal));
        var names = RefreshableMarketplaces;
        if (names.Count == 0)
            return Task.FromResult(Remember(new ClaudePluginOperationResult(ClaudePluginStatus.Skipped,
                IsCodex ? CodexPluginStrings.NoGitMarketplaces : PluginStrings.NoRefreshableMarketplaces)));
        return RunAsync(PhaseRefreshing, null, gateway, async token =>
        {
            ClaudePluginOperationResult? failed = null;
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                var one = await gateway.RefreshMarketplaceAsync(name, Workspace, token);
                if (one.Status != ClaudePluginStatus.Succeeded) { failed = one; break; }
            }
            return failed ?? new ClaudePluginOperationResult(ClaudePluginStatus.Succeeded,
                PluginStrings.MarketplacesRefreshedTemplate.Replace("{count}", names.Count.ToString()));
        }, cancellation);
    }

    private ClaudePluginOperationResult? Refuse()
    {
        if (IsRemote) return new ClaudePluginOperationResult(ClaudePluginStatus.Remote, PluginStrings.RemoteNote);
        if (IsBusy) return new ClaudePluginOperationResult(ClaudePluginStatus.Busy, PluginStrings.OperationBusy);
        if (!IsReady) return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.LoadListFirst);
        return null;
    }

    private ClaudePluginOperationResult Remember(ClaudePluginOperationResult result)
    {
        LastResult = result;
        return result;
    }

    private async Task<ClaudePluginOperationResult> RunAsync(
        string phase, string? activePluginId, IPluginReader gateway,
        Func<CancellationToken, Task<ClaudePluginOperationResult>> body, CancellationToken cancellation)
    {
        Phase = phase;
        ActivePluginId = activePluginId;
        LastResult = null;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        operationCancel = source;
        ClaudePluginOperationResult result;
        try { result = await body(source.Token); }
        catch (OperationCanceledException) { result = new ClaudePluginOperationResult(ClaudePluginStatus.Cancelled, PluginStrings.OperationCancelled); }
        finally { operationCancel = null; }

        // A cancel the user asked for says so, and the list is read again
        // because a change may already have reached the CLI.
        if (IsCancelling && result.Status == ClaudePluginStatus.Cancelled)
            result = result with { Detail = PluginStrings.OperationCancelledByUser };
        LastResult = result;
        Phase = PhaseIdle;
        ActivePluginId = null;
        IsCancelling = false;
        try { Apply(await gateway.SnapshotAsync(Workspace, cancellation)); }
        catch (OperationCanceledException) { Loading = false; }
        return result;
    }

    public void BeginLoad()
    {
        Loading = true;
        if (Phase == PhaseIdle) Phase = PhaseLoading;
    }

    /// The one place a finished read reaches the screen state. A filter that the
    /// new list no longer offers falls back to 전체, as on macOS.
    public void Apply(ClaudePluginSnapshot snapshot)
    {
        Snapshot = snapshot;
        Loading = false;
        if (Phase == PhaseLoading) Phase = PhaseIdle;
        if (MarketplaceFilter.Length > 0 && !Marketplaces.Contains(MarketplaceFilter)) MarketplaceFilter = "";
    }

    /// The tab count is what the tab lists, so a row this provider does not
    /// show is not counted either. Claude lists every scope its CLI reports, so
    /// its count is unchanged; Codex never counts a non-user row.
    public int InstalledCount => Snapshot?.Installed.Count(p => SupportedScopes.Contains(p.Scope)) ?? 0;
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
            .Where(p => SupportedScopes.Contains(p.Scope))
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
            // macOS keeps this copy for both providers and puts the Claude link
            // or the Codex sentence underneath it, rather than replacing it.
            return Tab == MarketplaceTab ? PluginStrings.EmptyAvailable : PluginStrings.EmptyInstalled;
        }
    }

    /// The sentence under the filters: the snapshot detail whenever the read did
    /// not end ready, and nothing while the list is good.
    public string? StatusText => Snapshot is { } snapshot
        && (snapshot.Status != ClaudePluginStatus.Ready || (IsCodex && snapshot.Detail.Length > 0))
        ? snapshot.Detail : null;

    /// Shown when the marketplace tab is empty because nothing is registered.
    /// macOS puts the 마켓플레이스 추가 방법 link there for Claude and a sentence
    /// telling the user to register one in the CLI for Codex.
    public bool ShowsMarketplaceHelp =>
        IsReady && Tab == MarketplaceTab && Snapshot!.Marketplaces.Count == 0;

    public bool ShowsMarketplaceHelpLink => ShowsMarketplaceHelp && !IsCodex;
    public string? MarketplaceHelpText => ShowsMarketplaceHelp && IsCodex ? CodexPluginStrings.MarketplaceHelp : null;
}

/// What the GUI smoke run records under the key "pluginMarketplace" after it has
/// driven the real plugin window with a fake reader through the install and
/// marketplace refresh controls. No real claude or codex process starts.
public sealed record PluginMarketplaceSmokeOutcome
{
    public const string ResultKey = "pluginMarketplace";

    /// Number of scope options the Claude picker offered (3: local/project/user).
    [JsonPropertyName("claudeScopeOptions")] public int ClaudeScopeOptions { get; init; }
    /// Number of scope options the Codex picker offered (1: user only).
    [JsonPropertyName("codexScopeOptions")] public int CodexScopeOptions { get; init; }
    /// The result sentence after a successful install.
    [JsonPropertyName("installResult")] public string InstallResult { get; init; } = "";
    /// The result sentence after a cancelled operation.
    [JsonPropertyName("cancelResult")] public string CancelResult { get; init; } = "";
    /// The result sentence after a successful marketplace refresh.
    [JsonPropertyName("refreshResult")] public string RefreshResult { get; init; } = "";
    [JsonPropertyName("restored")] public bool Restored { get; init; }
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

/// What the GUI smoke run records under the key "codexPluginList" after it has
/// driven that same plugin window with a Codex fixture snapshot. No CLI starts.
public sealed record CodexPluginSmokeOutcome
{
    public const string ResultKey = "codexPluginList";

    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("installedTab")] public string InstalledTab { get; init; } = "";
    [JsonPropertyName("marketplaceTab")] public string MarketplaceTab { get; init; } = "";
    [JsonPropertyName("installedRows")] public int InstalledRows { get; init; }
    [JsonPropertyName("availableRows")] public int AvailableRows { get; init; }
    [JsonPropertyName("filteredRows")] public int FilteredRows { get; init; }
    [JsonPropertyName("searchedRows")] public int SearchedRows { get; init; }
    [JsonPropertyName("installedSubtitle")] public string InstalledSubtitle { get; init; } = "";
    [JsonPropertyName("footerNote")] public string FooterNote { get; init; } = "";
    [JsonPropertyName("readyStatus")] public string ReadyStatus { get; init; } = "";
    [JsonPropertyName("noMarketplaceHelp")] public string NoMarketplaceHelp { get; init; } = "";
    [JsonPropertyName("unsupportedStatus")] public string UnsupportedStatus { get; init; } = "";
    [JsonPropertyName("reads")] public int Reads { get; init; }
    [JsonPropertyName("mutatingControls")] public int MutatingControls { get; init; }
    [JsonPropertyName("restored")] public bool Restored { get; init; }
}
