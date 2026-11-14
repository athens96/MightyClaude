//! C# CodexSessionWatcherVerification (MightyCode.Core.Tests/CodexSessionWatcherVerification.cs): a Codex run's sub-agents read from
//! the session files under a temporary `CODEX_HOME`, merged into the live graph by the tracker. The running process's check
//! (`RunningProcessPublishesGraphBeforeItExits`) is mighty-run's tests/live_graph.rs.

mod common;

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use mighty_core::locale::Locale;
use mighty_core::model::LogEntry;
use mighty_core::time::DateTimeOffset;
use mighty_graph::ExecutionGraphTracker;
use mighty_history::codex_sessions::{CodexSessionWatcher, Limits};
use serde_json::{Value, json};

const ROOT: &str = "10000000-0000-4000-8000-000000000001";
const CHILD: &str = "10000000-0000-4000-8000-000000000002";
const GRANDCHILD: &str = "10000000-0000-4000-8000-000000000003";
const OTHER: &str = "10000000-0000-4000-8000-000000000004";

/// `Started`: 2026-10-01 12:00 UTC.
fn started() -> DateTimeOffset {
    common::at("2026-10-01T12:00:00Z")
}

fn record(kind: &str, payload: Value, ordinal: i64, at: Option<DateTimeOffset>) -> String {
    json!({"timestamp": at.unwrap_or_else(started).to_o(), "ordinal": ordinal, "type": kind, "payload": payload}).to_string()
}

fn metadata(id: &str, parent: &str, fork: bool, ordinal: Option<i64>, guardian: bool) -> String {
    let source = if guardian { json!({"subagent": "review"}) } else { json!({"subagent": {"thread_spawn": {"parent_thread_id": parent}}}) };
    record(
        "session_meta",
        json!({
            "id": id, "parent_thread_id": parent, "agent_nickname": "Worker", "agent_path": "/root/worker",
            "forked_from_id": if fork { Value::String(parent.into()) } else { Value::Null },
            "subagent_history_start_ordinal": ordinal, "source": source,
        }),
        10,
        None,
    )
}

fn meta(id: &str, parent: &str) -> String {
    metadata(id, parent, false, None, false)
}

fn start(ordinal: i64, at: Option<DateTimeOffset>) -> String {
    record("event_msg", json!({"type": "task_started"}), ordinal, at)
}

fn done(answer: &str, ordinal: i64) -> String {
    record("event_msg", json!({"type": "task_complete", "last_agent_message": answer}), ordinal, None)
}

fn message(text: &str, id: &str, ordinal: i64) -> String {
    record(
        "event_msg",
        json!({"type": "item_completed", "item": {"type": "AgentMessage", "id": id, "phase": "commentary", "content": [{"type": "Text", "text": text}]}}),
        ordinal,
        None,
    )
}

fn agent_message(text: &str, id: Option<&str>, ordinal: i64) -> String {
    let mut payload = json!({"type": "agent_message", "recipient": "/root/worker", "content": [{"type": "input_text", "text": text}]});
    if let Some(id) = id {
        payload["id"] = json!(id);
    }
    record("response_item", payload, ordinal, None)
}

/// `CodexSessionWatcherVerification.Fixture`: a Codex home with today's session folder.
struct Fixture {
    home: PathBuf,
    folder: PathBuf,
}

impl Fixture {
    fn new() -> Fixture {
        let home = common::temp("codex-sessions");
        let day = started().to_chrono().with_timezone(&chrono::Local).date_naive();
        let folder =
            home.join("sessions").join(day.format("%Y").to_string()).join(day.format("%m").to_string()).join(day.format("%d").to_string());
        std::fs::create_dir_all(&folder).unwrap();
        Fixture { home, folder }
    }
    fn write(&self, id: &str, lines: &[String]) -> PathBuf {
        let path = self.folder.join(format!("rollout-2026-10-01T12-00-00-{id}.jsonl"));
        std::fs::write(&path, format!("{}\n", lines.join("\n"))).unwrap();
        common::set_modified(&path, common::plus_seconds(started(), 3.0));
        path
    }
    fn watch(&self, limits: Option<Limits>) -> CodexSessionWatcher {
        let now = common::plus_seconds(started(), 300.0);
        CodexSessionWatcher::new(&self.home, ROOT, started(), "watch-run", Some(Box::new(move || now)), limits).expect("the watcher")
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.home);
    }
}

fn append(path: &Path, text: &str) {
    use std::io::Write;
    std::fs::OpenOptions::new().append(true).open(path).unwrap().write_all(text.as_bytes()).unwrap();
}

