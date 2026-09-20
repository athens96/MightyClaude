namespace MightyClaude.Core;

// What the GUI smoke run records under the key "completionNotification" after
// its one real notifier call: "sent", or "skipped" with the reason when the
// notification API is unsupported or registration is unavailable on the runner.
// Skipped is not a failure. Whether the toast was actually visible is never
// claimed here - that stays an on-device checklist item.
public sealed record CompletionNotificationSmokeOutcome
{
    public const string ResultKey = "completionNotification";
    public const string SentStatus = "sent";
    public const string SkippedStatus = "skipped";
    // The smoke call carries a fixture title and a fixture session id only -
    // never a prompt, tool argument, output or project path.
    public const string FixtureTitle = "Smoke fixture";
    public const string FixtureSessionId = "smoke-fixture-session";
    public const string UnsupportedReason = "IsSupported false";
    public const string UnknownReason = "unknown reason";

    public string Status { get; init; } = SentStatus;
    public string? Reason { get; init; }

    public static CompletionNotificationSmokeOutcome Sent() => new();

    public static CompletionNotificationSmokeOutcome Skipped(string? reason) => new()
    {
        Status = SkippedStatus,
        Reason = Wire.Clean(reason, 200).Trim() is { Length: > 0 } text ? text : UnknownReason
    };
}

public static class CompletionNotificationSmoke
{
    // Drives one real notifier call through the supplied callbacks (WinUI passes
    // its Windows App SDK notifier). Support is checked before anything else and
    // an unavailable notifier is only ever recorded as skipped, never as a
    // failure - a notification problem must not fail the smoke run. A throw
    // after support was confirmed is propagated so CI turns red.
    public static async Task<CompletionNotificationSmokeOutcome> RunAsync(
        bool isSupported,
        Func<Task<string?>> registerAsync,
        Func<string, string, Task> sendAsync)
    {
        if (!isSupported) return CompletionNotificationSmokeOutcome.Skipped(CompletionNotificationSmokeOutcome.UnsupportedReason);
        var reason = await registerAsync();
        if (reason is not null) return CompletionNotificationSmokeOutcome.Skipped(reason);
        await sendAsync(CompletionNotificationSmokeOutcome.FixtureTitle, CompletionNotificationSmokeOutcome.FixtureSessionId);
        return CompletionNotificationSmokeOutcome.Sent();
    }
}
