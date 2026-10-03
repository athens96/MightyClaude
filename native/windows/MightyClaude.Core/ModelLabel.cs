using System.Collections.Concurrent;

namespace MightyClaude.Core;

/// <summary>
/// The one display label for a model: family plus version (<c>Opus 5.5</c>,
/// <c>GPT-6.1 Sol</c>, <c>Gemini 3 Pro</c>). Only the label changes; the value stored and
/// sent to the CLI is never rewritten. A port of macOS <c>MightyCore/ModelLabel.swift</c>;
/// both are held to <c>native/contracts/fixtures/model-labels.json</c>.
/// </summary>
public static class ModelLabel
{
    internal const string OneMillionSuffix = " (1M)";
    private static readonly HashSet<string> Families = ["opus", "sonnet", "haiku", "fable"];
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// A full model id read into its label, or null when the id is not one of the shapes
    /// macOS reads (aliases, <c>default</c>, unknown names). Results are cached by id.
    /// <c>claude-opus-5-5</c> → <c>Opus 5.5</c>; Bedrock/Vertex wrappers are unwrapped;
    /// <c>gpt-5.2-codex</c> → <c>GPT-5.2 Codex</c>; <c>gemini-3-pro-preview</c> →
    /// <c>Gemini 3 Pro</c>; a trailing <c>[1m]</c> adds <c> (1M)</c>.
    /// </summary>
    public static string? Format(string id)
    {
        if (Cache.TryGetValue(id, out var hit)) return hit;
        var made = Read(id);
        if (Cache.Count >= 512) Cache.Clear();
        Cache[id] = made;
        return made;
    }

    /// <summary>
    /// The label for a model value. A full id is read with <see cref="Format"/>. A Claude
    /// family alias (<c>opus</c>, <c>sonnet[1m]</c>, …) takes its version from
    /// <paramref name="resolved"/> when that id is the same family, else it stays the bare
    /// family name — a version is never invented. Any other value keeps its own name
    /// (<paramref name="fallback"/>, else the value) and adds the model it resolves to after
    /// <c> · </c> when <paramref name="resolved"/> reads.
    /// </summary>
    public static string Text(string model, string? resolved = null, string? fallback = null)
    {
        var value = model.Trim();
        if (Format(value) is { } label) return label;
        var resolvedLabel = resolved is null ? null : Format(resolved);
        var alias = value.ToLowerInvariant();
        var oneMillion = alias.EndsWith("[1m]", StringComparison.Ordinal);
        if (oneMillion) alias = alias[..^4];
        if (Families.Contains(alias))
        {
            var family = Capitalized(alias);
            var suffix = oneMillion ? OneMillionSuffix : "";
            if (resolvedLabel is null || !resolvedLabel.StartsWith(family + " ", StringComparison.Ordinal)) return family + suffix;
            return resolvedLabel.EndsWith(OneMillionSuffix, StringComparison.Ordinal) ? resolvedLabel : resolvedLabel + suffix;
        }
        var name = string.IsNullOrEmpty(fallback) ? value : fallback;
        return resolvedLabel is null ? name : name + " · " + resolvedLabel;
    }

    /// <summary>
    /// The label a picker row and a display of that row both use. <paramref name="hint"/>
    /// (a model the CLI reported for this very selection) only reaches a bare family alias
    /// the catalogue does not resolve.
    /// </summary>
    public static string Option(ModelOption option, string? hint = null) =>
        Text(option.Value, option.ResolvedModel ?? FamilyHint(option.Value, hint), option.DisplayName);

    /// <summary>The label for a model against its catalogue (see <see cref="Option"/>).</summary>
    public static string InCatalog(string model, ModelCatalog? catalog, string? hint = null) =>
        catalog?.Models.FirstOrDefault(m => m.Value == model) is { } option ? Option(option, hint) : Text(model, FamilyHint(model, hint));

    // ── a pane's model ──────────────────────────────────────────────────────

    /// <summary>
    /// The model the CLI reported for the pane, only while the model it was reported for is
    /// still the pane's selection. Usage saved before the selection was recorded gives none.
    /// </summary>
    public static string? ReportedModel(RunSession session) =>
        session.SessionUsage is { } usage && usage.Provider == session.Provider && usage.SelectedModel == session.Model ? usage.Model : null;

    /// <summary>
    /// The pane's selection as displays show it (composer button, session info, status
    /// line): the picker row's label without the picker-only marks.
    /// </summary>
    public static string Selection(RunSession session, ModelCatalog catalog)
    {
        if (session.Model == "default" && !catalog.Models.Any(m => m.Value == "default")) return CliDefaultName;
        return InCatalog(session.Model, catalog, ReportedModel(session));
    }

    /// <summary>
    /// Picker rows (composer, <c>/model</c>) with their label in <c>DisplayName</c>. Values
    /// are untouched; the reported model labels only the selected row; a saved model the
    /// catalogue lacks is marked as such.
    /// </summary>
    public static List<ModelOption> PickerOptions(RunSession session, ModelCatalog catalog)
    {
        var hint = ReportedModel(session);
        var options = catalog.Models.Select(option => option with { DisplayName = Option(option, option.Value == session.Model ? hint : null) }).ToList();
        if (!options.Any(o => o.Value == "default")) options.Insert(0, new ModelOption("default", CliDefaultName, ""));
        if (!options.Any(o => o.Value == session.Model)) options.Add(new ModelOption(session.Model, Selection(session, catalog) + " · " + Locale.Get("composer.model.saved"), ""));
        return options;
    }

