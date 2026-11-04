import CoreGraphics

/// The design contract's geometry (`metrics.spacing`, `metrics.inset` and `metrics.layout` in
/// native/contracts/fixtures/design-tokens.json), as named constants for the Mac's views. Windows
/// reads the same table as `DesignMetrics` in MightyClaude.Core; `DesignTokenParityTests` holds
/// both this file and the fixture to each other through each namespace's `all`.
/// Spec: docs/design-system/index.html §4.
public enum DesignMetrics {
    /// The spacing scale: a 4pt grid with 2 and 6 between its first steps. Gaps between
    /// views and paddings with no name of their own come from here.
    public enum Spacing {
        public static let xxs: CGFloat = 2
        public static let xs: CGFloat = 4
        public static let sm: CGFloat = 6
        public static let md: CGFloat = 8
        public static let lg: CGFloat = 12
        public static let xl: CGFloat = 16

        /// Every step by its fixture name.
        public static let all: [String: CGFloat] = ["xxs": xxs, "xs": xs, "sm": sm, "md": md, "lg": lg, "xl": xl]
    }

    /// Named insets: the padding of one surface. `H`/`V` are horizontal and vertical,
    /// `T`/`B` top and bottom, `Leading`/`Trailing` the two sides; a bare name is every side.
    public enum Inset {
        /// The pane dock inside its scroll view, on every side.
        public static let dock: CGFloat = 4
        /// The workspace header over the panes (its leading edge also clears the traffic lights while the sidebar is folded).
        public static let workspaceHeaderH: CGFloat = 12
        public static let workspaceHeaderT: CGFloat = 6
        public static let workspaceHeaderB: CGFloat = 4
        /// An agent pane's one-line header.
        public static let paneHeaderLeading: CGFloat = 8
        public static let paneHeaderTrailing: CGFloat = 6
        /// A tab in a pane group's strip, around its mark and title.
        public static let tabLeading: CGFloat = 8
        public static let tabTrailing: CGFloat = 4
        /// The composer card inside its pane.
        public static let composerOuter: CGFloat = 6
        /// The composer's rows inside its card: their sides and the space under the toolbar.
        public static let composerInnerH: CGFloat = 8
        public static let composerInnerB: CGFloat = 6
        /// Between the composer's rows.
        public static let composerStack: CGFloat = 4
        /// The transcript's text inside its scroll view.
        public static let transcript: CGFloat = 8
        /// A reply's white card in the transcript.
        public static let replyCardH: CGFloat = 8
        public static let replyCardT: CGFloat = 6
        public static let replyCardB: CGFloat = 4
        /// The user's ink bubble.
        public static let userBubbleH: CGFloat = 8
        public static let userBubbleT: CGFloat = 6
        public static let userBubbleB: CGFloat = 4
        /// A tool call's chip row.
        public static let toolChipH: CGFloat = 6
        public static let toolChipV: CGFloat = 4
        /// A code block on the ink code surface, and the plain boxes like it.
        public static let codeBlock: CGFloat = 6
        /// A block quote inside its accent rule.
        public static let quote: CGFloat = 6
        /// The Mighty view's bar over the diagram or timeline.
        public static let graphBarH: CGFloat = 8
        public static let graphBarV: CGFloat = 4
        /// A diagram block's body and the timeline's rows.
        public static let graphBlockBodyH: CGFloat = 8
        public static let graphBlockBodyV: CGFloat = 4
        /// The sidebar's search field, inside its wash.
        public static let sidebarSearch: CGFloat = 6
        /// The sidebar's section headers ("Workspaces 3").
        public static let sidebarSectionH: CGFloat = 12
        public static let sidebarSectionT: CGFloat = 10
        public static let sidebarSectionB: CGFloat = 4
        /// A workspace row in the sidebar.
        public static let sidebarRowV: CGFloat = 4
        /// A pane row under its workspace in the sidebar.
        public static let paneRowV: CGFloat = 3
        /// The dashboard's page.
        public static let dashboardPageH: CGFloat = 12
        public static let dashboardPageT: CGFloat = 10
        public static let dashboardPageB: CGFloat = 12
        /// A dashboard number tile and the usage card beside them.
        public static let dashboardTileH: CGFloat = 10
        public static let dashboardTileV: CGFloat = 8
        /// A pane's row in a dashboard workspace group.
        public static let dashboardRowH: CGFloat = 8
        public static let dashboardRowV: CGFloat = 6
        /// The window's status bar.
        public static let statusBarH: CGFloat = 10
        public static let statusBarV: CGFloat = 3
        /// A sheet's content.
        public static let sheet: CGFloat = 12
        /// A popover's content.
        public static let popover: CGFloat = 10
        /// Between the rows of a list (the sidebar's workspaces).
        public static let listGap: CGFloat = 2

