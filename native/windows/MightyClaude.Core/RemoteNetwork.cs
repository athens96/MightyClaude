using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record PinnedRemote(Uri Origin, IPAddress Address);
public static class RemoteNetwork
{
    public const int Protocol = 1;
    public const int DefaultPort = 43137;
    public const string VersionHeader = "x-mighty-remote-version";
    public static bool TailAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 100 && bytes[1] is >= 64 and <= 127 && !address.Equals(IPAddress.Parse("100.100.100.100")) : bytes.Length == 16 && bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11 && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0;
    }
    public static bool Allowed(IPAddress address, bool testLoopback) => TailAddress(address) || testLoopback && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
    public static bool ValidToken(string? token) => token is { Length: >= 43 and <= 128 } && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    public static bool Authenticate(string header, string token) => header.Length <= 256 && header.StartsWith("Bearer ", StringComparison.Ordinal) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Uri Parse(string address)
    {
        if (address.Length > 2048 || address.Any(char.IsWhiteSpace) || address.Contains('\\') || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.Host.Contains('%')) throw new ArgumentException("http://Tailscale주소:포트 형식을 사용하세요. 사용자 정보·경로·쿼리는 허용하지 않습니다.");
        return new(uri.GetLeftPart(UriPartial.Authority));
    }
    public static async Task<PinnedRemote> PinAsync(string address, TailscaleInfo tailscale, bool testLoopback = false, Func<string, CancellationToken, Task<IPAddress[]>>? lookup = null, CancellationToken token = default)
    {
        var uri = Parse(address);
        if (!testLoopback && !tailscale.Available) throw new InvalidOperationException("Tailscale을 실행하고 로그인한 뒤 연결하세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var host = uri.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var direct) ? [direct] : await (lookup ?? Dns.GetHostAddressesAsync)(host, timeout.Token);
        if (addresses.Length == 0 || addresses.Any(ip => !Allowed(ip, testLoopback))) throw new ArgumentException("Tailscale 장치 IP만 연결할 수 있습니다.");
        var selected = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        if (!testLoopback && tailscale.Peers is not null && !tailscale.Peers.Concat(tailscale.Addresses).Any(ip => IPAddress.TryParse(ip, out var known) && known.Equals(selected))) throw new ArgumentException("현재 tailnet에서 확인된 장치가 아닙니다.");
        return new(uri, selected);
    }
    public static async Task<JsonDocument> RequestAsync(PinnedRemote target, string token, HttpMethod method, string path, object? body = null, CancellationToken cancellation = default, int timeoutSeconds = 10)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectCallback = async (_, ct) => { var socket = new Socket(target.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp); try { await socket.ConnectAsync(new IPEndPoint(target.Address, target.Origin.Port), ct); return new NetworkStream(socket, true); } catch { socket.Dispose(); throw; } } };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(method, new Uri(target.Origin, path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token); request.Headers.TryAddWithoutValidation(VersionHeader, "1");
        request.Headers.TryAddWithoutValidation("x-mighty-activity", "1"); request.Headers.TryAddWithoutValidation("x-mighty-usage", "1");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Wire.Json), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.Headers.TryGetValues(VersionHeader, out var versions) || !versions.SequenceEqual(["1"])) throw new InvalidDataException("원격 응답 프로토콜 버전이 다릅니다.");
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("원격 응답 크기 제한을 초과했습니다.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0) { if (data.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("원격 응답 크기 제한을 초과했습니다."); data.Write(buffer, 0, count); }
        var document = JsonDocument.Parse(data.ToArray(), new() { MaxDepth = 48 });
        if (!document.RootElement.TryGetProperty("protocol", out var version) || version.GetInt32() != 1 || !response.IsSuccessStatusCode) { var message = Wire.Clean(document.RootElement.Text("error") ?? "호환되는 원격 응답이 아닙니다.", 300); document.Dispose(); throw new IOException(message); }
        return document;
    }
    public static async Task<TailscaleInfo> DiscoverTailscaleAsync(CancellationToken token = default)
    {
        var binaryName = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => p.Length > 0).Select(p => Path.Combine(p, binaryName)).Concat(["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/opt/homebrew/bin/tailscale", "/usr/bin/tailscale", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")]).Distinct();
        foreach (var binary in candidates.Where(File.Exists))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                await using var process = ChildProcess.Start(ChildProcess.StartInfo(binary, ["status", "--json"], Path.GetTempPath()));
                var errors = ProviderCatalog.DrainAsync(process.Error, timeout.Token); _ = errors.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                using var json = JsonDocument.Parse(await ProviderCatalog.ReadBoundedAsync(process.Output, 1024 * 1024, timeout.Token));
                var root = json.RootElement;
                if (root.Text("BackendState") != "Running") return new(false, [], null, "Tailscale을 실행하고 로그인하세요.");
                var self = root.TryGetProperty("Self", out var peer) ? peer : root;
                string[] Addresses(JsonElement value) => value.TryGetProperty("TailscaleIPs", out var ips) ? ips.EnumerateArray().Select(v => v.GetString()).Where(v => IPAddress.TryParse(v, out var ip) && TailAddress(ip)).Cast<string>().ToArray() : [];
                var addresses = Addresses(self).Take(8).ToArray();
                var peers = root.TryGetProperty("Peer", out var all) && all.ValueKind == JsonValueKind.Object ? all.EnumerateObject().SelectMany(p => Addresses(p.Value)).Take(4096).ToArray() : null;
                return new(addresses.Length > 0, addresses, Wire.Clean(self.Text("HostName"), 120), addresses.Length > 0 ? "Tailscale 연결됨" : "Tailscale 장치 주소가 없습니다.") { Peers = peers };
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception) { if (token.IsCancellationRequested) throw; }
        }
        return new(false, [], null, "Tailscale CLI를 찾거나 상태를 확인하지 못했습니다. 설치·실행·로그인을 확인하세요.");
    }
}
