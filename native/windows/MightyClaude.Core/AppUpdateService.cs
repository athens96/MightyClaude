using System.IO.Compression;
using System.Security.Cryptography;

namespace MightyClaude.Core;

/// The phases the app update section moves through, in order.
public enum AppUpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Staging,
    Ready,
    Installing,
    Failed,
}

/// Download progress in [0, 1].
public delegate void DownloadProgress(double fraction);

/// The network and file-system half of the update pipeline.
///
/// Transport is https only, including every redirect hop: automatic redirects
/// are off and each Location is checked before it is followed, so a manifest or
/// package that is moved to http (or to any other scheme) is refused instead of
/// silently downgraded. Core.Tests injects an <see cref="HttpMessageHandler"/>
/// that answers from memory, so no check ever opens a socket.
public sealed class AppUpdateService : IDisposable
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const string PackageName = "MightyClaude-windows.zip";
    public const string ExecutableName = "MightyClaude.exe";
    private const int MaxRedirects = 5;

    private readonly string stateDirectory;
    private readonly byte[]? publicKey;
    private readonly HttpClient http;
    private CancellationTokenSource? downloading;

    public AppUpdateService(string stateDirectory, byte[]? publicKey, HttpMessageHandler? handler = null)
    {
        this.stateDirectory = stateDirectory;
        this.publicKey = publicKey?.Length == Ed25519Verify.PublicKeyBytes ? publicKey : null;
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
    }

    /// Windows rule 1: without a compiled-in public key the app never checks.
    public bool VerifiesSignatures => publicKey is not null;

    /// The folder every download lands under: &lt;state&gt;/updates.
    public string UpdatesDirectory => Path.Combine(stateDirectory, "updates");

    public async Task<AppUpdateAvailability> CheckAsync(string? manifestUrl, string currentVersion, CancellationToken cancellation = default)
    {
        if (publicKey is null)
            throw new InvalidOperationException(AppUpdateStrings.NoPublicKeyNotice);
        var url = RequireHttps(manifestUrl, "업데이트 정보 주소는 https여야 합니다.");

        using var response = await SendFollowingHttpsRedirectsAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"업데이트 정보를 받지 못했습니다. HTTP {(int)response.StatusCode}");
        var data = await ReadCappedAsync(response, 256 * 1024, "업데이트 정보 파일이 너무 큽니다.", cancellation);
        return new AppUpdateAvailability(currentVersion, AppUpdateManifest.Parse(data, publicKey));
    }

    /// Downloads the package into updates/&lt;version&gt;/, removes every other
    /// version folder first, and verifies the size and the SHA-256 of the bytes
    /// that are actually on disk. Anything that does not match is deleted, and a
    /// cancelled download leaves nothing behind.
    public async Task<string> DownloadAsync(
        AppUpdateAsset asset, string version, DownloadProgress? progress = null, CancellationToken cancellation = default)
    {
        var url = RequireHttps(asset.Url, "패키지 주소는 https여야 합니다.");
        if (asset.Size > MaxPackageBytes) throw new InvalidOperationException("패키지가 너무 큽니다.");

        var name = AppVersion.Normalized(version) ?? throw new InvalidOperationException("업데이트 정보에 유효한 version이 없습니다.");
        Directory.CreateDirectory(UpdatesDirectory);
        foreach (var folder in Directory.GetDirectories(UpdatesDirectory))
            if (!Path.GetFileName(folder).Equals(name, StringComparison.Ordinal))
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        var versionDirectory = Path.Combine(UpdatesDirectory, name);
        if (Directory.Exists(versionDirectory)) Directory.Delete(versionDirectory, true);
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, PackageName);

        var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        downloading = owned;
        try
        {
            await WriteToDiskAsync(url, destination, asset.Size, progress, owned.Token);

            var received = new FileInfo(destination).Length;
            if (received != asset.Size)
                throw new InvalidOperationException($"패키지 크기가 업데이트 정보와 다릅니다 ({received} ≠ {asset.Size}).");
            if (!string.Equals(await Sha256Async(destination, owned.Token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("패키지 SHA-256이 업데이트 정보와 다릅니다.");
        }
        catch
        {
            // Closed on failure: nothing half-verified is left for a later step to find.
            try { Directory.Delete(versionDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            downloading = null;
            owned.Dispose();
        }

        progress?.Invoke(1);
        return destination;
    }

    public void CancelDownload() => downloading?.Cancel();

    /// Unpacks the zip into a fresh folder beside it and refuses:
    /// an entry with an absolute path, a `..` segment or any other path that
    /// escapes the folder; an entry stored as a link; a package that does not
    /// carry exactly one MightyClaude.exe at its root; and a package built for
    /// another architecture.
    public static string Stage(string packagePath, string expectedArchitecture)
    {
        var staged = Path.Combine(Path.GetDirectoryName(packagePath)!, "staged");
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Directory.CreateDirectory(staged);
        var root = Path.GetFullPath(staged).TrimEnd(Path.DirectorySeparatorChar);

        using (var zip = ZipFile.OpenRead(packagePath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Length == 0) continue;
                RefuseUnsafeEntry(entry, root);
                if (entry.FullName.EndsWith('/')) continue;
                var destination = Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open();
                using var target = File.Create(destination);
                source.CopyTo(target);
            }
        }

        // The package shape: exactly one MightyClaude.exe, at the root of the zip.
        var executables = Directory.GetFiles(root, ExecutableName, SearchOption.AllDirectories);
        var atRoot = executables.Where(path => Path.GetDirectoryName(path) == root).ToArray();
        if (executables.Length != 1 || atRoot.Length != 1)
            throw new InvalidOperationException($"패키지 안에 {ExecutableName}가 하나 있어야 합니다 ({executables.Length}개).");

        RefuseAnotherArchitecture(atRoot[0], expectedArchitecture);
        return staged;
    }

    private static void RefuseUnsafeEntry(ZipArchiveEntry entry, string root)
    {
        var name = entry.FullName;
        if (name.StartsWith('/') || name.StartsWith('\\') || (name.Length >= 2 && name[1] == ':'))
            throw new InvalidOperationException("패키지에 절대 경로 항목이 있어 설치하지 않습니다.");
        if (name.Split('/', '\\').Any(segment => segment == ".."))
            throw new InvalidOperationException("패키지에 상위 폴더로 나가는 항목이 있어 설치하지 않습니다.");
        // Unix mode is stored in the high 16 bits; S_IFLNK is 0xA000.
        if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            throw new InvalidOperationException("패키지에 링크 항목이 있어 설치하지 않습니다.");

        var resolved = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("패키지 항목이 대상 폴더를 벗어납니다.");
    }

    /// PE machine type: 0x8664 is x64, 0xAA64 is arm64.
    private static void RefuseAnotherArchitecture(string executable, string expected)
    {
        ushort machine;
        using (var file = File.OpenRead(executable))
        {
            var header = new byte[4];
            file.Seek(0x3c, SeekOrigin.Begin);
            if (file.Read(header, 0, 4) < 4) throw new InvalidOperationException("패키지의 실행 파일이 손상되었습니다.");
            var offset = BitConverter.ToInt32(header, 0);
            if (offset <= 0 || offset > file.Length - 6) throw new InvalidOperationException("패키지의 실행 파일이 손상되었습니다.");
            file.Seek(offset + 4, SeekOrigin.Begin);
            if (file.Read(header, 0, 2) < 2) throw new InvalidOperationException("패키지의 실행 파일이 손상되었습니다.");
            machine = BitConverter.ToUInt16(header, 0);
        }
        var matches = expected.ToLowerInvariant() switch
        {
            "x64" => machine == 0x8664,
            "arm64" => machine == 0xaa64,
            _ => false,
        };
        if (!matches)
            throw new InvalidOperationException($"패키지의 아키텍처가 {expected}와 다릅니다 (machine=0x{machine:X4}).");
    }

    /// SHA-256 of the bytes on disk, read in 1 MiB pieces.
    public static async Task<string> Sha256Async(string path, CancellationToken cancellation = default)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = File.OpenRead(path);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await file.ReadAsync(buffer, cancellation)) > 0) digest.AppendData(buffer, 0, read);
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    /// Every hop must be https. The scheme is checked before the hop is taken,
    /// so a redirect to http is refused rather than followed and then noticed.
    private async Task<HttpResponseMessage> SendFollowingHttpsRedirectsAsync(
        Uri url, HttpCompletionOption option, CancellationToken cancellation)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, current), option, cancellation);
            var redirect = (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;
            if (!redirect || response.Headers.Location is null) return response;

            var next = new Uri(current, response.Headers.Location);
            response.Dispose();
            current = RequireHttps(next.ToString(), "업데이트 주소가 https가 아닌 주소로 넘어갔습니다.");
        }
        throw new InvalidOperationException("업데이트 주소의 리디렉션이 너무 많습니다.");
    }

    private static Uri RequireHttps(string? value, string message)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException(message);
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException(message);
        return uri;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int cap, string message, CancellationToken cancellation)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + read > cap) throw new InvalidOperationException(message);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task WriteToDiskAsync(Uri url, string destination, long expected, DownloadProgress? progress, CancellationToken cancellation)
    {
        using var response = await SendFollowingHttpsRedirectsAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"패키지를 받지 못했습니다. HTTP {(int)response.StatusCode}");

        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        await using (var target = File.Create(destination))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                written += read;
                // The cap is enforced on the bytes as they arrive, so an endless
                // body cannot fill the disk before the size check runs.
                if (written > MaxPackageBytes || written > expected)
                    throw new InvalidOperationException("패키지가 너무 큽니다.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                if (expected > 0) progress?.Invoke(Math.Min(0.99, (double)written / expected));
            }
        }
    }

    public void Dispose() => http.Dispose();
}
