import Foundation

/// How much of a pane's transcript survives. The app trims on every append and
/// the repository trims again while normalizing a snapshot, so the number has
/// to be one number in one place: a repository that kept fewer would silently
/// shorten history across a restart, and one that kept more would hand the
/// phone a page cursor the running app can no longer resolve.
public enum TranscriptRetention {
    public static let maximumEntries = 400
    public static func trimmed(_ entries: [LogEntry]) -> [LogEntry] { Array(entries.suffix(maximumEntries)) }
}
