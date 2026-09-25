namespace MightyClaude.Core;

public sealed record GraphChildBlock(GraphTokenUsage? Usage, IReadOnlyList<GraphResponseRecord> Records);

public static class GraphChildBlocks
{
    public static Dictionary<string, GraphChildBlock> Map(
        IReadOnlyList<GraphResponseRecord>? responseRecords,
        IReadOnlyList<MightyGraphAgent> agents,
        string runId)
    {
        if (responseRecords is null) return [];
        var result = new Dictionary<string, GraphChildBlock>();
        foreach (var actId in responseRecords.SelectMany(r => r.ActivityIds))
        {
            var nodeId = ExecutionGraphSupport.AgentNodeID(runId, actId);
            var agent = agents.FirstOrDefault(a => a.Id == nodeId);
            if (agent is null) continue;
            result[actId] = new GraphChildBlock(agent.Usage, agent.ResponseRecords ?? []);
        }
        return result;
    }
}
