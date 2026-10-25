using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Smoke key <c>remoteWorkspaces</c> (the web smoke's live steps): the Settings page's half of remote workspaces, with nothing
    /// outside this process. The app's own host shares <paramref name="local"/> on loopback (the seam only a smoke run sets, in code)
    /// and the app's own client connects to it; the page's form (the one the web page draws) is read before sharing, while sharing
    /// and once connected. The remote pane's runs over that link, and a host that stops sharing, are Core's RemoteLoopbackVerification. No clipboard, no firewall, no Tailscale state, no network: the page's copy and firewall buttons are
    /// never pressed, and the firewall buttons must be off. Everything it adds is taken away again, and the window is left as it found it.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunRemoteWorkspacesSmoke(Workspace local)
    {
        var checks = new Dictionary<string, object?>();
        var saved = service.Snapshot; var heldSend = smokeSend; var cleanup = new List<string>();
        StackPanel? page = null; RemoteHostService? host = null; RemoteConnectionInfo? info = null; Exception? failure = null;
        static async Task Until(Func<bool> condition, double seconds, Func<string> seen)
        {
            try { await WaitUI(condition, "", seconds); }
            catch (TimeoutException) { throw new InvalidOperationException("remoteWorkspaces: " + seen()); }
        }
        static void Must(bool condition, string message) => Require(condition, "remoteWorkspaces: " + message);
        try
        {
            // The client and the host, the loopback seam in place of Tailscale; the page reads both sides once it is shown.
            InitializeRemoteWorkspaces(loopback: true);
            host = remoteHost ?? throw new InvalidOperationException("remoteWorkspaces: InitializeRemoteWorkspaces(loopback: true) made no host");
            var client = service.Remote ?? throw new InvalidOperationException("remoteWorkspaces: InitializeRemoteWorkspaces(loopback: true) attached no client");
            Must(!host.Current().Enabled && client.List().Count == 0, $"a fresh profile starts with sharing off and no saved connection; sharing {host.Current().Enabled}, connections {client.List().Count}");
            var frontIsLocal = saved.Workspaces.Any(w => w.Id == saved.ActiveWorkspaceId && w.Remote is null);
            page = BuildRemoteWorkspacesSection();
            page.Opacity = 0; page.IsHitTestVisible = false; page.HorizontalAlignment = HorizontalAlignment.Left; page.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetAccessibilityView(page, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetRow(page, 1); root.Children.Add(page);
            var shown = page;
            T Element<T>(string id) where T : FrameworkElement => SettingsElements(shown).OfType<T>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id)
                ?? throw new InvalidOperationException("remoteWorkspaces: the settings page has no " + typeof(T).Name + " " + id);
            var shareStatus = Element<TextBlock>("settings-remote-share-status");
            await Until(() => shown.IsLoaded && shareStatus.Text.Length > 0, 10, () => $"the settings page never drew its sharing line: loaded {shown.IsLoaded}, line '{shareStatus.Text}'");
            Must(shareStatus.Text == Locale.Get("settings.remote.sharingOff"), $"before sharing the page must say sharing is off (the smoke's fixed Tailscale reading); got '{shareStatus.Text}'");
            var start = Element<Control>("settings-remote-start-sharing");
            Must(start.IsEnabled == frontIsLocal, $"the start button must be on with the local workspace in front picked; on {start.IsEnabled}, front {saved.ActiveWorkspaceId ?? "none"} local {frontIsLocal}");
            Must(!Element<Control>("settings-remote-connect").IsEnabled, "connect must wait for an address and a key");
            Control firewallAdd = Element<Control>("settings-remote-firewall-add"), firewallRemove = Element<Control>("settings-remote-firewall-remove");
            Must(!firewallAdd.IsEnabled && !firewallRemove.IsEnabled && !firewallBusy, $"the firewall buttons must be off in a smoke run (no netsh, no UAC prompt); add {firewallAdd.IsEnabled}, remove {firewallRemove.IsEnabled}, busy {firewallBusy}");
            checks["settingsPage"] = true;

            // Sharing on the loopback seam, port chosen by the system; commands wait for the full-access switch.
            var state = await host.StartAsync([local.Id], 0);
            Must(state is { Enabled: true, AllowsFullAccess: false, Port: > 0, Key.Length: 43 } && state.WorkspaceIds.SequenceEqual([local.Id]) && state.Addresses.SequenceEqual(["127.0.0.1:" + state.Port]),
                "sharing must start on 127.0.0.1 with full access off and a new key: " + state);
            var port = state.Port!.Value; var address = "127.0.0.1:" + port;
            Must(host.SetAllowsFullAccess(true).AllowsFullAccess, "the full-access switch did not take");
            RefreshRemoteViews();
            var sharingOn = Locale.Get("settings.remote.sharingOn", new Dictionary<string, string> { ["count"] = "1" });
            Must(shareStatus.Text == sharingOn, $"the page must say one workspace is shared; got '{shareStatus.Text}'");
            var keyText = Element<TextBlock>("settings-remote-key-text");
            Must(keyText.Text == new string('•', 16) && !keyText.Text.Contains(state.Key!, StringComparison.Ordinal), $"the key must show masked until asked; got {keyText.Text.Length} characters");
            Must(Element<ToggleButton>("settings-remote-full-access").IsChecked == true, "the full-access switch must read on");
            Must(SettingsElements(shown).OfType<TextBlock>().Any(t => t.Text == address), "the page must list " + address);
            Element<Control>("settings-remote-copy-key"); Element<Control>("settings-remote-copy-address");
            Must(Element<TextBlock>("settings-remote-copy-key-note").Visibility == Visibility.Collapsed, "nothing was copied, so no copy note shows");
            checks["sharing"] = true;

            // The client reaches the host (a first /info reads this PC's CLIs, which may outlast one request on a cold runner).
            Exception? refused = null;
            for (var attempt = 0; attempt < 2 && info is null; attempt++)
            {
                try { info = await client.ConnectAsync("", address, state.Key!); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { refused = ex; }
            }
            Must(info is not null, $"the client could not reach the host at {address}: {refused?.GetType().Name}: {refused?.Message}");
            var connection = info!;
            Must(connection.Status == RemoteConnectionStatus.Connected && connection.Runtime?.Platform == "win32" && !string.IsNullOrWhiteSpace(connection.HostName) && connection.Shared.Any(s => s.Id == local.Id),
                $"the client must be connected to a Windows host sharing the workspace; status {connection.Status}, platform {connection.Runtime?.Platform ?? "none"}, host '{connection.HostName}', shared [{string.Join(", ", connection.Shared.Select(s => s.Id))}]");
            RefreshRemoteViews();
            Element<FrameworkElement>("settings-remote-connection-" + connection.Id); Element<Control>("settings-remote-open-" + local.Id);
            checks["connected"] = true;
            root.Children.Remove(page); page = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex; }
        finally
        {
            // Taken away in order, each step on its own, so a later phase finds the window as this one found it.
            async Task Step(string what, Func<Task> work)
            {
                try { await work(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { cleanup.Add(what + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
            smokeSend = heldSend; smokeTerminalEnabled = false;
            if (page is not null) root.Children.Remove(page);
            if (host is { } sharing) await Step("stop sharing", async () => await sharing.StopAsync());
            if (info is { } made && service.Remote is { } client) await Step("remove the connection", () => client.RemoveAsync(made.Id));
            await Step("restore the window's state", async () => { await service.UpdateAsync(_ => saved); Render(); });
            remoteError = null; remoteSharingShown = false;
            if (error.Text == RemoteWindowsStrings.PaneUnavailable) error.Text = "";
        }
        // A check that failed is reported with whatever its cleanup could not undo, so neither hides the other.
        if (failure is not null)
        {
            if (cleanup.Count > 0) throw new InvalidOperationException(failure.Message + " | cleaning up failed: " + string.Join(" | ", cleanup), failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        // Nothing of it is left for the phases after it: no sharing, no connection, no remote workspace or pane, no held run.
        Must(cleanup.Count == 0, "cleaning up failed: " + string.Join(" | ", cleanup));
        await Until(() => remoteHostRuns == 0, 10, () => "the host's run count never went back to 0: " + remoteHostRuns);
        var after = service.Snapshot;
        Must(!host!.Current().Enabled && service.Remote!.List().Count == 0 && after.Workspaces.All(w => w.Remote is null) && after.ActiveWorkspaceId == saved.ActiveWorkspaceId,
            $"the window must be left as it was; sharing {host.Current().Enabled}, connections {service.Remote!.List().Count}, remote workspaces {after.Workspaces.Count(w => w.Remote is not null)}, front {after.ActiveWorkspaceId ?? "none"} (was {saved.ActiveWorkspaceId ?? "none"})");
        checks["passed"] = true;
        return checks;
    }
}
