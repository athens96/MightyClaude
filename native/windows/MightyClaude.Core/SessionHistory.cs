using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// <summary>
/// The session record a pane's CLI keeps on disk: Claude's
/// <c>&lt;config&gt;/projects/&lt;escaped cwd&gt;/&lt;session&gt;.jsonl</c> or Codex's
/// <c>sessions/YYYY/MM/DD/rollout-…-&lt;thread&gt;.jsonl</c>. macOS tells a replaced file
/// apart by device and inode; .NET has no inode, so the file's creation time
/// stands in (an atomic replace or a rotation creates a new file).
/// </summary>
public sealed record SessionHistoryFile(string Path, long Created);

/// <summary>The retained request the loaded history continues above: its text and, when known, when it was sent.</summary>
public sealed record SessionHistoryAnchor(string Text, DateTimeOffset? Date)
{
    /// What the user typed: the transcript adds one "첨부: " line per attachment.
    public static string Typed(string text)
    {
        var typed = text.StartsWith("첨부: ", StringComparison.Ordinal) ? "" : text.Split("\n\n첨부: ")[0];
        return typed.Trim();
    }
    /// 2 when the record prompt is the retained text, 1 when it only contains it,
    /// null otherwise. With both dates known it must also be written around the
    /// same time (the CLI records it a moment after the app sent it).
    public int? Quality(string prompt, DateTimeOffset? date)
    {
        var typed = Typed(Text); prompt = prompt.Trim();
        if (typed.Length == 0 || !(prompt == typed || prompt.Contains(typed, StringComparison.Ordinal))) return null;
        if (Date is { } anchor && date is { } at && (at < anchor.AddSeconds(-600) || at > anchor.AddSeconds(120))) return null;
        return prompt == typed ? 2 : 1;
    }
    public double Distance(DateTimeOffset? date) => Date is { } anchor && date is { } at ? Math.Abs((at - anchor).TotalSeconds) : 0;
    /// Written after any prompt that could match: a newer, retained request.
    public bool Newer(DateTimeOffset? date) => Date is { } anchor && date is { } at && at > anchor.AddSeconds(120);
    /// Written before any prompt that could still match: the search is over.
    public bool Below(DateTimeOffset? date) => Date is { } anchor && date is { } at && at < anchor.AddSeconds(-600);
    /// Written before the retained request was sent: older history even unmatched.
    public bool Precedes(DateTimeOffset? date) => Date is { } anchor && date is { } at && at < anchor.AddSeconds(-1);
}

public sealed record SessionHistoryRequest(string Provider, string ResumeID, string WorkspacePath)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string Home { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    /// The file an earlier chunk came from; null locates it.
    public SessionHistoryFile? File { get; init; }
    /// Where the previous chunk began; null reads from the end of the file.
    public long? End { get; init; }
    /// Only for the first chunk: newer requests than this one are already shown.
    public SessionHistoryAnchor? Anchor { get; init; }
    public int Turns { get; init; } = SessionHistory.TurnsPerChunk;
    /// Bytes read per chunk once at least one request was found.
    public long MaximumBytes { get; init; } = SessionHistory.MaximumChunkBytes;
}

/// <summary>Oldest first, each built by the live graph's own tracker.</summary>
public sealed record SessionHistoryChunk(List<MightyGraphRun> Runs, SessionHistoryFile File, long End, bool ReachedStart);

public enum SessionHistoryError { Unsupported, Missing, Changed, Unreadable }

public sealed class SessionHistoryException(SessionHistoryError error) : Exception(error.ToString())
{
    public SessionHistoryError Error { get; } = error;
}

/// <summary>
/// Older requests of a pane, read back from the CLI's own session record and
/// turned into graph runs by replaying each request through the same
/// <see cref="ExecutionGraphTracker"/> a live run uses. They are a view of the
/// record: never saved with the profile. Port of macOS MightyCore/SessionHistory.swift.
/// </summary>
public static class SessionHistory
{
    public static IReadOnlyList<string> Providers => MightyGraphSupport.Providers;
    public const int TurnsPerChunk = 10;
    public const long MaximumChunkBytes = 48L * 1_048_576;
    /// A single record line larger than this is skipped rather than held in memory.
    public const int MaximumLineBytes = 16 * 1_048_576;
    /// One request's lines kept for replay; past it only the prompt survives.
    public const int MaximumTurnBytes = 24 * 1_048_576;
    /// Text a replayed request may keep, as the saved profile bounds a run.
    public const int RunBudget = 1_048_576;

    // ── locating the record ───────────────────────────────────────────────

    /// Claude names a project folder after its working directory with every
    /// character other than an ASCII letter or digit replaced by "-".
    public static string ClaudeProjectFolder(string path) =>
        string.Concat(path.EnumerateRunes().Select(r => r.IsAscii && char.IsAsciiLetterOrDigit((char)r.Value) ? ((char)r.Value).ToString() : "-"));

    public static string ClaudeConfig(string home, IReadOnlyDictionary<string, string> environment) =>
        environment.TryGetValue("CLAUDE_CONFIG_DIR", out var configured) && configured.Length > 0 ? configured : Path.Combine(home, ".claude");

