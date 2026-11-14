//! C# AcpVerification: the ACP client (opencode, Hermes) against the Mac's recordings (native/contracts/fixtures/opencode-acp-*.jsonl,
//! hermes-acp-*.jsonl) with the Mac's results — the channel's requests and answers, the transcript, the activity rows, usage, the
//! approval and question cards. No process: the channel is fed the recordings line by line, as the run's stdout pump feeds it.

mod common;

use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::model::{AgentActivity, RunSettings, SessionUsage};
use mighty_run::acp::agents::{self, HermesStderrFilter};
use mighty_run::acp::{self, AcpChannel, AcpHooks, AcpRequest};
use mighty_run::model::StartRunRequest;
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::presentation::ToolPermissionPresentation;
use mighty_run::questionnaire::{UserQuestion, UserQuestionAnswer, UserQuestionnaire};
use mighty_run::strings;
use serde_json::Value;

const OPENCODE_SESSION: &str = "ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw";
const HERMES_SESSION: &str = "local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b";
const HERMES_RESUMED: &str = "local-a8a2537a19ce1905c0a99559ca0f2e86197234e68b0931bf190fc6517e91386e";
const HERMES_STALE: &str = "local-b3d62fb4c0780d2c11596ef324e3582b301cb89d9993a5daaa93c6431187c278";
const HERMES_MODEL: &str = "custom:example-model-2.5";
const HERMES_NAMED_MODEL: &str = "custom:llm.example.com:example-model-2.5";

fn fixture(provider: &str, name: &str) -> Vec<String> {
    common::recording(&format!("{provider}-acp-{name}"))
}

#[derive(Default)]
struct Shared {
    logs: Mutex<Vec<(String, String)>>,
    activities: Mutex<Vec<AgentActivity>>,
    resumes: Mutex<Vec<String>>,
    usage: Mutex<Vec<SessionUsage>>,
    writes: Mutex<Vec<Value>>,
    permissions: Mutex<Vec<ToolPermissionRequest>>,
    warnings: Mutex<Vec<String>>,
    failures: Mutex<Vec<String>>,
    completed: AtomicUsize,
}

/// What a channel and its parser did with a recording.
struct Replay {
    shared: Arc<Shared>,
    parser: Arc<OutputParser>,
    channel: Arc<AcpChannel>,
}

impl Replay {
    fn logs(&self) -> Vec<(String, String)> {
        self.shared.logs.lock().unwrap().clone()
    }
    fn texts(&self, kind: &str) -> Vec<String> {
        self.logs().into_iter().filter(|(k, _)| k == kind).map(|(_, t)| t).collect()
    }
    fn activities(&self) -> Vec<AgentActivity> {
        self.shared.activities.lock().unwrap().clone()
    }
    fn tools(&self, name: &str) -> Vec<AgentActivity> {
        self.activities().into_iter().filter(|a| a.tool_name.as_deref() == Some(name)).collect()
    }
    fn last_row(&self) -> AgentActivity {
        self.activities().into_iter().rev().find(|a| a.kind != "turn").expect("a tool row")
    }
    fn resumes(&self) -> Vec<String> {
        self.shared.resumes.lock().unwrap().clone()
    }
    fn usage(&self) -> Vec<SessionUsage> {
        self.shared.usage.lock().unwrap().clone()
    }
    fn writes(&self) -> Vec<Value> {
        self.shared.writes.lock().unwrap().clone()
    }
    fn methods(&self) -> Vec<String> {
        self.writes().iter().filter_map(|w| w["method"].as_str().map(str::to_string)).collect()
    }
    fn answers(&self) -> Vec<Value> {
        self.writes().into_iter().filter(|w| w.get("result").is_some()).collect()
    }
    /// The result written for the agent's request `id` (a number).
    fn result(&self, id: i64) -> Option<Value> {
        self.writes().into_iter().find(|w| w["id"].as_i64() == Some(id) && w["id"].is_number()).and_then(|w| w.get("result").cloned())
    }
    fn permissions(&self) -> Vec<ToolPermissionRequest> {
        self.shared.permissions.lock().unwrap().clone()
    }
    fn states(&self) -> Vec<String> {
        self.permissions().into_iter().map(|p| p.state).collect()
    }
    fn warnings(&self) -> Vec<String> {
        self.shared.warnings.lock().unwrap().clone()
    }
    fn failures(&self) -> Vec<String> {
        self.shared.failures.lock().unwrap().clone()
    }
    fn completed(&self) -> usize {
        self.shared.completed.load(Ordering::SeqCst)
    }
    fn receive(&self, line: &str) {
        self.channel.receive(line);
    }
}

fn option(answer: &Value) -> Option<String> {
    answer["result"]["outcome"]["optionId"].as_str().map(str::to_string)
}

fn request(provider: &str, input: &str, model: &str, settings: RunSettings, resume: Option<&str>) -> StartRunRequest {
    let mut request = StartRunRequest::new("pane", "workspace", "claude", input).with_provider(provider).with_settings(settings);
    request.model = model.into();
    request.resume_id = resume.map(str::to_string);
    request
}

fn simple(provider: &str, input: &str) -> StartRunRequest {
    request(provider, input, "default", RunSettings::default(), None)
}

fn settings(effort: &str, mode: &str) -> RunSettings {
    RunSettings { effort: effort.into(), permission_mode: mode.into(), ..RunSettings::default() }
}

fn channel(request: &StartRunRequest, admit: Option<acp::AdmitHook>, note: Option<&str>) -> Replay {
    common::korean();
    let shared = Arc::new(Shared::default());
    let provider = request.provider.clone();
    let (logs, resumes, activities, usage) = (shared.clone(), shared.clone(), shared.clone(), shared.clone());
    let hooks = ParserHooks {
        log: Some(Arc::new(move |kind, text| logs.logs.lock().unwrap().push((kind.to_string(), text.to_string())))),
        resume: Some(Arc::new(move |id| resumes.resumes.lock().unwrap().push(id.to_string()))),
        activity: Some(Arc::new(move |value| activities.activities.lock().unwrap().push(value))),
        usage: Some(Arc::new(move |value| usage.usage.lock().unwrap().push(value))),
        ..ParserHooks::default()
    };
    let parser = Arc::new(OutputParser::new(&provider, &format!("run-{provider}"), hooks));
    let hermes = provider == "hermes";
    let (writes, consumer, emits, rows, warnings, failures, completed) =
        (shared.clone(), parser.clone(), shared.clone(), parser.clone(), shared.clone(), shared.clone(), shared.clone());
    let hooks = AcpHooks {
        write: Arc::new(move |frame| writes.writes.lock().unwrap().push(common::json(frame.trim_end_matches('\n')))),
        consume: Arc::new(move |line| consumer.parse(line)),
        emit: Arc::new(move |value| emits.permissions.lock().unwrap().push(value)),
        activity: Arc::new(move |value, state| rows.permission_activity(value, state)),
        warning: Arc::new(move |message| warnings.warnings.lock().unwrap().push(message.to_string())),
        fail: Arc::new(move |message| failures.failures.lock().unwrap().push(message.to_string())),
        completed: Arc::new(move || {
            completed.completed.fetch_add(1, Ordering::SeqCst);
        }),
        admit,
    };
    let config_values = if hermes { agents::hermes_config_values(request) } else { agents::opencode_config_values(request) };
    let channel = Arc::new(AcpChannel::new(
        AcpRequest {
            run_id: format!("run-{provider}"),
            resume_id: request.resume_id.clone(),
            workspace_path: "/tmp/project".into(),
            prompt_text: acp::prompt_text(&request.input, &[], note),
            images: vec![],
            config_values,
            free_choices: hermes,
        },
        hooks,
    ));
    Replay { shared, parser, channel }
}

