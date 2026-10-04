using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MightyClaude.WinUI;

internal sealed record TranscriptAction(string Kind, string Value, AgentImageRef? Image = null);

internal sealed partial class AgentTranscript
{
    private Dictionary<string, TranscriptAction> actions = [];
    private readonly HashSet<string> expandedTools = [];
    internal Func<TranscriptAction, Task>? OpenImage { get; set; }
    private void InitializeTranscriptActions()
    {
        var menu = new MenuFlyout(); var copy = new MenuFlyoutItem { Text = Locale.Get("pane.copyButton") };
        copy.Click += (_, _) => CopySelection(); menu.Items.Add(copy); View.ContextFlyout = menu;
        View.PreviewKeyDown += async (_, args) =>
        {
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (args.Key == Windows.System.VirtualKey.C && control)
            { if (View.Document.Selection.Length > 0) { args.Handled = true; CopySelection(); } }
            else if (args.Key == Windows.System.VirtualKey.Enter && control)
            {
                var position = View.Document.Selection.StartPosition;
                if (position < Text.Length && await InvokeTranscriptAction(View.Document.GetRange(position, position + 1).Link)) args.Handled = true;
            }
        };
    }
    private void CopySelection()
    {
        View.Document.Selection.GetText(TextGetOptions.None, out var text);
        if (text.Length > 0) CopyText(text);
    }
    private static void CopyText(string text)
    { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
    private async Task<bool> InvokeTranscriptAction(string link)
    {
        if (!Uri.TryCreate(link.Trim('"', '\ufddf'), UriKind.Absolute, out var uri) || uri.Scheme != "mighty-action" || !actions.TryGetValue(uri.Host, out var action)) return false;
        switch (action.Kind)
        {
            case "copy": CopyText(action.Value); break;
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
            const string exactCode = "const \uD55C\uAE00 = `\uADF8\uB300\uB85C \uBCF5\uC0AC`;";
            var fixture = new RunSession { Logs = [
                new("copy-fixture", "assistant", "```js\n" + exactCode + "\n```", Wire.Now()),
                new("tool-fixture", "activity", "read", Wire.Now(), "claude", new("tool", "claude", "read", "completed", "Read README.md", Output: "hidden tool detail")),
            ] };
            Update(fixture, false);
            var code = actions.Single(pair => pair.Value.Kind == "copy");
            if (code.Value.Value != exactCode) throw new InvalidOperationException("Code-copy action changed the original Korean source.");
            var show = Locale.Get("transcript.tool.showDetail"); var at = Text.IndexOf(show, StringComparison.Ordinal);
            if (at < 0 || Text.Contains("hidden tool detail", StringComparison.Ordinal)) throw new InvalidOperationException("Tool details are not initially collapsed.");
            var link = View.Document.GetRange(at, at + show.Length).Link;
            if (!await InvokeTranscriptAction(link) || !Text.Contains("hidden tool detail", StringComparison.Ordinal)) throw new InvalidOperationException("Native tool hyperlink did not expand its own detail.");
            if (await InvokeTranscriptAction("\"mighty-action://unregistered\"")) throw new InvalidOperationException("An unregistered output URL became an application action.");
            return new() { ["exactCodeCopy"] = true, ["nativeToolToggle"] = true, ["unknownActionRefused"] = true };
        }
        finally
        {
            expandedTools.Clear(); expandedTools.UnionWith(previousExpanded);
            if (previous is { } state) Update(state.Session, state.Light);
        }
    }
}
