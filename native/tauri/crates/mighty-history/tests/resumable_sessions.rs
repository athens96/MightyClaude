//! The other CLIs' history and session lists: C# AntigravityVerification.cs:449 `OlderRequestsAreReadFromTheConversationTranscript` and
//! :493 `TheConversationListAndSkills` (its list part), PiVerification.cs:317 `SessionsOfTheWorkspaceFolderAreListedWithTheirFirstRequest`,
//! AcpSessionsVerification.cs (opencode's `opencode.db`, Hermes's `state.db`), AddPaneMenuVerification.cs:44
//! `OnlyClaudeAndCodexFromTheMenuMayAskToResume`, ComposerParityVerification.cs:489 `ResumeLookUpAsksOnlyForASessionToContinue` and the
//! resume row of ModelLabelVerification.cs:155 `GraphCapsulesAndResumeRowsReadVersions`. The databases are written here with SQLite in
//! the shapes the CLIs keep them (C# writes them with the system's SQLite, `TestSqlite`).

mod common;

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use common::*;
use mighty_core::graph::GraphTokenUsage;
use mighty_core::model::RunSession;
use mighty_history::history::{self, SessionHistoryRequest};
use mighty_history::resumable::{self, ResumableSession, ResumableSessionQuery, known};
use mighty_history::{Never, ResumableSessionListing, acp, antigravity, pi, replay};
use serde_json::{Value, json};

fn env(pairs: &[(&str, &str)]) -> HashMap<String, String> {
    pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
}

/// `TestSqlite.Write`: the statements run on a new database file.
fn write_database(path: &Path, statements: &[String]) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    let connection = rusqlite::Connection::open(path).unwrap();
    for sql in statements {
        connection.execute_batch(sql).unwrap_or_else(|e| panic!("{sql}: {e}"));
    }
}

