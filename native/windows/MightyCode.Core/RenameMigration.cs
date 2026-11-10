using System.Security;
using Microsoft.Win32;

namespace MightyCode.Core;

/// <summary>The kinds of registry value, named as <c>RegistryValueKind</c> names them (which is Windows-only to mention).</summary>
public enum RegistryDataKind { Unknown = 0, String = 1, ExpandString = 2, Binary = 3, DWord = 4, MultiString = 7, QWord = 11 }

/// <summary>The registry as <see cref="RenameMigration"/> reads and writes it, under HKEY_CURRENT_USER; a fake stands in for it in tests.</summary>
public interface IRegistryStore
{
    bool KeyExists(string path);
    IReadOnlyList<string> SubKeyNames(string path);
    IReadOnlyList<(string Name, object Value, RegistryDataKind Kind)> Values(string path);
    void CreateKey(string path);
    void SetValue(string path, string name, object value, RegistryDataKind kind);
}

/// <summary>The real registry (HKEY_CURRENT_USER). Windows only: elsewhere every key is missing.</summary>
public sealed class CurrentUserRegistry : IRegistryStore
{
    public bool KeyExists(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }

    public IReadOnlyList<string> SubKeyNames(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<(string Name, object Value, RegistryDataKind Kind)> Values(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null) return [];
        var values = new List<(string Name, object Value, RegistryDataKind Kind)>();
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value) values.Add((name, value, Enum.Parse<RegistryDataKind>(key.GetValueKind(name).ToString())));
        return values;
    }

    public void CreateKey(string path) { if (OperatingSystem.IsWindows()) Registry.CurrentUser.CreateSubKey(path).Dispose(); }

    public void SetValue(string path, string name, object value, RegistryDataKind kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value, Enum.Parse<RegistryValueKind>(kind.ToString()));
    }
}

/// <summary>
/// Moves what an app from before the rename to Mighty Code kept under its old identifiers (docs/rename-identifiers.md, Windows):
/// the data folder <c>%APPDATA%\MightyClaudeNative</c>, the settings under <c>HKCU\Software\MightyClaudeNative</c>, and the child
/// worktrees under <c>%USERPROFILE%\.mightyclaude</c>. It runs at start before anything reads them, and only for the default
/// profile. Each step is done once: a folder moves only while the new one is not there yet, and the registry key is copied
/// (the old one stays) only while the new one is not there yet.
/// </summary>
public static class RenameMigration
{
    public const string LegacyDataFolder = "MightyClaudeNative";
    public const string DataFolder = "MightyCode";
    public const string LegacyHomeFolder = ".mightyclaude";
    public const string HomeFolder = ".mightycode";
    public const string LegacyRegistryKey = @"Software\MightyClaudeNative";
    public const string RegistryKey = @"Software\MightyCode";

    public sealed record Report
    {
        public bool MovedDataFolder { get; set; }
        public bool MovedWorktrees { get; set; }
        public bool CopiedRegistry { get; set; }
        /// <summary>The child worktrees now under the new folder, whose git links are repaired after start (<see cref="RepairWorktreesAsync"/>).</summary>
        public List<string> Worktrees { get; } = [];
    }

    /// <summary>Runs every step. <paramref name="claudeConfig"/> is Claude's config folder (<c>~/.claude</c>); never throws.</summary>
    public static Report Run(string appData, string home, string claudeConfig, IRegistryStore registry)
    {
        var report = new Report();
        var data = Path.Combine(appData, DataFolder);
        report.MovedDataFolder = Move(Path.Combine(appData, LegacyDataFolder), data);

        var oldHome = Path.Combine(home, LegacyHomeFolder);
        var newHome = Path.Combine(home, HomeFolder);
        if (Move(oldHome, newHome))
        {
            report.MovedWorktrees = true;
            // The records name each child's worktree by its full path.
            foreach (var name in new[] { "delegation-state.json", "workspace-state.json" }) RewritePaths(Path.Combine(data, name), oldHome, newHome);
            try { report.Worktrees.AddRange(Directory.EnumerateDirectories(Path.Combine(newHome, "worktrees"))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            MoveClaudeProjects(claudeConfig, oldHome, newHome);
        }

        try { report.CopiedRegistry = CopyRegistry(registry, LegacyRegistryKey, RegistryKey); }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { }
        return report;
    }

    /// <summary>Moves <paramref name="old"/> to <paramref name="target"/> when it is a folder and the target is not there yet. Whether it moved.</summary>
    public static bool Move(string old, string target)
    {
        if (!Directory.Exists(old) || Directory.Exists(target) || File.Exists(target)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(old, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies a key, its values and subkeys, when <paramref name="old"/> exists and <paramref name="target"/> does not. The old key stays.</summary>
    public static bool CopyRegistry(IRegistryStore registry, string old, string target)
    {
        if (!registry.KeyExists(old) || registry.KeyExists(target)) return false;
        Copy(registry, old, target);
        return true;
    }

    private static void Copy(IRegistryStore registry, string from, string to)
    {
        registry.CreateKey(to);
        foreach (var (name, value, kind) in registry.Values(from)) registry.SetValue(to, name, value, kind);
        foreach (var child in registry.SubKeyNames(from)) Copy(registry, from + @"\" + child, to + @"\" + child);
    }

    /// <summary>
    /// Replaces the folder path <paramref name="old"/> with <paramref name="target"/> in a JSON file, written plainly or with
    /// its backslashes doubled (and slashes escaped) as JSON text escapes them.
    /// </summary>
    public static void RewritePaths(string file, string old, string target)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var updated = Rewritten(text, old, target);
        if (updated == text) return;
        try { File.WriteAllText(file, updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Rewritten(string text, string old, string target)
    {
        // A separator follows, so a folder that merely starts with the old name is left alone.
        foreach (var separator in new[] { "\\", "/" }) text = text.Replace(old + separator, target + separator, StringComparison.Ordinal);
        static string Escaped(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
        foreach (var separator in new[] { "\\\\", "/", "\\/" }) text = text.Replace(Escaped(old) + separator, Escaped(target) + separator, StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// Claude keeps a child pane's conversations in a folder named after the pane's working folder
    /// (<c>~/.claude/projects/C--Users-me--mightyclaude-worktrees-&lt;id&gt;</c>, <see cref="SessionHistory.ClaudeProjectFolder"/>);
    /// it follows the worktree so the child resumes its conversation.
    /// </summary>
    public static void MoveClaudeProjects(string claudeConfig, string oldHome, string newHome)
    {
        var projects = Path.Combine(claudeConfig, "projects");
        var prefix = SessionHistory.ClaudeProjectFolder(Path.Combine(oldHome, "worktrees")) + "-";
        var replacement = SessionHistory.ClaudeProjectFolder(Path.Combine(newHome, "worktrees")) + "-";
        string[] folders;
        try { folders = Directory.Exists(projects) ? Directory.GetDirectories(projects) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith(prefix, StringComparison.Ordinal)) Move(folder, Path.Combine(projects, replacement + name[prefix.Length..]));
        }
    }

    /// <summary>Points each moved worktree's parent repository at its new place (<c>git worktree repair</c>, run inside the worktree). Off the UI thread: it starts git once per worktree.</summary>
    public static async Task RepairWorktreesAsync(IEnumerable<string> worktrees)
    {
        foreach (var worktree in worktrees) _ = await DelegationGit.Run(["worktree", "repair"], worktree);
    }
}
