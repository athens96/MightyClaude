using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>The next-action rows under the transcript (M/NextActionButtons.swift:15, 42): 5 apart, padding h12 t8 b2 from the pane's edge.</summary>
        private readonly StackPanel nextActionsHost = new() { Spacing = 5, Visibility = Visibility.Collapsed, Margin = new(12, 8, 12, 2) };
        /// <summary>A next-action row's corner (M/NextActionButtons.swift:31).</summary>
        private const double NextActionRadius = 12;
        private (string Id, string Text, string Language)? nextActionsKey;
        private void RefreshNextActions(RunSession pane, bool busy)
        {
            if (pane.Kind != "claude" || busy) { nextActionsHost.Visibility = Visibility.Collapsed; return; }
            var reply = pane.Logs.LastOrDefault(entry => entry.Kind is "user" or "assistant");
            if (reply?.Kind != "assistant") { nextActionsHost.Visibility = Visibility.Collapsed; return; }
            var key = (reply.Id, reply.Text, Locale.LanguagePreference);
            if (nextActionsKey == key) { nextActionsHost.Visibility = nextActionsHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed; return; }
            nextActionsKey = key; nextActionsHost.Children.Clear();
            var actions = NextActions.Parse(reply.Text);
            nextActionsHost.Visibility = actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (actions.Count == 0) return;
            AutomationProperties.SetAutomationId(nextActionsHost, "next-actions-" + id);
            AutomationProperties.SetName(nextActionsHost, Locale.Get("pane.nextActions.label"));
            // The heading in 11.5 bold ink2; then each option as a card row with a 1pt line at radius 12, padding h14 v9: its
            // words in 13 semibold ink (two lines) and a bold accent arrow (M/NextActionButtons.swift:16-34).
            var b = owner.brushes;
            nextActionsHost.Children.Add(new TextBlock { Text = Locale.Get("pane.nextActions.label"), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = b.Brush(DesignToken.Ink2), Margin = new(3, 0, 3, 0) });
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index]; var entryId = reply.Id;
                var button = Button(action.DisplayLabel, () => FillNextAction(entryId, action));
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Padding = new(14 - DesignMetrics.Stroke.Line, 9 - DesignMetrics.Stroke.Line, 14 - DesignMetrics.Stroke.Line, 9 - DesignMetrics.Stroke.Line); button.MinHeight = 0; button.CornerRadius = new(NextActionRadius); button.BorderThickness = new(DesignMetrics.Stroke.Line);
                owner.PaintPlainButton(button, b.Brush(DesignToken.Card), b.Subtle, b.Brush(DesignToken.Line), b.Brush(DesignToken.Ink));
                var row = new Grid { ColumnSpacing = 10 }; row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = action.DisplayLabel, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var arrow = ComposerGlyph.ArrowRight().Ink(b.Brush(DesignToken.Accent)).View;
                Grid.SetColumn(arrow, 1); row.Children.Add(arrow); button.Content = row;
                AutomationProperties.SetAutomationId(button, "next-action-" + id + "-" + index);
                AutomationProperties.SetName(button, action.DisplayLabel); AutomationProperties.SetHelpText(button, Locale.Get("pane.nextActions.fillHint")); ToolTipService.SetToolTip(button, action.DisplayLabel);
                nextActionsHost.Children.Add(button);
            }
        }

        private Task FillNextAction(string entryId, NextAction action)
        {
            if (!QueuePaneAlive || owner.dialogOpen || composingInput || starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session)) return Task.CompletedTask;
            // A retained button cannot apply an option from an older reply.
            if (NextActions.Latest(Session.Logs) is not { } latest || latest.EntryId != entryId || !latest.Actions.Contains(action)) return Task.CompletedTask;
            var insertion = NextActions.Insertion(input.Text, action.Fill);
            input.Select(insertion.ReplacesDraft ? 0 : input.Text.Length, insertion.ReplacesDraft ? input.Text.Length : 0);
            input.SelectedText = insertion.Text; // One native edit; retains undo and the current draft.
            input.Select(input.Text.Length, 0); input.Focus(FocusState.Programmatic);
            return Task.CompletedTask;
        }

        internal async Task<Dictionary<string, object?>> RunNextActionsSmoke()
        {
            var original = Session; var draft = input.Text; var sendCount = 0; var previousStart = owner.smokeStart;
            owner.smokeStart = _ => { sendCount++; return Task.CompletedTask; };
            try
            {
                var reply = new LogEntry("next-actions-fixture", "assistant", "◆ done → next: `ooo run` or Review", Wire.Now());
                await Change(p => p with { Status = "completed", Logs = [reply] }); Refresh();
                await WaitUI(() => nextActionsHost.IsLoaded && nextActionsHost.ActualHeight > 0 && nextActionsHost.Children.OfType<Button>().Count() == 2);
                input.Text = "\uD55C\uAE00 draft"; await WaitUI(() => Session.Draft == input.Text);
                var button = nextActionsHost.Children.OfType<Button>().First(); await WaitUI(() => button.IsLoaded && button.ActualWidth > 0);
                await SettleDesktopCapture(owner.root);
                await owner.CaptureSmoke(Path.Combine(owner.options.ProfileDirectory!, "smoke-next-actions.png"));
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await WaitUI(() => Session.Draft.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') == "\uD55C\uAE00 draft\nooo run");
                Require(sendCount == 0 && ReferenceEquals(input, VisualChildren(Container).OfType<TextBox>().First(control => ReferenceEquals(control, input))), "Next action must only fill the existing native editor.");
                Require(input.SelectionStart == input.Text.Length, "Next action must place caret after preserved draft.");
                await Change(p => p with { Status = "running" }); Refresh(); Require(nextActionsHost.Visibility == Visibility.Collapsed, "Suggestions must hide while running.");
                await Change(p => p with { Status = "completed", Logs = [reply, new("new-user", "user", "next", Wire.Now())] }); Refresh();
                var before = input.Text; await FillNextAction(reply.Id, new("`ooo run`", "ooo run"));
                Require(nextActionsHost.Visibility == Visibility.Collapsed && input.Text == before, "Old reply suggestions must not mutate a newer conversation.");
                return new() { ["sharedActionsRendered"] = true, ["nativeDraftPreserved"] = true, ["neverAutoSends"] = true, ["runningAndStaleRepliesHidden"] = true };
            }
            finally
            {
                owner.smokeStart = previousStart; await Change(_ => original); updating = true; input.Text = draft; updating = false; Refresh();
            }
        }
    }
}
