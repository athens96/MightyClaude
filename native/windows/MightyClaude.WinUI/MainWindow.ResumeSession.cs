using MightyClaude.Core;
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
        dialogOpen = true;
        try { return smokeResumeDialog is { } driver ? await driver(dialog) : await dialog.ShowAsync(); }
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
    /// Start new / Resume… (M/ResumeSessionSheet.swift): a 400-wide sheet, padding 20, the agent's 18pt mark
    /// beside the 12pt <c>ink2</c> sentence; Cancel (Esc) | Resume… | Start new (the default, Enter).
    /// </summary>
    private ContentDialog ResumeChoiceDialog(string provider)
    {
        var name = ProviderCatalog.BetaLabel(provider, ProviderCatalog.Name(provider));
        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var mark = ProviderMarkView.Create(provider, ResumeMarkSize); mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 1, 0, 0);
        body.Children.Add(mark);
        var message = new TextBlock { Text = Locale.Get("resume.choice.message", new Dictionary<string, string> { ["provider"] = name }), FontSize = 12, Foreground = brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(message, 1); body.Children.Add(message);
        var choice = StyledDialog(new ContentDialog
        {
            Title = Locale.Get("resume.choice.title", new Dictionary<string, string> { ["provider"] = name }),
            Content = body,
            PrimaryButtonText = Locale.Get("resume.choice.startNew"),
            SecondaryButtonText = Locale.Get("resume.choice.resume"),
            CloseButtonText = Locale.Get("resume.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root.XamlRoot,
        }, ChoiceSheetWidth);
        AutomationProperties.SetAutomationId(choice, "add-pane-choice");
        return choice;
    }

    /// <summary>The agent mark on the resume sheets (M/ResumeSessionSheet.swift).</summary>
    internal const double ResumeMarkSize = 18;

    /// <summary>
    /// The list of one agent's sessions for the folder, newest first: title (the
    /// first request), when, how many requests and the model. Nested Ouroboros
    /// runs are hidden unless Show all sessions is on; a session already open in a
    /// pane never shows. Picking one adds a pane that continues it.
    /// </summary>
    private async Task PickResumedSession(string provider, Workspace workspace, string? groupId)
    {
        // The list sheet (M/ResumeSessionSheet.swift, 560x520): the folder's path, the title search, the rows
        // (mark, title, "5 days ago · 1 request · Opus 5.5"), Show all sessions with the hidden count, and the note.
        var ink2 = brushes.Brush(DesignToken.Ink2);
        var path = new Grid { Children = { new TextBlock { Text = workspace.Path, FontSize = DesignMetrics.Type.Mono, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis } } };
        var search = new TextBox { PlaceholderText = Locale.Get("resume.search"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(search, Locale.Get("resume.search"));
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Height = 300, Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Padding = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(list, "resume-session-list");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ink2 };
        var showAll = new CheckBox { Content = Locale.Get("resume.showAll") };
        AutomationProperties.SetAutomationId(showAll, "resume-show-all");
        var hidden = new TextBlock { FontSize = DesignMetrics.Type.Pill, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { showAll, hidden } };
        var note = new TextBlock { Text = Locale.Get("resume.note"), FontSize = DesignMetrics.Type.Pill, Foreground = ink2 };
        var dialog = StyledDialog(new ContentDialog
        {
            Title = Locale.Get("resume.title"),
            Content = new StackPanel { Spacing = 8, Children = { path, search, status, list, footer, note } },
            CloseButtonText = Locale.Get("resume.cancel"),
            XamlRoot = root.XamlRoot,
        }, ResumeSheetWidth, ResumeSheetHeight);
        AutomationProperties.SetAutomationId(dialog, "resume-session-picker");
        var listing = ResumableSessionListing.Empty;
        ResumableSession? chosen = null;
        var loads = 0;

        void Fill()
        {
            var now = DateTimeOffset.UtcNow;
            var excluding = ResumableSessions.InUse(service.Snapshot.Sessions);
            var items = ResumableSessions.Filter(listing.Items.Where(i => !excluding.Contains(i.SessionID.ToLowerInvariant())), search.Text);
            list.Items.Clear();
            foreach (var item in items) list.Items.Add(ResumeRow(item, now));
            hidden.Text = listing.Hidden > 0 && showAll.IsChecked != true ? Locale.Get("resume.hiddenCount", new Dictionary<string, string> { ["count"] = listing.Hidden.ToString() }) : "";
            status.Text = items.Count > 0 ? "" : listing.Items.Count == 0
                ? Locale.Get("resume.empty") + "\n" + Locale.Get("resume.emptyReason", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) })
                : Locale.Get("resume.noMatch");
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Load()
        {
            var load = ++loads;
            status.Text = Locale.Get("resume.loading"); status.Visibility = Visibility.Visible; list.Items.Clear();
            var query = ResumeQuery(workspace.Path) with { IncludeAutomated = showAll.IsChecked == true };
            ResumableSessionListing result;
            try { result = await Task.Run(() => ResumableSessions.Listing(query, provider)); }
            catch (Exception) { result = ResumableSessionListing.Empty; }
            if (load != loads) return;
            listing = result; Fill();
        }
        search.TextChanged += (_, _) => Fill();
        showAll.Checked += async (_, _) => await Act(Load);
        showAll.Unchecked += async (_, _) => await Act(Load);
        void Choose(ResumableSession item) { chosen = item; if (smokeResumeDialog is null) dialog.Hide(); }
        // The smoke picks a row the way a click does.
        dialog.Tag = (Action<ResumableSession>)Choose;
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

    private FrameworkElement ResumeRow(ResumableSession item, DateTimeOffset now)
    {
        var title = ResumableSessions.RowTitle(item);
        var details = ResumableSessions.Details(item, now);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = DesignMetrics.Type.Title, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 });
        text.Children.Add(new TextBlock { Text = details, FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 });
        var mark = ProviderMarkView.Create(item.Provider, 14);
        mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 3, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Tag = item, Padding = new Thickness(0, 4, 0, 4), Children = { mark, text } };
        var provider = ProviderCatalog.BetaLabel(item.Provider, ProviderMark.Label(item.Provider));
        AutomationProperties.SetName(row, Locale.Get("resume.rowAccessibility", new Dictionary<string, string> { ["provider"] = provider, ["title"] = title, ["details"] = details }));
        AutomationProperties.SetAutomationId(row, "resume-session-" + item.SessionID);
        return row;
    }
}
