using System.Text.Json;
using MightyCode.Core;

namespace MightyCode.WinUI;

/// <summary>
/// The remote mirror in the window (docs/remote-workspaces.md "그 컴퓨터의 창 그대로", M/MightyCode/AppStore+RemoteMirror.swift and
/// RemoteMirrorViews.swift): this PC's shared panes handed to the sharing host, and the connected computers' panes shown here — the
/// sidebar's server groups, a mirrored pane's strip and the status bar's words, as the web shell's model carries them.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Hands this PC's panes to the sharing host (read and acted on through the phone's handlers, on the window's thread) and follows the
    /// client's mirrors: a change redraws what names them, a refused action is said in the error line.
    /// </summary>
    private void InitializeRemoteMirror(RemoteHostService host)
    {
        host.SetMirror(new RemoteMirrorDesktopHost(service, () => mobileRouter, id => views.TryGetValue(id, out var pane) ? pane.MirrorQueued() : [], work => OnWindow(work)));
        service.MirrorChanged += () => DispatcherQueue.TryEnqueue(() => { if (!closing) { RenderSidebar(); RefreshStatusBar(); RefreshRemoteViews(); QueueWebShellModel(); } });
        service.MirrorFailed += (_, message) => DispatcherQueue.TryEnqueue(() => { if (!closing) { error.Text = message; QueueWebShellModel(); } });
    }

    /// <summary>
    /// Settings' 보기: the shared workspace as the mirror lists it, chosen once it shows (within five seconds). False when the host turns
    /// out not to have the mirror (an older Mighty Code): the caller opens it as this PC's own remote workspace instead, as 열기 did.
    /// </summary>
    private async Task<bool> ShowMirrorWorkspace(string connectionId, string hostWorkspaceId)
    {
        var id = RemoteMirrorSupport.ClientId(connectionId, hostWorkspaceId);
        if (service.MirrorLink(connectionId) is { Unsupported: true }) return false;
        service.StartMirror(connectionId);
        settingsWindow?.Close(); CloseWebSettings(); HideDashboard();
        for (var i = 0; i < 50; i++)
        {
            if (service.Snapshot.Workspaces.Any(w => w.Id == id)) { await SelectWorkspace(id); return true; }
            if (service.MirrorLink(connectionId) is { Unsupported: true }) return false;
            await Task.Delay(100);
        }
        return true;
    }

    /// <summary>The sidebar server menu's 연결 (Settings' 새로고침, then the mirror) and 연결 끊기.</summary>
    private void RegisterMirrorMethods(WebBridge bridge)
    {
        bridge.Register("serverConnect", async args => { if (args.Text("id") is { } id) await RemoteAction(() => service.ConnectMirrorAsync(id)); return null; });
        bridge.Register("serverDisconnect", async args => { if (args.Text("id") is { } id) await RemoteAction(() => service.Remote?.DisconnectAsync(id) ?? Task.CompletedTask); return null; });
    }

    /// <summary>A server's state words and its dot's tone (M/RemoteMirrorViews.swift RemoteMirrorText.state): removed, reconnecting, connected, saved or off.</summary>
    private (string Text, string Tone) MirrorState(string connectionId)
    {
        if (service.Remote?.Connection(connectionId) is not { } connection) return (Locale.Get("remote.mirror.status.removed"), "idle");
        if (service.MirrorLink(connectionId) is { Phase: "reconnecting" }) return (Locale.Get("remote.mirror.status.reconnecting"), "wait");
        return connection.Status switch
        {
            RemoteConnectionStatus.Connected => (Locale.Get("remote.mirror.status.live"), "done"),
            RemoteConnectionStatus.Saved => (Locale.Get("remote.mirror.status.saved"), "idle"),
            _ => (Locale.Get("remote.mirror.status.off"), "idle"),
        };
    }

    /// <summary>"{host}에서 실행 중 · {state}", for a mirrored pane or workspace.</summary>
    private (string Text, string Tone) MirrorRunningOn(string host, string connectionId)
    {
        var state = MirrorState(connectionId);
        return (Locale.Get("remote.mirror.runningOn", new Dictionary<string, string> { ["host"] = host, ["state"] = state.Text }), state.Tone);
    }

    /// <summary>
    /// The servers the sidebar lists under this PC's own workspaces (M/AppStore+RemoteMirror.swift remoteServerGroups): every saved
    /// connection, then any connection a remote workspace still names after it was removed; each with its header's words.
    /// </summary>
    private List<object> WebRemoteServers(AppSnapshot state)
    {
        var connections = service.Remote?.List() ?? [];
        var servers = connections.Select(c => (Id: c.Id, Name: c.Name, Address: (string?)c.Address, Connected: c.Status == RemoteConnectionStatus.Connected, Detail: c.Detail)).ToList();
        foreach (var workspace in state.Workspaces.Where(w => w.Remote is not null))
            if (servers.All(s => s.Id != workspace.Remote!.ConnectionId)) servers.Add((workspace.Remote!.ConnectionId, workspace.Remote.HostName, null, false, null));
        return servers.Select(server =>
        {
            var (text, tone) = MirrorState(server.Id);
            var name = Locale.Get("remote.mirror.server", new Dictionary<string, string> { ["name"] = server.Name });
            return (object)new
            {
                id = server.Id, title = name, line = string.Join(" · ", new[] { server.Address, text }.OfType<string>()), state = text, tone, connected = server.Connected,
                known = connections.Any(c => c.Id == server.Id),
                help = service.MirrorLink(server.Id)?.Error ?? server.Detail ?? Locale.Get("remote.mirror.serverHelp", new Dictionary<string, string> { ["name"] = server.Name }),
            };
        }).ToList();
    }

    /// <summary>A mirrored pane's strip (M/RemoteMirrorViews.swift MirrorPaneBanner): its words, the host pane's branch and its queue; null for any other pane.</summary>
    private object? WebMirrorStrip(RunSession session)
    {
        if (session.Mirror is not { } mirror) return null;
        var (text, tone) = MirrorRunningOn(mirror.HostName, mirror.ConnectionId);
        var counts = service.MirrorCounts(session.Id) ?? new();
        return new
        {
            badge = Locale.Get("remote.mirror.paneBadge"), state = text, tone,
            branch = counts.Branch is { } branch ? counts.Base is { } baseBranch ? branch + " ← " + baseBranch : branch : null,
            queued = counts.Queued > 0 ? Locale.Get("remote.mirror.queued", new Dictionary<string, string> { ["count"] = counts.Queued.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null,
            queuedHelp = counts.QueuedTexts is { Count: > 0 } texts ? string.Join("\n", texts) : null,
            help = Locale.Get("remote.mirror.bannerHelp", new Dictionary<string, string> { ["host"] = mirror.HostName, ["count"] = RemoteMirrorProtocol.MaximumEntries.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
        };
    }

    /// <summary>The status bar's words while a mirror workspace is the one shown: "원격 · {host}에서 실행 중 · {state}"; null otherwise.</summary>
    private string? MirrorStatusText(Workspace? workspace) =>
        workspace?.Remote is { Mirror: true } remote ? Locale.Get("remote.mirror.server", new Dictionary<string, string> { ["name"] = MirrorRunningOn(remote.HostName, remote.ConnectionId).Text }) : null;

    private sealed partial class PaneView
    {
        /// <summary>The requests this pane's composer holds, in order, as a mirroring client lists them.</summary>
        internal IReadOnlyList<RemoteMirrorQueued> MirrorQueued() => queuedInputs.Items.Select(item => new RemoteMirrorQueued(item.Id, item.Text)).ToList();
    }
}
