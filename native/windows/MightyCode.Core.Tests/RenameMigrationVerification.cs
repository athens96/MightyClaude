using MightyCode.Core;

/// <summary>RenameMigration (docs/rename-identifiers.md), mirroring native/macos Tests/MightyCoreTests/RenameMigrationTests.swift.</summary>
internal static class RenameMigrationVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeRegistry : IRegistryStore
    {
        public readonly Dictionary<string, Dictionary<string, (object Value, RegistryDataKind Kind)>> Keys = new(StringComparer.OrdinalIgnoreCase);
        public bool KeyExists(string path) => Keys.ContainsKey(path);
        public IReadOnlyList<string> SubKeyNames(string path) => Keys.Keys.Where(key => key.StartsWith(path + @"\", StringComparison.OrdinalIgnoreCase) && !key[(path.Length + 1)..].Contains('\\')).Select(key => key[(path.Length + 1)..]).ToList();
        public IReadOnlyList<(string Name, object Value, RegistryDataKind Kind)> Values(string path) => Keys.TryGetValue(path, out var values) ? values.Select(pair => (pair.Key, pair.Value.Value, pair.Value.Kind)).ToList() : [];
        public void CreateKey(string path)
        {
            // Like the registry: a key's parents exist (below the Software key, which the fake leaves out).
            var parts = path.Split('\\');
            for (var count = 2; count <= parts.Length; count++) { var key = string.Join('\\', parts.Take(count)); if (!Keys.ContainsKey(key)) Keys[key] = []; }
        }
        public void SetValue(string path, string name, object value, RegistryDataKind kind) { CreateKey(path); Keys[path][name] = (value, kind); }
    }

    internal static Task TheOldFoldersMoveOnceAndTheRecordsFollow()
    {
        var root = Verification.Temp();
        try
        {
            var appData = Path.Combine(root, "AppData"); var home = Path.Combine(root, "home"); var claude = Path.Combine(home, ".claude");
            var oldData = Path.Combine(appData, "MightyClaudeNative"); var oldHome = Path.Combine(home, ".mightyclaude");
            Directory.CreateDirectory(Path.Combine(oldHome, "worktrees", "abc")); Directory.CreateDirectory(oldData);
            var oldPrefix = SessionHistory.ClaudeProjectFolder(Path.Combine(oldHome, "worktrees")) + "-";
            Directory.CreateDirectory(Path.Combine(claude, "projects", oldPrefix + "abc")); Directory.CreateDirectory(Path.Combine(claude, "projects", "other-project"));
            File.WriteAllText(Path.Combine(oldData, "workspace-state.json"), "{\"path\":\"" + oldHome.Replace("\\", "\\\\") + "/worktrees/abc\"}");
            File.WriteAllText(Path.Combine(oldData, "delegation-state.json"), "{\"worktree\":\"" + oldHome + "/worktrees/abc\"}");
            File.WriteAllText(Path.Combine(oldData, "keep.txt"), "kept");

            var report = RenameMigration.Run(appData, home, claude, new FakeRegistry());
            var newHome = Path.Combine(home, ".mightycode");
            Check(report.MovedDataFolder && report.MovedWorktrees && !report.CopiedRegistry, "both folders moved, no registry key to copy");
            Check(!Directory.Exists(oldData) && !Directory.Exists(oldHome) && File.ReadAllText(Path.Combine(appData, "MightyCode", "keep.txt")) == "kept" && Directory.Exists(Path.Combine(newHome, "worktrees", "abc")), "the contents arrive under the new names");
            Check(report.Worktrees.Count == 1 && report.Worktrees[0] == Path.Combine(newHome, "worktrees", "abc"), "the moved worktrees are listed for the git repair");
            Check(File.ReadAllText(Path.Combine(appData, "MightyCode", "delegation-state.json")).Contains(newHome + "/worktrees/abc") && !File.ReadAllText(Path.Combine(appData, "MightyCode", "delegation-state.json")).Contains(".mightyclaude"), "the delegation records name the new worktree folder");
            Check(File.ReadAllText(Path.Combine(appData, "MightyCode", "workspace-state.json")).Contains(newHome.Replace("\\", "\\\\") + "/worktrees/abc"), "the workspace records name the new worktree folder");
            var newPrefix = SessionHistory.ClaudeProjectFolder(Path.Combine(newHome, "worktrees")) + "-";
            Check(Directory.Exists(Path.Combine(claude, "projects", newPrefix + "abc")) && !Directory.Exists(Path.Combine(claude, "projects", oldPrefix + "abc")) && Directory.Exists(Path.Combine(claude, "projects", "other-project")), "Claude's conversations for the worktree follow it, others stay");

            // Again: the old folders come back (an older copy of the app ran), and nothing is replaced.
            Directory.CreateDirectory(oldData); File.WriteAllText(Path.Combine(oldData, "keep.txt"), "old"); Directory.CreateDirectory(oldHome);
            var second = RenameMigration.Run(appData, home, claude, new FakeRegistry());
            Check(!second.MovedDataFolder && !second.MovedWorktrees && second.Worktrees.Count == 0 && File.ReadAllText(Path.Combine(appData, "MightyCode", "keep.txt")) == "kept" && Directory.Exists(oldData) && Directory.Exists(oldHome), "a folder moves only while the new one is not there");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task NothingOldMeansNothingToDo()
    {
        var root = Verification.Temp();
        try
        {
            var registry = new FakeRegistry();
            var report = RenameMigration.Run(Path.Combine(root, "AppData"), Path.Combine(root, "home"), Path.Combine(root, "home", ".claude"), registry);
            Check(!report.MovedDataFolder && !report.MovedWorktrees && !report.CopiedRegistry && report.Worktrees.Count == 0, "nothing old, nothing done");
            Check(registry.Keys.Count == 0 && !Directory.Exists(Path.Combine(root, "AppData")) && !Directory.Exists(Path.Combine(root, "home")), "nothing is created");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task TheRegistryKeyIsCopiedOnceAndTheOldOneStays()
    {
        var root = Verification.Temp();
        try
        {
            var registry = new FakeRegistry();
            registry.SetValue(@"Software\MightyClaudeNative", "delegation.enabled", 0, RegistryDataKind.DWord);
            registry.SetValue(@"Software\MightyClaudeNative", "", "default", RegistryDataKind.String);
            registry.SetValue(@"Software\MightyClaudeNative\Child\Deep", "path", "%USERPROFILE%\\x", RegistryDataKind.ExpandString);
            registry.CreateKey(@"Software\MightyClaudeNative\Empty");
            var run = () => RenameMigration.Run(Path.Combine(root, "AppData"), Path.Combine(root, "home"), Path.Combine(root, "claude"), registry);
            Check(run().CopiedRegistry, "the old key is copied");
            Check(registry.Keys[@"Software\MightyCode"]["delegation.enabled"] is (0, RegistryDataKind.DWord) && (string)registry.Keys[@"Software\MightyCode"][""].Value == "default", "values keep their data and kind");
            Check(registry.Keys[@"Software\MightyCode\Child\Deep"]["path"] is ("%USERPROFILE%\\x", RegistryDataKind.ExpandString) && registry.Keys.ContainsKey(@"Software\MightyCode\Empty"), "subkeys come along, recursively, empty ones too");
            Check(registry.Keys.ContainsKey(@"Software\MightyClaudeNative") && registry.Keys.Count == 8, "the old key is left");
            registry.SetValue(@"Software\MightyCode", "delegation.enabled", 1, RegistryDataKind.DWord);
            Check(!run().CopiedRegistry && registry.Keys[@"Software\MightyCode"]["delegation.enabled"].Value is 1, "copied once: a later change is not undone");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task PathsAreRewrittenPlainAndEscaped()
    {
        const string old = @"C:\Users\me\.mightyclaude", target = @"C:\Users\me\.mightycode";
        var plain = RenameMigration.Rewritten(@"cd C:\Users\me\.mightyclaude\worktrees\a and C:\Users\me\.mightyclaude/worktrees/b", old, target);
        Check(plain == @"cd C:\Users\me\.mightycode\worktrees\a and C:\Users\me\.mightycode/worktrees/b", "plain paths are rewritten, either separator");
        var json = RenameMigration.Rewritten("{\"p\":\"C:\\\\Users\\\\me\\\\.mightyclaude\\\\worktrees\\\\a\",\"q\":\"C:\\\\Users\\\\me\\\\.mightyclaude\\/worktrees\"}", old, target);
        Check(json == "{\"p\":\"C:\\\\Users\\\\me\\\\.mightycode\\\\worktrees\\\\a\",\"q\":\"C:\\\\Users\\\\me\\\\.mightycode\\/worktrees\"}", "JSON-escaped paths are rewritten");
        Check(RenameMigration.Rewritten(@"C:\Users\me\.mightyclaude-other\x", old, target) == @"C:\Users\me\.mightyclaude-other\x", "a folder that only starts with the old name is left alone");
        return Task.CompletedTask;
    }
}
