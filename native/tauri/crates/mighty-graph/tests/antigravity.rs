//! C# AntigravityVerification.cs, the graph's parts (the Mac's AntigravityGraphTests): a run draws the request, its sub-agents' work read
//! from their own transcripts and the latest answer (:346); a sub-agent waiting for its helper is not done until it answers (:380); the
//! watcher follows growing transcripts and nested sub-agents (:419). Every id, path and text is made up; the shapes follow what agy 1.3.5
//! printed and wrote. English copy, as the C# checks read it, in a process of its own.

use std::sync::{Arc, Mutex};

use mighty_core::graph::GraphTokenUsage;
use mighty_core::locale::Locale;
use mighty_core::model::AgentActivity;
use mighty_graph::ExecutionGraphNode;
use mighty_graph::antigravity::{AntigravityChildRecord, AntigravitySubagentWatcher, Limits};
use mighty_graph::tracker::{AntigravitySubagent, ExecutionGraphTracker};
use mighty_run::antigravity::records;
use mighty_run::parser::{OutputParser, ParserHooks};
use serde_json::{Value, json};

const PARENT: &str = "aaaaaaaa-0000-4000-8000-000000000001";
const CHILD: &str = "aaaaaaaa-0000-4000-8000-000000000002";
const GRANDCHILD: &str = "aaaaaaaa-0000-4000-8000-000000000003";

fn english() -> Locale {
    mighty_run::strings::set_language("en");
    Locale::new("en")
}

fn step(mut fields: Value) -> String {
    fields["conversation_id"] = json!(PARENT);
    json!({"event": "step_update", "step_update": fields}).to_string()
}

fn spec() -> Value {
    json!({"type_name": "research", "role": "File Reader", "initial_prompt": "Read a.txt", "conversation_id": CHILD})
}

/// `SubagentStream`: the stream of a run that starts one sub-agent and answers after its message.
fn subagent_stream() -> Vec<String> {
    vec![
        json!({"event": "init", "conversation_id": PARENT, "init": {"model": "gemini-3.8-flash-low", "cwd": "/workspace/demo"}}).to_string(),
        step(json!({"step_index": 0, "state": "DONE", "step_type": "user_input"})),
        step(json!({"step_index": 1, "state": "DONE", "step_type": "agent_response", "usage": {"input_tokens": 100, "output_tokens": 10, "cache_read_tokens": 0}})),
        step(json!({"step_index": 2, "state": "ACTIVE", "step_type": "subagent", "tool_name": "invoke_subagent", "subagent_info": {"subagents": [spec()]}})),
        step(json!({"step_index": 2, "state": "DONE", "step_type": "subagent", "tool_name": "invoke_subagent", "subagent_info": {"subagents": [spec()]}})),
        step(json!({"step_index": 3, "state": "ACTIVE", "step_type": "agent_response", "text_delta": "Waiting for "})),
        step(json!({"step_index": 3, "state": "DONE", "step_type": "agent_response", "text_delta": "the helper.\n", "usage": {"input_tokens": 120, "output_tokens": 5, "cache_read_tokens": 0}})),
        step(json!({"step_index": 4, "state": "DONE", "step_type": "system_message"})),
        step(json!({"step_index": 5, "state": "DONE", "step_type": "agent_response", "text_delta": "The file says hi.\n", "usage": {"input_tokens": 130, "output_tokens": 6, "cache_read_tokens": 40}})),
        json!({"event": "result", "result": {"conversation_id": PARENT, "status": "SUCCESS", "response": "Waiting for the helper.\nThe file says hi.\n"}}).to_string(),
    ]
}

fn line(index: i64, source: &str, kind: &str, status: &str, minute: u32, extra: Value) -> String {
    let mut fields = json!({"step_index": index, "source": source, "type": kind, "status": status, "created_at": format!("2026-01-02T03:{minute:02}:00Z")});
    if let Value::Object(more) = extra {
        for (key, value) in more {
            fields[key] = value;
        }
    }
    fields.to_string()
}

