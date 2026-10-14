using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyClaude.Core;

/// <summary>
/// The delegation MCP server's side of the protocol (M/DelegationMCPServer.swift, M/MCPStdioServer.swift): the app binary run as
/// <c>--agent-delegation-mcp</c>. A separate server from mighty-terminal, so that one keeps exactly its four tools. A run gets
/// it only in a Claude pane and only while the hidden <see cref="DelegationSwitch"/> is on, and it lists exactly the six tools
/// of <see cref="DelegationToolManifest"/>. Each <c>tools/call</c> is forwarded to the app, which decides who may call them and
/// runs them (<see cref="DelegationCoordinator"/>). The words of every answer are the Mac's: the agent reads them.
/// </summary>
/// <param name="transport">Forwards one call to the app on this pane's token; null when the server was started without a pane connection.</param>
public sealed class DelegationMcp(Func<DelegationRequest, CancellationToken, Task<DelegationResponse>>? transport)
{
    public const string ServerName = "mighty-delegation", HeadlessArgument = "--agent-delegation-mcp";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The headless mode. It shares the pane's token and pipe with mighty-terminal (M/DelegationMCPServer.swift:8-12): each call
    /// is forwarded with <c>MIGHTY_PANE_TOKEN</c> over <c>MIGHTY_AGENT_IO_PIPE</c>, and the token is never written anywhere.
    /// </summary>
    public static async Task<int> MainAsync()
    {
        var pipe = Environment.GetEnvironmentVariable(AgentIOBinding.PipeKey); var token = Environment.GetEnvironmentVariable(AgentIOBinding.TokenKey);
        var server = new DelegationMcp(string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(token) ? null : (request, cancellation) => AgentIOPipe.CallDelegation(pipe, token, request, cancellation));
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await server.Serve(input, output, CancellationToken.None); return 0;
    }

    /// <summary>Serves <paramref name="input"/> until it closes, writing responses to <paramref name="output"/>.</summary>
    public Task Serve(TextReader input, TextWriter output, CancellationToken cancellation) => AgentIOMcp.Serve(input, output, Handle, cancellation);

