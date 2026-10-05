using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private AgentIOPipe? agentIO;
    private readonly Dictionary<string, AgentTerminal> agentTerminals = [];
    private readonly HashSet<Task> agentTerminalClosures = [];
    private readonly AgentWebOpenPrompts agentUrlPrompts = new();

    private void InitializeAgentIO()
    {
        if (options.SmokeTest || agentIO is not null || Environment.ProcessPath is not { } executable) return;
        agentUrlPrompts.Changed += RefreshAgentWebPrompts;
        var server = new AgentIOPipe(HandleAgentIO); agentIO = server;
        service.ConfigureAgentIO((request, workspace) => server.Bindings.Bind(request.SessionId, workspace, request.Provider, server.Name, executable));
    }
    private async Task<object> HandleAgentIO(AgentIOBinding binding, string tool, string value, CancellationToken cancellation)
    {
        var terminal = await AgentUI(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            RequireActiveAgentBinding(binding);
            if (closing || !service.Snapshot.Sessions.Any(p => p.Id == binding.PaneId && p.WorkspaceId == binding.WorkspaceId)) throw new InvalidOperationException("Agent pane closed.");
            if (tool == "open_url") return null;
            if (!agentTerminals.TryGetValue(binding.PaneId, out var terminal))
            {
                if (tool != "run_in_terminal") throw new ArgumentException("Unknown terminal handle.");
                agentTerminals[binding.PaneId] = terminal = new AgentTerminal(binding.WorkspacePath);
            }
            if (tool == "run_in_terminal") await ShowAgentIOPane(binding, AgentIOPaneKind.Terminal);
            cancellation.ThrowIfCancellationRequested();
            RequireActiveAgentBinding(binding);
            return terminal;
        }, cancellation);
        if (tool == "open_url") return await AgentUI(() => OpenAgentUrl(binding, new Uri(value), cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        RequireActiveAgentBinding(binding);
        return tool switch
        {
            "run_in_terminal" => await terminal!.Run(value, cancellation),
            "read_latest_output" => terminal!.Read(value),
            "stop" => await terminal!.Stop(value),
            _ => throw new ArgumentException("Unknown agent tool."),
        };
    }
    private void RequireActiveAgentBinding(AgentIOBinding binding)
    {
        binding.Revoked.ThrowIfCancellationRequested();
        if (agentIO?.Bindings.IsCurrent(binding) != true) throw new OperationCanceledException("Agent binding was replaced.");
    }
    private Task<T> AgentUI<T>(Func<Task<T>> action, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellation.Register(() => completion.TrySetCanceled(cancellation));
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { cancellation.ThrowIfCancellationRequested(); if (closing) throw new OperationCanceledException(); completion.TrySetResult(await action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellation); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { registration.Dispose(); }
        })) { registration.Dispose(); completion.TrySetCanceled(); }
        return completion.Task;
    }
    private async Task<string> ShowAgentIOPane(AgentIOBinding binding, string kind)
    {
        var paneId = AgentIOPaneKind.PaneId(binding.PaneId, kind);
        await service.UpdateAsync(state =>
        {
            RequireActiveAgentBinding(binding);
            return WithAgentIOPane(state, binding.PaneId, binding.WorkspaceId, kind);
        });
        RequireActiveAgentBinding(binding);
        HideDashboard(); Render(); return paneId;
    }
    /// <summary>The state with the agent pane's terminal or browser pane to its right; unchanged while that pane is already open.</summary>
    private AppSnapshot WithAgentIOPane(AppSnapshot state, string agentPaneId, string workspaceId, string kind)
    {
        var paneId = AgentIOPaneKind.PaneId(agentPaneId, kind);
        var parent = state.Sessions.FirstOrDefault(p => p.Id == agentPaneId && p.WorkspaceId == workspaceId) ?? throw new InvalidOperationException("Agent pane closed.");
        if (state.Sessions.Any(p => p.Id == paneId)) return state;
        if (state.Sessions.Count >= 128) throw new InvalidOperationException("Close a pane before opening another.");
        var before = EffectiveLayout(state, workspaceId);
        var target = PaneLayout.Groups(before).FirstOrDefault(g => g.SessionIds.Contains(agentPaneId));
        // Titled as on the Mac, "터미널" or "브라우저" (M/AppStore+AgentIO.swift:97-98, 114-115); it opens beside its agent.
        var pane = new RunSession { Id = paneId, Kind = kind, OwnerSessionId = agentPaneId, WorkspaceId = workspaceId, WorkspaceProfileKey = workspaceId, Provider = parent.Provider, Title = Locale.Get(kind == AgentIOPaneKind.Terminal ? "agentTerminal.terminalPane.title" : "agentTerminal.browserPane.title") };
        var next = state with { Sessions = state.Sessions.Append(pane).ToList() };
        var tree = EffectiveLayout(next, workspaceId);
        if (tree is not null && target is not null) tree = PaneLayout.Move(tree, paneId, target.Id, "right");
        return SaveLayoutMode(SaveLayout(next, workspaceId, tree), workspaceId, "custom");
    }
    /// <summary>
    /// The agent header's terminal button (M/SessionPaneView.swift:269-276, M/AppStore+AgentIO.swift:95-99):
    /// puts the agent's terminal pane back beside it when it was closed, and selects it. The terminal
    /// itself kept running, so the pane shows its output again.
    /// </summary>
    private Task OpenAgentTerminalPane(string agentPaneId) => Act(async () =>
    {
        if (!agentTerminals.ContainsKey(agentPaneId) || service.Snapshot.Sessions.FirstOrDefault(p => p.Id == agentPaneId) is not { } agent) return;
        await service.UpdateAsync(state => state.Sessions.Any(p => p.Id == agentPaneId) ? WithAgentIOPane(state, agentPaneId, agent.WorkspaceId, AgentIOPaneKind.Terminal) : state);
        await SelectLayoutSession(AgentIOPaneKind.PaneId(agentPaneId, AgentIOPaneKind.Terminal));
    });
    private async Task<object> OpenAgentUrl(AgentIOBinding binding, Uri url, CancellationToken cancellation)
    {
        RequireActiveAgentBinding(binding);
        string? destination = service.Snapshot.AgentWebOpenChoices?.GetValueOrDefault(binding.WorkspaceId);
        if (destination is null)
        {
            var answer = await agentUrlPrompts.RequestAsync(binding, url, cancellation);
            cancellation.ThrowIfCancellationRequested(); RequireActiveAgentBinding(binding);
            destination = answer.Destination;
            if (answer.Remember) await service.UpdateAsync(state => { cancellation.ThrowIfCancellationRequested(); RequireActiveAgentBinding(binding); return state with { AgentWebOpenChoices = new Dictionary<string, string>(state.AgentWebOpenChoices ?? []) { [binding.WorkspaceId] = destination } }; });
        }
        cancellation.ThrowIfCancellationRequested();
        RequireActiveAgentBinding(binding);
        if (closing || !service.Snapshot.Sessions.Any(p => p.Id == binding.PaneId)) throw new OperationCanceledException();
        var fallback = false;
        if (destination == "inApp")
        {
            if (BrowserEngineEnabled)
            {
                var paneId = await ShowAgentIOPane(binding, AgentIOPaneKind.Browser);
                if (!views.TryGetValue(paneId, out var pane)) { await SelectLayoutSession(paneId); pane = views.GetValueOrDefault(paneId); }
                if (pane is not null)
                {
                    try
                    {
                        pane.EnsureBrowserView();
                        // Leave enough of the bounded tool request for the external
                        // fallback when WebView2 is unavailable or still installing.
                        await pane.BrowserReady.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
                        cancellation.ThrowIfCancellationRequested(); RequireActiveAgentBinding(binding);
                        if (pane.OpenAgentBrowserUrl(url)) return new { destination = "inApp", url = url.AbsoluteUri };
                    }
                    catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // Only unavailable native browser failures fall through;
                        // cancellation/revocation always propagates without opening.
                    }
                }
            }
            fallback = true;
        }
        cancellation.ThrowIfCancellationRequested();
        RequireActiveAgentBinding(binding);
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        return new { destination = "external", url = url.AbsoluteUri, inAppUnavailable = fallback };
    }
    private void ReconcileAgentIO()
    {
        var ids = service.Snapshot.Sessions.Where(p => p.Kind == "claude").Select(p => p.Id).ToHashSet();
        foreach (var id in agentTerminals.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            agentIO?.Bindings.Revoke(id);
            var terminal = agentTerminals[id]; agentTerminals.Remove(id);
            var task = terminal.DisposeAsync().AsTask(); agentTerminalClosures.Add(task);
            _ = task.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => agentTerminalClosures.Remove(task)), TaskScheduler.Default);
        }
        foreach (var id in agentBoundPanes.Except(ids).ToArray()) { agentIO?.Bindings.Revoke(id); agentBoundPanes.Remove(id); }
        agentBoundPanes.UnionWith(ids);
    }
    private readonly HashSet<string> agentBoundPanes = [];
    private async Task ShutdownAgentIO()
    {
        agentUrlPrompts.Changed -= RefreshAgentWebPrompts;
        agentUrlPrompts.Dispose();
        service.ConfigureAgentIO(null);
        if (agentIO is not null) { await agentIO.DisposeAsync(); agentIO = null; }
        await Task.WhenAll(agentTerminals.Values.Select(t => t.DisposeAsync().AsTask()).Concat(agentTerminalClosures)); agentTerminals.Clear(); agentTerminalClosures.Clear();
    }
    private sealed partial class PaneView
    {
        private AgentTerminal? ownedAgentTerminal;
        internal void CloseBrowserView() { webView?.Close(); webView = null; }
        internal bool OpenAgentBrowserUrl(Uri url)
        {
            if (webView?.CoreWebView2 is not { } engine) return false;
            engine.Navigate(url.AbsoluteUri); return true;
        }
        private bool ConnectAgentTerminal(int columns, int rows)
        {
            if (Session.OwnerSessionId is not { } parent || !owner.agentTerminals.TryGetValue(parent, out var source)) return false;
            ownedAgentTerminal = source; source.Output += ReceiveTerminalOutput; ReceiveTerminalOutput(source.Replay); source.Resize(columns, rows);
            return true;
        }
    }
}
