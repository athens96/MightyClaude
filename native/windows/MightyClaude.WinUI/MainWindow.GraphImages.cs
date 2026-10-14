using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>
        /// The preview tiles' pictures as drawn, by item (macOS AgentImageLibrary): read and decoded once, off the UI
        /// thread, through the transcript's own thumbnail path. Null is a picture that could not be read.
        /// </summary>
        private readonly Dictionary<string, BitmapImage?> graphImageTiles = [];
        private const int GraphImageTilesKept = 256;
        /// <summary>
        /// The veil under a "+k" tile's count. The Mac lays black × 0.45 under white words (M/MightyGraphImagesView.swift:76-79);
        /// here the card's own surface under its ink, so the count reads in both themes from the token colours.
        /// </summary>
        private const double ImagesMoreVeil = 0.72;
        /// <summary>The "+k" count is 15 semibold (M/MightyGraphImagesView.swift:73); the type scale has no 15.</summary>
        private const double ImagesMoreSize = 15;

        /// <summary>The pictures a preview block shows: its step's, of the request it hangs beside.</summary>
        private IReadOnlyList<AgentImageItem> GraphImageItems(MightyGraphBlock block) =>
            block.RecordId is { } step && graphDrawnRuns?.FirstOrDefault(run => block.Id.StartsWith(MightyGraphBlockSize.NodeId(run.Id, ""), StringComparison.Ordinal)) is { } run
                ? MightyGraphImages.Items(run, step, Workspace.Path) : [];

        /// <summary>
        /// The picture preview beside a step (M/MightyGraphImagesView.swift:8-55): the pictures it produced, in order, as
        /// <see cref="MightyGraphLayout.ImagesTile"/> thumbnails, three to a row; two rows folded, four unfolded, the last
        /// tile saying how many more there are. A <c>Layout.BlockHead</c> head, padding <c>Spacing.Lg</c>, its parts
        /// <c>Spacing.Sm</c> apart, over a <c>line</c> rule: the pictures symbol in <c>steerText</c>, the title 12 semibold,
        /// the count 10 <c>ink2</c> and, when there is more to show, the fold control (<c>accent</c> while unfolded). Under it
        /// the tiles, <c>Spacing.Md</c> apart and <c>Spacing.Lg</c> from the card's edge. A tile opens its picture larger.
        /// </summary>
        private Grid BuildImagesCard(MightyGraphBlock block, IReadOnlyList<AgentImageItem> items)
        {
            var b = owner.brushes;
            var expanded = graphExpanded.Contains(block.Id);
            var shown = items.Take(MightyGraphLayout.VisibleImages(items.Count, expanded)).ToList();
            var hidden = items.Count - shown.Count;
            var foldable = MightyGraphLayout.VisibleImages(items.Count, true) > MightyGraphLayout.VisibleImages(items.Count, false);
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            AutomationProperties.SetAutomationId(panel, "mighty-images-" + block.Id);
            var head = new Grid
            {
                ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Lg - BlockEdge, 0, DesignMetrics.Spacing.Lg - BlockEdge, 0), Height = DesignMetrics.Layout.BlockHead - BlockEdge + DesignMetrics.Stroke.Line,
                BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
            };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            head.Children.Add(MightySymbols.Create("photo.on.rectangle.angled", 13, b.Brush(DesignToken.SteerText)));
            var title = new TextBlock { Text = block.Title, FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); head.Children.Add(title); graphTitles[block.Id] = title;
            var count = new TextBlock { Text = Locale.Get("graph.images.count", new Dictionary<string, string> { ["count"] = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), FontSize = DesignMetrics.Type.Small, Foreground = b.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
            Grid.SetColumn(count, 2); head.Children.Add(count);
            if (foldable)
            {
                var name = Locale.Get(expanded ? "graph.images.collapse" : "graph.images.expand");
                var ink = b.Brush(expanded ? DesignToken.Accent : DesignToken.Ink2);
                var fold = HeaderButton(MightySymbols.Create(expanded ? "rectangle.compress.vertical" : "rectangle.expand.vertical", 13, ink), ink);
                AutomationProperties.SetAutomationId(fold, "mighty-images-toggle-" + block.Id);
                AutomationProperties.SetName(fold, name); ToolTipService.SetToolTip(fold, name);
                fold.Click += (_, _) => ToggleAttachmentFold(block.Id);
                Grid.SetColumn(fold, 3); head.Children.Add(fold);
            }
            panel.Children.Add(head);
            var tiles = new Grid
            {
                ColumnSpacing = DesignMetrics.Spacing.Md, RowSpacing = DesignMetrics.Spacing.Md, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(DesignMetrics.Spacing.Lg - BlockEdge, DesignMetrics.Spacing.Lg, DesignMetrics.Spacing.Lg - BlockEdge, 0),
            };
            for (var column = 0; column < MightyGraphLayout.ImagesColumns; column++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MightyGraphLayout.ImagesTile) });
            for (var row = 0; row * MightyGraphLayout.ImagesColumns < shown.Count; row++) tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MightyGraphLayout.ImagesTile) });
            for (var index = 0; index < shown.Count; index++)
            {
                var tile = BuildImageTile(block.Id, shown[index], index == shown.Count - 1 ? hidden : 0, expanded);
                Grid.SetColumn(tile, index % MightyGraphLayout.ImagesColumns); Grid.SetRow(tile, index / MightyGraphLayout.ImagesColumns);
                tiles.Children.Add(tile);
            }
            Grid.SetRow(tiles, 1); panel.Children.Add(tiles);
            return panel;
        }

        /// <summary>
        /// One thumbnail (M/MightyGraphImagesView.swift:57-99): a <c>cardRaised</c> square, radius 7 with a <c>line</c> edge,
        /// the picture filling it; a spinner while it loads, the warning mark when it cannot be read. With
        /// <paramref name="more"/> hidden pictures it carries "+k" and unfolds the block instead of opening.
        /// </summary>
        private Button BuildImageTile(string nodeId, AgentImageItem item, int more, bool expanded)
        {
            var b = owner.brushes;
            var label = item.Stored?.Source ?? (item.Alt.Length > 0 ? item.Alt : item.Located is AgentImageLocation.File file ? Path.GetFileName(file.Path) : Locale.Get("images.source.agent"));
            var picture = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var busy = new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 0, MinHeight = 0 };
            var missing = MightySymbols.Create("photo.badge.exclamationmark", 15, b.Brush(DesignToken.Ink2));
            missing.HorizontalAlignment = HorizontalAlignment.Center; missing.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(missing, Locale.Get("images.missing"));
            var face = new Grid { Width = MightyGraphLayout.ImagesTile, Height = MightyGraphLayout.ImagesTile };
            face.Children.Add(picture); face.Children.Add(busy); face.Children.Add(missing);
            if (more > 0)
            {
                face.Children.Add(new Border { Background = b.Brush(DesignToken.Card, ImagesMoreVeil) });
                face.Children.Add(new TextBlock
                {
                    Text = Locale.Get("graph.images.more", new Dictionary<string, string> { ["count"] = more.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                    FontSize = ImagesMoreSize, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var tile = new Button
            {
                Content = face, MinWidth = 0, MinHeight = 0, Width = MightyGraphLayout.ImagesTile, Height = MightyGraphLayout.ImagesTile, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), BorderThickness = new Thickness(DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
            };
            owner.PaintPlainButton(tile, b.Brush(DesignToken.CardRaised), b.Brush(DesignToken.Track));
            AutomationProperties.SetAutomationId(tile, "mighty-image-" + nodeId + "-" + item.Id);
            AutomationProperties.SetName(tile, Locale.Get("images.entry.one", new Dictionary<string, string> { ["source"] = label }));
            ToolTipService.SetToolTip(tile, label);
            Task Open() => OpenTranscriptImage(new TranscriptAction("image", item.Source, item.Stored));
            // "+k" folds the rest away; unfolding shows them in the block.
            tile.Click += async (_, _) => { if (more > 0 && !expanded) ToggleAttachmentFold(nodeId); else await Open(); };
            var open = new MenuFlyoutItem { Text = Locale.Get("images.menu.open") };
            open.Click += async (_, _) => await Open();
            var menu = new MenuFlyout(); menu.Items.Add(open); tile.ContextFlyout = menu;
            _ = ShowImageTile(item, picture, busy, missing);
            return tile;
        }

        /// <summary>
        /// Unfolds or folds a block attached beside the flow: a picture preview, an execution's AC list. Nothing moves the
        /// camera for it; it follows on only if the newest block is still in view.
        /// </summary>
        private void ToggleAttachmentFold(string nodeId)
        {
            if (!graphExpanded.Remove(nodeId)) graphExpanded.Add(nodeId);
            graphFollow.UserChangedLayout();
            RefreshMightyView(Session);
        }

        private async Task ShowImageTile(AgentImageItem item, Image picture, ProgressRing busy, FrameworkElement missing)
        {
            if (!graphImageTiles.TryGetValue(item.Id, out var bitmap))
            {
                try
                {
                    var thumbnail = await Task.Run(async () =>
                    {
                        var data = item.Stored is { } stored ? owner.service.Images.Data(stored) : item.Located switch
                        {
                            AgentImageLocation.File file => AgentImagePaths.Read(file.Path, file.Root),
                            AgentImageLocation.Inline inline => AgentImageSupport.DecodeBase64(inline.Base64),
                            _ => null,
                        };
                        if (data is null) return ((byte[] Encoded, int Width, int Height)?)null;
                        var svg = AgentImageSupport.SvgDocument(data);
                        _ = AgentImageSupport.Inspect(data, svg ? "image/svg+xml" : "image/png");
                        if (svg) data = (await NativeSvgRaster.Render(data, AgentImageSupport.ThumbnailPixels)).Png;
                        return await AgentPictures.Thumbnail(data);
                    });
                    if (thumbnail is { } ready)
                    {
                        // Decoded no larger than the tile needs at 150% on a dense display: its short side fills the tile.
                        var side = (int)(MightyGraphLayout.ImagesTile * 4);
                        bitmap = new BitmapImage();
                        if (ready.Width >= ready.Height) bitmap.DecodePixelHeight = Math.Min(side, ready.Height); else bitmap.DecodePixelWidth = Math.Min(side, ready.Width);
                        using var stream = await Buffer(ready.Encoded);
                        await bitmap.SetSourceAsync(stream);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { bitmap = null; }
                if (graphImageTiles.Count >= GraphImageTilesKept) graphImageTiles.Clear();
                graphImageTiles[item.Id] = bitmap;
            }
            busy.IsActive = false; busy.Visibility = Visibility.Collapsed;
            if (bitmap is null) missing.Visibility = Visibility.Visible; else picture.Source = bitmap;
        }
    }
}
