//! C# AcpGraphVerification.EveryRecordingIsTheMacsGraph: every shared recording of an ACP agent's run (native/contracts/fixtures/
//! opencode-acp-*.jsonl, hermes-acp-*.jsonl) replayed through mighty-run's ACP channel, its stream parser and the graph tracker wired as
//! the runner wires them, against native/contracts/fixtures/acp-graphs.json (the Mac's AcpGraphTests make it from the same recordings).
//!
//! Then the graph parts of C# AcpRunVerification, which mighty-run's own tests/acp_run.rs cannot reach (that crate cannot name this
//! one): opencode and Hermes runs through mighty-run's `RunManager` with this crate's `TrackerFactory`, over a real stand-in agent (this
//! crate's build of mighty-run's tests/fake/cli.rs, `--fake-acp`) — the request's block with its answer and tokens (Hermes's none), and
//! the replies and tool rows the pane keeps on it. Windows only: the Mac ProcessHost is W1-D's.

mod common;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use mighty_core::model::RunSettings;
use mighty_graph::ExecutionGraphNode;
use mighty_run::acp::{self, AcpChannel, AcpHooks, AcpRequest, agents};
use mighty_run::model::StartRunRequest;
use mighty_run::parser::{OutputParser, ParserHooks};
use serde_json::{Map, Value, json};

/// What the tracker emitted last for each block of a case, in the order the blocks first came, and how the run ended.
fn blocks(item: &Value) -> Value {
    mighty_run::strings::set_language("ko");
    let provider = item["provider"].as_str().unwrap();
    let hermes = provider == "hermes";
    let fields = &item["request"];
    let input = fields["input"].as_str().unwrap_or("hi there");
    let mode = fields["permissionMode"].as_str().unwrap_or("manual");
    let mut request = StartRunRequest::new("pane", "workspace", "claude", input)
        .with_provider(provider)
        .with_settings(RunSettings { permission_mode: mode.into(), ..RunSettings::default() });
    request.resume_id = fields["resumeId"].as_str().map(str::to_string);
    let run_id = format!("run-{provider}");
    let (tracker, emitted) = common::recording_tracker(&run_id, Some(input), provider, None);
    let tracker = Arc::new(Mutex::new(tracker));
    // As the runner wires a run: the parser's shown reply is the graph's answer, a tool's activity goes by the graph first.
    let (answers, rows) = (tracker.clone(), tracker.clone());
    let hooks = ParserHooks {
        answer: Some(Arc::new(move |text: &str| answers.lock().unwrap().antigravity_answer(text))),
        graph_activity: Some(Arc::new(move |value, tool| rows.lock().unwrap().activity(value, tool))),
        ..ParserHooks::default()
    };
    let parser = Arc::new(OutputParser::new(provider, &run_id, hooks));
    let (consumer, frames, permission) = (parser.clone(), tracker.clone(), parser.clone());
    let mut channel_hooks = AcpHooks::writing(Arc::new(|_| {}));
    channel_hooks.consume = Arc::new(move |line| {
        if let Ok(frame) = serde_json::from_str::<Value>(line) {
            frames.lock().unwrap().consume(&frame);
        }
        consumer.parse(line);
    });
    channel_hooks.activity = Arc::new(move |value, state| permission.permission_activity(value, state));
    let channel = AcpChannel::new(
        AcpRequest {
            run_id: run_id.clone(),
            resume_id: request.resume_id.clone(),
            workspace_path: "/tmp/project".into(),
            prompt_text: acp::prompt_text(input, &[], None),
            images: vec![],
            config_values: if hermes { agents::hermes_config_values(&request) } else { agents::opencode_config_values(&request) },
            free_choices: hermes,
        },
        channel_hooks,
    );
    channel.start();
    let recording = item["recording"].as_str().unwrap();
    for line in common::fixture_text(&format!("{provider}-acp-{recording}.jsonl")).split('\n') {
        let line = line.trim_end_matches('\r');
        if !line.is_empty() {
            channel.receive(line);
        }
    }
    parser.flush();
    parser.finish_activities(false);
    // As the run ends (RunManager): completed when the turn completed and nothing failed.
    let state = if channel.turn_completed() && !channel.failed() && !parser.failed() { "completed" } else { "error" };
    tracker.lock().unwrap().finish(state);
    let mut order: Vec<String> = Vec::new();
    let mut last: HashMap<String, ExecutionGraphNode> = HashMap::new();
    for node in emitted.lock().unwrap().iter() {
        if !last.contains_key(&node.id) {
            order.push(node.id.clone());
        }
        last.insert(node.id.clone(), node.clone());
    }
    let shown: Vec<Value> = order
        .iter()
        .map(|id| {
            let node = &last[id];
            let mut block = Map::new();
            block.insert("kind".into(), json!(node.kind));
            block.insert("state".into(), json!(node.state));
            block.insert("title".into(), json!(node.title));
            if let Some(asked) = &node.input {
                block.insert("input".into(), json!(asked));
            }
            if let Some(answer) = &node.output {
                block.insert("output".into(), json!(answer));
            }
            if let Some(usage) = &node.usage {
                block.insert(
                    "usage".into(),
                    json!({
                        "inputTokens": usage.input_tokens,
                        "outputTokens": usage.output_tokens,
                        "cacheReadTokens": usage.cache_read_tokens,
                        "cacheCreationTokens": usage.cache_creation_tokens,
                    }),
                );
            }
            Value::Object(block)
        })
        .collect();
    json!({"state": state, "blocks": shown})
}