fn play(provider: &str, name: &str, request: Option<StartRunRequest>, answer: Option<&dyn Fn(&Replay)>) -> Replay {
    let run = channel(&request.unwrap_or_else(|| simple(provider, "hi there")), None, None);
    run.channel.start();
    for line in fixture(provider, name) {
        run.receive(&line);
        if let Some(answer) = answer {
            answer(&run);
        }
    }
    run.parser.flush();
    run.parser.finish_activities(false);
    run
}

/// `Answering`: each card answered once, as it appears.
fn answering(answer: impl Fn(&Replay, &ToolPermissionRequest)) -> impl Fn(&Replay) {
    move |run| {
        let permissions = run.permissions();
        if let Some(card) = permissions.last().filter(|c| c.state == "pending")
            && permissions.iter().filter(|p| p.id == card.id).count() == 1
        {
            answer(run, card);
        }
    }
}

fn allowing(allow: bool) -> impl Fn(&Replay) {
    move |run| {
        if let Some(ask) = run.permissions().last().filter(|c| c.state == "pending") {
            run.channel.respond(&ask.id, allow).unwrap();
        }
    }
}

fn questionnaire(card: &ToolPermissionRequest) -> UserQuestionnaire {
    UserQuestionnaire::parse(&card.input_json).expect("the card's questions parse")
}

fn first_question(card: &ToolPermissionRequest) -> UserQuestion {
    questionnaire(card).questions[0].clone()
}

fn answer(card: &ToolPermissionRequest, picked: &[&str], typed: Option<&str>) -> IndexMap<String, UserQuestionAnswer> {
    IndexMap::from([(first_question(card).question, UserQuestionAnswer::new(picked, typed))])
}

fn labels(question: &UserQuestion) -> Vec<String> {
    question.options.iter().map(|o| o.label.clone()).collect()
}

/// A value as compact JSON text, its keys in the order they were written.
fn compact(value: &Value) -> String {
    value.to_string()
}

fn text(value: &Value) -> &str {
    value.as_str().unwrap_or("")
}

// ── opencode ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

#[test]
fn a_new_opencode_session_is_initialize_then_session_new_then_one_prompt() {
    let run = play("opencode", "answer", None, None);
    assert_eq!(run.methods(), ["initialize", "session/new", "session/prompt"], "methods");
    let writes = run.writes();
    assert!(writes.iter().all(|w| w["jsonrpc"] == "2.0"), "JSON-RPC 2.0");
    let initialize = &writes[0]["params"];
    assert!(
        initialize["protocolVersion"] == 1
            && initialize["clientCapabilities"]["terminal"] == false
            && initialize["clientCapabilities"]["elicitation"]["form"].as_object().is_some_and(|f| f.is_empty()),
        "initialize offers no terminal and form elicitation"
    );
    assert!(writes[1]["params"]["cwd"] == "/tmp/project" && writes[1]["params"]["mcpServers"] == serde_json::json!([]), "session/new");
    assert!(writes[2]["params"]["sessionId"] == OPENCODE_SESSION && writes[2]["params"]["prompt"][0]["text"] == "hi there", "the prompt");
    assert!(
        run.channel.turn_completed() && !run.channel.failed() && run.completed() == 1 && run.failures().is_empty(),
        "the turn completed"
    );
    let logs = run.logs();
    assert!(
        logs.iter().map(|l| l.0.as_str()).eq(["assistant"])
            && logs[0].1 == "Hello! How can I help with this project?"
            && !logs.iter().any(|l| l.1.contains("greets")),
        "streamed text once, no thoughts: {logs:?}"
    );
    assert!(run.resumes() == [OPENCODE_SESSION] && !run.parser.failed(), "the session is the pane's resume id");
    let last = run.usage().last().cloned().unwrap();
    assert!(
        last.provider == "opencode"
            && last.source == "opencode.acp"
            && last.provider_session_id.as_deref() == Some(OPENCODE_SESSION)
            && last.model.as_deref() == Some("opencode/big-pickle"),
        "usage identity: {last:?}"
    );
    assert!(
        last.cost_usd == Some(0.0123)
            && last.cost_scope.as_deref() == Some("session")
            && last.context_used_tokens == Some(1221)
            && last.context_window_tokens == Some(200000),
        "the session's cost and the context"
    );
    assert!(
        last.input_tokens == Some(1203) && last.output_tokens == Some(18) && last.total_tokens == Some(1221) && last.token_scope == "run",
        "the turn's tokens"
    );
}

#[test]
fn opencode_settings_go_as_config_options_and_one_the_agent_lacks_is_named_and_skipped() {
    let run = channel(&request("opencode", "plan it", "anthropic/claude-sonnet-4-5", settings("low", "plan"), None), None, None);
    run.channel.start();
    let lines = fixture("opencode", "answer");
    run.receive(&lines[0]);
    run.receive(&lines[1]);
    // The model first; the session's effort option has no "low"; the mode differs from build.
    let writes = run.writes();
    assert!(
        run.methods() == ["initialize", "session/new", "session/set_config_option"]
            && writes[2]["params"]["configId"] == "model"
            && writes[2]["params"]["value"] == "anthropic/claude-sonnet-4-5"
            && run.warnings().is_empty(),
        "the model goes first"
    );
    run.receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-3\",\"result\":{}}");
    let writes = run.writes();
    assert!(
        run.warnings().len() == 1
            && run.warnings()[0].contains("effort")
            && writes[3]["params"]["configId"] == "mode"
            && writes[3]["params"]["value"] == "plan",
        "an effort the session lacks is named; plan follows"
    );
    // A setting the agent refuses is named; the request still goes.
    run.receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-4\",\"error\":{\"code\":-32602,\"message\":\"Invalid value\"}}");
    assert!(run.methods().last().map(String::as_str) == Some("session/prompt") && run.warnings().len() == 2, "a refused setting is named");
}

#[test]
fn a_missing_opencode_session_to_resume_starts_a_new_one_with_a_notice() {
    let resume = "ses_0000000000000000000000dead";
    let run = play("opencode", "resume-missing", Some(request("opencode", "go on", "default", RunSettings::default(), Some(resume))), None);
    assert!(
        run.methods() == ["initialize", "session/resume", "session/new", "session/prompt"]
            && run.writes()[1]["params"]["sessionId"] == resume,
        "resume, then new"
    );
    assert!(
        run.warnings() == [strings::get("acp.channel.newSession")] && run.resumes() == [OPENCODE_SESSION],
        "the notice and the new session"
    );
    assert!(
        run.texts("assistant") == ["Starting fresh."] && run.channel.turn_completed() && !run.parser.failed(),
        "the answer: {:?}",
        run.logs()
    );
}

