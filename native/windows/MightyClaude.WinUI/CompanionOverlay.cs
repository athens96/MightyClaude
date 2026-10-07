using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MightyClaude.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Area = Windows.Foundation.Rect;
using Tint = Windows.UI.Color;

namespace MightyClaude.WinUI;

/// <param name="Glyph">Drawn as the open-in-app symbol instead of its words (M/AgentCompanionViews.swift:337-340).</param>
/// <param name="OwnRow">On a row of its own under the others, at the right edge: an answer too long to share
/// the row, as the plan's approve (M/AgentCompanionViews.swift CompanionPlanBubble).</param>
internal sealed record CompanionOverlayButton(string Id, string Label, bool Prominent = false, bool Glyph = false, bool OwnRow = false);
internal sealed record CompanionOverlayOption(string Label, string Description, bool Picked);

/// <summary>
/// The request a bubble shows in place of the task (M/AgentCompanionViews.swift:299-373): an approval
/// (<paramref name="Detail"/>, <paramref name="Headline"/> and its <paramref name="Code"/>, in a box when
/// <paramref name="Boxed"/>), or one question (<paramref name="Headline"/>) with its choices. The first
/// button stands at the left, the others at the right.
/// </summary>
internal sealed record CompanionOverlayRequest(bool Question, string Title, string Origin, string Detail, string? Headline, string? Code, bool Boxed,
    bool MultiSelect, IReadOnlyList<CompanionOverlayOption> Options, IReadOnlyList<CompanionOverlayButton> Buttons, string? Error = null)
{
    internal string Drawn => string.Join('\u001e', Question, Title, Origin, Detail, Headline, Code, Boxed, MultiSelect, Error,
        string.Join('\u001d', Options.Select(o => o.Label + '\u001c' + o.Description + '\u001c' + o.Picked)),
        string.Join('\u001d', Buttons.Select(b => b.Id + '\u001c' + b.Label + '\u001c' + b.Prominent + '\u001c' + b.Glyph + '\u001c' + b.OwnRow)));
}

/// <summary>
/// What the bubble says about one agent (M/AgentCompanionViews.swift:173-245): its presence mark, the
/// workspace over the title, the state word over the clock, the request and the work.
/// <paramref name="Position"/> of <paramref name="Count"/> pages it among the busy agents, and
/// <paramref name="Pending"/> is another agent's request behind this page.
/// </summary>
internal sealed record CompanionOverlayCard(string Key, bool Light, string Status, string Workspace, string Title, string State, AgentRunTiming? Timing,
    string? Request, string Work, int Position = 0, int Count = 0, bool Pending = false, CompanionOverlayRequest? Approval = null)
{
    /// <summary>Everything a redraw depends on but the running clock, which the window follows by itself.</summary>
    internal string Drawn => string.Join('\u001f', Key, Light, Status, Workspace, Title, State, Timing is null, Request, Work, Position, Count, Pending, Approval?.Drawn);
}

/// <summary>One text the last frame drew, for the GUI smoke: its words, type, the ink's token and where it stands.</summary>
internal sealed record CompanionOverlayText(string Value, double Size, int Weight, string Family, int Lines, bool Trimmed, DesignToken Ink, Area Bounds);

/// A layered HWND instead of a focusable XAML window. WS_EX_NOACTIVATE plus
/// MA_NOACTIVATE keep IME ownership with the user's current application even
/// while the pet is dragged or its approval buttons are clicked. The window is
/// the Mac's panel (M/AgentCompanionViews.swift:121-169, M/ResizeEdges.swift:46-75):
/// the bubble above the pet, drawn with Win2D in the app's design tokens.
internal sealed class CompanionOverlay : IDisposable
{
    private const uint ExStyle = 0x08000000 | 0x00080000 | 0x00000080; // NOACTIVATE, LAYERED, TOOLWINDOW
    /// The bubble (M/AgentCompanionViews.swift:190-193, 365-367): padding 12 inside a radius of 18.
    private const double Inset = 12, Corner = 18;
    /// A small system button (`.controlSize(.small)`, M/AgentCompanionViews.swift:336-362) and the gap between two.
    private const double ButtonHeight = 20, ButtonRadius = DesignMetrics.Radius.FileRow, ButtonGap = 6;
    /// The pager's row and its chevron capsules (M/AgentCompanionViews.swift:262-294).
    private const double PagerHeight = 16, PagerButton = 22;
    /// The pet's menu: rows of 26 with 13pt words, 4 inside a radius of 8, the row under the pointer on a wash of radius 4.
    private const double MenuRow = 26, MenuInset = 4, MenuRowRadius = 4, MenuType = 13;
    private const float Wide = 4096, LineScale = 0.9f;
    private static readonly string[] Installed = CanvasTextFormat.GetSystemFontFamilies();
    private static string Family(string names) => names.Split(',').Select(name => name.Trim()).FirstOrDefault(name => Installed.Contains(name, StringComparer.OrdinalIgnoreCase)) ?? "Segoe UI";
    /// SF Pro and SF Mono as the app draws them (DesignMetrics.Font), the icon font a FontIcon uses, and the symbol font that has the raised hand.
    private static readonly string Body = Family(DesignMetrics.Font.Body), Mono = Family(DesignMetrics.Font.Mono), Icons = Family("Segoe Fluent Icons, Segoe MDL2 Assets"), Symbols = Family("Segoe UI Symbol");

