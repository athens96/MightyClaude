//! `mighty.structure` (C# `MobileStructure` and `MobileStructureCache`, MightyCode.Core/Remote/MobileStructure.cs; macOS
//! Remote/MobileStructure.swift): the host's own structure view of a pane ([`super::make`]), cut down for the phone. The phone draws it
//! and never works a phase or a step out for itself. Versioned and bounded hard: every text has a cap under the desktop card's, only the
//! newest `MAXIMUM_ITEMS` requests travel, a request carries at most `MAXIMUM_NODES` sub-agent rows, and the whole value stays under
//! `BUDGET_BYTES` of JSON (`Wire.Json`'s bytes), older requests dropped first.

use std::collections::HashMap;
use std::sync::Arc;

use mighty_core::graph::MightyGraphRun;
use mighty_core::json::{self, JsonOut, Obj, R, Wire};
use mighty_core::locale::Locale;
use serde_json::Value;
use sha2::{Digest, Sha256};
use unicode_segmentation::UnicodeSegmentation;

use super::{ActivityLine, Inputs, Item, ItemCache, MAXIMUM_INDENT, NEWLINES, REQUEST_ACTIVITY, Shape, Signature, open_fence, word};

/// `MobileStructure.Version`: the shape this build writes.
pub const VERSION: i32 = 1;
pub const REQUEST_BYTES: usize = 1_024;
pub const MESSAGE_BYTES: usize = 1_024;
pub const ANSWER_BYTES: usize = 4_096;
pub const PROMPT_BYTES: usize = 300;
pub const OUTPUT_BYTES: usize = 1_024;
pub const ACTIVITY_BYTES: usize = 200;
pub const TITLE_BYTES: usize = 160;
pub const MODEL_BYTES: usize = 80;
pub const MAXIMUM_ITEMS: usize = 20;
pub const MAXIMUM_NODES: usize = 60;
/// Deeper nodes are drawn at this depth, as the desktop stops indenting there.
pub const MAXIMUM_DEPTH: i32 = MAXIMUM_INDENT as i32;
pub const MAXIMUM_ACTIVITY: usize = REQUEST_ACTIVITY;
/// The JSON the whole value may take.
pub const BUDGET_BYTES: usize = 192 * 1_024;
/// What a request adds to the JSON beside its own object: an estimate; `make` checks the real size.
const ITEM_OVERHEAD: usize = 96;
/// `BackgroundOutputTail.MaxLines`: a task's last output lines.
pub const OUTPUT_TAIL_LINES: usize = 6;

/// A background run's last output lines by block id (host memory only).
pub type Tails = HashMap<String, Vec<String>>;

/// `MobileStructure(V, Shape, Columns, Rows, Omitted)`: "phases" (a style with phases: `columns`) or "free" (`rows`); `omitted`: older
/// requests left out.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileStructure {
    pub v: i32,
    pub shape: String,
    pub columns: Option<Vec<MobileColumn>>,
    pub rows: Option<Vec<MobileRow>>,
    pub omitted: Option<i32>,
}

/// `MobileStructure.Column(Id, Title, Step, Items)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileColumn {
    pub id: String,
    pub title: String,
    pub step: String,
    pub items: Vec<MobileItem>,
}

/// `MobileStructure.Row(Item, Request, Execution, Result)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileRow {
    pub item: MobileItem,
    pub request: String,
    pub execution: String,
    pub result: String,
}

/// `MobileStructure.Item`: one request in its wire form.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileItem {
    pub run_id: String,
    pub ordinal: i32,
    pub title: Option<String>,
    pub status: String,
    pub finished: bool,
    pub request: Option<String>,
    pub message: Option<String>,
    pub activity: Vec<MobileActivity>,
    pub answer: Option<String>,
    pub tally: MobileTally,
    pub clusters: Vec<MobileCluster>,
    pub omitted_nodes: Option<i32>,
    pub notice_run: Option<bool>,
    pub background_waiting: Option<i32>,
    pub awaiting_notice: Option<bool>,
}

