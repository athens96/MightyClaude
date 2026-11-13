//! RunManager over real children: the fake CLI (tests/fake/cli.rs, C# `Verification.FakeCliAsync`) started through the Windows
//! ProcessHost. C# ToolPermissionVerification.RunLaunchesWithHostPromptsAndAnswersOneRequest, ClaudePlanModeVerification's
//! RunnerAnswersAPlanAndReportsProgress and StoppingAPaneWaitingOnBackgroundWorkEndsEverything,
//! ResumedAgentVerification.TheRunnerKeepsTheInputOpenForAResumedAgent, and Verification.cs "provider prompt goes to stdin; shell
//! output and cancellation execute real children", "fake CLI receives attachment bytes and staging is removed on completion, error
//! and stop", "pending stop and disposal prevent a late child launch" and "desktop routing retains cancellation before manager
//! admission", and CliLoginRecoveryVerification.ClaudeFakeCliRunKeepsTheReasonWhenTheResultRepeatsTheMarkedFailure. Windows only: the
//! Mac ProcessHost is W1-D's.
#![cfg(windows)]

mod common;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock, Weak};

use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AgentActivity, AppSnapshot, LogEntry, RunSession, RunSettings, Workspace};
use mighty_run::graph::{GraphFactory, GraphSink, GraphStart};
use mighty_run::manager::{Emit, ResolveWorkspace};
use mighty_run::model::{RunEvent, StartRunRequest};
use mighty_run::mods::ModMetadata;
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::plan_mode::{PlanApprovalRequest, PlanDecision, todos, work};
use mighty_run::service::{RunService, RunServiceOptions};
use mighty_run::{RunManager, RunManagerOptions};
use serde_json::Value;

#[derive(Clone, Default)]
struct Events(Arc<Mutex<Vec<RunEvent>>>);

impl Events {
    fn emit(&self) -> Emit {
        let list = self.0.clone();
        Arc::new(move |event| list.lock().unwrap().push(event))
    }
    fn all(&self) -> Vec<RunEvent> {
        self.0.lock().unwrap().clone()
    }
    fn of(&self, session: &str) -> Vec<RunEvent> {
        self.all().into_iter().filter(|e| e.session_id == session).collect()
    }
    fn any_status(&self, session: &str, status: &str) -> bool {
        self.of(session).iter().any(|e| e.status.as_deref() == Some(status))
    }
    fn last_status(&self, session: &str) -> Option<String> {
        self.of(session).iter().rev().find(|e| e.kind == "status").and_then(|e| e.status.clone())
    }
}

fn workspace(directory: &Path) -> Workspace {
    Workspace { path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() }
}

fn resolve(workspace: &Workspace) -> ResolveWorkspace {
    let workspace = workspace.clone();
    Arc::new(move |_| {
        let workspace = workspace.clone();
        Box::pin(async move { Ok(workspace) })
    })
}

fn request(id: &str, workspace: &Workspace, kind: &str, input: &str) -> StartRunRequest {
    StartRunRequest::new(id, workspace.id.as_deref().unwrap(), kind, input)
}

fn args(record: &Path) -> Vec<String> {
    common::recorded_args(record)
}

fn read(path: impl AsRef<Path>) -> String {
    std::fs::read_to_string(path).unwrap_or_default()
}

fn suffixed(record: &Path, suffix: &str) -> PathBuf {
    PathBuf::from(format!("{}{suffix}", record.display()))
}

/// The manager a permission sink can answer through (the sink is made before the manager it calls).
type Cell = Arc<OnceLock<Weak<RunManager>>>;

fn manager_of(cell: &Cell) -> Arc<RunManager> {
    cell.get().and_then(Weak::upgrade).expect("the manager")
}

