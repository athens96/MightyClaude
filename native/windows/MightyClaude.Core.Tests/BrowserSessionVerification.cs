using System.Text.Json;
using MightyClaude.Core;

internal static class BrowserSessionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // A snapshot written by macOS with a browser pane loads on Windows.
    // Browser sessions are now kept; workspaceProfileKey round-trips.
    internal static async Task BrowserSessionLoadsWithWorkspaceProfileKey()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var browserSession = new RunSession { Id = "browser-session-1", WorkspaceId = workspace.Id, Kind = "browser", WorkspaceProfileKey = workspace.Id };
            var claudeSession = new RunSession { Id = "claude-session-1", WorkspaceId = workspace.Id, Kind = "claude" };
            var state = new AppSnapshot
            {
                Workspaces = [workspace],
                Sessions = [browserSession, claudeSession],
                ActiveWorkspaceId = workspace.Id,
                ActiveSessionId = browserSession.Id,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(state, Wire.Json);
            await File.WriteAllBytesAsync(Path.Combine(directory, "workspace-state.json"), json);
            var loaded = await new StateStore(directory).LoadAsync();
            Check(loaded.Sessions.Any(s => s.Kind == "browser"), "browser session must load on Windows");
            var browser = loaded.Sessions.First(s => s.Kind == "browser");
            Check(browser.WorkspaceProfileKey == workspace.Id, "workspaceProfileKey must round-trip");
            Check(browser.Status == "idle", "browser session must always be idle");
            Check(browser.Logs.Count == 0, "browser session must have no logs");
            Check(loaded.Sessions.Any(s => s.Id == claudeSession.Id), "claude session must survive");
        }
        finally { Directory.Delete(directory, true); }
    }
}
