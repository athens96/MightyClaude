using MightyClaude.Core;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // Admission shares the UI dispatcher with StartFromComposer, whose call
    // reserves a DesktopService route synchronously before its first await.
    // A worker-thread flag alone would leave a check/start race.
    private string? manuallyUpdatingProvider;
    private DateTimeOffset? lastManualUpdateRefresh;
    private async Task ManualUpdateFinished()
    {
        if (closing || coordinator.FinishedAt is not { } finished || lastManualUpdateRefresh == finished) return;
        lastManualUpdateRefresh = finished;
        if (!options.SmokeTest) await RefreshRuntime();
        await ResendLoginRequestsAfterUpdate();
    }

    private Task<CliUpdateResult> UpdateManualProvider(string provider, CancellationToken token)
    {
        var completion = new TaskCompletionSource<CliUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (closing) throw new OperationCanceledException();
                if (options.SmokeTest && smokeCliUpdater is null)
                { completion.TrySetResult(new(provider, "skipped", Detail: CliUpdateStrings.DetailInspectFailed)); return; }
                if (loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider) || accountLoginWaits.ContainsKey(provider) || pluginOperations.IsRunning || service.HasActiveProvider(provider) || remoteHostRuns > 0
                    || views.Values.Any(p => p.UpdateProvider == provider && p.UpdateStarting))
                { completion.TrySetResult(new(provider, "busy", Detail: CliUpdateStrings.DetailBusy)); return; }
                manuallyUpdatingProvider = provider;
                try
                {
                    NotifyAutomaticUpdates();
                    var result = smokeCliUpdater is { } fake ? await fake(provider, token) : await cliUpdateService.UpdateAsync(provider, token);
                    completion.TrySetResult(result);
                }
                finally { manuallyUpdatingProvider = null; if (!closing) NotifyAutomaticUpdates(); }
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { completion.TrySetResult(new(provider, "failed", Detail: CliUpdateStrings.DetailInspectFailed)); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }

    private string? ManualMutationBlockReason(RunSession pane)
    {
        // A remote pane runs on its host: this PC's CLI updates and plugin changes never hold it (M/AppStore.swift runBlockedReason).
        if (pane.Kind != "claude" || service.IsRemoteWorkspace(pane.WorkspaceId)) return null;
        // A child whose last run's end is still being recorded (the app's commit of its work among it) starts nothing yet.
        if (service.Delegation?.EndingRuns.Contains(pane.Id) == true) return Locale.Get("delegation.child.recordingEnd");
        if (manuallyUpdatingProvider == pane.Provider)
            return Locale.Get("windows.updates.manualProvider", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(pane.Provider) });
        return pane.Provider is "claude" or "codex" && pluginOperations.IsRunning && !(automaticUpdateRunning && automaticallyUpdatingPlugins)
            ? Locale.Get("plugins.operation.busy") : null;
    }

    private string? PluginMutationBlockReason(string provider, Workspace workspace)
    {
        if (closing || !service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id && w.Path == workspace.Path && w.Remote == workspace.Remote)) return Locale.Get("windows.updates.workspaceChanged");
        if (LocalPath(workspace) is null) return Locale.Get("remote.workspace.pluginsOnHost");
        if (AnyCliUpdateRunning) return Locale.Get("loginRecovery.updating");
        if (pluginOperations.IsRunning) return Locale.Get("plugins.operation.busy");
        // Another computer's runs on this PC use its CLIs too (M localCLIIsRunning).
        return service.HasActiveProvider(provider) || remoteHostRuns > 0 || views.Values.Any(p => p.UpdateProvider == provider && p.UpdateStarting)
            ? Locale.Get("windows.updates.providerRunning", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) }) : null;
    }
}