    public static string? Locate(string provider, string resumeID, string workspacePath, IReadOnlyDictionary<string, string> environment, string home)
    {
        if (!Wire.Identifier(resumeID)) return null;
        switch (provider)
        {
            case "claude":
                var projects = Path.Combine(ClaudeConfig(home, environment), "projects");
                var name = resumeID + ".jsonl";
                foreach (var path in ResumableSessions.WorkspacePaths(workspacePath))
                {
                    var candidate = Path.Combine(projects, ClaudeProjectFolder(path), name);
                    if (RecordFiles.Regular(candidate)) return candidate;
                }
                // A long path is shortened by the CLI; the session id alone still finds it.
                try
                {
                    foreach (var entry in Directory.EnumerateDirectories(projects).Take(8_192))
                    {
                        var candidate = Path.Combine(entry, name);
                        if (RecordFiles.Regular(candidate)) return candidate;
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                return null;
            case "codex":
                return RecordFiles.Sessions(CliAccountSupport.CodexHome(home, environment)) is { } sessions ? CodexRollout(sessions, resumeID) : null;
            default: return null;
        }
    }

    /// Codex thread ids are UUIDv7: the first 48 bits are the creation time,
    /// which names the day folder. Other days are walked newest first.
    public static DateTimeOffset? UuidV7Date(string value)
    {
        var hex = value.Replace("-", "");
        if (hex.Length != 32 || hex[12] != '7' || !long.TryParse(hex[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var milliseconds)) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string? CodexRollout(string sessions, string thread)
    {
        string? Find(string folder) => RecordFiles.Rollouts(folder).FirstOrDefault(r => r.Thread.Equals(thread, StringComparison.OrdinalIgnoreCase)).Path;
        if (UuidV7Date(thread) is { } created)
        {
            // Codex files a thread under the local date it was created; the day before covers a zone change.
            var day = created.ToLocalTime().Date;
            foreach (var date in new[] { day.AddDays(1), day, day.AddDays(-1) })
            {
                var folder = Path.Combine(sessions, date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture));
                if (RecordFiles.RealDirectory(folder) && Find(folder) is { } found) return found;
            }
        }
        foreach (var folder in RecordFiles.DayFolders(sessions))
            if (Find(folder) is { } found) return found;
        return null;
    }

    /// The record's identity, for a request that should read this file only.
    public static SessionHistoryFile? Identify(string path) =>
        RecordFiles.Regular(path) ? new SessionHistoryFile(path, File.GetCreationTimeUtc(path).Ticks) : null;

    // ── loading a chunk ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the next older chunk. Synchronous and file-bound: call it off the UI
    /// thread. Never reads the whole record into memory. A cancelled token stops
    /// between requests with <see cref="OperationCanceledException"/>.
    /// </summary>
    public static SessionHistoryChunk Load(SessionHistoryRequest request, CancellationToken token = default)
    {
        if (!Providers.Contains(request.Provider)) throw new SessionHistoryException(SessionHistoryError.Unsupported);
        var path = request.File?.Path ?? Locate(request.Provider, request.ResumeID, request.WorkspacePath, request.Environment, request.Home)
            ?? throw new SessionHistoryException(SessionHistoryError.Missing);
        using var handle = RecordFiles.Open(path) ?? throw new SessionHistoryException(request.File is null ? SessionHistoryError.Missing : SessionHistoryError.Changed);
        var file = new SessionHistoryFile(path, File.GetCreationTimeUtc(path).Ticks);
        if (request.File is { } known && known != file) throw new SessionHistoryException(SessionHistoryError.Changed);
        var size = RandomAccess.GetLength(handle);
        var end = request.End ?? size;
        if (end > size || end < 0) throw new SessionHistoryException(SessionHistoryError.Changed);
        var reader = new JsonlBackwardReader(handle, end, MaximumLineBytes);
        var format = request.Provider == "codex"
            ? HistoryFormat.Codex(RecordFiles.Thread(Path.GetFileName(path)) ?? request.ResumeID) : HistoryFormat.Claude;
        var scan = HistoryScan.Run(reader, format, request.End is null ? request.Anchor : null, Math.Max(1, request.Turns), request.MaximumBytes, token);
        var runs = new List<MightyGraphRun>();
        foreach (var turn in scan.Turns)
        {
            token.ThrowIfCancellationRequested();
            if (HistoryReplay.Run(turn, format) is { } run) runs.Add(run);
        }
        return new SessionHistoryChunk(runs, file, scan.End, scan.ReachedStart);
    }
}

/// <summary>Record files are opened as regular files only, never through a link.</summary>
internal static class RecordFiles
{
    private static readonly Regex RolloutName = new(@"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<thread>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\.jsonl\z");

    internal static string? Thread(string fileName) => RolloutName.Match(fileName) is { Success: true } match ? match.Groups["thread"].Value : null;

    internal static bool Regular(string path)
    {
        try { var info = new FileInfo(path); return info.Exists && info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (ArgumentException) { return false; }
    }
    internal static bool RealDirectory(string path)
    {
        try { var info = new DirectoryInfo(path); return info.Exists && info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (ArgumentException) { return false; }
    }
    internal static SafeFileHandle? Open(string path)
    {
        if (!Regular(path)) return null;
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }
    /// <c>$CODEX_HOME/sessions</c> with its own links resolved once; nothing below it may be a link.
    internal static string? Sessions(string codexHome)
    {
        var path = Path.Combine(codexHome, "sessions");
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null) path = info.ResolveLinkTarget(true)?.FullName ?? path;
        }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        return RealDirectory(path) ? path : null;
    }
    private static IEnumerable<string> NumberedFolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Where(d => Path.GetFileName(d) is { Length: > 0 } name && !name.StartsWith('.') && name.All(char.IsAsciiDigit) && RealDirectory(d))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
        }
        catch (IOException) { return []; } catch (UnauthorizedAccessException) { return []; }
    }
    /// Every day folder, newest first, at most 4 000; links are not followed.
    internal static List<string> DayFolders(string sessions)
    {
        var days = new List<string>();
        foreach (var year in NumberedFolders(sessions))
            foreach (var month in NumberedFolders(year))
                foreach (var day in NumberedFolders(month))
                {
                    days.Add(day);
                    if (days.Count >= 4_000) return days;
                }
        return days;
    }
    /// Rollout files in one day folder that are regular files, not links.
    internal static List<(string Path, string Thread, DateTimeOffset Modified)> Rollouts(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Select(path => (Path: path, Thread: Thread(Path.GetFileName(path))))
                .Where(item => item.Thread is not null && Regular(item.Path))
                .Select(item => (item.Path, item.Thread!, new DateTimeOffset(File.GetLastWriteTimeUtc(item.Path), TimeSpan.Zero))).ToList();
        }
        catch (IOException) { return []; } catch (UnauthorizedAccessException) { return []; }
    }
    /// Regular files (not links) with the extension directly in the folder.
    internal static List<(string Path, DateTimeOffset Modified)> Files(string folder, string extension)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*" + extension).Where(p => Path.GetExtension(p) == extension && !Path.GetFileName(p).StartsWith('.') && Regular(p))
                .Select(p => (p, new DateTimeOffset(File.GetLastWriteTimeUtc(p), TimeSpan.Zero))).ToList();
        }
        catch (IOException) { return []; } catch (UnauthorizedAccessException) { return []; }
    }
    internal static int Read(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            int read;
            try { read = RandomAccess.Read(handle, buffer[total..], offset + total); }
            catch (IOException) { return total; }
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    /// Visits the complete lines of the first <paramref name="limit"/> bytes in
    /// order until <paramref name="visit"/> returns false. True when every line of
    /// the file was visited; a line still being written is never visited.
    internal static bool ForEachLine(SafeFileHandle handle, long size, long limit, Func<byte[], bool> visit, int block = 64 * 1_024)
    {
        var total = Math.Min(size, Math.Max(0, limit));
        var pending = new List<byte>();
        long offset = 0;
        var chunk = new byte[block];
        while (offset < total)
        {
            var wanted = (int)Math.Min(block, total - offset);
            var read = Read(handle, chunk.AsSpan(0, wanted), offset);
            if (read <= 0) return false;
            offset += read;
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != 10) continue;
                if (pending.Count > 0)
                {
                    pending.AddRange(chunk.AsSpan(start, i - start).ToArray());
                    var line = pending.ToArray(); pending.Clear();
                    if (!visit(line)) return false;
                }
                else if (i > start && !visit(chunk[start..i])) return false;
                start = i + 1;
            }
            if (start < read) pending.AddRange(chunk.AsSpan(start, read - start).ToArray());
        }
        return offset == size && pending.Count == 0;
    }

    /// The newest value <paramref name="pick"/> finds in the record's last lines.
    internal static string? TailValue(SafeFileHandle handle, long size, Func<byte[], string?> pick)
    {
        foreach (var window in new long[] { 64 * 1_024, 256 * 1_024 })
        {
            var start = Math.Max(0, size - window);
            var data = new byte[size - start];
            if (Read(handle, data, start) != data.Length) return null;
            var lines = new List<byte[]>();
            int from = 0; var first = true;
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] != 10) continue;
                // The window's first piece is the end of a longer line.
                if (i > from && !(first && start > 0)) lines.Add(data[from..i]);
                first = false; from = i + 1;
            }
            for (var i = lines.Count - 1; i >= 0; i--) if (pick(lines[i]) is { } value) return value;
            if (start == 0) break;
        }
        return null;
    }
}