fn tracker(input: &str) -> ExecutionGraphTracker {
    let mut tracker = ExecutionGraphTracker::new("watch-run", Some(input), "codex", None, Box::new(|_| {}), None, None, Locale::new("ko"));
    tracker.consume(&json!({"type": "thread.started", "thread_id": ROOT}));
    tracker
}

fn texts(entries: &Option<Vec<LogEntry>>) -> Vec<String> {
    entries.iter().flatten().filter_map(|e| e.text.clone()).collect()
}

/// CodexSessionWatcherVerification.cs:34 `NestedChildrenStayScopedAndPublishLiveModels`.
#[test]
fn nested_children_stay_scoped_and_publish_live_models() {
    let f = Fixture::new();
    f.write(GRANDCHILD, &[meta(GRANDCHILD, CHILD), start(10, None), message("nested reasoning", "note", 15), done("nested result", 20)]);
    f.write(OTHER, &[meta(OTHER, "10000000-0000-4000-8000-0000000000ff"), start(10, None), done("unrelated secret", 20)]);
    f.write(
        CHILD,
        &[
            meta(CHILD, ROOT),
            start(10, Some(common::plus_hours(started(), -1.0))),
            message("old private answer", "old-message", 15),
            done("old answer", 20),
            start(10, None),
            record("turn_context", json!({"model": "gpt-fixture"}), 10, None),
            agent_message("Inspect Windows", Some("input"), 10),
            record(
                "token_usage_record",
                json!({"response_id": "response-1", "usage": {"input_tokens": 20, "output_tokens": 4, "cached_input_tokens": 8}}),
                10,
                None,
            ),
            record(
                "token_usage_record",
                json!({"response_id": "response-1", "usage": {"input_tokens": 20, "output_tokens": 4, "cached_input_tokens": 8}}),
                10,
                None,
            ),
            message("working now", "note", 15),
        ],
    );
    let mut watcher = f.watch(None);
    let updates = watcher.poll();
    assert!(
        updates.len() == 2 && updates.iter().all(|a| a.thread != OTHER),
        "only proven root descendants enter this run, even when grandchild is discovered first; found {}, headers {}, folder {}",
        updates.len(),
        watcher.first_line_reads,
        f.folder.display()
    );
    let child = updates.iter().find(|a| a.thread == CHILD).unwrap();
    assert!(
        child.input.as_deref() == Some("Inspect Windows")
            && child.turns == 1
            && child.usage.len() == 1
            && child.usage[0].model.as_deref() == Some("gpt-fixture"),
        "fresh input and per-response model usage are deduplicated: {child:?}"
    );
    assert!(!child.entries.iter().any(|e| e.text.as_deref().is_some_and(|t| t.contains("old"))), "pre-run transcripts are never replayed");
    let mut tracker = tracker("root prompt");
    for update in &updates {
        tracker.codex_session(update);
    }
    let graph = tracker.build_run(true).unwrap();
    let agents = graph.agents.clone().unwrap_or_default();
    let nested = agents.iter().find(|a| texts(&a.entries).iter().any(|t| t == "nested result"));
    let worker = agents.iter().find(|a| a.input.as_deref() == Some("Inspect Windows"));
    assert!(
        graph.status.as_deref() == Some("running")
            && agents.len() == 2
            && nested.is_some_and(|n| n.parent_id == worker.and_then(|w| w.id.clone())),
        "live graph preserves nested parents before Finish: {agents:?}"
    );
    assert!(watcher.poll().is_empty(), "unchanged poll emits no duplicate transcript or usage");
}

/// CodexSessionWatcherVerification.cs:58 `ForksDoNotExposeInheritedHistoryOrSealedMessages`.
#[test]
fn forks_do_not_expose_inherited_history_or_sealed_messages() {
    let f = Fixture::new();
    f.write(
        CHILD,
        &[
            metadata(CHILD, ROOT, true, Some(50), false),
            start(10, None),
            message("copied parent secret", "note", 11),
            start(50, None),
            message("own note", "note", 51),
            agent_message("Sender: parent\nMessage:\n", None, 52),
            agent_message("gAAAAsealed", None, 53),
            done("own result", 54),
        ],
    );
    f.write(
        GRANDCHILD,
        &[
            metadata(GRANDCHILD, CHILD, true, None, false),
            start(10, None),
            message("copied history", "note", 15),
            record("event_msg", json!({"type": "thread_settings_applied"}), 10, None),
            start(10, None),
            message("must stay private", "note", 15),
            done("must stay hidden", 20),
        ],
    );
    let mut watcher = f.watch(None);
    let updates = watcher.poll();
    let child = updates.iter().find(|a| a.thread == CHILD).expect("the child");
    let fork = updates.iter().find(|a| a.thread == GRANDCHILD).expect("the fork");
    assert!(
        child.input.is_none()
            && child.output.as_deref() == Some("own result")
            && child.entries.len() == 1
            && child.entries[0].text.as_deref() == Some("own note"),
        "ordinal boundary, sealed body and envelope header are honored: {child:?}"
    );
    assert!(
        fork.state == "completed" && fork.output.is_none() && fork.input.is_none() && fork.entries.is_empty() && fork.usage.is_empty(),
        "legacy forks without trustworthy ordinals expose state only: {fork:?}"
    );
}

