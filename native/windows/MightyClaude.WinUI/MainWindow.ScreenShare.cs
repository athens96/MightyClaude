using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private WindowsScreenPlatform? screenPlatform;
    private ScreenShareReferenceSceneWindow? screenScene;
    private ScreenShareHub? screenHub;
    private Border? screenBanner;
    private string screenUnavailable = "";
    private async Task InitializeScreenShare(string directory)
    {
        if (options.SmokeTest || screenPlatform is not null || mobileHost is null || mobileRouter is null) return;
        screenPlatform = new(DispatcherQueue, root);
        try { await screenPlatform.InitializeAsync(directory); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { screenUnavailable = Locale.Get("windows.screenShare.initializeFailed"); }
        screenHub = new(directory, screenPlatform, (device, message) => mobileHost?.SendToDeviceAsync(device, message) ?? Task.CompletedTask, ConfirmScreenKey, device => mobileHost?.IsTrustedDevice(device) == true);
        screenPlatform.Hub = screenHub;
        mobileHost.ScreenSignal = screenHub.SignalAsync; mobileHost.ScreenDisconnected = screenHub.DeviceDisconnected; mobileHost.TurnCredentials = screenHub.SetIceServersAsync;
        mobileHost.ExtraCapabilities = () => screenPlatform?.Available == true ? ["screenShare"] : [];
        mobileRouter.ExtraCapabilities = mobileHost.ExtraCapabilities; mobileRouter.ScreenRoute = screenHub.RouteAsync;
        screenHub.Changed += ScreenChanged; screenPlatform.KillRequested += () => _ = screenHub.KillAllAsync();
        var stop = Button(Locale.Get("windows.screenShare.stopShortcut"), () => screenHub?.KillAllAsync() ?? Task.CompletedTask);
        // The stop control while a phone shares this screen: on the err fill, radius 8 (the stop button's look).
        stop.BorderThickness = new(0); PaintPlainButton(stop, brushes.Brush(DesignToken.Err), brushes.Brush(DesignToken.Err), ink: brushes.Brush(DesignToken.OnStatus));
        screenBanner = new Border { Child = stop, Background = brushes.Brush(DesignToken.Err), CornerRadius = new(DesignMetrics.Radius.Row), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Margin = new(DesignMetrics.Spacing.Md) };
        Grid.SetColumnSpan(screenBanner, 2); Grid.SetRowSpan(screenBanner, 3); Canvas.SetZIndex(screenBanner, 100); root.Children.Add(screenBanner);
        // The error banner's close button stands in the same corner: while the banner shows, the stop control stands under it.
        void Place() { if (screenBanner is not null) screenBanner.Margin = new(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Md + (errorBanner.Visibility == Visibility.Visible ? errorBanner.ActualHeight : 0), DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Md); }
        errorBanner.SizeChanged += (_, _) => Place(); errorBanner.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Place()); Place();
    }
    private void ScreenChanged() => DispatcherQueue.TryEnqueue(() => { if (screenBanner is not null) screenBanner.Visibility = screenHub?.Sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed; mobileRouter?.Changed(); });
    private Task<bool> ConfirmScreenKey(string device, string fingerprint, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            var ownsDialog = false;
            try
            {
                if (closing || token.IsCancellationRequested || mobileHost?.Devices.FirstOrDefault(d => d.Id == device) is not { } phone || dialogOpen) { completion.TrySetResult(false); return; }
                dialogOpen = true; ownsDialog = true;
                var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get("windows.screenShare.confirmKeyTitle"), Content = phone.Name + "\n\n" + fingerprint + "\n\n" + Locale.Get("windows.screenShare.confirmKeyMessage"), PrimaryButtonText = Locale.Get("windows.screenShare.approveKey"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
                (settingsWindow ?? this).Activate(); using var cancel = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
                var choice = await dialog.ShowAsync(); completion.TrySetResult(!token.IsCancellationRequested && choice == ContentDialogResult.Primary);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { completion.TrySetResult(false); }
            finally { if (ownsDialog) dialogOpen = false; }
        })) completion.TrySetResult(false);
        return completion.Task;
    }
    // 화면 보기·조작 (M/ScreenShareSettingsSection.swift:23-175): the explanation; the small scene button (and
    // the Windows stop-all button); the scene's 10pt note; whether this PC can capture; then a row per
    // paired phone — its name against the small allow switch, the access picker (segmented, small) while it
    // is allowed, the control key's fingerprint beside the remove button, and its live sessions.
    private StackPanel BuildScreenShareSection()
    {
        var rows = new StackPanel(); const string deviceRow = "screen-share-device";
        AutomationProperties.SetAutomationId(rows, "settings-screen-share");
        var description = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        description.Children.Add(SettingsText(Locale.Get("windows.screenShare.description"), 11, DesignToken.Ink2));
        description.Children.Add(SettingsText(Locale.Get("windows.screenShare.inputDescription"), 11, DesignToken.Ink2));
        SettingsRow(rows, description);
        var scene = SettingsPush(Button(Locale.Get("settings.screenShare.scene.button"), () => { screenScene?.Close(); screenScene = new(phase => screenHub?.BroadcastSceneAsync(phase) ?? Task.CompletedTask); screenScene.Show(); return Task.CompletedTask; }), SettingsControlSize.Small);
        scene.Content = SettingsGlyphLabel("", Locale.Get("settings.screenShare.scene.button"), 11);
        AutomationProperties.SetAutomationId(scene, "settings-screen-share-scene");
        var stopAll = SettingsPush(Button(Locale.Get("windows.screenShare.stopAll"), () => screenHub?.KillAllAsync() ?? Task.CompletedTask), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(stopAll, "settings-screen-share-stop-all");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        buttons.Children.Add(scene); buttons.Children.Add(stopAll);
        SettingsRow(rows, buttons);
        SettingsRow(rows, SettingsText(Locale.Get("settings.screenShare.scene.description"), 10, DesignToken.Ink2));
        var availability = SettingsText("", 11, DesignToken.Ink2);
        SettingsRow(rows, availability);
        async Task Change(string device, bool allowed, string grant, string previous)
        {
            if (screenHub is null) return;
            if (allowed && grant == "control" && previous != "control")
            {
                var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get("settings.screenShare.controlTitle"), Content = Locale.Get("windows.screenShare.controlConfirm"), PrimaryButtonText = Locale.Get("settings.screenShare.controlConfirm"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { Refresh(); return; }
            }
            await screenHub.SetGrantAsync(device, allowed, grant); Refresh();
        }
        void Refresh()
        {
            availability.Text = options.SmokeTest ? Locale.Get("windows.screenShare.smokeDisabled") : screenPlatform?.Available == true ? Locale.Get("windows.screenShare.ready") : screenUnavailable.Length > 0 ? screenUnavailable : Locale.Get("windows.screenShare.unavailable");
            var contents = new List<FrameworkElement>();
            foreach (var device in mobileHost?.Devices ?? [])
            {
                var grant = screenHub?.Grants.FirstOrDefault(g => g.DeviceId == device.Id) ?? new ScreenDeviceGrant(device.Id);
                if (device.Legacy)
                {
                    // A phone that pairs with the key alone cannot be told apart, so it can never be allowed (M/ScreenShareSettingsSection.swift:93-103).
                    var old = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                    AutomationProperties.SetAutomationId(old, "settings-screen-share-legacy");
                    old.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); old.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                    var unknown = SettingsSymbol("", 14, DesignToken.Ink2); unknown.VerticalAlignment = VerticalAlignment.Top; old.Children.Add(unknown);
                    var why = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                    why.Children.Add(SettingsText(device.Name, 12)); why.Children.Add(SettingsText(Locale.Get("settings.screenShare.legacyUnsupported"), 10, DesignToken.Ink2));
                    Grid.SetColumn(why, 1); old.Children.Add(why);
                    contents.Add(old); continue;
                }
                var sessions = screenHub?.Sessions.Where(s => s.DeviceId == device.Id).ToList() ?? [];
                var available = screenPlatform?.Available == true && service.Snapshot.MobileRemote.Enabled;
                var row = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs) };
                AutomationProperties.SetAutomationId(row, "settings-screen-share-device-" + device.Id);
                var head = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                head.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                head.Children.Add(SettingsSymbol("", 14, sessions.Count > 0 ? DesignToken.DoneText : DesignToken.Ink2));
                var name = SettingsText(device.Name, 12); Grid.SetColumn(name, 1); head.Children.Add(name);
                var allowLabel = Locale.Get("settings.screenShare.allowToggle");
                var toggle = SettingsSwitch(allowLabel, grant.Allowed, "settings-screen-share-allow-" + device.Id); toggle.IsEnabled = available;
                void Toggled() => _ = Act(() => Change(device.Id, toggle.IsChecked == true, grant.Grant, grant.Grant));
                toggle.Checked += (_, _) => Toggled(); toggle.Unchecked += (_, _) => Toggled();
                var allow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                allow.Children.Add(SettingsText(allowLabel, 11)); allow.Children.Add(toggle);
                Grid.SetColumn(allow, 2); head.Children.Add(allow);
                row.Children.Add(head);
                if (grant.Allowed)
                {
                    // Control is the one access that needs the key ceremony explained before it is given.
                    var grantLabel = Locale.Get("settings.screenShare.grantLabel");
                    var picker = SettingsSegmented(grantLabel, "settings-screen-share-grant-" + device.Id,
                        [("none", Locale.Get("settings.screenShare.grantNone")), ("view", Locale.Get("settings.screenShare.grantView")), ("control", Locale.Get("settings.screenShare.grantControl"))],
                        grant.Grant is "view" or "control" ? grant.Grant : "none", value => Act(() => Change(device.Id, toggle.IsChecked == true, value, grant.Grant)), small: true, enabled: available);
                    row.Children.Add(SettingsLabeled(SettingsText(grantLabel, 11), picker, share: true));
                }
                if (grant.ControlKeyPublic is { } key)
                {
                    var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                    line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    line.Children.Add(SettingsSymbol("", 10, DesignToken.Ink2));
                    var fingerprint = SettingsText(Locale.Get("settings.screenShare.keyFingerprint", new Dictionary<string, string> { ["fingerprint"] = ScreenSharePolicy.Fingerprint(Convert.FromBase64String(key)) }), 11, mono: true, selectable: true);
                    Grid.SetColumn(fingerprint, 1); line.Children.Add(fingerprint);
                    var removeKey = SettingsPush(SafeButton(Locale.Get("settings.screenShare.removeKeyButton"), async () =>
                    {
                        if (screenHub is not { } hub || closing) return;
                        var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get("settings.screenShare.removeKeyTitle"), Content = Locale.Get("settings.screenShare.removeKeyBody", new Dictionary<string, string> { ["device"] = device.Name }), PrimaryButtonText = Locale.Get("settings.screenShare.removeKeyConfirm"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || !ReferenceEquals(hub, screenHub) || mobileHost?.Devices.Any(phone => phone.Id == device.Id) != true || hub.Grants.FirstOrDefault(current => current.DeviceId == device.Id)?.ControlKeyPublic != key) return;
                        await hub.RemoveControlKeyAsync(device.Id); if (rows.IsLoaded) Refresh();
                    }), SettingsControlSize.Small);
                    AutomationProperties.SetAutomationId(removeKey, "settings-screen-share-remove-key-" + device.Id);
                    Grid.SetColumn(removeKey, 2); line.Children.Add(removeKey); row.Children.Add(line);
                }
                if (sessions.Count == 0) row.Children.Add(SettingsText(Locale.Get("settings.screenShare.noSession"), 10, DesignToken.Ink2));
                foreach (var session in sessions)
                {
                    var live = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm }; live.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); live.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); live.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    live.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = brushes.Brush(DesignToken.DoneText), VerticalAlignment = VerticalAlignment.Center });
                    var since = SettingsText(Locale.Get(session.Mode == "control" ? "settings.screenShare.sessionControl" : "settings.screenShare.sessionView", new Dictionary<string, string> { ["time"] = session.StartedAt.ToLocalTime().ToString("HH:mm") }), 11);
                    Grid.SetColumn(since, 1); live.Children.Add(since);
                    var stop = SettingsPush(SafeButton(Locale.Get("settings.screenShare.stopButton"), async () => { if (screenHub is not null) await screenHub.StopSessionAsync(session.SessionId); if (rows.IsLoaded) Refresh(); }), SettingsControlSize.Small, destructive: true);
                    AutomationProperties.SetAutomationId(stop, "settings-screen-share-stop-" + session.SessionId);
                    Grid.SetColumn(stop, 2); live.Children.Add(stop); row.Children.Add(live);
                }
                contents.Add(row);
            }
            if (contents.Count == 0) contents.Add(SettingsText(Locale.Get("settings.screenShare.noPhones"), 11, DesignToken.Ink2));
            ReplaceSettingsRows(rows, deviceRow, contents);
        }
        void Changed() => DispatcherQueue.TryEnqueue(() => { if (rows.IsLoaded && !closing) Refresh(); });
        void DevicesChanged(MobileRelayStatus _) => Changed();
        rows.Loaded += (_, _) => { if (screenHub is not null) screenHub.Changed += Changed; if (mobileHost is not null) mobileHost.StatusChanged += DevicesChanged; Refresh(); };
        rows.Unloaded += (_, _) => { if (screenHub is not null) screenHub.Changed -= Changed; if (mobileHost is not null) mobileHost.StatusChanged -= DevicesChanged; };
        Refresh(); return rows;
    }
    private async Task ShutdownScreenShare()
    {
        screenScene?.Close(); screenScene = null;
        if (screenHub is not null) { screenHub.Changed -= ScreenChanged; await screenHub.DisposeAsync(); screenHub = null; }
        if (screenPlatform is not null) { await screenPlatform.DisposeAsync(); screenPlatform = null; }
        if (screenBanner is not null) root.Children.Remove(screenBanner); screenBanner = null;
    }
}
