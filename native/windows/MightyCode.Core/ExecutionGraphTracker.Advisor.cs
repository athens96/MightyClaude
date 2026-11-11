using System.Text.Json;

namespace MightyCode.Core;

// The advisor (Claude Code's server-side advisor tool, `claude --advisor <model>`) in a Claude run's graph: a `server_tool_use` block named
// `advisor` in the caller's own message opens a consultation and its `advisor_tool_result` settles it; a turn's result (`usage.iterations[]` of
// type `advisor_message`) and a transcript line's `advisorModel` name the advisor's model and its tokens. All of it is the run main block's
// optional advisor record (GraphAdvisor), never a tool block or a tool row (macOS ExecutionGraphTracker "Advisor").
public sealed partial class ExecutionGraphTracker
{
    /// <summary>The tools that change a file, for <see cref="GraphAdvisorCall.BeforeChange"/> (macOS RunAudit.kind == .write). Bash is not one:
    /// whether a command changed a file cannot be seen.</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal) { "Edit", "Write", "MultiEdit", "NotebookEdit", "apply_patch", "write_file", "replace", "edit_file" };

    /// <summary>The run main block that keeps a consultation: a notice turn's own run for what its main session asks, else the request's, whose
    /// blocks the sub-agents are.</summary>
    private string AdvisorOwner(bool root)
    {
        if (!root || (noticeTurn ?? mergeTurn) is not { } run) return mainID;
        var id = ExecutionGraphSupport.MainNodeID(run);
        return nodes.GetValueOrDefault(id)?.Kind == "main" ? id : mainID;
    }

    /// <summary><paramref name="caller"/> consulted the advisor (a <c>server_tool_use</c> block named <c>advisor</c>).</summary>
    private void AdvisorAsked(string call, string caller, bool root)
    {
        if (Provider != "claude" || !Wire.Identifier(call) || advisorOwners.ContainsKey(call)) return;
        var owner = AdvisorOwner(root);
        advisorOwners[call] = owner;
        advisorOrder.Add(call);
        if (advisorOrder.Count > 1_024) { advisorOwners.Remove(advisorOrder[0]); advisorOrder.RemoveAt(0); }
        string? asker = root ? null : caller;
        var time = Now;
        var unchanged = !changedFiles.Contains(caller);
        Update(owner, node =>
        {
            var advisor = node.Advisor ?? new GraphAdvisor();
            // Asking again is going on with the work: this caller's earlier consultations were not its last step.
            var calls = advisor.Consultations.Select(c => c.Caller == asker ? c with { Followed = true } : c).ToList();
            var total = Math.Min(GraphAdvisor.MaximumTotal, advisor.Count + 1);
            calls.Add(new GraphAdvisorCall(call, asker, time, State: "consulting", BeforeChange: unchanged ? true : null));
            return node with { Advisor = advisor with { Calls = calls, Total = total } };
        });
    }

    /// <summary>A consultation's result (<c>advisor_tool_result</c>): encrypted, the advice as text, or an error with its code.</summary>
    private void AdvisorAnswered(string call, JsonElement content)
    {
        if (!advisorOwners.TryGetValue(call, out var owner)) return;
        var result = content.ValueKind == JsonValueKind.Object ? content : default;
        var kind = result.ValueKind == JsonValueKind.Object ? result.Text("type") ?? "" : "";
        var time = Now;
        Update(owner, node =>
        {
            if (node.Advisor is not { } advisor) return node;
            var calls = advisor.Consultations.ToList();
            var index = calls.FindIndex(c => c.Id == call);
            if (index < 0 || calls[index].State != "consulting") return node;
            var value = calls[index] with { EndedAt = time };
            if (result.ValueKind == JsonValueKind.Object && result.Text("error_code") is { } code) value = value with { State = "error", ErrorCode = code };
            else if (kind.Contains("error", StringComparison.Ordinal)) value = value with { State = "error" };
            else
            {
                value = value with { State = result.ValueKind == JsonValueKind.Object && result.Text("stop_reason") == "refusal" ? "declined" : "done" };
                if (kind == "advisor_redacted_result") value = value with { Redacted = true };
                // Only the text the CLI gives: an encrypted reply has none.
                if (kind == "advisor_result" && result.Text("text") is { } text) value = value with { Text = text };
            }
            calls[index] = value;
            return node with { Advisor = advisor with { Calls = calls } };
        });
    }

    /// <summary><paramref name="caller"/> called a tool: its consultations so far were not its last step, and a file-changing tool is remembered.</summary>
    private void AdvisorWorked(string caller, bool root, string? tool)
    {
        if (Provider != "claude") return;
        if (tool is not null && FileTools.Contains(tool)) changedFiles.Add(caller);
        var owner = AdvisorOwner(root);
        string? asker = root ? null : caller;
        if (nodes.GetValueOrDefault(owner)?.Advisor?.Consultations.Any(c => c.Caller == asker && c.Followed != true) != true) return;
        Update(owner, node => node.Advisor is not { } advisor ? node
            : node with { Advisor = advisor with { Calls = advisor.Consultations.Select(c => c.Caller == asker ? c with { Followed = true } : c).ToList() } });
    }

