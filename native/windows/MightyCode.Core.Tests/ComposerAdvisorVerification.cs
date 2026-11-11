using MightyCode.Core;

/// <summary>
/// A Claude pane's 조언자 setting in the composer (Core ComposerAdvisor, macOS SessionPaneView advisorMenu): Claude 설정 따름, the pane's Claude models
/// but the CLI's default (and the pane's own choice even when the list no longer has it), 끔; the pill names a chosen model only; only a Claude pane
/// on this computer has it; a choice is kept as the settings normalizer keeps it.
/// </summary>
internal static class ComposerAdvisorVerification
{
    private static void Same<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{why}: expected <{expected}> but was <{actual}>"); }
    private static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why)
    {
        var (was, wanted) = (actual.ToList(), expected.ToList());
        if (!was.SequenceEqual(wanted)) throw new InvalidOperationException($"{why}: expected [{string.Join(" | ", wanted)}] but was [{string.Join(" | ", was)}]");
    }

    internal static Task TheMenuOffersFollowTheModelsAndOff()
    {
        FlowTrackerVerification.InLanguage("ko", Check);
        return Task.CompletedTask;
    }

    private static void Check()
    {
        var catalog = new ModelCatalog("cli", [new("default", "Claude 기본값", ""), new("opus", "Opus 5.5", ""), new("sonnet", "Sonnet 5.5", "")], "");
        var pane = new RunSession { Kind = "claude", Provider = "claude" };
        SameList<(string?, string, bool)>(ComposerAdvisor.Options(pane, catalog).Select(o => (o.Value, o.Label, o.Selected)),
            [(null, "Claude 설정 따름", true), ("opus", "Opus 5.5", false), ("sonnet", "Sonnet 5.5", false), (RunSettings.AdvisorOff, "끔", false)], "follow, the models but the default, off");
        Same(ComposerAdvisor.PillTitle(pane, catalog), "", "following Claude's settings: the bulb alone");
        Same(ComposerAdvisor.Help(pane, catalog).Split('\n')[0], "조언자 · Claude 설정 따름", "its help");

        var chosen = pane with { Settings = pane.Settings with { Advisor = "opus" } };
        Same(ComposerAdvisor.PillTitle(chosen, catalog), "Opus 5.5", "a chosen model's name");
        Same(ComposerAdvisor.Options(chosen, catalog).Single(o => o.Selected).Value, "opus", "the choice is checked");
        var off = pane with { Settings = pane.Settings with { Advisor = RunSettings.AdvisorOff } };
        Same(ComposerAdvisor.PillTitle(off, catalog), "", "off: the bulb alone");
        Same(ComposerAdvisor.Label(RunSettings.AdvisorOff, ComposerAdvisor.Models(off, catalog)), "끔", "off in words");
        // A choice the list no longer has is still offered, by its own name.
        var gone = pane with { Settings = pane.Settings with { Advisor = "claude-opus-5-5[1m]" } };
        Same(ComposerAdvisor.Options(gone, catalog).Single(o => o.Selected).Label, "claude-opus-5-5[1m]", "a choice no longer listed");

        // Only a Claude pane on this computer has it.
        Same(ComposerAdvisor.Shows(pane, false), true, "a local Claude pane");
        Same(ComposerAdvisor.Shows(pane, true), false, "another computer's pane");
        Same(ComposerAdvisor.Shows(pane with { Provider = "codex" }, false), false, "another agent");
        Same(ComposerAdvisor.Shows(pane with { Kind = "shell" }, false), false, "a shell");

        // Kept as the normalizer keeps it.
        Same(ComposerAdvisor.Apply(new RunSettings(), "opus").Advisor, "opus", "a model");
        Same(ComposerAdvisor.Apply(new RunSettings(Advisor: "opus"), null).Advisor, null, "follow");
        Same(ComposerAdvisor.Apply(new RunSettings(), "two words").Advisor, null, "a value that is no model");
        Same(ProviderCatalog.NormalizeSettings("claude", ComposerAdvisor.Apply(new RunSettings(), RunSettings.AdvisorOff)).Advisor, RunSettings.AdvisorOff, "off survives the normalizer");
    }
}
