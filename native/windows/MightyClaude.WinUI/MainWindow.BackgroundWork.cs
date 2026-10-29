using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The background strip above a Claude pane's composer is open on its task rows (null, older state, means folded); saved,
    /// so it opens the same way next time (M/AppStore.swift backgroundWorkOpen). Nothing hides the strip.
    /// </summary>
    private bool BackgroundWorkOpen => service.Snapshot.BackgroundWorkOpen == true;

    /// <summary>Opens or folds the line in every pane now; the returned task is the save.</summary>
    private Task SetBackgroundWorkOpen(bool open)
    {
        if (BackgroundWorkOpen == open) return Task.CompletedTask;
        var save = service.UpdateAsync(s => s with { BackgroundWorkOpen = open });
        RefreshBackgroundWorkLines();
        return save;
    }

    private void RefreshBackgroundWorkLines()
    {
        var sessions = service.Snapshot.Sessions;
        foreach (var (id, view) in views)
            if (sessions.FirstOrDefault(s => s.Id == id) is { } pane) view.RefreshBackgroundWork(pane);
    }

    private sealed partial class PaneView
    {
        /// <summary>The background strip above the composer (M/BackgroundWorkViews.swift BackgroundWorkStrip).</summary>
        private StackPanel? backgroundHost;
        private string? backgroundKey;
        /// <summary>The elapsed texts on screen: the strip's and the guided panel's task list, ticked once a second.</summary>
        private readonly List<(TextBlock Text, PlanCardSupport.BackgroundStripRow Row)> stripElapsed = [], guidedElapsed = [];
        private DispatcherTimer? backgroundTicker;
        /// <summary>The strip rows opened to their command and output lines, by task id; only this pane's strip draws again for them.</summary>
        private readonly HashSet<string> backgroundOpenRows = new(StringComparer.Ordinal);

        /// <summary>
        /// The pane's background work outside a style that draws its own list: in any view once the turn is over, and in the
        /// Mighty view all along, while it runs and after it ended with its report — folded to one line, headed by how many
        /// tasks ended and how many still run, that opens to the task rows (M/BackgroundWorkViews.swift BackgroundWorkStrip).
        /// </summary>
        internal void RefreshBackgroundWork(RunSession pane)
        {
            owner.WebCardsChanged(this);
            if (backgroundHost is null) return;
            var mighty = pane.Kind == "claude" && pane.Provider == "claude" && pane.AgentViewMode == "mighty";
            var drawsTasks = mighty && activeStyle?.Evaluator.DrawsTasks == true;
            var work = pane.BackgroundWork;
            var shows = pane.Kind == "claude" && PlanCardSupport.ShowsBackgroundStrip(work, mighty, drawsTasks);
            var backgroundOpen = owner.BackgroundWorkOpen;
            // The rows meet the newest request's blocks by tool use id (never a notice run's copies): their kind words, command and output lines.
            var rows = shows && backgroundOpen ? PlanCardSupport.BackgroundStripRows(work, MightyGraphSupport.NewestRequestRun(pane.GraphRuns), owner.BackgroundOutputFor(pane.Id)) : [];
            backgroundOpenRows.IntersectWith(rows.Select(r => r.Id).OfType<string>());
            var key = shows ? string.Join("|", work!.Tasks.Select(t => t.Id + ":" + t.Status + ":" + t.Description)) + "|" + string.Join("|", rows.Select(r => r.KindText + ":" + r.Tail
                    + (r.Id is { } rowId && backgroundOpenRows.Contains(rowId) ? ":open:" + r.Command + ":" + string.Join("\n", r.TailLines ?? []) : "")))
                + "|" + work.TurnEnded + "|" + backgroundOpen + "|" + Locale.LanguagePreference + "|" + owner.service.Snapshot.Theme : "";
            if (key == backgroundKey) return;
            backgroundKey = key;
            backgroundHost.Children.Clear(); ForgetElapsed(stripElapsed);
            backgroundHost.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (!shows) return;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var counts = PlanCardSupport.BackgroundCounts(work!);
            var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) face.ColumnDefinitions.Add(new() { Width = width });
            face.Children.Add(ComposerGlyph.Icon("", 11).Ink(b.Brush(DesignToken.Accent)).View);
            var title = new TextBlock { Text = Locale.Get("plan.background.title"), TextSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); face.Children.Add(title);
            var summary = new TextBlock { Text = counts, TextSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(summary, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
            Grid.SetColumn(summary, 2); face.Children.Add(summary);
            var chevron = (backgroundOpen ? ComposerGlyph.ChevronDown(8, 1.2) : ComposerGlyph.ChevronRight(8, 1.2)).Ink(ink2);
            Grid.SetColumn(chevron.View, 3); face.Children.Add(chevron.View);
            var fold = FoldButton(face, Locale.Get(backgroundOpen ? "plan.background.hide" : "plan.background.show"));
            fold.IsChecked = backgroundOpen;
            AutomationProperties.SetAutomationId(fold, "background-work-" + id);
            fold.Click += (_, _) => _ = ToggleBackgroundWorkOpen();
            var strip = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            strip.Children.Add(fold);
            if (backgroundOpen) strip.Children.Add(BackgroundRows(rows, stripElapsed));
            backgroundHost.Children.Add(new Border
            {
                Child = strip, Padding = new Thickness(DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Sm - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Sm - DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.Card), BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
            });
        }

        /// <summary>The line's own fold: opens or folds it in every pane, and saves that.</summary>
        private Task ToggleBackgroundWorkOpen() => owner.Act(() => owner.SetBackgroundWorkOpen(!owner.BackgroundWorkOpen));

        /// <summary>
        /// Background tasks, one row each (M/BackgroundWorkViews.swift BackgroundTaskRows): a status dot, what the task is
        /// doing, and its kind · status · elapsed time. The elapsed text is kept in <paramref name="elapsed"/> and ticks.
        /// </summary>
        private StackPanel BackgroundRows(IReadOnlyList<StylePresentation.TaskRow> tasks, List<(TextBlock Text, PlanCardSupport.BackgroundStripRow Row)> elapsed) =>
            BackgroundRows(tasks.Select(task => new PlanCardSupport.BackgroundStripRow(task)).ToList(), elapsed);

        /// <summary>The strip's rows carry the diagram's kind words and a background run's last output line under its text (M/BackgroundWorkViews.swift BackgroundTaskRows(strip:)).</summary>
        private StackPanel BackgroundRows(IReadOnlyList<PlanCardSupport.BackgroundStripRow> items, List<(TextBlock Text, PlanCardSupport.BackgroundStripRow Row)> elapsed)
        {
            var b = owner.brushes; var rows = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            AutomationProperties.SetAutomationId(rows, "background-tasks-" + id);
            foreach (var item in items)
            {
                var task = item.Task;
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) row.ColumnDefinitions.Add(new() { Width = width });
                var tone = task.Status switch { "running" => DesignToken.Accent, "completed" => DesignToken.Done, "failed" => DesignToken.Err, _ => DesignToken.Ink2 };
                row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = b.Brush(tone), VerticalAlignment = VerticalAlignment.Center });
                var text = new TextBlock { Text = task.Text, TextSize = 11, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, 1); row.Children.Add(text);
                var detail = new TextBlock { Text = item.Detail(DateTimeOffset.UtcNow), TextSize = 10, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(detail, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
                Grid.SetColumn(detail, 2); row.Children.Add(detail);
                AutomationProperties.SetName(row, task.Text + " · " + detail.Text);
                var opened = item.Id is { } openId && item.Opens && backgroundOpenRows.Contains(openId);
                if (opened)
                {
                    // Opened: the command (or the sub-agent's first prompt line) and the run's last output lines (M/BackgroundWorkViews.swift).
                    var more = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                    if (item.Command is { } command)
                        more.Children.Add(new TextBlock { Text = command, TextSize = 10, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true, TextFont = TextArea.Code });
                    foreach (var output in item.TailLines ?? [])
                        more.Children.Add(new TextBlock { Text = output, TextSize = 10, Foreground = b.Brush(DesignToken.Ink2), Opacity = 0.8, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, TextFont = TextArea.Code });
                    Grid.SetColumn(more, 1); Grid.SetColumnSpan(more, 2); Grid.SetRow(more, 1);
                    row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    row.Children.Add(more);
                }
                else if (item.Tail is { } tail)
                {
                    // Under the text, past the dot and its gap: one dim monospace line, cut at its end.
                    var line = new TextBlock
                    {
                        Text = tail, TextSize = 10, Foreground = b.Brush(DesignToken.Ink2), Opacity = 0.8, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                        TextFont = TextArea.Code,
                    };
                    Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, 2); Grid.SetRow(line, 1);
                    row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    row.Children.Add(line);
                }
                if (item.Opens && item.Id is { } rowKey)
                {
                    // A row with more to show opens and folds on a press; only this pane's strip is drawn again.
                    var toggle = new Button { Content = row, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    owner.PaintPlainButton(toggle, b.Transparent, b.Subtle);
                    AutomationProperties.SetName(toggle, task.Text + " · " + detail.Text);
                    AutomationProperties.SetHelpText(toggle, Locale.Get(opened ? "graph.block.collapse" : "graph.block.expand"));
                    toggle.Click += (_, _) =>
                    {
                        if (!backgroundOpenRows.Remove(rowKey)) backgroundOpenRows.Add(rowKey);
                        RefreshBackgroundWork(Session);
                    };
                    rows.Children.Add(toggle);
                }
                else rows.Children.Add(row);
                elapsed.Add((detail, item));
            }
            if (items.Any(t => t.Task.Running))
            {
                StartBackgroundTicker();
                // Rows that load again (a view switched back) restart the clock the last tick stopped.
                rows.Loaded += (_, _) => { if (!owner.closing) StartBackgroundTicker(); };
            }
            return rows;
        }



        private void ForgetElapsed(List<(TextBlock Text, PlanCardSupport.BackgroundStripRow Row)> elapsed) => elapsed.Clear();

        /// <summary>One timer per pane, stopping itself once nothing on screen still runs or the pane has gone.</summary>
        private void StartBackgroundTicker()
        {
            if (backgroundTicker is { IsEnabled: true }) return;
            backgroundTicker ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            backgroundTicker.Tick -= TickBackground; backgroundTicker.Tick += TickBackground;
            backgroundTicker.Start();
        }

        private void TickBackground(object? sender, object e)
        {
            if (owner.closing || !owner.service.Snapshot.Sessions.Any(p => p.Id == id)) { backgroundTicker?.Stop(); return; }
            var now = DateTimeOffset.UtcNow; var live = false;
            foreach (var (text, item) in stripElapsed.Concat(guidedElapsed))
            {
                if (!item.Task.Running || !text.IsLoaded) continue;
                live = true; text.Text = item.Detail(now);
            }
            if (!live) backgroundTicker?.Stop();
        }
    }
}
