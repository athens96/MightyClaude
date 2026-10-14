using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

[JsonConverter(typeof(DelegationNameConverter<DelegationCopyKind>))]
public enum DelegationCopyKind { Task, Report }

/// <summary>
/// A stored copy of a child's TASK.md or REPORT.md (M/DelegationFileStore.swift:3-69). A copy's text never takes more than
/// <see cref="DelegationFileStore.MaximumCopyBytes"/> inside the delegation file, its JSON escapes included (quotes, backslashes,
/// newlines and colour codes take more there than in UTF-8): a longer file is cut at a character boundary and ends with
/// <see cref="TruncationMarker"/>, whose own escapes come on top, while the full file stays in the worktree until cleanup.
/// </summary>
/// <param name="Revision">The report revision this copy holds; 0 for TASK.md.</param>
/// <param name="OriginalBytes">The whole file's size in bytes.</param>
public sealed record DelegationCopy(string ChildId, DelegationCopyKind Kind, long Revision, string Text, long OriginalBytes, bool Truncated)
{
    public const string TruncationMarker = "\n\n[truncated at 64 KiB: the full file stays in the child's worktree until cleanup]\n";

    /// <summary>The copy of a file whose bytes start with <paramref name="contents"/>. Pass <paramref name="totalBytes"/> when they hold only the start of a longer file.</summary>
    public static DelegationCopy Of(string childId, DelegationCopyKind kind, long revision, ReadOnlySpan<byte> contents, long? totalBytes = null)
    {
        var original = Math.Max(totalBytes ?? contents.Length, contents.Length); const int cap = DelegationFileStore.MaximumCopyBytes;
        var decoded = Decode(contents[..Math.Min(contents.Length, cap + 1)]);
        if (original <= cap && StoredBytes(decoded) <= cap) return new(childId, kind, revision, decoded, original, false);
        return new(childId, kind, revision, Start(decoded, cap - Encoding.UTF8.GetByteCount(TruncationMarker)) + TruncationMarker, original, true);
    }

    /// <summary>
    /// A file's text by its byte-order mark: UTF-16 little or big endian, or UTF-8 with or without its mark. Windows PowerShell
    /// 5.1's <c>&gt;</c> writes UTF-16, so a report an agent wrote that way still reads as its words.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(bytes[2..]),
        [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode.GetString(bytes[2..]),
        [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(bytes[3..]),
        _ => Encoding.UTF8.GetString(bytes),
    };

    /// <summary>The bytes <paramref name="text"/> takes inside the delegation file, its escapes included.</summary>
    public static int StoredBytes(string text) => DelegationJson.EscapedBytes(text);

    /// <summary>The longest whole-character start of <paramref name="text"/> that takes at most <paramref name="budget"/> bytes inside the delegation file.</summary>
    public static string Start(string text, int budget)
    {
        // Escapes only add bytes, so the cut by UTF-8 bytes is the longest candidate.
        var starts = StringInfo.ParseCombiningCharacters(text); var count = 0; var used = 0;
        int End(int characters) => characters < starts.Length ? starts[characters] : text.Length;
        for (; count < starts.Length; count++)
        {
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(starts[count], End(count + 1) - starts[count]));
            if (used + size > budget) break;
            used += size;
        }
        if (StoredBytes(text[..End(count)]) <= budget) return text[..End(count)];
        var fits = 0; var over = count; // the first `fits` characters fit; the first `over` do not
        while (over - fits > 1)
        {
            var middle = (fits + over) / 2;
            if (StoredBytes(text[..End(middle)]) <= budget) fits = middle; else over = middle;
        }
        return text[..End(fits)];
    }

    /// <summary>Reads at most a copy's worth of the file at <paramref name="path"/>, so a huge file is never loaded whole.</summary>
    public static DelegationCopy Read(string childId, DelegationCopyKind kind, long revision, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[(int)Math.Min(stream.Length, DelegationFileStore.MaximumCopyBytes + 1)];
        var read = stream.ReadAtLeast(head, head.Length, false);
        return Of(childId, kind, revision, head.AsSpan(0, read), stream.Length);
    }

