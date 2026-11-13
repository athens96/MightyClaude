//! A pane's Mighty view as the page draws it (C# `PaneView.WebMightyModel`, `WebTimeline` and `WebStructure`,
//! MainWindow.WebShell.Mighty.cs:369-460, 536-619): the diagram's blocks, the layout's inputs, the 기록 groups in the timeline presentation
//! and the 파이프라인 boards in the structure one, read with the very calls the diagram makes. What the pane view keeps between pushes
//! ([`MightyPane`]: the opened blocks, the result files, the timeline's flips, the sizes of blocks read back from the record, the structure's
//! card cache) is plain state here, and the page's methods (`mightyExpand`, `mightySaveSize` …) are functions over it and the pane's
//! `RunSession`; what lives outside this crate (the session record, the Ouroboros dashboard, the style engine, the files on disk, the
//! background output) comes in through [`Sources`].

use std::collections::{HashMap, HashSet};
use std::hash::{DefaultHasher, Hash, Hasher};

use indexmap::IndexMap;
use mighty_core::graph::{GraphBlockSize, GraphResponseRecord, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{PlanRecord, RunSession};
use mighty_core::model_label::ModelOption;
use mighty_core::shell::Tone;
use serde_json::Value;

use super::{
    MightyProjection, WebExecution, WebGallery, WebMightyAwaitedTask, WebMightyAwaiting, WebMightyBlock, WebMightyFile, WebMightyHistory,
    WebMightyInputs, WebPlanRecord, WebStructureActivity, WebStructureBoard, WebStructureCard, WebStructureColumn, WebTimelineGroup,
    WebTimelineRow, header, loaded_from_record, older_key, output_tail, request_title, shows_history_block,
};
use crate::activity;
use crate::block_prefs;
use crate::flow::copy;
use crate::flow::roles::{self, AgentDefinition};
use crate::images::{self, AgentImageItem, ImageLocator};
use crate::labels;
use crate::layout::{self, FanOut};
use crate::node;
use crate::notice;
use crate::result_files::ResultFile;
use crate::structure::board::{self, BoardColumn, BoardKind, Card, CardKind};
use crate::structure::{self, ItemCache, Step, StructureStyle, StylePhase, fold, tone};
use crate::support;
use crate::timeline;
use crate::usage_format;
use crate::view_model::{self, MightyGraphBlock, ResultFilesPanel};

/// The Ouroboros dashboard's side of a background execution's block (C# `graphOuroboros`, MainWindow.GraphOuroboros.cs): the host reads
/// the dashboard; the projection asks it for the block's status and the card's copy.
pub trait ExecutionDashboard {
    /// `OuroborosDashboard.BlockStatus(snapshot)` for the execution `key`: "starting" while nothing was read.
    fn block_status(&self, key: &str) -> String;
    /// `WebExecutionCard(key, link, listOpen)`: the execution's card (`WebMightyExecution`) as the page reads it.
    fn card(&self, key: &str, list_open: bool) -> Option<Value>;
}

/// No dashboard read yet: every execution is starting and has no card.
pub struct NoDashboard;

impl ExecutionDashboard for NoDashboard {
    fn block_status(&self, _key: &str) -> String {
        "starting".into()
    }
    fn card(&self, _key: &str, _list_open: bool) -> Option<Value> {
        None
    }
}

/// The pane's guided style as the projection reads it (C# `activeStyle`, the frozen style engine's): its name, its phase as the phase bar
/// shows it, its evaluator for the structure and the request prefix its prompts carry.
pub struct StyleSource<'a> {
    /// `activeStyle.Manifest.Name`.
    pub name: &'a str,
    /// `GuidedPhase(activeStyle)?.Title`, for the toolbar's header.
    pub phase_title: Option<&'a str>,
    /// `activeStyle.Evaluator`.
    pub structure: &'a dyn StructureStyle,
    /// `Evaluator.EffectivePhase(GuidedPhase(style), startingNew)`, for the structure.
    pub phase: Option<&'a StylePhase>,
    /// `StylePresentation.RequestPrefix(registry.Styles, input)`: the style's own name for a request's prompt.
    pub request_prefix: &'a dyn Fn(&str) -> Option<String>,
}