#[test]
fn opencodes_question_tool_is_the_question_card() {
    let shown: Mutex<Option<ToolPermissionRequest>> = Mutex::new(None);
    let run = play(
        "opencode",
        "question",
        None,
        Some(&answering(|run, card| {
            *shown.lock().unwrap() = Some(card.clone());
            assert_eq!(run.activities().last().unwrap().state, "waiting", "the call's row waits");
            run.channel.answer_questions(&card.id, &answer(card, &["Green"], None)).unwrap();
        })),
    );
    let card = shown.lock().unwrap().clone().expect("a card");
    let question = first_question(&card);
    assert!(
        card.tool_name == "AskUserQuestion" && card.can_answer_questions && !card.can_allow && card.tool_use_id == "call_question_1",
        "the card: {card:?}"
    );
    // The property's title is the header, its description the question, each `oneOf` entry an option.
    assert!(
        question.header == "Favorite color"
            && question.question == "What's your favorite color?"
            && !question.multi_select
            && labels(&question) == ["Red", "Green", "Blue"]
            && question.options[0].description == "You prefer red.",
        "the question: {question:?}"
    );
    let result = run.result(0).unwrap();
    assert!(
        result["action"] == "accept" && compact(&result["content"]) == "{\"q0\":\"Green\"}",
        "the pick is the form's content: {result}"
    );
    assert!(
        run.states() == ["pending", "answered"]
            && run.warnings().is_empty()
            && run.failures().is_empty()
            && run.channel.turn_completed()
            && !run.parser.failed()
            && run.completed() == 1,
        "answered once"
    );
    assert_eq!(run.last_row().state, "completed", "the call's row settles with opencode's own update");
    // A typed answer travels in the custom property; several picks as a list, in the offered order, with the typed one.
    let run = play(
        "opencode",
        "question",
        None,
        Some(&answering(|run, card| run.channel.answer_questions(&card.id, &answer(card, &[], Some(" Purple "))).unwrap())),
    );
    assert_eq!(run.result(0).unwrap()["content"].to_string(), "{\"q0_custom\":\"Purple\"}", "a typed answer");
    let run = play(
        "opencode",
        "question-multiple",
        None,
        Some(&answering(|run, card| {
            let question = first_question(card);
            assert!(question.multi_select && labels(&question) == ["Apple", "Banana", "Cherry"], "a multiple choice");
            run.channel.answer_questions(&card.id, &answer(card, &["Cherry", "Apple"], Some("Mango"))).unwrap();
        })),
    );
    let content = run.result(0).unwrap()["content"].clone();
    assert!(
        compact(&content["q0"]) == "[\"Apple\",\"Cherry\"]" && content["q0_custom"] == "Mango" && content.as_object().unwrap().len() == 2,
        "picks and a typed answer: {content}"
    );
    // The card's Cancel declines the form; opencode ends the turn `cancelled`, which is not a failed run.
    let run = play(
        "opencode",
        "question-dismissed",
        None,
        Some(&answering(|run, card| {
            assert!(run.channel.respond(&card.id, true).is_err(), "a question cannot be allowed");
            run.channel.respond(&card.id, false).unwrap();
        })),
    );
    let result = run.result(0).unwrap();
    assert!(result["action"] == "decline" && result.as_object().unwrap().len() == 1 && run.states() == ["pending", "denied"], "declined");
    assert!(
        run.last_row().state == "error" && run.channel.turn_completed() && !run.parser.failed() && run.failures().is_empty(),
        "the dismissed call fails, the run does not"
    );
}

#[test]
fn a_form_the_card_cannot_ask_is_declined_and_a_withdrawn_question_leaves_no_card() {
    let asked = |properties: &str| {
        let run = channel(&simple("opencode", "hi"), None, None);
        run.channel.start();
        let lines = fixture("opencode", "answer");
        run.receive(&lines[0]);
        run.receive(&lines[1]);
        run.receive(&format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"elicitation/create\",\"params\":{{\"mode\":\"form\",\"sessionId\":\"{OPENCODE_SESSION}\",\"toolCallId\":\"call-9\",\"message\":\"Questions\",\"requestedSchema\":{{\"type\":\"object\",\"properties\":{properties},\"required\":[]}}}}}}"
        ));
        run
    };
    let run = asked("{\"q0\":{\"type\":\"integer\",\"title\":\"Retries\",\"description\":\"How many retries?\"}}");
    assert!(
        run.permissions().is_empty()
            && run.result(0).is_some_and(|r| r["action"] == "decline")
            && run.warnings() == [strings::get_with("acp.channel.formDeclined", &[("question", "Questions")])],
        "a number is declined and named"
    );
    let options =
        |count: usize| (1..=count).map(|i| format!("{{\"const\":\"o{i}\",\"title\":\"Option {i}\"}}")).collect::<Vec<_>>().join(",");
    let run =
        asked(&format!("{{\"q0\":{{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[{}]}}}}", options(13)));
    assert!(run.permissions().is_empty() && run.result(0).is_some_and(|r| r["action"] == "decline"), "more options than the card holds");
    let run =
        asked(&format!("{{\"q0\":{{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[{}]}}}}", options(12)));
    assert!(run.permissions().len() == 1 && first_question(&run.permissions()[0]).options.len() == 12, "twelve options fit the card");
    let run = asked(
        "{\"q0\":{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[{\"const\":\"a\",\"title\":\"First\"},{\"const\":\"b\",\"title\":\"Second\"}]},\"q1\":{\"type\":\"boolean\",\"title\":\"Sure\",\"description\":\"Are you sure?\"}}",
    );
    let card = run.permissions()[0].clone();
    let questions = questionnaire(&card).questions;
    let (yes, no) = (strings::get("questionnaire.option.yes"), strings::get("questionnaire.option.no"));
    assert!(
        questions.iter().map(|q| q.question.as_str()).eq(["Which one?", "Are you sure?"])
            && labels(&questions[1]) == [yes.clone(), no.clone()],
        "a choice and a yes or no"
    );
    // A choice without the custom property takes only its options; the card stays answerable.
    let typed = IndexMap::from([
        ("Which one?".to_string(), UserQuestionAnswer::new(&[], Some("Third"))),
        ("Are you sure?".to_string(), UserQuestionAnswer::new(&[&yes], None)),
    ]);
    assert!(run.channel.answer_questions(&card.id, &typed).is_err() && run.result(0).is_none(), "a typed answer is refused");
    let picked = IndexMap::from([
        ("Which one?".to_string(), UserQuestionAnswer::new(&["Second"], None)),
        ("Are you sure?".to_string(), UserQuestionAnswer::new(&[&no], None)),
    ]);
    run.channel.answer_questions(&card.id, &picked).unwrap();
    let content = run.result(0).unwrap()["content"].clone();
    assert!(content["q0"] == "b" && content["q1"] == false, "the label's value travels; a boolean as a boolean: {content}");
    let pick = "{\"q0\":{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[{\"const\":\"a\",\"title\":\"First\"},{\"const\":\"b\",\"title\":\"Second\"}]}}";
    let run = asked(pick);
    run.receive("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancel_request\",\"params\":{\"requestId\":0}}");
    assert!(run.states() == ["pending", "cancelled"] && run.result(0).is_none(), "a withdrawn request takes its card away");
    let run = asked(pick);
    run.receive(&format!(
        "{{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{{\"sessionId\":\"{OPENCODE_SESSION}\",\"update\":{{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call-9\",\"status\":\"failed\"}}}}}}"
    ));
    assert!(run.states() == ["pending", "cancelled"] && run.result(0).is_none(), "a settled call takes its card away");
    let run = asked(pick);
    run.channel.cancel_all();
    assert!(run.states() == ["pending", "cancelled"] && run.result(0).is_none(), "a stopped run withdraws the card");
}

