//! The Mighty graph drawn as its structure, the 파이프라인 view (C# `MightyStructure`, MightyCode.Core/MightyStructure.cs; macOS
//! MightyStructure.swift): a styled pane's requests stand under the phase each belongs to, a free pane's as one request → run → result
//! row each, and under every request its sub-agent trees as clusters. The view shows it one board at a time (`board`); the phone reads
//! the shape as it is (`mobile`). It reads the runs the diagram and the timeline draw and stores nothing.
//!
//! A style (the frozen style engine, C# MightyCode.Core/Styles, ported in a later stage) is read through [`StructureStyle`]: only what
//! the structure reads of it.

pub mod board;
pub mod fold;
pub mod follow;
pub mod mobile;
pub mod pan;
pub mod tone;
pub mod wheel;

use std::collections::{HashMap, HashSet};
use std::hash::{DefaultHasher, Hash, Hasher};
use std::sync::Arc;

use mighty_core::graph::{MightyGraphRun, terminal};
use mighty_core::locale::Locale;
use mighty_core::model::{LogEntry, PlanRecord};
use mighty_core::normalize::{activity_clean, prefix_utf8};
use mighty_core::wire;
use serde_json::Value;
use unicode_segmentation::UnicodeSegmentation;

use crate::flow::roles;
use crate::notice;
use crate::support;
use crate::timeline;

/// `MightyStructure.Step`: where one phase, or one step of a free request, stands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Step {
    Done,
    Current,
    Pending,
    Error,
    Stopped,
}

/// `MightyStructure.Word`: a step as the phone is sent it.
pub fn word(step: Step) -> &'static str {
    match step {
        Step::Done => "done",
        Step::Current => "current",
        Step::Pending => "pending",
        Step::Error => "error",
        Step::Stopped => "stopped",
    }
}

/// C# `StylePhase(Id, Title, Order)` (Styles/StyleManifest.cs:36): one phase of a style.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct StylePhase {
    pub id: String,
    pub title: String,
    pub order: i32,
}

/// What the structure reads of a style (C# `StyleEvaluator` and its `StyleManifest`): the frozen style engine implements it.
pub trait StructureStyle {
    /// `StyleManifest.Id`.
    fn id(&self) -> &str;
    /// `StyleManifest.Phases`, in the manifest's order.
    fn phases(&self) -> &[StylePhase];
    /// The style's phase rule is `planState` (§1.17): the pane's plan stage, not its prompts, moves its phase.
    fn plan_state(&self) -> bool;
    /// `StyleEvaluator.CurrentPhase([], null, planStage)`: the phase of an empty history — a plan-state style's phase for `plan_stage`
    /// (planning when none), any other style's default phase; none for a style without a phase rule.
    fn current_phase(&self, plan_stage: Option<&str>) -> Option<&StylePhase>;
    /// The phase id a prompt names, as `StyleEvaluator.CurrentPhase` reads one: the recognised action's phase (none for a phase-less
    /// action), else the phase of the recognised name's alias. Not checked against `phases`.
    fn prompt_phase(&self, prompt: &str) -> Option<String>;
}

/// `MightyStructure.Excerpt(Text, Preview)`: a text a card shows in place, up to its cap, and the first lines a folded card shows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Excerpt {
    pub text: String,
    pub preview: String,
}

impl Excerpt {
    /// `Cut`: the preview leaves some of the text out.
    pub fn cut(&self) -> bool {
        self.preview != self.text
    }
    /// `Folds`: a folded card of `lines` lines may leave some of the text out.
    pub fn folds(&self, lines: usize, characters_per_line: usize) -> bool {
        self.cut() || visual_lines(&self.preview, characters_per_line) > lines
    }
}

/// `MightyStructure.ActivityLine(Id, Kind, State, Text)`: one tool call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ActivityLine {
    pub id: String,
    pub kind: String,
    pub state: String,
    pub text: String,
}

/// `MightyStructure.Node`: one block of a cluster, flattened depth-first; `node_id` is the diagram node it stands for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Node {
    pub agent_id: String,
    pub agent_index: usize,
    pub node_id: String,
    pub depth: usize,
    pub title: String,
    pub status: String,
    pub kind: String,
    pub prompt: Option<Excerpt>,
    pub output: Option<Excerpt>,
    pub activity: Vec<ActivityLine>,
    pub model: Option<String>,
    pub background: bool,
    pub notice_reply: Option<Excerpt>,
    pub agent_type: Option<String>,
}

/// `MightyStructure.Cluster(Nodes)`: one root sub-agent's tree, the root first.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Cluster {
    pub nodes: Vec<Node>,
}

impl Cluster {
    pub fn id(&self) -> &str {
        self.nodes.first().map_or("", |n| n.node_id.as_str())
    }
}

