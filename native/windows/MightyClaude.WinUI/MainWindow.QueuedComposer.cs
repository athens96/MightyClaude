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
        private async Task<bool> DeferBusyComposer(bool secondSend)
        {
            if (Session.Status != "running" && !starting && !owner.service.IsSessionRunning(id) && !owner.BackgroundUpdateHolds(Session)) return false;
            if (Session.Kind != "claude" || !HasComposerContent || starting || composingInput || attachmentsLoading) return true;
            var text = input.Text; var files = pendingAttachments.ToArray();
            var pane = Session;
            // The same request validation applies before consuming a draft even though it runs later.
            _ = new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, text, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files).Validate();
            // Ctrl+Enter on a pane that may have children runs written words now in a new child worktree; files alone wait.
            if (secondSend && !owner.BackgroundUpdateHolds(pane) && !string.IsNullOrWhiteSpace(text) && owner.service.SecondSend(pane) == ComposerSecondSend.Worktree)
            {
                // Taken from the composer now, so a second Ctrl+Enter cannot send it again.
                var sent = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => sent.Contains(file.Id)); RefreshAttachments();
                if (input.Text == text) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
                RunInNewWorktree(text, files);
                RefreshComposerState();
                return true;
            }
            // A plan-mode style's queued request starts fresh in plan mode (§1.17.4), and the queued item keeps that decision.
            queuedInputs.Add(text, files, BackgroundQueuePolicy.QueuedOverride(LaunchesInPlanMode));
            var consumed = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => consumed.Contains(file.Id)); RefreshAttachments();
            if (input.Text == text) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
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
            queueDrainTimer.Stop(); if (!queueKeptOnStop) CancelQueueOnStop(); RenderQueuedInputs();
            stopping = true; RefreshComposerState();
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
        /// The waiting requests (M/QueuedInputsView.swift:15-63): a line that counts them behind an accent clock, with
        /// "run next" while the pane is idle or anything is held; then delegation's held notices and follow-ups first, each
        /// with a pin and no remove button; then each request on the subtle wash at radius 8 — its number in 10 semibold
        /// <c>ink2</c>, its words in 11 (two lines), its attachments' names in 10 <c>ink2</c> and a remove button.
        /// </summary>
        private void RenderQueuedInputs()
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var accent = b.Brush(DesignToken.Accent);
            var held = owner.service.HeldItems(id); var total = held.Count + queuedInputs.Items.Count;
            queuedInputHost.Children.Clear(); queuedInputHost.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
            FitComposerSpacing();
            if (total == 0) return;
            var busy = Session.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(Session);
            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = "", TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            var waitsOnBackground = BackgroundQueuePolicy.WaitsOnBackground(Session.BackgroundWork, queuedInputs.Items.Count);
            var count = new TextBlock { Text = Locale.Get(busy ? "queue.waitingBusy" : "queue.waiting", new Dictionary<string, string> { ["count"] = total.ToString() }) + (waitsOnBackground ? "\n" + Locale.Get(owner.service.SecondSend(Session) == ComposerSecondSend.Worktree ? "queue.waitingOnBackground.windowsWorktree" : "queue.waitingOnBackground.windows") : ""), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (waitsOnBackground) AutomationProperties.SetAutomationId(count, "queue-background-" + id);
            Grid.SetColumn(count, 1); header.Children.Add(count);
            if (DelegationQueueRows.OffersRunNext(busy, held.Count))
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
                label.Children.Add(new FontIcon { Glyph = "", TextSize = 8, VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = Locale.Get("queue.runNext"), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
                var next = Button(Locale.Get("queue.runNext"), RunNext); next.Content = label; next.Padding = new(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1); next.MinWidth = 0; next.MinHeight = 0; next.BorderThickness = new(0); next.CornerRadius = new(DesignMetrics.Radius.FileRow);
                owner.PaintPlainButton(next, b.Transparent, b.Subtle, ink: accent);
                AutomationProperties.SetAutomationId(next, "queue-run-" + id); Grid.SetColumn(next, 2); header.Children.Add(next);
            }
            queuedInputHost.Children.Add(header);
            var index = 0;
            foreach (var item in held)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs) }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var number = new TextBlock { Text = (++index).ToString(), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, Width = 14, TextAlignment = TextAlignment.Right, Margin = new(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); row.Children.Add(number);
                var words = new TextBlock { Text = item.Text, TextSize = 11, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(words, 1); row.Children.Add(words);
                // Held for the pane, not the human's request: a pin where the remove button would be (M/QueuedInputsView.swift:39-41).
                var pin = new FontIcon { Glyph = "", TextSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, TextWidth = 18, TextHeight = 16, VerticalAlignment = VerticalAlignment.Top };
                ToolTipService.SetToolTip(pin, Locale.Get("queue.heldHelp")); AutomationProperties.SetName(pin, Locale.Get("queue.held")); Grid.SetColumn(pin, 2); row.Children.Add(pin);
                AutomationProperties.SetAutomationId(row, "queue-held-" + item.Id);
                queuedInputHost.Children.Add(new Border { Child = row, CornerRadius = new(DesignMetrics.Radius.Row), Background = b.Subtle });
            }
            foreach (var item in queuedInputs.Items)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs) }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var number = new TextBlock { Text = (++index).ToString(), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, Width = 14, TextAlignment = TextAlignment.Right, Margin = new(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); row.Children.Add(number);
                var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                words.Children.Add(new TextBlock { Text = item.Text.Length == 0 ? Locale.Get("queue.attachmentsOnly") : item.Text, TextSize = 11, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                if (item.Attachments.Count > 0) words.Children.Add(new TextBlock { Text = string.Join(", ", item.Attachments.Select(file => file.Name)), TextSize = 10, Foreground = ink2, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(words, 1); row.Children.Add(words);
                var remove = Button("×", () => { queuedInputs.Remove(item.Id); RenderQueuedInputs(); RefreshComposerState(); return Task.CompletedTask; }); remove.Content = new FontIcon { Glyph = "", TextSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                remove.Width = 18; remove.Height = 16; remove.MinWidth = 0; remove.MinHeight = 0; remove.Padding = new(0); remove.BorderThickness = new(0); remove.CornerRadius = new(DesignMetrics.Radius.FileRow); remove.VerticalAlignment = VerticalAlignment.Top;
                owner.PaintPlainButton(remove, b.Transparent, b.Subtle, ink: ink2);
                AutomationProperties.SetName(remove, Locale.Get("queue.remove")); AutomationProperties.SetAutomationId(remove, "queue-remove-" + item.Id); Grid.SetColumn(remove, 2); row.Children.Add(remove);
                AutomationProperties.SetAutomationId(row, "queue-item-" + item.Id);
                queuedInputHost.Children.Add(new Border { Child = row, CornerRadius = new(DesignMetrics.Radius.Row), Background = b.Subtle });
            }
        }

        /// <summary>
        /// Run next (M/AppStore.swift:865-885): the items delegation holds for the pane go first, all of them in one run, steered
        /// into its running Claude run when it has one; only with nothing held does the first queued row run, as before. Held
        /// items no run took stay held.
        /// </summary>
        private Task RunNext() => owner.Act(async () =>
        {
            if (!QueuePaneAlive) return;
            // Nothing held: the first queued row runs at once, as before.
            if (owner.service.HeldItems(id).Count == 0) { await StartNextQueuedInput(); return; }
            if (owner.DelegatedRunBlockReason(Session) is { } blocked) throw new InvalidOperationException(blocked);
            switch (await owner.service.RunNextAsync(id))
            {
                case DelegationRelease.NotStarted: throw new InvalidOperationException(Locale.Get("remote.error.startFailed"));
                case DelegationRelease.NothingHeld: await StartNextQueuedInput(); break;
            }
            if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); }
        });

        private Task StartNextQueuedInput() => owner.Act(async () =>
        {
            // An update or a plugin change, by hand or in the background, only holds what waited: it passes (M/AppStore.swift:902-903).
            if (!QueuePaneAlive || starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session) || owner.ManualMutationBlockReason(Session) is not null || queuedInputs.Items.FirstOrDefault() is not { } item) return;
            // A pane that cannot run at all drops what waited and says why (M/AppStore.swift:904-908), but only once a fresh reading,
            // the CLI looked for again rather than the one held, says so too: a reading taken mid-update may be wrong.
            if (RunBlockReason(PaneRuntime(Session.Provider)) is not null)
            {
                queueStarting = true; RefreshComposerState();
                ProviderRuntime fresh;
                // A remote pane's CLI is its host's: its last answer, never a reading of this PC's CLI in the host's path.
                try { fresh = LocalPath(Workspace) is { } folder ? await owner.ReadProviderAfresh(Session.Provider, folder) : RemoteWorkspaceFeatures.ProviderRuntime(Session.Provider, Workspace.Remote!, owner.service.Remote?.Connection(Workspace.Remote!.ConnectionId)); }
                finally { queueStarting = false; if (QueuePaneAlive) RefreshComposerState(); }
                if (!QueuePaneAlive || !queuedInputs.Items.Contains(item)) return;
                if (RunBlockReason(fresh) is { } blocked)
                {
                    var notice = QueuedInputBuffer.UnrunnableNotice(queuedInputs.Items.Count, blocked);
                    queuedInputs.Clear(); RenderQueuedInputs(); await SlashNote(notice);
                    return;
                }
            }
            queueStarting = true; RefreshComposerState();
            string? last = null;
            try
            {
                await PrepareModels();
                if (!QueuePaneAlive || !queuedInputs.Items.Any(queued => queued.Id == item.Id)) return;
                var pane = Session; last = pane.Logs.LastOrDefault()?.Id;
                var submitted = item.Prepared ? item.Text : await PrepareStyleSubmission(item.Text, item.Attachments.Count > 0);
                var request = await PrepareStyleRunRequest(new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, submitted, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, item.Attachments.ToList()));
                // The decision made when the item was queued wins (§1.17.4); an Antigravity card's continuation carries its rules.
                request = request with { PermissionModeOverride = item.PermissionModeOverride, AntigravityGrant = item.AntigravityGrant, AntigravityCard = item.AntigravityCard };
                // Stop or removal while style consent was open must prevent this request from starting.
                if (!QueuePaneAlive || !queuedInputs.Items.Any(queued => queued.Id == item.Id)) return;
                // No run took it (the items held for the pane stay held): it stays at the head of the queue, and the transcript says so (M/AppStore+Delegation.swift:191-195).
                if (await owner.StartFromComposer(request) is DelegationRelease.NotStarted) { if (QueuePaneAlive) await SlashNote(QueuedInputBuffer.KeptNotice(Locale.Get("remote.error.startFailed"))); return; }
                queuedInputs.Remove(item.Id);
            }
            // Refused before its run began (nothing of it reached the transcript): the request stays, and the transcript says why (M/AppStore.swift:917-921).
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException) && QueuePaneAlive && Session.Logs.LastOrDefault()?.Id == last)
            {
                await SlashNote(QueuedInputBuffer.KeptNotice(ex.Message));
                throw;
            }
            finally { queueStarting = false; if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); } }
        });
    }
}
