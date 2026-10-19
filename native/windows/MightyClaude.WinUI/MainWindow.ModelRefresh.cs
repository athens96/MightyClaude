using MightyClaude.Core;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // Each pane's model list keeps itself right (M/AppStore+ModelCatalogs.swift): a selected pane's list is read again, a run
    // waits for the list as the CLI has it now, and a model or effort the list no longer has gives way with a line in the
    // transcript. When to read is Core's rule (ModelCatalogRefresh); `runtime` stays the active workspace's lists, and
    // the whole-runtime reads (start-up, a workspace selected, an update, an account change) still look for the CLIs
    // themselves. Every request is made on the UI thread, so every answer comes back on it. A smoke run asks no CLI
    // and reconciles nothing.
    private ModelCatalogRefresh? modelRefreshes;
    private ModelCatalogRefresh ModelRefreshes => modelRefreshes ??= new(LoadModels, AcceptModels);
    /// <summary>The folder <see cref="runtime"/> was read for.</summary>
    private string? runtimePath;
    /// <summary>The list a pane had before a newer one came while it ran; it is put right with both before its next request.</summary>
    private readonly Dictionary<string, ModelCatalog> modelPriorCatalogs = [];
    /// <summary>Each pane's model and effort when a reading began: a choice made meanwhile is the user's and stays.</summary>
    private readonly Dictionary<ModelContext, Dictionary<string, (string Model, string Effort)>> modelRefreshSelections = [];
    private string? modelsSelectedSession;

    private static ModelContext? ModelContextOf(RunSession session, AppSnapshot state) =>
        // A remote workspace's models are its host's: none is read here (M/AppStore+ModelCatalogs.swift).
        session.Kind == "claude" && state.Workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId) is { } workspace && LocalPath(workspace) is { } folder ? new(workspace.Id, folder, session.Provider) : null;
    private void RememberModelSelections(ModelContext key, AppSnapshot state) =>
        modelRefreshSelections[key] = state.Sessions.Where(s => s.WorkspaceId == key.WorkspaceId && s.Provider == key.Provider).ToDictionary(s => s.Id, s => (s.Model, s.Settings.Effort));
    private Task<ProviderRuntime> LoadModels(ModelContext key)
    {
        RememberModelSelections(key, service.Snapshot);
        return service.Providers.RefreshProviderAsync(key.Provider, key.Path);
    }

    /// <summary>Reads a pane's model list again; <paramref name="invalidate"/> when the one in hand is known to be another agent's or account's (M/AppStore+ModelCatalogs.swift:10-19).</summary>
    private void RefreshModels(string sessionId, bool invalidate = false)
    {
        if (closing || options.SmokeTest) return;
        var state = service.Snapshot;
        if (state.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } session && ModelContextOf(session, state) is { } key) _ = ModelRefreshes.Request(key, force: true, invalidate);
    }

    /// <summary>
    /// Selecting a pane reads its list again, however it was selected (M/AppStore.swift:546, 565). While the runtime in hand is
    /// another folder's, the whole runtime is on its way for this one (start-up, SelectWorkspace) and brings the list with it.
    /// </summary>
    private void RefreshSelectedPaneModels()
    {
        var selected = service.ActiveSessionId;
        if (selected == modelsSelectedSession) return;
        modelsSelectedSession = selected;
        if (selected is null || closing || options.SmokeTest) return;
        var state = service.Snapshot;
        if (state.Sessions.FirstOrDefault(s => s.Id == selected) is { } session && ModelContextOf(session, state) is { } key && key.Path == runtimePath) _ = ModelRefreshes.Request(key, force: true);
    }

    /// <summary>
    /// A newly read list (M/AppStore+ModelCatalogs.swift:32-45): it becomes the provider's list on screen where the runtime in
    /// hand is its folder's, and every idle pane of that folder and provider is reconciled with it. A pane that runs, or is
    /// about to, is left alone until its next request; so is a choice made while the list was being read.
    /// </summary>
    private void AcceptModels(ModelContext key, ProviderRuntime? previous, ProviderRuntime refreshed)
    {
        if (closing || options.SmokeTest) return;
        var state = service.Snapshot;
        if (!state.Workspaces.Any(w => w.Id == key.WorkspaceId && w.Path == key.Path)) return;
        // Every reading is a new object: the panes are drawn again only when it says something new.
        if (runtime is not null && runtimePath == key.Path && !ModelCatalogRefresh.Same(Runtime(key.Provider), refreshed)) { runtime = ProviderCatalog.With(runtime, refreshed); RefreshEnvironment(); }
        var chosen = modelRefreshSelections.GetValueOrDefault(key);
        foreach (var session in state.Sessions.Where(s => s.WorkspaceId == key.WorkspaceId && s.Provider == key.Provider && s.Kind == "claude"))
        {
            if (session.Status == "running" || views.GetValueOrDefault(session.Id)?.UpdateStarting == true)
            {
                if (previous is not null) modelPriorCatalogs.TryAdd(session.Id, previous.ModelCatalog);
                continue;
            }
            if (chosen is null || !chosen.TryGetValue(session.Id, out var was) || was != (session.Model, session.Settings.Effort)) continue;
            ReconcileModel(session, previous?.ModelCatalog, refreshed.ModelCatalog);
        }
    }

    /// <summary>Puts one pane's model and effort right against a list and says so in its transcript (ModelSelectionSupport.Adjusted); the save follows.</summary>
    private void ReconcileModel(RunSession session, ModelCatalog? previous, ModelCatalog refreshed)
    {
        if (ModelSelectionSupport.Adjusted(session, previous, refreshed) is null) return;
        var id = session.Id;
        _ = service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id == id && s.Status != "running" ? ModelSelectionSupport.Adjusted(s, previous, refreshed) ?? s : s).ToList() });
        if (views.TryGetValue(id, out var pane)) pane.Refresh();
    }

    /// <summary>
    /// Before a run starts its pane waits for the model list as the CLI has it now, read again when the one in hand is older
    /// than a minute (M/AppStore+ModelCatalogs.swift:66-76); then the pane is put right with it, and with the list it had
    /// before when a newer one came while it ran. A list that cannot be read, or not within ModelCatalogRefresh.PreRunWait,
    /// holds nothing up.
    /// </summary>
    private async Task PrepareModels(string sessionId)
    {
        if (closing || options.SmokeTest) return;
        var state = service.Snapshot;
        if (state.Sessions.FirstOrDefault(s => s.Id == sessionId) is not { } session || ModelContextOf(session, state) is not { } key) return;
        if (!await ModelRefreshes.WaitCurrent(key) || closing) return;
        modelPriorCatalogs.Remove(sessionId, out var prior);
        if (ModelRefreshes.Value(key) is { } value && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } current && current.Provider == key.Provider)
            ReconcileModel(current, prior, value.ModelCatalog);
    }

    /// <summary>
    /// A provider read with its CLI looked for again rather than the one held (ProviderCatalog.RefreshProviderAsync), for a verdict
    /// that would drop waiting requests; the runtime in hand for that folder takes it.
    /// </summary>
    private async Task<ProviderRuntime> ReadProviderAfresh(string provider, string path)
    {
        var fresh = await service.Providers.RefreshProviderAsync(provider, path, rediscover: true);
        if (!closing && runtime is not null && runtimePath == path && !ModelCatalogRefresh.Same(Runtime(provider), fresh)) { runtime = ProviderCatalog.With(runtime, fresh); RefreshEnvironment(); }
        return fresh;
    }

    private sealed partial class PaneView
    {
        /// <summary>The pane waits for its model list before a run: the editor's placeholder says so (M/SessionPaneView.swift:100).</summary>
        private bool checkingModels;
        private async Task PrepareModels()
        {
            checkingModels = true; RefreshComposerState();
            try { await owner.PrepareModels(id); }
            finally { checkingModels = false; if (QueuePaneAlive) RefreshComposerState(); }
        }
    }
}
