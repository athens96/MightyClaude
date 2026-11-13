//! Where the state lives and how it loads (C# `StateStore`, MightyCode.Core/StateStore.cs), read-only: the skeleton never writes
//! the state file, never moves a damaged one aside and never copies a legacy one. What C# would have done is reported instead.

use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

use serde_json::{Map, Value};

use crate::model::AppSnapshot;

pub const STATE_FILE: &str = "workspace-state.json";
/// The largest state file C# reads (`store.error.fileTooLarge`).
pub const MAXIMUM_STATE_BYTES: u64 = 8 * 1024 * 1024;
/// The only `remote` reference version a workspace may carry (C# `RemoteWorkspaceReference.CurrentVersion`).
pub const REMOTE_VERSION: f64 = 2.0;

/// The default profile, `%APPDATA%\MightyCode` on Windows (the platform's per-user config folder elsewhere).
pub fn default_data_dir() -> Option<PathBuf> {
    dirs::config_dir().map(|dir| dir.join("MightyCode"))
}

/// The profile folders of the app before its rename, whose state C# copies once into a default profile that has none (an explicit
/// `--profile` never reads them).
pub fn legacy_data_dirs() -> Vec<PathBuf> {
    dirs::config_dir().map(|dir| vec![dir.join("MightyClaude"), dir.join("mighty-claude")]).unwrap_or_default()
}

/// Why a state file does not load. Each maps to the C# message key the window would show.
#[derive(Debug)]
pub enum StateError {
    Io(std::io::Error),
    TooLarge(u64),
    /// Not JSON, or not a version 1 object with `workspaces` and `sessions` arrays (`store.error.invalidFormat`).
    InvalidFormat(String),
    /// Valid JSON whose fields do not decode as the C# records do (a `JsonException` there).
    Decode(serde_json::Error),
}

impl StateError {
    /// The locale key of the message C# shows for this failure.
    pub fn locale_key(&self) -> &'static str {
        match self {
            StateError::TooLarge(_) => "store.error.fileTooLarge",
            _ => "store.error.invalidFormat",
        }
    }
}

impl fmt::Display for StateError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            StateError::Io(e) => write!(f, "cannot read the state file: {e}"),
            StateError::TooLarge(n) => write!(f, "the state file is {n} bytes, over the {MAXIMUM_STATE_BYTES}-byte limit"),
            StateError::InvalidFormat(why) => write!(f, "the state file is not a version 1 state: {why}"),
            StateError::Decode(e) => write!(f, "the state file does not decode: {e}"),
        }
    }
}

impl std::error::Error for StateError {}

/// True when the bytes hold a NUL, which the JSON this app writes never does: what a power cut leaves behind (C# `Damaged`).
pub fn damaged(bytes: &[u8]) -> bool {
    bytes.contains(&0)
}

/// Whether a saved workspace object is kept on load (C# `StateStore.KeepsWorkspace`): a local one (no `remote`, or an explicit
/// null), or a remote one whose reference carries version 2 as a JSON number (2.0 included).
pub fn keeps_workspace(value: &Value) -> bool {
    let Some(object) = value.as_object() else { return false };
    match object.get("remote") {
        None | Some(Value::Null) => true,
        Some(Value::Object(remote)) => remote.get("version").and_then(Value::as_f64) == Some(REMOTE_VERSION),
        Some(_) => false,
    }
}

/// The saved state with only the workspaces [`keeps_workspace`] keeps (C# `KeptWorkspaces`): a key spelled like `workspaces` or
/// `remote` in another case is left out, as the C# reader would otherwise read it; a kept remote reference's version is written
/// as the integer 2, and one whose other fields are not strings is left out on its own.
pub fn kept_workspaces(root: &Map<String, Value>) -> Map<String, Value> {
    let mut out = Map::new();
    for (name, value) in root {
        if !name.eq_ignore_ascii_case("workspaces") {
            out.insert(name.clone(), value.clone());
            continue;
        }
        let (true, Some(list)) = (name == "workspaces", value.as_array()) else { continue };
        let kept: Vec<Value> = list.iter().filter(|w| keeps_workspace(w)).filter_map(kept_workspace).collect();
        out.insert(name.clone(), Value::Array(kept));
    }
    out
}

