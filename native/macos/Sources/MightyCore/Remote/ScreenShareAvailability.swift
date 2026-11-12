/// The one switch for the BETA screen share (viewing and controlling this Mac
/// from a phone over WebRTC).
///
/// Off for now: the relay is moving to Cloudflare Workers, which has no TURN
/// server, so a phone off the Mac's network could not reach the picture. While
/// it is off the app builds no engine, menu-bar item or input monitors, asks
/// macOS for no Screen Recording permission, settings shows one line saying the
/// feature is paused, `MobileRemoteService` never advertises `screenShare` and
/// the `/m1/screen-share` routes answer 503 — so the phone never offers it. All
/// the code stays; set this back to `true` to bring it back. See
/// docs/mobile-remote.md and docs/relay.md (TURN).
public enum ScreenShareAvailability {
    public static let enabled = false
}
