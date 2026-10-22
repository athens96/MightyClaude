using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private MobileRelayHost? mobileHost;
    private MobileDesktopRouter? mobileRouter;
    private MobilePushHub? mobilePush;
    private readonly DispatcherTimer mobileNotify = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool mobileNotifying, mobileWatchesPower;
    private async Task InitializeMobileRemote()
    {
        if (options.SmokeTest || mobileHost is not null) return;
        var directory = Path.Combine(StateDirectory, "mobile-remote");
        // Catalog: this PC's CLI lists — a phone's summaries cover local panes only (RemoteWorkspacePhone), never a remote workspace's.
        mobileRouter = new(service, directory, Environment.MachineName, MobileAction, MobileExtras) { AppVersion = AppVersionText, ImagePreview = MakeMobileImagePreview, QueueCount = id => views.TryGetValue(id, out var pane) ? pane.MobileQueuedCount : 0, Structure = MobileStructureFor,
            Catalog = pane => Runtime(pane.Provider)?.ModelCatalog, Digest = pane => pane.Kind == "claude" && views.TryGetValue(pane.Id, out var view) ? view.MobileDigest() : null, ActiveStyle = MobileRunningStyle };
        mobileHost = new(directory, Environment.MachineName, MobileDesktopRouter.Capabilities, MobileRoute) { AppVersion = AppVersionText };
        mobileRouter.HostId = mobileHost.HostId;
        mobileRouter.DirectEndpoints = () => mobileHost?.DirectEndpoints ?? [];
        // Phone notifications (M/MightyClaude/AppStore+MobilePush.swift), routed before the host starts so the "push" it advertises always
        // has its routes. A confirmed lost sign-in is the login recovery's remembered request, read on this (the UI) thread.
        mobilePush = new MobilePushHub(service, mobileHost, Environment.MachineName) { SignInLost = id => loginRetries.Requests.ContainsKey(id) };
        mobileRouter.Push = mobilePush;
        await InitializeScreenShare(directory);
        // What a phone shows of each pane is read again four times a second while a phone is connected, and a phone is told
        // only of the panes (and the list) that moved (M/MightyClaude/AppStore+MobileRemote.swift:271-323).
        mobileNotify.Tick += async (_, _) =>
        {
            if (closing) return;
            // Pushes are for phones that are not connected, so they are planned on every tick (nothing while the remote is off);
            // sending happens off this thread.
            try { mobilePush?.Observe(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("mobile push: " + ex.GetType().Name); }
            if (mobileNotifying || mobileRouter is null || mobileHost is null || mobileHost.Status.Connections == 0) return;
            mobileNotifying = true;
            try { mobileRouter.Changed(); mobileRouter.Observe(); foreach (var (scope, revision) in mobileRouter.TakeNotifications()) await mobileHost.NotifyAsync(scope, revision); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            finally { mobileNotifying = false; }
        };
        // Asleep, the PC missed the relay's pings and its control socket may be gone on the relay's side while it still
        // looks open here (M/MightyClaude/AppStore+MobileRemote.swift:209-213).
        try { Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatusChanged += MobilePowerChanged; mobileWatchesPower = true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        mobileNotify.Start(); await mobileHost.ApplyAsync(service.Snapshot.MobileRemote);
    }
    private void MobilePowerChanged(object? sender, object e)
    {
        try { if (Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatus is Microsoft.Windows.System.Power.SystemSuspendStatus.AutoResume or Microsoft.Windows.System.Power.SystemSuspendStatus.ManualResume) mobileHost?.ReconnectSoon(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private Task<MobileReply> MobileRoute(string method, string path, JsonElement? body, string device, CancellationToken token)
    {
        var completion = new TaskCompletionSource<MobileReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => completion.TrySetCanceled(token));
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { token.ThrowIfCancellationRequested(); if (closing || mobileRouter is null) throw new OperationCanceledException(); completion.TrySetResult(await mobileRouter.RouteAsync(method, path, body, device, token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch { completion.TrySetResult(new(500, new { protocol = 1, error = "Desktop request failed." })); }
            finally { registration.Dispose(); }
        })) { registration.Dispose(); completion.TrySetCanceled(); }
        return completion.Task;
    }
    private async Task ShutdownMobileRemote()
    {
        // Nothing is planned, sent or tried again from here on.
        mobileNotify.Stop(); mobilePush?.Dispose(); mobilePush = null; if (mobileWatchesPower) { mobileWatchesPower = false; try { Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatusChanged -= MobilePowerChanged; } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
        await ShutdownScreenShare(); if (mobileHost is not null) await mobileHost.DisposeAsync(); mobileHost = null; mobileRouter?.Dispose(); mobileRouter = null;
    }
    // 모바일 리모트 (M/MobileRemoteSettingsView.swift:17-110): the allow switch over its explanation; the
    // relay's label, the address used while the field is empty, the field and the apply button; the four
    // steps; the state dot and words beside the reconnect button, and under them what the host is doing (the
    // retry it waits for, why the relay refused it, no network); the pairing QR (160) beside the host id
    // under its label, the key and the link and key buttons; the legacy-apps switch; then the paired
    // phones, a row each with its "connected" and "new device" marks. The switches take effect as they are
    // switched and the apply button is the address's own, as on the Mac.
    private StackPanel BuildMobileRemoteSection()
    {
        var rows = new StackPanel(); const string deviceRow = "mobile-device";
        var allowLabel = Locale.Get("windows.mobile.allow");
        var enabled = SettingsSwitch(allowLabel, service.Snapshot.MobileRemote.Enabled, "settings-mobile-toggle");
        var relayLabel = Locale.Get("settings.mobileRemote.relayLabel");
        var relay = SettingsField(new TextBox { Text = service.Snapshot.MobileRemote.RelayURL, MaxLength = 256 });
        AutomationProperties.SetName(relay, relayLabel); AutomationProperties.SetAutomationId(relay, "settings-mobile-relay");
        var legacyLabel = Locale.Get("settings.mobileRemote.legacyAppsToggle");
        var legacy = SettingsSwitch(legacyLabel, service.Snapshot.MobileRemote.AllowLegacyPhones, "settings-mobile-legacy-toggle");
        // The direct path (M/MobileRemoteSettingsView.swift `direct`): its switch over the Windows wording, the port with its own apply
        // button, what it found, and the Windows Firewall rule's row with the one button that asks Windows to elevate.
        var directLabel = Locale.Get("mobileRemote.direct.toggle");
        var direct = SettingsSwitch(directLabel, service.Snapshot.MobileRemote.DirectEnabled, "settings-mobile-direct-toggle");
        var portLabel = Locale.Get("mobileRemote.direct.portLabel");
        var port = SettingsField(new TextBox { Text = service.Snapshot.MobileRemote.DirectPort.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxLength = 5, Width = MobileDirectPortWidth });
        AutomationProperties.SetName(port, portLabel); AutomationProperties.SetAutomationId(port, "settings-mobile-direct-port");
        var directDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var directStatus = SettingsText("", 11, DesignToken.Ink2, selectable: true); AutomationProperties.SetAutomationId(directStatus, "settings-mobile-direct-status");
        var firewallText = SettingsText("", 11, DesignToken.Ink2); AutomationProperties.SetAutomationId(firewallText, "settings-mobile-firewall-state");
        Button firewallButton = null!, firewallUpdate = null!, applyPort = null!;
        Border portRow = null!, directStatusRow = null!, firewallRow = null!;
        var status = SettingsText("", 11, DesignToken.Ink2); var detail = SettingsText("", 11, DesignToken.Ink2);
        AutomationProperties.SetAutomationId(detail, "settings-mobile-detail");
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var qr = new Image { Width = MobileQrSize, Height = MobileQrSize }; AutomationProperties.SetName(qr, Locale.Get("settings.mobileRemote.qrAccessibility")); AutomationProperties.SetAutomationId(qr, "settings-mobile-qr");
        Button reconnect = null!; Border identityRow = null!, devicesTitleRow = null!;
        var showsKey = false;
        var hostIdLabel = Locale.Get("settings.mobileRemote.hostIdLabel"); var keyLabel = Locale.Get("settings.mobileRemote.keyLabel");
        var hostId = SettingsText("", 11, mono: true, selectable: true); var pairingKey = SettingsText("", 11, mono: true, selectable: true);
        hostId.TextWrapping = TextWrapping.NoWrap; hostId.TextTrimming = TextTrimming.CharacterEllipsis; pairingKey.TextWrapping = TextWrapping.NoWrap; pairingKey.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(hostId, hostIdLabel); AutomationProperties.SetName(pairingKey, keyLabel);
        AutomationProperties.SetAutomationId(hostId, "settings-mobile-host-id"); AutomationProperties.SetAutomationId(pairingKey, "settings-mobile-pairing-key");
        Button? showKey = null;
        void RefreshPairingText()
        {
            hostId.Text = mobileHost?.PairingServerId ?? "";
            pairingKey.Text = showsKey ? mobileHost?.PairingKeyForDisplay ?? "" : new string('•', 16);
            if (showKey is null) return;
            var label = Locale.Get(showsKey ? "settings.mobileRemote.hideKeyButton" : "settings.mobileRemote.showKeyButton");
            showKey.Content = label; AutomationProperties.SetName(showKey, label);
        }
        showKey = SettingsPush(Button(Locale.Get("settings.mobileRemote.showKeyButton"), () => { showsKey = !showsKey; RefreshPairingText(); return Task.CompletedTask; }), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(showKey, "settings-mobile-show-key");
        reconnect = SettingsPush(SafeButton(Locale.Get("settings.mobileRemote.reconnectButton"), async () =>
        {
            if (options.SmokeTest || closing || mobileHost is null || !service.Snapshot.MobileRemote.Enabled) return;
            await mobileHost.ReconnectAsync(); if (!closing && rows.IsLoaded) await Refresh();
        }), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(reconnect, "settings-mobile-reconnect");
        // Each switch is saved and takes effect as it is switched, and the button applies the address alone
        // (M/MobileRemoteSettingsView.swift:19, 32, 88).
        Button apply = null!; var writing = false; var showing = false; var savedRelay = service.Snapshot.MobileRemote.RelayURL;
        // Set only by a switch the user turned off (never one the code sets: `showing`): once the changes then under way have all
        // landed, the rule is taken away if nothing needs it any more, still as that click's own removal (RemoveRuleAfterOff).
        var removeRuleWhenSettled = false;
        // An address as it is saved: the same address typed without its scheme is not another address.
        static string Same(string address) => address.Length == 0 ? "" : MobileRelayHost.NormalizeRelay(address) ?? address;
        // On only for another address that can be used; an empty field goes back to the address shown before it.
        void ShowApply() { var typed = relay.Text.Trim(); apply.IsEnabled = !writing && !options.SmokeTest && Same(typed) != Same(savedRelay) && (typed.Length == 0 || MobileRelayHost.NormalizeRelay(typed) is not null); }
        // The port as typed when it is one the setting takes (1024-65535); the button is on only for another such port.
        int? TypedPort() => int.TryParse(port.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && MobileDirect.ValidPort(value) ? value : null;
        void ShowApplyPort() => applyPort.IsEnabled = !writing && !options.SmokeTest && TypedPort() is { } typed && typed != service.Snapshot.MobileRemote.DirectPort;
        Task ApplyPort() => TypedPort() is { } typed && typed != service.Snapshot.MobileRemote.DirectPort ? Write(saved => saved with { DirectPort = typed }) : Task.CompletedTask;
        // The switches and the button as the saved settings have them.
        void ShowControls()
        {
            var saved = service.Snapshot.MobileRemote; savedRelay = saved.RelayURL;
            showing = true; enabled.IsChecked = saved.Enabled; legacy.IsChecked = saved.AllowLegacyPhones; direct.IsChecked = saved.DirectEnabled; showing = false;
            enabled.IsEnabled = legacy.IsEnabled = direct.IsEnabled = !options.SmokeTest; ShowApply(); ShowApplyPort(); ShowDirect();
        }
        Task Write(Func<MobileRemoteSettings, MobileRemoteSettings> change) => Act(async () =>
        {
            // A switch pressed while the last change is still being applied waits for it: the switches stay as they were
            // pressed, and keep the keyboard, and what they then say is applied next (below).
            if (options.SmokeTest || writing) return;
            writing = true; ShowApply(); ShowApplyPort();
            MobileRemoteSettings? meant = null;
            try
            {
                await InitializeMobileRemote(); var settings = change(service.Snapshot.MobileRemote);
                // The host follows the change even when its save then fails: access the switch shows as off is off.
                var saving = service.UpdateAsync(s => s with { MobileRemote = settings });
                try { if (!settings.Enabled && screenHub is not null) await screenHub.KillAllAsync("revoked"); await mobileHost!.ApplyAsync(settings); await Refresh(); }
                finally { await saving; }
                meant = settings;
            }
            finally
            {
                writing = false;
                // The last press is what the user wants: a switch that no longer says what this change meant was pressed
                // meanwhile (off, while on was still being applied), and is applied now rather than put back, whether or
                // not the settings window is still open. After a failure the switches go back to what is saved and
                // nothing is tried again by itself.
                bool wantsOn = enabled.IsChecked == true, wantsLegacy = legacy.IsChecked == true, wantsDirect = direct.IsChecked == true;
                if (meant is not null && !closing && (wantsOn != meant.Enabled || wantsLegacy != meant.AllowLegacyPhones || wantsDirect != meant.DirectEnabled)) _ = Write(next => next with { Enabled = wantsOn, AllowLegacyPhones = wantsLegacy, DirectEnabled = wantsDirect });
                else
                {
                    ShowControls();
                    // The last queued change has landed: an off-click pressed meanwhile (whose own Write only queued it) looks at the rule now.
                    if (removeRuleWhenSettled) { removeRuleWhenSettled = false; _ = RemoveRuleAfterOff(); }
                }
            }
        });
        async Task ApplyRelay()
        {
            var typed = relay.Text.Trim();
            if (typed.Length > 0 && MobileRelayHost.NormalizeRelay(typed) is null) { await Act(() => throw new ArgumentException(Locale.Get("windows.mobile.invalidRelay"))); return; }
            await Write(saved => saved with { RelayURL = typed });
            // The field then holds the address as it was saved, as the Mac's does (M/MobileRemoteSettingsView.swift:195).
            if (relay.Text.Trim() == typed && Same(service.Snapshot.MobileRemote.RelayURL) == Same(typed)) { relay.Text = service.Snapshot.MobileRemote.RelayURL; ShowApply(); }
        }
        apply = SettingsPush(Button(Locale.Get("settings.mobileRemote.applyButton"), ApplyRelay));
        AutomationProperties.SetAutomationId(apply, "settings-mobile-apply");
        applyPort = SettingsPush(Button(Locale.Get("settings.mobileRemote.applyButton"), ApplyPort), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(applyPort, "settings-mobile-direct-apply");
        port.TextChanged += (_, _) => ShowApplyPort();
        // Enter in the field applies the port, as the Mac's field does on submit.
        port.KeyDown += async (_, args) => { if (args.Key != Windows.System.VirtualKey.Enter || !applyPort.IsEnabled) return; args.Handled = true; await ApplyPort(); };
        // The only button that elevates: add while there is none, remove while there is one. It is off while a change waits on Windows'
        // UAC prompt and comes back (FirewallChanged) once that change ends, approved, cancelled, refused, failed or timed out. While the
        // rule could not be read it reads it again instead (unelevated), so an unknown state is never a dead end.
        firewallButton = SettingsPush(SafeButton(MobileDirectStrings.FirewallButton(FirewallRuleState.Absent), () => firewallState == FirewallRuleState.Unknown ? RecheckFirewallRule() : ChangeFirewallRule(firewallState != FirewallRuleState.Present)), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(firewallButton, "settings-mobile-firewall-button");
        // The one rule also opens this PC's sharing port (Settings → Remote Connection): while it is there and a port is in use, it can be
        // set to exactly the ports in use now (an elevated set rule), so turning the direct path on after sharing never leaves its port shut.
        firewallUpdate = SettingsPush(SafeButton(RemoteWindowsStrings.FirewallUpdate, () => ChangeFirewallRule(add: true)), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(firewallUpdate, "settings-mobile-firewall-update");
        enabled.Checked += async (_, _) => { if (!showing) await Write(saved => saved with { Enabled = true }); };
        // A switch the user turns off (never one the code sets: `showing`) takes the rule away when nothing needs it, from that same click.
        // While an earlier change is still being applied, the click's Write only queues it; the rule is then looked at once the queued
        // change lands (Write's last finally), not now, when the saved settings still need the port.
        // The rule is looked at before Act, which clears the error line: a failed write's own error stays on screen when nothing elevates.
        async Task RemoveRuleAfterOff() { if (firewallState == FirewallRuleState.Present && FirewallPorts().Count == 0) await Act(RemoveFirewallRuleIfUnneeded); if (!closing && rows.IsLoaded) ShowDirect(); }
        async Task SwitchedOff(Func<MobileRemoteSettings, MobileRemoteSettings> change)
        {
            var queued = writing; if (queued) removeRuleWhenSettled = true;
            await Write(change);
            if (queued) return;
            // A press made while this change was applied is being applied now: the rule waits for that one to land too.
            if (writing) { removeRuleWhenSettled = true; return; }
            await RemoveRuleAfterOff();
        }
        enabled.Unchecked += async (_, _) => { if (showing) return; await SwitchedOff(saved => saved with { Enabled = false }); };
        legacy.Checked += async (_, _) => { if (!showing) await Write(saved => saved with { AllowLegacyPhones = true }); };
        legacy.Unchecked += async (_, _) => { if (!showing) await Write(saved => saved with { AllowLegacyPhones = false }); };
        direct.Checked += async (_, _) => { if (!showing) await Write(saved => saved with { DirectEnabled = true }); };
        direct.Unchecked += async (_, _) => { if (showing) return; await SwitchedOff(saved => saved with { DirectEnabled = false }); };
        relay.TextChanged += (_, _) => ShowApply();
        // Enter in the field applies the address, as the Mac's field does on submit (M/MobileRemoteSettingsView.swift:31).
        relay.KeyDown += async (_, args) => { if (args.Key != Windows.System.VirtualKey.Enter || !apply.IsEnabled) return; args.Handled = true; await ApplyRelay(); };

        SettingsRow(rows, SettingsLabeled(SettingsTitled(allowLabel, Locale.Get("windows.mobile.relayDescription")), enabled, top: true));
        // The form draws the field's own title — the address used while the field is empty — as a label before it,
        // and gives the field 0.54 of what the row's label and button leave (screens/10-settings-mobile-redacted-dark.webp).
        var relayRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        relayRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); relayRow.ColumnDefinitions.Add(new() { Width = new(1 - MobileRelayFieldShare, GridUnitType.Star) });
        relayRow.ColumnDefinitions.Add(new() { Width = new(MobileRelayFieldShare, GridUnitType.Star) }); relayRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var relayDefault = SettingsText(MobileRelayHost.DefaultRelay, 11, mono: true); relayDefault.TextWrapping = TextWrapping.NoWrap; relayDefault.TextTrimming = TextTrimming.CharacterEllipsis;
        relayRow.Children.Add(SettingsText(relayLabel)); Grid.SetColumn(relayDefault, 1); relayRow.Children.Add(relayDefault);
        Grid.SetColumn(relay, 2); relayRow.Children.Add(relay); Grid.SetColumn(apply, 3); relayRow.Children.Add(apply);
        SettingsRow(rows, relayRow);
        // The four steps (M/MobileRemoteSettingsView.swift:175-191): the number in a 14-wide column, right-aligned, 5 from its sentence.
        var guide = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs) };
        AutomationProperties.SetAutomationId(guide, "settings-mobile-guide");
        var steps = new[] { Locale.Get("settings.mobileRemote.guide.step1"), Locale.Get("settings.mobileRemote.guide.step2"), Locale.Get("settings.mobileRemote.guide.step3"), Locale.Get("settings.mobileRemote.guide.step4") };
        for (var step = 0; step < steps.Length; step++)
        {
            var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Xs };
            line.ColumnDefinitions.Add(new() { Width = new(14) }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var number = SettingsText(step + 1 + ".", 11, DesignToken.Ink2); number.HorizontalAlignment = HorizontalAlignment.Right; number.VerticalAlignment = VerticalAlignment.Top;
            line.Children.Add(number);
            var sentence = SettingsText(steps[step], 11, DesignToken.Ink2); Grid.SetColumn(sentence, 1); line.Children.Add(sentence);
            guide.Children.Add(line);
        }
        SettingsRow(rows, guide);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        state.Children.Add(dot); state.Children.Add(status);
        SettingsRow(rows, SettingsLabeled(state, reconnect));
        SettingsRow(rows, detail);
        SettingsRow(rows, SettingsLabeled(SettingsTitled(directLabel, MobileDirectStrings.Description), direct, top: true));
        var portLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        portLine.Children.Add(SettingsText(portLabel)); portLine.Children.Add(port); portLine.Children.Add(applyPort);
        portRow = SettingsRow(rows, portLine);
        var directLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        directLine.Children.Add(directDot); directLine.Children.Add(directStatus);
        directStatusRow = SettingsRow(rows, directLine);
        var firewallWords = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
        firewallWords.Children.Add(firewallText);
        firewallWords.Children.Add(SettingsText(MobileDirectStrings.FirewallDescription, 11, DesignToken.Ink2));
        firewallWords.Children.Add(SettingsText(MobileDirectStrings.PortHint, 11, DesignToken.Ink2));
        firewallWords.Children.Add(SettingsText(RemoteWindowsStrings.FirewallRemoveClosesBoth, 11, DesignToken.Ink2));
        var firewallButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        firewallButtons.Children.Add(firewallUpdate); firewallButtons.Children.Add(firewallButton);
        firewallRow = SettingsRow(rows, SettingsLabeled(firewallWords, firewallButtons, top: true));

        var copy = SettingsPush(Button(Locale.Get("settings.mobileRemote.copyLinkButton"), () => { if (mobileHost is not null && service.Snapshot.MobileRemote.Enabled) { var package = new DataPackage(); package.SetText(mobileHost.PairingUrl); Clipboard.SetContent(package); } return Task.CompletedTask; }), SettingsControlSize.Small);
        copy.Content = SettingsGlyphLabel("", Locale.Get("settings.mobileRemote.copyLinkButton"), 11);
        var regenerate = SettingsPush(SafeButton(Locale.Get("settings.mobileRemote.regenerateKeyButton"), () => Revoke(null)), SettingsControlSize.Small, destructive: true);
        regenerate.Content = SettingsGlyphLabel("", Locale.Get("settings.mobileRemote.regenerateKeyButton"), 11);
        // On Windows a new key unpairs every phone; the button says so before it asks.
        ToolTipService.SetToolTip(regenerate, Locale.Get("windows.mobile.rotationDescription"));
        var pairing = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        pairing.Children.Add(SettingsText(Locale.Get("settings.mobileRemote.scanInstruction"), 11, DesignToken.Ink2));
        // The host id is too long for its label's line, so the form sets it under the label; the key stays beside its own.
        pairing.Children.Add(SettingsText(hostIdLabel)); pairing.Children.Add(hostId);
        var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        key.Children.Add(pairingKey); key.Children.Add(showKey);
        pairing.Children.Add(SettingsLabeled(SettingsText(keyLabel), key));
        var pairingButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        pairingButtons.Children.Add(copy); pairingButtons.Children.Add(regenerate);
        pairing.Children.Add(pairingButtons);
        // The QR on white at radius 8, 16 from the pairing words (M/MobileRemoteSettingsView.swift:61-65).
        var identity = new Grid { ColumnSpacing = DesignMetrics.Spacing.Lg, Margin = new Thickness(0, DesignMetrics.Spacing.Xs, 0, DesignMetrics.Spacing.Xs) };
        identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); identity.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        identity.Children.Add(new Border { Child = qr, Background = brushes.Brush(DesignToken.OnStatus), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), VerticalAlignment = VerticalAlignment.Top });
        Grid.SetColumn(pairing, 1); identity.Children.Add(pairing);
        identityRow = SettingsRow(rows, identity);
        SettingsRow(rows, SettingsLabeled(SettingsTitled(legacyLabel, Locale.Get("settings.mobileRemote.legacyAppsDescription")), legacy, top: true));
        devicesTitleRow = SettingsRow(rows, SettingsText(Locale.Get("settings.mobileRemote.connectedDevicesTitle"), 11, DesignToken.Ink2, medium: true));
        async Task Revoke(string? id)
        {
            if (mobileHost is null || options.SmokeTest || id is not null && !mobileHost.Devices.Any(device => device.Id == id)) return;
            var host = mobileHost;
            // The note names the phone being unpaired (M/MobileRemoteSettingsView.swift:104-124); what follows is Windows' own
            // rule, where a new key unpairs every phone.
            var named = id is null ? null : host.Devices.FirstOrDefault(device => device.Id == id)?.Name;
            var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get(id is null ? "settings.mobileRemote.regenerateKeyButton" : "settings.mobileRemote.revokeDialogTitle"), Content = (named is null ? "" : named + "\n\n") + Locale.Get("windows.mobile.rotationConfirm"), PrimaryButtonText = Locale.Get("settings.mobileRemote.revokeConfirmButton"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (closing || !ReferenceEquals(host, mobileHost) || id is not null && !host.Devices.Any(device => device.Id == id)) return;
            if (screenHub is not null) await screenHub.ResetAsync("rekey-pairing"); await host.RotateKeyAsync(id); mobileRouter?.ResetUploads(); showsKey = false; await Refresh();
        }
        // The time a phone was first and last seen, in the Mac's yyyy-MM-dd HH:mm (M/MobileRemoteSettingsView.swift:168-173).
        static string Stamp(string value) => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var seen)
            ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : Locale.Get("settings.mobileRemote.unknownTime");
        void ShowState()
        {
            var on = service.Snapshot.MobileRemote.Enabled; var connected = mobileHost?.Status;
            // The dot: done while the relay is connected, waiting while it is being reached, idle when off (M/MobileRemoteSettingsView.swift:36).
            dot.Fill = brushes.Brush(!on ? DesignToken.Ink3 : connected?.Connected == true ? DesignToken.Done : DesignToken.WaitText);
            status.Text = !on ? Locale.Get("settings.mobileRemote.statusOff") : connected?.Connected != true ? Locale.Get("settings.mobileRemote.statusConnecting")
                : connected.Connections > 0 ? Locale.Get("settings.mobileRemote.statusConnectedTemplate", new Dictionary<string, string> { ["count"] = connected.Connections.ToString() }) : Locale.Get("settings.mobileRemote.statusRelayConnected");
            // Under it, what the host is doing (M/MobileRemoteSettingsView.swift:48): until the host has been read, off or connecting.
            detail.Text = MobileRelayStatusText.Detail(!on ? new(false, 0, "disabled") : connected ?? new(false, 0, "connecting"));
        }
        // The direct rows (M/MobileRemoteSettingsView.swift `direct`): the port and the status line under an on switch, the status only
        // while the remote is on; the firewall row while the path is on or the rule is still there to remove. The row says only Core's
        // words: the rule's state and what the last change that did not happen said.
        void ShowDirect()
        {
            var saved = service.Snapshot.MobileRemote; var status = MobileDirectStatusNow; var words = MobileDirectStatusText.Detail(status);
            portRow.Visibility = saved.DirectEnabled ? Visibility.Visible : Visibility.Collapsed;
            directStatusRow.Visibility = saved.Enabled && saved.DirectEnabled && words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            firewallRow.Visibility = saved.DirectEnabled || firewallState == FirewallRuleState.Present ? Visibility.Visible : Visibility.Collapsed;
            directDot.Fill = brushes.Brush(status.Listening ? DesignToken.Done : DesignToken.Ink3);
            directStatus.Text = words;
            firewallText.Text = MobileDirectStrings.FirewallState(firewallState) + FirewallNeedsUpdateWords() + (firewallMessage is { } message ? " · " + message : "");
            var label = firewallState == FirewallRuleState.Unknown ? MobileDirectStrings.FirewallRecheckButton : MobileDirectStrings.FirewallButton(firewallState);
            firewallButton.Content = label; AutomationProperties.SetName(firewallButton, label);
            // With no port to open (the remote off) and no rule to remove there is nothing to press, so the button is not shown at all.
            firewallButton.Visibility = firewallState != FirewallRuleState.Absent || FirewallPorts().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            firewallButton.IsEnabled = !options.SmokeTest && !firewallBusy;
            firewallUpdate.Visibility = firewallState == FirewallRuleState.Present && FirewallPorts().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            firewallUpdate.IsEnabled = !options.SmokeTest && !firewallBusy;
        }
        // The "connected" and "new device" marks (M/MobileRemoteSettingsView.swift:150-163): 9pt medium on the soft fill, a capsule.
        Border Mark(string text, DesignToken ink, DesignToken fill, string id)
        {
            var mark = new Border { Child = SettingsText(text, 9, ink, medium: true), Background = brushes.Brush(fill), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(mark, id); return mark;
        }
        void HidePairing() { identityRow.Visibility = devicesTitleRow.Visibility = Visibility.Collapsed; ReplaceSettingsRows(rows, deviceRow, []); qr.Source = null; }
        async Task Refresh()
        {
            var showsIdentity = service.Snapshot.MobileRemote.Enabled && mobileHost is not null;
            identityRow.Visibility = showsIdentity ? Visibility.Visible : Visibility.Collapsed;
            reconnect.Visibility = service.Snapshot.MobileRemote.Enabled ? Visibility.Visible : Visibility.Collapsed; reconnect.IsEnabled = mobileHost is not null && !options.SmokeTest;
            if (!showsIdentity) showsKey = false;
            RefreshPairingText(); ShowState(); ShowDirect();
            if (!showsIdentity) { HidePairing(); return; }
            var link = mobileHost!.PairingUrl; var png = PngByteQRCodeHelper.GetQRCode(link, QRCodeGenerator.ECCLevel.M, 6);
            using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); } stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); if (mobileHost?.PairingUrl != link) return; qr.Source = bitmap;
            // The paired phones, a row each (M/MobileRemoteSettingsView.swift:128-148): the symbol, the name over when it was seen, the unpair button.
            var contents = new List<FrameworkElement>();
            foreach (var device in mobileHost.Devices)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                AutomationProperties.SetAutomationId(row, "settings-mobile-device-" + device.Id);
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(SettingsSymbol(device.Legacy ? "" : "", 14, device.Connected ? DesignToken.DoneText : DesignToken.Ink2));
                var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm }; name.Children.Add(SettingsText(device.Name, 12));
                if (device.Connected) name.Children.Add(Mark(Locale.Get("settings.mobileRemote.connectedBadge"), DesignToken.DoneText, DesignToken.DoneSoft, "settings-mobile-connected-badge-" + device.Id));
                if (device.IsNew) name.Children.Add(Mark(Locale.Get("settings.mobileRemote.newDeviceBadge"), DesignToken.WaitText, DesignToken.WaitSoft, "settings-mobile-new-badge-" + device.Id));
                words.Children.Add(name);
                var seenAt = SettingsText(Locale.Get("settings.mobileRemote.seenTemplate", new Dictionary<string, string> { ["first"] = Stamp(device.FirstSeen), ["last"] = Stamp(device.LastSeen) }), 10, DesignToken.Ink2);
                seenAt.TextWrapping = TextWrapping.NoWrap; seenAt.TextTrimming = TextTrimming.CharacterEllipsis; words.Children.Add(seenAt);
                Grid.SetColumn(words, 1); row.Children.Add(words);
                var revoke = SettingsPush(SafeButton(Locale.Get("settings.mobileRemote.revokeRowButton"), () => Revoke(device.Id)), SettingsControlSize.Small);
                AutomationProperties.SetAutomationId(revoke, "settings-mobile-revoke-" + device.Id); ToolTipService.SetToolTip(revoke, Locale.Get("windows.mobile.rotationDescription"));
                Grid.SetColumn(revoke, 2); row.Children.Add(revoke);
                contents.Add(row);
            }
            devicesTitleRow.Visibility = contents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ReplaceSettingsRows(rows, deviceRow, contents);
        }
        void Updated(MobileRelayStatus _) => DispatcherQueue.TryEnqueue(async () => { if (!closing && rows.IsLoaded) await Act(Refresh); });
        // Until the host has been read (and for the whole of a smoke run) the section shows its switches and the saved state only.
        HidePairing(); reconnect.Visibility = service.Snapshot.MobileRemote.Enabled ? Visibility.Visible : Visibility.Collapsed; RefreshPairingText(); ShowState(); ShowControls();
        void FirewallUpdated() { if (!closing && rows.IsLoaded) ShowDirect(); }
        rows.Loaded += async (_, _) => { if (options.SmokeTest) { enabled.IsEnabled = relay.IsEnabled = legacy.IsEnabled = apply.IsEnabled = reconnect.IsEnabled = direct.IsEnabled = port.IsEnabled = applyPort.IsEnabled = firewallButton.IsEnabled = firewallUpdate.IsEnabled = false; HidePairing(); RefreshPairingText(); return; } FirewallChanged -= FirewallUpdated; FirewallChanged += FirewallUpdated; await Act(async () => { await InitializeMobileRemote(); if (!firewallBusy) await RefreshFirewallState(); if (closing || !rows.IsLoaded || mobileHost is null) return; mobileHost.StatusChanged += Updated; await Refresh(); }); };
        rows.Unloaded += (_, _) => { if (!rows.IsLoaded) FirewallChanged -= FirewallUpdated; if (mobileHost is not null) mobileHost.StatusChanged -= Updated; qr.Source = null; showsKey = false; pairingKey.Text = ""; hostId.Text = ""; };
        return rows;
    }

    /// <summary>The pairing QR's side (M/MobileRemoteSettingsView.swift:63).</summary>
    internal const double MobileQrSize = 160;
    /// <summary>The relay field's share of its row beside the default address (the Mac draws it 242 wide of 449).</summary>
    private const double MobileRelayFieldShare = 0.54;
    /// <summary>The direct port field's width (the Mac's is 80).</summary>
    private const double MobileDirectPortWidth = 80;
    private PaneView MobilePane(string id)
    {
        // A remote workspace's pane is another computer's: the phone never reaches it (M/AppStore+MobileRemote.swift mobilePane).
        if (!service.Snapshot.Sessions.Any(p => p.Id == id && p.Kind == "claude") || service.IsRemoteSession(id)) throw new MobileRequestException(404, "Session not found.");
        if (!views.TryGetValue(id, out var pane)) { pane = new(this, id); views[id] = pane; pane.InitRefresher(); }
        return pane;
    }
    /// <summary>Each workspace's styles as a pane with no view of its own reads them, and when they were read.</summary>
    private readonly Dictionary<string, (StyleRegistry? Registry, DateTime Read, bool Reading)> mobileStyleRegistries = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>How long a workspace's styles are taken as read for those panes: approvals and edited style files show within it.</summary>
    private static readonly TimeSpan MobileStyleRefresh = TimeSpan.FromSeconds(5);
    /// <summary>
    /// The style a pane really runs (M/MightyClaude/AppStore+MobileRemote.swift:346-351). A pane with a view answers from it; one
    /// without is never given a view for this — the phone reads every pane's style four times a second — and answers from its
    /// workspace's approved styles, read off the window's thread, none until they have been read.
    /// </summary>
    private string? MobileRunningStyle(RunSession pane)
    {
        if (pane.Kind != "claude") return null;
        if (views.TryGetValue(pane.Id, out var view)) return view.MobileStyleId;
        if (!MobileRemoteSupport.GuidedStylesAvailable(pane.Kind, pane.Provider, MobileRemoteSupport.ViewMode(pane.AgentViewMode)) || pane.MightyStyle is null) return null;
        if (LocalPath(service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId)) is not { } workspace) return null;
        var known = mobileStyleRegistries.GetValueOrDefault(workspace);
        if (!known.Reading && DateTime.UtcNow - known.Read >= MobileStyleRefresh) { mobileStyleRegistries[workspace] = known with { Reading = true }; _ = ReadMobileStyles(workspace, known.Registry); }
        return known.Registry?.Runnable(pane.MightyStyle, pane.MightyStyleHash)?.Id;
    }
    private async Task ReadMobileStyles(string workspace, StyleRegistry? prior)
    {
        var registry = prior;
        try { registry = await Task.Run(() => StyleRegistry.Load(StateDirectory, workspace)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        mobileStyleRegistries[workspace] = (registry, DateTime.UtcNow, false);
        // The panes are read again only when what may run changed.
        static string Approved(StyleRegistry? styles) => string.Join("|", (styles?.Styles ?? []).Where(s => s.Runnable).Select(s => s.Id + ":" + s.Source + ":" + s.Hash));
        if (Approved(registry) != Approved(prior)) mobileRouter?.Changed();
    }
    private MobilePaneExtras MobileExtras(RunSession pane)
        => pane.Kind == "claude" ? MobilePane(pane.Id).MobileSnapshot() : new([]);
    /// <summary>Each pane's structure as the phone was last sent it, built again only when what it draws moved.</summary>
    private readonly MobileStructureCache mobileStructures = new();
    private MobileStructureCache.Built? MobileStructureFor(RunSession pane)
    {
        if (pane.Kind != "claude") return null;
        mobileStructures.Retain(service.Snapshot.Sessions.Select(p => p.Id).ToHashSet());
        return MobilePane(pane.Id).MobileStructureSnapshot();
    }
    private async Task<object?> MobileAction(MobileDesktopAction request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (closing) throw new OperationCanceledException();
        if (request.Action == "create")
        {
            var created = SessionTemplate.Inherit(new RunSession { WorkspaceId = request.SessionId, Provider = request.Body.Text("provider") ?? "claude", Kind = "claude", Title = ProviderCatalog.Name(request.Body.Text("provider") ?? "claude") }, service.Snapshot.Sessions);
            await service.UpdateAsync(s => !s.Workspaces.Any(w => w.Id == request.SessionId && w.Remote is null) ? throw new MobileRequestException(404, "Workspace not found.") : PaneLayout.HasRoom(s) ? s with { Sessions = s.Sessions.Append(created).ToList() } : throw new MobileRequestException(409, Locale.Get("workspace.paneLimit"))); Render(); return created.Id;
        }
        var pane = MobilePane(request.SessionId); var body = request.Body;
        switch (request.Action)
        {
            case "submit": return await pane.MobileSubmit(body.Text("text") ?? "", body.Text("mode"), request.Attachments ?? [], token);
            case "stop": var running = service.IsSessionRunning(request.SessionId); pane.MobileStopQueue(); await service.StopAsync(request.SessionId); return running;
            case "queue-remove": pane.MobileQueueRemove(body.Text("id") ?? ""); return null;
            case "queue-next": return await pane.MobileRunNext(token);
            case "rename": if (body.Text("titleMode") == "auto") await service.SetSessionAutoTitleAsync(request.SessionId); else await service.RenameSessionAsync(request.SessionId, body.Text("title")!.Trim()); Render(); return null;
            // The phone's close never asks, a parent's with open children included (M/AppStore+Delegation.swift:204-206), and goes through the service like the desktop's.
            case "close": pane.MobileCancelQueue(); token.ThrowIfCancellationRequested(); await ClosePane(request.SessionId); return null;
            case "settings": await pane.MobileSettings(body, token); return null;
            case "guided": return await pane.MobileGuided(body, token);
            // An Antigravity card answered on the phone, as its own buttons answer it; what comes back is why nothing happened.
            case "antigravityPermission":
                var card = service.PendingAntigravityCard(request.SessionId, body.Text("requestId") ?? "", body.Text("runId") ?? "") ?? throw new MobileRequestException(409, Locale.Get("remote.error.notPendingPermission"));
                token.ThrowIfCancellationRequested();
                return await pane.AnswerAntigravityPermission(card, Enum.Parse<AntigravityPermissionDecision>(body.Text("decision") ?? "deny", ignoreCase: true));
            case "command":
                if (body.Text("action") == "help") return SlashCommandCatalog.HelpText(pane.MobileSession.Provider);
                if (body.Text("action") == "usage") return pane.MobileUsage();
                if (!await pane.ResetConversationAsync(token)) throw new MobileRequestException(409, "Stop the session before clearing it.");
                return null;
            default: throw new MobileRequestException(404, "Unknown desktop action.");
        }
    }
    private sealed partial class PaneView
    {
        internal string MobileUsage()
        {
            var pane = Session; var usage = pane.SessionUsage;
            var lines = new List<string> { Locale.Get("composer.label.model") + " · " + (usage?.Model ?? pane.Model) };
            if (usage?.ContextUsedTokens is { } context) lines.Add(Locale.Get("composer.sessionInfo.context") + $" · {context:N0} / {usage.ContextWindowTokens:N0}");
            if (usage?.TotalTokens is { } tokens) lines.Add("Tokens · " + tokens.ToString("N0"));
            if (usage?.CostUSD is { } cost) lines.Add(Locale.Get("composer.sessionInfo.cost") + $" · ${cost:0.####}");
            if (pane.RunTiming is { } timing) lines.Add(Locale.Get("composer.sessionInfo.elapsed") + " · " + timing.Label());
            return string.Join("\n", lines);
        }
        internal int MobileQueuedCount => queuedInputs.Items.Count;
        internal RunSession MobileSession => Session;
        internal void MobileQueueRemove(string itemId) { if (!queuedInputs.Remove(itemId)) throw new MobileRequestException(404, "Queued request not found."); RenderQueuedInputs(); RefreshComposerState(); }
        private bool MobileBusy => starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session);
        /// <summary>The phone's stop: the queue is cancelled, unless the turn was over and only background work ran (M/AppStore.swift stop).</summary>
        internal void MobileStopQueue()
        {
            queueKeptOnStop = BackgroundQueuePolicy.StopKeepsQueue(Session.BackgroundWork);
            if (!queueKeptOnStop) { CancelQueueOnStop(); MobileCancelQueue(); } else { composerSubmissionVersion++; queueDrainTimer.Stop(); }
        }
        internal void MobileCancelQueue() { composerSubmissionVersion++; queueDrainTimer.Stop(); queuedInputs.Clear(); RenderQueuedInputs(); RefreshComposerState(); }
        internal async Task<string> MobileSubmit(string text, string? mode, IReadOnlyList<RunAttachment> files, CancellationToken token, bool prepared = false, string? queueId = null)
        {
            token.ThrowIfCancellationRequested(); var pane = Session;
            if (owner.ManualMutationBlockReason(pane) is { } block) throw new MobileRequestException(409, block);
            var request = new StartRunRequest(id, pane.WorkspaceId, pane.Kind, text, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files).Validate();
            if (MobileBusy)
            {
                // The composer's rule (§1.17.4): a plan-mode style never steers into a turn that only waits on background work.
                var plans = LaunchesInPlanMode;
                if (BackgroundQueuePolicy.PhoneSteers(mode, pane.BackgroundWork, plans) && files.Count == 0 && DelegationProviders.CanSteer(pane.Kind, pane.Provider) && await owner.service.TrySteerAsync(id, text)) return "steered";
                token.ThrowIfCancellationRequested(); queuedInputs.Add(text, files, BackgroundQueuePolicy.QueuedOverride(plans), prepared); RenderQueuedInputs(); RefreshComposerState(); if (!MobileBusy) queueDrainTimer.Start(); return "queued";
            }
            var submission = ++composerSubmissionVersion; starting = true; RefreshComposerState();
            try
            {
                // The model list as the CLI has it now comes first; the request takes the pane's model and effort after it.
                await PrepareModels(); token.ThrowIfCancellationRequested();
                if (!QueuePaneAlive || submission != composerSubmissionVersion) throw new OperationCanceledException();
                request = request with { Model = Session.Model, Settings = Session.Settings };
                var submitted = prepared ? text : await PrepareStyleSubmission(text, files.Count > 0); request = await PrepareStyleRunRequest(request with { Input = submitted });
                // A queued item keeps the launch decision made when it was queued (§1.17.4).
                if (queueId is not null && queuedInputs.Items.FirstOrDefault(q => q.Id == queueId) is { } queuedItem) request = request with { PermissionModeOverride = queuedItem.PermissionModeOverride, AntigravityGrant = queuedItem.AntigravityGrant };
                token.ThrowIfCancellationRequested(); if (!QueuePaneAlive || submission != composerSubmissionVersion || queueId is not null && !queuedInputs.Items.Any(q => q.Id == queueId)) throw new OperationCanceledException();
                // A phone send no run took is dropped, and never put in the composer (M/AppStore+Delegation.swift:196-198).
                return await owner.StartFromComposer(request) is DelegationRelease.NotStarted ? throw new MobileRequestException(409, Locale.Get("remote.error.dropped")) : "started";
            }
            finally { starting = false; if (QueuePaneAlive) Refresh(); }
        }
        /// <summary>
        /// The phone's run next (M/AppStore+MobileRemote.swift:688-707): the items delegation holds for the pane go first, all of them
        /// in one run, also during a run; with nothing held, the first queued row runs as before. Held items no run took stay held.
        /// </summary>
        internal async Task<object?> MobileRunNext(CancellationToken token)
        {
            if (owner.service.HeldItems(id).Count > 0)
            {
                if (owner.DelegatedRunBlockReason(Session) is { } blocked) throw new MobileRequestException(409, blocked);
                var release = await owner.service.RunNextAsync(id); token.ThrowIfCancellationRequested();
                if (release is DelegationRelease.NotStarted) throw new MobileRequestException(409, Locale.Get("remote.error.startFailed"));
                if (release is DelegationRelease.Released) { if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); } return null; }
            }
            if (MobileBusy) throw new MobileRequestException(409, "Session is still running.");
            var next = queuedInputs.Items.FirstOrDefault() ?? throw new MobileRequestException(409, "The queue is empty.");
            await MobileSubmit(next.Text, "queue", next.Attachments, token, next.Prepared, next.Id); queuedInputs.Remove(next.Id); RenderQueuedInputs(); RefreshComposerState(); return null;
        }
        /// <summary>
        /// The style this pane is really running: a Claude pane in the Mighty view whose stored style is registered, approved and
        /// unchanged. Anything else answers none, so nothing an approval hid shows through (M/MightyClaude/AppStore+MobileRemote.swift:346-351).
        /// A pane only ever watched from a phone has never read its style, so it is read here: when the stored style moved, and
        /// again when a run starts or ends. A read the phone causes never writes the desktop's error line (M/MightyClaude/AppStore+MobileRemote.swift:649-659).
        /// </summary>
        private RegisteredStyle? MobileActiveStyle
        {
            get
            {
                var pane = Session; if (!MobileRemoteSupport.GuidedStylesAvailable(pane.Kind, pane.Provider, MobileRemoteSupport.ViewMode(pane.AgentViewMode))) return null;
                var identity = pane.MightyStyle + "|" + pane.MightyStyleHash + "|" + pane.MightyStyleSince + "|";
                if (!styleLoading && (loadedStyleKey is not { } loaded || !loaded.StartsWith(identity, StringComparison.Ordinal) || !loaded.EndsWith("|" + pane.Status, StringComparison.Ordinal))) _ = LoadStyles(quiet: true);
                return activeStyle is { } style && style.Id == pane.MightyStyle ? style : null;
            }
        }
        internal string? MobileStyleId => MobileActiveStyle?.Id;
        /// <summary>
        /// The pickers the phone shows, from the same sources as the composer's menus (M/MightyClaude/AppStore+MobileRemote.swift:547-569).
        /// <paramref name="model"/> and <paramref name="viewMode"/> let a request that also changes them be checked against the pane
        /// it is switching to. Styles are offered only to a Claude pane in the Mighty view; elsewhere "cli" is the one choice.
        /// </summary>
        private MobileSettingsOptions MobileOptions(RunSession pane, string? model = null, string? viewMode = null)
        {
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            IReadOnlyList<RegisteredStyle> styles = MobileRemoteSupport.GuidedStylesAvailable(pane.Kind, pane.Provider, viewMode ?? MobileRemoteSupport.ViewMode(pane.AgentViewMode)) && LocalPath(Workspace) is { } folder ? (styleRegistry ??= StyleRegistry.Load(owner.StateDirectory, folder)).Styles : [];
            return new(ModelLabel.PickerOptions(pane, catalog).Select(m => new MobileOption(m.Value, m.DisplayName)).ToArray(), (Capabilities.PermissionModes ?? []).Select(m => new MobileOption(m, PermissionLabel(pane.Provider, m))).ToArray(),
                MobileRemoteSupport.EffortOptions(Capabilities.Effort, ProviderCatalog.Efforts(pane.Provider, model ?? pane.Model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot))), MobileRemoteSupport.LegacyStyleOptions(styles), MobileRemoteSupport.StyleOptions(styles));
        }
        /// <summary>
        /// What the pane's revision is read from beside its record (M/MightyClaude/AppStore+MobileRemote.swift:353-363, 488-529): whether
        /// the phone may use its pickers, what they would offer, and the Mighty payload's identity — its runs and blocks, the
        /// answered plans, and the running style with its phase, widgets and prerequisites. Never the streamed text.
        /// </summary>
        internal MobilePaneDigest MobileDigest()
        {
            var pane = Session; var offered = MobileOptions(pane); var options = new HashCode();
            foreach (var item in offered.Models) { options.Add(item.Id); options.Add(item.Label); }
            foreach (var item in offered.PermissionModes) options.Add(item.Id);
            foreach (var item in offered.MightyStyles) options.Add(item.Id);
            foreach (var item in offered.Styles) { options.Add(item.Id); options.Add(item.Source); }
            foreach (var item in offered.Efforts ?? []) options.Add(item.Id);
            var mighty = new HashCode(); mighty.Add(MobileMightySupport.Digest(pane)); mighty.Add(pane.PlanHistory?.Count); mighty.Add(pane.PlanHistory?.LastOrDefault()?.Outcome);
            if (MobileActiveStyle is { } style)
            {
                mighty.Add(style.Id); mighty.Add(style.Hash); mighty.Add(GuidedPhase(style)?.Id); mighty.Add(pane.Status == "running"); mighty.Add(stylePrerequisites?.Ready);
                foreach (var missing in stylePrerequisites?.Missing ?? []) mighty.Add(missing);
                mighty.Add(JsonSerializer.Serialize(LiveStyleReading(style).Widgets.Select(StylePresentation.Payload), Wire.Json));
            }
            return new(!MobileBusy, options.ToHashCode(), mighty.ToHashCode());
        }
        internal MobilePaneExtras MobileSnapshot()
        {
            var pane = Session; var style = MobileActiveStyle; var styleId = style?.Id ?? MobileWire.CliStyle; var options = MobileOptions(pane);
            var settings = new MobileSettings(!MobileBusy, pane.Model, pane.Settings.PermissionMode, options.Efforts is null ? null : pane.Settings.Effort, MobileRemoteSupport.ViewMode(pane.AgentViewMode), MobileRemoteSupport.Style(styleId), styleId, options);
            // Only a pane that really runs an approved style carries a panel, the payload an older phone reads, and the
            // style's own names on its requests (M/MightyClaude/AppStore+MobileRemote.swift:410-439).
            var styles = style is null ? null : styleRegistry?.Styles;
            var legacy = style is null ? null : MobileLegacyStyleAdapter.Payload(style, GuidedPhase(style), stylePrerequisites?.Ready ?? false);
            var mighty = new { style = MobileRemoteSupport.Style(styleId), styleId, runs = MobileMightySupport.Runs(MobileMightySupport.SavedRuns(pane), styles is null ? null : input => StylePresentation.RequestPrefix(styles, input)), panel = MobileStylePanel(style), ouroboros = legacy };
            var statusLine = Refresher?.Result is { } statusResult ? new { lines = statusResult.Lines.Select(line => line.Select(s => new { s.Text, fg = Terminal(s.Foreground) is { } color ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : null, bold = s.Bold })) } : null;
            // The rows delegation holds for the pane come first, as on the desktop (M/AppStore+MobileRemote.swift:398).
            return new(DelegationQueueRows.MobileItems(owner.service.HeldItems(id), queuedInputs.Items), settings, mighty, statusLine);
        }
        /// <summary>The style panel the phone draws, in the shared golden shape (M/MightyCore/Styles/StylePanelProjection.swift).</summary>
        private object? MobileStylePanel(RegisteredStyle? chosen) => chosen is { } style
            ? StylePanelProjection.Make(style, Session, styleGroup, NoCapabilityStates, stylePrerequisites ?? new StylePrerequisiteResult(false, [], null, null), MobileBusy, LiveStyleReading(style))
            : null;
        internal async Task MobileSettings(JsonElement value, CancellationToken token)
        {
            if (MobileBusy) throw new MobileRequestException(409, "Stop the session before changing settings.");
            if (!value.EnumerateObject().Any() || value.EnumerateObject().Any(p => p.Name is not ("model" or "permissionMode" or "effort" or "agentViewMode" or "mightyStyle" or "styleId") || p.Value.ValueKind != JsonValueKind.String)) throw new MobileRequestException(400, "Unknown setting.");
            var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider); var model = value.Text("model") ?? pane.Model; var mode = value.Text("permissionMode") ?? pane.Settings.PermissionMode; var effort = value.Text("effort") ?? pane.Settings.Effort;
            if (!ModelLabel.PickerOptions(pane, catalog).Any(m => m.Value == model) || !(Capabilities.PermissionModes ?? []).Contains(mode) || effort != "default" && !ProviderCatalog.Efforts(pane.Provider, model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot)).Contains(effort)) throw new MobileRequestException(400, "Unsupported model, permission, or effort.");
            var view = value.Text("agentViewMode") ?? (pane.AgentViewMode == "mighty" ? "mighty" : "plain"); if (view is not ("plain" or "mighty")) throw new MobileRequestException(400, "Unsupported view.");
            // What this sender was last told, read before anything is changed: a pane in the plain view reports "cli" whatever it
            // has stored, so a phone handing that back while it turns the Mighty view on leaves the stored style alone
            // (M/MightyClaude/AppStore+MobileRemote.swift:783-795). The style is judged against the view this same request asks for.
            var shown = MobileStyleId ?? MobileWire.CliStyle; var requested = value.Text("styleId") ?? value.Text("mightyStyle"); var offered = MobileOptions(pane, model, view);
            if (requested is not null && !(value.Text("styleId") is not null ? offered.Styles.Any(s => s.Id == requested) : offered.MightyStyles.Any(s => s.Id == requested))) throw new MobileRequestException(400, Locale.Get("remote.error.unknownStyle"));
            // A style other than the one shown is picked anew, so a stored one the pane did not run is approved again as it is now.
            var style = MobileRemoteSupport.StyleAfter(requested, shown, pane.MightyStyle); var changes = requested is not null && requested != shown; RegisteredStyle? selected = null;
            if (changes && style is not null)
            {
                var folder = LocalFolder(Workspace); var registry = await Task.Run(() => StyleRegistry.Load(owner.StateDirectory, folder), token); selected = registry.Styles.FirstOrDefault(s => s.Id == style && s.Runnable) ?? throw new MobileRequestException(400, Locale.Get("remote.error.unknownStyle"));
                if (!StyleRegistry.Unchanged(selected)) throw new MobileRequestException(409, "Style changed.");
            }
            token.ThrowIfCancellationRequested(); if (MobileBusy || Session.Provider != pane.Provider || Session.Model != pane.Model || Session.MightyStyle != pane.MightyStyle || Session.Settings != pane.Settings) throw new MobileRequestException(409, "The pane changed.");
            await Change(p => p with { Model = model, Settings = p.Settings with { PermissionMode = mode, Effort = effort, NetworkAccess = mode is ("acceptEdits" or "onRequest") && p.Settings.NetworkAccess }, AgentViewMode = view == "mighty" ? "mighty" : "default", MightyStyle = changes ? style : p.MightyStyle, MightyStyleHash = changes ? selected?.Hash : p.MightyStyleHash, MightyStyleSince = !changes || p.MightyStyle == style ? p.MightyStyleSince : null }); loadedStyleKey = null; await LoadStyles(quiet: true); Refresh();
        }
        /// <summary>
        /// A style's button pressed on the phone (M/MightyClaude/AppStore+MobileRemote.swift:634-647). The prompt is built by the
        /// style's own action and then travels the ordinary send path, so into a running pane it steers or queues, and
        /// <c>accepted</c> means what it does there. An unregistered style and an unapproved one answer alike.
        /// </summary>
        internal async Task<string> MobileGuided(JsonElement body, CancellationToken token)
        {
            var pane = Session; var request = MobileRemoteSupport.GuidedRequest(body);
            var folder = LocalFolder(Workspace); var registry = await Task.Run(() => StyleRegistry.Load(owner.StateDirectory, folder), token);
            if (pane.Provider != "claude" || registry.Styles.FirstOrDefault(s => s.Id == request.Style) is not { Runnable: true }) throw new MobileRequestException(400, Locale.Get("remote.error.unknownStyle"));
            if (pane.AgentViewMode != "mighty" || request.Style != pane.MightyStyle || registry.Runnable(pane.MightyStyle, pane.MightyStyleHash) is not { } style) throw new MobileRequestException(409, Locale.Get("remote.error.otherStyle", new Dictionary<string, string> { ["style"] = request.Style }));
            var action = style.Manifest.Actions.FirstOrDefault(a => a.Id == request.Action) ?? throw new MobileRequestException(400, Locale.Get("remote.error.unknownSkill"));
            if (action.RequiresText && request.Text.Length == 0) throw new MobileRequestException(400, "This action needs text.");
            token.ThrowIfCancellationRequested(); if (Session.MightyStyle != request.Style || !StyleRegistry.Unchanged(style)) throw new MobileRequestException(409, "Style changed.");
            // An action reads its argument up to the first line break, so the phone's text is folded to one line first.
            return await MobileSubmit(action.Prompt(action.TakesText ? MobileRemoteSupport.SingleLine(request.Text) : ""), null, [], token, true);
        }
    }
}
