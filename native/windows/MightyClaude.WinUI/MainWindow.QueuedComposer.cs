using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private readonly QueuedInputBuffer queuedInputs = new();
        /// <summary>The requests waiting behind the current run, over the editor (M/QueuedInputsView.swift:15, M/SessionPaneView.swift:555-557).</summary>
        private readonly StackPanel queuedInputHost = new() { Spacing = DesignMetrics.Spacing.Xs, Margin = new Thickness(DesignMetrics.Inset.ComposerInnerH, DesignMetrics.Spacing.Md, DesignMetrics.Inset.ComposerInnerH, 0), Visibility = Visibility.Collapsed };
        private readonly DispatcherTimer queueDrainTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private Button? queueStopButton;
        /// <summary>
        /// The second send (Ctrl+Enter) beside queue when it opens a new child worktree (<see cref="ComposerSecondSend.Worktree"/>), or
        /// for its agent menu alone: the send button's toolbar-high circle with the branch, shown while the draft has words for the
        /// child's task, in <c>run</c> while the draft can go and <c>track</c> at half strength while not (<see cref="PaintWorktree"/>;
        /// M/SessionPaneView.swift worktreeButton/worktreeMenuButton). A right-click opens the menu (MainWindow.WorktreeMenu.cs).
        /// Nothing a person sends joins the running turn, so there is no other second button.
        /// </summary>
        private Button worktreeButton = null!;
        private readonly Border worktreeDisc = new() { Width = DesignMetrics.Layout.Toolbar, Height = DesignMetrics.Layout.Toolbar, CornerRadius = new CornerRadius(16), IsHitTestVisible = false };
        private readonly Grid worktreeHost = new() { Width = DesignMetrics.Layout.Toolbar, Height = DesignMetrics.Layout.Toolbar, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly ComposerGlyph worktreeBranch = ComposerGlyph.WorktreeBranch();
        /// <summary>The second send offers a new worktree (or its menu) now.</summary>
        private bool offersWorktree;
        private bool queueStarting;
        private int composerSubmissionVersion;
        private bool HasComposerContent => !string.IsNullOrWhiteSpace(input.Text) || pendingAttachments.Count > 0;

        /// <summary>The small stop square before the worktree button and queue: 4pt under the toolbar's height, as the Mac's.</summary>
        internal const double QueueStopSize = DesignMetrics.Layout.Toolbar - 4;
        private void InitializeQueuedComposer(StackPanel actions)
        {
            AutomationProperties.SetAutomationId(queuedInputHost, "queue-" + id);
            queueStopButton = Button("■", StopActiveRun); queueStopButton.Width = queueStopButton.Height = QueueStopSize; queueStopButton.MinWidth = 0; queueStopButton.MinHeight = 0; queueStopButton.Padding = new(0); queueStopButton.BorderThickness = new(0); queueStopButton.Visibility = Visibility.Collapsed;
            // With text waiting, stop shrinks to the err square 4pt under the toolbar (r7, a 10pt symbol) beside send (M/SessionPaneView.swift:737-750).
            queueStopButton.CornerRadius = new(7); queueStopButton.VerticalAlignment = VerticalAlignment.Center;
            queueStopButton.Content = ComposerGlyph.Stop(compact: true).Ink(owner.brushes.Brush(DesignToken.OnStatus)).View;
            owner.PaintPlainButton(queueStopButton, owner.brushes.Brush(DesignToken.Err), owner.brushes.Brush(DesignToken.Err), ink: owner.brushes.Brush(DesignToken.OnStatus), disabledInk: owner.brushes.Brush(DesignToken.OnStatus));
            AutomationProperties.SetName(queueStopButton, Locale.Get("composer.stop.name")); AutomationProperties.SetAutomationId(queueStopButton, "composer-stop-" + id);
            actions.Children.Add(queueStopButton);
            // Stop, the new worktree, then queue where send sits (M/SessionPaneView.swift:773-805).
            worktreeButton = new Button { Width = DesignMetrics.Layout.Toolbar, Height = DesignMetrics.Layout.Toolbar, MinWidth = 0, MinHeight = 0, Padding = new(0), CornerRadius = new(16), BorderThickness = new(0), Content = worktreeBranch.View };
            // A click (as Ctrl+Enter) runs the draft in a child of this pane's agent; where only another agent's child fits it opens the menu.
            worktreeButton.Click += async (_, _) => { if (worktreeMenuOnly) worktreeMenu.ShowAt(worktreeButton); else await Send(secondSend: true); };
            worktreeMenu.Opening += (_, _) => FillWorktreeMenu();
            worktreeButton.ContextFlyout = worktreeMenu;
            AutomationProperties.SetName(worktreeButton, Locale.Get("queue.worktree")); AutomationProperties.SetAutomationId(worktreeButton, "composer-worktree-" + id); ToolTipService.SetToolTip(worktreeButton, Locale.Get("queue.worktreeHint"));
            // Like send, the button draws nothing of its own; the disc and the branch carry its look (PaintWorktree).
            owner.PaintPlainButton(worktreeButton, owner.brushes.Transparent, owner.brushes.Transparent);
            worktreeButton.IsEnabledChanged += (_, _) => PaintWorktree();
            worktreeHost.Children.Add(worktreeDisc); worktreeHost.Children.Add(worktreeButton); actions.Children.Add(worktreeHost);
            queueDrainTimer.Tick += async (_, _) =>
            {
                if (!QueuePaneAlive) { queueDrainTimer.Stop(); return; }
                if (owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session) || owner.ManualMutationBlockReason(Session) is not null || starting || queueStarting) return;
                queueDrainTimer.Stop(); await StartNextQueuedInput();
            };
        }

        private bool QueuePaneAlive => !owner.closing && owner.views.TryGetValue(id, out var pane) && ReferenceEquals(pane, this)
            && owner.service.Snapshot.Sessions.Any(p => p.Id == id);

        /// <summary>
        /// A busy pane (running, starting, held for an update, or a turn that only waits on background work) always queues the draft
        /// as its next request; nothing a person sends joins the running turn. <paramref name="secondSend"/>: Ctrl+Enter, which runs
        /// written words in a new child worktree where <see cref="DesktopService.SecondSend"/> offers one, and otherwise queues too.
        /// </summary>
        /// <returns>True when the busy path handled this send, including validation refusals.</returns>
        /// <param name="asked">The pane's "send to" target, or null for Claude: its words are queued already wrapped, and staged files stay.</param>
        private async Task<bool> DeferBusyComposer(bool secondSend, AskTarget? asked = null)
        {
            if (Session.Status != "running" && !starting && !owner.service.IsSessionRunning(id) && !owner.BackgroundUpdateHolds(Session)) return false;
            if (Session.Kind != "claude" || !HasComposerContent || starting || composingInput || attachmentsLoading) return true;
            var typed = input.Text; var text = asked is null ? typed : AskTargets.Wrap(typed, asked);
            if (text is null) return true;
            RunAttachment[] files = asked is null ? pendingAttachments.ToArray() : [];
            var pane = Session;
            // The same request validation applies before consuming a draft even though it runs later.
            _ = new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, text, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files).Validate();
            // Ctrl+Enter on a pane that may have children runs written words now in a new child worktree; files alone wait.
            if (secondSend && !owner.BackgroundUpdateHolds(pane) && !string.IsNullOrWhiteSpace(text) && owner.service.SecondSend(pane) == ComposerSecondSend.Worktree)
            {
                // Taken from the composer now, so a second Ctrl+Enter cannot send it again.
                var sent = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => sent.Contains(file.Id)); RefreshAttachments();
                if (input.Text == typed) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
                RunInNewWorktree(text, files);
                RefreshComposerState();
                return true;
            }
            // A plan-mode style's queued request starts fresh in plan mode (§1.17.4), and the queued item keeps that decision.
            queuedInputs.Add(text, files, BackgroundQueuePolicy.QueuedOverride(LaunchesInPlanMode));
            var consumed = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => consumed.Contains(file.Id)); RefreshAttachments();
            if (input.Text == typed) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
            RefreshComposerState(); RenderQueuedInputs();
            // The stream may finish while the draft is being taken.
            if (Session.Status is "completed" or "idle") queueDrainTimer.Start();
            return true;
        }

        private Task ComposerPrimaryAction() => starting || queueStarting || Session.Status == "running" && !HasComposerContent ? StopActiveRun() : Send();

        /// <summary>A plan-mode style starts every new request in plan mode (§1.17.4).</summary>
        private bool LaunchesInPlanMode => activeStyle?.Evaluator.LaunchPermissionMode == "plan";
        /// <summary>Stopped while the turn was over and only background work ran: the queue survives and runs next (M/AppStore.swift queueKeptOnStop).</summary>
        private bool queueKeptOnStop;

        private Task StopActiveRun() => owner.Act(async () =>
        {
            if (stopping) return;
            composerSubmissionVersion++;
            queueKeptOnStop = BackgroundQueuePolicy.StopKeepsQueue(Session.BackgroundWork);
            queueDrainTimer.Stop(); if (!queueKeptOnStop) CancelQueueOnStop();
            // The rows are drawn as the stop leaves them: none steers into a run being stopped.
            stopping = true; RenderQueuedInputs(); RefreshComposerState();
            try { await owner.service.StopAsync(id); }
            finally { stopping = false; if (QueuePaneAlive) Refresh(); }
        });

        /// <summary>A stop cancels what waited, and the transcript says how many requests that was (M/AppStore.swift:926-927).</summary>
        private void CancelQueueOnStop()
        {
            var notice = QueuedInputBuffer.SettleNotice("stopped", queuedInputs.Items.Count);
            queuedInputs.Clear();
            if (notice is not null && QueuePaneAlive) _ = SlashNote(notice);
        }

        private void RefreshQueuedComposer(bool busy)
        {
            // Until start is acknowledged, the still-visible draft belongs to
            // that submission. Keep its stop action instead of presenting it
            // as a new queue item (which would also disable the primary button).
            var queueable = busy && !starting && !queueStarting && Session.Kind == "claude" && HasComposerContent;
            if (queueStopButton is not null) { queueStopButton.Visibility = queueable ? Visibility.Visible : Visibility.Collapsed; queueStopButton.IsEnabled = !stopping; }
            // The second send: a new worktree for written words in a pane that may have children; otherwise Ctrl+Enter queues, as Enter.
            var second = queueable ? owner.service.SecondSend(Session) : ComposerSecondSend.Queue;
            // Where this pane's own agent cannot have a child in its mode but another agent's can, the same button shows for its menu alone.
            worktreeMenuOnly = queueable && second == ComposerSecondSend.Queue && owner.service.WorktreeMenuOnly(Session, LaunchesInPlanMode);
            offersWorktree = (second == ComposerSecondSend.Worktree || worktreeMenuOnly) && !string.IsNullOrWhiteSpace(input.Text);
            if (worktreeTipIsMenu != worktreeMenuOnly) { worktreeTipIsMenu = worktreeMenuOnly; ToolTipService.SetToolTip(worktreeButton, Locale.Get(worktreeMenuOnly ? "queue.worktreeMenuHintMac" : "queue.worktreeHint")); }
            worktreeHost.Visibility = offersWorktree ? Visibility.Visible : Visibility.Collapsed;
            if (queueable)
            {
                ShowSendSymbol("queue"); send.IsEnabled = !starting && !queueStarting && !attachmentsLoading && !stopping && queuedInputs.Items.Count < QueuedInputBuffer.MaximumItems && owner.ManualMutationBlockReason(Session) is null;
                AutomationProperties.SetName(send, Locale.Get("queue.add")); ToolTipService.SetToolTip(send, Locale.Get("queue.addHint"));
            }
            worktreeButton.IsEnabled = offersWorktree && send.IsEnabled;
            PaintWorktree(); RenderQueuedInputs();
        }

        internal void ReceiveQueueRunEvent(RunEvent value)
        {
            if (value.Type != "status") return;
            var kept = value.Status == "stopped" && queueKeptOnStop; if (value.Status is "stopped" or "completed" or "idle" or "error") queueKeptOnStop = false;
            // A busy pane learns whether its folder can host a child worktree for Ctrl+Enter.
            if (value.Status == "running") owner.service.RefreshWorktreeReadiness(Session);
            // A stop from anywhere cancels what still waits and an error holds it: either is said in the transcript (M/AppStore.swift:922-929).
            if (QueuedInputBuffer.SettleNotice(value.Status ?? "", queuedInputs.Items.Count, kept) is { } notice && QueuePaneAlive) _ = SlashNote(notice);
            if (queuedInputs.Settle(value.Status ?? "", kept)) queueDrainTimer.Start();
            else if (value.Status is "error" or "stopped") queueDrainTimer.Stop();
            RenderQueuedInputs();
        }

        /// <summary>The editor sits 9 under the card's edge, or straight under the attachments or the queue, and the queue 6 under the attachments (M/SessionPaneView.swift:557, 583).</summary>
        private void FitComposerSpacing()
        {
            var attached = pendingAttachments.Count > 0; var queued = queuedInputs.Items.Count > 0 || owner.service.HeldItems(id).Count > 0;
            queuedInputHost.Margin = new Thickness(DesignMetrics.Inset.ComposerInnerH, attached ? DesignMetrics.Spacing.Sm : DesignMetrics.Spacing.Md, DesignMetrics.Inset.ComposerInnerH, 0);
            if (inputRow is not null) inputRow.Margin = new Thickness(DesignMetrics.Inset.ComposerInnerH, attached || queued ? 0 : DesignMetrics.Spacing.Sm, DesignMetrics.Inset.ComposerInnerH, 0);
        }

        /// <summary>
        /// The waiting requests (M/QueuedInputsView.swift): a line that counts them behind an accent clock; then delegation's held
        /// notices and follow-ups first, each with a pin and no remove button; then each request on the subtle wash at radius 8 — its
        /// number in 10 semibold <c>ink2</c>, its words in 11 (two lines), its attachments' names in 10 <c>ink2</c> and a remove
        /// button. Each human row has 바로 실행 before its remove button, and the first held row before its pin (<see cref="RunNowButton"/>),
        /// decided once for the pane as it is now (<see cref="RunNowRows"/>); the other held rows have none.
        /// </summary>
        private void RenderQueuedInputs()
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var accent = b.Brush(DesignToken.Accent);
            var held = owner.service.HeldItems(id); var total = held.Count + queuedInputs.Items.Count;
            queuedInputHost.Children.Clear(); queuedInputHost.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
            FitComposerSpacing();
            if (total == 0) return;
            var busy = Session.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(Session);
            var runNow = RunNowRows();
            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            header.Children.Add(new FontIcon { Glyph = "", TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            var waitsOnBackground = BackgroundQueuePolicy.WaitsOnBackground(Session.BackgroundWork, queuedInputs.Items.Count);
            var count = new TextBlock { Text = Locale.Get(busy ? "queue.waitingBusy" : "queue.waiting", new Dictionary<string, string> { ["count"] = total.ToString() }) + (waitsOnBackground ? "\n" + Locale.Get(owner.service.SecondSend(Session) == ComposerSecondSend.Worktree ? "queue.waitingOnBackground.windowsWorktree" : "queue.waitingOnBackground.windows") : ""), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (waitsOnBackground) AutomationProperties.SetAutomationId(count, "queue-background-" + id);
            Grid.SetColumn(count, 1); header.Children.Add(count);
            queuedInputHost.Children.Add(header);
            var index = 0;
            foreach (var item in held)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs) }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var number = new TextBlock { Text = (++index).ToString(), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, Width = 14, TextAlignment = TextAlignment.Right, Margin = new(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); row.Children.Add(number);
                var words = new TextBlock { Text = item.Text, TextSize = 11, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(words, 1); row.Children.Add(words);
                // The first held row's 바로 실행 sends them all; the others draw nothing (Hidden).
                if (RunNowButton(item.Id, runNow.GetValueOrDefault(item.Id, QueueRunNowPath.Hidden), held.Count) is { } runButton) { Grid.SetColumn(runButton, 2); row.Children.Add(runButton); }
                // Held for the pane, not the human's request: a pin where the remove button would be (M/QueuedInputsView.swift:39-41).
                var pin = new FontIcon { Glyph = "", TextSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, TextWidth = 18, TextHeight = 16, VerticalAlignment = VerticalAlignment.Top };
                ToolTipService.SetToolTip(pin, Locale.Get("queue.heldHelp")); AutomationProperties.SetName(pin, Locale.Get("queue.held")); Grid.SetColumn(pin, 3); row.Children.Add(pin);
                AutomationProperties.SetAutomationId(row, "queue-held-" + item.Id);
                queuedInputHost.Children.Add(new Border { Child = row, CornerRadius = new(DesignMetrics.Radius.Row), Background = b.Subtle });
            }
            foreach (var item in queuedInputs.Items)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs) }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var number = new TextBlock { Text = (++index).ToString(), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, Width = 14, TextAlignment = TextAlignment.Right, Margin = new(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); row.Children.Add(number);
                var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                words.Children.Add(new TextBlock { Text = item.Text.Length == 0 ? Locale.Get("queue.attachmentsOnly") : item.Text, TextSize = 11, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                if (item.Attachments.Count > 0) words.Children.Add(new TextBlock { Text = string.Join(", ", item.Attachments.Select(file => file.Name)), TextSize = 10, Foreground = ink2, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(words, 1); row.Children.Add(words);
                if (RunNowButton(item.Id, runNow.GetValueOrDefault(item.Id, QueueRunNowPath.Hidden), held.Count) is { } runButton) { Grid.SetColumn(runButton, 2); row.Children.Add(runButton); }
                var remove = Button("×", () => { queuedInputs.Remove(item.Id); RenderQueuedInputs(); RefreshComposerState(); return Task.CompletedTask; }); remove.Content = new FontIcon { Glyph = "", TextSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                remove.Width = 18; remove.Height = 16; remove.MinWidth = 0; remove.MinHeight = 0; remove.Padding = new(0); remove.BorderThickness = new(0); remove.CornerRadius = new(DesignMetrics.Radius.FileRow); remove.VerticalAlignment = VerticalAlignment.Top;
                owner.PaintPlainButton(remove, b.Transparent, b.Subtle, ink: ink2);
                AutomationProperties.SetName(remove, Locale.Get("queue.remove")); AutomationProperties.SetAutomationId(remove, "queue-remove-" + item.Id); Grid.SetColumn(remove, 3); row.Children.Add(remove);
                AutomationProperties.SetAutomationId(row, "queue-item-" + item.Id);
                queuedInputHost.Children.Add(new Border { Child = row, CornerRadius = new(DesignMetrics.Radius.Row), Background = b.Subtle });
            }
        }

        /// <summary>
        /// 바로 실행 beside a row's remove button (or the first held row's pin) in the old 다음 실행's look: the play triangle, or the
        /// branch on a worktree row, at 8 and "바로 실행" at 10 medium in the accent (M/QueuedInputsView.swift runNowButton). A row that
        /// cannot run now keeps its slot with the button disabled in <c>ink2</c>; a disabled control shows no tooltip, so the reason
        /// sits on a transparent wrapper, and in the button's help text. A worktree row's right-click (or Shift+F10, or the menu key)
        /// picks the child's agent; an agent-menu row's click opens that menu. Null for <see cref="QueueRunNowPath.Hidden"/>.
        /// </summary>
        private FrameworkElement? RunNowButton(string itemId, QueueRunNowPath path, int heldCount)
        {
            if (path == QueueRunNowPath.Hidden) return null;
            var b = owner.brushes; var waits = !QueueRunNow.Runs(path); var branch = path is QueueRunNowPath.Worktree or QueueRunNowPath.AgentMenu;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
            var ink = b.Brush(waits ? DesignToken.Ink2 : DesignToken.Accent);
            label.Children.Add(branch ? ComposerGlyph.QueueBranch().Ink(ink).View : new FontIcon { Glyph = "", TextSize = 8, VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = Locale.Get("queue.runNow"), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
            var tip = RunNowTip(path, heldCount);
            var button = new Button { Content = label, Padding = new(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), MinWidth = 0, MinHeight = 0, BorderThickness = new(0), CornerRadius = new(DesignMetrics.Radius.FileRow), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(button, Locale.Get("queue.runNow")); AutomationProperties.SetHelpText(button, tip); AutomationProperties.SetAutomationId(button, "queue-run-now-" + itemId);
            if (waits)
            {
                owner.PaintPlainButton(button, b.Transparent, b.Subtle, ink: ink, disabledInk: ink);
                button.IsEnabled = false;
                // The wrapper's transparent background is hit-tested, so its tooltip shows over the disabled button.
                var wrapper = new Border { Child = button, Background = b.Transparent, VerticalAlignment = VerticalAlignment.Top };
                ToolTipService.SetToolTip(wrapper, tip);
                return wrapper;
            }
            owner.PaintPlainButton(button, b.Transparent, b.Subtle, ink: ink);
            ToolTipService.SetToolTip(button, tip);
            if (branch)
            {
                // The composer worktree menu's rows without the Ctrl+Enter mark: a pick runs the row in a new child of that agent.
                var menu = new MenuFlyout();
                menu.Opening += (_, _) => FillRunNowMenu(menu, itemId);
                button.ContextFlyout = menu;
                if (path == QueueRunNowPath.AgentMenu) { button.Click += (_, _) => menu.ShowAt(button); return button; }
            }
            button.Click += async (_, _) => await owner.Act(() => RunQueuedNow(itemId));
            return button;
        }

        /// <summary>The tooltip of 바로 실행 for <paramref name="path"/>: which of a new worktree or this pane happens, or why the row waits.</summary>
        private static string RunNowTip(QueueRunNowPath path, int heldCount) => path switch
        {
            QueueRunNowPath.Worktree => Locale.Get("queue.runNowWorktreeHelp"),
            QueueRunNowPath.AgentMenu => Locale.Get("queue.worktreeMenuHintMac"),
            QueueRunNowPath.Steer => Locale.Get("queue.runNowSteerHelp"),
            QueueRunNowPath.Held => Locale.Get("queue.runNowHeldHelp", new Dictionary<string, string> { ["count"] = heldCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            QueueRunNowPath.WaitsForTurn => Locale.Get("queue.runNowUnavailableHelp"),
            QueueRunNowPath.WaitsForHeld => Locale.Get("queue.runNowAfterHeldHelp"),
            QueueRunNowPath.WaitsForUpdate => Locale.Get("queue.runNowUpdateHelp"),
            _ => Locale.Get("queue.runNowHereHelp"),
        };

        /// <summary>The pane as 바로 실행 reads it (M/AppStore.swift queueRunNowPane), worked out once for all its rows.</summary>
        private QueueRunNow.Pane RunNowPane()
        {
            var pane = Session; var second = owner.service.SecondSend(pane);
            // A manual CLI update or plugin change holds the pane's start as a background update does (StartNextQueuedInput passes), also
            // in a child recording its last run's end; such a child is otherwise a busy pane, which starts nothing yet. Neither offers a
            // here that would do nothing (M/AppStore.swift queueRunNowPane).
            var holds = owner.BackgroundUpdateHolds(pane) || owner.ManualUpdateReason(pane) is not null;
            var recording = !holds && owner.ManualMutationBlockReason(pane) is not null;
            return new(pane.Status == "running" || starting || queueStarting || holds || recording, holds, second,
                second == ComposerSecondSend.Queue && owner.service.WorktreeMenuOnly(pane, LaunchesInPlanMode),
                pane.Kind == "claude" && pane.Provider == "claude" && Workspace.Remote is null && !stopping && !recording && pane.BackgroundWork?.WaitingOnBackground == true
                    && !MightyGraphSupport.NoticeTurnRunning(pane.GraphRuns ?? []),
                owner.service.HeldItems(id).Count);
        }

        /// <summary>What 바로 실행 does on each row of the pane (held first, then the human's), by row id, the pane read once (M/AppStore.swift queueRunNowRows).</summary>
        internal IReadOnlyDictionary<string, QueueRunNowPath> RunNowRows()
        {
            var pane = RunNowPane(); var rows = new Dictionary<string, QueueRunNowPath>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in owner.service.HeldItems(id)) rows.TryAdd(item.Id, QueueRunNow.Decide(index++, "", 0, pane));
            // An Antigravity card's follow-up (its grant or card) is never sent to a worktree or steered.
            foreach (var item in queuedInputs.Items) rows.TryAdd(item.Id, QueueRunNow.Decide(null, item.Text, item.Attachments.Count, pane, QueueRunNow.IsContinuation(item)));
            return rows;
        }

        /// <summary>What 바로 실행 on the row <paramref name="itemId"/> (held or the human's) does now (M/AppStore.swift queueRunNow).</summary>
        internal QueueRunNowPath RunNowFor(string itemId)
        {
            var heldIndex = owner.service.HeldItems(id).ToList().FindIndex(item => item.Id == itemId);
            if (heldIndex >= 0) return QueueRunNow.Decide(heldIndex, "", 0, RunNowPane());
            return queuedInputs.Items.FirstOrDefault(item => item.Id == itemId) is { } row
                ? QueueRunNow.Decide(null, row.Text, row.Attachments.Count, RunNowPane(), QueueRunNow.IsContinuation(row))
                : QueueRunNowPath.Hidden;
        }

        /// <summary>
        /// 바로 실행 on a queued or held row (M/AppStore.swift runQueuedNow): a new worktree child (of <paramref name="agent"/> when the
        /// row's menu picked one), the row at the head of the queue started now, the row steered into a run that only waits on
        /// background work, or every held item released. <paramref name="quiet"/>: the phone's, whose worktree child is not selected
        /// here. What ran, or null when nothing did (a disabled or hidden row among them): a disabled answer does nothing before the
        /// queue is touched.
        /// </summary>
        internal async Task<QueueRunNowPath?> RunQueuedNow(string itemId, string? agent = null, bool quiet = false)
        {
            if (!QueuePaneAlive) return null;
            var path = RunNowFor(itemId);
            if (!QueueRunNow.Runs(path)) return null;
            if (path == QueueRunNowPath.Held) return await ReleaseHeldNow() ? path : null;
            if (queuedInputs.Items.FirstOrDefault(item => item.Id == itemId) is not { } row) return null;
            if (path == QueueRunNowPath.Worktree || (path == QueueRunNowPath.AgentMenu && agent is not null))
            {
                // A pick from the menu needs the menu still on offer; a plain click runs the pane's own agent. An Antigravity card's
                // continuation never leaves its pane (QueueRunNow.Decide).
                if (QueueRunNow.IsContinuation(row) || agent is not null && !owner.service.OffersWorktreeMenu(Session, LaunchesInPlanMode)) return null;
                queuedInputs.Remove(row.Id); RenderQueuedInputs(); RefreshComposerState();
                RunInNewWorktree(row.Text, row.Attachments, agent, queued: row, quiet: quiet);
                return QueueRunNowPath.Worktree;
            }
            if (path == QueueRunNowPath.Steer) return await SteerQueuedInput(row) ? path : null;
            if (path != QueueRunNowPath.Here) return null;
            // At the head, the queue's own start takes it: held items go ahead of it in its run, a refused start keeps it there.
            queuedInputs.MoveToFront(row.Id); RenderQueuedInputs();
            // Not taken (refused, or held meanwhile): nothing ran, and the row stays at the head.
            return await StartNextQueuedInput() ? path : null;
        }

        /// <summary>
        /// The held rows' 바로 실행 (M/AppStore.swift runNextHeld): the items delegation holds for the pane go to it now, all of them in
        /// one run, steered into its running Claude run when it has one; with nothing held any more, the first queued row runs, as run
        /// next did. Items no run took stay held, and that throws why.
        /// </summary>
        private async Task<bool> ReleaseHeldNow()
        {
            if (owner.DelegatedRunBlockReason(Session) is { } blocked) throw new InvalidOperationException(blocked);
            switch (await owner.service.RunNextAsync(id))
            {
                case DelegationRelease.NotStarted: throw new InvalidOperationException(Locale.Get("remote.error.startFailed"));
                // Taken meanwhile by another run: the first queued row runs now, as run next did, and only a run that took it counts (M/AppStore.swift runNextHeld).
                case DelegationRelease.NothingHeld: if (!await StartNextQueuedInput()) return false; break;
            }
            if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); }
            return true;
        }

        /// <summary>A queued row is being steered into this pane's run, until the run answers: a second click cannot steer a row twice.</summary>
        private bool steeringQueuedRow;

        /// <summary>
        /// Hands the queued request <paramref name="row"/> (null: the first) into the run that only waits on background work, as a new
        /// turn in the same process (M/AppStore.swift steerQueuedRow); the tracker shows it as a 중간 요청 under the request. False when
        /// it may not (the row has attachments, continues an Antigravity card, or the run does not only wait on background work), a
        /// steer of this pane is under way, or the run refused it: it stays queued.
        /// </summary>
        private async Task<bool> SteerQueuedInput(QueuedComposerInput? row = null)
        {
            // One steer at a time per pane: the row stays queued while the run answers.
            if (steeringQueuedRow) return false;
            // The row asked for, or the first; a row no longer queued is not steered.
            var asked = row?.Id;
            var next = asked is null ? queuedInputs.Items.FirstOrDefault() : queuedInputs.Items.FirstOrDefault(item => item.Id == asked);
            if (next is null || Session.Kind != "claude" || next.Attachments.Count > 0 || QueueRunNow.IsContinuation(next)
                || !BackgroundQueuePolicy.SteersRunNext(Session.BackgroundWork, Session.Provider == "claude" && Workspace.Remote is null, stopping, 1, false)
                // Claude is answering finished background work: a steer now would lose its own answer to that turn's result.
                || MightyGraphSupport.NoticeTurnRunning(Session.GraphRuns ?? [])) return false;
            steeringQueuedRow = true;
            try { if (!await owner.service.TrySteerAsync(id, next.Text)) return false; }
            finally { steeringQueuedRow = false; }
            queuedInputs.Remove(next.Id);
            if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); }
            return true;
        }

        /// <summary>
        /// A queued row whose new worktree could not be made or started (M/AppStore+Delegation.swift putBackQueuedRow): back at the
        /// head of the queue (never the composer, and without the 16-row check: it was queued), and the transcript says why. A pane
        /// idle meanwhile runs it now, unless an error paused its queue.
        /// </summary>
        internal async Task PutBackQueuedRow(QueuedComposerInput item, string reason)
        {
            if (!QueuePaneAlive) return;
            await SlashNote(Locale.Get("delegation.human.queued", new Dictionary<string, string> { ["reason"] = reason }));
            if (!QueuePaneAlive) return;
            var waiting = queuedInputs.Items.Count > 0;
            queuedInputs.Insert(0, item);
            RenderQueuedInputs(); RefreshComposerState();
            if (!waiting && Session.Status is not ("running" or "error") && !starting && !queueStarting && !owner.service.IsSessionRunning(id)) queueDrainTimer.Start();
        }

        /// <summary>
        /// Starts the first queued row now (M/AppStore.swift runNextQueuedRow): true when its run took it. False, never silently, when
        /// nothing did: the pane cannot take it now (busy, held for an update or a plugin change, the row gone meanwhile), the pane cannot
        /// run at all (what waited is dropped and the transcript says why), or its start was refused or no run took it (the row stays at
        /// the head and the transcript says so).
        /// </summary>
        private async Task<bool> StartNextQueuedInput()
        {
            var started = false;
            await owner.Act(async () => { started = await TakeNextQueuedInput(); });
            return started;
        }

        private async Task<bool> TakeNextQueuedInput()
        {
            // An update or a plugin change, by hand or in the background, only holds what waited: it passes (M/AppStore.swift:902-903).
            if (!QueuePaneAlive || starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session) || owner.ManualMutationBlockReason(Session) is not null || queuedInputs.Items.FirstOrDefault() is not { } item) return false;
            // A pane that cannot run at all drops what waited and says why (M/AppStore.swift:904-908), but only once a fresh reading,
            // the CLI looked for again rather than the one held, says so too: a reading taken mid-update may be wrong.
            if (RunBlockReason(PaneRuntime(Session.Provider)) is not null)
            {
                queueStarting = true; RefreshComposerState();
                ProviderRuntime fresh;
                // A remote pane's CLI is its host's: its last answer, never a reading of this PC's CLI in the host's path.
                try { fresh = LocalPath(Workspace) is { } folder ? await owner.ReadProviderAfresh(Session.Provider, folder) : RemoteWorkspaceFeatures.ProviderRuntime(Session.Provider, Workspace.Remote!, owner.service.Remote?.Connection(Workspace.Remote!.ConnectionId)); }
                finally { queueStarting = false; if (QueuePaneAlive) RefreshComposerState(); }
                if (!QueuePaneAlive || !queuedInputs.Items.Contains(item)) return false;
                if (RunBlockReason(fresh) is { } blocked)
                {
                    var notice = QueuedInputBuffer.UnrunnableNotice(queuedInputs.Items.Count, blocked);
                    queuedInputs.Clear(); RenderQueuedInputs(); await SlashNote(notice);
                    return false;
                }
            }
            queueStarting = true; RefreshComposerState();
            string? last = null;
            try
            {
                await PrepareModels();
                if (!QueuePaneAlive || !queuedInputs.Items.Any(queued => queued.Id == item.Id)) return false;
                var pane = Session; last = pane.Logs.LastOrDefault()?.Id;
                var submitted = item.Prepared ? item.Text : await PrepareStyleSubmission(item.Text, item.Attachments.Count > 0);
                var request = await PrepareStyleRunRequest(new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, submitted, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, item.Attachments.ToList()));
                // The decision made when the item was queued wins (§1.17.4); an Antigravity card's continuation carries its rules.
                request = request with { PermissionModeOverride = item.PermissionModeOverride, AntigravityGrant = item.AntigravityGrant, AntigravityCard = item.AntigravityCard };
                // Stop or removal while style consent was open must prevent this request from starting.
                if (!QueuePaneAlive || !queuedInputs.Items.Any(queued => queued.Id == item.Id)) return false;
                // No run took it (the items held for the pane stay held): it stays at the head of the queue, and the transcript says so (M/AppStore+Delegation.swift:191-195).
                if (await owner.StartFromComposer(request) is DelegationRelease.NotStarted) { if (QueuePaneAlive) await SlashNote(QueuedInputBuffer.KeptNotice(Locale.Get("remote.error.startFailed"))); return false; }
                queuedInputs.Remove(item.Id);
                return true;
            }
            // Refused before its run began (nothing of it reached the transcript): the request stays, and the transcript says why (M/AppStore.swift:917-921).
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException) && QueuePaneAlive && Session.Logs.LastOrDefault()?.Id == last)
            {
                await SlashNote(QueuedInputBuffer.KeptNotice(ex.Message));
                throw;
            }
            finally { queueStarting = false; if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); } }
        }
    }
}