    /// <summary>Handles one line, tool calls included. The response line, or null for notifications and blank lines.</summary>
    public async Task<string?> Handle(string line, CancellationToken cancellation = default)
    {
        line = line.TrimEnd('\r');
        if (line.All(character => character is ' ' or '\t')) return null;
        static string Reply(JsonNode? id, string key, JsonNode value) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, [key] = value }.ToJsonString(Json);
        static string Error(JsonNode? id, int code, string message) => Reply(id, "error", new JsonObject { ["code"] = code, ["message"] = message });
        if (Encoding.UTF8.GetByteCount(line) > AgentIOPipe.MaximumBytes) return Error(null, -32700, "Message exceeds 1 MB.");
        JsonNode? json;
        try { json = JsonNode.Parse(line); } catch (JsonException) { return Error(null, -32700, "Parse error"); }
        // A JsonNode reads its strings late: one holding a lone surrogate escape fails only when it is read.
        try { return await Answer(json, cancellation).ConfigureAwait(false); }
        catch (InvalidOperationException) { return Error(null, -32700, "Parse error"); }
    }

    private async Task<string?> Answer(JsonNode? json, CancellationToken cancellation)
    {
        static string Reply(JsonNode? id, string key, JsonNode value) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, [key] = value }.ToJsonString(Json);
        static string Error(JsonNode? id, int code, string message) => Reply(id, "error", new JsonObject { ["code"] = code, ["message"] = message });
        static JsonNode? ValidId(JsonNode? value) => value is JsonValue id && id.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? id.DeepClone() : null;
        if (json is not JsonObject request || request["jsonrpc"] is not JsonValue version || version.GetValueKind() != JsonValueKind.String || version.GetValue<string>() != "2.0"
            || request["method"] is not JsonValue named || named.GetValueKind() != JsonValueKind.String)
            return Error(ValidId((json as JsonObject)?["id"]), -32600, "Invalid Request");
        // Without an id it is a notification: never answered, whatever the method.
        if (!request.ContainsKey("id")) return null;
        if (ValidId(request["id"]) is not { } requestId) return Error(null, -32600, "Invalid Request");
        var method = named.GetValue<string>(); var parameters = request["params"] as JsonObject ?? [];
        string? Text(JsonNode? value) => value is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : null;
        switch (method)
        {
            case "initialize":
                var asked = Text(parameters["protocolVersion"]);
                return Reply(requestId, "result", new JsonObject
                {
                    ["protocolVersion"] = asked is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? asked : "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Mighty Claude delegation", ["version"] = "1.0.0" },
                });
            case "ping": return Reply(requestId, "result", new JsonObject());
            case "tools/list": return Reply(requestId, "result", new JsonObject { ["tools"] = new JsonArray([.. DelegationToolManifest.All.Select(Definition)]) });
            case "tools/call":
                if (Text(parameters["name"]) is not { } name || !DelegationRequest.IsToolName(name))
                    return Error(requestId, -32602, $"Unknown tool: {DelegationNames.Prefix(Text(parameters["name"]) ?? parameters["name"]?.ToJsonString(Json) ?? "none", 80)}");
                if (parameters.ContainsKey("arguments") && parameters["arguments"] is not JsonObject) return Reply(requestId, "result", ToolError("arguments must be an object."));
                return Reply(requestId, "result", await Call(name, parameters["arguments"] as JsonObject ?? [], cancellation).ConfigureAwait(false));
            default: return Error(requestId, -32601, $"Method not found: {DelegationNames.Prefix(method, 80)}");
        }
    }

    /// <summary>Forwards every well-formed call to the app, which decides whether this pane may use the tools.</summary>
    private async Task<JsonObject> Call(string name, JsonObject arguments, CancellationToken cancellation)
    {
        var values = new Dictionary<string, string>();
        foreach (var (key, value) in arguments)
        {
            if (!DelegationRequest.IsToolName(key) || value is not JsonValue text || text.GetValueKind() != JsonValueKind.String) return ToolError($"Arguments of {name} must be strings with lowercase names.");
            values[key] = text.GetValue<string>();
        }
        if (transport is null) return ToolError("This MCP server was started without a Mighty Claude pane connection, so it cannot reach the app.");
        return Result(await transport(new(name, values), cancellation).ConfigureAwait(false));
    }

    /// <summary>The <c>tools/list</c> entry for <paramref name="tool"/>.</summary>
    public static JsonObject Definition(DelegationToolManifest.Tool tool)
    {
        var properties = new JsonObject();
        foreach (var argument in tool.Arguments) properties[argument.Name] = new JsonObject { ["type"] = "string", ["description"] = argument.Description };
        return new()
        {
            ["name"] = tool.Name, ["description"] = tool.Description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray([.. tool.Arguments.Select(argument => (JsonNode)argument.Name)]), ["additionalProperties"] = false },
        };
    }

    /// <summary>A failed tool call: shown to the model as the call's result, never as a protocol error.</summary>
    public static JsonObject ToolError(string message) => new() { ["content"] = Content(message), ["isError"] = true };

    private static JsonArray Content(string text) => [new JsonObject { ["type"] = "text", ["text"] = text }];
    private static JsonObject Answer(string text, string key, JsonNode data) => new() { ["content"] = Content(text), ["structuredContent"] = new JsonObject { [key] = data } };

    /// <summary>
    /// The <c>tools/call</c> result for the app's answer. A refusal names its one reason code in the text and as structured
    /// content; a child, a list, a status, a merge and a follow-up are tool data in both too.
    /// </summary>
    public static JsonObject Result(DelegationResponse response)
    {
        if (response.Refused is { } reason)
        {
            var code = DelegationNames.Of(reason);
            return new() { ["content"] = Content($"Refused: {code}. Nothing was changed."), ["structuredContent"] = new JsonObject { ["refused"] = code }, ["isError"] = true };
        }
        if (response.Child is { } child)
            return Answer($"Child {child.Id} is {DelegationNames.Of(child.State)}, on the branch {child.Branch}, starting in {child.StartingMode} mode.", "child",
                new JsonObject { ["id"] = child.Id, ["state"] = DelegationNames.Of(child.State), ["branch"] = child.Branch, ["mode"] = child.StartingMode });
        if (response.Children is { } children)
        {
            var lines = children.Select(listed => $"- {listed.Id}: {DelegationNames.Of(listed.State)}, report revision {listed.Revision}");
            return Answer(children.Count == 0 ? "You have no children." : string.Join("\n", lines.Prepend("Your children:")), "children",
                new JsonArray([.. children.Select(listed => new JsonObject { ["id"] = listed.Id, ["state"] = DelegationNames.Of(listed.State), ["revision"] = listed.Revision })]));
        }
        if (response.Status is { } status)
        {
            var text = $"Child {status.Id} is {DelegationNames.Of(status.State)}.";
            if (status.Revision == 0) text += " It has not reported yet.";
            else text += $" Its report revision {status.Revision} was made at head {status.Head ?? "unknown"}." + (status.Report is { } report ? $"\n\nREPORT.md:\n{report}" : " Its REPORT.md copy is no longer kept.");
            return Answer(text, "status", new JsonObject { ["id"] = status.Id, ["state"] = DelegationNames.Of(status.State), ["revision"] = status.Revision, ["head"] = status.Head, ["report"] = status.Report, ["reportTruncated"] = status.ReportTruncated });
        }
        if (response.Merged is { } merged)
            return Answer($"Merged child {merged.ChildId}: {merged.ParentBranch} fast-forwarded from {merged.PreMergeCommit} to {merged.MergedCommit}. The merge is recorded; only a human can undo it, from the child's card.", "merged",
                new JsonObject { ["child"] = merged.ChildId, ["kind"] = DelegationNames.Of(merged.Kind), ["parentBranch"] = merged.ParentBranch, ["preMergeCommit"] = merged.PreMergeCommit, ["mergedCommit"] = merged.MergedCommit, ["childHead"] = merged.ChildHead });
        if (response.FollowUp is { } followUp)
        {
            var left = followUp.Remaining == 0 ? "It was this child's last follow-up." : $"This child may get {followUp.Remaining} more.";
            return Answer($"Follow-up {followUp.Id} for child {followUp.Child} is recorded; Mighty Claude hands it to the child exactly once. {left}", "followUp",
                new JsonObject { ["id"] = followUp.Id, ["child"] = followUp.Child, ["lane"] = DelegationNames.Of(followUp.Lane), ["remaining"] = followUp.Remaining });
        }
        return ToolError(response.Error ?? "Mighty Claude did not answer the request.");
    }
}
