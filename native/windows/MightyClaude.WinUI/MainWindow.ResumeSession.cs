using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // ── Add pane → resume an earlier session (macOS ResumeSessionSheet, docs/session-resume.md) ──

    /// One look-up at a time: picking Claude or Codex again while it runs does nothing.
    private bool resumeLookup;
    /// The smoke answers the choice and the list instead of a person.
    private Func<ContentDialog, Task<ContentDialogResult>>? smokeResumeDialog;

    private ResumableSessionQuery ResumeQuery(string workspacePath)
    {
        return new ResumableSessionQuery(workspacePath)
        {
            Environment = PaneView.HistoryEnvironment(),
            Home = PaneView.HistoryHomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Excluding = ResumableSessions.InUse(service.Snapshot.Sessions),
            Known = service.KnownSessions(),
        };
    }

    private async Task<ContentDialogResult> ShowResumeDialog(ContentDialog dialog)
    {
        // One sheet at a time: Settings or another sheet opened while the folder's sessions were being read keeps the screen, and this add is let go.
        if (dialogOpen) return ContentDialogResult.None;
        dialogOpen = true;
        try
        {
            var shown = smokeResumeDialog is { } driver ? await driver(dialog) : await dialog.ShowAsync();
            // The choice sheet draws its own buttons, which leave what was picked on the sheet before they close it.
            return dialog.Tag is ContentDialogResult picked ? picked : shown;
        }
        finally { dialogOpen = false; }
    }

    /// <summary>
    /// "Add pane" for an agent: Claude and Codex first look for a session this
    /// folder recorded (the head of each record only, nested runs hidden, open
    /// panes' sessions left out). With one, the user picks Start new or Resume…;
    /// without one the pane is made at once. Gemini never asks, and Ctrl+N
    /// (AddPaneFromShortcut) never comes here. It runs under Act: a menu click
    /// awaits it from an async void handler, so no exception may escape.
    /// </summary>
    private Task AddAgentPane(string provider, string? groupId) => Act(async () =>
    {
        if (!AddPaneMenu.MayAskResume(provider, fromMenu: true) || service.Snapshot.ActiveWorkspaceId is not { } workspaceId
            || service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is not { } workspace)
        {
            await AddPane("claude", provider, groupId);
            return;
        }
        if (resumeLookup) return;
        resumeLookup = true;
        ResumableSessionListing probe;
        try
        {
            var query = ResumeQuery(workspace.Path) with { HeadOnly = true, MaximumSessions = 1 };
            probe = await Task.Run(() => ResumableSessions.Listing(query, provider));
        }
        // Any failure reading the records (a malformed file, a race with the CLI) means nothing to offer.
        catch (Exception) { probe = ResumableSessionListing.Empty; }
        finally { resumeLookup = false; }
        // Moved to another workspace meanwhile: the answer is about another folder.
        if (service.Snapshot.ActiveWorkspaceId != workspaceId) return;
        if (probe.Items.Count == 0) { await AddPane("claude", provider, groupId); return; }
        var picked = await ShowResumeDialog(ResumeChoiceDialog(provider));
        if (picked == ContentDialogResult.Primary) await AddPane("claude", provider, groupId);
        else if (picked == ContentDialogResult.Secondary) await PickResumedSession(provider, workspace, groupId);
    });

    /// <summary>
    /// Start new / Resume… (M/ResumeSessionSheet.swift:13-50): a 400-wide sheet, padding 20, its two rows 16
    /// apart. First the agent's mark (18, in a 22-wide column, 1 down) 10 from the 13pt bold title over the 12pt
    /// <c>ink2</c> sentence, 4 apart; then the buttons 8 apart: Cancel (Esc) at the leading edge, and after a
    /// spacer Resume… and Start new (the default, Enter). What was picked is left in the sheet's <c>Tag</c>.
    /// </summary>
    private ContentDialog ResumeChoiceDialog(string provider)
    {
        var agent = ProviderMark.Label(provider);
        var title = Locale.Get("resume.choice.title", new Dictionary<string, string> { ["provider"] = agent });
        var top = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
        top.ColumnDefinitions.Add(new() { Width = new(ResumeMarkColumn) }); top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var mark = ProviderMarkView.Create(provider, ResumeMarkSize * ProviderIconScale);
        mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 1, 0, 0);
        top.Children.Add(mark);
        var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        words.Children.Add(SheetTitle(title));
        words.Children.Add(new TextBlock { Text = Locale.Get("resume.choice.message", new Dictionary<string, string> { ["provider"] = agent }), FontSize = 12, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(words, 1); top.Children.Add(words);

        var cancel = PushButton(Locale.Get("resume.cancel")); var resume = PushButton(Locale.Get("resume.choice.resume")); var startNew = PushButton(Locale.Get("resume.choice.startNew"), prominent: true);
        AutomationProperties.SetAutomationId(cancel, "add-pane-choice-cancel"); AutomationProperties.SetAutomationId(resume, "add-pane-choice-resume"); AutomationProperties.SetAutomationId(startNew, "add-pane-choice-new");
        var buttons = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) buttons.ColumnDefinitions.Add(new() { Width = width });
        buttons.Children.Add(cancel); Grid.SetColumn(resume, 2); buttons.Children.Add(resume); Grid.SetColumn(startNew, 3); buttons.Children.Add(startNew);

        var body = new StackPanel { Spacing = DesignMetrics.Spacing.Lg };
        body.Children.Add(top); body.Children.Add(buttons);
        var choice = StyledDialog(new ContentDialog { Content = body, XamlRoot = root.XamlRoot }, ChoiceSheetWidth, bare: true);
        AutomationProperties.SetAutomationId(choice, "add-pane-choice"); AutomationProperties.SetName(choice, title);
        void Pick(ContentDialogResult picked) { choice.Tag = picked; choice.Hide(); }
        cancel.Click += (_, _) => Pick(ContentDialogResult.None); resume.Click += (_, _) => Pick(ContentDialogResult.Secondary); startNew.Click += (_, _) => Pick(ContentDialogResult.Primary);
        // Start new is the default: first in the tab order, it takes the focus as the sheet opens (a dialog gives it to
        // its first control), so Enter starts a new session; Esc closes the sheet with nothing picked.
        startNew.TabIndex = 0; cancel.TabIndex = 1; resume.TabIndex = 2;
        choice.Opened += (_, _) => { choice.Tag = null; startNew.Focus(FocusState.Programmatic); };
        return choice;
    }

    /// <summary>The agent mark on the choice sheet and the column it stands in (M/ResumeSessionSheet.swift:17), and the list's marks (:78, :148).</summary>
    internal const double ResumeMarkSize = 18, ResumeMarkColumn = 22, ResumeRowMarkSize = 14, ResumeRowMarkColumn = 18;

    /// <summary>The list sheet's parts the smoke drives: the rows, the hidden count, the search, the show-all check box and its words, and the pick a click makes.</summary>
    internal sealed record ResumeSheetParts(ListView List, TextBlock Hidden, TextBox Search, CheckBox ShowAll, TextBlock ShowAllWords, Action<ResumableSession> Choose);

    /// <summary>
    /// The list of one agent's sessions for the folder, newest first: title (the
    /// first request), when, how many requests and the model. Nested Ouroboros
    /// runs are hidden unless Show all sessions is on; a session already open in a
    /// pane never shows. Picking one adds a pane that continues it.
    /// </summary>
    private async Task PickResumedSession(string provider, Workspace workspace, string? groupId)
    {
        // The list sheet (M/ResumeSessionSheet.swift:74-112): 560 × 520, padding 20, its five rows 12 apart: the
        // title beside the agent's mark over the folder's path; the title search; the sessions; Show all sessions
        // with the hidden count; the note with Cancel at its end.
        var ink2 = brushes.Brush(DesignToken.Ink2); var tertiary = brushes.Tertiary;
        var content = new Grid { RowSpacing = DesignMetrics.Spacing.Md };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) content.RowDefinitions.Add(new() { Height = height });
        void Place(FrameworkElement part, int row) { Grid.SetRow(part, row); content.Children.Add(part); }

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        titleRow.Children.Add(ProviderMarkView.Create(provider, ResumeRowMarkSize * ProviderIconScale)); titleRow.Children.Add(SheetTitle(Locale.Get("resume.title")));
        var path = new TextBlock { FontSize = DesignMetrics.Type.Mono, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left, LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        MiddleTrim.Fit(path, workspace.Path, ResumeSheetWidth - 2 * SheetPadding - 2 * DesignMetrics.Stroke.Line); ToolTipService.SetToolTip(path, workspace.Path);
        var heading = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs }; heading.Children.Add(titleRow); heading.Children.Add(path);
        Place(heading, 0);

        // The search: a magnifier in the tertiary ink (:85) and the plain 12pt field 7 apart, padding 7 on the subtle wash at radius 7 (29 high on the Mac's crop).
        var search = new TextBox { PlaceholderText = Locale.Get("resume.search"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        PaintPlainTextBox(search);
        AutomationProperties.SetName(search, Locale.Get("resume.search")); AutomationProperties.SetAutomationId(search, "resume-search");
        var searchRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        searchRow.Children.Add(new FontIcon { Glyph = "\uE721", FontSize = 15, Foreground = tertiary, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(search, 1); searchRow.Children.Add(search);
        Place(new Border { Child = searchRow, Height = 29, Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), Background = brushes.Subtle }, 1);

        // The sessions on half the subtle wash at radius 8, their rows 2 apart (standing in place at once, as the Mac's,
        // when the search narrows them); or, in their place, why there are none.
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Background = brushes.Brush(DesignToken.Ink, DesignMetrics.Opacity.Subtle / 2), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Padding = new Thickness(0), ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection() };
        SetResourcesOnce(list, [("ListViewItemBackgroundPointerOver", brushes.Subtle), ("ListViewItemBackgroundPressed", brushes.Subtle)]);
        AutomationProperties.SetAutomationId(list, "resume-session-list");
        var state = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var sessions = new Grid(); sessions.Children.Add(list); sessions.Children.Add(state);
        Place(sessions, 2);

        // The Mac's checkbox is 14 high with a 10pt (caption) label in the label colour (M/ResumeSessionSheet.swift:93): the stock box, drawn
        // at seven tenths, and its words beside it at their own size in ink. Scaled along with the box, their stems fall between pixels and
        // read bold beside the caption after them; a click on the words still turns the box.
        const double checkScale = 0.7;
        var showAll = new CheckBox { MinWidth = 0, MinHeight = 0, Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(showAll, "resume-show-all"); AutomationProperties.SetName(showAll, Locale.Get("resume.showAll"));
        var showAllWords = new TextBlock { Text = Locale.Get("resume.showAll"), FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink), Margin = new Thickness(8 * checkScale, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(showAllWords, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        showAllWords.Tapped += (_, _) => showAll.IsChecked = showAll.IsChecked != true;
        var showAllRow = new StackPanel { Orientation = Orientation.Horizontal };
        showAllRow.Children.Add(new Viewbox { Height = 20 * checkScale, Child = showAll, VerticalAlignment = VerticalAlignment.Center }); showAllRow.Children.Add(showAllWords);
        var hidden = new TextBlock { FontSize = DesignMetrics.Type.Small, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(hidden, "resume-hidden-count");
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Md };
        footer.Children.Add(showAllRow); footer.Children.Add(hidden);
        Place(footer, 3);

        var note = new TextBlock { Text = Locale.Get("resume.note"), FontSize = DesignMetrics.Type.Small, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var cancel = PushButton(Locale.Get("resume.cancel"));
        var bottom = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        bottom.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        bottom.Children.Add(note); Grid.SetColumn(cancel, 1); bottom.Children.Add(cancel);
        Place(bottom, 4);

        var dialog = StyledDialog(new ContentDialog { Content = content, XamlRoot = root.XamlRoot }, ResumeSheetWidth, ResumeSheetHeight, bare: true);
        AutomationProperties.SetAutomationId(dialog, "resume-session-picker"); AutomationProperties.SetName(dialog, Locale.Get("resume.title"));
        cancel.Click += (_, _) => dialog.Hide();
        var listing = ResumableSessionListing.Empty;
        ResumableSession? chosen = null;
        var loads = 0; var reading = false;

        // What stands in the list's place (M/ResumeSessionSheet.swift:114-128): a 26pt light symbol in the tertiary ink, a 13pt medium line and its 11pt reason, 8 apart.
        FrameworkElement Nothing(string line, string? reason)
        {
            var words = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Lg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new FontIcon { Glyph = "\uE81C", FontSize = 28, FontWeight = FontWeights.Light, Foreground = tertiary });
            words.Children.Add(new TextBlock { Text = line, FontSize = DesignMetrics.Type.Title, FontWeight = FontWeights.Medium, Foreground = brushes.Brush(DesignToken.Ink), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (reason is not null) words.Children.Add(new TextBlock { Text = reason, FontSize = DesignMetrics.Type.Pill, Foreground = ink2, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            AutomationProperties.SetAutomationId(words, "resume-empty");
            return words;
        }
        void Fill()
        {
            var now = DateTimeOffset.UtcNow;
            var excluding = ResumableSessions.InUse(service.Snapshot.Sessions);
            var items = ResumableSessions.Filter(listing.Items.Where(i => !excluding.Contains(i.SessionID.ToLowerInvariant())), search.Text);
            list.Items.Clear();
            foreach (var item in items) list.Items.Add(ResumeRow(item, now));
            hidden.Text = listing.Hidden > 0 && showAll.IsChecked != true ? Locale.Get("resume.hiddenCount", new Dictionary<string, string> { ["count"] = listing.Hidden.ToString() }) : "";
            hidden.Visibility = hidden.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            state.Child = items.Count > 0 ? null : string.IsNullOrWhiteSpace(search.Text)
                ? Nothing(Locale.Get("resume.empty"), Locale.Get("resume.emptyReason", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(provider) }))
                : Nothing(Locale.Get("resume.noMatch"), null);
        }
        async Task Load()
        {
            var load = ++loads; reading = true;
            // The Mac's progress view over its words, in the list's place.
            var progress = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            progress.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, MinWidth = 0, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Center });
            progress.Children.Add(new TextBlock { Text = Locale.Get("resume.loading"), FontSize = DesignMetrics.Type.Pill, Foreground = ink2, HorizontalAlignment = HorizontalAlignment.Center });
            list.Items.Clear(); list.Visibility = Visibility.Collapsed; state.Child = progress;
            var query = ResumeQuery(workspace.Path) with { IncludeAutomated = showAll.IsChecked == true };
            ResumableSessionListing result;
            try { result = await Task.Run(() => ResumableSessions.Listing(query, provider)); }
            catch (Exception) { result = ResumableSessionListing.Empty; }
            if (load != loads) return;
            reading = false; listing = result; Fill();
        }
        search.TextChanged += (_, _) => { if (!reading) Fill(); };
        showAll.Checked += async (_, _) => await Act(Load);
        showAll.Unchecked += async (_, _) => await Act(Load);
        void Choose(ResumableSession item) { chosen = item; if (smokeResumeDialog is null) dialog.Hide(); }
        // The smoke picks a row the way a click does.
        dialog.Tag = new ResumeSheetParts(list, hidden, search, showAll, showAllWords, Choose);
        list.ItemClick += (_, args) => { if ((args.ClickedItem as FrameworkElement)?.Tag is ResumableSession item) Choose(item); };
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        var loading = Load();
        await ShowResumeDialog(dialog);
        await loading;
        if (chosen is not { } session) return;
        // Taken by another pane while the list was open: never two panes on one session.
        if (ResumableSessions.InUse(service.Snapshot.Sessions).Contains(session.SessionID.ToLowerInvariant()))
        {
            error.Text = Locale.Get("resume.error.inUse");
            return;
        }
        service.RememberSession(session.SessionID);
        await AddPane("claude", session.Provider, groupId, pane => ResumableSessions.Apply(session, pane));
    }

    /// <summary>
    /// One session (M/ResumeSessionSheet.swift:139-172), padding h10 v8: the agent's mark (14, in an 18-wide column,
    /// 2 down) 10 from its words, which are 4 apart: the 12pt medium title of at most two lines (<c>ink2</c> when the
    /// session has none) with the beta capsule 6 after it, the 10pt <c>ink2</c> details, and for a record written
    /// moments ago the 10pt <c>waitText</c> warning. The subtle wash under the pointer.
    /// </summary>
    private ListViewItem ResumeRow(ResumableSession item, DateTimeOffset now)
    {
        var title = ResumableSessions.RowTitle(item);
        // Core's details end with the "recently modified" note, which the row shows as a line of its own.
        var spoken = ResumableSessions.Details(item, now);
        var busy = ResumableSessions.MayBeRunning(item, now); var warning = Locale.Get("resume.recentlyModified");
        var details = busy && spoken.EndsWith(" · " + warning, StringComparison.Ordinal) ? spoken[..^(warning.Length + 3)] : spoken;
        var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        var name = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = brushes.Brush(item.Title is null ? DesignToken.Ink2 : DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        if (ProviderCatalog.IsBeta(item.Provider))
        {
            var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Left };
            line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(name);
            var badge = BetaBadgeView.Create(brushes); Grid.SetColumn(badge, 1); line.Children.Add(badge);
            words.Children.Add(line);
        }
        else words.Children.Add(name);
        words.Children.Add(new TextBlock { Text = details, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis });
        if (busy)
        {
            var wait = brushes.Brush(DesignToken.WaitText);
            var caution = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
            caution.Children.Add(new FontIcon { Glyph = "\uE783", FontSize = 11, Foreground = wait, VerticalAlignment = VerticalAlignment.Center });
            caution.Children.Add(new TextBlock { Text = warning, FontSize = DesignMetrics.Type.Small, Foreground = wait, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetAutomationId(caution, "resume-busy-" + item.SessionID);
            words.Children.Add(caution);
        }
        var mark = ProviderMarkView.Create(item.Provider, ResumeRowMarkSize * ProviderIconScale);
        mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, 0);
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md, Tag = item };
        row.ColumnDefinitions.Add(new() { Width = new(ResumeRowMarkColumn) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.Children.Add(mark); Grid.SetColumn(words, 1); row.Children.Add(words);
        var container = new ListViewItem { Content = row, Tag = item, Padding = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm), MinHeight = 0, Margin = new Thickness(0, 0, 0, DesignMetrics.Spacing.Xxs), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        var provider = ProviderCatalog.BetaLabel(item.Provider, ProviderMark.Label(item.Provider));
        AutomationProperties.SetName(container, Locale.Get("resume.rowAccessibility", new Dictionary<string, string> { ["provider"] = provider, ["title"] = title, ["details"] = spoken }));
        AutomationProperties.SetAutomationId(container, "resume-session-" + item.SessionID);
        ToolTipService.SetToolTip(container, item.Title ?? item.SessionID);
        return container;
    }
}
