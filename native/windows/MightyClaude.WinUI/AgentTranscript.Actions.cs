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

}
