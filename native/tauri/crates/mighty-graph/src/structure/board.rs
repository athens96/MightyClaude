//! One page of the structure view's kanban (C# `MightyStructure.Board`, `Boards`, `Navigation`, MightyCode.Core/MightyStructureBoard.cs;
//! macOS MightyStructureBoard.swift): one request of a free pane, or one pipeline of a styled pane, with the steps it went through as its
//! header and one column of cards per step under it.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use mighty_core::locale::Locale;

use super::{Column, Item, NEWLINES, Node, Row, Shape, Step};
use crate::notice;

/// `MightyStructure.BoardKind`: a free pane's request, or a styled pane's run of requests through the style's phases.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum BoardKind {
    Request,
    Pipeline,
}

impl BoardKind {
    /// The kind as the page is sent it (`ToString().ToLowerInvariant()`).
    pub fn word(self) -> &'static str {
        match self {
            BoardKind::Request => "request",
            BoardKind::Pipeline => "pipeline",
        }
    }
}

/// `MightyStructure.BoardStep(Id, Title, Step)`: "request", "execution" or "result" on a free board, else the phase id; `title` the
/// phase's, none for a free board's steps, which the view names.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoardStep {
    pub id: String,
    pub title: Option<String>,
    pub step: Step,
}

/// `MightyStructure.CardKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CardKind {
    Request,
    Execution,
    Result,
    Summary,
    Agent,
}

impl CardKind {
    /// The kind as the page is sent it (`ToString().ToLowerInvariant()`).
    pub fn word(self) -> &'static str {
        match self {
            CardKind::Request => "request",
            CardKind::Execution => "execution",
            CardKind::Result => "result",
            CardKind::Summary => "summary",
            CardKind::Agent => "agent",
        }
    }
}

/// `MightyStructure.Card(Kind, Item, Node, Parent)`: an agent card carries its node and the title of the block it runs under.
#[derive(Debug, Clone, PartialEq)]
pub struct Card {
    pub kind: CardKind,
    pub item: Arc<Item>,
    pub node: Option<Node>,
    pub parent: Option<String>,
}

impl Card {
    pub fn id(&self) -> String {
        match (&self.kind, &self.node) {
            (CardKind::Agent, Some(node)) => format!("agent:{}", node.node_id),
            _ => format!("{}:{}", self.kind.word(), self.item.run_id),
        }
    }
}

/// `MightyStructure.BoardColumn(Step, Cards, Count)`: `count` is the header's — a styled column's requests, a free column's cards.
#[derive(Debug, Clone, PartialEq)]
pub struct BoardColumn {
    pub step: BoardStep,
    pub cards: Vec<Card>,
    pub count: usize,
}

impl BoardColumn {
    pub fn id(&self) -> &str {
        &self.step.id
    }
}

/// `MightyStructure.Board(Id, Kind, Ordinal, Items, Headline, Status, Steps, Columns)`. `id` is the first request's run id.
#[derive(Debug, Clone, PartialEq)]
pub struct Board {
    pub id: String,
    pub kind: BoardKind,
    pub ordinal: usize,
    pub items: Vec<Arc<Item>>,
    pub headline: Option<String>,
    pub status: String,
    pub steps: Vec<BoardStep>,
    pub columns: Vec<BoardColumn>,
}

impl Board {
    pub fn run_ids(&self) -> Vec<String> {
        self.items.iter().map(|i| i.run_id.clone()).collect()
    }
    /// `Quiet`: only notice runs taken into a running turn; the view never moves on to it by itself.
    pub fn quiet(&self) -> bool {
        !self.items.is_empty() && self.items.iter().all(|i| i.quiet)
    }
    /// `CurrentStepIndex`: the current step's place, else the last step that is over.
    pub fn current_step_index(&self) -> Option<usize> {
        self.steps.iter().position(|s| s.step == Step::Current).or_else(|| self.steps.iter().rposition(|s| s.step != Step::Pending))
    }
}

/// `MightyStructure.Boards` (MightyStructureBoard.cs:59): the boards of `shape`, oldest first. A free pane has one board per request; a
/// styled pane one per pipeline, a run of consecutive requests whose phase never goes back.
pub fn boards(shape: &Shape, locale: &Locale) -> Vec<Board> {
    match &shape.columns {
        Some(columns) => pipeline_boards(columns, locale),
        None => shape.rows.iter().flatten().enumerate().map(|(index, row)| free_board(row, index + 1, locale)).collect(),
    }
}

