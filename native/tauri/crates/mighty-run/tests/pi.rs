//! C# PiVerification (MightyCode.Core.Tests/PiVerification.cs): the RPC stdin pi reads, attachments in its prompt, and real runs against
//! a pi that answers from a recording (`mighty-fake-cli --fake-cli pi <record> --pi-fixture <file>`, C#'s `FakeAsync`) through the
//! Windows ProcessHost; with CliLoginRecoveryVerification's pi sign-in checks. The recordings' replays through the parser and the graph
//! are mighty-graph's tests/pi.rs; the argv's in pi_arguments.rs; the session list mighty-history's.

mod common;

use mighty_run::model::StartRunRequest;
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::permissions::ProviderInput;
use mighty_run::{failure, providers};

const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";

/// PiVerification.cs:160 `TheRpcStdinIsGetStateThenThePromptAndASteerIsOneLine`.
#[test]
fn the_rpc_stdin_is_get_state_then_the_prompt_and_a_steer_is_one_line() {
    let startup = ProviderInput::pi_startup("fix \"it\" / now", &[]);
    let lines: Vec<&str> = startup.split('\n').filter(|l| !l.is_empty()).collect();
    assert!(lines.len() == 2 && lines[0] == "{\"type\":\"get_state\"}", "get_state first");
    let prompt = common::json(lines[1]);
    assert!(prompt["type"] == "prompt" && prompt["message"] == "fix \"it\" / now" && prompt.get("images").is_none(), "the prompt");
    let with_image = ProviderInput::pi_startup("look", &[("image/png".into(), "AAAA".into())]);
    let image = common::json(with_image.split('\n').nth(1).unwrap())["images"][0].clone();
    assert!(image["type"] == "image" && image["data"] == "AAAA" && image["mimeType"] == "image/png", "an image rides in the prompt");
    assert_eq!(ProviderInput::pi_steer("stop and do this"), "{\"type\":\"steer\",\"message\":\"stop and do this\"}\n", "a steer");
}

/// PiVerification.cs:172 `AttachmentsRideInThePromptOrAreNamedByPath`.
#[test]
fn attachments_ride_in_the_prompt_or_are_named_by_path() {
    use base64::Engine;
    common::korean();
    let png = base64::engine::general_purpose::STANDARD.decode(PNG).unwrap();
    let files =
        vec![mighty_run::attachments::make("image.png", &png).unwrap(), mighty_run::attachments::make("source.cs", b"class A {}").unwrap()];
    let staged = mighty_run::attachments::StagedAttachments::create(&files).unwrap();
    let mut request = StartRunRequest::new("pane", "w", "claude", "see").with_provider("pi");
    request.attachments = Some(files.clone());
    request.resume_id = Some("resume-id".into());
    let arguments = staged.arguments_for(&request, "").unwrap();
    assert!(
        arguments[..2] == ["--mode", "rpc"]
            && !arguments.iter().any(|a| a == "--add-dir")
            && arguments[arguments.len() - 2..] == ["--session", "resume-id"],
        "no added folder: {arguments:?}"
    );
    let input = staged.input_for(&request);
    let lines: Vec<&str> = input.split('\n').filter(|l| !l.is_empty()).collect();
    let prompt = common::json(lines[1]);
    let images = prompt["images"].as_array().unwrap();
    assert!(
        lines.len() == 2 && images.len() == 1 && images[0]["mimeType"] == "image/png" && images[0]["data"] == files[0].data_base64.as_str(),
        "the picture is in the prompt"
    );
    let message = prompt["message"].as_str().unwrap();
    let path = |index: usize| staged.files[index].1.to_string_lossy().into_owned();
    assert!(message.contains(&path(1)) && !message.contains(&path(0)), "the other file is named by the path of its copy");
    staged.dispose().unwrap();
}

