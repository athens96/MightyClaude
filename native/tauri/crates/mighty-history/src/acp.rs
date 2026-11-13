//! The ACP agents' session stores (C# `OpencodeSessions`, `HermesSessions`, AcpSessions.cs): where OpenCode 2 keeps `opencode.db` and
//! Hermes Agent its `state.db`, the root sessions / ACP conversations of a folder the session list shows, and the two facts a Hermes run
//! reads before it continues a session. Read-only: the app never writes either database.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use mighty_core::time::DateTimeOffset;
use mighty_core::{normalize, wire};
use serde_json::Value;

use crate::{Cancel, sqlite};

fn absolute(path: &str) -> bool {
    path.starts_with('/') || normalize::fully_qualified(path)
}

/// `ActivitySupport.Clean(value, limit, singleLine: true)`.
fn clean(value: Option<&str>, limit: usize) -> String {
    mighty_run::activity::clean(value, limit, true)
}

fn json_object<T>(json: Option<&str>, read: impl FnOnce(&Value) -> Option<T>) -> Option<T> {
    let value: Value = serde_json::from_str(json?).ok()?;
    value.is_object().then(|| read(&value)).flatten()
}

/// A row as both stores give it: the id, the store's own title ("" when none), the last change, the user messages (None when not
/// counted), the first one's text and the model.
#[derive(Debug, Clone, PartialEq)]
pub struct Row {
    pub id: String,
    pub title: String,
    pub updated: DateTimeOffset,
    pub requests: Option<i32>,
    pub prompt: Option<String>,
    pub model: Option<String>,
}

fn from_unix_milliseconds(ms: i64) -> DateTimeOffset {
    DateTimeOffset::from_utc_ticks(621_355_968_000_000_000 + ms * 10_000)
}

/// Milliseconds since 1970 of a time.
pub fn unix_milliseconds(date: DateTimeOffset) -> i64 {
    (date.utc_ticks() - 621_355_968_000_000_000).div_euclid(10_000)
}

// ── opencode ────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `OpencodeSessions.DataDirectory`: `$XDG_DATA_HOME/opencode` when that is a full path, else `<home>/.local/share/opencode` (on Windows
/// too: 2.2.7 has no Windows branch).
pub fn opencode_data_directory(environment: &HashMap<String, String>, home: &Path) -> PathBuf {
    match environment.get("XDG_DATA_HOME").filter(|d| !d.is_empty() && absolute(d)) {
        Some(data) => Path::new(data).join("opencode"),
        None => home.join(".local").join("share").join("opencode"),
    }
}

/// `OpencodeSessions.Database`: `$OPENCODE_DB` (a relative value resolved against the data folder), else `opencode.db` in the data
/// folder; None for `:memory:`, which keeps nothing on disk.
pub fn opencode_database(environment: &HashMap<String, String>, home: &Path) -> Option<PathBuf> {
    let data = opencode_data_directory(environment, home);
    let Some(given) = environment.get("OPENCODE_DB").filter(|g| !g.is_empty()) else { return Some(data.join("opencode.db")) };
    if given == ":memory:" {
        return None;
    }
    mighty_core::workspace_files::full_path(&data.join(given))
}

/// `OpencodeSessions.PlaceholderTitle`: opencode names a new session "New session - <time>" (a sub-agent's "Child session …") until it
/// writes a real title.
pub fn opencode_placeholder_title(title: &str) -> bool {
    let lowered = title.to_lowercase();
    title.is_empty() || lowered.starts_with("new session") || lowered.starts_with("child session")
}

/// `OpencodeSessions.Model`: the session's `{providerID, id}` as `provider/model`.
pub fn opencode_model(json: Option<&str>) -> Option<String> {
    json_object(json, |value| {
        let provider = mighty_core::json::text(value, "providerID")?;
        let id = mighty_core::json::text(value, "id")?;
        let model = format!("{provider}/{id}");
        wire::model(&model).then_some(model)
    })
}

fn opencode_prompt(json: Option<&str>) -> Option<String> {
    json_object(json, |value| mighty_core::json::text(value, "text").map(str::trim).filter(|t| !t.is_empty()).map(str::to_string))
}

