using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// The graph part of one Mods event (macOS ModGraphMetadata).
public sealed record ModGraphMetadata(int Version, string Phase, string? AgentId = null, string? ParentAgentId = null, string? ParentToolUseId = null,
    string? Name = null, string? AgentType = null, string? Model = null, string? Input = null, string? Output = null);

/// The fields of one Mods event the execution graph reads (macOS ModMetadata).
public sealed record ModMetadata(string ClaudeSessionId, string Event, string? Tool = null, string? ToolUseId = null, string? AgentId = null, ModGraphMetadata? Graph = null);


/// One Codex `collab_tool_call` item (macOS CodexCollaborationItem).
internal sealed class CodexCollaborationItem
{
    internal sealed record AgentState(string Status, string? Message);
    public required string Id { get; init; }
    public required string Tool { get; init; }
    public string? Sender { get; init; }
    public required IReadOnlyList<string> Receivers { get; init; }
    public required IReadOnlyDictionary<string, AgentState> States { get; init; }
    public string? Prompt { get; init; }
    public required string Status { get; init; }

    internal static string? Key(JsonElement value) => value.ValueKind == JsonValueKind.String ? Key(value.GetString()) : null;
    internal static string? Key(string? text)
    {
        if (text is not { Length: > 0 } || Encoding.UTF8.GetByteCount(text) > 512) return null;
        foreach (var rune in text.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) return null;
        return text;
    }
    internal static CodexCollaborationItem? Parse(JsonElement item)
    {
        if (item.Text("type") is not ("collab_tool_call" or "collab_agent_tool_call") || Key(MetadataJson.Property(item, "id")) is not { } id
            || item.Text("tool") is not ("spawn_agent" or "send_input" or "wait" or "close_agent") || item.Text("tool") is not { } tool) return null;
        var seen = new HashSet<string>();
        var raw = MetadataJson.Property(item, "receiver_thread_ids");
        var receivers = raw.ValueKind == JsonValueKind.Array && raw.EnumerateArray().All(r => r.ValueKind == JsonValueKind.String)
            ? raw.EnumerateArray().Take(ExecutionGraphSupport.MaximumNodes).Select(Key).OfType<string>().Where(seen.Add).ToList() : [];
        var states = new Dictionary<string, AgentState>();
        var agents = MetadataJson.Property(item, "agents_states");
        if (agents.ValueKind == JsonValueKind.Object)
            foreach (var property in agents.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Take(ExecutionGraphSupport.MaximumNodes))
            {
                if (Key(property.Name) is not { } thread || property.Value.ValueKind != JsonValueKind.Object || property.Value.Text("status") is not { } status) continue;
                states[thread] = new(ExecutionGraphSupport.Prefix(status, 80),
                    property.Value.Text("message") is { } message ? ActivitySupport.Clean(message, ExecutionGraphSupport.MaximumOutputBytes) : null);
            }
        var prompt = item.Text("prompt") is { } text ? ActivitySupport.Clean(text, ExecutionGraphSupport.MaximumInputBytes) : null;
        return new() { Id = id, Tool = tool, Sender = Key(MetadataJson.Property(item, "sender_thread_id")), Receivers = receivers, States = states, Prompt = string.IsNullOrEmpty(prompt) ? null : prompt, Status = item.Text("status") ?? "in_progress" };
    }
    public IReadOnlyList<string> Threads => Receivers.Concat(States.Keys.OrderBy(k => k, StringComparer.Ordinal).Where(k => !Receivers.Contains(k))).Take(ExecutionGraphSupport.MaximumNodes).ToList();
}

/// Observes public CLI/Mods events for one run and emits node snapshots. It
/// never reads another session's transcript or guesses child prompts.
/// Port of macOS ExecutionGraphTracker.
public sealed class ExecutionGraphTracker
{
    private readonly record struct Owner(bool IsAgent, string Id)
    {
        public static Owner Node(string id) => new(false, id);
        public static Owner Agent(string id) => new(true, id);
    }
    private sealed class PendingAgent
    {
        public ModGraphMetadata? Metadata;
        public List<AgentActivity> Activities = [];
    }
    private static readonly Regex TaskLaunch = new(@"background with ID: (?<task>[A-Za-z0-9_-]+)");
    private readonly string runID;
    private readonly string mainID;
    /// "claude" observes stream-json plus Mods; "codex" observes exec JSONL.
    public string Provider { get; }
    private readonly string? configuredModel;
    private readonly Action<ExecutionGraphNode> emit;
    private readonly Dictionary<string, string> codexAgents = [];
    private string? codexRootThread;
    private readonly HashSet<string> codexSettledCalls = [];
    private readonly List<string> codexCallOrder = [];
    private readonly Dictionary<string, Dictionary<string, int>> codexCallGenerations = [];
    private readonly List<string> codexObservedCallOrder = [];
    private readonly Dictionary<string, ExecutionGraphNode> nodes = [];
    private readonly List<string> order = [];
    private readonly Dictionary<string, string> aliases = [];
    private readonly Dictionary<string, string> pendingParents = [];
    private readonly Dictionary<string, PendingAgent> pendingAgents = [];
    private readonly Dictionary<string, Owner> toolOwners = [];
    private readonly List<string> toolOrder = [];
    private readonly Dictionary<string, Owner> activityOwners = [];
    private readonly List<string> activityOrder = [];
    private readonly HashSet<string> backgroundTools = [];
    private readonly Dictionary<string, string> taskAliases = [];
    private readonly Dictionary<string, GraphTokenUsage> messageUsage = [];
    private readonly Dictionary<string, string> messageModel = [];
    private readonly Dictionary<string, IReadOnlyList<string>> messageActivityIds = [];
    private readonly Dictionary<string, bool> messageConfigured = [];
    private readonly Dictionary<string, List<string>> nodeResponseOrder = [];
    private readonly List<string> pendingSteers = [];
    private readonly List<string> messageOrder = [];
    private int unnamedUsage;
    private int compactions;
    private bool finished;

