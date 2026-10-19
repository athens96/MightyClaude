namespace MightyClaude.Core;

/// <summary>
/// What a remote workspace's panes may and may not do (M/AppStore+RemoteWorkspaces.swift, docs/remote-workspaces.md): delegation,
/// the files pane, the browser pane, styles that read files, the local terminal, agent I/O tools, the status line and the git
/// badge all read this PC's disk, network or processes, so a remote workspace has none of them.
/// </summary>
public static class RemoteWorkspaceFeatures
{
    /// <summary>The add-pane menu: agents and a remote command for a remote workspace, never a browser or files pane.</summary>
    public static IReadOnlyList<string> AddPaneEntries(Workspace? workspace) => workspace?.Remote is null ? AddPaneMenu.Entries()
        : [.. Wire.Providers.Select(p => AddPaneMenu.AgentPrefix + p), AddPaneMenu.Separator, AddPaneMenu.Shell, AddPaneMenu.Separator, AddPaneMenu.OpenProject];

    /// <summary>
    /// The workspace's folder on this PC, or null for a remote workspace, whose path is its host's (a UNC path among them): the one way
    /// the window reads a workspace's folder, so nothing local ever opens, reads, runs in or authenticates to a host's path.
    /// </summary>
    public static string? LocalPath(Workspace? workspace) => workspace?.Remote is null ? workspace?.Path : null;

    /// <summary>Delegation, files, browser, file-reading styles, agent I/O, the status line and the git badge.</summary>
    public static bool AllowsLocalOnly(Workspace? workspace) => workspace?.Remote is null;

    /// <summary>A command pane is this PC's ConPTY terminal only in a local workspace; a remote one sends each command to the host.</summary>
    public static bool UsesLocalTerminal(RunSession pane, Workspace? workspace) => pane.Kind == "shell" && workspace?.Remote is null;

    /// <summary>The host's CLI as its last answer described it; before this launch heard from the host, a stand-in that blocks nothing.</summary>
    public static ProviderRuntime ProviderRuntime(string provider, RemoteWorkspaceReference reference, RemoteConnectionInfo? connection)
    {
        if (connection?.Runtime?.Providers?.FirstOrDefault(p => p.Id == provider) is { } known) return known;
        return new(provider, ProviderCatalog.Name(provider), true, null, Locale.Get("remote.workspace.runtimeUnknown", new Dictionary<string, string> { ["host"] = reference.HostName }),
            ProviderCatalog.Fallback(provider), ProviderCatalog.Capabilities(provider) with { PermissionModes = ProviderCatalog.PermissionModes(provider) });
    }

    /// <summary>Why a remote pane cannot send now: its connection was removed, or the host said its CLI is missing or lacks the mode.</summary>
    public static string? BlockedReason(RunSession pane, RemoteWorkspaceReference reference, RemoteConnectionInfo? connection)
    {
        if (connection is null) return Locale.Get("remote.workspace.connectionGone", new Dictionary<string, string> { ["host"] = reference.HostName });
        if (pane.Kind == "shell" || connection.Status != RemoteConnectionStatus.Connected || connection.Runtime?.Providers?.FirstOrDefault(p => p.Id == pane.Provider) is not { } provider) return null;
        if (!provider.Available) return Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(pane.Provider) });
        if (!provider.Capabilities.PermissionModes.Contains(pane.Settings.PermissionMode)) return Locale.Get("remote.workspace.modeUnsupported", new Dictionary<string, string> { ["host"] = reference.HostName });
        return null;
    }

    /// <summary>The Windows Firewall rule's ports (WindowsFirewallRule): the phone's direct port and the sharing port, whichever are on.</summary>
    public static IReadOnlyList<int> FirewallPorts(int? phoneDirectPort, int? sharingPort) =>
        new[] { phoneDirectPort, sharingPort }.OfType<int>().Where(p => p is >= 1024 and <= 65535).Distinct().Order().ToList();
}

/// <summary>How the connection key is copied (M/RemoteSettingsView copyButton secret:): never in clipboard history or the cloud clipboard, cleared after a minute if still there.</summary>
public sealed record RemoteSecretCopy(bool AllowedInHistory, bool Roamable, TimeSpan ClearAfter)
{
    public static readonly RemoteSecretCopy Key = new(false, false, TimeSpan.FromSeconds(60));
    public static bool ShouldClear(string? clipboardText, string copied) => clipboardText is not null && clipboardText == copied;
}