fn kept_workspace(workspace: &Value) -> Option<Value> {
    let object = workspace.as_object()?;
    let mut item = Map::new();
    let mut remote = false;
    let mut current = true;
    for (name, field) in object {
        if !name.eq_ignore_ascii_case("remote") {
            item.insert(name.clone(), field.clone());
        } else if name != "remote" {
            continue;
        } else if let Value::Object(reference) = field {
            remote = true;
            let mut versioned = false;
            let mut kept = Map::new();
            for (part, part_value) in reference {
                if !part.eq_ignore_ascii_case("version") {
                    kept.insert(part.clone(), part_value.clone());
                } else if part == "version" && part_value.as_f64() == Some(REMOTE_VERSION) {
                    kept.insert(part.clone(), Value::from(2));
                    versioned = true;
                } else {
                    current = false;
                }
            }
            current &= versioned;
            item.insert(name.clone(), Value::Object(kept));
        } else {
            remote |= !field.is_null();
            item.insert(name.clone(), field.clone());
        }
    }
    if remote {
        let strings = item.get("remote").and_then(Value::as_object).is_some_and(|r| {
            ["connectionId", "workspaceId", "hostName"].iter().all(|k| r.get(*k).is_none_or(|v| v.is_string() || v.is_null()))
        });
        if !current || !strings {
            return None;
        }
    }
    Some(Value::Object(item))
}

/// Decodes a state object as it is, without the load's checks: what a round trip reads (tests, tools).
pub fn decode(value: Value) -> Result<AppSnapshot, serde_json::Error> {
    serde_json::from_value(value)
}

/// One saved state file's bytes, checked and decoded as C# `ReadAsync` does: at most 8 MB, a UTF-8 byte order mark skipped, a
/// version 1 object with `workspaces` and `sessions` arrays, the workspaces [`kept_workspaces`] keeps.
pub fn parse_state(bytes: &[u8]) -> Result<AppSnapshot, StateError> {
    if bytes.len() as u64 > MAXIMUM_STATE_BYTES {
        return Err(StateError::TooLarge(bytes.len() as u64));
    }
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(bytes);
    let text = String::from_utf8_lossy(bytes);
    let value: Value = serde_json::from_str(&text).map_err(|e| StateError::InvalidFormat(e.to_string()))?;
    let Value::Object(root) = value else { return Err(StateError::InvalidFormat("not an object".into())) };
    // C# TryGetInt32: an integer literal only (serde_json reads 1.0 and 1e0 as floats, which as_i64 refuses).
    if root.get("version").and_then(Value::as_i64) != Some(1) {
        return Err(StateError::InvalidFormat("version is not 1".into()));
    }
    if !root.get("workspaces").is_some_and(Value::is_array) || !root.get("sessions").is_some_and(Value::is_array) {
        return Err(StateError::InvalidFormat("workspaces or sessions is not an array".into()));
    }
    decode(Value::Object(kept_workspaces(&root))).map_err(StateError::Decode)
}

/// Where the loaded state came from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LoadSource {
    /// `workspace-state.json` read as it is.
    State,
    /// The state file was damaged (NUL bytes) and its `.bak` loaded instead.
    Backup,
    /// A default profile without a state file, read from the app's folder before the rename (C# copies it; this only reads).
    Legacy(PathBuf),
    /// No state file: an empty state.
    Empty,
    /// The state file was damaged and there was no readable backup: an empty state.
    DamagedEmpty,
    /// The state file does not read (`error`): C# stops loading and every save; this starts empty in memory and says why.
    Unreadable,
}

/// What a load found. `notice_key`/`aside` are what C# would show and where it would move the damaged file (it is not moved).
#[derive(Debug)]
pub struct LoadReport {
    pub snapshot: AppSnapshot,
    pub source: LoadSource,
    pub path: PathBuf,
    pub notice_key: Option<&'static str>,
    pub aside: Option<String>,
    pub error: Option<StateError>,
}

