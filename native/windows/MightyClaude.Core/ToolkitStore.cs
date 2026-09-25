using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolkitFileEntry = MightyClaude.Core.ToolkitFileReader.ToolkitFileEntry;

namespace MightyClaude.Core;

/// <summary>
/// Manages the user's toolkit list next to workspace-state.json.
///
/// list() always returns bundled entries first, then this-OS user entries.
/// Other-OS entries (brew/repoScript on Windows) are preserved in the file
/// byte-identical and never shown in the list.
/// If toolkit.json exists but is unreadable the error is surfaced and the file
/// is never modified.
/// </summary>
public sealed class ToolkitStore
{
    private readonly string directory;

    private bool loaded;
    private List<ToolkitFileEntry> thisOsEntries = [];
    private Dictionary<string, ToolkitFileReader.ToolkitApproval> approvals = [];
    private string? fileError;
    // Ordered list of all entry IDs as read from the file (both platforms).
    private List<string> allEntryIds = [];
    // Raw JSON text for each entry, keyed by id (preserves unknown keys like `platforms`).
    private Dictionary<string, string> allEntryRawText = [];
    // IDs of entries that are other-OS (brew/repoScript on Windows).
    private HashSet<string> otherOsIds = [];
    // IDs of entries added, edited, approved or removed this session.
    // Touched entries are written canonically; untouched entries emit original bytes.
    private HashSet<string> touchedEntryIds = [];

    public ToolkitStore(string directory) => this.directory = directory;

    private string FilePath => Path.Combine(directory, "toolkit.json");

    // Bundled entry — always first, always visible, never requires approval.
    public static IReadOnlyList<ToolkitFileEntry> Bundled { get; } = [
        new("mighty-styles", "mighty-styles", "plugin",
            new ToolkitFileReader.PluginSpec("athens96/mighty-styles", "mighty-styles@mighty-styles"),
            Approval: null,
            Source: ToolkitFileReader.ToolkitEntrySource.Bundled),
    ];

    // ── Public API ───────────────────────────────────────────────────────────

    /// Returns (bundled + this-OS user entries, error). Other-OS entries are
    /// kept in the file but never appear in the list.
    public (IReadOnlyList<ToolkitFileEntry> Entries, string? Error) List()
    {
        DoLoad();
        return ([..Bundled, ..thisOsEntries], fileError);
    }

    /// Returns the stored approval for a user entry only if the content hash
    /// still matches. Returns null for bundled entries or if the entry changed.
    public ToolkitFileReader.ToolkitApproval? GetApproval(ToolkitFileEntry entry)
    {
        DoLoad();
        if (entry.Source == ToolkitFileReader.ToolkitEntrySource.Bundled) return null;
        if (!approvals.TryGetValue(entry.Id, out var stored)) return null;
        return stored.ContentHash == CanonicalHash(entry) ? stored : null;
    }

    /// Approves a user entry by computing its current canonical hash.
    /// resolvedCommit is required for repoScript entries (40-hex SHA).
    public void Approve(string id, string? resolvedCommit = null)
    {
        RequireLoaded();
        var entry = thisOsEntries.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException("Entry not found: " + id);
        approvals[id] = new ToolkitFileReader.ToolkitApproval(CanonicalHash(entry), resolvedCommit);
        touchedEntryIds.Add(id);
        Persist();
    }

    /// Adds or replaces a user entry (always unapproved after this call).
    public void Add(ToolkitFileEntry entry)
    {
        RequireLoaded();
        thisOsEntries.RemoveAll(e => e.Id == entry.Id);
        approvals.Remove(entry.Id);
        touchedEntryIds.Add(entry.Id);
        thisOsEntries.Add(entry with { Approval = null, Source = ToolkitFileReader.ToolkitEntrySource.User });
        Persist();
    }

    /// Serialises user entries (without approvals) to a toolkit.json string for export.
    public string Export()
    {
        DoLoad();
        var parts = thisOsEntries.Select(EntryToJson).ToList();
        return $"{{\"version\":1,\"entries\":[{string.Join(",", parts)}]}}";
    }

    /// Removes a user entry. No command is executed.
    public void Remove(string id)
    {
        RequireLoaded();
        thisOsEntries.RemoveAll(e => e.Id == id);
        approvals.Remove(id);
        touchedEntryIds.Add(id);
        Persist();
    }

