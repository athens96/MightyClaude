using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The background work line above every agent pane's composer is drawn (null, older state, means shown). Settings →
    /// General → Display, the line's hide button, the pane's … menu and the composer's toggle all set this one saved value (M/AppStore.swift showsBackgroundWork).
    /// </summary>
    private bool ShowsBackgroundWork => service.Snapshot.ShowsBackgroundWork != false;

    /// <summary>The line is open on its task rows (null means folded); saved, so it opens the same way next time.</summary>
    private bool BackgroundWorkOpen => service.Snapshot.BackgroundWorkOpen == true;

    /// <summary>Shows or hides the line in every pane now; the returned task is the save.</summary>
    private Task SetShowsBackgroundWork(bool shown)
    {
        if (ShowsBackgroundWork == shown) return Task.CompletedTask;
        var save = service.UpdateAsync(s => s with { ShowsBackgroundWork = shown });
        RefreshBackgroundWorkLines();
        return save;
    }

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
        private readonly List<(TextBlock Text, StylePresentation.TaskRow Task)> stripElapsed = [], guidedElapsed = [];
        private DispatcherTimer? backgroundTicker;

        /// <summary>
        /// The composer's background-work toggle (M/SessionPaneView.swift backgroundWorkToggle): the status-line toggle's
        /// 16pt-wide, toolbar-high frame, on Claude panes alone. The Mac's <c>rectangle.stack</c> and <c>rectangle.stack.fill</c>
        /// become the list mark the line itself starts with (Segoe's BulletedList, one shape in both states), ink2 off and
        /// accent on, as the status-line toggle.
        /// </summary>
        private readonly Microsoft.UI.Xaml.Controls.Primitives.ToggleButton backgroundWorkToggle = new()
        {
            Width = 16, Height = DesignMetrics.Layout.Toolbar, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow),
        };
        private readonly FontIcon backgroundWorkGlyph = new() { Glyph = "\uE8FD", TextSize = 12, IsHitTestVisible = false };

        /// <summary>
        /// Adds the background-work toggle to the composer's right cluster, between the context ring and the status-line
        /// toggle. It sets the saved value the line's eye, the pane's … menu and Settings set, for every pane at once, so a
        /// hidden line can always be brought back from the composer.
        /// </summary>
        private void InitializeBackgroundWorkToggle(StackPanel cluster)
        {
            DressComposerToggle(backgroundWorkToggle, backgroundWorkGlyph);
            AutomationProperties.SetName(backgroundWorkToggle, Locale.Get("composer.backgroundWork.name"));
            AutomationProperties.SetAutomationId(backgroundWorkToggle, "background-work-toggle-" + id);
            backgroundWorkToggle.IsChecked = owner.ShowsBackgroundWork;
            UpdateBackgroundWorkTip();
            // A press (pointer, keys or the UIA Toggle pattern) flips the check and the saved value follows it. A refresh
            // only copies the saved value onto the check, so the two already agree and nothing is saved.
            void Flipped()
            {
                UpdateBackgroundWorkTip();
                var on = backgroundWorkToggle.IsChecked == true;
                if (on != owner.ShowsBackgroundWork) _ = owner.Act(() => owner.SetShowsBackgroundWork(on));
            }
            backgroundWorkToggle.Checked += (_, _) => Flipped();
            backgroundWorkToggle.Unchecked += (_, _) => Flipped();
            cluster.Children.Add(backgroundWorkToggle);
        }

        /// <summary>The help says what a press does next (M/SessionPaneView.swift backgroundWorkToggle).</summary>
        private void UpdateBackgroundWorkTip() =>
            ToolTipService.SetToolTip(backgroundWorkToggle, Locale.Get(backgroundWorkToggle.IsChecked == true ? "composer.backgroundWork.hide" : "composer.backgroundWork.show"));

        /// <summary>
        /// The pane's background agents outside a style that draws its own list: in any view once the turn is over
        /// and they still run, and in the Mighty view all along — folded to one line that opens to the task rows.
        /// </summary>
        internal void RefreshBackgroundWork(RunSession pane)
        {
            if (backgroundHost is null) return;
            // The composer's toggle shows the saved value, on Claude panes alone (M/SessionPaneView.swift showsBackgroundWorkToggle).
            backgroundWorkToggle.IsChecked = owner.ShowsBackgroundWork;
            backgroundWorkToggle.Visibility = pane.Kind == "claude" && pane.Provider == "claude" ? Visibility.Visible : Visibility.Collapsed;
            var mighty = pane.Kind == "claude" && pane.Provider == "claude" && pane.AgentViewMode == "mighty";
            var drawsTasks = mighty && activeStyle?.Evaluator.DrawsTasks == true;
            var work = pane.BackgroundWork;
            var shows = pane.Kind == "claude" && PlanCardSupport.ShowsBackgroundStrip(work, mighty, drawsTasks, owner.ShowsBackgroundWork);
            var backgroundOpen = owner.BackgroundWorkOpen;
            var key = shows ? string.Join("|", work!.Tasks.Select(t => t.Id + ":" + t.Status + ":" + t.Description)) + "|" + work.TurnEnded + "|" + backgroundOpen + "|" + Locale.LanguagePreference + "|" + owner.service.Snapshot.Theme : "";
            if (key == backgroundKey) return;
            backgroundKey = key;
            backgroundHost.Children.Clear(); ForgetElapsed(stripElapsed);
            backgroundHost.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (!shows) return;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var count = work!.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) face.ColumnDefinitions.Add(new() { Width = width });
            face.Children.Add(ComposerGlyph.Icon("", 11).Ink(b.Brush(DesignToken.Accent)).View);
            var title = new TextBlock { Text = Locale.Get("plan.background.listTitle", new Dictionary<string, string> { ["count"] = count }), TextSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); face.Children.Add(title);
            var summary = new TextBlock { Text = PlanCardSupport.BackgroundSummary(work), TextSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(summary, 2); face.Children.Add(summary);
            var chevron = (backgroundOpen ? ComposerGlyph.ChevronDown(8, 1.2) : ComposerGlyph.ChevronRight(8, 1.2)).Ink(ink2);
            Grid.SetColumn(chevron.View, 3); face.Children.Add(chevron.View);
            var fold = FoldButton(face, Locale.Get(backgroundOpen ? "plan.background.hide" : "plan.background.show"));
            fold.IsChecked = backgroundOpen;
            AutomationProperties.SetAutomationId(fold, "background-work-" + id);
            fold.Click += (_, _) => _ = ToggleBackgroundWorkOpen();
            // The eye on the line's trailing side hides it everywhere; Settings, the pane's … menu and the composer's toggle bring it back.
            var hide = new Button
            {
                Width = 22, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "\uED1A", TextSize = 11 }, VerticalAlignment = VerticalAlignment.Center,
            };
            owner.PaintPlainButton(hide, b.Transparent, b.Subtle, ink: ink2);
            var hideName = Locale.Get("plan.background.hideStrip");
            AutomationProperties.SetName(hide, hideName); AutomationProperties.SetAutomationId(hide, "background-work-hide-" + id);
            ToolTipService.SetToolTip(hide, Locale.Get("plan.background.hideStripHelp"));
            hide.Click += (_, _) => _ = HideBackgroundWork();
            var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(fold);
            Grid.SetColumn(hide, 1); line.Children.Add(hide);
            var strip = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            strip.Children.Add(line);
            if (backgroundOpen) strip.Children.Add(BackgroundRows(PlanCardSupport.BackgroundTasks(work), stripElapsed));
            backgroundHost.Children.Add(new Border
            {
                Child = strip, Padding = new Thickness(DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Sm - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Sm - DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.Card), BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
            });
        }

        /// <summary>The line's own fold: opens or folds it in every pane, and saves that.</summary>
        private Task ToggleBackgroundWorkOpen() => owner.Act(() => owner.SetBackgroundWorkOpen(!owner.BackgroundWorkOpen));

        /// <summary>The line's eye button: hides the line in every pane, and saves that.</summary>
        private Task HideBackgroundWork() => owner.Act(() => owner.SetShowsBackgroundWork(false));

        /// <summary>The pane's … menu item: shows or hides the line, and saves that.</summary>
        private Task ToggleBackgroundWorkShown() => owner.Act(() => owner.SetShowsBackgroundWork(!owner.ShowsBackgroundWork));

        /// <summary>
        /// Background tasks, one row each (M/BackgroundWorkViews.swift BackgroundTaskRows): a status dot, what the task is
        /// doing, and its kind · status · elapsed time. The elapsed text is kept in <paramref name="elapsed"/> and ticks.
        /// </summary>
        private StackPanel BackgroundRows(IReadOnlyList<StylePresentation.TaskRow> tasks, List<(TextBlock Text, StylePresentation.TaskRow Task)> elapsed)
        {
            var b = owner.brushes; var rows = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            AutomationProperties.SetAutomationId(rows, "background-tasks-" + id);
            foreach (var task in tasks)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) row.ColumnDefinitions.Add(new() { Width = width });
                var tone = task.Status switch { "running" => DesignToken.Accent, "completed" => DesignToken.Done, "failed" => DesignToken.Err, _ => DesignToken.Ink2 };
                row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = b.Brush(tone), VerticalAlignment = VerticalAlignment.Center });
                var text = new TextBlock { Text = task.Text, TextSize = 11, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, 1); row.Children.Add(text);
                var detail = new TextBlock { Text = TaskDetail(task, DateTimeOffset.UtcNow), TextSize = 10, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
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

        /// <summary>
        /// Gives the pane background work whose turn is over and opens the strip, for the palette walk; the design
        /// restore puts it back. The pane runs meanwhile, as a real one does while its process waits on that work
        /// (the store keeps a task running only while its pane runs).
        /// </summary>
        internal async Task OpenBackgroundStripForSmoke()
        {
            await owner.service.UpdateAsync(s => s with { ShowsBackgroundWork = true, BackgroundWorkOpen = true });
            var work = new BackgroundWork([
                new BackgroundTask("palette-bg-1", "agent", "palette background agent", Wire.Now()),
                new BackgroundTask("palette-bg-2", "shell", "palette background shell", Wire.Now(), "failed", EndedAt: Wire.Now()),
            ], TurnEnded: true);
            await Change(p => p with { Status = "running", BackgroundWork = work }); Refresh();
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