    /// <summary>
    /// <c>model.id</c> and <c>model.display_name</c> for a status line. A model reported for
    /// an earlier selection gives way to the current one.
    /// </summary>
    public static (string Id, string Name) StatusLine(RunSession session, ModelCatalog catalog)
    {
        var usage = session.SessionUsage?.Provider == session.Provider ? session.SessionUsage : null;
        var current = usage is not null && (usage.SelectedModel is null || usage.SelectedModel == session.Model) ? usage.Model : null;
        var id = current ?? catalog.Models.FirstOrDefault(m => m.Value == session.Model)?.ResolvedModel ?? session.Model;
        return (id, Format(id) ?? Selection(session, catalog));
    }

    internal static string CliDefaultName => Locale.Get("composer.model.cliDefault");

    // ── rules ───────────────────────────────────────────────────────────────

    /// A reported id may stand in for a resolution only behind a family alias, where
    /// <see cref="Text"/> also checks the family; <c>default</c>, <c>best</c> and the like
    /// may have meant another model on that run.
    private static string? FamilyHint(string model, string? hint)
    {
        var alias = model.Trim().ToLowerInvariant();
        if (alias.EndsWith("[1m]", StringComparison.Ordinal)) alias = alias[..^4];
        return Families.Contains(alias) ? hint : null;
    }

    private static string? Read(string id)
    {
        var core = id.Trim().ToLowerInvariant();
        var oneMillion = core.EndsWith("[1m]", StringComparison.Ordinal);
        if (oneMillion) core = core[..^4];
        if (core.LastIndexOf('/') is var slash and >= 0) core = core[(slash + 1)..];
        if (core.LastIndexOf("anthropic.", StringComparison.Ordinal) is var wrapper and >= 0) core = core[(wrapper + "anthropic.".Length)..];
        if (core.IndexOf('@') is var at and >= 0) core = core[..at];
        // Bedrock's revision suffix: `-v<n>:<n>` only.
        if (core.LastIndexOf("-v", StringComparison.Ordinal) is var revision and >= 0)
        {
            var parts = core[(revision + 2)..].Split(':');
            if (parts.Length == 2 && parts.All(Digits)) core = core[..revision];
        }
        var tokens = core.Split('-');
        if (tokens.Length < 2 || tokens.Any(t => t.Length == 0)) return null;
        var rest = tokens[1..];
        var label = tokens[0] switch { "claude" => Claude(rest), "gpt" => Gpt(rest), "gemini" => Gemini(rest), _ => null };
        return label is not null && oneMillion ? label + OneMillionSuffix : label;
    }

    private static string Capitalized(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
    private static bool Digits(string token) => token.Length > 0 && token.All(char.IsAsciiDigit);
    private static bool StartsWithDigit(string token) => token.Length > 0 && char.IsAsciiDigit(token[0]);
    /// `v2` stays as written; other words get a capital.
    private static bool Revision(string token) => token.Length >= 2 && token[0] == 'v' && Digits(token[1..]);
    private static string Word(string token) => Revision(token) ? token : Capitalized(token);

    /// One word (the family) and numbers: up to two digits are version parts, eight digits
    /// are a release date and dropped; a last `v<n>` is a suffix. Anything else is unknown.
    private static string? Claude(string[] tokens)
    {
        string? family = null; var version = new List<string>(); var suffix = "";
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (Digits(token))
            {
                if (token.Length <= 2) version.Add(token);
                else if (token.Length != 8) return null;
            }
            else if (index == tokens.Length - 1 && family is not null && Revision(token)) suffix = " " + token;
            else if (family is null && token.All(char.IsAsciiLetter)) family = token;
            else return null;
        }
        if (family is null || version.Count == 0) return null;
        return Capitalized(family) + " " + string.Join(".", version) + suffix;
    }

    private static string? Gpt(string[] tokens)
    {
        if (tokens.Length == 0 || !StartsWithDigit(tokens[0])) return null;
        var rest = tokens[1..].ToList();
        if (rest.Count >= 3 && Digits(rest[^3]) && rest[^3].Length == 4 && Digits(rest[^2]) && rest[^2].Length == 2 && Digits(rest[^1]) && rest[^1].Length == 2)
            rest.RemoveRange(rest.Count - 3, 3);
        return string.Join(" ", new[] { "GPT-" + tokens[0] }.Concat(rest.Select(Word)));
    }

    private static string? Gemini(string[] tokens)
    {
        if (tokens.Length == 0 || !StartsWithDigit(tokens[0])) return null;
        var words = new List<string>();
        foreach (var token in tokens[1..])
        {
            if (token is "preview" or "exp" or "latest") break;
            if (!Digits(token)) words.Add(Word(token));
        }
        return string.Join(" ", new[] { "Gemini", tokens[0] }.Concat(words));
    }
}