/// `MightyStructure.Tally(Agents, Tools)`: what the request holds now.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Tally {
    pub agents: usize,
    pub tools: usize,
}

/// `MightyStructure.Item`: one request as a card (MightyStructure.cs:67).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Item {
    pub run_index: usize,
    pub run_id: String,
    pub ordinal: i32,
    pub node_id: String,
    pub execution_node_id: String,
    pub input: String,
    pub status: String,
    pub finished: bool,
    pub result: Option<String>,
    pub request: Option<Excerpt>,
    pub message: Option<Excerpt>,
    pub activity: Vec<ActivityLine>,
    pub answer: Option<Excerpt>,
    pub tally: Tally,
    pub clusters: Vec<Cluster>,
    pub notice_run: bool,
    pub quiet: bool,
    pub notice_title: Option<String>,
    pub background_waiting: i32,
    pub awaiting: bool,
}

/// `MightyStructure.Column(Phase, Step, Items)`: one phase of a styled pane and its requests, oldest first.
#[derive(Debug, Clone, PartialEq)]
pub struct Column {
    pub phase: StylePhase,
    pub step: Step,
    pub items: Vec<Arc<Item>>,
}

impl Column {
    pub fn id(&self) -> &str {
        &self.phase.id
    }
}

/// `MightyStructure.Row(Item, Request, Execution, Result)`: one request of a free pane.
#[derive(Debug, Clone, PartialEq)]
pub struct Row {
    pub item: Arc<Item>,
    pub request: Step,
    pub execution: Step,
    pub result: Step,
}

/// `MightyStructure.Shape(Columns, Rows)`: a style with phases is one column per phase; else one row per request.
#[derive(Debug, Clone, PartialEq)]
pub struct Shape {
    pub columns: Option<Vec<Column>>,
    pub rows: Option<Vec<Row>>,
}

impl Shape {
    pub fn phases(columns: Vec<Column>) -> Shape {
        Shape { columns: Some(columns), rows: None }
    }
    pub fn free(rows: Vec<Row>) -> Shape {
        Shape { columns: None, rows: Some(rows) }
    }
    /// `Items`: every card the shape holds, column by column or row by row.
    pub fn items(&self) -> Vec<&Arc<Item>> {
        match &self.columns {
            Some(columns) => columns.iter().flat_map(|c| c.items.iter()).collect(),
            None => self.rows.iter().flatten().map(|r| &r.item).collect(),
        }
    }
}

/// What `make` reads of the pane besides its runs (the C# parameters of `Make`, `Signature` and `MobileStructureCache.Structure`):
/// its style and phase, whether the next request starts anew, its answered plans and the run whose plan waits for an answer.
#[derive(Clone, Copy, Default)]
pub struct Inputs<'a> {
    pub style: Option<&'a dyn StructureStyle>,
    pub current_phase: Option<&'a StylePhase>,
    pub starting_new: bool,
    pub plan_history: Option<&'a [PlanRecord]>,
    pub pending_plan_run_id: Option<&'a str>,
}

/// One block's part of a [`Signature`] (C# `Signature.Block`). `notice`: Claude's reply to a task's notice, which can land with no new
/// record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SignatureBlock {
    id: String,
    title: String,
    status: String,
    parent_id: Option<String>,
    kind: Option<String>,
    input: usize,
    entries: usize,
    tail: u64,
    records: usize,
    notice: usize,
}

/// One request's part of a [`Signature`] (C# `Signature.Request`), which is all its card reads ([`ItemCache`] keys on it).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SignatureRequest {
    id: String,
    status: String,
    source_run_id: Option<String>,
    input: usize,
    root_entries: usize,
    root_tail: u64,
    result_entries: usize,
    result: usize,
    final_output: Option<usize>,
    notice_title: Option<String>,
    background_waiting: i32,
    awaiting: bool,
    blocks: Vec<SignatureBlock>,
}

/// `MightyStructure.Signature` (MightyStructure.cs:107): what `make` reads of the pane, cheap to compare. A view keeps the shape it built
/// while this stays equal. Text is followed by its length, records by their count and a digest of their tail, a block's title and status
/// as they are; the app's language is part of it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Signature {
    requests: Vec<SignatureRequest>,
    phases: Vec<String>,
    plans: Vec<String>,
    recognition: Option<String>,
    current_phase: Option<String>,
    pending_plan_run_id: Option<String>,
    language: &'static str,
    starting_new: bool,
}

fn bytes(text: Option<&str>) -> usize {
    text.map_or(0, str::len)
}

