//! The host's `mighty` push (C# MightyCode.WinUI/MainWindow.WebShell.Mighty.cs): what the page's Mighty views of a pane need besides the
//! runs it already holds — each block's copy, the layout's inputs, the older requests read from the session record, the timeline's
//! groups and the structure's boards — and the message that sends only what changed (`{kind:"mighty", paneId, blocks?, removed?, inputs?,
//! older?, timeline?, structure?}`, blocks by id). The page's types are native/windows/web/src/mighty/types.ts; every record here writes
//! the same field names (camelCase, an absent value left out, as `Wire.Json` writes C#'s records).
//!
//! The pure parts: the records ([`WebMightyBlock`] …), the differ ([`MightySent`]), and the projection of a pane into them
//! ([`project`]), which takes what lives outside this crate (the session record's older requests, the pending plan, the pictures, the
//! background executions, the background output tails, the custom agents' definitions, the model catalogue) as plain inputs.

use std::collections::{HashMap, HashSet};

use indexmap::IndexMap;
use mighty_core::graph::MightyGraphRun;
use mighty_core::json::Wire;
use mighty_core::locale::Locale;
use serde::Serialize;
use serde_json::Value;

use crate::notice;

/// `WebMightyAwaitedTask(Title, Status, Word)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebMightyAwaitedTask {
    pub title: String,
    pub status: String,
    pub word: String,
}

/// `WebMightyAwaiting(Tasks, Progress)`: a 알림 대기 result's rows.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebMightyAwaiting {
    pub tasks: Vec<WebMightyAwaitedTask>,
    pub progress: String,
}

/// `WebMightyBlock`: one diagram block's copy, without its frame (the page lays out) or its transcript (the page holds the runs).
/// `flow` is the role view's copy of the node (`FlowCopy.Block`), written as its own module writes it.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebMightyBlock {
    pub kind: String,
    pub title: String,
    pub state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub capsule: Option<String>,
    pub help: String,
    pub indicator: String,
    pub outline: String,
    pub status: String,
    pub tone: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub files_run_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub record_id: Option<String>,
    pub from_record: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub background: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_tail: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notice_reply: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notice: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub awaiting: Option<WebMightyAwaiting>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub so_far: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub flow: Option<Value>,
}

/// `WebMightyFile(Path, Line?)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebMightyFile {
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub line: Option<i32>,
}

/// `WebMightyHistory(Text, Acts, Phase)`: the history block at the top of the diagram.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebMightyHistory {
    pub text: String,
    pub acts: bool,
    pub phase: String,
}

/// `{runID, recordID}`: an answered plan's block.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebPlanRecord {
    #[serde(rename = "runID")]
    pub run_id: String,
    #[serde(rename = "recordID")]
    pub record_id: String,
}

/// `{runID, step, count}`: a step's picture preview.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebGallery {
    #[serde(rename = "runID")]
    pub run_id: String,
    pub step: String,
    pub count: i32,
}

/// `{runID, key}`: a background execution's block.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebExecution {
    #[serde(rename = "runID")]
    pub run_id: String,
    pub key: String,
}

/// `WebMightyInputs`: what the page's layout needs besides the runs it holds, and the toolbar's words. `executionCards` (an execution's
/// card, `WebMightyExecution`) is the Ouroboros dashboard's copy and comes from outside as it is; `log` and `progress` are the role view's
/// (`FlowCopy.LogOf`, `FlowCopy.ProgressOf`).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebMightyInputs {
    #[serde(rename = "planRunID", skip_serializing_if = "Option::is_none")]
    pub plan_run_id: Option<String>,
    pub plan_records: Vec<WebPlanRecord>,
    pub galleries: Vec<WebGallery>,
    pub executions: Vec<WebExecution>,
    #[serde(rename = "resultFilesRunID", skip_serializing_if = "Option::is_none")]
    pub result_files_run_id: Option<String>,
    pub files: IndexMap<String, Vec<WebMightyFile>>,
    pub older: i32,
    pub retained_start: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub history: Option<WebMightyHistory>,
    pub summary: String,
    pub summary_help: String,
    pub execution_cards: IndexMap<String, Value>,
    pub animations: bool,
    pub header: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fan_out: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub log: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub progress: Option<Value>,
}

/// `WebTimelineRow`: one 기록 row; `ink` is its block's role ink.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebTimelineRow {
    pub node_id: String,
    pub kind: String,
    pub kind_label: String,
    pub title: String,
    pub status: String,
    pub status_word: String,
    pub tone: String,
    pub ring: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rail: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub latest: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub duration: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub capsule: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub input: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub agent_index: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ink: Option<String>,
}

