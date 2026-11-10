import Foundation

public enum GraphModelLabel {
    /// The model label for a graph request node or phone block.
    ///
    /// Priority:
    /// 1. If `cliReportedModel` is non-empty, it is the actual model the CLI used — return it.
    /// 2. If `configuredModel` is not "default", it was set before running — return it with
    ///    the "설정" marker so the user knows this is the configured name, not confirmed by CLI.
    /// 3. Otherwise return nil (no label; CLI decided and we do not know which model it chose).
    public static func nodeModelLabel(cliReportedModel: String?, configuredModel: String) -> String? {
        if let reported = cliReportedModel, !reported.isEmpty { return reported }
        if configuredModel != "default" { return configuredModel + " " + L("graph.nodeModel.configuredSuffix") }
        return nil
    }

    /// The model label of a sub-agent block before its own responses name the model that answered (`GraphAgentIdentity`):
    /// the model the CLI resolved when it launched the agent, else the alias the Agent call (or, failing that, the agent's
    /// definition) asked for, marked "요청" as a configured model is marked "설정": it is what was asked, not what ran.
    /// "inherit" is a definition's way of asking for none.
    public static func agentModelLabel(resolved: String?, requested: String?) -> String? {
        if let resolved, !resolved.isEmpty { return resolved }
        if let requested, !requested.isEmpty, requested != "inherit" { return requested + " " + L("graph.nodeModel.requestedSuffix") }
        return nil
    }
}