    // ── Load / persist ────────────────────────────────────────────────────────

    private void DoLoad()
    {
        if (loaded) return;
        loaded = true;
        var filePath = FilePath;
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { fileError = Locale.Get("settings.toolkit.errorBanner"); return; }
            if (!root.TryGetProperty("version", out var ver) || !ver.TryGetInt32(out int version) || version != 1) { fileError = Locale.Get("settings.toolkit.errorBanner"); return; }
            if (!root.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array) { fileError = Locale.Get("settings.toolkit.errorBanner"); return; }

            foreach (var item in entriesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String) continue;
                var itemId = idProp.GetString() ?? "";
                if (itemId.Length == 0) continue;
                allEntryIds.Add(itemId);
                allEntryRawText[itemId] = item.GetRawText();
                if (IsOtherOsItem(item))
                {
                    otherOsIds.Add(itemId);
                    continue;
                }
                // Strip the approval field before decoding (store manages it separately).
                var entry = ToolkitFileReader.ParseEntry(item);
                if (entry is null) continue;
                thisOsEntries.Add(entry with { Approval = null, Source = ToolkitFileReader.ToolkitEntrySource.User });
                if (item.TryGetProperty("approval", out var approvalEl) && approvalEl.ValueKind == JsonValueKind.Object)
                {
                    if (approvalEl.TryGetProperty("contentHash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
                    {
                        var hash = hashEl.GetString() ?? "";
                        string? commit = null;
                        if (approvalEl.TryGetProperty("resolvedCommit", out var commitEl) && commitEl.ValueKind == JsonValueKind.String)
                            commit = commitEl.GetString();
                        if (hash.Length > 0) approvals[entry.Id] = new ToolkitFileReader.ToolkitApproval(hash, commit);
                    }
                }
            }
        }
        catch { fileError = Locale.Get("settings.toolkit.errorBanner"); }
    }

    private void RequireLoaded()
    {
        DoLoad();
        if (fileError is not null) throw new InvalidOperationException(fileError);
    }

