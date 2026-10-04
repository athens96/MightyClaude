using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record ScreenDisplay(int DisplayId, int Width, int Height, bool Main, int Left = 0, int Top = 0);
public sealed record ScreenRegion(double X = 0, double Y = 0, double Width = 1, double Height = 1)
{
    public static ScreenRegion? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !Number(value, "x", out var x) || !Number(value, "y", out var y) || !Number(value, "width", out var w) || !Number(value, "height", out var h) || w < .02 || h < .02) return null;
        var left = Math.Clamp(x, 0, 1); var top = Math.Clamp(y, 0, 1); var width = Math.Min(1, x + w) - left; var height = Math.Min(1, y + h) - top;
        return width >= .02 && height >= .02 ? new(left, top, width, height) : null;
    }
    internal static bool Number(JsonElement value, string key, out double result)
    { result = 0; return value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out result) && double.IsFinite(result); }
}
public sealed record ScreenQuality(int Width, int Height, int Fps, int MaxBitrateKbps)
{
    public static ScreenQuality For(bool cellular, bool relay = false) => cellular ? new(1280, 720, 15, 1000) : new(1920, 1080, 30, relay ? 2000 : 6000);
}
public sealed record ScreenFrame(string Data, string Mime, int Width, int Height, string? OverviewData = null);
public sealed record ScreenDeviceGrant(string DeviceId, bool Allowed = false, string Grant = "none", string? ControlKeyPublic = null);
public sealed record ScreenLiveSession(string SessionId, string DeviceId, string Mode, int DisplayId, string Codec, ScreenQuality Quality, DateTimeOffset StartedAt);
public sealed class ScreenShareException(string reason, int status = 403) : Exception(reason) { public string Reason { get; } = reason; public int Status { get; } = status; }

public static class ScreenSharePolicy
{
    public const int ControlIdleSeconds = 600, ViewIdleSeconds = 1800, LocalInputPauseSeconds = 2, BackgroundSeconds = 30;
    public static bool GrantAllows(ScreenDeviceGrant? grant, string mode) => grant?.Allowed == true && mode is "view" or "control" && (grant.Grant == "control" || mode == "view" && grant.Grant == "view");
    public static bool CanJoin(string mode, IEnumerable<ScreenLiveSession> sessions) => mode switch { "control" => sessions.Count(s => s.Mode == "control") < 1, "view" => sessions.Count(s => s.Mode == "view") < 2, _ => false };
    public static string Fingerprint(byte[] key)
    { var hex = Convert.ToHexString(SHA256.HashData(key).AsSpan(0, 8)); return string.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4))); }
    public static bool ValidKey(byte[] key)
    {
        if (key.Length != 65 || key[0] != 4) return false;
        try { using var ecdsa = Import(key); return ecdsa.KeySize == 256; } catch (CryptographicException) { return false; }
    }
    private static ECDsa Import(byte[] key) => ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key[1..33], Y = key[33..65] } });
    public static bool Verify(byte[] challenge, byte[] signature, byte[] publicKey)
    {
        if (!ValidKey(publicKey) || signature.Length is < 8 or > 80) return false;
        try { using var ecdsa = Import(publicKey); return ecdsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); } catch (CryptographicException) { return false; }
    }
}

/// One bounded, explicitly typed data-channel frame. No OS virtual key codes,
/// arbitrary clipboard encodings, or non-finite coordinates cross this boundary.
public static class ScreenControlWire
{
    public const int MaximumBytes = 65536;
    public static JsonElement? Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(text); var value = doc.RootElement; var type = value.Text("t");
            bool Point() => value.TryGetProperty("displayId", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out _) && ScreenRegion.Number(value, "x", out _) && ScreenRegion.Number(value, "y", out _);
            var valid = type switch
            {
                "tap" => Point() && value.Text("button") is "left" or "right" && (!value.TryGetProperty("marker", out _) || value.Text("marker") is { } marker && System.Text.RegularExpressions.Regex.IsMatch(marker, "^[A-Za-z0-9_-]{1,32}$")),
                "drag" => Point() && value.Text("phase") is "begin" or "move" or "end",
                "scroll" => Point() && ScreenRegion.Number(value, "dx", out _) && ScreenRegion.Number(value, "dy", out _),
                "text" => value.Text("text") is { Length: > 0 } input && !input.Contains('\0') && Encoding.UTF8.GetByteCount(input) <= 4096,
                "key" => value.Text("combo") is { } combo && KeyCombo(combo),
                "display" => value.TryGetProperty("displayId", out var display) && display.ValueKind == JsonValueKind.Number && display.TryGetInt32(out _),
                "zoom" => value.TryGetProperty("displayId", out var display) && display.ValueKind == JsonValueKind.Number && display.TryGetInt32(out _) && value.TryGetProperty("region", out var region) && ScreenRegion.Parse(region) is not null,
                "clipboard-request" => true,
                "clipboard" => value.Text("dir") == "to-mac" && value.Text("id") is { Length: > 0 and <= 64 } && value.Text("enc") is "raw" or "zstd" && Integer(value, "bytes", 1, 1048576) && Integer(value, "seq", 0, 63) && Integer(value, "total", 1, 64) && value.Text("data") is not null,
                _ => false,
            };
            return valid ? value.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
    private static bool Integer(JsonElement value, string key, int min, int max) => value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) && number >= min && number <= max;
    public static bool KeyCombo(string combo)
    {
        var keys = combo.Split('+'); if (keys.Length is < 1 or > 5 || keys.Take(keys.Length - 1).Distinct().Count() != keys.Length - 1 || keys.Take(keys.Length - 1).Any(k => k is not ("ctrl" or "opt" or "shift" or "cmd"))) return false;
        var key = keys[^1]; return key.Length == 1 && (key[0] is >= 'a' and <= 'z' or >= '0' and <= '9') || key is "return" or "tab" or "space" or "backspace" or "delete" or "escape" or "left" or "right" or "up" or "down" or "home" or "end" or "pageup" or "pagedown";
    }
}