/// `MobileStructure.Activity(Kind, State, Text)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileActivity {
    pub kind: String,
    pub state: String,
    pub text: String,
}

/// `MobileStructure.Tally(Agents, Tools)`: counted over all of the request even when nodes were left out.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MobileTally {
    pub agents: i32,
    pub tools: i32,
}

/// `MobileStructure.Cluster(Nodes)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileCluster {
    pub nodes: Vec<MobileNode>,
}

/// `MobileStructure.Node`: `depth` 0 for the root, never past `MAXIMUM_DEPTH`; `activity` its last tool call.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileNode {
    pub id: String,
    pub depth: i32,
    pub title: String,
    pub status: String,
    pub kind: String,
    pub model: Option<String>,
    pub prompt: Option<String>,
    pub output: Option<String>,
    pub activity: Option<MobileActivity>,
    pub background: Option<bool>,
    pub output_tail: Option<Vec<String>>,
    pub notice_reply: Option<String>,
}

// ── the wire (`Wire.Json`: declaration order, nulls left out) ─────────────────────────────────────────────────────────────

fn text(o: &Obj, name: &str) -> R<String> {
    Ok(json::opt_string(o.get(name))?.unwrap_or_default())
}

fn int(o: &Obj, name: &str) -> R<i32> {
    json::int32(o.get(name), 0)
}

