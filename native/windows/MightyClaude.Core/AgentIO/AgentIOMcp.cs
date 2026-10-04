using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>Headless MCP stdio transport; stdout contains JSON-RPC only.</summary>
public sealed class AgentIOMcp(Func<string, JsonElement, CancellationToken, Task<JsonElement>> transport)
{
    public static async Task<int> MainAsync()
    {
        var pipe = Environment.GetEnvironmentVariable(AgentIOBinding.PipeKey) ?? "";
        var token = Environment.GetEnvironmentVariable(AgentIOBinding.TokenKey) ?? "";
        var server = new AgentIOMcp((tool, args, cancellation) => AgentIOPipe.Call(pipe, token, tool, args, cancellation));
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await server.Serve(input, output, CancellationToken.None); return 0;
    }
    public async Task Serve(TextReader input, TextWriter output, CancellationToken cancellation)
    {
        using var limit = new SemaphoreSlim(16); using var writer = new SemaphoreSlim(1); var pending = new HashSet<Task>();
        async Task Dispatch(string line)
        {
            try
            {
                if (await Handle(line, cancellation) is not { } response) return;
                await writer.WaitAsync(cancellation);
                try { await output.WriteLineAsync(response.AsMemory(), cancellation); await output.FlushAsync(cancellation); }
                finally { writer.Release(); }
            }
            finally { limit.Release(); }
        }
        var buffer = new char[4096]; var text = new StringBuilder(); var overflow = false;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n')
                {
                    if (!overflow) { text.Append(buffer[i]); if (text.Length > AgentIOPipe.MaximumBytes) { text.Clear(); overflow = true; } }
                    continue;
                }
                var line = overflow ? new string('x', AgentIOPipe.MaximumBytes + 1) : text.ToString(); text.Clear(); overflow = false;
                await limit.WaitAsync(cancellation); pending.RemoveWhere(task => task.IsCompleted); pending.Add(Dispatch(line));
            }
        }
        if (text.Length > 0) { await limit.WaitAsync(cancellation); pending.Add(Dispatch(text.ToString())); }
        await Task.WhenAll(pending);
    }
    public async Task<string?> Handle(string line, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        object? id = null;
        string Error(int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, new JsonSerializerOptions(Wire.Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });
        string Result(object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Wire.Json);
        try
        {
            if (Encoding.UTF8.GetByteCount(line) > AgentIOPipe.MaximumBytes) return Error(-32700, "Message exceeds 1 MiB.");
            using var document = JsonDocument.Parse(line); var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object || request.Text("jsonrpc") != "2.0" || request.Text("method") is not { } method) return Error(-32600, "Invalid request.");
            if (!request.TryGetProperty("id", out var requestId)) return null;
            if (requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return Error(-32600, "Invalid request identifier.");
            id = requestId.Clone();
            var args = request.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object ? parameters : JsonSerializer.SerializeToElement(new { });
            switch (method)
            {
                case "initialize":
                    var version = args.Text("protocolVersion") is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? args.Text("protocolVersion")! : "2025-06-18";
                    return Result(new { protocolVersion = version, capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = AgentIOBinding.ServerName, title = "Mighty Claude terminal and web", version = "1.0.0" }, instructions = AgentIOTools.Guidance });
                case "ping": return Result(new { });
                case "tools/list":
                    return Result(new { tools = AgentIOTools.All.Select(tool => new { name = tool.Name, description = tool.Description, inputSchema = new { type = "object", properties = new Dictionary<string, object> { [tool.Argument] = new { type = "string" } }, required = new[] { tool.Argument }, additionalProperties = false } }) });
                case "tools/call":
                    var name = args.Text("name") ?? "";
                    if (!AgentIOTools.All.Any(tool => tool.Name == name)) return Error(-32602, "Unknown tool.");
                    try
                    {
                        var input = args.GetProperty("arguments"); AgentIOTools.Validate(name, input);
                        var reply = await transport(name, input, cancellation);
                        var success = reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                        var value = success ? reply.GetProperty("result") : JsonSerializer.SerializeToElement(new { error = reply.Text("error") ?? "Request failed." });
                        return Result(new { content = new[] { new { type = "text", text = value.GetRawText() } }, structuredContent = value, isError = !success });
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { return Result(new { content = new[] { new { type = "text", text = "The tool request was refused or its pane is unavailable." } }, isError = true }); }
                default: return Error(-32601, "Method not found.");
            }
        }
        catch (JsonException) { return Error(-32700, "Parse error."); }
    }
}
