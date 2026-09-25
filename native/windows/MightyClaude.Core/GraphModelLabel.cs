namespace MightyClaude.Core;

public static class GraphModelLabel
{
    public static string? NodeModelLabel(string? cliReportedModel, string? configuredModel)
    {
        if (!string.IsNullOrEmpty(cliReportedModel)) return cliReportedModel;
        if (!string.IsNullOrEmpty(configuredModel) && configuredModel != "default")
            return configuredModel + " " + Locale.Get("graph.nodeModel.configuredSuffix");
        return null;
    }
}
