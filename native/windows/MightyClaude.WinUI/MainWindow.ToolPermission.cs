using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // Smoke mode intercept: set before smoke, cleared in finally.
    private Dictionary<string, bool>? smokePermissionResponses;

    private sealed partial class PaneView
    {
        // The card sits over the composer card, 12 in from the pane's sides and 8 under what is
        // above it (M/SessionPaneView.swift:156, M/ToolPermissionBar.swift:104-105).
        private readonly StackPanel toolPermissionHost = new() { Margin = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, 0), Visibility = Visibility.Collapsed };
        private readonly List<ToolPermissionRequest> toolPermissions = [];
        // Stored for smoke assertions — updated by RenderToolPermissions.
        private TextBlock permTitleBlock = null!;
        private TextBlock permToolBlock = null!;
        private TextBlock permHeadlineBlock = null!;
        private TextBlock permSummaryBlock = null!;
        private TextBlock permPathBlock = null!;
        private TextBlock permCountBlock = null!;
        private TextBlock permReasonBlock = null!;
        private TextBlock permCannotAllowBlock = null!;
        private Button permDenyButton = null!;
        private Button permAllowButton = null!;
        private TextBlock permInputBlock = null!;
        private StackPanel permFields = null!;
        private Microsoft.UI.Xaml.Controls.Primitives.ToggleButton permJsonFold = null!;
        private readonly ComposerGlyph permJsonFolded = ComposerGlyph.ChevronRight(8, 1.2), permJsonUnfolded = ComposerGlyph.ChevronDown(8, 1.2);
        /// <summary>The permission card: the Mac's wait card, <c>card</c> inside a 2pt <c>wait</c> edge, r16, padding 14 (M/PaneChrome.swift:185-190).</summary>
        private Border permissionCard = null!;
        /// <summary>A disabled card button is the whole button at 0.45 (M/PaneChrome.swift:165).</summary>
        private const double CardButtonDisabled = 0.45;

        /// <summary>
        /// A button on the question and permission cards (M/PaneChrome.swift:145-169): 12 bold, padding
        /// <see cref="CardButtonPadding"/>, <see cref="CardButtonHeight"/> high, r9; the prominent one <c>ink</c> behind <c>card</c> words, the other
        /// <c>cardRaised</c> with a 1pt <c>line</c> and <c>ink</c> words; the whole button at 0.45 while disabled.
        /// </summary>
        private void PaintCardButton(Button button, bool prominent)
        {
            button.CornerRadius = new CornerRadius(DesignMetrics.Radius.CardButton); button.FontSize = 12; button.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            button.Padding = CardButtonPadding; button.MinWidth = 0; button.MinHeight = CardButtonHeight; button.Height = CardButtonHeight;
            button.BorderThickness = new Thickness(prominent ? 0 : DesignMetrics.Stroke.Line);
            var fill = owner.brushes.Brush(prominent ? DesignToken.Ink : DesignToken.CardRaised); var ink = owner.brushes.Brush(prominent ? DesignToken.Card : DesignToken.Ink);
            owner.PaintPlainButton(button, fill, fill, prominent ? null : owner.brushes.Brush(DesignToken.Line), ink, ink);
            button.Opacity = button.IsEnabled ? 1 : CardButtonDisabled;
            button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : CardButtonDisabled;
        }

        /// <summary>The wait card the permission and question cards share (M/PaneChrome.swift:185-190).</summary>
        private Border WaitCard(UIElement body) => new()
        {
            Child = body, CornerRadius = new CornerRadius(DesignMetrics.Radius.Composer), BorderThickness = new Thickness(DesignMetrics.Stroke.Active),
            BorderBrush = owner.brushes.Brush(DesignToken.Wait), Background = owner.brushes.Brush(DesignToken.Card), Padding = new Thickness(DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Active),
        };

        /// <summary>The amber disc that heads a card waiting on the user (M/PaneChrome.swift:172-180): 22 across in <c>wait</c>, its symbol in <c>onWait</c>.</summary>
        private Grid WaitBadge(FrameworkElement symbol)
        {
            var badge = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            badge.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = owner.brushes.Brush(DesignToken.Wait) });
            badge.Children.Add(symbol);
            AutomationProperties.SetAccessibilityView(badge, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return badge;
        }

        /// <summary>Gives a toggle no look of its own in any state, checked or not: what it shows is drawn on its content.</summary>
        private void PaintPlainToggle(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle)
        {
            var b = owner.brushes; var plain = new List<(string, object)>();
            foreach (var at in new[] { "", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled" }) { plain.Add(("ToggleButtonBackground" + at, b.Transparent)); plain.Add(("ToggleButtonBorderBrush" + at, b.Transparent)); }
            owner.SetResourcesOnce(toggle, plain);
            toggle.Background = b.Transparent; toggle.BorderBrush = b.Transparent;
        }

        /// <summary>A row that folds what follows it away: a small chevron and its words, drawn by the caller.</summary>
        private Microsoft.UI.Xaml.Controls.Primitives.ToggleButton FoldButton(UIElement face, string name)
        {
            var fold = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = face, IsChecked = false, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            PaintPlainToggle(fold);
            AutomationProperties.SetName(fold, name);
            return fold;
        }

        /// <summary>
        /// Builds the permission card once (M/ToolPermissionBar.swift:44-108); RenderToolPermissions fills it. The hand
        /// on its amber disc, what is asked in 13 bold and the tool's own name in mono; the headline; the request's
        /// fields in a body that scrolls from 180, with the raw JSON folded away under them; and "applies to this
        /// request only" with Deny and Allow once.
        /// </summary>
        private void InitPermissionBar()
        {
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2); var mono = new FontFamily(DesignMetrics.Font.Mono);
            permTitleBlock = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(permTitleBlock, "permission-title-" + id);
            permToolBlock = new TextBlock { FontSize = 11.5, FontFamily = mono, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            permCountBlock = new TextBlock { FontSize = 12, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(permCountBlock, "permission-count-" + id);

            var titleRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) titleRow.ColumnDefinitions.Add(new() { Width = width });
            var hand = ComposerGlyph.Hand().Ink(b.Brush(DesignToken.OnWait));
            titleRow.Children.Add(WaitBadge(hand.View));
            Grid.SetColumn(permTitleBlock, 1); titleRow.Children.Add(permTitleBlock);
            Grid.SetColumn(permToolBlock, 2); titleRow.Children.Add(permToolBlock);
            Grid.SetColumn(permCountBlock, 3); titleRow.Children.Add(permCountBlock);

            permHeadlineBlock = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(permHeadlineBlock, "permission-headline-" + id);
            permFields = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            permSummaryBlock = new TextBlock { FontSize = 12, FontFamily = mono, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
            permPathBlock = new TextBlock { FontSize = 10, Foreground = ink, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(permPathBlock, "permission-path-" + id);
            permReasonBlock = new TextBlock { FontSize = 10, Foreground = ink2, TextWrapping = TextWrapping.Wrap };
            // The request as the CLI sent it, folded away (M/ToolPermissionBar.swift:80-83).
            permJsonFolded.Ink(ink2); permJsonUnfolded.Ink(ink2);
            var chevron = new Grid { Width = 8, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            permJsonFolded.View.HorizontalAlignment = permJsonUnfolded.View.HorizontalAlignment = HorizontalAlignment.Center;
            chevron.Children.Add(permJsonFolded.View); chevron.Children.Add(permJsonUnfolded.View);
            var foldWords = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
            foldWords.Children.Add(chevron); foldWords.Children.Add(new TextBlock { Text = ToolPermissionStrings.BarRawJson, FontSize = 10, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
            permJsonFold = FoldButton(foldWords, ToolPermissionStrings.BarRawJson);
            AutomationProperties.SetAutomationId(permJsonFold, "permission-json-" + id);
            permInputBlock = new TextBlock { FontFamily = mono, FontSize = 10, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, DesignMetrics.Spacing.Xs, 0, 0) };
            AutomationProperties.SetAutomationId(permInputBlock, "permission-input-" + id);
            void Fold()
            {
                var shown = permJsonFold.IsChecked == true;
                permInputBlock.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                permJsonFolded.View.Visibility = shown ? Visibility.Collapsed : Visibility.Visible; permJsonUnfolded.View.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            }
            permJsonFold.Checked += (_, _) => Fold(); permJsonFold.Unchecked += (_, _) => Fold(); Fold();
            var json = new StackPanel(); json.Children.Add(permJsonFold); json.Children.Add(permInputBlock);
            var details = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(0, 0, DesignMetrics.Spacing.Xs, 0) };
            foreach (var part in new FrameworkElement[] { permFields, permSummaryBlock, permPathBlock, permReasonBlock, json }) details.Children.Add(part);

            permCannotAllowBlock = new TextBlock { FontSize = 10, Foreground = ink2, TextWrapping = TextWrapping.Wrap };
            var noteBlock = new TextBlock { Text = ToolPermissionStrings.BarOnceOnlyNote, FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

            permDenyButton = new Button { Content = ToolPermissionStrings.ButtonDeny }; PaintCardButton(permDenyButton, prominent: false);
            AutomationProperties.SetName(permDenyButton, ToolPermissionStrings.ButtonDeny);
            AutomationProperties.SetAutomationId(permDenyButton, "permission-deny-" + id);
            permDenyButton.Click += (_, _) => OnPermissionDeny();

            permAllowButton = new Button { Content = ToolPermissionStrings.ButtonAllowOnce }; PaintCardButton(permAllowButton, prominent: true);
            AutomationProperties.SetName(permAllowButton, ToolPermissionStrings.ButtonAllowOnce);
            AutomationProperties.SetAutomationId(permAllowButton, "permission-allow-" + id);
            permAllowButton.Click += (_, _) => OnPermissionAllow();

            var footer = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) footer.ColumnDefinitions.Add(new() { Width = width });
            footer.Children.Add(noteBlock); Grid.SetColumn(permDenyButton, 1); footer.Children.Add(permDenyButton); Grid.SetColumn(permAllowButton, 2); footer.Children.Add(permAllowButton);

            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(titleRow); body.Children.Add(permHeadlineBlock);
            body.Children.Add(new ScrollViewer { Content = details, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
            body.Children.Add(permCannotAllowBlock); body.Children.Add(footer);

            var card = permissionCard = WaitCard(body);
            AutomationProperties.SetAutomationId(card, "permission-bar-" + id);
            toolPermissionHost.Children.Add(card);
        }

        internal void ReceiveToolPermission(ToolPermissionRequest value)
        {
            var idx = toolPermissions.FindIndex(p => p.Id == value.Id);
            if (value.State == "pending") { if (idx < 0) toolPermissions.Add(value); else toolPermissions[idx] = value; }
            else { if (idx >= 0) toolPermissions.RemoveAt(idx); }
            RenderToolPermissions(preserveQuestionnaire: true);
        }

        internal void ClearToolPermissions()
        {
            toolPermissions.Clear(); RenderToolPermissions();
        }

        /// <summary>The request the permission card shows now, so a new one starts with its JSON folded.</summary>
        private string? permissionShown;

        private void RenderToolPermissions(bool preserveQuestionnaire = false)
        {
            var pending = toolPermissions.Where(p => p.State == "pending").ToList();
            RenderGuidedStyle();
            if (pending.Count == 0) { HideQuestionnaire(); HidePlanDock(); NotePlanShown(null); permissionShown = null; toolPermissionHost.Visibility = Visibility.Collapsed; return; }
            var current = pending[0];
            // Claude's plan (ExitPlanMode) has its own card, docked here or in the diagram (MainWindow.PlanCard.cs).
            if (TryRenderPlan(current, pending.Count)) return;
            if (TryRenderQuestionnaire(current, pending.Count, preserveQuestionnaire)) return;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink);
            var pres = ToolPermissionPresentation.Make(current.ToolName, current.InputJson);
            if (permissionShown != current.Id) { permissionShown = current.Id; permJsonFold.IsChecked = false; }
            permInputBlock.Text = current.InputJson;
            permTitleBlock.Text = ToolPermissionStrings.BarTitleTemplate.Replace("{title}", pres.Title);
            permToolBlock.Text = current.ToolName;
            if (pending.Count > 1)
            {
                permCountBlock.Text = ToolPermissionStrings.BarWaitingCountTemplate.Replace("{count}", pending.Count.ToString());
                permCountBlock.Visibility = Visibility.Visible;
            }
            else { permCountBlock.Text = ""; permCountBlock.Visibility = Visibility.Collapsed; }
            permHeadlineBlock.Text = pres.Headline ?? ""; permHeadlineBlock.Visibility = pres.Headline is null ? Visibility.Collapsed : Visibility.Visible;
            // Each field: its name in 10 semibold ink2 over its value in 11pt — code in mono on the raised strip at radius 8 (M/ToolPermissionBar.swift:61-74).
            permFields.Children.Clear(); permFields.Visibility = pres.Fields.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var field in pres.Fields)
            {
                var block = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                block.Children.Add(new TextBlock { Text = field.Label, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink2) });
                var value = new TextBlock { Text = field.Value, FontSize = 11, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (field.Code)
                {
                    value.FontFamily = new FontFamily(DesignMetrics.Font.Mono);
                    block.Children.Add(new Border { Child = value, Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Sm), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = b.Brush(DesignToken.CardRaised) });
                }
                else block.Children.Add(value);
                permFields.Children.Add(block);
            }
            var bare = pres.Fields.Count == 0 && pres.Headline is null;
            permSummaryBlock.Text = bare ? current.Summary : ""; permSummaryBlock.Visibility = bare ? Visibility.Visible : Visibility.Collapsed;
            permPathBlock.Text = current.BlockedPath is { Length: > 0 } path ? ToolPermissionStrings.BarPathTemplate.Replace("{path}", path) : "";
            permPathBlock.Visibility = current.BlockedPath is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            permReasonBlock.Text = current.Reason ?? "";
            permReasonBlock.Visibility = current.Reason is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            var cannotAllow = !current.CanAllow;
            permCannotAllowBlock.Text = cannotAllow ? ToolPermissionStrings.BarCannotAllowHere : "";
            permCannotAllowBlock.Visibility = cannotAllow ? Visibility.Visible : Visibility.Collapsed;
            // A request that cannot be allowed here keeps its button, disabled (M/ToolPermissionBar.swift:98-99).
            permAllowButton.IsEnabled = !cannotAllow;
            toolPermissionHost.Visibility = Visibility.Visible;
        }

        private void OnPermissionAllow()
        {
            var current = toolPermissions.FirstOrDefault(p => p.State == "pending");
            if (current is null || !current.CanAllow) return;
            if (owner.smokePermissionResponses is { } dict) { dict[current.Id] = true; ReceiveToolPermission(current with { State = "allowed" }); return; }
            try { owner.service.RespondToToolPermission(id, current.Id, true); } catch (Exception ex) { owner.error.Text = ex.Message; }
        }

        private void OnPermissionDeny()
        {
            var current = toolPermissions.FirstOrDefault(p => p.State == "pending");
            if (current is null) return;
            if (owner.smokePermissionResponses is { } dict) { dict[current.Id] = false; ReceiveToolPermission(current with { State = "denied" }); return; }
            try { owner.service.RespondToToolPermission(id, current.Id, false); } catch (Exception ex) { owner.error.Text = ex.Message; }
        }

        internal async Task<Dictionary<string, object?>> RunToolPermissionSmoke()
        {
            var checks = new Dictionary<string, object?>();
            var savedDraft = input.Text;
            var savedFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(toolPermissionHost.XamlRoot);
            try
            {
                owner.smokePermissionResponses = [];

                var req1 = new ToolPermissionRequest("smoke-perm-1", "smoke-run", "smoke-tuid-1", "Read",
                    "{\"file_path\":\"/tmp/test.txt\"}", "Read file", BlockedPath: "/tmp/test.txt");
                var req2 = new ToolPermissionRequest("smoke-perm-2", "smoke-run", "smoke-tuid-2", "Bash",
                    "{\"command\":\"ls /tmp\"}", "Run command");

                ReceiveToolPermission(req1); ReceiveToolPermission(req2);
                await WaitUI(() => toolPermissionHost.Visibility == Visibility.Visible);

                var pres1 = ToolPermissionPresentation.Make(req1.ToolName, req1.InputJson);
                var expectedTitle = ToolPermissionStrings.BarTitleTemplate.Replace("{title}", pres1.Title);
                Require(permTitleBlock.Text == expectedTitle, "Unexpected permission bar title: " + permTitleBlock.Text);
                Require(permPathBlock.Visibility == Visibility.Visible && permPathBlock.Text.Contains("/tmp/test.txt"),
                    "Blocked path was not displayed.");
                Require(permCountBlock.Visibility == Visibility.Visible && permCountBlock.Text.Contains("2"),
                    "Pending count was not displayed.");
                // The request's fields stand in the card, and its raw JSON stays folded until asked for (M/ToolPermissionBar.swift:61-83).
                Require(permToolBlock.Text == req1.ToolName && permFields.Children.Count == pres1.Fields.Count && pres1.Fields.Count > 0 && permInputBlock.Text == req1.InputJson && permInputBlock.Visibility == Visibility.Collapsed,
                    "The permission card must show the tool's name and fields with the raw JSON folded away.");
                checks["barShownWithTitlePathCount"] = true;

                OnPermissionAllow();
                Require(owner.smokePermissionResponses.TryGetValue("smoke-perm-1", out var r1) && r1,
                    "One-time permission was not recorded.");
                checks["allowRecorded"] = true;

                Require(toolPermissions.Any(p => p.Id == "smoke-perm-2" && p.State == "pending"),
                    "Second request was not displayed.");
                OnPermissionDeny();
                Require(owner.smokePermissionResponses.TryGetValue("smoke-perm-2", out var r2) && !r2,
                    "Deny response was not recorded.");
                checks["denyRecorded"] = true;

                await WaitUI(() => toolPermissionHost.Visibility == Visibility.Collapsed);
                checks["barHiddenAfterAllAnswered"] = true;
            }
            finally
            {
                owner.smokePermissionResponses = null;
                toolPermissions.Clear(); RenderToolPermissions();
                updating = true; try { input.Text = savedDraft; } finally { updating = false; }
                ((FrameworkElement?)savedFocus)?.Focus(FocusState.Programmatic);
            }
            return checks;
        }
    }
}
