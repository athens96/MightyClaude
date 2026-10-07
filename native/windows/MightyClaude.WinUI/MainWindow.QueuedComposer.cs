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
        private bool queueStarting;
        private int composerSubmissionVersion;
        private bool HasComposerContent => !string.IsNullOrWhiteSpace(input.Text) || pendingAttachments.Count > 0;

        /// <summary>Adds the small stop square that stands before send while a draft waits to be queued (the toolbar's right cluster).</summary>
        /// <summary>The small stop square beside send: 4pt under the toolbar's height, as the Mac's.</summary>
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
            queueDrainTimer.Tick += async (_, _) =>
            {
                if (!QueuePaneAlive) { queueDrainTimer.Stop(); return; }
                if (owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session) || owner.ManualMutationBlockReason(Session) is not null || starting || queueStarting) return;
                queueDrainTimer.Stop(); await StartNextQueuedInput();
            };
        }

        private bool QueuePaneAlive => !owner.closing && owner.views.TryGetValue(id, out var pane) && ReferenceEquals(pane, this)
            && owner.service.Snapshot.Sessions.Any(p => p.Id == id);

        /// <returns>True when the busy path handled this send, including validation refusals.</returns>
        private async Task<bool> DeferBusyComposer(bool steering)
        {
            if (Session.Status != "running" && !starting && !owner.service.IsSessionRunning(id) && !owner.BackgroundUpdateHolds(Session)) return false;
            if (Session.Kind != "claude" || !HasComposerContent || starting || composingInput || attachmentsLoading) return true;
            var text = input.Text; var files = pendingAttachments.ToArray();
            var pane = Session;
            // The same request validation applies before consuming a draft even though it runs later.
            _ = new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, text, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files).Validate();
            // A turn that is over but still running background agents takes new input in the same process
            // (M/AppStore.swift submit); the queue is the fallback. A plan-mode style's new request waits and
            // starts fresh in plan mode (§1.17.4), and the queued item keeps that decision.
            var plans = LaunchesInPlanMode;
            var joins = BackgroundQueuePolicy.ComposerJoins(steering, pane.BackgroundWork, plans);
            var steered = joins && files.Length == 0 && pane.Provider == "claude" && !starting && await owner.service.TrySteerAsync(id, text);
            if (!QueuePaneAlive) return true;
            if (!steered) queuedInputs.Add(text, files, BackgroundQueuePolicy.QueuedOverride(plans));
            var consumed = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => consumed.Contains(file.Id)); RefreshAttachments();
            if (input.Text == text) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
            RefreshComposerState(); RenderQueuedInputs();
            // The stream may finish while a steering attempt is being admitted.
            if (!steered && Session.Status is "completed" or "idle") queueDrainTimer.Start();
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
            queueDrainTimer.Stop(); if (!queueKeptOnStop) queuedInputs.Clear(); RenderQueuedInputs();
            stopping = true; RefreshComposerState();
            try { await owner.service.StopAsync(id); }
            finally { stopping = false; if (QueuePaneAlive) Refresh(); }
        });

        private void RefreshQueuedComposer(bool busy)
        {
            // Until start is acknowledged, the still-visible draft belongs to
            // that submission. Keep its stop action instead of presenting it
            // as a new queue item (which would also disable the primary button).
            var queueable = busy && !starting && !queueStarting && Session.Kind == "claude" && HasComposerContent;
            if (queueStopButton is not null) { queueStopButton.Visibility = queueable ? Visibility.Visible : Visibility.Collapsed; queueStopButton.IsEnabled = !stopping; }
            if (queueable)
            {
                ShowSendSymbol("queue"); send.IsEnabled = !starting && !queueStarting && !attachmentsLoading && !stopping && queuedInputs.Items.Count < QueuedInputBuffer.MaximumItems;
                AutomationProperties.SetName(send, Locale.Get("queue.add")); ToolTipService.SetToolTip(send, Locale.Get(Session.Provider == "claude" ? "queue.addOrSteerHint" : "queue.addHint"));
            }
            RenderQueuedInputs();
        }

        internal void ReceiveQueueRunEvent(RunEvent value)
        {
            if (value.Type != "status") return;
            var kept = value.Status == "stopped" && queueKeptOnStop; if (value.Status is "stopped" or "completed" or "idle" or "error") queueKeptOnStop = false;
            if (queuedInputs.Settle(value.Status ?? "", kept)) queueDrainTimer.Start();
            else if (value.Status is "error" or "stopped") queueDrainTimer.Stop();
            RenderQueuedInputs();
        }

        /// <summary>The editor sits 9 under the card's edge, or straight under the attachments or the queue, and the queue 6 under the attachments (M/SessionPaneView.swift:557, 583).</summary>
        private void FitComposerSpacing()
        {
            var attached = pendingAttachments.Count > 0; var queued = queuedInputs.Items.Count > 0;
            queuedInputHost.Margin = new Thickness(DesignMetrics.Inset.ComposerInnerH, attached ? DesignMetrics.Spacing.Sm : DesignMetrics.Spacing.Md, DesignMetrics.Inset.ComposerInnerH, 0);
            if (inputRow is not null) inputRow.Margin = new Thickness(DesignMetrics.Inset.ComposerInnerH, attached || queued ? 0 : DesignMetrics.Spacing.Sm, DesignMetrics.Inset.ComposerInnerH, 0);
        }

        /// <summary>
        /// The waiting requests (M/QueuedInputsView.swift:15-44): a line that counts them behind an accent clock, with
        /// "run next" while the pane is idle; then each request on the subtle wash at radius 8 — its number in 10
        /// semibold <c>ink2</c>, its words in 11 (two lines), its attachments' names in 10 <c>ink2</c> and a remove button.
        /// </summary>
        private void RenderQueuedInputs()
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var accent = b.Brush(DesignToken.Accent);
            queuedInputHost.Children.Clear(); queuedInputHost.Visibility = queuedInputs.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            FitComposerSpacing();
            if (queuedInputs.Items.Count == 0) return;
            var busy = Session.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(Session);
            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = "", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            var waitsOnBackground = BackgroundQueuePolicy.WaitsOnBackground(Session.BackgroundWork, LaunchesInPlanMode, queuedInputs.Items.Count);
            var count = new TextBlock { Text = Locale.Get(busy ? "queue.waitingBusy" : "queue.waiting", new Dictionary<string, string> { ["count"] = queuedInputs.Items.Count.ToString() }) + (waitsOnBackground ? "\n" + Locale.Get("queue.waitingOnBackground.windows") : ""), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (waitsOnBackground) AutomationProperties.SetAutomationId(count, "queue-background-" + id);
            Grid.SetColumn(count, 1); header.Children.Add(count);
            if (!busy)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
                label.Children.Add(new FontIcon { Glyph = "", FontSize = 8, VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = Locale.Get("queue.runNext"), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
                var next = Button(Locale.Get("queue.runNext"), StartNextQueuedInput); next.Content = label; next.Padding = new(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1); next.MinWidth = 0; next.MinHeight = 0; next.BorderThickness = new(0); next.CornerRadius = new(DesignMetrics.Radius.FileRow);
                owner.PaintPlainButton(next, b.Transparent, b.Subtle, ink: accent);
                AutomationProperties.SetAutomationId(next, "queue-run-" + id); Grid.SetColumn(next, 2); header.Children.Add(next);
            }
            queuedInputHost.Children.Add(header);
            var index = 0;
            foreach (var item in queuedInputs.Items)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs) }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var number = new TextBlock { Text = (++index).ToString(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2, Width = 14, TextAlignment = TextAlignment.Right, Margin = new(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); row.Children.Add(number);
                var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                words.Children.Add(new TextBlock { Text = item.Text.Length == 0 ? Locale.Get("queue.attachmentsOnly") : item.Text, FontSize = 11, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                if (item.Attachments.Count > 0) words.Children.Add(new TextBlock { Text = string.Join(", ", item.Attachments.Select(file => file.Name)), FontSize = 10, Foreground = ink2, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(words, 1); row.Children.Add(words);
                var remove = Button("×", () => { queuedInputs.Remove(item.Id); RenderQueuedInputs(); RefreshComposerState(); return Task.CompletedTask; }); remove.Content = new FontIcon { Glyph = "", FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                remove.Width = 18; remove.Height = 16; remove.MinWidth = 0; remove.MinHeight = 0; remove.Padding = new(0); remove.BorderThickness = new(0); remove.CornerRadius = new(DesignMetrics.Radius.FileRow); remove.VerticalAlignment = VerticalAlignment.Top;
                owner.PaintPlainButton(remove, b.Transparent, b.Subtle, ink: ink2);
                AutomationProperties.SetName(remove, Locale.Get("queue.remove")); AutomationProperties.SetAutomationId(remove, "queue-remove-" + item.Id); Grid.SetColumn(remove, 2); row.Children.Add(remove);
                AutomationProperties.SetAutomationId(row, "queue-item-" + item.Id);
                queuedInputHost.Children.Add(new Border { Child = row, CornerRadius = new(DesignMetrics.Radius.Row), Background = b.Subtle });
            }
        }

        private Task StartNextQueuedInput() => owner.Act(async () =>
        {
            if (!QueuePaneAlive || starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session) || owner.ManualMutationBlockReason(Session) is not null || queuedInputs.Items.FirstOrDefault() is not { } item) return;
            queueStarting = true; RefreshComposerState();
            try
            {
                var pane = Session;
                var submitted = await PrepareStyleSubmission(item.Text, item.Attachments.Count > 0);
                var request = await PrepareStyleRunRequest(new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, submitted, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, item.Attachments.ToList()));
                // The decision made when the item was queued wins (§1.17.4).
                request = request with { PermissionModeOverride = item.PermissionModeOverride };
                // Stop or removal while style consent was open must prevent this request from starting.
                if (!QueuePaneAlive || !queuedInputs.Items.Any(queued => queued.Id == item.Id)) return;
                await owner.StartFromComposer(request);
                queuedInputs.Remove(item.Id);
            }
            finally { queueStarting = false; if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); } }
        });
    }
}
