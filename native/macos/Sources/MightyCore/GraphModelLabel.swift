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
}