fn milliseconds(value: Option<&str>) -> Option<DateTimeOffset> {
    let ms: f64 = value?.trim().parse().ok()?;
    (ms.is_finite() && (0.0..253_402_300_800_000.0).contains(&ms)).then(|| from_unix_milliseconds(ms as i64))
}

/// `OpencodeSessions.Rows`: `session_v2` rows without a parent, not archived, in one of `directories` (any letter case and either slash on
/// Windows), updated since `since`, newest first; with `counts` also the session's user messages — how many, and the first one's text.
pub fn opencode_rows(database: &Path, directories: &[String], since: DateTimeOffset, limit: usize, counts: bool) -> Option<Vec<Row>> {
    let windows = crate::windows_paths();
    let mut folders: Vec<String> = directories.to_vec();
    if windows {
        for directory in directories {
            let slashed = directory.replace('\\', "/");
            if !folders.contains(&slashed) {
                folders.push(slashed);
            }
        }
    }
    if folders.is_empty() {
        return None;
    }
    let collate = if windows { " COLLATE NOCASE" } else { "" };
    // A JSON value is read only up to 1 MiB, as the Mac's reader takes it.
    let details = if counts {
        ", (SELECT COUNT(*) FROM session_message m WHERE m.session_id = s.id AND m.type = 'user'), \
         (SELECT CASE WHEN length(CAST(m.data AS BLOB)) <= 1048576 THEN m.data END FROM session_message m WHERE m.session_id = s.id AND m.type = 'user' ORDER BY m.seq LIMIT 1)"
    } else {
        ""
    };
    let marks = vec!["?"; folders.len()].join(",");
    let sql = format!(
        "SELECT s.id, s.title, s.time_updated, CASE WHEN length(CAST(s.model AS BLOB)) <= 1048576 THEN s.model END{details} FROM session_v2 s \
         WHERE s.parent_id IS NULL AND s.time_archived IS NULL AND s.directory{collate} IN ({marks}) AND s.time_updated >= CAST(? AS INTEGER) \
         ORDER BY s.time_updated DESC LIMIT {limit}"
    );
    let mut parameters = folders;
    parameters.push(unix_milliseconds(since).to_string());
    let rows = sqlite::read_only(database, &sql, if counts { 6 } else { 4 }, &parameters)?;
    let mut result = vec![];
    for row in rows {
        let (Some(id), Some(updated)) = (row[0].clone(), milliseconds(row[2].as_deref())) else { continue };
        let title = row[1].clone().unwrap_or_default();
        let requests = counts.then(|| row[4].as_deref().and_then(|c| c.trim().parse::<i64>().ok()).map_or(0, |c| c.min(20_000) as i32));
        result.push(Row {
            id,
            title: if opencode_placeholder_title(&title) { String::new() } else { clean(Some(&title), 512) },
            updated,
            requests,
            prompt: if counts { opencode_prompt(row[5].as_deref()) } else { None },
            model: opencode_model(row[3].as_deref()),
        });
    }
    Some(result)
}

// ── Hermes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// The `state_meta` key of a session's creation receipt (hermes_state_local.py `POLICY_PREFIX`).
const POLICY_PREFIX: &str = "gateway.local_policy.v1:";
/// What the app writes after the user's request while Hermes's question tool is on: not the user's words.
const NOTE_START: &str = "[Note from the Mighty Code app, not from the user:";

/// `HermesSessions.Home`: `$HERMES_HOME` when that is a full path, else the platform default (`%LOCALAPPDATA%\hermes` on Windows,
/// `~/.hermes` elsewhere) with `$HERMES_DATA_DIR_SUFFIX` after the name. `home` is the user folder the default is found under when
/// `environment` names none (a session list's own home); None reads this user's.
pub fn hermes_home(environment: &HashMap<String, String>, home: Option<&Path>) -> PathBuf {
    if let Some(given) = environment.get("HERMES_HOME").map(|g| g.trim()).filter(|g| !g.is_empty() && absolute(g)) {
        return PathBuf::from(given);
    }
    let windows = crate::windows_paths();
    let name = format!("{}{}", if windows { "hermes" } else { ".hermes" }, environment.get("HERMES_DATA_DIR_SUFFIX").map_or("", |s| s));
    if windows {
        let local = match environment.get("LOCALAPPDATA").map(|v| v.trim()).filter(|v| !v.is_empty()) {
            Some(value) => PathBuf::from(value),
            None => match home {
                Some(home) => home.join("AppData").join("Local"),
                None => std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_default(),
            },
        };
        return local.join(name);
    }
    let user = match home {
        Some(home) => home.to_path_buf(),
        None => match environment.get("HOME").filter(|h| !h.is_empty() && absolute(h)) {
            Some(user) => PathBuf::from(user),
            None => crate::history::user_home(),
        },
    };
    user.join(name)
}