#[test]
fn opencode_tools_are_rows_and_an_ask_is_the_approval_card_answered_once() {
    let run = play("opencode", "tools", None, Some(&allowing(true)));
    // A sub-agent's own reply (a child session's update) is not part of the answer.
    assert_eq!(run.texts("assistant"), ["Let me run the tests.", "One test fails because main returns 1."], "replies");
    let read = run.tools("Read").pop().unwrap();
    assert!(
        read.state == "completed"
            && read.kind == "read"
            && read.summary == "src/main.ts"
            && read.output.as_deref().is_some_and(|o| o.contains("export function main()")),
        "the read row: {read:?}"
    );
    let bash = run.tools("Bash");
    let last = bash.last().unwrap();
    assert!(
        bash.iter().any(|a| a.state == "waiting")
            && last.state == "completed"
            && last.kind == "command"
            && last.output.as_deref() == Some("1 failing"),
        "the command row: {last:?}"
    );
    let edit = run.tools("Edit").pop().unwrap();
    assert!(edit.state == "error" && edit.output.as_deref().is_some_and(|o| o.contains("rejected")), "the rejected edit");
    let card = run.permissions()[0].clone();
    assert!(
        card.tool_name == "Bash" && card.tool_use_id == "call_02ShellB" && card.summary == "npm test" && card.can_allow,
        "the card: {card:?}"
    );
    assert!(
        card.input_json.contains("\"command\": \"npm test\"") && card.input_json.contains("\"cwd\": \"/tmp/project\""),
        "the card's input: {}",
        card.input_json
    );
    assert!(
        ToolPermissionPresentation::make(&card.tool_name, &card.input_json).fields.iter().any(|f| f.code && f.value == "npm test"),
        "the card shows the command"
    );
    assert_eq!(run.states(), ["pending", "allowed"], "allowed once");
    let reply = run.answers()[0].clone();
    assert!(
        reply["id"].is_number()
            && reply["id"] == 0
            && reply["result"]["outcome"]["outcome"] == "selected"
            && option(&reply).as_deref() == Some("once"),
        "answered with opencode's own option, id 0 as a number: {reply}"
    );
    assert!(run.channel.respond(&card.id, true).is_err() && !run.parser.failed() && run.channel.turn_completed(), "answered once");
    let usage = run.usage().last().cloned().unwrap();
    assert!(
        usage.input_tokens == Some(5148)
            && usage.cache_read_tokens == Some(2048)
            && usage.total_tokens == Some(5243)
            && usage.cost_usd == Some(0.0411),
        "cached input is inside input: {usage:?}"
    );
    let denied = play("opencode", "tools", None, Some(&allowing(false)));
    assert_eq!(option(&denied.answers()[0]).as_deref(), Some("reject"), "a denial is opencode's reject");
    let ignored = play("opencode", "tools", None, None);
    assert_eq!(ignored.states(), ["pending", "cancelled"], "an unanswered ask goes when its call settles");
}

#[test]
fn a_signed_out_opencode_run_fails_as_a_lost_sign_in() {
    let run = play("opencode", "auth", None, None);
    assert!(run.parser.failed() && run.parser.authentication_failure() && run.channel.failed() && run.completed() == 1, "a lost sign-in");
    assert!(run.texts("error").iter().any(|t| t.contains("provider authentication required")), "its words: {:?}", run.logs());
    // An agent request this client does not offer is refused; EOF before the answer is a failure.
    let refused = channel(&simple("opencode", "hi"), None, None);
    refused.channel.start();
    refused.receive("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"/etc/hosts\"}}");
    assert_eq!(refused.writes().last().unwrap()["error"]["code"], -32601, "fs is not offered");
    refused.channel.flush();
    assert!(refused.channel.failed() && refused.failures() == [strings::get("acp.channel.closedEarly")], "closed early");
}

/// The folder the app ships its agent files from (the Mac bundle's Resources/Agents, which the Windows build copies beside itself).
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

#[test]
fn opencodes_permission_modes_are_its_rules_for_the_run() {
    common::korean();
    assert_eq!(
        mighty_run::providers::permission_modes("opencode", true),
        ["manual", "plan", "acceptEdits", "fullAccess"],
        "opencode's modes"
    );
    let effect = |mode: &str, action: &str| {
        agents::opencode_permission_rules(mode).into_iter().rev().find(|r| r.0 == action || r.0 == "*").map(|r| r.1)
    };
    assert!(
        effect("manual", "edit") == Some("ask")
            && effect("manual", "shell") == Some("ask")
            && effect("manual", "external_directory") == Some("ask"),
        "manual asks"
    );
    assert!(effect("acceptEdits", "edit") == Some("allow") && effect("acceptEdits", "shell") == Some("ask"), "accept edits");
    assert!(effect("plan", "edit").is_none() && effect("plan", "webfetch") == Some("ask"), "plan leaves the plan agent's own edit rule");
    assert_eq!(agents::opencode_permission_rules("fullAccess"), [("*", "allow")], "full access allows all");
    let path = r"C:\Program Files\Mighty Code\Agents\opencode-questions.md";
    let content = common::json(&agents::opencode_config_content("manual", Some(path)));
    assert!(
        compact(&content["permissions"][0]) == "{\"action\":\"edit\",\"effect\":\"ask\",\"resource\":\"*\"}"
            && content["instructions"][0] == path,
        "the run's config: {content}"
    );
    assert!(common::json(&agents::opencode_config_content("plan", None)).get("instructions").is_none(), "no instructions file, none");
    let values = agents::opencode_config_values(&request("opencode", "hi there", "anthropic/x", settings("high", "plan"), None));
    assert!(
        values == [("model".to_string(), "anthropic/x".to_string()), ("effort".into(), "high".into()), ("mode".into(), "plan".into())],
        "config values: {values:?}"
    );
    assert!(
        agents::opencode_supported(Some("opencode v2.2.7"))
            && !agents::opencode_supported(Some("1.19.5"))
            && !agents::opencode_supported(Some("")),
        "OpenCode 2 only"
    );
    mighty_run::providers::set_agent_files(Some(agent_files()));
    let file = mighty_run::providers::agent_file(agents::OPENCODE_QUESTION_INSTRUCTIONS).expect("the question instructions");
    assert!(std::fs::read_to_string(file).unwrap().contains("`question` tool"), "the question instructions ship with the app");
}

// ── Hermes ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

