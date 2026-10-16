namespace MightyClaude.Core;

/// <summary>
/// The <c>gemini</c> panes used to run Gemini CLI and now run Antigravity CLI (<c>agy</c>). What they saved for Gemini CLI means
/// nothing to agy: its model names (agy refuses an unknown <c>--model</c> and the run fails) and its session ids (never agy
/// conversations). <see cref="StateStore"/> applies this once per profile (<see cref="AppSnapshot.AntigravityMigrated"/>),
/// as M/MightyCore/AntigravityMigration.swift does on the Mac.
/// </summary>
public static class AntigravityMigration
{
    /// <summary>The pane as agy can run it, with a note in its log for each change.</summary>
    public static RunSession Migrated(RunSession session)
    {
        if (session.Kind != "claude" || session.Provider != "gemini") return session;
        var value = session; var logs = (session.Logs ?? []).ToList();
        if (!ProviderCatalog.Fallback("gemini").Models.Any(model => model.Value == value.Model))
        {
            logs.Add(new(Wire.Id(), "system", Locale.Get("run.notice.antigravityModelReset", new Dictionary<string, string> { ["model"] = ActivitySupport.Clean(value.Model, 200, true) }), Wire.Now()));
            value = value with { Model = "default" };
        }
        if (value.ResumeId is not null)
        {
            logs.Add(new(Wire.Id(), "system", Locale.Get("run.notice.antigravityConversationCleared"), Wire.Now()));
            value = value with { ResumeId = null };
        }
        return value with { Logs = logs };
    }

    /// <summary>
    /// A pane's model agy's live list no longer has: the run would fail, so it starts on agy's default instead. Only the CLI's
    /// own list decides; the fallback list may lag behind it.
    /// </summary>
    public static bool ModelUnavailable(string model, ModelCatalog catalog) =>
        model != "default" && catalog.Source == "cli" && !catalog.Models.Any(option => option.Value == model);
}
