//! The Mighty diagram's layout (C# `MightyGraphLayout`, MightyCode.Core/MightyGraphLayout.cs, a port of macOS MightyGraphLayout): every
//! request as a tree — its request block, its 실행 block, the sub-agents under the block that launched them, the result — stacked down
//! the centreline, the next request's draft under them, and the attachments (file list, pictures, background executions, answered
//! plans) hung beside the flow. With `fan_out` it is the role view of a Claude pane: cards, steps, delegate lines and the return node.

use std::collections::{HashMap, HashSet};

use mighty_core::graph::{GraphBlockSize, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;

use crate::camera::{self, GraphRect, Size};
use crate::flow::{card, lines, steps};
use crate::support;

pub use crate::support::{finished, has_result, latest_result_id, layout_terminal as terminal, node_id};

/// `Node(Id, Kind, Frame, IsCompact)`: `is_compact`, a sub-agent or a background command drawn as its compact card.
#[derive(Debug, Clone, PartialEq)]
pub struct Node {
    pub id: String,
    pub kind: String,
    pub frame: GraphRect,
    pub is_compact: bool,
}

impl Node {
    pub fn new(id: impl Into<String>, kind: &str, frame: GraphRect) -> Self {
        Node { id: id.into(), kind: kind.into(), frame, is_compact: false }
    }
}

/// `Edge(Source, Target, Joins, Side)`: `side`, from a spine block's trailing edge to a block it launched beside it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Edge {
    pub source: String,
    pub target: String,
    pub joins: bool,
    pub side: bool,
}

impl Edge {
    fn new(source: &str, target: &str) -> Self {
        Edge { source: source.into(), target: target.into(), joins: false, side: false }
    }
    fn joining(source: &str, target: &str) -> Self {
        Edge { joins: true, ..Edge::new(source, target) }
    }
}

/// `FanOut(Opened, OpenAll)`: the role view; `opened` holds the nodes opened from their card into the full block, `open_all` lays every
/// card out as its block (the web page's copy).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FanOut {
    pub opened: Option<HashSet<String>>,
    pub open_all: bool,
}

impl FanOut {
    pub fn open_all() -> Self {
        FanOut { opened: None, open_all: true }
    }
    pub fn opened(ids: impl IntoIterator<Item = String>) -> Self {
        FanOut { opened: Some(ids.into_iter().collect()), open_all: false }
    }
    /// `IsOpened`.
    pub fn is_opened(&self, id: &str) -> bool {
        self.open_all || self.opened.as_ref().is_some_and(|o| o.contains(id))
    }
}

/// A step that produced pictures (`MightyGraphImages.Galleries`): its run, the step and how many.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Gallery {
    pub run_id: String,
    pub step: String,
    pub count: usize,
}

/// `DelegateKind`, `DelegateSuffix`, `ReturnKind`, `ReturnSuffix`, `StepKind` (MightyGraphLayout.cs:24-26).
pub const DELEGATE_KIND: &str = "delegate";
pub const DELEGATE_SUFFIX: &str = "delegate:";
pub const RETURN_KIND: &str = "return";
pub const RETURN_SUFFIX: &str = "return";
pub const STEP_KIND: &str = "step";
/// `DelegateGap`, `DelegateRowGap`.
pub const DELEGATE_GAP: f64 = 18.0;
pub const DELEGATE_ROW_GAP: f64 = 26.0;
const SIBLING_GAP: f64 = 20.0;
const ROW_GAP: f64 = 32.0;
/// `HistoryNodeID`, `HistoryWidth`, `HistoryHeight`: the top of the diagram, which loads older requests.
pub const HISTORY_NODE_ID: &str = "history-top";
pub const HISTORY_WIDTH: f64 = 380.0;
pub const HISTORY_HEIGHT: f64 = 44.0;
/// `PlanSuffix`, `PlanWidth`, `PlanHeight`, `PlanMinimumWidth`, `PlanMinimumHeight`.
pub const PLAN_SUFFIX: &str = "plan";
pub const PLAN_WIDTH: f64 = 640.0;
pub const PLAN_HEIGHT: f64 = 520.0;
pub const PLAN_MINIMUM_WIDTH: f64 = mighty_core::graph::MINIMUM_WIDTH;
pub const PLAN_MINIMUM_HEIGHT: f64 = mighty_core::graph::MINIMUM_HEIGHT;
/// `PlanRecordSuffix`, `PlanRecordWidth`.
pub const PLAN_RECORD_SUFFIX: &str = mighty_core::graph::PLAN_RECORD_SUFFIX;
pub const PLAN_RECORD_WIDTH: f64 = 320.0;
/// `WorkSuffix`, `WorkKind`: the 실행 block under a request.
pub const WORK_SUFFIX: &str = mighty_core::graph::WORK_SUFFIX;
pub const WORK_KIND: &str = "work";
/// `ExecutionKind`, `ExecutionSuffix`, `ExecutionWidth`: a background execution's block.
pub const EXECUTION_KIND: &str = "execution";
pub const EXECUTION_SUFFIX: &str = "ouroboros-execution:";
pub const EXECUTION_WIDTH: f64 = 380.0;
/// `ImagesKind`, `ImagesSuffix`, `ImagesWidth`, `ImagesColumns`, `ImagesTile`: a step's picture preview.
pub const IMAGES_KIND: &str = "images";
pub const IMAGES_SUFFIX: &str = "images:";
pub const IMAGES_WIDTH: f64 = 304.0;
pub const IMAGES_COLUMNS: usize = 3;
pub const IMAGES_TILE: f64 = 88.0;
const ATTACHMENT_GAP: f64 = 8.0;
/// `MinimumResultHeight`.
pub const MINIMUM_RESULT_HEIGHT: f64 = mighty_core::graph::MINIMUM_HEIGHT;

/// `PlanRecordHeight`.
pub fn plan_record_height(expanded: bool) -> f64 {
    if expanded { 420.0 } else { 104.0 }
}

/// `ExecutionHeight`.
pub fn execution_height(expanded: bool) -> f64 {
    if expanded { 470.0 } else { 290.0 }
}