impl Wire for MobileStructure {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile structure")?;
        Ok(MobileStructure {
            v: int(&o, "v")?,
            shape: text(&o, "shape")?,
            columns: json::strict_list(o.get("columns"), "column")?,
            rows: json::strict_list(o.get("rows"), "row")?,
            omitted: json::opt_int32(o.get("omitted"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_int("v", self.v as i64);
        out.prop_str("shape", Some(&self.shape));
        out.prop_list("columns", self.columns.as_ref());
        out.prop_list("rows", self.rows.as_ref());
        out.prop_opt_int("omitted", self.omitted.map(i64::from));
        out.end_object();
    }
}

impl Wire for MobileColumn {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile column")?;
        Ok(MobileColumn {
            id: text(&o, "id")?,
            title: text(&o, "title")?,
            step: text(&o, "step")?,
            items: json::strict_list(o.get("items"), "item")?.unwrap_or_default(),
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", Some(&self.id));
        out.prop_str("title", Some(&self.title));
        out.prop_str("step", Some(&self.step));
        out.name("items");
        out.list(&self.items);
        out.end_object();
    }
}

impl Wire for MobileRow {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile row")?;
        Ok(MobileRow {
            item: json::record(o.get("item"))?.ok_or_else(|| json::ReadError("a row without its item".into()))?,
            request: text(&o, "request")?,
            execution: text(&o, "execution")?,
            result: text(&o, "result")?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.name("item");
        self.item.write(out);
        out.prop_str("request", Some(&self.request));
        out.prop_str("execution", Some(&self.execution));
        out.prop_str("result", Some(&self.result));
        out.end_object();
    }
}

impl Wire for MobileItem {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile item")?;
        Ok(MobileItem {
            run_id: text(&o, "runId")?,
            ordinal: int(&o, "ordinal")?,
            title: json::opt_string(o.get("title"))?,
            status: text(&o, "status")?,
            finished: json::boolean(o.get("finished"), false)?,
            request: json::opt_string(o.get("request"))?,
            message: json::opt_string(o.get("message"))?,
            activity: json::strict_list(o.get("activity"), "activity")?.unwrap_or_default(),
            answer: json::opt_string(o.get("answer"))?,
            tally: json::record(o.get("tally"))?.unwrap_or(MobileTally { agents: 0, tools: 0 }),
            clusters: json::strict_list(o.get("clusters"), "cluster")?.unwrap_or_default(),
            omitted_nodes: json::opt_int32(o.get("omittedNodes"))?,
            notice_run: json::opt_boolean(o.get("noticeRun"))?,
            background_waiting: json::opt_int32(o.get("backgroundWaiting"))?,
            awaiting_notice: json::opt_boolean(o.get("awaitingNotice"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("runId", Some(&self.run_id));
        out.prop_int("ordinal", self.ordinal as i64);
        out.prop_str("title", self.title.as_deref());
        out.prop_str("status", Some(&self.status));
        out.prop_bool("finished", self.finished);
        out.prop_str("request", self.request.as_deref());
        out.prop_str("message", self.message.as_deref());
        out.name("activity");
        out.list(&self.activity);
        out.prop_str("answer", self.answer.as_deref());
        out.prop("tally", Some(&self.tally));
        out.name("clusters");
        out.list(&self.clusters);
        out.prop_opt_int("omittedNodes", self.omitted_nodes.map(i64::from));
        out.prop_opt_bool("noticeRun", self.notice_run);
        out.prop_opt_int("backgroundWaiting", self.background_waiting.map(i64::from));
        out.prop_opt_bool("awaitingNotice", self.awaiting_notice);
        out.end_object();
    }
}

impl Wire for MobileActivity {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile activity")?;
        Ok(MobileActivity { kind: text(&o, "kind")?, state: text(&o, "state")?, text: text(&o, "text")? })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("kind", Some(&self.kind));
        out.prop_str("state", Some(&self.state));
        out.prop_str("text", Some(&self.text));
        out.end_object();
    }
}

impl Wire for MobileTally {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile tally")?;
        Ok(MobileTally { agents: int(&o, "agents")?, tools: int(&o, "tools")? })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_int("agents", self.agents as i64);
        out.prop_int("tools", self.tools as i64);
        out.end_object();
    }
}

impl Wire for MobileCluster {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile cluster")?;
        Ok(MobileCluster { nodes: json::strict_list(o.get("nodes"), "node")?.unwrap_or_default() })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.name("nodes");
        out.list(&self.nodes);
        out.end_object();
    }
}

impl Wire for MobileNode {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a mobile node")?;
        Ok(MobileNode {
            id: text(&o, "id")?,
            depth: int(&o, "depth")?,
            title: text(&o, "title")?,
            status: text(&o, "status")?,
            kind: text(&o, "kind")?,
            model: json::opt_string(o.get("model"))?,
            prompt: json::opt_string(o.get("prompt"))?,
            output: json::opt_string(o.get("output"))?,
            activity: json::record(o.get("activity"))?,
            background: json::opt_boolean(o.get("background"))?,
            output_tail: json::string_list(o.get("outputTail"))?.map(|l| l.into_iter().flatten().collect()),
            notice_reply: json::opt_string(o.get("noticeReply"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", Some(&self.id));
        out.prop_int("depth", self.depth as i64);
        out.prop_str("title", Some(&self.title));
        out.prop_str("status", Some(&self.status));
        out.prop_str("kind", Some(&self.kind));
        out.prop_str("model", self.model.as_deref());
        out.prop_str("prompt", self.prompt.as_deref());
        out.prop_str("output", self.output.as_deref());
        out.prop("activity", self.activity.as_ref());
        out.prop_opt_bool("background", self.background);
        out.prop_strings("outputTail", self.output_tail.as_ref());
        out.prop_str("noticeReply", self.notice_reply.as_deref());
        out.end_object();
    }
}

/// `EncodedSize`: the bytes `Wire.Json` writes of the value (every non-ASCII character escaped, so one byte a character).
fn encoded_size<T: Wire>(value: &T) -> usize {
    value.to_json().len()
}

// ── projection ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `MobileStructure.Wired(Source, Title, Item, Size, Tails)`: one request in its wire form with its JSON size, kept between builds so a
/// rebuild encodes only the requests that moved.
#[derive(Debug, Clone)]
pub struct Wired {
    pub source: Arc<Item>,
    pub title: Option<String>,
    pub item: MobileItem,
    pub size: usize,
    pub tails: String,
}

/// A request's title (the style's name for the request with this run id) and a block's model id as the phone shows it.
pub type TitleOf<'a> = &'a dyn Fn(&str) -> Option<String>;
pub type ModelOf<'a> = &'a dyn Fn(&str) -> String;

/// `MobileStructure.Make` (MobileStructure.cs:75): the pane's structure for the phone; none when there is nothing to draw (no request yet
/// and no style phase to show). `wired`: the requests of the last build, by run id, replaced by this build's.
pub fn make(
    shape: &Shape,
    run_count: usize,
    title: Option<TitleOf>,
    model: Option<ModelOf>,
    wired: Option<&mut HashMap<String, Wired>>,
    tails: Option<&Tails>,
) -> Option<MobileStructure> {
    let nothing = match &shape.columns {
        Some(columns) => run_count == 0 && columns.is_empty(),
        None => shape.rows.as_ref().is_none_or(Vec::is_empty),
    };
    if nothing {
        return None;
    }
    let all = shape.items();
    // A run id the pane repeats (a damaged save) travels once, as its newest request.
    let mut newest: HashMap<String, usize> = HashMap::new();
    for item in &all {
        let at = newest.entry(item.run_id.clone()).or_insert(item.run_index);
        *at = (*at).max(item.run_index);
    }
    let mut sources: Vec<&Arc<Item>> = all.iter().copied().filter(|i| newest[&i.run_id] == i.run_index).collect();
    sources.sort_by_key(|i| std::cmp::Reverse(i.run_index));
    let mut next: HashMap<String, Wired> = HashMap::new();
    let previous = wired.as_deref();
    let requests: Vec<Wired> = sources
        .into_iter()
        .take(MAXIMUM_ITEMS)
        .map(|source| {
            // A notice run is no request: it takes no style title, but its own (none from an older save: the phone reads the generic one).
            let name = if source.notice_run { source.notice_title.clone() } else { title.and_then(|t| t(&source.run_id)) };
            let held = match tails {
                Some(t) if !t.is_empty() => tails_key(source, t),
                _ => String::new(),
            };
            let made = match previous.and_then(|w| w.get(&source.run_id)) {
                Some(made) if (Arc::ptr_eq(&made.source, source) || made.source == *source) && made.title == name && made.tails == held => {
                    made.clone()
                }
                _ => {
                    let item = item_for(source, name.as_deref(), model, tails);
                    Wired { source: source.clone(), title: name, size: encoded_size(&item), item, tails: held }
                }
            };
            next.insert(source.run_id.clone(), made.clone());
            made
        })
        .collect();
    if let Some(wired) = wired {
        *wired = next;
    }

    // Newest first by the estimate, then the real size decides.
    let mut used = encoded_size(&build(shape, &HashMap::new(), &newest, newest.len()));
    let mut count = 0;
    for request in &requests {
        let cost = request.size + ITEM_OVERHEAD;
        if count > 0 && used + cost > BUDGET_BYTES {
            break;
        }
        used += cost;
        count += 1;
    }
    let mut kept: HashMap<String, MobileItem> = requests[..count].iter().map(|r| (r.source.run_id.clone(), r.item.clone())).collect();
    let mut order: Vec<String> = requests[..count].iter().map(|r| r.source.run_id.clone()).collect();
    let mut result = build(shape, &kept, &newest, newest.len() - kept.len());
    while encoded_size(&result) > BUDGET_BYTES {
        if order.len() > 1 {
            let last = order.pop().expect("more than one request");
            kept.remove(&last);
            result = build(shape, &kept, &newest, newest.len() - kept.len());
            continue;
        }
        // The newest request alone is over: keep as many of its first nodes as fit.
        let Some(heavy) = order.first().and_then(|id| kept.get(id)).cloned() else { break };
        let fitted = |nodes: usize| {
            let mut trimmed = kept.clone();
            trimmed.insert(order[0].clone(), keeping(&heavy, nodes));
            build(shape, &trimmed, &newest, newest.len() - kept.len())
        };
        let (mut low, mut high) = (0, heavy.clusters.iter().map(|c| c.nodes.len()).sum::<usize>());
        while low < high {
            let middle = (low + high).div_ceil(2);
            if encoded_size(&fitted(middle)) <= BUDGET_BYTES {
                low = middle;
            } else {
                high = middle - 1;
            }
        }
        result = fitted(low);
        break;
    }
    Some(result)
}

/// `Build`: the wire value holding `kept` (by run id), each request once, where the desktop drew it.
fn build(shape: &Shape, kept: &HashMap<String, MobileItem>, newest: &HashMap<String, usize>, omitted: usize) -> MobileStructure {
    let carried = |item: &Item| if newest.get(&item.run_id) == Some(&item.run_index) { kept.get(&item.run_id).cloned() } else { None };
    let left = (omitted > 0).then_some(omitted as i32);
    match &shape.columns {
        Some(columns) => MobileStructure {
            v: VERSION,
            shape: "phases".into(),
            columns: Some(
                columns
                    .iter()
                    .map(|c| MobileColumn {
                        id: c.phase.id.clone(),
                        title: capped(&c.phase.title, TITLE_BYTES, true, false),
                        step: word(c.step).into(),
                        items: c.items.iter().filter_map(|i| carried(i)).collect(),
                    })
                    .collect(),
            ),
            rows: None,
            omitted: left,
        },
        None => MobileStructure {
            v: VERSION,
            shape: "free".into(),
            columns: None,
            rows: Some(
                shape
                    .rows
                    .iter()
                    .flatten()
                    .filter_map(|r| {
                        carried(&r.item).map(|item| MobileRow {
                            item,
                            request: word(r.request).into(),
                            execution: word(r.execution).into(),
                            result: word(r.result).into(),
                        })
                    })
                    .collect(),
            ),
            omitted: left,
        },
    }
}

/// `TailsKey`: the lines of `tails` that belong to the request's tasks, as one comparable key.
fn tails_key(item: &Item, tails: &Tails) -> String {
    let mut key = String::new();
    for node in item.clusters.iter().flat_map(|c| &c.nodes) {
        if node.kind == "task"
            && let Some(lines) = tails.get(&node.agent_id)
        {
            key.push_str(&node.agent_id);
            key.push('\u{1e}');
            key.push_str(&lines.join("\u{1f}"));
            key.push('\u{1d}');
        }
    }
    key
}

/// `MobileMightySupport.OutputTail`: a task's last output lines, none when none were read.
pub fn output_tail(lines: Option<&Vec<String>>) -> Option<Vec<String>> {
    lines.filter(|l| !l.is_empty()).map(|l| l[l.len().saturating_sub(OUTPUT_TAIL_LINES)..].to_vec())
}

/// `MobileStructure.ItemFor` (MobileStructure.cs:155): one request in its wire form, its texts under the phone's caps and its first
/// `MAXIMUM_NODES` nodes, the rest counted as left out.
pub fn item_for(item: &Item, title: Option<&str>, model: Option<ModelOf>, tails: Option<&Tails>) -> MobileItem {
    let mut nodes = 0;
    let mut clusters = vec![];
    for cluster in &item.clusters {
        if nodes >= MAXIMUM_NODES {
            break;
        }
        let taken: Vec<MobileNode> = cluster
            .nodes
            .iter()
            .take(MAXIMUM_NODES - nodes)
            .map(|node| MobileNode {
                id: node.agent_id.clone(),
                depth: (node.depth as i32).clamp(0, MAXIMUM_DEPTH),
                title: capped(&node.title, TITLE_BYTES, true, false),
                status: node.status.clone(),
                kind: node.kind.clone(),
                model: node.model.as_deref().map(|id| capped(&model.map_or_else(|| id.to_string(), |m| m(id)), MODEL_BYTES, true, false)),
                prompt: node.prompt.as_ref().map(|p| capped(&p.text, PROMPT_BYTES, false, false)),
                output: node.output.as_ref().map(|o| capped(&o.text, OUTPUT_BYTES, false, false)),
                activity: node.activity.last().map(activity_for),
                background: node.background.then_some(true),
                output_tail: if node.kind == "task" { output_tail(tails.and_then(|t| t.get(&node.agent_id))) } else { None },
                notice_reply: node.notice_reply.as_ref().map(|r| capped(&r.text, OUTPUT_BYTES, false, false)),
            })
            .collect();
        nodes += taken.len();
        clusters.push(MobileCluster { nodes: taken });
    }
    let total: usize = item.clusters.iter().map(|c| c.nodes.len()).sum();
    let named = title.map(|t| capped(t, TITLE_BYTES, true, false)).filter(|t| !t.is_empty());
    let activity = &item.activity[item.activity.len().saturating_sub(MAXIMUM_ACTIVITY)..];
    MobileItem {
        run_id: item.run_id.clone(),
        ordinal: item.ordinal,
        title: named,
        status: item.status.clone(),
        finished: item.finished,
        request: item.request.as_ref().map(|r| capped(&r.text, REQUEST_BYTES, false, false)),
        message: item.message.as_ref().map(|m| capped(&m.text, MESSAGE_BYTES, false, false)),
        activity: activity.iter().map(activity_for).collect(),
        answer: item.answer.as_ref().map(|a| capped(&a.text, ANSWER_BYTES, false, true)),
        tally: MobileTally { agents: item.tally.agents as i32, tools: item.tally.tools as i32 },
        clusters,
        omitted_nodes: (total > nodes).then(|| (total - nodes) as i32),
        notice_run: item.notice_run.then_some(true),
        background_waiting: (item.background_waiting > 0).then_some(item.background_waiting),
        awaiting_notice: item.awaiting.then_some(true),
    }
}

fn activity_for(line: &ActivityLine) -> MobileActivity {
    MobileActivity {
        kind: capped(&line.kind, 40, true, false),
        state: capped(&line.state, 40, true, false),
        text: capped(&line.text, ACTIVITY_BYTES, true, false),
    }
}

/// `MobileStructure.Keeping`: the request with only its first `nodes` nodes, depth-first, the rest counted as left out.
pub fn keeping(item: &MobileItem, nodes: usize) -> MobileItem {
    let mut left = nodes;
    let mut dropped = 0;
    let mut clusters = vec![];
    for cluster in &item.clusters {
        let taken: Vec<MobileNode> = cluster.nodes.iter().take(left).cloned().collect();
        dropped += cluster.nodes.len() - taken.len();
        left -= taken.len();
        if !taken.is_empty() {
            clusters.push(MobileCluster { nodes: taken });
        }
    }
    let omitted = item.omitted_nodes.unwrap_or(0) + dropped as i32;
    MobileItem { clusters, omitted_nodes: (omitted > 0).then_some(omitted), ..item.clone() }
}

/// `MobileStructure.Capped` (MobileStructure.cs:198): `text` within `maximum_bytes` of UTF-8, cut between characters (a combined emoji is
/// never split) and marked with "…"; a cut Markdown text has a code fence it leaves open closed again, the closing line inside the cap.
pub fn capped(text: &str, maximum_bytes: usize, single_line: bool, markdown: bool) -> String {
    let flat = if single_line { text.split(NEWLINES).filter(|l| !l.is_empty()).collect::<Vec<_>>().join(" ") } else { text.to_string() };
    if flat.len() <= maximum_bytes {
        return flat;
    }
    let mut reserved = 0;
    loop {
        let result = format!("{}…", prefix(&flat, maximum_bytes.saturating_sub(3 + reserved)).trim());
        let Some(fence) = open_fence(&result).filter(|_| markdown) else { return result };
        let closing = 1 + fence.len();
        // A shorter cut can land inside an earlier, longer fence: reserve for that one and cut again.
        if closing <= reserved {
            return format!("{result}\n{fence}");
        }
        reserved = closing;
    }
}

/// `Prefix`: the longest run of whole characters (grapheme clusters) of `text` within `bytes` of UTF-8.
fn prefix(text: &str, bytes: usize) -> &str {
    let mut end = 0;
    for (at, grapheme) in text.grapheme_indices(true) {
        if at + grapheme.len() > bytes {
            break;
        }
        end = at + grapheme.len();
    }
    &text[..end]
}

/// `MobileStructure.Tag`: what the phone echoes back (`?structure=<tag>`) — 1…999 999 999 999 from the value's bytes, 0 for none.
pub fn tag(value: Option<&MobileStructure>) -> i64 {
    let Some(value) = value else { return 0 };
    let digest = Sha256::digest(value.to_json().as_bytes());
    let first = u64::from_be_bytes(digest[..8].try_into().expect("eight bytes"));
    (first % 999_999_999_999) as i64 + 1
}

/// `MobileStructureCache.Built(Value, Tag)`: a pane's value and the tag the phone echoes for it.
#[derive(Debug, Clone, PartialEq)]
pub struct Built {
    pub value: Option<MobileStructure>,
    pub tag: i64,
}

/// What a pane's value names besides its runs (`MobileStructureCache.Structure`'s other parameters): `key` names the styles whose
/// request titles it carries and the model names it shows; `title` reads a request's input; `tails` the pane's background runs' last
/// output lines by block id — a new map (the host replaces it on every change) builds again.
#[derive(Clone, Copy, Default)]
pub struct Naming<'a> {
    pub key: &'a str,
    pub title: Option<TitleOf<'a>>,
    pub model: Option<ModelOf<'a>>,
    pub tails: Option<&'a Arc<Tails>>,
}

#[derive(Debug)]
struct Entry {
    signature: Option<Signature>,
    key: String,
    tails: Option<Arc<Tails>>,
    items: ItemCache,
    wired: HashMap<String, Wired>,
    built: Arc<Built>,
}

/// `MobileStructureCache` (MobileStructure.cs:243): the phone's structure per pane, built again only when what the structure view reads
/// moved, and then only the cards of the requests that moved.
#[derive(Debug, Default)]
pub struct MobileStructureCache {
    entries: HashMap<String, Entry>,
    builds: usize,
}

impl MobileStructureCache {
    pub fn new() -> MobileStructureCache {
        MobileStructureCache::default()
    }

    /// `Builds`: how many times a value was built rather than reused.
    pub fn builds(&self) -> usize {
        self.builds
    }

    /// `Structure`: `super::make` with the inputs the desktop view passes (a request never starts anew here), turned into the wire shape.
    pub fn structure(&mut self, pane: &str, runs: &[MightyGraphRun], inputs: &Inputs, naming: &Naming, locale: &Locale) -> Arc<Built> {
        let inputs = Inputs { starting_new: false, ..*inputs };
        let signature = Signature::new(runs, &inputs, locale);
        let entry = self.entries.entry(pane.to_string()).or_insert_with(|| Entry {
            signature: None,
            key: String::new(),
            tails: None,
            items: ItemCache::new(),
            wired: HashMap::new(),
            built: Arc::new(Built { value: None, tag: 0 }),
        });
        let same_tails = match (&entry.tails, naming.tails) {
            (None, None) => true,
            (Some(a), Some(b)) => Arc::ptr_eq(a, b),
            _ => false,
        };
        if entry.signature.as_ref() == Some(&signature) && entry.key == naming.key && same_tails {
            return entry.built.clone();
        }
        // Another key can name each request differently: nothing kept is reused.
        if entry.key != naming.key {
            entry.wired.clear();
        }
        let items = entry.items.items(runs, &signature, locale);
        let shape = super::make(runs, &inputs, Some(&items), locale);
        let inputs_by_run: HashMap<&str, &str> = runs.iter().map(|r| (r.id(), r.input())).collect();
        let title = naming.title.map(|t| move |id: &str| inputs_by_run.get(id).and_then(|input| t(input)));
        let value = make(
            &shape,
            runs.len(),
            title.as_ref().map(|t| t as &dyn Fn(&str) -> Option<String>),
            naming.model,
            Some(&mut entry.wired),
            naming.tails.map(|t| &**t),
        );
        entry.built = Arc::new(Built { tag: tag(value.as_ref()), value });
        entry.signature = Some(signature);
        entry.key = naming.key.to_string();
        entry.tails = naming.tails.cloned();
        self.builds += 1;
        entry.built.clone()
    }

    /// `Retain`: forgets the panes not in `panes`.
    pub fn retain(&mut self, panes: &std::collections::HashSet<String>) {
        self.entries.retain(|pane, _| panes.contains(pane));
    }
}