/// CodexSessionWatcherVerification.cs:75 `PartialOversizedAndTruncatedRecordsRemainBounded`.
#[test]
fn partial_oversized_and_truncated_records_remain_bounded() {
    let f = Fixture::new();
    let path = f.write(CHILD, &[meta(CHILD, ROOT), start(10, None)]);
    let mut watcher = f.watch(Some(Limits { maximum_line_bytes: 512, ..Limits::default() }));
    let first = watcher.poll();
    assert!(first.len() == 1 && first[0].state == "running", "first complete start is visible");
    let finished = done("한글 result", 20);
    append(&path, &finished[..finished.len() - 1]);
    assert!(watcher.poll().is_empty(), "partial JSON record never consumes its offset");
    append(&path, "}\n");
    let read = watcher.poll();
    assert!(read.len() == 1 && read[0].output.as_deref() == Some("한글 result"), "completed UTF-8 line is read exactly once");
    append(&path, &format!("{}\n{}\n{}\n", "x".repeat(1300), start(30, None), done("after oversized", 31)));
    let after = watcher.poll();
    assert!(
        after.len() == 1 && after[0].output.as_deref() == Some("after oversized"),
        "oversized line is skipped whole and next valid record remains readable"
    );
    std::fs::write(&path, format!("{}\n{}\n{}\n", meta(CHILD, ROOT), start(10, None), done("replacement must not be read", 20))).unwrap();
    assert!(watcher.poll().is_empty(), "truncated adopted file is permanently closed");
    watcher.finish();
    assert!(watcher.poll().is_empty(), "final read closes every later poll");
}

/// CodexSessionWatcherVerification.cs:92 `UntrustedMetadataAndLinksNeverBecomeChildren`.
#[test]
fn untrusted_metadata_and_links_never_become_children() {
    let f = Fixture::new();
    f.write(CHILD, &[meta(OTHER, ROOT), start(10, None), done("wrong filename identity", 20)]);
    f.write(GRANDCHILD, &[metadata(GRANDCHILD, ROOT, false, None, true), start(10, None), done("guardian internal", 20)]);
    let outside = f.home.join("outside.jsonl");
    std::fs::write(&outside, format!("{}\n{}\n{}\n", meta(OTHER, ROOT), start(10, None), done("linked private", 20))).unwrap();
    let link = f.folder.join(format!("rollout-2026-10-01T12-00-00-{OTHER}.jsonl"));
    // Windows refuses a symbolic link without the right to make one: the link half is then skipped, and says so (the junction check
    // below needs no right).
    #[cfg(windows)]
    let linked = std::os::windows::fs::symlink_file(&outside, &link).is_ok();
    #[cfg(unix)]
    let linked = std::os::unix::fs::symlink(&outside, &link).is_ok();
    assert!(linked || cfg!(windows), "a symbolic link is made off Windows");
    if !linked {
        eprintln!("skipped: the symbolic link half (no right to make a symbolic link here)");
    }
    let mut watcher = f.watch(None);
    assert!(watcher.poll().is_empty(), "filename mismatch, guardian sources and symlinks are refused");
    assert!(!linked || std::fs::symlink_metadata(&link).is_ok_and(|m| m.file_type().is_symlink()), "the link is there to refuse");
}

/// Makes `link` a junction to the folder `target` (any Windows account may), or a symbolic link elsewhere; false when it could not.
#[allow(clippy::disallowed_methods, reason = "the check's own mklink, no child of the app")]
fn folder_link(link: &Path, target: &Path) -> bool {
    #[cfg(windows)]
    {
        std::process::Command::new("cmd").arg("/c").arg("mklink").arg("/J").arg(link).arg(target).output().is_ok_and(|o| o.status.success())
    }
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(target, link).is_ok()
    }
}

