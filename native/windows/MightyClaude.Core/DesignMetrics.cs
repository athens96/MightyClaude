namespace MightyClaude.Core;

/// <summary>
/// The geometry and type of the macOS app, as named constants for the WinUI port
/// (artifacts/design-system/index.html §3–4). These live in Mac SwiftUI views rather than
/// MightyCore, so only the Windows tests hold them to the <c>metrics</c> section of
/// native/contracts/fixtures/design-tokens.json. 1pt is drawn as 1 epx.
/// </summary>
public static class DesignMetrics
{
    /// <summary>Corner radii (macOS mostly <c>.continuous</c>).</summary>
    public static class Radius
    {
        /// <summary>File-tree selection, resize grips, the Enter chip (M/FilePaneView.swift:120).</summary>
        public const double FileRow = 5;
        /// <summary>Segment chips, tabs, hint chips (M/PaneDockView.swift).</summary>
        public const double Segment = 6;
        /// <summary>Sidebar search, workspace rows, the work-status (dashboard) entry icon (M/WorkspaceView.swift:71-92, 191).</summary>
        public const double Search = 7;
        /// <summary>Pane rows, segment tracks, the stop button (M/WorkspaceView.swift:214-277).</summary>
        public const double Row = 8;
        /// <summary>Buttons on the question and permission cards.</summary>
        public const double CardButton = 9;
        /// <summary>The work-status (dashboard) entry and the slash palette.</summary>
        public const double Entry = 10;
        /// <summary>Pane cards, the slim header, the top of the tab strip (M/SessionPaneView.swift:164).</summary>
        public const double Pane = 11;
        /// <summary>Diagram blocks and the draft (M/MightyGraphView.swift:487-554).</summary>
        public const double Block = 12;
        /// <summary>Timeline row cards (M/MightyGraphTimelineView.swift).</summary>
        public const double TimelineRow = 14;
        /// <summary>The composer, waiting cards, the timeline result (M/SessionPaneView.swift:655).</summary>
        public const double Composer = 16;
        /// <summary>Dashboard tiles and groups (M/DashboardView.swift).</summary>
        public const double Tile = 18;
    }

    /// <summary>Line widths.</summary>
    public static class Stroke
    {
        /// <summary>The selected sidebar row (black × 0.07) and image tiles.</summary>
        public const double Hairline = 0.5;
        /// <summary>Every plain border (<c>line</c>).</summary>
        public const double Line = 1;
        /// <summary>The focused composer, the hero status pill, the draft dash, edges.</summary>
        public const double Focus = 1.5;
        /// <summary>Running, waiting and selected outlines; the edge into a running block.</summary>
        public const double Active = 2;
        /// <summary>Timeline node borders and rails.</summary>
        public const double Rail = 3;
    }

    /// <summary>The type scale in pt (M/… per the design doc §3).</summary>
    public static class Type
    {
        /// <summary>Dashboard number tiles (M/DashboardView.swift:139).</summary>
        public const double Tile = 34;
        /// <summary>The dashboard title (M/DashboardView.swift:102).</summary>
        public const double DashTitle = 29;
        /// <summary>The welcome title (M/WorkspaceView.swift:355).</summary>
        public const double Welcome = 27;
        /// <summary>The workspace header name and sheet titles (M/WorkspaceView.swift:293).</summary>
        public const double Header = 17;
        /// <summary>Timeline request headers (M/MightyGraphView.swift:731).</summary>
        public const double Timeline = 16;
        /// <summary>Dashboard row titles (M/DashboardView.swift:262-338).</summary>
        public const double DashRow = 14;
        /// <summary>The pane header title (M/SessionPaneView.swift:217).</summary>
        public const double Title = 13;
        /// <summary>Composer input and transcript body (M/NativeComposerEditor.swift:32).</summary>
        public const double Body = 13;
        /// <summary>Sidebar pane titles (M/WorkspaceView.swift:214-263).</summary>
        public const double SideRow = 12.5;
        /// <summary>Diagram block and Mighty bar titles (M/MightyGraphView.swift:487).</summary>
        public const double Block = 12;
        /// <summary>Workspace row names (M/WorkspaceView.swift:160-203).</summary>
        public const double Row = 12;
        /// <summary>The pane state word (M/SessionPaneView.swift:220).</summary>
        public const double State = 11.5;
        /// <summary>Composer pill words (M/ComposerControls.swift:4-33).</summary>
        public const double Pill = 11;
        /// <summary>Header metrics and workspace paths (M/PaneChrome.swift:61-70).</summary>
        public const double Mono = 11;
        /// <summary>The status bar and the Mighty summary (M/WorkspaceView.swift:375).</summary>
        public const double Small = 10;
        /// <summary>The beta badge (M/BetaBadge.swift).</summary>
        public const double Badge = 9;
    }

