using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// Where a connection's requests go: the address checked once, at connect or refresh, and the one IP every request uses. Only
/// <see cref="RemoteTransport.ResolveAsync"/> (and the checks, through InternalsVisibleTo) builds one, and it keeps the loopback
/// flag it was resolved with, so every request re-checks its IP by the same rule.
/// </summary>
public sealed record RemoteTarget
{
    internal RemoteTarget(RemoteAddress address, string ip, int port, bool allowLoopback = false) { Address = address; Ip = ip; Port = port; AllowLoopback = allowLoopback; }
    public RemoteAddress Address { get; }
    public string Ip { get; }
    public int Port { get; }
    /// <summary>Injected in code by the checks and the smoke run only; never from a setting, a connection field or IPC.</summary>
    internal bool AllowLoopback { get; }
}

/// <summary>
/// The remote-workspace client's HTTP (M/MightyCore/Remote/RemoteTransport.swift RemoteTransport): one request per connection,
/// straight to the resolved Tailscale IP, with the rules of native/contracts/fixtures/remote-v2/README.md.
/// </summary>
public static class RemoteTransport
{
    /// <summary>Resolves a MagicDNS name (or takes a literal) and accepts it only when every address it names is a Tailscale one.</summary>
    public static async Task<RemoteTarget> ResolveAsync(RemoteAddress address, bool allowLoopback, CancellationToken token = default)
    {
        IPAddress[] found;
        if (IPAddress.TryParse(address.Host, out var literal)) found = [literal];
        else
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            try { found = await Dns.GetHostAddressesAsync(address.Host, limit.Token); }
            // Any failure of the lookup (no such name, a name the resolver refuses, the 5 s limit) reads as the Mac's getaddrinfo failure;
            // only the caller's own cancellation passes through.
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { throw new InvalidOperationException(Locale.Get("remote.connect.error.unresolved")); }
        }
        var ips = found.Take(32).Select(ip => TailscaleAddresses.Canonical((ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString())).Distinct(StringComparer.Ordinal).ToList();
        if (ips.Count == 0 || !ips.All(ip => RemotePeerPolicy.Allowed(ip, allowLoopback))) throw new InvalidOperationException(Locale.Get("remote.connect.error.notTailscale"));
        var chosen = ips.OrderBy(ip => ip.Contains(':')).ThenBy(ip => ip, StringComparer.Ordinal).First();
        return new(address, chosen, address.Port, allowLoopback);
    }

    /// <summary>The extra request headers a caller may set: the Windows graph_run opt-in only. Every other header is the transport's own.</summary>
    public static readonly IReadOnlySet<string> AllowedExtraHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RemoteProtocol.GraphHeader };

    /// <summary>
    /// One authenticated request. The key rides in the Authorization header and a prompt in the body, never in the URL; a redirect
    /// is answered, never followed; no proxy, cookie or cache; the answer is read up to <paramref name="maximumBytes"/>.
    /// </summary>
    public static async Task<byte[]> RequestAsync(RemoteTarget target, string key, HttpMethod method, string path, byte[]? body = null, TimeSpan? timeout = null,
        int maximumBytes = RemoteProtocol.MaximumResponseBytes, IReadOnlyDictionary<string, string>? headers = null, CancellationToken token = default)
    {
        if (!RemoteWorkspaceValidation.Key(key) || !path.StartsWith(RemoteProtocol.PathPrefix, StringComparison.Ordinal) || !Regex.IsMatch(path, @"^[A-Za-z0-9/._:?=-]+\z"))
            throw new InvalidOperationException(Locale.Get("remote.connect.error.badRequest"));
        foreach (var name in (headers ?? new Dictionary<string, string>()).Keys)
            if (!AllowedExtraHeaders.Contains(name)) throw new ArgumentException("remote request header not allowed: " + name, nameof(headers));
        // The key goes only to a Tailscale peer, even for a target built by hand.
        if (!RemotePeerPolicy.Allowed(target.Ip, target.AllowLoopback)) throw new InvalidOperationException(Locale.Get("remote.connect.error.notTailscale"));
        var limitTime = timeout ?? RemoteProtocol.RequestTimeout;
        var host = target.Ip.Contains(':') ? "[" + target.Ip + "]" : target.Ip;
        // Uri collapses dot segments (/v2/../v1/info is /v1/info); only a path that goes out exactly as written is sent.
        if (!Uri.TryCreate("http://" + host + ":" + target.Port.ToString(CultureInfo.InvariantCulture) + path, UriKind.Absolute, out var url) || url.PathAndQuery != path)
            throw new InvalidOperationException(Locale.Get("remote.connect.error.badRequest"));
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = limitTime, PooledConnectionLifetime = TimeSpan.Zero };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(limitTime);
        using var request = new HttpRequestMessage(method, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation(RemoteProtocol.Header, RemoteProtocol.Version.ToString(CultureInfo.InvariantCulture));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ConnectionClose = true;
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            // Exactly one header, exactly "2": a repeated one is no Mighty answer either.
            if (!response.Headers.TryGetValues(RemoteProtocol.Header, out var versions) || versions.ToList() is not [var version] || version != RemoteProtocol.Version.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
            var status = (int)response.StatusCode;
            if (status is not (>= 200 and <= 299 or >= 400 and <= 599))
                throw new InvalidDataException(Locale.Get("remote.connect.error.unexpectedStatus", new Dictionary<string, string> { ["status"] = status.ToString(CultureInfo.InvariantCulture) }));
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException(Locale.Get("remote.connect.error.responseTooLarge"));
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var data = new MemoryStream(); var buffer = new byte[16 * 1024]; int count;
            while ((count = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (data.Length + count > maximumBytes) throw new InvalidDataException(Locale.Get("remote.connect.error.responseTooLarge"));
                data.Write(buffer, 0, count);
            }
            if (status >= 400)
            {
                // A route's {protocol, error}, read as the Mac reads it: the error string only. The HTTP layer's {error}-alone 400/413/431
                // carry no version header, so they were already refused above as notMighty, as on the Mac.
                string? message = null;
                try { using var json = JsonDocument.Parse(data.ToArray()); message = json.RootElement.Text("error"); } catch (JsonException) { }
                message ??= Locale.Get("remote.connect.error.unexpectedStatus", new Dictionary<string, string> { ["status"] = status.ToString(CultureInfo.InvariantCulture) });
                throw new RemoteHttpFailure(status, message.Length > 300 ? message[..300] : message);
            }
            return data.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(RemoteWindowsStrings.Timeout); }
        // The deadline can also surface as a broken read of the body.
        catch (Exception ex) when (ex is IOException or HttpRequestException && limit.IsCancellationRequested && !token.IsCancellationRequested) { throw new TimeoutException(RemoteWindowsStrings.Timeout); }
    }

    /// <summary>A failed request worth asking again for a while: the host was busy (429, 503) or the network stumbled.</summary>
    public static bool Transient(Exception error) => error switch
    {
        RemoteHttpFailure failure => failure.Transient,
        TimeoutException or HttpRequestException or IOException => true,
        _ => false,
    };
}
