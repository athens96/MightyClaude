//! C# CodexSessionWatcherVerification.cs:111 `RunningProcessPublishesGraphBeforeItExits`: a Codex run's graph reaches the host every
//! second while the CLI still runs, and a stop publishes one settled graph; with it, the runner's `ReadChildren` (the sub-agents' files
//! read with each of those publishes and once more at the end, from the CLI's environment). mighty-graph's tracker is not reachable from
//! here, so a graph standing in for it takes the frames (the watcher and the tracker's merge are mighty-history's tests/codex_sessions.rs).
//! Windows only: the Mac ProcessHost is W1-D's.
#![cfg(windows)]

mod common;

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AgentActivity, LogEntry, Workspace};
use mighty_run::graph::{GraphFactory, GraphSink, GraphStart};
use mighty_run::model::{RunEvent, StartRunRequest};
use mighty_run::mods::ModMetadata;
use mighty_run::{RunManager, RunManagerOptions};
use serde_json::Value;

/// What the stand-in graph saw of the runner's `ReadChildren`: whether each read was the closing one, and whether it had the CLI's
/// environment.
type Reads = Arc<Mutex<Vec<(bool, bool)>>>;

struct LiveGraph(Reads);

struct LiveSink {
    start: GraphStart,
    status: String,
    answer: Option<String>,
    reads: Reads,
}

impl GraphFactory for LiveGraph {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink> {
        Box::new(LiveSink { start, status: "running".into(), answer: None, reads: self.0.clone() })
    }
}

impl GraphSink for LiveSink {
    fn run_id(&self) -> String {
        self.start.run_id.clone()
    }
    /// The answer of Codex's `agent_message`, as the tracker makes it the run's final output.
    fn frame(&mut self, _provider: &str, frame: &Value) {
        if frame["type"] == "item.completed" && frame["item"]["type"] == "agent_message" {
            self.answer = frame["item"]["text"].as_str().map(str::to_string);
            (self.start.node)(frame.clone());
        }
    }
    fn notice_revision(&self) -> u64 {
        0
    }
    fn activity(&mut self, _activity: &AgentActivity, _tool_id: Option<&str>) -> bool {
        false
    }
    fn images(&mut self, _entry: &LogEntry, _tool_id: Option<&str>, _parent_tool_id: Option<&str>) -> bool {
        false
    }
    fn mod_event(&mut self, _event: &ModMetadata) {}
    fn steer(&mut self, _id: &str, _text: &str) {}
    fn note_effort(&mut self, _effort: &str) {}
    fn finish(&mut self, state: &str) {
        self.status = state.into();
    }
    fn build_run(&mut self, _include_running: bool) -> Option<MightyGraphRun> {
        Some(MightyGraphRun {
            id: Some(self.start.run_id.clone()),
            status: Some(self.status.clone()),
            final_output: self.answer.clone(),
            provider: Some(self.start.provider.clone()),
            ..MightyGraphRun::default()
        })
    }
    fn take_notice_runs(&mut self, _include_running: bool) -> Vec<MightyGraphRun> {
        vec![]
    }
    fn read_children(&mut self, environment: &BTreeMap<String, String>, closing: bool) {
        self.reads.lock().unwrap().push((closing, environment.keys().any(|k| k.eq_ignore_ascii_case("PATH"))));
    }
}

/// CodexSessionWatcherVerification.cs:111 `RunningProcessPublishesGraphBeforeItExits`.
#[tokio::test(flavor = "multi_thread")]
async fn running_process_publishes_graph_before_it_exits() {
    common::korean();
    let directory = common::temp("live-graph");
    let workspace = Workspace { path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() };
    let record = directory.join("fake").to_string_lossy().into_owned();
    let catalog = common::fake_catalog(&["--fake-cli", "codex", &record, "--hold-run"]);
    let events: Arc<Mutex<Vec<RunEvent>>> = Arc::default();
    let reads: Reads = Arc::default();
    let (list, resolved) = (events.clone(), workspace.clone());
    let mut options = RunManagerOptions::new(
        "",
        Arc::new(move |_| {
            let workspace = resolved.clone();
            Box::pin(async move { Ok(workspace) })
        }),
        Arc::new(move |event| list.lock().unwrap().push(event)),
    );
    options.graph = Some(Arc::new(LiveGraph(reads.clone())));
    let manager = RunManager::new(catalog, options);
    let request =
        StartRunRequest::new("live-graph", workspace.id.as_deref().unwrap(), "claude", "show live progress").with_provider("codex");
    manager.start(request).await.unwrap();
    let all = || events.lock().unwrap().clone();
    common::until(
        || {
            all().iter().any(|e| {
                e.graph_run
                    .as_ref()
                    .is_some_and(|g| g.status.as_deref() == Some("running") && g.final_output.as_deref() == Some("FAKE_CLI_OK"))
            })
        },
        15000,
        "a live graph with the answer",
    )
    .await;
    assert!(
        manager.is_running("live-graph") && !all().iter().any(|e| matches!(e.status.as_deref(), Some("completed" | "error" | "stopped"))),
        "live graph reaches the host before process completion"
    );
    common::until(|| !reads.lock().unwrap().is_empty(), 5000, "the sub-agents' files are read while the run goes on").await;
    manager.stop("live-graph").await.unwrap();
    let all = all();
    assert!(
        all.iter().rev().find(|e| e.kind == "status").and_then(|e| e.status.as_deref()) == Some("stopped")
            && all.iter().rev().find_map(|e| e.graph_run.as_ref()).and_then(|g| g.status.as_deref()) == Some("stopped"),
        "stop publishes one final settled graph and cancels polling"
    );
    let reads = reads.lock().unwrap().clone();
    assert!(
        reads.iter().all(|(_, environment)| *environment)
            && reads.iter().filter(|(closing, _)| *closing).count() == 1
            && reads.last() == Some(&(true, true)),
        "each read has the CLI's environment, and the last one closes: {reads:?}"
    );
    manager.dispose().await;
    common::remove_when_free(&directory).await;
}
