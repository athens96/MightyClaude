using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MightyClaude.Core;

/// <summary>One connection a direct listener delivered; it holds one of the listeners' slots until it is refused or disposed, whichever comes first, and gives it back once.</summary>
public sealed class DirectIncoming : IDisposable
{
    private readonly Socket socket;
    private readonly Action release;
    private int finished;

    /// <summary>The remote IP in its canonical spelling (<see cref="PeerOf"/>); null when the endpoint is no IP.</summary>
    public string? Peer { get; }
    public Stream Stream { get; }

    internal DirectIncoming(Socket socket, Action release)
    {
        this.socket = socket; this.release = release;
        Peer = PeerOf(socket.RemoteEndPoint);
        Stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// One spelling per remote address, so the per-address limits never split across forms of one address: an IPv4-mapped address as
    /// its IPv4, IPv6 lowercase and compressed with no <c>%scope</c>; null when the endpoint is no IP.
    /// </summary>
    internal static string? PeerOf(EndPoint? endpoint) =>
        endpoint is IPEndPoint remote ? TailscaleAddresses.Canonical((remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString()) : null;

    /// <summary>Drops the connection unread: a reset, nothing said. The slot is given back as it goes.</summary>
    public void Refuse()
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        release();
        try { socket.LingerState = new LingerOption(true, 0); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        Stream.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        try { Stream.Dispose(); }
        finally { release(); }
    }
}

/// <summary>
/// The direct path's listeners (M/MightyCore/Remote/MobileDirect.swift MobileDirectListener): one <see cref="TcpListener"/> per
/// Tailscale address, bound to that address alone (never a wildcard), with exclusive use of the port on Windows. HttpListener is not
/// used: binding it to one IP needs an administrator's URL ACL. Together the listeners hold at most
/// <see cref="MobileDirect.ConnectionLimit"/> connections; one over that is reset before anything is read. Windows' strong host
/// model stands in for the Mac's pinning to the utun interface.
/// </summary>
public sealed class MobileDirectListener
{
    private sealed record Entry(TcpListener Listener, string Host, int Port, CancellationTokenSource Stop);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> listeners = new(StringComparer.Ordinal);
    private readonly Action<DirectIncoming> onConnection;
    private int live;
    private bool stopped;

    public MobileDirectListener(Action<DirectIncoming> onConnection) => this.onConnection = onConnection;

    /// <summary>Connections delivered and not yet ended, across every listener.</summary>
    public int LiveConnections => Volatile.Read(ref live);

    /// <summary>
    /// Makes the listeners match <paramref name="addresses"/> on <paramref name="port"/>: stops the ones no longer wanted, keeps the
    /// ones already up (and the phones on them), binds the rest. Port 0 picks a free port, kept on later updates with 0. Answers every
    /// listener up afterwards with its port, and the first error as a <see cref="MobileDirectError"/> code (the socket's own text goes
    /// to the trace only).
    /// </summary>
    public (IReadOnlyList<(string Host, int Port)> Bound, string? Error) Update(IReadOnlyList<string> addresses, int port)
    {
        lock (sync)
        {
            if (stopped) return ([], null);
            var wanted = addresses.Select(address => Key(address, port)).ToHashSet(StringComparer.Ordinal);
            foreach (var key in listeners.Keys.Where(key => !wanted.Contains(key)).ToArray()) { Close(listeners[key]); listeners.Remove(key); }
            string? error = null;
            foreach (var address in addresses)
            {
                var key = Key(address, port);
                if (listeners.ContainsKey(key)) continue;
                if (!IPAddress.TryParse(address, out var ip)) { error ??= MobileDirectError.BindFailed; continue; }
                var listener = new TcpListener(ip, port);
                try
                {
                    if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
                    listener.Start(MobileDirect.ConnectionLimit);
                }
                catch (SocketException ex)
                {
                    listener.Stop(); error ??= MobileDirectError.Of(ex);
                    System.Diagnostics.Trace.TraceError("mobile direct: an address could not be listened on: " + ex.SocketErrorCode + ": " + ex.Message);
                    continue;
                }
                var entry = new Entry(listener, address, ((IPEndPoint)listener.LocalEndpoint).Port, new CancellationTokenSource());
                listeners[key] = entry;
                _ = AcceptLoop(entry);
            }
            return ([.. addresses.Select(address => Key(address, port)).Where(listeners.ContainsKey).Select(key => (listeners[key].Host, listeners[key].Port))], error);
        }
    }

    /// <summary>Stops accepting. Connections already delivered stay up; their owners close them.</summary>
    public void Stop()
    {
        lock (sync) { stopped = true; foreach (var entry in listeners.Values) Close(entry); listeners.Clear(); }
    }

    private async Task AcceptLoop(Entry entry)
    {
        while (!entry.Stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await entry.Listener.AcceptSocketAsync(entry.Stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) when (!entry.Stop.IsCancellationRequested) { await Task.Delay(100, CancellationToken.None); continue; }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }
            Deliver(socket);
        }
    }

    private void Deliver(Socket socket)
    {
        var count = Interlocked.Increment(ref live);
        DirectIncoming incoming;
        // The slot is taken before anything else; every path below gives it back exactly once: here when the connection never became
        // a DirectIncoming, otherwise through the DirectIncoming's own once-only Refuse/Dispose.
        try { incoming = new DirectIncoming(socket, () => Interlocked.Decrement(ref live)); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException) { Interlocked.Decrement(ref live); socket.Dispose(); return; }
        bool refuse;
        lock (sync) refuse = stopped || count > MobileDirect.ConnectionLimit;
        if (refuse) { incoming.Refuse(); return; }
        try { onConnection(incoming); }
        catch (Exception) { incoming.Refuse(); }
    }

    private static void Close(Entry entry) { entry.Stop.Cancel(); entry.Listener.Stop(); entry.Stop.Dispose(); }
    private static string Key(string address, int port) => address + "|" + port.ToString(CultureInfo.InvariantCulture);
}