/// `VisibleImages`: two rows folded, four unfolded.
pub fn visible_images(count: usize, expanded: bool) -> usize {
    count.min(IMAGES_COLUMNS * if expanded { 4 } else { 2 })
}

/// `ImagesHeight`.
pub fn images_height(count: usize, expanded: bool) -> f64 {
    let rows = 1.max(visible_images(count, expanded).div_ceil(IMAGES_COLUMNS)) as f64;
    38.0 + 24.0 + rows * IMAGES_TILE + (rows - 1.0) * 8.0
}

/// `RequestHeight`: a request block that holds its input alone, over its 실행 block.
pub fn request_height(expanded: bool) -> f64 {
    if expanded { 280.0 } else { 140.0 }
}

/// `MinimumBlockSize`: the least a drag may make a block of `kind`.
pub fn minimum_block_size(kind: &str) -> Size {
    match kind {
        "plan" => Size::new(PLAN_MINIMUM_WIDTH, PLAN_MINIMUM_HEIGHT),
        "planRecord" => Size::new(mighty_core::graph::MINIMUM_WIDTH, plan_record_height(false)),
        _ => Size::new(mighty_core::graph::MINIMUM_WIDTH, mighty_core::graph::MINIMUM_HEIGHT),
    }
}

/// `IsFixed`: neither selected nor resized (the history block, the role view's line and return node, a compact card).
pub fn is_fixed(node: &Node) -> bool {
    node.is_compact || matches!(node.kind.as_str(), "history" | DELEGATE_KIND | RETURN_KIND)
}

/// `IsAuxiliary(kind)`: attachments beside the flow.
pub fn is_auxiliary(kind: &str) -> bool {
    matches!(kind, "resultFiles" | "planRecord" | EXECUTION_KIND | IMAGES_KIND)
}

/// `IsResizable(kind)`: blocks with the corner handle and the edge bands.
pub fn is_resizable_kind(kind: &str) -> bool {
    matches!(kind, "request" | WORK_KIND | "agent" | "result" | "draft" | "plan" | "planRecord" | STEP_KIND)
}

/// `IsResizable(node)`: a resizable kind that is no compact card.
pub fn is_resizable(node: &Node) -> bool {
    !node.is_compact && is_resizable_kind(&node.kind)
}

/// `FittedResultId`.
pub fn fitted_result_id(runs: &[MightyGraphRun], viewport: Option<Size>, has_shared_result_size: bool) -> Option<String> {
    if viewport.is_some() && !has_shared_result_size { latest_result_id(runs) } else { None }
}

/// `ResultFitSize`: the canvas less 24pt margins and the files panel, never below 500 × 200.
pub fn result_fit_size(viewport: Size, files_panel_open: bool) -> Size {
    Size::new(500f64.max(viewport.w - 48.0 - if files_panel_open { 336.0 } else { 0.0 }), 200f64.max(viewport.h - 48.0))
}

/// `ResultViewportLimit`: the most of the pane the newest result card may take, in diagram coordinates at `zoom`.
pub fn result_viewport_limit(viewport: Size, zoom: f64, files_panel_open: bool) -> Size {
    let scale = if zoom.is_finite() && zoom > 0.0 { zoom } else { 1.0 };
    Size::new(
        mighty_core::graph::MINIMUM_WIDTH.max((viewport.w - 48.0) / scale - if files_panel_open { 336.0 } else { 0.0 }),
        MINIMUM_RESULT_HEIGHT.max((viewport.h - 48.0) / scale),
    )
}

/// `ResultCap`: `maximum` kept within `limit` side by side.
pub fn result_cap(maximum: Size, limit: Option<Size>) -> Size {
    match limit {
        Some(l) => Size::new(maximum.w.min(l.w), maximum.h.min(l.h)),
        None => maximum,
    }
}

/// `ResultSize`: as tall as its content up to `cap`, never shorter than the minimum; unmeasured content takes the cap.
pub fn result_size(cap: Size, content_height: Option<f64>) -> Size {
    match content_height {
        Some(content) if content.is_finite() && content >= 0.0 => Size::new(cap.w, cap.h.min(MINIMUM_RESULT_HEIGHT.max(content.ceil()))),
        _ => cap,
    }
}

/// `PlanSize`: the pending plan block's size, with the limit and the window fit in force for a drag of it.
pub fn plan_size(saved: Option<&GraphBlockSize>, viewport: Option<Size>, zoom: Option<f64>) -> (Size, Option<Size>, Option<Size>) {
    let saved = saved.and_then(|s| s.normalized()).map(|s| Size::new(s.width, s.height));
    let Some(vp) = viewport else { return (saved.unwrap_or(Size::new(PLAN_WIDTH, PLAN_HEIGHT)), None, None) };
    let fit = result_fit_size(vp, false);
    let limit = zoom.map(|z| result_viewport_limit(vp, z, false));
    (result_cap(saved.unwrap_or(fit), limit), limit, Some(fit))
}

/// `ResultDragSides`: which sides a corner drag moved.
pub fn result_drag_sides(start: Size, dragged: Size) -> (bool, bool) {
    ((dragged.w - start.w).abs() > 0.5, (dragged.h - start.h).abs() > 0.5)
}

/// `FilesPanelOpen`: whether the result files panel is open beside the newest result card.
pub fn files_panel_open(runs: &[MightyGraphRun], result_files_run_id: Option<&str>) -> bool {
    match runs.iter().rev().find(|r| has_result(r)) {
        Some(run) => result_files_run_id == Some(run.id()) && run.status() == "completed",
        None => false,
    }
}

/// `ResizePhase`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResizePhase {
    Live,
    Finished,
    Cancelled,
}