    public ExecutionGraphTracker(string runID, string? input, string provider, string? configuredModel, Action<ExecutionGraphNode> emit)
    {
        this.runID = runID; mainID = ExecutionGraphSupport.MainNodeID(runID); Provider = provider; this.configuredModel = configuredModel; this.emit = emit;
        var main = new ExecutionGraphNode(mainID, runID, null, "main", "running", ProviderLabel(provider)) { Input = input };
        if (ExecutionGraphSupport.Normalized(main) is { } normalized) { nodes[mainID] = normalized; order.Add(mainID); emit(normalized); }
    }
    private static string ProviderLabel(string id) => id switch { "codex" => "Codex", "gemini" => "Gemini", _ => "Claude" };
    private static string Prefix(string value, int count) => ExecutionGraphSupport.Prefix(value, count);

    /// A message the user sent while the turn ran. Claude reads it from stdin
    /// between tool calls; the block under main shows it until the next
    /// root-level answer, which is taken as the reply.
    public void Steer(string id, string text)
    {
        if (finished || Provider != "claude" || nodes.Count >= ExecutionGraphSupport.MaximumNodes) return;
        var nodeID = ExecutionGraphSupport.Identifier(runID, "steer:" + id);
        if (nodes.ContainsKey(nodeID)) return;
        var node = new ExecutionGraphNode(nodeID, runID, mainID, "steer", "running", Locale.Get("graph.block.steer")) { Input = text };
        if (ExecutionGraphSupport.Normalized(node) is not { } normalized) return;
        nodes[nodeID] = normalized; order.Add(nodeID); emit(normalized);
        pendingSteers.Add(nodeID);
    }

    /// The CLI summarized its context. The block sits under the request, or
    /// for Claude under the subagent that compacted, and is complete on arrival.
    private void Compaction(string key, string owner, string detail)
    {
        if (finished || nodes.Count >= ExecutionGraphSupport.MaximumNodes) return;
        var nodeID = ExecutionGraphSupport.Identifier(runID, "compact:" + key);
        if (nodes.ContainsKey(nodeID)) return;
        var parent = nodes.ContainsKey(owner) ? owner : mainID;
        var node = new ExecutionGraphNode(nodeID, runID, parent, "compact", "completed", ContextCompaction.Title) { Input = detail };
        if (ExecutionGraphSupport.Normalized(node) is not { } normalized) return;
        nodes[nodeID] = normalized; order.Add(nodeID); emit(normalized);
    }

    /// Header and readable body of an AskUserQuestion input: every question with its options.
    internal static (string? Title, string Body) QuestionSummary(JsonElement input)
    {
        var questions = Objects(MetadataJson.Property(input, "questions")) ?? [];
        var header = questions.Count > 0 ? questions[0].Text("header") : null;
        var title = string.IsNullOrEmpty(header) ? null : Locale.Get("graph.block.questionPrefix") + header;
        var body = string.Join("\n\n", questions.Select(question =>
        {
            var text = question.Text("question") ?? "";
            var options = (Objects(MetadataJson.Property(question, "options")) ?? []).Select(o => o.Text("label")).OfType<string>().ToList();
            return options.Count == 0 ? text : text + "\n" + string.Join("\n", options.Select(o => "  ○ " + o));
        }));
        return (title, body);
    }