fn free_board(row: &Row, ordinal: usize, locale: &Locale) -> Board {
    let item = &row.item;
    let steps = vec![
        BoardStep { id: "request".into(), title: None, step: row.request },
        BoardStep { id: "execution".into(), title: None, step: row.execution },
        BoardStep { id: "result".into(), title: None, step: row.result },
    ];
    let agents = agent_cards(item);
    let card = |kind| Card { kind, item: item.clone(), node: None, parent: None };
    let mut execution = vec![card(CardKind::Execution)];
    let count = 1 + agents.len();
    execution.extend(agents);
    let columns = vec![
        BoardColumn { step: steps[0].clone(), cards: vec![card(CardKind::Request)], count: 1 },
        BoardColumn { step: steps[1].clone(), cards: execution, count },
        BoardColumn { step: steps[2].clone(), cards: vec![card(CardKind::Result)], count: usize::from(item.finished) },
    ];
    Board {
        id: item.run_id.clone(),
        kind: BoardKind::Request,
        ordinal,
        items: vec![item.clone()],
        headline: first_line(item, locale),
        status: item.status.clone(),
        steps,
        columns,
    }
}

fn pipeline_boards(columns: &[Column], locale: &Locale) -> Vec<Board> {
    let mut phase_of: HashMap<usize, usize> = HashMap::new();
    let mut items: Vec<Arc<Item>> = vec![];
    for (phase, column) in columns.iter().enumerate() {
        for item in &column.items {
            phase_of.insert(item.run_index, phase);
            items.push(item.clone());
        }
    }
    items.sort_by_key(|i| i.run_index);
    let phase = |item: &Item| phase_of.get(&item.run_index).copied().unwrap_or(0);
    // Split where the phase goes back.
    let mut groups: Vec<Vec<Arc<Item>>> = vec![];
    let mut previous: Option<usize> = None;
    for item in items {
        let at = phase(&item);
        match (previous, groups.last_mut()) {
            (Some(before), Some(group)) if at >= before => group.push(item),
            _ => groups.push(vec![item]),
        }
        previous = Some(at);
    }
    let pane = columns.iter().position(|c| c.step == Step::Current);
    let count = groups.len();
    groups
        .into_iter()
        .enumerate()
        .map(|(index, group)| {
            let last = phase(group.last().expect("a group holds a request"));
            let newest = index == count - 1;
            let current = match pane {
                Some(at) if newest && at >= last => at,
                _ => last,
            };
            let over = !newest && group.iter().all(|i| i.finished);
            let board_columns: Vec<BoardColumn> = columns
                .iter()
                .enumerate()
                .map(|(at, column)| {
                    let step = if at < current {
                        Step::Done
                    } else if at > current {
                        Step::Pending
                    } else if over {
                        Step::Done
                    } else {
                        Step::Current
                    };
                    let held: Vec<&Arc<Item>> = group.iter().filter(|i| phase(i) == at).collect();
                    let mut cards = vec![];
                    for item in &held {
                        cards.push(Card { kind: CardKind::Summary, item: (*item).clone(), node: None, parent: None });
                        cards.extend(agent_cards(item));
                    }
                    BoardColumn {
                        step: BoardStep { id: column.phase.id.clone(), title: Some(column.phase.title.clone()), step },
                        cards,
                        count: held.len(),
                    }
                })
                .collect();
            let status = if group.iter().any(|i| i.status == "running") {
                "running".to_string()
            } else if group.iter().any(|i| i.status == "waiting") {
                "waiting".to_string()
            } else {
                group.last().map(|i| i.status.clone()).unwrap_or_default()
            };
            Board {
                id: group[0].run_id.clone(),
                kind: BoardKind::Pipeline,
                ordinal: index + 1,
                headline: first_line(&group[0], locale),
                status,
                steps: board_columns.iter().map(|c| c.step.clone()).collect(),
                columns: board_columns,
                items: group,
            }
        })
        .collect()
}