/// `WebTimelineGroup`: one 기록 group.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebTimelineGroup {
    pub run_id: String,
    pub ordinal: i32,
    pub title: String,
    pub status: String,
    pub status_word: String,
    pub tone: String,
    pub input: String,
    pub tally: String,
    pub open: bool,
    pub older: bool,
    pub rows: Vec<WebTimelineRow>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_node_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_tone: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<String>,
    pub result_folds: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_caption: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notice_run: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_title: Option<String>,
}

/// `WebStructureActivity(Kind, State, Text)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct WebStructureActivity {
    pub kind: String,
    pub state: String,
    pub text: String,
}

/// `WebStructureCard`: a structure card for the page.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebStructureCard {
    pub id: String,
    pub kind: String,
    pub run_id: String,
    pub node_id: String,
    pub title: String,
    pub status: String,
    pub status_word: String,
    pub tone: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub parent: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub answer: Option<String>,
    pub activity: Vec<WebStructureActivity>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tally: Option<String>,
    pub finished: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_line: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub node_kind: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub meta: Option<String>,
    pub depth: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub prompt: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub execution_node_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_node_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub background: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_tail: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notice_reply: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_title: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_awaiting: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_waiting: Option<bool>,
}

/// `WebStructureColumn`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebStructureColumn {
    pub id: String,
    pub title: String,
    pub step: String,
    pub step_word: String,
    pub count: i32,
    pub cards: Vec<WebStructureCard>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tone: Option<String>,
}

/// `WebStructureBoard`: one 파이프라인 board; `quiet`: only notice runs taken into a running turn.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebStructureBoard {
    pub id: String,
    pub kind: String,
    pub ordinal: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub headline: Option<String>,
    pub status: String,
    pub status_word: String,
    pub tone: String,
    pub run_ids: Vec<String>,
    pub columns: Vec<WebStructureColumn>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub style_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quiet: Option<bool>,
}

/// `WebMightyProjection(Blocks, Inputs, OlderKey, OlderRuns, Timeline?, Structure?)`: a pane's Mighty view as the page draws it. The
/// timeline is made only in the timeline presentation and the structure only in the structure one.
#[derive(Debug, Clone, PartialEq)]
pub struct MightyProjection {
    pub blocks: IndexMap<String, WebMightyBlock>,
    pub inputs: WebMightyInputs,
    pub older_key: String,
    pub older_runs: Vec<MightyGraphRun>,
    pub timeline: Option<Vec<WebTimelineGroup>>,
    pub structure: Option<Vec<WebStructureBoard>>,
}

/// `WebMightySent`: what was last pushed for one pane whose Mighty view the page shows (`mightyVisible`), as JSON text.
#[derive(Debug, Clone, Default)]
pub struct MightySent {
    blocks: IndexMap<String, String>,
    inputs: String,
    older: String,
    timeline: String,
    structure: String,
    /// The blocks the last message set, for the smoke: a streamed change must send only the block it changed.
    pub last_set: Vec<String>,
}

fn text_of<T: Serialize>(value: &T) -> String {
    serde_json::to_string(value).unwrap_or_default()
}

/// `WebDelta.PageRun`: a run as the page holds it, its agents' output files left on the host's side.
pub fn page_run(run: &MightyGraphRun) -> Value {
    let mut out = mighty_core::json::JsonOut::new();
    run.write_with(&mut out, false);
    serde_json::from_str(&out.finish()).unwrap_or(Value::Null)
}

impl MightySent {
    /// A pane the page starts showing: the first message carries the whole model.
    pub fn new() -> MightySent {
        MightySent::default()
    }