/// Loads the state of `directory` without writing anything (C# `StateStore.LoadAsync`, read-only). `legacy` is the pre-rename
/// profile to read when `directory` holds no state file (only for the default profile, as in C#).
pub fn load(directory: &Path, legacy: &[PathBuf]) -> LoadReport {
    let path = directory.join(STATE_FILE);
    let report = |snapshot, source, path: &Path| LoadReport {
        snapshot,
        source,
        path: path.to_path_buf(),
        notice_key: None,
        aside: None,
        error: None,
    };
    if !path.exists() {
        for old in legacy {
            let original = old.join(STATE_FILE);
            if original.exists() {
                return match fs::read(&original).map_err(StateError::Io).and_then(|b| parse_state(&b)) {
                    Ok(snapshot) => report(snapshot, LoadSource::Legacy(original.clone()), &original),
                    Err(error) => LoadReport { error: Some(error), ..report(AppSnapshot::default(), LoadSource::Unreadable, &original) },
                };
            }
        }
        return report(AppSnapshot::default(), LoadSource::Empty, &path);
    }
    let bytes = match fs::read(&path) {
        Ok(bytes) => bytes,
        Err(error) => {
            return LoadReport { error: Some(StateError::Io(error)), ..report(AppSnapshot::default(), LoadSource::Unreadable, &path) };
        }
    };
    if damaged(&bytes) {
        let aside =
            format!("{STATE_FILE}.unreadable-{}-{}", chrono::Local::now().format("%Y%m%d-%H%M%S"), &uuid::Uuid::new_v4().to_string()[..8]);
        let backup = path.with_file_name(format!("{STATE_FILE}.bak"));
        let restored = fs::read(&backup).ok().and_then(|b| parse_state(&b).ok());
        let (snapshot, source, key) = match restored {
            Some(snapshot) => (snapshot, LoadSource::Backup, "store.notice.restoredBackup"),
            None => (AppSnapshot::default(), LoadSource::DamagedEmpty, "store.notice.unreadableStartedEmpty"),
        };
        return LoadReport { notice_key: Some(key), aside: Some(aside), ..report(snapshot, source, &path) };
    }
    match parse_state(&bytes) {
        Ok(snapshot) => report(snapshot, LoadSource::State, &path),
        Err(error) => LoadReport { error: Some(error), ..report(AppSnapshot::default(), LoadSource::Unreadable, &path) },
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn version_must_be_the_integer_one() {
        assert!(parse_state(br#"{"version":1,"workspaces":[],"sessions":[]}"#).is_ok());
        assert!(parse_state(br#"{"version":1.0,"workspaces":[],"sessions":[]}"#).is_err());
        assert!(parse_state(br#"{"version":2,"workspaces":[],"sessions":[]}"#).is_err());
        assert!(parse_state(br#"{"version":1,"workspaces":{},"sessions":[]}"#).is_err());
        assert!(parse_state(b"\xEF\xBB\xBF{\"version\":1,\"workspaces\":[],\"sessions\":[]}").is_ok());
    }

    #[test]
    fn a_misspelled_workspaces_key_is_left_out() {
        let root = json!({"version": 1, "Workspaces": [{"id": "x"}], "workspaces": [{"id": "y"}], "sessions": []});
        let kept = kept_workspaces(root.as_object().unwrap());
        assert!(!kept.contains_key("Workspaces"));
        assert_eq!(kept["workspaces"], json!([{"id": "y"}]));
    }

    #[test]
    fn a_remote_version_of_two_point_zero_is_written_as_two() {
        let root =
            json!({"workspaces": [{"id": "a", "remote": {"version": 2.0, "connectionId": "c", "workspaceId": "w", "hostName": "h"}}]});
        let kept = kept_workspaces(root.as_object().unwrap());
        assert_eq!(kept["workspaces"][0]["remote"]["version"], json!(2));
    }
}
