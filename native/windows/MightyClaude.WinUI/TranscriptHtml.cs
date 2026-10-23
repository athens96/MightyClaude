using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// Prototype (WebTranscript): one log entry as HTML from the same Markdown parse the RTF is drawn from, so the two
/// views can be timed against each other on the same words. Every word is escaped; no colour is written here (the page
/// takes the palette as CSS variables), so a theme change sends new variables instead of new entries.
/// </summary>
internal static partial class TranscriptRtf
{
    /// <summary>The fonts the page draws in: the text setting's conversation and code faces, or the stand-ins, and their sizes.</summary>
    internal static (string Body, string Mono, double Size, double CodeSize) WebFaces() { var faces = Faces(); return (faces.Body, faces.Mono, AppText.Size(DesignMetrics.Type.Body), AppText.Size(12)); }

    /// <summary>One entry's HTML; a source that fails to parse or runs out of time is shown as its escaped lines.</summary>
    internal static string EntryHtml(RunSession session, LogEntry entry)
    {
        var html = new StringBuilder();
        try { WebEntry(html, session, entry); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { html.Clear().Append("<p class=\"plain\">"); Html(html, entry.Text); html.Append("</p>"); }
        return html.ToString();
    }

    private static void WebEntry(StringBuilder html, RunSession session, LogEntry entry)
    {
        if (entry.Activity is { } activity)
        {
            var live = session.Status == "running" && activity.State is "running" or "waiting";
            var name = activity.ToolName?.Trim() ?? "";
            var detail = activity.Summary.Length == 0 ? name.Length == 0 ? Locale.Get("transcript.tool.fallback") : "" : activity.Summary;
            html.Append("<div class=\"tool ").Append(live ? "live-" : "").Append(Attribute(activity.State)).Append("\"><span class=\"square\"></span>");
            if (name.Length > 0) { html.Append("<b>"); Html(html, name); html.Append("</b>"); }
            if (detail.Length > 0) { html.Append("<code>"); Html(html, detail); html.Append("</code>"); }
            if (ActivitySupport.DurationLabel(activity) is { } duration) { html.Append("<span class=\"meta\">· "); Html(html, duration); html.Append("</span>"); }
            if (live || activity.State == "error") { html.Append("<span class=\"state\">· "); Html(html, ToolState(activity.State)); html.Append("</span>"); }
            html.Append("</div>");
            if (!string.IsNullOrWhiteSpace(activity.Output)) { html.Append("<details><summary>"); Html(html, Locale.Get("transcript.tool.showDetail")); html.Append("</summary><pre>"); Html(html, activity.Output); html.Append("</pre></details>"); }
        }
        else if (entry.Kind == "user")
        {
            html.Append("<div class=\"user\"><div class=\"bubble\">"); Html(html, entry.Text);
            if (Time(entry.Timestamp) is { Length: > 0 } time) { html.Append("<div class=\"time\">"); Html(html, time); html.Append("</div>"); }
            html.Append("</div></div>");
        }
        else if (entry.Kind == "assistant" && session.Kind != "shell")
        {
            var time = Time(entry.Timestamp);
            html.Append("<div class=\"reply\"><div class=\"who\"><span class=\"mark\">•</span> "); Html(html, ProviderMark.Label(entry.Provider ?? session.Provider) + (time.Length == 0 ? "" : "  ·  " + time)); html.Append("</div>");
            WebMarkdown(html, entry.Text);
            html.Append("</div>");
        }
        else if (entry.Kind is "output" or "assistant") { html.Append("<div class=\"code\"><pre>"); Html(html, entry.Text.TrimEnd('\n', '\r')); html.Append("</pre></div>"); }
        else if (entry.Kind == "image") { html.Append("<p class=\"note\">"); Html(html, entry.Text); html.Append("</p>"); }
        else { html.Append(entry.Kind == "error" ? "<p class=\"status error\">" : "<p class=\"status\">"); Text(html, entry.Text); html.Append("</p>"); }
    }

    /// <summary>A reply's Markdown under the RTF's limits: past <see cref="MaximumRenderBytes"/> its plain lines, else parsed within <see cref="MarkdownBudget"/>.</summary>
    private static void WebMarkdown(StringBuilder html, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumRenderBytes) { html.Append("<p class=\"plain\">"); Html(html, text); html.Append("</p>"); return; }
        var w = new Writer(new TranscriptLook(), new PictureContext(null, null), DesignTokens.Palette(false), preview: false) { Cells = MaximumTableCells, Deadline = compiled ? System.Diagnostics.Stopwatch.GetTimestamp() + MarkdownBudget : long.MaxValue };
        WebBlocks(html, w, Parse(w, [.. Lines(text)], 0));
        compiled = true;
    }

