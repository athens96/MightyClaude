using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MightyClaude.WinUI;

/// <summary>
/// The Mac's soft shadows under a few rounded shapes: black at a low opacity, blur 1, one point down
/// (decision Q5: a Composition <see cref="DropShadow"/>, not <c>ThemeShadow</c>, whose elevation look
/// differs). A rounded <see cref="Rectangle"/> laid in the same cell under the shape casts it: its
/// alpha mask is the shadow's outline, and the shape drawn over it hides all but the offset rim.
/// </summary>
internal static class CardShadow
{
    /// <summary>The composer card's shadow (M/SessionPaneView.swift:654).</summary>
    internal const double Composer = 0.05;
    /// <summary>The chosen chip of the Default | Mighty switch (M/SessionPaneView.swift:317).</summary>
    internal const double SegmentChip = 0.12;
    private const float Blur = 1, Drop = 1;

    /// <summary>
    /// A caster for a shape of <paramref name="radius"/> filled with <paramref name="fill"/> (the shape's
    /// own background, so no rim of another colour shows at the anti-aliased corners). Stretch it over
    /// the same cell as the shape and add it first; it takes no input and is hidden from UI automation.
    /// The composition objects live only while the caster is loaded: built on Loaded, detached and
    /// disposed on Unloaded (a pane moved between tab groups unloads and loads again).
    /// </summary>
    internal static Rectangle Caster(double radius, double opacity, Brush fill)
    {
        var caster = new Rectangle { RadiusX = radius, RadiusY = radius, Fill = fill, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(caster, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SpriteVisual? sprite = null; DropShadow? shadow = null;
        void Fit() { if (sprite is not null) sprite.Size = new Vector2((float)caster.ActualWidth, (float)caster.ActualHeight); }
        caster.Loaded += (_, _) =>
        {
            if (sprite is not null) return;
            var compositor = ElementCompositionPreview.GetElementVisual(caster).Compositor;
            shadow = compositor.CreateDropShadow();
            shadow.Color = DesignBrushes.ShadowColor; shadow.Opacity = (float)opacity; shadow.BlurRadius = Blur; shadow.Offset = new Vector3(0, Drop, 0);
            // The mask is the shape's alpha; it follows the shape, a chip first shown later included.
            // The shape owns and caches it (the same brush on every call), so it is never disposed here:
            // disposing it made the next Loaded hand back a closed brush and throw.
            shadow.Mask = caster.GetAlphaMask();
            sprite = compositor.CreateSpriteVisual(); sprite.Shadow = shadow; Fit();
            ElementCompositionPreview.SetElementChildVisual(caster, sprite);
        };
        caster.SizeChanged += (_, _) => Fit();
        caster.Unloaded += (_, _) =>
        {
            if (sprite is null) return;
            ElementCompositionPreview.SetElementChildVisual(caster, null);
            if (shadow is not null) shadow.Mask = null;
            sprite.Dispose(); shadow?.Dispose();
            sprite = null; shadow = null;
        };
        return caster;
    }

    /// <summary>The shadow a caster draws, for the smoke: null when the element casts none.</summary>
    internal static DropShadow? Of(UIElement caster) => (ElementCompositionPreview.GetElementChildVisual(caster) as SpriteVisual)?.Shadow as DropShadow;
}