    /// The array's elements when every one is an object, else nil.
    private static List<JsonElement>? Objects(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object) ? value.EnumerateArray().ToList() : null;
    private static string? Key(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && Encoding.UTF8.GetByteCount(text) <= 512 ? text : null;
    private static string? Key(string? value) =>
        value is { Length: > 0 } && Encoding.UTF8.GetByteCount(value) <= 512 ? value : null;
    private static string? Text(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (Objects(value) is not { } blocks) return null;
        var values = blocks.Where(b => b.Text("type") == "text").Select(b => b.Text("text")).OfType<string>().ToList();
        return values.Count == 0 ? null : string.Join("\n", values);
    }
    private static bool IsTrue(JsonElement value, string key) => MetadataJson.Property(value, key).ValueKind == JsonValueKind.True;
    private static bool AgentTool(string? name) => name?.ToLowerInvariant() is "agent" or "task";

    private string? EnsureAgent(string toolID) => EnsureNode(toolID, "agent", Locale.Get("graph.block.agent"));
    private string? EnsureNode(string toolID, string kind, string title)
    {
        var id = ExecutionGraphSupport.AgentNodeID(runID, toolID);
        if (nodes.ContainsKey(id)) return id;
        if (nodes.Count >= ExecutionGraphSupport.MaximumNodes) return null;
        var value = new ExecutionGraphNode(id, runID, null, kind, "running", title);
        nodes[id] = value; order.Add(id); emit(value);
        return id;
    }
    private void Update(string id, Func<ExecutionGraphNode, ExecutionGraphNode> change, bool reopening = false)
    {
        if (finished || !nodes.TryGetValue(id, out var previous)) return;
        var value = change(previous);
        var restart = reopening && Provider == "codex" && previous.Kind == "agent" && (previous.ActivityGeneration ?? 0) < ExecutionGraphSupport.MaximumActivityGeneration;
        if (restart) value = value with { ActivityGeneration = (previous.ActivityGeneration ?? 0) + 1 };
        // Late starts and stdout fallbacks must not resurrect a final snapshot.
        if (!restart && previous.State is "error" or "stopped" && value.State != previous.State) value = value with { State = previous.State, Output = previous.Output ?? value.Output };
        else if (!restart && ExecutionGraphSupport.Terminal(previous.State) && !ExecutionGraphSupport.Terminal(value.State)) value = value with { State = previous.State };
        if (ExecutionGraphSupport.Normalized(value) is not { } normalized || normalized.Equals(previous)) return;
        normalized = normalized with { UpdatedAt = Wire.Now() }; nodes[id] = normalized; emit(normalized);
    }
    private void Append(LogEntry entry, string id) => Update(id, value =>
    {
        var entries = value.Entries.ToList(); var index = entries.FindIndex(e => e.Id == entry.Id);
        if (index >= 0) entries[index] = entry with { Timestamp = entries[index].Timestamp }; else entries.Add(entry);
        return value with { Entries = entries };
    });
    private LogEntry Entry(string id, string kind, string text, AgentActivity? activity = null) => new(id, kind, text, Wire.Now(), Provider, activity);
    private void RememberTool(string tool, Owner owner)
    {
        if (!toolOwners.ContainsKey(tool))
        {
            toolOrder.Add(tool);
            if (toolOrder.Count > 512) { toolOwners.Remove(toolOrder[0]); toolOrder.RemoveAt(0); }
        }
        // A child owner is more specific than a delayed unscoped Mod event.
        if (toolOwners.TryGetValue(tool, out var old) && old != Owner.Node(mainID) && owner == Owner.Node(mainID)) return;
        toolOwners[tool] = owner;
    }
    private string? NodeID(Owner owner) => !owner.IsAgent ? owner.Id : aliases.GetValueOrDefault(owner.Id);
    private void SetParent(string id, Owner owner)
    {
        if (NodeID(owner) is { } parent && parent != id)
        {
            if (Provider == "codex")
            {
                string? ancestor = parent; var seen = new HashSet<string>();
                while (ancestor is not null && seen.Add(ancestor))
                {
                    if (ancestor == id) { pendingParents.Remove(id); return; }
                    ancestor = nodes.GetValueOrDefault(ancestor)?.ParentId;
                }
            }
            pendingParents.Remove(id);
            Update(id, n => n with { ParentId = parent });
        }
        else if (owner.IsAgent) pendingParents[id] = owner.Id;
    }
    private void Alias(string agent, string id)
    {
        if (aliases.TryGetValue(agent, out var existing) && existing != id) return;
        if (existing is null && aliases.Count >= 512) return;
        aliases[agent] = id;
        foreach (var child in pendingParents.Where(p => p.Value == agent).Select(p => p.Key).ToList()) SetParent(child, Owner.Node(id));
        if (pendingAgents.Remove(agent, out var pending))
        {
            foreach (var activity in pending.Activities) AppendActivity(activity, id);
            if (pending.Metadata is { } metadata) Apply(metadata, id);
        }
    }

    /// Observe hierarchy before the stream parser creates the corresponding tool
    /// activity, so child tool rows take the same route as child Markdown.
    public void Consume(JsonElement value)
    {
        if (finished || value.ValueKind != JsonValueKind.Object) return;
        if (Provider == "codex") { ConsumeCodex(value); return; }
        var parentTool = Key(MetadataJson.Property(value, "parent_tool_use_id"));
        if (parentTool is not null) EnsureAgent(parentTool);
        // Over-cap child events are omitted, never reclassified as main text.
        var current = parentTool is null ? mainID : ExecutionGraphSupport.AgentNodeID(runID, parentTool);
        var owner = Owner.Node(current);
        var type = value.Text("type");
        var message = MetadataJson.Property(value, "message");
        if (type == "assistant" && message.ValueKind == JsonValueKind.Object)
        {
            var activityIds = (Objects(MetadataJson.Property(message, "content")) ?? []).Where(b => b.Text("type") == "tool_use")
                .Select(b => Key(MetadataJson.Property(b, "id"))).OfType<string>().ToList();
            if (GraphTokenUsage.Parse(MetadataJson.Property(message, "usage")) is { } usage)
            {
                unnamedUsage++;
                RecordUsage(usage, Key(MetadataJson.Property(message, "id")) ?? Key(MetadataJson.Property(value, "uuid")) ?? "unnamed:" + unnamedUsage, current, message.Text("model"), activityIds);
            }
        }
        if (type == "system" && value.Text("subtype") == "compact_boundary")
        {
            compactions++;
            Compaction(Key(MetadataJson.Property(value, "uuid")) ?? "n" + compactions, current, ContextCompaction.ClaudeSummary(MetadataJson.Property(value, "compact_metadata")));
            return;
        }
        if (type == "assistant" && message.ValueKind == JsonValueKind.Object && Objects(MetadataJson.Property(message, "content")) is { } blocks)
        {
            var content = MetadataJson.Property(message, "content");
            // A preamble beside a tool_use is not the reply; wait for a text-only answer.
            if (parentTool is null && pendingSteers.Count > 0 && !blocks.Any(b => b.Text("type") == "tool_use") && Text(content) is { Length: > 0 } reply)
            {
                var answered = pendingSteers.ToList(); pendingSteers.Clear();
                foreach (var id in answered) Update(id, n => n with { State = "completed", Output = reply });
            }
            if (parentTool is not null && current != mainID && Text(content) is { Length: > 0 } text)
            {
                var messageKey = Key(MetadataJson.Property(value, "uuid")) ?? Key(MetadataJson.Property(message, "id")) ?? ExecutionGraphSupport.Identifier(runID, text);
                Append(Entry(ExecutionGraphSupport.Identifier(runID, current + ":message:" + messageKey), "assistant", text), current);
            }
            foreach (var block in blocks.Where(b => b.Text("type") == "tool_use"))
            {
                if (Key(MetadataJson.Property(block, "id")) is not { } toolID) continue;
                RememberTool(toolID, owner);
                var input = MetadataJson.Property(block, "input");
                if (input.ValueKind != JsonValueKind.Object) input = default;
                var background = IsTrue(input, "run_in_background");
                if (AgentTool(block.Text("name")))
                {
                    if (EnsureAgent(toolID) is not { } child) continue;
                    SetParent(child, owner);
                    if (background) backgroundTools.Add(toolID);
                    Update(child, node =>
                    {
                        if (input.Text("prompt") is { } prompt) node = node with { Input = prompt };
                        // The first key present wins, as in the macOS `??` chain.
                        var named = new[] { "name", "description", "subagent_type" }.Select(k => MetadataJson.Property(input, k)).FirstOrDefault(p => p.ValueKind != JsonValueKind.Undefined);
                        if (named.ValueKind == JsonValueKind.String && named.GetString() is { Length: > 0 } title) node = node with { Title = title };
                        return node;
                    });
                }
                else if (block.Text("name") == "AskUserQuestion" && EnsureNode(toolID, "question", Locale.Get("graph.block.question")) is { } question)
                {
                    // Each question to the user is its own block; the answer settles it.
                    SetParent(question, owner);
                    var summary = QuestionSummary(input);
                    Update(question, node => node with { State = "waiting", Title = summary.Title ?? node.Title, Input = summary.Body });
                }
                else if (background && EnsureNode(toolID, "task", Locale.Get("graph.block.task")) is { } task)
                {
                    // A backgrounded command outlives its tool result. It gets its
                    // own child block; the engine's task notification settles it.
                    SetParent(task, owner);
                    backgroundTools.Add(toolID);
                    Update(task, node =>
                    {
                        var command = input.Text("command");
                        if (command is not null) node = node with { Input = command };
                        if (input.Text("description") is { Length: > 0 } title) node = node with { Title = title };
                        else if (command is { Length: > 0 }) node = node with { Title = command };
                        return node;
                    });
                }
            }
        }
        else if (type == "user" && message.ValueKind == JsonValueKind.Object)
        {
            foreach (var block in (Objects(MetadataJson.Property(message, "content")) ?? []).Where(b => b.Text("type") == "tool_result"))
            {
                if (Key(MetadataJson.Property(block, "tool_use_id")) is not { } toolID) continue;
                RememberTool(toolID, owner);
                var child = ExecutionGraphSupport.AgentNodeID(runID, toolID);
                if (!nodes.TryGetValue(child, out var node)) continue;
                var output = Text(MetadataJson.Property(block, "content"));
                if (node.Kind == "task") { AcknowledgeTask(child, block); continue; }
                if (node.Kind == "question")
                {
                    Update(child, n => n with { State = IsTrue(block, "is_error") ? "stopped" : "completed", Output = output });
                    continue;
                }
                // Background Agent tool results acknowledge launch; they are
                // not the agent's answer. Its turn.complete is authoritative.
                if (backgroundTools.Contains(toolID)) continue;
                Update(child, n => ExecutionGraphSupport.Terminal(n.State) ? n : n with { State = IsTrue(block, "is_error") ? "error" : "completed", Output = output });
            }
            // Task completion arrives as an injected user message, not a tool event.
            if (Text(MetadataJson.Property(message, "content")) is { } text && text.Contains("<task-notification>", StringComparison.Ordinal)) SettleTasks(text);
        }
        else if (type == "result" && !ClaudeStream.IsNotificationResult(value))
        {
            if (parentTool is null) Update(mainID, n => n with { Output = value.Text("result") });
            else if (current != mainID)
                Update(current, n => n with
                {
                    Output = value.Text("result"),
                    State = IsTrue(value, "is_error") || (value.Text("subtype") ?? "").StartsWith("error", StringComparison.Ordinal) ? "error" : "completed",
                });
        }
    }

    // Codex exec JSONL. Codex has no hook bridge. The root thread's items carry
    // the final message and turn usage; `collab_tool_call` items describe
    // subagents by thread ID with a prompt and a state, not their inner activity.
    private static string? CodexAgentState(string status) => status switch
    {
        "pending_init" or "running" => "running",
        "completed" => "completed",
        "interrupted" or "shutdown" => "stopped",
        "errored" or "not_found" => "error",
        _ => null,
    };
    internal static GraphTokenUsage? CodexUsage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var mapped = new Dictionary<string, JsonElement>();
        foreach (var (source, target) in new[] { ("input_tokens", "input_tokens"), ("output_tokens", "output_tokens"), ("cached_input_tokens", "cache_read_input_tokens"), ("cache_write_input_tokens", "cache_creation_input_tokens") })
            if (value.TryGetProperty(source, out var number)) mapped[target] = number;
        return GraphTokenUsage.Parse(JsonSerializer.SerializeToElement(mapped));
    }
    private string? EnsureCodexAgent(string thread)
    {
        if (codexAgents.TryGetValue(thread, out var existing)) return existing;
        if (codexAgents.Count >= ExecutionGraphSupport.MaximumNodes || nodes.Count >= ExecutionGraphSupport.MaximumNodes) return null;
        var id = ExecutionGraphSupport.Identifier(runID, "codex-agent:" + thread);
        var value = new ExecutionGraphNode(id, runID, mainID, "agent", "running", "Codex · " + Prefix(thread, 12));
        nodes[id] = value; order.Add(id); codexAgents[thread] = id; emit(value);
        Alias(thread, id);
        return id;
    }
    private void ConsumeCodex(JsonElement value)
    {
        if (value.Text("type") is not { } type) return;
        if (type == "thread.started" && CodexCollaborationItem.Key(MetadataJson.Property(value, "thread_id")) is { } rootThread)
        {
            codexRootThread = rootThread; Alias(rootThread, mainID); return;
        }
        if (type == "turn.completed")
        {
            if (CodexUsage(MetadataJson.Property(value, "usage")) is not { } usage) return;
            // Each turn reports its own usage once; sum turns for the main block.
            unnamedUsage++;
            RecordUsage(usage, "turn:" + unnamedUsage, mainID, configuredModel, [], configuredModel is not null);
            return;
        }
        var item = MetadataJson.Property(value, "item");
        if (type is not ("item.started" or "item.updated" or "item.completed") || item.ValueKind != JsonValueKind.Object || item.Text("type") is not { } itemType) return;
        if (itemType == "agent_message")
        {
            if (type == "item.completed" && item.Text("text") is { Length: > 0 } text) Update(mainID, n => n with { Output = text });
            return;
        }
        if (itemType == "context_compaction")
        {
            if (type != "item.completed") return;
            compactions++;
            Compaction(Key(MetadataJson.Property(item, "id")) ?? "n" + compactions, mainID, ContextCompaction.CodexSummary);
            return;
        }
        if (CodexCollaborationItem.Parse(item) is not { } call) return;
        // Completed envelopes may be repeated or followed by a late start.
        // An acknowledged input creates at most one new assignment generation.
        if (codexSettledCalls.Contains(call.Id)) return;
        var threads = call.Threads;
        if (!codexCallGenerations.ContainsKey(call.Id))
        {
            codexObservedCallOrder.Add(call.Id);
            if (codexObservedCallOrder.Count > 512) { codexCallGenerations.Remove(codexObservedCallOrder[0]); codexObservedCallOrder.RemoveAt(0); }
            codexCallGenerations[call.Id] = threads.ToDictionary(t => t, t => codexAgents.TryGetValue(t, out var nid) && nodes.TryGetValue(nid, out var n) ? n.ActivityGeneration ?? 0 : 0);
        }
        var completed = type == "item.completed";
        var succeeded = completed && call.Status == "completed";
        if (completed)
        {
            codexSettledCalls.Add(call.Id); codexCallOrder.Add(call.Id);
            if (codexCallOrder.Count > 512) { codexSettledCalls.Remove(codexCallOrder[0]); codexCallOrder.RemoveAt(0); }
        }
        var owner = call.Sender is { } sender && codexRootThread is not null && sender != codexRootThread ? Owner.Agent(sender) : Owner.Node(mainID);
        RememberTool(call.Id, owner);
        foreach (var thread in threads.Where(t => t != codexRootThread))
        {
            if (EnsureCodexAgent(thread) is not { } id) continue;
            // A wait already in flight before a new instruction may finish
            // late with the previous assignment's answer. Keep its tool row,
            // but do not settle or overwrite the newer child generation.
            var addressed = call.Receivers.Contains(thread);
            var stale = codexCallGenerations.TryGetValue(call.Id, out var generations) && generations.TryGetValue(thread, out var seen)
                && seen < (nodes.GetValueOrDefault(id)?.ActivityGeneration ?? 0);
            // Every accepted input is a real assignment, even if two sends
            // were in flight together. Preserve both prompts; stale returned
            // agent states must still not overwrite the newer assignment.
            if (stale && !(succeeded && call.Tool == "send_input" && addressed)) continue;
            // Resolve the sender later if a nested spawn precedes its parent.
            // Never manufacture another root or point a node at itself.
            if (call.Tool == "spawn_agent" && addressed) SetParent(id, owner);
            var restarting = succeeded && call.Tool == "send_input" && addressed;
            if (restarting && nodes.GetValueOrDefault(id)?.Output is { Length: > 0 } previousOutput)
            {
                var generation = nodes[id].ActivityGeneration ?? 0;
                Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":answer:" + generation), "assistant", previousOutput), id);
            }
            Update(id, node =>
            {
                if (call.Tool == "spawn_agent" && addressed && call.Prompt is { } prompt && node.Input is null)
                    node = node with { Input = prompt, Title = ActivitySupport.Clean(prompt, 100, true) + " · " + Prefix(thread, 12) };
                if (restarting) node = node with { State = "running", Output = null };
                if (!stale && call.States.TryGetValue(thread, out var state))
                {
                    if (CodexAgentState(state.Status) is { } mapped && !(state.Status == "shutdown" && node.State == "completed")) node = node with { State = mapped };
                    if (state.Message is { Length: > 0 } agentMessage) node = node with { Output = agentMessage };
                }
                // The call's failure reports an operation failure, not a child
                // failure. A successful spawn also does not complete its child.
                if (addressed && succeeded && call.Tool == "close_agent" && !ExecutionGraphSupport.Terminal(node.State)) node = node with { State = "stopped" };
                return node;
            }, restarting);
            if (restarting && call.Prompt is { } input)
                Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":input:" + call.Id), "user", input), id);
        }
    }

    /// One message is streamed as several events that all carry its usage, so
    /// a block adds each message once and keeps that message's latest figure.
    /// On first observation the model and activity IDs are recorded.
    private void RecordUsage(GraphTokenUsage usage, string message, string node, string? model = null, IReadOnlyList<string>? activityIds = null, bool markedAsConfigured = false)
    {
        if (!nodes.ContainsKey(node)) return;
        GraphTokenUsage? previous = messageUsage.TryGetValue(message, out var known) ? known : null;
        if (previous == usage) return;
        if (previous is null)
        {
            messageOrder.Add(message);
            if (messageOrder.Count > 1_024) { messageUsage.Remove(messageOrder[0]); messageOrder.RemoveAt(0); }
            if (model is not null) messageModel[message] = model;
            messageActivityIds[message] = activityIds ?? [];
            messageConfigured[message] = markedAsConfigured;
            if (!nodeResponseOrder.TryGetValue(node, out var responses)) nodeResponseOrder[node] = responses = [];
            responses.Add(message);
        }
        messageUsage[message] = usage;
        var records = BuildResponseRecords(node);
        Update(node, n => n with { Usage = (n.Usage ?? new()) - (previous ?? new()) + usage, ResponseRecords = records });
    }
    private List<GraphResponseRecord> BuildResponseRecords(string nodeId) =>
        !nodeResponseOrder.TryGetValue(nodeId, out var responses) ? [] : responses.Where(messageUsage.ContainsKey)
            .Select(id => new GraphResponseRecord(id, messageModel.GetValueOrDefault(id), messageUsage[id], (messageActivityIds.GetValueOrDefault(id) ?? []).ToList(), messageConfigured.GetValueOrDefault(id))).ToList();

    /// The launch acknowledgement names the engine's task ID. Keep it so a
    /// notification without a tool-use ID can still settle the block.
    private void AcknowledgeTask(string id, JsonElement block)
    {
        var text = Text(MetadataJson.Property(block, "content")) ?? "";
        if (IsTrue(block, "is_error"))
        {
            Update(id, n => n with { State = "error", Output = text.Length == 0 ? null : text });
            return;
        }
        if (TaskLaunch.Match(text) is { Success: true } match && taskAliases.Count < 512) taskAliases[match.Groups["task"].Value] = id;
        if (text.Length > 0) Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":launch"), "system", text), id);
    }
    private static string? Tag(string body, string name)
    {
        var open = "<" + name + ">"; var start = body.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = body.IndexOf("</" + name + ">", start + open.Length, StringComparison.Ordinal);
        if (end < 0) return null;
        var value = body[(start + open.Length)..end].Trim();
        return value.Length == 0 ? null : value;
    }
    private void SettleTasks(string text)
    {
        const string open = "<task-notification>", close = "</task-notification>";
        var remaining = text;
        while (remaining.IndexOf(open, StringComparison.Ordinal) is var start and >= 0 && remaining.IndexOf(close, start + open.Length, StringComparison.Ordinal) is var end and >= 0)
        {
            var body = remaining[(start + open.Length)..end];
            remaining = remaining[(end + close.Length)..];
            var byTool = Tag(body, "tool-use-id") is { } tool && ExecutionGraphSupport.AgentNodeID(runID, tool) is var toolNode && nodes.ContainsKey(toolNode) ? toolNode : null;
            var id = byTool ?? (Tag(body, "task-id") is { } task ? taskAliases.GetValueOrDefault(task) : null);
            if (id is null || nodes.GetValueOrDefault(id)?.Kind != "task") continue;
            var status = Tag(body, "status")?.ToLowerInvariant() ?? "completed";
            var state = status == "completed" ? "completed" : status is "failed" or "error" ? "error" : "stopped";
            var summary = Tag(body, "summary");
            Update(id, n => ExecutionGraphSupport.Terminal(n.State) ? n : n with { State = state, Output = summary ?? n.Output });
        }
    }

    public void ReceiveMod(ModMetadata value)
    {
        if (finished) return;
        if (Key(value.ToolUseId) is { } tool)
        {
            if (Key(value.AgentId) is { } agentOwner) RememberTool(tool, Owner.Agent(agentOwner));
            else if (value.Event is "tool.call" or "tool.complete") RememberTool(tool, Owner.Node(mainID));
        }
        if (value.Graph is not { Version: 1 } metadata) return;
        string? id;
        if (Key(metadata.ParentToolUseId) is { } toolID)
        {
            id = EnsureAgent(toolID);
            if (id is not null)
            {
                var parent = Key(metadata.ParentAgentId) is { } parentAgent ? Owner.Agent(parentAgent) : Owner.Node(mainID);
                SetParent(id, parent);
                // The Agent call belongs to its parent, never to the spawned child.
                RememberTool(toolID, parent);
                if (Key(metadata.AgentId) is { } agent) Alias(agent, id);
            }
        }
        else id = Key(metadata.AgentId) is { } agent ? aliases.GetValueOrDefault(agent) : null;
        if (id is not null) Apply(metadata, id);
        else if (Key(metadata.AgentId) is { } agent && (pendingAgents.ContainsKey(agent) || pendingAgents.Count < ExecutionGraphSupport.MaximumNodes))
        {
            var pending = pendingAgents.GetValueOrDefault(agent) ?? new PendingAgent();
            // Keep a final observation when a late starting event arrives.
            var previousState = pending.Metadata?.Phase;
            if (previousState is "error" or "stopped") { if (metadata.Phase == previousState) pending.Metadata = metadata; }
            else if (pending.Metadata is null || !ExecutionGraphSupport.Terminal(pending.Metadata.Phase) || ExecutionGraphSupport.Terminal(metadata.Phase)) pending.Metadata = metadata;
            pendingAgents[agent] = BoundedPending(pending, agent);
        }
    }
    private void Apply(ModGraphMetadata metadata, string id) => Update(id, node =>
    {
        if ((metadata.Name ?? metadata.AgentType) is { Length: > 0 } title) node = node with { Title = title };
        if (metadata.Input is not null) node = node with { Input = metadata.Input };
        if (metadata.Output is not null) node = node with { Output = metadata.Output };
        var state = metadata.Phase == "starting" ? "running" : metadata.Phase;
        if (ActivitySupport.States.Contains(state)) node = node with { State = state };
        return node;
    });

    /// Return true for a known child owner, including an agent whose spawn
    /// envelope is still in flight. Such rows must never leak into main logs.
    public bool Activity(AgentActivity value, string? toolID = null)
    {
        if (finished) return false;
        var owner = toolID is not null && toolOwners.TryGetValue(toolID, out var byTool) ? byTool
            : activityOwners.TryGetValue(value.Id, out var byActivity) ? byActivity : Owner.Node(mainID);
        if (!activityOwners.ContainsKey(value.Id))
        {
            activityOrder.Add(value.Id);
            if (activityOrder.Count > 512) { activityOwners.Remove(activityOrder[0]); activityOrder.RemoveAt(0); }
        }
        activityOwners[value.Id] = owner;
        if (NodeID(owner) is { } id)
        {
            if (id == mainID) return false;
            AppendActivity(value, id);
        }
        else if (owner.IsAgent && (pendingAgents.ContainsKey(owner.Id) || pendingAgents.Count < ExecutionGraphSupport.MaximumNodes))
        {
            var pending = pendingAgents.GetValueOrDefault(owner.Id) ?? new PendingAgent();
            var index = pending.Activities.FindIndex(a => a.Id == value.Id);
            if (index >= 0) pending.Activities[index] = value;
            else { pending.Activities.Add(value); pending.Activities = pending.Activities.TakeLast(ExecutionGraphSupport.MaximumEntries).ToList(); }
            pendingAgents[owner.Id] = BoundedPending(pending, owner.Id);
        }
        return true;
    }
    private PendingAgent BoundedPending(PendingAgent pending, string agent)
    {
        var metadata = pending.Metadata;
        var temporary = new ExecutionGraphNode(ExecutionGraphSupport.Identifier(runID, "pending:" + agent), runID, null, "agent", "running",
            metadata?.Name ?? metadata?.AgentType ?? Locale.Get("graph.block.agent"))
        {
            Input = metadata?.Input, Output = metadata?.Output,
            Entries = pending.Activities.Select(a => Entry(a.Id, "system", a.Summary, a)).ToList(),
        };
        if (ExecutionGraphSupport.Normalized(temporary) is not { } bounded) return new PendingAgent();
        var result = new PendingAgent { Activities = bounded.Entries.Select(e => e.Activity).OfType<AgentActivity>().ToList() };
        if (metadata is not null)
            result.Metadata = new ModGraphMetadata(1, metadata.Phase, Key(metadata.AgentId), Key(metadata.ParentAgentId), Key(metadata.ParentToolUseId), bounded.Title, Input: bounded.Input, Output: bounded.Output);
        return result;
    }
    private void AppendActivity(AgentActivity value, string id) => Append(Entry(value.Id, "system", value.Summary, value), id);

    public void Finish(string state)
    {
        if (finished || !ExecutionGraphSupport.Terminal(state)) return;
        // A dropped spawn may leave only an actual agent ID. Preserve those
        // observed results without inventing a parent or prompt.
        foreach (var (agent, pending) in pendingAgents.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
        {
            if (nodes.Count >= ExecutionGraphSupport.MaximumNodes) break;
            var id = ExecutionGraphSupport.Identifier(runID, "agent:" + agent);
            var node = new ExecutionGraphNode(id, runID, null, "agent", "running", Locale.Get("graph.block.agent"));
            nodes[id] = node; order.Add(id); emit(node);
            aliases[agent] = id;
            foreach (var activity in pending.Activities) AppendActivity(activity, id);
            if (pending.Metadata is { } metadata) Apply(metadata, id);
        }
        pendingAgents.Clear();
        foreach (var (id, agent) in pendingParents.ToList())
            if (aliases.TryGetValue(agent, out var parent) && id != parent) Update(id, n => n with { ParentId = parent });
        foreach (var id in order.Where(i => i != mainID).ToList())
        {
            if (!nodes.TryGetValue(id, out var node) || ExecutionGraphSupport.Terminal(node.State)) continue;
            // Process success does not prove that an unfinished background
            // agent succeeded or returned an answer.
            Update(id, value =>
            {
                var text = value.Kind switch
                {
                    "task" => Locale.Get("graph.block.unfinished.task"),
                    "steer" => Locale.Get("graph.block.unfinished.steer"),
                    "question" => Locale.Get("graph.block.unfinished.question"),
                    _ => Locale.Get("graph.block.unfinished.agent"),
                };
                return value with { State = state == "error" ? "error" : "stopped", Entries = [.. value.Entries, Entry(ExecutionGraphSupport.Identifier(runID, id + ":unfinished"), "system", text)] };
            });
        }
        Update(mainID, n => n with { State = state });
        finished = true;
    }

    /// <summary>
    /// The request's own graph as one MightyGraphRun, from the latest snapshot of
    /// every node. Only meaningful once Finish() settled the unfinished blocks.
    /// Mirrors the projection macOS RunSession.recordGraph builds node by node.
    /// </summary>
    public MightyGraphRun? BuildRun()
    {
        if (!finished || !nodes.TryGetValue(mainID, out var main)) return null;
        var run = new MightyGraphRun
        {
            Id = runID,
            Input = main.Input ?? "",
            Status = main.State,
            FinalOutput = main.Output is { Length: > 0 } ? main.Output : null,
            Usage = main.Usage,
            ResponseRecords = main.ResponseRecords,
            Provider = Provider,
            SourceRunID = runID,
            ConfiguredModel = configuredModel,
            NodeModelLabel = GraphModelLabel.NodeModelLabel(null, configuredModel),
        };
        foreach (var id in order.Where(value => value != mainID))
        {
            if (!nodes.TryGetValue(id, out var node)) continue;
            var agent = new MightyGraphAgent
            {
                Id = node.Id,
                ParentID = node.ParentId == mainID ? null : node.ParentId,
                Title = node.Title,
                Input = node.Input ?? "",
                Status = node.State,
                Entries = [.. node.Entries],
                Kind = node.Kind is "task" or "steer" or "compact" or "question" ? node.Kind : null,
                Usage = node.Usage,
                ActivityGeneration = node.ActivityGeneration,
                ResponseRecords = node.ResponseRecords,
            };
            if (node.Output is { Length: > 0 } output)
            {
                var answerId = Provider == "codex"
                    ? ExecutionGraphSupport.Identifier(runID, node.Id + ":answer:" + (node.ActivityGeneration ?? 0))
                    : node.Id + "-answer";
                var hasAnswer = agent.Entries.Any(e => Provider == "codex" ? e.Id == answerId : e.Kind == "assistant" && e.Text == output);
                if (!hasAnswer) agent.Entries.Add(new LogEntry(answerId, "assistant", output, Wire.Now(), Provider));
            }
            if (run.Agents.Count < 128) run.Agents.Add(agent);
        }
        MightyGraphSupport.ApplyProvider(run, Provider);
        MightyGraphSupport.RefreshResult(run);
        return run;
    }
}
