//! Follow mode for the structure view's page and columns (C# `MightyStructureFollow`, MightyCode.Core/MightyStructureFollow.cs; macOS
//! MightyGraphFollow.swift:129-318). While the reader is at the newest work, the newest card's bottom stays in view; a reader who scrolls or
//! pans away is left where they are until they come back to the newest card or send a request.

use std::collections::HashMap;

/// `MightyStructureFollow.Slack`: how far the newest card's bottom may hide below the viewport and the reader still counts as at it.
pub const SLACK: f64 = 36.0;

/// `MightyStructureFollow.Page(OriginY, VisibleHeight, DocumentHeight, NewestId, NewestBottom)`: where the page stands, top-down.
#[derive(Debug, Clone, PartialEq)]
pub struct Page {
    pub origin_y: f64,
    pub visible_height: f64,
    pub document_height: f64,
    pub newest_id: Option<String>,
    pub newest_bottom: Option<f64>,
}

impl Page {
    pub fn new(origin_y: f64, visible_height: f64, document_height: f64, newest_id: Option<&str>, newest_bottom: Option<f64>) -> Page {
        Page { origin_y, visible_height, document_height, newest_id: newest_id.map(str::to_string), newest_bottom }
    }

    /// `AtNewest`: the newest card's bottom is on screen or hidden by no more than the slack; with no request, the page's end is.
    fn at_newest(&self) -> bool {
        match (&self.newest_id, self.newest_bottom) {
            (None, _) => self.origin_y + self.visible_height >= self.document_height - SLACK,
            (Some(_), Some(bottom)) => bottom >= self.origin_y && bottom <= self.origin_y + self.visible_height + SLACK,
            (Some(_), None) => false,
        }
    }
    /// `NewestHidden`: the newest card's bottom is below the viewport or above it.
    fn newest_hidden(&self) -> bool {
        self.newest_bottom.is_some_and(|b| b > self.origin_y + self.visible_height + 0.5 || b < self.origin_y)
    }
    fn moved(&self, last: &Page) -> bool {
        (self.origin_y - last.origin_y).abs() > 0.5
    }
    fn laid_out(&self, last: &Page) -> bool {
        self.newest_id != last.newest_id
            || (self.document_height - last.document_height).abs() > 0.5
            || (self.visible_height - last.visible_height).abs() > 0.5
            || self.newest_bottom.is_none() != last.newest_bottom.is_none()
            || (self.newest_bottom.unwrap_or(0.0) - last.newest_bottom.unwrap_or(0.0)).abs() > 0.5
    }
}

/// `MightyStructureFollow.Scroll(Card)`: the page's end (`BOTTOM`), or the newest card's bottom when more of the page goes on below it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Scroll {
    pub card: Option<String>,
}

impl Scroll {
    pub const BOTTOM: Scroll = Scroll { card: None };
    pub fn to(card: &str) -> Scroll {
        Scroll { card: Some(card.to_string()) }
    }
}

const SHOW_TRIES: i32 = 3;

/// `MightyStructureFollow.PageWatch`: the view's follow state between page reports.
#[derive(Debug, Clone)]
pub struct PageWatch {
    following: bool,
    last: Option<Page>,
    /// A new request being brought into view: its own scrolls do not decide the follow state until it shows.
    showing: Option<String>,
    tries: i32,
    /// The page came back without following: its first report says where the reader is.
    judging: bool,
    /// The user's moves since the last report.
    user_move: f64,
}

impl Default for PageWatch {
    fn default() -> Self {
        PageWatch { following: true, last: None, showing: None, tries: 0, judging: false, user_move: 0.0 }
    }
}

impl PageWatch {
    pub fn new() -> PageWatch {
        PageWatch::default()
    }

    pub fn following(&self) -> bool {
        self.following
    }

    /// `Stop`: the user opened or folded a text; nothing follows.
    pub fn stop(&mut self) {
        self.following = false;
        self.showing = None;
        self.judging = false;
    }

    /// `UserScrolled` (MightyStructureFollow.cs:56): the user moved the page by `dy` points, judged at once from the last report moved by
    /// every such move since. Before any report there is nothing to judge from.
    pub fn user_scrolled(&mut self, dy: f64) {
        self.showing = None;
        self.judging = false;
        let Some(last) = &self.last else { return };
        self.user_move += dy;
        self.following = Page { origin_y: last.origin_y + self.user_move, ..last.clone() }.at_newest();
    }

    /// `Appeared`: a following reader is shown the newest card; any other follows from the first report if the page opened at it.
    pub fn appeared(&mut self, newest_id: Option<&str>) -> Option<Scroll> {
        self.last = None;
        if !self.following {
            self.judging = true;
            return None;
        }
        let Some(id) = newest_id else { return Some(Scroll::BOTTOM) };
        self.showing = Some(id.to_string());
        self.tries = SHOW_TRIES;
        Some(Scroll::to(id))
    }

