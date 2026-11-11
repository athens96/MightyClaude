using System.Globalization;
using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// The last list each CLI gave, by provider and workspace folder (M/MightyCore/KeptModelLists.swift), so a read that fails later
/// (offline, signed out, a CLI that changed its flags), or fails at the next launch, shows that list marked as kept instead of the
/// built-in one. <see cref="ProviderCatalog"/> keeps it in memory and, with a storage folder (the profile folder,
/// <c>%APPDATA%\MightyCode</c>), in <see cref="FileName"/> there: bounded, written when a list changes (or its file is
/// <see cref="RewriteAge"/> old), readable by this user only, and read tolerantly (a bad file or entry is left out, never an error).
/// Hermes is not here: its model list is the picker its last session left.
/// </summary>
public sealed class KeptModelLists
{
    /// <param name="Workspace">The workspace folder the list was read in; null for a read outside any (the app's runtime summary).</param>
    /// <param name="ListedAt">When the CLI gave it (ISO 8601).</param>
    public sealed record Entry(string Provider, string? Workspace, string ListedAt, string Detail, List<ModelOption> Models);
    private sealed record Document(int Version, List<Entry> Lists);

    public const string FileName = "model-lists.json";
    public const int MaximumBytes = 2 * 1_048_576;
    public const int MaximumEntries = 24;
    /// <summary>The most rows a list keeps (the largest a reader gives, opencode's default and 1 024 models).</summary>
    public const int MaximumModels = 1_025;
    /// <summary>A list read again unchanged is written again only once the file is this old.</summary>
    public static readonly TimeSpan RewriteAge = TimeSpan.FromMinutes(5);

    private readonly List<Entry> entries;
    /// <summary>Newest read first.</summary>
    public IReadOnlyList<Entry> Entries => entries;
    public KeptModelLists(IEnumerable<Entry>? entries = null) => this.entries = entries?.ToList() ?? [];

    public static string? FilePath(string? directory) => directory is null ? null : Path.Combine(directory, FileName);

