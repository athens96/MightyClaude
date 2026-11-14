//! C# AntigravityVerification.cs, the run engine's parts (the Mac's AntigravityTests): agy's stream read for replies, tool rows, the
//! conversation to continue and usage; results, declined actions, failures and notices; the arguments, the stdin line and attachments;
//! the tool rows' kinds and names; a model agy's live list lacks. Every check reads English copy, as the C# ones do, in a process of its
//! own. The graph's parts (sub-agents, the watcher) are mighty-graph's tests/antigravity.rs; the history's are mighty-history's
//! tests/resumable_sessions.rs; the model list is W5-D's.

mod common;

use std::sync::{Arc, Mutex};

use mighty_core::model::{AgentActivity, RunSettings, SessionUsage};
use mighty_run::antigravity::records;
use mighty_run::attachments::{self, StagedAttachments};
use mighty_run::model::{ModelCatalog, StartRunRequest};
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::permissions::ProviderInput;
use mighty_run::{providers, strings};
use serde_json::json;

fn english() {
    strings::set_language("en");
}

/// `Recorded`: a real `agy -p … --output-format stream-json --dangerously-skip-permissions` run (agy 1.3.5), trimmed.
const RECORDED: [&str; 11] = [
    r#"{"event":"init","conversation_id":"11111111-2222-4333-8444-555555555555","init":{"model":"gemini-3.8-flash-low","cwd":"/workspace/demo","tools":["view_file","run_command","write_to_file"],"permission_mode":"always-proceed"}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":0,"state":"DONE","step_type":"user_input"}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":2.916978,"usage":{"input_tokens":11835,"output_tokens":45,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":11880}}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/workspace/demo/note.txt"}}}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"DONE","step_type":"tool","tool_name":"view_file","duration_seconds":0.04126,"tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/workspace/demo/note.txt"},"output":"2 lines, 6 bytes"}}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":3,"state":"DONE","step_type":"agent_response","duration_seconds":2.9536,"usage":{"input_tokens":12065,"output_tokens":62,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12127}}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":4,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":4,"state":"DONE","step_type":"tool","tool_name":"run_command","duration_seconds":0.078959,"tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"output":"hi\r\n"}}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":5,"state":"ACTIVE","step_type":"agent_response","text_delta":"DONE"}}"#,
    r#"{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":5,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":2.01985,"usage":{"input_tokens":4094,"output_tokens":1,"thinking_tokens":0,"cache_read_tokens":8120,"total_tokens":4095}}}"#,
    r#"{"event":"result","result":{"conversation_id":"11111111-2222-4333-8444-555555555555","status":"SUCCESS","response":"DONE\n","duration_seconds":8.079104,"num_turns":1,"usage":{"input_tokens":27994,"output_tokens":108,"thinking_tokens":0,"cache_read_tokens":8120,"total_tokens":28102}}}"#,
];

/// C#'s `Recorder`: what a parser of activity id `agy-run` said.
#[derive(Default, Clone)]
struct Recorder {
    logs: Arc<Mutex<Vec<(String, String)>>>,
    resumes: Arc<Mutex<Vec<String>>>,
    activities: Arc<Mutex<Vec<AgentActivity>>>,
    usage: Arc<Mutex<Vec<SessionUsage>>>,
}

impl Recorder {
    fn parser(&self, activity: bool, requested_resume: Option<&str>) -> OutputParser {
        let (logs, resumes, activities, usage) = (self.logs.clone(), self.resumes.clone(), self.activities.clone(), self.usage.clone());
        let hooks = ParserHooks {
            log: Some(Arc::new(move |kind: &str, text: &str| logs.lock().unwrap().push((kind.into(), text.into())))),
            resume: Some(Arc::new(move |id: &str| resumes.lock().unwrap().push(id.into()))),
            activity: activity.then(|| Arc::new(move |value| activities.lock().unwrap().push(value)) as mighty_run::parser::ActivityHook),
            usage: Some(Arc::new(move |value| usage.lock().unwrap().push(value))),
            requested_resume: requested_resume.map(str::to_string),
            ..ParserHooks::default()
        };
        let clock = Arc::new(Mutex::new(0.0));
        OutputParser::new("gemini", "agy-run", hooks).with_clock(Arc::new(move || {
            let mut now = clock.lock().unwrap();
            *now += 10.0;
            *now
        }))
    }
    fn logs(&self) -> Vec<(String, String)> {
        self.logs.lock().unwrap().clone()
    }
    fn kinds(&self) -> Vec<String> {
        self.logs().into_iter().map(|(kind, _)| kind).collect()
    }
    fn resumes(&self) -> Vec<String> {
        self.resumes.lock().unwrap().clone()
    }
    fn activities(&self) -> Vec<AgentActivity> {
        self.activities.lock().unwrap().clone()
    }
}

