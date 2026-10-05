using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Polygon = Microsoft.UI.Xaml.Shapes.Polygon;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The reference bubble (macOS MightyGraphReferenceBubble.swift): a file an agent named, shown in a
    /// speech bubble docked beside the diagram or the timeline without leaving them. A <c>card</c> panel,
    /// radius 12 with a 1pt <c>line</c> edge and a 12pt tail on the side that faces the content; a 40pt head
    /// (the file's symbol in <c>accent</c>, its name 12 semibold over its path 10 mono <c>ink2</c>, then the
    /// flip, show-in-Explorer, open and close buttons) over a rule; the preview under it; and the two
    /// resize arrows in the bottom corner next to the content. It stands 12 from the edges of the area it
    /// covers, 420 wide (300 to 85% of the room) and as tall as the room until it is dragged.
    /// </summary>
    private sealed partial class PaneView
    {
        /// <summary>The bubble's width until it is dragged, its least size, its distance from the area's edges and its tail (M/MightyGraphReferenceBubble.swift:24-30, 169).</summary>
        private const double ReferenceDefaultWidth = 420, ReferenceMinimumWidth = 300, ReferenceMinimumHeight = 200, ReferenceMargin = 12, ReferenceTail = 12;
        /// <summary>The bubble's shadow: black at 0.18 (M/MightyGraphReferenceBubble.swift:59), cast the way every card's is (decision Q5).</summary>
        private const double ReferenceShadow = 0.18;
        /// <summary>The bubble with its tail: the element the pane shows and hides.</summary>
        private Grid? referencePanel;
        private Border? referenceCard, referenceBody;
        private Grid? referenceSymbol, referenceTail, referenceGrip;
        private Microsoft.UI.Xaml.Shapes.Rectangle? referenceShadow;
        private TextBlock? referenceTitle, referencePath;
        private Button? referenceFlip, referenceReveal, referenceOpen;
        private LocalHtmlView? referenceHtml;
        private bool referenceOnLeft;
        /// <summary>The dragged size; a height of 0 is "as tall as the room".</summary>
        private double referenceWidth = ReferenceDefaultWidth, referenceHeight;
        private int referenceGeneration;
        private ReferencePreview.Target? referenceTarget;
        private CancellationTokenSource? referenceCancellation;

        /// <summary>Both result-list and transcript clicks arrive here, inside the current pane.</summary>
        private async Task OpenReferencePreview(string path, int? line = null)
        {
            var generation = ++referenceGeneration;
            try
            {
            if (Container.Child is not Grid grid) return;
            EnsureReferencePreview(grid);
            referenceCancellation?.Cancel(); referenceCancellation?.Dispose();
            referenceHtml?.Dispose(); referenceHtml = null;
            var cancellation = referenceCancellation = new CancellationTokenSource();
            var root = Workspace.Path; filesRoot = root;
            var light = owner.service.Snapshot.Theme == "light";
            referenceTarget = null;
            // The file's own name (and the line asked for) over the path as it was written (M/MightyGraphReferenceBubble.swift:89-90).
            var name = Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } last ? last : path;
            referenceTitle!.Text = line is { } n ? Locale.Get("reference.line", new Dictionary<string, string> { ["title"] = name, ["line"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : name;
            referencePath!.Text = path; ToolTipService.SetToolTip(referencePath, path);
            ShowReferenceSymbol(ReferenceSymbol(path)); ShowReferenceFileActions(false);
            referencePanel!.Visibility = Visibility.Visible;
            referenceBody!.Child = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            FitReferencePreview();
            var target = await Task.Run(() => ReferencePreview.Resolve(path, root, line), cancellation.Token);
            if (generation != referenceGeneration || !FilesAlive) return;
            if (target is null) { ShowReferenceSymbol("questionmark.folder"); referenceBody.Child = ReferenceNotice(Locale.Get("files.preview.missing")); return; }
            referenceTarget = target; ShowReferenceFileActions(true);
            var prepared = await Task.Run(() => Prepare(root, target.RelativePath, light, cancellation.Token), cancellation.Token);
            if (generation != referenceGeneration || !FilesAlive) return;
            FrameworkElement content;
            if (prepared.Data.Failure is { } failure) content = ReferenceNotice(failure);
            else if (Path.GetExtension(target.RelativePath).ToLowerInvariant() is ".html" or ".htm")
                content = referenceHtml = new(new LocalHtmlDocument(root, target.RelativePath), Path.Combine(owner.StateDirectory, "preview-webview"));
            else if (prepared.Data.Kind.Tag == FilePreviewKindTag.Markdown && prepared.MarkdownRtf is { } rtf) content = MarkdownView(rtf);
            else if (prepared.Data.Text is not null)
            {
                content = SourceView(prepared);
                if (target.Line is { } requested && content is ScrollViewer source && !prepared.Wraps)
                    source.Loaded += (_, _) => source.ChangeView(null, Math.Max(0, requested - 1) * FilesLineHeight, null, true);
            }
            else if (prepared.Data.Kind.Tag == FilePreviewKindTag.Image) content = await ImageView(prepared.Data, ++previewRequest);
            else content = ReferenceNotice(prepared.Data.Reason ?? Locale.Get("files.preview.unsupported"));
            if (generation != referenceGeneration || !FilesAlive) return;
            // Caps are visible in this preview too; a clipped document must never look complete.
            if (prepared.Data.Truncated || prepared.RenderCapped || prepared.HighlightCapped)
            {
                var capped = new Grid { RowSpacing = 4 };
                capped.RowDefinitions.Add(new() { Height = GridLength.Auto }); capped.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
                var capText = prepared.RenderCapped ? Locale.Get("files.preview.renderCapped", new Dictionary<string, string> { ["count"] = (FilePaneDrawing.MaximumSourceUnits / 1024).ToString() })
                    : prepared.HighlightCapped ? Locale.Get("files.preview.highlightCapped", new Dictionary<string, string> { ["count"] = prepared.HighlightUnits.ToString() })
                    : Locale.Get("files.preview.truncated");
                capped.Children.Add(new TextBlock { Text = capText, FontSize = DesignMetrics.Type.Pill, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 0) });
                Grid.SetRow(content, 1); capped.Children.Add(content); content = capped;
            }
            referenceBody.Child = content;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (generation == referenceGeneration && referenceBody is not null) referenceBody.Child = ReferenceNotice(Locale.Get("files.preview.failed"));
            }
        }

        /// <summary>
        /// What the bubble says when it has no preview to show (M/MightyGraphReferenceBubble.swift:144-151): a 24pt
        /// question mark in <c>ink2</c> over the reason, 12 medium, 6 apart, centred with 20 round them.
        /// </summary>
        private StackPanel ReferenceNotice(string text)
        {
            var b = owner.brushes;
            var mark = MightySymbols.Create("doc.questionmark", 24, b.Brush(DesignToken.Ink2)); mark.HorizontalAlignment = HorizontalAlignment.Center;
            var notice = new StackPanel { Spacing = 6, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            notice.Children.Add(mark);
            notice.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.Medium, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            return notice;
        }

        /// <summary>The symbol that heads the bubble, by what the file is (M/MightyGraphReferenceBubble.swift:110-118).</summary>
        private static string ReferenceSymbol(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" or ".markdown" or ".mdx" => "doc.richtext",
            ".html" or ".htm" => "globe",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".ico" or ".svg" => "photo",
            _ => "doc.text",
        };

        private void ShowReferenceSymbol(string symbol)
        {
            if (referenceSymbol is null) return;
            referenceSymbol.Children.Clear();
            referenceSymbol.Children.Add(MightySymbols.Create(symbol, 12, owner.brushes.Brush(DesignToken.Accent)));
        }

        /// <summary>Show in Explorer and open are offered once the file is found inside the workspace.</summary>
        private void ShowReferenceFileActions(bool shown)
        {
            foreach (var button in new[] { referenceReveal, referenceOpen })
                if (button is not null) button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EnsureReferencePreview(Grid grid)
        {
            if (referencePanel is not null) return;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var card = b.Brush(DesignToken.Card);
            // The head: 40 tall and padded h12 from the panel's own edge, its parts 8 apart; the buttons plain, their symbols 12 in ink.
            var header = new Grid { Height = DesignMetrics.Layout.PreviewHead - DesignMetrics.Stroke.Line, Padding = new Thickness(12 - DesignMetrics.Stroke.Line, 0, 12 - DesignMetrics.Stroke.Line, 0), ColumnSpacing = 8 };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
            referenceSymbol = new Grid { VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(referenceSymbol);
            var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            words.Children.Add(referenceTitle = new TextBlock { FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            words.Children.Add(referencePath = new TextBlock { FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = b.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            Grid.SetColumn(words, 1); header.Children.Add(words);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            referenceFlip = ReferenceButton("rectangle.lefthalf.inset.filled.arrow.left", Locale.Get("reference.flipLeft"), "mighty-reference-flip-" + id, () => { referenceOnLeft = !referenceOnLeft; FitReferencePreview(); return Task.CompletedTask; });
            referenceReveal = ReferenceButton("folder", Locale.Get("menu.showInExplorer"), "mighty-reference-reveal-" + id, () => { if (referenceTarget is { } target) RevealInExplorer(target.RelativePath); return Task.CompletedTask; });
            referenceOpen = ReferenceButton("arrow.up.forward.app", Locale.Get("reference.openExternal"), "mighty-reference-open-" + id, async () =>
            {
                // Resolve again at the click: the workspace or file may have changed
                // while the preview was open. Nothing launches while content loads.
                if (referenceTarget is not { } target || !FilesAlive || WorkspaceFiles.Resolve(target.RelativePath, Workspace.Path) is not { } resolved) return;
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(resolved);
                await Windows.System.Launcher.LaunchFileAsync(file);
            });
            var close = ReferenceButton("xmark", Locale.Get("reference.close"), "mighty-reference-close-" + id, () => { CloseReferencePreview(); return Task.CompletedTask; });
            foreach (var button in new[] { referenceFlip, referenceReveal, referenceOpen, close }) controls.Children.Add(button);
            Grid.SetColumn(controls, 2); header.Children.Add(controls);

            var panel = new Grid();
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) panel.RowDefinitions.Add(new() { Height = height });
            panel.Children.Add(header);
            var rule = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) };
            Grid.SetRow(rule, 1); panel.Children.Add(rule);
            referenceBody = new Border(); Grid.SetRow(referenceBody, 2); panel.Children.Add(referenceBody);

            // The corner handle a block has (M/MightyGraphReferenceBubble.swift:73-83): the two arrows 10 semibold in ink2 on
            // card × 0.95, radius 5, 22 square, 2 from the corner. A thumb over it takes the drag and the double click.
            var arrows = MightySymbols.Create("arrow.up.left.and.arrow.down.right", 10, b.Brush(DesignToken.Ink2), MightySymbols.Weight.Semibold);
            arrows.HorizontalAlignment = HorizontalAlignment.Center;
            var thumb = new Thumb { Opacity = 0 };
            AutomationProperties.SetName(thumb, Locale.Get("reference.resize")); AutomationProperties.SetAutomationId(thumb, "mighty-reference-resize-" + id);
            ToolTipService.SetToolTip(thumb, Locale.Get("reference.resize"));
            thumb.DragStarted += (_, _) => { if (referenceHeight <= 0 && referencePanel is { ActualHeight: > 0 } shown) referenceHeight = shown.ActualHeight; };
            thumb.DragDelta += (_, args) => { referenceWidth += (referenceOnLeft ? 1 : -1) * args.HorizontalChange; referenceHeight = Math.Max(ReferenceMinimumHeight, referenceHeight + args.VerticalChange); FitReferencePreview(); };
            // Back to the size it opens at: 420 wide and as tall as the room.
            thumb.DoubleTapped += (_, args) => { referenceWidth = ReferenceDefaultWidth; referenceHeight = 0; FitReferencePreview(); args.Handled = true; };
            referenceGrip = new Grid { Width = 22, Height = 22, Margin = new Thickness(2 - DesignMetrics.Stroke.Line), VerticalAlignment = VerticalAlignment.Bottom };
            referenceGrip.Children.Add(new Border { CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), Background = b.Brush(DesignToken.Card, GripOpacity), Child = arrows });
            referenceGrip.Children.Add(thumb);
            Grid.SetRowSpan(referenceGrip, 3); panel.Children.Add(referenceGrip);

            referenceCard = new Border { Child = panel, Background = card, BorderThickness = new Thickness(DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line), CornerRadius = new CornerRadius(DesignMetrics.Radius.Block) };
            referenceShadow = CardShadow.Caster(DesignMetrics.Radius.Block, ReferenceShadow, card);
            // The tail (M/MightyGraphReferenceBubble.swift:166-186): 12 long and 18 across, its middle 40 down the panel; card inside a line edge.
            // Drawn pointing left, over the panel's edge so the edge opens into it; mirrored when the bubble stands on the left.
            var tailFill = new Polygon { Fill = card, Points = new PointCollection { new Point(ReferenceTail + 2, 0.5), new Point(0.7, 9), new Point(ReferenceTail + 2, 17.5) } };
            var tailEdge = new Polyline { Stroke = b.Brush(DesignToken.Line), StrokeThickness = DesignMetrics.Stroke.Line, StrokeLineJoin = PenLineJoin.Round, Points = new PointCollection { new Point(ReferenceTail + 0.5, 0), new Point(0.5, 9), new Point(ReferenceTail + 0.5, 18) } };
            referenceTail = new Grid { Width = ReferenceTail + 2, Height = 18, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5) };
            referenceTail.Children.Add(tailFill); referenceTail.Children.Add(tailEdge);
            AutomationProperties.SetAccessibilityView(referenceTail, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

            referencePanel = new Grid { VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
            referencePanel.Children.Add(referenceShadow); referencePanel.Children.Add(referenceCard); referencePanel.Children.Add(referenceTail);
            AutomationProperties.SetAutomationId(referencePanel, "mighty-reference-bubble-" + id); AutomationProperties.SetName(referencePanel, Locale.Get("reference.title"));
            Grid.SetRow(referencePanel, 1); grid.Children.Add(referencePanel);
            grid.SizeChanged += (_, _) => FitReferencePreview();
            Container.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape && referencePanel.Visibility == Visibility.Visible) { CloseReferencePreview(); args.Handled = true; } };
        }

        /// <summary>A button on the bubble's head: plain, its 12pt symbol in <c>ink</c>, the subtle wash under the pointer.</summary>
        private Button ReferenceButton(string symbol, string name, string automationId, Func<Task> action)
        {
            var button = HeaderButton(new Grid { Width = 12, Height = 12 }, owner.brushes.Brush(DesignToken.Ink));
            SetReferenceButton(button, symbol, name);
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += async (_, _) => await action();
            return button;
        }

        private void SetReferenceButton(Button button, string symbol, string name)
        {
            var box = (Grid)button.Content; box.Children.Clear();
            box.Children.Add(MightySymbols.Create(symbol, 12, owner.brushes.Brush(DesignToken.Ink)));
            AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        }

        /// <summary>
        /// Sizes and places the bubble (M/MightyGraphReferenceBubble.swift:33-45): 12 inside the diagram or
        /// timeline (the whole content row while the pane shows its transcript), on the right until flipped,
        /// its tail and its corner handle on the side that faces the content.
        /// </summary>
        private void FitReferencePreview()
        {
            if (referencePanel is null || referenceCard is null || Container.Child is not Grid grid || grid.RowDefinitions.Count < 2) return;
            // Under the Mighty bar while the Mighty view shows: the bar starts one row gap above this row.
            var top = graphHost is { Visibility: Visibility.Visible } && graphToolbar is { ActualHeight: > 0 } bar ? Math.Max(0, bar.ActualHeight + graphHost.Margin.Top) : 0;
            var roomWidth = Math.Max(0, grid.ActualWidth - grid.Padding.Left - grid.Padding.Right - 2 * ReferenceMargin);
            var roomHeight = Math.Max(0, grid.RowDefinitions[1].ActualHeight - top - 2 * ReferenceMargin);
            var width = Math.Min(Math.Max(ReferenceMinimumWidth, referenceWidth), Math.Max(ReferenceMinimumWidth, roomWidth * 0.85));
            var height = referenceHeight > 0 ? Math.Min(Math.Max(ReferenceMinimumHeight, referenceHeight), Math.Max(ReferenceMinimumHeight, roomHeight)) : Math.Max(ReferenceMinimumHeight, roomHeight);
            referenceWidth = width;
            referencePanel.Width = width + ReferenceTail; referencePanel.Height = height;
            referencePanel.Margin = new Thickness(ReferenceMargin, top + ReferenceMargin, ReferenceMargin, ReferenceMargin);
            referencePanel.HorizontalAlignment = referenceOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            // On the right the tail points left at the content, and the panel stands after it; mirrored on the left.
            var room = referenceOnLeft ? new Thickness(0, 0, ReferenceTail, 0) : new Thickness(ReferenceTail, 0, 0, 0);
            referenceCard.Margin = room; if (referenceShadow is not null) referenceShadow.Margin = room;
            if (referenceTail is not null)
            {
                referenceTail.HorizontalAlignment = referenceOnLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                referenceTail.RenderTransform = referenceOnLeft ? new ScaleTransform { ScaleX = -1 } : null;
                referenceTail.Margin = new Thickness(0, Math.Min(DesignMetrics.Layout.PreviewHead, height / 2) - 9, 0, 0);
            }
            if (referenceGrip is not null) referenceGrip.HorizontalAlignment = referenceOnLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            if (referenceFlip is not null) SetReferenceButton(referenceFlip, referenceOnLeft ? "rectangle.righthalf.inset.filled.arrow.right" : "rectangle.lefthalf.inset.filled.arrow.left", Locale.Get(referenceOnLeft ? "reference.flipRight" : "reference.flipLeft"));
        }

        internal void CloseReferencePreview()
        {
            referenceGeneration++; referenceCancellation?.Cancel();
            referenceHtml?.Dispose(); referenceHtml = null;
            if (referencePanel is not null) referencePanel.Visibility = Visibility.Collapsed;
        }
    }
}