/// What the projection reads that lives outside this crate, gathered by the host each time it projects a pane.
pub struct Sources<'a> {
    /// `GraphHistoryRuns(pane, retained)`: the requests read back from the session record that attach above the retained ones, oldest
    /// first; none when the record belongs to another session.
    pub older: &'a [MightyGraphRun],
    /// The history block's words (`SessionHistoryState.BlockText(older.Count)`, `BlockActs`, `Phase`): the record reader's, or a new
    /// state's when the record does not connect. Sent only while [`shows_history_block`] holds.
    pub history: Option<WebMightyHistory>,
    /// `PlanCardSupport.PendingPlan(...)` is a plan the user can answer, waiting.
    pub pending_plan: bool,
    /// `OuroborosExecutionLinks.Extract(retained)`: each background execution's run id and key.
    pub executions: &'a [(String, String)],
    pub dashboard: &'a dyn ExecutionDashboard,
    /// `LocalPath(Workspace)`: the pane's folder on this computer; none for a remote pane (no file is looked for).
    pub root: Option<&'a str>,
    /// The image path rule (`AgentImagePaths.Locate`).
    pub images: &'a dyn ImageLocator,
    /// `owner.BackgroundOutputFor(pane.Id)`: background runs' last output lines by block id.
    pub tails: &'a HashMap<String, Vec<String>>,
    /// `FlowDefinitions(runs, root)`: the custom agents the blocks' types name, as last read.
    pub definitions: &'a HashMap<String, AgentDefinition>,
    /// `PaneRuntime(pane.Provider)?.ModelCatalog?.Models`.
    pub catalog: Option<&'a [ModelOption]>,
    /// The guided style on, none without one.
    pub style: Option<StyleSource<'a>>,
    /// `AnimationsEnabled`: the system lets things move.
    pub animations: bool,
    /// The reader's time zone for the session log's clocks; none reads the local one.
    pub zone: Option<chrono::FixedOffset>,
}

/// A result the projection found not looked through yet (`LookThroughResultFiles`): the host reads the files it names off the page's
/// thread (`result_files::files_in(&view_model::result_texts(&run), root, …)`) and hands them back with [`MightyPane::found_result_files`].
#[derive(Debug, Clone)]
pub struct ResultFilesLookup {
    pub run: MightyGraphRun,
    pub key: u64,
}

/// `mightyLoadOlder` (`LoadOlderGraphHistory`): what the session record reader is asked for — the agent, the session it continues, the
/// folder its conversations are kept by and the first request the pane holds, above which the older ones attach.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OlderRequest {
    pub provider: String,
    pub resume_id: String,
    pub folder: String,
    pub first_retained: Option<String>,
}

/// `mightyImages` (`WebGalleryImages`): a step's pictures — how many there are and the ones the preview shows, which the host turns into
/// tiles (data URLs).
#[derive(Debug, Clone, PartialEq)]
pub struct GalleryPage {
    pub count: usize,
    pub shown: Vec<AgentImageItem>,
}

/// What a pane view keeps for its Mighty view between pushes (C# `PaneView`'s graph fields).
#[derive(Debug, Default)]
pub struct MightyPane {
    /// `graphExpanded`: the blocks the user opened.
    pub expanded: HashSet<String>,
    /// `graphResultFiles`: which result's file list is open.
    pub result_files: ResultFilesPanel,
    /// `graphResultFilesByRun`: each looked-through result's key and the files it names.
    result_files_by_run: HashMap<String, (u64, Vec<ResultFile>)>,
    /// `graphResultFilesLooking`: the results handed to the host and not back yet.
    result_files_looking: HashSet<(String, u64)>,
    /// The results the last projection found still to look through.
    lookups: Vec<ResultFilesLookup>,
    /// `webTimelineFlipped`: the 기록 groups the user opened or folded against their default.
    pub timeline_flipped: HashSet<String>,
    /// `graphRecordSizes`: the dragged sizes of blocks read back from the record, kept while the pane shows (never saved).
    pub record_sizes: HashMap<String, GraphBlockSize>,
    /// `structureItems`: the structure's cards of the last build.
    structure_items: ItemCache,
    /// `graphExecutionLinks`: the executions the last projection drew, for `mightyOpenDashboard`.
    executions: Vec<(String, String)>,
    /// `webMightyShown`: the page shows this pane's Mighty view (the dashboard is read only then).
    pub shown: bool,
    /// `styleStartingNew`: the next request starts a new pipeline of the style.
    pub style_starting_new: bool,
}

fn records(list: &Option<Vec<Option<GraphResponseRecord>>>) -> Vec<GraphResponseRecord> {
    list.iter().flatten().flatten().cloned().collect()
}

/// `PlanCardSupport.DiagramPlanRunID`: the request the waiting plan is drawn under — the newest request while it is still going.
pub fn diagram_plan_run_id(pending_plan: bool, shows_diagram: bool, runs: &[MightyGraphRun]) -> Option<String> {
    if !shows_diagram || !pending_plan {
        return None;
    }
    let last = support::newest_request_run(runs)?;
    (!support::finished(last)).then(|| last.id().to_string())
}

/// `PlanCardSupport.DiagramRecords`: the answered plans whose request the diagram draws, oldest first, as (graph run id, record id).
pub fn diagram_records(history: Option<&[PlanRecord]>, runs: &[MightyGraphRun]) -> Vec<(String, String)> {
    let mut list = vec![];
    for record in history.unwrap_or(&[]) {
        let Some(graph) = record.graph_run_id.as_deref() else { continue };
        if let Some(run) = runs.iter().rev().find(|r| r.source_run_id.as_deref() == Some(graph) || r.id() == graph) {
            list.push((run.id().to_string(), record.id.clone().unwrap_or_default()));
        }
    }
    list
}