/// End to end over a real child process: the handshake precedes the prompt, the request reaches the host that can show the bar,
/// 거부 answers that one request, and no rule or settings update is ever returned.
#[tokio::test(flavor = "multi_thread")]
async fn run_launches_with_host_prompts_and_answers_one_request() {
    common::korean();
    let directory = common::temp("hosted");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    let record = directory.join("hosted");
    let catalog = common::fake_catalog(&["--fake-cli", "claude", &record.to_string_lossy()]);
    let seen: Arc<Mutex<Vec<ToolPermissionRequest>>> = Arc::default();
    let events = Events::default();
    let cell: Cell = Arc::default();
    let mut options = RunManagerOptions::new(&plugin, resolve(&workspace), events.emit());
    let (sink, answer) = (seen.clone(), cell.clone());
    options.permission_requested = Some(Arc::new(move |value: ToolPermissionRequest| {
        sink.lock().unwrap().push(value.clone());
        // Answered on the run's own context, as the bar's 거부 button does.
        if value.state == "pending" {
            manager_of(&answer).respond_to_tool_permission("hosted", &value.id, false).unwrap();
        }
    }));
    let manager = Arc::new(RunManager::new(catalog, options));
    cell.set(Arc::downgrade(&manager)).ok();
    manager.start(request("hosted", &workspace, "claude", "fixture prompt")).await.unwrap();
    common::until(|| !manager.is_running("hosted"), 20000, "the run ends").await;
    manager.dispose().await;

    let launched = args(&record);
    assert_eq!(common::after(&launched, "--permission-prompts"), "host", "the run launched with host prompts");
    assert_eq!(common::after(&launched, "--permission-prompt-tool"), "stdio", "the run launched with the stdio prompt tool");
    let written: Vec<String> = read(suffixed(&record, ".input")).lines().filter(|l| !l.is_empty()).map(str::to_string).collect();
    assert_eq!(common::json(&written[0])["request"]["subtype"], "initialize", "the handshake is written first");
    assert_eq!(common::json(&written[1])["type"], "user", "the prompt only follows the handshake");
    let seen = seen.lock().unwrap().clone();
    assert!(
        seen.len() >= 2 && seen[0].state == "pending" && seen[0].tool_name == "Read",
        "the waiting request reached the host that can show the bar"
    );
    assert_eq!(seen[0].blocked_path.as_deref(), Some("~/.claude/CLAUDE.md"), "the bar is given the path");
    assert_eq!(seen.last().unwrap().state, "denied", "the request settles as denied");
    let decision = common::json(&read(suffixed(&record, ".decision")))["response"].clone();
    assert_eq!(decision["subtype"], "success", "the CLI receives an answer");
    let payload = &decision["response"];
    assert!(payload["behavior"] == "deny" && payload["toolUseID"] == "tool-ask-1", "거부 denies that one tool call");
    assert!(
        payload.get("updatedPermissions").is_none() && payload.get("updatedInput").is_none(),
        "no rule or settings update is ever returned"
    );
    common::remove_when_free(&directory).await;
}

/// A graph tracker standing in for mighty-graph's: the request's run, named by its id, with the run's state.
struct StubGraph;

struct StubSink {
    start: GraphStart,
    status: String,
}

impl GraphFactory for StubGraph {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink> {
        Box::new(StubSink { start, status: "running".into() })
    }
}

