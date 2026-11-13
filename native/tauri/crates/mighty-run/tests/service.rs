//! End to end against C#: RunService starts a Claude pane's request through the real Windows ProcessHost; the fake CLI
//! (`--replay-cli claude-stream-subagents`) asks one Bash permission, which the host allows, and one plan, which it approves for
//! auto-edit, then replays the recording. C#'s DesktopService runs the same fake the same way (`tools/parity run-replay`), and the
//! two panes must be the same state, and the CLI must have been sent the same answers.
//!
//! What may differ is taken out of both before the comparison: what is new on every run (ids, times, durations, the workspace's id,
//! the pane's activity ids, which hash the run's random id) and the graph runs (mighty-graph's tracker, plugged in by the lead). The
//! graph tracker also decides which tool rows a sub-agent's block takes from the pane (C# `ExecutionGraphTracker.Activity`); the
//! stub here takes the rows of the tools a sub-agent's own frames call, as C#'s tracker does for this recording.
//!
//! Without the .NET 10 SDK the comparison says so and passes (`MIGHTY_PARITY=require` makes it fail), as mighty-core's parity
//! check does; the Rust run's own checks hold either way.
#![cfg(windows)]

mod common;

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use mighty_core::graph::MightyGraphRun;
use mighty_core::json::Wire;
use mighty_core::model::{AgentActivity, AppSnapshot, LogEntry, RunSession, Workspace};
use mighty_run::ServiceEvent;
use mighty_run::graph::{GraphFactory, GraphSink, GraphStart};
use mighty_run::meta;
use mighty_run::model::{CliCommand, StartRunRequest};
use mighty_run::mods::ModMetadata;
use mighty_run::plan_mode::{PlanApprovalRequest, PlanDecision, outcome};
use mighty_run::service::{RunService, RunServiceOptions};
use serde_json::Value;

const RECORDING: &str = "claude-stream-subagents";
const PANE: &str = "replay-pane";
const INPUT: &str = "replay the recording";

/// The routing half of C#'s tracker for this recording: a tool a sub-agent's frame calls is that sub-agent's row.
#[derive(Default)]
struct ChildTools;

struct ChildToolsSink {
    run_id: String,
    child_tools: HashSet<String>,
}

impl GraphFactory for ChildTools {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink> {
        Box::new(ChildToolsSink { run_id: start.run_id, child_tools: HashSet::new() })
    }
}