/// The key a result's file list is kept by: what it was read from (C# `HashCode.Combine` over the folder and the result's texts).
fn files_key(folder: &str, run: &MightyGraphRun) -> u64 {
    let mut hash = DefaultHasher::new();
    folder.hash(&mut hash);
    run.result_entries().len().hash(&mut hash);
    run.result_entries().iter().map(|e| e.text().encode_utf16().count()).sum::<usize>().hash(&mut hash);
    run.result_entries().last().map(|e| e.id()).hash(&mut hash);
    run.final_output().map(|f| f.encode_utf16().count()).hash(&mut hash);
    hash.finish()
}

/// `ExecutionBlock`: an execution's block takes its status, its activity mark and its outline from what its dashboard last said.
fn execution_block(mut block: MightyGraphBlock, status: &str, animations: bool) -> MightyGraphBlock {
    block.indicator = view_model::block_indicator(status, animations).into();
    block.outline = activity::outline(if matches!(status, "running" | "waiting") { status } else { "completed" }, animations).into();
    block.status = if matches!(status, "starting" | "unknown") { String::new() } else { status.into() };
    block
}

impl MightyPane {
    pub fn new() -> MightyPane {
        MightyPane::default()
    }

    /// `ResolveResultFiles(own)`: the files each of the pane's own completed requests names, kept until its result's text or the folder
    /// changes; one not looked through yet is left for the host ([`MightyPane::take_result_file_lookups`]).
    fn resolve_result_files(&mut self, own: &[MightyGraphRun], folder: Option<&str>) -> IndexMap<String, Vec<ResultFile>> {
        let mut resolved = IndexMap::new();
        let mut finished = HashSet::new();
        // A remote pane's files are on its host: none is looked for here.
        let Some(folder) = folder else {
            self.result_files_by_run.clear();
            return resolved;
        };
        for run in own.iter().filter(|r| r.status() == "completed" && support::finished(r)) {
            finished.insert(run.id().to_string());
            let key = files_key(folder, run);
            match self.result_files_by_run.get(run.id()) {
                Some((kept, files)) if *kept == key => {
                    resolved.insert(run.id().to_string(), files.clone());
                }
                _ => {
                    if self.result_files_looking.insert((run.id().to_string(), key)) {
                        self.lookups.push(ResultFilesLookup { run: run.clone(), key });
                    }
                }
            }
        }
        self.result_files_by_run.retain(|id, _| finished.contains(id));
        resolved
    }

    /// The results the projections found still to look through, handed to the host once.
    pub fn take_result_file_lookups(&mut self) -> Vec<ResultFilesLookup> {
        std::mem::take(&mut self.lookups)
    }

    /// A result looked through off the page's thread (`LookThroughResultFiles`): what it names, kept by the key it was read at. A result
    /// that could not be looked through names nothing rather than being looked through again on every push.
    pub fn found_result_files(&mut self, run_id: &str, key: u64, files: Vec<ResultFile>) {
        self.result_files_looking.remove(&(run_id.to_string(), key));
        self.result_files_by_run.insert(run_id.to_string(), (key, files));
    }

    // ── the page's methods ──

    /// `mightyVisible` (`WebMightyShown`): the page shows this pane's Mighty view, or stops showing it.
    pub fn set_shown(&mut self, shown: bool) {
        self.shown = shown;
    }

    /// `mightySetPresentation{mode}` (`SetGraphPresentation`): the pane with its presentation, "diagram", "timeline" or "structure"
    /// (`MightyTimeline.ApplyMode`; an unknown word changes nothing).
    pub fn set_presentation(pane: &RunSession, mode: &str) -> RunSession {
        timeline::apply_mode(pane, mode)
    }

    /// `mightySaveSize{nodeId, target, size}` (`WebSaveSize`): a block drag released in the page, saved as the WinUI drag saves it — the
    /// newest result and the waiting plan keep the pane's one size, any other block its own; a block read back from the record (`older`,
    /// the record's runs) keeps its size here only. The pane changed, or none when only this state did.
    pub fn save_size(
        &mut self,
        pane: &RunSession,
        node_id: &str,
        target: &str,
        size: Option<GraphBlockSize>,
        older: &[MightyGraphRun],
    ) -> Option<RunSession> {
        match target {
            "result" => Some(RunSession { graph_result_size: size.and_then(|s| s.normalized()), ..pane.clone() }),
            "plan" => Some(RunSession { graph_plan_size: size.and_then(|s| s.normalized()), ..pane.clone() }),
            _ => match size {
                None => {
                    self.record_sizes.remove(node_id);
                    Some(block_prefs::set(pane, node_id, None))
                }
                Some(size) if !loaded_from_record(node_id, older) => Some(block_prefs::set(pane, node_id, Some(&size))),
                Some(size) => {
                    if let Some(kept) = size.normalized_for(node_id) {
                        self.record_sizes.insert(node_id.to_string(), kept);
                    }
                    None
                }
            },
        }
    }

