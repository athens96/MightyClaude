using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>A smoke run's stand-in for <see cref="DesktopService.AnswerPlan"/>; null answers through the service.</summary>
    internal Action<string, string, PlanDecision>? smokePlanAnswerer;

    private sealed partial class PaneView
    {
        /// <summary>What the plan card keeps between renders: the change request being written and what an answer did.</summary>
        private sealed class PlanDraft(string requestId)
        {
            internal readonly string RequestId = requestId;
            internal bool Revising, Sending;
            internal string Feedback = "";
            internal string? Error;
            internal string Key => $"{RequestId}|{Revising}|{Sending}|{Error}";
        }

        /// <summary>
        /// Where a plan card is: the whole card docked over the composer (the diagram does not draw the plan); the
        /// plan alone in the diagram's block; its answers alone over the composer while the diagram draws it
        /// (<see cref="PlanCardSupport.ComposerShowsPlanActions"/>, macOS PlanBody / PlanActionsCard).
        /// </summary>
        private enum PlanPlace { Dock, Diagram, Actions }

        /// <summary>
        /// One plan card, built once per request and place: its plan document is never torn down while the request
        /// waits; only the controls under it are rebuilt when the card's state changes.
        /// </summary>
        private sealed class PlanCardParts
        {
            internal required string RequestId;
            internal required Border Card;
            /// <summary>The plan's page; null on the answers-only card.</summary>
            internal RichEditBox? Document;
            /// <summary>The answers; null on the diagram's plan-only card.</summary>
            internal StackPanel? Controls;
            /// <summary>창에 맞춤 on the diagram's card, shown while a dragged plan size is saved.</summary>
            internal Button? Fit;
            internal required TextBlock Waiting;
            internal string? Key;
        }

        private PlanDraft? planDraft;
        /// <summary>Each place's card for the waiting plan.</summary>
        private readonly Dictionary<PlanPlace, PlanCardParts> planCards = [];
        private Border? planDockCard;
        private string? planShownId;
        /// <summary>Plan documents on screen, re-rendered in the new theme (their RTF bakes its colours in).</summary>
        private readonly List<(RichEditBox View, string Plan)> planViews = [];
        /// <summary>The answered plans above the composer outside the diagram (M/PlanApprovalCard.swift PlanHistoryStrip).</summary>
        private StackPanel? planHistoryHost;
        private string? planHistoryKey;
        private bool planHistoryOpen;
        private readonly HashSet<string> planHistoryExpanded = [];

        private ToolPermissionRequest? PendingPlan => PlanCardSupport.PendingPlan(toolPermissions);
        private int PendingCount => toolPermissions.Count(p => p.State == "pending");

        /// <summary>The pane shows its Mighty diagram, not the default view, the timeline or the structure (M/MightyCore/PlanCardSupport.swift:34-37).</summary>
        private bool InMightyDiagram
        {
            get
            {
                var pane = Session;
                return pane.Kind == "claude" && MightyGraphSupport.Providers.Contains(pane.Provider) && pane.AgentViewMode == "mighty" && MightyTimeline.Mode(pane) == "diagram";
            }
        }

        /// <summary>Where the composer shows this plan's answers (M/PlanCardSupport.swift:58-62); a pane with a style is the guided one.</summary>
        private PlanCardSupport.ComposerPlaces? ComposerPlanPlace(ToolPermissionRequest plan) => PlanCardSupport.ComposerPlace(plan, InMightyDiagram, Session.GraphRuns ?? [], activeStyle is not null);

        /// <summary>The waiting plan whose answers the style's panel shows, as it shows a question (M/GuidedPanel.swift:93-100); null otherwise.</summary>
        private ToolPermissionRequest? PanelPlan => activeStyle is not null && CardRequests.FirstOrDefault() is { } first && ComposerPlanPlace(first) == PlanCardSupport.ComposerPlaces.PanelActions ? first : null;

        /// <summary>
        /// The plan card in place of the generic permission card (macOS PlanCardSupport.composerPlace): the whole card docked
        /// over the composer; or, while the diagram draws the plan, only its answers — over the composer, or under a style
        /// in the style's panel, which draws them itself (RenderGuidedStyle). Exactly one set of answers shows. False for
        /// any other request.
        /// </summary>
        private bool TryRenderPlan(ToolPermissionRequest current, int count)
        {
            if (!current.CanAnswerPlan || current.Plan is null) { HidePlanDock(); NotePlanShown(null); return false; }
            if (planDraft?.RequestId != current.Id) planDraft = new(current.Id);
            NotePlanShown(current.Id);
            if (questionnaireCard is not null) HideQuestionnaire();
            // The generic card never shows beside a plan, wherever the plan is drawn.
            permissionCard.Visibility = Visibility.Collapsed;
            var place = ComposerPlanPlace(current);
            if (place == PlanCardSupport.ComposerPlaces.PanelActions)
            {
                // Nothing docks here: the answers are in the style's panel.
                if (planDockCard is not null) { toolPermissionHost.Children.Remove(planDockCard); planDockCard = null; }
                toolPermissionHost.Visibility = Visibility.Collapsed;
                return true;
            }
            var card = PlanCard(current, count, place == PlanCardSupport.ComposerPlaces.BarActions ? PlanPlace.Actions : PlanPlace.Dock);
            if (!ReferenceEquals(planDockCard, card) && planDockCard is not null) toolPermissionHost.Children.Remove(planDockCard);
            planDockCard = card;
            if (!toolPermissionHost.Children.Contains(card)) { Detach(card); toolPermissionHost.Children.Add(card); }
            toolPermissionHost.Visibility = Visibility.Visible;
            return true;
        }

        /// <summary>A plan that comes or goes changes the diagram too; a plan gone takes its cards with it.</summary>
        private void NotePlanShown(string? requestId)
        {
            if (planShownId == requestId) return;
            planShownId = requestId;
            if (requestId is null)
            {
                planDraft = null;
                foreach (var parts in planCards.Values) { Detach(parts.Card); if (parts.Document is { } document) ForgetPlanView(document); }
                planCards.Clear();
            }
            QueueGraphRefresh();
        }

        private void HidePlanDock()
        {
            if (planDockCard is null) return;
            toolPermissionHost.Children.Remove(planDockCard); planDockCard = null;
            if (toolPermissionHost.Children.Count > 0) toolPermissionHost.Children[0].Visibility = Visibility.Visible;
        }

        /// <summary>Takes a card out of whatever holds it, so it can be placed again.</summary>
        private static void Detach(FrameworkElement element)
        {
            switch (element.Parent)
            {
                case Border border: border.Child = null; break;
                case Panel panel: panel.Children.Remove(element); break;
            }
        }

        private void ForgetPlanView(RichEditBox view) => planViews.RemoveAll(entry => ReferenceEquals(entry.View, view));

        /// <summary>Both places a plan card can be, after its draft changed.</summary>
        private void RenderPlanEverywhere() { RenderToolPermissions(preserveQuestionnaire: true); QueueGraphRefresh(); }

        /// <summary>
        /// Called when the pane's view mode or its requests change: the plan is docked or drawn in the diagram
        /// again by the same rule, so exactly one card shows.
        /// </summary>
        private void RedecidePlanPlace()
        {
            if (PendingPlan is not null) RenderToolPermissions(preserveQuestionnaire: true);
        }

        /// <summary>The fingerprint part of the diagram's plan block: the plan alone, so only the plan and the count.</summary>
        private string PlanBlockKey => PendingPlan is { } plan ? plan.Id + "|" + PendingCount : "";

        /// <summary>The card for <paramref name="request"/> in one place: built once, its controls brought up to date.</summary>
        private Border PlanCard(ToolPermissionRequest request, int count, PlanPlace place)
        {
            if (!planCards.TryGetValue(place, out var parts) || parts.RequestId != request.Id)
            {
                if (parts is not null) { Detach(parts.Card); if (parts.Document is { } document) ForgetPlanView(document); }
                parts = BuildPlanCard(request, place);
                planCards[place] = parts;
            }
            parts.Waiting.Text = Locale.Get("phone.questionnaire.waiting", new Dictionary<string, string> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            parts.Waiting.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (parts.Fit is { } fit) fit.Visibility = Session.GraphPlanSize is not null ? Visibility.Visible : Visibility.Collapsed;
            var draft = planDraft is { } d && d.RequestId == request.Id ? d : planDraft = new(request.Id);
            if (parts.Controls is not null && parts.Key != draft.Key) { parts.Key = draft.Key; BuildPlanControls(parts, request, draft); }
            return parts.Card;
        }

        /// <summary>
        /// The plan card (M/PlanApprovalCard.swift) for one place: the list on its amber disc, 계획 and when it came,
        /// then — docked or in the diagram — 펼치기 and the plan rendered as Markdown on the raised strip, scrolling
        /// inside; docked or alone over the composer, the controls (<see cref="BuildPlanControls"/>). Over the composer
        /// it is the amber wait card; in the diagram the plan alone fills the block's card, with 창에 맞춤 while a
        /// dragged size is saved.
        /// </summary>
        private PlanCardParts BuildPlanCard(ToolPermissionRequest request, PlanPlace place)
        {
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var plan = request.Plan ?? "";
            var inDiagram = place == PlanPlace.Diagram;
            var body = new Grid { RowSpacing = DesignMetrics.Spacing.Md };
            foreach (var height in new[] { GridLength.Auto, inDiagram ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, GridLength.Auto })
                body.RowDefinitions.Add(new RowDefinition { Height = height });

            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
            header.Children.Add(WaitBadge(ComposerGlyph.Icon("", 11, 22, 22).Ink(b.Brush(DesignToken.OnWait)).View));
            var title = new TextBlock { Text = Locale.Get("plan.card.title"), TextSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); header.Children.Add(title);
            var received = request.ReceivedAt is { } at ? PlanCardSupport.ReceivedText(at) : "";
            var receivedBlock = new TextBlock { Text = received, TextSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(receivedBlock, "plan-received-" + id);
            Grid.SetColumn(receivedBlock, 2); header.Children.Add(receivedBlock);
            var waiting = new TextBlock { TextSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            Grid.SetColumn(waiting, 4); header.Children.Add(waiting);
            Button? fit = null;
            if (inDiagram)
            {
                // The plan block's way back to the window fit, as the newest result's (MainWindow.MightyGraph.cs).
                var label = Locale.Get(MightyGraphViewModel.LocaleKeyResultFitToWindow);
                fit = HeaderButton(new TextBlock { Text = label, TextSize = DesignMetrics.Type.Small }, b.Brush(DesignToken.Accent));
                AutomationProperties.SetAutomationId(fit, "plan-fit-to-window-" + id);
                AutomationProperties.SetName(fit, label); ToolTipService.SetToolTip(fit, label);
                fit.Click += async (_, _) => await FitPlanToWindow();
                Grid.SetColumn(fit, 5); header.Children.Add(fit);
            }
            if (place != PlanPlace.Actions)
            {
                var expand = Button(Locale.Get("plan.card.expand"), () => OpenPlanDocument(Locale.Get("plan.card.title"), received, plan)); PaintCardButton(expand, prominent: false);
                AutomationProperties.SetAutomationId(expand, "plan-expand-" + id);
                Grid.SetColumn(expand, 6); header.Children.Add(expand);
            }
            body.Children.Add(header);

            RichEditBox? document = null;
            if (place != PlanPlace.Actions)
            {
                document = PlanMarkdown(plan, "plan-text-" + id);
                var page = new Border { Child = document, CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.CardRaised), MinHeight = 80 };
                if (!inDiagram) page.MaxHeight = 260;
                Grid.SetRow(page, 1); body.Children.Add(page);
            }

            StackPanel? controls = null;
            if (!inDiagram)
            {
                controls = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
                Grid.SetRow(controls, 2); body.Children.Add(controls);
            }

            // Over the composer the amber wait card; in the diagram the block's own card around the plan alone.
            var card = inDiagram
                ? new Border { Child = body, Padding = new Thickness(DesignMetrics.Spacing.Md), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }
                : WaitCard(body);
            var automation = place switch { PlanPlace.Diagram => "plan-body-", PlanPlace.Actions => "plan-actions-", _ => "plan-card-" };
            AutomationProperties.SetAutomationId(card, automation + id);
            AutomationProperties.SetName(card, Locale.Get("plan.card.title"));
            return new PlanCardParts { RequestId = request.Id, Card = card, Document = document, Controls = controls, Fit = fit, Waiting = waiting };
        }

        /// <summary>
        /// What is under the plan, rebuilt when the card's state changes: the change request when asked for (with
        /// the too-long note while the words are over the bound), an error, then the four answers, the accent one last.
        /// </summary>
        private void BuildPlanControls(PlanCardParts parts, ToolPermissionRequest request, PlanDraft draft)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            if (parts.Controls is not { } controls) return;
            controls.Children.Clear();
            if (draft.Revising)
            {
                Button? send = null;
                var revise = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                var field = new TextBox { AcceptsReturn = true, Text = draft.Feedback, PlaceholderText = Locale.Get("plan.card.revisePlaceholder"), PlaceholderForeground = b.Tertiary, TextSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 120, IsEnabled = !draft.Sending };
                AutomationProperties.SetName(field, Locale.Get("plan.card.revisePlaceholder"));
                AutomationProperties.SetAutomationId(field, "plan-revise-text-" + id);
                var tooLong = new TextBlock { Text = Locale.Get("plan.error.feedbackTooLong"), Foreground = b.Brush(DesignToken.ErrText), TextSize = 11, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(tooLong, "plan-revise-too-long-" + id);
                void Check()
                {
                    tooLong.Visibility = PlanCardSupport.FeedbackTooLong(draft.Feedback) ? Visibility.Visible : Visibility.Collapsed;
                    if (send is not null) send.IsEnabled = !draft.Sending && PlanCardSupport.CanSendRevise(draft.Feedback);
                }
                field.TextChanged += (_, _) => { draft.Feedback = field.Text; Check(); };
                revise.Children.Add(field); revise.Children.Add(tooLong);
                var reviseActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Right };
                var close = Button(Locale.Get("plan.card.reviseClose"), () => { draft.Revising = false; draft.Error = null; RenderPlanEverywhere(); return Task.CompletedTask; }); PaintCardButton(close, prominent: false);
                close.IsEnabled = !draft.Sending;
                AutomationProperties.SetAutomationId(close, "plan-revise-close-" + id); reviseActions.Children.Add(close);
                send = Button(Locale.Get("plan.card.reviseSend"), () => AnswerPlanCard(request, PlanDecision.Revise(draft.Feedback)));
                PaintCardButton(send, prominent: true);
                AutomationProperties.SetAutomationId(send, "plan-revise-send-" + id); reviseActions.Children.Add(send);
                Check();
                revise.Children.Add(reviseActions);
                controls.Children.Add(revise);
                if (!draft.Sending) field.Loaded += (_, _) => { if (field.IsLoaded) field.Focus(FocusState.Programmatic); };
            }
            if (draft.Error is { } error)
            {
                var message = new TextBlock { Text = error, Foreground = b.Brush(DesignToken.ErrText), TextSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(message, "plan-error-" + id);
                controls.Children.Add(message);
            }

            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            if (draft.Sending)
            {
                var sending = new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = ink2 };
                AutomationProperties.SetAutomationId(sending, "plan-sending-" + id); hint.Children.Add(sending);
            }
            hint.Children.Add(new TextBlock { Text = Locale.Get("plan.card.hint"), TextSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            controls.Children.Add(hint);
            // Four answers as tiles: one row where the card is wide, two where it is not.
            var actions = new AdaptiveGridPanel { Minimum = 150, Gap = 8, IsHitTestVisible = !draft.Sending };
            void Answer(string key, string automation, PlanDecision? decision, bool accent)
            {
                var button = Button(Locale.Get(key), () =>
                {
                    if (decision is not null) return AnswerPlanCard(request, decision);
                    draft.Revising = true; draft.Error = null; RenderPlanEverywhere(); return Task.CompletedTask;
                });
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (accent) PaintAccentButton(button); else PaintCardButton(button, prominent: false);
                button.IsEnabled = !draft.Sending && (decision is not null || !draft.Revising);
                AutomationProperties.SetAutomationId(button, automation + "-" + id);
                actions.Children.Add(button);
            }
            Answer("plan.card.cancel", "plan-cancel", PlanDecision.Cancel, false);
            Answer("plan.card.revise", "plan-revise", null, false);
            Answer("plan.card.approveConfirm", "plan-approve-confirm", PlanDecision.ApproveConfirmEach, false);
            Answer("plan.card.approveAuto", "plan-approve-auto", PlanDecision.ApproveAutoEdit, true);
            controls.Children.Add(actions);
        }

        /// <summary>The one primary choice of a card on the accent, its words in <c>onAccent</c> (M/PaneChrome.swift accent).</summary>
        private void PaintAccentButton(Button button)
        {
            button.CornerRadius = new CornerRadius(DesignMetrics.Radius.CardButton); button.TextSize = 12; button.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            button.Padding = CardButtonPadding; button.MinWidth = 0; button.MinHeight = CardButtonHeight; button.Height = CardButtonHeight; button.BorderThickness = new Thickness(0);
            var fill = owner.brushes.Brush(DesignToken.Accent); var ink = owner.brushes.Brush(DesignToken.OnAccent);
            owner.PaintPlainButton(button, fill, fill, null, ink, ink);
            button.Opacity = button.IsEnabled ? 1 : CardButtonDisabled;
            button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : CardButtonDisabled;
        }

        /// <summary>
        /// Sends one of the four answers. A change request Core would refuse never leaves the card; while the answer is
        /// sent every button is off; a Core error stays on the card under the plan.
        /// </summary>
        private async Task AnswerPlanCard(ToolPermissionRequest request, PlanDecision decision)
        {
            if (planDraft is not { } draft || draft.RequestId != request.Id || draft.Sending) return;
            if (decision.Kind == "revise")
            {
                try { ClaudePlanMode.ValidatedFeedback(decision.Feedback); }
                catch (ArgumentException ex) { draft.Error = ex.Message; RenderPlanEverywhere(); return; }
            }
            draft.Sending = true; draft.Error = null; RenderPlanEverywhere();
            try
            {
                var paneId = id; var requestId = request.Id;
                if (owner.smokePlanAnswerer is { } fake) fake(paneId, requestId, decision);
                else await Task.Run(() => owner.service.AnswerPlan(paneId, requestId, decision));
                // The channel's own settled request removes it as well; this is the same removal, once.
                ReceiveToolPermission(request with { State = PlanOutcome.PaneMode(decision.Outcome) is null ? "denied" : "allowed" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Read the draft again: the request may have been replaced while the answer was out.
                if (planDraft is { } current && current.RequestId == request.Id) { current.Sending = false; current.Error = ex.Message; }
                RenderPlanEverywhere();
            }
        }

        /// <summary>A plan as a read-only Markdown document, drawn again in a new theme once the box has painted over it.</summary>
        private RichEditBox PlanMarkdown(string plan, string automationId)
        {
            var view = MarkdownView(TranscriptRtf.RenderMarkdown(plan, !owner.DarkTheme));
            AutomationProperties.SetAutomationId(view, automationId);
            AutomationProperties.SetName(view, Locale.Get("plan.card.title"));
            view.ActualThemeChanged += (_, _) => view.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (view.IsLoaded) SetMarkdownRtf(view, TranscriptRtf.RenderMarkdown(plan, !owner.DarkTheme));
            });
            planViews.Add((view, plan));
            return view;
        }

        /// <summary>The app's own theme toggle: every plan document on screen in the new colours.</summary>
        internal void RethemePlanViews()
        {
            var light = !owner.DarkTheme;
            foreach (var (view, plan) in planViews.ToArray())
            {
                if (!view.IsLoaded) continue;
                var rtf = TranscriptRtf.RenderMarkdown(plan, light);
                if (view.Tag as string != rtf) SetMarkdownRtf(view, rtf);
            }
        }

        /// <summary>펼치기: the plan opened like a document in a large sheet.</summary>
        private async Task OpenPlanDocument(string title, string subtitle, string plan)
        {
            if (owner.dialogOpen || owner.options.SmokeTest) return;
            var content = new Grid { RowSpacing = DesignMetrics.Spacing.Sm };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new TextBlock { Text = subtitle, TextSize = 11, Foreground = owner.brushes.Brush(DesignToken.Ink2) });
            var document = PlanMarkdown(plan, "plan-document");
            var page = new Border { Child = document, MinHeight = 420, Background = owner.brushes.Brush(DesignToken.Card) };
            Grid.SetRow(page, 1); content.Children.Add(page);
            var dialog = owner.StyledDialog(new ContentDialog { Title = title, Content = content, CloseButtonText = Locale.Get("plan.card.close"), XamlRoot = owner.root.XamlRoot, RequestedTheme = owner.root.RequestedTheme }, 860, 680);
            AutomationProperties.SetAutomationId(dialog, "plan-document-sheet");
            owner.dialogOpen = true;
            try { await owner.ShowDialogAsync(dialog); }
            finally { owner.dialogOpen = false; ForgetPlanView(document); }
        }

        // ── the answered plans ──────────────────────────────────────────────

        /// <summary>
        /// The pane's answered plans outside the diagram (the diagram draws them as blocks beside their requests):
        /// folded to one line above the composer that opens to each of them, newest first.
        /// </summary>
        internal void RefreshPlanHistory(RunSession pane)
        {
            owner.WebCardsChanged(this);
            if (planHistoryHost is null) return;
            var records = pane.Kind == "claude" && PlanCardSupport.ShowsHistoryStrip(InMightyDiagram) ? pane.PlanHistory ?? [] : [];
            var key = string.Join("|", records.Select(r => r.Id + ":" + r.Outcome + ":" + r.DecidedAt)) + "|" + planHistoryOpen + "|" + string.Join(",", planHistoryExpanded) + "|" + Locale.LanguagePreference;
            if (key == planHistoryKey) return;
            planHistoryKey = key;
            foreach (var view in VisualDescendants(planHistoryHost).OfType<RichEditBox>().ToArray()) ForgetPlanView(view);
            planHistoryHost.Children.Clear();
            planHistoryHost.Visibility = records.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (records.Count == 0) return;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var last = records[^1];
            var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) face.ColumnDefinitions.Add(new() { Width = width });
            face.Children.Add(ComposerGlyph.Icon("", 11).Ink(b.Brush(DesignToken.Accent)).View);
            var count = new TextBlock { Text = Locale.Get("plan.history.title", new Dictionary<string, string> { ["count"] = records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), TextSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(count, 1); face.Children.Add(count);
            var latest = new TextBlock { Text = PlanCardSupport.OutcomeTitle(last.Outcome) + " · " + PlanCardSupport.Headline(last.Plan), TextSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(latest, 2); face.Children.Add(latest);
            var chevron = (planHistoryOpen ? ComposerGlyph.ChevronDown(8, 1.2) : ComposerGlyph.ChevronRight(8, 1.2)).Ink(ink2);
            Grid.SetColumn(chevron.View, 3); face.Children.Add(chevron.View);
            var fold = FoldButton(face, Locale.Get(planHistoryOpen ? "plan.history.hide" : "plan.history.show"));
            fold.IsChecked = planHistoryOpen;
            AutomationProperties.SetAutomationId(fold, "plan-history-" + id);
            fold.Click += (_, _) => { planHistoryOpen = !planHistoryOpen; RefreshPlanHistory(Session); };
            var strip = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            strip.Children.Add(fold);
            if (planHistoryOpen)
            {
                var list = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                foreach (var record in Enumerable.Reverse(records))
                {
                    var recordId = record.Id;
                    list.Children.Add(PlanRecordRow(record, planHistoryExpanded.Contains(recordId), () => { if (!planHistoryExpanded.Remove(recordId)) planHistoryExpanded.Add(recordId); RefreshPlanHistory(Session); }, inDiagram: false));
                }
                strip.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
            }
            planHistoryHost.Children.Add(new Border
            {
                Child = strip, Padding = new Thickness(DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Sm - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Line, DesignMetrics.Spacing.Sm - DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.Card), BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
            });
        }

        /// <summary>
        /// One answered plan: the outcome on its tone's soft pill, the time and the first line; opened, the plan and
        /// the change asked for. In the diagram it fills its block (M/PlanApprovalCard.swift PlanRecordView).
        /// </summary>
        private Border PlanRecordRow(PlanRecord record, bool expanded, Action toggle, bool inDiagram)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            var tone = record.Outcome switch { PlanOutcome.ApprovedAuto or PlanOutcome.ApprovedConfirm => DesignTone.Done, PlanOutcome.Revised => DesignTone.Wait, _ => DesignTone.Stop };
            var head = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) head.ColumnDefinitions.Add(new() { Width = width });
            var pill = new Border { Child = new TextBlock { Text = PlanCardSupport.OutcomeTitle(record.Outcome), TextSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = b.Text(tone) }, Background = b.Soft(tone), CornerRadius = new CornerRadius(9), Padding = new Thickness(DesignMetrics.Spacing.Sm, 1, DesignMetrics.Spacing.Sm, 1), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(pill);
            var time = new TextBlock { Text = PlanCardSupport.TimeText(record.DecidedAt), TextSize = 10, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(time, 1); head.Children.Add(time);
            var line = new TextBlock { Text = PlanCardSupport.Headline(record.Plan), TextSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, 2); head.Children.Add(line);
            var chevron = (expanded ? ComposerGlyph.ChevronDown(8, 1.2) : ComposerGlyph.ChevronRight(8, 1.2)).Ink(ink2);
            Grid.SetColumn(chevron.View, 3); head.Children.Add(chevron.View);
            var fold = FoldButton(head, Locale.Get(expanded ? "plan.history.hide" : "plan.history.show"));
            fold.IsChecked = expanded;
            AutomationProperties.SetAutomationId(fold, "plan-record-" + record.Id);
            fold.Click += (_, _) => toggle();
            var row = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            row.Children.Add(fold);
            if (expanded)
            {
                row.Children.Add(new Border { Child = PlanMarkdown(record.Plan, "plan-record-text-" + record.Id), MaxHeight = inDiagram ? 320 : 300, CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = b.Brush(DesignToken.CardRaised) });
                if (record.PlanTruncated == true) row.Children.Add(new TextBlock { Text = Locale.Get("plan.history.truncated"), TextSize = 10, Foreground = ink2 });
            }
            else if (inDiagram)
                row.Children.Add(new TextBlock { Text = PlanCardSupport.Headline(record.Plan), TextSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            if (record.Feedback is { Length: > 0 } feedback)
                row.Children.Add(new TextBlock { Text = Locale.Get("plan.history.feedback") + " · " + feedback, TextSize = 10.5, Foreground = b.Brush(DesignToken.WaitText), TextWrapping = expanded ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = expanded });
            return new Border
            {
                Child = row, Padding = new Thickness(inDiagram ? DesignMetrics.Spacing.Md : DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Sm, inDiagram ? DesignMetrics.Spacing.Md : DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Sm), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row),
                Background = inDiagram ? b.Transparent : b.Brush(DesignToken.CardRaised), VerticalAlignment = inDiagram ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            };
        }

        /// <summary>An answered plan's block in the diagram: the record's row, opening in place to its plan.</summary>
        private Border? BuildPlanRecordBlock(MightyGraphBlock block)
        {
            if (block.RecordId is not { } recordId || Session.PlanHistory?.LastOrDefault(r => r.Id == recordId) is not { } record) return null;
            var blockId = block.Id;
            // Folding or opening drops a dragged size, as the other blocks' size control does (AddGraphBlockSizeControls).
            return PlanRecordRow(record, graphExpanded.Contains(blockId), () => _ = owner.Act(async () =>
            {
                graphRecordSizes.Remove(blockId); graphFollow.UserChangedLayout();
                await Change(p => GraphBlockPreferences.Set(p, blockId, null));
                if (!graphExpanded.Remove(blockId)) graphExpanded.Add(blockId);
                graphFollow.UserChangedLayout();
                RefreshMightyView(Session);
            }), inDiagram: true);
        }

        /// <summary>The fingerprint part of an answered plan's block.</summary>
        private string PlanRecordBlockKey(MightyGraphBlock block) =>
            block.RecordId is { } recordId && Session.PlanHistory?.LastOrDefault(r => r.Id == recordId) is { } record ? record.Id + ":" + record.Outcome + ":" + record.DecidedAt : "";

        private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var nested in VisualDescendants(child)) yield return nested;
            }
        }

        // ── smoke ───────────────────────────────────────────────────────────

        private static T? FindById<T>(DependencyObject? root, string automationId) where T : DependencyObject
        {
            if (root is null) return null;
            if (root is T match && AutomationProperties.GetAutomationId(root) == automationId) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindById<T>(VisualTreeHelper.GetChild(root, i), automationId) is { } found) return found;
            return null;
        }
        private static void Press(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

        private ToolPermissionRequest SmokePlan(string requestId) => new(requestId, id, "toolu_" + requestId, ClaudePlanMode.ToolName,
            "{\"plan\":\"# Smoke plan\\n\\n1. First step\\n2. Second step\"}", "plan", CanAllow: false, CanAnswerPlan: true, ReceivedAt: "2027-01-15T08:05:00.000Z");

    }
}
