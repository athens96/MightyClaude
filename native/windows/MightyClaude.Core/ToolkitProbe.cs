using System.Text.Json;
using ToolkitFileEntry = MightyClaude.Core.ToolkitFileReader.ToolkitFileEntry;

namespace MightyClaude.Core;

/// Injected paths for ToolkitProbe so tests use temporary directories.
public sealed class ToolkitProbeContext
{
    public required string HomeDirectory { get; init; }
    public required string[] PathDirectories { get; init; }
    public required string LocalAppData { get; init; }
}

/// Machine-wide, presence-only detection for toolkit entries.
/// Files and paths only — no process launch, no handshake.
public static class ToolkitProbe
{
    public enum Result { Installed, Missing }

    public static Result Probe(ToolkitFileEntry entry, ToolkitFileReader.ToolkitApproval? approval, ToolkitProbeContext ctx) =>
        entry.Install switch
        {
            ToolkitFileReader.PluginSpec ps => ProbePlugin(ps.PluginId, ctx.HomeDirectory),
            ToolkitFileReader.McpSpec ms => ProbeMcp(ms.Name, ctx.HomeDirectory),
            ToolkitFileReader.SkillSpec ss => ProbeSkill(ss.Url, ctx.HomeDirectory),
            ToolkitFileReader.PackageSpec { Manager: "npm" } ps => ProbeNpm(ps.Name, ctx.PathDirectories),
            ToolkitFileReader.PackageSpec { Manager: "winget", Executable: { } exe } => ProbeWinget(exe, ctx.LocalAppData, ctx.PathDirectories),
            // brew and repoScript are macOS-only; always missing on Windows.
            _ => Result.Missing,
        };

    // plugin: exact pluginID key in installed_plugins.json with at least one user-scope record.
    private static Result ProbePlugin(string pluginId, string home)
    {
        var path = Path.Combine(home, ".claude", "plugins", "installed_plugins.json");
        if (!File.Exists(path)) return Result.Missing;
        try
        {
            var text = File.ReadAllText(path);
            if (text.Length > 4 * 1024 * 1024) return Result.Missing;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object) return Result.Missing;
            if (!plugins.TryGetProperty(pluginId, out var records) || records.ValueKind != JsonValueKind.Array) return Result.Missing;
            foreach (var record in records.EnumerateArray())
                if (record.TryGetProperty("scope", out var scope) && scope.GetString() == "user") return Result.Installed;
            return Result.Missing;
        }
        catch { return Result.Missing; }
    }

    // mcp: name present under top-level mcpServers of %USERPROFILE%\.claude.json.
    private static Result ProbeMcp(string name, string home)
    {
        var path = Path.Combine(home, ".claude.json");
        if (!File.Exists(path)) return Result.Missing;
        try
        {
            var text = File.ReadAllText(path);
            if (text.Length > 4 * 1024 * 1024) return Result.Missing;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("mcpServers", out var mcp) || mcp.ValueKind != JsonValueKind.Object) return Result.Missing;
            return mcp.TryGetProperty(name, out _) ? Result.Installed : Result.Missing;
        }
        catch { return Result.Missing; }
    }

    // skill: %USERPROFILE%\.claude\skills\<name>\SKILL.md exists.
    // name = last URL path component with trailing .git stripped.
    private static Result ProbeSkill(string url, string home)
    {
        var component = new Uri(url, UriKind.Absolute).Segments.LastOrDefault()?.TrimEnd('/') ?? "";
        if (component.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) component = component[..^4];
        if (component.Length == 0) return Result.Missing;
        var skillMd = Path.Combine(home, ".claude", "skills", component, "SKILL.md");
        return File.Exists(skillMd) ? Result.Installed : Result.Missing;
    }

    // npm: find npm in PATH dirs, check <npmDir>/../node_modules/<name> (global prefix).
    private static Result ProbeNpm(string name, string[] pathDirs)
    {
        foreach (var dir in pathDirs)
        {
            var npmExe = Path.Combine(dir, "npm");
            if (!File.Exists(npmExe) && !File.Exists(npmExe + ".cmd") && !File.Exists(npmExe + ".exe")) continue;
            // Global node_modules is beside the npm bin directory's parent, or beside npm itself.
            var moduleDir = Path.Combine(dir, "node_modules", name);
            if (Directory.Exists(moduleDir)) return Result.Installed;
            // e.g. npm is in %APPDATA%\Roaming\npm; node_modules at same level
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null)
            {
                var parentModule = Path.Combine(parent, "node_modules", name);
                if (Directory.Exists(parentModule)) return Result.Installed;
            }
        }
        return Result.Missing;
    }

    // winget: file-only probe — executable exists in %LOCALAPPDATA%\Microsoft\WinGet\Links or a PATH dir.
    private static Result ProbeWinget(string executable, string localAppData, string[] pathDirs)
    {
        var wingetLinks = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        if (File.Exists(Path.Combine(wingetLinks, executable))) return Result.Installed;
        foreach (var dir in pathDirs)
            if (File.Exists(Path.Combine(dir, executable))) return Result.Installed;
        return Result.Missing;
    }
}