/// The kind an agy tool's row gets, read through the parser as a live run's row is (C#'s `ToolKind`).
fn tool_kind(tool: &str) -> String {
    let recorder = Recorder::default();
    recorder.parser(true, None).parse(
        &json!({"event": "step_update", "step_update": {"step_index": 1, "state": "ACTIVE", "step_type": "tool", "tool_info": {"name": tool}}})
            .to_string(),
    );
    recorder.activities().into_iter().find(|row| row.kind != "turn").unwrap().kind
}

/// AntigravityVerification.cs:58 `TheStreamIsReadForRepliesToolsResumeAndUsage`.
#[test]
fn the_stream_is_read_for_replies_tools_resume_and_usage() {
    english();
    let recorder = Recorder::default();
    let parser = recorder.parser(true, None);
    for line in RECORDED {
        parser.parse(line);
    }
    parser.flush();
    // The deltas make one answer (its closing newline trimmed); the result's full response is not logged again.
    assert_eq!(recorder.logs(), [("assistant".to_string(), "DONE".to_string())], "one answer from the deltas");
    assert!(recorder.resumes() == ["11111111-2222-4333-8444-555555555555"] && !parser.failed(), "the conversation is the resume id");
    let activities = recorder.activities();
    let tools: Vec<&AgentActivity> = activities.iter().filter(|a| a.kind != "turn").collect();
    let read = tools.iter().rev().find(|a| a.tool_name.as_deref() == Some("view_file")).unwrap();
    assert!(
        read.kind == "read"
            && read.state == "completed"
            && read.summary == "/workspace/demo/note.txt"
            && read.output.as_deref() == Some("2 lines, 6 bytes"),
        "the read row: {read:?}"
    );
    let command = tools.iter().rev().find(|a| a.tool_name.as_deref() == Some("run_command")).unwrap();
    assert!(
        command.kind == "command"
            && command.state == "completed"
            && command.summary == "echo hi"
            && command.output.as_deref().map(str::trim_end) == Some("hi"),
        "the command row: {command:?}"
    );
    assert!(tools.iter().any(|a| a.tool_name.as_deref() == Some("run_command") && a.state == "running"), "a tool runs before it ends");
    let turns: Vec<&str> = activities.iter().filter(|a| a.kind == "turn").map(|a| a.summary.as_str()).collect();
    assert_eq!(turns, ["Antigravity is writing a response", "Antigravity is finishing the response"], "turn rows name Antigravity");
    let usage = recorder.usage.lock().unwrap().last().cloned().unwrap();
    assert!(
        usage.provider == "gemini"
            && usage.source == "antigravity.stream-json"
            && usage.token_scope == "session"
            && usage.model.as_deref() == Some("gemini-3.8-flash-low")
            && usage.provider_session_id.as_deref() == Some("11111111-2222-4333-8444-555555555555"),
        "usage identity"
    );
    // agy counts cached input apart (input 27994 + cache 8120); input here includes it.
    assert!(
        usage.input_tokens == Some(36114)
            && usage.output_tokens == Some(108)
            && usage.cache_read_tokens == Some(8120)
            && usage.reasoning_tokens == Some(0)
            && usage.total_tokens == Some(36222),
        "usage counts: {usage:?}"
    );
    // Without activity rows the tools go to the log.
    let plain = Recorder::default();
    let logged = plain.parser(false, None);
    for line in RECORDED {
        logged.parse(line);
    }
    logged.flush();
    assert!(
        plain.kinds() == ["system", "output", "system", "output", "assistant"] && plain.logs()[3].1 == "hi\r\n",
        "tools in the log: {:?}",
        plain.logs()
    );
}

