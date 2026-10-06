using System.Text;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace MightyClaude.WinUI;

internal sealed record TranscriptAction(string Kind, string Value, AgentImageRef? Image = null);

internal sealed partial class AgentTranscript
{
    private Dictionary<string, TranscriptAction> actions = [];
    /// <summary>
    /// Where each action sits in the document: a tool row's disclosure words, a picture, a code block. The Mac
    /// draws its links with no underline (M/AgentTranscriptView.swift:169-171) and a RichEdit link always has
    /// one, so the document holds no link: a click, Ctrl+Enter and the menu ask these ranges instead.
    /// </summary>
    private List<(int Start, int End, string Key)> ranges = [];
    private readonly HashSet<string> expandedTools = [];
    internal Func<TranscriptAction, Task>? OpenImage { get; set; }
    /// <summary>
    /// The code block the next context menu is asked over: the one under the pointer's press, none when the
    /// press is beside every block. A menu no press asked for (the Menu key, Shift+F10) reads the selection.
    /// </summary>
    private (bool Pressed, TranscriptAction? Code) asked;
    /// <summary>The code block the open menu offers to copy: taken as it opens, so the item copies that block whatever is drawn or pressed afterwards.</summary>
    private TranscriptAction? offered;
    private void InitializeTranscriptActions()
    {
        // The Mac's menu: Copy, and over code "copy code block" (M/AgentTranscriptView.swift:579-595).
        var menu = new MenuFlyout(); var copy = new MenuFlyoutItem { Text = Locale.Get("pane.copyButton") };
        copy.Click += (_, _) => CopySelection(); menu.Items.Add(copy);
        var copyCode = new MenuFlyoutItem { Text = Locale.Get("menu.copyCodeBlock") };
        copyCode.Click += (_, _) => { if (offered is { } code) CopyText(code.Value); };
        menu.Items.Add(copyCode);
        menu.Opening += (_, _) => copyCode.Visibility = OfferCode() ? Visibility.Visible : Visibility.Collapsed;
        View.ContextFlyout = menu;
        // The stock flyout over a touch or pen selection copies with the box's own command, which writes each drawn mark as a word.
        View.SelectionFlyout = null;
        // The box opens its menu before a ContextRequested handler added here is called (the smoke records that
        // order), so a handler that only noted where the request was told the menu where the request before
        // this one had been. What the menu is asked over is read as the pointer is pressed, which every pointer
        // does before its release or its hold opens the menu; and the request, when it arrives with a pointer's
        // position, has the last word and puts right what an open menu took.
        View.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            AskCode(View.Document.GetRangeFromPoint(args.GetCurrentPoint(TextOrigin).Position, PointOptions.ClientCoordinates).StartPosition)), true);
        View.AddHandler(UIElement.ContextRequestedEvent, new TypedEventHandler<UIElement, ContextRequestedEventArgs>((_, args) =>
        {
            if (!args.TryGetPosition(TextOrigin, out var point)) return;
            AskCode(View.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates).StartPosition);
            if (menu.IsOpen) copyCode.Visibility = OfferCode() ? Visibility.Visible : Visibility.Collapsed;
        }), true);
        View.PreviewKeyDown += async (_, args) =>
        {
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            // The keys that open the menu press nowhere: an earlier press is not what they ask over.
            if (args.Key is Windows.System.VirtualKey.Application or Windows.System.VirtualKey.F10 or Windows.System.VirtualKey.GamepadMenu) asked = default;
            // Ctrl+Insert copies as Ctrl+C does; left to the box, it would write each drawn mark as a word.
            if ((args.Key == Windows.System.VirtualKey.C || args.Key == Windows.System.VirtualKey.Insert) && control)
            { if (View.Document.Selection.Length > 0) { args.Handled = true; CopySelection(); } }
            else if (args.Key == Windows.System.VirtualKey.Enter && control)
            { if (await InvokeTranscriptAction(View.Document.Selection.StartPosition)) args.Handled = true; }
        };
    }
    /// <summary>Remembers the code block at a pressed character for the menu that press may open.</summary>
    private void AskCode(int position) => asked = (true, ActionAt(position, "copy"));
    /// <summary>
    /// Settles what the opening menu offers: the block its press was over, or with no press the one the
    /// selection starts in. One press serves one menu. False when there is no block to offer.
    /// </summary>
    private bool OfferCode()
    {
        offered = asked.Pressed ? asked.Code : ActionAt(View.Document.Selection.StartPosition, "copy");
        asked = default;
        return offered is not null;
    }
    /// <summary>
    /// The character under a point of the text view, or null beside the text. The document's coordinates do
    /// not move when the box scrolls; the text view does, so the point is taken from it (<see cref="TextOrigin"/>).
    /// </summary>
    private int? PositionAt(Point point)
    {
        var range = View.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
        // The nearest boundary may be the far side of the character that was hit.
        foreach (var at in new[] { range.StartPosition, range.StartPosition - 1 })
        {
            if (at < 0 || at >= plain.Length) continue;
            View.Document.GetRange(at, at + 1).GetRect(PointOptions.ClientCoordinates, out var rect, out _);
            if (rect.Contains(point)) return at;
        }
        return null;
    }
    /// <summary>What a click acts on at a character (a disclosure, a picture), or with <paramref name="kind"/> the action of that kind there (a code block's copy).</summary>
    private TranscriptAction? ActionAt(int? position, string? kind = null)
    {
        if (position is not { } at) return null;
        foreach (var (start, end, key) in ranges)
            if (at >= start && at < end && actions.TryGetValue(key, out var action) && (kind is null ? action.Kind != "copy" : action.Kind == kind)) return action;
        return null;
    }
    /// <summary>The selection as Copy takes it: its words, without the drawn marks and the pictures' characters.</summary>
    private string SelectedWords()
    {
        var selection = View.Document.Selection;
        selection.GetText(TextGetOptions.None, out var text);
        // Attachment characters are layout, not text the user asked for (M/SelectableTextView.swift:124-132).
        return Words(text, Math.Min(selection.StartPosition, selection.EndPosition), marks, pictures: false);
    }
    private void CopySelection() { if (SelectedWords() is { Length: > 0 } text) CopyText(text); }
    private static void CopyText(string text)
    { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
    /// <summary>
    /// The words of a stretch of the document that starts at <paramref name="offset"/>: the drawn marks left
    /// out (and with <paramref name="pictures"/> off, every picture's character), a table's cells parted by
    /// tabs and its rows by their own line ends, and a line break inside a paragraph as the line end every
    /// paragraph has here (RichEdit keeps it as U+000B, which nothing outside the box reads as one).
    /// </summary>
    private static string Words(string text, int offset, HashSet<int> marks, bool pictures = true)
    {
        const char cell = (char)0x7, rowStart = (char)0xFFF9, rowEnd = (char)0xFFFB;
        if (marks.Count == 0 && pictures && text.AsSpan().IndexOfAny([cell, rowStart, rowEnd, TranscriptRtf.LineBreak]) < 0) return text;
        var words = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == TranscriptRtf.ObjectCharacter && (!pictures || marks.Contains(offset + index))) continue;
            if (c == rowStart) { if (index + 1 < text.Length && text[index + 1] == '\r') index++; continue; }
            if (c == rowEnd) continue;
            words.Append(c == cell ? '\t' : c == TranscriptRtf.LineBreak ? '\r' : c);
        }
        return words.ToString();
    }
    private async Task<bool> InvokeTranscriptAction(int position)
    {
        if (ActionAt(position) is not { } action) return false;
        switch (action.Kind)
        {
            case "tool": if (!expandedTools.Remove(action.Value)) expandedTools.Add(action.Value); Redraw(); break;
            case "image" when OpenImage is not null: await OpenImage(action); break;
        }
        return true;
    }

    internal async Task<Dictionary<string, object?>> RunActionsSmoke()
    {
        var previous = last; var previousExpanded = expandedTools.ToArray();
        try
        {
            // i18n-exempt-begin: RunActionsSmoke (--smoke-test) code-copy fixture: Korean source text that must be copied back unchanged.
            const string exactCode = "const \uD55C\uAE00 = `\uADF8\uB300\uB85C \uBCF5\uC0AC`;";
            // i18n-exempt-end
            var fixture = new RunSession { Logs = [
                new("copy-fixture", "assistant", "```js\n" + exactCode + "\n```\n\nhttps://example.com/agent-link [link](https://example.com/labelled)", Wire.Now()),
                new("tool-fixture", "activity", "read", Wire.Now(), "claude", new("tool", "claude", "read", "completed", "Read README.md", Output: "hidden tool detail")),
            ] };
            Update(fixture, false);
            var code = actions.Single(pair => pair.Value.Kind == "copy");
            if (code.Value.Value != exactCode) throw new InvalidOperationException("Code-copy action changed the original Korean source.");
            var codeAt = plain.IndexOf("const", StringComparison.Ordinal);
            if (codeAt < 0 || ActionAt(codeAt, "copy")?.Value != exactCode) throw new InvalidOperationException("The copy-code-block menu does not act on the code block under it.");
            var show = Locale.Get("transcript.tool.showDetail"); var at = plain.IndexOf(show, StringComparison.Ordinal);
            if (at < 0 || plain.Contains("hidden tool detail", StringComparison.Ordinal)) throw new InvalidOperationException("Tool details are not initially collapsed.");
            if (!await InvokeTranscriptAction(at) || !plain.Contains("hidden tool detail", StringComparison.Ordinal)) throw new InvalidOperationException("The tool row's own disclosure did not expand its detail.");
            // Nothing an agent writes is a link, and a click outside the disclosure and the pictures does nothing.
            View.Document.GetText(TextGetOptions.FormatRtf, out var written);
            var address = plain.IndexOf("https://example.com/agent-link", StringComparison.Ordinal);
            if (address < 0 || written.Contains("HYPERLINK", StringComparison.Ordinal) || View.Document.GetRange(address, address + 8).Link.Length > 0 || await InvokeTranscriptAction(address) || await InvokeTranscriptAction(codeAt))
                throw new InvalidOperationException("An unregistered output URL became an application action.");
            // A transcript that opens references draws the paths in its words underlined and its addresses up to their
            // punctuation, as the Mac's links (M/AgentTranscriptFormat.swift:268-288); any other keeps the words plain.
            var words = new RunSession { Logs = [new("reference-fixture", "assistant", "docs/guide/readme.md:12 https://example.com/agent-link. v1.2 and/or README.md", Wire.Now())] };
            var linked = TranscriptRtf.Render(words, false, null, null, null, null, new TranscriptLook { References = true });
            if (!linked.Contains(@"\ul docs/guide/readme.md:12}", StringComparison.Ordinal) || !linked.Contains(" https://example.com/agent-link}.", StringComparison.Ordinal)
                || linked.Split(@"\ul ").Length != 2 || TranscriptRtf.Render(words, false).Contains(@"\ul ", StringComparison.Ordinal))
                throw new InvalidOperationException("The paths and addresses in a Mighty block's words are not drawn as its links, or the Default conversation draws them.");

            void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            void Draw(string id, string reply) => Update(new RunSession { Logs = [new(id, "assistant", reply, Wire.Now())] }, false);
            // "Copy code block" reaches through the mark that ends the block's last line, where a press beside
            // the line lands, and no further (M/AgentTranscriptFormat.swift:445-452).
            var lineEnd = plain.IndexOf('\r', codeAt);
            Require(lineEnd > codeAt && ActionAt(lineEnd, "copy")?.Value == exactCode && ActionAt(lineEnd + 1, "copy") is null,
                $"A code block's range must run through the end of its last line and stop there; at the line's end {ActionAt(lineEnd, "copy")?.Value ?? "nothing"}, after it {ActionAt(lineEnd + 1, "copy")?.Value ?? "nothing"}.");
            // The menu, opened as the box itself opens it for a context request (the Menu key's way, with no pointer). It offers
            // the block its own press was over, not the block of the press before; none for a press beside every block,
            // wherever the selection is; and with no press at all the block the selection starts in.
            var menu = (MenuFlyout)View.ContextFlyout; var copyCode = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == Locale.Get("menu.copyCodeBlock"));
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(View) ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(View);
            // What a ContextRequested handler added to the box sees, and when: the order is kept for the report.
            var order = new List<string>(); var requested = new TypedEventHandler<UIElement, ContextRequestedEventArgs>((_, _) => order.Add("request"));
            async Task<bool> Menu()
            {
                int raised = 0, shut = 0; void Opening(object? sender, object args) { raised++; order.Add("opening"); } void Closed(object? sender, object args) => shut++;
                menu.Opening += Opening; menu.Closed += Closed;
                try
                {
                    peer.ShowContextMenu();
                    for (var wait = 0; !(raised > 0 && menu.IsOpen) && wait < 200; wait++) await Task.Delay(20);
                    Require(raised == 1 && menu.IsOpen, $"The transcript's menu did not open for a context request: opening raised {raised} times, open {menu.IsOpen}.");
                    var offers = copyCode.Visibility == Visibility.Visible;
                    menu.Hide();
                    // A menu that is still closing does not open again: the next request waits for this one to have closed.
                    for (var wait = 0; shut == 0 && wait < 200; wait++) await Task.Delay(20);
                    await Task.Delay(150);
                    return offers;
                }
                finally { menu.Opening -= Opening; menu.Closed -= Closed; }
            }
            View.AddHandler(UIElement.ContextRequestedEvent, requested, true);
            try
            {
                View.Document.Selection.SetRange(address, address);
                AskCode(lineEnd);
                Require(await Menu() && offered?.Value == exactCode, "A press at the end of a code line must offer that block to copy.");
                Require(!await Menu() && offered is null, "A menu opened with no press of its own offered the block of the press before it.");
                AskCode(address); View.Document.Selection.SetRange(codeAt, codeAt);
                Require(!await Menu() && offered is null, "A press beside every code block must offer none, wherever the selection is.");
                Require(await Menu() && offered?.Value == exactCode, "With no press, the menu must offer the block the selection starts in.");
            }
            finally { View.RemoveHandler(UIElement.ContextRequestedEvent, requested); }

            // A Windows path keeps every backslash, in the words and under a click; ordinary prose loses its escapes as CommonMark has it.
            const string path = @"C:\Work\MightyClaude\.claude\settings.json", share = @"\\server\share\_tmp\a.txt";
            Draw("path-fixture", "I updated " + path + " and " + share + ", snake\\_case and \\*literal\\* too.\n\n| where |\n|---|\n| " + path + " |");
            var pathAt = plain.IndexOf(path, StringComparison.Ordinal);
            Require(pathAt >= 0 && plain.Contains("I updated " + path + " and " + share + ", snake_case and *literal* too.", StringComparison.Ordinal) && plain.IndexOf(path, pathAt + 1, StringComparison.Ordinal) > pathAt,
                "A Windows path in a reply's words or in a table cell lost a backslash, or ordinary prose kept its escapes: " + (pathAt < 0 ? plain : plain[pathAt..Math.Min(plain.Length, pathAt + 160)]));
            Require(ReferencePreview.TextTargetAt(plain, pathAt + 24) is { Path: path, Line: null }, "A click on a Windows path in the words must read the whole path; got " + ReferencePreview.TextTargetAt(plain, pathAt + 24)?.Path);
            var pathWords = new RunSession { Logs = [new("path-reference-fixture", "assistant", "I updated " + path + " today.", Wire.Now())] };
            Require(TranscriptRtf.Render(pathWords, false, null, null, null, null, new TranscriptLook { References = true }).Contains(@"\ul " + TranscriptRtf.Escape(path) + "}", StringComparison.Ordinal),
                "A Mighty block must underline a Windows path whole, as one reference.");

            // A table directly under a line of text, or under a list item's line, is a table: the Mac's parser takes its head row back out of the paragraph.
            Draw("table-fixture", "Here is the table:\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- Results:\n  | c | d |\n  |---|---|\n  | 3 | 4 |");
            var tables = Text;
            Require(tables.Contains("Here is the table:\ra\tb\t\r1\t2\t\r", StringComparison.Ordinal) && tables.Contains("Results:\rc\td\t\r3\t4\t\r", StringComparison.Ordinal) && !tables.Contains("---", StringComparison.Ordinal),
                "A table under a line of text must be drawn as a paragraph and a table; the document reads: " + tables.Replace('\r', '/').Replace('\t', '^'));

            // A table RichEdit cannot hold (more than 63 columns) or one too large to draw (more cells than a source may) is
            // drawn as the lines it was written in, with every paragraph counted (the reply's card is still placed), and a table beside it is still a table.
            var wide = "|" + string.Concat(Enumerable.Range(0, 64).Select(column => "h" + column + "|")); var rule = "|" + string.Concat(Enumerable.Repeat("-|", 64));
            Draw("oversize-fixture", wide + "\n" + rule + "\n" + wide + "\n\n|p|q|\n|-|-|\n" + string.Join("\n", Enumerable.Repeat("|x|y|", 1_001)) + "\n\n| a | b |\n|---|---|\n| 1 | 2 |");
            Require(plain.Contains(wide + "\r" + rule + "\r" + wide + "\r", StringComparison.Ordinal) && plain.Contains("|p|q|\r|-|-|\r|x|y|\r|x|y|\r", StringComparison.Ordinal) && Text.Contains("a\tb\t\r1\t2\t\r", StringComparison.Ordinal),
                "A table of 64 columns, or of more cells than a source may draw, must be drawn as its source lines, and a small table after it as a table.");
            Require(blocks.Any(block => block.Block.Kind == TranscriptBlockKind.Card), "The document with its oversize tables does not hold the paragraphs the renderer counted: nothing was placed behind the words.");

            // A line break inside a paragraph leaves the box as a line end, not as the U+000B RichEdit keeps it as.
            Draw("break-fixture", "line one  \nline two\\\nline three");
            var held = "line one" + TranscriptRtf.LineBreak + "line two" + TranscriptRtf.LineBreak + "line three"; var broken = plain.IndexOf(held, StringComparison.Ordinal);
            Require(broken >= 0, "A hard break is expected in the document as U+000B inside its paragraph; the document reads: " + string.Concat(plain.Select(c => c < ' ' ? "<" + ((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + ">" : c.ToString())));
            View.Document.Selection.SetRange(broken, broken + held.Length);
            Require(SelectedWords() == "line one\rline two\rline three" && Text.Contains("line one\rline two\rline three\r", StringComparison.Ordinal) && !Text.Contains(TranscriptRtf.LineBreak),
                "A line break inside a paragraph must be copied as a line end; the selection reads: " + string.Concat(SelectedWords().Select(c => c < ' ' ? "<" + ((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + ">" : c.ToString())));

            // Text that is no valid UTF-16 (a surrogate without its pair) is drawn like any other: nothing an entry holds may end a refresh.
            Draw("surrogate-fixture", "{ \"lone\": \"" + (char)0xD83D + "\" }");
            Require(plain.Contains("lone", StringComparison.Ordinal), "An entry with a surrogate that has no pair was not drawn.");

            // One reply with hundreds of blocks in its card: wherever the box is scrolled, the painted layer holds every block
            // that reaches into the stretch it covers (the same ones measuring them all finds), and measures only a few to do it.
            Draw("long-fixture", string.Join("\n\n", Enumerable.Range(0, 150).Select(row => "paragraph " + row + "\n\n```\ncode " + row + "\n```\n\n> quote " + row)));
            var viewer = scroller ?? throw new InvalidOperationException("The transcript's painted layer is not attached to its box.");
            // The drawing's own scroll to the newest line runs first.
            await Task.Delay(80);
            Require(blocks.Count > 300 && viewer.ScrollableHeight > 2 * viewer.ViewportHeight, $"The long reply did not make a scrolling document of blocks: {blocks.Count} blocks, {viewer.ScrollableHeight:F0} to scroll.");
            var looks = new List<string>();
            foreach (var place in new[] { 0, 0.5, 1 })
            {
                var offset = Math.Round(viewer.ScrollableHeight * place);
                viewer.ChangeView(null, offset, null, true);
                for (var wait = 0; Math.Abs(viewer.VerticalOffset - offset) > 1 && wait < 200; wait++) await Task.Delay(20);
                RemeasureBackdrop();
                var looked = measured.Count(edges => edges is not null); var top = View.Padding.Top;
                var painted = layer.Children.OfType<Border>().Select(shape => (Top: Canvas.GetTop(shape) - top, shape.Height)).Where(shape => shape.Top + shape.Height > paintedFrom && shape.Top < paintedTo).ToList();
                var reaching = Enumerable.Range(0, blocks.Count).Select(Measure).Where(edges => edges.Bottom > paintedFrom && edges.Top < paintedTo).ToList();
                Require(Math.Abs(viewer.VerticalOffset - offset) <= 1 && reaching.Count > 4 && painted.Count == reaching.Count && painted.Zip(reaching).All(pair => Math.Abs(pair.First.Top - pair.Second.Top) < 0.5 && Math.Abs(pair.First.Height - (pair.Second.Bottom - pair.Second.Top)) < 0.5),
                    $"Scrolled to {viewer.VerticalOffset:F0} (asked {offset:F0} of {viewer.ScrollableHeight:F0}), the layer paints {painted.Count} blocks where {reaching.Count} reach into {paintedFrom:F0}..{paintedTo:F0}.");
                Require(looked < blocks.Count / 3, $"Scrolled to {offset:F0}, painting measured {looked} of the reply's {blocks.Count} blocks: the ones outside the painted stretch were to be left alone.");
                looks.Add($"{looked} of {blocks.Count} at {offset:F0} ({painted.Count} painted)");
            }
            return new()
            {
                ["exactCodeCopy"] = true, ["nativeToolToggle"] = true, ["unknownActionRefused"] = true, ["referencesDrawn"] = true,
                ["codeBlockRunsToItsLineEnd"] = true, ["menuOffersThePressedBlock"] = true, ["windowsPathsKeepBackslashes"] = true, ["tableUnderTextLine"] = true,
                ["oversizeTableAsSourceLines"] = true, ["lineBreakCopiedAsLineEnd"] = true, ["unpairedSurrogateDrawn"] = true, ["onlyNearbyBlocksMeasured"] = true,
                ["contextRequestOrder"] = string.Join(" ", order), ["blocksMeasured"] = string.Join("; ", looks),
            };
        }
        finally
        {
            expandedTools.Clear(); expandedTools.UnionWith(previousExpanded);
            if (previous is { } state) Update(state.Session, state.Light);
        }
    }
}
