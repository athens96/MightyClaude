using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace MightyClaude.WinUI;

/// <summary>
/// Settings → Remote Connection (Tailscale) on Windows (M/RemoteSettingsView.swift, M/AppStore+RemoteWorkspaces.swift), and the
/// sharing host and the client behind it (docs/remote-workspaces.md).
/// </summary>
public sealed partial class MainWindow
{
    private RemoteHostService? remoteHost;
    /// <summary>How many of another computer's runs go on this PC now; while any does, this PC's CLIs and plugins are not updated.</summary>
    private int remoteHostRuns;
    private bool remoteBusy;
    private string? remoteError;
    /// <summary>Whether the page last showed sharing on: a sharing that ended without Stop (a listener that died) is said on the page.</summary>
    private bool remoteSharingShown;
    private readonly List<Action> remoteViews = [];
    /// <summary>The label column of the connect fields (the Mac's 90).</summary>
    private const double RemoteFieldLabelWidth = 90;

    /// <summary>
    /// Builds the client and the host once the service is up. Every callback of theirs hands its work to the window (TryEnqueue) and never
    /// waits on it. Only a smoke run passes loopback in place of Tailscale.
    /// </summary>
    private void InitializeRemoteWorkspaces(bool loopback = false)
    {
        if (remoteHost is not null || closing) return;
        var directory = StateDirectory;
        IRemoteKeyVault keys = loopback ? new MemoryRemoteKeyVault() : new DpapiRemoteKeyVault(directory);
        var client = service.AttachRemote(directory, keys, new RemoteClientOptions { AllowLoopbackForTests = loopback });
        // A connection that comes up or goes down redraws everything that names it: the sidebar, the header's badge, the status bar and
        // the remote panes (their CLIs and notes are the host's last answer).
        client.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!closing) { RenderSidebar(); RefreshWorkspaceHeader(); RefreshStatusBar(); RefreshRemotePanes(); RefreshRemoteViews(); } });
        string machine;
        try { machine = Environment.MachineName; } catch (InvalidOperationException) { machine = ""; }
        var hostName = string.IsNullOrWhiteSpace(machine) ? RemoteWindowsStrings.HostOS("win32") : machine;
        remoteHost = new RemoteHostService(service.HostOptions(hostName, AppVersion.Normalized(AppVersionText) ?? "0.1.0", RemoteHostAdmission,
            count => DispatcherQueue.TryEnqueue(() => { remoteHostRuns = count; if (!closing) { RefreshRemoteViews(); NotifyAutomaticUpdates(); } }), loopback));
    }

    /// <summary>
    /// Sharing ends with the app: every remote run here stops and the key is gone (the client's runs end with the service). Whatever that
    /// throws never keeps the service from shutting down after it (as DesktopService.DisposeAsync treats the client).
    /// </summary>
    private async Task ShutdownRemoteWorkspacesAsync()
    {
        if (remoteHost is not { } host) return;
        try { await host.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("remote host shutdown: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Why this PC cannot take another computer's run now (M/AppStore+RemoteWorkspaces.remoteHostAdmission): its CLIs or plugins are
    /// changing, or Claude's models are being reloaded; null when it can. Asked on a request's thread, so it only reads.
    /// </summary>
    private string? RemoteHostAdmission(StartRunRequest request)
    {
        if (closing) return Locale.Get("common.appClosing");
        if (request.Kind == "shell") return null;
        if (AnyCliUpdateRunning || manuallyUpdatingProvider is not null) return Locale.Get("run.blocked.updatingCLI", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(request.Provider) });
        if (pluginOperations.IsRunning && request.Provider is "claude" or "codex") return Locale.Get("run.blocked.changingPlugins");
        if (claudeModelReset && request.Provider == "claude") return Locale.Get("composer.model.reloadingClaude");
        return null;
    }

    private void RefreshRemoteViews() { foreach (var refresh in remoteViews.ToList()) refresh(); }

    /// <summary>The connection a remote workspace runs through, as the client knows it now; null for a local workspace or a removed connection.</summary>
    internal RemoteConnectionInfo? RemoteConnection(Workspace? workspace) => workspace?.Remote is { } remote ? service.Remote?.Connection(remote.ConnectionId) : null;

    /// <summary>"host · OS" for a remote workspace (the OS from its connection's last answer), null for a local one.</summary>
    internal string? RemoteHostLabel(Workspace? workspace) => workspace?.Remote is { } remote ? RemoteWindowsStrings.HostWithOS(remote.HostName, RemoteConnection(workspace)?.Runtime?.Platform) : null;

    internal bool RemoteConnected(Workspace workspace) => RemoteConnection(workspace)?.Status == RemoteConnectionStatus.Connected;

    /// <summary>
    /// Opens Settings on its Remote Connection page (M openRemoteSettings): the sidebar header's button and the add-pane menus' Connect to Another Computer…. A
    /// Settings window already open comes forward on the page it shows.
    /// </summary>
    internal Task OpenRemoteSettings() => Act(async () =>
    {
        if (dialogOpen) return;
        await service.UpdateAsync(s => s with { SettingsPane = "remote" });
        await ShowCategorizedSettingsAsync();
    });

    /// <summary>The remote panes drawn again, after their connection changed.</summary>
    private void RefreshRemotePanes()
    {
        foreach (var (paneId, view) in views.ToList()) if (service.IsRemoteSession(paneId)) view.Refresh();
    }

    /// <summary>The Mac's capsule fill: its ink at 0.09 (M/WorkspaceView.swift RemoteHostBadge).</summary>
    private const double RemoteHostBadgeTint = 0.09;

    /// <summary>
    /// A remote workspace's host beside its name in the workspace header (M/WorkspaceView.swift RemoteHostBadge): the network symbol and
    /// the host's name in 10pt medium, accent while connected and ink2 otherwise, on a capsule of that ink at 0.09; its tooltip says
    /// whether it is connected.
    /// </summary>
    private Border RemoteHostBadge(Workspace workspace)
    {
        var connected = RemoteConnected(workspace); var host = workspace.Remote?.HostName ?? "";
        var ink = connected ? DesignToken.Accent : DesignToken.Ink2;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new FontIcon { Glyph = RemoteNetworkGlyph, TextSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(ink), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = host, TextSize = DesignMetrics.Type.Small, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(ink), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border
        {
            Child = line, Background = brushes.Brush(ink, RemoteHostBadgeTint), CornerRadius = new CornerRadius(DesignMetrics.Radius.Composer),
            Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs), VerticalAlignment = VerticalAlignment.Center,
        };
        var status = Locale.Get(connected ? "remote.status.connected" : "remote.status.notConnected", new Dictionary<string, string> { ["host"] = host });
        AutomationProperties.SetAutomationId(badge, "workspace-remote-host"); AutomationProperties.SetName(badge, status);
        ToolTipService.SetToolTip(badge, status);
        return badge;
    }

    /// <summary>One remote action at a time (M remoteAction): its error shows on the page, and the page reads both sides again after it.</summary>
    private async Task RemoteAction(Func<Task> operation)
    {
        if (remoteBusy) return;
        remoteBusy = true; remoteError = null; RefreshRemoteViews();
        try { await operation(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { remoteError = ex.Message; }
        finally { remoteBusy = false; if (!closing) RefreshRemoteViews(); }
    }

    /// <summary>The clipboard format third-party clipboard managers and monitors skip (the Mac's org.nspasteboard.ConcealedType).</summary>
    private const string ClipboardMonitorExclusionFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>
    /// The key copy: never in clipboard history or the cloud clipboard, marked for clipboard managers to skip, and cleared after a minute
    /// if the clipboard still holds it (M copyButton secret:). <paramref name="copied"/> is told whether the key reached the clipboard.
    /// The smoke run never calls it: it must not touch the real clipboard.
    /// </summary>
    private async Task CopyRemoteKeyAsync(string key, Action<bool> copied)
    {
        try
        {
            var copyOptions = new ClipboardContentOptions { IsAllowedInHistory = RemoteSecretCopy.Key.AllowedInHistory, IsRoamable = RemoteSecretCopy.Key.Roamable };
            var package = new DataPackage(); package.SetText(key);
            bool set;
            try
            {
                // The marker's presence is what counts; its content is an empty stream.
                package.SetData(ClipboardMonitorExclusionFormat, new Windows.Storage.Streams.InMemoryRandomAccessStream());
                set = Clipboard.SetContentWithOptions(package, copyOptions);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidCastException)
            {
                // Without the marker rather than not at all: history and the cloud clipboard stay off either way.
                var plain = new DataPackage(); plain.SetText(key);
                set = Clipboard.SetContentWithOptions(plain, copyOptions);
            }
            copied(set);
            if (!set) return;
            await Task.Delay(RemoteSecretCopy.Key.ClearAfter);
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) && RemoteSecretCopy.ShouldClear(await content.GetTextAsync(), key)) Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { copied(false); }
    }

    /// <summary>
    /// The page (M/RemoteSettingsView.swift): the intro; sharing this PC (its addresses, the key, the full-access switch, what is shared,
    /// or the workspaces to pick and the port); the Windows Firewall rule; connecting to another computer; the connected computers with
    /// their shared workspaces; and what remote workspaces can and cannot do. While it is open it reads both sides and this PC's
    /// Tailscale addresses every three seconds, as the Mac's does. Typed fields are built once and never moved, so a refresh never
    /// takes what is being typed; only the lists are built again, and only when what they show changed.
    /// </summary>
    private StackPanel BuildRemoteWorkspacesSection()
    {
        var rows = new StackPanel();
        SettingsRow(rows, SettingsText(Locale.Get("settings.remote.intro"), 11, DesignToken.Ink2));
        var selected = new HashSet<string>(StringComparer.Ordinal); var picked = false; var showsKey = false; var reading = false;
        IReadOnlyList<string> tailscale = [];
        string? currentKey = null; string shownAddresses = "\0", shownShared = "\0", shownPicks = "\0", shownConnections = "\0";
        // The controls built before Refresh reach it through this (set once Refresh exists).
        Action redraw = () => { };

        // Share this computer: the heading and its status line.
        var share = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        share.Children.Add(SettingsText(Locale.Get("settings.remote.shareTitle"), 13, medium: true));
        var statusLine = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        statusLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); statusLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); statusLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var shareDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var shareStatus = SettingsText("", 11, DesignToken.Ink2); AutomationProperties.SetAutomationId(shareStatus, "settings-remote-share-status");
        var activeRuns = SettingsText("", 11, DesignToken.WaitText); AutomationProperties.SetAutomationId(activeRuns, "settings-remote-active-runs");
        Grid.SetColumn(shareStatus, 1); Grid.SetColumn(activeRuns, 2);
        statusLine.Children.Add(shareDot); statusLine.Children.Add(shareStatus); statusLine.Children.Add(activeRuns);
        share.Children.Add(statusLine);

        // While sharing: the addresses, the key, the full-access switch, what is shared, and the stop button.
        var sharing = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        var addresses = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        sharing.Children.Add(addresses);
        var keyText = SettingsText("", 11, mono: true); keyText.TextWrapping = TextWrapping.NoWrap; keyText.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(keyText, Locale.Get("settings.remote.keyLabel")); AutomationProperties.SetAutomationId(keyText, "settings-remote-key-text");
        var showKey = SettingsPush(Button(Locale.Get("settings.remote.showKeyButton"), () => { showsKey = !showsKey; redraw(); return Task.CompletedTask; }), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(showKey, "settings-remote-show-key");
        // What the last copy did: copied (out of history, cleared in a minute) or not; gone when the key or the page goes.
        var copyNote = SettingsText("", 11, DesignToken.Ink2); copyNote.Visibility = Visibility.Collapsed; AutomationProperties.SetAutomationId(copyNote, "settings-remote-copy-key-note");
        void SayCopied(bool done)
        {
            if (closing) return;
            copyNote.Text = done ? RemoteWindowsStrings.KeyCopied : RemoteWindowsStrings.KeyCopyFailed;
            copyNote.Foreground = brushes.Brush(done ? DesignToken.Ink2 : DesignToken.ErrText); copyNote.Visibility = Visibility.Visible;
        }
        var copyKey = SettingsPush(Button(Locale.Get("settings.remote.copyButton"), () => { if (!options.SmokeTest && currentKey is { } value) _ = CopyRemoteKeyAsync(value, SayCopied); return Task.CompletedTask; }), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(copyKey, "settings-remote-copy-key"); ToolTipService.SetToolTip(copyKey, Locale.Get("settings.remote.copyButton"));
        var keyLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        keyLine.Children.Add(keyText); keyLine.Children.Add(showKey); keyLine.Children.Add(copyKey);
        var keyRow = SettingsLabeled(SettingsText(Locale.Get("settings.remote.keyLabel"), 12), keyLine);
        sharing.Children.Add(keyRow);
        sharing.Children.Add(copyNote);
        // "Allow full access and commands from remote": for the sharing now going, off whenever sharing starts (RemoteHostService.StartAsync).
        var fullAccess = SettingsSwitch(Locale.Get("settings.remote.allowFullAccess"), false, "settings-remote-full-access");
        fullAccess.Click += (_, _) =>
        {
            if (remoteHost is { } host && host.Current().Enabled) host.SetAllowsFullAccess(fullAccess.IsChecked == true);
            redraw();
        };
        sharing.Children.Add(SettingsLabeled(SettingsTitled(Locale.Get("settings.remote.allowFullAccess"), RemoteWindowsStrings.SettingsAllowFullAccessDescription), fullAccess, top: true));
        sharing.Children.Add(SettingsText(Locale.Get("settings.remote.sharedListTitle"), 11, DesignToken.Ink2, medium: true));
        var sharedList = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        sharing.Children.Add(sharedList);
        var stopSharing = SettingsPush(SafeButton(Locale.Get("settings.remote.stopSharing"), () => RemoteAction(async () =>
        {
            showsKey = false; remoteSharingShown = false;
            if (remoteHost is { } host) await host.StopAsync();
        })), SettingsControlSize.Small, destructive: true);
        stopSharing.HorizontalAlignment = HorizontalAlignment.Right; AutomationProperties.SetAutomationId(stopSharing, "settings-remote-stop-sharing");
        sharing.Children.Add(stopSharing);
        sharing.Children.Add(SettingsText(Locale.Get("settings.remote.stopSharingNote"), 11, DesignToken.Ink2));
        share.Children.Add(sharing);

        // While not sharing: the workspaces to pick, the port and the start button.
        var notSharing = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        var picks = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        notSharing.Children.Add(picks);
        var portLabel = Locale.Get("settings.remote.portLabel");
        var port = SettingsField(new TextBox { Text = RemoteProtocol.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxLength = 5, Width = MobileDirectPortWidth });
        AutomationProperties.SetName(port, portLabel); AutomationProperties.SetAutomationId(port, "settings-remote-port");
        var start = SettingsPush(SafeButton(Locale.Get("settings.remote.startSharing"), () => RemoteAction(async () =>
        {
            if (remoteHost is not { } host) return;
            var locals = service.Snapshot.Workspaces.Where(w => w.Remote is null).Select(w => w.Id).Where(selected.Contains).ToList();
            // As on the Mac, a port that is not a number starts on the default one; the host refuses one out of range.
            var number = int.TryParse(port.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var typed) ? typed : RemoteProtocol.DefaultPort;
            await host.StartAsync(locals, number);
        })), SettingsControlSize.Small, prominent: true);
        AutomationProperties.SetAutomationId(start, "settings-remote-start-sharing");
        var portLine = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        portLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); portLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); portLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); portLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var portWords = SettingsText(portLabel, 12); Grid.SetColumn(port, 1); Grid.SetColumn(start, 3);
        portLine.Children.Add(portWords); portLine.Children.Add(port); portLine.Children.Add(start);
        notSharing.Children.Add(portLine);
        notSharing.Children.Add(SettingsText(RemoteWindowsStrings.SettingsShareNote, 11, DesignToken.Ink2));
        share.Children.Add(notSharing);
        SettingsRow(rows, share);

        // The Windows Firewall rule (WindowsFirewallRule, shared with the phone's direct path): one UAC prompt per change, none in a smoke
        // run. Added or updated in place for the ports in use now (FirewallPorts), or removed.
        var firewallText = SettingsText("", 11, DesignToken.Ink2); AutomationProperties.SetAutomationId(firewallText, "settings-remote-firewall-state");
        var firewallWords = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
        firewallWords.Children.Add(firewallText); firewallWords.Children.Add(SettingsText(RemoteWindowsStrings.SettingsFirewallNote, 11, DesignToken.Ink2));
        var firewallAdd = SettingsPush(SafeButton(RemoteWindowsStrings.FirewallAdd, () => firewallState == FirewallRuleState.Unknown ? RecheckFirewallRule() : ChangeFirewallRule(add: true)), SettingsControlSize.Small);
        var firewallRemove = SettingsPush(SafeButton(RemoteWindowsStrings.FirewallRemove, () => ChangeFirewallRule(add: false)), SettingsControlSize.Small, destructive: true);
        AutomationProperties.SetAutomationId(firewallAdd, "settings-remote-firewall-add"); AutomationProperties.SetAutomationId(firewallRemove, "settings-remote-firewall-remove");
        var firewallButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        firewallButtons.Children.Add(firewallAdd); firewallButtons.Children.Add(firewallRemove);
        SettingsRow(rows, SettingsLabeled(firewallWords, firewallButtons, top: true));

        // Connect to another computer: the address, the key and a name, a label column beside each (the Mac's).
        var connect = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        connect.Children.Add(SettingsText(Locale.Get("settings.remote.connectTitle"), 13, medium: true));
        var address = SettingsField(new TextBox { PlaceholderText = Locale.Get("settings.remote.addressPlaceholder"), MaxLength = 300 });
        AutomationProperties.SetName(address, Locale.Get("settings.remote.addressField")); AutomationProperties.SetAutomationId(address, "settings-remote-address");
        var key = new PasswordBox { PlaceholderText = Locale.Get("settings.remote.keyPlaceholder"), MaxLength = 128, Height = SettingsFieldHeight, MinHeight = 0, MinWidth = 0, TextSize = 11, VerticalAlignment = VerticalAlignment.Center };
        key.TextFont = TextArea.Code; key.Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0); key.CornerRadius = new CornerRadius(SettingsControlRadius);
        {
            var card = brushes.Brush(DesignToken.Card); var line = brushes.Brush(DesignToken.Line); var values = new List<(string Key, object Value)>();
            foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" }) values.Add(("TextControlBackground" + state, card));
            foreach (var state in new[] { "", "PointerOver", "Disabled" }) values.Add(("TextControlBorderBrush" + state, line));
            if (SetResourcesOnce(key, values)) { key.Background = card; key.BorderBrush = line; }
        }
        AutomationProperties.SetName(key, Locale.Get("settings.remote.keyField")); AutomationProperties.SetAutomationId(key, "settings-remote-key");
        var name = SettingsField(new TextBox { PlaceholderText = Locale.Get("settings.remote.namePlaceholder"), MaxLength = 120 }, 12);
        AutomationProperties.SetName(name, Locale.Get("settings.remote.nameField")); AutomationProperties.SetAutomationId(name, "settings-remote-name");
        var connectButton = SettingsPush(SafeButton(Locale.Get("settings.remote.connectButton"), () => RemoteAction(async () =>
        {
            var client = service.Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
            var typed = key.Password; key.Password = "";
            await client.ConnectAsync(name.Text, address.Text, typed);
        })), SettingsControlSize.Small, prominent: true);
        AutomationProperties.SetAutomationId(connectButton, "settings-remote-connect");
        Grid Field(string label, FrameworkElement field, FrameworkElement? after = null)
        {
            var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            line.ColumnDefinitions.Add(new() { Width = new(RemoteFieldLabelWidth) }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(SettingsText(label, 12)); Grid.SetColumn(field, 1); line.Children.Add(field);
            if (after is not null) { Grid.SetColumn(after, 2); line.Children.Add(after); }
            return line;
        }
        connect.Children.Add(Field(Locale.Get("settings.remote.addressField"), address));
        connect.Children.Add(Field(Locale.Get("settings.remote.keyField"), key));
        connect.Children.Add(Field(Locale.Get("settings.remote.nameField"), name, connectButton));
        connect.Children.Add(SettingsText(Locale.Get("settings.remote.connectNote"), 11, DesignToken.Ink2));
        SettingsRow(rows, connect);

        var error = SettingsText("", 11, DesignToken.ErrText, selectable: true); AutomationProperties.SetAutomationId(error, "settings-remote-error");
        var errorRow = SettingsRow(rows, error);

        // The connected computers, each with its shared workspaces.
        var connections = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        var connectionsRow = SettingsRow(rows, connections);

        // What remote workspaces can and cannot do (the key line says where Windows keeps the key).
        var scope = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
        scope.Children.Add(SettingsText(Locale.Get("settings.remote.scopeTitle"), 13, medium: true));
        foreach (var line in new[] { Locale.Get("settings.remote.scope.runs"), Locale.Get("settings.remote.scope.notMirrored"), Locale.Get("settings.remote.scope.outOfScope"), RemoteWindowsStrings.SettingsScopeKey })
            scope.Children.Add(SettingsText(line, 11, DesignToken.Ink2));
        AutomationProperties.SetAutomationId(scope, "settings-remote-scope");
        SettingsRow(rows, scope);

        void Buttons()
        {
            var locals = service.Snapshot.Workspaces.Where(w => w.Remote is null);
            start.IsEnabled = !remoteBusy && remoteHost is not null && tailscale.Count > 0 && locals.Any(w => selected.Contains(w.Id));
            stopSharing.IsEnabled = !remoteBusy && remoteHost is not null;
            connectButton.IsEnabled = !remoteBusy && service.Remote is not null && address.Text.Trim().Length > 0 && key.Password.Trim().Length > 0;
        }

        void Refresh()
        {
            if (closing || !rows.IsLoaded) return;
            // Current() ends a sharing whose listener died before it answers, so the page never shows that sharing as on.
            var host = remoteHost?.Current() ?? new RemoteHostState();
            if (remoteSharingShown && !host.Enabled && !remoteBusy) remoteError = Locale.Get("remote.host.error.stopped");
            remoteSharingShown = host.Enabled;
            var snapshot = service.Snapshot;
            var locals = snapshot.Workspaces.Where(w => w.Remote is null).ToList();
            // What is shared, or else the workspace in front (M .task), once per showing.
            if (!picked)
            {
                picked = true;
                if (host.Enabled) selected.UnionWith(host.WorkspaceIds);
                else if (locals.FirstOrDefault(w => w.Id == snapshot.ActiveWorkspaceId) is { } front) selected.Add(front.Id);
            }
            if (!host.Enabled) showsKey = false;

            shareDot.Fill = brushes.Brush(host.Enabled ? DesignToken.Done : tailscale.Count == 0 ? DesignToken.Err : DesignToken.Idle);
            shareStatus.Text = host.Enabled ? Locale.Get("settings.remote.sharingOn", new Dictionary<string, string> { ["count"] = host.WorkspaceIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                : tailscale.Count == 0 ? RemoteWindowsStrings.SettingsTailscaleOff : Locale.Get("settings.remote.sharingOff");
            activeRuns.Text = Locale.Get("settings.remote.activeRuns", new Dictionary<string, string> { ["count"] = host.ActiveRuns.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            activeRuns.Visibility = host.Enabled && host.ActiveRuns > 0 ? Visibility.Visible : Visibility.Collapsed;
            sharing.Visibility = host.Enabled ? Visibility.Visible : Visibility.Collapsed;
            notSharing.Visibility = host.Enabled ? Visibility.Collapsed : Visibility.Visible;

            var addressKey = string.Join("\n", host.Addresses);
            if (addressKey != shownAddresses)
            {
                shownAddresses = addressKey; addresses.Children.Clear();
                foreach (var value in host.Addresses)
                {
                    var words = SettingsText(value, 11, mono: true, selectable: true); words.TextWrapping = TextWrapping.NoWrap; words.TextTrimming = TextTrimming.CharacterEllipsis;
                    var copy = SettingsPush(Button(Locale.Get("settings.remote.copyButton"), () => { if (!options.SmokeTest) Copy(value); return Task.CompletedTask; }), SettingsControlSize.Small);
                    AutomationProperties.SetAutomationId(copy, "settings-remote-copy-address");
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm }; line.Children.Add(words); line.Children.Add(copy);
                    addresses.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.addressLabel"), 12), line));
                }
            }
            currentKey = host.Key;
            keyRow.Visibility = host.Key is null ? Visibility.Collapsed : Visibility.Visible;
            if (host.Key is null) copyNote.Visibility = Visibility.Collapsed;
            keyText.Text = showsKey && host.Key is { } shown ? shown : new string('•', 16); keyText.IsTextSelectionEnabled = showsKey;
            var keyLabel = Locale.Get(showsKey ? "settings.remote.hideKeyButton" : "settings.remote.showKeyButton");
            showKey.Content = keyLabel; AutomationProperties.SetName(showKey, keyLabel);
            fullAccess.IsChecked = host.AllowsFullAccess;
            var sharedKey = string.Join("\n", locals.Where(w => host.WorkspaceIds.Contains(w.Id)).Select(w => w.Id + "\t" + w.Name + "\t" + LocalPath(w)));
            if (sharedKey != shownShared)
            {
                shownShared = sharedKey; sharedList.Children.Clear();
                foreach (var local in locals.Where(w => host.WorkspaceIds.Contains(w.Id)))
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                    line.Children.Add(SettingsSymbol("\uE8B7", 12, DesignToken.Ink2)); line.Children.Add(SettingsText(local.Name, 12));
                    ToolTipService.SetToolTip(line, LocalPath(local)); sharedList.Children.Add(line);
                }
            }

            var picksKey = string.Join("\n", locals.Select(w => w.Id + "\t" + w.Name + "\t" + LocalPath(w)));
            if (picksKey != shownPicks)
            {
                shownPicks = picksKey; picks.Children.Clear();
                if (locals.Count == 0) picks.Children.Add(SettingsText(Locale.Get("settings.remote.noLocalWorkspaces"), 11, DesignToken.Ink2));
                else
                {
                    picks.Children.Add(SettingsText(Locale.Get("settings.remote.pickWorkspaces"), 11, DesignToken.Ink2, medium: true));
                    foreach (var local in locals)
                    {
                        var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                        words.Children.Add(SettingsText(local.Name, 12));
                        var path = SettingsText(LocalPath(local) ?? "", 10, DesignToken.Ink2, mono: true); path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis;
                        words.Children.Add(path);
                        var id = local.Id;
                        var pick = new CheckBox { Content = words, IsChecked = selected.Contains(id), MinWidth = 0, Tag = id };
                        AutomationProperties.SetName(pick, local.Name); AutomationProperties.SetAutomationId(pick, "settings-remote-share-" + id);
                        pick.Click += (_, _) => { if (pick.IsChecked == true) selected.Add(id); else selected.Remove(id); Buttons(); };
                        picks.Children.Add(pick);
                    }
                }
            }
            else foreach (var pick in picks.Children.OfType<CheckBox>()) pick.IsChecked = pick.Tag is string id && selected.Contains(id);

            ShowFirewall();
            error.Text = remoteError ?? ""; errorRow.Visibility = remoteError is null ? Visibility.Collapsed : Visibility.Visible;

            var known = service.Remote?.List() ?? [];
            var connectionsKey = remoteBusy + "\n" + string.Join("\n", known.Select(c => string.Join("\t", c.Id, c.Name, c.Address, c.Status, c.Detail, c.Runtime?.Platform, c.Runtime is null,
                string.Join("\r", c.Shared.Select(s => s.Id + "\r" + s.Name + "\r" + s.Path + "\r" + snapshot.Workspaces.Any(w => w.Remote?.ConnectionId == c.Id && w.Remote.WorkspaceId == s.Id))))));
            if (connectionsKey != shownConnections)
            {
                shownConnections = connectionsKey; connections.Children.Clear();
                if (known.Count > 0) connections.Children.Add(SettingsText(Locale.Get("settings.remote.connectionsTitle"), 13, medium: true));
                foreach (var connection in known) connections.Children.Add(ConnectionRow(connection, snapshot));
            }
            connectionsRow.Visibility = known.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Buttons();
        }

        // The rule's state and what the last change that did not happen said (Core's words only), and the buttons that change it.
        void ShowFirewall()
        {
            firewallText.Text = MobileDirectStrings.FirewallState(firewallState) + (firewallMessage is { } message ? " · " + message : "");
            var label = firewallState switch
            {
                FirewallRuleState.Unknown => MobileDirectStrings.FirewallRecheckButton,
                FirewallRuleState.Present => RemoteWindowsStrings.FirewallUpdate,
                _ => RemoteWindowsStrings.FirewallAdd,
            };
            firewallAdd.Content = label; AutomationProperties.SetName(firewallAdd, label);
            firewallAdd.Visibility = firewallState == FirewallRuleState.Unknown || FirewallPorts().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            firewallRemove.Visibility = firewallState == FirewallRuleState.Present ? Visibility.Visible : Visibility.Collapsed;
            firewallAdd.IsEnabled = firewallRemove.IsEnabled = !options.SmokeTest && !firewallBusy;
        }

        FrameworkElement ConnectionRow(RemoteConnectionInfo connection, AppSnapshot snapshot)
        {
            var row = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
            AutomationProperties.SetAutomationId(row, "settings-remote-connection-" + connection.Id);
            var head = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            head.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var connected = connection.Status == RemoteConnectionStatus.Connected;
            head.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Fill = brushes.Brush(connected ? DesignToken.Done : connection.Status == RemoteConnectionStatus.Disconnected ? DesignToken.Err : DesignToken.Idle),
            });
            var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            var title = SettingsText(connection.Name, 12, medium: true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
            var where = SettingsText(connection.Address, 10, DesignToken.Ink2, mono: true);
            where.TextWrapping = TextWrapping.NoWrap; where.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(title); words.Children.Add(where); Grid.SetColumn(words, 1); head.Children.Add(words);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            var refresh = SettingsPush(SafeButton(Locale.Get("settings.remote.refreshButton"), () => RemoteAction(() => (service.Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"))).RefreshAsync(connection.Id))), SettingsControlSize.Small);
            refresh.IsEnabled = !remoteBusy; AutomationProperties.SetAutomationId(refresh, "settings-remote-refresh-" + connection.Id); actions.Children.Add(refresh);
            if (connected)
            {
                var disconnect = SettingsPush(SafeButton(Locale.Get("settings.remote.disconnectButton"), () => RemoteAction(() => service.Remote?.DisconnectAsync(connection.Id) ?? Task.CompletedTask)), SettingsControlSize.Small);
                disconnect.IsEnabled = !remoteBusy; AutomationProperties.SetAutomationId(disconnect, "settings-remote-disconnect-" + connection.Id); actions.Children.Add(disconnect);
            }
            var remove = SettingsIconButton("\uE74D", Locale.Get("settings.remote.removeHelp"), () => Act(() => RemoteAction(() => service.Remote?.RemoveAsync(connection.Id) ?? Task.CompletedTask)), 12);
            remove.IsEnabled = !remoteBusy; AutomationProperties.SetAutomationId(remove, "settings-remote-remove-" + connection.Id); actions.Children.Add(remove);
            Grid.SetColumn(actions, 2); head.Children.Add(actions);
            row.Children.Add(head);
            if (connection.Detail is { } detail) row.Children.Add(SettingsText(detail, 11, DesignToken.Ink2));
            if (!connected) return row;
            if (connection.Shared.Count == 0) row.Children.Add(SettingsText(Locale.Get("settings.remote.noShared"), 11, DesignToken.Ink2));
            foreach (var hosted in connection.Shared)
            {
                var opened = snapshot.Workspaces.Any(w => w.Remote?.ConnectionId == connection.Id && w.Remote.WorkspaceId == hosted.Id);
                var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(DesignMetrics.Spacing.Xl, 0, 0, 0) };
                line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                line.Children.Add(SettingsSymbol("\uE977", 12, DesignToken.Ink2));
                var labels = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                labels.Children.Add(SettingsText(hosted.Name, 12));
                // The host's folder, shown only: it is that computer's path, never one on this PC.
                var hostPath = SettingsText(hosted.Path, 10, DesignToken.Ink2, mono: true); hostPath.TextWrapping = TextWrapping.NoWrap; hostPath.TextTrimming = TextTrimming.CharacterEllipsis;
                labels.Children.Add(hostPath); Grid.SetColumn(labels, 1); line.Children.Add(labels);
                var open = SettingsPush(SafeButton(Locale.Get(opened ? "settings.remote.openedButton" : "settings.remote.openButton"), () => RemoteAction(async () =>
                {
                    // Listed in the sidebar and made active, Settings closes and a workspace with no pane gets a Claude pane (M openRemoteWorkspace, addWorkspace).
                    var workspace = await service.OpenRemoteWorkspaceAsync(connection.Id, hosted.Id);
                    settingsWindow?.Close();
                    HideDashboard(); Render();
                    if (!service.Snapshot.Sessions.Any(s => s.WorkspaceId == workspace.Id)) await AddPane("claude");
                })), SettingsControlSize.Small);
                open.IsEnabled = !remoteBusy; AutomationProperties.SetAutomationId(open, "settings-remote-open-" + hosted.Id);
                Grid.SetColumn(open, 2); line.Children.Add(open);
                row.Children.Add(line);
            }
            return row;
        }

        redraw = Refresh;
        async Task ReadTailscale()
        {
            if (options.SmokeTest) { tailscale = ["127.0.0.1"]; return; }
            // The adapters are read off the window's thread.
            try { tailscale = await Task.Run(TailscaleAddresses.Current); }
            catch (AdaptersUnreadableException) { tailscale = []; }
        }
        // The timer's and Loaded's handlers are async void: nothing they reach may throw out of them.
        async Task Poll()
        {
            if (reading || closing || !rows.IsLoaded) return;
            reading = true;
            try
            {
                try { await ReadTailscale(); } finally { reading = false; }
                Refresh();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { remoteError = ex.Message; }
        }
        void FirewallUpdated() { if (!closing && rows.IsLoaded) ShowFirewall(); }
        address.TextChanged += (_, _) => Buttons();
        key.PasswordChanged += (_, _) => Buttons();
        var poll = DispatcherQueue.CreateTimer(); poll.Interval = TimeSpan.FromSeconds(3); poll.IsRepeating = true;
        // A page no longer shown (its Settings window closed) keeps no timer and refreshes no detached tree.
        poll.Tick += async (_, _) => { if (closing || !rows.IsLoaded) { poll.Stop(); return; } await Poll(); };
        rows.Loaded += async (_, _) =>
        {
            if (!remoteViews.Contains(Refresh)) remoteViews.Add(Refresh);
            FirewallChanged -= FirewallUpdated; FirewallChanged += FirewallUpdated;
            poll.Start();
            await Poll();
            if (!options.SmokeTest && !firewallBusy) { await RefreshFirewallState(); FirewallUpdated(); }
        };
        rows.Unloaded += (_, _) =>
        {
            if (rows.IsLoaded) return;
            poll.Stop(); remoteViews.Remove(Refresh); FirewallChanged -= FirewallUpdated;
            // Shown again, the page picks what is shared or the workspace in front anew, as the Mac's does each time it appears.
            showsKey = false; currentKey = null; key.Password = ""; picked = false; selected.Clear();
            keyText.Text = new string('•', 16); copyNote.Text = ""; copyNote.Visibility = Visibility.Collapsed;
        };
        return rows;
    }
}
