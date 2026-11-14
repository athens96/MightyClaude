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
        manager.dispose().await;
        let what = if linger { "an ended" } else { "an exited" };
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
        let rules =
            agents::opencode_config_content("manual", mighty_run::providers::agent_file(agents::OPENCODE_QUESTION_INSTRUCTIONS).as_deref());
        assert_eq!(env["OPENCODE_CONFIG_CONTENT"], rules.as_str(), "the run's rules");
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
    for mode in ["manual", "fullAccess", "manual"] {
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
        assert_eq!(
            stdin.iter().any(|l| l.contains("\"edit_approval_policy\"") && l.contains(agents::hermes_edit_policy(mode))),
            mode != "manual",
            "{mode}: the edit policy: {stdin:?}"
        );
        let prompt = stdin.iter().find(|l| l.contains("\"session/prompt\"")).cloned().unwrap_or_default();
        // A fresh fake Hermes each time (its own state folder): the first launch sets the key, the app remembers it per executable.
        let notice: Vec<String> = events.logs("system").into_iter().filter(|t| t.contains(agents::HERMES_QUESTION_KEY)).collect();
        if mode == "manual" && !notice.is_empty() {
            assert_eq!(
                notice,
                [strings::get_with(
                    "run.notice.hermesClarifyEnabled",
                    &[("key", agents::HERMES_QUESTION_KEY), ("value", "hermes-acp, clarify, x_search")]
                )],
                "the pane says it turned clarify on"
            );
            assert!(
                lines(directory.join("config.log"))
                    .contains(&format!("set {} [\"hermes-acp\",\"clarify\",\"x_search\"]", agents::HERMES_QUESTION_KEY)),
                "the value Hermes stores as a list"
            );
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
    manager.stop("pane").await.unwrap();
    common::until(|| events.ended(), 30_000, "the run ends").await;
    manager.dispose().await;
    assert_eq!(events.last_status().status.as_deref(), Some("stopped"), "stopped: {}", events.described());
    let stdin = lines(directory.join("stdin.txt"));
    assert!(
        stdin.iter().any(|l| l.contains("\"session/cancel\"") && l.contains(HERMES_SESSION)),
        "session/cancel went before the end: {stdin:?}"
    );
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
