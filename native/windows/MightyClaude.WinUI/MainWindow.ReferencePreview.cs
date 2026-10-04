using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private Border? referencePanel, referenceBody;
        private TextBlock? referenceTitle;
        private Button? referenceFlip;
        private LocalHtmlView? referenceHtml;
        private Thumb? referenceGrip;
        private bool referenceOnLeft;
        private double referenceWidth = 420, referenceHeight = 480;
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
            referenceTitle!.Text = path + (line is { } n ? ":" + n : "");
            referencePanel!.Visibility = Visibility.Visible;
            referenceBody!.Child = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            FitReferencePreview();
            var target = await Task.Run(() => ReferencePreview.Resolve(path, root, line), cancellation.Token);
            if (generation != referenceGeneration || !FilesAlive) return;
            if (target is null) { referenceBody.Child = Centered(Locale.Get("files.preview.missing")); return; }
            referenceTarget = target;
            var prepared = await Task.Run(() => Prepare(root, target.RelativePath, light, cancellation.Token), cancellation.Token);
            if (generation != referenceGeneration || !FilesAlive) return;
            FrameworkElement content;
            if (prepared.Data.Failure is { } failure) content = Centered(failure);
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
            else content = Centered(prepared.Data.Reason ?? Locale.Get("files.preview.unsupported"));
            if (generation != referenceGeneration || !FilesAlive) return;
            // Caps are visible in this preview too; a clipped document must never look complete.
            if (prepared.Data.Truncated || prepared.RenderCapped || prepared.HighlightCapped)
            {
                var capped = new Grid { RowSpacing = 4 };
                capped.RowDefinitions.Add(new() { Height = GridLength.Auto }); capped.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
                var capText = prepared.RenderCapped ? Locale.Get("files.preview.renderCapped", new Dictionary<string, string> { ["count"] = (FilePaneDrawing.MaximumSourceUnits / 1024).ToString() })
                    : prepared.HighlightCapped ? Locale.Get("files.preview.highlightCapped", new Dictionary<string, string> { ["count"] = prepared.HighlightUnits.ToString() })
                    : Locale.Get("files.preview.truncated");
                capped.Children.Add(new TextBlock { Text = capText, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                Grid.SetRow(content, 1); capped.Children.Add(content); content = capped;
            }
            referenceBody.Child = content;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (generation == referenceGeneration && referenceBody is not null) referenceBody.Child = Centered(Locale.Get("files.preview.failed"));
            }
        }

        private void EnsureReferencePreview(Grid grid)
        {
            if (referencePanel is not null) return;
            var panel = new Grid { RowSpacing = 8, Padding = new Thickness(12) };
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var header = new Grid { ColumnSpacing = 4 }; header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            referenceTitle = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(referenceTitle);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            referenceFlip = ReferenceButton("⇄", "reference.flipLeft", () => { referenceOnLeft = !referenceOnLeft; FitReferencePreview(); return Task.CompletedTask; });
            controls.Children.Add(referenceFlip);
            controls.Children.Add(ReferenceButton("×", "reference.close", () => { CloseReferencePreview(); return Task.CompletedTask; }));
            Grid.SetColumn(controls, 1); header.Children.Add(controls); panel.Children.Add(header);
            referenceBody = new Border(); Grid.SetRow(referenceBody, 1); panel.Children.Add(referenceBody);
            var footer = new Grid(); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(24, 0, 24, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var reveal = Button(Locale.Get("menu.showInExplorer"), () => { if (referenceTarget is { } target) RevealInExplorer(target.RelativePath); return Task.CompletedTask; });
            reveal.FontSize = 11; actions.Children.Add(reveal);
            var open = Button(Locale.Get("reference.openExternal"), async () =>
            {
                // Resolve again at the click: the workspace or file may have changed
                // while the preview was open. Nothing launches while content loads.
                if (referenceTarget is not { } target || !FilesAlive || WorkspaceFiles.Resolve(target.RelativePath, Workspace.Path) is not { } resolved) return;
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(resolved);
                await Windows.System.Launcher.LaunchFileAsync(file);
            });
            open.FontSize = 11; actions.Children.Add(open); footer.Children.Add(actions);
            var grip = referenceGrip = new Thumb { Width = 22, Height = 22, Background = owner.brushes.Brush(DesignToken.Line), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            AutomationProperties.SetName(grip, Locale.Get("reference.resize")); AutomationProperties.SetAutomationId(grip, "mighty-reference-resize-" + id);
            grip.DragDelta += (_, args) => { referenceWidth += (referenceOnLeft ? 1 : -1) * args.HorizontalChange; referenceHeight += args.VerticalChange; FitReferencePreview(); };
            grip.DoubleTapped += (_, args) => { referenceWidth = 420; referenceHeight = 480; FitReferencePreview(); args.Handled = true; };
            Grid.SetColumn(grip, 1); footer.Children.Add(grip); Grid.SetRow(footer, 2); panel.Children.Add(footer);
            referencePanel = new Border { Child = panel, BorderThickness = new Thickness(DesignMetrics.Stroke.Line), BorderBrush = owner.brushes.Brush(DesignToken.Line), CornerRadius = new CornerRadius(DesignMetrics.Radius.Block), Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAutomationId(referencePanel, "mighty-reference-bubble-" + id); AutomationProperties.SetName(referencePanel, Locale.Get("reference.title"));
            Grid.SetRow(referencePanel, 1); grid.Children.Add(referencePanel);
            grid.SizeChanged += (_, _) => FitReferencePreview();
            Container.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape && referencePanel.Visibility == Visibility.Visible) { CloseReferencePreview(); args.Handled = true; } };
        }

        private static Button ReferenceButton(string label, string key, Func<Task> action)
        {
            var button = Button(label, action); button.MinWidth = 0; button.Padding = new Thickness(7, 2, 7, 2); button.FontSize = 12;
            AutomationProperties.SetName(button, Locale.Get(key)); ToolTipService.SetToolTip(button, Locale.Get(key)); return button;
        }

        private void FitReferencePreview()
        {
            if (referencePanel is null || Container.Child is not Grid grid) return;
            var availableWidth = Math.Max(120, grid.ActualWidth - 40);
            var availableHeight = Math.Max(120, grid.RowDefinitions.Count > 1 ? grid.RowDefinitions[1].ActualHeight - 16 : 480);
            referenceWidth = Math.Clamp(referenceWidth, Math.Min(300, availableWidth), availableWidth);
            referenceHeight = Math.Clamp(referenceHeight, Math.Min(200, availableHeight), availableHeight);
            referencePanel.Width = referenceWidth; referencePanel.Height = referenceHeight;
            referencePanel.HorizontalAlignment = referenceOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            if (referenceGrip is not null) { Grid.SetColumn(referenceGrip, referenceOnLeft ? 1 : 0); referenceGrip.HorizontalAlignment = referenceOnLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
            referencePanel.Background = owner.WindowBackground();
            if (referenceFlip is not null) { var text = Locale.Get(referenceOnLeft ? "reference.flipRight" : "reference.flipLeft"); AutomationProperties.SetName(referenceFlip, text); ToolTipService.SetToolTip(referenceFlip, text); }
        }

        internal void CloseReferencePreview()
        {
            referenceGeneration++; referenceCancellation?.Cancel();
            referenceHtml?.Dispose(); referenceHtml = null;
            if (referencePanel is not null) referencePanel.Visibility = Visibility.Collapsed;
        }
    }
}
