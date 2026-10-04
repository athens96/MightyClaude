using System.Text;
using System.Text.Json;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace MightyClaude.Core;

/// One transfer at a time, with explicit compressed/plaintext limits. Decode
/// is streaming with a bounded zstd window, never size-trusting allocation.
public sealed class ScreenClipboard
{
    public const int MaximumBytes = 1048576;
    private readonly object sync = new();
    private string? id, encoding;
    private int expected, total, next;
    private DateTimeOffset started;
    private MemoryStream? bytes;
    public string? Receive(JsonElement value, DateTimeOffset now)
    {
        lock (sync)
        {
            try
            {
                if (value.Text("dir") != "to-mac" || value.Text("id") is not { Length: > 0 and <= 64 } incoming || value.Text("enc") is not ("raw" or "zstd") || !value.TryGetProperty("seq", out var seq) || !seq.TryGetInt32(out var index) || !value.TryGetProperty("total", out var t) || !t.TryGetInt32(out var count) || !value.TryGetProperty("bytes", out var b) || !b.TryGetInt32(out var declared) || declared is < 1 or > MaximumBytes || count is < 1 or > 64 || index < 0 || index >= count) { Clear(); return null; }
                if (incoming != id || bytes is not null && now - started > TimeSpan.FromSeconds(30))
                {
                    Clear(); if (index != 0) return null; id = incoming; encoding = value.Text("enc"); expected = declared; total = count; started = now; bytes = new();
                }
                if (bytes is null || encoding != value.Text("enc") || expected != declared || total != count || index != next) { Clear(); return null; }
                var chunk = Convert.FromBase64String(value.Text("data") ?? ""); if (bytes.Length + chunk.Length > MaximumBytes) { Clear(); return null; }
                bytes.Write(chunk); next++; if (next != total) return null;
                bytes.Position = 0; byte[] plain;
                if (encoding == "raw") plain = bytes.ToArray();
                else
                {
                    using var decoder = new DecompressionStream(bytes); decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 20);
                    using var output = new MemoryStream(); var buffer = new byte[8192]; int length;
                    while ((length = decoder.Read(buffer)) > 0) { if (output.Length + length > expected || output.Length + length > MaximumBytes) { Clear(); return null; } output.Write(buffer, 0, length); }
                    plain = output.ToArray();
                }
                if (plain.Length != expected) { Clear(); return null; }
                var result = new UTF8Encoding(false, true).GetString(plain); Clear(); return result;
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException or IOException or ZstdException or InvalidOperationException) { Clear(); return null; }
        }
    }
    private void Clear() { id = encoding = null; expected = total = next = 0; bytes?.Dispose(); bytes = null; }
    public static IReadOnlyList<object> Encode(string? text)
    {
        var id = Wire.Id();
        if (text is null) return [new { t = "clipboard", dir = "to-phone", id, seq = 0, total = 1, enc = "raw", bytes = 0, data = "", concealed = true }];
        var plain = Encoding.UTF8.GetBytes(text); if (plain.Length is 0 or > MaximumBytes) return [];
        using var compressor = new Compressor(3); var packed = compressor.Wrap(plain).ToArray(); var compressed = packed.Length < plain.Length; var data = compressed ? packed : plain; var count = (data.Length + 32767) / 32768;
        return Enumerable.Range(0, count).Select(i => (object)new { t = "clipboard", dir = "to-phone", id, seq = i, total = count, enc = compressed ? "zstd" : "raw", bytes = plain.Length, data = Convert.ToBase64String(data.AsSpan(i * 32768, Math.Min(32768, data.Length - i * 32768))) }).ToArray();
    }
}