    /// `mightyExpand{nodeId, expanded}` (`WebExpand`): the saved size goes and the block opens or folds. The pane without the size.
    pub fn expand(&mut self, pane: &RunSession, node_id: &str, expanded: bool) -> RunSession {
        self.record_sizes.remove(node_id);
        let next = block_prefs::set(pane, node_id, None);
        if expanded {
            self.expanded.insert(node_id.to_string());
        } else {
            self.expanded.remove(node_id);
        }
        next
    }

    /// `mightyResultFiles{runId}` (`WebResultFiles`): a result card's file toggle, or the list's close with no run. A result that names
    /// no file has no list to open.
    pub fn result_files(&mut self, run_id: Option<&str>) {
        match run_id {
            None => self.result_files.close(),
            Some(id) => {
                let named = self.result_files_by_run.get(id).is_some_and(|(_, files)| !files.is_empty());
                self.result_files.toggle(id, named);
            }
        }
    }

    /// `mightyTimelineFlip{runId}` (`WebTimelineFlip`): a 기록 group opened or folded against its default.
    pub fn timeline_flip(&mut self, run_id: &str) {
        if !self.timeline_flipped.remove(run_id) {
            self.timeline_flipped.insert(run_id.to_string());
        }
    }

    /// `mightyLogCollapsed{collapsed}` (`WebLogCollapsed`): the pane with its session log strip folded or open (`FlowCopy.Collapse`).
    pub fn log_collapsed(pane: &RunSession, collapsed: bool) -> RunSession {
        copy::collapse(pane, collapsed)
    }

    /// `mightyOpenDashboard{key}` (`WebOpenDashboard`): the execution the last projection drew under `key`, as (run id, key), for the host
    /// to open its dashboard; none for a key it did not draw.
    pub fn open_dashboard(&self, key: &str) -> Option<&(String, String)> {
        self.executions.iter().find(|(_, k)| k == key)
    }

    /// `mightyLoadOlder` (`LoadOlderGraphHistory`): the session record read the pane asks for — an agent pane of a provider whose records
    /// are read, with a folder on this computer (`LocalPath(DelegationPanes.RunWorkspace(pane, Workspace))`); none otherwise. Whether a read
    /// is already on its way is the record reader's (`SessionHistoryState.Begin`).
    pub fn load_older(pane: &RunSession, folder: Option<&str>) -> Option<OlderRequest> {
        if pane.kind() != "claude" || !super::HISTORY_PROVIDERS.contains(&pane.provider()) {
            return None;
        }
        Some(OlderRequest {
            provider: pane.provider().to_string(),
            resume_id: pane.resume_id.clone().unwrap_or_default(),
            folder: folder?.to_string(),
            first_retained: pane.graph_runs().first().map(|r| r.id().to_string()),
        })
    }

    /// `mightyImages{runId, step, expanded}` (`WebGalleryImages`): a step's pictures in a run the pane or its record holds — how many,
    /// and the ones the preview shows (all of them while it is opened); none for a run neither holds.
    pub fn images(
        pane: &RunSession,
        older: &[MightyGraphRun],
        run_id: &str,
        step: &str,
        expanded: bool,
        root: Option<&str>,
        locator: &dyn ImageLocator,
    ) -> Option<GalleryPage> {
        let run = older.iter().chain(pane.graph_runs()).find(|r| r.id() == run_id)?;
        let items = images::items(run, step, root, locator);
        let count = items.len();
        Some(GalleryPage { count, shown: items.into_iter().take(layout::visible_images(count, expanded)).collect() })
    }
}

