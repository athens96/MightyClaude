using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>The gap between the toolbar's controls (M/ComposerControls.swift:41).</summary>
        private const double ToolbarSpacing = 6;
        /// <summary>The widest a pill's words may be, by pill and toolbar style (M/ComposerControls.swift:51-60, M/SessionPaneView.swift:372, 398).</summary>
        private const double ModelCapFull = 155, ModelCapCompact = 90, ModelCapOverflow = 110, EffortCap = 48, PermissionCap = 90, FastCap = 40;
        /// <summary>
        /// How SwiftUI draws a disabled plain button: its whole label at half strength. Read off
        /// docs/design-system/crops/composer-dark.webp (a running pane's pills) and composer-codex-dark.webp
        /// (the send button with nothing to send): ink × 0.5 words, line × 0.5 edge, track × 0.5 disc.
        /// </summary>
        private const double DisabledDim = 0.5;
        /// <summary>The composer card's margin from the pane's edge, all round (M/SessionPaneView.swift:660).</summary>
        private const double ComposerMargin = 12;
        /// <summary>A pill's words and symbol sit 8 from its edge (M/ComposerControls.swift:23); the 1pt edge is inside the face.</summary>
        private static readonly Thickness PillPadding = new(8 - DesignMetrics.Stroke.Line, 0, 8 - DesignMetrics.Stroke.Line, 0);

        /// <summary>How much of the toolbar shows (M/ComposerControls.swift:35): every pill with its words, symbols only, or one options menu.</summary>
        private enum ToolbarStyle { Full, Compact, Overflow }
        private ToolbarStyle toolbarStyle = ToolbarStyle.Full;

        /// <summary>The composer's stack: the Mac's <c>VStack(spacing: 9)</c>, each child with its own padding from the card's edge.</summary>
        private StackPanel composerPanel = null!;
        /// <summary>The card's edge, drawn over its contents like the Mac's overlay stroke, so a thicker focus ring moves nothing.</summary>
        private Border composerRing = null!;
        private Grid toolbar = null!, inputRow = null!;
        private readonly StackPanel toolbarActions = new() { Orientation = Orientation.Horizontal, Spacing = ToolbarSpacing, VerticalAlignment = VerticalAlignment.Center };
        /// <summary>The shell pane's words where an agent pane has its pills (M/SessionPaneView.swift:728).</summary>
        private readonly TextBlock shellLabel = new() { FontSize = DesignMetrics.Type.Pill, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        /// <summary>The mark of a pane that continues an earlier conversation (M/SessionPaneView.swift:732-734).</summary>
        private readonly ComposerGlyph resumeMark = ComposerGlyph.Branch();
        private readonly ComposerGlyph sendArrow = ComposerGlyph.ArrowUp(), sendQueue = ComposerGlyph.QueueAdd(), sendStop = ComposerGlyph.Stop(false);
        /// <summary>What the primary button shows: <c>send</c> (the arrow), <c>queue</c> (the draft joins the queue) or <c>stop</c> (the square).</summary>
        private string sendSymbol = "send";
        /// <summary>The rows between the editor and the toolbar: attachments being read, a background update holding sends, and why a draft cannot run yet.</summary>
        private StackPanel loadingRow = null!, holdRow = null!;
        private Grid blockedRow = null!;
        private readonly TextBlock holdText = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        /// <summary>Why an attachment could not be added: its own row of the composer until it is dismissed or the next read begins (M/SessionPaneView.swift:591-600).</summary>
        private Grid attachmentErrorRow = null!;
        private readonly TextBlock attachmentErrorText = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock pillMeasure = new() { FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.Medium, FontFamily = new FontFamily(DesignMetrics.Font.Body) };
        /// <summary>The provider whose mark the model pill carries now.</summary>
        private string? pillProvider;

        /// <summary>The parts of a ComposerPill's face (M/ComposerControls.swift:15-20): symbol, words, chevron.</summary>
        private sealed class PillParts(Border face, Grid iconHost, TextBlock words, ComposerGlyph chevron, bool hasChevron)
        {
            internal Border Face { get; } = face;
            internal Grid IconHost { get; } = iconHost;
            internal TextBlock Words { get; } = words;
            internal ComposerGlyph Chevron { get; } = chevron;
            internal bool HasChevron { get; } = hasChevron;
            /// <summary>The symbol that takes the pill's ink; null for the model pill, whose provider mark keeps its brand colour.</summary>
            internal ComposerGlyph? Icon { get; set; }
        }

        /// <summary>
        /// A ComposerPill (M/ComposerControls.swift:4-33): a 32-high capsule sized to its contents — a symbol in a
        /// 14 × 14 box, the words in 11 medium and, on a menu, a 7pt chevron, 5 apart, 8 in from the edge. The button
        /// itself draws nothing; its face carries the fill, the edge and the ink (<see cref="PaintPill"/>).
        /// </summary>
        private static T NewPill<T>(bool chevron = false) where T : ContentControl, new()
        {
            var words = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            var iconHost = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var mark = ComposerGlyph.ChevronDown(); mark.View.Visibility = chevron ? Visibility.Visible : Visibility.Collapsed;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(iconHost); row.Children.Add(words); row.Children.Add(mark.View);
            var face = new Border { Child = row, Padding = PillPadding, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(DesignMetrics.Stroke.Line) };
            return new T
            {
                MinWidth = 0, MinHeight = DesignMetrics.Layout.Toolbar, Height = DesignMetrics.Layout.Toolbar, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), FontSize = DesignMetrics.Type.Pill,
                BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                Content = face, Tag = new PillParts(face, iconHost, words, mark, chevron),
            };
        }
        private static PillParts Parts(ContentControl pill) => (PillParts)pill.Tag;
        /// <summary>The words on a pill's face.</summary>
        private static TextBlock PillText(ContentControl pill) => Parts(pill).Words;
        private static void Label(Button button, string text, string name)
        {
            PillText(button).Text = text; AutomationProperties.SetName(button, name + ": " + text);
        }
        /// <summary>Gives a pill its symbol; it takes the pill's ink at the next paint.</summary>
        private static void SetPillIcon(ContentControl pill, ComposerGlyph glyph)
        {
            var parts = Parts(pill);
            parts.IconHost.Children.Clear(); parts.IconHost.Children.Add(glyph.View); parts.IconHost.Visibility = Visibility.Visible; parts.Icon = glyph;
        }
        /// <summary>A compact pill is the 32pt circle around its symbol (M/ComposerControls.swift:18-24); otherwise it is as wide as its contents.</summary>
        private static void ShapePill(ContentControl pill, bool compact)
        {
            var parts = Parts(pill);
            parts.Words.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            parts.Chevron.View.Visibility = compact || !parts.HasChevron ? Visibility.Collapsed : Visibility.Visible;
            parts.Face.Padding = compact ? new Thickness(0) : PillPadding;
            pill.Width = compact ? DesignMetrics.Layout.Toolbar : double.NaN;
        }

        /// <summary>The width of a pill's words as drawn: 11 medium in the body font (M/ComposerControls.swift:42-44).</summary>
        private double PillTextWidth(string text)
        {
            pillMeasure.Text = text; pillMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(pillMeasure.DesiredSize.Width);
        }
        /// <summary>A pill's width: its words up to their cap, padding 16, the 14pt symbol, the gap 5 and, with a chevron, 12 more (M/ComposerControls.swift:45-47).</summary>
        private double PillWidth(string title, double cap, bool chevron = true) => Math.Min(PillTextWidth(title), cap) + 16 + 14 + 5 + (chevron ? 12 : 0);
        /// <summary>
        /// The toolbar style a width affords (M/ComposerControls.swift:48-55): full while every pill fits with its
        /// words, compact while the symbols fit beside the model pill, else the model pill and one options menu.
        /// </summary>
        private ToolbarStyle StyleFor(double width, string modelTitle, string? effortTitle, string permissionTitle, bool showsFast)
        {
            const double height = DesignMetrics.Layout.Toolbar;
            var count = 4 + (effortTitle is null ? 0 : 1) + (showsFast ? 1 : 0);
            var gaps = (count - 1) * ToolbarSpacing;
            var full = height * 2 + PillWidth(modelTitle, ModelCapFull) + PillWidth(permissionTitle, PermissionCap) + (effortTitle is null ? 0 : PillWidth(effortTitle, EffortCap)) + (showsFast ? PillWidth("Fast", FastCap, false) : 0) + gaps;
            if (width >= full + 4) return ToolbarStyle.Full;
            var compact = height * (count - 1) + PillWidth(modelTitle, ModelCapCompact) + gaps;
            return width >= compact + 4 ? ToolbarStyle.Compact : ToolbarStyle.Overflow;
        }
        /// <summary>How wide the model pill's words may be in a style (M/ComposerControls.swift:56-62).</summary>
        private static double ModelTextCap(ToolbarStyle style, double width) => style switch
        {
            ToolbarStyle.Full => ModelCapFull,
            ToolbarStyle.Compact => ModelCapCompact,
            _ => Math.Max(0, Math.Min(ModelCapOverflow, width - DesignMetrics.Layout.Toolbar * 2 - ToolbarSpacing * 2 - 47)),
        };

        /// <summary>
        /// Lays the toolbar out for its width (M/SessionPaneView.swift:707-726): attach, model, then effort,
        /// permission, Fast and … as pills, as symbols, or folded into the options menu. The width is what the
        /// right cluster leaves; the Mac's formula for that cluster leaves the status-line toggle out, so this
        /// measures the cluster itself and the pills never run under it.
        /// </summary>
        private void ArrangeComposer()
        {
            // WinUI can finish a layout pass after its pane was removed from
            // the snapshot. A detached editor must not resolve a live session.
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null || toolbar is null) return;
            var shell = pane.Kind == "shell";
            shellLabel.Visibility = shell ? Visibility.Visible : Visibility.Collapsed;
            if (shell) { attach.Visibility = model.Visibility = effort.Visibility = permission.Visibility = fast.Visibility = more.Visibility = options.Visibility = Visibility.Collapsed; return; }
            var capabilities = owner.Runtime(pane.Provider)?.Capabilities ?? ProviderCatalog.Capabilities(pane.Provider);
            var showsEffort = capabilities.Effort || pane.Settings.Effort != "default";
            var showsFast = pane.Provider == "codex" && (capabilities.FastMode || pane.Settings.FastMode);
            var style = ToolbarStyle.Full; var width = double.PositiveInfinity;
            if (toolbar.ActualWidth > 0)
            {
                toolbarActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Max(0, toolbar.ActualWidth - toolbarActions.DesiredSize.Width - ToolbarSpacing);
                style = StyleFor(width, PillText(model).Text, showsEffort ? PillText(effort).Text : null, PillText(permission).Text, showsFast);
            }
            toolbarStyle = style;
            var overflow = style == ToolbarStyle.Overflow; var compact = style == ToolbarStyle.Compact;
            attach.Visibility = model.Visibility = Visibility.Visible;
            PillText(model).MaxWidth = ModelTextCap(style, width);
            effort.Visibility = !overflow && showsEffort ? Visibility.Visible : Visibility.Collapsed;
            permission.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
            fast.Visibility = !overflow && showsFast ? Visibility.Visible : Visibility.Collapsed;
            more.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
            options.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            ShapePill(effort, compact); ShapePill(permission, compact); ShapePill(fast, compact);
        }

        private Task ChangeProvider(string value) => owner.Act(async () =>
        {
            if (Session.Status == "running" || starting || Session.Provider == value) return;
            await Change(p => p with { Provider = value, Title = p.Title == ProviderCatalog.Name(p.Provider) ? ProviderCatalog.Name(value) : p.Title, Model = "default", Settings = new(), ResumeId = null }); Refresh(); input.Focus(FocusState.Programmatic);
        });

        /// <summary>
        /// A ComposerPill's look (M/ComposerControls.swift:21-26): <c>card</c> with a 1pt <c>line</c> and
        /// <c>ink</c>, or, while its setting is on, <c>accentSoft</c> with accent × 0.35 and <c>accent</c>; the
        /// chevron always <c>ink2</c>; no change under the pointer, and the whole face at half strength while
        /// disabled. It is drawn on the pill's face, so it may change at any time; the button's own resources
        /// are written once (<see cref="InitializePills"/>).
        /// </summary>
        private void PaintPill(ContentControl pill, bool active)
        {
            var b = owner.brushes; var parts = Parts(pill);
            if (active) activePills.Add(pill); else activePills.Remove(pill);
            parts.Face.Background = b.Brush(active ? DesignToken.AccentSoft : DesignToken.Card);
            parts.Face.BorderBrush = active ? b.Brush(DesignToken.Accent, DesignMetrics.Opacity.PillActiveBorder) : b.Brush(DesignToken.Line);
            var ink = b.Brush(active ? DesignToken.Accent : DesignToken.Ink);
            parts.Words.Foreground = ink; parts.Icon?.Ink(ink); parts.Chevron.Ink(b.Brush(DesignToken.Ink2));
            parts.Face.Opacity = pill.IsEnabled ? 1 : DisabledDim;
        }

        /// <summary>
        /// Gives every pill its fixed resources once, before it enters the tree: no fill or edge of the
        /// button's own in any state and the subtle wash under the pointer (hidden by the opaque face,
        /// so the pill does not change there, as on the Mac). Their faces follow their state:
        /// enablement for all, the setting for the permission and … pills, the check for Fast (M/SessionPaneView.swift:408).
        /// </summary>
        private void InitializePills()
        {
            var b = owner.brushes;
            foreach (var pill in new[] { attach, model, effort, permission, more, options })
            {
                owner.PaintPlainButton(pill, b.Transparent, b.Subtle);
                pill.IsEnabledChanged += (_, _) => PaintPill(pill, activePills.Contains(pill));
                PaintPill(pill, false);
            }
            var values = new List<(string, object)>();
            foreach (var state in new[] { "", "Disabled", "Checked", "CheckedDisabled" }) { values.Add(("ToggleButtonBackground" + state, b.Transparent)); values.Add(("ToggleButtonBorderBrush" + state, b.Transparent)); }
            foreach (var state in new[] { "PointerOver", "Pressed", "CheckedPointerOver", "CheckedPressed" }) { values.Add(("ToggleButtonBackground" + state, b.Subtle)); values.Add(("ToggleButtonBorderBrush" + state, b.Transparent)); }
            owner.SetResourcesOnce(fast, values);
            fast.Background = b.Transparent; fast.BorderBrush = b.Transparent;
            // bolt.fill while on, bolt while off (M/SessionPaneView.swift:408).
            void PaintFast() { var on = fast.IsChecked == true; if (fastFilled != on || Parts(fast).Icon is null) { fastFilled = on; SetPillIcon(fast, ComposerGlyph.Bolt(on)); } PaintPill(fast, on); }
            fast.Checked += (_, _) => PaintFast(); fast.Unchecked += (_, _) => PaintFast(); fast.IsEnabledChanged += (_, _) => PaintFast();
            PaintFast();
        }
        private bool fastFilled;

        /// <summary>Shows the arrow, the queue mark or the stop square on the primary button.</summary>
        private void ShowSendSymbol(string symbol)
        {
            sendSymbol = symbol;
            sendArrow.View.Visibility = symbol == "send" ? Visibility.Visible : Visibility.Collapsed;
            sendQueue.View.Visibility = symbol == "queue" ? Visibility.Visible : Visibility.Collapsed;
            sendStop.View.Visibility = symbol == "stop" ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The shape under the send button (M/SessionPaneView.swift:737-760): the 32pt circle in
        /// <c>run</c> while there is something to send and <c>track</c> while not; the 32pt <c>err</c>
        /// square (r8) while it stops the run. The symbol is <c>onStatus</c>, or <c>ink2</c> on the arrow
        /// with nothing to send; a disabled button is drawn at half strength, as SwiftUI draws it. All of
        /// it lives on the shape and the symbol, never in the button's resources.
        /// </summary>
        private void PaintSend()
        {
            var b = owner.brushes; var stop = sendSymbol == "stop";
            sendDisc.CornerRadius = new CornerRadius(stop ? DesignMetrics.Radius.Row : 16);
            sendDisc.Background = b.Brush(stop ? DesignToken.Err : send.IsEnabled ? DesignToken.Run : DesignToken.Track);
            var ink = b.Brush(stop || send.IsEnabled ? DesignToken.OnStatus : DesignToken.Ink2);
            sendArrow.Ink(ink); sendQueue.Ink(ink); sendStop.Ink(ink);
            sendHost.Opacity = send.IsEnabled ? 1 : DisabledDim;
        }

        /// <summary>
        /// The composer card's edge (M/SessionPaneView.swift:655): 1pt <c>line</c>, or accent × 0.8 at 1.5pt
        /// while the editor has focus or files are dragged over it. It is an overlay, so the contents never move.
        /// </summary>
        private void PaintComposerRing()
        {
            var ring = composerFocused || composerDropTargeted;
            composerRing.BorderBrush = ring ? owner.brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.ComposerFocus) : owner.brushes.Brush(DesignToken.Line);
            composerRing.BorderThickness = new Thickness(ring ? DesignMetrics.Stroke.Focus : DesignMetrics.Stroke.Line);
        }

        /// <summary>
        /// The editor draws no box of its own in any state: the card is its frame. The placeholder is the
        /// tertiary ink (the Mac's placeholderTextColor, M/TextEditorHeightReader.swift:94).
        /// </summary>
        private void StyleComposerInput()
        {
            foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
            {
                input.Resources["TextControlBackground" + state] = owner.brushes.Transparent;
                input.Resources["TextControlBorderBrush" + state] = owner.brushes.Transparent;
                input.Resources["TextControlPlaceholderForeground" + state] = owner.brushes.Tertiary;
            }
            input.Background = owner.brushes.Transparent;
            input.Foreground = owner.brushes.Brush(DesignToken.Ink);
            // At rest the template reads the control's own property, not the resource: both are set.
            input.PlaceholderForeground = owner.brushes.Tertiary;
            // The Mac's 13pt line is 16 high and its words sit 2 higher in it than Segoe's do in its 18 (their middle 17 under
            // the card's top on docs/design-system/crops/composer-codex-*.webp, 19 here): the editor stands 2 up, in the room it had.
            input.Margin = new Thickness(0, -ComposerTextLift, 0, ComposerTextLift);
            // The Mac's placeholder is a label laid over the editor at its text origin, and a label sets its words 2 inside its
            // frame (M/TextEditorHeightReader.swift:86, 93): it starts 2 after where typed words start.
            input.Loaded += (_, _) => { if (VisualChildren(input).OfType<TextBlock>().FirstOrDefault(part => part.Name == "PlaceholderTextContentPresenter") is { } placeholder) placeholder.Margin = new Thickness(ComposerPlaceholderInset, 0, 0, 0); };
        }
        /// <summary>How far the editor's words stand above their own line box, how far the placeholder starts after typed words, and the room after the armed-command chip (M/SessionPaneView.swift:564).</summary>
        private const double ComposerTextLift = 2, ComposerPlaceholderInset = 2, ComposerChipGap = 6;

        /// <summary>Shows why an attachment could not be added, or takes the notice down (null).</summary>
        private void ShowAttachmentError(string? message)
        {
            if (attachmentErrorRow is null) return;
            attachmentErrorText.Text = message ?? "";
            attachmentErrorRow.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>A row of the composer that says something is in progress: a small spinner and 11pt <c>ink2</c> words (M/SessionPaneView.swift:586-589, 623-627).</summary>
        private StackPanel ProgressRow(TextBlock words)
        {
            words.Foreground = owner.brushes.Brush(DesignToken.Ink2);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 12, 0), Visibility = Visibility.Collapsed };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = owner.brushes.Brush(DesignToken.Ink2) });
            row.Children.Add(words);
            return row;
        }

        /// <summary>
        /// Builds the pills and the right cluster (M/SessionPaneView.swift:707-771): one 32pt row, the pills
        /// leading and sized to their contents, then the resume mark, the context ring, the status-line
        /// toggle and stop / send, 6 apart.
        /// </summary>
        private void BuildToolbar()
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            attach.Click += async (_, _) => await PickAttachments();
            AutomationProperties.SetName(attach, Locale.Get("composer.attach.name")); ToolTipService.SetToolTip(attach, Locale.Get("composer.attach.tooltip"));
            SetPillIcon(attach, ComposerGlyph.Paperclip()); ShapePill(attach, true);
            SetPillIcon(effort, ComposerGlyph.Brain()); PillText(effort).MaxWidth = EffortCap;
            SetPillIcon(permission, ComposerGlyph.Shield()); PillText(permission).MaxWidth = PermissionCap;
            PillText(fast).Text = "Fast"; PillText(fast).MaxWidth = FastCap;
            SetPillIcon(more, ComposerGlyph.Ellipsis()); ShapePill(more, true);
            SetPillIcon(options, ComposerGlyph.Sliders()); ShapePill(options, true); options.Visibility = Visibility.Collapsed;
            InitializePills();
            InitializeAttachmentMenu();
            more.Click += async (_, _) => await ShowRunSettings(more);
            shellLabel.Text = Locale.Get("composer.shell.title"); shellLabel.Foreground = ink2;
            foreach (var control in new FrameworkElement[] { shellLabel, attach, model, effort, permission, fast, more, options }) selectors.Children.Add(control);

            resumeMark.Ink(ink2);
            var resume = new Border { Child = resumeMark.View, Background = b.Transparent, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(resume, Locale.Get("composer.resume.name")); ToolTipService.SetToolTip(resume, Locale.Get("composer.resume.help"));
            resumeHost = resume; toolbarActions.Children.Add(resume);
            // The context ring's button: the ring's own 32 × 32 frame (M/SessionInfoViews.swift:26-27).
            context = new Button { Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            context.Click += async (_, _) => await ShowContext();
            owner.PaintPlainButton(context, b.Transparent, b.Subtle, ink: ink2);
            AutomationProperties.SetName(context, Locale.Get("composer.context.name")); AutomationProperties.SetAutomationId(context, "context-" + id);
            toolbarActions.Children.Add(context);
            InitializeStatusLineToggle(toolbarActions);
            InitializeQueuedComposer(toolbarActions);
            send = new Button { Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0) };
            send.Click += async (_, _) => await PrimaryAction();
            foreach (var glyph in new[] { sendArrow, sendQueue, sendStop }) sendGlyph.Children.Add(glyph.View);
            send.Content = sendGlyph; ShowSendSymbol("send");
            AutomationProperties.SetName(send, Locale.Get("composer.send.name")); AutomationProperties.SetAutomationId(send, "send-" + id);
            // The button draws no fill of its own in any state; the shape under it carries run, track or err
            // and the symbol carries its own ink in every state (PaintSend), so its resources never change.
            owner.PaintPlainButton(send, b.Transparent, b.Transparent);
            send.IsEnabledChanged += (_, _) => PaintSend();
            sendHost.Children.Add(sendDisc); sendHost.Children.Add(send); toolbarActions.Children.Add(sendHost);

            toolbar = new Grid { Height = DesignMetrics.Layout.Toolbar, ColumnSpacing = ToolbarSpacing, Margin = new Thickness(10, 0, 10, 10) };
            toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            toolbar.Children.Add(selectors); Grid.SetColumn(toolbarActions, 1); toolbar.Children.Add(toolbarActions);
            toolbar.SizeChanged += (_, _) => ArrangeComposer();
            toolbarActions.SizeChanged += (_, _) => ArrangeComposer();
            AutomationProperties.SetAutomationId(toolbar, "composer-toolbar-" + id);
        }
        private Border resumeHost = null!;

        /// <summary>
        /// Builds everything under the transcript (M/SessionPaneView.swift:152-161, 518-660): the next-action
        /// rows, the permission or question card, the web-open choices and the composer card. The region runs
        /// edge to edge over the pane grid's padding and spacing, so each part keeps the Mac's own padding
        /// from the pane's edge whatever the grid's are.
        /// </summary>
        private ScrollViewer BuildComposer(Grid grid)
        {
            var b = owner.brushes;
            ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Disabled);
            StyleComposerInput();
            BuildToolbar();
            InitializeLoginRecoveryCard(); InitializeAgentWebPrompts();

            // The editor, with the command Enter is about to send before it (M/SessionPaneView.swift:564-583). The Mac's row keeps its 6
            // between the chip and the editor only while the chip shows, so the gap is the chip's own margin, not the grid's.
            inputRow = new Grid { Margin = new Thickness(8, 9, 8, 0) };
            inputRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); inputRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            styleEnterChip.Margin = new Thickness(0, 0, ComposerChipGap, 0);
            inputRow.Children.Add(styleEnterChip); Grid.SetColumn(input, 1); inputRow.Children.Add(input);

            loadingRow = ProgressRow(new TextBlock { Text = Locale.Get("composer.hint.attachmentsLoading"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            holdRow = ProgressRow(holdText);
            AutomationProperties.SetAutomationId(holdRow, "background-update-" + id);
            // An attachment that could not be added (M/SessionPaneView.swift:591-600): the accent mark, the reason in 11pt ink2 and a cross that takes it down.
            attachmentErrorRow = new Grid { ColumnSpacing = 7, Margin = new Thickness(12, 0, 12, 0), Visibility = Visibility.Collapsed };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) attachmentErrorRow.ColumnDefinitions.Add(new() { Width = width });
            attachmentErrorRow.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = b.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            attachmentErrorText.Foreground = b.Brush(DesignToken.Ink2); Grid.SetColumn(attachmentErrorText, 1); attachmentErrorRow.Children.Add(attachmentErrorText);
            AutomationProperties.SetLiveSetting(attachmentErrorText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            var dismissError = owner.SafeButton("×", () => { ShowAttachmentError(null); return Task.CompletedTask; });
            dismissError.Content = new FontIcon { Glyph = "", FontSize = 9 }; dismissError.Width = 18; dismissError.Height = 16; dismissError.MinWidth = 0; dismissError.MinHeight = 0; dismissError.Padding = new Thickness(0); dismissError.BorderThickness = new Thickness(0);
            dismissError.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow); dismissError.VerticalAlignment = VerticalAlignment.Top;
            owner.PaintPlainButton(dismissError, b.Transparent, b.Subtle, ink: b.Brush(DesignToken.Ink2));
            AutomationProperties.SetName(dismissError, Locale.Get("composer.attachment.dismissError")); Grid.SetColumn(dismissError, 2); attachmentErrorRow.Children.Add(dismissError);
            AutomationProperties.SetAutomationId(attachmentErrorRow, "attachment-error-" + id);
            // Why a draft cannot run yet (M/SessionPaneView.swift:631-643): the accent mark, the 11pt medium line, the reason in ink2.
            blockedRow = new Grid { ColumnSpacing = 7, Margin = new Thickness(12, 0, 12, 0), Visibility = Visibility.Collapsed };
            blockedRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); blockedRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            blockedRow.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = b.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var blockedWords = new StackPanel { Spacing = 3 };
            blockedWords.Children.Add(new TextBlock { Text = Locale.Get("composer.blocked.title"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap });
            inputHint.Foreground = b.Brush(DesignToken.Ink2); blockedWords.Children.Add(inputHint);
            Grid.SetColumn(blockedWords, 1); blockedRow.Children.Add(blockedWords);
            AutomationProperties.SetAutomationId(blockedRow, "run-blocked-" + id);

            attachmentsScroll.Content = attachmentChips;
            composerPanel = new StackPanel { Spacing = 9 };
            foreach (var part in new FrameworkElement[] { styleHost, attachmentsScroll, queuedInputHost, slashPaletteHost, inputRow, loadingRow, attachmentErrorRow, loginRecoveryHost, holdRow, blockedRow, toolbar, statusLineHost })
                composerPanel.Children.Add(part);
            composerCard = new Border { Child = composerPanel, CornerRadius = new CornerRadius(DesignMetrics.Radius.Composer), Background = b.Brush(DesignToken.Card) };
            composerRing = new Border { CornerRadius = new CornerRadius(DesignMetrics.Radius.Composer), IsHitTestVisible = false };
            PaintComposerRing();
            // The card's shadow is cast by a shape under it (decision Q5); the region's own margin keeps it inside the scroll view.
            var cardHost = new Grid { Margin = new Thickness(ComposerMargin) };
            cardHost.Children.Add(composerShadow = CardShadow.Caster(DesignMetrics.Radius.Composer, CardShadow.Composer, b.Brush(DesignToken.Card)));
            cardHost.Children.Add(composerCard); cardHost.Children.Add(composerRing);
            AutomationProperties.SetAutomationId(composerCard, "composer-card-" + id);
            var region = new StackPanel();
            // The pane's answered plans, folded to one line over the permission and plan cards (MainWindow.PlanCard.cs).
            planHistoryHost = new StackPanel { Margin = new Thickness(12, 6, 12, 0), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(planHistoryHost, "plan-history-host-" + id);
            foreach (var part in new FrameworkElement[] { nextActionsHost, planHistoryHost, toolPermissionHost, agentWebPromptScroll, cardHost }) region.Children.Add(part);
            var scroll = new ScrollViewer
            {
                Content = region, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Auto,
                Margin = new Thickness(-grid.Padding.Left, -grid.RowSpacing, -grid.Padding.Right, -grid.Padding.Bottom),
            };
            Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
            return scroll;
        }

        /// <summary>The run clock for the pane's session as already read (the 1-second tick reads the snapshot once).</summary>
        internal void RefreshElapsed(RunSession pane)
        {
            elapsed.Text = pane.Kind == "shell" ? "" : pane.RunTiming?.Label() ?? "";
            RefreshSessionInfo();
        }

    }
}