fn sql_text(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn ids(listing: &ResumableSessionListing) -> Vec<String> {
    listing.items.iter().map(|i| i.session_id.clone()).collect()
}

fn listing(query: &ResumableSessionQuery, provider: &str) -> ResumableSessionListing {
    resumable::listing_of(query, provider, &Never)
}

fn query_for(workspace: &Path, home: &Path) -> ResumableSessionQuery {
    let mut query = ResumableSessionQuery::new(&workspace.to_string_lossy());
    query.home = home.to_path_buf();
    query
}

fn windows() -> bool {
    mighty_run::os::windows()
}

/// The path with its ASCII letters in capitals: the case SQLite's NOCASE folds.
fn capitals(path: &str) -> String {
    path.chars().map(|c| c.to_ascii_uppercase()).collect()
}

// ── Antigravity ─────────────────────────────────────────────────────────────────────────────────────────────────────────

const PARENT: &str = "aaaaaaaa-0000-4000-8000-000000000001";
const CHILD: &str = "aaaaaaaa-0000-4000-8000-000000000002";
const GRANDCHILD: &str = "aaaaaaaa-0000-4000-8000-000000000003";

fn step_line(index: i64, source: &str, kind: &str, status: &str, minute: u32, extra: Value) -> String {
    let mut fields = json!({"step_index": index, "source": source, "type": kind, "status": status, "created_at": format!("2026-01-02T03:{minute:02}:00Z")});
    if let (Some(object), Value::Object(more)) = (fields.as_object_mut(), extra) {
        for (key, value) in more {
            object.insert(key, value);
        }
    }
    fields.to_string()
}

fn request(text: &str) -> String {
    format!(
        "<USER_REQUEST>\n{text}\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: 2026-01-02T12:00:00+09:00.\n</ADDITIONAL_METADATA>"
    )
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

/// `ParentLines`: three requests — a command, a sub-agent, and one cut short by a declined edit.
fn parent_lines() -> Vec<String> {
    vec![
        step_line(0, "USER_EXPLICIT", "USER_INPUT", "DONE", 0, json!({"content": request("List the files")})),
        step_line(
            1,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            0,
            json!({"tool_calls": [call("run_command", json!({"CommandLine": "ls", "Cwd": "/workspace/demo"}))],
            "input_tokens": 100, "output_tokens": 10, "cache_read_tokens": 0}),
        ),
        step_line(
            2,
            "MODEL",
            "GENERIC",
            "DONE",
            0,
            json!({"content": "Created At: x\nCompleted At: x\n\nThe command exited with code 0.\nOutput:\na.txt\n"}),
        ),
        step_line(
            3,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            0,
            json!({"content": "There is one file.", "input_tokens": 120, "output_tokens": 8, "cache_read_tokens": 50}),
        ),
        step_line(4, "USER_EXPLICIT", "USER_INPUT", "DONE", 1, json!({"content": request("Ask a helper to read a.txt")})),
        step_line(
            5,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            1,
            json!({"tool_calls": [call("invoke_subagent", json!({"Subagents": [{"Role": "File Reader", "TypeName": "research", "Prompt": "Read a.txt", "Model": "inherit"}]}))]}),
        ),
        step_line(6, "MODEL", "GENERIC", "DONE", 1, json!({"content": spawned(CHILD)})),
        step_line(7, "MODEL", "PLANNER_RESPONSE", "DONE", 1, json!({"content": "Waiting for the helper."})),
        step_line(8, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 3, json!({"content": message(CHILD, "It says hi")})),
        step_line(9, "MODEL", "PLANNER_RESPONSE", "DONE", 3, json!({"content": "The file says hi."})),
        step_line(10, "USER_EXPLICIT", "USER_INPUT", "DONE", 4, json!({"content": request("Edit it")})),
        step_line(
            11,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            4,
            json!({"tool_calls": [call("write_to_file", json!({"TargetFile": "/workspace/demo/a.txt", "CodeContent": "new"}))]}),
        ),
        step_line(
            12,
            "MODEL",
            "GENERIC",
            "ERROR",
            4,
            json!({"content": "Created At: x\nCompleted At: x\nEncountered error", "error": "permission check failed: user denied permission"}),
        ),
    ]
}

/// `ChildLines`: the sub-agent reads a file, starts its own helper, sends its answer back.
fn child_lines() -> Vec<String> {
    vec![
        step_line(0, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 1, json!({"content": message(PARENT, "Read a.txt")})),
        step_line(
            1,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            1,
            json!({"tool_calls": [call("view_file", json!({"AbsolutePath": "/workspace/demo/a.txt"}))], "input_tokens": 50, "output_tokens": 5, "cache_read_tokens": 0}),
        ),
        step_line(
            2,
            "MODEL",
            "GENERIC",
            "DONE",
            1,
            json!({"content": "Created At: x\nCompleted At: x\nFile Path: `file:///workspace/demo/a.txt`\n1: secret file line"}),
        ),
        step_line(
            3,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            2,
            json!({"tool_calls": [call("invoke_subagent", json!({"Subagents": [{"Role": "Checker", "TypeName": "research", "Prompt": "Check it", "Model": "inherit"}]}))]}),
        ),
        step_line(4, "MODEL", "GENERIC", "DONE", 2, json!({"content": spawned(GRANDCHILD)})),
        step_line(5, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 2, json!({"content": message(GRANDCHILD, "Checked.")})),
        step_line(
            6,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            2,
            json!({"tool_calls": [call("send_message", json!({"Recipient": PARENT, "Message": "It says hi"}))], "input_tokens": 60, "output_tokens": 7, "cache_read_tokens": 10}),
        ),
        step_line(7, "MODEL", "GENERIC", "DONE", 2, json!({"content": "Created At: x\nCompleted At: x\nMessage sent."})),
        step_line(8, "MODEL", "PLANNER_RESPONSE", "DONE", 3, json!({"content": "Sent the content back."})),
    ]
}

/// `GrandchildLines`: the reader's own helper.
fn grandchild_lines() -> Vec<String> {
    vec![
        step_line(0, "SYSTEM", "SYSTEM_MESSAGE", "DONE", 2, json!({"content": message(CHILD, "Check it")})),
        step_line(
            1,
            "MODEL",
            "PLANNER_RESPONSE",
            "DONE",
            2,
            json!({"tool_calls": [call("send_message", json!({"Recipient": CHILD, "Message": "Checked."}))]}),
        ),
        step_line(2, "MODEL", "GENERIC", "DONE", 2, json!({"content": "Message sent."})),
        step_line(3, "MODEL", "PLANNER_RESPONSE", "DONE", 2, json!({"content": "Done checking."})),
    ]
}

fn write_transcript(home: &Path, conversation: &str, lines: &[String]) -> PathBuf {
    let path = antigravity::transcript(&antigravity::brain(home), conversation).unwrap();
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, lines.join("\n") + "\n").unwrap();
    path
}

/// AntigravityVerification.cs:449 `OlderRequestsAreReadFromTheConversationTranscript` (whole): the requests through the live parser,
/// their tool rows, and the sub-agents' own work read from their transcripts up to the next request.
#[test]
fn antigravity_older_requests_are_read_from_the_conversation_transcript() {
    mighty_run::strings::set_language("en");
    let home = temp("agy-history");
    write_transcript(&home, PARENT, &parent_lines());
    write_transcript(&home, CHILD, &child_lines());
    write_transcript(&home, GRANDCHILD, &grandchild_lines());
    check(history::PROVIDERS.contains(&"gemini"), "Antigravity has history");
    check(
        history::locate("gemini", PARENT, "/workspace/demo", &HashMap::new(), &home).is_some_and(|p| p.ends_with("transcript_full.jsonl")),
        "the transcript is found",
    );
    check(
        history::locate("gemini", &GRANDCHILD.replace('3', "9"), "/x", &HashMap::new(), &home).is_none(),
        "a missing conversation has no transcript",
    );
    let locale = mighty_core::locale::Locale::new("en");
    let chunk = history::load(&SessionHistoryRequest::new("gemini", PARENT, "/workspace/demo").home(&home), &locale, &Never)
        .expect("the transcript");
    let inputs: Vec<&str> = chunk.runs.iter().map(|r| r.input()).collect();
    check(
        chunk.reached_start && inputs == ["List the files", "Ask a helper to read a.txt", "Edit it"],
        &format!("three requests: {inputs:?}"),
    );
    check(chunk.runs.iter().all(|r| r.provider() == Some("gemini") && r.id().starts_with(replay::RUN_PREFIX)), "record runs");
    let statuses: Vec<&str> = chunk.runs.iter().map(|r| r.status()).collect();
    check(statuses == ["completed", "completed", "stopped"], &format!("statuses: {statuses:?}"));
    let command = &chunk.runs[0];
    check(
        command.final_output() == Some("There is one file.")
            && command.usage.map(|u| (u.input_tokens, u.output_tokens, u.cache_read_tokens)) == Some((220, 18, 50)),
        &format!("the command request's answer and usage: {:?} {:?}", command.final_output(), command.usage),
    );
    let delegated = &chunk.runs[1];
    let titles: Vec<&str> = delegated.agents().iter().map(|a| a.title()).collect();
    check(
        delegated.final_output() == Some("The file says hi.") && titles == ["File Reader (research)", "Checker (research)"],
        &format!("the sub-agents read back: {titles:?}"),
    );
    let (reader, checker) = (&delegated.agents()[0], &delegated.agents()[1]);
    let tools: Vec<&str> = reader.entries().iter().filter_map(|e| e.activity.as_ref()).filter_map(|a| a.tool_name.as_deref()).collect();
    check(
        reader.parent_id.is_none()
            && reader.input() == "Read a.txt"
            && reader.status() == "completed"
            && tools == ["view_file", "invoke_subagent", "send_message"],
        &format!("the reader: {tools:?}"),
    );
    check(
        !reader.entries().iter().any(|e| {
            e.text().contains("secret file line")
                || e.activity.as_ref().and_then(|a| a.output.as_deref()).is_some_and(|o| o.contains("secret file line"))
        }),
        "file contents are never copied",
    );
    check(
        checker.parent_id.as_deref() == reader.id.as_deref() && checker.input() == "Check it" && checker.status() == "completed",
        "the checker under the reader",
    );
    check(chunk.runs[2].agents().is_empty(), "the declined edit has no sub-agent");
    // A chunk at a time from the end, and a later chunk above the newer requests.
    let mut newest = SessionHistoryRequest::new("gemini", PARENT, "/x").home(&home);
    newest.turns = 1;
    let newest = history::load(&newest, &locale, &Never).expect("the newest");
    check(newest.runs.iter().map(|r| r.input()).collect::<Vec<_>>() == ["Edit it"] && !newest.reached_start, "the newest request alone");
    let mut older = SessionHistoryRequest::new("gemini", PARENT, "/x").home(&home);
    older.file = Some(newest.file.clone());
    older.end = Some(newest.end);
    let older = history::load(&older, &locale, &Never).expect("the older chunk");
    check(
        older.runs.iter().map(|r| r.input()).collect::<Vec<_>>() == ["List the files", "Ask a helper to read a.txt"]
            && older.runs[1].agents().len() == 2,
        "the older chunk keeps its sub-agents",
    );
    // Recorded steps become the events agy printed.
    let steps: Vec<Value> = parent_lines().iter().map(|l| serde_json::from_str(l).unwrap()).collect();
    let (events, status) = antigravity::stream_events(&steps[1..4], PARENT, &locale);
    let shapes: Vec<String> = events
        .iter()
        .map(|e| {
            format!("{}:{}", e["event"].as_str().unwrap_or(""), e.get("step_update").and_then(|s| s["step_type"].as_str()).unwrap_or(""))
        })
        .collect();
    check(
        status == "completed"
            && shapes == ["init:", "step_update:agent_response", "step_update:tool", "step_update:agent_response", "result:"],
        &format!("events {shapes:?}"),
    );
    let (open, status) = antigravity::stream_events(&steps[11..12], PARENT, &locale);
    check(status == "stopped" && open.last().unwrap()["step_update"]["state"] == json!("ACTIVE"), "a call left open ends stopped");
    let failed: Value =
        serde_json::from_str(&step_line(1, "MODEL", "PLANNER_RESPONSE", "ERROR", 0, json!({"error": "Quota exceeded"}))).unwrap();
    let (events, status) = antigravity::stream_events(&[failed], PARENT, &locale);
    check(status == "error" && events.last().unwrap()["result"]["error"]["message"] == json!("Quota exceeded"), "a failed answer");
    check(
        antigravity::request(Some("<USER_REQUEST>\n  hello  \n</USER_REQUEST>\n<ADDITIONAL_METADATA>x</ADDITIONAL_METADATA>")).as_deref()
            == Some("hello")
            && antigravity::message(Some(&message(PARENT, "Do it\nnow")), PARENT).as_deref() == Some("Do it\nnow")
            && antigravity::message(Some(&message("system", "[Notice]")), PARENT).is_none()
            && antigravity::title(Some(""), Some("research")).as_deref() == Some("research")
            && antigravity::title(None, None).is_none(),
        "record helpers",
    );
    check(
        antigravity::opening(
            br#"{"step_index": 0, "type": "USER_INPUT", "source": "USER_EXPLICIT", "content": "<USER_REQUEST>\nHi\n</USER_REQUEST>"}"#,
        )
        .map(|o| o.0)
        .as_deref()
            == Some("Hi"),
        "however agy spaces its JSON",
    );
    let _ = std::fs::remove_dir_all(home);
}

/// AntigravityVerification.cs:493 `TheConversationListAndSkills`, the conversation list.
#[test]
fn antigravity_the_conversation_list() {
    let root = temp("agy-list");
    let home = root.join("home");
    let workspace = root.join("work").join("demo project");
    std::fs::create_dir_all(&workspace).unwrap();
    let (other, automated, missing) =
        ("aaaaaaaa-0000-4000-8000-000000000004", "aaaaaaaa-0000-4000-8000-000000000005", "aaaaaaaa-0000-4000-8000-000000000006");
    let uri = |path: &Path| {
        let text = path.to_string_lossy().replace('\\', "/").replace(' ', "%20");
        if text.starts_with('/') { format!("file://{text}") } else { format!("file:///{text}") }
    };
    let here = uri(&workspace) + "/";
    let rows: Vec<(&str, &str, Vec<String>, &str)> = vec![
        (PARENT, "Summary title", vec![uri(&root.join("attachments")), here.clone()], ""),
        (CHILD, "", vec![here.clone()], PARENT),
        (other, "Elsewhere", vec![uri(&root.join("elsewhere"))], ""),
        (automated, "", vec![here.clone()], ""),
        (missing, "No transcript", vec![here.clone()], ""),
    ];
    let mut statements = vec!["CREATE TABLE `conversation_summaries` (`conversation_id` text,`title` text NOT NULL DEFAULT \"\",`step_count` integer NOT NULL DEFAULT 0,`workspace_uris` text NOT NULL,`agent_name` text NOT NULL DEFAULT \"\",`parent_conversation_id` text NOT NULL DEFAULT \"\",PRIMARY KEY (`conversation_id`))".to_string()];
    for (id, title, uris, parent) in &rows {
        statements.push(format!(
            "INSERT INTO conversation_summaries (conversation_id, title, workspace_uris, parent_conversation_id) VALUES ('{id}', '{title}', {}, '{parent}')",
            sql_text(&serde_json::to_string(uris).unwrap())
        ));
    }
    write_database(&antigravity::database(&home), &statements);
    write_transcript(&home, PARENT, &parent_lines());
    write_transcript(&home, CHILD, &child_lines());
    write_transcript(&home, other, &parent_lines()[..4]);
    write_transcript(
        &home,
        automated,
        &[step_line(0, "USER_EXPLICIT", "USER_INPUT", "DONE", 0, json!({"content": request("User: earlier text\nAssistant: more")}))],
    );
    let query = query_for(&workspace, &home);
    let found = listing(&query, "gemini");
    check(
        ids(&found) == [PARENT] && found.hidden == 1,
        &format!("this folder's conversation without sub-agents: {:?} hidden {}", ids(&found), found.hidden),
    );
    let item = &found.items[0];
    check(
        item.provider == "gemini"
            && item.title.as_deref() == Some("List the files")
            && item.requests == Some(3)
            && item.model.is_none()
            && !item.automated,
        "the row",
    );
    check(
        listing(&ResumableSessionQuery { include_automated: true, ..query.clone() }, "gemini").items.len() == 2
            && resumable::listing(&query, &Never).items.iter().map(|i| i.provider.as_str()).collect::<Vec<_>>() == ["gemini"],
        "automated rows and the whole list",
    );
    check(
        listing(&ResumableSessionQuery { excluding: HashSet::from([PARENT.to_string()]), ..query.clone() }, "gemini").items.is_empty(),
        "a conversation an open pane continues is left out",
    );
    check(resumable::offers_resume("claude", "gemini") && resumable::may_ask_resume("gemini", true), "창 추가 asks first");
    let pane = resumable::apply(
        item,
        &RunSession {
            workspace_id: Some("space".into()),
            title: Some("Antigravity".into()),
            provider: Some("gemini".into()),
            ..RunSession::default()
        },
    );
    check(
        pane.resume_id.as_deref() == Some(PARENT) && pane.title.as_deref() == Some("List the files"),
        "continuing one resumes that conversation",
    );
    check(
        antigravity::summaries(&antigravity::database(&home), 2).map(|s| s.into_iter().map(|c| c.conversation).collect::<Vec<_>>())
            == Some(vec![missing.to_string(), automated.to_string()]),
        "the newest rows come first",
    );
    check(listing(&query_for(&workspace, &root), "gemini").items.is_empty(), "no database (agy never ran): nothing to list");
    let _ = std::fs::remove_dir_all(root);
}

// ── pi ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// PiVerification.cs:317 `SessionsOfTheWorkspaceFolderAreListedWithTheirFirstRequest`.
#[test]
fn pi_sessions_of_the_workspace_folder_are_listed_with_their_first_request() {
    let home = temp("pi");
    let workspace = home.join("project");
    std::fs::create_dir_all(&workspace).unwrap();
    let real = resumable::workspace_paths(&workspace.to_string_lossy()).last().cloned().unwrap();
    let folder = home.join(".pi").join("agent").join("sessions").join(pi::session_folder(&real));
    std::fs::create_dir_all(&folder).unwrap();
    let (id, other, empty) =
        ("0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30", "0197c1d2-aaaa-7c4e-9b1a-5d2e8f6a4b31", "0197c1d2-bbbb-7c4e-9b1a-5d2e8f6a4b32");
    let cwd = serde_json::to_string(&real).unwrap();
    let lines = [
        format!(r#"{{"type":"session","version":3,"id":"{id}","timestamp":"2026-11-10T09:30:00.000Z","cwd":{cwd}}}"#),
        r#"{"type":"model_change","id":"a1b2c3d4","parentId":null,"timestamp":"2026-11-10T09:30:00.100Z","provider":"anthropic","modelId":"claude-sonnet-4-5"}"#.to_string(),
        r#"{"type":"message","id":"b2c3d4e5","parentId":"a1b2c3d4","timestamp":"2026-11-10T09:30:01.000Z","message":{"role":"user","content":[{"type":"text","text":"  Fix the\nfailing test  "}],"timestamp":1762767001000}}"#.to_string(),
        r#"{"type":"message","id":"c3d4e5f6","parentId":"b2c3d4e5","timestamp":"2026-11-10T09:30:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Done."}],"provider":"anthropic","model":"claude-sonnet-4-5","stopReason":"stop","timestamp":1762767005000}}"#.to_string(),
        r#"{"type":"message","id":"d4e5f6a7","parentId":"c3d4e5f6","timestamp":"2026-11-10T09:31:00.000Z","message":{"role":"user","content":"And the lint?","timestamp":1762767060000}}"#.to_string(),
        r#"{"type":"message","id":"e5f6a7b8","parentId":"d4e5f6a7","timestamp":"2026-11-10T09:31:09.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Clean."}],"provider":"anthropic","model":"claude-opus-4-1","stopReason":"stop","timestamp":1762767069000}}"#.to_string(),
    ];
    std::fs::write(folder.join(format!("2026-11-10T09-30-00-000Z_{id}.jsonl")), lines.join("\n") + "\n").unwrap();
    // Another folder's header, and a session nobody wrote a request in, are not listed.
    std::fs::write(
        folder.join(format!("2026-11-09T09-30-00-000Z_{other}.jsonl")),
        format!("{}\n{}\n", lines[0].replace(id, other).replace(&cwd, "\"/somewhere/else\""), lines[2]),
    )
    .unwrap();
    std::fs::write(folder.join(format!("2026-11-08T09-30-00-000Z_{empty}.jsonl")), lines[0].replace(id, empty) + "\n").unwrap();
    std::fs::write(folder.join("notes.txt"), "not a session").unwrap();
    // The records were just written; the list is read as of the next day.
    let mut query = query_for(&workspace, &home);
    query.now = mighty_core::time::DateTimeOffset::now();
    let found = listing(&query, "pi");
    check(found.items.len() == 1 && found.hidden == 0, &format!("one session: {}", found.items.len()));
    let item = &found.items[0];
    check(
        item.provider == "pi"
            && item.session_id == id
            && item.title.as_deref() == Some("Fix the failing test")
            && item.requests == Some(2)
            && item.model.as_deref() == Some("claude-opus-4-1"),
        &format!("the row: {item:?}"),
    );
    check(
        resumable::listing(&query, &Never).items.iter().map(|i| i.id()).collect::<Vec<_>>() == [format!("pi:{id}")]
            && resumable::PROVIDERS.contains(&"pi"),
        "the whole list holds it",
    );
    check(
        listing(&ResumableSessionQuery { excluding: HashSet::from([id.to_string()]), ..query.clone() }, "pi").items.is_empty(),
        "a session an open pane continues is left out",
    );
    check(
        pi::session_file(id, &workspace.to_string_lossy(), &home, &HashMap::new())
            .is_some_and(|p| p.file_name().is_some_and(|n| n.to_string_lossy() == format!("2026-11-10T09-30-00-000Z_{id}.jsonl")))
            && pi::session_file("0197c1d2-cccc-7c4e-9b1a-5d2e8f6a4b33", &workspace.to_string_lossy(), &home, &HashMap::new()).is_none(),
        "the file a pane resumes is found by the id",
    );
    check(
        pi::session_folder("/Users/dev/my project") == "--Users-dev-my project--"
            && pi::session_folder(r"C:\Users\dev") == "--C--Users-dev--",
        "the folder name",
    );
    check(resumable::offers_resume("claude", "pi"), "창 추가 offers to continue");
    let _ = std::fs::remove_dir_all(home);
}

// ── opencode ────────────────────────────────────────────────────────────────────────────────────────────────────────────

const SESSION: &str = "ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw";
const PLACEHOLDER: &str = "ses_8a0000000000Ab0000000000Cd";
const OC_CHILD: &str = "ses_7a0000000000Ab0000000000Ce";
const ELSEWHERE: &str = "ses_6a0000000000Ab0000000000Cf";
const ARCHIVED: &str = "ses_5a0000000000Ab0000000000Cg";
const OLD: &str = "ses_4a0000000000Ab0000000000Ch";
const UNUSED: &str = "ses_3a0000000000Ab0000000000Ci";
const AUTOMATED: &str = "ses_2a0000000000Ab0000000000Cj";
const BLANK: &str = "ses_1a0000000000Ab0000000000Ck";
const SPELLED: &str = "ses_0a0000000000Ab0000000000Cl";

fn now_ms() -> i64 {
    acp::unix_milliseconds(mighty_core::time::DateTimeOffset::now())
}

/// AcpSessionsVerification.cs:30 `OpencodeListsTheRootSessionsOfTheWorkspaceFolder`.
#[test]
fn resume_picker_lists_opencodes_root_sessions_of_this_folder_from_opencode_db() {
    let root = temp("opencode");
    let workspace = root.join("work").join("project");
    std::fs::create_dir_all(&workspace).unwrap();
    let home = root.join("home");
    let database = acp::opencode_database(&HashMap::new(), &home).expect("opencode.db has a place");
    let ms = now_ms();
    let folder = workspace.to_string_lossy().into_owned();
    let row = |id: &str, parent: Option<&str>, directory: &str, title: &str, model: Option<&str>, updated: i64, archived: Option<i64>| {
        format!(
            "INSERT INTO session_v2 VALUES ({}, 'p', {}, 's', {}, {}, '2.2.7', {}, {}, {updated}, {})",
            sql_text(id),
            parent.map_or("NULL".into(), sql_text),
            sql_text(directory),
            sql_text(title),
            model.map_or("NULL".into(), sql_text),
            updated - 9_000,
            archived.map_or("NULL".into(), |a| a.to_string()),
        )
    };
    let mut seq = 0;
    let mut message = |session: &str, kind: &str, data: &str| {
        seq += 1;
        format!(
            "INSERT INTO session_message VALUES ('msg_{seq}', {}, {}, {seq}, 1, 1, {})",
            sql_text(session),
            sql_text(kind),
            sql_text(data)
        )
    };
    // A folder spelled with the other slash and letter case is the same folder on Windows alone.
    let spelled = capitals(&if windows() { folder.replace('\\', "/") } else { folder.clone() });
    let elsewhere = root.join("elsewhere").to_string_lossy().into_owned();
    let statements = vec![
        "CREATE TABLE session_v2 (id TEXT PRIMARY KEY, project_id TEXT, parent_id TEXT, slug TEXT, directory TEXT, title TEXT, version TEXT, model TEXT, time_created INTEGER, time_updated INTEGER, time_archived INTEGER)".to_string(),
        "CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, seq INTEGER, time_created INTEGER, time_updated INTEGER, data TEXT)".to_string(),
        row(SESSION, None, &folder, "Fix the failing test", Some(r#"{"id":"claude-sonnet-4-5","providerID":"anthropic"}"#), ms - 1_000, None),
        row(PLACEHOLDER, None, &folder, "New session - 2026-11-10T10:00:00.000Z", None, ms - 2_000, None),
        row(OC_CHILD, Some(SESSION), &folder, "Child session - 2026-11-10T10:01:00.000Z", None, ms - 500, None),
        row(ELSEWHERE, None, &elsewhere, "Other folder", None, ms - 500, None),
        row(ARCHIVED, None, &folder, "Archived", None, ms - 500, Some(ms)),
        row(OLD, None, &folder, "Two months ago", None, ms - 61 * 86_400_000, None),
        row(UNUSED, None, &folder, "Planning", None, ms - 3_000, None),
        row(AUTOMATED, None, &folder, "Nested run", None, ms - 4_000, None),
        row(BLANK, None, &folder, "Rename the module", Some("{}"), ms - 5_000, None),
        row(SPELLED, None, &spelled, "Spelled differently", None, ms - 6_000, None),
        message(SESSION, "user", r#"{"text":"why does main return 1?","files":[]}"#),
        message(SESSION, "assistant", r#"{"agent":"build","content":[]}"#),
        message(SESSION, "user", r#"{"text":"and now?"}"#),
        message(PLACEHOLDER, "user", r#"{"text":"  hello\n"}"#),
        message(OC_CHILD, "user", r#"{"text":"a sub-agent's task"}"#),
        message(ELSEWHERE, "user", r#"{"text":"elsewhere"}"#),
        message(ARCHIVED, "user", r#"{"text":"archived"}"#),
        message(OLD, "user", r#"{"text":"long ago"}"#),
        message(AUTOMATED, "user", r#"{"text":"User: earlier text\nAssistant: more"}"#),
        message(BLANK, "user", r#"{"text":"   "}"#),
        message(SPELLED, "user", r#"{"text":"spelled"}"#),
    ];
    write_database(&database, &statements);
    let query = query_for(&workspace, &home);
    let found = listing(&query, "opencode");
    let expected: Vec<&str> = if windows() { vec![SESSION, PLACEHOLDER, BLANK, SPELLED] } else { vec![SESSION, PLACEHOLDER, BLANK] };
    check(
        ids(&found) == expected && found.hidden == 1,
        &format!("this folder's root sessions with a request, newest first: {:?} hidden {}", ids(&found), found.hidden),
    );
    let first = &found.items[0];
    check(
        first.provider == "opencode"
            && first.title.as_deref() == Some("why does main return 1?")
            && first.requests == Some(2)
            && first.model.as_deref() == Some("anthropic/claude-sonnet-4-5")
            && first.path == database
            && !first.automated
            && (acp::unix_milliseconds(first.modified) - (ms - 1_000)).abs() < 1,
        &format!("the first user message titles the row; its user messages are the requests: {first:?}"),
    );
    check(
        found.items[1].title.as_deref() == Some("hello") && found.items[1].requests == Some(1) && found.items[1].model.is_none(),
        "a placeholder title gives way to the request",
    );
    check(
        found.items[2].title.as_deref() == Some("Rename the module")
            && found.items[2].requests == Some(1)
            && found.items[2].model.is_none(),
        "a blank request leaves opencode's own title, and a model without its two names is none",
    );
    check(
        listing(&ResumableSessionQuery { excluding: HashSet::from([SESSION.to_lowercase()]), ..query.clone() }, "opencode")
            .items
            .iter()
            .all(|i| i.session_id != SESSION),
        "an open pane's session is left out",
    );
    let all = listing(&ResumableSessionQuery { include_automated: true, ..query.clone() }, "opencode");
    check(
        all.hidden == 0 && all.items.iter().any(|i| i.session_id == AUTOMATED && i.automated),
        "every session shown: the nested run too, marked",
    );
    check(
        listing(&ResumableSessionQuery { known: HashSet::from([AUTOMATED.to_lowercase()]), ..query.clone() }, "opencode")
            .items
            .iter()
            .any(|i| i.session_id == AUTOMATED && !i.automated),
        "a session the app's own pane ran is never hidden",
    );
    let head = listing(&ResumableSessionQuery { head_only: true, ..query.clone() }, "opencode");
    check(
        head.items.iter().all(|i| i.requests.is_none())
            && ids(&head).into_iter().take(5).collect::<Vec<_>>() == [SESSION, PLACEHOLDER, UNUSED, AUTOMATED, BLANK]
            && head.items[0].title.as_deref() == Some("Fix the failing test")
            && head.items[1].title.is_none(),
        &format!("a yes/no look-up reads no messages: opencode's titles, no counts: {:?}", ids(&head)),
    );
    check(
        ids(&listing(&ResumableSessionQuery { maximum_sessions: 1, ..query.clone() }, "opencode")) == [SESSION],
        "the list stops at its size",
    );
    check(ids(&resumable::listing(&query, &Never)) == expected, "the whole list has opencode's sessions");
    check(
        resumable::PROVIDERS.contains(&"opencode")
            && resumable::offers_resume("claude", "opencode")
            && resumable::may_ask_resume("opencode", true),
        "창 추가 asks first for opencode",
    );
    let pane = resumable::apply(
        first,
        &RunSession {
            workspace_id: Some("space".into()),
            title: Some("opencode".into()),
            provider: Some("opencode".into()),
            ..RunSession::default()
        },
    );
    check(
        pane.resume_id.as_deref() == Some(SESSION) && pane.title.as_deref() == Some("why does main return 1?"),
        "continuing one resumes that session",
    );
    check(
        mighty_core::wire::session_identifier(SESSION)
            && !mighty_core::wire::session_identifier("ses_../x")
            && known::adding(SESSION, &[]) == Some(vec![SESSION.to_string()]),
        "opencode's ids are session ids, remembered as the app's own",
    );
    check(listing(&query_for(&workspace, &root), "opencode").items.is_empty(), "no database (opencode never ran): nothing to list");
    check(
        listing(&ResumableSessionQuery { environment: env(&[("OPENCODE_DB", ":memory:")]), ..query.clone() }, "opencode").items.is_empty(),
        "an in-memory database keeps nothing to list",
    );
    check(
        listing(
            &ResumableSessionQuery {
                home: root.clone(),
                environment: env(&[("OPENCODE_DB", &database.to_string_lossy())]),
                ..query.clone()
            },
            "opencode",
        )
        .items
        .len()
            == expected.len(),
        "OPENCODE_DB names the database",
    );
    let _ = std::fs::remove_dir_all(root);
}

/// AcpSessionsVerification.cs:103 `OpencodeFindsItsDatabaseWhereOpenCodeKeepsIt`.
#[test]
fn resume_picker_finds_opencode_db_where_opencode_2_keeps_it() {
    let home = std::env::temp_dir().join("mighty-opencode-home");
    let data = home.join(".local").join("share").join("opencode");
    check(
        acp::opencode_data_directory(&env(&[]), &home) == data
            && acp::opencode_database(&env(&[]), &home) == Some(data.join("opencode.db")),
        "<home>/.local/share/opencode/opencode.db, on Windows too",
    );
    let xdg = home.join("xdg");
    let xdg_text = xdg.to_string_lossy().into_owned();
    check(
        acp::opencode_database(&env(&[("XDG_DATA_HOME", &xdg_text)]), &home) == Some(xdg.join("opencode").join("opencode.db")),
        "XDG_DATA_HOME moves the data folder",
    );
    check(
        acp::opencode_database(&env(&[("XDG_DATA_HOME", "relative")]), &home) == Some(data.join("opencode.db")),
        "a relative XDG_DATA_HOME names no folder the app can know",
    );
    let elsewhere = home.join("db").join("sessions.db");
    check(
        acp::opencode_database(&env(&[("OPENCODE_DB", &elsewhere.to_string_lossy()), ("XDG_DATA_HOME", &xdg_text)]), &home)
            == Some(elsewhere.clone()),
        "OPENCODE_DB names the file",
    );
    check(
        acp::opencode_database(&env(&[("OPENCODE_DB", "work.db")]), &home) == Some(data.join("work.db"))
            && acp::opencode_database(&env(&[("OPENCODE_DB", "work.db"), ("XDG_DATA_HOME", &xdg_text)]), &home)
                == Some(xdg.join("opencode").join("work.db")),
        "a relative OPENCODE_DB is in the data folder",
    );
    check(acp::opencode_database(&env(&[("OPENCODE_DB", ":memory:")]), &home).is_none(), ":memory: keeps nothing on disk");
    check(acp::opencode_database(&env(&[("OPENCODE_DB", "")]), &home) == Some(data.join("opencode.db")), "an empty value is no value");
    check(
        acp::opencode_placeholder_title("New session - 2026-11-10T10:00:00.000Z")
            && acp::opencode_placeholder_title("child session - x")
            && acp::opencode_placeholder_title("")
            && !acp::opencode_placeholder_title("Fix the build"),
        "opencode's placeholder titles",
    );
    check(
        acp::opencode_model(Some(r#"{"id":"gpt-5","providerID":"openai","variant":"high"}"#)).as_deref() == Some("openai/gpt-5")
            && acp::opencode_model(Some(r#"{"id":"x"}"#)).is_none()
            && acp::opencode_model(Some("not json")).is_none(),
        "the model is provider/model",
    );
    check(
        ["XDG_DATA_HOME", "OPENCODE_DB", "HERMES_HOME", "LOCALAPPDATA"].iter().all(|v| resumable::STORE_VARIABLES.contains(v)),
        "a session list carries the variables these databases are found by",
    );
}

// ── Hermes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

const MAIN: &str = "local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b";
const EARLIER: &str = "local-a8a2537a19ce1905c0a99559ca0f2e86197234e68b0931bf190fc6517e91386e";

fn local_id(c: char) -> String {
    format!("local-{}", c.to_string().repeat(64))
}

fn receipt(id: &str, yolo: bool) -> String {
    let mut request = json!({"cwd": "/tmp/project", "editor": {}, "source": "acp"});
    if yolo {
        request["yolo"] = json!(true);
    }
    json!({"policy": {"cwd": "/tmp/project", "edit_approval_policy": "ask", "request_json": request.to_string(), "source": "acp"}, "session_id": id}).to_string()
}

fn number(value: f64) -> String {
    format!("{value}")
}

/// `WriteState`: the fixture `state.db` (WAL, as Hermes keeps it) for `workspace`; `t` is now in seconds.
fn write_state(database: &Path, workspace: &str, root: &Path, t: f64) {
    let columns = "id, source, created_source, model, model_config, parent_session_id, started_at, message_count, cwd, title, last_activity_at, archived, hidden";
    let maybe = |value: Option<&str>| value.map_or("NULL".to_string(), sql_text);
    #[allow(clippy::too_many_arguments)]
    fn row(
        columns: &str,
        maybe: &dyn Fn(Option<&str>) -> String,
        id: &str,
        source: &str,
        created: Option<&str>,
        model: Option<&str>,
        config: Option<&str>,
        parent: Option<&str>,
        started: f64,
        messages: i32,
        cwd: Option<&str>,
        title: Option<&str>,
        active: Option<f64>,
        archived: i32,
        hidden: i32,
    ) -> String {
        format!(
            "INSERT INTO sessions ({columns}) VALUES ({}, {}, {}, {}, {}, {}, {}, {messages}, {}, {}, {}, {archived}, {hidden})",
            sql_text(id),
            sql_text(source),
            maybe(created),
            maybe(model),
            maybe(config),
            maybe(parent),
            number(started),
            maybe(cwd),
            maybe(title),
            active.map_or("NULL".into(), number)
        )
    }
    let message = |session: &str, role: &str, content: &str, timestamp: f64| {
        format!(
            "INSERT INTO messages (session_id, role, content, timestamp) VALUES ({}, {}, {}, {})",
            sql_text(session),
            sql_text(role),
            sql_text(content),
            number(timestamp)
        )
    };
    let old = t - 61.0 * 86_400.0;
    let (continued, noted, pictured, nested, cased) = (local_id('7'), local_id('c'), local_id('d'), local_id('e'), local_id('f'));
    let elsewhere = root.join("elsewhere").to_string_lossy().into_owned();
    let cased_folder = capitals(workspace);
    let config = json!({"cwd": workspace}).to_string();
    let m = &maybe;
    let statements = vec![
        "PRAGMA journal_mode=WAL".to_string(),
        "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, created_source TEXT, model TEXT, model_config TEXT, parent_session_id TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0, cwd TEXT, title TEXT, last_activity_at REAL, archived INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0)".to_string(),
        "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL)".to_string(),
        "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)".to_string(),
        row(columns, m, MAIN, "local", Some("acp"), Some("anthropic/claude-sonnet-4.5"), None, None, t - 900.0, 4, Some(workspace), Some("Clean the build"), Some(t - 800.0), 0, 0),
        row(columns, m, &continued, "local", Some("acp"), Some("anthropic/claude-sonnet-4.5"), None, Some(MAIN), t - 300.0, 2, Some(workspace), None, Some(t - 100.0), 0, 0),
        row(columns, m, EARLIER, "acp", None, None, Some(&config), None, t - 5_000.0, 2, None, None, None, 0, 0),
        row(columns, m, &noted, "local", Some("acp"), None, None, None, t - 2_000.0, 2, Some(workspace), Some("Quiz me"), Some(t - 1_990.0), 0, 0),
        row(columns, m, &pictured, "local", Some("acp"), None, None, None, t - 3_000.0, 2, Some(workspace), Some("Screenshot question"), Some(t - 2_990.0), 0, 0),
        row(columns, m, &nested, "local", Some("acp"), None, None, None, t - 4_000.0, 2, Some(workspace), Some("Nested"), Some(t - 3_990.0), 0, 0),
        row(columns, m, &cased, "local", Some("acp"), None, None, None, t - 6_000.0, 2, Some(&cased_folder), Some("Cased"), Some(t - 5_990.0), 0, 0),
        row(columns, m, "20261110_090000_0000cli1", "cli", Some("cli"), None, None, None, t - 50.0, 2, Some(workspace), Some("A terminal chat"), Some(t - 40.0), 0, 0),
        row(columns, m, "20261110_090000_0000else", "acp", Some("acp"), None, None, None, t - 50.0, 2, Some(&elsewhere), Some("Other folder"), Some(t - 40.0), 0, 0),
        row(columns, m, "20261110_090000_0000arch", "acp", Some("acp"), None, None, None, t - 50.0, 2, Some(workspace), Some("Archived"), Some(t - 40.0), 1, 0),
        row(columns, m, "20261110_090000_0000hide", "acp", Some("acp"), None, None, None, t - 50.0, 2, Some(workspace), Some("Hidden"), Some(t - 40.0), 0, 1),
        row(columns, m, "20261110_090000_0000none", "acp", Some("acp"), None, None, None, t - 50.0, 0, Some(workspace), Some("Never used"), Some(t - 40.0), 0, 0),
        row(columns, m, "20261110_090000_00000old", "acp", Some("acp"), None, None, None, old, 2, Some(workspace), Some("Two months ago"), Some(old + 100.0), 0, 0),
        message(MAIN, "user", "clean the build folder", 1.0),
        message(MAIN, "assistant", "done", 2.0),
        message(&continued, "user", "and write notes", t - 20.0),
        message(EARLIER, "user", r#"[{"type":"text","text":"what is in this picture?"},{"type":"image_url"}]"#, 4.0),
        message(&noted, "user", "선택하는 문제를 내\n\n[Note from the Mighty Code app, not from the user: this pane shows your clarify tool as a card the user answers with a click.]", t - 1_900.0),
        message(&pictured, "user", r#"[{"type":"image_url"}]"#, t - 2_900.0),
        message(&nested, "user", "User: earlier text\nAssistant: more", t - 3_900.0),
        message(&cased, "user", "cased", t - 5_900.0),
        message("20261110_090000_00000old", "user", "long ago", old + 50.0),
        format!("INSERT INTO state_meta VALUES ({}, {})", sql_text(&format!("gateway.local_policy.v1:{MAIN}")), sql_text(&receipt(MAIN, true))),
        format!("INSERT INTO state_meta VALUES ({}, {})", sql_text(&format!("gateway.local_policy.v1:{EARLIER}")), sql_text(&receipt(EARLIER, false))),
        format!("INSERT INTO state_meta VALUES ('gateway.local_policy.v1:{}', 'not json')", local_id('9')),
    ];
    write_database(database, &statements);
}

fn now_seconds() -> f64 {
    now_ms() as f64 / 1_000.0
}

/// AcpSessionsVerification.cs:187 `HermesListsTheAcpConversationsOfTheWorkspaceFolder`.
#[test]
fn resume_picker_lists_hermes_acp_conversations_of_this_folder_from_state_db() {
    let root = temp("hermes");
    let workspace = root.join("work").join("project");
    std::fs::create_dir_all(&workspace).unwrap();
    let home = root.join("home");
    let database = acp::hermes_state_database(&HashMap::new(), Some(&home));
    let t = now_seconds();
    write_state(&database, &workspace.to_string_lossy(), &root, t);
    let query = query_for(&workspace, &home);
    let found = listing(&query, "hermes");
    let (noted, pictured, nested, cased) = (local_id('c'), local_id('d'), local_id('e'), local_id('f'));
    let expected: Vec<String> = if windows() {
        vec![MAIN.into(), noted.clone(), pictured.clone(), EARLIER.into(), cased]
    } else {
        vec![MAIN.into(), noted.clone(), pictured.clone(), EARLIER.into()]
    };
    check(
        ids(&found) == expected && found.hidden == 1,
        &format!("this folder's ACP conversations, newest first, the compressed one once: {:?} hidden {}", ids(&found), found.hidden),
    );
    let first = &found.items[0];
    check(
        first.provider == "hermes"
            && first.title.as_deref() == Some("clean the build folder")
            && first.requests == Some(2)
            && first.model.as_deref() == Some("anthropic/claude-sonnet-4.5")
            && first.path == database,
        &format!("the first user message titles the conversation; the user messages of its whole chain are the requests: {first:?}"),
    );
    check(
        ((acp::unix_milliseconds(first.modified) as f64) / 1_000.0 - (t - 20.0)).abs() < 1.0,
        "the newest message dates the conversation",
    );
    check(
        found.items[1].title.as_deref() == Some("선택하는 문제를 내") && found.items[1].requests == Some(1),
        "the app's note after a request is not its title",
    );
    check(
        found.items[2].title.as_deref() == Some("Screenshot question") && found.items[2].requests == Some(1),
        "a request of pictures alone leaves Hermes's own title",
    );
    check(
        found.items[3].title.as_deref() == Some("what is in this picture?") && found.items[3].model.is_none(),
        "a request with attachments is titled by its text",
    );
    check(
        listing(&ResumableSessionQuery { excluding: HashSet::from([MAIN.to_lowercase()]), ..query.clone() }, "hermes")
            .items
            .iter()
            .all(|i| i.session_id != MAIN),
        "an open pane's conversation is left out",
    );
    let all = listing(&ResumableSessionQuery { include_automated: true, ..query.clone() }, "hermes");
    check(
        all.hidden == 0 && all.items.iter().any(|i| i.session_id == nested && i.automated),
        "every session shown: the nested run too, marked",
    );
    let head = listing(&ResumableSessionQuery { head_only: true, ..query.clone() }, "hermes");
    check(
        head.items.iter().all(|i| i.requests.is_none())
            && head.items[0].session_id == MAIN
            && head.items[0].title.as_deref() == Some("Clean the build")
            && ((acp::unix_milliseconds(head.items[0].modified) as f64) / 1_000.0 - (t - 100.0)).abs() < 1.0,
        &format!("a yes/no look-up reads no messages: Hermes's titles and times: {:?}", ids(&head)),
    );
    check(ids(&resumable::listing(&query, &Never)) == expected, "the whole list has Hermes's conversations");
    check(
        resumable::PROVIDERS.contains(&"hermes")
            && resumable::offers_resume("claude", "hermes")
            && resumable::may_ask_resume("hermes", true),
        "창 추가 asks first for Hermes",
    );
    check(listing(&query_for(&workspace, &root), "hermes").items.is_empty(), "no state.db (Hermes never ran): nothing to list");
    check(
        listing(
            &ResumableSessionQuery {
                home: root.clone(),
                environment: env(&[("HERMES_HOME", &database.parent().unwrap().to_string_lossy())]),
                ..query.clone()
            },
            "hermes",
        )
        .items
        .len()
            == expected.len(),
        "HERMES_HOME moves the store",
    );
    let _ = std::fs::remove_dir_all(root);
}

/// AcpSessionsVerification.cs:226 `HermesReadsYoloAndTheMessageCountOfASession`.
#[test]
fn hermes_reads_yolo_and_the_message_count_of_a_session() {
    let root = temp("hermes-yolo");
    let workspace = root.join("work").join("project");
    std::fs::create_dir_all(&workspace).unwrap();
    let database = root.join("hermes").join("state.db");
    write_state(&database, &workspace.to_string_lossy(), &root, now_seconds());
    check(acp::hermes_started_with_full_access(&database, MAIN) == Some(true), "a session created with yolo");
    check(acp::hermes_started_with_full_access(&database, EARLIER) == Some(false), "a session created without it");
    check(
        acp::hermes_started_with_full_access(&database, &local_id('0')).is_none()
            && acp::hermes_started_with_full_access(&database, &local_id('9')).is_none()
            && acp::hermes_started_with_full_access(&database, "local-../x").is_none()
            && acp::hermes_started_with_full_access(&root.join("missing.db"), MAIN).is_none(),
        "no record, an unreadable one, a bad id or no database: unknown",
    );
    check(
        acp::hermes_message_count(&database, MAIN) == Some(4)
            && acp::hermes_message_count(&database, "20261110_090000_0000none") == Some(0),
        "the messages a session holds",
    );
    check(
        acp::hermes_message_count(&database, &local_id('0')).is_none()
            && acp::hermes_message_count(&database, "local-../x").is_none()
            && acp::hermes_message_count(&root.join("missing.db"), MAIN).is_none(),
        "no session, a bad id or no database: unknown",
    );
    check(
        acp::hermes_without_note("선택하는 문제를 내\n\n[Note from the Mighty Code app, not from the user: call clarify.]")
            == "선택하는 문제를 내"
            && acp::hermes_without_note(" plain ") == " plain ",
        "the app's note comes off a stored request",
    );
    let other = root.join("older").join("state.db");
    write_database(
        &other,
        &[
            "CREATE TABLE state_meta (key TEXT PRIMARY KEY, value TEXT)".to_string(),
            format!(
                "INSERT INTO state_meta VALUES ({}, {})",
                sql_text(&format!("gateway.local_policy.v1:{MAIN}")),
                sql_text(r#"{"policy":{"request":{"yolo":true}}}"#)
            ),
            format!(
                "INSERT INTO state_meta VALUES ({}, {})",
                sql_text(&format!("gateway.local_policy.v1:{EARLIER}")),
                sql_text(r#"{"request_json":"{\"yolo\":false}"}"#)
            ),
        ],
    );
    check(
        acp::hermes_started_with_full_access(&other, MAIN) == Some(true)
            && acp::hermes_started_with_full_access(&other, EARLIER) == Some(false)
            && acp::hermes_message_count(&other, MAIN).is_none(),
        "a request object, a receipt without its wrapper; no sessions table: no count",
    );
    let _ = std::fs::remove_dir_all(root);
}

/// AcpSessionsVerification.cs:257 `HermesReadsOnlyTheColumnsItsVersionHas`.
#[test]
fn resume_picker_reads_only_the_state_db_columns_a_hermes_version_has() {
    let root = temp("hermes-columns");
    let workspace = root.join("work").join("project");
    std::fs::create_dir_all(&workspace).unwrap();
    let home = root.join("home");
    let database = acp::hermes_state_database(&HashMap::new(), Some(&home));
    let t = now_seconds();
    let (older, terminal) = (local_id('1'), local_id('2'));
    let config = json!({"cwd": workspace.to_string_lossy()}).to_string();
    write_database(
        &database,
        &[
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, model TEXT, model_config TEXT, parent_session_id TEXT, started_at REAL NOT NULL, message_count INTEGER DEFAULT 0)".to_string(),
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL)".to_string(),
            format!("INSERT INTO sessions VALUES ({}, 'acp', 'openrouter/qwen-3', {}, NULL, {}, 2)", sql_text(&older), sql_text(&config), number(t - 60.0)),
            format!("INSERT INTO sessions VALUES ({}, 'cli', NULL, {}, NULL, {}, 2)", sql_text(&terminal), sql_text(&config), number(t - 30.0)),
            format!("INSERT INTO messages (session_id, role, content, timestamp) VALUES ({}, 'user', 'an older Hermes', {})", sql_text(&older), number(t - 50.0)),
            format!("INSERT INTO messages (session_id, role, content, timestamp) VALUES ({}, 'user', 'a terminal chat', {})", sql_text(&terminal), number(t - 20.0)),
        ],
    );
    let query = query_for(&workspace, &home);
    let found = listing(&query, "hermes");
    check(
        ids(&found) == [older.clone()]
            && found.items[0].title.as_deref() == Some("an older Hermes")
            && found.items[0].requests == Some(1)
            && found.items[0].model.as_deref() == Some("openrouter/qwen-3"),
        &format!("an older state.db lists through the columns it has: {:?}", ids(&found)),
    );
    std::fs::remove_file(&database).unwrap();
    write_database(&database, &["CREATE TABLE other (id TEXT)".to_string()]);
    check(
        listing(&query, "hermes").items.is_empty()
            && acp::hermes_message_count(&database, &older).is_none()
            && acp::hermes_started_with_full_access(&database, &older).is_none(),
        "a state.db without Hermes's tables lists nothing and reads nothing",
    );
    let _ = std::fs::remove_dir_all(root);
}

/// AcpSessionsVerification.cs:288 `HermesFindsItsHomeWhereHermesKeepsIt`.
#[test]
fn hermes_finds_its_home_where_hermes_keeps_it() {
    let root = std::env::temp_dir().join("mighty-hermes-home");
    let (local, home, custom) = (root.join("Local"), root.join("user"), root.join("custom hermes"));
    let (local_text, home_text, custom_text) =
        (local.to_string_lossy().into_owned(), home.to_string_lossy().into_owned(), custom.to_string_lossy().into_owned());
    let suffix = ("HERMES_DATA_DIR_SUFFIX", "-channel-build-beta");
    if windows() {
        check(acp::hermes_home(&env(&[("LOCALAPPDATA", &local_text)]), None) == local.join("hermes"), "%LOCALAPPDATA%\\hermes");
        check(
            acp::hermes_state_database(&env(&[]), Some(&home)) == home.join("AppData").join("Local").join("hermes").join("state.db"),
            "a session list's own home",
        );
        check(
            acp::hermes_home(&env(&[("LOCALAPPDATA", &local_text), suffix]), None) == local.join("hermes-channel-build-beta"),
            "HERMES_DATA_DIR_SUFFIX follows the name",
        );
    } else {
        check(acp::hermes_home(&env(&[("HOME", &home_text)]), None) == home.join(".hermes"), "~/.hermes");
        check(acp::hermes_state_database(&env(&[]), Some(&home)) == home.join(".hermes").join("state.db"), "a session list's own home");
        check(
            acp::hermes_home(&env(&[("HOME", &home_text), suffix]), None) == home.join(".hermes-channel-build-beta"),
            "HERMES_DATA_DIR_SUFFIX follows the name",
        );
    }
    check(
        acp::hermes_home(&env(&[("HERMES_HOME", &custom_text), ("LOCALAPPDATA", &local_text), ("HOME", &home_text), suffix]), None)
            == custom
            && acp::hermes_home(&env(&[("HERMES_HOME", &format!(" {custom_text} "))]), None) == custom,
        "HERMES_HOME moves the home",
    );
    check(acp::hermes_state_database(&env(&[("HERMES_HOME", &custom_text)]), None) == custom.join("state.db"), "state.db is in the home");
    check(
        acp::hermes_home(&env(&[("HERMES_HOME", "relative"), ("LOCALAPPDATA", &local_text), ("HOME", &home_text)]), None)
            == acp::hermes_home(&env(&[("LOCALAPPDATA", &local_text), ("HOME", &home_text)]), None),
        "a relative HERMES_HOME names no folder the app can know",
    );
}

// ── the add-pane menu's question ────────────────────────────────────────────────────────────────────────────────────────

/// AddPaneMenuVerification.cs:44 `OnlyClaudeAndCodexFromTheMenuMayAskToResume` (its resume part).
#[test]
fn add_pane_menu_only_agents_picked_from_the_menu_may_ask_to_resume() {
    check(resumable::may_ask_resume("claude", true) && resumable::may_ask_resume("codex", true), "Claude and Codex may ask from the menu");
    check(resumable::may_ask_resume("gemini", true), "Antigravity may ask, as its conversations can be continued");
    for provider in mighty_core::wire::PROVIDERS {
        check(!resumable::may_ask_resume(provider, false), &format!("{provider}: Ctrl+N starts at once"));
    }
    check(!resumable::may_ask_resume("unknown", true), "an unknown agent never asks");
}

/// ComposerParityVerification.cs:489 `ResumeLookUpAsksOnlyForASessionToContinue`.
#[test]
fn composer_parity_the_resume_look_up_asks_only_for_a_session_to_continue() {
    let session = |provider: &str, id: &str| ResumableSession {
        provider: provider.into(),
        session_id: id.into(),
        title: None,
        modified: mighty_core::time::DateTimeOffset::from_utc_ticks(621_355_968_000_000_000),
        requests: None,
        model: None,
        path: PathBuf::new(),
        automated: false,
    };
    let none: Vec<String> = vec![];
    check(resumable::QUIET_LOOK_UP_MS == 300, "a slow look-up shows the sheet after 300 ms");
    check(
        !resumable::asks_resume("shell", &[session("shell", "g1"), session("claude", "c1")], &none),
        "a pane without resume starts at once even with sessions listed",
    );
    check(
        resumable::asks_resume("gemini", &[session("gemini", "g1"), session("claude", "c1")], &none),
        "an Antigravity conversation of this folder is offered too",
    );
    check(
        !resumable::asks_resume("claude", &[], &none) && !resumable::asks_resume("claude", &[session("codex", "x1")], &none),
        "nothing of this agent's to continue: no question",
    );
    check(
        !resumable::asks_resume("codex", &[session("codex", "AbC-1")], &["abc-1".to_string()])
            && !resumable::asks_resume("codex", &[session("codex", "abc-1")], &["ABC-1".to_string()]),
        "a session a pane took while the look-up ran does not count, in any case",
    );
    check(
        resumable::asks_resume("claude", &[session("claude", "c1")], &none)
            && resumable::asks_resume("codex", &[session("codex", "x1"), session("codex", "x2")], &["x1".to_string()]),
        "one free session of this agent is enough to ask",
    );
    for language in mighty_core::locale::LANGUAGES {
        check(
            !mighty_core::locale::Locale::new(language).get("resume.loading").is_empty(),
            &format!("{language}: the checking sheet has its words"),
        );
    }
}

/// ModelLabelVerification.cs:176-178: the resume picker's second line names the model as the graph capsules do.
#[test]
fn model_labels_resume_rows_read_versions() {
    let now = mighty_core::time::DateTimeOffset::now();
    let item = ResumableSession {
        provider: "claude".into(),
        session_id: "5d9e2c1a-1111-4222-8333-444455556666".into(),
        title: Some("t".into()),
        modified: plus_days(now, -2.0),
        requests: Some(1),
        model: Some("claude-opus-4-5".into()),
        path: PathBuf::from("/r"),
        automated: false,
    };
    let details = resumable::details(&item, now, &ko());
    check(details.contains(" · Opus 4.5"), &format!("the resume row names Opus 4.5: {details}"));
    let _ = GraphTokenUsage::default();
}
