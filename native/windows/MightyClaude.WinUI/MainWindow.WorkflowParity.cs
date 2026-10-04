using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task ShowTerminalHistory(string sessionId)
    {
        if (dialogOpen || service.Snapshot.Sessions.FirstOrDefault(p => p.Id == sessionId) is not { } session) return;
        dialogOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 12, Width = 580 };
            content.Children.Add(new TextBlock { Text = Locale.Get("terminal.history.help"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
            var text = session.Logs.Count == 0 ? Locale.Get("terminal.history.empty") : string.Join("\n\n", session.Logs.Select(log => "[" + log.Kind + "] " + log.Text));
            content.Children.Add(new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily(DesignMetrics.Font.Mono), FontSize = 11 } });
            await new ContentDialog { Title = Locale.Get("terminal.history.title"), Content = content, CloseButtonText = Locale.Get("settings.closeButton"), XamlRoot = root.XamlRoot }.ShowAsync();
        }
        finally { dialogOpen = false; }
    }
    private sealed partial class PaneView
    {
        private bool refreshingPaneModels;
        private async Task RefreshPaneModels()
        {
            if (refreshingPaneModels || !QueuePaneAlive || !owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
            refreshingPaneModels = true;
            try { if (!owner.options.SmokeTest) await owner.RefreshRuntime(); }
            finally { refreshingPaneModels = false; if (QueuePaneAlive && owner.service.Snapshot.Sessions.Any(p => p.Id == id)) Refresh(); }
        }
        private async Task ResetConversation() => await ResetConversationAsync(CancellationToken.None);
        internal async Task<bool> ResetConversationAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!QueuePaneAlive || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } session || starting || queueStarting || owner.service.IsSessionRunning(id) || session.Status == "running") return false;
            var changed = false;
            await Change(p =>
            {
                token.ThrowIfCancellationRequested();
                if (p.Status == "running") return p;
                changed = true;
                return p with { ResumeId = null, SessionUsage = null,
                    Logs = [.. p.Logs, new LogEntry(Wire.Id(), "system", Locale.Get("composer.newConversationNote"), Wire.Now(), p.Provider)] };
            });
            if (!changed) return false;
            owner.DismissLoginRecovery(id);
            await RefreshPaneModels();
            if (QueuePaneAlive && owner.service.Snapshot.Sessions.Any(p => p.Id == id)) input.Focus(FocusState.Programmatic);
            return true;
        }
    }
}
