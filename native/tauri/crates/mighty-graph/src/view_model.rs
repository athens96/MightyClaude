//! The per-pane Mighty view's decisions (C# `MightyGraphViewModel`, MightyCode.Core/MightyGraphViewModel.cs:8) and each canvas block's
//! copy (`MightyGraphBlock`, `MightyGraphBlockModel`, MightyGraphViewModel.cs:225-502): zoom, eligibility, selection and the wheel, the
//! result files panel, the running indicators, and every block in layout order with its title, state, transcript, capsule and outline.

use std::collections::HashMap;

use mighty_core::graph::{Awaiting, GraphResponseRecord, GraphTokenUsage, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{LogEntry, RunSession};
use mighty_core::model_label::ModelOption;
use mighty_core::shell::Tone;

use crate::activity;
use crate::camera::{GraphRect, Point, Size};
use crate::flow::roles;
use crate::labels;
use crate::layout::{self, Layout, Node};
use crate::node;
use crate::notice;
use crate::support;
use crate::usage_format;

// ── zoom ──

/// `ZoomMin`, `ZoomMax`, `ZoomStep`, `ZoomDefault`.
pub const ZOOM_MIN: f64 = 0.5;
pub const ZOOM_MAX: f64 = 1.5;
pub const ZOOM_STEP: f64 = 0.1;
pub const ZOOM_DEFAULT: f64 = 1.0;

/// `Math.Round(value, 1)` (to even, as .NET rounds by default).
fn round1(value: f64) -> f64 {
    (value * 10.0).round_ties_even() / 10.0
}

/// `ZoomLevels`: every zoom level from min to max.
pub fn zoom_levels() -> Vec<f64> {
    let mut levels = vec![];
    let mut z = ZOOM_MIN;
    while z <= ZOOM_MAX + 0.0001 {
        levels.push(round1(z));
        z += ZOOM_STEP;
    }
    levels
}

/// `ZoomIn`.
pub fn zoom_in(current: f64) -> f64 {
    ZOOM_MAX.min(round1(current + ZOOM_STEP))
}

/// `ZoomOut`.
pub fn zoom_out(current: f64) -> f64 {
    ZOOM_MIN.max(round1(current - ZOOM_STEP))
}

/// `ZoomInDisabled`, `ZoomOutDisabled`.
pub fn zoom_in_disabled(zoom: f64) -> bool {
    zoom >= ZOOM_MAX
}
pub fn zoom_out_disabled(zoom: f64) -> bool {
    zoom <= ZOOM_MIN
}

/// `ZoomAnchored`: what stood in the middle of the viewport stays there.
pub fn zoom_anchored(pan: Point, viewport: Size, previous: f64, zoom: f64) -> Point {
    if !previous.is_finite() || previous <= 0.0 || !zoom.is_finite() || zoom <= 0.0 {
        return pan;
    }
    let (cx, cy) = (viewport.w / 2.0, viewport.h / 2.0);
    Point::new(cx - (cx - pan.x) * zoom / previous, cy - (cy - pan.y) * zoom / previous)
}

/// `ZoomLabel`: "100%".
pub fn zoom_label(zoom: f64) -> String {
    format!("{}%", (zoom * 100.0).round_ties_even() as i64)
}

// ── pane eligibility ──

/// `SupportsMighty`: an agent pane of a provider whose runs produce a graph.
pub fn supports_mighty(session: &RunSession) -> bool {
    session.kind() == "claude" && support::PROVIDERS.contains(&session.provider())
}

/// `ViewMode`: "mighty" for an agent pane that has the Mighty view, none for any other.
pub fn view_mode(kind: &str, provider: &str) -> Option<&'static str> {
    (kind == "claude" && support::PROVIDERS.contains(&provider)).then_some("mighty")
}

// ── selection and the wheel ──

/// `ApplySelection`: a click selects what it hit, the canvas clears.
pub fn apply_selection(_current: Option<&str>, clicked: Option<&str>) -> Option<String> {
    clicked.map(str::to_string)
}

