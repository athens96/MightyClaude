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
    /// <summary>The call's one line as a tool row: what it does, its prompt, and the first agents it names (macOS CodexCollaborationItem.summary).</summary>
    public string Summary
    {
        get
        {
            var action = Tool switch { "spawn_agent" => Locale.Get("run.agent.spawn"), "send_input" => Locale.Get("run.agent.sendInput"), "wait" => Locale.Get("run.agent.wait"), _ => Locale.Get("run.agent.close") };
            var targets = string.Join(", ", Threads.Take(4).Select(thread => ExecutionGraphSupport.Prefix(thread, 12)));
            return ActivitySupport.Clean(string.Join(" · ", new[] { action, Prompt, targets.Length == 0 ? null : targets }.OfType<string>()), ActivitySupport.MaximumSummaryBytes, true);
        }
    }
    /// <summary>Each named agent's state and what it said, or null while none has reported (macOS CodexCollaborationItem.output).</summary>
    public string? Output
    {
        get
        {
            var rows = Threads.Where(States.ContainsKey).Select(thread => { var state = States[thread]; var label = ExecutionGraphSupport.Prefix(thread, 12) + " · " + state.Status; return state.Message is { } message ? label + "\n" + message : label; }).ToList();
            return rows.Count == 0 ? null : ActivitySupport.Clean(string.Join("\n\n", rows), ActivitySupport.MaximumOutputBytes);
        }
    }
}

