//! C# ToolPermissionVerification (MightyCode.Core.Tests/ToolPermissionVerification.cs) and QuestionnaireVerification's channel
//! checks: the stdio permission channel driven by its callbacks instead of a CLI. Every check is a fail-closed rule: nothing is
//! allowed without a user action on that request. The end-to-end check over a real child is in runner.rs.

mod common;

use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_run::flow_steps::TASK_TOOLS;
use mighty_run::model::StartRunRequest;
use mighty_run::permissions::{
    ChannelHooks, ClaudePermissionChannel, MAXIMUM_INPUT_BYTES, MAXIMUM_PENDING, ProviderInput, ToolPermissionRequest,
};
use mighty_run::presentation::{MAXIMUM_FIELDS, ToolPermissionPresentation, strings as copy};
use mighty_run::questionnaire::{UserQuestionAnswer, UserQuestionnaire};
use mighty_run::stream;
use serde_json::Value;

const PROMPT: &str = "{\"type\":\"user\",\"message\":{\"content\":\"prompt\"}}\n";

fn ask(id: &str, tool: &str, input: &str, interaction: bool) -> String {
    format!(
        "{{\"type\":\"control_request\",\"request_id\":\"{id}\",\"request\":{{\"subtype\":\"can_use_tool\",\"tool_name\":\"{tool}\",\"tool_use_id\":\"tool-{id}\",\"input\":{input},\"decision_reason\":\"Read outside the workspace\",\"blocked_path\":\"~/.claude/CLAUDE.md\",\"requires_user_interaction\":{interaction}}}}}"
    )
}

fn ask_read(id: &str) -> String {
    ask(id, "Read", "{\"file_path\":\"~/.claude/CLAUDE.md\"}", false)
}

/// The `response.response` payload of a control_response written.
fn result(line: &str) -> Value {
    common::json(line)["response"]["response"].clone()
}

fn subtype(line: &str) -> String {
    common::json(line)["response"]["subtype"].as_str().unwrap().to_string()
}

#[derive(Default)]
struct Lists {
    writes: Vec<String>,
    states: Vec<String>,
    warnings: Vec<String>,
    failures: Vec<String>,
    displays: Vec<ToolPermissionRequest>,
}

struct Channel {
    lists: Arc<Mutex<Lists>>,
    value: ClaudePermissionChannel,
}

impl Channel {
    fn new(prompt: &str) -> Channel {
        Channel::with_write(prompt, None)
    }

    fn with_write(prompt: &str, fail_writes: Option<&str>) -> Channel {
        common::korean();
        let lists = Arc::new(Mutex::new(Lists::default()));
        let (w, e, a, warn, f) = (lists.clone(), lists.clone(), lists.clone(), lists.clone(), lists.clone());
        let fail = fail_writes.map(str::to_string);
        let hooks = ChannelHooks {
            write: Arc::new(move |text| match &fail {
                Some(message) => Err(std::io::Error::other(message.clone())),
                None => {
                    w.lock().unwrap().writes.push(text.to_string());
                    Ok(())
                }
            }),
            emit: Arc::new(move |value| e.lock().unwrap().displays.push(value)),
            activity: Arc::new(move |_, state| a.lock().unwrap().states.push(state.to_string())),
            warning: Arc::new(move |message| warn.lock().unwrap().warnings.push(message.to_string())),
            fail: Arc::new(move |message| f.lock().unwrap().failures.push(message.to_string())),
            plan: Arc::new(|_| {}),
            clock: Arc::new(mighty_core::time::DateTimeOffset::now),
        };
        Channel { lists, value: ClaudePermissionChannel::new("run-1", prompt, hooks, "plan") }
    }

    fn writes(&self) -> Vec<String> {
        self.lists.lock().unwrap().writes.clone()
    }
    fn last(&self) -> String {
        self.writes().last().cloned().unwrap()
    }
    fn displays(&self) -> Vec<ToolPermissionRequest> {
        self.lists.lock().unwrap().displays.clone()
    }
    fn states(&self) -> Vec<String> {
        self.lists.lock().unwrap().states.clone()
    }
    fn warnings(&self) -> Vec<String> {
        self.lists.lock().unwrap().warnings.clone()
    }
    fn failures(&self) -> Vec<String> {
        self.lists.lock().unwrap().failures.clone()
    }
    fn initialize(&self) {
        let id = self.value.initialization_id.clone();
        self.value
            .receive(&format!(
                "{{\"type\":\"control_response\",\"response\":{{\"subtype\":\"success\",\"request_id\":\"{id}\",\"response\":{{}}}}}}"
            ))
            .unwrap();
    }
}