/// `WheelScrollsBlock`.
pub fn wheel_scrolls_block(selected: Option<&str>) -> bool {
    selected.is_some()
}

/// `WheelScrollTarget`: the block under the pointer, when it is the selected one or an attachment beside the flow.
pub fn wheel_scroll_target(selected: Option<&str>, hovered: Option<&str>, hovered_is_auxiliary: bool) -> Option<String> {
    hovered.filter(|h| Some(*h) == selected || hovered_is_auxiliary).map(str::to_string)
}

/// `WheelPan`: a vertical wheel moves the camera up and down; a horizontal wheel or Shift with the wheel, left and right.
pub fn wheel_pan(delta: f64, horizontal_wheel: bool, shift: bool) -> Point {
    if horizontal_wheel {
        Point::new(-delta, 0.0)
    } else if shift {
        Point::new(delta, 0.0)
    } else {
        Point::new(0.0, delta)
    }
}

/// `ResultFilesPanel`: which result's file list is open beside its card; one per graph.
#[derive(Debug, Clone, Default)]
pub struct ResultFilesPanel {
    selected_run_id: Option<String>,
    last_auto_run_id: Option<String>,
}

impl ResultFilesPanel {
    pub fn new() -> Self {
        Self::default()
    }
    /// `SelectedRunID`.
    pub fn selected_run_id(&self) -> Option<&str> {
        self.selected_run_id.as_deref()
    }
    /// `Toggle`: a result that names no file has no toggle.
    pub fn toggle(&mut self, run_id: &str, has_files: bool) {
        if has_files {
            self.selected_run_id = if self.selected_run_id.as_deref() == Some(run_id) { None } else { Some(run_id.into()) };
        }
    }
    /// `Close`.
    pub fn close(&mut self) {
        self.selected_run_id = None;
    }
    /// `Publish`: the newest result that names files opens by itself, once.
    pub fn publish(&mut self, sources: &[(String, bool)]) {
        if let Some(selected) = &self.selected_run_id
            && !sources.iter().any(|(id, has)| id == selected && *has)
        {
            self.selected_run_id = None;
        }
        if let Some((id, _)) = sources.iter().rev().find(|(_, has)| *has)
            && self.last_auto_run_id.as_deref() != Some(id)
        {
            self.selected_run_id = Some(id.clone());
            self.last_auto_run_id = Some(id.clone());
        }
    }
}

/// `BlockIndicator`: "animating", "static", "waiting" or "none".
pub fn block_indicator(status: &str, animations_enabled: bool) -> &'static str {
    match status {
        "running" if animations_enabled => "animating",
        "running" => "static",
        "waiting" => "waiting",
        _ => "none",
    }
}

/// The locale keys the view's controls read.
pub const LOCALE_KEY_MIGHTY: &str = "graph.view.mighty";
pub const LOCALE_KEY_ZOOM_OUT: &str = "graph.zoom.out";
pub const LOCALE_KEY_ZOOM_RESET: &str = "graph.zoom.reset";
pub const LOCALE_KEY_ZOOM_IN: &str = "graph.zoom.in";
pub const LOCALE_KEY_RESULT_FILES_TITLE: &str = "graph.resultFiles.title";
pub const LOCALE_KEY_RESULT_FILES_CLOSE: &str = "graph.resultFiles.closeButton";
pub const LOCALE_KEY_RESULT_FILES_OPEN: &str = "graph.resultFiles.openButton";
pub const LOCALE_KEY_RESULT_FILES_COUNT: &str = "graph.resultFiles.countLabel";
pub const LOCALE_KEY_BLOCK_SCROLLING: &str = "graph.block.scrolling";
pub const LOCALE_KEY_RESULT_FIT_TO_WINDOW: &str = "graph.result.fitToWindow";

// ── the blocks ──

