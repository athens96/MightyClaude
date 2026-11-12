namespace MightyCode.Core;

/// <summary>
/// The one switch for the BETA screen share (viewing and controlling this PC from a phone over WebRTC), as the Mac has it
/// (M/MightyCore/Remote/ScreenShareAvailability.swift).
///
/// Off for now: the relay moved to Cloudflare Workers, which has no TURN server, so a phone off this PC's network could not reach
/// the picture. While it is off the app builds no capture platform, hub or stop banner, settings shows one line saying the feature
/// is paused, <see cref="MobileDesktopRouter"/> and <see cref="MobileRelayHost"/> never advertise <c>screenShare</c> (in /m1/info and
/// auth_ok), the <c>/m1/screen-share</c> routes answer 503 and screen signalling frames reach nothing — so the phone never offers it.
/// All the code stays; set this back to <c>true</c> to bring it back. See docs/mobile-remote.md and docs/relay.md (TURN).
/// </summary>
public static class ScreenShareAvailability
{
    public static bool Enabled { get; } = false;
    public const string Capability = "screenShare";
    /// <summary>The extra capabilities a host advertises: <c>screenShare</c> is left out while screen share is off on it.</summary>
    internal static string[] Extra(Func<string[]>? extra, bool available) => extra?.Invoke() is { } values ? available ? values : values.Where(v => v != Capability).ToArray() : [];
}
