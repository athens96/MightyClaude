using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyCode.Core;

/// <summary>
/// An ACP agent's run (opencode, Hermes) through <see cref="AcpChannel"/>'s <c>acp.*</c> events (M/MightyCore/ExecutionGraphTracker.swift consumeACP).
/// One block for the request: the answer is the streamed text the parser shows (<see cref="AntigravityAnswer"/>), and opencode's prompt result carries
/// the turn's tokens. Hermes reports its session's running token counts, not the request's: its block carries none. Tool calls stay rows of the
/// request (<see cref="RunSession"/> keeps them with its transcript), questions are cards, and a sub-agent's own text (opencode's child sessions) is
/// not the answer (<see cref="OutputParser"/>).
/// </summary>
public sealed partial class ExecutionGraphTracker
{
    private void ConsumeAcp(JsonElement value)
    {
        if (value.Text("type") != "acp.prompt_result" || MetadataJson.Property(value, "usage") is not { ValueKind: JsonValueKind.Object } usage) return;
        var mapped = new JsonObject();
        foreach (var (source, target) in new[] { ("inputTokens", "input_tokens"), ("outputTokens", "output_tokens"), ("cachedReadTokens", "cache_read_input_tokens"), ("cachedWriteTokens", "cache_creation_input_tokens") })
            if (MetadataJson.Property(usage, source) is { ValueKind: JsonValueKind.Number } number) mapped[target] = JsonNode.Parse(number.GetRawText());
        if (GraphTokenUsage.Parse(JsonSerializer.SerializeToElement(mapped)) is { } tokens) RecordUsage(tokens, "acp:" + (value.Text("sessionId") ?? "turn"), mainID);
    }
}