    private void Persist()
    {
        var dir = directory;
        Directory.CreateDirectory(dir);
        var thisOsIds = new HashSet<string>(thisOsEntries.Select(e => e.Id));
        var entryLines = new List<string>();

        // Emit entries in file order; untouched entries use original bytes.
        foreach (var id in allEntryIds)
        {
            if (otherOsIds.Contains(id))
            {
                if (allEntryRawText.TryGetValue(id, out var rawText)) entryLines.Add(rawText);
            }
            else if (touchedEntryIds.Contains(id))
            {
                if (thisOsIds.Contains(id))
                    entryLines.Add(BuildEntryLine(thisOsEntries.First(e => e.Id == id)));
                // else: removed — skip
            }
            else
            {
                // Untouched thisOS entry: emit original bytes if still present.
                if (thisOsIds.Contains(id) && allEntryRawText.TryGetValue(id, out var rawText))
                    entryLines.Add(rawText);
            }
        }

        // New entries added this session (not in the original file).
        foreach (var entry in thisOsEntries)
            if (!allEntryIds.Contains(entry.Id))
                entryLines.Add(BuildEntryLine(entry));

        // Canonical file form: sorted top-level keys, 2-space indent, LF, one trailing newline.
        var body = entryLines.Count > 0
            ? "\n    " + string.Join(",\n    ", entryLines) + "\n  "
            : "";
        var json = "{\n  \"entries\": [" + body + "],\n  \"version\": 1\n}\n";
        var tmpPath = FilePath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, json, Encoding.UTF8);
            File.Move(tmpPath, FilePath, overwrite: true);
        }
        finally { if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { } }
    }

    // Builds one entry line for Persist(): canonical form with sorted keys, approval first.
    private string BuildEntryLine(ToolkitFileEntry entry)
    {
        var install = BuildInstallJson(entry);
        if (approvals.TryGetValue(entry.Id, out var approval))
        {
            var a = approval.ResolvedCommit is null
                ? $"\"contentHash\":{Jstr(approval.ContentHash)}"
                : $"\"contentHash\":{Jstr(approval.ContentHash)},\"resolvedCommit\":{Jstr(approval.ResolvedCommit)}";
            // Keys sorted: approval < displayName < id < install
            return $"{{\"approval\":{{{a}}},\"displayName\":{Jstr(entry.DisplayName)},\"id\":{Jstr(entry.Id)},\"install\":{install}}}";
        }
        return $"{{\"displayName\":{Jstr(entry.DisplayName)},\"id\":{Jstr(entry.Id)},\"install\":{install}}}";
    }

    // ── Canonical hash ────────────────────────────────────────────────────────

    /// SHA-256 of the canonical JSON for the entry (no approval, sorted keys).
    /// Matches ToolkitStore.canonicalHash(_:) on macOS.
    public static string CanonicalHash(ToolkitFileEntry entry)
    {
        var json = BuildCanonicalJson(entry);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildCanonicalJson(ToolkitFileEntry entry)
    {
        // Alphabetical key order: displayName, id, install
        return $"{{\"displayName\":{Jstr(entry.DisplayName)},\"id\":{Jstr(entry.Id)},\"install\":{BuildInstallJson(entry)}}}";
    }

    private static string BuildInstallJson(ToolkitFileEntry entry) => entry.Install switch
    {
        // Sorted keys: kind, pluginID, source
        ToolkitFileReader.PluginSpec ps =>
            $"{{\"kind\":\"plugin\",\"pluginID\":{Jstr(ps.PluginId)},\"source\":{Jstr(ps.Source)}}}",
        // Sorted keys: args, executable, kind, name
        ToolkitFileReader.McpSpec ms =>
            $"{{\"args\":{JsonSerializer.Serialize(ms.Args, Wire.Json)},\"executable\":{Jstr(ms.Executable)},\"kind\":\"mcp\",\"name\":{Jstr(ms.Name)}}}",
        // Sorted keys: kind, url
        ToolkitFileReader.SkillSpec ss =>
            $"{{\"kind\":\"skill\",\"url\":{Jstr(ss.Url)}}}",
        // Sorted keys: executable, kind, manager, name  (winget has executable)
        ToolkitFileReader.PackageSpec { Manager: "winget", Executable: { } exe } pks =>
            $"{{\"executable\":{Jstr(exe)},\"kind\":\"package\",\"manager\":\"winget\",\"name\":{Jstr(pks.Name)}}}",
        // Sorted keys: kind, manager, name  (brew/npm)
        ToolkitFileReader.PackageSpec pks =>
            $"{{\"kind\":\"package\",\"manager\":{Jstr(pks.Manager)},\"name\":{Jstr(pks.Name)}}}",
        // Sorted keys: kind, ref, scriptPath, url
        ToolkitFileReader.RepoScriptSpec rs =>
            $"{{\"kind\":\"repoScript\",\"ref\":{Jstr(rs.Ref)},\"scriptPath\":{Jstr(rs.ScriptPath)},\"url\":{Jstr(rs.Url)}}}",
        _ => "{}",
    };

    // Serialize a string to a JSON string literal (with proper escaping).
    private static string Jstr(string? s) => JsonSerializer.Serialize(s ?? "", Wire.Json);

    // ── JSON serialization of an entry (no approval) ─────────────────────────

    private static string EntryToJson(ToolkitFileEntry entry)
    {
        // Alphabetical key order: displayName, id, install (approval added later by Persist)
        return $"{{\"displayName\":{Jstr(entry.DisplayName)},\"id\":{Jstr(entry.Id)},\"install\":{BuildInstallJson(entry)}}}";
    }

    // ── Platform helpers ─────────────────────────────────────────────────────

    // brew and repoScript are macOS-only; everything else (plugin, mcp, skill, npm, winget) runs on Windows.
    private static bool IsOtherOsItem(JsonElement item)
    {
        if (!item.TryGetProperty("install", out var inst) || inst.ValueKind != JsonValueKind.Object) return false;
        if (!inst.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String) return false;
        var kind = kindEl.GetString() ?? "";
        if (kind == "repoScript") return true;
        if (kind == "package" && inst.TryGetProperty("manager", out var mgrEl) && mgrEl.ValueKind == JsonValueKind.String)
            return mgrEl.GetString() == "brew";
        return false;
    }
}
