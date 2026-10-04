using MightyClaude.Core;
using Microsoft.UI.Xaml;
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
        screenBanner = new Border { Child = stop, Background = brushes.Brush(DesignToken.Err), CornerRadius = new(DesignMetrics.Radius.Row), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Margin = new(12) };
        Grid.SetColumnSpan(screenBanner, 2); Grid.SetRowSpan(screenBanner, 3); Canvas.SetZIndex(screenBanner, 100); root.Children.Add(screenBanner);
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
    private StackPanel BuildScreenShareSection()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new(0, 20, 0, 0) };
        panel.Children.Add(new TextBlock { Text = Locale.Get("settings.screenShare.sectionTitle"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = Locale.Get("windows.screenShare.description"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Locale.Get("windows.screenShare.inputDescription"), TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 11 });
        var availability = new TextBlock { TextWrapping = TextWrapping.Wrap }; var rows = new StackPanel { Spacing = 12 }; panel.Children.Add(availability); panel.Children.Add(rows);
        panel.Children.Add(Button(Locale.Get("windows.screenShare.stopAll"), () => screenHub?.KillAllAsync() ?? Task.CompletedTask));
        panel.Children.Add(Button(Locale.Get("settings.screenShare.scene.button"), () => { screenScene?.Close(); screenScene = new(phase => screenHub?.BroadcastSceneAsync(phase) ?? Task.CompletedTask); screenScene.Show(); return Task.CompletedTask; }));
        panel.Children.Add(new TextBlock { Text = Locale.Get("settings.screenShare.scene.description"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .7 });
        async Task Change(string device, ToggleSwitch toggle, ComboBox picker, string previous)
        {
            if (screenHub is null || picker.SelectedItem is not ComboBoxItem { Tag: string grant }) return;
            if (toggle.IsOn && grant == "control" && previous != "control")
            {
                var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get("settings.screenShare.controlTitle"), Content = Locale.Get("windows.screenShare.controlConfirm"), PrimaryButtonText = Locale.Get("settings.screenShare.controlConfirm"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { Refresh(); return; }
            }
            await screenHub.SetGrantAsync(device, toggle.IsOn, grant); Refresh();
        }
        void Refresh()
        {
            availability.Text = options.SmokeTest ? Locale.Get("windows.screenShare.smokeDisabled") : screenPlatform?.Available == true ? Locale.Get("windows.screenShare.ready") : screenUnavailable.Length > 0 ? screenUnavailable : Locale.Get("windows.screenShare.unavailable");
            rows.Children.Clear();
            foreach (var device in mobileHost?.Devices ?? [])
            {
                var grant = screenHub?.Grants.FirstOrDefault(g => g.DeviceId == device.Id) ?? new ScreenDeviceGrant(device.Id);
                var row = new StackPanel { Spacing = 6 }; row.Children.Add(new TextBlock { Text = device.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (device.Legacy) { row.Children.Add(new TextBlock { Text = Locale.Get("settings.screenShare.legacyUnsupported"), TextWrapping = TextWrapping.Wrap }); rows.Children.Add(row); continue; }
                var toggle = new ToggleSwitch { Header = Locale.Get("settings.screenShare.allowToggle"), IsOn = grant.Allowed, IsEnabled = screenPlatform?.Available == true && service.Snapshot.MobileRemote.Enabled };
                var picker = new ComboBox { Header = Locale.Get("settings.screenShare.grantLabel"), IsEnabled = toggle.IsEnabled };
                foreach (var kind in new[] { "none", "view", "control" }) picker.Items.Add(new ComboBoxItem { Tag = kind, Content = Locale.Get(kind switch { "control" => "settings.screenShare.grantControl", "view" => "settings.screenShare.grantView", _ => "settings.screenShare.grantNone" }) });
                picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().First(p => (string)p.Tag == grant.Grant);
                toggle.Toggled += async (_, _) => await Act(() => Change(device.Id, toggle, picker, grant.Grant)); picker.SelectionChanged += async (_, _) => await Act(() => Change(device.Id, toggle, picker, grant.Grant)); row.Children.Add(toggle); row.Children.Add(picker);
                if (grant.ControlKeyPublic is { } key)
                {
                    row.Children.Add(new TextBlock { Text = Locale.Get("settings.screenShare.keyFingerprint", new Dictionary<string, string> { ["fingerprint"] = ScreenSharePolicy.Fingerprint(Convert.FromBase64String(key)) }), IsTextSelectionEnabled = true });
                    row.Children.Add(SafeButton(Locale.Get("settings.screenShare.removeKeyButton"), async () =>
                    {
                        if (screenHub is not { } hub || closing) return;
                        var dialog = StyledDialog(new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get("settings.screenShare.removeKeyTitle"), Content = Locale.Get("settings.screenShare.removeKeyBody", new Dictionary<string, string> { ["device"] = device.Name }), PrimaryButtonText = Locale.Get("settings.screenShare.removeKeyConfirm"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close });
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || !ReferenceEquals(hub, screenHub) || mobileHost?.Devices.Any(phone => phone.Id == device.Id) != true || hub.Grants.FirstOrDefault(current => current.DeviceId == device.Id)?.ControlKeyPublic != key) return;
                        await hub.RemoveControlKeyAsync(device.Id); if (panel.IsLoaded) Refresh();
                    }));
                }
                foreach (var session in screenHub?.Sessions.Where(s => s.DeviceId == device.Id) ?? [])
                {
                    var live = new Grid { ColumnSpacing = 8 }; live.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); live.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    live.Children.Add(new TextBlock { Text = Locale.Get(session.Mode == "control" ? "settings.screenShare.sessionControl" : "settings.screenShare.sessionView", new Dictionary<string, string> { ["time"] = session.StartedAt.ToLocalTime().ToString("HH:mm") }), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                    var stop = SafeButton(Locale.Get("settings.screenShare.stopButton"), async () => { if (screenHub is not null) await screenHub.StopSessionAsync(session.SessionId); if (panel.IsLoaded) Refresh(); });
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(stop, "settings-screen-share-stop-" + session.SessionId);
                    Grid.SetColumn(stop, 1); live.Children.Add(stop); row.Children.Add(live);
                }
                rows.Children.Add(row);
            }
            if (rows.Children.Count == 0) rows.Children.Add(new TextBlock { Text = Locale.Get("settings.screenShare.noPhones"), Opacity = .6 });
        }
        void Changed() => DispatcherQueue.TryEnqueue(() => { if (panel.IsLoaded && !closing) Refresh(); });
        void DevicesChanged(MobileRelayStatus _) => Changed();
        panel.Loaded += (_, _) => { if (screenHub is not null) screenHub.Changed += Changed; if (mobileHost is not null) mobileHost.StatusChanged += DevicesChanged; Refresh(); };
        panel.Unloaded += (_, _) => { if (screenHub is not null) screenHub.Changed -= Changed; if (mobileHost is not null) mobileHost.StatusChanged -= DevicesChanged; };
        Refresh(); return panel;
    }
    private async Task ShutdownScreenShare()
    {
        screenScene?.Close(); screenScene = null;
        if (screenHub is not null) { screenHub.Changed -= ScreenChanged; await screenHub.DisposeAsync(); screenHub = null; }
        if (screenPlatform is not null) { await screenPlatform.DisposeAsync(); screenPlatform = null; }
        if (screenBanner is not null) root.Children.Remove(screenBanner); screenBanner = null;
    }
}
