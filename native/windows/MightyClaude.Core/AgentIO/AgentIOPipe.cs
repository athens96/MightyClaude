using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed class AgentIOPipe : IAsyncDisposable
{
    public const int MaximumBytes = 1_048_576;
    private const string Prefix = "mighty-agent-io-";
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> listeners = [];
    public string Name { get; } = Prefix + Guid.NewGuid().ToString("N");
    public AgentIOBindings Bindings { get; } = new();
    private readonly Func<AgentIOBinding, string, string, CancellationToken, Task<object>> handle;
    public AgentIOPipe(Func<AgentIOBinding, string, string, CancellationToken, Task<object>> handle)
    {
        this.handle = handle;
        // Fixed workers bound both simultaneous calls and unauthenticated clients.
        for (var i = 0; i < 16; i++) listeners.Add(Listen());
    }
    private async Task Listen()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly checks the Windows account AND elevation level.
                // https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(shutdown.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var document = JsonDocument.Parse(await ReadPacket(pipe, deadline.Token));
                var request = document.RootElement;
                object response;
                try
                {
                    var token = request.Text("token") ?? "";
                    var binding = Bindings.Resolve(token) ?? throw new ArgumentException("This pane binding is no longer active.");
                    var tool = request.Text("tool") ?? "";
                    var value = AgentIOTools.Validate(tool, request.GetProperty("arguments"));
                    // The user has 30 seconds to choose a browser. Only an
                    // authenticated, validated URL request gets extra launch time.
                    if (tool == "open_url") deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    using var active = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, binding.Revoked);
                    active.Token.ThrowIfCancellationRequested();
                    var result = await handle(binding, tool, value, active.Token);
                    response = new { ok = true, result };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !shutdown.IsCancellationRequested)
                {
                    // Errors deliberately carry no exception text, token, command or private path.
                    response = new { ok = false, error = "The tool request was refused or could not complete. The pane may have closed." };
                }
                await WritePacket(pipe, JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json), deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (!shutdown.IsCancellationRequested)
                    try { await Task.Delay(100, shutdown.Token); } catch (OperationCanceledException) { }
            }
        }
    }
    public static async Task<JsonElement> Call(string pipeName, string token, string tool, JsonElement arguments, CancellationToken cancellation)
    {
        if (!pipeName.StartsWith(Prefix, StringComparison.Ordinal) || pipeName.Length != Prefix.Length + 32 || !pipeName[Prefix.Length..].All(Uri.IsHexDigit) || token.Length != 64) throw new ArgumentException("Invalid pane transport.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(tool == "open_url" ? 60 : 30));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await WritePacket(pipe, JsonSerializer.SerializeToUtf8Bytes(new { token, tool, arguments }, Wire.Json), timeout.Token);
        using var result = JsonDocument.Parse(await ReadPacket(pipe, timeout.Token));
        return result.RootElement.Clone();
    }
    public static async Task<byte[]> ReadPacket(Stream stream, CancellationToken cancellation)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancellation);
        var size = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (size is < 1 or > MaximumBytes) throw new IOException("Invalid tool packet size.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation); return bytes;
    }
    public static async Task WritePacket(Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new IOException("Tool response exceeds its limit.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellation); await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }
    public async ValueTask DisposeAsync()
    {
        Bindings.Clear(); await shutdown.CancelAsync(); await Task.WhenAll(listeners); shutdown.Dispose();
    }
}
