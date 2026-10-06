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
    private readonly DispatcherTimer mobileNotify = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long mobileNotified;
    private bool mobileNotifying;
    private async Task InitializeMobileRemote()
    {
        if (options.SmokeTest || mobileHost is not null) return;
        var directory = Path.Combine(StateDirectory, "mobile-remote");
        mobileRouter = new(service, directory, Environment.MachineName, MobileAction, MobileExtras) { AppVersion = AppVersionText, ImagePreview = MakeMobileImagePreview, QueueCount = id => views.TryGetValue(id, out var pane) ? pane.MobileQueuedCount : 0 };
        mobileHost = new(directory, Environment.MachineName, MobileDesktopRouter.Capabilities, MobileRoute) { AppVersion = AppVersionText };
        mobileRouter.HostId = mobileHost.HostId;
        await InitializeScreenShare(directory);
        mobileNotify.Tick += async (_, _) =>
        {
            if (closing || mobileNotifying || mobileRouter is null || mobileHost is null || mobileRouter.Revision == mobileNotified) return;
            mobileNotifying = true;
            try { mobileNotified = mobileRouter.Revision; await mobileHost.NotifyAsync("state", mobileNotified); foreach (var pane in service.Snapshot.Sessions.Where(s => s.Kind == "claude")) await mobileHost.NotifyAsync("session:" + pane.Id, mobileNotified); }
            finally { mobileNotifying = false; }
        };
        mobileNotify.Start(); await mobileHost.ApplyAsync(service.Snapshot.MobileRemote);
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
        mobileNotify.Stop(); await ShutdownScreenShare(); if (mobileHost is not null) await mobileHost.DisposeAsync(); mobileHost = null; mobileRouter?.Dispose(); mobileRouter = null;
    }
    // 모바일 리모트 (M/MobileRemoteSettingsView.swift:17-110): the allow switch over its explanation; the
    // relay's label, the address used while the field is empty, the field and the apply button; the four
    // steps; the state dot and words beside the reconnect button; the pairing QR (160) beside the host id
    // under its label, the key and the link and key buttons; the legacy-apps switch; then the paired
    // phones, a row each. The switches take effect as they are switched and the apply button is the
    // address's own, as on the Mac.
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
        var status = SettingsText("", 11, DesignToken.Ink2);
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
        // An address as it is saved: the same address typed without its scheme is not another address.
        static string Same(string address) => address.Length == 0 ? "" : MobileRelayHost.NormalizeRelay(address) ?? address;
        // On only for another address that can be used; an empty field goes back to the address shown before it.
        void ShowApply() { var typed = relay.Text.Trim(); apply.IsEnabled = !writing && !options.SmokeTest && Same(typed) != Same(savedRelay) && (typed.Length == 0 || MobileRelayHost.NormalizeRelay(typed) is not null); }
        // The switches and the button as the saved settings have them.
        void ShowControls()
        {
            var saved = service.Snapshot.MobileRemote; savedRelay = saved.RelayURL;
            showing = true; enabled.IsChecked = saved.Enabled; legacy.IsChecked = saved.AllowLegacyPhones; showing = false;
            enabled.IsEnabled = legacy.IsEnabled = !options.SmokeTest; ShowApply();
        }
        Task Write(Func<MobileRemoteSettings, MobileRemoteSettings> change) => Act(async () =>
        {
            // A switch pressed while the last change is still being applied waits for it: the switches stay as they were
            // pressed, and keep the keyboard, and what they then say is applied next (below).
            if (options.SmokeTest || writing) return;
            writing = true; ShowApply();
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
                bool wantsOn = enabled.IsChecked == true, wantsLegacy = legacy.IsChecked == true;
                if (meant is not null && !closing && (wantsOn != meant.Enabled || wantsLegacy != meant.AllowLegacyPhones)) _ = Write(next => next with { Enabled = wantsOn, AllowLegacyPhones = wantsLegacy });
                else ShowControls();
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
        enabled.Checked += async (_, _) => { if (!showing) await Write(saved => saved with { Enabled = true }); };
        enabled.Unchecked += async (_, _) => { if (!showing) await Write(saved => saved with { Enabled = false }); };
        legacy.Checked += async (_, _) => { if (!showing) await Write(saved => saved with { AllowLegacyPhones = true }); };
        legacy.Unchecked += async (_, _) => { if (!showing) await Write(saved => saved with { AllowLegacyPhones = false }); };
        relay.TextChanged += (_, _) => ShowApply();
        // Enter in the field applies the address, as the Mac's field does on submit (M/MobileRemoteSettingsView.swift:31).
        relay.KeyDown += async (_, args) => { if (args.Key != Windows.System.VirtualKey.Enter || !apply.IsEnabled) return; args.Handled = true; await ApplyRelay(); };

        SettingsRow(rows, SettingsLabeled(SettingsTitled(allowLabel, Locale.Get("windows.mobile.relayDescription")), enabled, top: true));
        // The form draws the field's own title — the address used while the field is empty — as a label before it,
        // and gives the field 0.54 of what the row's label and button leave (screens/10-settings-mobile-redacted-dark.webp).
        var relayRow = new Grid { ColumnSpacing = 8 };
        relayRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); relayRow.ColumnDefinitions.Add(new() { Width = new(1 - MobileRelayFieldShare, GridUnitType.Star) });
        relayRow.ColumnDefinitions.Add(new() { Width = new(MobileRelayFieldShare, GridUnitType.Star) }); relayRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var relayDefault = SettingsText(MobileRelayHost.DefaultRelay, 11, mono: true); relayDefault.TextWrapping = TextWrapping.NoWrap; relayDefault.TextTrimming = TextTrimming.CharacterEllipsis;
        relayRow.Children.Add(SettingsText(relayLabel)); Grid.SetColumn(relayDefault, 1); relayRow.Children.Add(relayDefault);
        Grid.SetColumn(relay, 2); relayRow.Children.Add(relay); Grid.SetColumn(apply, 3); relayRow.Children.Add(apply);
        SettingsRow(rows, relayRow);
        // The four steps (M/MobileRemoteSettingsView.swift:175-191): the number in a 14-wide column, right-aligned, 5 from its sentence.
        var guide = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetAutomationId(guide, "settings-mobile-guide");
        var steps = new[] { Locale.Get("settings.mobileRemote.guide.step1"), Locale.Get("settings.mobileRemote.guide.step2"), Locale.Get("settings.mobileRemote.guide.step3"), Locale.Get("settings.mobileRemote.guide.step4") };
        for (var step = 0; step < steps.Length; step++)
        {
            var line = new Grid { ColumnSpacing = 5 };
            line.ColumnDefinitions.Add(new() { Width = new(14) }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var number = SettingsText(step + 1 + ".", 11, DesignToken.Ink2); number.HorizontalAlignment = HorizontalAlignment.Right; number.VerticalAlignment = VerticalAlignment.Top;
            line.Children.Add(number);
            var sentence = SettingsText(steps[step], 11, DesignToken.Ink2); Grid.SetColumn(sentence, 1); line.Children.Add(sentence);
            guide.Children.Add(line);
        }
        SettingsRow(rows, guide);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        state.Children.Add(dot); state.Children.Add(status);
        SettingsRow(rows, SettingsLabeled(state, reconnect));

        var copy = SettingsPush(Button(Locale.Get("settings.mobileRemote.copyLinkButton"), () => { if (mobileHost is not null && service.Snapshot.MobileRemote.Enabled) { var package = new DataPackage(); package.SetText(mobileHost.PairingUrl); Clipboard.SetContent(package); } return Task.CompletedTask; }), SettingsControlSize.Small);
        copy.Content = SettingsGlyphLabel("", Locale.Get("settings.mobileRemote.copyLinkButton"), 11);
        var regenerate = SettingsPush(SafeButton(Locale.Get("settings.mobileRemote.regenerateKeyButton"), () => Revoke(null)), SettingsControlSize.Small, destructive: true);
        regenerate.Content = SettingsGlyphLabel("", Locale.Get("settings.mobileRemote.regenerateKeyButton"), 11);
        // On Windows a new key unpairs every phone; the button says so before it asks.
        ToolTipService.SetToolTip(regenerate, Locale.Get("windows.mobile.rotationDescription"));
        var pairing = new StackPanel { Spacing = 6 };
        pairing.Children.Add(SettingsText(Locale.Get("settings.mobileRemote.scanInstruction"), 11, DesignToken.Ink2));
        // The host id is too long for its label's line, so the form sets it under the label; the key stays beside its own.
        pairing.Children.Add(SettingsText(hostIdLabel)); pairing.Children.Add(hostId);
        var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        key.Children.Add(pairingKey); key.Children.Add(showKey);
        pairing.Children.Add(SettingsLabeled(SettingsText(keyLabel), key));
        var pairingButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pairingButtons.Children.Add(copy); pairingButtons.Children.Add(regenerate);
        pairing.Children.Add(pairingButtons);
        // The QR on white at radius 8, 16 from the pairing words (M/MobileRemoteSettingsView.swift:61-65).
        var identity = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 4, 0, 4) };
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
            var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get(id is null ? "settings.mobileRemote.regenerateKeyButton" : "settings.mobileRemote.revokeDialogTitle"), Content = Locale.Get("windows.mobile.rotationConfirm"), PrimaryButtonText = Locale.Get("settings.mobileRemote.revokeConfirmButton"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
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
        }
        void HidePairing() { identityRow.Visibility = devicesTitleRow.Visibility = Visibility.Collapsed; ReplaceSettingsRows(rows, deviceRow, []); qr.Source = null; }
        async Task Refresh()
        {
            var showsIdentity = service.Snapshot.MobileRemote.Enabled && mobileHost is not null;
            identityRow.Visibility = showsIdentity ? Visibility.Visible : Visibility.Collapsed;
            reconnect.Visibility = service.Snapshot.MobileRemote.Enabled ? Visibility.Visible : Visibility.Collapsed; reconnect.IsEnabled = mobileHost is not null && !options.SmokeTest;
            if (!showsIdentity) showsKey = false;
            RefreshPairingText(); ShowState();
            if (!showsIdentity) { HidePairing(); return; }
            var link = mobileHost!.PairingUrl; var png = PngByteQRCodeHelper.GetQRCode(link, QRCodeGenerator.ECCLevel.M, 6);
            using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); } stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); if (mobileHost?.PairingUrl != link) return; qr.Source = bitmap;
            // The paired phones, a row each (M/MobileRemoteSettingsView.swift:128-148): the symbol, the name over when it was seen, the unpair button.
            var contents = new List<FrameworkElement>();
            foreach (var device in mobileHost.Devices)
            {
                var row = new Grid { ColumnSpacing = 8 };
                AutomationProperties.SetAutomationId(row, "settings-mobile-device-" + device.Id);
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(SettingsSymbol(device.Legacy ? "" : "", 14, DesignToken.Ink2));
                var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(SettingsText(device.Name + (device.Legacy ? " · Legacy" : ""), 12));
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
        rows.Loaded += async (_, _) => { if (options.SmokeTest) { enabled.IsEnabled = relay.IsEnabled = legacy.IsEnabled = apply.IsEnabled = reconnect.IsEnabled = false; HidePairing(); RefreshPairingText(); return; } await Act(async () => { await InitializeMobileRemote(); if (closing || !rows.IsLoaded || mobileHost is null) return; mobileHost.StatusChanged += Updated; await Refresh(); }); };
        rows.Unloaded += (_, _) => { if (mobileHost is not null) mobileHost.StatusChanged -= Updated; qr.Source = null; showsKey = false; pairingKey.Text = ""; hostId.Text = ""; };
        return rows;
    }

    /// <summary>The pairing QR's side (M/MobileRemoteSettingsView.swift:63).</summary>
    internal const double MobileQrSize = 160;
    /// <summary>The relay field's share of its row beside the default address (the Mac draws it 242 wide of 449).</summary>
    private const double MobileRelayFieldShare = 0.54;
    private PaneView MobilePane(string id)
    {
        if (!service.Snapshot.Sessions.Any(p => p.Id == id && p.Kind == "claude")) throw new MobileRequestException(404, "Session not found.");
        if (!views.TryGetValue(id, out var pane)) { pane = new(this, id); views[id] = pane; pane.InitRefresher(); }
        return pane;
    }
    private MobilePaneExtras MobileExtras(RunSession pane)
        => pane.Kind == "claude" ? MobilePane(pane.Id).MobileSnapshot() : new([]);
    private async Task<object?> MobileAction(MobileDesktopAction request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (closing) throw new OperationCanceledException();
        if (request.Action == "create")
        {
            var created = SessionTemplate.Inherit(new RunSession { WorkspaceId = request.SessionId, Provider = request.Body.Text("provider") ?? "claude", Kind = "claude", Title = ProviderCatalog.Name(request.Body.Text("provider") ?? "claude") }, service.Snapshot.Sessions);
            await service.UpdateAsync(s => s.Workspaces.Any(w => w.Id == request.SessionId) ? s with { Sessions = s.Sessions.Append(created).ToList() } : throw new MobileRequestException(404, "Workspace not found.")); Render(); return created.Id;
        }
        var pane = MobilePane(request.SessionId); var body = request.Body;
        switch (request.Action)
        {
            case "submit": return await pane.MobileSubmit(body.Text("text") ?? "", body.Text("mode"), request.Attachments ?? [], token);
            case "stop": var running = service.IsSessionRunning(request.SessionId); pane.MobileStopQueue(); await service.StopAsync(request.SessionId); return running;
            case "queue-remove": pane.MobileQueueRemove(body.Text("id") ?? ""); return null;
            case "queue-next": return await pane.MobileRunNext(token);
            case "rename": if (body.Text("titleMode") == "auto") await service.SetSessionAutoTitleAsync(request.SessionId); else await service.RenameSessionAsync(request.SessionId, body.Text("title")!.Trim()); Render(); return null;
            case "close": pane.MobileCancelQueue(); await service.StopAsync(request.SessionId); token.ThrowIfCancellationRequested(); await service.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != request.SessionId).ToList() }); Render(); return null;
            case "settings": await pane.MobileSettings(body, token); return null;
            case "guided": return await pane.MobileGuided(body, token);
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
            if (!queueKeptOnStop) MobileCancelQueue(); else { composerSubmissionVersion++; queueDrainTimer.Stop(); }
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
                if (BackgroundQueuePolicy.PhoneSteers(mode, pane.BackgroundWork, plans) && files.Count == 0 && pane.Provider == "claude" && await owner.service.TrySteerAsync(id, text)) return "steered";
                token.ThrowIfCancellationRequested(); queuedInputs.Add(text, files, BackgroundQueuePolicy.QueuedOverride(plans)); RenderQueuedInputs(); RefreshComposerState(); if (!MobileBusy) queueDrainTimer.Start(); return "queued";
            }
            var submission = ++composerSubmissionVersion; starting = true; RefreshComposerState();
            try
            {
                var submitted = prepared ? text : await PrepareStyleSubmission(text, files.Count > 0); request = await PrepareStyleRunRequest(request with { Input = submitted });
                // A queued item keeps the launch decision made when it was queued (§1.17.4).
                if (queueId is not null && queuedInputs.Items.FirstOrDefault(q => q.Id == queueId) is { } queuedItem) request = request with { PermissionModeOverride = queuedItem.PermissionModeOverride };
                token.ThrowIfCancellationRequested(); if (!QueuePaneAlive || submission != composerSubmissionVersion || queueId is not null && !queuedInputs.Items.Any(q => q.Id == queueId)) throw new OperationCanceledException(); await owner.StartFromComposer(request); return "started";
            }
            finally { starting = false; if (QueuePaneAlive) Refresh(); }
        }
        internal async Task<object?> MobileRunNext(CancellationToken token)
        {
            if (MobileBusy) throw new MobileRequestException(409, "Session is still running.");
            var next = queuedInputs.Items.FirstOrDefault() ?? throw new MobileRequestException(409, "The queue is empty.");
            await MobileSubmit(next.Text, "queue", next.Attachments, token, queueId: next.Id); queuedInputs.Remove(next.Id); RenderQueuedInputs(); RefreshComposerState(); return null;
        }
        internal MobilePaneExtras MobileSnapshot()
        {
            var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var registered = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot);
            var registry = styleRegistry ??= StyleRegistry.Load(owner.StateDirectory, Workspace.Path); var styles = (registry?.Styles ?? []).Where(s => s.Runnable).Select(s => new { id = s.Id, label = s.Manifest.Name, source = s.Source }).ToArray();
            var settings = new { editable = !MobileBusy, model = pane.Model, permissionMode = pane.Settings.PermissionMode, effort = pane.Settings.Effort, agentViewMode = pane.AgentViewMode == "mighty" ? "mighty" : "plain", mightyStyle = pane.MightyStyle is "ouroboros" or "paperthin" ? pane.MightyStyle : "cli", styleId = pane.MightyStyle ?? "cli", options = new { models = ModelLabel.PickerOptions(pane, catalog).Select(m => new { id = m.Value, label = m.DisplayName }), permissionModes = (Capabilities.PermissionModes ?? []).Select(m => new { id = m, label = PermissionLabel(pane.Provider, m) }), efforts = new[] { "default" }.Concat(ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registered)).Select(m => new { id = m, label = m }), mightyStyles = new[] { new { id = "cli", label = "CLI" } }.Concat(styles.Where(s => s.id is "ouroboros" or "paperthin").Select(s => new { s.id, s.label })), styles } };
            var mighty = new { style = pane.MightyStyle is "ouroboros" or "paperthin" ? pane.MightyStyle : "cli", styleId = pane.MightyStyle ?? "cli", runs = (pane.GraphRuns ?? []).TakeLast(8).Select(run => new { run.Id, run.Input, run.Status, result = run.FinalOutput, blocks = new[] { new { id = run.Id + ":main", kind = "main", title = "Main", status = run.Status, summary = run.Input, output = string.Join("\n", run.RootEntries.Where(e => e.Kind is "assistant" or "output").Select(e => e.Text)), nodeModelLabel = run.NodeModelLabel } }.Concat(run.Agents.Select(a => new { id = a.Id, kind = a.Kind ?? "agent", title = a.Title, status = a.Status, summary = a.Input, output = string.Join("\n", a.Entries.Where(e => e.Kind is "assistant" or "output").Select(e => e.Text)), nodeModelLabel = (string?)null })) }), panel = MobileStylePanel() };
            var statusLine = Refresher?.Result is { } statusResult ? new { lines = statusResult.Lines.Select(line => line.Select(s => new { s.Text, fg = Terminal(s.Foreground) is { } color ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : null, bold = s.Bold })) } : null;
            return new(queuedInputs.Items.Select(q => new MobileQueuedItem(q.Id, q.Text)).ToArray(), settings, mighty, statusLine);
        }
        private object? MobileStylePanel()
        {
            if (activeStyle is not { } style) return null; var phase = GuidedPhase(style); var job = style.Evaluator.JobOpen(Session);
            var group = style.Manifest.Groups.FirstOrDefault(g=>g.Id==styleGroup) ?? style.Evaluator.InitialGroup(styleCapabilities); var next = style.Evaluator.VisibleActions(phase, group, MobileBusy, job).Select(a => a.Id).ToArray();
            var phases = style.Manifest.Phases.OrderBy(p => p.Order).ToArray();
            return new {
                style = new { id = style.Id, name = style.Manifest.Name, source = style.Source, icon = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"icon"), tint = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"tint") },
                phase = phase is null ? null : new { phase.Id, phase.Title, index = Array.FindIndex(phases, p => p.Id == phase.Id), count = phases.Length },
                groups = style.Manifest.Groups.Select(g => new { g.Id, g.Title, g.Axis, g.Question, selected = g.Id == group?.Id, g.Actions }),
                actions = style.Manifest.Actions.Select(a => new { a.Id, a.Title, a.Help, a.Icon, a.Glyph, a.Scope, a.TakesText, a.RequiresText, flags = a.Flags ?? [], prominent = next.FirstOrDefault() == a.Id }),
                next, recommended = style.Evaluator.RecommendedAction(styleCapabilities), attachments = styleCapabilityFiles.Select(file => new { id = StyleText.Safe(Path.GetFileName(file.Path),120), title = file.Title, detail = file.Detail, readOnly = file.ReadOnly }),
                setup = new { ready = stylePrerequisites?.Ready ?? false, missing = stylePrerequisites?.Missing ?? [], hint = stylePrerequisites?.Hint, installCommand = stylePrerequisites?.InstallCommand },
                guidance = style.Evaluator.Guidance(phase, MobileBusy, job), presentation = new { headerTitle = style.Manifest.Name + (phase is null ? "" : " · " + phase.Title), source = style.Source, icon = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"icon"), tint = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"tint") }, widgets = LiveStyleReading(style).Widgets is {Count:>0} live ? live.Select(StylePresentation.Payload).ToArray() : null
            };
        }
        internal async Task MobileSettings(JsonElement value, CancellationToken token)
        {
            if (MobileBusy) throw new MobileRequestException(409, "Stop the session before changing settings.");
            if (!value.EnumerateObject().Any() || value.EnumerateObject().Any(p => p.Name is not ("model" or "permissionMode" or "effort" or "agentViewMode" or "mightyStyle" or "styleId") || p.Value.ValueKind != JsonValueKind.String)) throw new MobileRequestException(400, "Unknown setting.");
            var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider); var model = value.Text("model") ?? pane.Model; var mode = value.Text("permissionMode") ?? pane.Settings.PermissionMode; var effort = value.Text("effort") ?? pane.Settings.Effort;
            if (!ModelLabel.PickerOptions(pane, catalog).Any(m => m.Value == model) || !(Capabilities.PermissionModes ?? []).Contains(mode) || effort != "default" && !ProviderCatalog.Efforts(pane.Provider, model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot)).Contains(effort)) throw new MobileRequestException(400, "Unsupported model, permission, or effort.");
            var view = value.Text("agentViewMode") ?? (pane.AgentViewMode == "mighty" ? "mighty" : "plain"); if (view is not ("plain" or "mighty")) throw new MobileRequestException(400, "Unsupported view.");
            var styleId = value.Text("styleId") ?? value.Text("mightyStyle") ?? pane.MightyStyle ?? "cli"; RegisteredStyle? selected = null;
            if (styleId != "cli")
            {
                if (pane.Provider != "claude") throw new MobileRequestException(400, "Styles require Claude.");
                var registry = await Task.Run(() => StyleRegistry.Load(owner.StateDirectory, Workspace.Path), token); selected = registry.Styles.FirstOrDefault(s => s.Id == styleId && s.Runnable) ?? throw new MobileRequestException(400, "Unknown or unapproved style.");
                if (!StyleRegistry.Unchanged(selected)) throw new MobileRequestException(409, "Style changed.");
            }
            token.ThrowIfCancellationRequested(); if (MobileBusy || Session.Provider != pane.Provider || Session.Model != pane.Model || Session.MightyStyle != pane.MightyStyle || Session.Settings != pane.Settings) throw new MobileRequestException(409, "The pane changed.");
            await Change(p => p with { Model = model, Settings = p.Settings with { PermissionMode = mode, Effort = effort, NetworkAccess = mode is ("acceptEdits" or "onRequest") && p.Settings.NetworkAccess }, AgentViewMode = view == "mighty" ? "mighty" : "default", MightyStyle = selected?.Id, MightyStyleHash = selected?.Hash, MightyStyleSince = p.MightyStyle == selected?.Id ? p.MightyStyleSince : null }); loadedStyleKey = null; await LoadStyles(); Refresh();
        }
        internal async Task<string> MobileGuided(JsonElement body, CancellationToken token)
        {
            var pane = Session; var chosen = body.Text("styleId") ?? body.Text("style"); var actionId = body.Text("actionId") ?? body.Text("skill"); var text = body.Text("text") ?? "";
            if (MobileBusy || pane.Provider != "claude" || pane.AgentViewMode != "mighty" || chosen != pane.MightyStyle || text.Length > 32768) throw new MobileRequestException(409, "The guided pane is unavailable.");
            var registry = await Task.Run(() => StyleRegistry.Load(owner.StateDirectory, Workspace.Path), token); var style = registry.Runnable(chosen, pane.MightyStyleHash) ?? throw new MobileRequestException(400, "Unknown or unapproved style.");
            var action = style.Manifest.Actions.FirstOrDefault(a => a.Id == actionId) ?? throw new MobileRequestException(400, "Unknown guided action.");
            if (action.RequiresText && string.IsNullOrWhiteSpace(text)) throw new MobileRequestException(400, "This action needs text.");
            if (!StylePrerequisites.Read(style.Manifest, Workspace.Path).Ready) throw new MobileRequestException(409, "Install the style prerequisites on the desktop.");
            token.ThrowIfCancellationRequested(); if (MobileBusy || Session.MightyStyle != chosen || !StyleRegistry.Unchanged(style)) throw new MobileRequestException(409, "Style changed.");
            return await MobileSubmit(action.Prompt(text), "queue", [], token, true);
        }
    }
}
