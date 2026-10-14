namespace MightyClaude.Core;

/// <summary>
/// Follow mode for the structure view's page (M/MightyCore/MightyGraphFollow.swift:129-237). While the reader is at the
/// newest work, the newest card's bottom stays in view: a new card or a card that grows is shown as it arrives. A reader who
/// scrolls or pans away is left exactly where they are until they come back to the newest card or send a request. The board
/// keeps this rule for each of its columns (<see cref="ColumnWatch"/>).
/// </summary>
public static class MightyStructureFollow
{
    /// <summary>How far the newest card's bottom may hide below the viewport and the reader still counts as at the newest work (M/MightyCore/TranscriptViewport.swift:8).</summary>
    public const double Slack = 36;

    /// <summary>Where the page stands, in its own top-down coordinates: the newest request, and its card's bottom when the card is laid out.</summary>
    public sealed record Page(double OriginY, double VisibleHeight, double DocumentHeight, string? NewestId, double? NewestBottom)
    {
        /// <summary>The newest card's bottom is on screen or hidden by no more than the slack; with no request, the page's end is.</summary>
        internal bool AtNewest => NewestId is null ? OriginY + VisibleHeight >= DocumentHeight - Slack
            : NewestBottom is { } bottom && bottom >= OriginY && bottom <= OriginY + VisibleHeight + Slack;
        /// <summary>The newest card's bottom is below the viewport or above it.</summary>
        internal bool NewestHidden => NewestBottom is { } bottom && (bottom > OriginY + VisibleHeight + 0.5 || bottom < OriginY);
        internal bool Moved(Page last) => Math.Abs(OriginY - last.OriginY) > 0.5;
        internal bool LaidOut(Page last) => NewestId != last.NewestId || Math.Abs(DocumentHeight - last.DocumentHeight) > 0.5 || Math.Abs(VisibleHeight - last.VisibleHeight) > 0.5
            || (NewestBottom is null) != (last.NewestBottom is null) || Math.Abs((NewestBottom ?? 0) - (last.NewestBottom ?? 0)) > 0.5;
    }

    /// <summary>Where the view scrolls: the page's end (<see cref="Bottom"/>), or the newest card's bottom when more of the page (a taller column) goes on below it.</summary>
    public readonly record struct Scroll(string? Card)
    {
        public static readonly Scroll Bottom = new(null);
    }

    /// <summary>The view's follow state between page reports.</summary>
    public sealed class PageWatch
    {
        public bool Following { get; private set; } = true;
        private Page? last;
        /// <summary>A new request being brought into view: the page may land short of it, so its own scrolls do not decide the follow state until it shows.</summary>
        private string? showing;
        private int tries;
        private const int ShowTries = 3;
        /// <summary>The page came back without following: its first report says where the reader is.</summary>
        private bool judging;
        /// <summary>The user's moves since the last report (<see cref="UserScrolled"/>).</summary>
        private double userMove;

        /// <summary>The user opened or folded a text: they are reading, nothing follows.</summary>
        public void Stop() { Following = false; showing = null; judging = false; }

        /// <summary>
        /// The user moved the page by <paramref name="dy"/> points (top-down) with a wheel, a touchpad, a key or a drag
        /// (M/MightyCore/MightyGraphFollow.swift:182-202). Judged at once, from the last report moved by all such moves since, like
        /// a report of a scroll the user is making: the page's own report of the move may arrive late, or together with a layout
        /// change, and must not keep a reader who left the newest card following. Before any report there is nothing to judge from.
        /// </summary>
        public void UserScrolled(double dy)
        {
            showing = null; judging = false;
            if (last is null) return;
            userMove += dy;
            Following = (last with { OriginY = last.OriginY + userMove }).AtNewest;
        }

        /// <summary>The view appeared again: a following reader is shown the newest card; any other reader follows from the first report if the page opened at the newest card.</summary>
        public Scroll? Appeared(string? newestId)
        {
            last = null;
            if (!Following) { judging = true; return null; }
            if (newestId is null) return Scroll.Bottom;
            showing = newestId; tries = ShowTries;
            return new(newestId);
        }