/// `ResultDrag`: a drag of the newest result card, both what it shows (`live`) and what it saves (on release).
pub fn result_drag(
    dragged: Size,
    horizontal: bool,
    vertical: bool,
    phase: ResizePhase,
    saved: Option<&GraphBlockSize>,
    limit: Option<Size>,
    window_fit: Option<Size>,
) -> (Option<GraphBlockSize>, Option<GraphBlockSize>) {
    match phase {
        ResizePhase::Cancelled => (None, None),
        ResizePhase::Live => {
            let shown = result_cap(dragged, limit);
            (GraphBlockSize { width: shown.w, height: shown.h }.normalized(), None)
        }
        ResizePhase::Finished => {
            let side = |moved: bool, dragged: f64, saved: Option<f64>, fit: Option<f64>, limit: Option<f64>| -> f64 {
                let maximum = saved.or(fit);
                if !moved {
                    return maximum.unwrap_or(dragged);
                }
                match limit {
                    Some(l) if dragged >= l - 0.5 => maximum.unwrap_or(l).max(l),
                    _ => dragged,
                }
            };
            let width = side(horizontal, dragged.w, saved.map(|s| s.width), window_fit.map(|f| f.w), limit.map(|l| l.w));
            let height = side(vertical, dragged.h, saved.map(|s| s.height), window_fit.map(|f| f.h), limit.map(|l| l.h));
            (None, GraphBlockSize { width, height }.normalized())
        }
    }
}

/// `MightyGraphLayout`: the laid-out diagram.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Layout {
    pub nodes: Vec<Node>,
    pub edges: Vec<Edge>,
    pub size: Size,
    pub origin_x: f64,
    /// Older requests loaded from the session record sit above the first retained one, at negative y; the canvas starts here.
    pub origin_y: f64,
    pub fitted_result_id: Option<String>,
    /// The newest result card when the viewport limit decides its size in some direction.
    pub viewport_bound_result_id: Option<String>,
    pub result_limit: Option<Size>,
    pub result_window_fit: Option<Size>,
    pub plan_limit: Option<Size>,
    pub plan_window_fit: Option<Size>,
}

impl Layout {
    /// `NodeAt`: the block that holds a point, the first laid out when two overlap.
    pub fn node_at(&self, x: f64, y: f64) -> Option<&Node> {
        self.nodes.iter().find(|n| x >= n.frame.x && x < n.frame.max_x() && y >= n.frame.y && y < n.frame.max_y())
    }

    /// `NewestRequestTargetID`: the newest request's 실행 block while it runs, else the request.
    pub fn newest_request_target_id(&self, runs: &[MightyGraphRun]) -> Option<String> {
        let last = support::newest_request_run(runs)?;
        let work = node_id(last, WORK_SUFFIX);
        Some(if !finished(last) && self.nodes.iter().any(|n| n.id == work) { work } else { node_id(last, "request") })
    }

    pub fn node(&self, id: &str) -> Option<&Node> {
        self.nodes.iter().find(|n| n.id == id)
    }
}

/// What `Make` reads besides the runs, the draft, the running flag and the expanded blocks (C#'s optional parameters, MightyGraphLayout.cs:293).
#[derive(Debug, Clone, Default)]
pub struct Options {
    pub result_files_run_id: Option<String>,
    pub viewport: Option<Size>,
    pub has_shared_result_size: bool,
    pub zoom: Option<f64>,
    pub shared_result_size: Option<GraphBlockSize>,
    pub retained_start: usize,
    pub history: bool,
    pub result_content_height: Option<f64>,
    pub block_sizes: Option<HashMap<String, GraphBlockSize>>,
    pub plan_run_id: Option<String>,
    /// `(RunID, RecordID)`.
    pub plan_records: Vec<(String, String)>,
    pub plan_size: Option<GraphBlockSize>,
    pub splits_execution: bool,
    pub galleries: Vec<Gallery>,
    /// `(RunID, Key)`.
    pub executions: Vec<(String, String)>,
    pub fan_out: Option<FanOut>,
}

struct Tree {
    nodes: Vec<Node>,
    edges: Vec<Edge>,
    width: f64,
    height: f64,
    top: String,
    leaves: Vec<String>,
}

impl Tree {
    fn offset(&mut self, dx: f64, dy: f64) {
        for node in &mut self.nodes {
            node.frame = node.frame.offset_by(dx, dy);
        }
    }
}

/// The sizes a block takes: the newest result's cap, a dragged size, else its default.
struct Sizer<'a> {
    latest_result_id: Option<&'a str>,
    viewport: Option<Size>,
    result_maximum: Option<Size>,
    result_limit: Option<Size>,
    has_shared_result_size: bool,
    result_content_height: Option<f64>,
    block_sizes: Option<&'a HashMap<String, GraphBlockSize>>,
}

impl Sizer<'_> {
    fn size(&self, id: &str, default_w: f64, default_h: f64) -> Size {
        if self.latest_result_id == Some(id) && self.viewport.is_some() {
            // The cap is the most it takes; it shrinks to its measured content.
            if let Some(maximum) = self.result_maximum {
                return result_size(result_cap(maximum, self.result_limit), self.result_content_height);
            }
            if self.has_shared_result_size {
                return Size::new(default_w, default_h);
            }
        }
        if let Some(custom) = self.block_sizes.and_then(|s| s.get(id)).and_then(|s| s.normalized_for(id)) {
            return Size::new(custom.width, custom.height);
        }
        Size::new(default_w, default_h)
    }
}

/// One run's blocks while its tree is built.
struct RunTree<'a> {
    run: &'a MightyGraphRun,
    agents: Vec<&'a MightyGraphAgent>,
    fan_out: Option<&'a FanOut>,
    in_step: HashSet<usize>,
    expanded: &'a HashSet<String>,
    sizer: &'a Sizer<'a>,
    visited: HashSet<usize>,
}

