using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>The Antigravity card over the composer, while the pane has one and no Claude request waits (M/ToolPermissionBar.swift:47-50).</summary>
        private Border? antigravityCardView;
        /// <summary>What the drawn card shows (its card, reason and whether an answer is in flight), so a redraw that changes nothing keeps it.</summary>
        private string? antigravityCardDrawn;

        /// <summary>
        /// Draws the pane's Antigravity card in the permission card's place (AntigravityPermissions.cs): every refused action with its kind,
        /// command, address or path and rule, then 거절 / 항상 허용 / 이번만 허용. False when the pane has no card.
        /// </summary>
        private bool TryRenderAntigravity()
        {
            if (owner.service.AntigravityCard(id) is not { } card) { HideAntigravity(); return false; }
            var reason = owner.service.AntigravityError(id); var busy = owner.service.AntigravityAnswering(id, card);
            var drawn = card.Id + "|" + card.RunId + "|" + reason + "|" + busy;
            permissionCard.Visibility = Visibility.Collapsed;
            if (antigravityCardView is null || antigravityCardDrawn != drawn)
            {
                if (antigravityCardView is not null) toolPermissionHost.Children.Remove(antigravityCardView);
                antigravityCardView = AntigravityCard(card, reason, busy); antigravityCardDrawn = drawn;
                toolPermissionHost.Children.Add(antigravityCardView);
            }
            toolPermissionHost.Visibility = Visibility.Visible;
            return true;
        }

        /// <summary>The service says the pane's card changed: drawn again where the permission card goes.</summary>
        internal void RenderAntigravityCard() => RenderToolPermissions(preserveQuestionnaire: true);

        private void HideAntigravity()
        {
            if (antigravityCardView is null) return;
            toolPermissionHost.Children.Remove(antigravityCardView); antigravityCardView = null; antigravityCardDrawn = null;
            if (toolPermissionHost.Children.Count > 0) toolPermissionHost.Children[0].Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The Claude card's shape for what an Antigravity turn was refused (M/ToolPermissionBar.swift AntigravityPermissionCard): the hand
        /// on its amber disc and the title, what the card does, each action in a body that scrolls from 180, the reason an answer did not
        /// go through, and the scope note with Deny, Always allow and Allow once (both off when no rule can allow it, Always allow off with its
        /// reason when a rule may be allowed only once).
        /// </summary>
        private Border AntigravityCard(AntigravityPermissionRequest card, string? reason, bool busy)
        {
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var titleRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            titleRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); titleRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.Children.Add(WaitBadge(ComposerGlyph.Hand().Ink(b.Brush(DesignToken.OnWait)).View));
            var title = new TextBlock { Text = Locale.Get("antigravity.permission.title"), TextSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, TextWrapping = TextWrapping.Wrap, MaxLines = 2, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(title, "antigravity-permission-title-" + id);
            Grid.SetColumn(title, 1); titleRow.Children.Add(title);

            var actions = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(0, 0, DesignMetrics.Spacing.Xs, 0) };
            foreach (var action in card.Actions)
            {
                var block = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                block.Children.Add(new TextBlock { Text = AntigravityPermissionFlow.KindLabel(action.Kind) + " · " + action.DisplayName, TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink2 });
                if (action.Target is { } target)
                {
                    // A command or a path in mono on the raised strip; an address or anything else as words.
                    if (action.Kind is "command" or "file")
                        block.Children.Add(new Border { Child = new TextBlock { Text = target, TextFont = TextArea.Code, TextSize = 11, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                            Padding = new Thickness(DesignMetrics.Spacing.Sm), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = b.Brush(DesignToken.CardRaised) });
                    else block.Children.Add(new TextBlock { Text = target, TextSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                }
                block.Children.Add(AntigravityPermissionFlow.RuleLine(action) is { } line
                    ? new TextBlock { Text = line, TextFont = TextArea.Code, TextSize = 10, Foreground = ink2, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true }
                    : new TextBlock { Text = Locale.Get("antigravity.permission.noRule"), TextSize = 10, Foreground = b.Brush(DesignToken.WaitText), TextWrapping = TextWrapping.Wrap });
                // A rule that may be allowed only once says so under it; 항상 허용 is off for the card.
                if (AntigravityPermissionFlow.OnceOnlyText(action) is { } onceOnly) block.Children.Add(new TextBlock { Text = onceOnly, TextSize = 10, Foreground = b.Brush(DesignToken.WaitText), TextWrapping = TextWrapping.Wrap });
                actions.Children.Add(block);
            }

            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(titleRow);
            body.Children.Add(new TextBlock { Text = Locale.Get("antigravity.permission.subtitle"), TextSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new ScrollViewer { Content = actions, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
            if (reason is not null)
            {
                var failed = new TextBlock { Text = reason, TextSize = 10, Foreground = b.Brush(DesignToken.ErrText), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
                AutomationProperties.SetAutomationId(failed, "antigravity-permission-error-" + id);
                body.Children.Add(failed);
            }

            var footer = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto }) footer.ColumnDefinitions.Add(new() { Width = width });
            footer.Children.Add(new TextBlock { Text = Locale.Get("antigravity.permission.scopeNote"), TextSize = 10, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            if (busy)
            {
                var sending = new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = ink2 };
                Grid.SetColumn(sending, 1); footer.Children.Add(sending);
            }
            Button Answer(string key, string automation, AntigravityPermissionDecision decision, bool prominent, bool enabled, int column)
            {
                var label = Locale.Get(key);
                var button = new Button { Content = label, IsEnabled = enabled && !busy }; PaintCardButton(button, prominent);
                AutomationProperties.SetName(button, label); AutomationProperties.SetAutomationId(button, automation + id);
                button.Click += async (_, _) => await owner.Act(async () => { await AnswerAntigravityPermission(card, decision); });
                Grid.SetColumn(button, column); footer.Children.Add(button);
                return button;
            }
            Answer("antigravity.permission.deny", "antigravity-permission-deny-", AntigravityPermissionDecision.Deny, false, true, 2);
            var always = Answer("antigravity.permission.allowAlways", "antigravity-permission-allow-always-", AntigravityPermissionDecision.Always, false, card.CanAllowAlways, 3);
            if (card.CanAllow && !card.CanAllowAlways) ToolTipService.SetToolTip(always, card.Actions.Select(AntigravityPermissionFlow.OnceOnlyText).OfType<string>().FirstOrDefault() ?? Locale.Get("antigravity.permission.onceOnly"));
            Answer("permission.allowOnce", "antigravity-permission-allow-once-", AntigravityPermissionDecision.Once, true, card.CanAllow, 4);
            body.Children.Add(footer);

            var view = WaitCard(body);
            AutomationProperties.SetAutomationId(view, "antigravity-permission-" + id);
            return view;
        }

        /// <summary>
        /// The card's buttons, and the phone's answer (M/AppStore+AntigravityPermissions.swift answerAntigravityPermission). Deny closes the
        /// card. An allow sends the continuation on the pane's conversation now, or queues it behind a busy pane, with the card's rules: put
        /// in place while that run goes (once) or for good (always). Returns why nothing happened, which the card shows too; null when it
        /// went through.
        /// </summary>
        internal async Task<string?> AnswerAntigravityPermission(AntigravityPermissionRequest request, AntigravityPermissionDecision decision)
        {
            var service = owner.service; var notPending = Locale.Get("remote.error.notPendingPermission");
            if (owner.closing || !QueuePaneAlive || service.AntigravityAnswering(id, request) || service.PendingAntigravityCard(id, request.Id, request.RunId) is not { } card) return notPending;
            service.SetAntigravityError(id, null);
            if (decision == AntigravityPermissionDecision.Deny) { service.TakeAntigravityCard(id); return null; }
            string Refuse(string reason) { service.SetAntigravityError(id, reason); return reason; }
            // No rule, or 항상 허용 for a rule that may be allowed only once.
            if (AntigravityPermissionFlow.Refusal(card, decision) is { } refusal) return Refuse(refusal);
            if (AntigravityPermissionFlow.Continuation(card, decision) is not { } continuation) return Refuse(Locale.Get("antigravity.permission.noRule"));
            if (!service.BeginAntigravityAnswer(id, card)) return notPending;
            try
            {
                // A local pane's rules go in this PC's settings: one that cannot take them is refused here, before anything is sent.
                if (!service.IsRemoteSession(id))
                {
                    try { await service.CheckAntigravitySettingsAsync(); }
                    catch (AntigravitySettingsException ex) { return Refuse(ex.Message); }
                }
                if (owner.closing || !QueuePaneAlive || service.PendingAntigravityCard(id, card.Id, card.RunId) is null || Session is not { Kind: "claude", Provider: "gemini" } pane) return notPending;
                if (RunBlockReason(PaneRuntime(pane.Provider)) is { } blocked) return Refuse(blocked);
                if (MobileBusy)
                {
                    // The composer's rule for a busy pane: it waits in the queue, and its rules go in place when that run starts.
                    if (queuedInputs.Items.Count >= QueuedInputBuffer.MaximumItems) return Refuse(Locale.Get("queue.fullCount", new Dictionary<string, string> { ["count"] = QueuedInputBuffer.MaximumItems.ToString() }));
                    queuedInputs.Add(continuation.Prompt, [], null, prepared: true, antigravityGrant: continuation.Grant, antigravityCard: card);
                    service.TakeAntigravityCard(id);
                    RenderQueuedInputs(); RefreshComposerState();
                    if (!MobileBusy) queueDrainTimer.Start();
                    return null;
                }
                if (!service.IsRemoteWorkspace(pane.WorkspaceId) && owner.AdmissionRefusal(pane.Kind, pane.Provider) is { } admission) return Refuse(admission);
                var run = new StartRunRequest(id, pane.WorkspaceId, pane.Kind, continuation.Prompt, RegisteredModelsFor(pane.Provider, Workspace, service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId)
                    { AntigravityGrant = continuation.Grant, AntigravityCard = card };
                service.TakeAntigravityCard(id);
                starting = true; RefreshComposerState();
                try { await service.StartReachedAsync(run); return null; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // It did not start (agy's settings refused the rules meanwhile, a remote host said no): the card comes back with the reason.
                    if (owner.closing || !QueuePaneAlive) return ex.Message;
                    service.RestoreAntigravityCard(id, card);
                    return Refuse(ex.Message);
                }
                finally { starting = false; if (QueuePaneAlive) Refresh(); }
            }
            finally { service.EndAntigravityAnswer(id, card); }
        }
    }
}
