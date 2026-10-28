namespace MightyClaude.Core;

/// <summary>One composer "send to" target: another CLI asked through oh-my-claudecode's ask skill (native/contracts/fixtures/ask-targets.json).</summary>
public sealed record AskTarget(string Id, string Label, string Executable);

/// <summary>
/// The composer's "send to" rules (M/MightyCore/AskTargets.swift), read from the same fixture as the Mac and the phone: which targets
/// a pane offers, which one a send uses, the text it sends, and when a saved choice counts as gone.
/// </summary>
public static class AskTargets
{
    public const string Skill = "oh-my-claudecode:ask";

    /// <summary>Every target, in the menu's order. Neither claude (the default) nor muse is one.</summary>
    public static readonly IReadOnlyList<AskTarget> All =
    [
        new("codex", "Codex", "codex"),
        new("antigravity", "Antigravity", "agy"),
        new("gemini", "Gemini CLI", "gemini"),
        new("grok", "Grok", "grok"),
        new("cursor", "Cursor", "cursor-agent"),
    ];

    /// <summary>The targets a pane offers: none unless it is a Claude agent pane whose slash list has the skill; then every installed one.</summary>
    public static IReadOnlyList<AskTarget> Available(bool isClaudeAgent, IReadOnlySet<string> slash, IReadOnlySet<string> installed)
        => !isClaudeAgent || !slash.Contains(Skill) ? [] : All.Where(target => installed.Contains(target.Executable)).ToArray();

    /// <summary>The target a send uses: null (Claude) when nothing is saved or the saved id is not in <paramref name="available"/>.</summary>
    public static AskTarget? Effective(string? saved, IReadOnlyList<AskTarget> available)
        => saved is null ? null : available.FirstOrDefault(target => target.Id == saved);

    /// <summary>
    /// Whether a saved target is gone and the pane falls back to Claude (with its one notice). Only judged once the PATH was read
    /// conclusively (<paramref name="probed"/>) and the pane's slash catalogue scanned, so nothing unknown yet counts as gone.
    /// </summary>
    public static bool FellBack(string? saved, IReadOnlyList<AskTarget> available, bool probed, bool scanned)
        => saved is not null && probed && scanned && Effective(saved, available) is null;

    /// <summary>
    /// The installed set after a PATH read, and whether the read is conclusive enough to judge a target gone. A PATH that is not the
    /// login shell's only adds what it found; a read finding nothing after one that found some is a hiccup, not every CLI uninstalled at once.
    /// </summary>
    public static (IReadOnlySet<string> Installed, bool Conclusive) Probed(IReadOnlySet<string> previous, IReadOnlySet<string> found, bool loginShell)
    {
        if (!loginShell) return (previous.Union(found).ToHashSet(StringComparer.Ordinal), false);
        if (found.Count == 0 && previous.Count > 0) return (previous, false);
        return (found, true);
    }

    /// <summary>The question inside a wrapped send (<c>/oh-my-claudecode:ask codex &lt;text&gt;</c> gives <c>&lt;text&gt;</c>), so a pane's automatic title names the question; any other text unchanged.</summary>
    public static string Question(string text)
    {
        var trimmed = text.Trim();
        foreach (var target in All)
        {
            var prefix = "/" + Skill + " " + target.Id + " ";
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return trimmed[prefix.Length..];
        }
        return text;
    }

    /// <summary>What is sent: the text unchanged for Claude or a slash command, the ask command around the trimmed text otherwise; null when nothing should be sent.</summary>
    public static string? Wrap(string text, AskTarget? target)
    {
        if (target is null) return text;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith('/')) return text;
        return "/" + Skill + " " + target.Id + " " + trimmed;
    }
}
