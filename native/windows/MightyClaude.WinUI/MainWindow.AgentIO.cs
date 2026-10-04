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
    private readonly SemaphoreSlim agentUrlPrompts = new(1, 1);

    private void InitializeAgentIO()
    {
        if (options.SmokeTest || agentIO is not null || Environment.ProcessPath is not { } executable) return;
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
            var parent = state.Sessions.FirstOrDefault(p => p.Id == binding.PaneId && p.WorkspaceId == binding.WorkspaceId) ?? throw new InvalidOperationException("Agent pane closed.");
            if (state.Sessions.Any(p => p.Id == paneId)) return state;
            if (state.Sessions.Count >= 128) throw new InvalidOperationException("Close a pane before opening another.");
            var before = EffectiveLayout(state, binding.WorkspaceId);
            var target = PaneLayout.Groups(before).FirstOrDefault(g => g.SessionIds.Contains(binding.PaneId));
            var pane = new RunSession { Id = paneId, Kind = kind, OwnerSessionId = binding.PaneId, WorkspaceId = binding.WorkspaceId, WorkspaceProfileKey = binding.WorkspaceId, Provider = parent.Provider, Title = parent.Title + " — " + Locale.Get(kind == AgentIOPaneKind.Terminal ? "agentTerminal.terminalPane.title" : "agentTerminal.browserPane.title") };
            var next = state with { Sessions = state.Sessions.Append(pane).ToList() };
            var tree = EffectiveLayout(next, binding.WorkspaceId);
            if (tree is not null && target is not null) tree = PaneLayout.Move(tree, paneId, target.Id, "right");
            return SaveLayoutMode(SaveLayout(next, binding.WorkspaceId, tree), binding.WorkspaceId, "custom");
        });
        RequireActiveAgentBinding(binding);
        HideDashboard(); Render(); return paneId;
    }
    private async Task<object> OpenAgentUrl(AgentIOBinding binding, Uri url, CancellationToken cancellation)
    {
        RequireActiveAgentBinding(binding);
        string? destination = service.Snapshot.AgentWebOpenChoices?.GetValueOrDefault(binding.WorkspaceId);
        if (destination is null)
        {
            await agentUrlPrompts.WaitAsync(cancellation);
            var ownsDialog = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                while (dialogOpen || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(p => p.IsOpen))
                    await Task.Delay(100, cancellation);
                dialogOpen = ownsDialog = true;
                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.dialogMessage"), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBox { Text = url.AbsoluteUri, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
                var remember = new CheckBox { Content = Locale.Get("agentTerminal.urlOpen.rememberToggle") }; content.Children.Add(remember);
                var dialog = new ContentDialog { Title = Locale.Get("agentTerminal.urlOpen.dialogTitle"), Content = content, XamlRoot = root.XamlRoot, PrimaryButtonText = Locale.Get("agentTerminal.urlOpen.inAppButton"), SecondaryButtonText = Locale.Get("agentTerminal.urlOpen.externalButton"), CloseButtonText = Locale.Get("guidedPanel.cancelButton"), DefaultButton = ContentDialogButton.None };
                using var cancelled = cancellation.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
                var result = await dialog.ShowAsync(); cancellation.ThrowIfCancellationRequested();
                RequireActiveAgentBinding(binding);
                if (result == ContentDialogResult.None) throw new OperationCanceledException();
                destination = result == ContentDialogResult.Primary ? "inApp" : "external";
                if (remember.IsChecked == true) await service.UpdateAsync(state => { RequireActiveAgentBinding(binding); return state with { AgentWebOpenChoices = new Dictionary<string, string>(state.AgentWebOpenChoices ?? []) { [binding.WorkspaceId] = destination } }; });
            }
            finally { if (ownsDialog) dialogOpen = false; agentUrlPrompts.Release(); }
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
                    pane.EnsureBrowserView(); await pane.BrowserReady.WaitAsync(cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    RequireActiveAgentBinding(binding);
                    if (pane.OpenAgentBrowserUrl(url)) return new { destination = "inApp", url = url.AbsoluteUri };
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
        service.ConfigureAgentIO(null);
        if (agentIO is not null) { await agentIO.DisposeAsync(); agentIO = null; }
        await Task.WhenAll(agentTerminals.Values.Select(t => t.DisposeAsync().AsTask()).Concat(agentTerminalClosures)); agentTerminals.Clear(); agentTerminalClosures.Clear();
    }
    private FrameworkElement BuildAgentWebOpenSetting()
    {
        var workspace = service.Snapshot.ActiveWorkspaceId;
        var picker = new ComboBox { Header = Locale.Get("agentTerminal.urlOpen.settingTitle"), IsEnabled = workspace is not null, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var pair in new[] { ("ask", "settingAsk"), ("inApp", "settingInApp"), ("external", "settingExternal") }) picker.Items.Add(new ComboBoxItem { Tag = pair.Item1, Content = Locale.Get("agentTerminal.urlOpen." + pair.Item2) });
        picker.SelectedIndex = service.Snapshot.AgentWebOpenChoices?.GetValueOrDefault(workspace ?? "") switch { "inApp" => 1, "external" => 2, _ => 0 };
        picker.SelectionChanged += async (_, _) =>
        {
            if (workspace is null || picker.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            await Act(() => service.UpdateAsync(state => { var choices = new Dictionary<string, string>(state.AgentWebOpenChoices ?? []); if (value == "ask") choices.Remove(workspace); else choices[workspace] = value; return state with { AgentWebOpenChoices = choices }; }));
        };
        return picker;
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
