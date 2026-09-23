using System.Text.Json;
using MightyClaude.Core;

internal static class BrowserSessionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task BrowserSessionIsDroppedWithoutError()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var browserSession = new RunSession { Id = "browser-session-1", WorkspaceId = workspace.Id, Kind = "browser" };
            var claudeSession = new RunSession { Id = "claude-session-1", WorkspaceId = workspace.Id, Kind = "claude" };
            var state = new AppSnapshot { Workspaces = [workspace], Sessions = [browserSession, claudeSession] };
            var json = JsonSerializer.SerializeToUtf8Bytes(state, Wire.Json);
            await File.WriteAllBytesAsync(Path.Combine(directory, "workspace-state.json"), json);
            var loaded = await new StateStore(directory).LoadAsync();
            Check(!loaded.Sessions.Any(s => s.Kind == "browser"), "browser session must be dropped on Windows load");
            Check(loaded.Sessions.Any(s => s.Id == "claude-session-1"), "claude session must survive");
        }
        finally { Directory.Delete(directory, true); }
    }
}