fn message(sender: &str, text: &str) -> String {
    format!(
        "The following is a <SYSTEM_MESSAGE> not actually sent by the user.\n\n<SYSTEM_MESSAGE>\n[Message] timestamp=2026-01-02T03:00:00Z sender={sender} priority=MESSAGE_PRIORITY_HIGH content={text}\n</SYSTEM_MESSAGE>"
    )
}

fn call(name: &str, mut args: Value) -> Value {
    args["toolAction"] = json!("Doing it");
    args["toolSummary"] = json!(format!("Summary of {name}"));
    json!({"name": name, "args": args})
}

fn spawned(id: &str) -> String {
    format!(
        "Created At: 2026-01-02T12:00:00+09:00\nCompleted At: 2026-01-02T12:00:00+09:00\nCreated the following subagents:\n{{\n  \"conversationId\": \"{id}\",\n  \"workspaceUris\": [\n    \"file:///workspace/demo\"\n  ]\n}}\nThe subagents will send you a message when they have completed their task."
    )
}

/// `ChildLines`: the sub-agent reads a file, starts its own helper, sends its answer back.
fn child_lines() -> Vec<String> {
    vec![
        line(0, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 1, json!({"content": message(PARENT, "Read a.txt")})),
        line(
            1,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            1,
            json!({"tool_calls": [call("view_file", json!({"AbsolutePath": "/workspace/demo/a.txt"}))], "input_tokens": 50, "output_tokens": 5, "cache_read_tokens": 0}),
        ),
        line(
            2,
            "MODEL",
            "GENERIC",
            "DONE",
            1,
            json!({"content": "Created At: x\nCompleted At: x\nFile Path: `file:///workspace/demo/a.txt`\n1: secret file line"}),
        ),
        line(
            3,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            2,
            json!({"tool_calls": [call("invoke_subagent", json!({"Subagents": [{"Role": "Checker", "TypeName": "research", "Prompt": "Check it", "Model": "inherit"}]}))]}),
        ),
        line(4, "MODEL", "GENERIC", "DONE", 2, json!({"content": spawned(GRANDCHILD)})),
        line(5, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 2, json!({"content": message(GRANDCHILD, "Checked.")})),
        line(
            6,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            2,
            json!({"tool_calls": [call("send_message", json!({"Recipient": PARENT, "Message": "It says hi"}))], "input_tokens": 60, "output_tokens": 7, "cache_read_tokens": 10}),
        ),
        line(7, "MODEL", "GENERIC", "DONE", 2, json!({"content": "Created At: x\nCompleted At: x\nMessage sent."})),
        line(8, "MODEL", "PLANNER_RESPONSE", "DONE", 3, json!({"content": "Sent the content back."})),
    ]
}

fn grandchild_lines() -> Vec<String> {
    vec![
        line(0, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 2, json!({"content": message(CHILD, "Check it")})),
        line(
            1,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            2,
            json!({"tool_calls": [call("send_message", json!({"Recipient": CHILD, "Message": "Checked."}))]}),
        ),
        line(2, "MODEL", "GENERIC", "DONE", 2, json!({"content": "Message sent."})),
        line(3, "MODEL", "PLANNER_RESPONSE", "DONE", 2, json!({"content": "Done checking."})),
    ]
}

fn json_of(text: &str) -> Value {
    serde_json::from_str(text).unwrap()
}

fn tracker(input: &str, model: Option<&str>, locale: Locale) -> ExecutionGraphTracker {
    ExecutionGraphTracker::new("agy-run", Some(input), "gemini", model, Box::new(|_: &ExecutionGraphNode| {}), None, None, locale)
}

fn usage(input: i64, output: i64, cache: i64) -> GraphTokenUsage {
    GraphTokenUsage { input_tokens: input, output_tokens: output, cache_read_tokens: cache, cache_creation_tokens: 0 }
}