        /// Every inset by its fixture name.
        public static let all: [String: CGFloat] = [
            "dock": dock,
            "workspaceHeaderH": workspaceHeaderH, "workspaceHeaderT": workspaceHeaderT, "workspaceHeaderB": workspaceHeaderB,
            "paneHeaderLeading": paneHeaderLeading, "paneHeaderTrailing": paneHeaderTrailing,
            "tabLeading": tabLeading, "tabTrailing": tabTrailing,
            "composerOuter": composerOuter, "composerInnerH": composerInnerH, "composerInnerB": composerInnerB, "composerStack": composerStack,
            "transcript": transcript,
            "replyCardH": replyCardH, "replyCardT": replyCardT, "replyCardB": replyCardB,
            "userBubbleH": userBubbleH, "userBubbleT": userBubbleT, "userBubbleB": userBubbleB,
            "toolChipH": toolChipH, "toolChipV": toolChipV,
            "codeBlock": codeBlock, "quote": quote,
            "graphBarH": graphBarH, "graphBarV": graphBarV,
            "graphBlockBodyH": graphBlockBodyH, "graphBlockBodyV": graphBlockBodyV,
            "sidebarSearch": sidebarSearch,
            "sidebarSectionH": sidebarSectionH, "sidebarSectionT": sidebarSectionT, "sidebarSectionB": sidebarSectionB,
            "sidebarRowV": sidebarRowV, "paneRowV": paneRowV,
            "dashboardPageH": dashboardPageH, "dashboardPageT": dashboardPageT, "dashboardPageB": dashboardPageB,
            "dashboardTileH": dashboardTileH, "dashboardTileV": dashboardTileV,
            "dashboardRowH": dashboardRowH, "dashboardRowV": dashboardRowV,
            "statusBarH": statusBarH, "statusBarV": statusBarV,
            "sheet": sheet, "popover": popover, "listGap": listGap,
        ]
    }

    /// Layout sizes: the sidebar's bounds, the fixed heights of the chrome, the smallest pane.
    public enum Layout {
        /// The sidebar's width bounds and default, and the drag width that folds it (`SidebarFold`).
        public static let sidebarMin: CGFloat = 210
        public static let sidebarDefault: CGFloat = 252
        public static let sidebarMax: CGFloat = 360
        public static let sidebarFoldThreshold: CGFloat = 150
        /// An agent pane's header line and the slim ink bar over the other panes.
        public static let paneHeader: CGFloat = 26
        /// A pane group's tab strip, and a tab in it.
        public static let tabStrip: CGFloat = 28
        public static let tab: CGFloat = 24
        /// A diagram block's header.
        public static let blockHead: CGFloat = 28
        /// A file or reference preview's header.
        public static let previewHead: CGFloat = 30
        /// The composer's toolbar: its pills and its round buttons.
        public static let toolbar: CGFloat = 26
        /// The smallest pane (a tab group) the dock lays out before it scrolls.
        public static let paneMinWidth: CGFloat = 315
        public static let paneMinHeight: CGFloat = 290
        /// A split's divider: the hit area between two panes.
        public static let splitDivider: CGFloat = 6
        /// The least height of something to click (a chip, a dense row, a settings switch's row), so
        /// tightening the spacing never shrinks a target below it.
        public static let hitTarget: CGFloat = 22

        /// Every size by its fixture name.
        public static let all: [String: CGFloat] = [
            "sidebarMin": sidebarMin, "sidebarDefault": sidebarDefault, "sidebarMax": sidebarMax, "sidebarFoldThreshold": sidebarFoldThreshold,
            "paneHeader": paneHeader, "tabStrip": tabStrip, "tab": tab, "blockHead": blockHead, "previewHead": previewHead,
            "toolbar": toolbar, "paneMinWidth": paneMinWidth, "paneMinHeight": paneMinHeight, "splitDivider": splitDivider, "hitTarget": hitTarget,
        ]
    }