/// `HermesSessions.StateDatabase`: `state.db` in [`hermes_home`].
pub fn hermes_state_database(environment: &HashMap<String, String>, home: Option<&Path>) -> PathBuf {
    hermes_home(environment, home).join("state.db")
}

/// `HermesSessions.MessageCount`: the messages the session holds (0 for one whose turns all failed or never ran); None when the database
/// or the session is not there, or cannot be read.
pub fn hermes_message_count(database: &Path, session_id: &str) -> Option<i32> {
    if !wire::session_identifier(session_id) {
        return None;
    }
    let rows = sqlite::read_only(database, "SELECT COALESCE(message_count, 0) FROM sessions WHERE id = ?", 1, &[session_id.to_string()])?;
    rows.first()?.first()?.as_deref()?.trim().parse().ok()
}

/// `HermesSessions.StartedWithFullAccess`: whether Hermes created the session with `--yolo`, from its creation receipt in `state_meta`;
/// None when the database or the record is missing or cannot be read.
pub fn hermes_started_with_full_access(database: &Path, session_id: &str) -> Option<bool> {
    if !wire::session_identifier(session_id) {
        return None;
    }
    let rows = sqlite::read_only(
        database,
        "SELECT CASE WHEN length(CAST(value AS BLOB)) <= 1048576 THEN value END FROM state_meta WHERE key = ?",
        1,
        &[format!("{POLICY_PREFIX}{session_id}")],
    )?;
    let raw = rows.first()?.first()?.clone()?;
    let receipt: Value = serde_json::from_str(&raw).ok()?;
    if !receipt.is_object() {
        return None;
    }
    let policy = receipt.get("policy").filter(|p| p.is_object()).unwrap_or(&receipt);
    if let Some(text) = mighty_core::json::text(policy, "request_json")
        && let Ok(request) = serde_json::from_str::<Value>(text)
        && request.is_object()
    {
        return Some(mighty_core::json::flag(&request, "yolo"));
    }
    policy.get("request").filter(|r| r.is_object()).map(|given| mighty_core::json::flag(given, "yolo"))
}

/// `HermesSessions.WithoutNote`: a stored request without the app's note after it, cut where the note starts, then trimmed.
pub fn hermes_without_note(text: &str) -> String {
    match text.find(NOTE_START) {
        Some(start) => text[..start].trim().to_string(),
        None => text.to_string(),
    }
}

fn number(value: Option<&str>) -> Option<f64> {
    value?.trim().parse::<f64>().ok().filter(|n| n.is_finite())
}

fn seconds_time(seconds: f64) -> Option<DateTimeOffset> {
    (0.0..253_402_300_800.0).contains(&seconds).then(|| from_unix_milliseconds((seconds * 1_000.0) as i64))
}

struct Segment {
    parent: Option<String>,
    title: String,
    updated: f64,
    messages: i64,
    model: Option<String>,
    cwd: Option<String>,
}

struct Conversation {
    title: String,
    updated: f64,
    messages: i64,
    model: Option<String>,
    cwd: Option<String>,
    members: Vec<String>,
}

