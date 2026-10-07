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
        /// One plan card, built once per request and place (docked or in the diagram): its plan document is
        /// never torn down while the request waits; only the controls under it are rebuilt when the card's
        /// state changes.
        /// </summary>
        private sealed class PlanCardParts
        {
            internal required string RequestId;
            internal required Border Card;
            internal required RichEditBox Document;
            internal required StackPanel Controls;
            internal required TextBlock Waiting;
            internal string? Key;
        }

        private PlanDraft? planDraft;
        /// <summary>The card docked over the composer (false) and the one in the diagram (true).</summary>
        private readonly Dictionary<bool, PlanCardParts> planCards = [];
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

        /// <summary>The pane shows its Mighty diagram, not the default view or the timeline.</summary>
        private bool InMightyDiagram
        {
            get
            {
                var pane = Session;
                return pane.Kind == "claude" && MightyGraphSupport.Providers.Contains(pane.Provider) && pane.AgentViewMode == "mighty" && MightyTimeline.Mode(pane) != "timeline";
            }
        }

        /// <summary>The Mighty diagram draws the pending plan where its unfinished request's result will go.</summary>
        private bool DiagramShowsPlan(ToolPermissionRequest plan) => PlanCardSupport.DiagramPlanRunID(plan, InMightyDiagram, Session.GraphRuns ?? []) is not null;

        /// <summary>
        /// The plan card in place of the generic permission card: docked over the composer, or nothing here
        /// while the diagram draws it. False for any other request.
        /// </summary>
        private bool TryRenderPlan(ToolPermissionRequest current, int count)
        {
            if (!current.CanAnswerPlan || current.Plan is null) { HidePlanDock(); NotePlanShown(null); return false; }
            if (planDraft?.RequestId != current.Id) planDraft = new(current.Id);
            NotePlanShown(current.Id);
            if (questionnaireCard is not null) HideQuestionnaire();
            // The generic card never shows beside a plan, wherever the plan is drawn.
            permissionCard.Visibility = Visibility.Collapsed;
            if (DiagramShowsPlan(current))
            {
                HidePlanDock();
                permissionCard.Visibility = Visibility.Collapsed;
                toolPermissionHost.Visibility = Visibility.Collapsed;
                return true;
            }
            var card = PlanCard(current, count, inDiagram: false);
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
                foreach (var parts in planCards.Values) { Detach(parts.Card); ForgetPlanView(parts.Document); }
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

        /// <summary>The fingerprint part of the diagram's plan block: rebuilt only when what it shows changes.</summary>
        private string PlanBlockKey => PendingPlan is { } plan
            ? (planDraft?.RequestId == plan.Id ? planDraft.Key : plan.Id) + "|" + PendingCount
            : "";

        /// <summary>The card for <paramref name="request"/> in one place: built once, its controls brought up to date.</summary>
        private Border PlanCard(ToolPermissionRequest request, int count, bool inDiagram)
        {
            if (!planCards.TryGetValue(inDiagram, out var parts) || parts.RequestId != request.Id)
            {
                if (parts is not null) { Detach(parts.Card); ForgetPlanView(parts.Document); }
                parts = BuildPlanCard(request, inDiagram);
                planCards[inDiagram] = parts;
            }
            parts.Waiting.Text = Locale.Get("phone.questionnaire.waiting", new Dictionary<string, string> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            parts.Waiting.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            var draft = planDraft is { } d && d.RequestId == request.Id ? d : planDraft = new(request.Id);
            if (parts.Key != draft.Key) { parts.Key = draft.Key; BuildPlanControls(parts, request, draft); }
            return parts.Card;
        }

        /// <summary>
        /// The plan card (M/PlanApprovalCard.swift): the list on its amber disc, 계획 and when it came, 펼치기; the plan
        /// rendered as Markdown on the raised strip, scrolling inside; then the controls (<see cref="BuildPlanControls"/>).
        /// </summary>
        private PlanCardParts BuildPlanCard(ToolPermissionRequest request, bool inDiagram)
        {
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var plan = request.Plan ?? "";
            var body = new Grid { RowSpacing = DesignMetrics.Spacing.Md };
            foreach (var height in new[] { GridLength.Auto, inDiagram ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, GridLength.Auto })
                body.RowDefinitions.Add(new RowDefinition { Height = height });

            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
            header.Children.Add(WaitBadge(ComposerGlyph.Icon("", 11, 22, 22).Ink(b.Brush(DesignToken.OnWait)).View));
            var title = new TextBlock { Text = Locale.Get("plan.card.title"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); header.Children.Add(title);
            var received = request.ReceivedAt is { } at ? PlanCardSupport.ReceivedText(at) : "";
            var receivedBlock = new TextBlock { Text = received, FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(receivedBlock, "plan-received-" + id);
            Grid.SetColumn(receivedBlock, 2); header.Children.Add(receivedBlock);
            var waiting = new TextBlock { FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            Grid.SetColumn(waiting, 4); header.Children.Add(waiting);
            var expand = Button(Locale.Get("plan.card.expand"), () => OpenPlanDocument(Locale.Get("plan.card.title"), received, plan)); PaintCardButton(expand, prominent: false);
            AutomationProperties.SetAutomationId(expand, "plan-expand-" + id);
            Grid.SetColumn(expand, 5); header.Children.Add(expand);
            body.Children.Add(header);

            var document = PlanMarkdown(plan, "plan-text-" + id);
            var page = new Border { Child = document, CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.CardRaised), MinHeight = 80 };
            if (!inDiagram) page.MaxHeight = 260;
            Grid.SetRow(page, 1); body.Children.Add(page);

            var controls = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
            Grid.SetRow(controls, 2); body.Children.Add(controls);

            var card = WaitCard(body);
            if (inDiagram) { card.HorizontalAlignment = HorizontalAlignment.Stretch; card.VerticalAlignment = VerticalAlignment.Stretch; }
            AutomationProperties.SetAutomationId(card, "plan-card-" + id);
            AutomationProperties.SetName(card, Locale.Get("plan.card.title"));
            return new PlanCardParts { RequestId = request.Id, Card = card, Document = document, Controls = controls, Waiting = waiting };
        }

        /// <summary>
        /// What is under the plan, rebuilt when the card's state changes: the change request when asked for (with
        /// the too-long note while the words are over the bound), an error, then the four answers, the accent one last.
        /// </summary>
        private void BuildPlanControls(PlanCardParts parts, ToolPermissionRequest request, PlanDraft draft)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            parts.Controls.Children.Clear();
            if (draft.Revising)
            {
                Button? send = null;
                var revise = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                var field = new TextBox { AcceptsReturn = true, Text = draft.Feedback, PlaceholderText = Locale.Get("plan.card.revisePlaceholder"), PlaceholderForeground = b.Tertiary, FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 120, IsEnabled = !draft.Sending };
                AutomationProperties.SetName(field, Locale.Get("plan.card.revisePlaceholder"));
                AutomationProperties.SetAutomationId(field, "plan-revise-text-" + id);
                var tooLong = new TextBlock { Text = Locale.Get("plan.error.feedbackTooLong"), Foreground = b.Brush(DesignToken.ErrText), FontSize = 11, TextWrapping = TextWrapping.Wrap };
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
                parts.Controls.Children.Add(revise);
                if (!draft.Sending) field.Loaded += (_, _) => { if (field.IsLoaded) field.Focus(FocusState.Programmatic); };
            }
            if (draft.Error is { } error)
            {
                var message = new TextBlock { Text = error, Foreground = b.Brush(DesignToken.ErrText), FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(message, "plan-error-" + id);
                parts.Controls.Children.Add(message);
            }

            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            if (draft.Sending)
            {
                var sending = new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = ink2 };
                AutomationProperties.SetAutomationId(sending, "plan-sending-" + id); hint.Children.Add(sending);
            }
            hint.Children.Add(new TextBlock { Text = Locale.Get("plan.card.hint"), FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            parts.Controls.Children.Add(hint);
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
            parts.Controls.Children.Add(actions);
        }

        /// <summary>The one primary choice of a card on the accent, its words in <c>onAccent</c> (M/PaneChrome.swift accent).</summary>
        private void PaintAccentButton(Button button)
        {
            button.CornerRadius = new CornerRadius(DesignMetrics.Radius.CardButton); button.FontSize = 12; button.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
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
            content.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = owner.brushes.Brush(DesignToken.Ink2) });
            var document = PlanMarkdown(plan, "plan-document");
            var page = new Border { Child = document, MinHeight = 420, Background = owner.brushes.Brush(DesignToken.Card) };
            Grid.SetRow(page, 1); content.Children.Add(page);
            var dialog = owner.StyledDialog(new ContentDialog { Title = title, Content = content, CloseButtonText = Locale.Get("plan.card.close"), XamlRoot = owner.root.XamlRoot, RequestedTheme = owner.root.RequestedTheme }, 860, 680);
            AutomationProperties.SetAutomationId(dialog, "plan-document-sheet");
            owner.dialogOpen = true;
            try { await dialog.ShowAsync(); }
            finally { owner.dialogOpen = false; ForgetPlanView(document); }
        }

        // ── the answered plans ──────────────────────────────────────────────

        /// <summary>
        /// The pane's answered plans outside the diagram (the diagram draws them as blocks beside their requests):
        /// folded to one line above the composer that opens to each of them, newest first.
        /// </summary>
        internal void RefreshPlanHistory(RunSession pane)
        {
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
            var count = new TextBlock { Text = Locale.Get("plan.history.title", new Dictionary<string, string> { ["count"] = records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(count, 1); face.Children.Add(count);
            var latest = new TextBlock { Text = PlanCardSupport.OutcomeTitle(last.Outcome) + " · " + PlanCardSupport.Headline(last.Plan), FontSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
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
            var pill = new Border { Child = new TextBlock { Text = PlanCardSupport.OutcomeTitle(record.Outcome), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = b.Text(tone) }, Background = b.Soft(tone), CornerRadius = new CornerRadius(9), Padding = new Thickness(DesignMetrics.Spacing.Sm, 1, DesignMetrics.Spacing.Sm, 1), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(pill);
            var time = new TextBlock { Text = PlanCardSupport.TimeText(record.DecidedAt), FontSize = 10, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(time, 1); head.Children.Add(time);
            var line = new TextBlock { Text = PlanCardSupport.Headline(record.Plan), FontSize = 11, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
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
                if (record.PlanTruncated == true) row.Children.Add(new TextBlock { Text = Locale.Get("plan.history.truncated"), FontSize = 10, Foreground = ink2 });
            }
            else if (inDiagram)
                row.Children.Add(new TextBlock { Text = PlanCardSupport.Headline(record.Plan), FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            if (record.Feedback is { Length: > 0 } feedback)
                row.Children.Add(new TextBlock { Text = Locale.Get("plan.history.feedback") + " · " + feedback, FontSize = 10.5, Foreground = b.Brush(DesignToken.WaitText), TextWrapping = expanded ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = expanded });
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
            return PlanRecordRow(record, graphExpanded.Contains(blockId), () => { if (!graphExpanded.Remove(blockId)) graphExpanded.Add(blockId); QueueGraphRefresh(); }, inDiagram: true);
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

        /// <summary>
        /// The docked plan card with a fake pending plan and an injected answerer: the card shows, its Markdown renders,
        /// each button answers with its own decision, a change request needs words, and the card goes once answered.
        /// Then the view mode switches while a plan waits, and exactly one card shows after each switch. The pane's
        /// draft, focus, requests, graph runs and view modes are put back afterwards.
        /// </summary>
        internal async Task<Dictionary<string, object?>> RunPlanCardSmoke()
        {
            var checks = new Dictionary<string, object?>();
            var savedDraft = input.Text;
            var savedFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(toolPermissionHost.XamlRoot);
            var original = Session;
            var answers = new List<(string Pane, string Request, PlanDecision Decision)>();
            async Task Shown(string requestId)
            {
                ReceiveToolPermission(SmokePlan(requestId));
                await WaitUI(() => planDockCard is not null && toolPermissionHost.Visibility == Visibility.Visible && planDraft?.RequestId == requestId);
            }
            // Always the card on screen now: an await may have replaced it.
            Button Find(string automation) => FindById<Button>(planDockCard, automation + "-" + id) ?? throw new InvalidOperationException("The plan card has no " + automation + " button.");
            try
            {
                owner.smokePlanAnswerer = (pane, request, decision) => answers.Add((pane, request, decision));
                await owner.Act(async () => { await Change(p => p with { AgentViewMode = "default" }); Refresh(); RefreshMightyView(Session); });

                await Shown("smoke-plan-1");
                await WaitUI(() => planViews.Any(entry => entry.View.IsLoaded));
                var view = planViews.Last(entry => entry.View.IsLoaded).View;
                view.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var rendered);
                Require(rendered.Contains("Smoke plan", StringComparison.Ordinal) && rendered.Contains("First step", StringComparison.Ordinal) && !rendered.Contains("# Smoke", StringComparison.Ordinal),
                    "The plan card must render the plan's Markdown: " + rendered);
                Require(FindById<TextBlock>(planDockCard, "plan-received-" + id)?.Text.Length > 0, "The plan card must say when the plan came.");
                Require(permissionCard.Visibility == Visibility.Collapsed, "The generic permission card must not show beside the plan.");
                checks["cardShowsRenderedPlan"] = true;

                Press(Find("plan-approve-auto"));
                await WaitUI(() => planDockCard is null && toolPermissionHost.Visibility == Visibility.Collapsed);
                Require(answers.Count == 1 && answers[0] == (id, "smoke-plan-1", PlanDecision.ApproveAutoEdit), "Approve (auto edit) must answer approveAutoEdit.");
                checks["approveAutoAnswers"] = true;

                await Shown("smoke-plan-2");
                Press(Find("plan-approve-confirm"));
                await WaitUI(() => planDockCard is null);
                Require(answers.Count == 2 && answers[1].Decision == PlanDecision.ApproveConfirmEach, "Approve (confirm each) must answer approveConfirmEach.");
                await Shown("smoke-plan-3");
                Press(Find("plan-cancel"));
                await WaitUI(() => planDockCard is null);
                Require(answers.Count == 3 && answers[2].Decision == PlanDecision.Cancel, "Cancel must answer cancel.");
                checks["approveConfirmAndCancelAnswer"] = true;

                await Shown("smoke-plan-4");
                var document = planCards[false].Document;
                Press(Find("plan-revise"));
                await WaitUI(() => FindById<TextBox>(planDockCard, "plan-revise-text-" + id) is not null);
                Require(ReferenceEquals(planCards[false].Document, document), "Opening the change request must keep the plan's document.");
                Require(!Find("plan-revise-send").IsEnabled, "An empty change request cannot be sent.");
                // Sent anyway, Core's refusal stays on the card and nothing is answered.
                await AnswerPlanCard(SmokePlan("smoke-plan-4"), PlanDecision.Revise("  "));
                await WaitUI(() => FindById<TextBlock>(planDockCard, "plan-error-" + id) is { } shown && shown.Text == Locale.Get("plan.error.emptyFeedback"));
                Require(answers.Count == 3 && planDockCard is not null, "An empty change request must not answer the plan.");
                var field = FindById<TextBox>(planDockCard, "plan-revise-text-" + id)!;
                field.Text = new string('a', ClaudePlanMode.MaximumFeedbackBytes + 1);
                await WaitUI(() => FindById<TextBlock>(planDockCard, "plan-revise-too-long-" + id)?.Visibility == Visibility.Visible && !Find("plan-revise-send").IsEnabled);
                field = FindById<TextBox>(planDockCard, "plan-revise-text-" + id)!;
                field.Text = "Write the tests first";
                await WaitUI(() => FindById<Button>(planDockCard, "plan-revise-send-" + id)?.IsEnabled == true);
                Press(Find("plan-revise-send"));
                await WaitUI(() => planDockCard is null && toolPermissionHost.Visibility == Visibility.Collapsed);
                Require(answers.Count == 4 && answers[3].Decision == PlanDecision.Revise("Write the tests first"), "Request changes must send the words as revise.");
                checks["reviseNeedsWordsAndSendsThem"] = true;
                checks["cardGoneAfterAnswer"] = planDockCard is null && PendingPlan is null;

                // A plan waiting while the view changes: docked, in the diagram, docked in the timeline, docked again.
                var runId = "smoke-plan-run";
                var running = new MightyGraphRun { Id = runId, SourceRunID = runId, Input = "Plan this", Status = "running", Provider = Session.Provider };
                await owner.Act(async () => { await Change(p => p with { GraphRuns = [running] }); Refresh(); });
                ReceiveToolPermission(SmokePlan("smoke-plan-5"));
                var planNode = MightyGraphBlockSize.NodeId(runId, MightyGraphLayout.PlanSuffix);
                bool Docked() => planDockCard is not null && toolPermissionHost.Visibility == Visibility.Visible && toolPermissionHost.Children.Contains(planDockCard);
                bool Drawn() => graphHost?.Visibility == Visibility.Visible && graphViewport?.Visibility == Visibility.Visible
                    && graphCards.TryGetValue(planNode, out var drawn) && graphCanvas.Children.Contains(drawn) && drawn is Border { Child: not null };
                async Task Mode(string agentView, string graphView, bool diagram, string step)
                {
                    await owner.Act(async () => { await Change(p => p with { AgentViewMode = agentView, GraphViewMode = graphView }); Refresh(); RefreshMightyView(Session); });
                    await WaitUI(() => diagram ? Drawn() && !Docked() : Docked() && !Drawn());
                    Require((Docked() ? 1 : 0) + (Drawn() ? 1 : 0) == 1, "Exactly one plan card must show after switching to " + step + ".");
                    Require(permissionCard.Visibility == Visibility.Collapsed, "The generic card must stay hidden after switching to " + step + ".");
                }
                await Mode("default", "diagram", false, "the default view");
                await Mode("mighty", "diagram", true, "the diagram");
                await Mode("mighty", "timeline", false, "the timeline");
                await Mode("mighty", "diagram", true, "the diagram again");
                await Mode("default", "diagram", false, "the default view again");
                checks["oneCardAfterEachViewSwitch"] = true;
            }
            finally
            {
                owner.smokePlanAnswerer = null;
                toolPermissions.Clear(); planDraft = null; RenderToolPermissions();
                await owner.Act(async () => { await Change(p => p with { GraphRuns = original.GraphRuns, AgentViewMode = original.AgentViewMode, GraphViewMode = original.GraphViewMode }); Refresh(); RefreshMightyView(Session); });
                updating = true; try { input.Text = savedDraft; } finally { updating = false; }
                ((FrameworkElement?)savedFocus)?.Focus(FocusState.Programmatic);
            }
            return checks;
        }
    }
}