impl Signature {
    pub fn new(runs: &[MightyGraphRun], inputs: &Inputs, locale: &Locale) -> Signature {
        let counted = support::with_background_waiting(runs);
        let requests = counted
            .iter()
            .map(|run| SignatureRequest {
                id: run.id().to_string(),
                status: run.status().to_string(),
                source_run_id: run.source_run_id.clone(),
                input: bytes(run.input.as_deref()),
                root_entries: run.root_entries().len(),
                root_tail: tail(run.root_entries()),
                result_entries: run.result_entries().len(),
                result: bytes(run.result_entries().last().and_then(|e| e.text.as_deref())),
                final_output: run.final_output.as_deref().map(str::len),
                notice_title: run.notice_title.clone(),
                background_waiting: support::background_waiting(run),
                awaiting: notice::awaiting_notice(run, runs, locale).is_some(),
                blocks: run
                    .agents()
                    .iter()
                    .map(|a| SignatureBlock {
                        id: a.id().to_string(),
                        title: a.title().to_string(),
                        status: a.status().to_string(),
                        parent_id: a.parent_id.clone(),
                        kind: a.kind.clone(),
                        input: bytes(a.input.as_deref()),
                        entries: a.entries().len(),
                        tail: tail(a.entries()),
                        records: a.response_records.as_ref().map_or(0, Vec::len),
                        notice: bytes(a.notice_reply.as_deref()),
                    })
                    .collect(),
            })
            .collect();
        Signature {
            requests,
            phases: inputs.style.map(|s| ordered(s).into_iter().map(|p| p.id.clone()).collect()).unwrap_or_default(),
            recognition: inputs.style.map(|s| s.id().to_string()),
            current_phase: inputs.current_phase.map(|p| p.id.clone()),
            starting_new: inputs.starting_new,
            plans: inputs
                .plan_history
                .unwrap_or(&[])
                .iter()
                .map(|r| {
                    [r.id.as_deref(), r.outcome.as_deref(), r.graph_run_id.as_deref(), r.run_id.as_deref()]
                        .map(|p| p.unwrap_or(""))
                        .join("\u{1f}")
                })
                .collect(),
            pending_plan_run_id: inputs.pending_plan_run_id.map(str::to_string),
            language: locale.language(),
        }
    }
}

/// `MightyStructure.ItemCache` (MightyStructure.cs:149): the cards of the last build, each built again only when its own request's part
/// of the signature moved.
#[derive(Debug, Default)]
pub struct ItemCache {
    built: HashMap<String, (SignatureRequest, Arc<Item>)>,
    language: Option<&'static str>,
}

impl ItemCache {
    pub fn new() -> ItemCache {
        ItemCache::default()
    }

    /// `Items`: the cards of `runs`, which `signature` was taken of.
    pub fn items(&mut self, runs: &[MightyGraphRun], signature: &Signature, locale: &Locale) -> Vec<Arc<Item>> {
        // A card names an untitled block in the app's language: in another language every card is read again.
        if self.language != Some(locale.language()) {
            self.built.clear();
            self.language = Some(locale.language());
        }
        let runs = support::with_background_waiting(runs);
        let ordinals = support::request_ordinals(&runs);
        let mut next = HashMap::with_capacity(runs.len());
        let mut items = Vec::with_capacity(runs.len());
        for (index, run) in runs.iter().enumerate() {
            let request = signature.requests.get(index);
            // A trim of older requests moves a kept card's number, nothing else; a card that kept its place is the same card.
            let item = match (request, self.built.get(run.id())) {
                (Some(request), Some((kept, item))) if kept == request => {
                    if item.run_index == index && item.ordinal == ordinals[index] {
                        item.clone()
                    } else {
                        Arc::new(Item { run_index: index, ordinal: ordinals[index], ..(**item).clone() })
                    }
                }
                _ => Arc::new(item_for(run, index, Some(ordinals[index]), Some(&runs), locale)),
            };
            if let Some(request) = request {
                next.insert(run.id().to_string(), (request.clone(), item.clone()));
            }
            items.push(item);
        }
        self.built = next;
        items
    }
}

/// `MightyStructure.Tail`: the records a card reads, as one number — how many, how many tool calls settled, and the id, kind, text length
/// (in `TAIL_TEXT_STEP` steps) and tool state and summary length of the last `TAIL_ENTRIES`. Only compared within a process, as C#'s
/// `HashCode` is.
fn tail(entries: &[LogEntry]) -> u64 {
    let mut hash = DefaultHasher::new();
    entries.len().hash(&mut hash);
    entries.iter().filter(|e| e.activity.as_ref().is_some_and(|a| terminal(&a.state))).count().hash(&mut hash);
    for entry in &entries[entries.len().saturating_sub(TAIL_ENTRIES)..] {
        entry.id.hash(&mut hash);
        entry.kind.hash(&mut hash);
        (bytes(entry.text.as_deref()) / TAIL_TEXT_STEP).hash(&mut hash);
        entry.activity.as_ref().map(|a| a.state.as_str()).hash(&mut hash);
        entry.activity.as_ref().map_or(-1, |a| a.summary.len() as i64).hash(&mut hash);
    }
    hash.finish()
}

