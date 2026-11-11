using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyCode.Core;

/// <summary>
/// An ACP agent (opencode, Hermes) through <see cref="AcpChannel"/> (M/MightyCore/CLIStreamParser.swift acp): its <c>session/update</c>s, the
/// prompt's result and a failed call, as <c>acp.*</c> events. Text arrives as deltas and is logged whole when the message, a tool call or the
/// turn ends; thoughts are not copied.
/// </summary>
public sealed partial class OutputParser
{
    /// <summary>The message the streamed ACP text belongs to: a new one ends the previous reply.</summary>
    private string? acpMessage;
    /// <summary>The tail of the turn's reply text: Hermes says why a turn failed (a missing provider, a rejected key) in the reply, and the error that follows only says that it failed.</summary>
    private string acpReplyTail = "";

    private void Acp(JsonElement root, string? type)
    {
        var name = new Dictionary<string, string> { ["name"] = ProviderMark.Label(provider) };
        switch (type)
        {
            case "acp.session": Resume(root.Text("sessionId")); break;
            case "acp.prompt": acpReplyTail = ""; Turn(Locale.Get("run.turn.generating", name)); break;
            case "acp.output": if (root.Text("text") is { } output) log("output", output); break;
            case "acp.update":
                if (MetadataJson.Property(root, "update") is not { ValueKind: JsonValueKind.Object } update) return;
                switch (update.Text("sessionUpdate"))
                {
                    case "agent_message_chunk": AcpText(update); break;
                    case "tool_call" or "tool_call_update": AcpTool(update); break;
                    case "plan":
                        // A plan's entries as the checklist rows other agents show.
                        var lines = (MetadataJson.Property(update, "entries") is { ValueKind: JsonValueKind.Array } entries ? entries.EnumerateArray().Take(TodoProgress.MaximumItems) : [])
                            .Where(entry => entry.ValueKind == JsonValueKind.Object && entry.Text("content") is not null)
                            .Select(entry => (entry.Text("status") == "completed" ? "[x] " : "[ ] ") + ActivitySupport.Clean(entry.Text("content"), 400, singleLine: true)).ToList();
                        if (lines.Count > 0) log("system", string.Join("\n", lines));
                        break;
                    // Thoughts, usage, commands, modes and options are not transcript lines.
                }
                break;
            case "acp.prompt_result":
                AcpFlushText();
                switch (root.Text("stopReason"))
                {
                    case "max_tokens" or "max_turn_requests": log("system", Locale.Get("run.notice.acpStopped", new Dictionary<string, string> { ["reason"] = root.Text("stopReason")! })); break;
                    case "refusal": log("system", Locale.Get("run.notice.acpRefused", name)); break;
                }
                AuthenticationFailure = false;
                Turn(Locale.Get("run.turn.finishing", name));
                break;
            case "acp.error":
                AcpFlushText();
                Failed = true;
                var message = root.Text("message") is { Length: > 0 } said ? said : null;
                var data = MetadataJson.Property(root, "data");
                // The Python ACP SDK (Hermes) puts an unexpected failure's words in `data.details`.
                var detail = data.ValueKind == JsonValueKind.Object ? data.Text("message") ?? data.Text("details") : data.ValueKind == JsonValueKind.String ? data.GetString() : null;
                var text = string.Join("\n", new[] { message, detail }.Where(part => !string.IsNullOrEmpty(part)));
                if (provider == "hermes")
                {
                    AuthenticationFailure = MetadataJson.Flag(root, "signIn") || CliAuthFailure.Hermes(text + "\n" + acpReplyTail);
                    // Hermes's gateway names a failed turn only by this word; the reply above said why.
                    if (detail == "admitted_turn_failed") text = Locale.Get("run.error.hermesTurnFailed");
                    else if (message == "Internal error" && !string.IsNullOrEmpty(detail)) text = detail;
                }
                else AuthenticationFailure = MetadataJson.SafeInteger(root, "code") == -32000 || CliAuthFailure.Opencode(text);
                log("error", text.Length == 0 ? Locale.Get("run.error.providerRunFailed", name) : text.Length > 32_768 ? text[..32_768] : text);
                break;
        }
    }