impl RunTree<'_> {
    fn ordered(&self, values: Vec<usize>) -> Vec<usize> {
        let mut values = values;
        if self.fan_out.is_some() {
            values.sort_by_key(|&i| support::flow_order(self.run, i));
        }
        values
    }

    fn below(&self, index: usize) -> Vec<usize> {
        let id = self.agents[index].id();
        self.ordered((0..self.agents.len()).filter(|&ci| ci != index && self.agents[ci].parent_id() == Some(id)).collect())
    }

    /// A card counts the blocks under it instead of drawing them: they are laid out nowhere.
    fn fold(&mut self, index: usize, depth: usize) {
        if depth >= 64 || !self.visited.insert(index) {
            return;
        }
        let id = self.agents[index].id();
        let under: Vec<usize> = (0..self.agents.len()).filter(|&ci| ci != index && self.agents[ci].parent_id() == Some(id)).collect();
        for ci in under {
            self.fold(ci, depth + 1);
        }
    }

    fn branch(&mut self, index: usize, depth: usize) -> Option<Tree> {
        if depth >= 64 || !self.visited.insert(index) {
            return None;
        }
        let agent = self.agents[index];
        let id = node_id(self.run, &format!("agent:{}", agent.id()));
        let compact = self.fan_out.is_some_and(|f| card::is_compact(agent, f.is_opened(&id)));
        let card_size = if compact {
            Size::new(card::WIDTH, if self.in_step.contains(&index) { card::STEP_HEIGHT } else { card::HEIGHT })
        } else {
            let default_h = if self.expanded.contains(&id) {
                480.0
            } else if agent.is_task() && agent.notice_reply.is_some() {
                320.0
            } else {
                240.0
            };
            self.sizer.size(&id, 360.0, default_h)
        };
        let height = card_size.h;
        let below = self.below(index);
        let children: Vec<Tree> = if compact {
            for ci in below {
                self.fold(ci, depth + 1);
            }
            vec![]
        } else {
            below.into_iter().filter_map(|ci| self.branch(ci, depth + 1)).collect()
        };
        let children_width = children.iter().map(|c| c.width).sum::<f64>() + children.len().saturating_sub(1) as f64 * SIBLING_GAP;
        // A block that delegated says so on the branch to its row (FlowDelegation).
        let delegates = self.fan_out.is_some() && !children.is_empty() && !lines::children(agent.id(), self.run).is_empty();
        let width = card_size.w.max(children_width).max(if delegates { lines::DELEGATE_WIDTH } else { 0.0 });
        let node = Node {
            id: id.clone(),
            kind: "agent".into(),
            frame: GraphRect::new((width - card_size.w) / 2.0, 0.0, card_size.w, height),
            is_compact: compact,
        };
        let mut result = Tree { nodes: vec![node], edges: vec![], width, height, top: id.clone(), leaves: vec![id.clone()] };
        if !children.is_empty() {
            result.leaves = vec![];
            let mut source = id.clone();
            let mut top = height + ROW_GAP;
            if delegates {
                let line = node_id(self.run, &format!("{DELEGATE_SUFFIX}{}", agent.id()));
                let frame = GraphRect::new(
                    (width - lines::DELEGATE_WIDTH) / 2.0,
                    height + DELEGATE_GAP,
                    lines::DELEGATE_WIDTH,
                    lines::DELEGATE_HEIGHT,
                );
                result.nodes.push(Node::new(line.clone(), DELEGATE_KIND, frame));
                result.edges.push(Edge::new(&id, &line));
                source = line;
                top = frame.max_y() + DELEGATE_ROW_GAP;
            }
            let mut x = (width - children_width) / 2.0;
            for mut child in children {
                child.offset(x, top);
                result.nodes.append(&mut child.nodes);
                result.edges.push(Edge::new(&source, &child.top));
                result.edges.append(&mut child.edges);
                result.leaves.append(&mut child.leaves);
                result.height = result.height.max(top + child.height);
                x += child.width + SIBLING_GAP;
            }
        }
        Some(result)
    }

    fn is_top(&self, index: usize, indexes: &HashMap<&str, usize>) -> bool {
        let agent = self.agents[index];
        agent.parent_id().is_none_or(|p| !indexes.contains_key(p) || p == agent.id())
    }
}