/// `MightyStructure.MaximumDepth`: the deepest tree level a cluster follows, as the diagram's layout does.
pub const MAXIMUM_DEPTH: usize = 64;
const PREVIEW_BYTES: usize = 400;
/// The bounds of the texts a card shows in place, and the lines each shows folded (MightyStructure.cs:200).
pub const REQUEST_BYTES: usize = 4_096;
pub const REQUEST_LINES: usize = 10;
pub const MESSAGE_BYTES: usize = 4_096;
pub const MESSAGE_LINES: usize = 8;
pub const ANSWER_BYTES: usize = 16_384;
pub const ANSWER_LINES: usize = 15;
pub const PROMPT_BYTES: usize = 2_048;
pub const PROMPT_LINES: usize = 2;
pub const OUTPUT_BYTES: usize = 4_096;
pub const OUTPUT_LINES: usize = 6;
/// The tool calls a request's run card and a sub-agent's card list.
pub const REQUEST_ACTIVITY: usize = 5;
pub const NODE_ACTIVITY: usize = 3;
/// The narrowest kanban column.
pub const MINIMUM_COLUMN_WIDTH: f64 = 280.0;
/// A folded line is cut at this many characters; a Markdown preview holds this many per line it shows.
pub const PREVIEW_LINE_CHARACTERS: usize = 200;
const TAIL_ENTRIES: usize = 12;
const TAIL_TEXT_STEP: usize = 256;
/// A tree's indent stops growing past this depth.
pub const MAXIMUM_INDENT: usize = 6;
const WRAP_FILL: f64 = 0.75;
const ACTIVITY_BYTES: usize = 1_000;
/// `MightyStructure.ExecutionSuffix`: the suffix of the diagram's run block under a request.
pub const EXECUTION_SUFFIX: &str = "execution";
const TOOL_KINDS: [&str; 6] = ["tool", "command", "read", "edit", "search", "web"];
/// `MightyStructure.Newlines`: the characters the Mac reads as the end of a line.
pub const NEWLINES: [char; 7] = crate::node::NEWLINES;
/// `DesignMetrics.Spacing.Lg`, `Md`, `Xs`.
const SPACING_LG: f64 = 12.0;
const SPACING_MD: f64 = 8.0;
const SPACING_XS: f64 = 4.0;

/// `Ordered`: the style's phases by their order (a stable sort, as `OrderBy`).
fn ordered(style: &dyn StructureStyle) -> Vec<&StylePhase> {
    let mut phases: Vec<&StylePhase> = style.phases().iter().collect();
    phases.sort_by_key(|p| p.order);
    phases
}

/// `MightyStructure.Make` (MightyStructure.cs:232): the pane as the structure view draws it. The current column is the pane's phase; a
/// column that holds a card is never pending; an empty column is done before the current one and pending after it. `items`: the cards
/// an [`ItemCache`] kept for these runs.
pub fn make(runs: &[MightyGraphRun], inputs: &Inputs, items: Option<&[Arc<Item>]>, locale: &Locale) -> Shape {
    let runs = support::with_background_waiting(runs);
    let cards: Vec<Arc<Item>> = match items {
        Some(items) if items.len() == runs.len() => items.to_vec(),
        _ => {
            let ordinals = support::request_ordinals(&runs);
            runs.iter().enumerate().map(|(i, run)| Arc::new(item_for(run, i, Some(ordinals[i]), Some(&runs), locale))).collect()
        }
    };
    let Some(style) = inputs.style.filter(|s| !s.phases().is_empty()) else {
        return Shape::free(cards.iter().map(|c| row_for(c.clone())).collect());
    };
    let ordered = ordered(style);
    let pane = inputs.current_phase.filter(|c| ordered.iter().any(|p| p.id == c.id));
    let assigned = phase_ids(&runs, style, if inputs.starting_new { None } else { pane }, inputs.plan_history, inputs.pending_plan_run_id);
    // A pane that names no phase stands where its newest request does.
    let current_id = pane.map(|p| p.id.as_str()).or(assigned.last().map(String::as_str));
    let current_index = current_id.and_then(|id| ordered.iter().position(|p| p.id == id));
    let held: HashSet<&str> = assigned.iter().map(String::as_str).collect();
    Shape::phases(
        ordered
            .iter()
            .enumerate()
            .map(|(index, phase)| Column {
                phase: (*phase).clone(),
                step: if Some(index) == current_index {
                    Step::Current
                } else if held.contains(phase.id.as_str()) || current_index.is_some_and(|c| index < c) {
                    Step::Done
                } else {
                    Step::Pending
                },
                items: cards.iter().enumerate().filter(|(at, _)| assigned.get(*at) == Some(&phase.id)).map(|(_, c)| c.clone()).collect(),
            })
            .collect(),
    )
}