/// `MightyGraphBlock`: one canvas block as the Mighty view draws it.
#[derive(Debug, Clone, PartialEq)]
pub struct MightyGraphBlock {
    pub id: String,
    pub kind: String,
    pub frame: GraphRect,
    pub title: String,
    pub state: String,
    pub request: String,
    pub entries: Vec<LogEntry>,
    pub capsule: Option<String>,
    pub capsule_help: String,
    pub indicator: String,
    pub result_files_run_id: Option<String>,
    pub outline: String,
    pub status: String,
    pub record_id: Option<String>,
    pub awaiting: Option<Awaiting>,
    pub so_far: bool,
}

impl MightyGraphBlock {
    fn new(node: &Node, kind: &str, title: String, state: String, request: String) -> Self {
        MightyGraphBlock {
            id: node.id.clone(),
            kind: kind.into(),
            frame: node.frame,
            title,
            state,
            request,
            entries: vec![],
            capsule: None,
            capsule_help: String::new(),
            indicator: "none".into(),
            result_files_run_id: None,
            outline: activity::NONE.into(),
            status: String::new(),
            record_id: None,
            awaiting: None,
            so_far: false,
        }
    }
}

/// `StateLabel`: the short state word under a block header.
pub fn state_label(status: &str, locale: &Locale) -> String {
    locale.get(match status {
        "completed" => "graph.state.completed",
        "error" | "failed" => "graph.state.error",
        "stopped" | "cancelled" | "interrupted" => "graph.state.stopped",
        "waiting" => "graph.state.waiting",
        "starting" | "queued" => "graph.state.starting",
        _ => "graph.state.running",
    })
}

/// `RunStateLabel`: a request whose turn ended while its background tasks run says it waits on them.
pub fn run_state_label(run: &MightyGraphRun, locale: &Locale) -> String {
    match support::background_waiting(run) {
        count if count > 0 => locale.get_with("plan.background.status", &[("count", &count.to_string())]),
        _ => state_label(run.status(), locale),
    }
}

/// `ExecutionTitle`: "실행 · 요청 N".
pub fn execution_title(ordinal: i32, locale: &Locale) -> String {
    locale.get_with("graph.block.executionOf", &[("n", &ordinal.to_string())])
}

/// `RequestTitle`: "요청 N · Claude".
pub fn request_title(ordinal: i32, provider_label: &str, locale: &Locale) -> String {
    locale.get_with("graph.block.requestTitle", &[("ordinal", &ordinal.to_string()), ("provider", provider_label)])
}

/// `ProviderMark.MarkedProvider`.
fn marked_provider(provider: &str) -> Option<&str> {
    mighty_core::wire::PROVIDERS.contains(&provider).then_some(provider)
}

/// `TitleProvider`: the provider whose mark goes before the name a request block's title ends with.
pub fn title_provider<'a>(block: &MightyGraphBlock, pane_provider: &'a str) -> Option<&'a str> {
    let marked = marked_provider(pane_provider)?;
    (block.kind == "request" && block.title.ends_with(labels::provider_label(marked))).then_some(marked)
}

/// `MightyTimeline.Status`: a status in the block buckets.
pub fn timeline_status(value: &str) -> &'static str {
    match value {
        "completed" => "completed",
        "error" | "failed" => "error",
        "stopped" | "cancelled" | "interrupted" => "stopped",
        "waiting" => "waiting",
        _ => "running",
    }
}

/// `Tone`: the tone a block's status pill, result strip and incoming edge take; idle for a block with no status.
pub fn tone(block: &MightyGraphBlock) -> Tone {
    if block.status.is_empty() { Tone::Idle } else { Tone::of(timeline_status(&block.status)) }
}

/// `StripTone`: idle while the result is the result so far, else its tone.
pub fn strip_tone(block: &MightyGraphBlock) -> Tone {
    if block.so_far { Tone::Idle } else { tone(block) }
}

