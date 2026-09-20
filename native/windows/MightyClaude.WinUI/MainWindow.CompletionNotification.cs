using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;

namespace MightyClaude.WinUI;

// Sits behind an interface so Core (tested on macOS) never depends on Windows APIs.
internal interface ICompletionNotifier
{
    bool IsSupported { get; }
    // Returns null on success, a human-readable reason when unavailable.
    Task<string?> TryRegisterAsync(Action<string> onSessionActivated);
    // Sends the notification. May throw; callers in the normal run path must catch.
    // The smoke check lets the exception propagate so CI can detect real failures.
    Task SendAsync(string runTitle, string sessionId);
}

internal sealed class WindowsAppNotifier : ICompletionNotifier
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    private Action<string>? activationCallback;
    private bool registered;

    public bool IsSupported => AppNotificationManager.IsSupported();

    public async Task<string?> TryRegisterAsync(Action<string> onSessionActivated)
    {
        try
        {
            using var cts = new CancellationTokenSource(CallTimeout);
            await Task.Run(AppNotificationManager.Default.Register, cts.Token);
            activationCallback = onSessionActivated;
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            registered = true;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void OnInvoked(AppNotificationManager _, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("sessionId", out var sessionId))
            activationCallback?.Invoke(sessionId);
    }

    public async Task SendAsync(string runTitle, string sessionId)
    {
        if (!registered) return;
        var body = CompletionNotificationStrings.NotificationBodyTemplate.Replace("{title}", runTitle);
        var encodedId = System.Net.WebUtility.UrlEncode(sessionId);
        var xmlTitle = System.Security.SecurityElement.Escape(CompletionNotificationStrings.NotificationTitle);
        var xmlBody = System.Security.SecurityElement.Escape(body);
        var xml = $"<toast launch=\"sessionId={encodedId}\"><visual><binding template=\"ToastGeneric\"><text>{xmlTitle}</text><text>{xmlBody}</text></binding></visual></toast>";
        using var cts = new CancellationTokenSource(CallTimeout);
        await Task.Run(() => AppNotificationManager.Default.Show(new AppNotification(xml)), cts.Token);
    }
}

public sealed partial class MainWindow
{
    private readonly CompletionNotificationDecision notificationDecision = new();
    private ICompletionNotifier? notifier;
    private string notifierStatus = CompletionNotificationStrings.StatusVerificationMode;

    private async Task InitNotifierAsync()
    {
        var impl = new WindowsAppNotifier();
        if (!impl.IsSupported)
        {
            notifierStatus = CompletionNotificationStrings.StatusVerificationMode;
            notifier = impl;
            return;
        }
        var reason = await impl.TryRegisterAsync(FocusSession);
        notifierStatus = reason is null
            ? CompletionNotificationStrings.StatusAllowed
            : CompletionNotificationStrings.StatusNeedPermission;
        notifier = impl;
    }

    // Brings the window to the front and selects the session that fired the notification.
    internal void FocusSession(string sessionId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!service.Snapshot.Sessions.Any(s => s.Id == sessionId)) return;
            AppWindow.Show();
            _ = Act(() => SelectLayoutSession(sessionId));
        });
    }

    private void HandleRunEventForNotification(RunEvent ev)
    {
        if (closing || notifier is null) return;
        var snapshot = service.Snapshot;
        var title = notificationDecision.Process(ev, snapshot, snapshot.CompletionNotificationsEnabled);
        if (title is null) return;
        var runTitle = snapshot.Sessions.FirstOrDefault(s => s.Id == ev.SessionId)?.Title ?? ev.SessionId;
        _ = Task.Run(async () =>
        {
            try { await notifier.SendAsync(runTitle, ev.SessionId); }
            catch { }
        });
    }

    // Returns "sent", or an anonymous {skipped, reason} object when unsupported.
    // Throws if IsSupported was true at registration but the API call failed.
    private async Task<object?> RunCompletionNotificationSmoke()
    {
        var impl = new WindowsAppNotifier();
        if (!impl.IsSupported)
            return new { skipped = true, reason = "IsSupported false" };
        var error = await impl.TryRegisterAsync(_ => { });
        if (error is not null)
            return new { skipped = true, reason = error };
        await impl.SendAsync("Smoke fixture", "smoke-fixture-session");
        return "sent";
    }

    // Settings section built from Core strings — portable to the future sectioned screen.
    private StackPanel BuildNotificationSettingsSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        var toggle = new ToggleSwitch
        {
            Header = CompletionNotificationStrings.ToggleLabel,
            IsOn = service.Snapshot.CompletionNotificationsEnabled,
            OffContent = "",
            OnContent = ""
        };
        toggle.Toggled += async (_, _) =>
            await service.UpdateAsync(s => s with { CompletionNotificationsEnabled = toggle.IsOn });
        panel.Children.Add(toggle);
        panel.Children.Add(new TextBlock
        {
            Text = notifierStatus,
            FontSize = 12,
            Opacity = .7,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(Button(CompletionNotificationStrings.SettingsButton, async () =>
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"))));
        return panel;
    }
}
