using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>A scripted HTTP/1.1 server on loopback for the remote client checks: one request per connection, answers built by the test.</summary>
internal sealed class RemoteFakeHost : IAsyncDisposable
{
    internal sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body);
    internal sealed record Reply(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null, bool VersionHeader = true, TimeSpan? Delay = null);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Func<Request, Task<Reply>> answer;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    internal readonly ConcurrentQueue<Request> Seen = new();
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal RemoteFakeHost(Func<Request, Task<Reply>> answer) { this.answer = answer; listener.Start(); loop = Task.Run(LoopAsync); }
    internal RemoteFakeHost(Func<Request, Reply> answer) : this(request => Task.FromResult(answer(request))) { }
    internal static Reply Fixture(string name, int status = 200) => new(status, RemoteWireFixtureVerification.Node(name).ToJsonString());

    private async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream(); var buffer = new List<byte>(); var chunk = new byte[8192]; int end;
            while ((end = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0) { var read = await stream.ReadAsync(chunk); if (read == 0) return; buffer.AddRange(chunk[..read]); }
            var head = Encoding.UTF8.GetString(buffer.ToArray(), 0, end).Split("\r\n");
            var line = head[0].Split(' ');
            var headers = head.Skip(1).Select(h => h.Split(':', 2)).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
            var length = headers.TryGetValue("content-length", out var text) ? int.Parse(text) : 0;
            var body = buffer.Skip(end + 4).ToList();
            while (body.Count < length) { var read = await stream.ReadAsync(chunk); if (read == 0) break; body.AddRange(chunk[..read]); }
            var request = new Request(line[0], line[1], headers, body.ToArray()); Seen.Enqueue(request);
            var reply = await answer(request);
            if (reply.Delay is { } delay) await Task.Delay(delay);
            var payload = Encoding.UTF8.GetBytes(reply.Body);
            var response = new StringBuilder($"HTTP/1.1 {reply.Status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n");
            if (reply.VersionHeader) response.Append("x-mighty-remote-version: 2\r\n");
            foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>()) response.Append(name).Append(": ").Append(value).Append("\r\n");
            response.Append("\r\n");
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(response.ToString())); await stream.WriteAsync(payload); } catch (IOException) { }
        }
    }
    private static int IndexOf(List<byte> data, byte[] pattern) { for (var i = 0; i + pattern.Length <= data.Count; i++) { var hit = true; for (var j = 0; j < pattern.Length && hit; j++) hit = data[i + j] == pattern[j]; if (hit) return i; } return -1; }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); try { await loop; } catch (Exception) { } }
}