/// `HermesSessions.Rows`: the sessions an ACP client created, not archived or hidden, as conversations (a compressed one listed once
/// under its first row's id), with messages, active since `since`, whose folder `here` takes; newest first. Columns a Hermes version
/// lacks are left out of the query. With `counts` also each conversation's user messages, the first one's text and the newest time.
pub fn hermes_rows(
    database: &Path,
    here: &mut dyn FnMut(&str) -> bool,
    since: DateTimeOffset,
    limit: usize,
    counts: bool,
    cancel: &dyn Cancel,
) -> Option<Vec<Row>> {
    let info = sqlite::read_only(database, "PRAGMA table_info(sessions)", 2, &[])?;
    let have: Vec<String> = info.iter().filter_map(|c| c.get(1).cloned().flatten()).map(|n| n.to_lowercase()).collect();
    let has = |name: &str| have.iter().any(|h| h == name);
    if !(has("id") && has("source") && has("started_at")) {
        return None;
    }
    let column = |name: &str, fallback: &str| if has(name) { name.to_string() } else { fallback.to_string() };
    let mut filters =
        vec![if has("created_source") { "(source = 'acp' OR created_source = 'acp')".to_string() } else { "source = 'acp'".to_string() }];
    for flag in ["archived", "hidden"] {
        if has(flag) {
            filters.push(format!("COALESCE({flag}, 0) = 0"));
        }
    }
    // Older rows keep the folder in `model_config` alone: read only where `cwd` is empty, and only up to 1 MiB.
    let (cwd, config) = (column("cwd", "NULL"), column("model_config", "NULL"));
    let sql = format!(
        "SELECT id, {}, {}, started_at, {}, {}, {}, {cwd}, CASE WHEN {cwd} IS NULL AND length(CAST({config} AS BLOB)) <= 1048576 THEN {config} END \
         FROM sessions WHERE {} ORDER BY started_at DESC LIMIT {limit}",
        column("parent_session_id", "NULL"),
        column("title", "NULL"),
        column("last_activity_at", "NULL"),
        column("message_count", "1"),
        column("model", "NULL"),
        filters.join(" AND "),
    );
    let rows = sqlite::read_only(database, &sql, 9, &[])?;
    let mut segments: HashMap<String, Segment> = HashMap::new();
    let mut order: Vec<String> = vec![];
    for row in rows {
        let Some(id) = row[0].clone() else { continue };
        if segments.contains_key(&id) {
            continue;
        }
        let config_folder = row[8]
            .as_deref()
            .and_then(|json| serde_json::from_str::<Value>(json).ok().and_then(|v| mighty_core::json::text(&v, "cwd").map(str::to_string)));
        segments.insert(
            id.clone(),
            Segment {
                parent: row[1].clone(),
                title: clean(row[2].as_deref(), 512),
                updated: number(row[3].as_deref()).unwrap_or(0.0).max(number(row[4].as_deref()).unwrap_or(0.0)),
                messages: number(row[5].as_deref()).unwrap_or(0.0).clamp(0.0, i32::MAX as f64) as i64,
                model: row[6].clone(),
                cwd: row[7].clone().or(config_folder),
            },
        );
        order.push(id);
    }
    // A conversation is its chain's first row; later rows (compression) only add to it.
    let root = |id: &str| -> String {
        let mut current = id.to_string();
        let mut hops = 0;
        while hops < 64 {
            match segments[&current].parent.as_ref().filter(|p| segments.contains_key(*p)) {
                Some(parent) => current = parent.clone(),
                None => break,
            }
            hops += 1;
        }
        current
    };
    let mut conversations: HashMap<String, Conversation> = HashMap::new();
    let mut roots: Vec<String> = vec![];
    for id in &order {
        let key = root(id);
        let segment = &segments[id];
        let conversation = conversations.entry(key.clone()).or_insert_with(|| {
            roots.push(key.clone());
            let first = &segments[&key];
            Conversation {
                title: first.title.clone(),
                updated: segment.updated,
                messages: 0,
                model: first.model.clone().filter(|m| wire::model(m)),
                cwd: first.cwd.clone(),
                members: vec![],
            }
        });
        conversation.members.push(id.clone());
        conversation.messages += segment.messages;
        conversation.updated = conversation.updated.max(segment.updated);
        if conversation.title.is_empty() {
            conversation.title = segment.title.clone();
        }
    }
    let mut result = vec![];
    for key in roots {
        if cancel.cancelled() {
            break;
        }
        let conversation = &conversations[&key];
        let Some(updated) = seconds_time(conversation.updated) else { continue };
        let Some(folder) = conversation.cwd.as_deref().filter(|f| !f.is_empty()) else { continue };
        if conversation.messages <= 0 || updated < since || !here(folder) {
            continue;
        }
        let row = Row {
            id: key.clone(),
            title: conversation.title.clone(),
            updated,
            requests: None,
            prompt: None,
            model: conversation.model.clone(),
        };
        result.push(if counts { hermes_details(database, &conversation.members, row) } else { row });
    }
    result.sort_by_key(|row| std::cmp::Reverse(row.updated.utc_ticks()));
    Some(result)
}

