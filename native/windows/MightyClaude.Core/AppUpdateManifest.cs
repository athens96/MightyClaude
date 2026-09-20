using System.Text.Json;

namespace MightyClaude.Core;

/// One Windows package asset: url, sha256, and size are all mandatory on Windows.
public sealed record AppUpdateAsset(string Url, string Sha256, long Size);

/// Parsed and verified update manifest.
public sealed record AppUpdateManifest(
    string Version,
    int? Build,
    string? Notes,
    string? PublishedAt,
    IReadOnlyDictionary<string, AppUpdateAsset> Windows)
{
    public const string EnvelopeFormat = "mightyclaude-update-v1";

    /// Parse the bytes of latest.json given the compiled-in public key.
    /// Windows rule 1: a null publicKey throws immediately.
    /// Windows rule 2: any Windows asset lacking sha256 or size is rejected.
    /// Transport: every URL must be https.
    public static AppUpdateManifest Parse(ReadOnlySpan<byte> data, byte[] publicKey)
    {
        if (data.Length > 256 * 1024) throw new InvalidOperationException("업데이트 정보 파일이 너무 큽니다.");
        using var doc = JsonDocument.Parse(data.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("업데이트 정보 파일을 JSON 객체로 읽지 못했습니다.");

        // Must be a signed envelope.
        if (!root.TryGetProperty("payload", out var payloadEl) || !root.TryGetProperty("signature", out var sigEl))
            throw new InvalidOperationException("서명되지 않은 업데이트 정보입니다. 이 앱은 서명된 정보만 받습니다.");

        if (!root.TryGetProperty("format", out var fmtEl) || fmtEl.GetString() != EnvelopeFormat)
            throw new InvalidOperationException("업데이트 정보의 서명 형식을 알 수 없습니다.");

        var payloadB64 = payloadEl.GetString() ?? throw new InvalidOperationException("업데이트 정보의 서명 인코딩이 잘못되었습니다.");
        var sigB64 = sigEl.GetString() ?? throw new InvalidOperationException("업데이트 정보의 서명 인코딩이 잘못되었습니다.");

        if (!Convert.TryFromBase64String(payloadB64, new byte[payloadB64.Length * 3 / 4 + 4].AsSpan(), out _))
            throw new InvalidOperationException("업데이트 정보의 서명 인코딩이 잘못되었습니다.");
        var payloadBytes = Convert.FromBase64String(payloadB64);
        var signatureBytes = Convert.FromBase64String(sigB64);

        if (!Ed25519Verify.Verify(payloadBytes, signatureBytes, publicKey))
            throw new InvalidOperationException("업데이트 정보의 서명이 이 앱의 공개 키와 맞지 않습니다.");

        return ParsePlain(payloadBytes);
    }

    internal static AppUpdateManifest ParsePlain(ReadOnlySpan<byte> data)
    {
        if (data.Length > 256 * 1024) throw new InvalidOperationException("업데이트 정보를 JSON 객체로 읽지 못했습니다.");
        using var doc = JsonDocument.Parse(data.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("업데이트 정보를 JSON 객체로 읽지 못했습니다.");

        var rawVersion =
            TryGetString(root, "version") ??
            TryGetString(root, "latest") ??
            TryGetString(root, "latestVersion");
        if (AppVersion.Normalized(rawVersion) is not string version)
            throw new InvalidOperationException("업데이트 정보에 유효한 version이 없습니다.");

        // Windows assets live in root["windows"]["x64" | "arm64"]
        var windows = new Dictionary<string, AppUpdateAsset>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("windows", out var winEl) && winEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var arch in new[] { "x64", "arm64" })
            {
                if (!winEl.TryGetProperty(arch, out var assetEl)) continue;
                if (ParseAsset(assetEl) is { } asset)
                    windows[arch] = asset;
            }
        }

        int? build = root.TryGetProperty("build", out var buildEl) && buildEl.TryGetInt32(out var bi) && bi >= 0 ? bi :
                     root.TryGetProperty("buildNumber", out var bnEl) && bnEl.TryGetInt32(out var bni) && bni >= 0 ? bni :
                     null;

        return new AppUpdateManifest(
            version,
            build,
            TryGetString(root, "notes") ?? TryGetString(root, "releaseNotes") ?? TryGetString(root, "changelog"),
            TryGetString(root, "publishedAt") ?? TryGetString(root, "date"),
            windows);
    }

    // Windows rule 2: sha256 and size are mandatory; asset is rejected if either is absent.
    private static AppUpdateAsset? ParseAsset(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var urlStr = TryGetString(el, "url") ??
                     TryGetString(el, "download") ??
                     TryGetString(el, "downloadUrl") ??
                     TryGetString(el, "href");
        if (!Allowed(urlStr)) return null;

        var sha256 = TryGetString(el, "sha256");
        if (sha256 is not null)
        {
            var clean = sha256.ToLowerInvariant().Trim().Replace("sha256:", "");
            if (clean.Length != 64 || !clean.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                sha256 = null;
            else
                sha256 = clean;
        }
        // Both sha256 and size are required on Windows.
        if (sha256 is null) return null;
        if (!el.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out var size) || size <= 0) return null;

        return new AppUpdateAsset(urlStr!, sha256, size);
    }

    /// Transport is https only — there is no test-only escape hatch, so the
    /// rule the checks exercise is the rule the app runs with.
    public static bool Allowed(string? url) =>
        url is not null &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(uri.Host);

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// Dotted numeric versions: 1.2.0 > 1.2.0-beta.2 > 1.1.9; missing components
/// read as 0; a leading 'v' is stripped.
public static class AppVersion
{
    public static string? Normalized(string? value)
    {
        if (value is null) return null;
        var s = value.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        if (s.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(
            s, @"^[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?$"))
            return null;
        return s;
    }

    public static int Compare(string lhs, string rhs)
    {
        if (Normalized(lhs) is not string l || Normalized(rhs) is not string r) return 0;
        static (int[] nums, string? pre) Split(string v)
        {
            var core = v.Split('+')[0];
            var parts = core.Split('-', 2);
            var nums = parts[0].Split('.').Select(n => int.TryParse(n, out var x) ? x : 0).ToArray();
            return (nums, parts.Length > 1 ? parts[1] : null);
        }
        var (a, aPre) = Split(l);
        var (b, bPre) = Split(r);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return (aPre, bPre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (ap, bp) => string.Compare(ap, bp, StringComparison.OrdinalIgnoreCase),
        };
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;
}

/// Check result: current version vs manifest.
public sealed record AppUpdateAvailability(string Current, AppUpdateManifest Manifest)
{
    public bool IsNewer => AppVersion.IsNewer(Manifest.Version, Current);
}