#[test]
fn a_new_hermes_session_is_initialize_then_session_new_then_one_prompt() {
    let run = play("hermes", "answer", None, None);
    assert_eq!(run.methods(), ["initialize", "session/new", "session/prompt"], "methods");
    let writes = run.writes();
    assert!(
        writes[2]["params"]["sessionId"] == HERMES_SESSION
            && mighty_core::wire::session_identifier(HERMES_SESSION)
            && writes[2]["params"]["prompt"][0]["text"] == "hi there",
        "the prompt"
    );
    assert!(
        run.channel.turn_completed()
            && !run.channel.failed()
            && run.completed() == 1
            && run.failures().is_empty()
            && run.warnings().is_empty(),
        "the turn"
    );
    assert!(run.channel.cancellation.take().is_none(), "nothing is left to cancel");
    let logs = run.logs();
    assert!(
        logs.iter().map(|l| l.0.as_str()).eq(["assistant"]) && logs[0].1 == "Hi." && run.resumes() == [HERMES_SESSION],
        "logged once: {logs:?}"
    );
    let usage = run.usage();
    let last = usage.last().unwrap();
    assert!(
        last.provider == "hermes"
            && last.source == "hermes.acp"
            && last.provider_session_id.as_deref() == Some(HERMES_SESSION)
            && usage[0].model.as_deref() == Some(HERMES_MODEL)
            && last.model.as_deref() == Some(HERMES_NAMED_MODEL),
        "the model settles: {last:?}"
    );
    assert!(
        last.cost_usd.is_none() && last.context_used_tokens == Some(11194) && last.context_window_tokens == Some(256000),
        "no cost, the context"
    );
    assert!(
        last.token_scope == "session"
            && last.input_tokens == Some(11166)
            && last.output_tokens == Some(28)
            && last.total_tokens == Some(11194),
        "the session's running counts"
    );
    let cancelled = play("hermes", "cancel", None, None);
    assert!(
        cancelled.channel.turn_completed() && !cancelled.parser.failed() && cancelled.usage().iter().all(|u| u.total_tokens.is_none()),
        "a cancelled turn's zeros are not counts"
    );
}

#[test]
fn hermess_model_effort_and_edit_policy_go_as_config_options() {
    let named = request("hermes", "do it", HERMES_NAMED_MODEL, settings("high", "acceptEdits"), None);
    assert!(
        agents::hermes_config_values(&named).iter().map(|v| v.0.as_str()).eq(["model", "reasoning_effort", "edit_approval_policy"]),
        "the three settings"
    );
    let run = channel(&named, None, None);
    run.channel.start();
    let lines = fixture("hermes", "answer");
    run.receive(&lines[0]);
    run.receive(&lines[1]);
    let sent = |index: usize| {
        let writes = run.writes();
        (text(&writes[index]["params"]["configId"]).to_string(), text(&writes[index]["params"]["value"]).to_string())
    };
    assert_eq!(sent(2), ("model".to_string(), HERMES_NAMED_MODEL.to_string()), "model");
    run.receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-3\",\"result\":{}}");
    assert_eq!(sent(3), ("reasoning_effort".to_string(), "high".to_string()), "effort");
    run.receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-4\",\"result\":{}}");
    assert_eq!(sent(4), ("edit_approval_policy".to_string(), "workspace_session".to_string()), "edit policy");
    run.receive(
        "{\"jsonrpc\":\"2.0\",\"id\":\"mighty-5\",\"error\":{\"code\":-32603,\"message\":\"Mode changes apply when the session is idle; try again after this turn.\"}}",
    );
    assert!(
        run.methods().last().map(String::as_str) == Some("session/prompt")
            && run.warnings().len() == 1
            && run.warnings()[0].contains("idle"),
        "a busy session's refusal is named"
    );
    let same = channel(&request("hermes", "do it", HERMES_MODEL, settings("max", "manual"), None), None, None);
    same.channel.start();
    same.receive(&lines[0]);
    same.receive(&lines[1]);
    assert!(same.methods() == ["initialize", "session/new", "session/prompt"] && same.warnings().is_empty(), "the session's own values");
    let unknown = channel(&request("hermes", "do it", "openrouter:gone/model", RunSettings::default(), None), None, None);
    unknown.channel.start();
    unknown.receive(&lines[0]);
    unknown.receive(&lines[1]);
    assert!(
        unknown.methods() == ["initialize", "session/new", "session/prompt"]
            && unknown.warnings().len() == 1
            && unknown.warnings()[0].contains("openrouter:gone/model"),
        "a model the picker lacks is named"
    );
}

#[test]
fn a_hermes_resume_ignores_the_replayed_conversation() {
    let run = play(
        "hermes",
        "resume",
        Some(request("hermes", "go on", "default", settings("default", "acceptEdits"), Some(HERMES_RESUMED))),
        None,
    );
    assert!(
        run.methods() == ["initialize", "session/resume", "session/set_config_option", "session/prompt"]
            && run.writes()[2]["params"]["value"] == "workspace_session",
        "methods: {:?}",
        run.methods()
    );
    assert!(
        run.texts("assistant") == ["Bye."] && run.activities().iter().all(|a| a.kind == "turn"),
        "the replay is not this run's: {:?}",
        run.logs()
    );
    assert!(
        run.resumes() == [HERMES_RESUMED]
            && run.channel.turn_completed()
            && !run.parser.failed()
            && run.usage().last().unwrap().total_tokens == Some(46130),
        "the counters carry on"
    );
    let gone = format!("local-{}", "0".repeat(64));
    let missing = play("hermes", "resume-missing", Some(request("hermes", "go on", "default", RunSettings::default(), Some(&gone))), None);
    assert!(
        missing.methods() == ["initialize", "session/resume", "session/new", "session/prompt"]
            && missing.warnings() == [strings::get("acp.channel.newSession")],
        "a missing session starts a new one"
    );
    assert!(
        missing.resumes().len() == 1 && missing.resumes()[0] != gone && missing.texts("assistant") == ["Hello."],
        "the new session answers"
    );
}

#[test]
fn without_a_provider_hermes_stops_at_initialize_as_a_missing_sign_in() {
    let admit = |refusal: String| -> acp::AdmitHook {
        Arc::new(move |result: &Value| agents::hermes_provider(result).is_none().then(|| refusal.clone()))
    };
    let run = channel(&simple("hermes", "hi"), Some(admit(strings::get("run.error.hermesNoProvider"))), None);
    run.channel.start();
    run.receive(&fixture("hermes", "unconfigured")[0]);
    assert!(
        run.methods() == ["initialize"] && run.channel.failed() && run.completed() == 1 && run.resumes().is_empty(),
        "no session is opened"
    );
    assert!(
        run.parser.failed()
            && run.parser.authentication_failure()
            && run.logs().iter().map(|l| l.1.clone()).eq([strings::get("run.error.hermesNoProvider")]),
        "a missing sign-in: {:?}",
        run.logs()
    );
    let configured = channel(&simple("hermes", "hi"), Some(admit("no".into())), None);
    configured.channel.start();
    configured.receive(&fixture("hermes", "answer")[0]);
    assert_eq!(configured.methods(), ["initialize", "session/new"], "a configured Hermes passes");
    // A turn that fails for a missing provider: the reply says why, the error is the gateway's word.
    let stale = play("hermes", "auth", Some(request("hermes", "hi", "default", RunSettings::default(), Some(HERMES_STALE))), None);
    assert!(
        stale.parser.failed() && stale.parser.authentication_failure() && stale.channel.failed() && stale.completed() == 1,
        "a lost sign-in"
    );
    assert!(
        stale.texts("assistant")[0].starts_with("⚠️ provider 'auto' has no endpoint configured")
            && stale.texts("error").last() == Some(&strings::get("run.error.hermesTurnFailed")),
        "its words: {:?}",
        stale.logs()
    );
    let other = channel(&simple("hermes", "hi"), None, None);
    other.channel.start();
    other.receive(&fixture("hermes", "answer")[0]);
    other.receive(
        "{\"jsonrpc\":\"2.0\",\"id\":\"mighty-2\",\"error\":{\"code\":-32603,\"message\":\"Internal error\",\"data\":{\"details\":\"Gateway could not start: port in use\"}}}",
    );
    assert!(
        other.parser.failed()
            && !other.parser.authentication_failure()
            && other.logs().last().unwrap().1 == "Gateway could not start: port in use",
        "data.details is the error's words"
    );
}