#[test]
fn every_recording_is_the_macs_graph() {
    let fixture = common::fixture("acp-graphs.json");
    let cases = fixture["cases"].as_array().unwrap();
    assert_eq!(cases.len(), 16, "the fixture has its sixteen recordings");
    for item in cases {
        let name = format!("{}-acp-{}", item["provider"].as_str().unwrap(), item["recording"].as_str().unwrap());
        let made = blocks(item);
        assert!(made == item["expect"], "{name} differs; Rust makes: {}", serde_json::to_string_pretty(&made).unwrap());
    }
}

#[cfg(windows)]
mod runs {
    use std::path::Path;
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, Instant};

    use mighty_core::graph::MightyGraphRun;
    use mighty_core::model::{AppSnapshot, RunSession, RunSettings, Workspace};
    use mighty_graph::TrackerFactory;
    use mighty_run::acp::agents;
    use mighty_run::manager::ResolveWorkspace;
    use mighty_run::model::{CliCommand, RunEvent, StartRunRequest};
    use mighty_run::providers::{Finder, ProviderCatalog};
    use mighty_run::{RunManager, RunManagerOptions};

    fn temp(name: &str) -> std::path::PathBuf {
        let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos();
        let path = std::env::temp_dir().join(format!("mighty-graph-test-{name}-{}-{nanos}", std::process::id()));
        std::fs::create_dir_all(&path).unwrap();
        path
    }

    async fn remove_when_free(path: &Path) {
        for _ in 0..40 {
            if !path.exists() || std::fs::remove_dir_all(path).is_ok() {
                return;
            }
            tokio::time::sleep(Duration::from_millis(250)).await;
        }
    }

    /// `Fake(provider, state, fixture, version)`: a catalog whose `provider` is the stand-in agent replaying the recording.
    fn fake(provider: &str, state: &Path, fixture: &str, version: &str) -> Arc<ProviderCatalog> {
        let path = super::common::contracts().join("fixtures").join(format!("{provider}-acp-{fixture}.jsonl"));
        let prefix = vec!["--fake-acp".to_string(), state.to_string_lossy().into_owned(), path.to_string_lossy().into_owned()];
        let command = CliCommand::new(env!("CARGO_BIN_EXE_mighty-graph-fake-cli"), prefix, Some(version));
        let provider = provider.to_string();
        let finder: Finder = Arc::new(move |asked: String| {
            let found = (asked == provider).then(|| command.clone());
            Box::pin(async move { found })
        });
        Arc::new(ProviderCatalog::new(
            Arc::new(mighty_platform_windows::WindowsProcessHost::new()),
            Arc::new(mighty_platform::fake::FakeShellEnvironment::new(std::env::vars().collect())),
            Some(finder),
        ))
    }

    /// One run of `provider` over the recording through the run engine with the real tracker; every event it sent.
    async fn run(provider: &str, fixture: &str, version: &str, mode: &str, directory: &Path, workspace: &Workspace) -> Vec<RunEvent> {
        mighty_run::strings::set_language("ko");
        let events: Arc<Mutex<Vec<RunEvent>>> = Arc::default();
        let sink = events.clone();
        let resolved = workspace.clone();
        let resolve: ResolveWorkspace = Arc::new(move |_| {
            let workspace = resolved.clone();
            Box::pin(async move { Ok(workspace) })
        });
        let mut options = RunManagerOptions::new("", resolve, Arc::new(move |event| sink.lock().unwrap().push(event)));
        options.graph = Some(Arc::new(TrackerFactory::new()));
        let manager = RunManager::new(fake(provider, directory, fixture, version), options);
        let request = StartRunRequest::new("pane", workspace.id.as_deref().unwrap(), "claude", "hi there")
            .with_provider(provider)
            .with_settings(RunSettings { permission_mode: mode.into(), ..RunSettings::default() });
        manager.start(request).await.unwrap();
        let end = Instant::now() + Duration::from_secs(30);
        let ended = |events: &[RunEvent]| {
            events.iter().any(|e| e.kind == "status" && matches!(e.status.as_deref(), Some("completed" | "error" | "stopped")))
        };
        while !ended(&events.lock().unwrap()) {
            assert!(Instant::now() < end, "the {provider} run did not end");
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        manager.dispose().await;
        events.lock().unwrap().clone()
    }

    /// `LastGraph`: the request's graph as the pane last got it.
    fn last_graph(events: &[RunEvent]) -> Option<MightyGraphRun> {
        events.iter().rev().find(|e| e.kind == "graph_run").and_then(|e| e.graph_run.clone())
    }

    fn described(run: &Option<MightyGraphRun>) -> String {
        match run {
            None => "no graph".into(),
            Some(run) => format!(
                "{:?} {:?} {:?} {:?} {:?} blocks {}",
                run.provider,
                run.status,
                run.input,
                run.final_output,
                run.usage,
                run.agents.as_ref().map_or(0, Vec::len)
            ),
        }
    }

    fn workspace(directory: &Path) -> Workspace {
        Workspace { path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() }
    }

    /// AcpRunVerification.AnOpencodeRunSpeaksAcpAndCompletesWhetherTheServerExitsOrIsEnded, its graph: the request's one block with the
    /// answer and the turn's tokens.
    #[tokio::test(flavor = "multi_thread")]
    async fn an_opencode_runs_graph_is_its_request_with_the_answer_and_the_turns_tokens() {
        let directory = temp("acp-opencode");
        let workspace = workspace(&directory);
        let events = run("opencode", "answer", "opencode v2.2.7", "manual", &directory, &workspace).await;
        let graph = last_graph(&events);
        assert!(
            graph.as_ref().is_some_and(|g| g.provider.as_deref() == Some("opencode")
                && g.status.as_deref() == Some("completed")
                && g.input.as_deref() == Some("hi there")
                && g.final_output.as_deref() == Some("Hello! How can I help with this project?")
                && g.agents.as_ref().is_none_or(Vec::is_empty)
                && g.usage.as_ref().is_some_and(|u| u.input_tokens == 1203 && u.output_tokens == 18)),
            "the graph: {}",
            described(&graph)
        );
        remove_when_free(&directory).await;
    }

    /// AcpRunVerification.AnOpencodeRunsBlockKeepsItsRepliesAndToolRows: the request's block of an opencode run as its pane keeps it
    /// (the replies and the tool rows the transcript shows since the request, a sub-agent's own text kept out, the answer and the
    /// turn's tokens); the card nobody answered goes when its call settles, which then reads completed.
    #[tokio::test(flavor = "multi_thread")]
    async fn an_opencode_runs_block_keeps_its_replies_and_tool_rows() {
        let directory = temp("acp-opencode-tools");
        let workspace = workspace(&directory);
        let pane = RunSession {
            id: Some("pane".into()),
            workspace_id: workspace.id.clone(),
            provider: Some("opencode".into()),
            kind: Some("claude".into()),
            model: Some("default".into()),
            ..RunSession::default()
        };
        let mut snapshot = AppSnapshot {
            version: 1,
            workspaces: Some(vec![workspace.clone()]),
            sessions: Some(vec![Arc::new(pane)]),
            ..AppSnapshot::default()
        };
        let now = mighty_core::time::DateTimeOffset::now;
        // The composer records the request itself, as the app does.
        snapshot = mighty_run::apply::apply(&snapshot, &RunEvent::log("pane", "user", "hi there", Some("opencode")), now());
        let events = run("opencode", "tools", "opencode v2.2.7", "manual", &directory, &workspace).await;
        for event in &events {
            snapshot = mighty_run::apply::apply(&snapshot, event, now());
        }
        let runs = snapshot.session("pane").and_then(|s| s.graph_runs.clone()).unwrap_or_default();
        assert_eq!(runs.len(), 1, "one request's run");
        let run = runs[0].clone();
        assert!(
            run.provider.as_deref() == Some("opencode")
                && run.status.as_deref() == Some("completed")
                && run.final_output.as_deref() == Some("One test fails because main returns 1.")
                && run.agents.as_ref().is_none_or(Vec::is_empty)
                && run.usage.as_ref().is_some_and(|u| u.input_tokens == 3100 && u.output_tokens == 95 && u.cache_read_tokens == 2048),
            "the run: {}",
            described(&Some(run.clone()))
        );
        let entries = run.root_entries.clone().unwrap_or_default();
        let replies: Vec<String> =
            entries.iter().filter(|e| e.kind.as_deref() == Some("assistant")).filter_map(|e| e.text.clone()).collect();
        assert_eq!(replies, ["Let me run the tests.", "One test fails because main returns 1."], "the replies");
        let rows: Vec<String> = entries
            .iter()
            .filter_map(|e| e.activity.as_ref().filter(|a| a.kind != "turn"))
            .map(|a| format!("{} {}", a.tool_name.as_deref().unwrap_or(""), a.state))
            .collect();
        assert_eq!(rows, ["Read completed", "Bash completed", "Edit error"], "the tool rows");
        remove_when_free(&directory).await;
    }

    /// AcpRunVerification.AHermesRunGivesFullAccessAsYoloAndTurnsClarifyOnOnce, its graph: the request's one block with the answer, and no
    /// tokens (Hermes reports its session's running totals), whatever the mode.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_hermes_runs_graph_is_its_request_with_the_answer_and_no_tokens() {
        agents::hermes_reset_question_tool();
        for mode in ["manual", "fullAccess", "manual"] {
            let directory = temp(&format!("acp-hermes-{mode}"));
            let workspace = workspace(&directory);
            let recording = if mode == "fullAccess" { "full-access" } else { "answer" };
            let events = run("hermes", recording, "Hermes Agent v0.23.1 (2026.9.24) · stable", mode, &directory, &workspace).await;
            let graph = last_graph(&events);
            assert!(
                graph.as_ref().is_some_and(|g| g.provider.as_deref() == Some("hermes")
                    && g.status.as_deref() == Some("completed")
                    && g.input.as_deref() == Some("hi there")
                    && g.final_output.as_deref() == Some("Hi.")
                    && g.agents.as_ref().is_none_or(Vec::is_empty)
                    && g.usage.is_none()),
                "{mode}: the graph: {}",
                described(&graph)
            );
            remove_when_free(&directory).await;
        }
        agents::hermes_reset_question_tool();
    }
}
