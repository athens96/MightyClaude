import Foundation

/// The Mac's `gemini` panes used to run Gemini CLI and now run Antigravity
/// CLI (`agy`). What they saved for Gemini CLI means nothing to agy: its
/// model names (agy refuses an unknown `--model` and the run fails) and its
/// session ids (never agy conversations). `StateRepository` applies this once
/// per profile (`AppSnapshot.antigravityMigrated`).
public enum AntigravityMigration {
    /// The pane as agy can run it, with a note in its log for each change.
    public static func migrated(_ session: RunSession) -> RunSession {
        guard session.kind == SessionKind.claude, session.provider == "gemini" else { return session }
        var value = session
        if !ProviderOptions.fallbackCatalog("gemini").models.contains(where: { $0.value == value.model }) {
            value.logs.append(LogEntry(kind: "system", text: L("run.notice.antigravityModelReset", ["model": ActivitySupport.clean(value.model, maximumBytes: 200, singleLine: true)])))
            value.model = "default"
        }
        if value.resumeId != nil {
            value.resumeId = nil
            value.logs.append(LogEntry(kind: "system", text: L("run.notice.antigravityConversationCleared")))
        }
        return value
    }

    /// A pane's model agy's live list no longer has: the run would fail, so it
    /// starts on agy's default instead. Only the CLI's own list decides; the
    /// fallback list may lag behind it.
    public static func modelUnavailable(_ model: String, catalog: ModelCatalog) -> Bool {
        model != "default" && catalog.source == "cli" && !catalog.models.contains { $0.value == model }
    }
}