    private readonly WindowProc procedure;
    private readonly string className = "MightyCompanion-" + Guid.NewGuid().ToString("N");
    private readonly nint instance = GetModuleHandle(null);
    private readonly CanvasDevice device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true);
    private nint window, dc, bitmap, oldBitmap, bits;
    private int pixelWidth, pixelHeight, left, top;
    private double scale = 1;
    private byte[] pixels = [];
    private CanvasRenderTarget? surface, cardLayer;
    private CanvasBitmap? sprite;
    private bool cardDirty = true, composeDirty = true, fitPending;
    private int lastRow = -1, lastColumn = -1, lastSpin = -1;
    /// <summary>The frame last asked for, which a change of size draws again at once; false until the first one.</summary>
    private bool presented, presentedStill;
    private int presentedRow, presentedColumn;
    /// <summary>Frames in a row the system refused, and how many of them (three seconds' worth) end the pet.</summary>
    private int refused;
    private const int RefusedFrames = 36;
    private string? drawnElapsed;
    private CompanionOverlayCard? card;
    private readonly List<(Area Bounds, string Id)> hitButtons = [];
    private readonly List<(Area Bounds, string Id, string Label)> menuItems = [];
    private Area menuArea;
    private bool menuOpen;
    private int menuHover = -1;
    private Vector2? spinner;
    private bool mouseDown, dragged, disposed, showBubble, scaling, scalingMissed;
    private int walkingRow = 1, swipeDirection, wheelDelta;
    private long walkedAt;
    private Point dragStart, lastPointer, startOrigin;
    private (int Left, int Top, int Width, int Height) startFrame;
    private string? pressedAction, pressedKey;
    private int width, bubbleHeight, startWidth, startDrawnWidth, startHeight;
    /// <summary>How much of the bubble's content the screen's height left no room for (0 unless a request is taller than the screen).</summary>
    private int bubbleCut;
    private double panelWidth, height;
    private double? fixedBubbleHeight, startFixedHeight;
    private CompanionResizeEdges resizeEdges;
    internal event Action<string, string>? Action;
    internal event Action<int, int>? Moved;
    internal event Action<double?, double?>? Resized;
    internal nint Handle => window;
    internal bool NonActivating => (GetWindowLongPtr(window, -20).ToInt64() & 0x08000000) != 0;
    /// <summary>Set by the GUI smoke to read back every text a frame draws.</summary>
    internal List<CompanionOverlayText>? SmokeTexts;
    /// <summary>What the smoke reads back about a text of this pass: the weight the design asks for, and whether its line limit cut it short.</summary>
    private readonly Dictionary<CanvasTextLayout, (int Weight, bool Shortened)> noted = new(ReferenceEqualityComparer.Instance);
    /// <summary>Set by the GUI smoke: the desktop it runs on is not its own, and a click in another window must not put the menu away mid-check.</summary>
    internal bool SmokeKeepsMenu;
    internal bool SmokeClick(string id)
    {
        if (cardDirty) RenderCard();
        Area? found = id == "toggle" ? PetArea : menuOpen ? menuItems.Where(item => item.Id == id).Select(item => (Area?)item.Bounds).FirstOrDefault()
            : hitButtons.Where(item => item.Id == id).Select(item => (Area?)item.Bounds).FirstOrDefault();
        if (found is not { } hit) return false;
        var x = (int)((hit.Left + hit.Right) * scale / 2); var y = (int)((hit.Top + hit.Bottom) * scale / 2);
        var point = (nint)((y << 16) | x); SendMessage(window, 0x0201, 1, point); SendMessage(window, 0x0202, 0, point); return true;
    }
    internal static nint ForegroundWindow => GetForegroundWindow();
    /// <summary>
    /// Whether the pet's window is the one its thread made active or gave the keyboard to. Unlike the desktop's front
    /// window this is the thread's own, so the smoke can read it whether or not its program is the one in front.
    /// </summary>
    internal bool TookThread => window != 0 && (GetActiveWindow() == window || GetFocus() == window);
    /// <summary>A right click on the pet.</summary>
    internal void SmokeContextMenu()
    {
        var pet = PetArea; SendMessage(window, 0x0205, 0, (nint)(((int)((pet.Top + pet.Height / 2) * scale) << 16) | (int)((pet.Left + pet.Width / 2) * scale)));
    }
    internal IReadOnlyList<(string Id, string Label)> SmokeMenu => menuOpen ? menuItems.Select(item => (item.Id, item.Label)).ToArray() : [];
    internal (int Width, int Height, int Right, int Bottom, bool AutomaticHeight) SmokeLayout =>
        (width, bubbleHeight, left + pixelWidth, top + pixelHeight, fixedBubbleHeight is null || card?.Approval is not null);
    /// <summary>The window, the bubble and the pet's frame in logical pixels, and the drawing's scale.</summary>
    internal (double Width, double Height, Area Bubble, Area Pet, double Scale) SmokeGeometry => (panelWidth, height, BubbleArea, PetArea, scale);
    internal static (string Body, string Mono, string Icons, string Symbols) SmokeFonts => (Body, Mono, Icons, Symbols);
    /// <summary>A point of the bubble (in its own coordinates) as a resize strip or none.</summary>
    internal CompanionResizeEdges SmokeResizeHit(int x, int y) { var bubble = BubbleArea; return ResizeAt(new Point { X = (int)((bubble.X + x) * scale), Y = (int)((bubble.Y + y) * scale) }); }
    internal void SmokeResize(CompanionResizeEdges edges, int dx, int dy, bool cancel = false)
    {
        startWidth = width; startDrawnWidth = (int)DrawnWidth; startHeight = bubbleHeight; startFixedHeight = fixedBubbleHeight; startOrigin = new() { X = left, Y = top }; startFrame = (left, top, pixelWidth, pixelHeight);
        mouseDown = true; dragged = false; swipeDirection = 0; resizeEdges = edges; pressedAction = "resize"; pressedKey = card?.Key;
        ResizeFromPointer((int)(dx * scale), (int)(dy * scale));
        if (cancel) CancelResize(); else SendMessage(window, 0x0202, 0, 0);
    }
    internal void SmokeResetSize() => ResetSize();
    /// <summary>The display scale changes, as when the pet is carried to another monitor.</summary>
    internal void SmokeDpi(int dpi) => SendMessage(window, 0x02e0, (nuint)(dpi | dpi << 16), 0);
    // The isolated window's own rendered buffer; this never reads the desktop.
    internal (uint Width, uint Height, byte[] Pixels) CaptureForSmoke() => ((uint)pixelWidth, (uint)pixelHeight, pixels.ToArray());

    internal CompanionOverlay(CompanionPreferences preferences)
    {
        width = (int)CompanionBubbleLayout.Width(preferences.BubbleWidth);
        fixedBubbleHeight = CompanionBubbleLayout.Height(preferences.BubbleHeight);
        (panelWidth, height) = CompanionBubbleLayout.PanelSize(width, fixedBubbleHeight, false);
        procedure = WndProc;
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 8, Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance, ClassName = className, Cursor = LoadCursor(0, 32512) };
        if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Made where it was left, so it takes the scale of the display it will stand on: made elsewhere and moved there,
        // it would be sized for the first display and then change its size around its bottom edge, away from the saved top.
        window = CreateWindowEx(ExStyle, className, "Mighty Claude Companion", 0x80000000, preferences.Left ?? 0, preferences.Top ?? 0, (int)panelWidth, (int)height, 0, 0, instance, 0);
        if (window == 0) { UnregisterClass(className, instance); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            scale = Math.Clamp(GetDpiForWindow(window) / 96.0, 1, 4);
            dc = CreateCompatibleDC(0); if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Allocate((int)Math.Ceiling(panelWidth * scale), (int)Math.Ceiling(height * scale)); var work = WorkArea(new Point { X = preferences.Left ?? 0, Y = preferences.Top ?? 0 });
            // The Mac's first place (M/AgentCompanionViews.swift:438-439): 300 in from the right edge and 24 above the bottom one.
            left = preferences.Left ?? work.Right - (int)Math.Round(300 * scale); top = preferences.Top ?? work.Bottom - pixelHeight - (int)Math.Round(24 * scale);
            ClampPosition();
        }
        catch { Dispose(); throw; }
    }
    /// <summary>The pet's sprite sheet as premultiplied BGRA, 1536 wide; empty for no pet, which draws the paw.</summary>
    internal void SetAtlas(byte[] premultipliedBgra)
    {
        sprite?.Dispose(); sprite = null; lastRow = -1; composeDirty = true;
        var rows = premultipliedBgra.Length / (1536 * 4);
        if (rows >= 9 * 208 && premultipliedBgra.Length == rows * 1536 * 4)
            sprite = CanvasBitmap.CreateFromBytes(device, premultipliedBgra, 1536, rows, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
    }
    internal void SetCard(CompanionOverlayCard? value, bool visible)
    {
        var shown = visible && value is not null;
        if (mouseDown && resizeEdges != CompanionResizeEdges.None && (!shown || card?.Key != value?.Key)) CancelResize();
        var changed = showBubble != shown || card?.Drawn != value?.Drawn;
        card = value; showBubble = shown;
        if (!changed) return;
        cardDirty = true;
        if (mouseDown) fitPending = true; else Fit();
    }
    internal void Show(bool visible)
    {
        if (!visible && menuOpen) { menuOpen = false; composeDirty = true; }
        if (window != 0) ShowWindow(window, visible ? 8 : 0);
    }

    private bool Tall => showBubble && card?.Approval is not null;
    /// The approval is never narrower than the default (M/AgentCompanionViews.swift:365).
    private double DrawnWidth => Tall ? CompanionBubbleLayout.ApprovalWidth(width) : width;
    /// The bubble stands on the pet: 2 above its frame, 12 in from each side of the window (M/AgentCompanionViews.swift:129-145).
    private Area BubbleArea => new(CompanionBubbleLayout.SidePadding, height - CompanionBubbleLayout.Chrome + CompanionBubbleLayout.Padding - bubbleHeight, DrawnWidth, bubbleHeight);
    /// The pet's 125 × 135 frame, centred at the bottom above the 8 of padding (M/AgentCompanionViews.swift:141-145).
    private Area PetArea => new((panelWidth - CompanionBubbleLayout.PetWidth) / 2, height - CompanionBubbleLayout.Padding - CompanionBubbleLayout.PetHeight, CompanionBubbleLayout.PetWidth, CompanionBubbleLayout.PetHeight);
    /// The saved top is the one of the window around a bubble that follows its content, so the pet returns to where it stood whatever was showing.
    private int SavedTop => top + pixelHeight - (int)Math.Ceiling(CompanionBubbleLayout.PanelSize(width, fixedBubbleHeight, false).Height * scale);

    private void Allocate(int w, int h)
    {
        if (bitmap != 0) { SelectObject(dc, oldBitmap); DeleteObject(bitmap); bitmap = 0; }
        pixelWidth = w; pixelHeight = h; pixels = new byte[w * h * 4]; cardDirty = true; composeDirty = true;
        var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length };
        bitmap = CreateDIBSection(dc, ref info, 0, out bits, 0, 0); if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); oldBitmap = SelectObject(dc, bitmap);
        // Drawn in logical pixels through a scale, into targets of exactly the window's pixels.
        surface?.Dispose(); cardLayer?.Dispose();
        surface = new CanvasRenderTarget(device, w, h, 96); cardLayer = new CanvasRenderTarget(device, w, h, 96);
    }
    /// <summary>
    /// Sizes the window around the bubble (M/AgentCompanionViews.swift:463-475, M/ResizeEdges.swift:61-67). It grows
    /// upward from its bottom edge; sideways it keeps its left edge, its right one (<paramref name="keep"/> 1) or its
    /// middle (0), measured from <paramref name="from"/> while an edge is dragged.
    /// </summary>
    private void Fit(int keep = -1, (int Left, int Top, int Width, int Height)? from = null)
    {
        if (dc == 0) return;
        fitPending = false;
        var origin = from ?? (left, top, pixelWidth, pixelHeight);
        // Whatever a request holds, the bubble is never taller than the screen it stands on. What does not fit is cut
        // at the bubble's top (RenderCard), so its buttons, which stand last, are still there to press.
        var screen = WorkArea(new Point { X = origin.Left + origin.Width / 2, Y = origin.Top + origin.Height - 64 });
        var needed = MeasureBubble();
        bubbleHeight = Math.Min(needed, Math.Max(1, (int)((screen.Bottom - screen.Top) / scale - CompanionBubbleLayout.Chrome)));
        bubbleCut = needed - bubbleHeight;
        var size = CompanionBubbleLayout.PanelSize(width, fixedBubbleHeight, Tall);
        // Never shorter than what the bubble needs: the Mac would cut it off at the window's top.
        panelWidth = size.Width; height = Math.Max(size.Height, bubbleHeight + CompanionBubbleLayout.Chrome);
        int w = (int)Math.Ceiling(panelWidth * scale), h = (int)Math.Ceiling(height * scale);
        left = origin.Left + (keep < 0 ? 0 : keep > 0 ? origin.Width - w : (origin.Width - w) / 2); top = origin.Top + origin.Height - h;
        // A menu belongs to the place it opened at; a window of another size puts it away.
        if (w != pixelWidth || h != pixelHeight) { Allocate(w, h); menuOpen = false; }
        cardDirty = true;
        // The new size and place arrive with the picture drawn for them, which UpdateLayeredWindow carries together:
        // moved first, the window would show the last picture at the new place until the next frame.
        ClampPosition(move: !presented);
        if (presented) Draw(presentedRow, presentedColumn, presentedStill);
    }
    private int MeasureBubble()
    {
        if (card is not { } current) return bubbleHeight;
        var palette = DesignTokens.Palette(!current.Light); var inner = DrawnWidth - 2 * Inset;
        if (current.Approval is { } request) return (int)Math.Ceiling(RequestContent(null, current, request, palette, 0, 0, inner) + 2 * Inset);
        var natural = TaskContent(null, current, palette, 0, 0, inner, fixedBubbleHeight - 2 * Inset) + 2 * Inset;
        return (int)Math.Ceiling(Math.Max(natural, fixedBubbleHeight ?? 0));
    }

    internal void Draw(int row, int column, bool reducedMotion = false)
    {
        if (disposed || surface is null || cardLayer is null) return;
        if (menuOpen && MenuDismissed()) { menuOpen = false; composeDirty = true; }
        if (menuOpen && !SmokeKeepsMenu)
        {
            // The row under the pointer takes the wash; the window is not told when the pointer leaves it, so it looks.
            GetCursorPos(out var pointer); var over = menuItems.FindIndex(item => Inside(item.Bounds, (pointer.X - left) / scale, (pointer.Y - top) / scale));
            if (over != menuHover) { menuHover = over; composeDirty = true; }
        }
        // Carried, the pet walks the way it goes and stands again once the pointer rests (M/CompanionPetMotion.swift:41-57).
        if (mouseDown && dragged && pressedAction == "toggle" && Environment.TickCount64 - walkedAt < 350) { row = walkingRow; column = CompanionPet.Frame(row, Environment.TickCount64 / 1000.0, reducedMotion); }
        // The clock ticks by itself, as the Mac's does once a second (M/AgentElapsedView.swift:9); a request draws none.
        if (showBubble && card is { Approval: null } timed && timed.Timing?.Label() != drawnElapsed) cardDirty = true;
        if (cardDirty) RenderCard();
        presented = true; presentedRow = row; presentedColumn = column; presentedStill = reducedMotion;
        var spin = spinner is null || reducedMotion ? 0 : (int)(Environment.TickCount64 * 12 / 1000 % 12);
        if (!composeDirty && lastRow == row && lastColumn == column && lastSpin == spin) return;
        lastRow = row; lastColumn = column; lastSpin = spin; composeDirty = false;
        var palette = DesignTokens.Palette(card?.Light != true);
        using (var session = surface.CreateDrawingSession())
        {
            session.Clear(default(Tint)); session.DrawImage(cardLayer);
            session.Transform = Matrix3x2.CreateScale((float)scale); session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            if (spinner is { } centre) Spinner(session, centre, spin, Paint(palette.Accent));
            Pet(session, Math.Clamp(row, 0, 8), column, palette);
            if (menuOpen) Menu(session, palette);
        }
        surface.GetPixelBytes(pixels.AsBuffer());
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var destination = new Point { X = left, Y = top }; var source = new Point(); var size = new Point { X = pixelWidth, Y = pixelHeight }; var blend = new Blend { Operation = 0, SourceAlpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(window, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
        {
            // A frame the system does not take is drawn again on the next tick; only a window that stays refused ends the pet.
            var refusal = Marshal.GetLastWin32Error(); composeDirty = true;
            if (++refused >= RefusedFrames) throw new Win32Exception(refusal);
            return;
        }
        refused = 0;
        // Showing the frame can itself bring a change of display, which sizes the window again and draws it inside
        // that call: this frame was then for the old size, and the next tick draws the one for the new.
        if (size.X != pixelWidth || size.Y != pixelHeight) composeDirty = true;
        SetWindowPos(window, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // topmost, no activation/move/resize
    }
    /// The bubble, drawn once per change: a card of the app's theme at radius 18 with a hairline of the primary ink at 10%
    /// (M/AgentCompanionViews.swift:190-193), or of the amber at 45% around a request (M:366-367). The Mac's material blurs
    /// what is behind it; a layered window cannot, so the card is the opaque `card` token.
    private void RenderCard()
    {
        cardDirty = false; composeDirty = true; hitButtons.Clear(); spinner = null; SmokeTexts?.Clear(); noted.Clear(); drawnElapsed = card?.Timing?.Label();
        using var session = cardLayer!.CreateDrawingSession();
        session.Clear(default(Tint));
        if (!showBubble || card is not { } current) return;
        session.Transform = Matrix3x2.CreateScale((float)scale); session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        var palette = DesignTokens.Palette(!current.Light); var bubble = BubbleArea;
        session.FillRoundedRectangle(bubble, (float)Corner, (float)Corner, Paint(palette.Card));
        session.DrawRoundedRectangle((float)bubble.X + .5f, (float)bubble.Y + .5f, (float)bubble.Width - 1, (float)bubble.Height - 1, (float)Corner - .5f, (float)Corner - .5f,
            current.Approval is null ? Paint(palette.Ink, 0.10) : Paint(palette.Wait, 0.45), (float)DesignMetrics.Stroke.Line);
        // A bubble held to the screen's height shows the end of what it holds: the content starts above the bubble
        // by what was cut and is clipped to it.
        double x = bubble.X + Inset, y = bubble.Y + Inset - bubbleCut, w = bubble.Width - 2 * Inset;
        using var clip = bubbleCut > 0 ? session.CreateLayer(1f, bubble) : null;
        if (current.Approval is { } request) RequestContent(session, current, request, palette, x, y, w);
        else TaskContent(session, current, palette, x, y, w, fixedBubbleHeight - 2 * Inset);
    }

    /// <summary>
    /// The task (M/AgentCompanionViews.swift:213-245) inside the bubble's padding: lays it out, draws it when given a
    /// session, and returns its height. <paramref name="room"/> is what a bubble of a fixed height leaves for it.
    /// </summary>
    private double TaskContent(CanvasDrawingSession? session, CompanionOverlayCard current, DesignPalette palette, double x, double y, double w, double? room)
    {
        var pager = current.Count > 1 || current.Pending; var asks = current.Request is { Length: > 0 };
        // The header (M:215-230): the presence mark in its 15pt frame, the workspace over the title, and at the right
        // the state word over the clock, each column centred on the row.
        var elapsed = current.Timing?.Label();
        using var state = Text(current.State, 9, 400, Wide);
        using var clock = elapsed is null ? null : Text(elapsed, 10, 400, Wide, family: Mono);
        const double clockIcon = 10, clockGap = 4;
        var trailing = Math.Ceiling(Math.Max(state.LayoutBounds.Width, clock is null ? 0 : clockIcon + clockGap + clock.LayoutBounds.Width));
        var trailingHeight = state.LayoutBounds.Height + (clock is null ? 0 : 2 + clock.LayoutBounds.Height);
        // 15 for the mark, then 7 before the names, 7 before the spacer and 7 after it (HStack(spacing: 7), M:215).
        var leading = Math.Max(1, w - 15 - 7 - 7 - 7 - trailing);
        using var workspace = current.Workspace.Length == 0 ? null : Text(current.Workspace, 9, 500, leading);
        using var title = Text(current.Title, 11, 600, leading);
        var leadingHeight = (workspace is null ? 0 : workspace.LayoutBounds.Height + 1) + title.LayoutBounds.Height;
        var head = Math.Ceiling(Math.Max(15, Math.Max(leadingHeight, trailingHeight)));
        if (session is not null)
        {
            Presence(session, current.Status, palette, x + 7.5, y + head / 2);
            var line = Math.Round(y + (head - leadingHeight) / 2);
            if (workspace is not null) { Put(session, workspace, current.Workspace, x + 22, line, palette, DesignToken.Ink2); line += workspace.LayoutBounds.Height + 1; }
            Put(session, title, current.Title, x + 22, line, palette, DesignToken.Ink);
            line = Math.Round(y + (head - trailingHeight) / 2);
            Put(session, state, current.State, x + w - state.LayoutBounds.Width, line, palette, DesignToken.Ink2);
            if (clock is not null)
            {
                line += state.LayoutBounds.Height + 2;
                Icon(session, Icons, "\uE823", clockIcon, x + w - clock.LayoutBounds.Width - clockGap - clockIcon / 2, line + clock.LayoutBounds.Height / 2, Paint(palette.Ink2));
                Put(session, clock, elapsed!, x + w - clock.LayoutBounds.Width, line, palette, DesignToken.Ink2);
            }
        }
        // "요청" and "작업", each a 9pt label two down from the top of its words (M:231-242).
        string asked = Locale.Get("graph.timeline.kind.request"), doing = Locale.Get("transcript.tool.fallback");
        using var askedTag = asks ? Text(asked, 9, 500, Wide) : null;
        using var doingTag = Text(doing, 9, 500, Wide);
        double askedWidth = askedTag is null ? 0 : Math.Ceiling(askedTag.LayoutBounds.Width), doingWidth = Math.Ceiling(doingTag.LayoutBounds.Width);
        double requestWidth = Math.Max(1, w - askedWidth - 6), workWidth = Math.Max(1, w - doingWidth - 6);
        // Two lines each while the height follows the content; a taller bubble shows more of the request and the
        // work instead of padding (M:198-199), and the work keeps its lines first (layoutPriority, M:241).
        int requestLines = 2, workLines = 2;
        if (room is { } available)
        {
            using var probe = Text("Ag", 11, 400, Wide);
            var apart = head + (asks ? 6 + 6 + DesignMetrics.Stroke.Line : 0) + 6 + (pager ? 5 + PagerHeight : 0);
            var lines = Math.Max(asks ? 2 : 1, (int)((available - apart) / probe.LayoutBounds.Height));
            using var allWork = Text(current.Work, 11, 400, workWidth, 0);
            workLines = Math.Clamp(allWork.LineCount, 1, lines - (asks ? 1 : 0));
            if (asks) { using var allRequest = Text(current.Request!, 11, 400, requestWidth, 0); requestLines = Math.Clamp(allRequest.LineCount, 1, lines - workLines); }
        }
        var cursor = y + head;
        if (asks)
        {
            cursor += 6;
            using var words = Text(current.Request!, 11, 400, requestWidth, requestLines);
            if (session is not null) { Put(session, askedTag!, asked, x, cursor + 2, palette, DesignToken.Ink2); Put(session, words, current.Request!, x + askedWidth + 6, cursor, palette, DesignToken.Ink); }
            cursor += Math.Ceiling(Math.Max(askedTag!.LayoutBounds.Height + 2, words.LayoutBounds.Height)) + 6;
            session?.FillRectangle((float)x, (float)cursor, (float)w, (float)DesignMetrics.Stroke.Line, Paint(palette.Line));
            cursor += DesignMetrics.Stroke.Line;
        }
        cursor += 6;
        using (var words = Text(current.Work, 11, 400, workWidth, workLines))
        {
            if (session is not null) { Put(session, doingTag, doing, x, cursor + 2, palette, DesignToken.Ink2); Put(session, words, current.Work, x + doingWidth + 6, cursor, palette, DesignToken.Ink); }
            cursor += Math.Ceiling(Math.Max(doingTag.LayoutBounds.Height + 2, words.LayoutBounds.Height));
        }
        if (pager) { cursor += 5; if (session is not null) Pager(session, current, palette, x, cursor, w); cursor += PagerHeight; }
        return cursor - y;
    }

    /// <summary>An approval or one question (M/AgentCompanionViews.swift:299-373), laid out and drawn like the task: 7 between its parts.</summary>
    private double RequestContent(CanvasDrawingSession? session, CompanionOverlayCard current, CompanionOverlayRequest request, DesignPalette palette, double x, double y, double w)
    {
        const double spacing = 7;
        // The header (M:307-314): the symbol in the amber ink, "승인 요청" or "선택 요청 2/3", and where it comes from, which keeps its end.
        double iconWidth = request.Question ? 14 : 10.5, iconHeight = 13.5;
        using var title = Text(request.Title, 11, 600, Wide, tabular: true);
        var kept = KeepEnd(request.Origin, 9, 400, Math.Max(1, w - iconWidth - 6 - title.LayoutBounds.Width - 12));
        using var origin = Text(kept, 9, 400, Wide);
        var head = Math.Ceiling(Math.Max(iconHeight, Math.Max(title.LayoutBounds.Height, origin.LayoutBounds.Height)));
        if (session is not null)
        {
            if (request.Question) QuestionBubble(session, x + iconWidth / 2, y + head / 2, palette);
            else Icon(session, Symbols, "\u270B", iconHeight, x + iconWidth / 2, y + head / 2, Paint(palette.WaitText));
            Put(session, title, request.Title, x + iconWidth + 6, Math.Round(y + (head - title.LayoutBounds.Height) / 2), palette, DesignToken.Ink);
            Put(session, origin, kept, x + w - origin.LayoutBounds.Width, Math.Round(y + (head - origin.LayoutBounds.Height) / 2), palette, DesignToken.Ink2);
        }
        var cursor = y + head;
        void Stack(string? value, double size, int weight, int lines, DesignToken ink, string? family = null)
        {
            if (value is not { Length: > 0 }) return;
            cursor += spacing;
            using var words = Text(value, size, weight, w, lines, family);
            if (session is not null) Put(session, words, value, x, cursor, palette, ink);
            cursor += Math.Ceiling(words.LayoutBounds.Height);
        }
        if (request.Question)
        {
            Stack(request.Headline, 11, 500, 3, DesignToken.Ink);
            // A choice (M:391-412): its label over its description on the subtle wash, or on the accent at 16% once picked.
            foreach (var option in request.Options)
            {
                cursor += spacing;
                var lead = request.MultiSelect ? 10 + 6 : 0;
                using var label = Text(option.Label, 11, 500, Math.Max(1, w - 16 - lead));
                using var description = option.Description.Length == 0 ? null : Text(option.Description, 9, 400, Math.Max(1, w - 16 - lead));
                var rowHeight = Math.Ceiling(5 + label.LayoutBounds.Height + (description is null ? 0 : 1 + description.LayoutBounds.Height) + 5);
                if (session is not null)
                {
                    session.FillRoundedRectangle((float)x, (float)cursor, (float)w, (float)rowHeight, (float)DesignMetrics.Radius.Search, (float)DesignMetrics.Radius.Search,
                        option.Picked ? Paint(palette.Accent, 0.16) : Paint(DesignTokens.Subtle(palette), DesignMetrics.Opacity.Subtle));
                    if (request.MultiSelect) Icon(session, Icons, option.Picked ? "\uE73D" : "\uE739", 10, x + 8 + 5, cursor + 5 + 1 + 5, Paint(option.Picked ? palette.Accent : palette.Ink2));
                    Put(session, label, option.Label, x + 8 + lead, cursor + 5, palette, DesignToken.Ink);
                    if (description is not null) Put(session, description, option.Description, x + 8 + lead, cursor + 5 + label.LayoutBounds.Height + 1, palette, DesignToken.Ink2);
                    hitButtons.Add((new(x, cursor, w, rowHeight), "questions"));
                }
                cursor += rowHeight;
            }
        }
        else
        {
            Stack(request.Detail, 10, 400, 1, DesignToken.Ink2);
            Stack(request.Headline, 11, 500, 2, DesignToken.Ink);
            if (request.Boxed && request.Code is { Length: > 0 } code)
            {
                // The command, three lines in a box of the primary ink at 6% (M:327-330).
                cursor += spacing;
                using var words = Text(code, 10, 400, Math.Max(1, w - 14), 3, Mono);
                var boxHeight = Math.Ceiling(words.LayoutBounds.Height) + 10;
                if (session is not null)
                {
                    session.FillRoundedRectangle((float)x, (float)cursor, (float)w, (float)boxHeight, (float)DesignMetrics.Radius.Segment, (float)DesignMetrics.Radius.Segment, Paint(palette.Ink, 0.06));
                    Put(session, words, code, x + 7, cursor + 5, palette, DesignToken.Ink);
                }
                cursor += boxHeight;
            }
            else Stack(request.Code, 10, 400, 3, DesignToken.Ink, Mono);
        }
        Stack(request.Error, 9, 400, 2, DesignToken.ErrText);
        var row = request.Buttons.Where(button => !button.OwnRow).ToList();
        if (row.Count > 0)
        {
            // The first button at the left, the others from the right edge, 6 apart (M:336-362).
            cursor += spacing;
            var edge = x + w; var free = w - row.Sum(button => ButtonWidth(button, Wide)) - (row.Count - 1) * ButtonGap;
            for (var index = row.Count - 1; index >= 1; index--)
            {
                var button = row[index]; var wide = ButtonWidth(button, button.Prominent && free < 0 ? ButtonWidth(button, Wide) + free : Wide);
                edge -= wide; if (session is not null) Button(session, button, palette, edge, cursor, wide); edge -= ButtonGap;
            }
            if (session is not null) Button(session, row[0], palette, x, cursor, ButtonWidth(row[0], Wide));
            cursor += ButtonHeight;
        }
        var under = row.Count > 0;
        foreach (var button in request.Buttons.Where(button => button.OwnRow))
        {
            // A long answer under the row, at the right edge, as wide as its words within the bubble.
            cursor += under ? ButtonGap : spacing; under = true;
            var wide = ButtonWidth(button, w);
            if (session is not null) Button(session, button, palette, x + w - wide, cursor, wide);
            cursor += ButtonHeight;
        }
        if (current.Count > 1 || current.Pending) { cursor += spacing; if (session is not null) Pager(session, current, palette, x, cursor, w); cursor += PagerHeight; }
        return cursor - y;
    }

    /// The presence mark in its 15pt frame (M/AgentCompanionViews.swift:107-115): a turning arc while it runs, the raised
    /// hand while it waits, the filled check or exclamation once it ended, else a ring; each in its state's ink.
    private void Presence(CanvasDrawingSession session, string status, DesignPalette palette, double x, double y)
    {
        var ink = Paint(palette.Text(StatusGlyph.Tone(status)));
        switch (status)
        {
            case "running" or "starting" or "queued": spinner = new((float)x, (float)y); break;
            case "waiting": Icon(session, Symbols, "\u270B", 13.5, x, y, ink); break;
            case "completed": Icon(session, Icons, "\uEC61", 13, x, y, ink); break;
            case "error": Icon(session, Icons, "\uF136", 13, x, y, ink, "\uF13C", Paint(palette.Card)); break;
            default: session.DrawEllipse((float)x, (float)y, 5.9f, 5.9f, ink, 1.2f); break;
        }
    }
    /// The Mac's mini progress view (M/AgentCompanionViews.swift:111) as Windows draws one: three quarters of a ring, turning once a second.
    private void Spinner(CanvasDrawingSession session, Vector2 centre, int step, Tint ink)
    {
        const float radius = 4.5f; var angle = step * MathF.Tau / 12;
        using var path = new CanvasPathBuilder(device);
        path.BeginFigure(centre + radius * new Vector2(MathF.Cos(angle), MathF.Sin(angle))); path.AddArc(centre, radius, radius, angle, MathF.Tau * 0.75f); path.EndFigure(CanvasFigureLoop.Open);
        using var arc = CanvasGeometry.CreatePath(path); using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        session.DrawGeometry(arc, ink, 1.5f, round);
    }
    /// "‹ ● ○ ○ 1 / 3 ›" under a bubble while more than one agent is busy (M/AgentCompanionViews.swift:254-295); the
    /// raised hand turns to the agent whose request is behind this page.
    private void Pager(CanvasDrawingSession session, CompanionOverlayCard current, DesignPalette palette, double x, double y, double w)
    {
        var paged = current.Count > 1;
        if (paged) foreach (var (id, at, direction) in new[] { ("previous", x, -1), ("next", x + w - PagerButton, 1) })
        {
            session.FillRoundedRectangle((float)at, (float)y, (float)PagerButton, (float)PagerHeight, (float)PagerHeight / 2, (float)PagerHeight / 2, Paint(DesignTokens.Subtle(palette), DesignMetrics.Opacity.Subtle));
            // chevron.left / chevron.right at 9pt semibold.
            using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
            float tipX = (float)(at + PagerButton / 2 + direction * 2.2), backX = (float)(at + PagerButton / 2 - direction * 2.2), middle = (float)(y + PagerHeight / 2);
            session.DrawLine(backX, middle - 4, tipX, middle, Paint(palette.Ink), 1.5f, round); session.DrawLine(tipX, middle, backX, middle + 4, Paint(palette.Ink), 1.5f, round);
            hitButtons.Add((new(at - 3, y - 3, PagerButton + 6, PagerHeight + 6), id));
        }
        var text = current.Position + " / " + current.Count;
        using var label = paged ? Text(text, 9, 500, Wide, tabular: true) : null;
        double dots = paged && current.Count <= 8 ? current.Count * 5 + (current.Count - 1) * 4 : 0, words = label?.LayoutBounds.Width ?? 0;
        var group = dots + (dots > 0 && label is not null ? 6 : 0) + words + (current.Pending ? (paged ? 6 : 0) + 16 : 0);
        var cursor = x + (w - group) / 2;
        if (dots > 0)
        {
            for (var index = 0; index < current.Count; index++)
                session.FillCircle((float)(cursor + 2.5 + index * 9), (float)(y + PagerHeight / 2), 2.5f, index + 1 == current.Position ? Paint(palette.Accent) : Paint(palette.Ink, 0.22));
            cursor += dots + 6;
        }
        if (label is not null) { Put(session, label, text, cursor, Math.Round(y + (PagerHeight - label.LayoutBounds.Height) / 2), palette, DesignToken.Ink2); cursor += words + 6; }
        if (current.Pending)
        {
            Icon(session, Symbols, "\u270B", 9.5, cursor + 8, y + PagerHeight / 2, Paint(palette.WaitText));
            hitButtons.Add((new(cursor - 2, y - 3, 20, PagerHeight + 6), "pending"));
        }
    }
    private double ButtonWidth(CompanionOverlayButton button, double limit)
    {
        if (button.Glyph) return 26;
        using var label = Text(button.Label, 11, 400, Wide);
        return Math.Max(26, Math.Min(limit, Math.Ceiling(label.LayoutBounds.Width) + 16));
    }
    /// A small system button: bordered on the raised card with a line, or prominent in the accent (M/AgentCompanionViews.swift:336-362).
    private void Button(CanvasDrawingSession session, CompanionOverlayButton button, DesignPalette palette, double x, double y, double wide)
    {
        float radius = (float)ButtonRadius;
        if (button.Prominent) session.FillRoundedRectangle((float)x, (float)y, (float)wide, (float)ButtonHeight, radius, radius, Paint(palette.Accent));
        else
        {
            session.FillRoundedRectangle((float)x, (float)y, (float)wide, (float)ButtonHeight, radius, radius, Paint(palette.CardRaised));
            session.DrawRoundedRectangle((float)x + .5f, (float)y + .5f, (float)wide - 1, (float)ButtonHeight - 1, radius - .5f, radius - .5f, Paint(palette.Line), (float)DesignMetrics.Stroke.Line);
        }
        var ink = button.Prominent ? DesignToken.OnAccent : DesignToken.Ink;
        if (button.Glyph) Icon(session, Icons, "\uE8A7", 11, x + wide / 2, y + ButtonHeight / 2, Paint(palette[ink]));
        else
        {
            using var label = Text(button.Label, 11, 400, Math.Max(1, wide - 16));
            Put(session, label, button.Label, x + (wide - label.LayoutBounds.Width) / 2, Math.Round(y + (ButtonHeight - label.LayoutBounds.Height) / 2), palette, ink);
        }
        hitButtons.Add((new(x, y, wide, ButtonHeight), button.Id));
    }
    /// questionmark.bubble.fill: no Windows icon font has it, so the bubble and its tail are drawn, and the mark is cut in the card's colour.
    private void QuestionBubble(CanvasDrawingSession session, double x, double y, DesignPalette palette)
    {
        var ink = Paint(palette.WaitText); float leftEdge = (float)x - 7, topEdge = (float)y - 6.5f;
        session.FillRoundedRectangle(leftEdge, topEdge, 14, 10.5f, 3.6f, 3.6f, ink);
        using var path = new CanvasPathBuilder(device);
        path.BeginFigure(leftEdge + 2.6f, topEdge + 9.5f); path.AddLine(leftEdge + 2.2f, topEdge + 13.2f); path.AddLine(leftEdge + 6.6f, topEdge + 9.5f); path.EndFigure(CanvasFigureLoop.Closed);
        using var tail = CanvasGeometry.CreatePath(path); session.FillGeometry(tail, ink);
        using var mark = Text("?", 9, 800, Wide);
        session.DrawTextLayout(mark, (float)(x - mark.DrawBounds.X - mark.DrawBounds.Width / 2), (float)(topEdge + 5.25 - mark.DrawBounds.Y - mark.DrawBounds.Height / 2), Paint(palette.Card));
    }

    /// The pet in its frame, 192 × 208 scaled to fit (M/AgentCompanionViews.swift:136-141, `.interpolation(.high)`); with no
    /// pet image, the paw in the accent with 35 around it.
    private void Pet(CanvasDrawingSession session, int row, int column, DesignPalette palette)
    {
        var frame = PetArea;
        if (sprite is null)
        {
            // pawprint.fill on its 16-unit grid, the same shape as the main window's pet button.
            var side = Math.Min(frame.Width, frame.Height) - 70; var unit = (float)(side / 16);
            var origin = new Vector2((float)(frame.X + (frame.Width - side) / 2), (float)(frame.Y + (frame.Height - side) / 2)); var ink = Paint(palette.Accent);
            foreach (var (toeX, toeY) in new[] { (2.3f, 5.5f), (6f, 2.8f), (10f, 2.8f), (13.7f, 5.5f) }) session.FillEllipse(origin + unit * new Vector2(toeX, toeY), 1.7f * unit, 2f * unit, ink);
            Vector2 At(float px, float py) => origin + unit * new Vector2(px, py);
            using var path = new CanvasPathBuilder(device);
            path.BeginFigure(At(8, 6.5f)); path.AddCubicBezier(At(5.7f, 6.5f), At(2, 10.8f), At(2, 12.8f)); path.AddCubicBezier(At(2, 16.7f), At(6, 14.5f), At(8, 14.5f));
            path.AddCubicBezier(At(10, 14.5f), At(14, 16.7f), At(14, 12.8f)); path.AddCubicBezier(At(14, 10.8f), At(10.3f, 6.5f), At(8, 6.5f)); path.EndFigure(CanvasFigureLoop.Closed);
            using var pad = CanvasGeometry.CreatePath(path); session.FillGeometry(pad, ink);
            return;
        }
        column = Math.Clamp(column, 0, CompanionPet.FrameCounts[row] - 1);
        var fit = Math.Min(frame.Width / 192, frame.Height / 208);
        var target = new Area(frame.X + (frame.Width - 192 * fit) / 2, frame.Y + (frame.Height - 208 * fit) / 2, 192 * fit, 208 * fit); var cell = new Area(column * 192, row * 208, 192, 208);
        // Cropped first, so the smooth scaling never reads the next frame of the sheet.
        using var cropped = new CropEffect { Source = sprite, SourceRectangle = cell };
        session.DrawImage(cropped, target, cell, 1, CanvasImageInterpolation.HighQualityCubic);
    }

    /// The pet's menu (M/AgentCompanionViews.swift:144): 펫 숨기기, 에이전트 열기. A system menu would take the keyboard
    /// from the user's application, so it is drawn here, as the app's cards are: the card, a line, radius 8.
    private void LayoutMenu(Vector2 at)
    {
        menuItems.Clear();
        var entries = new[] { ("hide", Locale.Get("menu.hidePet")), ("open", Locale.Get("menu.openAgent")) };
        double widest = 0;
        foreach (var (_, label) in entries) { using var words = Text(label, MenuType, 400, Wide); widest = Math.Max(widest, words.LayoutBounds.Width); }
        double w = Math.Ceiling(Math.Max(120, widest + 2 * (MenuInset + 10))), h = 2 * MenuInset + entries.Length * MenuRow;
        var x = Math.Clamp(at.X + w > panelWidth - 2 ? at.X - w : at.X, 2, Math.Max(2, panelWidth - w - 2));
        var y = Math.Clamp(at.Y + h > height - 2 ? at.Y - h : at.Y, 2, Math.Max(2, height - h - 2));
        menuArea = new(Math.Round(x), Math.Round(y), w, h); menuHover = -1;
        for (var index = 0; index < entries.Length; index++) menuItems.Add((new(menuArea.X + MenuInset, menuArea.Y + MenuInset + index * MenuRow, w - 2 * MenuInset, MenuRow), entries[index].Item1, entries[index].Item2));
    }
    private void Menu(CanvasDrawingSession session, DesignPalette palette)
    {
        session.FillRoundedRectangle(menuArea, (float)DesignMetrics.Radius.Row, (float)DesignMetrics.Radius.Row, Paint(palette.Card));
        session.DrawRoundedRectangle((float)menuArea.X + .5f, (float)menuArea.Y + .5f, (float)menuArea.Width - 1, (float)menuArea.Height - 1, (float)DesignMetrics.Radius.Row - .5f, (float)DesignMetrics.Radius.Row - .5f, Paint(palette.Line), (float)DesignMetrics.Stroke.Line);
        for (var index = 0; index < menuItems.Count; index++)
        {
            var (bounds, _, label) = menuItems[index];
            if (index == menuHover) session.FillRoundedRectangle(bounds, (float)MenuRowRadius, (float)MenuRowRadius, Paint(DesignTokens.Subtle(palette), DesignMetrics.Opacity.Subtle));
            using var words = Text(label, MenuType, 400, Math.Max(1, bounds.Width - 20));
            session.DrawTextLayout(words, (float)(bounds.X + 10), (float)Math.Round(bounds.Y + (bounds.Height - words.LayoutBounds.Height) / 2), Paint(palette.Ink));
        }
    }
    /// A press anywhere outside the menu, or Escape, puts it away; the window never holds the keyboard or the pointer to learn of it.
    private bool MenuDismissed()
    {
        if (SmokeKeepsMenu) return false;
        // Down now, or pressed and let go since the last look: a short tap between two frames counts too.
        static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8001) != 0;
        if (Pressed(0x1b)) return true;
        if (!(Pressed(1) | Pressed(2) | Pressed(4))) return false;
        GetCursorPos(out var pointer);
        double x = (pointer.X - left) / scale, y = (pointer.Y - top) / scale;
        return !Inside(new(menuArea.X - 3, menuArea.Y - 3, menuArea.Width + 6, menuArea.Height + 6), x, y);
    }

    /// <summary>The characters DirectWrite starts a new line at.</summary>
    private static readonly System.Buffers.SearchValues<char> LineBreaks = System.Buffers.SearchValues.Create(['\r', '\n', '\v', '\f', (char)0x85, (char)0x2028, (char)0x2029]);
    private static Tint Paint(DesignColor color, double opacity = 1) => DesignBrushes.ToColor(color, opacity);
    private static bool Inside(Area area, double x, double y) => x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom;
    /// <summary>
    /// A text as SwiftUI lays one out: <paramref name="lines"/> is its lineLimit (0 for none), and what does not fit
    /// ends in an ellipsis on its last line, never cut through.
    /// </summary>
    private CanvasTextLayout Text(string value, double size, int weight, double wide, int lines = 1, string? family = null, bool tabular = false)
    {
        // One line is one line: a break in the words ends it there, with the ellipsis that says more follows, as
        // lineLimit(1) does. Not wrapping would still start a new line at each break and grow the bubble with them.
        if (lines == 1 && value.AsSpan().IndexOfAny(LineBreaks) is >= 0 and var broken) value = value[..broken].TrimEnd() + "\u2026";
        using var format = new CanvasTextFormat
        {
            // Medium has no face of its own here: asked for 500, DirectWrite hands out the semibold instance of Segoe UI
            // Variable and the bold of the Hangul fallback. Regular is the nearer of the two weights there are.
            FontFamily = family ?? Body, FontSize = (float)size, FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(weight == 500 ? 400 : weight) },
            WordWrapping = lines == 1 ? CanvasWordWrapping.NoWrap : CanvasWordWrapping.EmergencyBreak,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
            // Words keep an emoji's colours, as a TextBlock does; the icon fonts stay one ink.
            Options = family is null || family == Mono ? CanvasDrawTextOptions.EnableColorFont : CanvasDrawTextOptions.Default,
        };
        // SF Pro sets a line 1.19 of its size, Segoe UI and the Hangul fallback 1.33: nine tenths of theirs keeps the Mac's
        // rows and the bubble's height (25 for the header, 13 a line of 11pt words). The mono face is already as tight.
        if (family is null) { format.LineSpacingMode = CanvasLineSpacingMode.Proportional; format.LineSpacing = LineScale; format.LineSpacingBaseline = LineScale; }
        CanvasTextLayout Lay(string words)
        {
            var laid = new CanvasTextLayout(device, words, format, (float)wide, Wide);
            if (tabular && words.Length > 0) { using var typography = new CanvasTypography(); typography.AddFeature(CanvasTypographyFeatureName.TabularFigures, 1); laid.SetTypography(0, words.Length, typography); }
            return laid;
        }
        var layout = Lay(value);
        if (lines <= 1 || layout.LineCount <= lines) { if (SmokeTexts is not null) noted[layout] = (weight, false); return layout; }
        // DirectWrite ends a line that is too wide in an ellipsis, but not a paragraph that is too tall: keep the words of
        // the lines that fit, less as many characters as the ellipsis needs to stay on the last of them.
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        var cut = layout.LineMetrics.Take(lines).Sum(line => line.CharacterCount);
        for (var kept = Array.FindLastIndex(starts, start => start < cut) + 1; ; kept--)
        {
            layout.Dispose();
            layout = Lay((kept < starts.Length ? value[..starts[Math.Max(0, kept)]] : value).TrimEnd() + "\u2026");
            if (kept <= 0 || layout.LineCount <= lines) break;
        }
        if (SmokeTexts is not null) noted[layout] = (weight, true);
        return layout;
    }
    /// One line that keeps its end, as `.truncationMode(.head)` does (M/AgentCompanionViews.swift:313).
    private string KeepEnd(string value, double size, int weight, double wide)
    {
        bool Fits(string candidate) { using var words = Text(candidate, size, weight, Wide); return words.LayoutBounds.Width <= wide; }
        if (Fits(value)) return value;
        int low = 1, high = value.Length;
        while (low < high) { var middle = (low + high) / 2; if (Fits("\u2026" + value[middle..])) high = middle; else low = middle + 1; }
        if (low < value.Length && char.IsLowSurrogate(value[low])) low++;
        return "\u2026" + value[low..];
    }
    private void Put(CanvasDrawingSession session, CanvasTextLayout layout, string value, double x, double y, DesignPalette palette, DesignToken ink)
    {
        session.DrawTextLayout(layout, (float)x, (float)y, Paint(palette[ink]));
        if (SmokeTexts is null) return;
        var (weight, shortened) = noted.GetValueOrDefault(layout, (layout.DefaultFontWeight.Weight, false));
        SmokeTexts.Add(new(value, layout.DefaultFontSize, weight, layout.DefaultFontFamily, layout.LineCount, shortened || layout.LineMetrics is { Length: > 0 } metrics && metrics[^1].IsTrimmed, ink, new(x, y, layout.LayoutBounds.Width, layout.LayoutBounds.Height)));
    }
    /// A glyph of an icon font, sized so that its ink is <paramref name="tall"/> high and centred on a point: an SF Symbol
    /// draws about as tall as its font size, an icon font's glyph does not. <paramref name="over"/> is a second glyph of the
    /// same cell drawn on it, in its own ink.
    private void Icon(CanvasDrawingSession session, string family, string glyph, double tall, double x, double y, Tint ink, string? over = null, Tint overInk = default)
    {
        const float probeSize = 32;
        double size;
        using (var probe = Text(glyph, probeSize, 400, Wide, family: family)) { if (probe.DrawBounds.Height <= 0) return; size = probeSize * tall / probe.DrawBounds.Height; }
        using var mark = Text(glyph, size, 400, Wide, family: family);
        float originX = (float)(x - mark.DrawBounds.X - mark.DrawBounds.Width / 2), originY = (float)(y - mark.DrawBounds.Y - mark.DrawBounds.Height / 2);
        session.DrawTextLayout(mark, originX, originY, ink);
        if (over is null) return;
        using var second = Text(over, size, 400, Wide, family: family);
        session.DrawTextLayout(second, originX, originY, overInk);
    }

    private CompanionResizeEdges ResizeAt(Point point)
    {
        if (!showBubble || menuOpen || card is null) return CompanionResizeEdges.None;
        var bubble = BubbleArea;
        return CompanionBubbleLayout.Hit(point.X / scale - bubble.X, point.Y / scale - bubble.Y, bubble.Width, bubble.Height, card.Approval is null);
    }
    private void ResizeFromPointer(int dx, int dy)
    {
        // An approval is drawn no narrower than the default (M/AgentCompanionViews.swift:365): its edge is dragged from
        // where it is drawn, and left where it was it keeps the narrower width that was saved.
        var next = CompanionBubbleLayout.Resize(startDrawnWidth, startHeight, dx, dy, scale, resizeEdges);
        var nextWidth = (int)next.Width; var vertical = CompanionBubbleLayout.Vertical(resizeEdges);
        if (card?.Approval is not null) { nextWidth = Math.Max((int)CompanionBubbleLayout.DefaultWidth, nextWidth); if (nextWidth == startDrawnWidth) nextWidth = startWidth; }
        // A sideways drag leaves a content-following height as it was.
        if (nextWidth == width && (!vertical || (int)next.Height == bubbleHeight)) return;
        width = nextWidth; if (vertical) fixedBubbleHeight = (int)next.Height;
        dragged = true;
        Fit(resizeEdges.HasFlag(CompanionResizeEdges.Left) ? 1 : -1, startFrame);
    }
    private void ResetSize()
    {
        width = (int)CompanionBubbleLayout.DefaultWidth; fixedBubbleHeight = null;
        Fit(0); Resized?.Invoke(null, null); Moved?.Invoke(left, SavedTop);
    }
    private void CancelResize()
    {
        if (resizeEdges == CompanionResizeEdges.None) return;
        width = startWidth; fixedBubbleHeight = startFixedHeight;
        mouseDown = false; dragged = false; resizeEdges = CompanionResizeEdges.None; pressedAction = null;
        Fit(-1, startFrame); ReleaseCapture();
    }
    private static Point ClientPoint(nint lParam) => new() { X = (short)(lParam.ToInt64() & 65535), Y = (short)((lParam.ToInt64() >> 16) & 65535) };
    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        // Do not allow any managed exception to cross the native callback.
        try
        {
            switch (message)
            {
                case 0x0021: return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE
                case 0x0020:
                    if ((lParam.ToInt64() & 65535) != 1) break;
                    GetCursorPos(out var cursorPoint); cursorPoint.X -= left; cursorPoint.Y -= top;
                    var cursor = ResizeAt(cursorPoint) switch
                    {
                        CompanionResizeEdges.Left or CompanionResizeEdges.Right => 32644,
                        CompanionResizeEdges.Top => 32645,
                        CompanionResizeEdges.Left | CompanionResizeEdges.Top => 32642,
                        CompanionResizeEdges.Right | CompanionResizeEdges.Top => 32643,
                        _ => 32512
                    };
                    SetCursor(LoadCursor(0, cursor)); return 1;
                case 0x0201:
                    GetCursorPos(out dragStart); lastPointer = dragStart; startOrigin = new() { X = left, Y = top }; startFrame = (left, top, pixelWidth, pixelHeight); mouseDown = true; dragged = false; swipeDirection = 0;
                    startWidth = width; startDrawnWidth = (int)DrawnWidth; startHeight = bubbleHeight; startFixedHeight = fixedBubbleHeight;
                    var p = ClientPoint(lParam);
                    resizeEdges = ResizeAt(p); pressedAction = Hit(p); pressedKey = card?.Key ?? ""; SetCapture(hwnd); return 0;
                case 0x0200 when mouseDown:
                    GetCursorPos(out var move); var dx = move.X - dragStart.X; var dy = move.Y - dragStart.Y;
                    // Four points of travel make a press a carry, not a click (M/CompanionPetMotion.swift:162).
                    if (pressedAction == "toggle" && (dragged || Math.Sqrt((double)dx * dx + (double)dy * dy) >= 4 * scale))
                    {
                        dragged = true; left = startOrigin.X + dx; top = startOrigin.Y + dy; SetWindowPos(hwnd, -1, left, top, 0, 0, 0x0001 | 0x0010);
                        // Row 1 walks to the right, row 2 to the left (M/CompanionPetMotion.swift:6-8, 48).
                        if (move.X != lastPointer.X) { walkingRow = move.X > lastPointer.X ? 1 : 2; walkedAt = Environment.TickCount64; }
                    }
                    else if (pressedAction == "open" && !menuOpen && card?.Count > 1 && Math.Abs(dx) / scale > 36 && Math.Abs(dx) > Math.Abs(dy))
                    { dragged = true; swipeDirection = dx < 0 ? 1 : -1; }
                    else if (pressedAction == "resize") ResizeFromPointer(dx, dy);
                    lastPointer = move; return 0;
                case 0x0202 when mouseDown:
                    var completedAction = pressedAction; var completedKey = pressedKey; mouseDown = false; resizeEdges = CompanionResizeEdges.None; ReleaseCapture();
                    if (dragged && swipeDirection != 0) { if (completedKey == card?.Key) Action?.Invoke(completedKey!, swipeDirection > 0 ? "next" : "previous"); }
                    else if (dragged)
                    {
                        if (completedAction == "resize" && width == startWidth && bubbleHeight == startHeight) { fixedBubbleHeight = startFixedHeight; Fit(); }
                        else { ClampPosition(); Moved?.Invoke(left, SavedTop); if (completedAction == "resize") Resized?.Invoke(width, fixedBubbleHeight); }
                    }
                    else
                    {
                        var release = ClientPoint(lParam);
                        if (completedAction is { Length: > 0 } action && action == Hit(release) && completedKey == (card?.Key ?? ""))
                        {
                            if (menuOpen) { menuOpen = false; composeDirty = true; }
                            if (action is not ("menu-close" or "resize")) Action?.Invoke(completedKey!, action);
                        }
                    }
                    pressedAction = null; lastRow = -1; if (fitPending) Fit(); return 0;
                case 0x0203: // Double click any resize strip to restore automatic sizing.
                    var twice = Hit(ClientPoint(lParam));
                    if (twice == "resize") { ResetSize(); return 0; }
                    // On the pager the second of two quick presses is a press like the first: a chevron pressed twice turns
                    // two pages. Anywhere else it is let go, as before: the first press may have put another request, or
                    // another card, under the pointer, and the second was not meant for that.
                    if (twice is "previous" or "next") goto case 0x0201;
                    return 0;
                case 0x0205: // The pet's menu opens where the pointer is; a right click elsewhere on the pet moves it there.
                    var at = ClientPoint(lParam);
                    if (menuOpen && Inside(menuArea, at.X / scale, at.Y / scale)) return 0;
                    menuOpen = false;
                    if (Hit(at).Length > 0)
                    {
                        LayoutMenu(new((float)(at.X / scale), (float)(at.Y / scale))); menuOpen = true;
                        // The press that opened it is not one that puts it away.
                        foreach (var key in new[] { 1, 2, 4, 0x1b }) GetAsyncKeyState(key);
                    }
                    composeDirty = true; return 0;
                case 0x001f: // cancel mode, such as an interrupted drag
                case 0x0215:
                    if (mouseDown && resizeEdges != CompanionResizeEdges.None) CancelResize();
                    if (mouseDown)
                    {
                        // A carry cut short leaves the pet where it was let go: on the screen, and saved there.
                        var carried = dragged && pressedAction == "toggle";
                        mouseDown = false; if (message == 0x001f) ReleaseCapture();
                        if (carried) { ClampPosition(); Moved?.Invoke(left, SavedTop); }
                        if (fitPending) Fit();
                    }
                    pressedAction = null; return 0;
                case 0x020e:
                    // Only over a bubble: a stray scroll on the pet must not turn a page (M/AgentCompanionViews.swift:446-447).
                    var wheelX = ((short)lParam.ToInt64() - left) / scale; var wheelY = ((short)(lParam.ToInt64() >> 16) - top) / scale;
                    if (!showBubble || menuOpen || card is not { Count: > 1 } || !Inside(BubbleArea, wheelX, wheelY)) return 0;
                    wheelDelta += unchecked((short)(wParam >> 16));
                    if (Math.Abs(wheelDelta) >= 120) { Action?.Invoke(card.Key, wheelDelta > 0 ? "next" : "previous"); wheelDelta = 0; } return 0;
                case 0x007e: ClampPosition(); return 0; // monitor configuration changed
                case 0x02e0:
                    // Sizing the window for its new display can tip it back over the edge between two displays. The scale
                    // of where it then stands is taken at the next change, never inside this one, which would not end.
                    if (scaling) { scalingMissed = true; return 0; }
                    scaling = true;
                    try
                    {
                        if (mouseDown && resizeEdges != CompanionResizeEdges.None) CancelResize();
                        scale = Math.Clamp((wParam & 65535) / 96.0, 1, 4); Fit();
                        // A change that arrived meanwhile was let go; the display the window stands on now is asked once.
                        if (scalingMissed)
                        {
                            scalingMissed = false;
                            var standing = Math.Clamp(GetDpiForWindow(hwnd) / 96.0, 1, 4);
                            if (standing != scale) { scale = standing; Fit(); }
                        }
                    }
                    finally { scaling = false; scalingMissed = false; }
                    return 0;
            }
        }
        // A press that failed part-way gives the pointer back; only this window's own press, never another's capture.
        catch { if (mouseDown) { mouseDown = false; ReleaseCapture(); } }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private string Hit(Point p)
    {
        // The targets are those of the frame about to be shown, never of the one before a change.
        if (cardDirty && cardLayer is not null) RenderCard();
        double x = p.X / scale, y = p.Y / scale;
        if (menuOpen) { foreach (var (bounds, id, _) in menuItems) if (Inside(bounds, x, y)) return id; return "menu-close"; }
        if (showBubble && card is not null && Inside(BubbleArea, x, y))
        {
            if (ResizeAt(p) != CompanionResizeEdges.None) return "resize";
            foreach (var (bounds, id) in hitButtons) if (Inside(bounds, x, y)) return id;
            return "open";
        }
        return Inside(PetArea, x, y) ? "toggle" : "";
    }
    private static Rect WorkArea(Point point)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() }; var monitor = MonitorFromPoint(point, 2);
        return GetMonitorInfo(monitor, ref info) ? info.Work : new Rect(0, 0, 1920, 1080);
    }
    /// The pet and a bubble that follows its content stay on the screen; the window's own clear padding (12 at the sides,
    /// 8 below) may hang over its edge. The pet's feet are what is held, never the window's top: an approval's taller
    /// window grows past the screen's top rather than push the pet down, so it stands where it stood once the request is answered.
    private void ClampPosition(bool move = true)
    {
        var work = WorkArea(new Point { X = left + pixelWidth / 2, Y = top + pixelHeight - 64 });
        int side = (int)(CompanionBubbleLayout.SidePadding * scale), below = (int)(CompanionBubbleLayout.Padding * scale);
        var room = (int)Math.Ceiling(CompanionBubbleLayout.PanelSize(width, fixedBubbleHeight, false).Height * scale);
        left = Math.Clamp(left, work.Left - side, Math.Max(work.Left - side, work.Right - pixelWidth + side));
        top = Math.Clamp(top + pixelHeight, Math.Min(work.Top + room, work.Bottom + below), work.Bottom + below) - pixelHeight;
        if (move && window != 0) SetWindowPos(window, -1, left, top, pixelWidth, pixelHeight, 0x0010);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (window != 0) { DestroyWindow(window); window = 0; }
        if (bitmap != 0) { SelectObject(dc, oldBitmap); DeleteObject(bitmap); bitmap = 0; }
        if (dc != 0) { DeleteDC(dc); dc = 0; }
        UnregisterClass(className, instance); pixels = [];
        surface?.Dispose(); surface = null; cardLayer?.Dispose(); cardLayer = null; sprite?.Dispose(); sprite = null;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect(int left, int top, int right, int bottom) { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPixels, YPixels; public uint ColorsUsed, ColorsImportant, Color; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, SourceAlpha, Format; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int id);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint screenDc, ref Point destination, ref Point size, nint sourceDc, ref Point source, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
}