/// `Make`: the diagram of `runs` (as views draw them), the draft, whether the pane runs and the blocks the user opened.
pub fn make(runs: &[MightyGraphRun], draft: &str, running: bool, expanded: &HashSet<String>, options: &Options, locale: &Locale) -> Layout {
    // The runs as views draw them: a request's background blocks show only in the notice run that reports them (BackgroundNoticeRuns).
    let runs = support::shown_runs(runs, locale);
    let runs: &[MightyGraphRun] = &runs;
    let latest = latest_result_id(runs);
    let files_open_for_latest = files_panel_open(runs, options.result_files_run_id.as_deref());
    let shared = options.shared_result_size.and_then(|s| s.normalized()).map(|s| Size::new(s.width, s.height));
    let has_shared = options.has_shared_result_size || shared.is_some();
    let auto_fit = match (options.viewport, &latest) {
        (Some(vp), Some(_)) => Some(result_fit_size(vp, files_open_for_latest)),
        _ => None,
    };
    // With a zoom the newest result card never exceeds the pane (macOS); without one (the parity vectors) its cap stays whole.
    let result_limit = match (options.viewport, options.zoom, &latest) {
        (Some(vp), Some(z), Some(_)) => Some(result_viewport_limit(vp, z, files_open_for_latest)),
        _ => None,
    };
    let result_maximum = shared.or(if has_shared { None } else { auto_fit });
    let sizer = Sizer {
        latest_result_id: latest.as_deref(),
        viewport: options.viewport,
        result_maximum,
        result_limit,
        has_shared_result_size: has_shared,
        result_content_height: options.result_content_height,
        block_sizes: options.block_sizes.as_ref(),
    };
    let splits = options.splits_execution;
    let fan_out = options.fan_out.as_ref();

    let mut trees: Vec<Tree> = vec![];
    let mut plan_fit: Option<(Size, Option<Size>, Option<Size>)> = None;
    for run in runs {
        let main_id = node_id(run, "request");
        let main_size = sizer.size(
            &main_id,
            camera::REQUEST_WIDTH,
            if splits {
                request_height(expanded.contains(&main_id))
            } else if expanded.contains(&main_id) {
                540.0
            } else {
                280.0
            },
        );
        let main_height = main_size.h;
        let work_id = node_id(run, WORK_SUFFIX);
        let work_size =
            splits.then(|| sizer.size(&work_id, camera::REQUEST_WIDTH, if expanded.contains(&work_id) { 540.0 } else { 280.0 }));
        let result_id = node_id(run, "result");
        let result_size = sizer.size(&result_id, camera::REQUEST_WIDTH, if expanded.contains(&result_id) { 440.0 } else { 200.0 });

        // With a fan-out the run's background sub-agents and commands are laid out too, each where it was launched (macOS make(fanOut:)).
        let agents: Vec<&MightyGraphAgent> = if fan_out.is_none() { run.agents().iter().collect() } else { support::flow_agents(run) };
        let in_step: HashSet<usize> = if fan_out.is_none() {
            HashSet::new()
        } else {
            (0..agents.len()).filter(|&i| steps::step_label(agents[i], run, locale).is_some()).collect()
        };
        let mut indexes: HashMap<&str, usize> = HashMap::new();
        for (i, a) in agents.iter().enumerate() {
            indexes.entry(a.id()).or_insert(i);
        }
        let mut built = RunTree { run, agents, fan_out, in_step, expanded, sizer: &sizer, visited: HashSet::new() };

        // A plan waiting for its answer stands where the result will.
        let plan_waits = options.plan_run_id.as_deref() == Some(run.id()) && !finished(run);
        // The role view: while there is no answer yet the main session's card stands in its place.
        let ends_in_work = fan_out.is_some() && splits && !has_result(run) && !plan_waits;
        let work_compact = fan_out.is_some_and(|f| !f.is_opened(&work_id));
        let work_shown = if work_compact { Some(Size::new(steps::CARD_WIDTH, steps::CARD_HEIGHT)) } else { work_size };
        // The request's steps that began, by their index among all its steps.
        let started: Vec<(usize, &mighty_core::graph::FlowStep)> =
            if fan_out.is_none() { vec![] } else { steps::steps(run).iter().enumerate().filter(|(_, s)| !steps::is_pending(s)).collect() };
        let tree_width;
        let mut tree;
        if !started.is_empty() {
            // The request's spine: the request, then each step that began, every block launched from the main session beside the one whose
            // time it was launched in (FlowSteps.Slot; before the first step, beside the request).
            let spine_steps: Vec<mighty_core::graph::FlowStep> = started.iter().map(|(_, s)| (*s).clone()).collect();
            let mut slots: Vec<Vec<Tree>> = (0..=started.len()).map(|_| vec![]).collect();
            let order = built.ordered((0..built.agents.len()).collect());
            for ai in order {
                if !built.is_top(ai, &indexes) {
                    continue;
                }
                if let Some(t) = built.branch(ai, 0) {
                    let slot = steps::slot(built.agents[ai], &spine_steps);
                    slots[slot].push(t);
                }
            }
            for ai in 0..built.agents.len() {
                if !built.visited.contains(&ai)
                    && let Some(t) = built.branch(ai, 0)
                {
                    slots[0].push(t);
                }
            }
            let mut spine: Vec<(String, &str, Size, bool)> = vec![(main_id.clone(), "request", main_size, false)];
            for (_, step) in &started {
                let id = node_id(run, &steps::node_suffix(step));
                let compact = !fan_out.is_some_and(|f| f.is_opened(&id));
                let size = if compact {
                    Size::new(steps::CARD_WIDTH, steps::CARD_HEIGHT)
                } else {
                    sizer.size(&id, 360.0, if expanded.contains(&id) { 480.0 } else { 240.0 })
                };
                spine.push((id, STEP_KIND, size, compact));
            }
            // The spine stands on the run's centreline: the column beside it is matched by room on the other side.
            let mut half =
                (if has_result(run) { result_size.w } else { 0.0 }).max(if ends_in_work { work_shown.map_or(0.0, |w| w.w) } else { 0.0 })
                    / 2.0;
            for (slot, block) in spine.iter().enumerate() {
                let column = slots[slot].iter().map(|t| t.width).fold(0.0, f64::max);
                half = half.max(block.2.w / 2.0 + if column > 0.0 { steps::SIDE_GAP + column } else { 0.0 });
            }
            tree_width = 2.0 * half;
            tree = Tree { nodes: vec![], edges: vec![], width: tree_width, height: 0.0, top: main_id.clone(), leaves: vec![] };
            let mut spine_y = 0.0;
            let mut previous_block: Option<String> = None;
            for (slot, (id, kind, size, compact)) in spine.into_iter().enumerate() {
                let frame =
                    GraphRect::new(half - size.w / 2.0, if previous_block.is_none() { 0.0 } else { spine_y + ROW_GAP }, size.w, size.h);
                tree.nodes.push(Node { id: id.clone(), kind: kind.into(), frame, is_compact: compact });
                if let Some(previous) = &previous_block {
                    tree.edges.push(Edge::new(previous, &id));
                }
                let mut lowest = frame.max_y();
                let mut top = frame.y;
                for mut child in std::mem::take(&mut slots[slot]) {
                    child.offset(frame.max_x() + steps::SIDE_GAP, top);
                    tree.nodes.append(&mut child.nodes);
                    tree.edges.push(Edge { side: true, ..Edge::new(&id, &child.top) });
                    tree.edges.append(&mut child.edges);
                    lowest = lowest.max(top + child.height);
                    top += child.height + SIBLING_GAP;
                }
                spine_y = lowest;
                previous_block = Some(id);
            }
            tree.height = spine_y;
            tree.leaves = vec![previous_block.unwrap_or_else(|| main_id.clone())];
        } else {
            let mut branches: Vec<Tree> = vec![];
            let order = built.ordered((0..built.agents.len()).collect());
            for ai in order {
                if built.is_top(ai, &indexes)
                    && let Some(t) = built.branch(ai, 0)
                {
                    branches.push(t);
                }
            }
            for ai in 0..built.agents.len() {
                if !built.visited.contains(&ai)
                    && let Some(t) = built.branch(ai, 0)
                {
                    branches.push(t);
                }
            }
            let branch_width = branches.iter().map(|b| b.width).sum::<f64>() + branches.len().saturating_sub(1) as f64 * SIBLING_GAP;
            // The role view draws the 실행 block only where the answer will be (below); every other view right under the request.
            let work_under = if fan_out.is_none() { work_shown } else { None };
            tree_width = [
                main_size.w,
                work_under.map_or(0.0, |w| w.w),
                branch_width,
                if has_result(run) { result_size.w } else { 0.0 },
                if ends_in_work { work_shown.map_or(0.0, |w| w.w) } else { 0.0 },
            ]
            .into_iter()
            .fold(f64::NEG_INFINITY, f64::max);
            let main_node =
                Node::new(main_id.clone(), "request", GraphRect::new((tree_width - main_size.w) / 2.0, 0.0, main_size.w, main_height));
            tree = Tree {
                nodes: vec![main_node],
                edges: vec![],
                width: tree_width,
                height: main_height,
                top: main_id.clone(),
                leaves: vec![main_id.clone()],
            };
            // The block the sub-agents branch from: the 실행 block right under the request, or the request itself.
            let mut parent_id = main_id.clone();
            if let Some(work) = work_under {
                tree.nodes.push(Node::new(
                    work_id.clone(),
                    WORK_KIND,
                    GraphRect::new((tree_width - work.w) / 2.0, main_height + ROW_GAP, work.w, work.h),
                ));
                tree.edges.push(Edge::new(&main_id, &work_id));
                tree.leaves = vec![work_id.clone()];
                tree.height += ROW_GAP + work.h;
                parent_id = work_id.clone();
            }
            if !branches.is_empty() {
                tree.leaves = vec![];
                let mut top = tree.height + ROW_GAP;
                // The main session's delegate line stands between it and the row (FlowDelegation).
                if fan_out.is_some() && !lines::children(lines::MAIN_KEY, run).is_empty() {
                    let line = node_id(run, &format!("{DELEGATE_SUFFIX}{}", lines::MAIN_KEY));
                    let frame = GraphRect::new(
                        (tree_width - lines::DELEGATE_WIDTH) / 2.0,
                        tree.height + DELEGATE_GAP,
                        lines::DELEGATE_WIDTH,
                        lines::DELEGATE_HEIGHT,
                    );
                    tree.nodes.push(Node::new(line.clone(), DELEGATE_KIND, frame));
                    tree.edges.push(Edge::new(&parent_id, &line));
                    parent_id = line;
                    top = frame.max_y() + DELEGATE_ROW_GAP;
                }
                let mut x = (tree_width - branch_width) / 2.0;
                for mut branch in branches {
                    branch.offset(x, top);
                    tree.nodes.append(&mut branch.nodes);
                    tree.edges.push(Edge::new(&parent_id, &branch.top));
                    tree.edges.append(&mut branch.edges);
                    tree.leaves.append(&mut branch.leaves);
                    tree.height = tree.height.max(top + branch.height);
                    x += branch.width + SIBLING_GAP;
                }
            }
        }
        // A plan waiting for an answer goes where the result will: the run is still going, so nothing is below it yet.
        if plan_waits {
            let plan_id = node_id(run, PLAN_SUFFIX);
            let fit = plan_size(options.plan_size.as_ref(), options.viewport, options.zoom);
            plan_fit = Some(fit);
            let block = fit.0;
            if block.w > tree.width {
                tree.offset((block.w - tree.width) / 2.0, 0.0);
                tree.width = block.w;
            }
            tree.nodes.push(Node::new(
                plan_id.clone(),
                "plan",
                GraphRect::new((tree.width - block.w) / 2.0, tree.height + ROW_GAP, block.w, block.h),
            ));
            let leaves = std::mem::take(&mut tree.leaves);
            tree.edges.extend(leaves.iter().map(|l| Edge::joining(l, &plan_id)));
            tree.leaves = vec![plan_id];
            tree.height += ROW_GAP + block.h;
        }
        // The role view: the main session at work, where its answer will come, until it is there.
        if ends_in_work && let Some(at_work) = work_shown {
            tree.nodes.push(Node {
                id: work_id.clone(),
                kind: WORK_KIND.into(),
                frame: GraphRect::new((tree_width - at_work.w) / 2.0, tree.height + ROW_GAP, at_work.w, at_work.h),
                is_compact: work_compact,
            });
            let leaves = std::mem::take(&mut tree.leaves);
            tree.edges.extend(leaves.iter().map(|l| Edge::joining(l, &work_id)));
            tree.leaves = vec![work_id.clone()];
            tree.height += ROW_GAP + at_work.h;
        }
        if has_result(run) {
            let height = result_size.h;
            tree.nodes.push(Node::new(
                result_id.clone(),
                "result",
                GraphRect::new((tree_width - result_size.w) / 2.0, tree.height + ROW_GAP, result_size.w, height),
            ));
            let leaves = std::mem::take(&mut tree.leaves);
            tree.edges.extend(leaves.iter().map(|l| Edge::joining(l, &result_id)));
            tree.leaves = vec![result_id];
            tree.height += ROW_GAP + height;
        }
        trees.push(tree);
    }

    let has_pending = (!running && runs.last().is_none_or(finished)) || !draft.is_empty();
    if has_pending {
        let pending_id = camera::PENDING_NODE_ID;
        let size = sizer.size(pending_id, camera::REQUEST_WIDTH, if expanded.contains(pending_id) { 280.0 } else { 140.0 });
        trees.push(Tree {
            nodes: vec![Node::new(pending_id, "draft", GraphRect::new(0.0, 0.0, size.w, size.h))],
            edges: vec![],
            width: size.w,
            height: size.h,
            top: pending_id.into(),
            leaves: vec![pending_id.into()],
        });
    }

    let mut layout = Layout::default();
    // The first retained tree starts where the first tree always started; the older ones, loaded from the session record, stack upward.
    let split = options.retained_start.min(runs.len());
    let mut tops = vec![0.0; trees.len()];
    let mut y = 24.0;
    for i in split..trees.len() {
        tops[i] = y;
        y += trees[i].height + ROW_GAP;
    }
    let mut above = 24.0;
    for i in (0..split).rev() {
        above -= trees[i].height + ROW_GAP;
        tops[i] = above;
    }
    let mut previous: Vec<String> = vec![];
    for (i, mut tree) in trees.into_iter().enumerate() {
        tree.offset(camera::x(tree.width), tops[i]);
        layout.nodes.append(&mut tree.nodes);
        layout.edges.extend(previous.iter().map(|p| Edge::joining(p, &tree.top)));
        layout.edges.append(&mut tree.edges);
        previous = tree.leaves;
    }
    let tree_count = tops.len();
    let bottom = if split < tree_count { y - ROW_GAP } else { 24.0 - ROW_GAP };
    if options.history {
        let top = if layout.nodes.is_empty() { 24.0 } else { layout.nodes.iter().map(|n| n.frame.y).fold(f64::INFINITY, f64::min) };
        layout.nodes.insert(
            0,
            Node::new(
                HISTORY_NODE_ID,
                "history",
                GraphRect::new(camera::CENTRE_X - HISTORY_WIDTH / 2.0, top - 28.0 - HISTORY_HEIGHT, HISTORY_WIDTH, HISTORY_HEIGHT),
            ),
        );
    }

    let (leading, trailing) = if layout.nodes.is_empty() {
        (camera::x(camera::REQUEST_WIDTH), camera::CENTRE_X + camera::REQUEST_WIDTH / 2.0)
    } else {
        (
            layout.nodes.iter().map(|n| n.frame.x).fold(f64::INFINITY, f64::min),
            layout.nodes.iter().map(|n| n.frame.max_x()).fold(f64::NEG_INFINITY, f64::max),
        )
    };
    layout.origin_x = camera::origin_x(leading);
    let min_y = if layout.nodes.is_empty() { 24.0 } else { layout.nodes.iter().map(|n| n.frame.y).fold(f64::INFINITY, f64::min) };
    layout.origin_y = 0f64.min(min_y - 24.0);
    let max_y = if layout.nodes.is_empty() { 0.0 } else { layout.nodes.iter().map(|n| n.frame.max_y()).fold(f64::NEG_INFINITY, f64::max) };
    layout.size = Size::new(camera::canvas_width(leading, trailing), 188f64.max(bottom.max(max_y) + 24.0 - layout.origin_y));

    if let Some(files_run) = options.result_files_run_id.as_deref()
        && let Some(panel_run) = runs.iter().find(|r| r.id() == files_run)
        && panel_run.status() == "completed"
        && has_result(panel_run)
    {
        let result_node_id = node_id(panel_run, "result");
        if let Some(result_node) = layout.nodes.iter().find(|n| n.kind == "result" && n.id == result_node_id) {
            let frame = GraphRect::new(result_node.frame.max_x() + 16.0, result_node.frame.y, 320.0, result_node.frame.h);
            layout.nodes.push(Node::new(node_id(panel_run, "result-files"), "resultFiles", frame));
            layout.size = Size::new(layout.size.w.max(frame.max_x() + 24.0 - layout.origin_x), layout.size.h);
        }
    }
    let run_index = |run_id: &str| runs.iter().position(|r| r.id() == run_id);
    fn right_of(layout: &Layout, top: f64, height: f64, owner: GraphRect) -> f64 {
        let sharing = layout.nodes.iter().filter(|n| n.frame.y < top + height && n.frame.max_y() > top).map(|n| n.frame.max_x());
        sharing.fold(owner.max_x(), f64::max).max(owner.max_x()) + ATTACHMENT_GAP
    }
    fn grow(layout: &mut Layout, frame: GraphRect) {
        layout.size =
            Size::new(layout.size.w.max(frame.max_x() + 24.0 - layout.origin_x), layout.size.h.max(frame.max_y() + 24.0 - layout.origin_y));
    }
    // The block attachments hang off: the 실행 block (the main agent started them), else the request.
    let anchor = |layout: &Layout, run: &MightyGraphRun| -> Option<GraphRect> {
        let work = node_id(run, WORK_SUFFIX);
        let request = node_id(run, "request");
        (if splits { layout.nodes.iter().find(|n| n.kind == WORK_KIND && n.id == work) } else { None })
            .or_else(|| layout.nodes.iter().find(|n| n.kind == "request" && n.id == request))
            .map(|n| n.frame)
    };
    // A picture preview hangs off the top of the step that produced it, right of every card it would share rows with.
    for gallery in &options.galleries {
        let Some(index) = run_index(&gallery.run_id).filter(|_| gallery.count > 0) else { continue };
        let run = &runs[index];
        let request_step = gallery.step == crate::images::REQUEST_STEP;
        let step_id = node_id(run, if splits && request_step { WORK_SUFFIX } else { &gallery.step });
        let request_id = node_id(run, "request");
        let step = layout
            .nodes
            .iter()
            .find(|n| n.id == step_id && !is_auxiliary(&n.kind))
            .or_else(|| if request_step { layout.nodes.iter().find(|n| n.id == request_id) } else { None })
            .map(|n| n.frame);
        let Some(step) = step else { continue };
        let id = node_id(run, &format!("{IMAGES_SUFFIX}{}", gallery.step));
        if layout.nodes.iter().any(|n| n.id == id) {
            continue;
        }
        let height = images_height(gallery.count, expanded.contains(&id));
        let frame = GraphRect::new(right_of(&layout, step.y, height, step), step.y, IMAGES_WIDTH, height);
        layout.nodes.push(Node::new(id, IMAGES_KIND, frame));
        grow(&mut layout, frame);
    }
    // Execution blocks hang off their request's top (its 실행 block's), stacked, right of every card they would share rows with.
    let mut next_y: HashMap<usize, f64> = HashMap::new();
    for (run_id, key) in &options.executions {
        let Some(index) = run_index(run_id) else { continue };
        let Some(owner) = anchor(&layout, &runs[index]) else { continue };
        let id = node_id(&runs[index], &format!("{EXECUTION_SUFFIX}{key}"));
        if layout.nodes.iter().any(|n| n.id == id) {
            continue;
        }
        let top = next_y.get(&index).copied().unwrap_or(owner.y);
        let height = execution_height(expanded.contains(&id));
        let frame = GraphRect::new(right_of(&layout, top, height, owner), top, EXECUTION_WIDTH, height);
        layout.nodes.push(Node::new(id, EXECUTION_KIND, frame));
        grow(&mut layout, frame);
        next_y.insert(index, frame.max_y() + ATTACHMENT_GAP);
    }
    // Answered plans hang off the same block as well, below its execution blocks.
    for (run_id, record_id) in &options.plan_records {
        let Some(index) = run_index(run_id) else { continue };
        let Some(request) = anchor(&layout, &runs[index]) else { continue };
        let id = node_id(&runs[index], &format!("{PLAN_RECORD_SUFFIX}{record_id}"));
        if layout.nodes.iter().any(|n| n.id == id) {
            continue;
        }
        let top = next_y.get(&index).copied().unwrap_or(request.y);
        // A dragged size wins over folded or opened, as on the other blocks.
        let size = sizer.size(&id, PLAN_RECORD_WIDTH, plan_record_height(expanded.contains(&id)));
        let frame = GraphRect::new(right_of(&layout, top, size.h, request), top, size.w, size.h);
        layout.nodes.push(Node::new(id, "planRecord", frame));
        grow(&mut layout, frame);
        next_y.insert(index, frame.max_y() + ATTACHMENT_GAP);
    }
    layout.fitted_result_id = fitted_result_id(runs, options.viewport, has_shared);
    layout.result_limit = result_limit;
    layout.result_window_fit = auto_fit;
    layout.plan_limit = plan_fit.and_then(|f| f.1);
    layout.plan_window_fit = plan_fit.and_then(|f| f.2);
    if options.viewport.is_some()
        && let Some(bounded) = result_maximum
        && result_cap(bounded, result_limit) != bounded
    {
        layout.viewport_bound_result_id = latest;
    }
    layout
}