/// `WebMightyModel(pane)`: the pane's Mighty view as the page draws it, read with the same calls the diagram makes. The timeline is made
/// only in the timeline presentation and the structure only in the structure one.
pub fn project(pane: &RunSession, state: &mut MightyPane, sources: &Sources, locale: &Locale) -> MightyProjection {
    let retained = pane.graph_runs();
    let older = sources.older;
    let runs: Vec<MightyGraphRun> = older.iter().chain(retained).cloned().collect();
    let presentation = timeline::mode(pane);
    // Each result card names its own request's files; the newest that names any opens its list by itself, once.
    let files = state.resolve_result_files(retained, sources.root);
    let published: Vec<(String, bool)> =
        retained.iter().filter_map(|r| files.get(r.id()).map(|f| (r.id().to_string(), !f.is_empty()))).collect();
    state.result_files.publish(&published);
    let plan_run_id = diagram_plan_run_id(sources.pending_plan, true, &runs);
    state.executions = sources.executions.to_vec();
    let galleries = images::galleries(&runs, sources.root, older.len(), sources.images);
    let plan_records = diagram_records(pane.plan_history.as_deref(), &runs);
    // A Claude pane's flow is its role view: every card is laid out open here, so every block the page may open has its copy.
    let fan_out = pane.kind() == "claude" && pane.provider() == "claude";
    let history = shows_history_block(pane.provider(), retained, pane.resume_id.as_deref());
    // The copy is the layout's blocks; frames do not matter here, so the layout is made without a viewport.
    let options = layout::Options {
        result_files_run_id: state.result_files.selected_run_id().map(str::to_string),
        retained_start: older.len(),
        history,
        plan_run_id: plan_run_id.clone(),
        plan_records: plan_records.clone(),
        splits_execution: true,
        galleries: galleries.clone(),
        executions: sources.executions.to_vec(),
        fan_out: fan_out.then(FanOut::open_all),
        ..layout::Options::default()
    };
    let draft = pane.draft.as_deref().unwrap_or("");
    let made = layout::make(&runs, draft, pane.status() == "running", &state.expanded, &options, locale);
    let provider_label = labels::provider_label(pane.provider());
    let blocks = view_model::blocks(&made, &runs, draft, provider_label, sources.animations, sources.catalog, Some(true), locale);
    // The background sub-agents and tasks by their diagram node, for their kind, output lines and notice reply.
    let mut background: HashMap<String, &MightyGraphAgent> = HashMap::new();
    for run in &runs {
        for agent in run.agents() {
            if agent.is_task() || agent.is_background_agent() {
                background.entry(support::node_id(run, &format!("agent:{}", agent.id()))).or_insert(agent);
            }
        }
    }
    let tails = sources.tails;
    let output = |id: &str| tails.get(id).cloned().unwrap_or_default();
    let flow = copy::blocks(&made, &runs, fan_out, sources.definitions, &output, locale);
    let ordinals = support::request_ordinals(&runs);
    let mut cards: IndexMap<String, Value> = IndexMap::new();
    let mut copies: IndexMap<String, WebMightyBlock> = IndexMap::new();
    for raw in blocks {
        let mut block = raw;
        let index = if block.kind == "request" { runs.iter().position(|r| block.id == support::node_id(r, "request")) } else { None };
        if let Some(i) = index {
            block.title = graph_request_title(&runs[i], ordinals[i], pane.provider(), sources, locale);
        }
        let notice = index.is_some_and(|i| runs[i].is_notice());
        if block.kind == layout::EXECUTION_KIND {
            let status = block.record_id.as_deref().map_or_else(|| "starting".to_string(), |key| sources.dashboard.block_status(key));
            block = execution_block(block, &status, sources.animations);
            if let Some(key) = block.record_id.as_deref()
                && sources.executions.iter().any(|(_, k)| k == key)
                && let Some(card) = sources.dashboard.card(key, state.expanded.contains(&block.id))
            {
                cards.insert(block.id.clone(), card);
            }
        }
        let from_record = loaded_from_record(&block.id, older) && (block.kind == "request" || block.kind == layout::WORK_KIND);
        let held = background.get(&block.id).copied();
        let tone = view_model::tone(&block).name().to_string();
        let provider = if notice { None } else { view_model::title_provider(&block, pane.provider()).map(str::to_string) };
        copies.insert(
            block.id.clone(),
            WebMightyBlock {
                kind: block.kind,
                title: block.title,
                state: block.state,
                capsule: block.capsule,
                help: block.capsule_help,
                indicator: block.indicator,
                outline: block.outline,
                status: block.status,
                tone,
                files_run_id: block.result_files_run_id,
                record_id: block.record_id,
                from_record,
                provider,
                background: held.map(|_| true),
                output_tail: held.filter(|a| a.is_task()).and_then(|a| output_tail(tails.get(a.id()).map(Vec::as_slice))),
                notice_reply: held.and_then(|a| a.notice_reply.clone()).filter(|r| !r.is_empty()),
                notice: notice.then_some(true),
                awaiting: block.awaiting.map(|awaiting| WebMightyAwaiting {
                    tasks: awaiting
                        .tasks
                        .iter()
                        .map(|t| WebMightyAwaitedTask {
                            title: t.title.clone(),
                            status: t.status.clone(),
                            word: notice::awaited_word(t, locale),
                        })
                        .collect(),
                    progress: notice::awaiting_progress(&awaiting, locale),
                }),
                so_far: block.so_far.then_some(true),
                flow: flow.get(&block.id).map(copy::Block::to_json),
            },
        );
    }
    // A step, a delegate line and a return node have only their role-view copy: a step its title, state and status.
    for node in &made.nodes {
        if !matches!(node.kind.as_str(), layout::DELEGATE_KIND | layout::RETURN_KIND | layout::STEP_KIND) {
            continue;
        }
        let Some(own) = flow.get(&node.id) else { continue };
        let tone = if node.kind == layout::RETURN_KIND {
            "run"
        } else {
            match own.step.as_ref().map(|s| s.status.as_str()) {
                Some("running") => "run",
                Some("completed") => "done",
                Some("error") => "err",
                Some("stopped") => "stop",
                _ => "idle",
            }
        };
        copies.insert(
            node.id.clone(),
            WebMightyBlock {
                kind: node.kind.clone(),
                title: own.step.as_ref().map_or_else(String::new, |s| s.title.clone()),
                state: own.step.as_ref().map_or_else(String::new, |s| s.state_word.clone()),
                capsule: None,
                help: String::new(),
                indicator: "none".into(),
                outline: activity::NONE.into(),
                status: own.step.as_ref().map_or_else(String::new, |s| s.status.clone()),
                tone: tone.into(),
                files_run_id: None,
                record_id: None,
                from_record: false,
                provider: None,
                background: None,
                output_tail: None,
                notice_reply: None,
                notice: None,
                awaiting: None,
                so_far: None,
                flow: Some(own.to_json()),
            },
        );
    }
    let mut summary = view_model::toolbar_summary(retained, locale);
    if !older.is_empty() {
        summary.push_str(" · ");
        summary.push_str(&locale.get_with("graph.history.headerLoaded", &[("count", &older.len().to_string())]));
    }
    let style = sources.style.as_ref();
    let inputs = WebMightyInputs {
        plan_run_id,
        plan_records: plan_records.into_iter().map(|(run_id, record_id)| WebPlanRecord { run_id, record_id }).collect(),
        galleries: galleries.into_iter().map(|g| WebGallery { run_id: g.run_id, step: g.step, count: g.count as i32 }).collect(),
        executions: sources.executions.iter().map(|(run_id, key)| WebExecution { run_id: run_id.clone(), key: key.clone() }).collect(),
        result_files_run_id: state.result_files.selected_run_id().map(str::to_string),
        files: files
            .into_iter()
            .map(|(id, list)| (id, list.into_iter().map(|f| WebMightyFile { path: f.path, line: f.line }).collect()))
            .collect(),
        older: older.len() as i32,
        retained_start: older.len() as i32,
        history: if history { sources.history.clone() } else { None },
        summary,
        summary_help: view_model::toolbar_help(retained, locale),
        execution_cards: cards,
        animations: sources.animations,
        header: header(style.map(|s| s.name), style.and_then(|s| s.phase_title), locale),
        fan_out: fan_out.then_some(true),
        log: if fan_out {
            copy::log_of(&runs, pane.flow_log_collapsed == Some(true), sources.definitions, sources.zone, locale).map(|l| l.to_json())
        } else {
            None
        },
        // Every agent pane has the progress panel: the request in view, its state, and the plan its agent keeps, if any.
        progress: Some(Value::Object(copy::progress_of(&runs, locale).into_iter().map(|(id, p)| (id, p.to_json())).collect())),
    };
    MightyProjection {
        blocks: copies,
        inputs,
        older_key: older_key(older),
        older_runs: older.to_vec(),
        timeline: (presentation == "timeline").then(|| web_timeline(&runs, older.len(), state, sources, pane.provider(), locale)),
        structure: (presentation == "structure").then(|| web_structure(pane, &runs, state, sources, locale)),
    }
}