    private static void WebBlocks(StringBuilder html, Writer w, List<Md> blocks)
    {
        foreach (var block in blocks)
        {
            w.Spend();
            switch (block)
            {
                case MdParagraph paragraph: html.Append("<p>"); WebSpans(html, w, paragraph.Text, 0); html.Append("</p>"); break;
                case MdSource source: html.Append("<p class=\"plain\">"); Html(html, string.Join('\n', source.Lines)); html.Append("</p>"); break;
                case MdHeading heading:
                    var level = Math.Clamp(heading.Level, 1, 6).ToString(CultureInfo.InvariantCulture);
                    html.Append("<h").Append(level).Append('>'); WebSpans(html, w, heading.Text, 0); html.Append("</h").Append(level).Append('>');
                    break;
                case MdList list:
                    html.Append(list.Ordered ? "<ol start=\"" + (list.Items.Count > 0 ? list.Items[0].Ordinal : 1).ToString(CultureInfo.InvariantCulture) + "\">" : "<ul>");
                    foreach (var item in list.Items)
                    {
                        var children = item.Children.ToList();
                        if (children.FirstOrDefault() is MdParagraph first && first.Text.Length >= 4 && first.Text[..4].ToLowerInvariant() is "[x] " or "[ ] ")
                        { html.Append("<li class=\"task\">").Append(first.Text[1] is 'x' or 'X' ? "☑ " : "☐ "); children[0] = new MdParagraph(first.Text[4..]); }
                        else html.Append("<li>");
                        WebBlocks(html, w, children); html.Append("</li>");
                    }
                    html.Append(list.Ordered ? "</ol>" : "</ul>");
                    break;
                case MdCode code:
                    html.Append("<div class=\"code\">");
                    if (code.Language?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } name) { html.Append("<div class=\"lang\">"); Html(html, name); html.Append("</div>"); }
                    html.Append("<pre>"); Html(html, code.Text); html.Append("</pre></div>");
                    break;
                case MdQuote quote: html.Append("<blockquote>"); WebBlocks(html, w, quote.Children); html.Append("</blockquote>"); break;
                case MdRule: html.Append("<hr>"); break;
                case MdTable table:
                    html.Append("<table>");
                    for (var index = 0; index < table.Rows.Count; index++)
                    {
                        var cell = index == 0 ? "th" : "td"; html.Append("<tr>");
                        for (var column = 0; column < table.Alignments.Length; column++)
                        {
                            html.Append('<').Append(cell).Append(table.Alignments[column] switch { 'r' => " style=\"text-align:right\">", 'c' => " style=\"text-align:center\">", _ => ">" });
                            WebSpans(html, w, column < table.Rows[index].Length ? table.Rows[index][column] : "", 0);
                            html.Append("</").Append(cell).Append('>');
                        }
                        html.Append("</tr>");
                    }
                    html.Append("</table>");
                    break;
            }
        }
    }

    /// <summary>The inline marks as <see cref="Spans"/> reads them; a picture is its alt text (pictures are not drawn in the prototype).</summary>
    private static void WebSpans(StringBuilder html, Writer w, string text, int depth)
    {
        var position = 0; (int Start, int End, bool Path) word = default;
        foreach (Match match in Inline.Matches(text))
        {
            w.Spend();
            if (match.Groups[1].Success && InPath(text, match.Index, ref word)) continue;
            Text(html, text[position..match.Index]); var value = match.Value; position = match.Index + match.Length;
            void Nested(string tag, string inner) { html.Append('<').Append(tag).Append('>'); if (depth < MaximumDepth) WebSpans(html, w, inner, depth + 1); else Html(html, inner); html.Append("</").Append(tag).Append('>'); }
            if (match.Groups[1].Success) Html(html, value[1..]);
            else if (match.Groups[2].Success) { html.Append("<span class=\"quiet\">"); Html(html, AgentMarkdownImages.Extract(value).FirstOrDefault()?.Alt ?? value); html.Append("</span>"); }
            else if (match.Groups[3].Success) { html.Append("<code>"); Html(html, value.StartsWith("``", StringComparison.Ordinal) ? value[2..^2].Trim() : value[1..^1]); html.Append("</code>"); }
            else if (match.Groups[4].Success) { html.Append("<b>"); Nested("i", value[3..^3]); html.Append("</b>"); }
            else if (match.Groups[5].Success) Nested("b", value[2..^2]);
            else if (match.Groups[6].Success) Nested("i", value[1..^1]);
            else if (match.Groups[7].Success) Nested("s", value[2..^2]);
            else if (match.Groups[9].Success) { html.Append("<span class=\"link\">"); Html(html, value[1..^1]); html.Append("</span>"); }
            else
            {
                // A web address shows itself after its label and opens nothing; a file in the workspace is a reference the click opens.
                var split = value.IndexOf("](", StringComparison.Ordinal); var label = value[1..split]; var link = value[(split + 2)..^1].Trim();
                if (Uri.TryCreate(link, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" or "mailto") { html.Append("<span class=\"link\">"); if (depth < MaximumDepth) WebSpans(html, w, label, depth + 1); else Html(html, label); html.Append("</span>"); Html(html, " (" + link + ")"); }
                else if (ResultFiles.LocalPath(link) is { } local) { html.Append("<a class=\"ref\" data-ref=\"").Append(Attribute(local)).Append("\">"); Html(html, label); html.Append("</a>"); }
                else if (depth < MaximumDepth) WebSpans(html, w, label, depth + 1); else Html(html, label);
            }
        }
        Text(html, text[position..]);
    }

    /// <summary>Plain words with their file paths as references a click opens and their web addresses in the accent (<see cref="References"/>).</summary>
    private static void Text(StringBuilder html, string text)
    {
        if (text.AsSpan().IndexOfAny('/', '\\') < 0) { Html(html, text); return; }
        var at = 0;
        foreach (var (start, length, web) in References(text))
        {
            if (start < at) continue;
            Html(html, text[at..start]); var words = text.Substring(start, length);
            if (web) { html.Append("<span class=\"link\">"); Html(html, words); html.Append("</span>"); }
            else { html.Append("<a class=\"ref\" data-ref=\"").Append(Attribute(words)).Append("\">"); Html(html, words); html.Append("</a>"); }
            at = start + length;
        }
        Html(html, text[at..]);
    }

    /// <summary>Text as HTML: the five marks escaped and the control characters but tab and line break dropped.</summary>
    private static void Html(StringBuilder html, string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': html.Append("&amp;"); break;
                case '<': html.Append("&lt;"); break;
                case '>': html.Append("&gt;"); break;
                case '"': html.Append("&quot;"); break;
                case '\'': html.Append("&#39;"); break;
                case '\r': break;
                case '\n' or '\t': html.Append(c); break;
                default: if (c >= ' ') html.Append(c); break;
            }
        }
    }
    private static string Attribute(string text) { var html = new StringBuilder(text.Length); Html(html, text); return html.ToString(); }
}
