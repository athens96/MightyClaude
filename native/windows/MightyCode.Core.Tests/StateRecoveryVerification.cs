using MightyCode.Core;

// A saved state that does not read (a power cut left it all zeros) never stops the app: the backup the last save kept comes back,
// or the app starts empty and can save again, and the unreadable file is put aside, never overwritten.
internal static class StateRecoveryVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "mighty-state-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    internal static async Task AZeroedStateComesBackFromItsBackup()
    {
        var root = Temp();
        try
        {
            var state = Path.Combine(root, "workspace-state.json");
            // Two saves: the second keeps the first as the backup.
            var first = new StateStore(root); await first.LoadAsync();
            await first.SaveAsync(first.Snapshot with { Theme = "light" });
            await first.SaveAsync(first.Snapshot with { Theme = "dark" });
            Check(File.Exists(state + ".bak") && File.ReadAllText(state + ".bak").Contains("\"light\""), "a save keeps the file it replaces as the backup");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "no temporary file is left behind");

            // The power cut: the file keeps its length and loses its bytes.
            var length = new FileInfo(state).Length; File.WriteAllBytes(state, new byte[length]);
            var second = new StateStore(root); var restored = await second.LoadAsync();
            Check(restored.Theme == "light" && second.LoadNotice is { Length: > 0 }, "the backup comes back and the window is told");
            var aside = Directory.GetFiles(root, "workspace-state.json.unreadable-*");
            Check(aside.Length == 1 && File.ReadAllBytes(aside[0]).All(b => b == 0) && new FileInfo(aside[0]).Length == length, "the unreadable file is put aside as it was");
            await second.SaveAsync(restored with { Theme = "dark" });
            Check(File.ReadAllText(state).Contains("\"dark\"") && File.ReadAllText(state + ".bak").Contains("\"light\""), "the next save works and the good backup stays");

            // No backup either: the app starts empty and saves again.
            File.Delete(state + ".bak"); File.WriteAllBytes(state, new byte[64]);
            var third = new StateStore(root); var empty = await third.LoadAsync();
            Check(empty.Workspaces.Count == 0 && third.LoadNotice is { Length: > 0 }, "without a backup the app starts empty and says so");
            await third.SaveAsync(empty);
            Check(File.Exists(state) && File.ReadAllText(state).Contains("\"version\""), "an empty start saves again instead of refusing every save");
            Check(Directory.GetFiles(root, "workspace-state.json.unreadable-*").Length >= 1, "the second unreadable file is put aside too");

            // A healthy file says nothing.
            var fourth = new StateStore(root); await fourth.LoadAsync();
            Check(fourth.LoadNotice is null, "a file that reads brings no notice");
        }
        finally { Directory.Delete(root, true); }
    }
}
