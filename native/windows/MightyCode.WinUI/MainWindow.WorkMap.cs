using System.Text.Json;
using MightyCode.Core;
using Microsoft.UI.Xaml.Controls;

namespace MightyCode.WinUI;

// The work map (docs/work-map.md): one pane per workspace, drawn by the page alone. Its model is Core's WorkMapService (the same the
// phone reads through the router); this file is the bridge between the two and the files pane a changed file opens in.
public sealed partial class MainWindow
{
    private WorkMapService? workMapService;

    private WorkMapService WorkMaps => workMapService ??= CreateWorkMapService();

    private WorkMapService CreateWorkMapService()
    {
        var maps = new WorkMapService(StateDirectory, () => service.Snapshot, service.FileOriginals, token => service.Providers.FindAsync("claude", token));
        // A summary that ends (done or failed) is pushed to the page; the phone polls.
        maps.Changed += workspaceId => DispatcherQueue.TryEnqueue(() => _ = PushWorkMap(workspaceId));
        return maps;
    }

    private Workspace? WorkMapWorkspace(JsonElement args) => service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == args.Text("workspaceId"));

    private async Task PushWorkMap(string workspaceId)
    {
        if (webShell is null || service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is not { } workspace) return;
        webShell.Bridge.Push(new { kind = "workmap", paneId = WorkMapPaneKind.PaneId(workspaceId), state = JsonSerializer.SerializeToElement(await WorkMaps.ModelAsync(workspace), WorkMap.Json) });
    }

    private void RegisterWorkMapMethods(WebBridge bridge)
    {
        bridge.Register("openWorkMap", async args => { if (!dialogOpen) { HideDashboard(); await OpenWorkMapPane(args.Text("workspaceId")); } return null; });
        bridge.Register("workMapState", async args =>
            WorkMapWorkspace(args) is { } workspace ? JsonSerializer.SerializeToElement(await WorkMaps.ModelAsync(workspace), WorkMap.Json) : throw new InvalidOperationException("Workspace not found."));
        bridge.Register("workMapSummarize", async args =>
        {
            var workspace = WorkMapWorkspace(args) ?? throw new InvalidOperationException("Workspace not found.");
            var started = await WorkMaps.StartAsync(workspace, args.Text("key") ?? "");
            if (started == WorkMapStart.Busy) throw new InvalidOperationException(Locale.Get("workMap.error.busy"));
            // The page asks again with the summary marked as running.
            return started == WorkMapStart.Started;
        });
        // A changed file opens in the workspace's files pane, shown and selected there.
        bridge.Register("workMapOpenFile", async args =>
        {
            var workspace = WorkMapWorkspace(args) ?? throw new InvalidOperationException("Workspace not found.");
            var path = (args.Text("path") ?? "").Replace('\\', '/');
            if (dialogOpen || path.Length == 0 || LocalPath(workspace) is not { } root || WorkspaceFiles.Resolve(path, root) is null) return false;
            var tree = FilePaneTreeFor(workspace.Id);
            tree.Filter = ""; tree.Reveal(path); tree.SelectedPath = path; tree.PreviewPath = path;
            await OpenFilePane(workspace.Id);
            var files = new WebFilesTarget(tree, root);
            foreach (var folder in tree.Expanded.Where(p => path.StartsWith(p + "/", StringComparison.Ordinal)).OrderBy(p => p.Length).ToList()) await WebLoadFolder(files, folder);
            webShell?.Bridge.Push(new { kind = "files", paneId = FilePaneKind.PaneId(workspace.Id), path });
            return true;
        });
    }

    internal Task OpenWorkMapPane(string? workspaceId = null) => OpenSlimPane(workspaceId, WorkMapPaneKind.PaneId, WorkMapPaneKind.Kind, "workMap.title");

    private MenuFlyoutItem OpenWorkMapMenuItem(string? workspaceId = null)
    {
        var item = MenuItem(Locale.Get("workMap.open"), () => OpenWorkMapPane(workspaceId));
        item.Icon = new FontIcon { Glyph = "\uE707" };
        return item;
    }

    /// <summary>The header button's state: off for a remote workspace (its history is on its host) and for a folder that is no repository.</summary>
    private static (bool Enabled, string Help) WorkMapButton(Workspace workspace, bool git) =>
        workspace.Remote is not null ? (false, Locale.Get("workMap.unavailable.remote"))
        : !git ? (false, Locale.Get("workMap.unavailable.notGit"))
        : (true, Locale.Get("workMap.open"));
}