/// `HermesSessions.Details`: the conversation's user messages (as many as the Mac counts) and the first with text, read with the newest
/// message's time, which `last_activity_at` is not (it stays near the start).
fn hermes_details(database: &Path, members: &[String], row: Row) -> Row {
    let ids: Vec<String> = members.iter().take(64).cloned().collect();
    let marks = vec!["?"; ids.len()].join(",");
    let sql = format!(
        "SELECT 0, (SELECT COUNT(*) FROM (SELECT 1 FROM messages WHERE session_id IN ({marks}) AND role = 'user' LIMIT 20000)), \
         (SELECT MAX(timestamp) FROM messages WHERE session_id IN ({marks})) \
         UNION ALL SELECT * FROM (SELECT 1, id, content FROM messages WHERE session_id IN ({marks}) AND role = 'user' AND length(CAST(content AS BLOB)) <= 1048576 ORDER BY id LIMIT 16)"
    );
    let parameters: Vec<String> = ids.iter().chain(ids.iter()).chain(ids.iter()).cloned().collect();
    let Some(rows) = sqlite::read_only(database, &sql, 3, &parameters) else { return row };
    let Some(totals) = rows.iter().find(|r| r[0].as_deref() == Some("0")) else { return row };
    let newest = number(totals[2].as_deref()).and_then(seconds_time);
    let updated = match newest {
        Some(time) if time > row.updated => time,
        _ => row.updated,
    };
    let requests = totals[1].as_deref().and_then(|c| c.trim().parse::<i32>().ok()).unwrap_or(0);
    let mut messages: Vec<&Vec<Option<String>>> = rows.iter().filter(|r| r[0].as_deref() == Some("1")).collect();
    messages.sort_by_key(|r| r[1].as_deref().and_then(|id| id.trim().parse::<i64>().ok()).unwrap_or(i64::MAX));
    let mut prompt = None;
    for message in messages {
        let Some(content) = message[2].as_deref() else { continue };
        let text = if content.starts_with('[')
            && let Some(parts) = hermes_parts(content)
        {
            parts
        } else {
            content.to_string()
        };
        let words = hermes_without_note(&text).trim().to_string();
        if words.is_empty() {
            continue;
        }
        let units: Vec<u16> = words.encode_utf16().collect();
        prompt = Some(if units.len() > 2_000 {
            // `char.IsHighSurrogate(words[1_999])`: never cut a pair in two.
            let cut = if (0xD800..0xDC00).contains(&units[1_999]) { 1_999 } else { 2_000 };
            String::from_utf16_lossy(&units[..cut])
        } else {
            words
        });
        break;
    }
    Row { updated, requests: Some(requests), prompt, ..row }
}

/// The text parts of a message stored as a JSON list of parts, joined; None when it is not such a list.
fn hermes_parts(json: &str) -> Option<String> {
    let Value::Array(parts) = serde_json::from_str::<Value>(json).ok()? else { return None };
    if parts.iter().any(|p| !p.is_object()) {
        return None;
    }
    Some(parts.iter().filter_map(|p| mighty_core::json::text(p, "text")).collect::<Vec<_>>().join(" "))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn placeholders_and_models() {
        assert!(opencode_placeholder_title("New session - 2026-11-10T10:00:00.000Z"));
        assert!(opencode_placeholder_title("child session - x"));
        assert!(opencode_placeholder_title(""));
        assert!(!opencode_placeholder_title("Fix the build"));
        assert_eq!(opencode_model(Some(r#"{"id":"gpt-5","providerID":"openai","variant":"high"}"#)).as_deref(), Some("openai/gpt-5"));
        assert_eq!(opencode_model(Some(r#"{"id":"x"}"#)), None);
        assert_eq!(opencode_model(Some("not json")), None);
        assert_eq!(
            hermes_without_note("선택하는 문제를 내\n\n[Note from the Mighty Code app, not from the user: call clarify.]"),
            "선택하는 문제를 내"
        );
        assert_eq!(hermes_without_note(" plain "), " plain ");
    }
}
