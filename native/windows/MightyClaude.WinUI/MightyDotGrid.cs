using System.Numerics;
using MightyClaude.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
/// The Win2D control sits in a host grid: when a moved pane raises the new Loaded before the old
/// Unloaded, Win2D takes the late Unloaded as final and never draws again, so a control unloaded
/// while it is still loaded is replaced by a fresh one in the same host.
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
    /// <summary>Device pixels a point, and the dot's centre in the tile, in points: the middle of the tile's middle pixel.</summary>
    private double pixels = 1, dotCentre;
    private CanvasControl canvas;

    /// <summary>The surface's host, laid behind the diagram canvas; it takes no input and is hidden from UI automation.</summary>
    internal Grid View { get; }

    /// <param name="ink">The window's shared <c>line</c> brush; its colour is read at every draw, never set here.</param>
    internal MightyDotGrid(SolidColorBrush ink)
    {
        this.ink = ink;
        View = new Grid { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAccessibilityView(View, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        canvas = NewCanvas();
        View.Children.Add(canvas);
        // A theme change recolours the shared brush in place: draw again in the new colour. The
        // callback lives only while the control is loaded, so a closed pane is not kept by the brush.
        View.Loaded += (_, _) => { if (!closed && inkCallback < 0) inkCallback = ink.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => { if (!closed) canvas.Invalidate(); }); };
        View.Unloaded += (sender, _) =>
        {
            if (((FrameworkElement)sender).IsLoaded || inkCallback < 0) return;
            ink.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, inkCallback); inkCallback = -1;
        };
    }

    private CanvasControl NewCanvas()
    {
        var control = new CanvasControl { ClearColor = Microsoft.UI.Colors.Transparent, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(control, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        control.Draw += OnDraw;
        // A new or lost device: the tile lived on the old one, so it is drawn again on this one.
        control.CreateResources += (sender, _) => { ResourceCreations++; DropTile(); BuildTile(sender); };
        control.Unloaded += (sender, _) =>
        {
            if (closed || !ReferenceEquals(sender, canvas) || !((FrameworkElement)sender).IsLoaded) return;
            // Still loaded after its Unloaded: the order was reversed and Win2D has stopped drawing.
            View.DispatcherQueue.TryEnqueue(() =>
            {
                if (closed || !ReferenceEquals(sender, canvas)) return;
                var stale = canvas;
                stale.Draw -= OnDraw;
                DropTile();
                View.Children.Remove(stale);
                stale.RemoveFromVisualTree();
                canvas = NewCanvas();
                View.Children.Add(canvas);
                Replacements++;
            });
        };
        return control;
    }

    /// <summary>Follows the camera: the dots sit at the pan offset + n · step, the step 18pt × zoom.</summary>
    internal void Follow(double zoom, double x, double y)
    {
        if (closed || zoom == this.zoom && x == offsetX && y == offsetY) return;
        this.zoom = zoom; offsetX = x; offsetY = y;
        canvas.Invalidate();
    }

    /// <summary>Draws again: the diagram was hidden (the timeline showed) while the theme or camera changed.</summary>
    internal void Redraw()
    {
        if (!closed) canvas.Invalidate();
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
        canvas.Draw -= OnDraw;
        DropTile();
        canvas.RemoveFromVisualTree();
    }

    // ── smoke accessors ───────────────────────────────────────────────────

    internal SolidColorBrush Ink => ink;
    internal double Step => MightyGraphDotGrid.Step(zoom);
    internal double Radius => MightyGraphDotGrid.Radius(zoom);
    internal bool Shown => MightyGraphDotGrid.Shown(zoom);
    /// <summary>The dot's centre in the last tile built, in device pixels from the tile's edge.</summary>
    internal double DotCentrePixels => dotCentre * pixels;
    /// <summary>How many times a tile was drawn: only a zoom or colour change (or a new device) adds one.</summary>
    internal int TileBuilds { get; private set; }
    /// <summary>How many times Win2D asked for resources again (a new device or a reloaded control); each one rebuilds the tile.</summary>
    internal int ResourceCreations { get; private set; }
    /// <summary>How many times a Win2D control that stopped drawing after a reversed Unloaded was replaced.</summary>
    internal int Replacements { get; private set; }
    /// <summary>What the last draw put down: whether it filled the surface, the tile's step and colour, the offset it was moved by, the surface size.</summary>
    internal (bool Filled, double Step, Windows.UI.Color Color, double OffsetX, double OffsetY, double Width, double Height) LastDraw { get; private set; }

    private void DropTile()
    {
        fill?.Dispose(); tile?.Dispose(); fill = null; tile = null;
    }

    /// <summary>
    /// The step × step tile with one dot in its middle, for the current zoom and colour; kept while they hold. The dot
    /// stands on the middle of a device pixel, so that pixel is all <c>line</c>, as the middle of the Mac's dot is
    /// (M/MightyGraphView.swift:1003-1008; #2D323E and #E0E2E6 on docs/design-system/screens/04-mighty-result-card-*.webp):
    /// across a pixel corner a 1pt dot is four half-covered pixels on a display at 100%.
    /// </summary>
    private void BuildTile(CanvasControl sender)
    {
        if (!MightyGraphDotGrid.Shown(zoom)) return;
        var key = (MightyGraphDotGrid.Step(zoom), MightyGraphDotGrid.Radius(zoom), ink.Color);
        if (fill is not null && key == tileKey) return;
        DropTile();
        var (step, radius, color) = key;
        tile = new CanvasRenderTarget(sender, (float)step, (float)step);
        pixels = sender.Dpi / 96;
        dotCentre = (Math.Floor(step * pixels / 2) + 0.5) / pixels;
        using (var session = tile.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            session.FillCircle((float)dotCentre, (float)dotCentre, (float)radius, color);
        }
        // The tile's own size can round up a pixel at this DPI: the brush repeats exactly one step of it. Every repeat
        // takes the tile's own pixels, never a blend of two of them, so each dot is that same dot whatever the step.
        fill = new CanvasImageBrush(sender, tile)
        {
            ExtendX = CanvasEdgeBehavior.Wrap, ExtendY = CanvasEdgeBehavior.Wrap,
            SourceRectangle = new Windows.Foundation.Rect(0, 0, step, step),
            Interpolation = CanvasImageInterpolation.NearestNeighbor,
        };
        tileKey = key; TileBuilds++;
    }

    /// <summary>
    /// Where the tile starts along one axis so that its dot stands on <paramref name="offset"/> + n · step: on a whole
    /// device pixel and <see cref="PixelPhase"/> of one more, so no repeat of the tile is read on the line between two of its pixels.
    /// </summary>
    private float TileStart(double offset, double step) => (float)((Math.Floor(MightyGraphDotGrid.First(offset - dotCentre, step) * pixels) + PixelPhase) / pixels);

    /// <summary>A part of a pixel that no step of the grid adds up to at 100%, 125%, 150% or 175% and a zoom of a tenth.</summary>
    private const double PixelPhase = 0.375;

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var (width, height) = (sender.ActualWidth, sender.ActualHeight);
        var step = MightyGraphDotGrid.Step(zoom);
        BuildTile(sender);
        var filled = false;
        if (MightyGraphDotGrid.Shown(zoom) && fill is not null)
        {
            // The tile's dot sits in its middle, so the tile starts that far before the offset: a dot on every offset + n · step.
            fill.Transform = Matrix3x2.CreateTranslation(TileStart(offsetX, step), TileStart(offsetY, step));
            args.DrawingSession.FillRectangle(0, 0, (float)width, (float)height, fill);
            filled = true;
        }
        LastDraw = (filled, filled ? tileKey.Step : 0, ink.Color, offsetX, offsetY, width, height);
    }
}
