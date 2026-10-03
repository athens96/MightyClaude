using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// Automatic agent-pane titles (macOS MightyCore/PaneTitle.swift): an agent pane's title follows its
/// latest request, shortened to one line of 40 characters, until a rename fixes it.
/// <c>RunSession.TitleMode</c> is <c>"auto"</c> (or absent) or <c>"fixed"</c>, as on macOS.
/// </summary>
public static class PaneTitle
{
    public const int MaximumTextElements = 40;
    public const string Automatic = "auto";
    public const string Fixed = "fixed";
    /// The "첨부: " prefix AttachmentSupport.Summary writes before a request's attachment list. It is
    /// stored log data, not UI text: it stays Korean in every language, so it is spelled with escapes
    /// rather than read from the locale catalogues.
    internal const string AttachmentMarker = "\uCCA8\uBD80: ";

    /// <summary>
    /// The one-line title for a request, or null for empty or whitespace-only input so the previous
    /// title stays. Whitespace runs and line breaks collapse to one space; past 40 characters (text
    /// elements, like a Swift Character count) the text is cut and "…" appended. Slash commands too.
    /// </summary>
    public static string? Shortened(string? rawInput)
    {
        var collapsed = string.Join(' ', (rawInput ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return null;
        var enumerator = StringInfo.GetTextElementEnumerator(collapsed);
        var count = 0;
        while (enumerator.MoveNext())
        {
            if (count == MaximumTextElements) return collapsed[..enumerator.ElementIndex] + "…";
            count++;
        }
        return collapsed;
    }

    /// <summary>True for an agent pane whose title follows its requests; shell, browser and files panes never do.</summary>
    public static bool FollowsRequests(RunSession session) => session.Kind == "claude" && session.TitleMode is null or Automatic;

    /// <summary>
    /// The typed text of the newest user log entry that titles the pane (macOS RunSession.titleTooltip).
    /// The log holds the typed input, then a blank line and the "첨부: " attachment line; only the
    /// typed part counts, and an attachment-only entry is skipped.
    /// </summary>
    public static string? Tooltip(RunSession session)
    {
        for (var i = session.Logs.Count - 1; i >= 0; i--)
        {
            var entry = session.Logs[i];
            if (entry.Kind != "user") continue;
            var text = entry.Text.StartsWith(AttachmentMarker, StringComparison.Ordinal) ? "" : entry.Text.Split("\n\n" + AttachmentMarker)[0];
            var trimmed = text.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }

    /// <summary>Hover text for a pane title: the full request behind an automatic agent title, otherwise the title.</summary>
    public static string Help(RunSession session) => FollowsRequests(session) ? Tooltip(session) ?? session.Title : session.Title;

    /// <summary>The automatic title: the newest titled request shortened, or <paramref name="defaultTitle"/>.</summary>
    public static string AutoTitle(RunSession session, string defaultTitle) =>
        Tooltip(session) is { } text ? Shortened(text) ?? defaultTitle : defaultTitle;

    /// <summary>A pane that just sent <paramref name="input"/>: retitled when it follows requests and the input has text.</summary>
    public static RunSession Requested(RunSession session, string? input) =>
        FollowsRequests(session) && Shortened(input) is { } title ? session with { Title = title } : session;

    /// <summary>
    /// Restore-time migration (macOS StateRepository.normalize): every automatic agent pane — including
    /// one saved before title modes existed — is retitled from its newest request. A pane that continued
    /// an earlier session keeps that session's title until it sends a request of its own.
    /// </summary>
    public static RunSession Restored(RunSession session)
    {
        if (!FollowsRequests(session)) return session;
        var fallback = session.ResumeId is not null && session.Title.Length > 0 ? session.Title : ProviderCatalog.Name(session.Provider);
        return session with { Title = AutoTitle(session, fallback) };
    }

    /// <summary>The rename dialog's 자동 choice: back to automatic, retitled now from the newest request or the provider default.</summary>
    public static AppSnapshot SetAutomatic(AppSnapshot snapshot, string sessionId)
    {
        if (!snapshot.Sessions.Any(s => s.Id == sessionId)) throw new ArgumentException(RenameStrings.ErrorNotFound);
        return snapshot with
        {
            Sessions = snapshot.Sessions.Select(s => s.Id == sessionId
                ? s with { TitleMode = Automatic, Title = AutoTitle(s, ProviderCatalog.Name(s.Provider)) }
                : s).ToList(),
        };
    }

    /// <summary>A stored mode is kept only when it is one macOS writes.</summary>
    public static string? NormalizedMode(string? mode) => mode is Automatic or Fixed ? mode : null;
}
