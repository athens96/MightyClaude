//! C# PiVerification's recordings (MightyCode.Core.Tests/PiVerification.cs:42-128; the Mac replays the same files,
//! M/Tests/MightyCoreTests/PiTests.swift): native/contracts/fixtures/pi-rpc-*.jsonl fed to the tracker and to the run engine's stream
//! parser together, as `RunManager` feeds them (C#'s `Play`). The argv, the stdin and the real runs are mighty-run's (tests/pi.rs).

use std::sync::{Arc, Mutex};

use mighty_core::graph::{GraphTokenUsage, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, SessionUsage};
use mighty_graph::ExecutionGraphTracker;
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::{activity, compaction, strings};

const SESSION_ID: &str = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30";

fn fixture(name: &str) -> Vec<String> {
    let path =
        std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures").join(format!("pi-rpc-{name}.jsonl"));
    std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("{}: {e}", path.display()))
        .split('\n')
        .map(|l| l.trim_end_matches('\r').to_string())
        .filter(|l| !l.is_empty())
        .collect()
}

#[derive(Default)]
struct Replay {
    logs: Vec<(String, String)>,
    activities: Vec<AgentActivity>,
    resumes: Vec<String>,
    usage: Vec<SessionUsage>,
    replies: Vec<String>,
    turn_open_after_each: Vec<bool>,
    failed: bool,
    run: Option<MightyGraphRun>,
}

/// C# `PiVerification.Play`, in English as C#'s `English` runs it.
fn play(name: &str) -> Replay {
    strings::set_language("en");
    let result: Arc<Mutex<Replay>> = Arc::default();
    let tracker = Arc::new(Mutex::new(ExecutionGraphTracker::new(
        "run-pi",
        Some("go"),
        "pi",
        Some("default"),
        Box::new(|_| {}),
        None,
        None,
        Locale::new("en"),
    )));
    let (log, resume, rows, usage, reply) = (result.clone(), result.clone(), result.clone(), result.clone(), result.clone());
    let routed = tracker.clone();
    let parser = OutputParser::new(
        "pi",
        "run-pi",
        ParserHooks {
            log: Some(Arc::new(move |kind, text| log.lock().unwrap().logs.push((kind.into(), text.into())))),
            resume: Some(Arc::new(move |id| resume.lock().unwrap().resumes.push(id.into()))),
            activity: Some(Arc::new(move |value: AgentActivity| {
                routed.lock().unwrap().activity(&value, None);
                rows.lock().unwrap().activities.push(value);
            })),
            usage: Some(Arc::new(move |value| usage.lock().unwrap().usage.push(value))),
            pi_reply: Some(Arc::new(move |text| reply.lock().unwrap().replies.push(text.into()))),
            ..ParserHooks::default()
        },
    );
    for line in fixture(name) {
        tracker.lock().unwrap().consume(&serde_json::from_str(&line).unwrap());
        parser.parse(&line);
        result.lock().unwrap().turn_open_after_each.push(parser.pi_turn_open());
    }
    parser.flush();
    parser.finish_activities(false);
    let run = {
        let mut tracker = tracker.lock().unwrap();
        tracker.finish("completed");
        tracker.build_run(false)
    };
    let mut replay = std::mem::take(&mut *result.lock().unwrap());
    replay.failed = parser.failed();
    replay.run = run;
    replay
}

fn texts(run: &Replay, kind: &str) -> Vec<String> {
    run.logs.iter().filter(|(k, _)| k == kind).map(|(_, text)| text.clone()).collect()
}

/// PiVerification.cs:55 `AnAnswerIsLoggedOnceWhateverTheDeltasAndTheEndOfTheMessage`.
#[test]
fn an_answer_is_logged_once_whatever_the_deltas_and_the_end_of_the_message() {
    let run = play("answer");
    assert!(
        run.logs.len() == 1 && run.logs[0].0 == "assistant" && run.logs[0].1 == "Hello! How can I help with this project?",
        "one reply: {:?}",
        run.logs
    );
    assert!(!run.logs.iter().any(|(_, text)| text.contains("greets")), "the thinking text is not copied");
    assert!(run.resumes == [SESSION_ID] && !run.failed, "get_state names the session");
    assert!(run.turn_open_after_each.contains(&true) && !run.turn_open_after_each.last().unwrap(), "open from agent_start to agent_end");
    let block = run.run.as_ref().expect("the request's block");
    assert!(
        block.status.as_deref() == Some("completed")
            && block.final_output.as_deref() == Some("Hello! How can I help with this project?")
            && block.usage == Some(GraphTokenUsage { input_tokens: 1203, output_tokens: 18, ..GraphTokenUsage::default() }),
        "the request's block: {block:?}"
    );
}

