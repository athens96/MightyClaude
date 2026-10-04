namespace MightyClaude.Core;

public sealed partial class ExecutionGraphTracker
{
    private readonly Dictionary<string, int> codexSessionTurns = [];
    /// <summary>Merge only descendants already scoped by CodexSessionWatcher.</summary>
    public void CodexSession(CodexSessionAgent agent)
    {
        lock (sync)
        {
            if (finished || Provider != "codex" || EnsureCodexAgent(agent.Thread) is not { } id) return;
            SetParent(id, agent.ParentThread == codexRootThread ? Owner.Node(mainID) : Owner.Agent(agent.ParentThread));
            var seen = codexSessionTurns.GetValueOrDefault(agent.Thread);
            var old = nodes[id];
            var reopening = seen > 0 && agent.Turns > seen && ExecutionGraphSupport.Terminal(old.State);
            codexSessionTurns[agent.Thread] = Math.Max(seen, agent.Turns);
            foreach (var record in agent.Usage)
                RecordUsage(record.Usage, "codex-session:" + agent.Thread + ":" + record.ResponseId, id, record.Model, record.ActivityIds);
            if (reopening && old.Output is { Length: > 0 } previous && !agent.Entries.Any(e => e.Kind == "assistant" && e.Text == previous))
                Append(Entry(ExecutionGraphSupport.Identifier(runID, id + ":answer:" + (old.ActivityGeneration ?? 0)), "assistant", previous), id);
            Update(id, value =>
            {
                var entries = value.Entries.ToList();
                foreach (var entry in agent.Entries)
                {
                    if (agent.Answers.Contains(entry.Id) && entries.Any(e => e.Kind == "assistant" && e.Text == entry.Text)) continue;
                    var index = entries.FindIndex(e => e.Id == entry.Id);
                    if (index < 0) entries.Add(entry); else entries[index] = entry with { Timestamp = entries[index].Timestamp };
                }
                return value with { Title = agent.Title ?? value.Title, Input = value.Input ?? agent.Input, Entries = entries,
                    Output = reopening || agent.Output is not null ? agent.Output : value.Output, State = agent.State };
            }, reopening);
        }
    }
}