/// `MightyStructure.PhaseIDs` (MightyStructure.cs:258): each run's phase id, in run order. A plan-state style reads each run as the pane's
/// plan stage reads its latest one; every other style takes, by the first rule that holds: the pane's phase for the newest run, the phase
/// the run's input names, the previous run's, for the first run the style's default phase, else its first.
pub fn phase_ids(
    runs: &[MightyGraphRun],
    style: &dyn StructureStyle,
    current_phase: Option<&StylePhase>,
    plan_history: Option<&[PlanRecord]>,
    pending_plan_run_id: Option<&str>,
) -> Vec<String> {
    let ordered = ordered(style);
    let Some(first) = ordered.first().map(|p| p.id.clone()) else { return vec![] };
    if style.plan_state() {
        let phase = |stage: &str| style.current_phase(Some(stage)).map_or_else(|| first.clone(), |p| p.id.clone());
        // The diagram's own match for the pending plan's request (PlanCardSupport.DiagramRecords).
        let waiting = pending_plan_run_id
            .and_then(|pending| runs.iter().rposition(|r| r.source_run_id.as_deref() == Some(pending) || r.id() == pending));
        let mut stages: Vec<String> = Vec::with_capacity(runs.len());
        for (index, run) in runs.iter().enumerate() {
            // A notice run is no request of its own: it stays in the phase of the run before it.
            let stage = if run.is_notice() {
                stages.last().cloned().unwrap_or_else(|| phase("planning"))
            } else if Some(index) == waiting {
                phase("awaitingApproval")
            } else {
                let latest = plan_history.unwrap_or(&[]).iter().rev().find(|r| plan_record_belongs(r, run));
                match latest.and_then(|r| r.outcome.as_deref()) {
                    Some("approvedAuto" | "approvedConfirm") => phase("executing"),
                    _ => phase("planning"),
                }
            };
            stages.push(stage);
        }
        return stages;
    }
    // An empty history reads as the style's default phase.
    let initial = style.current_phase(None).map_or_else(|| first.clone(), |p| p.id.clone());
    let pane = current_phase.and_then(|c| style.phases().iter().find(|p| p.id == c.id)).map(|p| p.id.clone());
    // The pane's phase is its newest request's; a notice run after it is not one.
    let newest = support::newest_request_index(runs);
    let mut result: Vec<String> = Vec::with_capacity(runs.len());
    for (index, run) in runs.iter().enumerate() {
        if run.is_notice() {
            result.push(result.last().cloned().unwrap_or_else(|| initial.clone()));
            continue;
        }
        let phase = (if Some(index) == newest { pane.clone() } else { None })
            .or_else(|| recognised_phase(run.input(), style))
            .or_else(|| result.last().cloned())
            .unwrap_or_else(|| initial.clone());
        result.push(phase);
    }
    result
}

/// `MightyStructure.RecognisedPhase`: the phase a prompt names; phase-less actions and free text name none.
pub fn recognised_phase(input: &str, style: &dyn StructureStyle) -> Option<String> {
    let phase = style.prompt_phase(input)?;
    style.phases().iter().find(|p| p.id == phase).map(|p| p.id.clone())
}

/// `PlanRecordBelongs`: a record's `graphRunId` is its run's `sourceRunID` (or the block id), and an older Mac record names it as its
/// `runId`.
fn plan_record_belongs(record: &PlanRecord, run: &MightyGraphRun) -> bool {
    record.graph_run_id.as_deref() == Some(run.id())
        || run.source_run_id.as_deref().is_some_and(|s| record.graph_run_id.as_deref() == Some(s) || record.run_id.as_deref() == Some(s))
}

/// `MightyStructure.ColumnWidth`: the columns share what the pane has, never narrower than `MINIMUM_COLUMN_WIDTH`.
pub fn column_width(available: f64, count: usize, gap: f64) -> f64 {
    if count > 0 && available.is_finite() {
        MINIMUM_COLUMN_WIDTH.max(((available - gap * (count as f64 - 1.0)) / count as f64).floor())
    } else {
        MINIMUM_COLUMN_WIDTH
    }
}

/// `MightyStructure.CharactersPerLine`: the fewest characters a wrapped line of `width` points holds at `font_size`.
pub fn characters_per_line(width: f64, font_size: f64) -> usize {
    if width.is_finite() && font_size > 0.0 { ((width / font_size * WRAP_FILL).floor() as i64).max(1) as usize } else { 1 }
}

