using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// Antigravity's stream-json (M/MightyCore/ExecutionGraphTracker.swift consumeAntigravity). agy has no hook bridge either. Each
/// <c>agent_response</c> step carries its own usage; the reply text reaches the main block through the parser
/// (<see cref="AntigravityAnswer"/>), so it is exactly what the transcript shows. A <c>subagent</c> step names each sub-agent's
/// conversation, role and prompt; what the sub-agent does is read from its own transcript (<see cref="AntigravitySubagentWatcher"/>).
/// </summary>
public sealed partial class ExecutionGraphTracker
{
    private readonly Dictionary<string, string> antigravityAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> antigravityTurns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AntigravitySubagent> antigravitySubagents = [];
    private string? antigravityConversation, antigravityModel;
    /// <summary>The Antigravity conversation this run continues or started.</summary>
    public string? AntigravityConversation { get { lock (sync) return antigravityConversation; } }
    /// <summary>Sub-agents the stream named, oldest first, for the runner's watcher.</summary>
    public IReadOnlyList<AntigravitySubagent> AntigravitySubagents { get { lock (sync) return antigravitySubagents.ToArray(); } }

    private void ConsumeAntigravity(JsonElement value)
    {
        if (value.Text("event") is not { } kind) return;
        var payload = MetadataJson.Property(value, kind);
        switch (kind)
        {
            case "init":
                if (Key(MetadataJson.Property(value, "conversation_id")) is { } started) { antigravityConversation = started; Alias(started, mainID); }
                if (payload.Text("model") is { } model && Wire.Model(model)) antigravityModel = model;
                break;
            case "step_update":
                var index = MetadataJson.Integer(payload, "step_index") ?? -1;
                switch (payload.Text("step_type"))
                {
                    case "agent_response":
                        if (AntigravityRecords.Usage(MetadataJson.Property(payload, "usage")) is not { } usage) return;
                        RecordUsage(usage, "step:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), mainID, antigravityModel ?? configuredModel, [], antigravityModel is null && configuredModel is not null);
                        break;
                    case "subagent":
                        var failed = payload.Text("state")?.ToUpperInvariant() == "ERROR";
                        var specs = MetadataJson.Property(MetadataJson.Property(payload, "subagent_info"), "subagents");
                        if (specs.ValueKind != JsonValueKind.Array) return;
                        foreach (var spec in specs.EnumerateArray().Take(ExecutionGraphSupport.MaximumNodes))
                        {
                            if (AntigravityRecords.ConversationID(spec.Text("conversation_id")) is not { } conversation || conversation == antigravityConversation
                                || EnsureAntigravityAgent(conversation) is not { } id) continue;
                            var title = AntigravityRecords.Title(spec.Text("role"), spec.Text("type_name"));
                            var prompt = spec.Text("initial_prompt") is { Length: > 0 } text ? text : null;
                            if (!antigravitySubagents.Any(s => s.Conversation == conversation)) antigravitySubagents.Add(new(conversation, antigravityConversation ?? "", title, prompt));
                            // The step ends once the sub-agent is started; only a failed start ends the block.
                            Update(id, node => node with { Title = title ?? node.Title, Input = node.Input ?? prompt, State = failed ? "error" : node.State });
                        }
                        break;
                }
                break;
        }
    }

    /// <summary>
    /// The reply the parser just showed for an <c>agent_response</c> step (or the result's full response when nothing was
    /// streamed): the latest one is the request's answer.
    /// </summary>
    public void AntigravityAnswer(string text)
    {
        lock (sync)
        {
            if (finished || Provider != "gemini" || text.Length == 0) return;
            Update(mainID, n => n with { Output = text });
        }
    }

    private string? EnsureAntigravityAgent(string conversation)
    {
        if (antigravityAgents.TryGetValue(conversation, out var known)) return known;
        if (nodes.Count >= ExecutionGraphSupport.MaximumNodes) return null;
        var id = ExecutionGraphSupport.Identifier(runID, "agy-agent:" + conversation);
        var value = new ExecutionGraphNode(id, runID, mainID, "agent", "running", Locale.Get("graph.block.agent"));
        nodes[id] = value; order.Add(id); antigravityAgents[conversation] = id; emit(value);
        Alias(conversation, id);
        return id;
    }

    /// <summary>
    /// A sub-agent's transcript snapshot (<see cref="AntigravityChildRecord"/>). A new instruction on a settled sub-agent is new
    /// work, as Codex's <c>send_input</c>; a sub-agent that answered while helpers it started were still out goes on with the same
    /// assignment when one reports.
    /// </summary>
    public void AntigravitySession(CodexSessionAgent agent)
    {
        lock (sync)
        {
            if (finished || Provider != "gemini" || agent.Thread == antigravityConversation || EnsureAntigravityAgent(agent.Thread) is not { } id) return;
            SetParent(id, agent.ParentThread == antigravityConversation ? Owner.Node(mainID) : Owner.Agent(agent.ParentThread));
            var seen = antigravityTurns.GetValueOrDefault(agent.Thread);
            antigravityTurns[agent.Thread] = Math.Max(seen, agent.Turns);
            var old = nodes[id];
            var reopening = seen > 0 && agent.Turns > seen && ExecutionGraphSupport.Terminal(old.State);
            foreach (var record in agent.Usage)
                RecordUsage(record.Usage, record.ResponseId, id, antigravityModel ?? configuredModel, record.ActivityIds, antigravityModel is null && configuredModel is not null);
            // The previous assignment's answer stays in the block above the new one.
            if (reopening && nodes[id].Output is { Length: > 0 } previous && !nodes[id].Entries.Any(e => e.Kind == "assistant" && e.Text == previous))
                Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":answer:" + (nodes[id].ActivityGeneration ?? 0)), "assistant", previous), id);
            var continuing = !reopening && agent.State == "running" && nodes[id].State == "completed";
            Update(id, value =>
            {
                var entries = value.Entries.ToList();
                foreach (var entry in agent.Entries)
                {
                    var index = entries.FindIndex(e => e.Id == entry.Id);
                    if (index < 0) entries.Add(entry); else entries[index] = entry with { Timestamp = entries[index].Timestamp };
                }
                return value with { Title = agent.Title ?? value.Title, Input = value.Input ?? agent.Input, Entries = entries,
                    Output = reopening || agent.Output is not null ? agent.Output : value.Output, State = agent.State };
            }, reopening, continuing);
        }
    }
}