/// <summary>
/// Complete lines of a JSONL file, newest first, read in blocks from an end
/// offset towards the start. A line still being written past the last newline
/// is never returned, and a line longer than the limit is skipped whole.
/// </summary>
internal sealed class JsonlBackwardReader(SafeFileHandle handle, long end, int maximumLineBytes, int blockSize = 256 * 1_024)
{
    // Bytes [bufferStart, bufferStart + length) of the file not returned yet.
    private byte[] buffer = [];
    private int length;
    private long bufferStart = end;
    private bool aligned;
    private readonly int block = Math.Max(1, blockSize);
    private readonly int maximum = Math.Max(1, maximumLineBytes);
    /// Every line not yet returned ends before this offset.
    public long End { get; private set; } = end;
    /// A read failed: the reader stops as if the file began here.
    public bool Failed { get; private set; }

    private bool Fill()
    {
        if (bufferStart <= 0 || Failed) return false;
        var count = (int)Math.Min(block, bufferStart);
        var next = new byte[count + length];
        if (RecordFiles.Read(handle, next.AsSpan(0, count), bufferStart - count) != count) { Failed = true; return false; }
        Array.Copy(buffer, 0, next, count, length);
        buffer = next; length += count; bufferStart -= count;
        return true;
    }

    /// Drop whatever follows the last newline: it is a line still being written.
    private void Align()
    {
        aligned = true;
        while (true)
        {
            var index = length > 0 ? Array.LastIndexOf(buffer, (byte)10, length - 1) : -1;
            if (index >= 0) { length = index + 1; End = bufferStart + index + 1; return; }
            length = 0;
            if (!Fill()) { End = 0; return; }
        }
    }

    /// The previous complete line and its starting offset; null at the start.
    public (long Offset, byte[] Line)? Previous()
    {
        if (!aligned) Align();
        while (End > 0)
        {
            // The buffer ends with the newline that closes the wanted line.
            var discarding = false;
            while (true)
            {
                var searchEnd = length - 1;
                var index = searchEnd > 0 ? Array.LastIndexOf(buffer, (byte)10, searchEnd - 1) : -1;
                if (index >= 0)
                {
                    var start = index + 1;
                    var line = discarding || searchEnd - start > maximum ? [] : buffer[start..searchEnd];
                    var offset = bufferStart + start;
                    length = start; End = offset;
                    if (discarding || line.Length == 0) break;
                    return (offset, line);
                }
                if (bufferStart == 0 || Failed)
                {
                    // The first line of the file (or of what could be read).
                    var line = discarding || searchEnd <= 0 || searchEnd > maximum ? [] : buffer[..searchEnd];
                    var offset = bufferStart;
                    length = 0; End = 0;
                    if (Failed) return null;
                    return line.Length == 0 ? null : (offset, line);
                }
                if (length > maximum + 1)
                {
                    // Too long to keep: forget its bytes but remember the newline
                    // that ends it, then look for where it starts.
                    discarding = true;
                    buffer = [10]; length = 1;
                }
                if (!Fill()) { End = 0; return null; }
            }
        }
        return null;
    }
}

// ── finding requests ──────────────────────────────────────────────────────

public sealed record HistoryFormat(string Provider, string? Thread)
{
    public static readonly HistoryFormat Claude = new("claude", null);
    public static HistoryFormat Codex(string thread) => new("codex", thread);
}

public sealed record HistoryTurn(long Start, string Prompt, string? Timestamp, DateTimeOffset? Date, string? Key, List<byte[]> Lines);

public static class HistoryScan
{
    public sealed record Result(List<HistoryTurn> Turns, long End, bool ReachedStart);
    public sealed record Opening(string? Prompt, string? Timestamp, string? Key);
    /// Requests looked at before the retained one must be placed.
    public const int MaximumUndecided = 16;
    private static readonly HashSet<string> InjectedTurnOrigins = ["peer", "task_notification", "task-notification"];