/// `GraphRequestTitle(run, ordinal)`: a notice run titled after the work Claude replied to; a request after its style's prefix.
fn graph_request_title(run: &MightyGraphRun, ordinal: i32, provider: &str, sources: &Sources, locale: &Locale) -> String {
    let prefix = sources.style.as_ref().and_then(|s| (s.request_prefix)(run.input()));
    request_title(run, ordinal, labels::provider_label(provider), prefix.as_deref(), locale)
}

/// `WebTimeline`: the timeline's groups (`MightyTimeline.Groups`), each row's words built here.
fn web_timeline(
    runs: &[MightyGraphRun],
    older: usize,
    state: &MightyPane,
    sources: &Sources,
    provider: &str,
    locale: &Locale,
) -> Vec<WebTimelineGroup> {
    let shown_runs = support::shown_runs(runs, locale);
    let mut groups = vec![];
    for group in timeline::groups(runs, locale) {
        let run = &shown_runs[group.run_index];
        let rows = group
            .rows
            .iter()
            .map(|row| {
                let agent = row.agent_index.and_then(|at| run.agents().get(at));
                // Who the block is, as its diagram header says it: a sub-agent's type and marks, the main session's effort, its role's ink.
                let who = match agent {
                    Some(a) => roles::subagent(
                        a,
                        a.identity.as_ref().and_then(|i| i.kind.as_deref()).and_then(|t| sources.definitions.get(t)),
                        locale,
                    ),
                    None if row.is_execution => Some(roles::lead(run, locale)),
                    None => None,
                };
                let capsule = if row.is_execution {
                    usage_format::block_capsule(
                        run.usage,
                        &records(&run.response_records),
                        run.node_model_label.as_deref(),
                        sources.catalog,
                        true,
                        locale,
                    )
                } else if let Some(a) = agent {
                    let label = who.as_ref().and_then(|w| w.model_label.clone());
                    usage_format::block_capsule(a.usage, &records(&a.response_records), label.as_deref(), sources.catalog, true, locale)
                } else {
                    None
                };
                let mut words = vec![timeline::kind_label(&row.kind, agent.map(|a| a.input()), locale)];
                if let Some(who) = &who {
                    words.extend(who.type_chip.clone());
                    words.extend(who.effort.clone());
                    words.extend(who.marks.iter().map(|m| roles::word(*m, locale)));
                }
                words.retain(|w| !w.is_empty());
                let input = match agent {
                    Some(a) => Some(support::shown_input(a).to_string()),
                    None if row.kind == "main" => Some(run.input().to_string()),
                    None => None,
                };
                WebTimelineRow {
                    node_id: row.node_id.clone(),
                    kind: row.kind.clone(),
                    kind_label: words.join(" · "),
                    title: row.title.clone(),
                    status: row.status.clone(),
                    status_word: if row.status == timeline::REQUEST_STATUS {
                        String::new()
                    } else {
                        view_model::state_label(&row.status, locale)
                    },
                    tone: row.node.tone.name().into(),
                    ring: row.node.ring,
                    rail: row.node.rail.map(|r| r.name().to_string()),
                    latest: row.latest.clone(),
                    duration: timeline::duration_label(row.duration_ms, locale),
                    capsule,
                    input,
                    agent_index: row.agent_index.map(|i| i as i32),
                    ink: who.map(|w| roles::token(w.ink).to_string()),
                }
            })
            .collect();
        let tally = locale
            .get_with("phone.blocks.tally", &[("total", &group.tally.total.to_string()), ("settled", &group.tally.settled.to_string())]);
        let mut caption = if group.notice_run {
            notice::heading_of(group.notice_title.as_deref(), locale)
        } else {
            locale.get_with("graph.timeline.requestOrdinal", &[("n", &group.ordinal.to_string())])
        };
        if let Some(total) = support::total_usage(run).filter(|t| !node::is_empty(t)) {
            caption.push_str(" · ");
            caption.push_str(&node::summary(&total, locale));
        }
        let result = group.result.as_ref();
        groups.push(WebTimelineGroup {
            run_id: group.run_id.clone(),
            ordinal: group.ordinal,
            title: graph_request_title(run, group.ordinal, provider, sources, locale),
            status: group.status.clone(),
            status_word: view_model::run_state_label(run, locale),
            tone: Tone::of(&group.status).name().into(),
            input: group.input.clone(),
            tally,
            open: timeline::group_open(&group, runs, &state.timeline_flipped),
            older: group.run_index < older,
            rows,
            result_node_id: result.map(|r| r.node_id.clone()),
            result_tone: result.map(|r| r.tone.name().to_string()),
            result: result.and_then(|r| r.text.clone()),
            result_folds: result.and_then(|r| r.text.as_deref()).is_some_and(timeline::folds),
            result_caption: result.map(|_| caption),
            notice_run: group.notice_run.then_some(true),
            // How the request ended, or 알림 대기 중 while it awaits its background work's notices.
            result_title: result.map(|r| view_model::result_title(run.status(), support::background_waiting(run), r.awaiting, locale)),
        });
    }
    groups
}