/// AntigravityVerification.cs:346 `ARunDrawsTheRequestItsSubagentsWorkAndTheLatestAnswer`.
#[test]
fn a_run_draws_the_request_its_subagents_work_and_the_latest_answer() {
    let locale = english();
    let graph = Arc::new(Mutex::new(tracker("Ask a helper to read a.txt", Some("default"), locale)));
    let logs: Arc<Mutex<Vec<(String, String)>>> = Arc::default();
    let activities: Arc<Mutex<Vec<AgentActivity>>> = Arc::default();
    let (log, rows, row_graph, answer_graph) = (logs.clone(), activities.clone(), graph.clone(), graph.clone());
    let hooks = ParserHooks {
        log: Some(Arc::new(move |kind: &str, text: &str| log.lock().unwrap().push((kind.into(), text.into())))),
        resume: Some(Arc::new(|_: &str| {})),
        activity: Some(Arc::new(move |value: AgentActivity| {
            rows.lock().unwrap().push(value.clone());
            row_graph.lock().unwrap().activity(&value, None);
        })),
        answer: Some(Arc::new(move |text: &str| answer_graph.lock().unwrap().antigravity_answer(text))),
        ..ParserHooks::default()
    };
    let parser = OutputParser::new("gemini", "agy-run", hooks);
    for frame in subagent_stream() {
        graph.lock().unwrap().consume(&json_of(&frame));
        parser.parse(&frame);
    }
    let subagents = graph.lock().unwrap().antigravity_subagents().to_vec();
    assert_eq!(
        subagents,
        [AntigravitySubagent {
            conversation: CHILD.into(),
            parent: PARENT.into(),
            title: Some("File Reader (research)".into()),
            prompt: Some("Read a.txt".into())
        }],
        "the stream names the sub-agent"
    );
    assert_eq!(graph.lock().unwrap().antigravity_conversation(), Some(PARENT), "the run's own conversation");
    // The sub-agent's own transcript, as the runner's watcher reads it.
    let mut record = AntigravityChildRecord::new(subagents[0].clone(), "agy-run");
    for text in child_lines() {
        record.consume(&json_of(&text));
    }
    assert_eq!(
        record.take_spawned().iter().map(|s| s.conversation.as_str()).collect::<Vec<_>>(),
        [GRANDCHILD],
        "a nested helper is handed out"
    );
    graph.lock().unwrap().antigravity_session(&record.take().unwrap());
    parser.flush();
    parser.finish_activities(false);
    let mut graph = graph.lock().unwrap();
    graph.finish("completed");
    let run = graph.build_run(false).unwrap();
    assert!(run.status() == "completed" && run.input() == "Ask a helper to read a.txt" && run.provider() == Some("gemini"), "the request");
    // The latest reply is the answer, exactly as the transcript shows it.
    assert_eq!(run.final_output(), Some("The file says hi."), "the answer");
    let replies: Vec<String> = logs.lock().unwrap().iter().filter(|(kind, _)| kind == "assistant").map(|(_, text)| text.clone()).collect();
    assert_eq!(replies, ["Waiting for the helper.", "The file says hi."], "the replies");
    assert_eq!(run.usage, Some(usage(350, 21, 40)), "the request's usage");
    let records = run.response_records.clone().unwrap_or_default();
    assert!(
        records.len() == 3
            && records.iter().flatten().all(|r| r.model.as_deref() == Some("gemini-3.8-flash-low") && !r.marked_as_configured),
        "per-response records name agy's model: {records:?}"
    );
    let agent = &run.agents()[0];
    assert!(run.agents().len() == 1, "one sub-agent");
    assert!(
        agent.parent_id.is_none()
            && agent.title() == "File Reader (research)"
            && agent.input() == "Read a.txt"
            && agent.status() == "completed",
        "the sub-agent block: {agent:?}"
    );
    let tools: Vec<&AgentActivity> = agent.entries().iter().filter_map(|e| e.activity.as_ref()).collect();
    assert!(
        tools.iter().filter_map(|a| a.tool_name.as_deref()).collect::<Vec<_>>() == ["view_file", "invoke_subagent", "send_message"]
            && tools.iter().all(|a| a.state == "completed" && a.provider == "gemini"),
        "its tool rows: {tools:?}"
    );
    // A file it read is named, never copied.
    let read = tools.iter().find(|a| a.tool_name.as_deref() == Some("view_file")).unwrap();
    assert!(read.summary == "/workspace/demo/a.txt" && read.output.is_none(), "a read file is never copied");
    assert_eq!(
        tools.iter().find(|a| a.tool_name.as_deref() == Some("send_message")).map(|a| a.summary.as_str()),
        Some("Summary of send_message"),
        "agy's own summary stands in"
    );
    assert!(
        agent.entries().iter().any(|e| e.kind() == "assistant" && e.text() == "It says hi") && agent.usage == Some(usage(110, 12, 10)),
        "its answer and usage"
    );
    // The start is one row in the request's own block.
    let row = activities.lock().unwrap().iter().rev().find(|a| a.tool_name.as_deref() == Some("invoke_subagent")).cloned().unwrap();
    assert!(row.kind == "agent" && row.state == "completed" && row.summary == "File Reader (research)", "the start row: {row:?}");
}