/// `MightyStructure.NodeTextWidth`: a sub-agent card's text width in a column at `depth`.
pub fn node_text_width(column: f64, depth: usize) -> f64 {
    column - depth.min(MAXIMUM_INDENT) as f64 * SPACING_LG - (SPACING_MD + SPACING_XS)
}

fn visual_lines(text: &str, characters_per_line: usize) -> usize {
    let width = characters_per_line.max(1);
    text.split('\n').map(|line| characters(line).div_ceil(width).max(1)).sum()
}

/// Characters as the Mac counts them: whole grapheme clusters.
fn characters(text: &str) -> usize {
    text.graphemes(true).count()
}

fn prefix(text: &str, count: usize) -> &str {
    if wire::utf16_len(text) <= count {
        return text;
    }
    let end = text.grapheme_indices(true).nth(count).map_or(text.len(), |(at, _)| at);
    &text[..end]
}

/// `MightyStructure.ItemFor` (MightyStructure.cs:355): one card. `ordinal`: the request's number, none counting every run before it.
/// The card reads the run as views draw it; `runs`, the pane's, say whether it awaits notices.
pub fn item_for(raw: &MightyGraphRun, index: usize, ordinal: Option<i32>, runs: Option<&[MightyGraphRun]>, locale: &Locale) -> Item {
    let run = support::shown_in_borrowed(raw, runs.unwrap_or(std::slice::from_ref(raw)), locale);
    let finished = support::finished(&run);
    let answer = run
        .result_entries()
        .iter()
        .rev()
        .find(|e| e.kind() == "assistant" && !e.text().is_empty())
        .map(|e| e.text().to_string())
        .or_else(|| run.final_output.clone());
    let result = if finished { answer.as_deref().map(preview).filter(|l| !l.is_empty()) } else { None };
    let full = if finished { answer.as_deref().and_then(|a| excerpt_of(a, ANSWER_BYTES, ANSWER_LINES, true)) } else { None };
    // RefreshResult keeps the final answer in the main block too: shown once, as the answer.
    let latest = reply(run.root_entries());
    let message = match latest {
        Some(latest) if !(full.is_some() && Some(latest) == answer.as_deref()) => excerpt_of(latest, MESSAGE_BYTES, MESSAGE_LINES, false),
        _ => None,
    };
    // Counted over the blocks the clusters draw, so a steer's or a question's records add nothing.
    let blocks: Vec<_> = kept(&run).into_iter().map(|at| &run.agents()[at]).collect();
    let tools = run
        .root_entries()
        .iter()
        .chain(blocks.iter().flat_map(|b| b.entries()))
        .filter(|e| e.activity.as_ref().is_some_and(|a| TOOL_KINDS.contains(&a.kind.as_str())))
        .count();
    Item {
        run_index: index,
        run_id: run.id().to_string(),
        ordinal: ordinal.unwrap_or(index as i32 + 1),
        node_id: support::node_id(&run, "request"),
        execution_node_id: support::node_id(&run, EXECUTION_SUFFIX),
        input: preview(run.input()),
        status: timeline::status(run.status()).to_string(),
        finished,
        result,
        request: excerpt_of(run.input(), REQUEST_BYTES, REQUEST_LINES, false),
        message,
        activity: activity(run.root_entries(), REQUEST_ACTIVITY),
        answer: full,
        tally: Tally { agents: blocks.iter().filter(|b| support::block_kind(b) == "agent").count(), tools },
        clusters: clusters(&run, locale),
        notice_run: run.is_notice(),
        quiet: support::quiet_notice(&run),
        notice_title: if run.is_notice() { run.notice_title.clone() } else { None },
        background_waiting: support::background_waiting(&run),
        awaiting: notice::awaiting_notice(&run, std::slice::from_ref(&run), locale).is_some(),
    }
}

/// `MightyStructure.RowFor`: the request is always there; the run goes on until every block settled, unless the request failed or was
/// stopped; the result waits until the request finished.
pub fn row_for(item: Arc<Item>) -> Row {
    let ended = match item.status.as_str() {
        "error" => Some(Step::Error),
        "stopped" => Some(Step::Stopped),
        _ => None,
    };
    Row {
        request: Step::Done,
        execution: ended.unwrap_or(if item.finished { Step::Done } else { Step::Current }),
        result: if item.finished { ended.unwrap_or(Step::Done) } else { Step::Pending },
        item,
    }
}

