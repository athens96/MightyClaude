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
                if (loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider) || pluginOperations.IsRunning || service.HasActiveProvider(provider)
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
        if (pane.Kind != "claude") return null;
        if (manuallyUpdatingProvider == pane.Provider)
            return Locale.Get("windows.updates.manualProvider", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(pane.Provider) });
        return pane.Provider is "claude" or "codex" && pluginOperations.IsRunning && !(automaticUpdateRunning && automaticallyUpdatingPlugins)
            ? Locale.Get("plugins.operation.busy") : null;
    }

    private string? PluginMutationBlockReason(string provider, Workspace workspace)
    {
        if (closing || !service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id && w.Path == workspace.Path)) return Locale.Get("windows.updates.workspaceChanged");
        if (AnyCliUpdateRunning) return Locale.Get("loginRecovery.updating");
        if (pluginOperations.IsRunning) return Locale.Get("plugins.operation.busy");
        return service.HasActiveProvider(provider) || views.Values.Any(p => p.UpdateProvider == provider && p.UpdateStarting)
            ? Locale.Get("windows.updates.providerRunning", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) }) : null;
    }
}
