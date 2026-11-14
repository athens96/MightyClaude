//! C# AcpRunVerification: opencode and Hermes runs through the real RunManager against a stand-in agent (tests/fake/cli.rs
//! `--fake-acp`, C#'s `--fake-acp`) that answers the channel's requests with a recorded session — argv, the run's environment, stdin, the
//! end of a run whose server exits or lingers, a lost sign-in, OpenCode 1, Hermes's yolo, its clarify switch and its stop. The graph
//! the runner feeds is a recording sink here (mighty-run cannot name mighty-graph's tracker); the blocks it makes of these runs are
//! mighty-graph's tests/acp_graphs.rs. Windows only: the Mac ProcessHost is W1-D's.
#![cfg(windows)]

mod common;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock, Weak};

use indexmap::IndexMap;
use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AgentActivity, LogEntry, RunSettings, Workspace};
use mighty_run::acp::agents;
use mighty_run::graph::{GraphFactory, GraphSink, GraphStart};
use mighty_run::manager::{Emit, ResolveWorkspace};
use mighty_run::model::{CliCommand, RunEvent, StartRunRequest};
use mighty_run::mods::ModMetadata;
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::questionnaire::{UserQuestionAnswer, UserQuestionnaire};
use mighty_run::{RunManager, RunManagerOptions, strings};
use serde_json::Value;

const OPENCODE_SESSION: &str = "ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw";
const HERMES_SESSION: &str = "local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b";

/// Hermes's clarify switch is looked at once per launch for each Hermes (C# `HermesQuestionTool.Reset` between checks): the checks
/// that run the fake Hermes take turns.
static HERMES: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

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
    fn ended(&self) -> bool {
        self.all().iter().any(|e| e.kind == "status" && matches!(e.status.as_deref(), Some("completed" | "error" | "stopped")))
    }
    fn last_status(&self) -> RunEvent {
        self.all().into_iter().rev().find(|e| e.kind == "status").expect("a status")
    }
    fn logs(&self, kind: &str) -> Vec<String> {
        self.all()
            .into_iter()
            .filter_map(|e| e.entry.filter(|entry| entry.kind.as_deref() == Some(kind)).and_then(|entry| entry.text))
            .collect()
    }
    fn described(&self) -> String {
        self.all()
            .iter()
            .filter(|e| e.kind == "status" || e.kind == "log")
            .map(|e| match &e.entry {
                Some(entry) => format!("{}: {}", entry.kind.as_deref().unwrap_or(""), entry.text.as_deref().unwrap_or("")),
                None => e.status.clone().unwrap_or_default(),
            })
            .collect::<Vec<_>>()
            .join(" | ")
    }
}

/// What the runner fed the run's graph: every frame, every answer the parser showed, how it ended.
#[derive(Default)]
struct Fed {
    frames: Vec<Value>,
    answers: Vec<String>,
    finished: Option<String>,
}

struct Recorder(Arc<Mutex<Fed>>);

struct Sink(Arc<Mutex<Fed>>, String);

impl GraphFactory for Recorder {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink> {
        Box::new(Sink(self.0.clone(), start.run_id))
    }
}