#[test]
fn handshake_runs_before_the_prompt() {
    let channel = Channel::new(PROMPT);
    channel.value.start().unwrap();
    assert!(channel.writes().len() == 1 && channel.last() != PROMPT, "start must send initialize, not the prompt");
    let initialize = common::json(&channel.last());
    assert_eq!(initialize["type"], "control_request", "the handshake is a control_request");
    assert_eq!(initialize["request"]["subtype"], "initialize", "the handshake subtype is initialize");
    assert_eq!(initialize["request_id"].as_str(), Some(channel.value.initialization_id.as_str()), "the handshake carries the channel id");
    assert!(!channel.value.initialized(), "the channel is not initialized before a success");
    channel.initialize();
    assert!(channel.value.initialized() && channel.last() == PROMPT, "the prompt follows a successful handshake");
    channel.initialize();
    assert_eq!(channel.writes().len(), 2, "a repeated handshake success sends nothing further");
}

#[test]
fn handshake_failure_and_timeout_fail_closed() {
    let rejected = Channel::new("never");
    rejected.value.start().unwrap();
    let id = rejected.value.initialization_id.clone();
    rejected
        .value
        .receive(&format!(
            "{{\"type\":\"control_response\",\"response\":{{\"subtype\":\"error\",\"request_id\":\"{id}\",\"error\":\"not supported\"}}}}"
        ))
        .unwrap();
    assert!(rejected.value.failed() && rejected.failures().len() == 1, "a rejected handshake fails the channel");
    assert!(!rejected.writes().contains(&"never".to_string()), "a rejected handshake never sends the prompt");
    assert_eq!(rejected.failures()[0], copy::initialize_failed(), "the failure uses the macOS copy");

    let timed = Channel::new("never");
    timed.value.receive(&ask_read("ask-1")).unwrap();
    assert_eq!(timed.displays().len(), 1, "a request before the handshake still shows");
    timed.value.initialization_timed_out();
    assert!(timed.value.failed() && timed.failures()[0] == copy::initialize_timed_out(), "a timed-out handshake fails closed");
    assert_eq!(timed.displays().last().unwrap().state, "cancelled", "a timed-out handshake settles waiting requests");
    assert!(timed.value.respond("ask-1", true).is_err(), "a failed channel must not allow anything");
    timed.initialize();
    assert!(!timed.writes().contains(&"never".to_string()), "a closed channel never sends the prompt");
}

