using MightyClaude.Core;
using Microsoft.UI.Xaml;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly CliAutoUpdateSchedule automaticUpdateSchedule = new();
    private readonly DispatcherTimer automaticUpdateTimer = new() { Interval = CliAutoUpdateSchedule.Tick };
    private readonly CancellationTokenSource automaticUpdateClosing = new();
    private readonly Dictionary<string, DateTimeOffset> providerLastActive = [];
    private readonly Dictionary<string, PluginAutoUpdateResult> pluginUpdateResults = [];
    private Task automaticUpdateTask = Task.CompletedTask;
    private bool automaticUpdateRunning;
    private string? automaticallyUpdatingProvider;
    private bool automaticallyUpdatingPlugins;
    private DateTimeOffset? automaticUpdateFinishedAt;
    private event Action? AutomaticUpdatesChanged;
    private bool AnyCliUpdateRunning => coordinator.IsUpdating || automaticUpdateRunning;

    private void BeginAutomaticUpdates()
    {
        if (options.SmokeTest || closing) return;
        automaticUpdateTimer.Tick += (_, _) => CheckAutomaticUpdates();
        automaticUpdateTimer.Start(); CheckAutomaticUpdates();
    }
    private void RecordProviderActivity(RunEvent value)
    {
        if (value.Type is not ("status" or "log")) return;
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == value.SessionId && p.Kind == "claude") is { } pane)
            providerLastActive[pane.Provider] = DateTimeOffset.UtcNow;
    }
    private bool ReadyForAutomaticUpdate(string provider)
    {
        var panes = views.Values.Where(p => p.UpdateProvider == provider).ToArray();
        var busy = service.HasActiveProvider(provider) || loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider) || panes.Any(p => p.UpdateStarting);
        return CliAutoUpdateSchedule.IdleLongEnough(busy, panes.Any(p => p.UpdateHasQueue), providerLastActive.TryGetValue(provider, out var last) ? last : null, DateTimeOffset.UtcNow);
    }
    private bool BackgroundUpdateHolds(RunSession pane) => pane.Kind == "claude" && automaticUpdateRunning && automaticallyUpdatingProvider == pane.Provider;
    private void CheckAutomaticUpdates()
    {
        if (options.SmokeTest || closing || AnyCliUpdateRunning || pluginOperations.IsRunning) return;
        var state = service.Snapshot; var enabled = state.AutoUpdateCLIs == true || state.AutoUpdatePlugins != false;
        string[] providers;
        if (automaticUpdateSchedule.IsDue(DateTimeOffset.UtcNow, enabled))
        { automaticUpdateSchedule.PassStarted(DateTimeOffset.UtcNow); providers = Wire.Providers; }
        else providers = automaticUpdateSchedule.DueRetries(enabled, ReadyForAutomaticUpdate);
        if (providers.Length > 0) automaticUpdateTask = RunAutomaticUpdates(providers);
    }
    private void NotifyAutomaticUpdates()
    {
        foreach (var pane in views.Values.ToArray()) pane.BackgroundUpdateChanged();
        AutomaticUpdatesChanged?.Invoke(); mobileRouter?.Changed();
    }
    private async Task RunAutomaticUpdates(IReadOnlyList<string> providers)
    {
        automaticUpdateRunning = true; automaticUpdateFinishedAt = null;
        var token = automaticUpdateClosing.Token;
        try
        {
            NotifyAutomaticUpdates();
            var results = lastCliUpdateResults.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Last());
            foreach (var provider in providers)
            {
                token.ThrowIfCancellationRequested(); if (service.Snapshot.AutoUpdateCLIs != true) break;
                if (!ReadyForAutomaticUpdate(provider))
                { automaticUpdateSchedule.SkippedBusy(provider); results[provider] = new(provider, "skipped", Detail: Locale.Get("settings.cliUpdate.detailDeferred")); }
                else
                {
                    automaticallyUpdatingProvider = provider; automaticallyUpdatingPlugins = false; NotifyAutomaticUpdates();
                    results[provider] = await cliUpdateService.UpdateAsync(provider, token);
                    automaticUpdateSchedule.Updated(provider);
                    automaticallyUpdatingProvider = null; NotifyAutomaticUpdates();
                }
                lastCliUpdateResults = Wire.Providers.Where(results.ContainsKey).Select(p => results[p]).ToArray(); AutomaticUpdatesChanged?.Invoke();
            }
            var began = DateTimeOffset.UtcNow;
            var home = new Workspace { Name = "Home", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            foreach (var provider in providers.Where(p => p is "claude" or "codex"))
            {
                token.ThrowIfCancellationRequested(); if (service.Snapshot.AutoUpdatePlugins == false) break;
                var remaining = CliAutoUpdateSchedule.PluginBudget - (DateTimeOffset.UtcNow - began);
                if (!ReadyForAutomaticUpdate(provider) || remaining <= TimeSpan.Zero)
                { automaticUpdateSchedule.SkippedBusy(provider); pluginUpdateResults[provider] = new("skipped", Locale.Get("settings.cliUpdate.detailDeferred")); continue; }
                if (pluginOperations.IsRunning) { pluginUpdateResults[provider] = PluginAutoUpdateResult.Busy; continue; }
                automaticallyUpdatingProvider = provider; automaticallyUpdatingPlugins = true; NotifyAutomaticUpdates();
                pluginUpdateResults[provider] = await pluginOperations.UpdateInstalledAsync(provider, home, remaining, token);
                automaticallyUpdatingProvider = null; NotifyAutomaticUpdates();
                slashCatalogs.ClearProvider(provider);
            }
            if (!closing) await RefreshRuntime();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Background failures expose a bounded generic result, never CLI
            // output, environment variables or a marketplace's shown command.
            if (automaticallyUpdatingProvider is { } provider)
                if (automaticallyUpdatingPlugins) pluginUpdateResults[provider] = PluginAutoUpdateResult.Failure;
                else lastCliUpdateResults = [new(provider, "failed", Detail: CliUpdateStrings.DetailInspectFailed)];
        }
        finally
        {
            automaticUpdateRunning = false; automaticallyUpdatingProvider = null; automaticallyUpdatingPlugins = false;
            automaticUpdateFinishedAt = DateTimeOffset.UtcNow;
            if (!closing) { NotifyAutomaticUpdates(); TrackLoginTask(ResendLoginRequestsAfterUpdate()); }
        }
    }
    private async Task ShutdownAutomaticUpdates()
    {
        automaticUpdateTimer.Stop(); automaticUpdateClosing.Cancel(); await automaticUpdateTask;
    }

    private sealed partial class PaneView
    {
        internal string UpdateProvider => owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id && p.Kind == "claude")?.Provider ?? "";
        internal bool UpdateStarting => starting || queueStarting;
        internal bool UpdateHasQueue => owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is { Status: not "error" } && queuedInputs.Items.Count > 0;
        internal void BackgroundUpdateChanged()
        {
            if (!QueuePaneAlive) return;
            RefreshComposerState();
            if (!owner.BackgroundUpdateHolds(Session) && queuedInputs.Items.Count > 0 && Session.Status is not ("running" or "error") && !starting && !queueStarting)
                queueDrainTimer.Start();
        }
    }
}
