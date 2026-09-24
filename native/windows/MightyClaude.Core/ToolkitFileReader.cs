using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// Parse-only reader for toolkit.json.  No install, no approval mutation —
/// Windows reads the file so the settings screen can display the list.
/// </summary>
public static class ToolkitFileReader
{
    public sealed record ToolkitApproval(string ContentHash, string? ResolvedCommit = null);

    public sealed record ToolkitFileEntry(
        string Id,
        string DisplayName,
        string InstallKind,
        ToolkitInstallSpec Install,
        ToolkitApproval? Approval);

    public abstract record ToolkitInstallSpec;
    public sealed record PluginSpec(string Source, string PluginId) : ToolkitInstallSpec;
    public sealed record McpSpec(string Name, string Executable, IReadOnlyList<string> Args) : ToolkitInstallSpec;
    public sealed record SkillSpec(string Url) : ToolkitInstallSpec;
    public sealed record PackageSpec(string Manager, string Name) : ToolkitInstallSpec;
    public sealed record RepoScriptSpec(string Url, string Ref, string ScriptPath) : ToolkitInstallSpec;

    public sealed record ToolkitFile(IReadOnlyList<ToolkitFileEntry> Entries);

    public static ToolkitFile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("toolkit.json root must be an object");
        if (!root.TryGetProperty("version", out var ver) || !ver.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("toolkit.json must have version:1");
        if (!root.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("toolkit.json must have an 'entries' array");

        var entries = new List<ToolkitFileEntry>();
        foreach (var item in entriesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var entry = ParseEntry(item);
            if (entry is not null) entries.Add(entry);
        }
        return new ToolkitFile(entries);
    }

    private static ToolkitFileEntry? ParseEntry(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        var id = idEl.GetString() ?? "";
        if (!ValidIdentifier(id)) return null;

        if (!item.TryGetProperty("displayName", out var dnEl) || dnEl.ValueKind != JsonValueKind.String) return null;
        var displayName = dnEl.GetString() ?? "";
        if (displayName.Length == 0 || displayName.Length > 120) return null;

        if (!item.TryGetProperty("install", out var installEl) || installEl.ValueKind != JsonValueKind.Object) return null;
        if (!installEl.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String) return null;
        var kind = kindEl.GetString() ?? "";

        ToolkitInstallSpec? spec = kind switch
        {
            "plugin" => ParsePlugin(installEl),
            "mcp" => ParseMcp(installEl),
            "skill" => ParseSkill(installEl),
            "package" => ParsePackage(installEl),
            "repoScript" => ParseRepoScript(installEl),
            _ => null,
        };
        if (spec is null) return null;

        ToolkitApproval? approval = null;
        if (item.TryGetProperty("approval", out var approvalEl) && approvalEl.ValueKind == JsonValueKind.Object)
        {
            if (approvalEl.TryGetProperty("contentHash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
            {
                var hash = hashEl.GetString() ?? "";
                string? commit = null;
                if (approvalEl.TryGetProperty("resolvedCommit", out var commitEl) && commitEl.ValueKind == JsonValueKind.String)
                    commit = commitEl.GetString();
                if (hash.Length > 0) approval = new ToolkitApproval(hash, commit);
            }
        }

        return new ToolkitFileEntry(id, displayName, kind, spec, approval);
    }

    private static PluginSpec? ParsePlugin(JsonElement el)
    {
        if (!el.TryGetProperty("source", out var srcEl) || srcEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("pluginID", out var pidEl) || pidEl.ValueKind != JsonValueKind.String) return null;
        var source = srcEl.GetString() ?? "";
        var pluginId = pidEl.GetString() ?? "";
        if (!ValidPluginSource(source) || !ValidPluginId(pluginId)) return null;
        return new PluginSpec(source, pluginId);
    }

    private static McpSpec? ParseMcp(JsonElement el)
    {
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("executable", out var exEl) || exEl.ValueKind != JsonValueKind.String) return null;
        var name = nameEl.GetString() ?? "";
        var executable = exEl.GetString() ?? "";
        if (!ValidIdentifier(name) || !ValidExecutable(executable)) return null;
        var args = new List<string>();
        if (el.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            if (argsEl.GetArrayLength() > 64) return null;
            foreach (var arg in argsEl.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.String) return null;
                var s = arg.GetString() ?? "";
                if (s.Contains('\0')) return null;
                args.Add(s);
            }
        }
        return new McpSpec(name, executable, args);
    }

    private static SkillSpec? ParseSkill(JsonElement el)
    {
        if (!el.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return null;
        var url = urlEl.GetString() ?? "";
        return ValidHttpsUrl(url) ? new SkillSpec(url) : null;
    }

    private static PackageSpec? ParsePackage(JsonElement el)
    {
        if (!el.TryGetProperty("manager", out var mgrEl) || mgrEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
        var manager = mgrEl.GetString() ?? "";
        var name = nameEl.GetString() ?? "";
        if (manager is not ("brew" or "npm")) return null;
        return ValidPackageName(name) ? new PackageSpec(manager, name) : null;
    }

    private static RepoScriptSpec? ParseRepoScript(JsonElement el)
    {
        if (el.TryGetProperty("arguments", out _)) return null;
        if (!el.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("ref", out var refEl) || refEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("scriptPath", out var spEl) || spEl.ValueKind != JsonValueKind.String) return null;
        var url = urlEl.GetString() ?? "";
        var @ref = refEl.GetString() ?? "";
        var scriptPath = spEl.GetString() ?? "";
        if (!ValidHttpsUrl(url) || !ValidRef(@ref) || !ValidScriptPath(scriptPath)) return null;
        return new RepoScriptSpec(url, @ref, scriptPath);
    }

    // Validation helpers (mirror Swift ToolkitEntryDecoder)

    private static bool ValidIdentifier(string value) =>
        value.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(value) <= 128
        && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$");

    private static bool ValidPluginSource(string value)
    {
        if (value.Length == 0 || value.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(value) > 2048) return false;
        if (Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*$")) return true;
        return value.StartsWith("https://", StringComparison.Ordinal);
    }

    private static bool ValidPluginId(string value)
    {
        var parts = value.Split('@');
        return parts.Length == 2 && ValidIdentifier(parts[0]) && ValidIdentifier(parts[1]);
    }

    private static bool ValidExecutable(string value)
    {
        if (value.Length == 0 || value.Contains('\0') || value.Contains(' ') || System.Text.Encoding.UTF8.GetByteCount(value) > 4096) return false;
        if (value.StartsWith('/')) return !value.Contains("..") && !value.Contains(';') && !value.Contains('|');
        return Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$");
    }

    private static bool ValidPackageName(string value)
    {
        if (value.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(value) > 128) return false;
        if (value.StartsWith('@'))
            return Regex.IsMatch(value, @"^@[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*$");
        return Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$");
    }

    private static bool ValidHttpsUrl(string value) =>
        value.StartsWith("https://", StringComparison.Ordinal) && !value.Contains('\0')
        && System.Text.Encoding.UTF8.GetByteCount(value) <= 2048;

    private static bool ValidRef(string value)
    {
        if (value.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(value) > 128) return false;
        if (Regex.IsMatch(value, @"^[0-9a-f]{40}$")) return true;
        return Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$");
    }

    private static bool ValidScriptPath(string value)
    {
        if (value.Length == 0 || value.StartsWith('/') || value.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(value) > 4096) return false;
        return !value.Split('/').Contains("..");
    }
}