    /// <summary><see cref="Read"/>, or null when the file is not there or cannot be read.</summary>
    public static DelegationCopy? TryRead(string childId, DelegationCopyKind kind, long revision, string path)
    {
        try { return Read(childId, kind, revision, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }
}

/// <summary>
/// Everything the delegation side file holds (M/DelegationFileStore.swift:71-97). It is the authority for each child's parent
/// link and working folder; receipts travel inside their notice or follow-up. A value: every change makes a new file.
/// </summary>
public sealed partial record DelegationFile(int Version, ImmutableList<ChildRecord> Children, ImmutableList<Notice> Notices, ImmutableList<FollowUp> FollowUps,
    ImmutableList<MergeRecord> Merges, ImmutableList<DelegationCopy> Copies)
{
    public const int CurrentVersion = 1;
    public static DelegationFile Empty { get; } = Of();

    public static DelegationFile Of(IEnumerable<ChildRecord>? children = null, IEnumerable<Notice>? notices = null, IEnumerable<FollowUp>? followUps = null,
        IEnumerable<MergeRecord>? merges = null, IEnumerable<DelegationCopy>? copies = null) =>
        new(CurrentVersion, [.. children ?? []], [.. notices ?? []], [.. followUps ?? []], [.. merges ?? []], [.. copies ?? []]);

    public bool Equals(DelegationFile? other) => other is not null && Version == other.Version && Children.SequenceEqual(other.Children) && Notices.SequenceEqual(other.Notices)
        && FollowUps.SequenceEqual(other.FollowUps) && Merges.SequenceEqual(other.Merges) && Copies.SequenceEqual(other.Copies);
    public override int GetHashCode() => HashCode.Combine(Version, Children.Count, Notices.Count, FollowUps.Count, Merges.Count, Copies.Count);

    /// <summary>The stored copy of one child's TASK.md or REPORT.md.</summary>
    public DelegationCopy? Copy(string childId, DelegationCopyKind kind) => Copies.Find(copy => copy.ChildId == childId && copy.Kind == kind);

    /// <summary>This file with <paramref name="copy"/> in place of the child's earlier copy of the same kind.</summary>
    public DelegationFile SettingCopy(DelegationCopy copy) => this with { Copies = Copies.RemoveAll(old => old.ChildId == copy.ChildId && old.Kind == copy.Kind).Add(copy) };

    /// <summary>This file with the child at <paramref name="index"/> replaced.</summary>
    public DelegationFile SettingChild(int index, ChildRecord child) => this with { Children = Children.SetItem(index, child) };
}

public enum DelegationFileError
{
    /// <summary>The file would pass <see cref="DelegationFileStore.MaximumFileBytes"/> (the store_full refusal); nothing was written.</summary>
    Full,
    /// <summary>The file on disk is not a regular file, is over the cap, or is not a delegation file of this version. It is left untouched.</summary>
    Unreadable,
}

public sealed class DelegationFileException(DelegationFileError error) : Exception("The delegation file is " + (error == DelegationFileError.Full ? "full." : "unreadable."))
{
    public DelegationFileError Error { get; } = error;
}

/// <summary>
/// Reads and writes <c>delegation-state.json</c> beside <c>workspace-state.json</c> in the profile folder
/// (M/DelegationFileStore.swift:108-145). A save replaces the file atomically and is refused, writing nothing, when the file
/// would pass 4 MiB. A missing file loads as empty. Only the app process writes it. Another program can hold the file open for
/// a moment, and Windows then refuses the replace: it is tried again <paramref name="replaceAttempts"/> times 50 ms apart
/// (about 2 s) before the save fails, as <see cref="StateStore.AtomicWriteAsync"/> does. The wait is on the calling thread,
/// because the coordinator's saves must not be interleaved. <paramref name="refused"/> hears each refused read before it is tried again.
/// </summary>
public sealed class DelegationFileStore(string folder, int replaceAttempts = 40, Action? refused = null)
{
    public const string FileName = "delegation-state.json";
    public const int MaximumFileBytes = 4 * 1024 * 1024, MaximumCopyBytes = 64 * 1024;

    public string Folder { get; } = folder;
    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>The exact bytes a save writes, for measuring what still fits. A part of the file encodes to the same bytes it takes inside the file.</summary>
    public static byte[] Encode<T>(T value) => DelegationJson.Encode(value);

    /// <summary>
    /// The file as saved; empty when there is none. A read another program refuses for a moment is tried again as a save's
    /// replace is; any read that still fails, like a file that does not decode, throws <see cref="DelegationFileException"/>.
    /// </summary>
    public DelegationFile Load()
    {
        byte[] data;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists) return Directory.Exists(FilePath) ? throw new DelegationFileException(DelegationFileError.Unreadable) : DelegationFile.Empty;
                if (info.LinkTarget is not null || info.Length > MaximumFileBytes) throw new DelegationFileException(DelegationFileError.Unreadable);
                data = File.ReadAllBytes(FilePath);
                break;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < replaceAttempts) { refused?.Invoke(); Thread.Sleep(50); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { throw new DelegationFileException(DelegationFileError.Unreadable); }
        }
        DelegationFile? file = null;
        try { if (data.Length <= MaximumFileBytes) file = JsonSerializer.Deserialize<DelegationFile>(data, DelegationJson.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
        if (file is not { Version: DelegationFile.CurrentVersion } || new IEnumerable<object?>[] { file.Children, file.Notices, file.FollowUps, file.Merges, file.Copies }.Any(list => list.Contains(null)))
            throw new DelegationFileException(DelegationFileError.Unreadable);
        return file;
    }

    public void Save(DelegationFile file)
    {
        var data = Encode(file);
        if (data.Length > MaximumFileBytes) throw new DelegationFileException(DelegationFileError.Full);
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + "." + Wire.Id() + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temporary, FilePath, true); break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < replaceAttempts) { Thread.Sleep(50); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Saves <paramref name="file"/>, pruning first when it nears the cap (M/DelegationPruning.swift:118-131): when it leaves
    /// less than one more child's room, pruning frees that room if it can, and otherwise as much as the file itself needs.
    /// Throws <see cref="DelegationFileError.Full"/>, writing nothing, when even that is not enough. Returns what was written.
    /// </summary>
    public DelegationFile Save(DelegationFile file, DelegationPruneContext context)
    {
        var kept = file.Pruned(context, DelegationFile.NewChildRoom()) ?? file.Pruned(context) ?? throw new DelegationFileException(DelegationFileError.Full);
        Save(kept);
        return kept;
    }

    /// <summary>Whether <paramref name="exception"/> is a save the disk or the cap refused, which callers answer instead of letting it escape.</summary>
    public static bool IsSaveFailure(Exception exception) => exception is DelegationFileException or IOException or UnauthorizedAccessException;
}

/// <summary>
/// The delegation file's JSON: the Mac's field names and, byte for byte, its encoder's output (JSONEncoder with sorted keys),
/// so that a copy's 64 KiB and a follow-up's 48 KiB measure the same on both platforms. Keys are sorted, absent values are left
/// out, text outside ASCII is written as it is, and only quotes, backslashes, slashes and control characters are escaped.
/// </summary>
internal static class DelegationJson
{
    internal static readonly JsonSerializerOptions Options = new(Wire.Json) { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    internal static byte[] Encode<T>(T value)
    {
        var text = new StringBuilder();
        Write(JsonSerializer.SerializeToNode(value, Options), text);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static void Write(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case JsonObject fields:
                text.Append('{'); var first = true;
                foreach (var (key, value) in fields.OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    if (!first) text.Append(','); first = false;
                    Escape(key, text); text.Append(':'); Write(value, text);
                }
                text.Append('}'); break;
            case JsonArray items:
                text.Append('[');
                for (var index = 0; index < items.Count; index++) { if (index > 0) text.Append(','); Write(items[index], text); }
                text.Append(']'); break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String: Escape(value.GetValue<string>(), text); break;
            default: text.Append(node?.ToJsonString() ?? "null"); break;
        }
    }

    private static string? Escaped(char character) => character switch
    {
        '"' => "\\\"", '\\' => "\\\\", '/' => "\\/", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
        < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static void Escape(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (var character in value) { if (Escaped(character) is { } escape) text.Append(escape); else text.Append(character); }
        text.Append('"');
    }

    /// <summary>The bytes <paramref name="value"/> takes between its quotes.</summary>
    internal static int EscapedBytes(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        foreach (var character in value) if (Escaped(character) is { } escape) bytes += escape.Length - 1;
        return bytes;
    }
}
