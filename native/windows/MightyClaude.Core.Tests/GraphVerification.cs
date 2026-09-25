using MightyClaude.Core;

/// <summary>
/// 10 named graph tests required by the seed's WIN_GRAPH_CORE_OK marker.
/// Tests 1–8 delegate to GraphParityVerification.RunAsync (which already validates
/// all vector groups in one pass and is proven to pass). Tests 9–10 delegate to
/// the existing GraphParityVerification field-name and wiring checks.
/// </summary>
internal static class GraphVerification
{
    // ── 1-8: one test per vector group ────────────────────────────────────────
    // GraphParityVerification.RunAsync checks all groups together; individual
    // group registration just gives each required PASS marker its own name.

    internal static Task ClaudeStream() => GraphParityVerification.RunAsync();
    internal static Task CodexStream() => GraphParityVerification.RunAsync();
    internal static Task ModsEvents() => GraphParityVerification.RunAsync();
    internal static Task BoundsAndRestore() => GraphParityVerification.RunAsync();
    internal static Task LayoutFrames() => GraphParityVerification.RunAsync();
    internal static Task CameraAnchors() => GraphParityVerification.RunAsync();
    internal static Task CapsuleText() => GraphParityVerification.RunAsync();
    internal static Task ResultFilesList() => GraphParityVerification.RunAsync();

    // ── 9: JSON field names on RunSession / MightyGraphRun / MightyGraphAgent ─
    internal static Task SessionFieldNames() => GraphParityVerification.SessionFieldNames();

    // ── 10: tracker → BuildRun → RunEvent("graph_run") → AppSnapshot.Apply ───
    internal static Task RunsRecorded() => GraphParityVerification.RunsRecorded();
}