/// AntigravityVerification.cs:380 `ASubagentWaitingForItsHelperIsNotDoneUntilItAnswers`.
#[test]
fn a_subagent_waiting_for_its_helper_is_not_done_until_it_answers() {
    let locale = english();
    let child = AntigravitySubagent {
        conversation: CHILD.into(),
        parent: PARENT.into(),
        title: Some("File Reader (research)".into()),
        prompt: Some("Read a.txt".into()),
    };
    let mut record = AntigravityChildRecord::new(child, "agy-run");
    for text in [
        child_lines()[0].clone(),
        line(
            1,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            1,
            json!({"tool_calls": [call("invoke_subagent", json!({"Subagents": [{"Role": "Checker", "TypeName": "research", "Prompt": "Check it"}]}))]}),
        ),
        line(2, "MODEL", "GENERIC", "DONE", 1, json!({"content": spawned(GRANDCHILD)})),
        line(3, "MODEL", "PLANNER_RESPONSE", "DONE", 1, json!({"content": "Waiting for my helper."})),
    ] {
        record.consume(&json_of(&text));
    }
    let waiting = record.take().unwrap();
    assert!(waiting.state == "running" && waiting.output.is_none(), "waiting for its helper");
    for text in [
        line(4, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 2, json!({"content": message(GRANDCHILD, "Checked.")})),
        line(5, "MODEL", "PLANNER_RESPONSE", "DONE", 2, json!({"content": "All checked."})),
    ] {
        record.consume(&json_of(&text));
    }
    let done = record.take().unwrap();
    assert!(
        done.state == "completed" && done.output.as_deref() == Some("All checked.") && done.turns == 1,
        "done once the helper reported"
    );
    // A helper that reports again after it answered: the same assignment goes on, in the graph too.
    let mut graph = tracker("Ask a helper", None, locale);
    for frame in subagent_stream() {
        graph.consume(&json_of(&frame));
    }
    graph.antigravity_session(&waiting);
    graph.antigravity_session(&done);
    assert_eq!(graph.build_run(true).unwrap().agents()[0].status(), "completed", "completed first");
    record.consume(&json_of(&line(6, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 3, json!({"content": message(GRANDCHILD, "One more thing.")}))));
    let continuing = record.take().unwrap();
    assert!(continuing.state == "running" && continuing.turns == 1, "the same assignment goes on");
    graph.antigravity_session(&continuing);
    let agent = graph.build_run(true).unwrap().agents()[0].clone();
    assert!(agent.status() == "running" && agent.activity_generation.unwrap_or(0) == 0, "running again without a new generation");
    record.consume(&json_of(&line(7, "MODEL", "PLANNER_RESPONSE", "DONE", 3, json!({"content": "Updated."}))));
    graph.antigravity_session(&record.take().unwrap());
    let updated = graph.build_run(true).unwrap().agents()[0].clone();
    assert!(
        updated.status() == "completed" && updated.entries().iter().any(|e| e.kind() == "assistant" && e.text() == "Updated."),
        "and done again"
    );
    // A new instruction from the parent reopens a settled sub-agent and keeps its answer.
    record.consume(&json_of(&line(8, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 4, json!({"content": message(PARENT, "Now read b.txt")}))));
    graph.antigravity_session(&record.take().unwrap());
    let reopened = graph.build_run(true).unwrap().agents()[0].clone();
    assert!(
        reopened.status() == "running"
            && reopened.activity_generation == Some(1)
            && reopened.entries().iter().any(|e| e.kind() == "user" && e.text() == "Now read b.txt"),
        "a new instruction is new work"
    );
    // Cancelled steps stop.
    let mut stopped = AntigravityChildRecord::new(
        AntigravitySubagent { conversation: CHILD.into(), parent: PARENT.into(), title: None, prompt: None },
        "agy-run",
    );
    for text in [child_lines()[0].clone(), line(1, "MODEL", "PLANNER_RESPONSE", "INTERRUPTED", 0, json!({"content": "Stopping"}))] {
        stopped.consume(&json_of(&text));
    }
    assert_eq!(stopped.take().map(|s| s.state), Some("stopped".into()), "an interrupted step stops the sub-agent");
}