    public static bool Contains(byte[] line, string pattern) => line.AsSpan().IndexOf(Encoding.UTF8.GetBytes(pattern)) >= 0;

    internal static JsonElement? Parse(byte[] line)
    {
        try { using var document = JsonDocument.Parse(line); return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null; }
        catch (JsonException) { return null; }
    }

    internal static DateTimeOffset? Date(string? timestamp) => AgentRunTiming.Parse(timestamp);

    /// The line that opens a request, cheaply rejected on raw bytes first: a key
    /// in raw JSON (<c>"toolUseResult":</c>) can never appear inside a string.
    public static Opening? OpeningOf(byte[] line, HistoryFormat format)
    {
        if (format.Provider == "claude")
        {
            if (!Contains(line, "\"type\":\"user\"") || Contains(line, "\"toolUseResult\":") || Parse(line) is not { } value || ClaudePrompt(value) is not { } prompt) return null;
            return new(prompt, value.Text("timestamp"), value.Text("uuid"));
        }
        if (!Contains(line, "\"task_started\"") || Parse(line) is not { } codex || codex.Text("type") != "event_msg") return null;
        var payload = MetadataJson.Property(codex, "payload");
        return payload.Text("type") == "task_started" ? new(null, codex.Text("timestamp"), payload.Text("turn_id")) : null;
    }

    /// No origin (stream input) or one the user typed; never a notification, a
    /// peer agent or any other injected kind.
    public static bool HumanOrigin(JsonElement origin) =>
        origin.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || origin.Text("kind") == "human";