/// `ResultTitle`: how the request ended, 알림 대기 중 while it awaits notices, else the result so far while its background work runs.
pub fn result_title(run_status: &str, waiting: i32, awaiting: bool, locale: &Locale) -> String {
    match run_status {
        "error" | "failed" => locale.get("graph.block.resultError"),
        "stopped" | "cancelled" | "interrupted" => locale.get("graph.block.resultStopped"),
        _ if awaiting => locale.get("graph.block.awaitingNotice"),
        _ if waiting > 0 => locale.get_with("graph.block.resultSoFar", &[("count", &waiting.to_string())]),
        _ => locale.get("graph.block.result"),
    }
}

/// `ResultState`: the result block's own state.
pub fn result_state(run_status: &str) -> &'static str {
    match run_status {
        "error" | "failed" => "error",
        "stopped" | "cancelled" | "interrupted" => "stopped",
        _ => "completed",
    }
}

/// `DraftTitle`, `DraftState`.
pub fn draft_title(any_runs: bool, locale: &Locale) -> String {
    locale.get(if any_runs { "graph.block.nextRequest" } else { "graph.block.firstRequest" })
}
pub fn draft_state(draft: &str, locale: &Locale) -> String {
    locale.get(if draft.is_empty() { "graph.state.draftIdle" } else { "graph.state.draftTyping" })
}

fn records(records: &Option<Vec<Option<GraphResponseRecord>>>) -> Vec<GraphResponseRecord> {
    records.iter().flatten().flatten().cloned().collect()
}

fn help(
    usage: Option<GraphTokenUsage>,
    records: &[GraphResponseRecord],
    usage_label: &str,
    catalog: Option<&[ModelOption]>,
    locale: &Locale,
) -> String {
    if records.is_empty() {
        usage.map_or(String::new(), |u| format!("{usage_label} · {}", node::detail(&u, locale)))
    } else {
        usage_format::block_capsule_help(records, catalog, true, locale)
    }
}

fn visible(entries: &[LogEntry]) -> Vec<LogEntry> {
    entries.iter().filter(|e| e.kind() != "user").cloned().collect()
}

/// The node kinds `Locate` finds by their whole id, with the suffix each run's id takes.
const EXACT: [(&str, &str); 5] = [
    ("request", "request"),
    (layout::WORK_KIND, layout::WORK_SUFFIX),
    ("result", "result"),
    ("resultFiles", "result-files"),
    ("plan", layout::PLAN_SUFFIX),
];

/// The node kinds `Locate` finds by the start of their id, the rest being the record (a plan record, a step, an execution's key).
const PREFIXED: [(&str, &str); 3] = [
    ("planRecord", layout::PLAN_RECORD_SUFFIX),
    (layout::IMAGES_KIND, layout::IMAGES_SUFFIX),
    (layout::EXECUTION_KIND, layout::EXECUTION_SUFFIX),
];

/// One node a whole id names: its kind, its run and, for an agent's node, the agent's id.
type Found<'a> = (&'static str, usize, Option<&'a str>);

/// `Locate` for every node of a layout: each run's node ids made once (C# makes them again for every node, which a pane of many
/// requests pays for with every push), each id answering the first run in order, as `Locate`'s walk does.
struct Locator<'a> {
    /// The nodes each whole id names, in the runs' order.
    exact: HashMap<String, Vec<Found<'a>>>,
    /// Each run's prefixes, in [`PREFIXED`]'s order.
    prefixes: Vec<[String; 3]>,
}

impl<'a> Locator<'a> {
    fn new(runs: &'a [MightyGraphRun]) -> Self {
        let mut exact: HashMap<String, Vec<Found<'a>>> = HashMap::new();
        let mut prefixes = Vec::with_capacity(runs.len());
        for (i, run) in runs.iter().enumerate() {
            for (kind, suffix) in EXACT {
                exact.entry(support::node_id(run, suffix)).or_default().push((kind, i, None));
            }
            for agent in run.agents() {
                exact.entry(support::node_id(run, &format!("agent:{}", agent.id()))).or_default().push(("agent", i, Some(agent.id())));
            }
            prefixes.push(PREFIXED.map(|(_, suffix)| support::node_id(run, suffix)));
        }
        Locator { exact, prefixes }
    }