#[test]
fn allow_once_returns_the_original_input() {
    let input = r#"{"query":"official documentation","allowed_domains":["example.com"],"nested":{"unchanged":true}}"#;
    let channel = Channel::new(PROMPT);
    let request = ask("ask-1", "WebSearch", input, false);
    channel.value.receive(&request).unwrap();
    channel.value.receive(&request).unwrap();
    let displays = channel.displays();
    assert_eq!(displays.len(), 1, "a replayed request id is answered once");
    assert!(displays[0].run_id == "run-1" && displays[0].can_allow, "a plain tool request can be allowed");
    assert_eq!(displays[0].blocked_path.as_deref(), Some("~/.claude/CLAUDE.md"), "the blocked path is shown");
    assert_eq!(displays[0].reason.as_deref(), Some("Read outside the workspace"), "the reason is shown");
    assert_eq!(channel.value.waiting().len(), 1, "one request waits");

    channel.value.respond("ask-1", true).unwrap();
    let result = result(&channel.last());
    assert_eq!(result["behavior"], "allow", "이번만 허용 answers allow");
    assert_eq!(result["updatedInput"], common::json(input), "allow returns the original input unchanged");
    assert!(
        channel
            .last()
            .contains(r#""updatedInput":{"query":"official documentation","allowed_domains":["example.com"],"nested":{"unchanged":true}}"#)
    );
    assert_eq!(result["toolUseID"], "tool-ask-1", "allow carries the tool use id");
    assert!(result.get("updatedPermissions").is_none(), "allow never returns persistent rules");
    assert!(result.get("updatedSettings").is_none() && result.get("mode").is_none(), "allow never returns settings or a mode");
    assert_eq!(
        channel.displays().iter().map(|d| d.state.as_str()).collect::<Vec<_>>(),
        ["pending", "allowed"],
        "the request settles to allowed"
    );
    assert_eq!(channel.states(), ["waiting", "running"], "the activity row follows the answer");
    assert!(channel.value.respond("ask-1", true).is_err(), "a settled request cannot be allowed twice");
    channel.value.receive(&request).unwrap();
    assert!(channel.writes().len() == 1 && channel.value.waiting().is_empty(), "a settled request is not reopened");
}

#[test]
fn deny_never_returns_allow() {
    let channel = Channel::new(PROMPT);
    channel.value.receive(&ask_read("ask-1")).unwrap();
    channel.value.respond("ask-1", false).unwrap();
    let result = result(&channel.last());
    assert_eq!(result["behavior"], "deny", "거부 answers deny");
    assert_eq!(result["toolUseID"], "tool-ask-1", "deny carries the tool use id");
    assert!(result["message"].as_str().is_some_and(|m| !m.is_empty()), "deny carries a message");
    assert!(result.get("updatedInput").is_none() && result.get("updatedPermissions").is_none(), "deny returns no input and no rules");
    assert!(
        channel.displays().last().unwrap().state == "denied" && channel.states().last().unwrap() == "error",
        "the request settles to denied"
    );
    assert!(channel.value.respond("ask-1", false).is_err(), "a settled request cannot be denied twice");
    assert!(channel.writes().iter().all(|w| result_of(w)["behavior"] == "deny"), "no write allowed anything");
}

fn result_of(line: &str) -> Value {
    result(line)
}

#[test]
fn only_can_use_tool_is_accepted_and_identifiers_are_validated() {
    let channel = Channel::new(PROMPT);
    channel
        .value
        .receive(
            r#"{"type":"control_request","request_id":"other","request":{"subtype":"set_permission_mode","mode":"bypassPermissions"}}"#,
        )
        .unwrap();
    assert_eq!(subtype(&channel.last()), "error", "an unknown subtype is answered with an error");
    assert!(channel.displays().is_empty() && !channel.value.failed(), "an unknown subtype shows nothing and keeps the channel open");
    channel.value.receive(r#"{"type":"control_request","request_id":"dialog","request":{"subtype":"request_user_dialog"}}"#).unwrap();
    assert!(
        channel.writes().len() == 1 && *channel.warnings().last().unwrap() == copy::unsupported_dialog(),
        "a dialog request is left to the CLI with a warning"
    );
    channel.value.receive(r#"{"type":"control_request","request_id":"elicit","request":{"subtype":"elicitation"}}"#).unwrap();
    assert_eq!(result(&channel.last())["action"], "decline", "an elicitation is declined");
    channel.value.receive(&ask("bad", "", "{}", false)).unwrap();
    assert!(
        subtype(&channel.last()) == "error" && *channel.warnings().last().unwrap() == copy::denied_malformed_request(),
        "an empty tool name is refused"
    );
    channel
        .value
        .receive(r#"{"type":"control_request","request_id":"noinput","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool-noinput"}}"#)
        .unwrap();
    assert_eq!(subtype(&channel.last()), "error", "a request without an input object is refused");
    channel
        .value
        .receive(r#"{"type":"control_request","request_id":"badtool","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool id","input":{}}}"#)
        .unwrap();
    assert_eq!(subtype(&channel.last()), "error", "an invalid tool use id is refused");
    assert!(channel.displays().is_empty(), "nothing malformed ever reaches the bar");
    channel.value.receive("not json at all").unwrap();
    assert!(!channel.value.failed(), "a non-JSON line is ignored");

    let broken = Channel::new(PROMPT);
    broken
        .value
        .receive(r#"{"type":"control_request","request_id":"bad id","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool-1","input":{}}}"#)
        .unwrap();
    assert!(
        broken.value.failed() && broken.failures()[0] == copy::malformed_control_request(),
        "an invalid request id fails the channel closed"
    );
}

#[test]
fn pending_requests_are_capped_at_sixteen() {
    let channel = Channel::new(PROMPT);
    for index in 0..17 {
        channel.value.receive(&ask_read(&format!("queued-{index}"))).unwrap();
    }
    assert_eq!(channel.displays().len(), MAXIMUM_PENDING, "the bar never holds more than 16 requests");
    assert_eq!(channel.value.waiting().len(), 16, "16 requests wait");
    assert!(channel.writes().len() == 1 && result(&channel.last())["behavior"] == "deny", "the seventeenth request is denied");
    assert_eq!(*channel.warnings().last().unwrap(), copy::denied_too_many_pending(), "the cap warning uses the macOS copy");
    channel.value.respond("queued-0", false).unwrap();
    channel.value.receive(&ask_read("queued-17")).unwrap();
    assert!(
        channel.value.waiting().len() == 16 && channel.displays().last().unwrap().id == "queued-17",
        "a freed slot accepts the next request"
    );
}

#[test]
fn input_too_large_to_show_is_denied() {
    let channel = Channel::new(PROMPT);
    let big = serde_json::json!({"command": "x".repeat(MAXIMUM_INPUT_BYTES)}).to_string();
    channel.value.receive(&ask("large", "Bash", &big, false)).unwrap();
    assert!(channel.displays().is_empty(), "an input that cannot be shown completely never reaches the bar");
    assert_eq!(result(&channel.last())["behavior"], "deny", "an oversized input is denied");
    assert_eq!(*channel.warnings().last().unwrap(), copy::denied_oversized_input(), "the size warning uses the macOS copy");

    let wordy = Channel::new(PROMPT);
    wordy
        .value
        .receive(&format!(
            "{{\"type\":\"control_request\",\"request_id\":\"wordy\",\"request\":{{\"subtype\":\"can_use_tool\",\"tool_name\":\"Read\",\"tool_use_id\":\"tool-wordy\",\"input\":{{\"file_path\":\"/w/a\"}},\"decision_reason\":\"{}\"}}}}",
            "r".repeat(9000)
        ))
        .unwrap();
    let shown = wordy.displays().last().cloned().unwrap();
    assert!(!shown.can_allow, "a reason that cannot be shown completely cannot be allowed");
    assert!(shown.reason.unwrap().ends_with(&copy::metadata_too_large()), "the bar says why it cannot be allowed");
    assert!(wordy.value.respond("wordy", true).is_err(), "an undisplayable reason must not be allowed");
    wordy.value.respond("wordy", false).unwrap();
    assert_eq!(result(&wordy.last())["behavior"], "deny", "it can still be denied");
}

#[test]
fn questionnaires_and_extra_screens_can_only_be_denied() {
    let channel = Channel::new(PROMPT);
    channel.value.receive(&ask("question", "AskUserQuestion", r#"{"questions":[{"question":"Choose a region"}]}"#, false)).unwrap();
    let shown = channel.displays().last().cloned().unwrap();
    assert!(!shown.can_allow && !shown.can_answer_questions, "a questionnaire cannot be allowed or answered here");
    assert!(
        shown.reason.unwrap().contains(&copy::needs_separate_input_screen()),
        "the bar shows the macOS sentence for a request it cannot allow"
    );
    assert!(channel.value.respond("question", true).is_err(), "a questionnaire must not be allowed");
    channel.value.respond("question", false).unwrap();
    assert_eq!(result(&channel.last())["behavior"], "deny", "a questionnaire can be denied");

    channel.value.receive(&ask("special", "Read", "{\"file_path\":\"~/.claude/CLAUDE.md\"}", true)).unwrap();
    assert!(!channel.displays().last().unwrap().can_allow, "a request that needs its own screen cannot be allowed");
    assert!(channel.value.respond("special", true).is_err(), "an interaction request must not be allowed");
    assert!(channel.writes().iter().all(|w| result(w)["behavior"] == "deny"), "nothing was ever allowed");
}

#[test]
fn stopping_the_run_settles_every_waiting_request() {
    let channel = Channel::new(PROMPT);
    channel.value.receive(&ask_read("first")).unwrap();
    channel.value.receive(&ask_read("second")).unwrap();
    channel.value.receive(r#"{"type":"control_cancel_request","request_id":"second"}"#).unwrap();
    let last = channel.displays().last().cloned().unwrap();
    assert!(last.id == "second" && last.state == "cancelled", "a cancelled request settles");
    assert!(channel.value.respond("second", true).is_err(), "a cancelled request cannot be allowed");
    channel.value.receive(&ask_read("third")).unwrap();
    assert_eq!(channel.value.waiting().len(), 2, "two requests wait when the run stops");
    channel.value.cancel_all();
    assert!(channel.value.waiting().is_empty(), "stopping the run leaves nothing waiting");
    let cancelled: Vec<String> = channel.displays().iter().filter(|d| d.state == "cancelled").map(|d| d.id.clone()).collect();
    assert_eq!(cancelled, ["second", "first", "third"], "every waiting request is cancelled");
    assert_eq!(channel.states().last().unwrap(), "stopped", "the activity rows stop");
    assert!(channel.value.respond("first", true).is_err(), "a stopped run allows nothing");
    channel.value.receive(&ask_read("late")).unwrap();
    assert!(channel.writes().is_empty(), "a stopped channel writes nothing further");
    channel.value.cancel_all();
}

#[test]
fn bar_shows_the_title_summary_reason_path_and_count() {
    let channel = Channel::new(PROMPT);
    channel.value.receive(&ask("one", "Bash", r#"{"command":"rm -rf build","description":"Clear the build folder"}"#, false)).unwrap();
    channel.value.receive(&ask("two", "Write", r##"{"file_path":"/w/b.md","content":"# Title"}"##, false)).unwrap();
    let waiting = channel.value.waiting();
    assert!(waiting.len() == 2 && waiting[0].id == "one", "the bar shows the oldest waiting request");
    let first = &waiting[0];
    let presentation = ToolPermissionPresentation::make(&first.tool_name, &first.input_json);
    assert_eq!(presentation.title, copy::title_bash(), "the bar shows what the tool does");
    assert_eq!(presentation.headline.as_deref(), Some("Clear the build folder"), "the bar shows what it wants to do");
    assert_eq!(
        presentation.fields.iter().map(|f| f.label.clone()).collect::<Vec<_>>(),
        [copy::field_command()],
        "the command is a named field"
    );
    assert!(presentation.fields[0].value == "rm -rf build" && presentation.fields[0].code, "the command is shown verbatim");
    assert_eq!(
        copy::bar_title_template().replace("{title}", &presentation.title),
        "명령 실행 · 승인 요청",
        "the bar title is the macOS title"
    );
    assert_eq!(
        copy::bar_waiting_count_template().replace("{count}", &waiting.len().to_string()),
        "2개 대기",
        "the bar shows the waiting count"
    );
    assert_eq!(
        copy::bar_path_template().replace("{path}", first.blocked_path.as_deref().unwrap()),
        "접근 경로: ~/.claude/CLAUDE.md",
        "the bar shows the path"
    );
    assert_eq!(first.summary, "rm -rf build", "the summary is the tool's own target");
    assert_eq!(
        ToolPermissionPresentation::make(&waiting[1].tool_name, &waiting[1].input_json).title,
        copy::title_write(),
        "each tool has its own title"
    );

    let mcp = ToolPermissionPresentation::make("mcp__mcp-gcoo__execute_sql", r#"{"sql":"select 1"}"#);
    assert_eq!(mcp.title, "MCP 도구 · mcp-gcoo", "an MCP tool names its server");
    let unknown = ToolPermissionPresentation::make("Custom", "not json");
    assert!(
        unknown.title == copy::title_tool() && unknown.headline.is_none() && unknown.fields.is_empty(),
        "an unreadable input falls back to the plain title"
    );
    let many = format!("{{{}}}", (0..20).map(|i| format!("\"k{i}\":\"v\"")).collect::<Vec<_>>().join(","));
    assert_eq!(ToolPermissionPresentation::make("Custom", &many).fields.len(), MAXIMUM_FIELDS, "the field list is bounded");

    // Invisible controls are made explicit; the approved input is untouched.
    let hidden = Channel::new(PROMPT);
    hidden.value.receive(&ask("hidden", "WebSearch", "{\"query\":\"\u{202E}visible\"}", false)).unwrap();
    assert!(hidden.displays().last().unwrap().input_json.contains("\\u202e"), "an invisible control is shown as an escape");
    hidden.value.respond("hidden", true).unwrap();
    assert_eq!(result(&hidden.last())["updatedInput"]["query"], "\u{202E}visible", "allow still sends the original input");
}

fn claude(id: &str) -> StartRunRequest {
    StartRunRequest::new(id, "workspace-fixture", "claude", "fixture prompt")
}

/// launch_arguments: the bar's own path gets host prompts over stdio; every other path keeps --permission-prompts none.
#[test]
fn host_prompts_only_where_the_bar_exists() {
    common::korean();
    let quiet = ProviderInput::prepare(&claude("quiet"), "/fixture-plugin", false).unwrap().arguments;
    assert_eq!(common::after(&quiet, "--permission-prompts"), "none", "a host that cannot show the bar keeps prompts off");
    assert!(
        !quiet.contains(&"--permission-prompt-tool".to_string()) && !quiet.contains(&"--input-format".to_string()),
        "no stdio prompt tool is offered without the bar"
    );

    let hosted = ProviderInput::prepare(&claude("hosted"), "/fixture-plugin", true).unwrap().arguments;
    assert_eq!(common::after(&hosted, "--permission-prompts"), "host", "the bar's path asks this host for prompts");
    assert_eq!(common::after(&hosted, "--permission-prompt-tool"), "stdio", "the prompts arrive over stdio");
    assert_eq!(common::after(&hosted, "--input-format"), "stream-json", "stdin carries stream-json so replies are possible");
    assert!(
        hosted.iter().filter(|a| *a == "--allowedTools").count() == 1 && common::after(&hosted, "--allowedTools") == TASK_TOOLS.join(","),
        "the bar's path still names the task tools, so the progress panel gets its steps"
    );

    let codex = ProviderInput::prepare(&claude("codex-run").with_provider("codex"), "/fixture-plugin", true).unwrap().arguments;
    assert!(!codex.contains(&"--permission-prompt-tool".to_string()), "only Claude speaks this protocol");

    let frame = ProviderInput::prompt_frame("안녕");
    assert!(frame.ends_with('\n'), "the prompt frame is newline framed");
    assert_eq!(common::json(&frame)["message"]["content"], "안녕", "the prompt frame carries the input");
    assert_eq!(common::json(&frame)["message"]["role"], "user", "the prompt frame names its role, which Claude Code 2.1.289 requires");
    assert_eq!(
        frame, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"\\uC548\\uB155\"}}\n",
        "written as Wire.Json writes it"
    );

    assert!(stream::is_turn_result("{\"type\":\"result\",\"subtype\":\"success\"}"), "the CLI's own result ends the turn");
    assert!(
        !stream::is_turn_result("{\"type\":\"result\",\"origin\":{\"kind\":\"task-notification\"}}"),
        "a task notification does not end the turn"
    );
    assert!(!stream::is_turn_result("{\"type\":\"result\",\"parent_tool_use_id\":\"tool-1\"}"), "a sub-agent result does not end the turn");
    assert!(!stream::is_turn_result("not json"), "an unreadable line does not end the turn");
}

// ── QuestionnaireVerification's channel checks ──────────────────────────────────────────────────────────────────────────

const QUESTIONS: &str = r#"{"questions":[{"header":"방식","question":"어떻게 처리할까요?","multiSelect":false,"options":[{"label":"첫 선택","description":"설명"},{"label":"다음 선택","description":""}]},{"header":"범위","question":"범위는?","multiSelect":true,"options":[{"label":"A","description":"첫 범위"},{"label":"B","description":"다음 범위"}]}],"response":"model-supplied response must not win","extra":"preserved"}"#;

fn answers(pairs: &[(&str, UserQuestionAnswer)]) -> IndexMap<String, UserQuestionAnswer> {
    pairs.iter().map(|(k, v)| (k.to_string(), v.clone())).collect()
}

fn question_request() -> String {
    format!(
        "{{\"type\":\"control_request\",\"request_id\":\"q1\",\"request\":{{\"subtype\":\"can_use_tool\",\"tool_name\":\"AskUserQuestion\",\"tool_use_id\":\"tool-q1\",\"input\":{QUESTIONS}}}}}"
    )
}

#[test]
fn answer_rpc_settles_once_and_replaces_model_supplied_response() {
    let channel = Channel::new("prompt");
    channel.value.receive(&question_request()).unwrap();
    let shown = channel.displays().last().cloned().unwrap();
    assert!(shown.can_answer_questions && !shown.can_allow, "question has answer UI, never generic allow");
    assert!(channel.value.answer_questions("q1", &IndexMap::new()).is_err());
    assert!(channel.value.waiting().len() == 1 && channel.writes().is_empty(), "invalid draft stays pending and emits no reply");
    let picked = answers(&[
        ("어떻게 처리할까요?", UserQuestionAnswer::new(&["다음 선택"], None)),
        ("범위는?", UserQuestionAnswer::new(&["B"], None)),
    ]);
    channel.value.answer_questions("q1", &picked).unwrap();
    assert_eq!(channel.writes().len(), 1);
    let result = result(&channel.last());
    let input = &result["updatedInput"];
    assert!(result["behavior"] == "allow" && result["toolUseID"] == "tool-q1", "answer is scoped to the specific tool call");
    assert!(
        input["answers"]["어떻게 처리할까요?"] == "다음 선택" && input.get("response").is_none() && input["extra"] == "preserved",
        "only user answers replace response, original extra fields preserved"
    );
    assert!(channel.value.waiting().is_empty() && channel.displays().last().unwrap().state == "answered", "request settles immediately");
    assert!(channel.value.answer_questions("q1", &picked).is_err());
    assert_eq!(channel.writes().len(), 1, "double submit never emits a second answer");
    channel.value.cancel_all();
    assert!(channel.value.answer_questions("q1", &picked).is_err());
    assert!(UserQuestionnaire::parse(&shown.input_json).is_some(), "the shown input reads back as the questionnaire");
}

/// C# "Questionnaire reply delivery failure".
#[test]
fn broken_questionnaire_reply_fails_without_claiming_delivery() {
    let channel = Channel::with_write("prompt", Some("stdin closed"));
    channel.value.receive(&question_request()).unwrap();
    let picked = answers(&[
        ("어떻게 처리할까요?", UserQuestionAnswer::new(&["다음 선택"], None)),
        ("범위는?", UserQuestionAnswer::new(&["B"], None)),
    ]);
    assert!(channel.value.answer_questions("q1", &picked).is_err());
    assert!(
        !channel.failures().is_empty() && channel.value.failed() && channel.value.waiting().is_empty(),
        "a broken response channel fails closed"
    );
    let displays = channel.displays();
    assert!(
        displays.iter().all(|r| r.state != "answered") && displays.last().unwrap().state == "cancelled",
        "a failed write never claims the answer was delivered"
    );
}

/// C# StringsVerification.ToolPermissionsMatchMacOS: every value the card and the channel read is the shared catalogue's.
#[test]
fn tool_permissions_match_mac_os() {
    common::korean();
    let locale = mighty_core::locale::Locale::new("ko");
    for (value, key) in [
        (copy::bar_title_template(), "permission.bar.title"),
        (copy::initialize_timed_out(), "claude.channel.initTimeout"),
        (copy::initialize_failed(), "claude.channel.initFailed"),
        (copy::malformed_control_request(), "claude.channel.badRequest"),
        (copy::needs_separate_input_screen(), "claude.channel.needsInteraction"),
        (copy::metadata_too_large(), "claude.channel.metadataTooLarge"),
        (copy::already_settled(), "permission.error.requestGone"),
        (copy::title_bash(), "permission.tool.bash"),
        (copy::field_command(), "permission.field.command"),
        (copy::boolean_yes(), "permission.value.yes"),
    ] {
        assert_eq!(value, locale.get(key), "{key}");
        assert_ne!(value, key, "{key} is in the catalogue");
    }
}

/// What a switchable channel kept: its writes and the requests it settled.
type Kept = (Arc<Mutex<Vec<String>>>, Arc<Mutex<Vec<ToolPermissionRequest>>>);

/// A channel whose writes fail while `failing` is set (C#'s `failWrite`), its writes and settled requests kept.
fn switchable(failing: Arc<std::sync::atomic::AtomicBool>) -> (ClaudePermissionChannel, Kept) {
    common::korean();
    let writes = Arc::new(Mutex::new(vec![]));
    let settled = Arc::new(Mutex::new(vec![]));
    let (w, s) = (writes.clone(), settled.clone());
    let hooks = ChannelHooks {
        write: Arc::new(move |text| {
            if failing.load(std::sync::atomic::Ordering::SeqCst) {
                return Err(std::io::Error::other("closed stdin"));
            }
            w.lock().unwrap().push(text.to_string());
            Ok(())
        }),
        emit: Arc::new(move |value| s.lock().unwrap().push(value)),
        activity: Arc::new(|_, _| {}),
        warning: Arc::new(|_| {}),
        fail: Arc::new(|_| {}),
        plan: Arc::new(|_| {}),
        clock: Arc::new(mighty_core::time::DateTimeOffset::now),
    };
    (ClaudePermissionChannel::new("run", "initial", hooks, "manual"), (writes, settled))
}

/// "queue: a steer needs a live, initialized channel and a write that went through" (SteerRequiresLiveInitializedChannelAndSuccessfulWrite,
/// QueuedComposerVerification.cs:31-44).
#[test]
fn steer_requires_live_initialized_channel_and_successful_write() {
    let failing = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let (channel, (writes, _)) = switchable(failing.clone());
    assert!(
        !channel.try_steer("before initialization") && writes.lock().unwrap().is_empty(),
        "uninitialized streams cannot accept a follow-up"
    );
    let id = channel.initialization_id.clone();
    channel
        .receive(&format!("{{\"type\":\"control_response\",\"response\":{{\"subtype\":\"success\",\"request_id\":\"{id}\"}}}}"))
        .unwrap();
    assert_eq!(writes.lock().unwrap().clone(), vec!["initial".to_string()], "initial prompt is sent only after the handshake");
    assert!(channel.try_steer("다음 지시"), "a live Claude channel accepts Korean text");
    assert_eq!(
        writes.lock().unwrap().last().cloned(),
        Some(ProviderInput::prompt_frame("다음 지시")),
        "steering uses the same supported user-message frame as the initial prompt"
    );
    assert!(
        !channel.try_steer("") && !channel.try_steer("bad\0input") && !channel.try_steer(&"x".repeat(100_001)),
        "invalid instructions are refused before a write"
    );
    failing.store(true, std::sync::atomic::Ordering::SeqCst);
    assert!(!channel.try_steer("not delivered"), "failed writes fall back to the queue instead of losing input");
    failing.store(false, std::sync::atomic::Ordering::SeqCst);
    channel.cancel_all();
    assert!(!channel.try_steer("after stop"), "a stopped channel never restarts through steering");
}

/// "queue: a permission answered while the run stops is settled once" (PermissionResponsesAndCancellationSettleOnlyOnce,
/// QueuedComposerVerification.cs:46-53).
#[test]
fn permission_responses_and_cancellation_settle_only_once() {
    for _ in 0..50 {
        let (channel, (writes, settled)) = switchable(Arc::new(std::sync::atomic::AtomicBool::new(false)));
        let channel = Arc::new(channel);
        channel
            .receive(r#"{"type":"control_request","request_id":"request","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool","input":{"file_path":"README.md"}}}"#)
            .unwrap();
        let (answering, stopping) = (channel.clone(), channel.clone());
        let answer = std::thread::spawn(move || {
            let _ = answering.respond("request", true);
        });
        let stop = std::thread::spawn(move || stopping.cancel_all());
        answer.join().unwrap();
        stop.join().unwrap();
        let ended = settled.lock().unwrap().iter().filter(|value| value.state != "pending").count();
        assert!(
            channel.waiting().is_empty() && ended == 1 && writes.lock().unwrap().len() <= 1,
            "concurrent parser stop and UI consent settle a request once"
        );
    }
}