/// `MightyStructure.Clusters` (MightyStructure.cs:389): one cluster per root of the run's sub-agent trees, by the diagram's rule — an agent
/// whose parent is missing, unknown or itself is a root, a tree stops at `MAXIMUM_DEPTH`, and whatever a cycle or the cap left out starts
/// a cluster of its own. Steer, compaction and question blocks are left out; tasks and a task's notice stay. A node's agent index indexes
/// the agents of the run as views draw it.
pub fn clusters(raw: &MightyGraphRun, locale: &Locale) -> Vec<Cluster> {
    let run = support::shown(raw, locale);
    let agents = run.agents();
    let kept = kept(&run);
    let known: HashSet<&str> = kept.iter().map(|&at| agents[at].id()).collect();
    let mut children: HashMap<&str, Vec<usize>> = HashMap::new();
    for &index in &kept {
        if let Some(parent) = agents[index].parent_id() {
            children.entry(parent).or_default().push(index);
        }
    }
    let mut visited = HashSet::new();
    let mut result = vec![];
    fn walk(
        run: &MightyGraphRun,
        index: usize,
        depth: usize,
        nodes: &mut Vec<Node>,
        visited: &mut HashSet<usize>,
        children: &HashMap<&str, Vec<usize>>,
        locale: &Locale,
    ) {
        if depth >= MAXIMUM_DEPTH || !visited.insert(index) {
            return;
        }
        let agent = &run.agents()[index];
        nodes.push(Node {
            agent_id: agent.id().to_string(),
            agent_index: index,
            node_id: support::node_id(run, &format!("agent:{}", agent.id())),
            depth,
            title: support::block_title(agent, locale),
            status: timeline::status(agent.status()).to_string(),
            kind: support::block_kind(agent).to_string(),
            prompt: excerpt_of(support::shown_input(agent), PROMPT_BYTES, PROMPT_LINES, false),
            output: reply(agent.entries()).and_then(|r| excerpt_of(r, OUTPUT_BYTES, OUTPUT_LINES, false)),
            activity: activity(agent.entries(), NODE_ACTIVITY),
            model: agent.response_records.iter().flatten().flatten().filter_map(|r| r.model.clone()).find(|m| !m.is_empty()),
            background: agent.background == Some(true),
            notice_reply: agent.notice_reply.as_deref().and_then(|n| excerpt_of(n, OUTPUT_BYTES, OUTPUT_LINES, false)),
            agent_type: roles::subagent(agent, None, locale).and_then(|i| i.type_chip),
        });
        for &child in children.get(agent.id()).map(Vec::as_slice).unwrap_or(&[]) {
            if child != index {
                walk(run, child, depth + 1, nodes, visited, children, locale);
            }
        }
    }
    let start = |index: usize, visited: &mut HashSet<usize>, result: &mut Vec<Cluster>| {
        let mut nodes = vec![];
        walk(&run, index, 0, &mut nodes, visited, &children, locale);
        if !nodes.is_empty() {
            result.push(Cluster { nodes });
        }
    };
    for &index in &kept {
        let agent = &agents[index];
        if agent.parent_id().is_none_or(|p| !known.contains(p) || p == agent.id()) {
            start(index, &mut visited, &mut result);
        }
    }
    for &index in &kept {
        if !visited.contains(&index) {
            start(index, &mut visited, &mut result);
        }
    }
    result
}

/// `Kept`: the run's sub-agent, task and task-notice blocks, by index, a repeated id kept at its first place only.
fn kept(run: &MightyGraphRun) -> Vec<usize> {
    let mut seen = HashSet::new();
    let agents = run.agents();
    (0..agents.len())
        .filter(|&i| {
            (matches!(support::block_kind(&agents[i]), "agent" | "task") || support::is_task_notice_block(&agents[i]))
                && seen.insert(agents[i].id())
        })
        .collect()
}

fn preview(text: &str) -> String {
    activity_clean(Some(text), PREVIEW_BYTES, true)
}

/// `MightyStructure.ExcerptOf` (MightyStructure.cs:443): `raw` cleaned as every shown text is and cut at `maximum_bytes`, with its first
/// `lines` lines as the folded preview; none when nothing is left. A plain preview cuts each line at `PREVIEW_LINE_CHARACTERS`; a
/// `markdown` one keeps whole lines within `lines × PREVIEW_LINE_CHARACTERS` characters and closes a code fence left open. CR LF and a
/// bare CR each end one line.
pub fn excerpt_of(raw: &str, maximum_bytes: usize, lines: usize, markdown: bool) -> Option<Excerpt> {
    // Cleaning reads the whole string; a text far over its cap is cut first.
    let cleaned =
        activity_clean(Some(&prefix_utf8(raw, 2 * maximum_bytes + 64)), maximum_bytes + 4, false).replace("\r\n", "\n").replace('\r', "\n");
    if cleaned.is_empty() {
        return None;
    }
    let text = if cleaned.len() > maximum_bytes {
        format!("{}…", prefix_utf8(&cleaned, maximum_bytes.saturating_sub(3)).trim())
    } else {
        cleaned
    };
    let mut kept: Vec<&str> = vec![];
    if markdown {
        let mut budget = lines * PREVIEW_LINE_CHARACTERS;
        for line in text.split('\n').take(lines) {
            let count = characters(line);
            // Only a first line longer than the whole budget is cut.
            if count > budget {
                if kept.is_empty() {
                    kept.push(prefix(line, budget));
                }
                break;
            }
            kept.push(line);
            budget -= count;
        }
    } else {
        kept.extend(text.split('\n').take(lines).map(|line| prefix(line, PREVIEW_LINE_CHARACTERS)));
    }
    let mut shown = kept.join("\n").trim().to_string();
    if markdown
        && shown != text
        && let Some(fence) = open_fence(&shown)
    {
        shown = format!("{shown}\n{fence}");
    }
    Some(Excerpt { text, preview: shown })
}