    /// `Locate`: which run (and for an agent node which agent, for an attachment its record) a layout node belongs to.
    fn locate(&self, node: &Node) -> (Option<usize>, Option<String>) {
        if let Some(slot) = PREFIXED.iter().position(|(kind, _)| node.kind == *kind) {
            return self
                .prefixes
                .iter()
                .enumerate()
                .find(|(_, prefixes)| node.id.starts_with(&prefixes[slot]))
                .map_or((None, None), |(i, prefixes)| (Some(i), Some(node.id[prefixes[slot].len()..].to_string())));
        }
        self.exact
            .get(&node.id)
            .and_then(|found| found.iter().find(|(kind, ..)| node.kind == *kind))
            .map_or((None, None), |(_, i, agent)| (Some(*i), agent.map(str::to_string)))
    }
}

/// `Blocks`: every canvas block, in layout order, for one pane. `splits_execution` says a request block holds its input alone (default:
/// the layout has a 실행 block).
#[allow(clippy::too_many_arguments, reason = "C# MightyGraphBlockModel.Blocks's parameters, and the locale")]
pub fn blocks(
    layout: &Layout,
    runs: &[MightyGraphRun],
    draft: &str,
    provider_label: &str,
    animations_enabled: bool,
    catalog: Option<&[ModelOption]>,
    splits_execution: Option<bool>,
    locale: &Locale,
) -> Vec<MightyGraphBlock> {
    make(layout, runs, draft, provider_label, animations_enabled, catalog, splits_execution, true, locale)
}

/// [`blocks`] without each block's transcript (`entries` left empty): what the host's `mighty` push sends, which carries no entries,
/// so a large pane's push copies none of them.
#[allow(clippy::too_many_arguments, reason = "C# MightyGraphBlockModel.Blocks's parameters, and the locale")]
pub fn block_heads(
    layout: &Layout,
    runs: &[MightyGraphRun],
    draft: &str,
    provider_label: &str,
    animations_enabled: bool,
    catalog: Option<&[ModelOption]>,
    splits_execution: Option<bool>,
    locale: &Locale,
) -> Vec<MightyGraphBlock> {
    make(layout, runs, draft, provider_label, animations_enabled, catalog, splits_execution, false, locale)
}