/// A diagram block resizes from its corner handle and from a band along each border (C# `ResizeEdges`, `ResizeEdgeRules`,
/// MightyCode.Core/ResizeEdges.cs, a port of macOS ResizeEdges). Coordinates are y-down.
pub mod resize {
    use crate::camera::{GraphRect, Point, Size};

    /// `ResizeEdges`: which sides of a box a drag is moving (flags).
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
    pub struct Edges(pub u8);

    impl Edges {
        pub const NONE: Edges = Edges(0);
        pub const LEFT: Edges = Edges(1);
        pub const RIGHT: Edges = Edges(2);
        pub const TOP: Edges = Edges(4);
        pub const BOTTOM: Edges = Edges(8);
        pub const BOTTOM_RIGHT: Edges = Edges(2 | 8);

        pub fn contains(self, other: Edges) -> bool {
            self.0 & other.0 == other.0
        }
        /// `Horizontal`.
        pub fn horizontal(self) -> bool {
            self.0 & (Self::LEFT.0 | Self::RIGHT.0) != 0
        }
        /// `Vertical`.
        pub fn vertical(self) -> bool {
            self.0 & (Self::TOP.0 | Self::BOTTOM.0) != 0
        }

        /// `Size`: the size after the pointer moved `delta` screen points since the drag began at `initial`.
        pub fn size(self, initial: Size, delta: Point, zoom: f64, minimum: Size, maximum: Size) -> Size {
            let scale = if zoom.is_finite() && zoom > 0.0 { zoom } else { 1.0 };
            let (mut width, mut height) = (initial.w, initial.h);
            if self.contains(Self::RIGHT) {
                width += delta.x / scale;
            } else if self.contains(Self::LEFT) {
                width -= delta.x / scale;
            }
            if self.contains(Self::BOTTOM) {
                height += delta.y / scale;
            } else if self.contains(Self::TOP) {
                height -= delta.y / scale;
            }
            Size::new(maximum.w.min(minimum.w.max(width)), maximum.h.min(minimum.h.max(height)))
        }

