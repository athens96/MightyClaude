//! C# CodexSessionWatcherVerification.cs:111 `RunningProcessPublishesGraphBeforeItExits`: a Codex run's graph reaches the host every
//! second while the CLI still runs, and a stop publishes one settled graph; with it, the runner's `ReadChildren` (the sub-agents' files
//! read with each of those publishes and once more at the end, from the CLI's environment). mighty-graph's tracker is not reachable from
//! here, so a graph standing in for it takes the frames (the watcher and the tracker's merge are mighty-history's tests/codex_sessions.rs).
//! The read runs without the graph's lock: a slow one holds up neither the run's output nor a card's answer.
//! Windows only: the Mac ProcessHost is W1-D's.
#![cfg(windows)]

mod common;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AgentActivity, LogEntry, Workspace};
use mighty_run::graph::{ChildRead, ChildSources, ChildrenFound, GraphFactory, GraphSink, GraphStart};
use mighty_run::model::CliCommand;
use mighty_run::model::{RunEvent, StartRunRequest};
use mighty_run::mods::ModMetadata;
use mighty_run::permissions::ToolPermissionRequest;
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
    fn read_children(&mut self, sources: &ChildSources<'_>, closing: bool) -> Option<ChildRead> {
        self.reads.lock().unwrap().push((closing, sources.environment.keys().any(|k| k.eq_ignore_ascii_case("PATH"))));
        None
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

/// A read of the sub-agents' files that takes as long as the check wants: it says when it began and waits to be let go (30 s at most).
#[derive(Default)]
struct SlowRead {
    began: AtomicBool,
    released: AtomicBool,
    /// Each stream frame the graph took, and whether the read was under way then.
    frames: Mutex<Vec<bool>>,
}

impl SlowRead {
    fn reading(&self) -> bool {
        self.began.load(Ordering::SeqCst) && !self.released.load(Ordering::SeqCst)
    }
}

struct SlowGraph(Arc<SlowRead>);

struct SlowSink(GraphStart, Arc<SlowRead>);

impl GraphFactory for SlowGraph {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink> {
        Box::new(SlowSink(start, self.0.clone()))
    }
}

impl GraphSink for SlowSink {
    fn run_id(&self) -> String {
        self.0.run_id.clone()
    }
    fn frame(&mut self, _provider: &str, _frame: &Value) {
        let reading = self.1.reading();
        self.1.frames.lock().unwrap().push(reading);
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
    fn finish(&mut self, _state: &str) {}
    fn build_run(&mut self, _include_running: bool) -> Option<MightyGraphRun> {
        None
    }
    fn take_notice_runs(&mut self, _include_running: bool) -> Vec<MightyGraphRun> {
        vec![]
    }
    /// The first read is the slow one; the later ones find nothing.
    fn read_children(&mut self, _sources: &ChildSources<'_>, _closing: bool) -> Option<ChildRead> {
        if self.1.began.swap(true, Ordering::SeqCst) {
            return None;
        }
        let read = self.1.clone();
        Some(Box::new(move || {
            let end = Instant::now() + Duration::from_secs(30);
            while !read.released.load(Ordering::SeqCst) && Instant::now() < end {
                std::thread::sleep(Duration::from_millis(10));
            }
            read.released.store(true, Ordering::SeqCst);
            Box::new(()) as ChildrenFound
        }))
    }
}

/// C# `RunManager.ReadChildren` polls its watchers outside the tracker's lock: while the sub-agents' files are being read (a read held
/// open here), the run's output still reaches the graph and the pane, and a card's answer goes through at once.
#[tokio::test(flavor = "multi_thread")]
async fn a_slow_read_of_the_sub_agents_files_holds_up_neither_the_output_nor_an_answer() {
    common::korean();
    let directory = common::temp("slow-read");
    let folder = directory.join("workspace");
    std::fs::create_dir_all(&folder).unwrap();
    let workspace = Workspace { path: Some(folder.to_string_lossy().into_owned()), ..Workspace::default() };
    let plugin = common::plugin(&directory);
    let record = directory.join("replay").to_string_lossy().into_owned();
    // The stand-in Claude asks for one Bash command, then (once answered) for its plan, then replays a recording.
    let command =
        CliCommand::new(common::fake_text(), vec!["--replay-cli".into(), "claude-stream-subagents".into(), record], Some("2.1.322"));
    let catalog = common::catalog(move |_| Some(command.clone()));
    let read = Arc::new(SlowRead::default());
    let cards: Arc<Mutex<Vec<(ToolPermissionRequest, bool)>>> = Arc::default();
    let resolved = workspace.clone();
    let mut options = RunManagerOptions::new(
        &plugin,
        Arc::new(move |_| {
            let workspace = resolved.clone();
            Box::pin(async move { Ok(workspace) })
        }),
        Arc::new(|_| {}),
    );
    let (seen, reading) = (cards.clone(), read.clone());
    options.permission_requested = Some(Arc::new(move |value| seen.lock().unwrap().push((value, reading.reading()))));
    options.graph = Some(Arc::new(SlowGraph(read.clone())));
    let manager = RunManager::new(catalog, options);
    let request = StartRunRequest::new("slow-read", workspace.id.as_deref().unwrap(), "claude", "replay the recording");
    manager.start(request).await.unwrap();
    let pending = |tool: &str| {
        let cards = cards.lock().unwrap();
        cards.iter().find(|(card, _)| card.state == "pending" && card.tool_name == tool).map(|(card, during)| (card.clone(), *during))
    };
    common::until(|| read.reading() && pending("Bash").is_some(), 20_000, "the Bash card while the files are read").await;
    let (bash, _) = pending("Bash").unwrap();
    let asked = Instant::now();
    manager.respond_to_tool_permission("slow-read", &bash.id, true).unwrap();
    let answered = asked.elapsed();
    common::until(|| pending("ExitPlanMode").is_some() || !read.reading(), 20_000, "the plan card").await;
    let plan = pending("ExitPlanMode");
    let frames = read.frames.lock().unwrap().clone();
    let still = read.reading();
    read.released.store(true, Ordering::SeqCst);
    manager.stop("slow-read").await.unwrap();
    manager.dispose().await;
    assert!(still && answered < Duration::from_secs(1), "the answer went through during the read, in {answered:?}");
    assert!(plan.is_some_and(|(_, during)| during), "the CLI's next card reached the pane during the read");
    assert!(frames.iter().any(|during| *during), "the output pump fed the graph during the read: {frames:?}");
    common::remove_when_free(&directory).await;
}