/// `CliLoginRecoveryVerification.PiMessageEnd`: an assistant message pi ended, with the error it ended in.
fn pi_message_end(error: Option<&str>) -> String {
    let mut message = serde_json::json!({"role": "assistant", "content": [], "stopReason": if error.is_some() { "error" } else { "stop" }});
    if let Some(error) = error {
        message["errorMessage"] = serde_json::json!(error);
    }
    serde_json::json!({"type": "message_end", "message": message}).to_string()
}

/// CliLoginRecoveryVerification.cs:245 `PiSignInFailuresAndTheTerminalTable`, its stream part: pi's own sign-in failures, read from its
/// RPC stream. The terminal sign-ins' table (`CliTerminalSignIn`, `CliAccountTerminal`) is the CLI accounts' (W6).
#[test]
fn pi_sign_in_failures_and_the_terminal_table() {
    for text in [
        "No API key found for anthropic. Use /login or set ANTHROPIC_API_KEY.",
        "No models available. Use /login or set an API key.",
        "Please use /login",
    ] {
        assert!(failure::pi(text), "pi's sign-in failure: {text}");
    }
    for text in ["rate limit exceeded", "407 Proxy Authentication Required", "context window exceeded"] {
        assert!(!failure::pi(text), "not pi's sign-in failure: {text}");
    }
    let parser = |provider: &str| OutputParser::new(provider, "run-pi", ParserHooks::default());
    let rejected = parser("pi");
    rejected.parse(r#"{"type":"response","command":"prompt","success":false,"error":"No API key found for anthropic. Use /login or set ANTHROPIC_API_KEY."}"#);
    assert!(rejected.failed() && rejected.authentication_failure(), "a prompt pi refuses for its sign-in is a lost sign-in");
    let other = parser("pi");
    other.parse(r#"{"type":"response","command":"prompt","success":false,"error":"rate limit exceeded"}"#);
    assert!(other.failed() && !other.authentication_failure(), "another refusal is not");
    // A message that ended in it, until a retry starts or a later message answers; a retry that gave up on it.
    let turn = parser("pi");
    turn.parse(&pi_message_end(Some("No models available. Use /login or set an API key.")));
    assert!(turn.authentication_failure(), "a message that ended in it");
    turn.parse(r#"{"type":"auto_retry_start","attempt":1,"maxAttempts":3,"delayMs":1000,"errorMessage":"No models available."}"#);
    assert!(!turn.authentication_failure(), "a retry starting clears it");
    turn.parse(&pi_message_end(Some("No API key found for openai. Use /login.")));
    turn.parse(&pi_message_end(None));
    assert!(!turn.authentication_failure(), "a later answer clears it");
    turn.parse(r#"{"type":"auto_retry_end","success":false,"attempt":3,"finalError":"No API key found for openai. Use /login."}"#);
    assert!(turn.failed() && turn.authentication_failure(), "a retry that gave up on it");
    let claude = parser("claude");
    claude.parse(r#"{"type":"response","command":"prompt","success":false,"error":"No API key found. Use /login"}"#);
    assert!(!claude.authentication_failure(), "only pi's stream is read this way");
}

/// PiVerification.cs:455 `PiIsNeitherADelegationChildNorAParentNorInARemoteWorkspaceAndTheHostAdvertisesIt`, the run engine's part:
/// beta, its tool names and its graph. History replay (none for pi) is mighty-history's `history::PROVIDERS`; the delegation menus (W7), the remote workspace's menu and the phone's capabilities
/// (W8) are their stages'; the slash commands are mighty-composer's (tests/slash.rs) and the add menu mighty-core's.
#[test]
fn pi_is_beta_reads_its_tool_names_and_draws_a_graph() {
    assert!(providers::is_beta("pi") && providers::is_local_only("pi"), "beta, on this computer only");
    assert!(mighty_run::activity::kind("find") == "search" && mighty_run::activity::kind("ls") == "read", "pi's tool names");
    assert!(mighty_core::graph::GRAPH_PROVIDERS.contains(&"pi"), "a graph");
    assert!(
        mighty_run::strings::provider_label("pi") == "pi" && providers::executable("pi") == "pi" && providers::name("pi") == "pi",
        "name and place"
    );
}

#[cfg(windows)]
mod runs {
    use std::path::{Path, PathBuf};
    use std::sync::{Arc, Mutex};

    use mighty_core::graph::MightyGraphRun;
    use mighty_core::model::{AgentActivity, LogEntry, RunSettings, Workspace};
    use mighty_run::graph::{GraphFactory, GraphSink, GraphStart};
    use mighty_run::model::{RunEvent, StartRunRequest};
    use mighty_run::mods::ModMetadata;
    use mighty_run::{RunManager, RunManagerOptions, providers};
    use serde_json::Value;

    use super::common;

    const SESSION_ID: &str = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30";

    /// A graph standing in for mighty-graph's tracker: the request's run with its provider and state.
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
                provider: Some(self.start.provider.clone()),
                ..MightyGraphRun::default()
            })
        }
        fn take_notice_runs(&mut self, _include_running: bool) -> Vec<MightyGraphRun> {
            vec![]
        }
    }

    #[derive(Clone, Default)]
    struct Events(Arc<Mutex<Vec<RunEvent>>>);

    impl Events {
        fn all(&self) -> Vec<RunEvent> {
            self.0.lock().unwrap().clone()
        }
        fn ended(&self) -> bool {
            self.all().iter().any(|e| e.kind == "status" && matches!(e.status.as_deref(), Some("completed" | "error" | "stopped")))
        }
        fn last_status(&self) -> Option<String> {
            self.all().iter().rev().find(|e| e.kind == "status").and_then(|e| e.status.clone())
        }
    }

    /// `FakePi(record, fixture, extra)` behind a manager whose runs read the shipped extension and draw a graph.
    fn manager(directory: &Path, record: &Path, fixture: &str, extra: &[&str], events: &Events) -> (RunManager, Workspace) {
        providers::set_agent_files(Some(common::agents()));
        let file = common::fixtures().join(format!("pi-rpc-{fixture}.jsonl"));
        let mut args = vec!["--fake-cli", "pi"];
        let (record, file) = (record.to_string_lossy().into_owned(), file.to_string_lossy().into_owned());
        args.extend([record.as_str(), "--pi-fixture", file.as_str()]);
        args.extend(extra);
        let catalog = common::fake_catalog(&args);
        let workspace = Workspace { path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() };
        let resolved = workspace.clone();
        let list = events.0.clone();
        let mut options = RunManagerOptions::new(
            "",
            Arc::new(move |_| {
                let workspace = resolved.clone();
                Box::pin(async move { Ok(workspace) })
            }),
            Arc::new(move |event| list.lock().unwrap().push(event)),
        );
        options.graph = Some(Arc::new(StubGraph));
        // No session this run is asked to continue was ever written (C# looks in the real home's ~/.pi, where it is not either).
        options.pi_sessions = Some(Arc::new(|_, _, _| false));
        (RunManager::new(catalog, options), workspace)
    }

    fn lines(path: PathBuf) -> Vec<String> {
        std::fs::read_to_string(path).unwrap_or_default().split('\n').filter(|l| !l.is_empty()).map(str::to_string).collect()
    }

    fn suffixed(record: &Path, suffix: &str) -> PathBuf {
        PathBuf::from(format!("{}{suffix}", record.display()))
    }

    fn pi(id: &str, workspace: &Workspace, input: &str) -> StartRunRequest {
        StartRunRequest::new(id, workspace.id.as_deref().unwrap(), "claude", input).with_provider("pi")
    }

    /// CliLoginRecoveryVerification.cs:310 `PiFakeCliRunLostItsSignIn`: a pi run whose turn ended on its own sign-in failure ends with
    /// the authentication reason; another failure does not.
    #[tokio::test(flavor = "multi_thread")]
    async fn pi_fake_cli_run_lost_its_sign_in() {
        common::korean();
        let directory = common::temp("pi-sign-in");
        for (error, expected) in [
            ("No API key found for anthropic. Use /login or set ANTHROPIC_API_KEY.", Some("authentication")),
            ("rate limit exceeded", None),
        ] {
            let fixture = directory.join(format!("pi-{}.jsonl", expected.unwrap_or("other")));
            let lines = [
                r#"{"id":"s1","type":"response","command":"get_state","success":true,"data":{"sessionId":"0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30"}}"#.to_string(),
                r#"{"type":"agent_start"}"#.to_string(),
                super::pi_message_end(Some(error)),
                r#"{"type":"agent_end","messages":[]}"#.to_string(),
            ];
            std::fs::write(&fixture, format!("{}\n", lines.join("\n"))).unwrap();
            let record = directory.join(format!("record-{}", expected.unwrap_or("other")));
            let (record, file) = (record.to_string_lossy().into_owned(), fixture.to_string_lossy().into_owned());
            let catalog = common::fake_catalog(&["--fake-cli", "pi", &record, "--pi-fixture", &file]);
            let events = Events::default();
            let workspace = Workspace { path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() };
            let resolved = workspace.clone();
            let list = events.0.clone();
            let manager = RunManager::new(
                catalog,
                RunManagerOptions::new(
                    "",
                    Arc::new(move |_| {
                        let workspace = resolved.clone();
                        Box::pin(async move { Ok(workspace) })
                    }),
                    Arc::new(move |event| list.lock().unwrap().push(event)),
                ),
            );
            manager.start(pi("pi-pane", &workspace, "hello")).await.unwrap();
            common::until(|| events.ended(), 30000, "the pi run ends").await;
            manager.dispose().await;
            let all = events.all();
            let last = all.iter().rev().find(|e| e.kind == "status").unwrap();
            assert!(
                last.status.as_deref() == Some("error") && last.reason.as_deref() == expected,
                "{error}: expected error with reason {expected:?}, got {:?} with {:?}",
                last.status,
                last.reason
            );
        }
        common::remove_when_free(&directory).await;
    }

    /// PiVerification.cs:381 `ARunWritesTheStartupLinesKeepsStdinOpenUntilTheTurnEndsThenCompletes`.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_run_writes_the_startup_lines_keeps_stdin_open_until_the_turn_ends_then_completes() {
        common::korean();
        let directory = common::temp("pi-run");
        let record = directory.join("pi");
        let events = Events::default();
        // pi stays quiet for 1.5 s before its `agent_end`: longer than the second of silence after which a turn that ended is closed.
        let (manager, workspace) = manager(&directory, &record, "answer", &["--pi-pause-before-end", "1500"], &events);
        manager.start(pi("pane", &workspace, "hi there")).await.unwrap();
        common::until(|| events.ended(), 30000, "the pi run ends").await;
        manager.dispose().await;
        let all = events.all();
        assert_eq!(
            events.last_status().as_deref(),
            Some("completed"),
            "completed: {:?}",
            all.iter().map(|e| (&e.kind, &e.status)).collect::<Vec<_>>()
        );
        assert!(
            all.iter().any(|e| e.kind == "log"
                && e.entry.as_ref().is_some_and(
                    |l| l.kind.as_deref() == Some("assistant") && l.text.as_deref() == Some("Hello! How can I help with this project?")
                )),
            "the reply"
        );
        assert!(all.iter().any(|e| e.kind == "resume" && e.resume_id.as_deref() == Some(SESSION_ID)), "the session id");
        assert!(
            all.iter().any(|e| e.kind == "usage" && e.usage.as_ref().is_some_and(|u| u.provider == "pi" && u.output_tokens == Some(18))),
            "the usage"
        );
        assert!(
            all.iter().any(|e| e.kind == "graph_run" && e.graph_run.as_ref().is_some_and(|g| g.provider.as_deref() == Some("pi"))),
            "the graph"
        );
        let argv = common::recorded_args(&record);
        let extension = providers::agent_file(providers::PI_QUESTION_EXTENSION).unwrap();
        let from: Vec<&str> = argv.iter().skip_while(|a| *a != "--mode").map(String::as_str).collect();
        // The app's ask_user extension rides along, and the read-only list names its tool.
        assert_eq!(from, ["--mode", "rpc", "--tools", "read,grep,find,ls,ask_user", "--extension", extension.as_str()], "argv: {argv:?}");
        let stdin = lines(suffixed(&record, ".stdin"));
        assert!(
            stdin.len() == 2 && stdin[0] == "{\"type\":\"get_state\"}" && stdin[1].contains("\"message\":\"hi there\""),
            "stdin: get_state then the prompt: {stdin:?}"
        );
        assert_eq!(
            std::fs::read_to_string(suffixed(&record, ".eof")).unwrap_or_default(),
            "after agent_end",
            "stdin stayed open until the turn ended"
        );
        common::remove_when_free(&directory).await;
    }

    /// PiVerification.cs:408 `ASteerIsWrittenWhileTheTurnIsOpenAndRefusedBeforeAndAfter`.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_steer_is_written_while_the_turn_is_open_and_refused_before_and_after() {
        common::korean();
        let directory = common::temp("pi-steer");
        let record = directory.join("pi");
        let events = Events::default();
        // pi holds its `agent_start` back until the check lets it go, so a steer can be tried while the run is up and the turn not open.
        let hold = directory.join("go");
        let hold_text = hold.to_string_lossy().into_owned();
        let (manager, workspace) = manager(&directory, &record, "answer", &["--pi-steer", "--pi-hold-start", &hold_text], &events);
        assert!(!manager.try_steer("pane", "nobody home"), "nothing running");
        manager.start(pi("pane", &workspace, "hi")).await.unwrap();
        common::until(
            || std::fs::read_to_string(suffixed(&record, ".stdin")).is_ok_and(|s| s.contains("\"prompt\"")),
            30000,
            "pi read the prompt",
        )
        .await;
        tokio::time::sleep(std::time::Duration::from_millis(300)).await;
        assert!(!manager.try_steer("pane", "before the turn"), "refused between the start and agent_start");
        std::fs::write(&hold, "").unwrap();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(30);
        let mut steered = false;
        while !steered && std::time::Instant::now() < deadline {
            steered = manager.try_steer("pane", "also check the lint");
            if !steered {
                tokio::time::sleep(std::time::Duration::from_millis(20)).await;
            }
        }
        assert!(steered, "accepted once the turn is open");
        common::until(|| events.ended(), 30000, "the steered run ends").await;
        assert_eq!(events.last_status().as_deref(), Some("completed"), "completed");
        let stdin = std::fs::read_to_string(suffixed(&record, ".stdin")).unwrap_or_default();
        assert!(stdin.contains("{\"type\":\"steer\",\"message\":\"also check the lint\"}"), "the steer line: {stdin}");
        assert!(!stdin.contains("before the turn"), "the refused steer was never written: {stdin}");
        assert!(!manager.try_steer("pane", "too late"), "refused after the run");
        manager.dispose().await;
        common::remove_when_free(&directory).await;
    }

    /// PiVerification.cs:431 `StoppingARunEndsItStoppedAndAResumeOfAMissingSessionStartsANewOne`.
    #[tokio::test(flavor = "multi_thread")]
    async fn stopping_a_run_ends_it_stopped_and_a_resume_of_a_missing_session_starts_a_new_one() {
        common::korean();
        let directory = common::temp("pi-stop");
        let record = directory.join("pi");
        let events = Events::default();
        let (manager, workspace) = manager(&directory, &record, "answer", &["--pi-hang"], &events);
        // This session was never written: pi would exit on it, so the run drops --session.
        let mut request =
            pi("pane", &workspace, "stop me").with_settings(RunSettings { permission_mode: "fullAccess".into(), ..RunSettings::default() });
        request.resume_id = Some("0197c1d2-dead-7c4e-9b1a-5d2e8f6a4b34".into());
        manager.start(request).await.unwrap();
        common::until(
            || std::fs::read_to_string(suffixed(&record, ".stdin")).is_ok_and(|s| s.contains("prompt")),
            30000,
            "the prompt is read",
        )
        .await;
        manager.stop("pane").await.unwrap();
        assert_eq!(events.last_status().as_deref(), Some("stopped"), "stopped");
        manager.dispose().await;
        let argv = common::recorded_args(&record);
        let extension = providers::agent_file(providers::PI_QUESTION_EXTENSION).unwrap();
        let from: Vec<&str> = argv.iter().skip_while(|a| *a != "--mode").map(String::as_str).collect();
        assert_eq!(from, ["--mode", "rpc", "--extension", extension.as_str()], "no --session: {argv:?}");
        assert!(!argv.iter().any(|a| a == "--session"), "the missing session is dropped");
        common::remove_when_free(&directory).await;
    }

    /// PiDialogVerification.cs:94's timed dialog through the runner: pi gives its select dialog a 1.5 s deadline, and the run's own timer
    /// (C#'s `timer` → `PiDialogs.TimedOut`) takes the card away when it passes, unanswered, and tells the pane.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_timed_dialogs_card_goes_when_the_runs_deadline_passes() {
        use mighty_run::permissions::ToolPermissionRequest;
        use std::time::{Duration, Instant};

        common::korean();
        providers::set_agent_files(Some(common::agents()));
        let directory = common::temp("pi-deadline");
        let record = directory.join("pi").to_string_lossy().into_owned();
        let file = common::fixtures().join("pi-rpc-dialogs.jsonl").to_string_lossy().into_owned();
        let catalog = common::fake_catalog(&["--fake-cli", "pi", &record, "--pi-fixture", &file, "--pi-dialogs"]);
        let workspace = Workspace { path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() };
        let events = Events::default();
        let cards: Arc<Mutex<Vec<(ToolPermissionRequest, Instant)>>> = Arc::default();
        let (resolved, list, seen) = (workspace.clone(), events.0.clone(), cards.clone());
        let mut options = RunManagerOptions::new(
            "",
            Arc::new(move |_| {
                let workspace = resolved.clone();
                Box::pin(async move { Ok(workspace) })
            }),
            Arc::new(move |event| list.lock().unwrap().push(event)),
        );
        options.permission_requested = Some(Arc::new(move |card| seen.lock().unwrap().push((card, Instant::now()))));
        options.pi_sessions = Some(Arc::new(|_, _, _| false));
        let manager = RunManager::new(catalog, options);
        manager.start(pi("pane", &workspace, "hi")).await.unwrap();
        let timed = |state: &str| {
            let cards = cards.lock().unwrap();
            cards.iter().find(|(card, _)| card.input_json.contains("Pick a color") && card.state == state).map(|(_, at)| *at)
        };
        common::until(|| timed("pending").is_some(), 30000, "the timed dialog's card").await;
        common::until(|| timed("cancelled").is_some(), 10000, "the card goes when the deadline passes").await;
        let waited = timed("cancelled").unwrap() - timed("pending").unwrap();
        let told = mighty_run::strings::get_with("run.notice.piDialogTimedOut", &[("title", "Pick a color")]);
        let warned = events.all().iter().any(|e| e.entry.as_ref().is_some_and(|l| l.text.as_deref() == Some(told.as_str())));
        manager.stop("pane").await.unwrap();
        manager.dispose().await;
        assert!(waited >= Duration::from_millis(1300) && waited < Duration::from_secs(5), "after its 1.5 s: {waited:?}");
        assert!(warned, "the pane is told");
        common::remove_when_free(&directory).await;
    }
}