impl GraphSink for Sink {
    fn run_id(&self) -> String {
        self.1.clone()
    }
    fn frame(&mut self, _provider: &str, frame: &Value) {
        self.0.lock().unwrap().frames.push(frame.clone());
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
        self.0.lock().unwrap().finished = Some(state.into());
    }
    fn build_run(&mut self, _include_running: bool) -> Option<MightyGraphRun> {
        None
    }
    fn take_notice_runs(&mut self, _include_running: bool) -> Vec<MightyGraphRun> {
        vec![]
    }
    fn antigravity_answer(&mut self, text: &str) {
        self.0.lock().unwrap().answers.push(text.into());
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

fn fixture_path(provider: &str, name: &str) -> String {
    common::fixtures().join(format!("{provider}-acp-{name}.jsonl")).to_string_lossy().into_owned()
}

/// `Fake(provider, state, fixture, version, options)`: a catalog whose `provider` is the stand-in agent replaying the recording.
fn fake(provider: &str, state: &Path, fixture: &str, version: &str, options: &[&str]) -> Arc<mighty_run::providers::ProviderCatalog> {
    let mut prefix = vec!["--fake-acp".to_string(), state.to_string_lossy().into_owned(), fixture_path(provider, fixture)];
    prefix.extend(options.iter().map(|o| o.to_string()));
    let command = CliCommand::new(common::fake_text(), prefix, Some(version));
    let provider = provider.to_string();
    common::catalog(move |asked| (asked == provider).then(|| command.clone()))
}

fn lines(path: PathBuf) -> Vec<String> {
    std::fs::read_to_string(path).unwrap_or_default().lines().filter(|l| !l.is_empty()).map(str::to_string).collect()
}

/// Now, in milliseconds since 1970 (the stand-in's clock in `stdin-times.txt` and `stdin-ended`).
fn now_ms() -> u128 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis()
}

/// When the stand-in read its first stdin line that `test` takes, by its own clock.
fn read_at(directory: &Path, test: impl Fn(&str) -> bool) -> Option<u128> {
    lines(directory.join("stdin-times.txt")).iter().find_map(|l| {
        let (at, line) = l.split_once(' ')?;
        test(line).then(|| at.parse().ok()).flatten()
    })
}

/// When the stand-in's stdin ended, by its own clock; None while it is open.
fn stdin_ended(directory: &Path) -> Option<u128> {
    std::fs::read_to_string(directory.join("stdin-ended")).ok().and_then(|t| t.trim().parse().ok())
}

fn agent_files() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("macos")
        .join("Sources")
        .join("MightyCore")
        .join("Resources")
        .join("Agents")
}

fn start(id: &str, workspace: &Workspace, input: &str, provider: &str, mode: &str) -> StartRunRequest {
    StartRunRequest::new(id, workspace.id.as_deref().unwrap(), "claude", input)
        .with_provider(provider)
        .with_settings(RunSettings { permission_mode: mode.into(), ..RunSettings::default() })
}