/// AntigravityVerification.cs:86 `ResultsDeniedActionsFailuresAndNotices`.
#[test]
fn results_denied_actions_failures_and_notices() {
    english();
    let only = Recorder::default();
    let parser = only.parser(true, None);
    parser.parse(r#"{"event":"result","result":{"status":"SUCCESS","response":"Only in the result"}}"#);
    parser.flush();
    assert_eq!(
        only.logs().into_iter().map(|(_, t)| t).collect::<Vec<_>>(),
        ["Only in the result"],
        "a result without deltas is the answer"
    );

    let denied = Recorder::default();
    let declined = denied.parser(true, None);
    declined.parse(r#"{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}}}"#);
    declined.parse(r#"{"event":"step_update","step_update":{"step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"error":{"type":"TOOL_ERROR","message":"permission check failed for unsandboxed \"echo hi\": user denied permission to run command"}}}}"#);
    declined.parse(r#"{"event":"result","result":{"status":"SUCCESS","response":"","usage":{"input_tokens":11834,"output_tokens":57,"cache_read_tokens":0},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}"#);
    declined.flush();
    // The turn went on and succeeded; the declined command shows as a failed tool and a notice.
    let activities = denied.activities();
    let command = activities.iter().rev().find(|a| a.tool_name.as_deref() == Some("run_command")).unwrap();
    assert!(
        !declined.failed() && command.state == "error" && command.output.as_deref().is_some_and(|o| o.contains("user denied permission")),
        "a declined command is a failed tool"
    );
    let logs = denied.logs();
    assert!(
        logs.len() == 1 && logs[0].0 == "system" && logs[0].1.contains("RunCommand") && logs[0].1.contains("permissions.allow"),
        "the declined action is named: {logs:?}"
    );

    let failed = Recorder::default();
    let error = failed.parser(true, None);
    error.parse(r#"{"event":"result","result":{"status":"ERROR","error":{"message":"Quota exceeded"}}}"#);
    assert!(
        error.failed() && failed.logs().last() == Some(&("error".into(), "Quota exceeded".into())),
        "an error result fails the run with its message"
    );
    let invalid = Recorder::default().parser(true, None);
    invalid.parse(r#"{"event":"result","result":{"status":"INVALID"}}"#);
    assert!(invalid.failed(), "an invalid result fails");
    // agy ending the turn by itself is a failure; a stop the user asked for is the runner's.
    let ended = Recorder::default();
    let cancelled = ended.parser(true, None);
    cancelled.parse(r#"{"event":"result","result":{"status":"INTERRUPTED"}}"#);
    let last = ended.logs().last().cloned().unwrap();
    assert!(cancelled.failed() && last.0 == "error" && last.1.contains("INTERRUPTED"), "agy ending the turn itself");
    let waiting = Recorder::default();
    let unknown = waiting.parser(true, None);
    unknown.parse(r#"{"event":"result","result":{"status":"WAITING"}}"#);
    let last = waiting.logs().last().cloned().unwrap();
    assert!(!unknown.failed() && last.0 == "system" && last.1.contains("WAITING"), "an unknown status is a notice");

    // A missing conversation is noted once, from stderr or from a new id at init.
    let notes = Recorder::default();
    let resumed = notes.parser(true, Some("old-conversation"));
    resumed.antigravity_stderr("warning: conversation \"old-conversation\" not found");
    resumed.parse(r#"{"event":"init","conversation_id":"new-conversation","init":{}}"#);
    assert!(
        notes.kinds() == ["system"]
            && notes.logs()[0].1 == "The previous conversation was not found, so Antigravity started a new one."
            && notes.resumes() == ["new-conversation"],
        "a missing conversation is noted once"
    );
    let same = Recorder::default();
    same.parser(true, Some("c1")).parse(r#"{"event":"init","conversation_id":"c1","init":{}}"#);
    assert!(same.logs().is_empty(), "the same conversation is not noted");
    // A read-only command's payload is left to its result; anything unknown is shown.
    let other = Recorder::default();
    let shown = other.parser(true, None);
    shown.parse(r#"{"event":"command_result","command":{"name":"help","data":{}}}"#);
    shown.parse(r#"{"event":"result","result":{"conversation_id":"","status":"SUCCESS","response":"/help\tShow available commands\n"}}"#);
    shown.parse(r#"{"event":"future_event","future_event":{"x":1}}"#);
    assert!(
        other.kinds() == ["assistant", "output"] && other.logs()[1].1.contains("future_event") && other.resumes().is_empty(),
        "unknown events are shown: {:?}",
        other.logs()
    );
    // A reply's start is kept as written; its closing whitespace is not.
    let reply = Recorder::default();
    let code = reply.parser(true, None);
    code.parse(r#"{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"    let x = 1\n\n"}}"#);
    assert_eq!(reply.logs().into_iter().map(|(_, t)| t).collect::<Vec<_>>(), ["    let x = 1"], "an indented reply keeps its start");
}

/// AntigravityVerification.cs:133 `ArgumentsStdinAndAttachments`.
#[test]
fn arguments_stdin_and_attachments() {
    english();
    let mut request = StartRunRequest::new("run", "workspace", "claude", "--dangerously-skip-permissions /clear").with_provider("gemini");
    request.model = "gemini-4-argon-medium".into();
    request.settings = Some(RunSettings { permission_mode: "manual".into(), ..RunSettings::default() });
    request.resume_id = Some("11111111-2222-4333-8444-555555555555".into());
    let plain = ProviderInput::prepare(&request, "/plugin", false).unwrap();
    assert_eq!(
        plain.arguments,
        [
            "--print=",
            "--input-format",
            "stream-json",
            "--output-format",
            "stream-json",
            "--print-timeout",
            "0",
            "--model",
            "gemini-4-argon-medium",
            "--conversation",
            "11111111-2222-4333-8444-555555555555"
        ],
        "manual runs headless in agy's own review mode"
    );
    let line = String::from_utf8(plain.standard_input.clone()).unwrap();
    assert!(line.ends_with('\n') && line.matches('\n').count() == 1, "the prompt is one stdin line");
    let value = common::json(line.trim_end());
    assert!(
        value["event"] == "user" && value["message"]["content"] == request.input.as_str(),
        "the prompt is a user event, never an argument"
    );
    assert!(!plain.arguments.contains(&request.input), "the prompt is never an argument");
    for (mode, expected) in [
        ("plan", vec!["--mode", "plan"]),
        ("acceptEdits", vec!["--mode", "accept-edits"]),
        ("fullAccess", vec!["--dangerously-skip-permissions"]),
    ] {
        let mut changed = request.clone();
        changed.resume_id = None;
        changed.model = "default".into();
        changed.settings = Some(RunSettings { permission_mode: mode.into(), ..RunSettings::default() });
        let args = providers::arguments(&changed.validate().unwrap(), "/plugin", None).unwrap();
        assert_eq!(&args[7..], expected.as_slice(), "{mode} maps to {expected:?}: {args:?}");
    }
    let file = attachments::make("notes.txt", b"PURPLE-42").unwrap();
    let staged = StagedAttachments::create(std::slice::from_ref(&file)).unwrap();
    let mut attached = request.clone();
    attached.input = "What does it say?".into();
    attached.attachments = Some(vec![file]);
    let arguments = staged.arguments_for(&attached, "/plugin").unwrap();
    assert_eq!(&arguments[arguments.len() - 2..], ["--add-dir".to_string(), staged.directory_text()], "agy reads the staged folder");
    let prompt = staged.input_for(&attached);
    assert!(
        prompt.starts_with("What does it say?\n\n") && prompt.contains(&format!(": \"{}\"", staged.files[0].1.display())),
        "the copy is named by its plain path: {prompt}"
    );
    assert_eq!(
        ProviderInput::antigravity_user_message("a\nb"),
        "{\"event\":\"user\",\"message\":{\"content\":\"a\\nb\"}}\n",
        "one escaped line"
    );
    staged.dispose().unwrap();
}

/// AntigravityVerification.cs:158 `TheModelListLabelsAndInstall`, its run-engine parts: agy's tool kinds, its PascalCase parameters,
/// its names. The model list itself is W5-D's; the installer and update rows are W6's.
#[test]
fn the_tool_kinds_parameters_and_names() {
    english();
    assert!(
        tool_kind("grep_search") == "search"
            && tool_kind("write_to_file") == "edit"
            && tool_kind("browser_click_element") == "web"
            && tool_kind("list_dir") == "read"
            && tool_kind("invoke_subagent") == "agent"
            && tool_kind("search_web") == "web"
            && tool_kind("run_command") == "command",
        "agy's tool kinds"
    );
    let parameters = records::antigravity_parameters(Some(&json!({"TargetFile": "/a", "SearchPath": "/b"}))).unwrap();
    let mut names: Vec<&String> = parameters.as_object().unwrap().keys().collect();
    names.sort();
    assert_eq!(names, ["search_path", "target_file"], "PascalCase parameters read as snake_case");
    assert!(
        providers::name("gemini") == "Antigravity CLI"
            && strings::provider_label("gemini") == "Antigravity"
            && providers::executable("gemini") == "agy",
        "the name and the executable"
    );
}

/// AntigravityVerification.cs:222 `ASavedGeminiCliPaneIsMovedOnce`, `AntigravityMigration.ModelUnavailable`: agy's live list decides at
/// run time; the fallback list never resets a model. (The move itself is mighty-core's load: tests/load.rs.)
#[test]
fn only_the_live_list_says_a_model_is_gone() {
    let live: ModelCatalog = providers::antigravity_catalog("gemini-4-argon-high\tGemini 4 Argon (High)\n");
    assert!(
        mighty_run::antigravity::model_unavailable("gemini-2.5-pro", &live)
            && !mighty_run::antigravity::model_unavailable("gemini-4-argon-high", &live)
            && !mighty_run::antigravity::model_unavailable("default", &live),
        "only the live list decides"
    );
    assert!(
        !mighty_run::antigravity::model_unavailable("gemini-9-new", &providers::fallback("gemini")),
        "the fallback list never resets a model"
    );
}