#[allow(clippy::too_many_arguments, reason = "C# MightyGraphBlockModel.Blocks's parameters, whether entries are copied, and the locale")]
fn make(
    layout: &Layout,
    runs: &[MightyGraphRun],
    draft: &str,
    provider_label: &str,
    animations_enabled: bool,
    catalog: Option<&[ModelOption]>,
    splits_execution: Option<bool>,
    with_entries: bool,
    locale: &Locale,
) -> Vec<MightyGraphBlock> {
    let runs = support::with_background_waiting(runs);
    let runs: &[MightyGraphRun] = &runs;
    let locator = Locator::new(runs);
    let visible = |entries: &[LogEntry]| if with_entries { visible(entries) } else { vec![] };
    let split = splits_execution.unwrap_or_else(|| layout.nodes.iter().any(|n| n.kind == layout::WORK_KIND));
    // Request numbers: a notice run takes none and is titled after the work it reports on.
    let ordinals = support::request_ordinals(runs);
    let title = |run: &MightyGraphRun, index: usize| {
        if run.is_notice() { notice::heading(run, locale) } else { request_title(ordinals[index], provider_label, locale) }
    };
    let usage_label = locale.get("graph.block.blockUsageLabel");
    let mut result = vec![];
    for node in &layout.nodes {
        let (index, agent_id) = locator.locate(node);
        let run = index.map(|i| &runs[i]);
        match (node.kind.as_str(), run) {
            ("draft", _) => {
                let mut block =
                    MightyGraphBlock::new(node, "draft", draft_title(!runs.is_empty(), locale), draft_state(draft, locale), draft.into());
                block.indicator = "none".into();
                result.push(block);
            }
            // The request alone, with no status of its own: the 실행 block under it runs and settles.
            ("request", Some(run)) if split => {
                result.push(MightyGraphBlock::new(node, "request", title(run, index.unwrap_or(0)), String::new(), run.input().into()));
            }
            (layout::WORK_KIND, Some(run)) | ("request", Some(run)) => {
                let index = index.unwrap_or(0);
                let records = records(&run.response_records);
                let work = node.kind == layout::WORK_KIND;
                let mut block = MightyGraphBlock::new(
                    node,
                    if work { layout::WORK_KIND } else { "request" },
                    if work { notice::execution_title(run.is_notice(), ordinals[index], locale) } else { title(run, index) },
                    run_state_label(run, locale),
                    if work { String::new() } else { run.input().into() },
                );
                block.entries = visible(run.root_entries());
                block.capsule = usage_format::block_capsule(run.usage, &records, run.node_model_label.as_deref(), catalog, true, locale);
                block.capsule_help = help(run.usage, &records, &usage_label, catalog, locale);
                block.indicator = block_indicator(run.status(), animations_enabled).into();
                block.outline = activity::outline(run.status(), animations_enabled).into();
                block.status = run.status().into();
                result.push(block);
            }
            ("agent", Some(run)) => {
                let Some(agent) = run.agents().iter().find(|a| Some(a.id()) == agent_id.as_deref()) else { continue };
                // Before its own responses name the model that answered, a sub-agent shows the model the CLI resolved, else the alias it asked for.
                let records = records(&agent.response_records);
                let label = roles::subagent(agent, None, locale).and_then(|w| w.model_label);
                let mut block = MightyGraphBlock::new(
                    node,
                    support::block_kind(agent),
                    support::block_title(agent, locale),
                    state_label(agent.status(), locale),
                    support::shown_input(agent).into(),
                );
                block.entries = visible(agent.entries());
                block.capsule = usage_format::block_capsule(agent.usage, &records, label.as_deref(), catalog, true, locale);
                block.capsule_help = help(agent.usage, &records, &usage_label, catalog, locale);
                block.indicator = block_indicator(agent.status(), animations_enabled).into();
                block.outline = activity::outline(agent.status(), animations_enabled).into();
                block.status = agent.status().into();
                result.push(block);
            }
            ("result", Some(run)) => {
                // While the request awaits the notices of background work it started, its result is the 알림 대기 block.
                let awaiting = notice::awaiting_notice(run, runs, locale);
                let waiting = support::background_waiting(run);
                let total = support::total_usage(run);
                let state = if awaiting.is_none() { state_label(result_state(run.status()), locale) } else { notice::state_word(locale) };
                let mut block = MightyGraphBlock::new(
                    node,
                    "result",
                    result_title(run.status(), waiting, awaiting.is_some(), locale),
                    state,
                    String::new(),
                );
                block.entries = visible(run.result_entries());
                block.capsule = usage_format::block_capsule(total, &[], None, catalog, true, locale);
                block.capsule_help = help(total, &[], &locale.get("graph.block.totalUsageLabel"), catalog, locale);
                block.result_files_run_id = (run.status() == "completed").then(|| run.id().to_string());
                block.outline =
                    if awaiting.is_none() { activity::NONE.into() } else { activity::outline("running", animations_enabled).into() };
                block.status = if awaiting.is_none() { result_state(run.status()).into() } else { "waiting".into() };
                // The result so far is neutral, not the done strip.
                block.so_far = awaiting.is_none() && result_state(run.status()) == "completed" && waiting > 0;
                block.awaiting = awaiting;
                result.push(block);
            }
            ("plan", Some(_)) => {
                let mut block =
                    MightyGraphBlock::new(node, "plan", locale.get("plan.card.title"), state_label("waiting", locale), String::new());
                block.status = "waiting".into();
                result.push(block);
            }
            ("planRecord", Some(_)) if agent_id.is_some() => {
                let mut block = MightyGraphBlock::new(node, "planRecord", locale.get("plan.card.title"), String::new(), String::new());
                block.record_id = agent_id;
                result.push(block);
            }
            ("resultFiles", Some(run)) => {
                let mut block =
                    MightyGraphBlock::new(node, "resultFiles", locale.get("graph.resultFiles.title"), String::new(), String::new());
                block.result_files_run_id = Some(run.id().into());
                result.push(block);
            }
            // A step's picture preview and a background execution's block: the step, or the execution's key, in the record slot.
            (layout::IMAGES_KIND, Some(_)) if agent_id.is_some() => {
                let mut block =
                    MightyGraphBlock::new(node, layout::IMAGES_KIND, locale.get("graph.images.title"), String::new(), String::new());
                block.record_id = agent_id;
                result.push(block);
            }
            (layout::EXECUTION_KIND, Some(_)) if agent_id.is_some() => {
                let mut block =
                    MightyGraphBlock::new(node, layout::EXECUTION_KIND, locale.get("graph.ouroboros.title"), String::new(), String::new());
                block.record_id = agent_id;
                result.push(block);
            }
            _ => {}
        }
    }
    result
}