    /// `NewRequest`: a new request is the user's own, shown and followed from now on.
    pub fn new_request(&mut self, id: &str) -> Scroll {
        self.following = true;
        self.judging = false;
        self.showing = Some(id.to_string());
        self.tries = SHOW_TRIES;
        Scroll::to(id)
    }

    /// `Observe` (MightyStructureFollow.cs:88): a page report; `user_scrolling` is a scroll the user is making right now. Returns where to
    /// scroll. The page only moving is a scroll; laid out anew it keeps the follow state it had, and scrolls only when the newest card's
    /// bottom is hidden.
    pub fn observe(&mut self, page: &Page, user_scrolling: bool) -> Option<Scroll> {
        let previous = self.last.replace(page.clone());
        self.user_move = 0.0;
        if self.judging {
            self.judging = false;
            self.following = page.at_newest();
            return None;
        }
        if user_scrolling {
            self.showing = None;
            self.following = page.at_newest();
            return None;
        }
        if let Some(previous) = &previous
            && page.moved(previous)
            && !page.laid_out(previous)
            && self.showing.is_none()
        {
            self.following = page.at_newest();
            return None;
        }
        if !self.following {
            return None;
        }
        if self.showing.is_some() && page.newest_id == self.showing && page.newest_bottom.is_some() && !page.newest_hidden() {
            self.showing = None;
        }
        let (true, Some(id), Some(bottom)) = (page.newest_hidden(), &page.newest_id, page.newest_bottom) else { return None };
        if self.showing.is_some() {
            self.tries -= 1;
            if self.tries <= 0 {
                self.showing = None;
            }
        }
        Some(if page.document_height - bottom <= SLACK { Scroll::BOTTOM } else { Scroll::to(id) })
    }
}

/// `MightyStructureFollow.ColumnWatch` (MightyStructureFollow.cs:108): the board's follow state, a `PageWatch` per column. Only the
/// newest board follows; another board, or the shown board becoming or ceasing to be the newest, starts over.
#[derive(Debug, Clone, Default)]
pub struct ColumnWatch {
    board_id: Option<String>,
    newest: bool,
    columns: HashMap<String, PageWatch>,
}

impl ColumnWatch {
    pub fn new() -> ColumnWatch {
        ColumnWatch::default()
    }

    pub fn board_id(&self) -> Option<&str> {
        self.board_id.as_deref()
    }

    pub fn newest(&self) -> bool {
        self.newest
    }

    /// `Following`: the column follows its end, on the newest board, unless the reader left it.
    pub fn following(&self, column: &str) -> bool {
        self.newest && self.columns.get(column).is_none_or(PageWatch::following)
    }

    fn show(&mut self, board: &str, newest: bool) {
        if self.board_id.as_deref() == Some(board) && newest == self.newest {
            return;
        }
        self.board_id = Some(board.to_string());
        self.newest = newest;
        self.columns.clear();
    }

    fn watch(&mut self, column: &str) -> &mut PageWatch {
        self.columns.entry(column.to_string()).or_default()
    }

    /// `Appeared`: a column of `board` appeared; a following column shows its end, any other is judged by its first report.
    pub fn appeared(&mut self, board: &str, newest: bool, column: &str) -> Option<Scroll> {
        self.show(board, newest);
        if newest { self.watch(column).appeared(Some(column)) } else { None }
    }

    /// `NewRequest`: a new request on the newest board; the column shows its end and follows it from now on.
    pub fn new_request(&mut self, board: &str, column: &str) -> Scroll {
        self.show(board, true);
        let mut watch = PageWatch::new();
        let scroll = watch.new_request(column);
        self.columns.insert(column.to_string(), watch);
        scroll
    }

    /// `Stop`: the user opened or folded a text in the column.
    pub fn stop(&mut self, column: &str) {
        if self.newest {
            self.watch(column).stop();
        }
    }

    /// `UserScrolled`: the user moved the column by `dy` points.
    pub fn user_scrolled(&mut self, column: &str, dy: f64) {
        if self.newest
            && let Some(watch) = self.columns.get_mut(column)
        {
            watch.user_scrolled(dy);
        }
    }

    /// `Observe`: a column's report; where the column scrolls, none on an older board.
    pub fn observe(&mut self, board: &str, newest: bool, column: &str, page: &Page, user_scrolling: bool) -> Option<Scroll> {
        self.show(board, newest);
        if newest { self.watch(column).observe(page, user_scrolling) } else { None }
    }
}