    /// <summary>Font families (doc §3): Windows stand-ins for SF Pro, SF Mono and Avenir Next.</summary>
    public static class Font
    {
        /// <summary>SF Mono → Cascadia Mono, with Consolas where Windows Terminal never installed it.</summary>
        public const string Mono = "Cascadia Mono, Consolas";
        /// <summary>SF Pro → Segoe UI Variable (Windows 11), Segoe UI on Windows 10; Korean falls back to Malgun Gothic.</summary>
        public const string Body = "Segoe UI Variable Text, Segoe UI";
        /// <summary>Avenir Next Bold → open decision Q1; its default (a) is the system display face.</summary>
        public const string Heading = "Segoe UI Variable Display, Segoe UI";
    }

    /// <summary>Layout sizes (doc §4 "layout sizes").</summary>
    public static class Layout
    {
        /// <summary>Sidebar width bounds and default (M/WorkspaceView.swift:14, 129, 532).</summary>
        public const double SidebarMin = 210;
        public const double SidebarDefault = 252;
        public const double SidebarMax = 360;
        /// <summary>The pane dock's outer inset (M/PaneDockView.swift:62-63).</summary>
        public const double DockInset = 16;
        /// <summary>Header heights: pane header, tab strip, diagram block head, file preview head.</summary>
        public const double PaneHeader = 34;
        public const double TabStrip = 38;
        public const double BlockHead = 38;
        public const double PreviewHead = 40;
        /// <summary>The composer toolbar and its chips (M/ComposerControls.swift:39-62).</summary>
        public const double Toolbar = 32;
    }

    /// <summary>
    /// The opacity a token is drawn at where the Mac draws it translucent (doc §2 "opacity uses");
    /// the fixture's <c>opacities</c> names the token and the Mac source of each.
    /// </summary>
    public static class Opacity
    {
        /// <summary>The neutral wash for hover and selected rows: black by day, white by night (M/Palette.swift:30).</summary>
        public const double Subtle = 0.035;
        public const double SidebarWorkspaceSelected = 0.10;
        public const double SidebarRowSelectedBorder = 0.07;
        public const double PaneActiveBorder = 0.58;
        public const double TabActiveRule = 0.55;
        public const double ComposerFocus = 0.8;
        public const double PillActiveBorder = 0.35;
        public const double InputPreview = 0.055;
        public const double Edge = 0.45;
        public const double Draft = 0.6;
        public const double FileSelection = 0.18;
    }

    /// <summary>Dashed outlines, in pt as the Mac draws them.</summary>
    public static class Dash
    {
        /// <summary>A running block's marching outline: [9, 7] at 2pt (M/MightyGraphActivityView.swift:11-43).</summary>
        public static readonly IReadOnlyList<double> Running = [9, 7];
        public const double RunningWidth = 2;
        /// <summary>The draft node's outline: [5, 4] at 1.5pt (M/MightyGraphView.swift:404-420).</summary>
        public static readonly IReadOnlyList<double> Draft = [5, 4];
        public const double DraftWidth = 1.5;

        /// <summary>
        /// A dash pattern in pt as WinUI's <c>StrokeDashArray</c>, whose entries are multiples of
        /// the stroke width: [9, 7] at 2pt is [4.5, 3.5].
        /// </summary>
        public static double[] InStrokeUnits(IReadOnlyList<double> pt, double strokeWidth) =>
            strokeWidth > 0 ? pt.Select(value => value / strokeWidth).ToArray() : throw new ArgumentOutOfRangeException(nameof(strokeWidth), strokeWidth, "a dash needs a positive stroke width");
    }
}