/// The links half of `UntrustedMetadataAndLinksNeverBecomeChildren` with a link anyone may make: a day folder that is a junction to a
/// folder outside the sessions holds a descendant's rollout, which is never read.
#[test]
fn a_day_folder_that_is_a_link_is_never_read() {
    let f = Fixture::new();
    let outside = f.home.join("outside-day");
    std::fs::create_dir_all(&outside).unwrap();
    let path = outside.join(format!("rollout-2026-10-01T12-00-00-{CHILD}.jsonl"));
    std::fs::write(&path, format!("{}\n{}\n{}\n", meta(CHILD, ROOT), start(10, None), done("through a link", 20))).unwrap();
    common::set_modified(&path, common::plus_seconds(started(), 3.0));
    std::fs::remove_dir(&f.folder).unwrap();
    assert!(folder_link(&f.folder, &outside), "the day folder is a link");
    assert!(f.watch(None).poll().is_empty(), "a linked day folder is not read");
}

/// Takes away a link made by [`folder_link`] (never what it leads to).
fn unlink(link: &Path) {
    std::fs::remove_dir(link).or_else(|_| std::fs::remove_file(link)).unwrap();
}

/// `sessions ??= WorkspaceFiles.RealPath(...)`: a sessions folder whose real path cannot be worked out at a poll (here its link loops)
/// is not kept as it is written, but looked for again; once it leads to a folder (the user moved it elsewhere and linked it), that
/// folder is read.
#[test]
fn a_sessions_folder_that_cannot_be_resolved_yet_is_looked_for_again() {
    let parent = common::temp("codex-late-sessions");
    let home = parent.join("home");
    std::fs::create_dir_all(&home).unwrap();
    let sessions = home.join("sessions");
    let looping = home.join("loop");
    assert!(folder_link(&sessions, &looping) && folder_link(&looping, &sessions), "the sessions folder's link loops");
    let now = common::plus_seconds(started(), 300.0);
    let mut watcher = CodexSessionWatcher::new(&home, ROOT, started(), "watch-run", Some(Box::new(move || now)), None).unwrap();
    assert!(watcher.poll().is_empty(), "nothing to read while the link loops");
    unlink(&sessions);
    unlink(&looping);
    let moved = parent.join("moved-sessions");
    let day = started().to_chrono().with_timezone(&chrono::Local).date_naive();
    let folder = moved.join(day.format("%Y").to_string()).join(day.format("%m").to_string()).join(day.format("%d").to_string());
    std::fs::create_dir_all(&folder).unwrap();
    let path = folder.join(format!("rollout-2026-10-01T12-00-00-{CHILD}.jsonl"));
    std::fs::write(&path, format!("{}\n{}\n", meta(CHILD, ROOT), start(10, None))).unwrap();
    common::set_modified(&path, common::plus_seconds(started(), 3.0));
    assert!(folder_link(&sessions, &moved), "the sessions folder now leads elsewhere");
    let read = watcher.poll();
    assert!(read.len() == 1 && read[0].thread == CHILD, "the child is read from the folder the link leads to: {read:?}");
    drop(watcher);
    unlink(&sessions);
    let _ = std::fs::remove_dir_all(&parent);
}

/// `WorkspaceFiles.OpenFile`'s handle check: a rollout whose open handle is somewhere else than the sessions folder (a name swapped for a
/// link between the look and the open) is not read, whatever its path said; a handle inside it is.
#[test]
fn a_rollout_is_read_only_when_its_open_handle_is_inside_the_sessions_folder() {
    use mighty_platform::fake::FakeFileHandles;

    let f = Fixture::new();
    let path = f.write(CHILD, &[meta(CHILD, ROOT), start(10, None)]);
    let elsewhere = Arc::new(FakeFileHandles::asked(Some(f.home.join("elsewhere.jsonl")), None));
    assert!(f.watch(None).with_file_handles(elsewhere).poll().is_empty(), "a handle outside the sessions folder is refused");
    let real = mighty_core::workspace_files::real_path(&path).unwrap();
    let inside = Arc::new(FakeFileHandles::asked(Some(real), None));
    assert_eq!(f.watch(None).with_file_handles(inside).poll().len(), 1, "a handle inside it is read");
    let not_disk = Arc::new(FakeFileHandles { asks: true, not_disk: true, ..FakeFileHandles::default() });
    assert!(f.watch(None).with_file_handles(not_disk).poll().is_empty(), "a handle that is no disk file is refused");
}