fn write_transcript(home: &std::path::Path, conversation: &str, lines: &[String], trailing: &str) -> std::path::PathBuf {
    let path = records::transcript(&records::brain(home), conversation).unwrap();
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, lines.join("\n") + "\n" + trailing).unwrap();
    path
}

/// AntigravityVerification.cs:419 `TheWatcherFollowsGrowingTranscriptsAndNestedSubagents`.
#[test]
fn the_watcher_follows_growing_transcripts_and_nested_subagents() {
    english();
    let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos();
    let home = std::env::temp_dir().join(format!("mighty-graph-agy-watcher-{}-{nanos}", std::process::id()));
    let brain = records::brain(&home);
    let mut watcher = AntigravitySubagentWatcher::new(&brain, "agy-run", None, None, Limits::default());
    let started = [AntigravitySubagent {
        conversation: CHILD.into(),
        parent: PARENT.into(),
        title: Some("File Reader (research)".into()),
        prompt: Some("Read a.txt".into()),
    }];
    // Not written yet: nothing to read, and no error.
    assert!(watcher.poll(&started).is_empty(), "nothing to read yet");
    // A line still being written is not read.
    let lines = child_lines();
    let path = write_transcript(&home, CHILD, &lines[..2], &lines[2][..20]);
    let first = watcher.poll(&started);
    assert_eq!(first.len(), 1);
    let first = &first[0];
    assert!(
        first.thread == CHILD
            && first.state == "running"
            && first.turns == 1
            && first.input.as_deref() == Some("Read a.txt")
            && first.entries.iter().map(|e| e.activity.as_ref().map(|a| a.state.as_str())).collect::<Vec<_>>() == [Some("running")],
        "the first two steps"
    );
    // The rest arrives; the helper it starts is read in the same pass.
    let mut file = std::fs::OpenOptions::new().append(true).open(&path).unwrap();
    use std::io::Write;
    write!(file, "{}\n{}\n", &lines[2][20..], lines[3..].join("\n")).unwrap();
    drop(file);
    write_transcript(&home, GRANDCHILD, &grandchild_lines(), "");
    let second = watcher.poll(&started);
    assert_eq!(second.iter().map(|a| a.thread.as_str()).collect::<Vec<_>>(), [CHILD, GRANDCHILD], "the child and its helper");
    assert!(
        second[0].state == "completed"
            && second[0].output.as_deref() == Some("It says hi")
            && second[0].entries.iter().any(|e| e.id == first.entries[0].id && e.activity.as_ref().is_some_and(|a| a.state == "completed")),
        "the child finished"
    );
    assert!(
        second[1].parent_thread == CHILD
            && second[1].title.as_deref() == Some("Checker (research)")
            && second[1].input.as_deref() == Some("Check it")
            && second[1].output.as_deref() == Some("Checked."),
        "the helper under it"
    );
    assert!(
        watcher.poll(&started).is_empty() && watcher.finish(&started).is_empty() && watcher.poll(&started).is_empty(),
        "nothing new, and nothing after the end"
    );
    // The run's own conversation is never followed as a sub-agent; only conversation ids name a transcript.
    assert!(
        AntigravitySubagentWatcher::new(&brain, "agy-run", Some(CHILD), None, Limits::default()).poll(&started).is_empty(),
        "the root is never followed"
    );
    assert!(records::transcript(&brain, "../escape").is_none(), "no path leaves the brain folder");
    let _ = std::fs::remove_dir_all(&home);
}