    private void AcpText(JsonElement update)
    {
        // A sub-agent's own reply (OpenCode 2 marks its child sessions' updates) is not the answer.
        if (MetadataJson.Property(update, "_meta") is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty("opencode/child-session", out _)) return;
        var message = update.Text("messageId");
        if (message is not null && acpMessage is not null && message != acpMessage) AcpFlushText();
        if (message is not null) acpMessage = message;
        if (MetadataJson.Property(update, "content") is not { ValueKind: JsonValueKind.Object } content || content.Text("type") != "text" || content.Text("text") is not { Length: > 0 } text) return;
        // A reply does not begin with the blank lines an agent streams ahead of it.
        if (pending.Length == 0) { text = text.TrimStart('\r', '\n'); if (text.Length == 0) return; }
        // Hermes sends a reply it streamed once more, whole, when the turn settles (with a note appended when a hook added one): the whole
        // takes the streamed text's place.
        var shown = pending.ToString().Trim(); var whole = text.Trim();
        if (Encoding.UTF8.GetByteCount(shown) >= 16 && whole.StartsWith(shown, StringComparison.Ordinal))
        {
            pending.Clear(); pending.Append(ActivitySupport.PrefixUtf8(whole, ActivitySupport.MaximumMessageBytes));
            pendingTruncated = Encoding.UTF8.GetByteCount(whole) > ActivitySupport.MaximumMessageBytes;
            acpReplyTail = Tail(whole); return;
        }
        var remaining = Math.Max(0, ActivitySupport.MaximumMessageBytes - Encoding.UTF8.GetByteCount(pending.ToString()));
        if (Encoding.UTF8.GetByteCount(text) > remaining) pendingTruncated = true;
        pending.Append(ActivitySupport.PrefixUtf8(text, remaining));
        acpReplyTail = Tail(acpReplyTail + text);
    }
    private static string Tail(string text) => text.Length > 8_192 ? text[^8_192..] : text;

    private void AcpTool(JsonElement update)
    {
        AcpFlushText();
        if (update.Text("toolCallId") is not { } id) return;
        var status = update.Text("status");
        var state = status == "completed" ? "completed" : status == "failed" ? "error" : "running";
        var known = activities.ContainsKey(ActivitySupport.Id(activityNamespace, id));
        string? kind = update.Text("kind"), title = update.Text("title");
        // A settled call that was never announced and says nothing of itself (the close of a permission card's own bubble) is not a tool row.
        if (!known && state != "running" && kind is null && title is null) return;
        var toolName = kind is not null || !known ? AcpChannel.ToolName(kind, update.Text("name") ?? AcpChannel.TitledTool(title)) : null;
        // An update that repeats a kind without a name (Hermes's "other") does not rename the row.
        if (known && toolName == "Tool") toolName = null;
        // Hermes sends no `rawInput` for its own tools: the path, shell line or title stands in.
        var node = JsonNode.Parse(update.GetRawText()) as JsonObject ?? [];
        var raw = node["rawInput"];
        var input = raw is not null && raw.GetValueKind() != JsonValueKind.Null ? AcpChannel.DisplayInput(raw) : known ? null : AcpChannel.DescribedInput(node);
        var output = state == "running" ? null : AcpOutput(update);
        Tool(id, state, toolName, input is null ? default : JsonSerializer.SerializeToElement(input), output);
        if (activity is null && state != "running" && !string.IsNullOrEmpty(output)) log("output", output);
    }

    /// <summary>The reply streamed so far, logged whole.</summary>
    private void AcpFlushText()
    {
        var text = TrailingTrimmed(pending.ToString());
        if (text.Length > 0) Assistant(text, "acp:" + (acpMessage ?? ""));
        if (pendingTruncated) log("system", Locale.Get("run.notice.messageTruncated"));
        pending.Clear(); pendingTruncated = false;
    }

    /// <summary>A settled ACP tool call's result: its text content blocks, a diff's path, or <c>rawOutput</c>.</summary>
    public static string? AcpOutput(JsonElement update)
    {
        var parts = new List<string>();
        if (MetadataJson.Property(update, "content") is { ValueKind: JsonValueKind.Array } content)
            foreach (var block in content.EnumerateArray().Take(64))
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                switch (block.Text("type"))
                {
                    case "content":
                        if (MetadataJson.Property(block, "content") is { ValueKind: JsonValueKind.Object } inner && inner.Text("type") == "text" && inner.Text("text") is { } text) parts.Add(text);
                        break;
                    case "diff": if (block.Text("path") is { } path) parts.Add(path); break;
                }
            }
        if (parts.Count == 0 && MetadataJson.Property(update, "rawOutput") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } raw)
        {
            if (raw.ValueKind == JsonValueKind.String) parts.Add(raw.GetString()!);
            else if (raw.ValueKind == JsonValueKind.Object)
            {
                if ((raw.Text("output") ?? raw.Text("error")) is { } said) parts.Add(said);
                else return ActivitySupport.Output(raw);
            }
        }
        var joined = string.Join("\n", parts);
        return joined.Length == 0 ? null : joined;
    }
}
