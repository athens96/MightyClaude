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
internal sealed partial class AgentTranscript
{
    internal RichEditBox View { get; } = new() { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, FontSize = 13, MinHeight = 80, BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new(12) };
    private string rendered = "", plain = "";
    private bool selecting;
    internal bool IsSelecting => selecting;
    internal Action? SelectionEnded { get; set; }
    private (RunSession Session, bool Light)? deferred;
    private (RunSession Session, bool Light)? last;
    private AgentPictures? pictures;
    private string? workspaceRoot;
    internal Func<string, int?, Task>? OpenReference { get; set; }
    internal AgentTranscript()
    {
        AutomationProperties.SetName(View, Locale.Get("transcript.accessibility"));
        InitializeTranscriptActions();
        ScrollViewer.SetVerticalScrollBarVisibility(View, ScrollBarVisibility.Auto);
        View.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { if (e.GetCurrentPoint(View).Properties.IsLeftButtonPressed) selecting = true; }), true);
        void Finish(object sender, PointerRoutedEventArgs args) { selecting = false; if (deferred is { } value) { deferred = null; Update(value.Session, value.Light); } SelectionEnded?.Invoke(); }
        View.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Finish), true);
        View.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Finish), true);
        View.AddHandler(UIElement.TappedEvent, new TappedEventHandler(async (_, args) =>
        {
            if (View.Document.Selection.Length != 0) return;
            var point = args.GetPosition(View);
            var range = View.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
            range.SetRange(range.StartPosition, range.StartPosition + 1);
            range.GetRect(PointOptions.ClientCoordinates, out var rect, out var hit);
            if (!rect.Contains(point)) return; // A blank area beside a path is not a link.
            if (await InvokeTranscriptAction(range.Link)) { args.Handled = true; return; }
            if (OpenReference is null || ReferencePreview.TextTargetAt(Text, range.StartPosition) is not { } target) return;
            args.Handled = true;
            await OpenReference(target.Path, target.Line);
        }), true);
    }
    internal string Text { get { View.Document.GetText(TextGetOptions.None, out var value); return value; } }
    /// <summary>
    /// Pictures the agent showed are drawn where they appeared (macOS AgentTranscriptView):
    /// thumbnails come from <paramref name="images"/>, Markdown pictures resolve against
    /// <paramref name="root"/>, and a finished thumbnail redraws the last session.
    /// </summary>
    internal void Update(RunSession session, bool light, AgentPictures? images, string? root)
    {
        if (images is not null && !ReferenceEquals(images, pictures)) images.Listen(this);
        pictures = images; workspaceRoot = root;
        Update(session, light);
    }
    internal void Redraw() { if (last is { } value) Update(value.Session, value.Light); }
    internal void Update(RunSession session, bool light)
    {
        last = (session, light);
        if (selecting) { deferred = (session, light); return; }
        var nextActions = new Dictionary<string, TranscriptAction>();
        var next = TranscriptRtf.Render(session, light, pictures, workspaceRoot, expandedTools, nextActions); actions = nextActions; if (next == rendered) return;
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
    // The agent transcript also reads `![alt](target)`; the files pane preview keeps the plain link rule.
    private static readonly Regex InlineWithPictures = new(@"(!\[[^\]\r\n]*\]\([^\)\r\n]+\)|`[^`\r\n]+`|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__|(?<!\*)\*[^*\r\n]+\*|~~[^~\r\n]+~~|\[[^\]\r\n]+\]\([^\)\r\n]+\))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(150));

    /// <summary>Markdown pictures found in one line, drawn as their own paragraphs after it.</summary>
    private sealed class PictureContext(AgentPictures? pictures, string? root, IReadOnlySet<string>? expanded = null, Dictionary<string, TranscriptAction>? actions = null)
    {
        internal readonly AgentPictures? Pictures = pictures;
        internal readonly string? Root = root;
        internal readonly List<(AgentPictures.Picture Picture, TranscriptAction? Action)> Pending = [];
        internal readonly IReadOnlySet<string>? Expanded = expanded;
        internal readonly Dictionary<string, TranscriptAction>? Actions = actions;
    }

    internal static string Render(RunSession session, bool light) => Render(session, light, null, null);
    internal static string Render(RunSession session, bool light, AgentPictures? pictures, string? root, IReadOnlySet<string>? expanded = null, Dictionary<string, TranscriptAction>? actions = null)
    {
        var context = new PictureContext(pictures, root, expanded, actions);
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
                if (!string.IsNullOrWhiteSpace(activity.Output))
                {
                    if (actions is not null) { body.Append(Escape("  ")); ActionLink(body, Locale.Get(expanded?.Contains(entry.Id) == true ? "transcript.tool.hideDetail" : "transcript.tool.showDetail"), new("tool", entry.Id), context); }
                    body.Append(@"\par ");
                    if (actions is null || expanded?.Contains(entry.Id) == true) Code(body, activity.Output, context);
                }
                else body.Append(@"\par ");
            }
            else if (entry.Kind == "image")
            {
                // The pictures, then their source in small grey type (macOS: `Read · /path/shot.png`).
                foreach (var image in entry.Images ?? []) Picture(body, pictures?.For(image) ?? new(AgentPictures.State.Unsupported), context, new("image", image.Source, image));
                body.Append(@"\pard\sa120\fs20\cf2 ").Append(Escape(entry.Text)).Append(@"\par ");
            }
            else
            {
                if (entry.Kind == "user") body.Append(@"\pard\sb220\sa90\cf3\b ").Append(Escape(Locale.Get("transcript.requestHeading"))).Append(@"\b0\par ");
                else if (entry.Kind == "error") body.Append(@"\pard\sb160\cf4 ").Append(Escape("⚠ "));
                else if (entry.Kind is "system") body.Append(@"\pard\sb100\fs23\cf2 ").Append(Escape("· "));
                var contentStart = body.Length;
                try { if (session.Kind == "shell") Code(body, entry.Text, context); else Markdown(body, entry.Text, entry.Kind == "assistant" ? context : null); }
                catch (RegexMatchTimeoutException)
                {
                    // A provider's malformed Markdown must remain readable and
                    // cannot escape the native UI event dispatcher as an error.
                    body.Length = contentStart; context.Pending.Clear();
                    body.Append(@"\pard\sa90\f0\fs26\cf1 ").Append(Escape(entry.Text)).Append(@"\par ");
                }
                body.Append(@"\pard\sa120\par ");
            }
        }
        return body.Append('}').ToString();
    }
    /// <summary>One Markdown document (the files pane preview) with the transcript's renderer and colours.</summary>
    internal static string RenderMarkdown(string text, bool light)
    {
        var body = new StringBuilder(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 Segoe UI;}{\f1 Cascadia Mono;}}{\colortbl;");
        body.Append(light ? @"\red32\green34\blue39;\red95\green100\blue112;\red37\green99\blue200;\red172\green54\blue45;\red238\green241\blue246;}" : @"\red231\green233\blue238;\red164\green172\blue187;\red128\green177\blue255;\red255\green147\blue132;\red37\green41\blue49;}");
        body.Append(@"\viewkind4\f0\fs26\cf1 ");
        try { Markdown(body, text); }
        catch (RegexMatchTimeoutException) { body.Append(@"\pard\sa90\f0\fs26\cf1 ").Append(Escape(text)).Append(@"\par "); }
        return body.Append('}').ToString();
    }
    /// <summary>A thumbnail, or the macOS placeholder while it loads or once it is gone. An svg is not drawn here.</summary>
    private static void Picture(StringBuilder body, AgentPictures.Picture picture, PictureContext? context = null, TranscriptAction? action = null)
    {
        switch (picture.State)
        {
            case AgentPictures.State.Ready:
                body.Append(@"\pard\sb80\sa40 ");
                if (context?.Actions is not null && action is not null) ActionLink(body, picture.Rtf!, action, context, raw: true); else body.Append(picture.Rtf);
                body.Append(@"\par ");
                if (context?.Actions is not null && action is not null) { ActionLink(body, Locale.Get("images.open"), action, context); body.Append(@"\par "); }
                break;
            case AgentPictures.State.Loading: body.Append(@"\pard\sb80\sa40\fs22\cf2 ").Append(Escape(Locale.Get("images.loading"))).Append(@"\par "); break;
            case AgentPictures.State.Missing: body.Append(@"\pard\sb80\sa40\fs22\cf2 ").Append(Escape(Locale.Get("images.missing"))).Append(@"\par "); break;
        }
    }
    private static void Markdown(StringBuilder body, string text, PictureContext? context = null)
    {
        bool code = false; var fenced = new StringBuilder(); string? fence = null;
        foreach (var original in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = original.TrimEnd(); var trim = line.TrimStart();
            if (!code && (trim.StartsWith("```", StringComparison.Ordinal) || trim.StartsWith("~~~", StringComparison.Ordinal))) { code = true; fence = trim[..3]; continue; }
            if (code && trim.StartsWith(fence!, StringComparison.Ordinal)) { Code(body, fenced.ToString().TrimEnd('\n'), context); fenced.Clear(); code = false; continue; }
            if (code) { fenced.Append(original).Append('\n'); continue; }
            if (Regex.IsMatch(trim, @"^\|?\s*:?-{3,}.*\|", RegexOptions.None, TimeSpan.FromMilliseconds(100))) continue;
            var heading = Regex.Match(trim, @"^(#{1,6})\s+(.+)$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (heading.Success) { body.Append(@"\pard\sb180\sa100\b\cf1\fs").Append(heading.Groups[1].Length <= 2 ? 34 : 28).Append(' '); WriteInline(body, heading.Groups[2].Value, context); body.Append(@"\b0\par "); Flush(body, context); continue; }
            if (trim.StartsWith('>')) { body.Append(@"\pard\li240\sa75\cf2\fs26 "); WriteInline(body, "│ " + trim[1..].TrimStart(), context); }
            else if (Regex.IsMatch(trim, @"^(?:[-*+] |\d+[.)] )", RegexOptions.None, TimeSpan.FromMilliseconds(100))) { body.Append(@"\pard\li240\fi-180\sa60\cf1\fs26 "); WriteInline(body, Regex.Replace(trim, @"^[-*+] ", "• ", RegexOptions.None, TimeSpan.FromMilliseconds(100)), context); }
            else if (trim.Contains('|') && trim.Count(c => c == '|') >= 2) { body.Append(@"\pard\sa70\f1\fs23\cf1 "); WriteInline(body, trim.Trim('|').Replace("|", "  │  ", StringComparison.Ordinal), context); }
            else { body.Append(@"\pard\sa90\f0\fs26\cf1 "); WriteInline(body, line, context); }
            body.Append(@"\par ");
            Flush(body, context);
        }
        if (code) Code(body, fenced.ToString().TrimEnd('\n'), context);
    }
    private static void Flush(StringBuilder body, PictureContext? context)
    {
        if (context is null || context.Pending.Count == 0) return;
        foreach (var picture in context.Pending) Picture(body, picture.Picture, context, picture.Action);
        context.Pending.Clear();
    }
    private static void Code(StringBuilder body, string text, PictureContext? context = null)
    {
        if (context?.Actions is not null) { body.Append(@"\pard\sb60\sa40\fs20\f0 "); ActionLink(body, Locale.Get("transcript.code.copy"), new("copy", text), context); body.Append(@"\par "); }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) body.Append(@"\pard\li140\ri100\sa30\f1\fs23\cf1\highlight5 ").Append(Escape(line)).Append(@"\highlight0\par ");
    }
    private static void WriteInline(StringBuilder body, string text, PictureContext? context = null)
    {
        var position = 0;
        foreach (Match match in (context is null ? Inline : InlineWithPictures).Matches(text))
        {
            body.Append(Escape(text[position..match.Index])); var value = match.Value;
            if (context is not null && value.StartsWith("![", StringComparison.Ordinal)) InlinePicture(body, value, context);
            else if (value.StartsWith('`')) body.Append(@"{\f1\highlight5 ").Append(Escape(value[1..^1])).Append('}');
            else if (value.StartsWith("**", StringComparison.Ordinal) || value.StartsWith("__", StringComparison.Ordinal)) body.Append(@"{\b ").Append(Escape(value[2..^2])).Append('}');
            else if (value.StartsWith("~~", StringComparison.Ordinal)) body.Append(@"{\strike ").Append(Escape(value[2..^2])).Append('}');
            else if (value.StartsWith('*')) body.Append(@"{\i ").Append(Escape(value[1..^1])).Append('}');
            else { var split = value.IndexOf("](", StringComparison.Ordinal); var link = value[(split + 2)..^1]; body.Append(@"{\cf3\ul ").Append(Escape(value[1..split])).Append('}'); if (Uri.TryCreate(link, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" or "mailto") body.Append(Escape(" (" + link + ")")); else if (ResultFiles.LocalPath(link) is { } local) body.Append(@"{\cf3\ul ").Append(Escape(" (" + local + ")")).Append('}'); }
            position = match.Index + match.Length;
        }
        body.Append(Escape(text[position..]));
    }
    /// <summary>
    /// A Markdown picture under the macOS rule: a file inside the workspace or temporary
    /// folder, or a data URI, is drawn after the line; an http(s) picture is a link that is
    /// never fetched; anything else keeps its alt text and says why it is not shown.
    /// </summary>
    private static void InlinePicture(StringBuilder body, string value, PictureContext context)
    {
        var picture = AgentMarkdownImages.Extract(value).FirstOrDefault();
        if (picture is null) { body.Append(Escape(value)); return; }
        var alt = picture.Alt.Trim();
        // With pictures, the location comes from AgentPictures' cache: a render never asks the disk.
        AgentImageLocation location; AgentPictures.Picture? shown = null;
        if (context.Pictures is null) location = AgentImagePaths.Locate(picture.Source, context.Root);
        else if (context.Pictures.Locate(picture.Source, context.Root) is { } found) (location, shown) = found;
        else
        {
            // Still being found: the alt text now, the picture (or its link) once it is known.
            if (alt.Length > 0) body.Append(@"{\cf2 ").Append(Escape(alt)).Append('}');
            context.Pending.Add((new(AgentPictures.State.Loading), null));
            return;
        }
        switch (location)
        {
            case AgentImageLocation.Remote remote:
                body.Append(@"{\cf3\ul ").Append(Escape(alt.Length > 0 ? alt : remote.Url.ToString())).Append('}').Append(Escape(" (" + remote.Url + ")"));
                body.Append(@"{\cf2 ").Append(Escape(" (" + Locale.Get("images.remote") + ")")).Append('}');
                break;
            case AgentImageLocation.File or AgentImageLocation.Inline when context.Pictures is not null:
                if (alt.Length > 0) body.Append(@"{\cf2 ").Append(Escape(alt)).Append('}');
                context.Pending.Add((shown ?? new(AgentPictures.State.Missing), new("image", picture.Source)));
                break;
            default:
                body.Append(@"{\cf2 ").Append(Escape((alt.Length > 0 ? alt + " " : "") + "(" + Locale.Get("images.refused") + ")")).Append('}');
                break;
        }
    }
    private static void ActionLink(StringBuilder body, string label, TranscriptAction action, PictureContext context, bool raw = false)
    {
        if (context.Actions is null) { body.Append(raw ? label : Escape(label)); return; }
        var key = context.Actions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); context.Actions[key] = action;
        // Only generated URLs become fields; all model text still passes Escape.
        body.Append(@"{\field{\*\fldinst HYPERLINK ""mighty-action://").Append(key).Append(@"""}{\fldrslt{\cf3\ul ").Append(raw ? label : Escape(label)).Append("}}}");
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