    /// <summary>
    /// A request the user sent: not a tool result, an injected notification or
    /// peer message, a meta line, a sub-agent line, a compaction summary, an
    /// interruption or a local command's printed output. Interactive <c>claude</c>
    /// stamps what the user typed with a human origin; <c>-p</c> stream input has none.
    /// </summary>
    public static string? ClaudePrompt(JsonElement value)
    {
        if (value.Text("type") != "user" || MetadataJson.Flag(value, "isMeta") || MetadataJson.Flag(value, "isSidechain") || MetadataJson.Flag(value, "isCompactSummary")
            || !HumanOrigin(MetadataJson.Property(value, "origin")) || InjectedTurnOrigins.Contains(value.Text("turnOrigin") ?? "")) return null;
        var message = MetadataJson.Property(value, "message");
        if (message.ValueKind != JsonValueKind.Object) return null;
        var content = MetadataJson.Property(message, "content");
        string text;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var blocks = content.EnumerateArray().ToList();
            if (blocks.Any(b => b.Text("type") == "tool_result")) return null;
            text = string.Join("\n", blocks.Where(b => b.Text("type") == "text").Select(b => b.Text("text")).OfType<string>());
        }
        else return null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal) || trimmed.StartsWith("<local-command-", StringComparison.Ordinal)) return null;
        // A slash command the user typed is recorded as tags only.
        return trimmed.StartsWith("<command-", StringComparison.Ordinal) ? CommandPrompt(trimmed) : trimmed;
    }

    /// A slash command is recorded as tags; show it as it was typed.
    public static string? CommandPrompt(string text)
    {
        string? Tag(string name)
        {
            var open = "<" + name + ">"; var start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;
            var end = text.IndexOf("</" + name + ">", start + open.Length, StringComparison.Ordinal);
            return end < 0 ? null : text[(start + open.Length)..end].Trim();
        }
        if (Tag("command-name") is not { Length: > 0 } name) return null;
        if (!name.StartsWith('/')) name = "/" + name;
        var arguments = Tag("command-args") ?? "";
        return arguments.Length == 0 ? name : name + " " + arguments;
    }

    /// What a local command (<c>/model</c>, <c>/login</c>) printed: shown as the
    /// command's result, never as a request of its own. Null for other text.
    public static string? LocalCommandOutput(string text)
    {
        var trimmed = text.Trim();
        foreach (var tag in new[] { "local-command-stdout", "local-command-stderr" })
        {
            if (!trimmed.StartsWith("<" + tag + ">", StringComparison.Ordinal)) continue;
            var inner = trimmed[(tag.Length + 2)..];
            if (inner.EndsWith("</" + tag + ">", StringComparison.Ordinal)) inner = inner[..^(tag.Length + 3)];
            return Regex.Replace(inner, "\u001B\\[[0-9;?]*[A-Za-z]", "").Trim();
        }
        return null;
    }

    /// <summary>
    /// The user's text of a Codex turn, one rule for the history and the session
    /// list: the <c>UserMessage</c> item (0.153 and later), else the
    /// <c>user_message</c> event (0.147 and earlier), else the first user message
    /// item the model saw that is not injected context.
    /// </summary>
    public static string? CodexPrompt(IEnumerable<byte[]> lines)
    {
        string? evented = null, response = null;
        foreach (var line in lines)
        {
            if (Contains(line, "\"UserMessage\"") && CodexUserItem(line) is { } text) return text;
            if (evented is null && Contains(line, "\"user_message\"")) evented = CodexUserEvent(line);
            if (response is null && Contains(line, "\"role\":\"user\"")) response = CodexUserResponse(line);
        }
        return evented ?? response;
    }
    /// Whether a line can carry a Codex turn's user text at all.
    public static bool CodexUserLine(byte[] line) =>
        Contains(line, "\"UserMessage\"") || Contains(line, "\"user_message\"") || Contains(line, "\"role\":\"user\"");
    private static JsonElement? CodexPayload(byte[] line, string type) =>
        Parse(line) is { } value && value.Text("type") == type && MetadataJson.Property(value, "payload") is { ValueKind: JsonValueKind.Object } payload ? payload : null;
    private static string? NonEmpty(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    private static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    public static string? CodexUserItem(byte[] line)
    {
        if (CodexPayload(line, "event_msg") is not { } payload || payload.Text("type") != "item_completed") return null;
        var item = MetadataJson.Property(payload, "item");
        if (item.Text("type") != "UserMessage") return null;
        return NonEmpty(string.Join("\n", Array(MetadataJson.Property(item, "content")).Select(c => c.Text("text")).OfType<string>()));
    }
    public static string? CodexUserEvent(byte[] line) =>
        CodexPayload(line, "event_msg") is { } payload && payload.Text("type") == "user_message" ? NonEmpty(payload.Text("message")) : null;
    public static string? CodexUserResponse(byte[] line)
    {
        if (CodexPayload(line, "response_item") is not { } payload || payload.Text("type") != "message" || payload.Text("role") != "user") return null;
        var text = NonEmpty(string.Join("\n", Array(MetadataJson.Property(payload, "content")).Where(c => c.Text("type") == "input_text").Select(c => c.Text("text")).OfType<string>()));
        return text is null || text.StartsWith('<') || text.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal) ? null : text;
    }

    /// The undecided requests (newest first) the retained one is among: the same
    /// text before only a containing one, then the closest send time.
    public static int? AnchorIndex(IReadOnlyList<HistoryTurn> turns, SessionHistoryAnchor anchor)
    {
        (int Index, int Quality, double Distance)? best = null;
        for (var index = 0; index < turns.Count; index++)
        {
            if (anchor.Quality(turns[index].Prompt, turns[index].Date) is not { } quality) continue;
            var distance = anchor.Distance(turns[index].Date);
            if (best is { } current && (quality < current.Quality || (quality == current.Quality && distance >= current.Distance))) continue;
            best = (index, quality, distance);
        }
        return best?.Index;
    }

    /// <summary>
    /// Walks back from the reader's end. With an anchor, requests at or after the
    /// retained one are passed over (they are already on screen) and the chunk
    /// starts right above it. Stops after <paramref name="wanted"/> requests, or
    /// once <paramref name="maximumBytes"/> were read and one request was found.
    /// </summary>
    internal static Result Run(JsonlBackwardReader reader, HistoryFormat format, SessionHistoryAnchor? anchor, int wanted, long maximumBytes, CancellationToken token)
    {
        var collected = new List<HistoryTurn>();
        var pending = new List<byte[]>(); long pendingBytes = 0;
        // Requests near the retained one, newest first, until it is placed.
        var undecided = new List<HistoryTurn>();
        var past = anchor is null;
        long end = reader.End, read = 0;
        void Keep(byte[] line)
        {
            // An oversized request keeps the lines nearest its end; a Codex turn
            // also keeps the lines that carry its user text.
            if (pendingBytes + line.Length > SessionHistory.MaximumTurnBytes && !(format.Provider == "codex" && CodexUserLine(line))) return;
            pending.Add(line); pendingBytes += line.Length;
        }
        bool Take(HistoryTurn turn)
        {
            collected.Add(turn); end = turn.Start;
            return collected.Count >= wanted || read >= maximumBytes;
        }
        // Places the retained request among the undecided ones: what is older
        // than it is history. Without a match, what was written before it was sent is.
        bool Place()
        {
            past = true;
            if (anchor is null) return false;
            var start = AnchorIndex(undecided, anchor) is { } found ? found + 1 : undecided.FindIndex(t => anchor.Precedes(t.Date)) is var precedes and >= 0 ? precedes : undecided.Count;
            var older = undecided.Skip(start).ToList();
            undecided.Clear();
            foreach (var turn in older) if (Take(turn)) return true;
            return false;
        }
        Result Full() { collected.Reverse(); return new(collected, end, false); }
        while (reader.Previous() is var (offset, line))
        {
            read += line.Length + 1;
            if (OpeningOf(line, format) is not { } opening) { Keep(line); continue; }
            token.ThrowIfCancellationRequested();
            pending.Reverse();
            var lines = pending.ToList();
            pending.Clear(); pendingBytes = 0;
            if (format.Provider == "codex") lines.Insert(0, line);
            var prompt = opening.Prompt ?? CodexPrompt(lines);
            var date = Date(opening.Timestamp);
            if (prompt is null) { end = offset; continue; }
            var turn = new HistoryTurn(offset, prompt, opening.Timestamp, date, opening.Key, lines);
            if (!past && anchor is not null)
            {
                end = offset;
                if (anchor.Below(date))
                {
                    if (Place() || Take(turn)) return Full();
                    continue;
                }
                var quality = anchor.Quality(prompt, date);
                if (anchor.Date is not null && date is not null)
                {
                    // Written after the retained request could have been: on screen.
                    if (anchor.Newer(date)) continue;
                    undecided.Add(turn);
                }
                else
                {
                    // Without dates only the text tells; until a candidate shows
                    // up the requests are the newer, retained ones.
                    if (quality is null && undecided.Count == 0) continue;
                    undecided.Add(turn);
                    if (quality == 2)
                    {
                        if (Place()) return Full();
                        continue;
                    }
                }
                if (undecided.Count >= MaximumUndecided && Place()) return Full();
                continue;
            }
            if (Take(turn)) return Full();
        }
        if (reader.Failed) throw new SessionHistoryException(SessionHistoryError.Unreadable);
        if (!past && Place()) return Full();
        // Lines before the first request belong to no request.
        collected.Reverse();
        return new(collected, 0, true);
    }
}

// ── replaying a request ───────────────────────────────────────────────────

/// <summary>
/// Feeds one recorded request through a fresh <see cref="ExecutionGraphTracker"/>,
/// the one a live Windows run uses, and builds its graph run.
/// </summary>
public static class HistoryReplay
{
    /// Ids of runs read from the record start with this, so they never meet a
    /// retained run's id.
    public const string RunPrefix = "record-";
    public static string RunID(HistoryTurn turn) =>
        turn.Key is { } key && Wire.Identifier(RunPrefix + key) ? RunPrefix + key : RunPrefix + "o" + turn.Start.ToString(CultureInfo.InvariantCulture);