#[test]
fn hermes_tools_are_rows_and_an_edit_and_a_dangerous_command_are_cards() {
    let run = play("hermes", "tools", None, Some(&allowing(false)));
    let replies = run.texts("assistant");
    assert!(
        replies.len() == 2
            && replies[0] == "[tool call]"
            && replies[1].starts_with("Done what could be done;")
            && replies[1].matches("Done what could be done;").count() == 1,
        "the reply sent again is logged once: {replies:?}"
    );
    let read = run.tools("Read").pop().unwrap();
    assert!(
        read.state == "completed" && read.summary == "/etc/shells" && read.output.as_deref().is_some_and(|o| o.contains("/bin/zsh")),
        "the read row: {read:?}"
    );
    let edit: Vec<AgentActivity> = run.tools("Edit").into_iter().filter(|a| a.summary == "note.txt").collect();
    assert!(
        edit[0].state == "running"
            && edit.last().unwrap().state == "error"
            && edit.last().unwrap().output.as_deref().is_some_and(|o| o.contains("Edit approval denied")),
        "the denied edit: {edit:?}"
    );
    let bash = run.tools("Bash").into_iter().rfind(|a| a.summary == "rm -rf ./nothing-here").unwrap();
    assert!(
        bash.state == "error" && bash.kind == "command" && bash.output.as_deref().is_some_and(|o| o.contains("Command denied by user")),
        "the denied command: {bash:?}"
    );
    assert_eq!(run.tools("tool_search").last().unwrap().state, "completed", "a tool named by its title");
    let cards: Vec<ToolPermissionRequest> = run.permissions().into_iter().filter(|p| p.state == "pending").collect();
    assert!(
        cards.iter().map(|c| c.tool_use_id.as_str()).eq(["edit-approval-1", "perm-check-1"]) && cards.iter().all(|c| c.can_allow),
        "two cards"
    );
    assert!(
        cards[0].tool_name == "Edit"
            && cards[0].summary == "Approve edit: /private/tmp/project/note.txt"
            && cards[0].input_json.contains("\"path\": \"note.txt\"")
            && cards[0].input_json.contains("\"content\": \"hi\"")
            && !cards[0].input_json.contains("\"tool\""),
        "the edit's arguments unwrapped: {}",
        cards[0].input_json
    );
    assert!(
        cards[1].tool_name == "Bash"
            && cards[1].summary == "recursive delete: rm -rf ./nothing-here"
            && ToolPermissionPresentation::make(&cards[1].tool_name, &cards[1].input_json)
                .fields
                .iter()
                .any(|f| f.code && f.value == "rm -rf ./nothing-here"),
        "the command card"
    );
    assert_eq!(run.states(), ["pending", "denied", "pending", "denied"], "both denied");
    let answers = run.answers();
    assert!(
        answers.iter().map(|a| a["id"].as_i64()).eq([Some(0), Some(1)])
            && answers.iter().map(option).eq([Some("deny".to_string()), Some("deny".to_string())]),
        "Hermes's own option ids, number ids as numbers"
    );
    assert!(
        !run.parser.failed() && run.channel.turn_completed() && run.usage().last().unwrap().total_tokens == Some(24646),
        "the turn completes"
    );
    let allowed = play("hermes", "tools", None, Some(&allowing(true)));
    assert!(
        allowed.answers().iter().map(option).eq([Some("allow_once".to_string()), Some("allow_once".to_string())]),
        "allowed once, never for the session"
    );
}

/// The frames Hermes 0.23.1's own builders (acp_adapter/elicitation.py) make for clarify (M/Tests/HermesTests.swift builtClarify).
fn built_clarify(index: usize) -> String {
    let frames = [
        format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"elicitation/create\",\"params\":{{\"sessionId\":\"{HERMES_SESSION}\",\"mode\":\"form\",\"message\":\"Commit the change now?\",\"requestedSchema\":{{\"type\":\"object\",\"properties\":{{\"answer\":{{\"type\":\"string\",\"enum\":[\"Yes\",\"Not yet\"]}}}},\"required\":[\"answer\"]}}}}}}"
        ),
        format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"elicitation/create\",\"params\":{{\"sessionId\":\"{HERMES_SESSION}\",\"mode\":\"form\",\"message\":\"Which files should be staged?\",\"requestedSchema\":{{\"type\":\"object\",\"properties\":{{\"answer\":{{\"type\":\"array\",\"items\":{{\"type\":\"string\",\"enum\":[\"README.md\",\"notes.txt\",\"src/main.py\"]}}}}}},\"required\":[\"answer\"]}}}}}}"
        ),
        format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"elicitation/create\",\"params\":{{\"sessionId\":\"{HERMES_SESSION}\",\"mode\":\"form\",\"message\":\"What should the commit message say?\",\"requestedSchema\":{{\"type\":\"object\",\"properties\":{{\"answer\":{{\"type\":\"string\"}}}},\"required\":[\"answer\"]}}}}}}"
        ),
        format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/request_permission\",\"params\":{{\"sessionId\":\"{HERMES_SESSION}\",\"toolCall\":{{\"content\":[{{\"content\":{{\"text\":\"Commit the change now?\",\"type\":\"text\"}},\"type\":\"content\"}}],\"kind\":\"other\",\"rawInput\":{{\"question\":\"Commit the change now?\",\"choices\":[\"Yes\",\"Not yet\"]}},\"status\":\"pending\",\"title\":\"Commit the change now?\",\"toolCallId\":\"clarify-7c1d\",\"sessionUpdate\":\"tool_call_update\"}},\"options\":[{{\"kind\":\"allow_once\",\"name\":\"Yes\",\"optionId\":\"choice-0\"}},{{\"kind\":\"allow_once\",\"name\":\"Not yet\",\"optionId\":\"choice-1\"}},{{\"kind\":\"reject_once\",\"name\":\"Skip\",\"optionId\":\"skip\"}}]}}}}"
        ),
    ];
    frames[index].clone()
}

fn clarify(index: usize) -> (Replay, Option<ToolPermissionRequest>) {
    let run = channel(&simple("hermes", "hi"), None, None);
    run.channel.start();
    let lines = fixture("hermes", "answer");
    run.receive(&lines[0]);
    run.receive(&lines[1]);
    run.receive(&built_clarify(index));
    let card = run.permissions().first().cloned();
    (run, card)
}