/// `AgentCards`: a request's sub-agent cards, each tree depth-first, each naming the block it runs under.
fn agent_cards(item: &Arc<Item>) -> Vec<Card> {
    let mut cards = vec![];
    for cluster in &item.clusters {
        let mut titles: Vec<&str> = vec![];
        for node in &cluster.nodes {
            titles.truncate(node.depth.min(titles.len()));
            cards.push(Card {
                kind: CardKind::Agent,
                item: item.clone(),
                node: Some(node.clone()),
                parent: titles.last().map(|t| t.to_string()),
            });
            titles.push(&node.title);
        }
    }
    cards
}

/// `FirstLine`: the request's first line with words in it; a notice run's heading.
fn first_line(item: &Item, locale: &Locale) -> Option<String> {
    if item.notice_run {
        return Some(notice::heading_of(item.notice_title.as_deref(), locale));
    }
    let text = item.request.as_ref().map_or(item.input.as_str(), |r| r.text.as_str());
    text.split(NEWLINES).map(str::trim).find(|l| !l.is_empty()).map(str::to_string)
}

/// `MightyStructure.Navigation` (MightyStructureBoard.cs:138): which board the view shows. While the user is on the newest board the view
/// moves on with it; a board the user picks that is not the newest is kept by its id, and by the requests it held when picked.
#[derive(Debug, Clone, Default)]
pub struct Navigation {
    chosen: Option<String>,
    chosen_runs: Vec<String>,
    seen_newest: Option<String>,
}

impl Navigation {
    pub fn new() -> Navigation {
        Navigation::default()
    }

    /// `Chosen`: the board the user picked, none while the view shows the newest one.
    pub fn chosen(&self) -> Option<&str> {
        self.chosen.as_deref()
    }

    /// `NewestIndex`: the last board, but a quiet notice's board after it.
    pub fn newest_index(boards: &[Board]) -> Option<usize> {
        boards.iter().rposition(|b| !b.quiet()).or(boards.len().checked_sub(1))
    }

    /// `Index`: the place of the board shown. A picked board that is gone is found by a request it held, else the oldest board is shown.
    pub fn index(&self, boards: &[Board]) -> Option<usize> {
        if boards.is_empty() {
            return None;
        }
        let Some(chosen) = &self.chosen else { return Navigation::newest_index(boards) };
        if let Some(at) = boards.iter().position(|b| &b.id == chosen) {
            return Some(at);
        }
        let runs: HashSet<&str> = self.chosen_runs.iter().map(String::as_str).collect();
        Some(boards.iter().position(|b| b.items.iter().any(|i| runs.contains(i.run_id.as_str()))).unwrap_or(0))
    }

    /// `OnNewest`: the shown board is the newest one.
    pub fn on_newest(&self, boards: &[Board]) -> bool {
        self.index(boards).is_none_or(|i| Some(i) == Navigation::newest_index(boards))
    }

    /// `HasUnseen`: a board newer than the one shown arrived since the user left the newest board.
    pub fn has_unseen(&self, boards: &[Board]) -> bool {
        !self.on_newest(boards) && Navigation::newest_index(boards).map(|n| boards[n].id.as_str()) != self.seen_newest.as_deref()
    }

    /// `Select`: shows board `id`; the newest board is shown as "the newest", so the view moves on with new ones again.
    pub fn select(&mut self, id: &str, boards: &[Board]) {
        let Some(target) = boards.iter().find(|b| b.id == id) else { return };
        let newest = Navigation::newest_index(boards).map(|at| boards[at].id.clone());
        if Some(&target.id) == newest.as_ref() {
            self.chosen = None;
            self.chosen_runs.clear();
            self.seen_newest = None;
            return;
        }
        if self.on_newest(boards) {
            self.seen_newest = newest;
        }
        self.chosen = Some(target.id.clone());
        self.chosen_runs = target.run_ids();
    }

    /// `Step`: shows the board `offset` places from the shown one, stopping at the ends.
    pub fn step(&mut self, offset: isize, boards: &[Board]) {
        let Some(index) = self.index(boards) else { return };
        let at = (index as isize + offset).clamp(0, boards.len() as isize - 1) as usize;
        let id = boards[at].id.clone();
        self.select(&id, boards);
    }

    /// `ShowNewest`: shows the newest board, and moves on with new ones from now on.
    pub fn show_newest(&mut self, boards: &[Board]) {
        match Navigation::newest_index(boards) {
            None => self.chosen = None,
            Some(newest) => {
                let id = boards[newest].id.clone();
                self.select(&id, boards);
            }
        }
    }
}
