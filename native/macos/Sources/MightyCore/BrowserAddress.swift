import Foundation

public struct BrowserAddress: Sendable {
    // Normalizes typed text into a URL for navigation.
    // Returns nil for empty/whitespace-only input.
    // Adds https:// when no scheme is present.
    public static func resolve(_ text: String) -> URL? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if trimmed.contains("://") {
            return URL(string: trimmed)
        }
        return URL(string: "https://" + trimmed)
    }
}
