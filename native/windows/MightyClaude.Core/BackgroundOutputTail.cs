using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// The lines a host shows for a background run: the end of its output file (native/contracts/fixtures/background-output.json).
/// Port of macOS MightyCore/BackgroundOutputTail.swift.
/// </summary>
public static class BackgroundOutputTail
{
    public const int MaxBytes = 8192;
    public const int MaxLines = 6;
    public const int MaxLineChars = 200;

    /// <summary>CSI sequences, OSC strings (ended by BEL or ST), then the other two-byte escapes.</summary>
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.CultureInvariant);
    /// <summary>Invalid UTF-8 reads as U+FFFD, as Swift's <c>String(decoding:as:)</c> reads it.</summary>
    private static readonly UTF8Encoding Utf8 = new(false, false);

    /// <summary>
    /// The last <see cref="MaxLines"/> lines of <paramref name="data"/>; <paramref name="truncated"/> says the data starts inside
    /// the file, so its first line is cut and dropped.
    /// </summary>
    public static IReadOnlyList<string> Lines(byte[] data, bool truncated)
    {
        var clean = Ansi.Replace(Utf8.GetString(data), "");
        var rows = clean.Split('\n').Select(row =>
        {
            var line = row.EndsWith('\r') ? row[..^1] : row;
            return line.Split('\r')[^1];
        }).ToList();
        if (truncated && rows.Count > 0) rows.RemoveAt(0);
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        // The cut counts Unicode scalars, as the Mac and the phone do (not grapheme clusters, not UTF-16 units).
        return rows.TakeLast(MaxLines).Select(row => row.EnumerateRunes().Count() > MaxLineChars ? ExecutionGraphSupport.Prefix(row, MaxLineChars) + "…" : row).ToList();
    }

    /// <summary>The file's last lines, or null when it cannot be read (missing, or on another computer).</summary>
    public static IReadOnlyList<string>? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0L, stream.Length - MaxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Lines(buffer.ToArray(), start > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
