using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>
/// The window side of parent → child delegation (M/AppStore+Delegation.swift, M/WorkspaceView.swift:81-97, 331-448): the
/// service's hooks, each delegated child one step in under its parent with its state and its card, the 'parent closed' node,
/// the discard and parent-close confirmations, and every close of a pane through the service. Layout and sizes are the Mac's;
/// where it draws with the sidebar accent, the token colours stand in.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>A redraw for delegation is waiting on the window's thread: changes that come meanwhile ride on it.</summary>
    private bool delegationRedrawQueued;
    /// <summary>The saved state is loaded and first drawn: delegation's redraws before then would draw the empty state over the splash.</summary>
    private bool delegationRedrawReady;
    /// <summary>The panes the last full <see cref="Render"/> laid out.</summary>
    private HashSet<string> renderedSessionIds = [];
    /// <summary>Each delegated child's sidebar row, by its pane's id, whose state word the clock keeps current.</summary>
    private readonly Dictionary<string, DelegationChildRow> sidebarChildStates = [];
    /// <summary>A smoke driver answers the delegation confirmations in their buttons' place.</summary>
    internal Func<ContentDialog, Task<ContentDialogResult>>? smokeDelegationDialog;
    /// <summary>A smoke run's Mighty Orchestration switch, in the registry value's place: off until a smoke step turns it on.</summary>
    private volatile bool smokeDelegationOn;

    /// <summary>Whether Mighty Orchestration is on: the registry value, or a smoke run's own switch.</summary>
    private bool DelegationOn => options.SmokeTest ? smokeDelegationOn : DelegationSwitch.IsOn();

    /// <summary>Settings › General's switch (M/SettingsViews.swift:240-251): new runs get the delegation tools or not, and every composer's Ctrl+Enter follows at once.</summary>
    private void SetDelegationOn(bool on)
    {
        if (options.SmokeTest) smokeDelegationOn = on; else DelegationSwitch.Set(on);
        // A pane already running learns whether its folder can host a child worktree, so its Ctrl+Enter follows at once.
        service.RefreshRunningWorktreeReadiness();
        foreach (var view in views.Values) view.RefreshHeldRows();
    }

    /// <summary>How long a run delegation starts waits for the window to say whether it would refuse it; past that it is refused.</summary>
    private static readonly TimeSpan DelegatedRunAnswer = TimeSpan.FromSeconds(5);

    /// <summary>Hands the service the window's part: why a run is refused, how a run is finished, the agent terminal's end, and the redraw.</summary>
    private void InitializeDelegation()
    {
        service.DelegatedRunBlocked = DelegatedRunBlocked;
        service.DelegatedRunRequest = request => OnWindow(() => FinishDelegatedRequest(request));
        service.EndAgentTerminal = id => OnWindow(() => EndAgentTerminal(id));
        // The app's note in a pane's transcript (its commit of a child's work, among others) shows at once.
        service.DelegationNoted += id => DispatcherQueue.TryEnqueue(() => { if (!closing && views.TryGetValue(id, out var view)) view.Refresh(); });
        service.DelegationChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (delegationRedrawQueued) return;
            delegationRedrawQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                delegationRedrawQueued = false; if (closing || !delegationRedrawReady) return;
                try
                {
                    // Only a pane made or closed meanwhile lays the panes out again; otherwise the sidebar and the queued
                    // lists are drawn again and the composers keep their focus and any composition.
                    if (!service.Snapshot.Sessions.Select(s => s.Id).ToHashSet().SetEquals(renderedSessionIds)) Render(); else RenderSidebar();
                    foreach (var view in views.Values) view.RefreshHeldRows();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { error.Text = ex.Message; }
            });
        });
    }

    /// <summary>Runs <paramref name="work"/> on the window's thread and hands back its answer; a window that can take no more work cancels it.</summary>
    private Task<T> OnWindow<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { done.TrySetResult(await work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        })) done.TrySetCanceled();
        return done.Task;
    }

    /// <summary>
    /// The service's question, asked on a thread-pool thread: why the window would refuse a run in the pane now. It is asked of
    /// the window's thread and waited for a bounded time, so a busy window never stalls delegation: one that does not answer in
    /// time refuses, and what came for the pane is held.
    /// </summary>
    private string? DelegatedRunBlocked(RunSession pane)
    {
        if (DispatcherQueue.HasThreadAccess) return DelegatedRunBlockReason(pane);
        var answer = OnWindow(() => Task.FromResult(DelegatedRunBlockReason(pane)));
        try { return answer.Wait(DelegatedRunAnswer) ? answer.Result : Locale.Get("remote.error.startFailed"); }
        catch (AggregateException) { return Locale.Get("remote.error.startFailed"); }
    }

    /// <summary>
    /// Why the window refuses a run delegation starts in the pane now, the reasons its own sends meet (<see cref="AdmissionRefusal"/>)
    /// and a background update holding the pane's sends (M/AppStore.swift:1037-1045); null when one may start. The GUI smoke starts
    /// one only while its scripted Claude stands in for the CLI.
    /// </summary>
    private string? DelegatedRunBlockReason(RunSession pane) =>
        closing ? Locale.Get("remote.error.startFailed")
        : options.SmokeTest && smokeSend is null ? SmokeNeverStartsCli
        : AdmissionRefusal(pane.Kind, pane.Provider) ?? (BackgroundUpdateHolds(pane) ? Locale.Get("settings.cliUpdate.backgroundUpdateQueued", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(pane.Provider) }) : null);

    /// <summary>
    /// A run delegation starts on its own, finished the way a send from the pane is: after its model list, with the pane's model,
    /// settings and the registered models, and its style's tools bound (M/AppStore+Delegation.swift:144-155).
    /// </summary>
    private async Task<StartRunRequest> FinishDelegatedRequest(StartRunRequest request)
    {
        if (closing) return request;
        await PrepareModels(request.SessionId);
        if (closing || service.Snapshot.Sessions.FirstOrDefault(s => s.Id == request.SessionId) is not { Kind: "claude" } pane) return request;
        return await MobilePane(pane.Id).FinishDelegatedRequest(request);
    }

    /// <summary>
    /// Ends the pane's agent terminal and everything it started, and returns once they have exited: a child's discard and its
    /// close's cleanup look for links in its worktree only then. Its binding is revoked, so no tool call opens another.
    /// </summary>
    private async Task<bool> EndAgentTerminal(string paneId)
    {
        agentIO?.Bindings.Revoke(paneId);
        if (agentTerminals.Remove(paneId, out var terminal)) TrackAgentTerminalClosure(paneId, terminal.DisposeAsync().AsTask());
        if (agentTerminalClosures.TryGetValue(paneId, out var ending)) await ending;
        return true;
    }

    /// <summary>
    /// Closes the pane through the service (M/AppStore.swift:592-625): its run has ended before it goes, and a delegated child's
    /// close goes on once it is gone (a merged child's worktree and branch cleaned up), redrawn when that is over. Returns once
    /// the pane is gone.
    /// </summary>
    private async Task ClosePane(string id)
    {
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = service.CloseSessionAsync(id, () => gone.TrySetResult());
        if (await Task.WhenAny(gone.Task, close) == close) await close;
        if (!closing) Render();
    }

    /// <summary>The parent-close confirmation (M/WorkspaceView.swift:91-96): only the parent closes, its open children go on. True when the human confirmed.</summary>
    private async Task<bool> ConfirmParentClose(PendingParentClose pending)
    {
        if (dialogOpen) return false;
        var dialog = StyledDialog(new ContentDialog
        {
            Title = Locale.Get("delegation.parentClose.title"), Content = new TextBlock { Text = DelegationText.ParentCloseMessage(pending), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Locale.Get("delegation.parentClose.confirm"), CloseButtonText = Locale.Get("resume.cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
        });
        AutomationProperties.SetAutomationId(dialog, "delegation-parent-close-" + pending.Id);
        return await ShowDelegationDialog(dialog) == ContentDialogResult.Primary && !closing;
    }

    /// <summary>
    /// A child's discard from its card (M/AppStore+Delegation.swift:259-305): its confirmation names the worktrees nested in the
    /// child's that go with it, and offers no discard when they cannot be listed. When more are nested by the time it runs,
    /// nothing is removed and it asks again naming them all. One confirmation at a time.
    /// </summary>
    private Task DiscardChild(string id) => Act(async () =>
    {
        if (dialogOpen || await service.AskToDiscardChildAsync(id) is not { } pending) return;
        while (await ConfirmDiscard(pending))
        {
            if ((await service.DiscardChildAsync(pending))?.Result is not DelegationCardResult.Reconfirm again) break;
            pending = pending with { Nested = again.Nested };
        }
        if (!closing) Render();
    });

    /// <summary>The discard confirmation (M/WorkspaceView.swift:81-90). True when the human confirmed a discard it offered.</summary>
    private async Task<bool> ConfirmDiscard(PendingChildDiscard pending)
    {
        if (dialogOpen) return false;
        var dialog = StyledDialog(new ContentDialog
        {
            Title = DelegationText.DiscardTitle(pending), Content = new TextBlock { Text = DelegationText.DiscardMessage(pending), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
        });
        if (pending.Nested is not null) { dialog.PrimaryButtonText = Locale.Get("delegation.discard.confirm"); dialog.CloseButtonText = Locale.Get("resume.cancel"); }
        else dialog.CloseButtonText = Locale.Get("common.close");
        AutomationProperties.SetAutomationId(dialog, "delegation-discard-" + pending.Id);
        return await ShowDelegationDialog(dialog) == ContentDialogResult.Primary && pending.Nested is not null && !closing;
    }

    /// <summary>
    /// Once a merge from the child's card went through, the human is offered to discard the child right away (M/WorkspaceView.swift:93-97):
    /// its work is in the parent's branch, so only its worktree, its branch and the undo go. Nothing is asked when the worktrees
    /// nested in it cannot be listed (the card's own discard says why) or another confirmation is up.
    /// </summary>
    private async Task OfferDiscardAfterMerge(string id)
    {
        if (dialogOpen || await service.MergedDiscardOffer(id) is not { } pending || dialogOpen || closing) return;
        var dialog = StyledDialog(new ContentDialog
        {
            Title = Locale.Get("delegation.mergedDiscard.title"), Content = new TextBlock { Text = DelegationText.MergedDiscardMessage(pending), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = Locale.Get("delegation.mergedDiscard.confirm"), CloseButtonText = Locale.Get("delegation.mergedDiscard.later"), DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
        });
        AutomationProperties.SetAutomationId(dialog, "delegation-merged-discard-" + id);
        if (await ShowDelegationDialog(dialog) != ContentDialogResult.Primary || closing) return;
        while ((await service.DiscardChildAsync(pending))?.Result is DelegationCardResult.Reconfirm again && await ConfirmDiscard(pending = pending with { Nested = again.Nested })) { }
        if (!closing) Render();
    }

    private async Task<ContentDialogResult> ShowDelegationDialog(ContentDialog dialog)
    {
        dialogOpen = true;
        try { return smokeDelegationDialog is { } driver ? await driver(dialog) : await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
    }

    private Task PerformCardAction(DelegationCardAction action, string id) => action switch
    {
        DelegationCardAction.Merge => Act(async () =>
        {
            var note = await service.MergeChildAsync(id);
            if (closing) return;
            RenderSidebar();
            if (note?.Result is DelegationCardResult.Done) await OfferDiscardAfterMerge(id);
        }),
        DelegationCardAction.Undo => Act(async () => { await service.UndoChildMergeAsync(id); if (!closing) RenderSidebar(); }),
        _ => DiscardChild(id),
    };

    /// <summary>The card's actions in a child row's menu (M/WorkspaceView.swift:400-406), off while one of them is going.</summary>
    private void AddChildCardItems(MenuFlyout menu, DelegationChildRow child)
    {
        foreach (var action in child.CardActions)
        {
            var item = MenuItem(DelegationText.Action(action), () => PerformCardAction(action, child.Id));
            menu.Opening += (_, _) => item.IsEnabled = !service.IsCardBusy(child.Id) && !dialogOpen;
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// A delegated child under its parent (M/WorkspaceView.swift:331-352), one step in: its pane's row while the pane is open,
    /// otherwise a row for the child the delegation file keeps, and under it its card when it has an action, an answer or, under
    /// a closed parent, held notices.
    /// </summary>
    private StackPanel SidebarChildRow(DelegationChildRow child, AppSnapshot state, bool parentClosed)
    {
        var card = new StackPanel { Margin = new Thickness(DesignMetrics.Spacing.Xl, 0, 0, 0) };
        AutomationProperties.SetAutomationId(card, "sidebar-child-card-" + child.Id);
        card.Children.Add(state.Sessions.FirstOrDefault(s => s.Id == child.Id) is { } session ? SidebarPaneButton(session, state, child) : SidebarClosedChildRow(child));
        if (SidebarChildCard(child, parentClosed ? child.HeldNotices : 0) is { } line) card.Children.Add(line);
        return card;
    }

    /// <summary>
    /// A child's card line (M/WorkspaceView.swift:354-398): its actions as small buttons (merge while reported, undo while its
    /// merge is the one in effect, discard until discarded), a busy line while one is going, the held notices a closed parent
    /// left, and the last answer in its tone, with git's output kept in it; null when there is none of these.
    /// </summary>
    private StackPanel? SidebarChildCard(DelegationChildRow child, int heldNotices)
    {
        var busy = service.IsCardBusy(child.Id);
        var note = service.CardNote(child.Id) is { } answer && !busy ? DelegationText.Note(answer, child) : null;
        var kept = DelegationText.CloseNote(service.ChildClose(child.Id) is { IsCompletedSuccessfully: true } close ? close.Result : null);
        if (child.CardActions.Count == 0 && !busy && heldNotices == 0 && note is null && kept is null) return null;
        var ink2 = brushes.Brush(DesignToken.SidebarInk2);
        var card = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, Margin = new Thickness(DesignMetrics.Spacing.Xl + DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Md };
        if (busy)
        {
            actions.Children.Add(new ProgressRing { IsActive = true, Width = 10, Height = 10, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center });
            actions.Children.Add(new TextBlock { Text = Locale.Get("delegation.card.working"), FontSize = 10.5, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        else foreach (var action in child.CardActions) actions.Children.Add(SidebarCardButton(action, child));
        if (actions.Children.Count > 0) card.Children.Add(actions);
        if (heldNotices > 0)
        {
            var held = new TextBlock { Text = Locale.Get("delegation.card.heldNotices", new Dictionary<string, string> { ["count"] = heldNotices.ToString(System.Globalization.CultureInfo.InvariantCulture) }), FontSize = 10.5, Foreground = ink2, TextWrapping = TextWrapping.Wrap };
            ToolTipService.SetToolTip(held, Locale.Get("delegation.card.heldNoticesHelp")); AutomationProperties.SetAutomationId(held, "child-card-held-" + child.Id);
            card.Children.Add(held);
        }
        if (note is { } line) card.Children.Add(SidebarCardNote(line.Text, line.Tone, "child-card-note-" + child.Id));
        if (kept is not null) card.Children.Add(SidebarCardNote(kept, DesignTone.Wait, "child-card-close-" + child.Id));
        AutomationProperties.SetAutomationId(card, "child-card-" + child.Id);
        return card;
    }

    /// <summary>A card's answer: 10.5pt in its tone's ink, wrapped and selectable, so git's words can be copied.</summary>
    private TextBlock SidebarCardNote(string text, DesignTone tone, string automationId)
    {
        var note = new TextBlock { Text = text, FontSize = 10.5, Foreground = brushes.Text(tone), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(note, automationId);
        return note;
    }

    /// <summary>One card action, a plain 10.5pt semibold word: the accent's for merge and undo, the muted ink for discard.</summary>
    private Button SidebarCardButton(DelegationCardAction action, DelegationChildRow child)
    {
        var words = DelegationText.Action(action);
        var button = SidebarButton(words, () => PerformCardAction(action, child.Id));
        button.Content = new TextBlock { Text = words, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(action == DelegationCardAction.Discard ? DesignToken.SidebarInk2 : DesignToken.SidebarAccent) };
        button.HorizontalAlignment = HorizontalAlignment.Left; button.Padding = new Thickness(0); button.MinWidth = 0; button.MinHeight = 0;
        PlainSidebarButton(button, brushes.Transparent, brushes.Transparent);
        var help = DelegationText.ActionHelp(action, child);
        ToolTipService.SetToolTip(button, help); AutomationProperties.SetName(button, help);
        AutomationProperties.SetAutomationId(button, "child-card-" + action.ToString().ToLowerInvariant() + "-" + child.Id);
        return button;
    }

    /// <summary>
    /// A child with no open pane (its start failed, or its pane was closed), still listed under its parent (M/WorkspaceView.swift:408-428):
    /// the glyph of its state's tone, its task in the muted ink and its state word; its card actions in its menu.
    /// </summary>
    private Border SidebarClosedChildRow(DelegationChildRow child)
    {
        var title = child.Task ?? Locale.Get("delegation.child.untitled"); var tone = DelegationSidebar.Tone(child.State);
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var mark = new StatusMark(); mark.Update(StatusCountsView.Status(tone), "claude", 0, DarkTheme);
        mark.View.VerticalAlignment = VerticalAlignment.Top; mark.View.Margin = new Thickness(0, 1.5, 0, 0); row.Children.Add(mark.View);
        var words = new TextBlock { Text = title, FontSize = DesignMetrics.Type.SideRow, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(DesignToken.SidebarInk2), TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 17, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(words, title); Grid.SetColumn(words, 1); row.Children.Add(words);
        var state = new TextBlock { Margin = new Thickness(DesignMetrics.Spacing.Sm, 0, 0, 0) }; ShowChildState(state, child, new());
        Grid.SetColumn(state, 2); row.Children.Add(state);
        var menu = new MenuFlyout(); AddChildCardItems(menu, child);
        var host = new Border { Child = row, Padding = SidebarPaneRowPadding, Background = brushes.Transparent, ContextFlyout = menu.Items.Count > 0 ? menu : null };
        AutomationProperties.SetName(host, Locale.Get("delegation.child.accessibility", new Dictionary<string, string> { ["title"] = title, ["state"] = DelegationText.State(child.State) }));
        AutomationProperties.SetAutomationId(host, "sidebar-child-" + child.Id);
        return host;
    }

    /// <summary>
    /// A child's state in words on the right of its row (M/WorkspaceView.swift:450-456): 10.5pt bold in its tone's ink, a quiet
    /// state in the sidebar's muted ink; waiting while its running pane asks the human.
    /// </summary>
    private void ShowChildState(TextBlock word, DelegationChildRow child, WorkDashboard.Attention attention)
    {
        var shown = DelegationSidebar.ShownState(child.State, attention.Total > 0); var tone = DelegationSidebar.Tone(shown);
        word.Text = DelegationText.State(shown); word.FontSize = 10.5; word.FontWeight = Microsoft.UI.Text.FontWeights.Bold; word.MinHeight = 17;
        word.Foreground = tone == DesignTone.Idle ? brushes.Brush(DesignToken.SidebarInk2) : brushes.Text(tone); word.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The children of a closed parent pane under a node of their own (M/WorkspaceView.swift:430-448): the closed-window symbol,
    /// "parent closed" in 11.5 semibold, the parent's branch in mono and the count of its children, all in the muted ink.
    /// </summary>
    private StackPanel SidebarParentClosedNode(DelegationParentClosed node, AppSnapshot state)
    {
        var ink2 = brushes.Brush(DesignToken.SidebarInk2);
        var group = new StackPanel { Spacing = DesignMetrics.Inset.ListGap };
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = SidebarPaneRowPadding };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) row.ColumnDefinitions.Add(new() { Width = width });
        var symbol = new FontIcon { Glyph = "", FontSize = 11, Width = 14, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(symbol, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); row.Children.Add(symbol);
        var label = new TextBlock { Text = Locale.Get("delegation.tree.parentClosed"), FontSize = DesignMetrics.Type.State, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        if (node.ParentBranch.Length > 0)
        {
            var branch = new TextBlock { Text = node.ParentBranch, FontSize = 10.5, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(branch, 2); row.Children.Add(branch);
        }
        var count = new TextBlock { Text = node.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 3); row.Children.Add(count);
        var header = new Border { Child = row, Background = brushes.Transparent };
        ToolTipService.SetToolTip(header, Locale.Get("delegation.tree.parentClosedHelp"));
        AutomationProperties.SetName(header, Locale.Get("delegation.tree.parentClosedAccessibility", new Dictionary<string, string> { ["count"] = node.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        AutomationProperties.SetAutomationId(header, "sidebar-parent-closed-" + node.Id);
        group.Children.Add(header);
        foreach (var child in node.Children) group.Children.Add(SidebarChildRow(child, state, true));
        return group;
    }

    private sealed partial class PaneView
    {
        /// <summary>A run delegation starts in this pane, finished as this pane's own send is: its model, settings, registered models and style.</summary>
        internal async Task<StartRunRequest> FinishDelegatedRequest(StartRunRequest request)
        {
            var pane = Session;
            return await PrepareStyleRunRequest(request with { Model = pane.Model, Settings = pane.Settings, RegisteredModels = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot) });
        }

        /// <summary>The queued list again, for the items delegation holds for the pane, and the composer, whose second send follows delegation.</summary>
        internal void RefreshHeldRows() { if (QueuePaneAlive && Session.Kind == "claude") { RenderQueuedInputs(); RefreshComposerState(); } }

        /// <summary>
        /// The composer's second send on this busy pane (<see cref="ComposerSecondSend.Worktree"/>; M/AppStore+Delegation.swift
        /// runInNewWorktree): the text and its attachments run now in a new child pane, in its own worktree beside this one, made
        /// as delegate makes one, in plan mode when the pane's style starts every request so. This pane's run goes on undisturbed,
        /// and the new child shows at once. When no child can be made, or it cannot start after all, the transcript says why and
        /// the text goes on by <see cref="RunAfterNoWorktree"/>.
        /// </summary>
        private void RunInNewWorktree(string text, IReadOnlyList<RunAttachment> files)
        {
            var planFirst = LaunchesInPlanMode; var parentId = id;
            // A pane that went away meanwhile has no composer to fall back to: the text is the child's failed task on its card.
            Task FallBack(string reason) => owner.OnWindow(async () =>
            {
                if (owner.views.TryGetValue(parentId, out var view)) await view.RunAfterNoWorktree(text, files, reason);
                return true;
            });
            _ = owner.Act(async () =>
            {
                var answer = await owner.service.StartHumanChildAsync(parentId, text, files, planFirst, _ => FallBack(Locale.Get("delegation.human.error.startFailed")));
                if (answer switch { HumanChildStart.Refused refused => DelegationText.Reason(refused.Reason), HumanChildStart.Failed failed => failed.Message, _ => null } is { } reason) await FallBack(reason);
                if (!owner.closing) owner.Render();
            });
        }

        /// <summary>
        /// The text of a second send that no new child runs (<see cref="ComposerSecondSends.Fallback"/>): steered into the pane's
        /// running Claude turn when that can take it, otherwise the pane's next request, queued behind the run or started now when
        /// the run ended meanwhile. With the queue full it goes back to the composer instead, ahead of anything typed there
        /// meanwhile, so it is never lost. The transcript says which, and why.
        /// </summary>
        private async Task RunAfterNoWorktree(string text, IReadOnlyList<RunAttachment> files, string reason)
        {
            if (!QueuePaneAlive) return;
            var pane = Session; var words = new Dictionary<string, string> { ["reason"] = reason };
            var busy = pane.Status == "running" || starting || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(pane);
            var fallback = ComposerSecondSends.Fallback(busy && DelegationProviders.CanSteer(pane.Kind, pane.Provider) && files.Count == 0);
            if (queuedInputs.Items.Count >= QueuedInputBuffer.MaximumItems)
            {
                await SlashNote(Locale.Get("delegation.human.returned", words));
                owner.error.Text = Locale.Get("queue.full");
                var draft = input.Text.Length == 0 ? text : text + "\n\n" + input.Text;
                updating = true; input.Text = draft; updating = false; await Change(p => p with { Draft = draft });
                var kept = pendingAttachments.Select(file => file.Id).ToHashSet(); pendingAttachments.InsertRange(0, files.Where(file => !kept.Contains(file.Id))); RefreshAttachments();
                RefreshComposerState();
                return;
            }
            await SlashNote(Locale.Get(fallback == ComposerSecondSend.Steer ? "delegation.human.steered" : "delegation.human.queued", words));
            // A turn that ends before taking the steer runs it next instead.
            if (fallback == ComposerSecondSend.Steer && await owner.service.TrySteerAsync(id, text)) { RefreshComposerState(); return; }
            if (!QueuePaneAlive) return;
            queuedInputs.Add(text, files, BackgroundQueuePolicy.QueuedOverride(LaunchesInPlanMode));
            RenderQueuedInputs(); RefreshComposerState();
            // The run ended meanwhile: it is the next request now.
            if (!busy) queueDrainTimer.Start();
        }
    }
}
