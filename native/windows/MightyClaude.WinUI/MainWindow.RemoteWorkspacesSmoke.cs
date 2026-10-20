using System.Diagnostics;
using System.Text.Json;
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
    /// Smoke key <c>remoteWorkspaces</c>: remote workspaces with nothing outside this process. The app's own host shares
    /// <paramref name="local"/> on loopback (the seam only a smoke run sets, in code), the app's own client connects to it, opens the
    /// shared workspace and runs commands through a remote command pane's composer by the real host runner (Windows PowerShell). The
    /// Settings page, the sidebar, the header, the status bar, session info, the pane's empty state, the add-pane menus, the phone's
    /// view and a host that stops sharing are read as the user meets them. No clipboard, no firewall, no Tailscale state, no network:
    /// the page's copy and firewall buttons are never pressed, and the firewall buttons must be off. Everything it adds is taken away
    /// again (sharing, the connection, the remote workspace and its pane), and the window is left as it found it.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunRemoteWorkspacesSmoke(Workspace local)
    {
        var checks = new Dictionary<string, object?>();
        var saved = service.Snapshot; var heldSend = smokeSend; var cleanup = new List<string>();
        StackPanel? page = null; RemoteHostService? host = null; RemoteConnectionInfo? info = null;
        var paneId = Wire.Id();
        static async Task Until(Func<bool> condition, double seconds, Func<string> seen)
        {
            try { await WaitUI(condition, "", seconds); }
            catch (TimeoutException) { throw new InvalidOperationException("remoteWorkspaces: " + seen()); }
        }
        static void Must(bool condition, string message) => Require(condition, "remoteWorkspaces: " + message);
        string Status(string id) => service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id)?.Status ?? "gone";
        // What the host printed for the pane, as one string: an output line cut by a console width still reads whole.
        string Printed(string id) => string.Concat((service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id)?.Logs ?? []).Where(l => l.Kind == "output").Select(l => l.Text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)));
        bool SameInk(Brush? brush, DesignToken token) => brush is SolidColorBrush shown && brushes.Brush(token) is var want && shown.Color == want.Color && shown.Opacity == want.Opacity;
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

            // The shared workspace opens here as remote, then is chosen the way the sidebar chooses one.
            var remote = await service.OpenRemoteWorkspaceAsync(connection.Id, local.Id);
            Must(remote.Remote is { } opened && opened.ConnectionId == connection.Id && opened.WorkspaceId == local.Id && remote.Id != local.Id && service.Snapshot.ActiveWorkspaceId == remote.Id,
                $"the shared workspace must open as a remote one and come to the front; remote {remote.Remote is not null}, id {remote.Id}, front {service.Snapshot.ActiveWorkspaceId}");
            await SelectWorkspace(remote.Id);
            var reference = remote.Remote!; var hostName = reference.HostName; var os = RemoteWindowsStrings.HostOS("win32");
            Must(hostName == connection.HostName && RemoteHostLabel(remote) == RemoteWindowsStrings.HostWithOS(hostName, "win32"), $"the workspace names the host and its OS; host '{hostName}', label '{RemoteHostLabel(remote)}'");
            var connectedWords = Locale.Get("remote.status.connected", new Dictionary<string, string> { ["host"] = hostName });
            await Until(() => workspaceHeader.Visibility == Visibility.Visible && workspaceHeaderRemote is { Visibility: Visibility.Visible, Child: Border badge } && AutomationProperties.GetAutomationId(badge) == "workspace-remote-host" && AutomationProperties.GetName(badge) == connectedWords,
                10, () => $"the header must show the host capsule 'workspace-remote-host' named '{connectedWords}'; header {workspaceHeader.Visibility}, capsule {workspaceHeaderRemote.Visibility} '{(workspaceHeaderRemote.Child is { } child ? AutomationProperties.GetName(child) : "none")}'");
            Must(AutomationProperties.GetAutomationId(workspaceHeaderFiles) == "workspace-open-files-" + remote.Id && !workspaceHeaderFiles.IsEnabled,
                $"the header's files button must be off for a remote workspace; id '{AutomationProperties.GetAutomationId(workspaceHeaderFiles)}', on {workspaceHeaderFiles.IsEnabled}");
            var statusWords = RemoteWorkspaceFeatures.StatusLine(reference, client.Connection(connection.Id));
            await Until(() => AutomationProperties.GetAutomationId(status) == StatusRemoteHostId && status.Text == statusWords && statusRemoteDot.Visibility == Visibility.Visible && SameInk(statusRemoteDot.Fill, DesignToken.Done) && statusMachineParts.All(p => p.Visibility == Visibility.Collapsed),
                10, () => $"the status bar must read '{statusWords}' as '{StatusRemoteHostId}' with the connected dot; got '{status.Text}' as '{AutomationProperties.GetAutomationId(status)}', dot {statusRemoteDot.Visibility}");
            Must(statusWords.Contains(os, StringComparison.Ordinal) && AutomationProperties.GetAutomationId(statusRemoteDot) == "status-remote-dot", $"the status bar names the host's OS '{os}': '{statusWords}'");
            checks["workspaceOpened"] = true;

            // The sidebar: the computer symbol, the host under the workspace's name, and the header's connect button.
            StackPanel Group() => SettingsElements(workspaces).OfType<StackPanel>().FirstOrDefault(g => AutomationProperties.GetAutomationId(g) == "sidebar-workspace-" + remote.Id)
                ?? throw new InvalidOperationException("remoteWorkspaces: the sidebar does not list the remote workspace sidebar-workspace-" + remote.Id);
            var hostLine = SettingsElements(Group()).OfType<TextBlock>().FirstOrDefault(t => AutomationProperties.GetAutomationId(t) == "sidebar-remote-host-" + remote.Id);
            var badgeName = Locale.Get("remote.workspace.badgeAccessibility", new Dictionary<string, string> { ["host"] = hostName });
            Must(hostLine is not null && hostLine.Text == hostName && AutomationProperties.GetName(hostLine) == badgeName, $"the sidebar row must show the host '{hostName}' as sidebar-remote-host-{remote.Id}; got '{hostLine?.Text ?? "none"}'");
            var pcGlyph = SettingsElements(Group()).OfType<FontIcon>().FirstOrDefault(i => i.Glyph == SidebarRemoteGlyph);
            Must(pcGlyph is not null, "the sidebar row must draw the computer symbol U+E977 for a remote workspace");
            checks["sidebarComputerGlyphWidth"] = pcGlyph!.ActualWidth;
            Must(sidebarConnectRemoteButton is { Visibility: Visibility.Visible, IsEnabled: true } connect && AutomationProperties.GetAutomationId(connect) == "sidebar-connect-remote", "the sidebar header must offer sidebar-connect-remote");
            checks["sidebarBadge"] = true;

            // A remote command pane: its own title, no terminal on this PC, the composer and the conversation, the empty state with the
            // line that says where its commands run, and the host and its OS in session info.
            smokeTerminalEnabled = true;
            bool added;
            try { added = await AddPane("shell", shape: pane => pane with { Id = paneId }); }
            finally { smokeTerminalEnabled = false; }
            var created = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == paneId);
            Must(added && created is { Kind: "shell" } && created.WorkspaceId == remote.Id && created.Title == Locale.Get("remote.pane.commandTitle") && service.IsRemoteSession(paneId),
                $"a remote command pane must be added, titled '{Locale.Get("remote.pane.commandTitle")}'; added {added}, title '{created?.Title ?? "none"}', error '{error.Text}'");
            await Until(() => views.TryGetValue(paneId, out var drawn) && drawn.Container.ActualWidth > 0, 10, () => "the remote command pane was never drawn");
            var view = views[paneId]; view.Refresh();
            var note = RemoteWindowsStrings.CommandNote(hostName, "win32"); string? paneProblem = "not read yet";
            await Until(() => (paneProblem = view.SmokeRemoteCommandPaneProblem(note)) is null, 10, () => "the remote command pane: " + paneProblem);
            var infoProblem = await view.SmokeRemoteSessionInfoProblem(hostName, os);
            Must(infoProblem is null, "session info: " + infoProblem);
            checks["commandPaneComposer"] = true;

            // One command through the composer, run by the host's PowerShell in the shared folder: -NoProfile -NonInteractive, with
            // NoDefaultCurrentDirectoryInExePath set. Only a remote workspace's send reaches the service; nothing starts a CLI here.
            smokeSend = request => service.IsRemoteWorkspace(request.WorkspaceId) ? service.SendAsync(request) : throw new InvalidOperationException(SmokeNeverStartsCli);
            const string probe = "Write-Output ('remote-smoke-' + 'ok'); Write-Output ('nocd=' + $env:NoDefaultCurrentDirectoryInExePath); Write-Output ('cwd=' + (Get-Location).Path); Write-Output ('argv=' + [Environment]::CommandLine)";
            var sendProblem = await view.SmokeRemoteSendAsync(probe);
            Must(sendProblem is null, "the composer did not send the command: " + sendProblem);
            await Until(() => Status(paneId) is "completed" or "error" or "stopped" && !service.IsSessionRunning(paneId), 30, () => $"the remote command never finished: status {Status(paneId)}, printed '{Printed(paneId)}'");
            var printed = Printed(paneId);
            Must(Status(paneId) == "completed" && printed.Contains("remote-smoke-ok", StringComparison.Ordinal), $"the host must run the command and the pane show its output; status {Status(paneId)}, printed '{printed}'");
            Must(printed.Contains("nocd=1", StringComparison.Ordinal), "the command must run with NoDefaultCurrentDirectoryInExePath=1: " + printed);
            Must(printed.Contains("-NoProfile", StringComparison.Ordinal) && printed.Contains("-NonInteractive", StringComparison.Ordinal) && printed.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase),
                "the command must run in powershell.exe -NoProfile -NonInteractive: " + printed);
            // Compared with Windows separators: the CI hands the profile folder over with a forward slash in it.
            var folder = Path.GetFullPath(local.Path).Replace('/', '\\').TrimEnd('\\');
            Must(printed.Replace('/', '\\').Contains("cwd=" + folder, StringComparison.OrdinalIgnoreCase), $"the command must run in the shared folder '{folder}': {printed}");
            // A command that prompts fails at once (-NonInteractive) instead of waiting on the host's desktop for an answer nobody gives.
            var asked = Stopwatch.StartNew();
            sendProblem = await view.SmokeRemoteSendAsync("Read-Host 'remote smoke prompt'");
            Must(sendProblem is null, "the composer did not send the Read-Host command: " + sendProblem);
            await Until(() => Status(paneId) is "completed" or "error" or "stopped" && !service.IsSessionRunning(paneId), 25, () => $"a command that prompts (Read-Host) must fail at once, not wait: status {Status(paneId)} after {asked.Elapsed.TotalSeconds:0.0} s");
            checks["readHostSeconds"] = Math.Round(asked.Elapsed.TotalSeconds, 1);
            Must(Status(paneId) == "error", $"Read-Host in a remote command must end in error (PowerShell is non-interactive); status {Status(paneId)}, printed '{Printed(paneId)}'");
            checks["commandRan"] = true;

            // Local-only features stay off: every add-pane menu offers agents and a remote command only and ends with Connect to Another
            // Computer…; the files button is off; a browser pane is refused; the pane never offers a child worktree.
            var entries = RemoteWorkspaceFeatures.AddPaneEntries(remote);
            Must(entries.Contains(AddPaneMenu.Shell) && !entries.Contains(AddPaneMenu.Files) && !entries.Contains(AddPaneMenu.Browser), "a remote workspace's add-pane entries: " + string.Join(", ", entries));
            string Agent(string provider) => ProviderCatalog.BetaLabel(provider, Locale.Get("workspace.newAgentPane", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(provider) }));
            var want = entries.Select(entry => entry switch
            {
                AddPaneMenu.Separator => "-", AddPaneMenu.Shell => Locale.Get("remote.menu.newCommand"), AddPaneMenu.OpenProject => Locale.Get(AddPaneMenu.OpenProjectKey), _ => Agent(AddPaneMenu.AgentProvider(entry)!),
            }).Append(Locale.Get("remote.menu.connect")).ToList();
            var sidebarAdd = SettingsElements(Group()).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "workspace-add-session-" + remote.Id);
            Must(sidebarAdd is not null, "the sidebar must offer workspace-add-session-" + remote.Id);
            foreach (var (menu, what) in new (FlyoutBase?, string)[] { (sidebarAdd!.Flyout, "the sidebar's"), (NewSessionMenu(), "a tab group's"), (DashboardAddMenu(remote), "the dashboard's") })
            {
                Must(MenuWords(menu).SequenceEqual(want), $"{what} add-pane menu must read [{string.Join(" | ", want)}]; got [{string.Join(" | ", MenuWords(menu))}]");
                var ids = menu is MenuFlyout { Items: var items } ? items.Select(item => AutomationProperties.GetAutomationId(item)).ToList() : new List<string>();
                Must(ids.Count > 0 && ids[^1] == "workspace-connect-remote-" + remote.Id && !ids.Any(id => id.StartsWith("dashboard-add-" + AddPaneMenu.Browser + "-", StringComparison.Ordinal) || id.StartsWith("dashboard-add-" + AddPaneMenu.Files + "-", StringComparison.Ordinal)),
                    $"{what} add-pane menu must end with workspace-connect-remote-{remote.Id} and offer no browser or files pane; ids [{string.Join(", ", ids)}]");
            }
            Must(!workspaceHeaderFiles.IsEnabled, "the files button stays off");
            var paneCount = service.Snapshot.Sessions.Count;
            await AddBrowserPane();
            Must(service.Snapshot.Sessions.Count == paneCount && error.Text == RemoteWindowsStrings.PaneUnavailable, $"a browser pane must be refused in a remote workspace; panes {paneCount} → {service.Snapshot.Sessions.Count}, error '{error.Text}'");
            error.Text = "";
            Must(service.SecondSend(service.Snapshot.Sessions.First(s => s.Id == paneId)) != ComposerSecondSend.Worktree, "a remote pane never offers a child worktree");
            checks["localOnlyOff"] = true;

            // The phone is never told of a remote workspace or its panes: its list leaves them out and the pane itself is not found.
            using (var router = new MobileDesktopRouter(service, Path.Combine(options.ProfileDirectory!, "remote-smoke-phone"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])))
            {
                router.Observe();
                var told = router.TakeNotifications().Select(n => n.Scope).ToList();
                var list = await router.RouteAsync("GET", "/m1/state?since=0&wait=0", null, "smoke-phone", CancellationToken.None);
                var listed = JsonSerializer.Serialize(list.Body, Wire.Json);
                Must(list.Status == 200 && listed.Contains(local.Id, StringComparison.Ordinal) && !listed.Contains(remote.Id, StringComparison.Ordinal) && !listed.Contains(paneId, StringComparison.Ordinal) && !told.Contains("session:" + paneId),
                    $"the phone's list must hold this PC's workspace only; status {list.Status}, local {listed.Contains(local.Id, StringComparison.Ordinal)}, remote {listed.Contains(remote.Id, StringComparison.Ordinal)}, pane {listed.Contains(paneId, StringComparison.Ordinal)}, told [{string.Join(", ", told)}]");
                var asked404 = await router.RouteAsync("GET", "/m1/sessions/" + paneId, null, "smoke-phone", CancellationToken.None);
                Must(asked404.Status == 404, "the phone must not reach a remote pane: " + asked404.Status);
            }
            checks["phoneHidden"] = true;

            // The key's copy, read without the clipboard: never in clipboard history or the cloud clipboard, marked for clipboard
            // managers to skip, cleared after a minute only while the clipboard still holds it.
            Must(RemoteSecretCopy.Key is { AllowedInHistory: false, Roamable: false } && RemoteSecretCopy.Key.ClearAfter == TimeSpan.FromSeconds(60), "the key copy must stay out of clipboard history and the cloud clipboard and clear after a minute: " + RemoteSecretCopy.Key);
            Must(RemoteSecretCopy.ShouldClear("copied", "copied") && !RemoteSecretCopy.ShouldClear("other", "copied") && !RemoteSecretCopy.ShouldClear(null, "copied"), "the clear takes only the key it copied");
            Must(ClipboardMonitorExclusionFormat == "ExcludeClipboardContentFromMonitorProcessing", "the key copy carries the clipboard-manager exclusion format");
            checks["keyCopyOptions"] = true;

            // A host that stops sharing ends the client's runs and the connection; this PC's update hold follows the host's run count.
            sendProblem = await view.SmokeRemoteSendAsync("Start-Sleep -Seconds 30");
            Must(sendProblem is null, "the composer did not send the long command: " + sendProblem);
            await Until(() => service.IsSessionRunning(paneId) && remoteHostRuns >= 1 && host.Current().ActiveRuns >= 1, 20,
                () => $"the long command must be running on the host; pane {Status(paneId)} running {service.IsSessionRunning(paneId)}, host runs {host.Current().ActiveRuns}, held runs {remoteHostRuns}");
            var stopping = Stopwatch.StartNew();
            await host.StopAsync();
            await Until(() => Status(paneId) == "error" && !service.IsSessionRunning(paneId), 45, () => $"a host that stops sharing must end the client's run in error; status {Status(paneId)} after {stopping.Elapsed.TotalSeconds:0.0} s");
            checks["stopSeconds"] = Math.Round(stopping.Elapsed.TotalSeconds, 1);
            Must(!host.Current().Enabled && client.Connection(connection.Id)?.Status == RemoteConnectionStatus.Disconnected, $"a host that stops sharing ends the connection; sharing {host.Current().Enabled}, connection {client.Connection(connection.Id)?.Status}");
            var lostWords = RemoteWorkspaceFeatures.StatusLine(reference, client.Connection(connection.Id));
            await Until(() => remoteHostRuns == 0 && status.Text == lostWords && SameInk(statusRemoteDot.Fill, DesignToken.Ink3), 10,
                () => $"after the stop the status bar must read '{lostWords}' with the idle dot and no run held; got '{status.Text}', held runs {remoteHostRuns}");
            checks["stopEndsRuns"] = true;
        }
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
            if (service.IsSessionRunning(paneId)) await Step("stop the remote pane", () => service.StopAsync(paneId));
            if (host is { } sharing) await Step("stop sharing", async () => await sharing.StopAsync());
            if (info is { } made && service.Remote is { } client) await Step("remove the connection", () => client.RemoveAsync(made.Id));
            await Step("restore the window's state", async () => { await service.UpdateAsync(_ => saved); Render(); });
            remoteError = null; remoteSharingShown = false;
            if (error.Text == RemoteWindowsStrings.PaneUnavailable) error.Text = "";
        }
        // Nothing of it is left for the phases after it: no sharing, no connection, no remote workspace or pane, no held run.
        Must(cleanup.Count == 0, "cleaning up failed: " + string.Join(" | ", cleanup));
        await Until(() => remoteHostRuns == 0, 10, () => "the host's run count never went back to 0: " + remoteHostRuns);
        var after = service.Snapshot;
        Must(!host!.Current().Enabled && service.Remote!.List().Count == 0 && after.Workspaces.All(w => w.Remote is null) && after.Sessions.All(s => s.Id != paneId) && after.ActiveWorkspaceId == saved.ActiveWorkspaceId
            && AutomationProperties.GetAutomationId(status) == StatusBarTextId && statusRemoteDot.Visibility == Visibility.Collapsed,
            $"the window must be left as it was; sharing {host.Current().Enabled}, connections {service.Remote!.List().Count}, remote workspaces {after.Workspaces.Count(w => w.Remote is not null)}, front {after.ActiveWorkspaceId ?? "none"} (was {saved.ActiveWorkspaceId ?? "none"}), status id '{AutomationProperties.GetAutomationId(status)}'");
        checks["passed"] = true;
        return checks;
    }

    private sealed partial class PaneView
    {
        /// <summary>
        /// Why this remote command pane does not look as it must before its first command, or null: no terminal of this PC, the
        /// conversation and the composer shown, and the empty state with the line <paramref name="note"/> under it.
        /// </summary>
        internal string? SmokeRemoteCommandPaneProblem(string note)
        {
            if (terminalHost is not null) return "a remote command pane made a terminal on this PC";
            if (output.View.Visibility != Visibility.Visible) return "the conversation is hidden";
            if (!input.IsLoaded || input.ActualWidth <= 0 || input.ActualHeight <= 0 || !send.IsLoaded) return $"the composer is not shown: input {input.ActualWidth:0}×{input.ActualHeight:0} loaded {input.IsLoaded}, send loaded {send.IsLoaded}";
            if (emptyOutput is not { Visibility: Visibility.Visible } empty || AutomationProperties.GetAutomationId(empty) != "pane-empty-" + id) return $"no empty state pane-empty-{id}: {(emptyOutput is null ? "none" : emptyOutput.Visibility.ToString())}";
            var line = SettingsElements(empty).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == "pane-remote-note-" + id);
            if (line is null || AutomationProperties.GetName(line) != note) return $"the note pane-remote-note-{id} must read '{note}'; got '{(line is null ? "none" : AutomationProperties.GetName(line))}'";
            return null;
        }

        /// <summary>Opens session info and reads the host's two rows, or why they are not <paramref name="host"/> and <paramref name="os"/>; the popover is closed again.</summary>
        internal async Task<string?> SmokeRemoteSessionInfoProblem(string host, string os)
        {
            await ShowContext();
            try
            {
                if (!sessionInfoOpen) return "the session popover did not open";
                foreach (var (key, want) in new[] { ("host", host), ("hostOS", os) })
                {
                    if (!sessionInfoRows.TryGetValue(key, out var row)) return "no " + key + " row";
                    if (AutomationProperties.GetAutomationId(row.Value) != "session-info-" + key + "-" + id || row.Host.Visibility != Visibility.Visible || row.Value.Text != want)
                        return $"session-info-{key}-{id} must show '{want}'; id '{AutomationProperties.GetAutomationId(row.Value)}', {row.Host.Visibility}, '{row.Value.Text}'";
                }
                return null;
            }
            finally { sessionInfoFlyout?.Hide(); }
        }

        /// <summary>Types <paramref name="text"/> into the composer and sends it as the user does; why it was not taken, or null.</summary>
        internal async Task<string?> SmokeRemoteSendAsync(string text)
        {
            var before = Session.Logs.Count(l => l.Kind == "user");
            updating = true; input.Text = text; updating = false; RefreshComposerState();
            if (!canSend) return $"the composer would not send; reason '{inputHint.Text}', status {Session.Status}";
            await Send();
            var taken = Session.Logs.Count(l => l.Kind == "user") == before + 1 && Session.Logs.Last(l => l.Kind == "user").Text == text;
            return taken && input.Text.Length == 0 ? null : $"the send was not taken; draft '{input.Text}', status {Session.Status}, error '{owner.error.Text}'";
        }
    }
}
