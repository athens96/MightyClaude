namespace MightyClaude.Core;

public static partial class MightyStructure
{
    /// <summary>
    /// Finished requests fold in the phone's structure view, so a long pane reads as a list (M/MightyCore/MightyStructureFold.swift):
    /// a request that completed, failed or stopped shows as one compact card until the user opens it. A running or waiting
    /// request is always open, and one that finishes folds by itself unless the user opened it by hand. The open cards are the
    /// only state: a card is folded exactly while it is finished and the user has not opened it. The desktop view shows one
    /// board at a time (<see cref="Boards"/>), so it folds no whole request; a styled board's request card still says how it
    /// ended with <see cref="ResultLine"/>.
    /// </summary>
    public static class Fold
    {
        /// <summary>The card shows folded: finished, and not opened by hand.</summary>
        public static bool IsFolded(Item item, IReadOnlySet<string> unfolded) => item.Finished && !unfolded.Contains(item.RunId);

        /// <summary>The cards of <paramref name="items"/> that show folded.</summary>
        public static HashSet<string> Folded(IEnumerable<Item> items, IReadOnlySet<string> unfolded) =>
            items.Where(item => IsFolded(item, unfolded)).Select(item => item.RunId).ToHashSet();

        /// <summary>The hand-opened cards after the user opens or folds <paramref name="item"/>'s card. Only a finished card has the control; an unfinished one is left as it is.</summary>
        public static HashSet<string> Toggled(Item item, IReadOnlySet<string> unfolded)
        {
            var next = unfolded.ToHashSet();
            if (item.Finished && !next.Remove(item.RunId)) next.Add(item.RunId);
            return next;
        }

        /// <summary>
        /// The folded card's result line: the answer's first line, without its Markdown marks. Null for a request that failed or
        /// stopped, or completed without an answer: the card says how it ended instead.
        /// </summary>
        public static string? ResultLine(Item item) =>
            item.Status == "completed" && (item.Answer?.Text ?? item.Result) is { } text ? FirstLine(text) : null;

        /// <summary>The first line of <paramref name="text"/> with words in it, without the Markdown that opens a line (a heading's #, a quote's >, a list's mark) or wraps a word (`, **, __).</summary>
        public static string? FirstLine(string text)
        {
            foreach (var raw in text.Split(Newlines, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.AsSpan().TrimStart(" \t");
                if (line.StartsWith("```", StringComparison.Ordinal)) continue;
                while (!line.IsEmpty && line[0] is '#' or '>') line = line[1..].TrimStart(' ');
                if (line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ') line = line[2..];
                var dot = line.IndexOf('.');
                if (dot > 0 && dot + 1 < line.Length && line[dot + 1] == ' ' && Digits(line[..dot])) line = line[(dot + 2)..];
                var cleaned = line.ToString().Replace("**", "").Replace("__", "").Replace("`", "").Trim();
                if (cleaned.Length > 0) return cleaned;
            }
            return null;
        }

        private static bool Digits(ReadOnlySpan<char> text)
        {
            foreach (var character in text) if (!char.IsNumber(character)) return false;
            return true;
        }
    }
}