    /// <summary>The advisor's own messages among a usage's <c>iterations</c> (<c>type: "advisor_message"</c>): their tokens, and the advisor's model as
    /// the last of them names it.</summary>
    private static (GraphTokenUsage Tokens, string? Model) AdvisorMessages(JsonElement usage)
    {
        var tokens = new GraphTokenUsage();
        string? model = null;
        var iterations = MetadataJson.Property(usage, "iterations");
        if (iterations.ValueKind != JsonValueKind.Array) return (tokens, model);
        foreach (var iteration in iterations.EnumerateArray().Take(4_096))
        {
            if (iteration.ValueKind != JsonValueKind.Object || iteration.Text("type") != "advisor_message") continue;
            if (GraphTokenUsage.Parse(iteration) is { } counted) tokens += counted;
            if (iteration.Text("model") is { } name && Wire.Model(name)) model = name;
        }
        return (tokens, model);
    }

    /// <summary>What a message itself says of the advisor. A transcript's assistant line names the advisor its message ran with
    /// (<c>advisorModel</c>) and carries the message's final usage, the advisor's own messages among its iterations; the live stream's messages
    /// have neither (its turn's result has the iterations).</summary>
    private void AdvisorNoted(JsonElement frame, JsonElement message, bool root)
    {
        if (Provider != "claude") return;
        var said = AdvisorMessages(MetadataJson.Property(message, "usage"));
        var model = said.Model ?? (frame.Text("advisorModel") is { } named && Wire.Model(named) ? named : null);
        if (model is null && said.Tokens.IsEmpty) return;
        var owner = AdvisorOwner(root);
        if (!said.Tokens.IsEmpty && Key(MetadataJson.Property(message, "id")) is { } id)
        {
            // A message comes as several lines that all carry its usage: counted once, at its latest figure.
            var key = owner + "|" + id;
            GraphTokenUsage? previous = advisorMessageUsage.TryGetValue(key, out var known) ? known : null;
            if (previous is null)
            {
                advisorMessageOrder.Add(key);
                if (advisorMessageOrder.Count > 1_024) { advisorMessageUsage.Remove(advisorMessageOrder[0]); advisorMessageOrder.RemoveAt(0); }
            }
            if (previous != said.Tokens)
            {
                advisorMessageUsage[key] = said.Tokens;
                advisorMessageSum[owner] = (advisorMessageSum.GetValueOrDefault(owner) ?? new()) - (previous ?? new()) + said.Tokens;
            }
        }
        PublishAdvisor(owner, model);
    }

    /// <summary>The advisor's model and tokens on run main block <paramref name="owner"/>: the larger of what its messages and what its turns' results
    /// said of the tokens.</summary>
    private void PublishAdvisor(string owner, string? model, Func<GraphAdvisor, GraphAdvisor>? settle = null)
    {
        var usage = new[] { advisorMessageSum.GetValueOrDefault(owner), advisorResultSum.GetValueOrDefault(owner) }.OfType<GraphTokenUsage>()
            .Aggregate((GraphTokenUsage?)null, (best, next) => best is null || next.Total > best.Total ? next : best);
        if (nodes.GetValueOrDefault(owner) is not { Kind: "main" } current) return;
        var known = current.Advisor;
        if (settle is null && !(model is not null && known?.Model != model) && !(usage is not null && !Equals(known?.Usage, usage))) return;
        Update(owner, node =>
        {
            var advisor = node.Advisor ?? new GraphAdvisor();
            if (model is not null) advisor = advisor with { Model = model };
            if (usage is not null) advisor = advisor with { Usage = usage };
            if (settle is not null) advisor = settle(advisor);
            return node with { Advisor = advisor };
        });
    }

    /// <summary>A root turn's result: what it says the advisor's own messages cost (<c>usage.iterations[]</c> of type <c>advisor_message</c>, which also
    /// names the advisor's model), and the end of what the main session still had out.</summary>
    private void AdvisorTurnEnded(JsonElement frame)
    {
        if (Provider != "claude") return;
        var owner = AdvisorOwner(true);
        var said = AdvisorMessages(MetadataJson.Property(frame, "usage"));
        if (!said.Tokens.IsEmpty) advisorResultSum[owner] = (advisorResultSum.GetValueOrDefault(owner) ?? new()) + said.Tokens;
        var outstanding = nodes.GetValueOrDefault(owner)?.Advisor?.Consultations.Any(c => c.Caller is null && c.State == "consulting") == true;
        if (said.Tokens.IsEmpty && said.Model is null && !outstanding) return;
        var time = Now;
        PublishAdvisor(owner, said.Model, !outstanding ? null : advisor => advisor with
        {
            Calls = advisor.Consultations.Select(c => c.Caller is null && c.State == "consulting" ? c with { State = "stopped", EndedAt = time } : c).ToList(),
        });
    }

    /// <summary>The process ended: a consultation still out never got its result.</summary>
    private void StopAdvisorCalls()
    {
        var time = Now;
        foreach (var id in order.ToList())
        {
            if (nodes.GetValueOrDefault(id)?.Advisor?.Consultations.Any(c => c.State == "consulting") != true) continue;
            Update(id, node => node.Advisor is not { } advisor ? node
                : node with { Advisor = advisor with { Calls = advisor.Consultations.Select(c => c.State == "consulting" ? c with { State = "stopped", EndedAt = time } : c).ToList() } });
        }
    }
}
