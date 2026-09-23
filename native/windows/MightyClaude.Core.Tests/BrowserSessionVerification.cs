using System.Text.Json;
using MightyClaude.Core;

internal static class BrowserSessionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // The macOS app can save a workspace whose pane layout holds a browser tab.
    // Windows has no browser pane in this cut, so that state must still load:
    // the browser session is dropped and everything around it stays coherent.
    internal static async Task BrowserSessionIsDroppedWithoutError()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var browserSession = new RunSession { Id = "browser-session-1", WorkspaceId = workspace.Id, Kind = "browser" };
            var claudeSession = new RunSession { Id = "claude-session-1", WorkspaceId = workspace.Id, Kind = "claude" };
            var tabs = new PaneLayoutNode { Id = "tabs-1", SessionIds = [browserSession.Id, claudeSession.Id], SelectedSessionId = browserSession.Id };
            var state = new AppSnapshot
            {
                Workspaces = [workspace],
                Sessions = [browserSession, claudeSession],
                ActiveWorkspaceId = workspace.Id,
                ActiveSessionId = browserSession.Id,
                PaneLayouts = new() { [workspace.Id] = tabs },
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(state, Wire.Json);
            await File.WriteAllBytesAsync(Path.Combine(directory, "workspace-state.json"), json);
            var loaded = await new StateStore(directory).LoadAsync();
            Check(!loaded.Sessions.Any(s => s.Kind == "browser"), "browser session must be dropped on Windows load");
            Check(loaded.Sessions.Any(s => s.Id == claudeSession.Id), "claude session must survive");
            Check(loaded.Workspaces.Any(w => w.Id == workspace.Id), "the workspace must survive");
            var layout = loaded.PaneLayouts?.GetValueOrDefault(workspace.Id);
            Check(layout is not null && PaneLayout.Sessions(layout).SequenceEqual([claudeSession.Id]), "the pane layout must keep only the surviving session");
            Check(loaded.ActiveSessionId == claudeSession.Id, "the active session must fall back off the dropped browser tab");
            Check(loaded.PaneLayoutActiveSessionIds?.GetValueOrDefault(workspace.Id) == claudeSession.Id, "the selected tab must fall back off the dropped browser tab");
        }
        finally { Directory.Delete(directory, true); }
    }
}