        /// `PinnedUnit`: the corner that stays put, as a fraction of the box.
        pub fn pinned_unit(self) -> Point {
            Point::new(if self.contains(Self::LEFT) { 1.0 } else { 0.0 }, if self.contains(Self::TOP) { 1.0 } else { 0.0 })
        }
    }

    impl std::ops::BitOr for Edges {
        type Output = Edges;
        fn bitor(self, other: Edges) -> Edges {
            Edges(self.0 | other.0)
        }
    }

    /// `Band`: how far outside a block's border a press still grabs that side, in screen points at `zoom`.
    pub fn band(zoom: f64) -> f64 {
        4f64.max(6.0 * zoom)
    }
    /// `BandInside`.
    pub const BAND_INSIDE: f64 = 2.0;
    /// `HandleSide`: the corner handle's side on screen at `zoom`.
    pub fn handle_side(zoom: f64) -> f64 {
        16f64.max(22.0 * zoom)
    }

    /// `At`: the sides whose band holds the point; near a corner both.
    pub fn at(point: Point, frame: GraphRect, outside: f64, inside: f64) -> Edges {
        if point.x < frame.x - outside || point.x >= frame.max_x() + outside || point.y < frame.y - outside || point.y >= frame.max_y() + outside {
            return Edges::NONE;
        }
        let band = |distance: f64| distance >= -outside && distance <= inside;
        let mut edges = Edges::NONE;
        if band(point.x - frame.x) {
            edges = edges | Edges::LEFT;
        } else if band(frame.max_x() - point.x) {
            edges = edges | Edges::RIGHT;
        }
        if band(point.y - frame.y) {
            edges = edges | Edges::TOP;
        } else if band(frame.max_y() - point.y) {
            edges = edges | Edges::BOTTOM;
        }
        edges
    }

    /// `Target`: the block and sides a press would resize: the bottom-right handle first, then a band; the block drawn last wins.
    pub fn target(point: Point, frames: &[(String, GraphRect)], zoom: f64) -> Option<(String, GraphRect, Edges)> {
        let side = handle_side(zoom);
        for (id, frame) in frames.iter().rev() {
            let inside = point.x >= frame.x && point.x < frame.max_x() && point.y >= frame.y && point.y < frame.max_y();
            if inside && point.x >= frame.max_x() - side && point.y >= frame.max_y() - side {
                return Some((id.clone(), *frame, Edges::BOTTOM_RIGHT));
            }
            let edges = at(point, *frame, band(zoom), BAND_INSIDE);
            if edges != Edges::NONE {
                return Some((id.clone(), *frame, edges));
            }
        }
        None
    }

    /// `PinnedCamera`: the camera that keeps the pinned corner of a resized block at `anchor`.
    pub fn pinned_camera(anchor: Point, unit: Point, frame: GraphRect, zoom: f64) -> Point {
        Point::new(anchor.x - (frame.x + unit.x * frame.w) * zoom, anchor.y - (frame.y + unit.y * frame.h) * zoom)
    }
}