// ── toolbar ──

/// `ToolbarSummary`: the pane total line in the graph toolbar.
pub fn toolbar_summary(runs: &[MightyGraphRun], locale: &Locale) -> String {
    // Only steers a person sent are counted; a delegation or task notice is not.
    let kind = |k: &str| -> usize {
        runs.iter().map(|r| r.agents().iter().filter(|a| support::block_kind(a) == k && !support::is_notice(a)).count()).sum()
    };
    let n = |v: usize| v.to_string();
    let mut parts = vec![
        locale.get_with("graph.header.requests", &[("n", &n(runs.len()))]),
        locale.get_with("graph.header.agents", &[("n", &n(kind("agent")))]),
    ];
    for (k, key) in [
        ("question", "graph.header.questions"),
        ("task", "graph.header.tasks"),
        ("steer", "graph.header.steers"),
        ("compact", "graph.header.compactions"),
    ] {
        if kind(k) > 0 {
            parts.push(locale.get_with(key, &[("n", &n(kind(k)))]));
        }
    }
    let tokens = total(runs);
    if !node::is_empty(&tokens) {
        parts.push(node::summary(&tokens, locale));
    }
    parts.join(" · ")
}

/// `ToolbarHelp`: the tooltip on the toolbar total; empty when nothing was counted.
pub fn toolbar_help(runs: &[MightyGraphRun], locale: &Locale) -> String {
    let tokens = total(runs);
    if node::is_empty(&tokens) {
        String::new()
    } else {
        locale.get_with("graph.header.totalHelp", &[("detail", &node::detail(&tokens, locale))])
    }
}

fn total(runs: &[MightyGraphRun]) -> GraphTokenUsage {
    runs.iter().fold(GraphTokenUsage::default(), |sum, run| node::add(sum, support::total_usage(run).unwrap_or_default()))
}

/// `FilesFor`'s texts: the result entries and the final answer a run's result files are read from (`ResultFiles.In` reads them).
pub fn result_texts(run: &MightyGraphRun) -> Vec<String> {
    let mut texts: Vec<String> = run.result_entries().iter().map(|e| e.text().to_string()).collect();
    if let Some(answer) = run.final_output().filter(|f| !f.is_empty()) {
        texts.push(answer.into());
    }
    texts
}

/// `LatestCompletedRun`: the newest run whose result is complete.
pub fn latest_completed_run(runs: &[MightyGraphRun]) -> Option<&MightyGraphRun> {
    runs.iter().rev().find(|r| r.status() == "completed" && support::finished(r))
}
