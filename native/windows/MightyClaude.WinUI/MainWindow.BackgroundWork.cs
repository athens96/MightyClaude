using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>The background strip above the composer (M/BackgroundWorkViews.swift BackgroundWorkStrip).</summary>
        private StackPanel? backgroundHost;
        private string? backgroundKey;
        private bool backgroundOpen;
        /// <summary>The elapsed texts on screen: the strip's and the guided panel's task list, ticked once a second.</summary>
        private readonly List<(TextBlock Text, StylePresentation.TaskRow Task)> stripElapsed = [], guidedElapsed = [];
        private DispatcherTimer? backgroundTicker;

        /// <summary>
        /// The pane's background agents outside a style that draws its own list: in any view once the turn is over
        /// and they still run, and in the Mighty view all along — folded to one line that opens to the task rows.
        /// </summary>
        internal void RefreshBackgroundWork(RunSession pane)
        {
            if (backgroundHost is null) return;
            var mighty = pane.Kind == "claude" && pane.Provider == "claude" && pane.AgentViewMode == "mighty";
            var drawsTasks = mighty && activeStyle?.Evaluator.DrawsTasks == true;
            var work = pane.BackgroundWork;
            var shows = pane.Kind == "claude" && PlanCardSupport.ShowsBackgroundStrip(work, mighty, drawsTasks);
            var key = shows ? string.Join("|", work!.Tasks.Select(t => t.Id + ":" + t.Status + ":" + t.Description)) + "|" + work.TurnEnded + "|" + backgroundOpen + "|" + Locale.LanguagePreference + "|" + owner.service.Snapshot.Theme : "";
            if (key == backgroundKey) return;
            backgroundKey = key;
            backgroundHost.Children.Clear(); ForgetElapsed(stripElapsed);
            backgroundHost.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (!shows) return;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var count = work!.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var face = new Grid { ColumnSpacing = 6 };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) face.ColumnDefinitions.Add(new() { Width = width });
            face.Children.Add(ComposerGlyph.Icon("", 11).Ink(b.Brush(DesignToken.Accent)).View);
            var title = new TextBlock { Text = Locale.Get("plan.background.listTitle", new Dictionary<string, string> { ["count"] = count }), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); face.Children.Add(title);
            var summary = new TextBlock { Text = PlanCardSupport.BackgroundSummary(work), FontSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(summary, 2); face.Children.Add(summary);
            var chevron = (backgroundOpen ? ComposerGlyph.ChevronDown(8, 1.2) : ComposerGlyph.ChevronRight(8, 1.2)).Ink(ink2);
            Grid.SetColumn(chevron.View, 3); face.Children.Add(chevron.View);
            var fold = FoldButton(face, Locale.Get(backgroundOpen ? "plan.background.hide" : "plan.background.show"));
            fold.IsChecked = backgroundOpen;
            AutomationProperties.SetAutomationId(fold, "background-work-" + id);
            fold.Click += (_, _) => { backgroundOpen = !backgroundOpen; RefreshBackgroundWork(Session); };
            var strip = new StackPanel { Spacing = 6 };
            strip.Children.Add(fold);
            if (backgroundOpen) strip.Children.Add(BackgroundRows(PlanCardSupport.BackgroundTasks(work), stripElapsed));
            backgroundHost.Children.Add(new Border
            {
                Child = strip, Padding = new Thickness(12 - DesignMetrics.Stroke.Line, 6 - DesignMetrics.Stroke.Line, 12 - DesignMetrics.Stroke.Line, 6 - DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.Card), BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
            });
        }

        /// <summary>
        /// Background tasks, one row each (M/BackgroundWorkViews.swift BackgroundTaskRows): a status dot, what the task is
        /// doing, and its kind · status · elapsed time. The elapsed text is kept in <paramref name="elapsed"/> and ticks.
        /// </summary>
        private StackPanel BackgroundRows(IReadOnlyList<StylePresentation.TaskRow> tasks, List<(TextBlock Text, StylePresentation.TaskRow Task)> elapsed)
        {
            var b = owner.brushes; var rows = new StackPanel { Spacing = 3 };
            AutomationProperties.SetAutomationId(rows, "background-tasks-" + id);
            foreach (var task in tasks)
            {
                var row = new Grid { ColumnSpacing = 6 };
                foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) row.ColumnDefinitions.Add(new() { Width = width });
                var tone = task.Status switch { "running" => DesignToken.Accent, "completed" => DesignToken.Done, "failed" => DesignToken.Err, _ => DesignToken.Ink2 };
                row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = b.Brush(tone), VerticalAlignment = VerticalAlignment.Center });
                var text = new TextBlock { Text = task.Text, FontSize = 11, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, 1); row.Children.Add(text);
                var detail = new TextBlock { Text = TaskDetail(task, DateTimeOffset.UtcNow), FontSize = 10, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(detail, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
                Grid.SetColumn(detail, 2); row.Children.Add(detail);
                AutomationProperties.SetName(row, task.Text + " · " + detail.Text);
                rows.Children.Add(row);
                elapsed.Add((detail, task));
            }
            if (tasks.Any(t => t.Running))
            {
                StartBackgroundTicker();
                // Rows that load again (a view switched back) restart the clock the last tick stopped.
                rows.Loaded += (_, _) => { if (!owner.closing) StartBackgroundTicker(); };
            }
            return rows;
        }

        /// <summary>The open background strip, for the palette walk (MainWindow.PaletteDesignSmoke.cs).</summary>
        internal StackPanel? BackgroundHostForSmoke => backgroundHost;
        /// <summary>The opened strip's task rows are on screen.</summary>
        internal bool BackgroundRowsLoadedForSmoke => FindById<StackPanel>(backgroundHost, "background-tasks-" + id) is { IsLoaded: true };

        /// <summary>Gives the pane background work whose turn is over and opens the strip, for the palette walk; the design restore puts it back.</summary>
        internal async Task OpenBackgroundStripForSmoke()
        {
            backgroundOpen = true;
            var work = new BackgroundWork([
                new BackgroundTask("palette-bg-1", "agent", "palette background agent", Wire.Now()),
                new BackgroundTask("palette-bg-2", "shell", "palette background shell", Wire.Now(), "failed", EndedAt: Wire.Now()),
            ], TurnEnded: true);
            await Change(p => p with { BackgroundWork = work }); Refresh();
        }

        private static string TaskDetail(StylePresentation.TaskRow task, DateTimeOffset now) =>
            string.Join(" · ", new[] { task.KindTitle, task.StatusTitle, task.Elapsed(now) }.Where(s => s.Length > 0));

        private void ForgetElapsed(List<(TextBlock Text, StylePresentation.TaskRow Task)> elapsed) => elapsed.Clear();

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
            foreach (var (text, task) in stripElapsed.Concat(guidedElapsed))
            {
                if (!task.Running || !text.IsLoaded) continue;
                live = true; text.Text = TaskDetail(task, now);
            }
            if (!live) backgroundTicker?.Stop();
        }
    }
}
