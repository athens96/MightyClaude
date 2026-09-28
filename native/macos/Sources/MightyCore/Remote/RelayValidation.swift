import Foundation

/// Failures raised by the local HTTP server and the relay channel.
typealias RemoteFailure = MightyError

/// Shape rules shared by the local HTTP server, the relay channel and the
/// phone extension: what a pairing key must look like, and which run events
/// a client is allowed to publish for a pane.
enum RemoteValidation {
    static func token(_ value: String) -> Bool { value.range(of: "^[a-zA-Z0-9_-]{43,128}$", options: .regularExpression) != nil }

    static func event(_ value: RunEvent, sessionId: String) -> Bool {
        guard value.sessionId == sessionId else { return false }
        if let activity = value.activity, !ActivitySupport.valid(activity) { return false }
        switch value.type {
        // Damaged optional usage must not erase otherwise valid output.
        // The client normalizes (or ignores) this observation before publishing.
        case "usage": return true
        case "graph": return true // Optional graph observations are normalized independently.
        case "activity": return value.activity != nil
        case "status": return ["idle", "running", "completed", "error", "stopped"].contains(value.status ?? "")
        case "resume": return CoreValidation.identifier(value.resumeId ?? "")
        case "log":
            guard let entry = value.entry else { return false }
            if let activity = entry.activity, !ActivitySupport.valid(activity) { return false }
            return CoreValidation.identifier(entry.id) && ["user", "assistant", "system", "output", "error"].contains(entry.kind) && entry.text.utf8.count <= 131_072 && (entry.provider == nil || ProviderOptions.ids.contains(entry.provider!))
        default: return false
        }
    }
}
