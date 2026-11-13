//! Where a run's execution graph is made: the seam between the run engine and the graph tracker (C# `ExecutionGraphTracker`,
//! mighty-graph). [`GraphSink`] is every call `RunManager.ExecuteAsync` makes on its tracker, in the same places; [`GraphFactory`]
//! makes one per run, as `new ExecutionGraphTracker(runId, input, provider, model, onNode, effort)` does.
//!
//! The tracker hands each node it changed to `GraphStart::node` (as JSON, the remote wire's `graph` event): the runner marks the
//! graph dirty for its one-second publish and passes the node to a sharing host's `GraphNodes`, never after the run's last status.
//! A sink is called from the run's task, from the Mods bridge and from the permission channel; calls come one at a time.

use std::sync::Arc;

use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AgentActivity, LogEntry};
use serde_json::Value;

use crate::mods::ModMetadata;

/// What a run's graph starts from (the tracker's constructor arguments).
#[derive(Clone)]
pub struct GraphStart {
    /// The id the request's diagram block carries (`StartRunRequest.GraphRunId`, else a new one).
    pub run_id: String,
    pub input: String,
    pub provider: String,
    /// `PhaseModelPreferences.EffectiveModel(request)`.
    pub model: String,
    /// The pane's effort the request starts with (macOS beginGraphRun).
    pub effort: Option<String>,
    /// Hears every node the tracker changed.
    pub node: Arc<dyn Fn(Value) + Send + Sync>,
}

/// C# `ExecutionGraphTracker` as `RunManager` uses it.
pub trait GraphSink: Send {
    /// `RunID`: the graph run's id; the plan record and the checklist name it.
    fn run_id(&self) -> String;
    /// `Consume`: one stream frame of the run (Claude's stream-json, Codex's JSONL, Antigravity's events; a Bedrock denial already
    /// shown as guidance).
    fn frame(&mut self, provider: &str, frame: &Value);
    /// `NoticeRevision`: grows when a background notice opens a run of its own (the runner publishes the graph at once).
    fn notice_revision(&self) -> u64;
    /// `Activity(value, toolId)`: a tool row of the run; true when a child graph took it (the pane then never shows it).
    fn activity(&mut self, activity: &AgentActivity, tool_id: Option<&str>) -> bool;
    /// `Images(entry, toolId, parentToolId)`: a picture entry; true when it belongs to a child graph.
    fn images(&mut self, entry: &LogEntry, tool_id: Option<&str>, parent_tool_id: Option<&str>) -> bool;
    /// `ReceiveMod`: a Mods event of the run.
    fn mod_event(&mut self, event: &ModMetadata);
    /// `Steer(id, text)`: a steer reopened the turn with `text`.
    fn steer(&mut self, id: &str, text: &str);
    /// `NoteEffort`: the effort the run starts with once Settings › Models resolved it.
    fn note_effort(&mut self, effort: &str);
    /// `Finish(state)`: the run ended completed, error or stopped.
    fn finish(&mut self, state: &str);
    /// `BuildRun(includeRunning)`: the request's graph run as it is now.
    fn build_run(&mut self, include_running: bool) -> Option<MightyGraphRun>;
    /// `TakeNoticeRuns(includeRunning)`: each notice run the run's background work opened.
    fn take_notice_runs(&mut self, include_running: bool) -> Vec<MightyGraphRun>;
    /// `AntigravityAnswer`: an Antigravity reply as the transcript shows it (W5).
    fn antigravity_answer(&mut self, _text: &str) {}
    /// `CodexRootThread`: the Codex thread the run's session files are under (W5).
    fn codex_root_thread(&self) -> Option<String> {
        None
    }
}

/// Makes one [`GraphSink`] per run (the lead plugs mighty-graph's tracker in here).
pub trait GraphFactory: Send + Sync {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink>;
}