        /// <summary>A new request is the user's own: shown, and followed from now on.</summary>
        public Scroll NewRequest(string id)
        {
            Following = true; judging = false; showing = id; tries = ShowTries;
            return new(id);
        }

        /// <summary>
        /// A page report; <paramref name="userScrolling"/> is a scroll the user's wheel, finger or drag is making right now.
        /// Returns where to scroll. The page only moving is a scroll: the reader follows when they are at the newest card. The
        /// page laid out anew (a card grew, a new card, a taller composer) keeps the follow state it had before, so a growth
        /// that hides the newest card in one pass is still followed; and it scrolls only when the newest card's bottom is
        /// hidden, never up to it from below, so a reader at the true end of the page stays there.
        /// </summary>
        public Scroll? Observe(Page page, bool userScrolling)
        {
            var previous = last; last = page; userMove = 0;
            if (judging) { judging = false; Following = page.AtNewest; return null; }
            if (userScrolling) { showing = null; Following = page.AtNewest; return null; }
            if (previous is not null && page.Moved(previous) && !page.LaidOut(previous) && showing is null) { Following = page.AtNewest; return null; }
            if (!Following) return null;
            if (showing is not null && page.NewestId == showing && page.NewestBottom is not null && !page.NewestHidden) showing = null;
            if (!page.NewestHidden || page.NewestId is not { } id || page.NewestBottom is not { } bottom) return null;
            if (showing is not null && --tries <= 0) showing = null;
            return page.DocumentHeight - bottom <= Slack ? Scroll.Bottom : new(id);
        }
    }

    /// <summary>
    /// The board's follow state (M/MightyCore/MightyGraphFollow.swift:257-318): each column of the board shown scrolls on its own and
    /// follows its own end by the page rule (a <see cref="PageWatch"/> per column, whose newest card is the column's last). Only the
    /// newest board follows; another board, or the board shown becoming or ceasing to be the newest, starts over: every column of
    /// the newest board follows, no column of an older one does. A user's move or an opened text stops one column only.
    /// </summary>
    public sealed class ColumnWatch
    {
        public string? BoardId { get; private set; }
        public bool Newest { get; private set; }
        private readonly Dictionary<string, PageWatch> columns = [];

        /// <summary>The column follows its end: on the newest board, unless the reader left it.</summary>
        public bool Following(string column) => Newest && (!columns.TryGetValue(column, out var watch) || watch.Following);

        private void Show(string board, bool newest)
        {
            if (board == BoardId && newest == Newest) return;
            BoardId = board; Newest = newest; columns.Clear();
        }

        private PageWatch Watch(string column) => columns.TryGetValue(column, out var watch) ? watch : columns[column] = new();

        /// <summary>A column of <paramref name="board"/> appeared: the board was shown, or the view came back to it. A following column shows its end; any other is judged by its first report.</summary>
        public Scroll? Appeared(string board, bool newest, string column)
        {
            Show(board, newest);
            return newest ? Watch(column).Appeared(column) : null;
        }

        /// <summary>A new request on the newest board is the user's own: the column shows its end and follows it from now on.</summary>
        public Scroll NewRequest(string board, string column)
        {
            Show(board, true);
            return (columns[column] = new()).NewRequest(column);
        }

        /// <summary>The user opened or folded a text in the column: nothing follows there.</summary>
        public void Stop(string column) { if (Newest) Watch(column).Stop(); }

        /// <summary>The user moved the column by <paramref name="dy"/> points (<see cref="PageWatch.UserScrolled"/>).</summary>
        public void UserScrolled(string column, double dy) { if (Newest && columns.TryGetValue(column, out var watch)) watch.UserScrolled(dy); }

        /// <summary>A column's report (<see cref="PageWatch.Observe"/>). Returns where the column scrolls; null on an older board.</summary>
        public Scroll? Observe(string board, bool newest, string column, Page page, bool userScrolling)
        {
            Show(board, newest);
            return newest ? Watch(column).Observe(page, userScrolling) : null;
        }
    }
}
