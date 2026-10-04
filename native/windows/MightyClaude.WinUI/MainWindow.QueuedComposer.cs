using MightyClaude.Core;
using Microsoft.UI;
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
        private readonly StackPanel queuedInputHost = new() { Spacing = 4, Visibility = Visibility.Collapsed };
        private readonly DispatcherTimer queueDrainTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private Button? queueStopButton;
        private bool queueStarting;
        private int composerSubmissionVersion;
        private bool HasComposerContent => !string.IsNullOrWhiteSpace(input.Text) || pendingAttachments.Count > 0;

        private void InitializeQueuedComposer(StackPanel composer, Grid bottom)
        {
            composer.Children.Insert(Math.Min(2, composer.Children.Count), queuedInputHost);
            AutomationProperties.SetAutomationId(queuedInputHost, "queue-" + id);
            queueStopButton = Button("■", StopActiveRun); queueStopButton.Width = queueStopButton.Height = 28; queueStopButton.MinWidth = 0; queueStopButton.Padding = new(0); queueStopButton.Visibility = Visibility.Collapsed;
            queueStopButton.Background = new SolidColorBrush(Colors.Firebrick); queueStopButton.Foreground = new SolidColorBrush(Colors.White);
            AutomationProperties.SetName(queueStopButton, Locale.Get("composer.stop.name"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            bottom.Children.Remove(send); actions.Children.Add(queueStopButton); actions.Children.Add(send); Grid.SetColumn(actions, 2); bottom.Children.Add(actions);
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
            var steered = steering && files.Length == 0 && pane.Provider == "claude" && !starting && await owner.service.TrySteerAsync(id, text);
            if (!QueuePaneAlive) return true;
            if (!steered) queuedInputs.Add(text, files);
            var consumed = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => consumed.Contains(file.Id)); RefreshAttachments();
            if (input.Text == text) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
            RefreshComposerState(); RenderQueuedInputs();
            // The stream may finish while a steering attempt is being admitted.
            if (!steered && Session.Status is "completed" or "idle") queueDrainTimer.Start();
            return true;
        }

        private Task ComposerPrimaryAction() => starting || queueStarting || Session.Status == "running" && !HasComposerContent ? StopActiveRun() : Send();

        private Task StopActiveRun() => owner.Act(async () =>
        {
            if (stopping) return;
            composerSubmissionVersion++;
            queueDrainTimer.Stop(); queuedInputs.Clear(); RenderQueuedInputs();
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
                send.Content = "+"; send.FontSize = 20; send.IsEnabled = !starting && !queueStarting && !attachmentsLoading && !stopping && queuedInputs.Items.Count < QueuedInputBuffer.MaximumItems;
                AutomationProperties.SetName(send, Locale.Get("queue.add")); ToolTipService.SetToolTip(send, Locale.Get(Session.Provider == "claude" ? "queue.addOrSteerHint" : "queue.addHint"));
            }
            RenderQueuedInputs();
        }

        internal void ReceiveQueueRunEvent(RunEvent value)
        {
            if (value.Type != "status") return;
            if (queuedInputs.Settle(value.Status ?? "")) queueDrainTimer.Start();
            else if (value.Status is "error" or "stopped") queueDrainTimer.Stop();
            RenderQueuedInputs();
        }

        private void RenderQueuedInputs()
        {
            queuedInputHost.Children.Clear(); queuedInputHost.Visibility = queuedInputs.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (queuedInputs.Items.Count == 0) return;
            var busy = Session.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(Session);
            var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = Locale.Get(busy ? "queue.waitingBusy" : "queue.waiting", new Dictionary<string, string> { ["count"] = queuedInputs.Items.Count.ToString() }), FontSize = 10, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            if (!busy)
            {
                var next = Button(Locale.Get("queue.runNext"), StartNextQueuedInput); next.FontSize = 10; next.Padding = new(5, 2, 5, 2); next.MinWidth = 0; Grid.SetColumn(next, 1); header.Children.Add(next);
            }
            queuedInputHost.Children.Add(header);
            foreach (var item in queuedInputs.Items)
            {
                var row = new Grid { ColumnSpacing = 6, Padding = new(8, 5, 8, 5) }; row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var detail = item.Text + (item.Attachments.Count > 0 ? "\n" + string.Join(", ", item.Attachments.Select(file => file.Name)) : "");
                row.Children.Add(new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
                var remove = Button("×", () => { queuedInputs.Remove(item.Id); RenderQueuedInputs(); RefreshComposerState(); return Task.CompletedTask; }); remove.MinWidth = 0; remove.Padding = new(5, 1, 5, 1); AutomationProperties.SetName(remove, Locale.Get("queue.remove")); Grid.SetColumn(remove, 1); row.Children.Add(remove);
                AutomationProperties.SetAutomationId(row, "queue-item-" + item.Id);
                queuedInputHost.Children.Add(new Border { Child = row, CornerRadius = new(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 135, 135, 135)) });
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
                // Stop or removal while style consent was open must prevent this request from starting.
                if (!QueuePaneAlive || !queuedInputs.Items.Any(queued => queued.Id == item.Id)) return;
                await owner.StartFromComposer(request);
                queuedInputs.Remove(item.Id);
            }
            finally { queueStarting = false; if (QueuePaneAlive) { RenderQueuedInputs(); RefreshComposerState(); } }
        });
    }
}
