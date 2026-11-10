using System.Text.Json.Serialization;

namespace MightyCode.Core;

/// <summary>
/// Where the desktop app's update stands, as the phone shows it (docs/phone-desktop-update.md; M: MobileAppUpdate).
/// <c>Phase</c> is idle, checking, upToDate, available, downloading, ready, installing or failed; <c>Running</c> the requests an install stops.
/// </summary>
public sealed record MobileAppUpdate(string Phase, string Current, string? Available = null, string? Notes = null, double? Progress = null, string? Error = null, int Running = 0)
{
    public const int MaximumNotes = 2_000;
    public string? Notes { get; init; } = Notes is { } text ? (text.Length > MaximumNotes ? text[..MaximumNotes] : text) : null;
}

/// <summary><c>POST /m1/app-update</c>: <c>Action</c> is check or install; <c>Confirm</c> says the user agreed that running requests stop.</summary>
public sealed record MobileAppUpdateRequest(string Action, bool? Confirm = null);

/// <summary>What the phone's install does next as the update moves.</summary>
public enum PhoneAppUpdateStep
{
    /// <summary>A step is under way: wait for the next change of the update.</summary>
    Wait,
    /// <summary>The request is over: nothing newer, or the update failed.</summary>
    Done,
    /// <summary>Look for an update first.</summary>
    Check,
    /// <summary>A newer version was found: download it.</summary>
    Download,
    /// <summary>The update is ready: install it and restart the app (the request is over).</summary>
    Install,
}

public static class PhoneAppUpdate
{
    /// <summary>The state for the phone; null when this build cannot update itself.</summary>
    public static MobileAppUpdate? Map(AppUpdateState? state, string current, int running)
    {
        if (state is null) return null;
        var offered = state.Availability is { IsNewer: true } availability ? availability.Manifest : null;
        var (phase, progress) = state.Phase switch
        {
            AppUpdatePhase.Checking => ("checking", (double?)null),
            AppUpdatePhase.UpToDate => ("upToDate", null),
            AppUpdatePhase.Available => ("available", null),
            AppUpdatePhase.Downloading => ("downloading", Math.Round(Math.Clamp(state.DownloadFraction, 0, 1), 2)),
            AppUpdatePhase.Staging => ("downloading", 1),
            AppUpdatePhase.Ready => ("ready", null),
            AppUpdatePhase.Installing => ("installing", null),
            AppUpdatePhase.Failed => ("failed", null),
            _ => ("idle", null),
        };
        return new(phase, current, offered?.Version, offered?.Notes, progress, state.Phase == AppUpdatePhase.Failed ? state.ErrorMessage : null, running);
    }

    /// <summary>The first step of the phone's install: what the app knows may be old, so it looks again unless an update is already under way.</summary>
    public static PhoneAppUpdateStep Begin(AppUpdateState state) =>
        state.Phase is AppUpdatePhase.Idle or AppUpdatePhase.UpToDate or AppUpdatePhase.Failed ? PhoneAppUpdateStep.Check : Next(state);

    /// <summary>The next step, asked at each change of the update while the phone's install is pending.</summary>
    public static PhoneAppUpdateStep Next(AppUpdateState state) => state.Phase switch
    {
        AppUpdatePhase.Available => state.Availability?.IsNewer == true ? PhoneAppUpdateStep.Download : PhoneAppUpdateStep.Done,
        AppUpdatePhase.Ready => PhoneAppUpdateStep.Install,
        AppUpdatePhase.Idle or AppUpdatePhase.UpToDate or AppUpdatePhase.Failed => PhoneAppUpdateStep.Done,
        _ => PhoneAppUpdateStep.Wait,
    };
}
