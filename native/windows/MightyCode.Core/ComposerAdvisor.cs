namespace MightyCode.Core;

/// <summary>
/// A Claude pane's 조언자 setting in the composer (macOS SessionPaneView advisorMenu): Claude 설정 따름 (nothing is passed), a model of the pane's
/// Claude catalogue (passed as <c>--advisor</c>), or 끔 (<see cref="RunSettings.AdvisorOff"/>). Only a Claude pane on this computer has it: another
/// computer's host runs its own CLI with its own settings (<c>RemoteWireRequest.From</c> drops it).
/// </summary>
public static class ComposerAdvisor
{
    /// <summary>One row of the menu: the value it sets (null follows Claude's settings), its words, whether it is the pane's choice.</summary>
    public sealed record Option(string? Value, string Label, bool Selected);

    /// <summary>Whether the pane offers the setting.</summary>
    public static bool Shows(RunSession pane, bool remote) => pane.Kind == "claude" && pane.Provider == "claude" && !remote;

    /// <summary>The models an advisor may be: the pane's Claude models but the CLI's default, and the pane's own choice even when the list no longer has it.</summary>
    public static List<ModelOption> Models(RunSession pane, ModelCatalog catalog)
    {
        var offered = catalog.Models.Where(m => m.Value != "default").ToList();
        if (pane.Settings.Advisor is { } chosen && chosen != RunSettings.AdvisorOff && !offered.Any(m => m.Value == chosen)) offered.Add(new ModelOption(chosen, chosen, ""));
        return offered;
    }

    /// <summary>A value in words: Claude 설정 따름, 끔, or the model's name.</summary>
    public static string Label(string? value, IReadOnlyList<ModelOption> models) =>
        value is null ? Locale.Get("composer.advisor.follow")
        : value == RunSettings.AdvisorOff ? Locale.Get("composer.advisor.off")
        : models.FirstOrDefault(m => m.Value == value)?.DisplayName ?? value;

    /// <summary>The menu's rows in order: follow, the models, off (the separators and the note between them are the view's).</summary>
    public static List<Option> Options(RunSession pane, ModelCatalog catalog)
    {
        var models = Models(pane, catalog);
        var current = pane.Settings.Advisor;
        var rows = new List<Option> { new(null, Locale.Get("composer.advisor.follow"), current is null) };
        rows.AddRange(models.Select(m => new Option(m.Value, m.DisplayName, m.Value == current)));
        rows.Add(new(RunSettings.AdvisorOff, Locale.Get("composer.advisor.off"), current == RunSettings.AdvisorOff));
        return rows;
    }

    /// <summary>The pill's words: a chosen model's name; a pane that follows Claude's settings, or turned the advisor off, shows its icon alone.</summary>
    public static string PillTitle(RunSession pane, ModelCatalog catalog) =>
        pane.Settings.Advisor is { } value && value != RunSettings.AdvisorOff ? Label(value, Models(pane, catalog)) : "";

    /// <summary>The pill's help: "조언자 · &lt;value&gt;" and what choosing one means.</summary>
    public static string Help(RunSession pane, ModelCatalog catalog) =>
        Locale.Get("composer.advisor.help", new Dictionary<string, string> { ["value"] = Label(pane.Settings.Advisor, Models(pane, catalog)) });

    /// <summary>The settings with the advisor set to <paramref name="value"/>, as the settings normalizer keeps it (a model's name, off, or nothing).</summary>
    public static RunSettings Apply(RunSettings settings, string? value) => settings with { Advisor = value is null || Wire.Model(value) ? value : null };
}