    /// `FlushWebMighty` for one pane (MainWindow.WebShell.Mighty.cs:143-178): the message with only the blocks whose copy changed, the
    /// blocks that went, and the inputs, older requests, timeline or boards when their JSON changed; none when nothing did. A presentation
    /// that made no timeline (or no boards) keeps what was sent.
    pub fn message(&mut self, pane_id: &str, model: &MightyProjection) -> Option<Value> {
        let mut set = serde_json::Map::new();
        self.last_set.clear();
        for (id, block) in &model.blocks {
            let json = text_of(block);
            if self.blocks.get(id) == Some(&json) {
                continue;
            }
            self.blocks.insert(id.clone(), json);
            set.insert(id.clone(), serde_json::to_value(block).unwrap_or(Value::Null));
            self.last_set.push(id.clone());
        }
        let removed: Vec<String> = self.blocks.keys().filter(|id| !model.blocks.contains_key(*id)).cloned().collect();
        for id in &removed {
            self.blocks.shift_remove(id);
        }
        let inputs = text_of(&model.inputs);
        let timeline = model.timeline.as_ref().map_or_else(|| self.timeline.clone(), text_of);
        let structure = model.structure.as_ref().map_or_else(|| self.structure.clone(), text_of);
        let mut message = serde_json::Map::new();
        message.insert("kind".into(), Value::from("mighty"));
        message.insert("paneId".into(), Value::from(pane_id));
        if !set.is_empty() {
            message.insert("blocks".into(), Value::Object(set));
        }
        if !removed.is_empty() {
            message.insert("removed".into(), Value::from(removed));
        }
        if inputs != self.inputs {
            message.insert("inputs".into(), serde_json::to_value(&model.inputs).unwrap_or(Value::Null));
            self.inputs = inputs;
        }
        if model.older_key != self.older {
            message.insert("older".into(), Value::Array(model.older_runs.iter().map(page_run).collect()));
            self.older = model.older_key.clone();
        }
        if timeline != self.timeline {
            message.insert("timeline".into(), serde_json::to_value(&model.timeline).unwrap_or(Value::Null));
            self.timeline = timeline;
        }
        if structure != self.structure {
            message.insert("structure".into(), serde_json::to_value(&model.structure).unwrap_or(Value::Null));
            self.structure = structure;
        }
        (message.len() > 2).then_some(Value::Object(message))
    }
}

/// The panes whose Mighty view the page shows and what was last pushed for each (C# `webMighty`): `mightyVisible` starts and stops one;
/// the host projects each at most once per 120 ms while it changed (and every eighth tick, as the inputs may have).
#[derive(Debug, Clone, Default)]
pub struct MightyPushes {
    panes: HashMap<String, MightySent>,
    ticks: u64,
}

impl MightyPushes {
    /// `mightyVisible{visible}`: the page shows (or stops showing) a pane's Mighty view; pushes start with the whole model.
    pub fn visible(&mut self, pane_id: &str, visible: bool) {
        if visible {
            self.panes.insert(pane_id.into(), MightySent::new());
        } else {
            self.panes.remove(pane_id);
        }
    }

    /// The panes shown, for the host's timer (it stops when none is).
    pub fn shown(&self) -> impl Iterator<Item = &str> {
        self.panes.keys().map(String::as_str)
    }

    /// One 120 ms tick: whether every pane is projected again this time (every eighth), whatever changed.
    pub fn tick(&mut self) -> bool {
        self.ticks += 1;
        self.ticks.is_multiple_of(8)
    }

    /// The message for one shown pane, or none (a pane not shown, or nothing changed).
    pub fn message(&mut self, pane_id: &str, model: &MightyProjection) -> Option<Value> {
        self.panes.get_mut(pane_id)?.message(pane_id, model)
    }

    /// A pane that left the state stops being shown.
    pub fn retain(&mut self, alive: &HashSet<String>) {
        self.panes.retain(|id, _| alive.contains(id));
    }
}

/// `LoadedFromRecord(nodeId, older)`: a node of a request read back from the session record.
pub fn loaded_from_record(node_id: &str, older: &[MightyGraphRun]) -> bool {
    older.iter().any(|run| node_id.starts_with(&mighty_core::graph::node_id(run.id(), "")))
}

/// `SessionHistory.Providers`: the agents whose session record the history block reads.
pub const HISTORY_PROVIDERS: [&str; 3] = ["claude", "codex", "gemini"];

/// `ShowsHistoryBlock(pane, retained)`: the history block at the top of a pane that has a session record to read.
pub fn shows_history_block(provider: &str, retained: &[MightyGraphRun], resume_id: Option<&str>) -> bool {
    HISTORY_PROVIDERS.contains(&provider) && (!retained.is_empty() || resume_id.is_some())
}

/// The older requests' key: what the page holds of them changes only when one of them does.
pub fn older_key(older: &[MightyGraphRun]) -> String {
    let runs: Vec<String> = older.iter().map(|r| format!("{}/{}/{}", r.id(), r.status(), r.agents().len())).collect();
    format!("{}:{}", older.len(), runs.join(","))
}