impl GraphSink for ChildToolsSink {
    fn run_id(&self) -> String {
        self.run_id.clone()
    }
    fn frame(&mut self, _provider: &str, frame: &Value) {
        if meta::top_level(frame) {
            return;
        }
        let blocks = frame.get("message").and_then(|m| m.get("content")).and_then(Value::as_array);
        for block in blocks.into_iter().flatten() {
            if meta::text(block, "type") == Some("tool_use")
                && let Some(id) = meta::text(block, "id")
            {
                self.child_tools.insert(id.to_string());
            }
        }
    }
    fn notice_revision(&self) -> u64 {
        0
    }
    fn activity(&mut self, _activity: &AgentActivity, tool_id: Option<&str>) -> bool {
        tool_id.is_some_and(|id| self.child_tools.contains(id))
    }
    fn images(&mut self, _entry: &LogEntry, tool_id: Option<&str>, parent_tool_id: Option<&str>) -> bool {
        tool_id.is_some_and(|id| self.child_tools.contains(id)) || parent_tool_id.is_some()
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
}

/// The folders C#'s `run-replay` makes in its work folder: the workspace, the plugin, the record.
fn prepare(work: &Path) -> (PathBuf, String, PathBuf) {
    let folder = work.join("workspace");
    std::fs::create_dir_all(&folder).unwrap();
    let plugin = common::plugin(work);
    (folder, plugin, work.join("replay"))
}

/// The Rust run: the pane as RunService leaves it, and the answers the CLI got.
async fn rust_run(work: &Path) -> (RunSession, Vec<Value>) {
    let (folder, plugin, record) = prepare(work);
    let command = CliCommand::new(
        common::fake_text(),
        vec!["--replay-cli".into(), RECORDING.into(), record.to_string_lossy().into_owned()],
        Some("2.1.322"),
    );
    let catalog = common::catalog(move |_| Some(command.clone()));
    let configure: mighty_run::service::Configure = Box::new(|options| options.graph = Some(Arc::new(ChildTools)));
    let (service, mut events) = RunService::new(
        AppSnapshot::default(),
        catalog,
        RunServiceOptions { plugin_directory: plugin, permission_card: true, remote: None, configure: Some(configure), admitting: None },
    );
    let service = Arc::new(service);
    let workspace = Workspace { name: Some("workspace".into()), path: Some(folder.to_string_lossy().into_owned()), ..Workspace::default() };
    let pane =
        RunSession { id: Some(PANE.into()), workspace_id: workspace.id.clone(), title: Some("Replay".into()), ..RunSession::default() };
    service
        .update(|s| AppSnapshot {
            workspaces: Some(vec![workspace.clone()]),
            sessions: Some(vec![Arc::new(pane.clone())]),
            active_workspace_id: workspace.id.clone(),
            ..s.clone()
        })
        .unwrap();
    // The host's card: the Bash request allowed once, the plan approved for auto-edit (C#: ToolPermissionChanged).
    let answering = service.clone();
    let listener = tokio::spawn(async move {
        while let Some(event) = events.recv().await {
            let ServiceEvent::ToolPermission(value) = event else { continue };
            if value.state != "pending" {
                continue;
            }
            match PlanApprovalRequest::from(&value.run_id, &value) {
                Some(approval) => {
                    answering.answer_plan(&value.run_id, &approval.request_id, &PlanDecision::approve_auto_edit()).await.unwrap();
                }
                None => answering.respond_to_tool_permission(&value.run_id, &value.id, true).await.unwrap(),
            }
        }
    });
    service.start(StartRunRequest::new(PANE, workspace.id.as_deref().unwrap(), "claude", INPUT)).await.unwrap();
    let status = || service.snapshot().session(PANE).and_then(|s| s.status.clone());
    common::until(|| !service.is_session_running(PANE) && status().as_deref() != Some("running"), 60000, "the replay ends").await;
    let session = service.snapshot().session(PANE).unwrap().as_ref().clone();
    service.dispose().await;
    listener.abort();
    let answers = std::fs::read_to_string(format!("{}.answers", record.display())).unwrap();
    (session, answers.lines().filter(|l| !l.is_empty()).map(common::json).collect())
}

/// The C# run (`tools/parity run-replay`), or None without the .NET SDK.
#[allow(clippy::disallowed_methods, reason = "the check's own .NET build and tool, no child of the app")]
fn csharp_run(work: &Path) -> Option<(Value, Vec<Value>)> {
    let tauri = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let tool = tauri.join("target/parity-cs");
    let built = std::process::Command::new("dotnet")
        .arg("build")
        .arg(tauri.join("tools/parity/Parity.csproj"))
        .args(["-c", "Release", "-o"])
        .arg(&tool)
        .args(["--nologo", "-v", "quiet"])
        .status();
    if !matches!(built, Ok(status) if status.success()) {
        let why = format!("the C# parity tool did not build ({built:?})");
        assert!(std::env::var("MIGHTY_PARITY").as_deref() != Ok("require"), "{why}");
        eprintln!("skipped the C# comparison: {why}");
        return None;
    }
    let out = work.join("out");
    let ran = std::process::Command::new("dotnet")
        .arg(tool.join("parity.dll"))
        .arg("run-replay")
        .arg(common::fake_cli())
        .arg(RECORDING)
        .arg(work)
        .arg(&out)
        .status()
        .expect("dotnet");
    assert!(ran.success(), "the C# replay failed");
    let session = common::json(&std::fs::read_to_string(out.join("session.json")).unwrap());
    let answers = std::fs::read_to_string(out.join("answers.jsonl")).unwrap();
    Some((session, answers.lines().filter(|l| !l.is_empty()).map(common::json).collect()))
}

fn remove(value: &mut Value, keys: &[&str]) {
    if let Some(object) = value.as_object_mut() {
        for key in keys {
            object.remove(*key);
        }
    }
}

/// The pane without what is new on every run, and without the graph runs (mighty-graph's).
fn comparable(mut session: Value) -> Value {
    remove(&mut session, &["workspaceId", "createdAt", "runTiming", "graphRuns"]);
    for entry in session["logs"].as_array_mut().into_iter().flatten() {
        remove(entry, &["id", "timestamp"]);
        if let Some(activity) = entry.get_mut("activity") {
            remove(activity, &["id", "durationMs"]);
        }
    }
    if let Some(usage) = session.get_mut("sessionUsage") {
        remove(usage, &["updatedAt", "rateLimitsUpdatedAt"]);
    }
    for record in session["planHistory"].as_array_mut().into_iter().flatten() {
        remove(record, &["receivedAt", "decidedAt", "graphRunId"]);
    }
    if let Some(progress) = session.get_mut("todoProgress") {
        remove(progress, &["runId"]);
    }
    if let Some(work) = session.get_mut("backgroundWork") {
        for task in work["tasks"].as_array_mut().into_iter().flatten() {
            remove(task, &["startedAt", "endedAt"]);
        }
    }
    session
}

#[tokio::test(flavor = "multi_thread")]
async fn a_replayed_run_leaves_the_state_csharp_leaves() {
    common::korean();
    let rust_work = common::temp("replay-rs");
    let (session, answers) = rust_run(&rust_work).await;

    // The Rust run's own account: allowed once, approved for auto-edit, the recording's answer, its conversation and its plan.
    assert_eq!(answers.len(), 2, "the CLI got two answers");
    let allowed = &answers[0]["response"];
    assert!(
        allowed["request_id"] == "replay-ask"
            && allowed["response"]["behavior"] == "allow"
            && allowed["response"]["toolUseID"] == "toolu_replay_ask",
        "the Bash request is allowed once"
    );
    let approved = &answers[1]["response"];
    assert!(
        approved["request_id"] == "replay-plan" && approved["response"]["updatedPermissions"][0]["mode"] == "acceptEdits",
        "the plan is approved for auto-edit"
    );
    assert_eq!(session.status.as_deref(), Some("completed"), "the run completes");
    assert_eq!(session.resume_id.as_deref(), Some("session-1"), "the recording's session is the pane's");
    let logs = session.logs.clone().unwrap_or_default();
    assert!(
        logs.first().is_some_and(|l| l.kind.as_deref() == Some("user") && l.text.as_deref() == Some(INPUT)),
        "the request opens the log"
    );
    assert!(logs.iter().any(|l| l.kind.as_deref() == Some("assistant") && l.text.as_deref() == Some("done.")), "the answer is in the log");
    let plans = session.plan_history.clone().unwrap_or_default();
    assert!(
        plans.len() == 1
            && plans[0].outcome.as_deref() == Some(outcome::APPROVED_AUTO)
            && plans[0].plan.as_deref() == Some("# Plan\n1. Replay"),
        "the plan is kept, approved"
    );

    let csharp_work = common::temp("replay-cs");
    prepare(&csharp_work);
    if let Some((expected, expected_answers)) = csharp_run(&csharp_work) {
        assert_eq!(answers, expected_answers, "the CLI got the answers C# sends");
        let ours = comparable(common::json(&session.to_json()));
        let theirs = comparable(expected);
        assert!(
            ours == theirs,
            "the pane differs from C#'s\n--- Rust\n{}\n--- C#\n{}",
            serde_json::to_string_pretty(&ours).unwrap(),
            serde_json::to_string_pretty(&theirs).unwrap()
        );
    }
    common::remove_when_free(&rust_work).await;
    common::remove_when_free(&csharp_work).await;
}