    /// The popup standard's desktop sizes (fixture `metrics.dialog`, .omc/plans/popup-standard.md): a
    /// form sheet (kind B) and a panel (kind C) share the header, body and footer drawn by
    /// `DialogScaffold`; a popover (kind D) its widths. A Mac sheet or popover keeps the system's
    /// corners and shadow, so `radius`, the backdrop and the shadows are Windows' alone, kept here so
    /// the table matches the fixture. The fixture's `phone` sizes are the phone's.
    public enum Dialog {
        public static let radius: CGFloat = 12
        public static let popoverRadius: CGFloat = 10
        public static let headerHeight: CGFloat = 48
        public static let footerHeight: CGFloat = 52
        /// The header's and footer's sides and the body on every side.
        public static let padding: CGFloat = 16
        public static let titleSize: CGFloat = 15
        public static let subtitleSize: CGFloat = 12
        public static let iconSize: CGFloat = 20
        /// The panel's ✕: its hit square, glyph and hover corner.
        public static let closeHit: CGFloat = 28
        public static let closeGlyph: CGFloat = 14
        public static let closeRadius: CGFloat = 8
        public static let buttonHeight: CGFloat = 28
        public static let buttonPaddingX: CGFloat = 12
        public static let buttonRadius: CGFloat = 7
        public static let buttonGap: CGFloat = 8
        public static let gripSize: CGFloat = 16
        public static let alertWidth: CGFloat = 400
        /// A form sheet's three fixed widths.
        public static let sheetWidthS: CGFloat = 360
        public static let sheetWidthM: CGFloat = 460
        public static let sheetWidthL: CGFloat = 560
        /// A popover's two widths, and the share of the screen it grows to before it scrolls.
        public static let popoverWidthS: CGFloat = 320
        public static let popoverWidthL: CGFloat = 360
        public static let popoverMaxHeightRatio: CGFloat = 0.6
        public static let backdropOpacityLight: CGFloat = 0.28
        public static let backdropOpacityDark: CGFloat = 0.5
        public static let darkBorder: CGFloat = 1

        /// Every number by its fixture name (not the CSS shadows, the note or the phone's sizes).
        public static let all: [String: CGFloat] = [
            "radius": radius, "popoverRadius": popoverRadius, "headerHeight": headerHeight, "footerHeight": footerHeight,
            "padding": padding, "titleSize": titleSize, "subtitleSize": subtitleSize, "iconSize": iconSize,
            "closeHit": closeHit, "closeGlyph": closeGlyph, "closeRadius": closeRadius,
            "buttonHeight": buttonHeight, "buttonPaddingX": buttonPaddingX, "buttonRadius": buttonRadius, "buttonGap": buttonGap,
            "gripSize": gripSize, "alertWidth": alertWidth,
            "sheetWidthS": sheetWidthS, "sheetWidthM": sheetWidthM, "sheetWidthL": sheetWidthL,
            "popoverWidthS": popoverWidthS, "popoverWidthL": popoverWidthL, "popoverMaxHeightRatio": popoverMaxHeightRatio,
            "backdropOpacityLight": backdropOpacityLight, "backdropOpacityDark": backdropOpacityDark, "darkBorder": darkBorder,
        ]
    }

    /// The settings window (fixture `metrics.settings`, the same numbers as Windows): its size, the
    /// list of entries, the large title, and the one row every section draws (`SettingsRow`).
    public enum Settings {
        public static let width: CGFloat = 860
        public static let height: CGFloat = 640
        public static let minWidth: CGFloat = 720
        public static let minHeight: CGFloat = 520
        public static let listWidth: CGFloat = 260
        public static let largeTitle: CGFloat = 28
        public static let heroHeight: CGFloat = 104
        public static let barTitle: CGFloat = 15
        /// An entry of the list: its row, its icon tile and glyph, its text, and where the line between two starts.
        public static let navRowHeight: CGFloat = 48
        public static let navTile: CGFloat = 30
        public static let navTileRadius: CGFloat = 9
        public static let navGlyph: CGFloat = 15
        public static let navText: CGFloat = 14
        public static let navDividerInset: CGFloat = 56
        /// A section: its title over a card, the card's corner and sides.
        public static let sectionTitle: CGFloat = 12
        public static let cardRadius: CGFloat = 16
        public static let cardPaddingX: CGFloat = 16
        /// A row: its least height, its name and caption, the inset of the line between two.
        public static let rowMinHeight: CGFloat = 48
        public static let rowName: CGFloat = 14
        public static let rowCaption: CGFloat = 12
        public static let rowDividerInset: CGFloat = 16
        /// A row's control: the switch, a select's width bounds, a text field, and each of a pair of selects.
        public static let switchWidth: CGFloat = 42
        public static let switchHeight: CGFloat = 24
        public static let selectMinWidth: CGFloat = 160
        public static let selectMaxWidth: CGFloat = 240
        public static let fieldWidth: CGFloat = 240
        public static let pairSelectWidth: CGFloat = 160