/// `MightyStructure.OpenFence`: the fence a Markdown text ends inside of (a line opening with ``` or ~~~ up to its matching close).
pub fn open_fence(markdown: &str) -> Option<String> {
    let mut open: Option<String> = None;
    for line in markdown.split('\n') {
        let lead = line.trim_start_matches(' ');
        if line.len() - lead.len() > 3 || lead.is_empty() {
            continue;
        }
        let mark = lead.as_bytes()[0];
        if mark != b'`' && mark != b'~' {
            continue;
        }
        let run = lead.bytes().take_while(|b| *b == mark).count();
        if run < 3 {
            continue;
        }
        // A close is the same mark, at least as long, with nothing after it.
        match &open {
            None => open = Some((mark as char).to_string().repeat(run)),
            Some(o) if o.as_bytes()[0] == mark && run >= o.len() && lead[run..].chars().all(char::is_whitespace) => open = None,
            _ => {}
        }
    }
    open
}

/// `Reply`: the block's latest reply, its last assistant record with words in it, a question's raw form left out.
fn reply(entries: &[LogEntry]) -> Option<&str> {
    entries
        .iter()
        .rev()
        .find(|e| e.kind() == "assistant" && !e.text().is_empty() && !(e.text().starts_with('{') && is_questionnaire(e.text())))
        .map(LogEntry::text)
}

/// `Activity`: the last `limit` tool calls of a block, oldest first, each its summary, else its tool's name; the turn is no tool call.
fn activity(entries: &[LogEntry], limit: usize) -> Vec<ActivityLine> {
    let mut lines = vec![];
    for entry in entries.iter().rev() {
        if lines.len() >= limit {
            break;
        }
        let Some(a) = &entry.activity else { continue };
        if a.kind == "turn" {
            continue;
        }
        let text = activity_clean(if a.summary.is_empty() { a.tool_name.as_deref() } else { Some(&a.summary) }, ACTIVITY_BYTES, true);
        if !text.is_empty() {
            lines.push(ActivityLine { id: entry.id().to_string(), kind: a.kind.clone(), state: a.state.clone(), text });
        }
    }
    lines.reverse();
    lines
}

/// `UserQuestionnaire.Parse(text) is not null` (UserQuestionnaire.cs:20): a complete bounded AskUserQuestion input — one to eight
/// questions, each with a header, a question, a multi-select flag and at most twelve options of distinct labels, the questions distinct.
pub(crate) fn is_questionnaire(input: &str) -> bool {
    if !input.trim_start().starts_with('{') || input.len() > 65_536 {
        return false;
    }
    let Ok(document) = serde_json::from_str::<Value>(input) else { return false };
    let valid = |value: &str, maximum: usize, empty: bool| {
        value.len() <= maximum
            && (empty || !value.trim().is_empty())
            && !value.chars().any(|c| c.is_control() && !matches!(c, '\t' | '\n' | '\r'))
    };
    let Some(questions) = document.get("questions").and_then(Value::as_array) else { return false };
    if questions.is_empty() || questions.len() > 8 {
        return false;
    }
    let mut asked = HashSet::new();
    for q in questions {
        let (Some(header), Some(question), Some(Value::Bool(_)), Some(options)) = (
            q.get("header").and_then(Value::as_str),
            q.get("question").and_then(Value::as_str),
            q.get("multiSelect"),
            q.get("options").and_then(Value::as_array),
        ) else {
            return false;
        };
        if !valid(header, 256, false) || !valid(question, 8192, false) || options.len() > 12 {
            return false;
        }
        let mut labels = HashSet::new();
        for option in options {
            let (Some(label), Some(description)) =
                (option.get("label").and_then(Value::as_str), option.get("description").and_then(Value::as_str))
            else {
                return false;
            };
            if !valid(label, 1024, false) || !valid(description, 8192, true) || !labels.insert(label) {
                return false;
            }
        }
        if !asked.insert(question) {
            return false;
        }
    }
    true
}