/// PiVerification.cs:66 `ToolsAreRowsThatSettleByTheirCallIdAndTheSecondMessageIsLoggedToo`.
#[test]
fn tools_are_rows_that_settle_by_their_call_id_and_the_second_message_is_logged_too() {
    let run = play("tools");
    assert_eq!(
        texts(&run, "assistant"),
        [
            "Let me look at the entry point and run the tests.",
            "The edit did not apply, but the read shows `main` returns 1 and one test fails."
        ],
        "both replies"
    );
    let rows =
        |tool: &str| -> Vec<AgentActivity> { run.activities.iter().filter(|a| a.tool_name.as_deref() == Some(tool)).cloned().collect() };
    let read = rows("read");
    let last = read.last().unwrap();
    assert!(
        read.iter().map(|a| a.state.as_str()).eq(["running", "completed"])
            && last.kind == "read"
            && last.summary == "src/main.ts"
            && last.output.as_deref().is_some_and(|o| o.contains("export function main()")),
        "read: {read:?}"
    );
    let bash = rows("bash");
    assert!(
        bash.iter().map(|a| a.state.as_str()).eq(["running", "running", "completed"])
            && bash[0].kind == "command"
            && bash[0].summary == "npm test"
            && bash.last().unwrap().output.as_deref().is_some_and(|o| o.contains("1 failed | 3 passed")),
        "bash: {bash:?}"
    );
    let edit = rows("edit");
    assert!(
        edit.iter().map(|a| a.state.as_str()).eq(["running", "error"])
            && edit.last().unwrap().kind == "edit"
            && edit.last().unwrap().output.as_deref() == Some("Could not find the exact text in src/main.ts."),
        "edit: {edit:?}"
    );
    assert!(!run.failed, "no failure");
}

/// PiVerification.cs:80 `UsageAddsEachMessageWithCachedInputInsideInput`.
#[test]
fn usage_adds_each_message_with_cached_input_inside_input() {
    let run = play("tools");
    let usage = run.usage.last().expect("the usage").clone();
    assert!(usage.provider == "pi" && usage.token_scope == "run" && usage.source == "pi.rpc", "identity");
    assert!(
        usage.input_tokens == Some((2410 + 1800) + (3120 + 1800) + (3380 + 1800)) && usage.output_tokens == Some(96 + 64 + 41),
        "input and output: {usage:?}"
    );
    assert!(
        usage.cache_read_tokens == Some(3600)
            && usage.cache_write_tokens == Some(1800)
            && usage.total_tokens == Some(usage.input_tokens.unwrap() + usage.output_tokens.unwrap()),
        "cache and total"
    );
    assert!(
        usage.provider_session_id.as_deref() == Some(SESSION_ID)
            && usage.model.as_deref() == Some("claude-sonnet-4-5")
            && usage.context_window_tokens == Some(200_000)
            && usage.context_used_tokens == Some(3380 + 1800),
        "session, model and context"
    );
    assert!(
        usage.cost_usd.is_some_and(|c| c > 0.0)
            && usage.cost_scope.as_deref() == Some("run")
            && mighty_core::normalize::session_usage(&usage).is_some(),
        "cost"
    );
    let block = run.run.as_ref().unwrap();
    let tokens = block.usage.as_ref().unwrap();
    assert!(
        tokens.cache_read_tokens == 3600
            && tokens.output_tokens == 201
            && block.final_output.as_deref() == Some("The edit did not apply, but the read shows `main` returns 1 and one test fails."),
        "the block adds each message and the last text is the answer"
    );
}

/// PiVerification.cs:93 `ARetriedErrorIsNotTheRunsFailureAndKeepsTheTurnOpen`.
#[test]
fn a_retried_error_is_not_the_runs_failure_and_keeps_the_turn_open() {
    let run = play("retry");
    assert!(!run.failed && texts(&run, "error").is_empty(), "a retried error is no failure");
    let overloaded = "529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}";
    let notice = strings::get_with(
        "run.notice.piRetry",
        &[("attempt", "1"), ("max", "3"), ("error", &activity::clean(Some(overloaded), 512, true))],
    );
    assert!(texts(&run, "system").contains(&notice), "the retry notice");
    assert_eq!(texts(&run, "assistant"), ["Done: the summary is below."], "the answer");
    let lines = fixture("retry");
    let ends: Vec<usize> = (0..lines.len()).filter(|i| lines[*i].contains("\"type\":\"agent_end\"")).collect();
    assert_eq!(ends.len(), 2, "two agent_end");
    let open = &run.turn_open_after_each;
    assert!(!open[ends[0]] && open[ends[0] + 1], "the retry announced right after reopens the turn");
    assert!(!open[ends[1]] && !open[ends[1] + 1], "the last end closes it");
    assert!(texts(&run, "system").iter().any(|t| t.starts_with(&compaction::title()) && t.contains("88,320")), "the compaction is a line");
    let start = lines.iter().position(|l| l.contains("compaction_start")).unwrap();
    assert!(open[start] && !open.last().unwrap(), "the compaction holds the turn");
}

/// PiVerification.cs:111 `AnErrorNoRetryAnsweredFailsTheRunAndADialogIsCancelled`.
#[test]
fn an_error_no_retry_answered_fails_the_run_and_a_dialog_is_cancelled() {
    let run = play("error");
    let errors = texts(&run, "error");
    assert!(
        run.failed && errors.len() == 1 && errors[0].contains("authentication_error"),
        "the unanswered error fails the run: {errors:?}"
    );
    assert_eq!(run.replies.len(), 1, "the confirm is answered, the notify needs none");
    let reply: serde_json::Value = serde_json::from_str(run.replies[0].trim_end()).unwrap();
    assert!(
        reply["type"] == "extension_ui_response"
            && reply["id"] == "d7a0f1e2-0000-4000-8000-000000000001"
            && reply["cancelled"] == true
            && run.replies[0].ends_with('\n'),
        "a cancel, one line"
    );
    let notice = strings::get_with("run.notice.piDialogDismissed", &[("title", "Allow dangerous command?")]);
    assert!(texts(&run, "system").contains(&notice), "the notice");
}

/// PiVerification.cs:123 `ARejectedCommandFailsTheRun`.
#[test]
fn a_rejected_command_fails_the_run() {
    let run = play("rejected");
    assert!(run.failed && texts(&run, "error").iter().any(|t| t.contains("already processing")), "rejected");
}