        /// Every number by its fixture name (not the note).
        public static let all: [String: CGFloat] = [
            "width": width, "height": height, "minWidth": minWidth, "minHeight": minHeight, "listWidth": listWidth,
            "largeTitle": largeTitle, "heroHeight": heroHeight, "barTitle": barTitle,
            "navRowHeight": navRowHeight, "navTile": navTile, "navTileRadius": navTileRadius, "navGlyph": navGlyph,
            "navText": navText, "navDividerInset": navDividerInset,
            "sectionTitle": sectionTitle, "cardRadius": cardRadius, "cardPaddingX": cardPaddingX,
            "rowMinHeight": rowMinHeight, "rowName": rowName, "rowCaption": rowCaption, "rowDividerInset": rowDividerInset,
            "switchWidth": switchWidth, "switchHeight": switchHeight,
            "selectMinWidth": selectMinWidth, "selectMaxWidth": selectMaxWidth, "fieldWidth": fieldWidth, "pairSelectWidth": pairSelectWidth,
        ]
    }

    /// The motion tokens (fixture `motion`), in whole milliseconds but the easing curve:
    /// `quick` a small state change (a pill, an icon), `standard` a block appearing or
    /// leaving, `scroll` a scroll or camera move, `easing` their cubic-bezier,
    /// `pendingShowAfter` and `pendingMinVisible` the pending rule (`PendingDisplay`),
    /// `highlight` how long a highlight stays (the queue row's flash). Reduce Motion drops
    /// the movement only; the pending rule's timing stays. Windows reads the same group as
    /// `DesignMetrics.Motion` in MightyClaude.Core, the Windows page in theme/tokens and the
    /// phone in mobile/src/theme.
    public enum Motion {
        public static let quick = 150
        public static let standard = 220
        public static let scroll = 280
        /// cubic-bezier(x1, y1, x2, y2).
        public static let easing: [Double] = [0.2, 0, 0, 1]
        public static let pendingShowAfter = 150
        public static let pendingMinVisible = 300
        public static let highlight = 1200

        /// Every duration by its fixture name (`easing` is not one).
        public static let durations: [String: Int] = [
            "quick": quick, "standard": standard, "scroll": scroll,
            "pendingShowAfter": pendingShowAfter, "pendingMinVisible": pendingMinVisible, "highlight": highlight,
        ]

        /// A token's milliseconds as seconds, for an animation's duration or a timer.
        public static func seconds(_ milliseconds: Int) -> Double { Double(milliseconds) / 1000 }
    }

    /// The 파이프라인 view's state colours (fixture `pipeline`): a column's cards sit on its tone's
    /// soft tint at `columnTintOpacity`, every card on its own tone's tint at the lighter
    /// `cardTintOpacity`, so a card stands out even in a column of its own tone, with a
    /// `cardBarWidth` bar in the tone's mark down its leading edge
    /// (`MightyStructure.Tone`). Windows reads the same group as `DesignMetrics.Pipeline` in
    /// MightyClaude.Core and the phone as `pipeline` in mobile/src/theme.
    public enum Pipeline {
        public static let columnTintOpacity: Double = 0.6
        public static let cardTintOpacity: Double = 0.35
        public static let cardBarWidth: CGFloat = 3

        /// Every value by its fixture name.
        public static let all: [String: Double] = [
            "columnTintOpacity": columnTintOpacity, "cardTintOpacity": cardTintOpacity, "cardBarWidth": Double(cardBarWidth),
        ]
    }
}