#[test]
fn hermess_clarify_is_the_question_card() {
    // Recorded: one clarify call of three questions, each its own elicitation/create (ids 0, 1, 2).
    let asked: Mutex<Vec<UserQuestion>> = Mutex::new(Vec::new());
    let run = play(
        "hermes",
        "clarify",
        None,
        Some(&answering(|run, card| {
            let question = first_question(card);
            asked.lock().unwrap().push(question.clone());
            assert!(
                card.tool_use_id == "call_clarify_1" && run.activities().last().unwrap().state == "waiting",
                "the form is the open clarify call's question"
            );
            run.channel.answer_questions(&card.id, &answer(card, &[&question.options[0].label], None)).unwrap();
        })),
    );
    let asked = asked.lock().unwrap().clone();
    let header = strings::get("questionnaire.header.agent");
    assert!(asked.len() == 3 && asked.iter().all(|q| !q.multi_select && q.header == header), "three cards, one at a time");
    assert_eq!(
        labels(&asked[0]),
        ["작은 웹 앱/CLI 툴 (Recommended)", "라이브러리/패키지", "대충 뭐라도 — 아이디어를 던져보고 같이 다듬기", "아직 모르겠음"],
        "Hermes's own choice texts"
    );
    let replies: Vec<Value> = run.writes().into_iter().filter(|w| w["result"].get("action").is_some()).collect();
    assert!(
        replies.iter().map(|r| r["id"].as_i64()).eq([Some(0), Some(1), Some(2)])
            && replies[0]["result"]["content"]["answer"] == "작은 웹 앱/CLI 툴 (Recommended)"
            && replies[0]["result"]["content"].as_object().unwrap().len() == 1,
        "zero is an id like any other: {}",
        replies[0]
    );
    assert!(
        run.states() == ["pending", "answered", "pending", "answered", "pending", "answered"]
            && run.warnings().is_empty()
            && run.channel.turn_completed()
            && !run.parser.failed(),
        "the turn goes on"
    );
    // Declined: Hermes reads a skip and goes on; unanswered: Hermes stops waiting and the card goes with a line.
    let declined = play("hermes", "clarify-declined", None, Some(&answering(|run, card| run.channel.respond(&card.id, false).unwrap())));
    let result = declined.result(0).unwrap();
    assert!(
        result["action"] == "decline"
            && result.as_object().unwrap().len() == 1
            && declined.states() == ["pending", "denied"]
            && declined.channel.turn_completed()
            && !declined.texts("assistant").is_empty(),
        "declined"
    );
    let waited = play("hermes", "clarify-declined", None, None);
    let summary = waited.permissions()[0].summary.clone();
    assert!(
        waited.states() == ["pending", "cancelled"]
            && waited.result(0).is_none()
            && waited.warnings() == [strings::get_with("acp.channel.questionWithdrawn", &[("question", &summary)])],
        "withdrawn with a line"
    );
    // The built frames: one choice, a typed answer in the same property, several picks, no choices, Cancel.
    let (one, card) = clarify(0);
    let card = card.expect("a clarify form");
    let question = first_question(&card);
    assert!(
        one.activities().iter().all(|a| a.kind == "turn")
            && question.question == "Commit the change now?"
            && labels(&question) == ["Yes", "Not yet"],
        "a clarify form"
    );
    one.channel.answer_questions(&card.id, &answer(&card, &["Not yet"], None)).unwrap();
    assert_eq!(one.result(0).unwrap()["content"].to_string(), "{\"answer\":\"Not yet\"}", "the pick");
    let (one, card) = clarify(0);
    let card = card.unwrap();
    one.channel.answer_questions(&card.id, &answer(&card, &[], Some("After the tests pass"))).unwrap();
    assert_eq!(
        one.result(0).unwrap()["content"].to_string(),
        "{\"answer\":\"After the tests pass\"}",
        "a typed answer in the same property"
    );
    let (one, card) = clarify(1);
    let card = card.unwrap();
    one.channel.answer_questions(&card.id, &answer(&card, &["notes.txt", "README.md"], Some("docs/"))).unwrap();
    assert_eq!(
        one.result(1).unwrap()["content"].to_string(),
        "{\"answer\":[\"README.md\",\"notes.txt\",\"docs/\"]}",
        "several picks, the typed one last"
    );
    let (one, card) = clarify(2);
    let card = card.unwrap();
    assert!(
        first_question(&card).options.is_empty() && one.channel.answer_questions(&card.id, &answer(&card, &[], None)).is_err(),
        "no choices: an answer must be typed"
    );
    one.channel.answer_questions(&card.id, &answer(&card, &[], Some("Fix the parser"))).unwrap();
    assert_eq!(one.result(2).unwrap()["content"].to_string(), "{\"answer\":\"Fix the parser\"}", "the typed answer");
    // A clarify approval (a Hermes not told about forms): the pick is its option; nothing typed can travel; Cancel is Skip.
    let (one, card) = clarify(3);
    let card = card.unwrap();
    assert!(
        card.can_answer_questions
            && !card.can_allow
            && card.tool_use_id == "clarify-7c1d"
            && one.channel.answer_questions(&card.id, &answer(&card, &[], Some("Later"))).is_err()
            && one.answers().is_empty(),
        "typed answers are refused"
    );
    one.channel.answer_questions(&card.id, &answer(&card, &["Not yet"], None)).unwrap();
    assert!(one.answers().iter().map(option).eq([Some("choice-1".to_string())]) && one.answers()[0]["id"] == 3, "the picked option");
    let (one, card) = clarify(3);
    one.channel.respond(&card.unwrap().id, false).unwrap();
    assert!(one.answers().iter().map(option).eq([Some("skip".to_string())]), "Cancel is Skip");
}

#[test]
fn the_note_about_the_card_follows_the_request() {
    let run = channel(&simple("hermes", "선택하는 문제를 내"), None, Some(agents::HERMES_PROMPT_NOTE));
    run.channel.start();
    let lines = fixture("hermes", "answer");
    run.receive(&lines[0]);
    run.receive(&lines[1]);
    let prompt = run.writes().into_iter().rev().find(|w| w["method"] == "session/prompt").unwrap();
    let sent = text(&prompt["params"]["prompt"][0]["text"]).to_string();
    assert_eq!(sent, format!("선택하는 문제를 내\n\n{}", agents::HERMES_PROMPT_NOTE), "after the request");
    assert!(
        agents::hermes_without_note(&sent) == "선택하는 문제를 내"
            && agents::hermes_without_note("plain") == "plain"
            && agents::HERMES_PROMPT_NOTE.contains("clarify"),
        "taken off for the list"
    );
}

#[test]
fn hermess_log_and_tracebacks_on_stderr_stay_out_of_the_pane() {
    let mut filter = HermesStderrFilter::default();
    let recorded = [
        "2026-11-11 01:18:54 [INFO] acp_adapter.entry: Starting hermes-agent ACP adapter",
        "2026-11-11 01:18:54 [ERROR] root: Background task failed",
        "Traceback (most recent call last):",
        "  File \"/x/venv/lib/python3.14/site-packages/acp/connection.py\", line 208, in _run_request",
        "    raise err from None",
        "acp.exceptions.RequestError: Internal error",
        "",
    ];
    assert!(recorded.iter().all(|line| !filter.keep(line)), "log lines and tracebacks stay out");
    let mut fresh = HermesStderrFilter::default();
    assert!(
        fresh.keep("The ACP server can't start: its protocol packages are missing from this install.")
            && fresh.keep("From the Hermes environment, run: hermes pm install --extra acp"),
        "a start-up failure's words pass"
    );
    assert_eq!(
        agents::hermes_version_line("Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\nInstall: C:\\x\nPython 3.14\n"),
        "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882",
        "the first line is the version"
    );
    let mut environment = std::collections::BTreeMap::from([("HERMES_YOLO_MODE".to_string(), "1".to_string())]);
    agents::hermes_apply("manual", &mut environment);
    assert!(!environment.contains_key("HERMES_YOLO_MODE"), "an exported yolo does not override the pane");
    agents::hermes_apply("fullAccess", &mut environment);
    assert!(
        environment["HERMES_YOLO_MODE"] == "1"
            && agents::hermes_edit_policy("fullAccess") == "session"
            && agents::hermes_edit_policy("acceptEdits") == "workspace_session"
            && agents::hermes_edit_policy("manual") == "ask",
        "full access is yolo and the session policy"
    );
    assert_eq!(mighty_run::providers::permission_modes("hermes", true), ["manual", "acceptEdits", "fullAccess"], "Hermes has no plan mode");
}

