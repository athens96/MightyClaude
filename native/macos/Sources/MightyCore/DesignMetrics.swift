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
        /// How far a diagram block's content stays above its bottom edge so the corner
        /// resize grip (a hit target square, `Spacing.xxs` in from the corner) covers none of it.
        public static let gripClearance: CGFloat = hitTarget + 2 * Spacing.xxs

        /// Every size by its fixture name.
        public static let all: [String: CGFloat] = [
            "sidebarMin": sidebarMin, "sidebarDefault": sidebarDefault, "sidebarMax": sidebarMax, "sidebarFoldThreshold": sidebarFoldThreshold,
            "paneHeader": paneHeader, "tabStrip": tabStrip, "tab": tab, "blockHead": blockHead, "previewHead": previewHead,
            "toolbar": toolbar, "paneMinWidth": paneMinWidth, "paneMinHeight": paneMinHeight, "splitDivider": splitDivider, "hitTarget": hitTarget,
        ]
    }
}
