using MightyCode.Core;

/// <summary>
/// Each CLI's model-list read (M/MightyCore/Tests ModelListProbeTests): the environment the reads run in.
/// </summary>
internal static class ModelListProbeVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>With DISABLE_TELEMETRY Claude Code answers with its built-in list (Fable 5.2 read as Fable 5.1 [1m]).</summary>
    internal static Task TheProbeLetsTheCliFetchTheAccountsCurrentModels()
    {
        // Only a variable the user's own environment already sets may reach the CLIs; the app adds none.
        var inherited = CliEnvironment.Current().ContainsKey("DISABLE_TELEMETRY");
        var claude = ProviderCatalog.ClaudeHeadlessEnvironment();
        Check(inherited || !claude.ContainsKey("DISABLE_TELEMETRY"), "the Claude model list probe sets no DISABLE_TELEMETRY");
        Check(inherited || !ProviderCatalog.QuietEnvironment().ContainsKey("DISABLE_TELEMETRY"), "no other read or run sets it either");
        Check(claude.GetValueOrDefault("DISABLE_AUTOUPDATER") == "1" && claude.GetValueOrDefault("CLAUDE_CODE_SAFE_MODE") == "1" && claude.GetValueOrDefault("DISABLE_ERROR_REPORTING") == "1",
            "the probe stays quiet otherwise");
        return Task.CompletedTask;
    }
}