#[test]
fn the_hermes_menu_is_the_picker_the_last_session_offered() {
    common::korean();
    let folder = common::temp("hermes-models");
    let path = folder.join(agents::HERMES_MODEL_CACHE_FILE);
    let empty = agents::hermes_cached_catalog(Some(&path));
    assert!(
        empty.models.len() == 1 && empty.models[0].value == "default" && empty.detail == strings::get("provider.catalog.hermesEmpty"),
        "before a session only Hermes's own default"
    );
    let session = common::json(&fixture("hermes", "answer")[1])["result"]["configOptions"].clone();
    agents::hermes_save_models(Some(&session), Some(&path));
    let catalog = agents::hermes_cached_catalog(Some(&path));
    assert!(
        catalog.source == "cli"
            && catalog.models[0].value == "default"
            && catalog.models.iter().any(|m| m.value == HERMES_MODEL)
            && catalog.detail == strings::get("provider.catalog.hermesSource"),
        "the picker: {:?}",
        catalog.models.iter().map(|m| m.value.clone()).collect::<Vec<_>>()
    );
    agents::hermes_forget_models(Some(&path));
    assert!(!path.exists() && agents::hermes_cached_catalog(Some(&path)).models.len() == 1, "forgotten without a provider");
    agents::hermes_save_models(Some(&serde_json::json!([])), Some(&path));
    assert!(!path.exists(), "an empty picker changes nothing");
    // Not C#'s own line: a kept file its deserializer would refuse is no picker at all.
    std::fs::write(&path, r#"[{"value":"custom:a","displayName":"A","description":""},{"value":5}]"#).unwrap();
    assert_eq!(agents::hermes_cached_catalog(Some(&path)).models.len(), 1, "a damaged file keeps nothing");
    std::fs::write(&path, r#"[null,{"Value":"custom:a","DisplayName":"A","Description":""}]"#).unwrap();
    assert!(
        agents::hermes_cached_catalog(Some(&path)).models.iter().map(|m| m.value.as_str()).eq(["default", "custom:a"]),
        "names in any case, a null row left out"
    );
    let _ = std::fs::remove_dir_all(&folder);
}

#[test]
fn a_hermes_session_is_continued_only_where_its_yolo_and_answer_allow() {
    let id = HERMES_RESUMED;
    let resume = |mode: &str, count: Option<i32>, full: Option<bool>| agents::hermes_resume(id, mode, count, full);
    assert!(
        resume("manual", Some(0), Some(false)) == (None, None) && resume("fullAccess", Some(0), Some(true)) == (None, None),
        "a session that never answered is not continued"
    );
    assert_eq!(
        resume("manual", Some(3), Some(true)),
        (None, Some("run.notice.hermesFullAccessSession")),
        "a yolo session, a pane that asks"
    );
    assert_eq!(
        resume("acceptEdits", Some(3), Some(true)),
        (None, Some("run.notice.hermesFullAccessSession")),
        "nor one that accepts edits"
    );
    assert_eq!(
        resume("fullAccess", Some(3), Some(false)),
        (Some(id.to_string()), Some("run.notice.hermesAskingSession")),
        "an asking session is continued and still asks"
    );
    assert!(
        resume("fullAccess", Some(3), Some(true)) == (Some(id.to_string()), None)
            && resume("manual", Some(3), Some(false)) == (Some(id.to_string()), None),
        "matching sessions are continued"
    );
    assert_eq!(resume("manual", None, None), (Some(id.to_string()), None), "an unreadable record changes nothing");
}

#[test]
fn attached_copies_are_named_in_the_prompt_and_pictures_are_image_blocks() {
    common::korean();
    let writes: Arc<Mutex<Vec<Value>>> = Arc::default();
    let sink = writes.clone();
    let reference = "Attached file \"notes.txt\": \"C:\\Users\\me\\AppData\\Local\\Temp\\m\\0-notes.txt\" (read this copy as needed).";
    let channel = AcpChannel::new(
        AcpRequest {
            run_id: "run-opencode".into(),
            resume_id: None,
            workspace_path: "/tmp/project".into(),
            prompt_text: acp::prompt_text(" ", &[reference.to_string()], None),
            images: vec![("image/png".into(), "iVBORw0KGgo=".into())],
            config_values: vec![],
            free_choices: false,
        },
        AcpHooks::writing(Arc::new(move |frame| sink.lock().unwrap().push(common::json(frame.trim_end_matches('\n'))))),
    );
    channel.start();
    let lines = fixture("opencode", "answer");
    channel.receive(&lines[0]);
    channel.receive(&lines[1]);
    let prompt = writes.lock().unwrap().iter().rev().find(|w| w["method"] == "session/prompt").cloned().unwrap();
    let blocks = prompt["params"]["prompt"].as_array().unwrap().clone();
    // No words of the user's own: the attachments' default words, then each copy by its plain path.
    assert_eq!(text(&blocks[0]["text"]), format!("{}\n\n{reference}", strings::get("attachments.prompt.default")), "the text block");
    assert!(
        blocks.len() == 2 && blocks[1]["type"] == "image" && blocks[1]["data"] == "iVBORw0KGgo=" && blocks[1]["mimeType"] == "image/png",
        "a picture is an image block"
    );
}

/// CliLoginRecoveryVerification.PiSignInFailuresAndTheTerminalTable, its opencode and Hermes words (CliLoginRecoveryVerification.cs:281-284;
/// the rest of it, pi's stream and the terminal table, is pi's and the sign-in card's).
#[test]
fn pi_sign_in_failures_and_the_terminal_table_opencode_and_hermes_words() {
    use mighty_run::failure;
    assert!(
        failure::opencode("provider authentication required")
            && failure::opencode("Free usage limit reached. Connect a provider to keep going")
            && failure::opencode("401 Unauthorized")
            && !failure::opencode("407 Proxy Authentication Required")
            && !failure::opencode("rate limited"),
        "opencode's sign-in words"
    );
    assert!(
        failure::hermes("⚠️ provider 'auto' has no endpoint configured, so the request is not sent anywhere")
            && failure::hermes("Hermes is not connected to any AI provider yet. Run `hermes model`.")
            && !failure::hermes("407 Proxy Authentication Required")
            && !failure::hermes("The file is written."),
        "Hermes's sign-in words"
    );
}

#[test]
fn the_clarify_config_value_is_the_macs() {
    assert!(
        agents::hermes_question_value(&[]) == ["hermes-acp", "clarify", "x_search"]
            && agents::hermes_question_value(&["kanban".to_string()]) == ["hermes-acp", "clarify", "x_search", "kanban"],
        "the saved list"
    );
    assert!(
        agents::hermes_question_names("[\"hermes-acp\",\"clarify\"]") == Some(vec!["hermes-acp".to_string(), "clarify".to_string()])
            && agents::hermes_question_names("\"['hermes-acp', 'clarify']\"")
                == Some(vec!["hermes-acp".to_string(), "clarify".to_string()])
            && agents::hermes_question_names("{}").is_none(),
        "lists as Hermes prints them"
    );
}
