namespace MightyClaude.Core;

/// <summary>
/// The geometry and type of the macOS app, as named constants for the WinUI port
/// (docs/design-system/index.html §3–4), held to the <c>metrics</c> section of
/// native/contracts/fixtures/design-tokens.json by the Core tests. <see cref="Spacing"/>,
/// <see cref="Inset"/> and <see cref="Layout"/> are also the Mac's own MightyCore
/// <c>DesignMetrics</c>, which DesignTokenParityTests holds to the same file. 1pt is drawn as 1 epx.
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
        /// <summary>
        /// Avenir Next Bold → Segoe UI Variable Display, drawn with <c>FontWeights.Bold</c> (decision Q1, 2026-10-04):
        /// the dashboard title, its number tiles and workspace names (M/DashboardView.swift:18-25), and the
        /// timeline's request headers (M/MightyGraphView.swift:731).
        /// </summary>
        public const string Heading = "Segoe UI Variable Display, Segoe UI";
    }

    /// <summary>The spacing scale (M/MightyCore/DesignMetrics.swift Spacing): a 4pt grid with 2 and 6 between its first steps.</summary>
    public static class Spacing
    {
        public const double Xxs = 2;
        public const double Xs = 4;
        public const double Sm = 6;
        public const double Md = 8;
        public const double Lg = 12;
        public const double Xl = 16;
    }

    /// <summary>
    /// Named insets, the padding of one surface (M/MightyCore/DesignMetrics.swift Inset): <c>H</c>/<c>V</c> are
    /// horizontal and vertical, <c>T</c>/<c>B</c> top and bottom, <c>Leading</c>/<c>Trailing</c> the two sides; a bare
    /// name is every side. Windows reads them where it already used a central constant; the rest moves in stage C.
    /// </summary>
    public static class Inset
    {
        /// <summary>The pane dock inside its scroll view, on every side (M/PaneDockView.swift).</summary>
        public const double Dock = 4;
        /// <summary>The workspace header over the panes.</summary>
        public const double WorkspaceHeaderH = 12;
        public const double WorkspaceHeaderT = 6;
        public const double WorkspaceHeaderB = 4;
        /// <summary>An agent pane's one-line header.</summary>
        public const double PaneHeaderLeading = 8;
        public const double PaneHeaderTrailing = 6;
        /// <summary>A tab in a pane group's strip.</summary>
        public const double TabLeading = 8;
        public const double TabTrailing = 4;
        /// <summary>The composer card in its pane, its rows inside the card, and the gap between them.</summary>
        public const double ComposerOuter = 6;
        public const double ComposerInnerH = 8;
        public const double ComposerInnerB = 6;
        public const double ComposerStack = 4;
        /// <summary>The transcript's text inside its scroll view.</summary>
        public const double Transcript = 8;
        /// <summary>A reply's card, the user's bubble and a tool call's chip in the transcript.</summary>
        public const double ReplyCardH = 8;
        public const double ReplyCardT = 6;
        public const double ReplyCardB = 4;
        public const double UserBubbleH = 8;
        public const double UserBubbleT = 6;
        public const double UserBubbleB = 4;
        public const double ToolChipH = 6;
        public const double ToolChipV = 4;
        /// <summary>A code block, and a block quote inside its rule.</summary>
        public const double CodeBlock = 6;
        public const double Quote = 6;
        /// <summary>The Mighty view's bar, and a diagram block's body and the timeline's rows.</summary>
        public const double GraphBarH = 8;
        public const double GraphBarV = 4;
        public const double GraphBlockBodyH = 8;
        public const double GraphBlockBodyV = 4;
        /// <summary>The sidebar's search, section headers, workspace rows and pane rows.</summary>
        public const double SidebarSearch = 6;
        public const double SidebarSectionH = 12;
        public const double SidebarSectionT = 10;
        public const double SidebarSectionB = 4;
        public const double SidebarRowV = 4;
        public const double PaneRowV = 3;
        /// <summary>The dashboard's page, its number tiles and its pane rows.</summary>
        public const double DashboardPageH = 12;
        public const double DashboardPageT = 10;
        public const double DashboardPageB = 12;
        public const double DashboardTileH = 10;
        public const double DashboardTileV = 8;
        public const double DashboardRowH = 8;
        public const double DashboardRowV = 6;
        /// <summary>The window's status bar.</summary>
        public const double StatusBarH = 10;
        public const double StatusBarV = 3;
        /// <summary>A sheet's and a popover's content.</summary>
        public const double Sheet = 12;
        public const double Popover = 10;
        /// <summary>Between the rows of a list.</summary>
        public const double ListGap = 2;
    }

    /// <summary>Layout sizes (doc §4 "layout sizes").</summary>
    public static class Layout
    {
        /// <summary>Sidebar width bounds and default (M/Models.swift SidebarFold).</summary>
        public const double SidebarMin = 210;
        public const double SidebarDefault = 252;
        public const double SidebarMax = 360;
        /// <summary>A sidebar grip drag that would leave the sidebar narrower than this folds it away (M/Models.swift SidebarFold.foldThreshold).</summary>
        public const double SidebarFoldThreshold = 150;
        /// <summary>Header heights: pane header, tab strip, diagram block head, file preview head.</summary>
        public const double PaneHeader = 26;
        public const double TabStrip = 28;
        /// <summary>A tab in the strip, its close button with it (M/PaneDockView.swift PaneDockTab).</summary>
        public const double Tab = 24;
        public const double BlockHead = 28;
        public const double PreviewHead = 30;
        /// <summary>The composer toolbar, its chips and its round buttons (M/ComposerControls.swift ComposerToolbarMetrics).</summary>
        public const double Toolbar = 26;
        /// <summary>The smallest pane (a tab group) the dock lays out before it scrolls (M/PaneDockView.swift:41-44).</summary>
        public const double PaneMinWidth = 315;
        public const double PaneMinHeight = 290;
        /// <summary>A split's divider: the hit area between two panes, around a visible 3×30 handle (M/PaneDockView.swift:42, 125-158).</summary>
        public const double SplitDivider = 6;
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
