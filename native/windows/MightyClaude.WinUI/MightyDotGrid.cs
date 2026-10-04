using System.Numerics;
using MightyClaude.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>
/// The dots under the Mighty diagram (macOS <c>MightyGraphDotGrid</c>), drawn with Win2D over the
/// viewport's <c>page</c> surface. As the Mac fills with a <c>.tiledImage</c>, one dot is drawn into a
/// step × step tile, rebuilt only when the zoom or the colour changes (or the device is lost), and
/// the viewport is filled with that tile through a wrapping image brush moved by the camera offset:
/// a pan or zoom frame costs one rectangle however many dots show. The control is exactly the
/// viewport's size and draws only when the camera moves, the viewport resizes or the shared
/// <c>line</c> brush is recoloured by a theme change (each coalesced by Win2D into one frame);
/// nothing animates it. The step, radius and offset rules are Core's <see cref="MightyGraphDotGrid"/>.
/// </summary>
internal sealed class MightyDotGrid
{
    private readonly SolidColorBrush ink;
    private long inkCallback = -1;
    private double zoom = 1, offsetX, offsetY;
    private bool closed;
    private CanvasRenderTarget? tile;
    private CanvasImageBrush? fill;
    private (double Step, double Radius, Windows.UI.Color Color) tileKey;

    /// <summary>The Win2D surface, laid behind the diagram canvas; it takes no input and is hidden from UI automation.</summary>
    internal CanvasControl View { get; }

    /// <param name="ink">The window's shared <c>line</c> brush; its colour is read at every draw, never set here.</param>
    internal MightyDotGrid(SolidColorBrush ink)
    {
        this.ink = ink;
        View = new CanvasControl
        {
            ClearColor = Microsoft.UI.Colors.Transparent, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAccessibilityView(View, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        View.Draw += OnDraw;
        // A new or lost device: the tile lived on the old one, so it is drawn again on this one.
        View.CreateResources += (sender, _) => { DropTile(); BuildTile(sender); };
        // A theme change recolours the shared brush in place: draw again in the new colour. The
        // callback lives only while the control is loaded, so a closed pane is not kept by the brush.
        View.Loaded += (_, _) => { if (!closed && inkCallback < 0) inkCallback = ink.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => { if (!closed) View.Invalidate(); }); };
        View.Unloaded += (sender, _) =>
        {
            if (((FrameworkElement)sender).IsLoaded || inkCallback < 0) return;
            ink.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, inkCallback); inkCallback = -1;
        };
    }

    /// <summary>Follows the camera: the dots sit at the pan offset + n · step, the step 18pt × zoom.</summary>
    internal void Follow(double zoom, double x, double y)
    {
        if (closed || zoom == this.zoom && x == offsetX && y == offsetY) return;
        this.zoom = zoom; offsetX = x; offsetY = y;
        View.Invalidate();
    }

    /// <summary>Draws again: the diagram was hidden (the timeline showed) while the theme or camera changed.</summary>
    internal void Redraw()
    {
        if (!closed) View.Invalidate();
    }

    /// <summary>Takes the viewport's size, so the surface never covers more than what shows.</summary>
    internal void Fit(double width, double height)
    {
        if (closed) return;
        View.Width = Math.Max(0, width); View.Height = Math.Max(0, height);
    }

    /// <summary>Lets go of the Win2D device when the pane closes for good.</summary>
    internal void Close()
    {
        if (closed) return;
        closed = true;
        View.Draw -= OnDraw;
        DropTile();
        View.RemoveFromVisualTree();
    }

    // ── smoke accessors ───────────────────────────────────────────────────

    internal SolidColorBrush Ink => ink;
    internal double Step => MightyGraphDotGrid.Step(zoom);
    internal double Radius => MightyGraphDotGrid.Radius(zoom);
    internal bool Shown => MightyGraphDotGrid.Shown(zoom);
    /// <summary>How many times a tile was drawn: only a zoom or colour change (or a new device) adds one.</summary>
    internal int TileBuilds { get; private set; }
    /// <summary>What the last draw put down: whether it filled the surface, the tile's step and colour, the offset it was moved by, the surface size.</summary>
    internal (bool Filled, double Step, Windows.UI.Color Color, double OffsetX, double OffsetY, double Width, double Height) LastDraw { get; private set; }

    private void DropTile()
    {
        fill?.Dispose(); tile?.Dispose(); fill = null; tile = null;
    }

    /// <summary>The step × step tile with one dot at its centre, for the current zoom and colour; kept while they hold.</summary>
    private void BuildTile(CanvasControl sender)
    {
        if (!MightyGraphDotGrid.Shown(zoom)) return;
        var key = (MightyGraphDotGrid.Step(zoom), MightyGraphDotGrid.Radius(zoom), ink.Color);
        if (fill is not null && key == tileKey) return;
        DropTile();
        var (step, radius, color) = key;
        tile = new CanvasRenderTarget(sender, (float)step, (float)step);
        using (var session = tile.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            session.FillCircle((float)(step / 2), (float)(step / 2), (float)radius, color);
        }
        // The tile's own size can round up a pixel at this DPI: the brush repeats exactly one step of it.
        fill = new CanvasImageBrush(sender, tile)
        {
            ExtendX = CanvasEdgeBehavior.Wrap, ExtendY = CanvasEdgeBehavior.Wrap,
            SourceRectangle = new Windows.Foundation.Rect(0, 0, step, step),
        };
        tileKey = key; TileBuilds++;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var (width, height) = (sender.ActualWidth, sender.ActualHeight);
        var step = MightyGraphDotGrid.Step(zoom);
        BuildTile(sender);
        var filled = false;
        if (MightyGraphDotGrid.Shown(zoom) && fill is not null)
        {
            // The tile's dot sits at its centre, so the tile starts half a step before the offset: a dot on every offset + n · step.
            fill.Transform = Matrix3x2.CreateTranslation((float)MightyGraphDotGrid.First(offsetX - step / 2, step), (float)MightyGraphDotGrid.First(offsetY - step / 2, step));
            args.DrawingSession.FillRectangle(0, 0, (float)width, (float)height, fill);
            filled = true;
        }
        LastDraw = (filled, filled ? tileKey.Step : 0, ink.Color, offsetX, offsetY, width, height);
    }
}