#[tokio::test(flavor = "multi_thread")]
async fn an_opencode_run_speaks_acp_and_completes_whether_the_server_exits_or_is_ended() {
    common::korean();
    mighty_run::providers::set_agent_files(Some(agent_files()));
    for linger in [false, true] {
        let directory = common::temp(if linger { "acp-linger" } else { "acp-exit" });
        let workspace = workspace(&directory);
        let events = Events::default();
        let fed = Arc::new(Mutex::new(Fed::default()));
        let catalog = fake("opencode", &directory, "answer", "opencode v2.2.7", if linger { &["--fake-linger"] } else { &[] });
        let mut options = RunManagerOptions::new("", resolve(&workspace), events.emit());
        options.graph = Some(Arc::new(Recorder(fed.clone())));
        let manager = RunManager::new(catalog, options);
        manager.start(start("pane", &workspace, "hi there", "opencode", "manual")).await.unwrap();
        common::until(|| events.ended(), 30_000, "the run ends").await;
        let ended = now_ms();
        manager.dispose().await;
        let what = if linger { "an ended" } else { "an exited" };
        // The prompt's answer closes the agent's stdin (EndAcp) whether it then exits or lingers; one that lingers is ended two
        // seconds later, not at its own minute.
        let closed = stdin_ended(&directory).unwrap_or_else(|| panic!("{what} server's stdin was closed"));
        let after = ended.saturating_sub(closed);
        if linger {
            assert!((1_900..6_000).contains(&after), "a lingering server is ended about two seconds after its stdin closed: {after} ms");
        } else {
            assert!(after < 2_000, "an exiting server's run ends soon after its stdin closed: {after} ms");
        }
        assert_eq!(events.last_status().status.as_deref(), Some("completed"), "{what} server completes: {}", events.described());
        assert!(events.logs("assistant").contains(&"Hello! How can I help with this project?".to_string()), "the answer");
        assert!(events.all().iter().any(|e| e.kind == "resume" && e.resume_id.as_deref() == Some(OPENCODE_SESSION)), "the session");
        assert!(
            events
                .all()
                .iter()
                .any(|e| e.kind == "usage" && e.usage.as_ref().is_some_and(|u| u.provider == "opencode" && u.cost_usd == Some(0.0123))),
            "usage"
        );
        // The pane's graph is fed the channel's events and the shown answer: the request's block takes the answer and the turn's tokens.
        {
            let fed = fed.lock().unwrap();
            assert!(
                fed.frames
                    .iter()
                    .any(|f| f["type"] == "acp.prompt_result" && f["usage"]["inputTokens"] == 1203 && f["usage"]["outputTokens"] == 18),
                "the graph reads the prompt's result"
            );
            assert!(
                fed.answers == ["Hello! How can I help with this project?"] && fed.finished.as_deref() == Some("completed"),
                "the graph's answer and end: {:?} {:?}",
                fed.answers,
                fed.finished
            );
        }
        let argv: Vec<String> = serde_json::from_str(&std::fs::read_to_string(directory.join("argv.json")).unwrap()).unwrap();
        assert_eq!(argv, ["acp"], "argv is acp");
        let env = common::json(&std::fs::read_to_string(directory.join("env.json")).unwrap());
        // OpenCode 2's rules for a pane that asks: edits and everything outside the folder ask, and the app's question instructions.
        let content = common::json(env["OPENCODE_CONFIG_CONTENT"].as_str().expect("the run's rules"));
        let ask = |action: &str| serde_json::json!({"action": action, "effect": "ask", "resource": "*"});
        assert_eq!(
            content["permissions"],
            serde_json::json!([ask("edit"), ask("shell"), ask("webfetch"), ask("websearch"), ask("subagent"), ask("external_directory")]),
            "the run's rules: {content}"
        );
        let instructions: Vec<&str> = content["instructions"].as_array().into_iter().flatten().filter_map(Value::as_str).collect();
        assert!(
            instructions.len() == 1
                && Path::new(instructions[0]) == agent_files().join(agents::OPENCODE_QUESTION_INSTRUCTIONS)
                && Path::new(instructions[0]).is_file(),
            "the app's question instructions: {instructions:?}"
        );
        let stdin = lines(directory.join("stdin.txt"));
        assert!(
            stdin.len() == 3
                && stdin[0].contains("\"initialize\"")
                && stdin[1].contains("\"session/new\"")
                && stdin[2].contains("\"session/prompt\""),
            "stdin: {stdin:?}"
        );
        common::remove_when_free(&directory).await;
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_signed_out_opencode_run_ends_in_error_with_the_sign_in_reason() {
    common::korean();
    let directory = common::temp("acp-auth");
    let workspace = workspace(&directory);
    let events = Events::default();
    let catalog = fake("opencode", &directory, "auth", "opencode v2.2.7", &["--fake-linger"]);
    let manager = RunManager::new(catalog, RunManagerOptions::new("", resolve(&workspace), events.emit()));
    manager.start(start("pane", &workspace, "hi", "opencode", "manual")).await.unwrap();
    common::until(|| events.ended(), 30_000, "the run ends").await;
    manager.dispose().await;
    let status = events.last_status();
    assert!(
        status.status.as_deref() == Some("error") && status.reason.as_deref() == Some("authentication"),
        "a lost sign-in: {status:?} {}",
        events.described()
    );
    common::remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn an_open_code_1_is_not_run_and_asks_for_open_code_2() {
    common::korean();
    let directory = common::temp("acp-v1");
    let workspace = workspace(&directory);
    let events = Events::default();
    let catalog = fake("opencode", &directory, "answer", "1.19.5", &[]);
    let manager = RunManager::new(catalog, RunManagerOptions::new("", resolve(&workspace), events.emit()));
    let _ = manager.start(start("pane", &workspace, "hi", "opencode", "manual")).await;
    common::until(|| events.ended(), 30_000, "the run ends").await;
    manager.dispose().await;
    assert!(
        events.logs("error").contains(&strings::get_with("run.error.opencodeV1", &[("version", "1.19.5")])),
        "OpenCode 1 is named: {}",
        events.described()
    );
    assert!(events.last_status().status.as_deref() == Some("error") && !directory.join("argv.json").exists(), "and not run");
    common::remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn a_hermes_run_gives_full_access_as_yolo_and_turns_clarify_on_once() {
    common::korean();
    let _turn = HERMES.lock().await;
    agents::hermes_reset_question_tool();
    for (index, mode) in ["manual", "fullAccess", "manual"].into_iter().enumerate() {
        let directory = common::temp(&format!("acp-hermes-{mode}"));
        let workspace = workspace(&directory);
        let events = Events::default();
        let fed = Arc::new(Mutex::new(Fed::default()));
        // Full access also lifts the edit policy, which Hermes answers before the prompt (the recorded full-access session).
        let recording = if mode == "fullAccess" { "full-access" } else { "answer" };
        let catalog = fake("hermes", &directory, recording, "Hermes Agent v0.23.1 (2026.9.24) · stable", &[]);
        let mut options = RunManagerOptions::new("", resolve(&workspace), events.emit());
        options.graph = Some(Arc::new(Recorder(fed.clone())));
        let manager = RunManager::new(catalog, options);
        manager.start(start("pane", &workspace, "hi there", "hermes", mode)).await.unwrap();
        common::until(|| events.ended(), 30_000, "the run ends").await;
        manager.dispose().await;
        assert_eq!(events.last_status().status.as_deref(), Some("completed"), "{mode}: completed: {}", events.described());
        // The graph takes the answer, and no tokens (Hermes reports its session's running totals: the tracker reads none of them).
        {
            let fed = fed.lock().unwrap();
            assert!(fed.answers == ["Hi."] && fed.finished.as_deref() == Some("completed"), "{mode}: the graph: {:?}", fed.answers);
        }
        let env = common::json(&std::fs::read_to_string(directory.join("env.json")).unwrap());
        let yolo = if mode == "fullAccess" { Value::String("1".into()) } else { Value::Null };
        assert_eq!(env["HERMES_YOLO_MODE"], yolo, "{mode}: yolo");
        let stdin = lines(directory.join("stdin.txt"));
        // The policy goes unless the session already has it (a new session asks before edits).
        let frames: Vec<Value> = stdin.iter().map(|l| common::json(l)).collect();
        let session = frames.iter().find(|f| f["method"] == "session/prompt").map(|f| f["params"]["sessionId"].clone()).unwrap_or_default();
        let policies: Vec<&Value> = frames
            .iter()
            .filter(|frame| frame["method"] == "session/set_config_option" && frame["params"]["configId"] == "edit_approval_policy")
            .collect();
        if mode == "manual" {
            assert!(policies.is_empty(), "{mode}: no edit policy is sent: {stdin:?}");
        } else {
            assert!(
                policies.len() == 1
                    && policies[0]["params"]["value"] == agents::hermes_edit_policy(mode)
                    && policies[0]["params"]["sessionId"] == session
                    && session.is_string(),
                "{mode}: the edit policy, for the run's session: {policies:?}"
            );
        }
        let prompt = stdin.iter().find(|l| l.contains("\"session/prompt\"")).cloned().unwrap_or_default();
        // A fresh fake Hermes each time (its own state folder), the same executable: the first launch finds the key unset, sets it and
        // says so; the app remembers it for that executable, so the later launches ask Hermes nothing and say nothing.
        let notice: Vec<String> = events.logs("system").into_iter().filter(|t| t.contains(agents::HERMES_QUESTION_KEY)).collect();
        let config = lines(directory.join("config.log"));
        if index == 0 {
            assert_eq!(
                notice,
                [strings::get_with(
                    "run.notice.hermesClarifyEnabled",
                    &[("key", agents::HERMES_QUESTION_KEY), ("value", "hermes-acp, clarify, x_search")]
                )],
                "the pane says it turned clarify on"
            );
            let key = agents::HERMES_QUESTION_KEY;
            assert_eq!(
                config,
                [
                    format!("get {key}"),
                    "get toolsets".to_string(),
                    format!("set {key} [\"hermes-acp\",\"clarify\",\"x_search\"]"),
                    format!("get {key}")
                ],
                "the key read, the old toolsets read, the value Hermes stores as a list set and read back"
            );
        } else {
            assert!(notice.is_empty() && config.is_empty(), "{mode}: looked at once per launch: {notice:?} {config:?}");
        }
        assert!(prompt.contains("Note from the Mighty Code app"), "{mode}: the note follows the request while clarify is on: {prompt}");
        common::remove_when_free(&directory).await;
    }
    agents::hermes_reset_question_tool();
}

#[tokio::test(flavor = "multi_thread")]
async fn a_stop_tells_hermes_to_cancel_the_turn_before_the_process_ends() {
    common::korean();
    let _turn = HERMES.lock().await;
    agents::hermes_reset_question_tool();
    let directory = common::temp("acp-hermes-stop");
    let workspace = workspace(&directory);
    let events = Events::default();
    // clarify already on: no config set during the run.
    std::fs::write(directory.join("hermes-config.json"), "{\"platform_toolsets.acp\":[\"hermes-acp\",\"clarify\"]}").unwrap();
    let catalog = fake("hermes", &directory, "answer", "Hermes Agent v0.23.1", &["--fake-stall"]);
    let manager = RunManager::new(catalog, RunManagerOptions::new("", resolve(&workspace), events.emit()));
    manager.start(start("pane", &workspace, "stop me", "hermes", "manual")).await.unwrap();
    common::until(|| lines(directory.join("stdin.txt")).iter().any(|l| l.contains("\"session/prompt\"")), 30_000, "the prompt").await;
    let stopped = now_ms();
    manager.stop("pane").await.unwrap();
    let ended = now_ms();
    common::until(|| events.ended(), 30_000, "the run ends").await;
    manager.dispose().await;
    assert_eq!(events.last_status().status.as_deref(), Some("stopped"), "stopped: {}", events.described());
    let cancelled = read_at(&directory, |l| l.contains("\"session/cancel\"") && l.contains(HERMES_SESSION));
    let stdin = lines(directory.join("stdin.txt"));
    assert!(
        cancelled.is_some_and(|at| at >= stopped.saturating_sub(50) && at < stopped + 500),
        "session/cancel was read at once: {cancelled:?} after the stop at {stopped}: {stdin:?}"
    );
    // Hermes read the cancel with its stdin still open (closing it would end the ACP process before the gateway's turn stopped), and the
    // process was ended a second later.
    assert!(stdin_ended(&directory).is_none(), "Hermes's stdin was never closed before its end");
    assert!((900..3_000).contains(&(ended - stopped)), "the stop ends the run about a second later: {} ms", ended - stopped);
    agents::hermes_reset_question_tool();
    common::remove_when_free(&directory).await;
}

/// ACP, Prompt Turn › Cancellation: "The Client MUST respond to all pending session/request_permission requests with the cancelled
/// outcome." A stop answers Hermes's waiting approval `cancelled` before it writes `session/cancel`, and its card settles; an Allow
/// pressed in the second before Hermes is ended (here on the approval Hermes asks after the cancel) is refused as already settled and
/// never reaches it.
#[tokio::test(flavor = "multi_thread")]
async fn a_stop_answers_hermess_waiting_approval_cancelled_first_and_takes_no_late_allow() {
    common::korean();
    let _turn = HERMES.lock().await;
    agents::hermes_reset_question_tool();
    let directory = common::temp("acp-hermes-stop-cards");
    let workspace = workspace(&directory);
    let events = Events::default();
    std::fs::write(directory.join("hermes-config.json"), "{\"platform_toolsets.acp\":[\"hermes-acp\",\"clarify\"]}").unwrap();
    // The recording asks two approvals (request ids 0 and 1); the stand-in waits for each answer, as Hermes does.
    let catalog = fake("hermes", &directory, "tools", "Hermes Agent v0.23.1", &["--fake-await-answers"]);
    let cards: Arc<Mutex<Vec<ToolPermissionRequest>>> = Arc::default();
    let seen = cards.clone();
    let mut options = RunManagerOptions::new("", resolve(&workspace), events.emit());
    options.permission_requested = Some(Arc::new(move |value: ToolPermissionRequest| seen.lock().unwrap().push(value)));
    let manager = RunManager::new(catalog, options);
    manager.start(start("pane", &workspace, "run the tests", "hermes", "manual")).await.unwrap();
    let pending = || cards.lock().unwrap().iter().filter(|c| c.state == "pending").cloned().collect::<Vec<_>>();
    common::until(|| pending().len() == 1, 30_000, "the first approval").await;
    let first = pending()[0].clone();
    let stopping = manager.clone();
    let stop = tokio::spawn(async move { stopping.stop("pane").await });
    common::until(|| pending().len() == 2, 900, "the approval Hermes asks after the cancel, within its second").await;
    let second = pending()[1].clone();
    let late = manager.respond_to_tool_permission("pane", &second.id, true);
    stop.await.unwrap().unwrap();
    common::until(|| events.ended(), 30_000, "the run ends").await;
    manager.dispose().await;
    let frames: Vec<Value> = lines(directory.join("stdin.txt")).iter().map(|l| common::json(l)).collect();
    let answered = frames.iter().position(|f| f["id"] == 0 && f["result"]["outcome"]["outcome"] == "cancelled");
    let cancel = frames.iter().position(|f| f["method"] == "session/cancel");
    assert!(
        answered.is_some() && cancel.is_some() && answered < cancel,
        "the waiting approval was answered cancelled before session/cancel: {frames:?}"
    );
    assert!(
        cards.lock().unwrap().iter().any(|c| c.id == first.id && c.state == "cancelled"),
        "its card settled cancelled"
    );
    assert!(late.is_err_and(|e| e.message() == mighty_run::presentation::strings::already_settled()), "a late Allow is refused");
    assert!(!frames.iter().any(|f| f["id"] == 1), "and never reaches Hermes: {frames:?}");
    agents::hermes_reset_question_tool();
    common::remove_when_free(&directory).await;
}

/// A pane that asks never continues a Hermes session whose policy receipt cannot be read (it may have started with `--yolo` and would
/// not ask): the run starts a new session instead (`HermesAgent.Resume`, the rule the C# and Mac apps take too).
#[tokio::test(flavor = "multi_thread")]
async fn a_pane_that_asks_starts_a_new_hermes_session_when_its_receipt_cannot_be_read() {
    common::korean();
    let _turn = HERMES.lock().await;
    agents::hermes_reset_question_tool();
    let directory = common::temp("acp-hermes-receipt");
    let workspace = workspace(&directory);
    let events = Events::default();
    std::fs::write(directory.join("hermes-config.json"), "{\"platform_toolsets.acp\":[\"hermes-acp\",\"clarify\"]}").unwrap();
    let catalog = fake("hermes", &directory, "answer", "Hermes Agent v0.23.1", &[]);
    let mut options = RunManagerOptions::new("", resolve(&workspace), events.emit());
    // The session has answered before; whether it started with --yolo cannot be read.
    options.hermes_session = Some(Arc::new(|_, _| (Some(3), None)));
    let manager = RunManager::new(catalog, options);
    let mut request = start("pane", &workspace, "hi there", "hermes", "manual");
    request.resume_id = Some(HERMES_SESSION.into());
    manager.start(request).await.unwrap();
    common::until(|| events.ended(), 30_000, "the run ends").await;
    manager.dispose().await;
    assert_eq!(events.last_status().status.as_deref(), Some("completed"), "completed: {}", events.described());
    let methods: Vec<String> = lines(directory.join("stdin.txt"))
        .iter()
        .filter_map(|l| common::json(l)["method"].as_str().map(str::to_string))
        .collect();
    assert_eq!(methods, ["initialize", "session/new", "session/prompt"], "a new session, not the one asked for");
    agents::hermes_reset_question_tool();
    common::remove_when_free(&directory).await;
}

/// The manager a permission sink can answer through (the sink is made before the manager it calls).
type Cell = Arc<OnceLock<Weak<RunManager>>>;

/// Not a C# check of its own: the pane's two cards reach an ACP run through the runner's answers (C# `RespondToToolPermission` and
/// `AnswerQuestionnaire` hand them to the run's `AcpChannel`), over a real process — opencode's approval allowed once, its question
/// answered with a pick.
#[tokio::test(flavor = "multi_thread")]
async fn the_runner_hands_an_acp_agents_cards_to_its_channel() {
    common::korean();
    for (recording, question) in [("tools", false), ("question", true)] {
        let directory = common::temp(&format!("acp-cards-{recording}"));
        let workspace = workspace(&directory);
        let events = Events::default();
        let seen: Arc<Mutex<Vec<ToolPermissionRequest>>> = Arc::default();
        let cell: Cell = Arc::default();
        // The agent waits for each card's answer, as a real one does.
        let catalog = fake("opencode", &directory, recording, "opencode v2.2.7", &["--fake-await-answers"]);
        let mut options = RunManagerOptions::new("", resolve(&workspace), events.emit());
        let (sink, answer) = (seen.clone(), cell.clone());
        options.permission_requested = Some(Arc::new(move |value: ToolPermissionRequest| {
            sink.lock().unwrap().push(value.clone());
            if value.state != "pending" {
                return;
            }
            // Answered on the run's own context, as the card's buttons do.
            let manager = answer.get().and_then(Weak::upgrade).expect("the manager");
            if value.can_answer_questions {
                let asked = UserQuestionnaire::parse(&value.input_json).unwrap().questions[0].question.clone();
                let picks = IndexMap::from([(asked, UserQuestionAnswer::new(&["Green"], None))]);
                manager.answer_questionnaire("cards", &value.id, &picks).unwrap();
            } else {
                manager.respond_to_tool_permission("cards", &value.id, true).unwrap();
            }
        }));
        let manager = Arc::new(RunManager::new(catalog, options));
        cell.set(Arc::downgrade(&manager)).ok();
        manager.start(start("cards", &workspace, "hi there", "opencode", "manual")).await.unwrap();
        common::until(|| events.ended(), 30_000, "the run ends").await;
        manager.dispose().await;
        assert_eq!(events.last_status().status.as_deref(), Some("completed"), "{recording}: {}", events.described());
        let states: Vec<String> = seen.lock().unwrap().iter().map(|p| p.state.clone()).collect();
        let stdin = lines(directory.join("stdin.txt"));
        if question {
            assert_eq!(states, ["pending", "answered"], "the question was answered once");
            assert!(stdin.iter().any(|l| l.contains("\"action\":\"accept\"") && l.contains("\"q0\":\"Green\"")), "the pick: {stdin:?}");
        } else {
            assert_eq!(states, ["pending", "allowed"], "the approval was allowed once");
            assert!(stdin.iter().any(|l| l.contains("\"optionId\":\"once\"")), "opencode's own option: {stdin:?}");
        }
        common::remove_when_free(&directory).await;
    }
}
