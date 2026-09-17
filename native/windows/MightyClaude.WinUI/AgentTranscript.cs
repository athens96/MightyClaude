using System.Text;
using System.Text.RegularExpressions;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>One native document permits selection and Ctrl+C across every message.</summary>
internal sealed class AgentTranscript
{
    internal RichEditBox View { get; } = new() { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, FontSize = 13, MinHeight = 80, BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new(12) };
    private string rendered = "", plain = "";
    private bool selecting;
    private (RunSession Session, bool Light)? deferred;
    internal AgentTranscript()
    {
        AutomationProperties.SetName(View, "에이전트 출력 · 여러 문단을 선택해 복사할 수 있습니다");
        ScrollViewer.SetVerticalScrollBarVisibility(View, ScrollBarVisibility.Auto);
        View.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { if (e.GetCurrentPoint(View).Properties.IsLeftButtonPressed) selecting = true; }), true);
        void Finish(object sender, PointerRoutedEventArgs args) { selecting = false; if (deferred is { } value) { deferred = null; Update(value.Session, value.Light); } }
        View.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Finish), true);
        View.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Finish), true);
    }
    internal string Text { get { View.Document.GetText(TextGetOptions.None, out var value); return value; } }
    internal void Update(RunSession session, bool light)
    {
        if (selecting) { deferred = (session, light); return; }
        var next = TranscriptRtf.Render(session, light); if (next == rendered) return;
        var selection = View.Document.Selection; var start = selection.StartPosition; var end = selection.EndPosition;
        var scroll = Descendant<ScrollViewer>(View); var offset = scroll?.VerticalOffset ?? 0;
        var follows = scroll is null || scroll.ScrollableHeight - offset < 32;
        var previous = plain;
        // WinUI also blocks programmatic document writes while IsReadOnly is true.
        // Keep this synchronous so no user input can run before protection returns.
        var readOnly = View.IsReadOnly;
        try { View.IsReadOnly = false; View.Document.SetText(TextSetOptions.FormatRtf, next); }
        finally { View.IsReadOnly = readOnly; }
        rendered = next;
        View.Document.GetText(TextGetOptions.None, out plain);
        var prefix = 0; while (prefix < previous.Length && prefix < plain.Length && previous[prefix] == plain[prefix]) prefix++;
        var suffix = 0; while (suffix < previous.Length - prefix && suffix < plain.Length - prefix && previous[^(suffix + 1)] == plain[^(suffix + 1)]) suffix++;
        int Position(int position) => Math.Clamp(position <= prefix ? position : position >= previous.Length - suffix ? position + plain.Length - previous.Length : prefix + Math.Min(position - prefix, plain.Length - prefix - suffix), 0, Math.Max(0, plain.Length - 1));
        selection.SetRange(Position(start), Position(end));
        View.DispatcherQueue.TryEnqueue(() => { var viewer = Descendant<ScrollViewer>(View); viewer?.ChangeView(null, follows && start == end ? viewer.ScrollableHeight : offset, null, true); });
    }
    private static T? Descendant<T>(DependencyObject value) where T : DependencyObject
    {
        if (value is T found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) if (Descendant<T>(VisualTreeHelper.GetChild(value, i)) is { } child) return child;
        return null;
    }
}

