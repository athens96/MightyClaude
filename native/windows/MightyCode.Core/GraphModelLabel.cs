namespace MightyCode.Core;

public static class GraphModelLabel
{
    public static string? NodeModelLabel(string? cliReportedModel, string? configuredModel)
    {
        if (!string.IsNullOrEmpty(cliReportedModel)) return cliReportedModel;
        if (!string.IsNullOrEmpty(configuredModel) && configuredModel != "default")
            return configuredModel + " " + Locale.Get("graph.nodeModel.configuredSuffix");
        return null;
    }

    /// <summary>The model label of a sub-agent block before its own responses name the model that answered (<see cref="GraphAgentIdentity"/>): the model
    /// the CLI resolved when it launched the agent, else the alias the Agent call (or, failing that, the agent's definition) asked for, marked "요청" as
    /// a configured model is marked "설정": it is what was asked, not what ran. "inherit" is a definition's way of asking for none (macOS).</summary>
    public static string? AgentModelLabel(string? resolved, string? requested)
    {
        if (!string.IsNullOrEmpty(resolved)) return resolved;
        if (!string.IsNullOrEmpty(requested) && requested != "inherit") return requested + " " + Locale.Get("graph.nodeModel.requestedSuffix");
        return null;
    }
}