/// Observes public CLI/Mods events for one run and emits node snapshots. It
/// never reads another session's transcript or guesses child prompts.
/// Port of macOS ExecutionGraphTracker.
public sealed partial class ExecutionGraphTracker
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
    private static readonly Regex AgentLaunch = new(@"agentId: (?<agent>[A-Za-z0-9_-]+)");
    /// <summary>Where a background run writes its output: the rest of the line, as a path may hold spaces.</summary>
    private static readonly Regex OutputLaunch = new(@"Output is being written to: (?<path>[^\r\n]+)");
    private readonly object sync = new();
    public string RunID => runID;
    public string? CodexRootThread { get { lock (sync) return codexRootThread; } }
    private readonly string runID;
    private readonly string mainID;
    /// "claude" observes stream-json plus Mods; "codex" observes exec JSONL; "gemini" observes Antigravity's (agy) stream-json.
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
    /// Engine task IDs and background agent IDs → block: a notification or hand-back without a tool-use ID finds its block by them.
    private readonly Dictionary<string, string> taskAliases = [];
    /// Agent calls' <c>name</c> inputs → block: a hand-back may name its agent by that instead of its ID.
    private readonly Dictionary<string, string> agentNames = [];
    /// The notification summary an agent block's result holds until its hand-back's report replaces it.
    private readonly Dictionary<string, string> agentSummaries = [];
    private readonly Dictionary<string, GraphTokenUsage> messageUsage = [];
    private readonly Dictionary<string, string> messageModel = [];
    private readonly Dictionary<string, IReadOnlyList<string>> messageActivityIds = [];
    private readonly Dictionary<string, bool> messageConfigured = [];
    private readonly Dictionary<string, List<string>> nodeResponseOrder = [];
    private readonly List<string> pendingSteers = [];
    /// <summary>The request's own root result arrived; a steer after it opens a turn whose answer follows in the result.</summary>
    private bool turnEnded;
    /// <summary>What the steer that opened such a turn is called (중간 요청 or 위임 알림): its answer's section is headed by it.</summary>
    private string? steeredTurn;
    /// <summary>Notice runs opened so far (<see cref="BackgroundNoticeRuns"/>); the next one is numbered one more.</summary>
    private int noticeIndex;
    /// <summary>The notice run of the turn a root-level notice opened after the request's own turn: that turn's result is its result.</summary>
    private string? noticeTurn;
    /// <summary>The notice run, complete at once and without a result, that notices taken into a running turn share until its result.</summary>
    private string? absorbedRun;
    /// <summary>The earlier notice run a notice turn answers in again while it concerns only blocks copied before. A turn about blocks
    /// of two earlier runs answers in the first one it names.</summary>
    private string? mergeTurn;
    /// <summary>The state <see cref="mergeTurn"/>'s main had before the turn reopened it, given back if the turn brings new work after all.</summary>
    private string? mergeRestore;
    /// <summary>Each notice run's copies of finished blocks, oldest first; the runs in the order they opened.</summary>
    private readonly Dictionary<string, List<string>> noticeCopies = [];
    private readonly List<string> noticeRuns = [];
    /// <summary>Each copied block's latest notice run.</summary>
    private readonly Dictionary<string, string> copiedIn = [];
    /// <summary>Moves each time a notice run opens or runs again (<see cref="NoticeRevision"/>).</summary>
    private int noticeRevision;
    /// <summary>Notice runs with a node that changed since <see cref="TakeNoticeRuns"/> last built them.</summary>
    private readonly HashSet<string> changedNoticeRuns = [];
    private readonly List<string> messageOrder = [];
    private int unnamedUsage;
    private int compactions;
    private bool finished;

    public ExecutionGraphTracker(string runID, string? input, string provider, string? configuredModel, Action<ExecutionGraphNode> emit)
    {
        this.runID = runID; mainID = ExecutionGraphSupport.MainNodeID(runID); Provider = provider; this.configuredModel = configuredModel;
        // Every node goes out as it changes; a notice run's (BackgroundNoticeRuns) also marks that run for TakeNoticeRuns.
        this.emit = node => { if (node.RunId != runID) changedNoticeRuns.Add(node.RunId); emit(node); };
        var main = new ExecutionGraphNode(mainID, runID, null, "main", "running", ProviderLabel(provider)) { Input = input };
        if (ExecutionGraphSupport.Normalized(main) is { } normalized) { nodes[mainID] = normalized; order.Add(mainID); emit(normalized); }
    }
    private static string ProviderLabel(string id) => id switch { "codex" => "Codex", "gemini" => "Antigravity", _ => "Claude" };
    private static string Prefix(string value, int count) => ExecutionGraphSupport.Prefix(value, count);

    /// A message the user sent while the turn ran. Claude reads it from stdin
    /// between tool calls; the block under main shows it until the next
    /// root-level answer, which is taken as the reply.
    public void Steer(string id, string text)
    {
        lock (sync)
        {
            var before = pendingSteers.Count;
            SteerLocked(id, text);
            // After the request's result the steer starts a new turn in the same process (macOS).
            if (turnEnded && steeredTurn is null && pendingSteers.Count > before)
                steeredTurn = Locale.Get(MightyGraphSupport.IsDelegationNotice(text) ? "graph.block.delegationNotice" : "graph.block.steer");
        }
    }
    private void SteerLocked(string id, string text)
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
    /// <param name="continuing">An Antigravity sub-agent that answered while helpers it started were still out goes on with the same assignment when one reports.</param>
    private void Update(string id, Func<ExecutionGraphNode, ExecutionGraphNode> change, bool reopening = false, bool continuing = false)
    {
        if (finished || !nodes.TryGetValue(id, out var previous)) return;
        var value = change(previous);
        var restart = reopening && Provider is "codex" or "gemini" && previous.Kind == "agent" && (previous.ActivityGeneration ?? 0) < ExecutionGraphSupport.MaximumActivityGeneration;
        if (restart) value = value with { ActivityGeneration = (previous.ActivityGeneration ?? 0) + 1 };
        // Late starts and stdout fallbacks must not resurrect a final snapshot.
        if (!restart && previous.State is "error" or "stopped" && value.State != previous.State) value = value with { State = previous.State, Output = previous.Output ?? value.Output };
        else if (!restart && ExecutionGraphSupport.Terminal(previous.State) && !ExecutionGraphSupport.Terminal(value.State)
            && !(continuing && Provider == "gemini" && previous.Kind == "agent" && previous.State == "completed" && value.State == "running")) value = value with { State = previous.State };
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
    public void Consume(JsonElement value) { lock (sync) ConsumeLocked(value); }
    private void ConsumeLocked(JsonElement value)
    {
        if (finished || value.ValueKind != JsonValueKind.Object) return;
        if (Provider == "codex") { ConsumeCodex(value); return; }
        if (Provider == "gemini") { ConsumeAntigravity(value); return; }
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
                    if (input.Text("name") is { Length: > 0 } agentName && Encoding.UTF8.GetByteCount(agentName) <= 512 && (agentNames.ContainsKey(agentName) || agentNames.Count < 512)) agentNames[agentName] = child;
                    Update(child, node =>
                    {
                        if (background) node = node with { Background = true };
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
                        node = node with { Background = true };
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
                // not the agent's answer. Its turn.complete, notification or hand-back is.
                if (backgroundTools.Contains(toolID)) { RememberAgentID(child, block); continue; }
                Update(child, n => ExecutionGraphSupport.Terminal(n.State) ? n : n with { State = IsTrue(block, "is_error") ? "error" : "completed", Output = output });
            }
            // Task completion arrives as an injected user message, not a tool event; so does a sub-agent's final report.
            // Either settles its block and copies it into a notice run; only a root-level one opens a notice turn (macOS).
            if (Text(MetadataJson.Property(message, "content")) is { } text)
            {
                if (text.Contains("<task-notification>", StringComparison.Ordinal)) SettleTasks(text, parentTool is null);
                if (text.Contains(AgentHandback.Opening, StringComparison.Ordinal)) SettleHandbacks(text, MetadataJson.Property(value, "origin"), parentTool is null);
            }
        }
        else if (type == "result" && (ClaudeStream.IsNotificationResult(value) || parentTool is null && (noticeTurn is not null || mergeTurn is not null)))
        {
            if (parentTool is not null) return;
            var (run, merged) = (noticeTurn, mergeTurn);
            ClearNoticeTurn();
            var answer = value.Text("result")?.Trim() ?? "";
            var failed = IsTrue(value, "is_error") || (value.Text("subtype") ?? "").StartsWith("error", StringComparison.Ordinal);
            if (run is not null)
            {
                // A notice turn's answer is its own run's result; the request's result stays as it was (macOS).
                Update(ExecutionGraphSupport.MainNodeID(run), n => n with { Output = answer.Length > 0 ? answer : n.Output, State = failed ? "error" : "completed" });
                return;
            }
            if (merged is not null)
            {
                // A turn about blocks an earlier notice run already holds goes on in that run's result, as a new paragraph.
                if (answer.Length > 0) Update(ExecutionGraphSupport.MainNodeID(merged), n => n with { Output = string.IsNullOrEmpty(n.Output) ? answer : n.Output + "\n\n" + answer });
                SetNoticeState(merged, failed ? "error" : "completed");
                return;
            }
            // Any other notice turn (a delegation notice, a sub-agent's message) follows the request's answer in the result.
            if (answer.Length == 0) return;
            Update(mainID, n => n with { Output = MightyGraphSupport.WithNoticeSection(n.Output, Locale.Get("graph.result.afterNotice"), answer) });
        }
        else if (type == "result")
        {
            // The request's own turn ended: it reads completed while background tasks it started still run; the process's end settles the rest.
            if (parentTool is null && steeredTurn is { } steered)
            {
                // A turn a steer opened after the request's result: its answer follows in the result, as a notice turn's does.
                ClearNoticeTurn();
                if (value.Text("result")?.Trim() is { Length: > 0 } answer)
                    Update(mainID, n => n with { Output = MightyGraphSupport.WithNoticeSection(n.Output, Locale.Get("graph.result.afterSteer", new Dictionary<string, string> { ["block"] = steered }), answer) });
            }
            else if (parentTool is null)
            {
                absorbedRun = null; turnEnded = true;
                Update(mainID, n => n with { Output = value.Text("result"), State = IsTrue(value, "is_error") || (value.Text("subtype") ?? "").StartsWith("error", StringComparison.Ordinal) ? n.State : "completed" });
            }
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
        // Where the run writes its output; a notification's <output-file> may move it.
        if (OutputLaunch.Match(text) is { Success: true } output)
        {
            var path = output.Groups["path"].Value.Trim();
            if (path.EndsWith('.')) path = path[..^1];
            if (path.Length > 0) Update(id, n => n with { OutputFile = path });
        }
        if (text.Length > 0) Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":launch"), "system", text), id);
    }
    /// A background Agent's launch result names its agent ID, which its
    /// notification's <c>task-id</c> and its hand-back's <c>from</c> carry.
    private void RememberAgentID(string id, JsonElement block)
    {
        if (Text(MetadataJson.Property(block, "content")) is not { } text || AgentLaunch.Match(text) is not { Success: true } match) return;
        var agent = match.Groups["agent"].Value;
        if (taskAliases.ContainsKey(agent) || taskAliases.Count < 512) taskAliases[agent] = id;
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
    private void SettleTasks(string text, bool notice)
    {
        const string open = "<task-notification>", close = "</task-notification>";
        var remaining = text;
        while (remaining.IndexOf(open, StringComparison.Ordinal) is var start and >= 0 && remaining.IndexOf(close, start + open.Length, StringComparison.Ordinal) is var end and >= 0)
        {
            var body = remaining[(start + open.Length)..end];
            remaining = remaining[(end + close.Length)..];
            var byTool = Tag(body, "tool-use-id") is { } tool && ExecutionGraphSupport.AgentNodeID(runID, tool) is var toolNode && nodes.ContainsKey(toolNode) ? toolNode : null;
            var id = byTool ?? (Tag(body, "task-id") is { } task ? taskAliases.GetValueOrDefault(task) : null);
            if (id is null || nodes.GetValueOrDefault(id)?.Kind is not { } kind || kind is not ("task" or "agent")) continue;
            var status = Tag(body, "status")?.ToLowerInvariant() ?? "completed";
            var state = status == "completed" ? "completed" : status is "failed" or "error" ? "error" : "stopped";
            var summary = Tag(body, "summary");
            if (kind == "agent")
            {
                // An agent's result is its report (SettleHandbacks); the summary only fills an empty one until the report comes.
                var empty = string.IsNullOrEmpty(nodes[id].Output);
                Update(id, n => n with { State = ExecutionGraphSupport.Terminal(n.State) ? n.State : state, Output = empty && summary is not null ? summary : n.Output });
                if (empty && summary is not null && nodes[id].Output is { } output) agentSummaries[id] = output;
                AddNotice(id, notice);
            }
            else
            {
                var file = Tag(body, "output-file");
                Update(id, n =>
                {
                    if (file is not null) n = n with { OutputFile = file };
                    return ExecutionGraphSupport.Terminal(n.State) ? n : n with { State = state, Output = summary ?? n.Output };
                });
                AddNotice(id, notice);
            }
        }
    }
    /// A finished background block goes into a notice run (<see cref="BackgroundNoticeRuns"/>) as a copy. A root-level notice after the
    /// request's own turn opens the notice turn's run, which every new block until that turn's result joins and whose result is Claude's
    /// answer. A notice taken into a running turn (a nested agent's, or one in the request's or a steer's own turn) joins a run that is
    /// complete at once and has no result; the running turn's answer stays its own. A block copied before only updates its latest copy: a
    /// notice turn about such blocks alone answers in that earlier run again (<see cref="mergeTurn"/>), and one that also brings a new block
    /// opens a run for the new blocks and adds nothing to the earlier one (macOS addNotice).
    private void AddNotice(string id, bool root)
    {
        if (Provider != "claude" || !nodes.ContainsKey(id)) return;
        var opensTurn = root && turnEnded && steeredTurn is null;
        if (copiedIn.TryGetValue(id, out var earlier))
        {
            Copy(id, earlier);
            if (noticeTurn is null && mergeTurn is null && opensTurn)
            {
                mergeTurn = earlier;
                mergeRestore = nodes.GetValueOrDefault(ExecutionGraphSupport.MainNodeID(earlier))?.State;
                SetNoticeState(earlier, "running");
            }
            return;
        }
        string run;
        if (noticeTurn is { } open) run = open;
        else if (opensTurn || mergeTurn is not null)
        {
            if (OpenNoticeRun(true) is not { } opened) return;
            // The turn brought new work after all: the earlier run gets nothing of it and ends as it was.
            if (mergeTurn is { } merged) { SetNoticeState(merged, mergeRestore ?? "completed"); mergeTurn = null; mergeRestore = null; }
            noticeTurn = opened; run = opened;
        }
        else if (absorbedRun is { } absorbed) run = absorbed;
        else
        {
            if (OpenNoticeRun(false) is not { } opened) return;
            absorbedRun = opened; run = opened;
        }
        Copy(id, run);
    }
    /// <summary>Creates or updates block <paramref name="id"/>'s copy in notice run <paramref name="run"/>, and the run's request lines.</summary>
    private void Copy(string id, string run)
    {
        if (!nodes.TryGetValue(id, out var block)) return;
        var main = ExecutionGraphSupport.MainNodeID(run);
        var joined = run + ":" + id;
        var copyID = Wire.Identifier(joined) ? joined : ExecutionGraphSupport.Identifier(run, "copy:" + id);
        if (!nodes.ContainsKey(copyID))
        {
            if (nodes.Count >= ExecutionGraphSupport.MaximumNodes) return;
            var copy = new ExecutionGraphNode(copyID, run, main, block.Kind, block.State, block.Title)
            {
                Input = block.Input, Output = block.Output, Background = true, OutputFile = block.OutputFile, SourceBlockId = id,
            };
            if (ExecutionGraphSupport.Normalized(copy) is not { } normalized) return;
            nodes[copyID] = normalized; order.Add(copyID); emit(normalized);
            if (!noticeCopies.TryGetValue(run, out var copies)) noticeCopies[run] = copies = [];
            copies.Add(copyID);
            if (copiedIn.ContainsKey(id) || copiedIn.Count < 512) copiedIn[id] = run;
        }
        else Update(copyID, n => n with { Title = block.Title, Input = block.Input, State = block.State, Output = block.Output, OutputFile = block.OutputFile });
        // The run's request: each finished block and how it ended, one per line.
        var lines = (noticeCopies.GetValueOrDefault(run) ?? []).Select(nodes.GetValueOrDefault).OfType<ExecutionGraphNode>().Select(copy =>
            (copy.Title is { Length: > 0 } title ? title : copy.Input ?? "") + " · "
            + Locale.Get(copy.State == "completed" ? "graph.block.taskNotice.completed" : copy.State == "error" ? "graph.block.taskNotice.failed" : "graph.block.taskNotice.stopped"));
        Update(main, n => n with { Input = string.Join("\n", lines) });
    }
    /// <summary>A notice run's main block running again for a turn that answers in it, or ended. Unlike <see cref="Update"/>, an ended
    /// main may run again: the turn after it is new work in the same run.</summary>
    private void SetNoticeState(string run, string state)
    {
        var main = ExecutionGraphSupport.MainNodeID(run);
        if (finished || !nodes.TryGetValue(main, out var node) || node.State == state) return;
        if (ExecutionGraphSupport.Normalized(node with { State = state }) is not { } normalized) return;
        normalized = normalized with { UpdatedAt = Wire.Now() };
        nodes[main] = normalized; emit(normalized);
        if (state == "running") noticeRevision += 1;
    }
    /// <summary>The next notice run's main block: running until a notice turn's result, or complete at once for absorbed notices.</summary>
    private string? OpenNoticeRun(bool running)
    {
        if (nodes.Count >= ExecutionGraphSupport.MaximumNodes) return null;
        var run = BackgroundNoticeRuns.RunID(runID, noticeIndex + 1);
        var main = new ExecutionGraphNode(ExecutionGraphSupport.MainNodeID(run), run, null, "main", running ? "running" : "completed", Locale.Get("graph.background.noticeRun"));
        if (ExecutionGraphSupport.Normalized(main) is not { } normalized) return null;
        noticeIndex += 1; noticeRevision += 1;
        nodes[normalized.Id] = normalized; order.Add(normalized.Id); emit(normalized);
        noticeRuns.Add(run);
        return run;
    }
    /// <summary>A notice turn or a steered turn ended: the next one starts with no notices.</summary>
    private void ClearNoticeTurn()
    {
        steeredTurn = null; noticeTurn = null; absorbedRun = null; mergeTurn = null; mergeRestore = null;
    }
    /// Messages a background sub-agent sent this session (AgentHandback). Its
    /// framed final report is its result and ends it; like the agent's
    /// notification it copies the block into a notice run (AddNotice). A
    /// message it sent while still running joins its block's log and changes
    /// nothing else. <paramref name="origin"/> is the event's: its <c>senderTaskId</c> is the agent ID
    /// of a lone message, and <c>handback: true</c> marks a lone one as the report (macOS settleHandbacks).
    private void SettleHandbacks(string text, JsonElement origin, bool notice)
    {
        var messages = AgentHandback.Parse(text);
        var lone = messages.Count == 1;
        var sender = lone ? Key(MetadataJson.Property(origin, "senderTaskId")) : null;
        foreach (var message in messages)
        {
            var id = taskAliases.GetValueOrDefault(message.From) ?? (sender is null ? null : taskAliases.GetValueOrDefault(sender)) ?? agentNames.GetValueOrDefault(message.From);
            if (id is null || nodes.GetValueOrDefault(id) is not { Kind: "agent" } node) continue;
            if (!message.IsHandback && !(lone && MetadataJson.Flag(origin, "handback")))
            {
                if (message.Body.Length > 0) Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":peer:" + message.Body), "assistant", message.Body), id);
                continue;
            }
            var terminal = ExecutionGraphSupport.Terminal(node.State);
            // A block that already ended keeps its state; its result takes the report only when empty or the notification's summary.
            var replaceable = !terminal || string.IsNullOrEmpty(node.Output) || node.Output == agentSummaries.GetValueOrDefault(id);
            var report = replaceable && message.Body.Length > 0;
            Update(id, n => n with { State = terminal ? n.State : "completed", Output = report ? message.Body : n.Output });
            if (report) agentSummaries.Remove(id);
            AddNotice(id, notice);
        }
    }

    public void ReceiveMod(ModMetadata value) { lock (sync) ReceiveModLocked(value); }
    private void ReceiveModLocked(ModMetadata value)
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
    public bool Activity(AgentActivity value, string? toolID = null) { lock (sync) return ActivityLocked(value, toolID); }
    private bool ActivityLocked(AgentActivity value, string? toolID)
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
    /// <summary>
    /// A child's tool or message pictures belong to its graph block. The spawning tool is the fallback when the individual
    /// tool's owner was never seen or was evicted; a child not yet bound to a block never sends its pictures to the main log.
    /// </summary>
    public bool Images(LogEntry entry, string? toolID, string? parentToolID = null)
    {
        lock (sync)
        {
            if (finished) return false;
            if (toolID is not null && toolOwners.TryGetValue(toolID, out var owner) && owner != Owner.Node(mainID))
            {
                if (NodeID(owner) is not { } id) return true;
                if (id != mainID) { Append(entry, id); return true; }
            }
            if (Key(parentToolID) is { } parent)
            {
                var child = ExecutionGraphSupport.AgentNodeID(runID, parent);
                if (nodes.ContainsKey(child)) Append(entry, child);
                return true;
            }
            return false;
        }
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

    public void Finish(string state) { lock (sync) FinishLocked(state); }
    private void FinishLocked(string state)
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
            // A notice turn the process ended before its answer ends with the request (macOS).
            if (node.Kind == "main") { Update(id, n => n with { State = state }); continue; }
            // Process success does not prove that an unfinished background
            // agent succeeded or returned an answer.
            Update(id, value =>
            {
                var text = value.Kind switch
                {
                    "task" => Locale.Get("graph.block.unfinished.task"),
                    // A notice or follow-up delegation handed into the turn is a 위임 알림, a task's notification a 작업 알림, not a person's 중간 요청 (macOS).
                    "steer" => Locale.Get(MightyGraphSupport.IsDelegationNotice(value.Input) ? "graph.block.unfinished.delegationNotice"
                        : MightyGraphSupport.IsTaskNotice(value.Input) ? "graph.block.unfinished.taskNotice" : "graph.block.unfinished.steer"),
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
    /// every node. Live snapshots are opt-in; Finish() settles unfinished blocks.
    /// Mirrors the projection macOS RunSession.recordGraph builds node by node.
    /// </summary>
    public MightyGraphRun? BuildRun(bool includeRunning = false) { lock (sync) return BuildRunLocked(runID, includeRunning); }

    /// <summary>
    /// The notice runs the process's background work opened (<see cref="BackgroundNoticeRuns"/>), oldest first, each as one
    /// MightyGraphRun flagged <c>NoticeRun</c>, as the macOS projection records them. Live snapshots are opt-in, as for <see cref="BuildRun"/>.
    /// </summary>
    public List<MightyGraphRun> BuildNoticeRuns(bool includeRunning = false)
    {
        lock (sync) return noticeRuns.Select(run => BuildRunLocked(run, includeRunning)).OfType<MightyGraphRun>().ToList();
    }

    /// <summary><see cref="BuildNoticeRuns"/> of only the notice runs that changed since the last call, which a live pane is handed again.</summary>
    public List<MightyGraphRun> TakeNoticeRuns(bool includeRunning = false)
    {
        lock (sync)
        {
            if (changedNoticeRuns.Count == 0 || !finished && !includeRunning) return [];
            var built = noticeRuns.Where(changedNoticeRuns.Contains).Select(run => BuildRunLocked(run, includeRunning)).OfType<MightyGraphRun>().ToList();
            changedNoticeRuns.Clear();
            return built;
        }
    }

    /// <summary>Moves each time a notice run opens or a notice turn reopens one: the caller hands the runs over before the turn's lines.</summary>
    public int NoticeRevision { get { lock (sync) return noticeRevision; } }

    private MightyGraphRun? BuildRunLocked(string id, bool includeRunning)
    {
        var mainOf = ExecutionGraphSupport.MainNodeID(id);
        if (!finished && !includeRunning || !nodes.TryGetValue(mainOf, out var main)) return null;
        var notice = id != runID;
        var run = new MightyGraphRun
        {
            Id = id,
            Input = main.Input ?? "",
            Status = main.State,
            FinalOutput = main.Output is { Length: > 0 } ? main.Output : null,
            Usage = main.Usage,
            ResponseRecords = main.ResponseRecords,
            Provider = Provider,
            SourceRunID = id,
            ConfiguredModel = configuredModel,
            NodeModelLabel = GraphModelLabel.NodeModelLabel(null, configuredModel),
            NoticeRun = notice ? true : null,
        };
        foreach (var nodeID in order.Where(value => value != mainOf))
        {
            if (!nodes.TryGetValue(nodeID, out var node) || node.RunId != id) continue;
            var agent = new MightyGraphAgent
            {
                Id = node.Id,
                ParentID = node.ParentId == mainOf ? null : node.ParentId,
                Title = node.Title,
                Input = node.Input ?? "",
                Status = node.State,
                Entries = [.. node.Entries],
                Kind = node.Kind is "task" or "steer" or "compact" or "question" ? node.Kind : null,
                Usage = node.Usage,
                ActivityGeneration = node.ActivityGeneration,
                ResponseRecords = node.ResponseRecords,
                Background = node.Background,
                OutputFile = node.OutputFile,
                NoticeReply = node.NoticeReply,
                SourceBlockId = node.SourceBlockId,
            };
            if (node.Output is { Length: > 0 } output)
            {
                // Codex and Antigravity children take new assignments: one answer per generation.
                var answerId = Provider is "codex" or "gemini"
                    ? ExecutionGraphSupport.Identifier(runID, node.Id + ":answer:" + (node.ActivityGeneration ?? 0))
                    : node.Id + "-answer";
                var hasAnswer = agent.Entries.Any(e => Provider == "codex" ? e.Id == answerId : e.Id == answerId || e.Kind == "assistant" && e.Text == output);
                if (!hasAnswer) agent.Entries.Add(new LogEntry(answerId, "assistant", output, Wire.Now(), Provider));
            }
            if (run.Agents.Count < 128) run.Agents.Add(agent);
        }
        MightyGraphSupport.ApplyProvider(run, Provider);
        MightyGraphSupport.RefreshResult(run);
        return run;
    }
}