/// <summary>Bounded Markdown to RTF. All source text is escaped; no HTML, images,
/// file links, fields or executable RTF instructions from provider output run.</summary>
internal static class TranscriptRtf
{
    private static readonly Regex Inline = new(@"(`[^`\r\n]+`|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__|(?<!\*)\*[^*\r\n]+\*|~~[^~\r\n]+~~|\[[^\]\r\n]+\]\([^\)\r\n]+\))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(150));
    internal static string Render(RunSession session, bool light)
    {
        var body = new StringBuilder(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 Segoe UI;}{\f1 Cascadia Mono;}}{\colortbl;");
        body.Append(light ? @"\red32\green34\blue39;\red95\green100\blue112;\red37\green99\blue200;\red172\green54\blue45;\red238\green241\blue246;}" : @"\red231\green233\blue238;\red164\green172\blue187;\red128\green177\blue255;\red255\green147\blue132;\red37\green41\blue49;}");
        body.Append(@"\viewkind4\f0\fs26\cf1 ");
        foreach (var entry in session.Logs)
        {
            if (entry.Activity is { } activity)
            {
                var icon = activity.State == "error" ? "⚠" : activity.State == "waiting" && session.Status == "running" ? "◷" : activity.Kind switch { "command" => "›_", "read" => "▤", "edit" => "✎", "search" or "web" => "⌕", "agent" => "◇", _ => "·" };
                body.Append(@"\pard\sa90\sb140\fs23\cf2 ").Append(Escape(icon + " " + activity.Summary));
                var duration = ActivitySupport.DurationLabel(activity); if (!string.IsNullOrEmpty(duration)) body.Append(Escape("  · " + duration));
                body.Append(@"\par ");
                if (!string.IsNullOrWhiteSpace(activity.Output)) Code(body, activity.Output);
            }
            else
            {
                if (entry.Kind == "user") body.Append(@"\pard\sb220\sa90\cf3\b ").Append(Escape("↑ 요청")).Append(@"\b0\par ");
                else if (entry.Kind == "error") body.Append(@"\pard\sb160\cf4 ").Append(Escape("⚠ "));
                else if (entry.Kind is "system") body.Append(@"\pard\sb100\fs23\cf2 ").Append(Escape("· "));
                var contentStart = body.Length;
                try { if (session.Kind == "shell") Code(body, entry.Text); else Markdown(body, entry.Text); }
                catch (RegexMatchTimeoutException)
                {
                    // A provider's malformed Markdown must remain readable and
                    // cannot escape the native UI event dispatcher as an error.
                    body.Length = contentStart;
                    body.Append(@"\pard\sa90\f0\fs26\cf1 ").Append(Escape(entry.Text)).Append(@"\par ");
                }
                body.Append(@"\pard\sa120\par ");
            }
        }
        return body.Append('}').ToString();
    }
    private static void Markdown(StringBuilder body, string text)
    {
        bool code = false;
        foreach (var original in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = original.TrimEnd(); var trim = line.TrimStart();
            if (trim.StartsWith("```", StringComparison.Ordinal) || trim.StartsWith("~~~", StringComparison.Ordinal)) { code = !code; continue; }
            if (code) { Code(body, line); continue; }
            if (Regex.IsMatch(trim, @"^\|?\s*:?-{3,}.*\|", RegexOptions.None, TimeSpan.FromMilliseconds(100))) continue;
            var heading = Regex.Match(trim, @"^(#{1,6})\s+(.+)$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (heading.Success) { body.Append(@"\pard\sb180\sa100\b\cf1\fs").Append(heading.Groups[1].Length <= 2 ? 34 : 28).Append(' '); WriteInline(body, heading.Groups[2].Value); body.Append(@"\b0\par "); continue; }
            if (trim.StartsWith('>')) { body.Append(@"\pard\li240\sa75\cf2\fs26 "); WriteInline(body, "│ " + trim[1..].TrimStart()); }
            else if (Regex.IsMatch(trim, @"^(?:[-*+] |\d+[.)] )", RegexOptions.None, TimeSpan.FromMilliseconds(100))) { body.Append(@"\pard\li240\fi-180\sa60\cf1\fs26 "); WriteInline(body, Regex.Replace(trim, @"^[-*+] ", "• ", RegexOptions.None, TimeSpan.FromMilliseconds(100))); }
            else if (trim.Contains('|') && trim.Count(c => c == '|') >= 2) { body.Append(@"\pard\sa70\f1\fs23\cf1 "); WriteInline(body, trim.Trim('|').Replace("|", "  │  ", StringComparison.Ordinal)); }
            else { body.Append(@"\pard\sa90\f0\fs26\cf1 "); WriteInline(body, line); }
            body.Append(@"\par ");
        }
    }
    private static void Code(StringBuilder body, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) body.Append(@"\pard\li140\ri100\sa30\f1\fs23\cf1\highlight5 ").Append(Escape(line)).Append(@"\highlight0\par ");
    }
    private static void WriteInline(StringBuilder body, string text)
    {
        var position = 0;
        foreach (Match match in Inline.Matches(text))
        {
            body.Append(Escape(text[position..match.Index])); var value = match.Value;
            if (value.StartsWith('`')) body.Append(@"{\f1\highlight5 ").Append(Escape(value[1..^1])).Append('}');
            else if (value.StartsWith("**", StringComparison.Ordinal) || value.StartsWith("__", StringComparison.Ordinal)) body.Append(@"{\b ").Append(Escape(value[2..^2])).Append('}');
            else if (value.StartsWith("~~", StringComparison.Ordinal)) body.Append(@"{\strike ").Append(Escape(value[2..^2])).Append('}');
            else if (value.StartsWith('*')) body.Append(@"{\i ").Append(Escape(value[1..^1])).Append('}');
            else { var split = value.IndexOf("](", StringComparison.Ordinal); var link = value[(split + 2)..^1]; body.Append(@"{\cf3\ul ").Append(Escape(value[1..split])).Append('}'); if (Uri.TryCreate(link, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" or "mailto") body.Append(Escape(" (" + link + ")")); }
            position = match.Index + match.Length;
        }
        body.Append(Escape(text[position..]));
    }
    internal static string Escape(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '\\' or '{' or '}') output.Append('\\').Append(c);
            else if (c == '\t') output.Append(@"\tab ");
            else if (c < ' ') { if (c is '\n' or '\r') output.Append(@"\par "); }
            else if (c <= 127) output.Append(c);
            else output.Append(@"\u").Append(unchecked((short)c)).Append('?');
        }
        return output.ToString();
    }
}