    /// <summary>The file's lists; a missing, oversized or unreadable file gives none, and an entry that does not check out is left out.</summary>
    public static KeptModelLists Load(string? path)
    {
        var lists = new KeptModelLists();
        if (path is null) return lists;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Length > MaximumBytes) return lists;
            var data = File.ReadAllBytes(path);
            if (data.Length > MaximumBytes) return lists;
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || MetadataJson.Integer(root, "version") != 1 || MetadataJson.Property(root, "lists") is not { ValueKind: JsonValueKind.Array } rows) return lists;
            foreach (var row in rows.EnumerateArray().Take(MaximumEntries * 4))
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                // Read as the Mac's decoder reads an entry: every field it does not take as optional must be there.
                Entry? entry;
                try { entry = row.Deserialize<Entry>(RemoteWire.Json); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { continue; }
                if (entry is null || Validated(entry) is not { } valid || lists.entries.Any(e => e.Provider == valid.Provider && e.Workspace == valid.Workspace)) continue;
                lists.entries.Add(valid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new KeptModelLists(); }
        // Newest first, as Remember keeps them (a stable order for the same time).
        var sorted = lists.entries.Select((entry, index) => (entry, index)).OrderByDescending(item => Date(item.entry.ListedAt) ?? DateTimeOffset.MinValue).ThenBy(item => item.index).Select(item => item.entry).ToList();
        lists.entries.Clear(); lists.entries.AddRange(sorted);
        lists.Trim();
        return lists;
    }

    /// <summary>The entry bounded as the file may hold it, or null when it cannot be kept.</summary>
    internal static Entry? Validated(Entry entry)
    {
        if (entry.Provider is null || !Wire.Providers.Contains(entry.Provider) || entry.Provider == "hermes" || Date(entry.ListedAt) is null
            || entry.Workspace is { } workspace && !(Path.IsPathRooted(workspace) && workspace.Length <= 4_096 && !workspace.Contains('\0'))) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = (entry.Models ?? []).Take(MaximumModels).Where(model => model is not null && Wire.Model(model.Value) && seen.Add(model.Value)).Select(model => model with
        {
            DisplayName = Wire.Clean(model.DisplayName ?? model.Value, 160),
            Description = Wire.Clean(model.Description ?? "", 2_400),
            ResolvedModel = Wire.Model(model.ResolvedModel) ? model.ResolvedModel : null,
            SupportedEffortLevels = model.SupportedEffortLevels?.Where(level => level is not null).Take(16).ToArray(),
        }).ToList();
        return models.Count == 0 ? null : new(entry.Provider, entry.Workspace, entry.ListedAt, Wire.Clean(entry.Detail ?? "", 2_000), models);
    }

    /// <summary>The file's bytes, the oldest lists left out until it fits <see cref="MaximumBytes"/>.</summary>
    public byte[] Encoded()
    {
        var kept = entries.ToList();
        while (true)
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new Document(1, kept), Wire.Json);
            if (data.Length <= MaximumBytes || kept.Count == 0) return data;
            kept.RemoveAt(kept.Count - 1);
        }
    }

    /// <summary>The list kept for this workspace, else the newest one this provider gave anywhere.</summary>
    public Entry? List(string provider, string? workspace) =>
        entries.FirstOrDefault(e => e.Provider == provider && e.Workspace == workspace) ?? entries.FirstOrDefault(e => e.Provider == provider);

    /// <summary>
    /// Keeps <paramref name="catalog"/> (a list the CLI just gave) as this provider and workspace's list, first as the newest read; true when
    /// the file should be written: the list changed, or <paramref name="writtenAt"/> (the file's last write) is <see cref="RewriteAge"/> old.
    /// A kept or built-in list is never kept.
    /// </summary>
    public bool Remember(ModelCatalog catalog, string provider, string? workspace, DateTimeOffset at, DateTimeOffset? writtenAt)
    {
        if (!catalog.IsFresh || Validated(new(provider, workspace, Timestamp(at), catalog.Detail, catalog.Models)) is not { } entry) return false;
        var old = entries.FindIndex(e => e.Provider == provider && e.Workspace == workspace);
        var changed = old < 0 || entries[old].Detail != entry.Detail || JsonSerializer.Serialize(entries[old].Models, Wire.Json) != JsonSerializer.Serialize(entry.Models, Wire.Json);
        if (old >= 0) entries.RemoveAt(old);
        entries.Insert(0, entry);
        Trim();
        return changed || writtenAt is not { } written || at - written >= RewriteAge;
    }

    /// <summary>At most <see cref="MaximumEntries"/>: the oldest reads go.</summary>
    private void Trim() { if (entries.Count > MaximumEntries) entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries); }

    /// <summary><paramref name="entry"/>'s models as the list a read that failed for <paramref name="reason"/> answers with.</summary>
    public static ModelCatalog Kept(Entry entry, string reason) => new("cli", entry.Models.ToList(), entry.Detail, reason, entry.ListedAt);

    /// <summary>ISO 8601 in UTC to the second, as the Mac's ISO8601DateFormatter writes it.</summary>
    public static string Timestamp(DateTimeOffset date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    public static DateTimeOffset? Date(string? text) =>
        text is { Length: > 0 and <= 64 } && DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

/// <summary>The muted line under a model menu's refresh row, and the failure its help shows (M/MightyCore/KeptModelLists.swift ModelListNotice).</summary>
public sealed record ModelListNotice(string Line, string Help);

public static class ModelListNotices
{
    /// <summary>
    /// What the model menu says about this list: the built-in list after a read that failed, or the last list the CLI gave when a newer
    /// read failed. A list the CLI just gave, or one nothing has read yet, needs no line.
    /// </summary>
    public static ModelListNotice? Notice(this ModelCatalog catalog, DateTimeOffset? now = null)
    {
        if (catalog.ReadFailure is not { Length: > 0 } failure) return null;
        if (catalog.Source != "cli") return new(Locale.Get("composer.model.listFallback"), failure);
        var age = KeptModelLists.Date(catalog.ListedAt) is { } listed ? ResumableSessions.RelativeTime(listed, now ?? DateTimeOffset.UtcNow) : "—";
        return new(Locale.Get("composer.model.listKept", new Dictionary<string, string> { ["age"] = age }), failure);
    }
}