    public static MightyGraphRun? Run(HistoryTurn turn, HistoryFormat format)
    {
        var provider = format.Provider;
        var model = provider == "codex" ? CodexModel(turn.Lines) : null;
        var runID = RunID(turn);
        var tracker = new ExecutionGraphTracker(runID, turn.Prompt, provider, model, _ => { });
        DateTimeOffset? latest = turn.Date;
        void At(JsonElement value) { if (HistoryScan.Date(value.Text("timestamp")) is { } date) latest = date; }
        var status = provider == "codex" ? Codex(turn.Lines, format.Thread ?? "", tracker, At) : Claude(turn.Lines, tracker, At);
        tracker.Finish(status);
        if (tracker.BuildRun() is not { } run || run.Id != runID) return null;
        if (run.NodeModelLabel is null && model is not null) run.NodeModelLabel = GraphModelLabel.NodeModelLabel(model, "default");
        // Blocks the tracker made carry the replay's own clock; the request
        // happened when its record says.
        if (latest is { } settled)
        {
            var stamp = settled.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            List<LogEntry> Settle(List<LogEntry> entries) =>
                entries.Select(e => AgentRunTiming.Parse(e.Timestamp) is { } date && date > settled.AddSeconds(1) ? e with { Timestamp = stamp } : e).ToList();
            run.RootEntries = Settle(run.RootEntries); run.ResultEntries = Settle(run.ResultEntries);
            foreach (var agent in run.Agents) agent.Entries = Settle(agent.Entries);
        }
        var budget = SessionHistory.RunBudget;
        return MightyGraphSupport.Normalized([run], restoring: true, budget: ref budget, provider: provider).FirstOrDefault();
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    /// A message the user typed while the request ran, recorded as a queued
    /// command: shown as the live graph's steer block. Injected kinds are not.
    public static (string? Id, string Text)? Steer(JsonElement value)
    {
        var attachment = MetadataJson.Property(value, "attachment");
        if (attachment.Text("type") != "queued_command" || attachment.Text("commandMode") != "prompt" || MetadataJson.Flag(attachment, "isMeta")
            || !HistoryScan.HumanOrigin(MetadataJson.Property(attachment, "origin"))) return null;
        var prompt = MetadataJson.Property(attachment, "prompt");
        var text = prompt.ValueKind == JsonValueKind.String ? prompt.GetString()
            : prompt.ValueKind == JsonValueKind.Array ? string.Join("\n", prompt.EnumerateArray().Where(b => b.Text("type") == "text").Select(b => b.Text("text")).OfType<string>()) : null;
        text = text?.Trim();
        return text is { Length: > 0 } && !text.StartsWith('<') ? (value.Text("uuid"), text) : null;
    }

    /// A Claude record line is the stream-json event it was printed as, minus the
    /// final <c>result</c>, which is rebuilt from the last answer.
    private static string Claude(List<byte[]> lines, ExecutionGraphTracker tracker, Action<JsonElement> at)
    {
        string? answer = null;
        bool interrupted = false, failed = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (HistoryScan.Parse(lines[index]) is not { } value || value.Text("type") is not ("assistant" or "user" or "system" or "attachment") || MetadataJson.Flag(value, "isSidechain")) continue;
            at(value);
            var consumed = value;
            switch (value.Text("type"))
            {
                case "attachment":
                    if (Steer(value) is { } steer) tracker.Steer(steer.Id ?? "line-" + index, steer.Text);
                    continue;
                case "system":
                    if (value.Text("subtype") != "compact_boundary") continue;
                    if (MetadataJson.Property(value, "compact_metadata").ValueKind == JsonValueKind.Undefined && MetadataJson.Property(value, "compactMetadata") is { ValueKind: not JsonValueKind.Undefined } metadata
                        && JsonNode.Parse(value.GetRawText()) is JsonObject node)
                    {
                        node["compact_metadata"] = JsonNode.Parse(metadata.GetRawText());
                        consumed = Element(node);
                    }
                    break;
                case "user":
                    if (MetadataJson.Flag(value, "isMeta") || MetadataJson.Flag(value, "isCompactSummary")) continue;
                    var message = MetadataJson.Property(value, "message");
                    if (message.ValueKind != JsonValueKind.Object) continue;
                    var content = MetadataJson.Property(message, "content");
                    var text = content.ValueKind == JsonValueKind.String ? content.GetString()
                        : content.ValueKind == JsonValueKind.Array ? string.Join("\n", content.EnumerateArray().Where(b => b.Text("type") == "text").Select(b => b.Text("text")).OfType<string>()) : null;
                    if (text?.StartsWith("[Request interrupted by user", StringComparison.Ordinal) == true) { interrupted = true; continue; }
                    // A local command's printed output is that command's result.
                    if (text is not null && HistoryScan.LocalCommandOutput(text) is { } output) { if (output.Length > 0) answer = output; continue; }
                    break;
                default:
                    if (MetadataJson.Flag(value, "isApiErrorMessage")) failed = true;
                    var blocks = MetadataJson.Property(MetadataJson.Property(value, "message"), "content");
                    if (blocks.ValueKind == JsonValueKind.Array && string.Join("\n", blocks.EnumerateArray().Where(b => b.Text("type") == "text").Select(b => b.Text("text")).OfType<string>()) is { Length: > 0 } reply) answer = reply;
                    break;
            }
            tracker.Consume(consumed);
        }
        if (answer is not null) tracker.Consume(Element(new Dictionary<string, object> { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = answer }));
        if (interrupted) return "stopped";
        return failed ? "error" : "completed";
    }

    public static string? CodexModel(IEnumerable<byte[]> lines)
    {
        foreach (var line in lines.Where(l => HistoryScan.Contains(l, "\"type\":\"turn_context\"")))
            if (HistoryScan.Parse(line) is { } value && MetadataJson.Property(value, "payload").Text("model") is { } model && Wire.Model(model)) return model;
        return null;
    }

