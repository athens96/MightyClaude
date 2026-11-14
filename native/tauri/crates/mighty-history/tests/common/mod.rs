//! What the history checks share (C# `SessionHistoryVerification.Fixture`, `ClaudeUser`, `ClaudeAnswer`, `Stamp`, `Verification.Temp`):
//! a temporary home and workspace with records written as the CLIs write them, and the times they carry. Every check reads Korean, as
//! C#'s `Verification.RunAsync` pins it.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use mighty_core::locale::Locale;
use mighty_core::time::DateTimeOffset;
use serde_json::{Value, json};

pub fn ko() -> Locale {
    Locale::new("ko")
}

const TICKS_PER_SECOND: i64 = 10_000_000;
const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

/// `new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)`.
pub fn base() -> DateTimeOffset {
    mighty_core::time::parse(Some("2026-10-01T00:00:00Z")).expect("the base time")
}

pub fn at(text: &str) -> DateTimeOffset {
    mighty_core::time::parse(Some(text)).unwrap_or_else(|| panic!("{text} is no time"))
}

pub fn plus_seconds(date: DateTimeOffset, seconds: f64) -> DateTimeOffset {
    DateTimeOffset::from_utc_ticks(date.utc_ticks() + (seconds * TICKS_PER_SECOND as f64) as i64)
}

pub fn plus_hours(date: DateTimeOffset, hours: f64) -> DateTimeOffset {
    plus_seconds(date, hours * 3_600.0)
}

pub fn plus_days(date: DateTimeOffset, days: f64) -> DateTimeOffset {
    plus_seconds(date, days * 86_400.0)
}

/// `Stamp`: `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`.
pub fn stamp(date: DateTimeOffset) -> String {
    date.to_chrono().format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string()
}

pub fn system_time(date: DateTimeOffset) -> SystemTime {
    UNIX_EPOCH + Duration::from_nanos(((date.utc_ticks() - UNIX_EPOCH_TICKS) * 100) as u64)
}

/// `File.SetLastWriteTimeUtc`.
pub fn set_modified(path: &Path, date: DateTimeOffset) {
    std::fs::File::options().write(true).open(path).and_then(|f| f.set_modified(system_time(date))).unwrap_or_else(|e| panic!("{e}"));
}

/// A new empty folder of this check's own (`Verification.Temp`).
pub fn temp(name: &str) -> PathBuf {
    // The count keeps two checks of one name apart when they read the same clock (tests run in parallel).
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let count = NEXT.fetch_add(1, Ordering::Relaxed);
    let path = std::env::temp_dir().join(format!("mighty-history-{name}-{}-{nanos}-{count}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    path
}

/// `SessionHistoryVerification.Fixture`: a home and a workspace folder under a temporary root, removed at the end.
pub struct Fixture {
    pub root: PathBuf,
}

impl Fixture {
    pub fn new(name: &str) -> Fixture {
        let fixture = Fixture { root: temp(name) };
        std::fs::create_dir_all(fixture.home()).unwrap();
        std::fs::create_dir_all(fixture.workspace()).unwrap();
        fixture
    }

    pub fn home(&self) -> PathBuf {
        self.root.join("home")
    }

    pub fn workspace(&self) -> PathBuf {
        self.root.join("work").join("project")
    }

    pub fn workspace_text(&self) -> String {
        self.workspace().to_string_lossy().into_owned()
    }

    /// A Claude record of `workspace` (the fixture's own by default).
    pub fn claude_record(&self, id: &str, lines: &[String], workspace: Option<&str>, modified: Option<DateTimeOffset>) -> PathBuf {
        let folder = self
            .home()
            .join(".claude")
            .join("projects")
            .join(mighty_history::history::claude_project_folder(workspace.unwrap_or(&self.workspace_text())));
        std::fs::create_dir_all(&folder).unwrap();
        let path = folder.join(format!("{id}.jsonl"));
        std::fs::write(&path, lines.join("\n") + "\n").unwrap();
        if let Some(date) = modified {
            set_modified(&path, date);
        }
        path
    }

    /// A Codex rollout in `sessions/2026/10/01`.
    pub fn codex_record(&self, thread: &str, lines: &[String], modified: Option<DateTimeOffset>) -> PathBuf {
        let folder = self.home().join(".codex").join("sessions").join("2026").join("10").join("01");
        std::fs::create_dir_all(&folder).unwrap();
        let path = folder.join(format!("rollout-2026-10-01T10-00-00-{thread}.jsonl"));
        std::fs::write(&path, lines.join("\n") + "\n").unwrap();
        if let Some(date) = modified {
            set_modified(&path, date);
        }
        path
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// `ClaudeUser(text, at, uuid, cwd, extra)`.
pub fn claude_user(text: &str, date: DateTimeOffset, uuid: &str, cwd: &str, extra: Value) -> String {
    let mut line =
        json!({"type": "user", "message": {"role": "user", "content": text}, "uuid": uuid, "timestamp": stamp(date), "cwd": cwd});
    if let (Some(object), Value::Object(more)) = (line.as_object_mut(), extra) {
        for (key, value) in more {
            object.insert(key, value);
        }
    }
    line.to_string()
}

/// `ClaudeAnswer(text, at, uuid)`.
pub fn claude_answer(text: &str, date: DateTimeOffset, uuid: &str) -> String {
    json!({"type": "assistant", "message": {"id": format!("msg-{uuid}"), "role": "assistant", "model": "claude-opus-4-5",
        "content": [{"type": "text", "text": text}]}, "uuid": uuid, "timestamp": stamp(date)})
    .to_string()
}

/// `ClaudeConversation`: requests an hour apart, each answered 5 s later.
pub fn claude_conversation(cwd: &str, count: usize) -> Vec<String> {
    let mut lines = vec![];
    for i in 1..=count {
        let date = plus_hours(base(), i as f64);
        lines.push(claude_user(&format!("request {i}"), date, &format!("u{i}"), cwd, Value::Null));
        lines.push(claude_answer(&format!("answer {i}"), plus_seconds(date, 5.0), &format!("a{i}")));
    }
    lines
}

pub fn check(value: bool, why: &str) {
    assert!(value, "{why}");
}