impl GraphSink for StubSink {
    fn run_id(&self) -> String {
        self.start.run_id.clone()
    }
    fn frame(&mut self, _provider: &str, _frame: &Value) {}
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
            input: Some(self.start.input.clone()),
            status: Some(self.status.clone()),
            root_entries: Some(vec![]),
            agents: Some(vec![]),
            source_run_id: Some(self.start.run_id.clone()),
            provider: Some(self.start.provider.clone()),
            ..MightyGraphRun::default()
        })
    }
    fn take_notice_runs(&mut self, _include_running: bool) -> Vec<MightyGraphRun> {
        vec![]
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn runner_answers_a_plan_and_reports_progress() {
    common::korean();
    let directory = common::temp("plan-runner");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    for (decision, last, quiet) in [
        (PlanDecision::approve_auto_edit(), "completed", false),
        (PlanDecision::cancel(), "stopped", false),
        (PlanDecision::approve_confirm_each(), "completed", true),
    ] {
        let record = directory.join(format!("plan-{}", decision.kind));
        if quiet {
            std::fs::write(suffixed(&record, ".quiet"), "").unwrap();
        }
        let catalog = common::fake_catalog(&["--fake-cli", "claude", &record.to_string_lossy()]);
        let events = Events::default();
        let seen: Arc<Mutex<Vec<ToolPermissionRequest>>> = Arc::default();
        let cell: Cell = Arc::default();
        let mut options = RunManagerOptions::new(&plugin, resolve(&workspace), events.emit());
        options.background_idle_close = std::time::Duration::from_millis(500);
        options.graph = Some(Arc::new(StubGraph));
        let (sink, answer, chosen) = (seen.clone(), cell.clone(), decision.clone());
        options.permission_requested = Some(Arc::new(move |value: ToolPermissionRequest| {
            sink.lock().unwrap().push(value.clone());
            if let Some(approval) = PlanApprovalRequest::from("plan-pane", &value) {
                manager_of(&answer).answer_plan("plan-pane", &approval.request_id, &chosen).unwrap();
            }
        }));
        let manager = Arc::new(RunManager::new(catalog, options));
        cell.set(Arc::downgrade(&manager)).ok();
        // The pane is stored in manual; this request alone starts in plan (a guided style's override).
        let mut start = request("plan-pane", &workspace, "claude", "plan fixture")
            .with_settings(RunSettings { permission_mode: "manual".into(), ..RunSettings::default() });
        start.permission_mode_override = Some("plan".into());
        manager.start(start).await.unwrap();
        if decision == PlanDecision::approve_auto_edit() {
            // The turn is over but its background agent runs: input stays open and new text joins.
            common::until(
                || events.all().iter().any(|e| e.background.as_ref().is_some_and(work::waiting_on_background)),
                20000,
                "the pane waits on its background agent",
            )
            .await;
            assert!(manager.is_running("plan-pane"), "the run waits for its background agent");
            assert!(manager.try_steer("plan-pane", "and one more thing"), "new input joins the open process");
        }
        common::until(|| !manager.is_running("plan-pane"), 20000, "the run ends").await;
        assert!(!manager.try_steer("plan-pane", "too late"), "a finished run takes no input");
        manager.dispose().await;

        let all = events.all();
        assert_eq!(common::after(&args(&record), "--permission-mode"), "plan", "the override launches in plan");
        assert_eq!(read(suffixed(&record, ".env")), "1", "the CLI is asked for its session state events");
        let answer = common::json(&read(suffixed(&record, ".decision")))["response"]["response"].clone();
        let seen = seen.lock().unwrap().clone();
        assert!(
            seen[0].can_answer_plan && !seen[0].can_allow && seen[0].plan().as_deref() == Some("# Plan\n1. Do it"),
            "the plan reached the host as a plan"
        );
        let plans: Vec<&RunEvent> = all.iter().filter(|e| e.kind == "plan").collect();
        assert!(
            plans.len() == 1 && plans[0].plan.as_ref().unwrap().outcome.as_deref() == Some(decision.outcome()),
            "the answer is recorded once"
        );
        let graph_run =
            all.iter().rev().filter(|e| e.kind == "graph_run").find_map(|e| e.graph_run.clone().filter(|r| r.notice_run != Some(true)));
        let graph_run = graph_run.expect("the request's graph run");
        assert_eq!(plans[0].plan.as_ref().unwrap().graph_run_id, graph_run.source_run_id, "the record names its request's graph run");
        assert_eq!(events.last_status("plan-pane").as_deref(), Some(last), "the run ends {last}");
        if decision == PlanDecision::cancel() {
            assert!(
                answer["interrupt"] == true && !all.iter().any(|e| e.entry.as_ref().is_some_and(|l| l.kind.as_deref() == Some("error"))),
                "cancel interrupts the turn without an error line"
            );
            continue;
        }
        if quiet {
            continue;
        }
        assert!(
            answer["updatedPermissions"][0]["mode"] == "acceptEdits" && answer["updatedInput"].as_object().unwrap().is_empty(),
            "approval switches the session to acceptEdits"
        );
        assert!(
            read(suffixed(&record, ".follow")).contains("and one more thing") && !suffixed(&record, ".extra").exists(),
            "the steered text reached the CLI before it closed"
        );
        let todos = all.iter().rev().find(|e| e.kind == "todos").and_then(|e| e.todos.clone()).unwrap();
        assert!(
            todos::current_text(&todos).as_deref() == Some("Doing it") && todos.run_id == graph_run.source_run_id,
            "the checklist is reported under its request's graph run"
        );
        let background = all.iter().rev().find(|e| e.kind == "background").and_then(|e| e.background.clone()).unwrap();
        assert!(
            work::tasks(&background)[0].status.as_deref() == Some("completed")
                && work::tasks(&background)[0].summary.as_deref() == Some("All good")
                && !work::waiting_on_background(&background),
            "its notification settles it"
        );
        let pane = RunSession {
            id: Some("plan-pane".into()),
            workspace_id: workspace.id.clone(),
            settings: Some(RunSettings { permission_mode: "manual".into(), ..RunSettings::default() }),
            ..RunSession::default()
        };
        let mut snapshot =
            AppSnapshot { workspaces: Some(vec![workspace.clone()]), sessions: Some(vec![Arc::new(pane)]), ..AppSnapshot::default() };
        for event in &all {
            snapshot = mighty_run::apply::apply(&snapshot, event, mighty_core::time::DateTimeOffset::now());
        }
        let pane = snapshot.sessions.unwrap()[0].clone();
        assert!(
            pane.settings.as_ref().unwrap().permission_mode == "manual"
                && pane.plan_history.as_ref().map(Vec::len) == Some(1)
                && pane.todo_progress.as_ref().map(todos::total) == Some(1),
            "a pane not stored in plan keeps its mode"
        );
    }
    common::remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn stopping_a_pane_waiting_on_background_work_ends_everything() {
    common::korean();
    let directory = common::temp("background-stop");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    let record = directory.join("stop");
    let catalog = common::fake_catalog(&["--fake-cli", "claude", &record.to_string_lossy()]);
    let events = Events::default();
    let mut options = RunManagerOptions::new(&plugin, resolve(&workspace), events.emit());
    options.permission_requested = Some(Arc::new(|_| {}));
    let manager = RunManager::new(catalog, options);
    manager.start(request("stop-pane", &workspace, "claude", "background stop fixture")).await.unwrap();
    common::until(
        || events.all().iter().any(|e| e.background.as_ref().is_some_and(work::waiting_on_background)),
        20000,
        "the pane waits on its background shell",
    )
    .await;
    assert!(manager.is_running("stop-pane"), "the run waits for its background shell");
    manager.stop("stop-pane").await.unwrap();
    common::until(|| !manager.is_running("stop-pane"), 20000, "the run ends").await;
    manager.dispose().await;
    let all = events.all();
    assert_eq!(events.last_status("stop-pane").as_deref(), Some("stopped"), "the stopped pane ends stopped");
    let background = all.iter().rev().find(|e| e.kind == "background").and_then(|e| e.background.clone()).unwrap();
    assert_eq!(work::tasks(&background)[0].status.as_deref(), Some("unknown"), "the task that never reported an end is unknown");
    let pid: u32 = read(suffixed(&record, ".pid")).trim().parse().unwrap();
    common::until(|| !common::alive(pid), 10000, "the CLI process is gone").await;
    common::remove_when_free(&directory).await;
}

/// The run's input as RunManager keeps it: open after the request's result while the CLI says it is not idle, so a notice turn that
/// comes after a result with nothing running can resume an agent and still take new input; closed once it is idle.
#[tokio::test(flavor = "multi_thread")]
async fn the_runner_keeps_the_input_open_for_a_resumed_agent() {
    common::korean();
    let directory = common::temp("resumed");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    let record = directory.join("resumed");
    let catalog = common::fake_catalog(&["--fake-cli", "claude", &record.to_string_lossy()]);
    let events = Events::default();
    let mut options = RunManagerOptions::new(&plugin, resolve(&workspace), events.emit());
    options.permission_requested = Some(Arc::new(|_| {}));
    let manager = RunManager::new(catalog, options);
    manager.start(request("resumed-pane", &workspace, "claude", "resumed agent fixture")).await.unwrap();
    // After the notice turn's result the resumed agent runs, the pane waits on it, and new input still reaches the process.
    common::until(
        || {
            events
                .all()
                .iter()
                .rev()
                .find(|e| e.kind == "background")
                .and_then(|e| e.background.clone())
                .is_some_and(|work| work::waiting_on_background(&work) && work::tasks(&work)[0].tool_use_id.as_deref() == Some("toolu_2"))
        },
        20000,
        "the pane waits on the resumed agent",
    )
    .await;
    assert!(manager.is_running("resumed-pane"), "the run waits for the resumed agent");
    assert!(manager.try_steer("resumed-pane", "and one more thing"), "new input still joins the open process");
    common::until(|| !manager.is_running("resumed-pane"), 20000, "the run ends").await;
    manager.dispose().await;
    let all = events.all();
    assert_eq!(events.last_status("resumed-pane").as_deref(), Some("completed"), "the run ends completed");
    assert!(
        read(suffixed(&record, ".follow")).contains("and one more thing") && !suffixed(&record, ".extra").exists(),
        "the steered text reached the CLI before its input closed"
    );
    let last = all.iter().rev().find(|e| e.kind == "background").and_then(|e| e.background.clone()).unwrap();
    assert!(
        !work::waiting_on_background(&last)
            && work::tasks(&last).len() == 1
            && work::tasks(&last)[0].status.as_deref() == Some("completed"),
        "the resumed run's notification ends it"
    );
    common::remove_when_free(&directory).await;
}

/// Verification.cs "provider prompt goes to stdin; shell output and cancellation execute real children".
#[tokio::test(flavor = "multi_thread")]
async fn provider_prompt_goes_to_stdin_shell_output_and_cancellation_execute_real_children() {
    common::korean();
    let directory = common::temp("stdin");
    let workspace = workspace(&directory);
    let record = directory.join("fake");
    let catalog = common::fake_catalog(&["--fake-cli", "codex", &record.to_string_lossy()]);
    let events = Events::default();
    let manager = RunManager::new(catalog, RunManagerOptions::new("", resolve(&workspace), events.emit()));
    let prompt = "stdin only ` & $() 한글";
    manager.start(request("provider", &workspace, "claude", prompt).with_provider("codex")).await.unwrap();
    common::until(|| events.any_status("provider", "completed"), 20000, "the codex run completes").await;
    assert_eq!(read(suffixed(&record, ".prompt")), prompt);
    assert!(!read(suffixed(&record, ".args")).contains(prompt));
    manager.start(request("echo", &workspace, "shell", "echo MIGHTY_NATIVE_OK")).await.unwrap();
    common::until(|| events.any_status("echo", "completed"), 20000, "the shell command completes").await;
    assert!(
        events.of("echo").iter().any(|e| e.entry.as_ref().and_then(|l| l.text.as_deref()).is_some_and(|t| t.contains("MIGHTY_NATIVE_OK")))
    );
    let pid = directory.join("pid");
    let long = format!("\"{}\" --long-child \"{}\"", common::fake_text(), pid.display());
    manager.start(request("long", &workspace, "shell", &long)).await.unwrap();
    common::until(|| pid.exists() && !read(&pid).is_empty(), 20000, "the long child starts").await;
    let process: u32 = read(&pid).trim().parse().unwrap();
    manager.stop("long").await.unwrap();
    assert!(events.any_status("long", "stopped"));
    common::until(|| !common::alive(process), 20000, "the long child is gone").await;
    manager.dispose().await;
    common::remove_when_free(&directory).await;
}

const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";

/// `AttachmentReferencePath`: the path a prompt names a copy by (Antigravity's is plain in quotes, the others' JSON).
fn attachment_reference_path(prompt: &str, name: &str, plain: bool) -> String {
    let prefix = format!("\"{name}\": ");
    let lines: Vec<&str> = prompt.split('\n').filter(|l| l.starts_with(&prefix)).collect();
    assert_eq!(lines.len(), 1, "one reference to {name}");
    let rest = &lines[0][prefix.len()..];
    if plain { rest.trim_matches('"').to_string() } else { serde_json::from_str(rest).expect("Missing attachment path.") }
}

/// Verification.cs "fake CLI receives attachment bytes and staging is removed on completion, error and stop".
#[tokio::test(flavor = "multi_thread")]
async fn fake_cli_receives_attachment_bytes_and_staging_is_removed() {
    use base64::Engine;
    common::korean();
    let directory = common::temp("attachments");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    let png = base64::engine::general_purpose::STANDARD.decode(PNG).unwrap();
    let files = vec![
        mighty_run::attachments::make("image.png", &png).unwrap(),
        mighty_run::attachments::make("source.cs", b"attachment-fixture-contents").unwrap(),
    ];
    let events = Events::default();
    // pi and the ACP agents take their attachments in their own channels (W5).
    for provider in mighty_core::wire::PROVIDERS.iter().copied().filter(|p| *p != "pi" && !mighty_run::providers::is_acp(p)) {
        let record = directory.join(provider);
        let catalog = common::fake_catalog(&["--fake-cli", provider, &record.to_string_lossy(), "--verify-attachments"]);
        let manager = RunManager::new(catalog, RunManagerOptions::new(&plugin, resolve(&workspace), events.emit()));
        let mut start = request(provider, &workspace, "claude", "").with_provider(provider);
        start.attachments = Some(files.clone());
        manager.start(start).await.unwrap();
        common::until(|| !manager.is_running(provider), 20000, "the attachment run ends").await;
        manager.dispose().await;
        assert!(events.any_status(provider, "completed"), "{provider} attachment run did not complete");
        let launched = args(&record);
        let flag = if provider == "codex" { "--image" } else { "--add-dir" };
        let mut path = PathBuf::from(common::after(&launched, flag));
        if provider == "codex" {
            path = path.parent().unwrap().to_path_buf();
        }
        assert!(!path.exists(), "{provider} staging cleanup");
        let mut prompt = read(suffixed(&record, ".prompt"));
        let captured: serde_json::Map<String, Value> = serde_json::from_str(&read(suffixed(&record, ".attachments"))).unwrap();
        assert_eq!(captured.len(), files.len(), "{provider} child did not read every staged attachment");
        if provider == "claude" {
            let json = common::json(&prompt);
            let content = json["message"]["content"].as_array().unwrap().clone();
            let image: Vec<&Value> = content.iter().filter(|c| c["type"] == "image").collect();
            assert!(image.len() == 1 && image[0]["source"]["data"] == files[0].data_base64.as_str(), "Claude inline image bytes changed");
            prompt = content.iter().find(|c| c["type"] == "text").unwrap()["text"].as_str().unwrap().to_string();
        }
        // Antigravity reads its prompt as one stream-json line.
        if provider == "gemini" {
            assert!(prompt.ends_with('\n') && prompt.matches('\n').count() == 1, "Antigravity's prompt is one stdin line");
            let json = common::json(&prompt);
            assert_eq!(json["event"], "user", "Antigravity's prompt is a user event");
            prompt = json["message"]["content"].as_str().unwrap().to_string();
        }
        for attachment in files.iter().skip(if provider == "claude" { 1 } else { 0 }) {
            let referenced = attachment_reference_path(&prompt, &attachment.name, provider == "gemini");
            assert_eq!(Path::new(&referenced).parent(), Some(path.as_path()), "{provider} reference escaped its staged directory");
            assert_eq!(
                captured.get(&referenced).and_then(Value::as_str),
                Some(attachment.data_base64.as_str()),
                "{provider} referenced bytes changed before CLI consumption"
            );
            if provider == "codex" && attachment.media_type.starts_with("image/") {
                assert_eq!(common::after(&launched, "--image"), referenced, "Codex image argument and reference differ");
            }
        }
        assert!(!launched.contains(&files[0].data_base64), "{provider} attachment bytes leaked into argv");
    }
    for behavior in ["--hold-run", "--fail-run"] {
        let record = directory.join(behavior.trim_start_matches('-'));
        let catalog = common::fake_catalog(&["--fake-cli", "codex", &record.to_string_lossy(), behavior]);
        let manager = RunManager::new(catalog, RunManagerOptions::new(&plugin, resolve(&workspace), events.emit()));
        let id = mighty_core::wire::new_id();
        let mut start = request(&id, &workspace, "claude", "").with_provider("codex");
        start.attachments = Some(files.clone());
        manager.start(start).await.unwrap();
        common::until(|| suffixed(&record, ".prompt").exists(), 20000, "the child read its prompt").await;
        let stage = PathBuf::from(common::after(&args(&record), "--image")).parent().unwrap().to_path_buf();
        if behavior == "--hold-run" {
            assert!(stage.exists());
            manager.stop(&id).await.unwrap();
        } else {
            common::until(|| !manager.is_running(&id), 20000, "the failing run ends").await;
        }
        assert!(!stage.exists());
        assert!(events.any_status(&id, if behavior == "--hold-run" { "stopped" } else { "error" }));
        manager.dispose().await;
    }
    let unavailable = RunManager::new(common::absent_catalog(), RunManagerOptions::new(&plugin, resolve(&workspace), events.emit()));
    let mut start = request("no-cli", &workspace, "claude", "").with_provider("codex");
    start.attachments = Some(files.clone());
    assert!(unavailable.start(start).await.is_err());
    assert!(!unavailable.is_running("no-cli"));
    unavailable.dispose().await;
    // A start still resolving its workspace when the manager closes is refused.
    let (sender, receiver) = tokio::sync::oneshot::channel::<Workspace>();
    let pending = Arc::new(Mutex::new(Some(receiver)));
    let late: ResolveWorkspace = Arc::new(move |_| {
        let receiver = pending.lock().unwrap().take();
        Box::pin(async move { Ok(receiver.unwrap().await.unwrap()) })
    });
    let cancelled = Arc::new(RunManager::new(common::absent_catalog(), RunManagerOptions::new(&plugin, late, events.emit())));
    let mut start = request("cancel-attach", &workspace, "claude", "").with_provider("codex");
    start.attachments = Some(files.clone());
    let starting = tokio::spawn({
        let cancelled = cancelled.clone();
        async move { cancelled.start(start).await }
    });
    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    let closing = tokio::spawn({
        let cancelled = cancelled.clone();
        async move { cancelled.dispose().await }
    });
    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    sender.send(workspace.clone()).unwrap();
    assert!(starting.await.unwrap().is_err());
    closing.await.unwrap();
    common::remove_when_free(&directory).await;
}

/// A Claude run with host prompts and a picture: the prompt the CLI reads after the handshake is the one stream-json user message
/// the staged files make, its content the text and the picture, never that message wrapped in another as text.
#[tokio::test(flavor = "multi_thread")]
async fn a_hosted_claude_run_with_a_picture_sends_its_user_message_once() {
    use base64::Engine;
    common::korean();
    let directory = common::temp("hosted-picture");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    let record = directory.join("hosted-picture");
    let catalog = common::fake_catalog(&["--fake-cli", "claude", &record.to_string_lossy()]);
    let events = Events::default();
    let cell: Cell = Arc::default();
    let mut options = RunManagerOptions::new(&plugin, resolve(&workspace), events.emit());
    let answer = cell.clone();
    options.permission_requested = Some(Arc::new(move |value: ToolPermissionRequest| {
        if value.state == "pending" {
            manager_of(&answer).respond_to_tool_permission("picture-pane", &value.id, false).unwrap();
        }
    }));
    let manager = Arc::new(RunManager::new(catalog, options));
    cell.set(Arc::downgrade(&manager)).ok();
    let png = base64::engine::general_purpose::STANDARD.decode(PNG).unwrap();
    let picture = mighty_run::attachments::make("image.png", &png).unwrap();
    let mut start = request("picture-pane", &workspace, "claude", "look at this");
    start.attachments = Some(vec![picture.clone()]);
    manager.start(start).await.unwrap();
    common::until(|| !manager.is_running("picture-pane"), 20000, "the run ends").await;
    manager.dispose().await;

    let written: Vec<String> = read(suffixed(&record, ".input")).lines().filter(|l| !l.is_empty()).map(str::to_string).collect();
    assert_eq!(common::json(&written[0])["request"]["subtype"], "initialize", "the handshake is written first");
    let prompt = common::json(&written[1]);
    assert_eq!(prompt["type"], "user", "the prompt follows the handshake");
    let content = prompt["message"]["content"].as_array().expect("the user message's content is a list of blocks, not a wrapped frame");
    assert!(
        content.iter().any(|c| c["type"] == "text" && c["text"] == "look at this"),
        "the text is the message's first block: {content:?}"
    );
    let images: Vec<&Value> = content.iter().filter(|c| c["type"] == "image").collect();
    assert!(
        images.len() == 1 && images[0]["source"]["data"] == picture.data_base64.as_str(),
        "the picture is an image block of the same message"
    );
    assert_eq!(events.last_status("picture-pane").as_deref(), Some("completed"), "the run completes");
    common::remove_when_free(&directory).await;
}

/// Verification.cs "pending stop and disposal prevent a late child launch".
#[tokio::test(flavor = "multi_thread")]
async fn pending_stop_and_disposal_prevent_a_late_child_launch() {
    common::korean();
    let directory = common::temp("late-launch");
    let workspace = workspace(&directory);
    let (sender, receiver) = tokio::sync::oneshot::channel::<Workspace>();
    let pending = Arc::new(Mutex::new(Some(receiver)));
    let late: ResolveWorkspace = Arc::new(move |_| {
        let receiver = pending.lock().unwrap().take();
        Box::pin(async move {
            match receiver {
                Some(receiver) => Ok(receiver.await.unwrap()),
                None => Err(mighty_run::RunError::State("resolved once".into())),
            }
        })
    });
    let events = Events::default();
    let manager = Arc::new(RunManager::new(common::absent_catalog(), RunManagerOptions::new("", late, events.emit())));
    manager.start(request("pending", &workspace, "shell", "echo NEVER")).await.unwrap();
    let shutdown = tokio::spawn({
        let manager = manager.clone();
        async move { manager.dispose().await }
    });
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    sender.send(workspace.clone()).unwrap();
    shutdown.await.unwrap();
    assert!(events.all().iter().all(|e| e.entry.as_ref().and_then(|l| l.text.as_deref()) != Some("NEVER")));
    assert!(manager.start(request("late", &workspace, "shell", "echo NEVER")).await.is_err());
    common::remove_when_free(&directory).await;
}

/// Verification.cs "desktop routing retains cancellation before manager admission": a stop asked for while the run is being
/// admitted stops it before its command runs.
#[tokio::test(flavor = "multi_thread")]
async fn desktop_routing_retains_cancellation_before_manager_admission() {
    common::korean();
    let directory = common::temp("routing");
    let workspace = Workspace { name: Some("Routing".into()), ..workspace(&directory) };
    let pane = RunSession { workspace_id: workspace.id.clone(), kind: Some("shell".into()), ..RunSession::default() };
    let pane_id = pane.id.clone().unwrap();
    let snapshot =
        AppSnapshot { workspaces: Some(vec![workspace.clone()]), sessions: Some(vec![Arc::new(pane)]), ..AppSnapshot::default() };
    let (service, mut events) = RunService::new(
        snapshot,
        common::absent_catalog(),
        RunServiceOptions { plugin_directory: String::new(), permission_card: false, remote: None, configure: None },
    );
    let service = Arc::new(service);
    // `RunEventReceived += value => { if (value.Status == "running") _ = service.StopAsync(value.SessionId); }`
    let stopper = service.clone();
    let listener = tokio::spawn(async move {
        while let Some(event) = events.recv().await {
            if let mighty_run::ServiceEvent::Run { event, .. } = event
                && event.status.as_deref() == Some("running")
            {
                let _ = stopper.stop(&event.session_id).await;
            }
        }
    });
    // C# hears the running event synchronously, before the runner admits the request, so its stop lands first and the command
    // never runs. Here the host hears it through the service's channel, after `launch` handed the request on: the stop reaches the
    // run itself (a deviation: no host can stop a run between its running event and its admission).
    let _ = service.start(request(&pane_id, &workspace, "shell", "ping -n 30 127.0.0.1")).await;
    common::until(|| service.snapshot().sessions.unwrap()[0].status.as_deref() == Some("stopped"), 20000, "the pane stops").await;
    let session = service.snapshot().sessions.unwrap()[0].clone();
    assert_eq!(session.status.as_deref(), Some("stopped"));
    service.dispose().await;
    listener.abort();
    common::remove_when_free(&directory).await;
}

/// CliLoginRecoveryVerification.ClaudeFakeCliRunKeepsTheReasonWhenTheResultRepeatsTheMarkedFailure.
#[tokio::test(flavor = "multi_thread")]
async fn claude_fake_cli_run_keeps_the_reason_when_the_result_repeats_the_marked_failure() {
    use mighty_run::failure;
    common::korean();
    let directory = common::temp("sign-in");
    let workspace = workspace(&directory);
    let plugin = common::plugin(&directory);
    for (prompt, expected) in [
        ("expired sign-in fixture", Some("authentication")),
        ("reworded sign-in fixture", Some("authentication")),
        ("child sign-in fixture", Some("authentication")),
        ("unmarked sign-in fixture", None),
        ("mismatched sign-in fixture", None),
        ("denied sign-in fixture", None),
    ] {
        let record = directory.join(prompt.split(' ').next().unwrap());
        let catalog = common::fake_catalog(&["--fake-cli", "claude", &record.to_string_lossy()]);
        let events = Events::default();
        let mut options = RunManagerOptions::new(&plugin, resolve(&workspace), events.emit());
        options.permission_requested = Some(Arc::new(|_| {}));
        let manager = RunManager::new(catalog, options);
        manager.start(request("claude-pane", &workspace, "claude", prompt)).await.unwrap();
        common::until(
            || events.all().iter().any(|e| e.kind == "status" && matches!(e.status.as_deref(), Some("completed" | "error" | "stopped"))),
            20000,
            "the run ends",
        )
        .await;
        manager.dispose().await;
        let all = events.all();
        let last = all.iter().rev().find(|e| e.kind == "status").unwrap();
        let logs: Vec<String> = all
            .iter()
            .filter(|e| e.kind == "log" && e.entry.as_ref().is_some_and(|l| matches!(l.kind.as_deref(), Some("error" | "system"))))
            .map(|e| e.entry.as_ref().unwrap().text.clone().unwrap_or_default())
            .collect();
        assert!(
            last.status.as_deref() == Some("error") && last.reason.as_deref() == expected,
            "{prompt}: expected error with reason {expected:?}, got {:?} with {:?}; logs: {logs:?}",
            last.status,
            last.reason
        );
    }
    common::remove_when_free(&directory).await;
    assert!(
        failure::claude_text("Failed to authenticate: OAuth session expired and could not be refreshed"),
        "a headless run's expired sign-in wording qualifies on its own"
    );
    let marked =
        common::json(r#"{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"  Stale  "}]}}"#);
    let limited = common::json(r#"{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"Stale"}]}}"#);
    let repeated = common::json(r#"{"type":"result","is_error":true,"result":"Stale\n"}"#);
    let succeeded = common::json(r#"{"type":"result","is_error":false,"result":"Stale"}"#);
    assert!(
        failure::claude_failure_text(&marked).as_deref() == Some("Stale") && failure::claude_failure_text(&limited).is_none(),
        "only a message marked authentication_failed is remembered, trimmed"
    );
    assert!(
        failure::claude_repeating(&repeated, Some("Stale"))
            && !failure::claude_repeating(&repeated, None)
            && !failure::claude_repeating(&repeated, Some(""))
            && !failure::claude_repeating(&succeeded, Some("Stale")),
        "only an error result repeating a remembered message keeps the signal"
    );
}