    /// A rollout records <c>codex exec --json</c> items under other names; each is
    /// mapped back to the exec shape the tracker reads.
    public static JsonElement? CodexItem(JsonElement item)
    {
        if (item.Text("type") is not { } type || item.Text("id") is not { } id) return null;
        var raw = item.Text("status");
        var status = raw is null or "completed" ? "completed" : raw == "in_progress" ? "in_progress" : "failed";
        JsonElement Renamed(string name, bool withStatus)
        {
            var node = (JsonObject)JsonNode.Parse(item.GetRawText())!;
            node["type"] = name;
            if (withStatus) node["status"] = status;
            return Element(node);
        }
        switch (type)
        {
            case "AgentMessage":
                var text = string.Join("\n", (MetadataJson.Property(item, "content") is { ValueKind: JsonValueKind.Array } c ? c.EnumerateArray() : []).Select(b => b.Text("text")).OfType<string>());
                return text.Length == 0 ? null : Element(new Dictionary<string, object> { ["id"] = id, ["type"] = "agent_message", ["text"] = text });
            case "CommandExecution":
                var commandValue = MetadataJson.Property(item, "command");
                string command;
                if (commandValue.ValueKind == JsonValueKind.Array)
                {
                    var parts = commandValue.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.GetRawText()).ToList();
                    command = parts.Count == 3 && parts[1] is "-lc" or "-c" ? parts[2] : string.Join(" ", parts);
                }
                else command = item.Text("command") ?? "";
                var mapped = new Dictionary<string, object> { ["id"] = id, ["type"] = "command_execution", ["command"] = command, ["status"] = status };
                if ((item.Text("aggregated_output") ?? item.Text("stdout")) is { } output) mapped["aggregated_output"] = output;
                if (MetadataJson.Property(item, "exit_code") is { ValueKind: JsonValueKind.Number } code) mapped["exit_code"] = code;
                return Element(mapped);
            case "FileChange":
                var changes = MetadataJson.Property(item, "changes") is { ValueKind: JsonValueKind.Object } all
                    ? all.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Take(64).Select(p => new Dictionary<string, string> { ["path"] = p.Name, ["kind"] = p.Value.Text("type") ?? "update" }).ToList()
                    : [];
                return Element(new Dictionary<string, object> { ["id"] = id, ["type"] = "file_change", ["changes"] = changes, ["status"] = status });
            case "McpToolCall": return Renamed("mcp_tool_call", true);
            case "Extension":
                return item.Text("kind") == "web.search" ? Element(new Dictionary<string, object> { ["id"] = id, ["type"] = "web_search", ["query"] = item.Text("query") ?? "", ["status"] = status }) : null;
            case "CollabAgentToolCall": return Renamed("collab_tool_call", false);
            case "ContextCompaction": return Element(new Dictionary<string, object> { ["id"] = id, ["type"] = "context_compaction" });
            default: return null;
        }
    }

    private static string Codex(List<byte[]> lines, string thread, ExecutionGraphTracker tracker, Action<JsonElement> at)
    {
        var usage = new Dictionary<string, long>();
        bool completed = false, aborted = false, failed = false;
        tracker.Consume(Element(new Dictionary<string, object> { ["type"] = "thread.started", ["thread_id"] = thread }));
        foreach (var line in lines)
        {
            if (HistoryScan.Parse(line) is not { } value || value.Text("type") is not { } type || MetadataJson.Property(value, "payload") is not { ValueKind: JsonValueKind.Object } payload) continue;
            at(value);
            switch (type, payload.Text("type"))
            {
                case ("event_msg", "task_started"): tracker.Consume(Element(new Dictionary<string, object> { ["type"] = "turn.started" })); break;
                case ("event_msg", "item_completed"):
                    if (CodexItem(MetadataJson.Property(payload, "item")) is { } mapped) tracker.Consume(Element(new Dictionary<string, object> { ["type"] = "item.completed", ["item"] = mapped }));
                    break;
                case ("token_usage_record", _):
                    if (MetadataJson.Property(payload, "usage") is { ValueKind: JsonValueKind.Object } counts)
                        foreach (var count in counts.EnumerateObject())
                            if (count.Value.ValueKind == JsonValueKind.Number && count.Value.TryGetInt64(out var number) && number >= 0) usage[count.Name] = usage.GetValueOrDefault(count.Name) + number;
                    break;
                case ("event_msg", "error"):
                    if (payload.Text("message") is { Length: > 0 } message) { failed = true; tracker.Consume(Element(new Dictionary<string, object> { ["type"] = "error", ["message"] = message })); }
                    break;
                case ("event_msg", "task_complete"): completed = true; break;
                case ("event_msg", "turn_aborted"): aborted = true; break;
            }
        }
        if (completed || usage.Count > 0) tracker.Consume(Element(new Dictionary<string, object> { ["type"] = "turn.completed", ["usage"] = usage }));
        if (aborted) return "stopped";
        if (completed) return "completed";
        return failed ? "error" : "stopped";
    }
}

// ── a pane's loaded history ───────────────────────────────────────────────

/// <summary>
/// What a pane loaded from its session record, kept in memory only. The rules
/// live here so they can be tested; the app only runs the file work off the UI
/// thread and hands the result back. Port of macOS SessionHistoryState.
/// </summary>
public sealed class SessionHistoryState
{
    public enum Phases
    {
        /// More can be loaded.
        Idle,
        Loading,
        /// The record's first request is on screen.
        Start,
        /// No record, or nothing in it before what is already shown.
        Unavailable,
        Failed,
        /// The pane holds as many older requests as it keeps in memory.
        Limit,
    }
    /// Older requests kept per pane, and the bytes they may hold together; past
    /// either the oldest go and the pane stops loading.
    public const int MaximumRuns = 100;
    public const int MaximumBytes = 8 * 1_048_576;
    public List<MightyGraphRun> Runs { get; private set; } = [];
    public Phases Phase { get; private set; } = Phases.Idle;
    public SessionHistoryFile? File { get; private set; }
    public long? End { get; private set; }
    /// The retained request and session the loaded runs attach above.
    public string? AnchorRunID { get; private set; }
    public string? ResumeID { get; private set; }
    /// The run the diagram keeps where it first stood.
    public string? PinnedRunID { get; private set; }
    /// Bumped on every reset so a load that started before it is discarded.
    public int Generation { get; private set; }

    public bool CanLoad => Phase is Phases.Idle or Phases.Failed;
    private bool Touched => Runs.Count > 0 || End is not null || Phase != Phases.Idle;

    /// Whether what was loaded still attaches above this retained request.
    public bool Connects(string? anchorRunID, string? resumeID) =>
        !Touched || (resumeID == ResumeID && (anchorRunID == AnchorRunID || AnchorRunID is null));