/// `StatusGlyph.Tone(MightyTimeline.Status(status))` as the page's word.
fn status_tone(status: &str) -> String {
    Tone::of(timeline::status(status)).name().into()
}

/// `WebStructure`: the structure's boards (`MightyStructure.Make`, `Boards`), each card's words built here.
fn web_structure(
    pane: &RunSession,
    runs: &[MightyGraphRun],
    state: &mut MightyPane,
    sources: &Sources,
    locale: &Locale,
) -> Vec<WebStructureBoard> {
    // StructureInputs: the pane's style, its phase as the phase bar shows it, and the request whose plan waits for an answer.
    let pending = diagram_plan_run_id(sources.pending_plan, true, runs);
    let style = sources.style.as_ref();
    let inputs = structure::Inputs {
        style: style.map(|s| s.structure),
        current_phase: style.and_then(|s| s.phase),
        starting_new: state.style_starting_new,
        plan_history: pane.plan_history.as_deref(),
        pending_plan_run_id: pending.as_deref(),
    };
    let signature = structure::Signature::new(runs, &inputs, locale);
    let items = state.structure_items.items(runs, &signature, locale);
    let boards = board::boards(&structure::make(runs, &inputs, Some(&items), locale), locale);
    let step_word = |step: Step| {
        locale.get(match step {
            Step::Done | Step::Error | Step::Stopped => "graph.structure.step.done",
            Step::Current => "graph.structure.step.current",
            Step::Pending => "graph.structure.step.pending",
        })
    };
    let card_of = |card: &Card, column: &BoardColumn| -> WebStructureCard {
        let item = &card.item;
        let node = card.node.as_ref();
        let title = node
            .map(|n| n.title.clone())
            .unwrap_or_else(|| graph_request_title(&runs[item.run_index], item.ordinal, pane.provider(), sources, locale));
        let status = node.map_or(item.status.as_str(), |n| n.status.as_str()).to_string();
        let activity = node
            .map_or(&item.activity, |n| &n.activity)
            .iter()
            .map(|a| WebStructureActivity { kind: a.kind.clone(), state: a.state.clone(), text: a.text.clone() })
            .collect();
        let model = node.and_then(|n| n.model.as_deref()).map(|id| usage_format::short_name(id, None, true));
        let text = match card.kind {
            CardKind::Agent => node.and_then(|n| n.output.as_ref()).map(|e| e.preview.clone()),
            _ => item.request.as_ref().map(|e| e.preview.clone()),
        };
        let summary = matches!(card.kind, CardKind::Result | CardKind::Summary);
        let ended = matches!(item.status.as_str(), "error" | "stopped");
        WebStructureCard {
            id: card.id(),
            kind: card.kind.word().into(),
            run_id: item.run_id.clone(),
            node_id: node
                .map(|n| n.node_id.clone())
                .unwrap_or_else(|| if card.kind == CardKind::Execution { item.execution_node_id.clone() } else { item.node_id.clone() }),
            title,
            status_word: view_model::state_label(&status, locale),
            tone: status_tone(&status),
            status,
            parent: card.parent.as_ref().map(|p| locale.get_with("graph.structure.underParent", &[("title", p)])),
            text,
            message: item.message.as_ref().map(|e| e.preview.clone()),
            answer: item.answer.as_ref().map(|e| e.text.clone()),
            activity,
            tally: Some(locale.get_with(
                "graph.structure.tally",
                &[("agents", &item.tally.agents.to_string()), ("tools", &item.tally.tools.to_string())],
            )),
            finished: item.finished,
            result_line: fold::result_line(item),
            node_kind: node.map(|n| n.kind.clone()),
            // A task notice's title already names its kind; a sub-agent's type follows its kind, as its diagram header names it.
            meta: node.map(|n| {
                let kind = (n.kind != "steer").then(|| timeline::kind_label(&n.kind, None, locale));
                [kind, n.agent_type.clone(), model.clone()].into_iter().flatten().filter(|p| !p.is_empty()).collect::<Vec<_>>().join(" · ")
            }),
            depth: node.map_or(0, |n| n.depth as i32),
            prompt: node.and_then(|n| n.prompt.as_ref()).map(|e| e.preview.clone()),
            execution_node_id: Some(item.execution_node_id.clone()),
            result_node_id: Some(support::node_id(&runs[item.run_index], "result")),
            tint: tone::of_card(card, column).map(|t| t.name().to_string()),
            background: node.filter(|n| n.background).map(|_| true),
            output_tail: node.filter(|n| n.kind == "task").and_then(|n| output_tail(sources.tails.get(&n.agent_id).map(Vec::as_slice))),
            notice_reply: node.and_then(|n| n.notice_reply.as_ref()).map(|e| e.text.clone()),
            result_title: summary.then(|| view_model::result_title(&item.status, item.background_waiting, item.awaiting, locale)),
            result_awaiting: (summary && item.awaiting && !ended).then_some(true),
            result_waiting: (summary && !item.awaiting && item.background_waiting > 0 && !ended).then_some(true),
        }
    };
    boards
        .iter()
        .map(|board| WebStructureBoard {
            id: board.id.clone(),
            kind: board.kind.word().into(),
            ordinal: board.ordinal as i32,
            headline: board.headline.clone(),
            status: board.status.clone(),
            status_word: view_model::state_label(&board.status, locale),
            tone: status_tone(&board.status),
            run_ids: board.run_ids(),
            columns: board
                .columns
                .iter()
                .map(|column| WebStructureColumn {
                    id: column.id().to_string(),
                    title: column.step.title.clone().unwrap_or_else(|| locale.get(&format!("graph.structure.column.{}", column.id()))),
                    step: structure::word(column.step.step).into(),
                    step_word: step_word(column.step.step),
                    count: column.count as i32,
                    cards: column.cards.iter().map(|card| card_of(card, column)).collect(),
                    tone: tone::of_column(column).map(|t| t.name().to_string()),
                })
                .collect(),
            style_name: (board.kind == BoardKind::Pipeline).then(|| style.map(|s| s.name.to_string())).flatten(),
            quiet: board.quiet().then_some(true),
        })
        .collect()
}