/// Handles that count the files opened (each open asks its handle once) and place every one at `path`.
struct Counting {
    opened: AtomicUsize,
    path: PathBuf,
}

impl mighty_platform::FileHandles for Counting {
    fn asks_handles(&self) -> bool {
        true
    }
    fn is_disk(&self, _file: &std::fs::File) -> bool {
        true
    }
    fn final_path(&self, _file: &std::fs::File) -> Option<PathBuf> {
        self.opened.fetch_add(1, Ordering::SeqCst);
        Some(self.path.clone())
    }
    fn final_directory_path(&self, path: &Path) -> Option<PathBuf> {
        Some(path.to_path_buf())
    }
}

/// A rollout last written before the run started is no sub-agent of it: its time is read from the folder's listing, so it is never
/// opened (a thousand old rollouts cost no open each second).
#[test]
fn a_rollout_older_than_the_run_is_never_opened() {
    let f = Fixture::new();
    let path = f.write(CHILD, &[meta(CHILD, ROOT), start(10, None)]);
    common::set_modified(&path, common::plus_seconds(started(), -60.0));
    let counting = Arc::new(Counting { opened: AtomicUsize::new(0), path: mighty_core::workspace_files::real_path(&path).unwrap() });
    let mut watcher = f.watch(None).with_file_handles(counting.clone());
    assert!(watcher.poll().is_empty() && watcher.poll().is_empty(), "an older rollout is no child");
    assert_eq!(counting.opened.load(Ordering::SeqCst), 0, "and is never opened");
    common::set_modified(&path, common::plus_seconds(started(), 5.0));
    assert!(watcher.poll().len() == 1 && counting.opened.load(Ordering::SeqCst) == 1, "once written in the run it is opened and read");
}

/// CodexSessionRecords.cs:134: an MCP call's name is `string.Join('.', server, tool)` over the names it has (an empty one kept), and
/// "MCP" only when that is empty.
#[test]
fn an_mcp_calls_name_joins_its_server_and_tool_as_csharp_does() {
    let f = Fixture::new();
    let call = |id: &str, server: Option<&str>, tool: Option<&str>, ordinal: i64| {
        let mut item = json!({"type": "McpToolCall", "id": id, "status": "completed", "arguments": {}, "result": {"content": []}});
        if let Some(server) = server {
            item["server"] = json!(server);
        }
        if let Some(tool) = tool {
            item["tool"] = json!(tool);
        }
        record("event_msg", json!({"type": "item_completed", "item": item}), ordinal, None)
    };
    f.write(
        CHILD,
        &[
            meta(CHILD, ROOT),
            start(10, None),
            call("a", Some(""), None, 11),
            call("b", None, None, 12),
            call("c", Some(""), Some("lookup"), 13),
            call("d", Some("docs"), Some("search"), 14),
        ],
    );
    let read = f.watch(None).poll();
    let names: Vec<String> = read[0].entries.iter().filter_map(|e| e.activity.as_ref().and_then(|a| a.tool_name.clone())).collect();
    assert_eq!(names, ["MCP", "MCP", ".lookup", "docs.search"], "the calls' names");
}

/// CodexSessionWatcherVerification.cs:105 `ReusedChildKeepsPreviousAnswersAndResponseUsageOnce`.
#[test]
fn reused_child_keeps_previous_answers_and_response_usage_once() {
    let f = Fixture::new();
    let path = f.write(CHILD, &[meta(CHILD, ROOT), start(10, None), done("first answer", 20)]);
    let mut watcher = f.watch(None);
    let mut tracker = tracker("request");
    for child in watcher.poll() {
        tracker.codex_session(&child);
    }
    append(&path, &format!("{}\n{}\n", start(30, None), record("event_msg", json!({"type": "error", "message": "tool failed"}), 31, None)));
    for child in watcher.poll() {
        tracker.codex_session(&child);
    }
    let resumed = tracker.build_run(true).unwrap().agents.unwrap_or_default();
    assert!(
        resumed.len() == 1
            && resumed[0].status.as_deref() == Some("running")
            && resumed[0].activity_generation == Some(1)
            && texts(&resumed[0].entries).iter().filter(|t| *t == "first answer").count() == 1,
        "reopened child increments generation and preserves one prior answer: {resumed:?}"
    );
    for child in watcher.finish() {
        tracker.codex_session(&child);
    }
    let settled = tracker.build_run(true).unwrap().agents.unwrap_or_default();
    assert_eq!(settled[0].status.as_deref(), Some("error"), "unfinished child with actual error settles as failed");
}