    /// History attaches above one retained request of one session; a pane
    /// without a retained request adopts its first one.
    public void Reconcile(string? anchorRunID, string? resumeID)
    {
        if (!Touched) return;
        if (!Connects(anchorRunID, resumeID)) { Reset(); return; }
        Adopt(anchorRunID);
    }
    private void Adopt(string? id)
    {
        if (AnchorRunID is not null || id is null) return;
        AnchorRunID = id;
        PinnedRunID ??= id;
    }
    public void Reset()
    {
        var next = Generation + 1;
        Runs = []; Phase = Phases.Idle; File = null; End = null; AnchorRunID = null; ResumeID = null; PinnedRunID = null;
        Generation = next;
    }

    /// <summary>
    /// The pane's retained list changed from previous to current. A trim that
    /// dropped the anchor moves the dropped runs here, newest last, and anchors on
    /// the new first one instead of throwing the history away.
    /// </summary>
    public void Follow(IReadOnlyList<MightyGraphRun> previous, IReadOnlyList<MightyGraphRun> current, string? resumeID, string provider)
    {
        if (!Touched || previous.FirstOrDefault()?.Id == current.FirstOrDefault()?.Id) return;
        if (resumeID != ResumeID) { Reset(); return; }
        if (AnchorRunID is not { } anchor) { Adopt(current.FirstOrDefault()?.Id); return; }
        var start = previous.ToList().FindIndex(r => r.Id == anchor);
        if (start < 0 || current.FirstOrDefault() is not { } first) { Reset(); return; }
        var stop = previous.ToList().FindIndex(r => r.Id == first.Id);
        if (stop < 0) stop = previous.Count;
        if (stop <= start) { Reset(); return; }
        var known = Runs.Select(r => r.Id).ToHashSet();
        foreach (var original in previous.Skip(start).Take(stop - start).Where(r => !known.Contains(r.Id)))
        {
            var run = original.Copy(); MightyGraphSupport.ApplyProvider(run, provider); Runs.Add(run);
        }
        AnchorRunID = first.Id;
        PinnedRunID ??= anchor;
        Bound();
    }

    /// Drops the oldest runs past the count and byte limits; the pane then stops
    /// loading, since the record cursor lies above what was dropped.
    private void Bound()
    {
        var bytes = MightyGraphSupport.LiveHistoryBytes(Runs);
        var dropped = false;
        while (Runs.Count > 0 && (Runs.Count > MaximumRuns || bytes > MaximumBytes))
        {
            bytes -= MightyGraphSupport.LiveHistoryBytes([Runs[0]]);
            Runs.RemoveAt(0);
            dropped = true;
        }
        if (dropped || Runs.Count >= MaximumRuns) Phase = Phases.Limit;
    }

    /// The request for the next chunk, or null when nothing more can be loaded.
    /// Marks the state as loading.
    public SessionHistoryRequest? Begin(string? anchorRunID, string? resumeID, SessionHistoryAnchor? anchor, SessionHistoryRequest template)
    {
        Reconcile(anchorRunID, resumeID);
        if (!CanLoad) return null;
        if (resumeID is null) { AnchorRunID = anchorRunID; Phase = Phases.Unavailable; return null; }
        if (!Touched) { AnchorRunID = anchorRunID; PinnedRunID = anchorRunID; }
        ResumeID = resumeID;
        Phase = Phases.Loading;
        return template with { ResumeID = resumeID, File = File, End = End, Anchor = End is null ? anchor : null };
    }

    /// <summary>
    /// Takes a finished load; a result from before a reset is ignored. Returns
    /// true when the load should start again from scratch (the record was
    /// replaced under the cursor).
    /// </summary>
    public bool Finish(SessionHistoryChunk? chunk, Exception? error, int generation)
    {
        if (generation != Generation || Phase != Phases.Loading) return false;
        if (chunk is not null)
        {
            var known = Runs.Select(r => r.Id).ToHashSet();
            Runs = [.. chunk.Runs.Where(r => !known.Contains(r.Id)), .. Runs];
            File = chunk.File; End = chunk.End;
            Phase = chunk.ReachedStart ? (Runs.Count == 0 ? Phases.Unavailable : Phases.Start) : Phases.Idle;
            Bound();
            return false;
        }
        switch ((error as SessionHistoryException)?.Error)
        {
            case SessionHistoryError.Changed: Reset(); return true;
            case SessionHistoryError.Missing or SessionHistoryError.Unsupported: Phase = Runs.Count == 0 ? Phases.Unavailable : Phases.Start; break;
            default: Phase = Phases.Failed; break;
        }
        return false;
    }

    /// The anchor of the retained list: its first run's text and send time.
    public static SessionHistoryAnchor? AnchorFor(RunSession session)
    {
        if (session.GraphRuns is not { Count: > 0 } runs) return null;
        var first = runs[0];
        var sent = session.Logs.FirstOrDefault(l => l.Id == first.Id)?.Timestamp ?? first.RootEntries.FirstOrDefault()?.Timestamp;
        return new SessionHistoryAnchor(first.Input, AgentRunTiming.Parse(sent));
    }

    /// What the history block at the top of the diagram says (shared locale keys,
    /// macOS MightyGraphView.historyCard): the phase line and, once requests were
    /// loaded, how many. The idle and failed lines are buttons.
    public string BlockText(int loaded)
    {
        var line = Phase switch
        {
            Phases.Loading => Locale.Get("graph.history.loading"),
            Phases.Start => Locale.Get("graph.history.start"),
            Phases.Unavailable => Locale.Get(loaded > 0 ? "graph.history.start" : "graph.history.none"),
            Phases.Limit => Locale.Get("graph.history.limit"),
            Phases.Failed => Locale.Get("graph.history.failed"),
            _ => Locale.Get("graph.history.load"),
        };
        return loaded > 0 ? line + " · " + Locale.Get("graph.history.loaded", new Dictionary<string, string> { ["count"] = loaded.ToString(CultureInfo.InvariantCulture) }) : line;
    }
    public bool BlockActs => Phase is Phases.Idle or Phases.Failed;
}