/// `GraphRequestTitle(run, ordinal)`: a notice run is titled after the work Claude replied to; a request "<style prefix> · 요청 n · <agent>".
/// `style_prefix` is the guided style's own name for the request's prompt (`StylePresentation.RequestPrefix`), when a style is on.
pub fn request_title(run: &MightyGraphRun, ordinal: i32, provider_label: &str, style_prefix: Option<&str>, locale: &Locale) -> String {
    if run.is_notice() {
        return notice::heading(run, locale);
    }
    let tail = locale.get_with("graph.block.requestTitle", &[("ordinal", &ordinal.to_string()), ("provider", provider_label)]);
    match style_prefix.filter(|p| !p.is_empty()) {
        Some(prefix) => format!("{prefix} · {tail}"),
        None => tail,
    }
}

/// The toolbar's header: "Mighty" and, with a guided style on, its name and its phase's title.
pub fn header(style_name: Option<&str>, phase_title: Option<&str>, locale: &Locale) -> String {
    let mut text = locale.get("graph.view.mighty");
    if let Some(name) = style_name {
        text.push_str(" · ");
        text.push_str(name);
        if let Some(phase) = phase_title {
            text.push_str(" · ");
            text.push_str(phase);
        }
    }
    text
}

/// `MobileMightySupport.OutputTail`: a background run's last output lines as the wire carries them, at most six, none when there are none.
pub fn output_tail(lines: Option<&[String]>) -> Option<Vec<String>> {
    let lines = lines.filter(|l| !l.is_empty())?;
    Some(lines[lines.len().saturating_sub(6)..].to_vec())
}

/// Makes a record the page reads out of its JSON (used for parts another module writes, e.g. the role view's copy).
pub fn to_value<T: Serialize>(value: &T) -> Value {
    serde_json::to_value(value).unwrap_or(Value::Null)
}

/// A run's JSON as `Wire.Json` writes it.
pub fn run_value(run: &MightyGraphRun) -> Value {
    serde_json::from_str(&run.to_json()).unwrap_or(Value::Null)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn block(title: &str) -> WebMightyBlock {
        WebMightyBlock {
            kind: "request".into(),
            title: title.into(),
            state: String::new(),
            capsule: None,
            help: String::new(),
            indicator: "none".into(),
            outline: "none".into(),
            status: "completed".into(),
            tone: "done".into(),
            files_run_id: None,
            record_id: None,
            from_record: false,
            provider: Some("claude".into()),
            background: None,
            output_tail: None,
            notice_reply: None,
            notice: None,
            awaiting: None,
            so_far: None,
            flow: None,
        }
    }

    fn projection(blocks: &[(&str, &str)]) -> MightyProjection {
        MightyProjection {
            blocks: blocks.iter().map(|(id, title)| (id.to_string(), block(title))).collect(),
            inputs: WebMightyInputs {
                plan_run_id: None,
                plan_records: vec![],
                galleries: vec![],
                executions: vec![],
                result_files_run_id: None,
                files: IndexMap::new(),
                older: 0,
                retained_start: 0,
                history: None,
                summary: String::new(),
                summary_help: String::new(),
                execution_cards: IndexMap::new(),
                animations: true,
                header: "Mighty".into(),
                fan_out: None,
                log: None,
                progress: None,
            },
            older_key: "0:".into(),
            older_runs: vec![],
            timeline: None,
            structure: None,
        }
    }

    #[test]
    fn only_what_changed_goes_out() {
        let mut sent = MightySent::new();
        let first = sent.message("p", &projection(&[("a", "A"), ("b", "B")])).expect("the whole model");
        assert_eq!(first["blocks"].as_object().map(|b| b.len()), Some(2));
        assert!(first.get("inputs").is_some() && first.get("older").is_some() && first.get("timeline").is_none());
        assert_eq!(sent.message("p", &projection(&[("a", "A"), ("b", "B")])), None, "nothing changed");
        let second = sent.message("p", &projection(&[("a", "A2")])).expect("a change");
        assert_eq!(second["blocks"].as_object().map(|b| b.keys().cloned().collect::<Vec<_>>()), Some(vec!["a".to_string()]));
        assert_eq!(second["removed"], serde_json::json!(["b"]));
        assert!(second.get("inputs").is_none(), "the inputs did not change");
        assert_eq!(sent.last_set, vec!["a".to_string()]);
    }

    #[test]
    fn a_block_writes_the_pages_names_and_leaves_out_what_is_absent() {
        let json = serde_json::to_value(block("T")).expect("json");
        assert_eq!(json["fromRecord"], Value::Bool(false));
        assert!(json.get("filesRunId").is_none() && json.get("soFar").is_none() && json.get("flow").is_none());
    }
}
