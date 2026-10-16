using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed class AgentIOPipe : IAsyncDisposable
{
    public const int MaximumBytes = 1_048_576;
    /// <summary>A delegation call's limit at both ends, as on the Mac (M/AgentIOSocketServer.swift:238-245): it covers the coordinator's <see cref="DelegationCoordinator.AnswerSeconds"/>.</summary>
    public const int DelegationSeconds = 60;
    public const string UnknownTokenMessage = "This agent pane is no longer connected to Mighty Claude. Reopen the pane and try again.";
    private const string MalformedMessage = "The request to Mighty Claude was malformed.", NoAnswerMessage = "Mighty Claude did not answer the request.";
    public const string BusyMessage = "This pane already has as many delegation calls going as Mighty Claude takes at once. Wait for one to answer and try again.";
    private const string Prefix = "mighty-agent-io-";
    /// <summary>
    /// The listeners waiting for a client, the connections served at once (waiting ones included) and the delegation calls one
    /// pane's token may have going. Each accepted connection is served on its own task, as the Mac's socket server serves each
    /// client (M/MightyCore/AgentIOSocketServer.swift:190-201), so long delegation calls never keep the terminal tools from a listener.
    /// </summary>
    public const int WaitingListeners = 4, Connections = 64, DelegationCallsPerToken = 4;
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> listeners = [];
    private readonly SemaphoreSlim connections = new(Connections);
    private readonly HashSet<Task> serving = [];
    private readonly Dictionary<string, int> delegationCalls = [];
    public string Name { get; } = Prefix + Guid.NewGuid().ToString("N");
    public AgentIOBindings Bindings { get; } = new();
    private readonly Func<AgentIOBinding, string, string, CancellationToken, Task<object>> handle;
    public AgentIOPipe(Func<AgentIOBinding, string, string, CancellationToken, Task<object>> handle)
    {
        this.handle = handle;
        for (var i = 0; i < WaitingListeners; i++) listeners.Add(Listen());
    }
    /// <summary>Waits for clients one at a time, each on a connection slot of its own, and hands each to <see cref="Serve"/>. No input ends it.</summary>
    private async Task Listen()
    {
        while (!shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null; var slot = false;
            try
            {
                await connections.WaitAsync(shutdown.Token); slot = true;
                // CurrentUserOnly checks the Windows account AND elevation level.
                // https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions
                pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(shutdown.Token);
                var accepted = pipe; pipe = null; slot = false;
                var served = Task.Run(() => Serve(accepted));
                lock (serving) serving.Add(served);
                _ = served.ContinueWith(done => { lock (serving) serving.Remove(done); }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!shutdown.IsCancellationRequested)
                    try { await Task.Delay(100, shutdown.Token); } catch (OperationCanceledException) { }
            }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync();
                if (slot) connections.Release();
            }
        }
    }
    /// <summary>One client's request and answer, on its connection slot; whatever it sends ends here.</summary>
    private async Task Serve(NamedPipeServerStream pipe)
    {
        try { await using (pipe) await Answer(pipe); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { connections.Release(); }
    }
    private async Task Answer(NamedPipeServerStream pipe)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var document = JsonDocument.Parse(await ReadPacket(pipe, deadline.Token));
        var request = document.RootElement;
        // The packet carries either a terminal and web tool call or a delegation tool call (M/AgentIOSocketServer.swift:16-22).
        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("delegation", out var call))
        {
            await WritePacket(pipe, JsonSerializer.SerializeToUtf8Bytes(await AnswerDelegation(request, call, deadline), Wire.Json), deadline.Token);
            return;
        }
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
    /// <summary>
    /// A delegation call, from whatever pane its token belongs to (M/AgentIOSocketServer.swift:227-233). A binding the run path
    /// connected to no delegation service is not attached, so its call reaches nothing. An authenticated call gets
    /// <see cref="DelegationSeconds"/>; the terminal and web tools keep their own limits. A pane has at most
    /// <see cref="DelegationCallsPerToken"/> going; one more is answered <see cref="BusyMessage"/>.
    /// </summary>
    private async Task<DelegationResponse> AnswerDelegation(JsonElement packet, JsonElement call, CancellationTokenSource deadline)
    {
        string? counted = null;
        try
        {
            string token; DelegationRequest? request;
            // A token or an argument that is not text (a lone surrogate among them) is a malformed call.
            try { token = packet.Text("token") ?? ""; request = call.Deserialize<DelegationRequest>(DelegationJson.Options); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or ArgumentException) { return DelegationResponse.Failure(MalformedMessage); }
            if (request?.Arguments is null || request.Arguments.Values.Any(value => value is null)) return DelegationResponse.Failure(MalformedMessage);
            if (Bindings.Resolve(token) is not { } binding) return DelegationResponse.Failure(UnknownTokenMessage);
            if (binding.DelegationHandler is not { } handler) return DelegationResponse.Failure(DelegationIOHandler.DetachedMessage);
            lock (delegationCalls)
            {
                if (delegationCalls.GetValueOrDefault(token) >= DelegationCallsPerToken) return DelegationResponse.Failure(BusyMessage);
                delegationCalls[token] = delegationCalls.GetValueOrDefault(token) + 1; counted = token;
            }
            deadline.CancelAfter(TimeSpan.FromSeconds(DelegationSeconds));
            return await handler.Handle(request, new(binding.PaneId, binding.Kind, binding.Provider, binding.Delegation, binding.PermissionMode)).WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !shutdown.IsCancellationRequested) { return DelegationResponse.Failure(NoAnswerMessage); }
        finally
        {
            if (counted is not null) lock (delegationCalls) { if (--delegationCalls[counted] == 0) delegationCalls.Remove(counted); }
        }
    }
    private static bool IsPipeName(string pipeName) => pipeName.StartsWith(Prefix, StringComparison.Ordinal) && pipeName.Length == Prefix.Length + 32 && pipeName[Prefix.Length..].All(Uri.IsHexDigit);
    /// <summary>
    /// One delegation call: the same pipe and token as the terminal tools, within <see cref="DelegationSeconds"/>
    /// (M/AgentIOSocketServer.swift:245-263). It never throws: a call that could not be made is answered as a failed one.
    /// </summary>
    public static async Task<DelegationResponse> CallDelegation(string pipeName, string token, DelegationRequest request, CancellationToken cancellation)
    {
        if (!IsPipeName(pipeName)) return DelegationResponse.Failure("Mighty Claude's agent socket path is invalid.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { token, delegation = request }, Wire.Json);
        if (bytes.Length >= MaximumBytes) return DelegationResponse.Failure("The request is too large.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(DelegationSeconds));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException) { return DelegationResponse.Failure("Mighty Claude is not running or not accepting agent tool calls."); }
        try { await WritePacket(pipe, bytes, timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return DelegationResponse.Failure("Could not send the request to Mighty Claude."); }
        try { return JsonSerializer.Deserialize<DelegationResponse>(await ReadPacket(pipe, timeout.Token), Wire.Json) ?? DelegationResponse.Failure(NoAnswerMessage); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { return DelegationResponse.Failure(NoAnswerMessage); }
    }
    public static async Task<JsonElement> Call(string pipeName, string token, string tool, JsonElement arguments, CancellationToken cancellation)
    {
        if (!IsPipeName(pipeName) || token.Length != 64) throw new ArgumentException("Invalid pane transport.");
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
        Bindings.Clear(); await shutdown.CancelAsync(); await Task.WhenAll(listeners);
        Task[] open; lock (serving) open = [.. serving];
        await Task.WhenAll(open); shutdown.Dispose();
    }
}
