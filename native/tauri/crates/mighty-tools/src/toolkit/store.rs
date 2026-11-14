//! The user's toolkit list next to the state file (C# `ToolkitStore`, ToolkitStore.cs). `list` always returns the bundled entry first,
//! then this OS's user entries. Other-OS entries (brew, repoScript) are kept in the file byte for byte and never shown. A toolkit.json
//! that is there but unreadable is reported in the app's words and never modified; every change is refused with the same reason.
//!
//! The file is written as C# writes it: sorted top-level keys, two-space indent, LF, one trailing newline, untouched entries as their
//! original bytes and touched ones canonically, UTF-8 with its mark (`File.WriteAllText(…, Encoding.UTF8)`), through a temporary file
//! moved over it.

use std::collections::{HashMap, HashSet};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};

use mighty_core::json::{JsonOut, escape};
use mighty_run::strings;
use serde_json::Value;
use serde_json::value::RawValue;
use sha2::{Digest, Sha256};

use crate::toolkit::file::{self, ToolkitApproval, ToolkitEntrySource, ToolkitFileEntry, ToolkitInstallSpec};

/// The file's name in the store's folder.
pub const FILE_NAME: &str = "toolkit.json";

#[derive(Default)]
struct State {
    loaded: bool,
    this_os_entries: Vec<ToolkitFileEntry>,
    approvals: HashMap<String, ToolkitApproval>,
    file_error: Option<String>,
    /// Every entry id as read from the file (both platforms), in file order.
    all_entry_ids: Vec<String>,
    /// The raw JSON text of each entry by id (keeps unknown keys such as a stale `platforms`).
    all_entry_raw_text: HashMap<String, String>,
    other_os_ids: HashSet<String>,
    /// Entries added, edited, approved or removed this session: written canonically; the others as their original bytes.
    touched_entry_ids: HashSet<String>,
}

/// C# `ToolkitStore`. Its methods load the file on first use; a lock keeps the box's actions and a running install apart.
pub struct ToolkitStore {
    directory: PathBuf,
    state: Mutex<State>,
}

impl ToolkitStore {
    pub fn new(directory: impl Into<PathBuf>) -> ToolkitStore {
        ToolkitStore { directory: directory.into(), state: Mutex::new(State::default()) }
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    fn file_path(&self) -> PathBuf {
        self.directory.join(FILE_NAME)
    }

    /// The bundled entry: always first, always visible, never requires approval.
    pub fn bundled() -> Vec<ToolkitFileEntry> {
        vec![ToolkitFileEntry {
            id: "mighty-styles".into(),
            display_name: "mighty-styles".into(),
            install_kind: "plugin".into(),
            install: ToolkitInstallSpec::Plugin {
                source: "athens96/mighty-styles".into(),
                plugin_id: "mighty-styles@mighty-styles".into(),
            },
            approval: None,
            source: ToolkitEntrySource::Bundled,
        }]
    }

    fn state(&self) -> MutexGuard<'_, State> {
        let mut state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if !state.loaded {
            state.loaded = true;
            load(&self.file_path(), &mut state);
        }
        state
    }

    /// `(bundled + this OS's user entries, error)`. Other-OS entries are kept in the file but never appear here.
    pub fn list(&self) -> (Vec<ToolkitFileEntry>, Option<String>) {
        let state = self.state();
        let mut entries = Self::bundled();
        entries.extend(state.this_os_entries.iter().cloned());
        (entries, state.file_error.clone())
    }

    /// The stored approval for a user entry, only while the content hash still matches; None for the bundled entry or a changed one.
    pub fn get_approval(&self, entry: &ToolkitFileEntry) -> Option<ToolkitApproval> {
        let state = self.state();
        if entry.source == ToolkitEntrySource::Bundled {
            return None;
        }
        let stored = state.approvals.get(&entry.id)?;
        (stored.content_hash == canonical_hash(entry)).then(|| stored.clone())
    }

    fn require_loaded(&self) -> Result<MutexGuard<'_, State>, String> {
        let state = self.state();
        match &state.file_error {
            Some(error) => Err(error.clone()),
            None => Ok(state),
        }
    }

    /// Approves a user entry by its current canonical hash; `resolved_commit` is a repoScript's 40-hex SHA.
    pub fn approve(&self, id: &str, resolved_commit: Option<&str>) -> Result<(), String> {
        let mut state = self.require_loaded()?;
        let entry = state.this_os_entries.iter().find(|e| e.id == id).cloned().ok_or_else(|| format!("Entry not found: {id}"))?;
        state.approvals.insert(
            id.into(),
            ToolkitApproval { content_hash: canonical_hash(&entry), resolved_commit: resolved_commit.map(str::to_string) },
        );
        state.touched_entry_ids.insert(id.into());
        self.persist(&state)
    }

    /// Adds or replaces a user entry (always unapproved after this). An entry for another OS is written to the file but stays out
    /// of the list and the plan, like one read from the file.
    pub fn add(&self, entry: &ToolkitFileEntry) -> Result<(), String> {
        let mut state = self.require_loaded()?;
        put(&mut state, entry);
        self.persist(&state)
    }

    /// Adds every entry of an export (a JSON array, as both platforms write it) or of a toolkit.json object as unapproved; an existing
    /// id is replaced. Other-OS entries are accepted and kept hidden. One invalid entry rejects the whole import and leaves the file
    /// untouched.
    pub fn import(&self, json: &str) -> Result<(), String> {
        let mut state = self.require_loaded()?;
        let root: Value = serde_json::from_str(file::unmarked(json)).map_err(|error| error.to_string())?;
        let items = match &root {
            Value::Array(items) => items,
            Value::Object(object) => match object.get("entries") {
                Some(Value::Array(items)) => items,
                _ => return Err("Import data must be a JSON array of toolkit entries".into()),
            },
            _ => return Err("Import data must be a JSON array of toolkit entries".into()),
        };
        let parsed = items
            .iter()
            .map(|item| {
                item.is_object()
                    .then(|| file::parse_entry(item))
                    .flatten()
                    .ok_or_else(|| "Import data holds an entry that is not a valid toolkit entry".to_string())
            })
            .collect::<Result<Vec<_>, _>>()?;
        for entry in &parsed {
            put(&mut state, entry);
        }
        self.persist(&state)
    }

    /// Every user entry, other-OS ones included, as a JSON array without approvals (the shape the Mac exports and imports).
    pub fn export(&self) -> String {
        let state = self.state();
        let mut parts = Vec::new();
        for id in &state.all_entry_ids {
            if state.other_os_ids.contains(id) {
                if let Some(raw) = state.all_entry_raw_text.get(id) {
                    parts.push(without_approval(raw));
                }
            } else if let Some(entry) = state.this_os_entries.iter().find(|e| &e.id == id) {
                parts.push(entry_to_json(entry));
            }
        }
        for entry in &state.this_os_entries {
            if !state.all_entry_ids.contains(&entry.id) {
                parts.push(entry_to_json(entry));
            }
        }
        format!("[{}]", parts.join(","))
    }

    /// Removes a user entry. No command is executed.
    pub fn remove(&self, id: &str) -> Result<(), String> {
        let mut state = self.require_loaded()?;
        state.this_os_entries.retain(|e| e.id != id);
        state.approvals.remove(id);
        state.touched_entry_ids.insert(id.into());
        self.persist(&state)
    }

    fn persist(&self, state: &State) -> Result<(), String> {
        std::fs::create_dir_all(&self.directory).map_err(|error| error.to_string())?;
        let this_os_ids: HashSet<&str> = state.this_os_entries.iter().map(|e| e.id.as_str()).collect();
        let mut lines: Vec<String> = Vec::new();
        // Entries in file order; untouched entries as their original bytes.
        for id in &state.all_entry_ids {
            if state.other_os_ids.contains(id) {
                if let Some(raw) = state.all_entry_raw_text.get(id) {
                    lines.push(raw.clone());
                }
            } else if state.touched_entry_ids.contains(id) {
                if this_os_ids.contains(id.as_str())
                    && let Some(entry) = state.this_os_entries.iter().find(|e| &e.id == id)
                {
                    lines.push(entry_line(entry, &state.approvals));
                }
            } else if this_os_ids.contains(id.as_str())
                && let Some(raw) = state.all_entry_raw_text.get(id)
            {
                lines.push(raw.clone());
            }
        }
        // Entries added this session (not in the original file).
        for entry in &state.this_os_entries {
            if !state.all_entry_ids.contains(&entry.id) {
                lines.push(entry_line(entry, &state.approvals));
            }
        }
        let body = if lines.is_empty() { String::new() } else { format!("\n    {}\n  ", lines.join(",\n    ")) };
        let json = format!("{{\n  \"entries\": [{body}],\n  \"version\": 1\n}}\n");
        let path = self.file_path();
        let temporary = PathBuf::from(format!("{}.{}.tmp", path.display(), random_file_name()));
        let written = (|| -> std::io::Result<()> {
            let mut file = std::fs::File::create(&temporary)?;
            file.write_all(&[0xEF, 0xBB, 0xBF])?;
            file.write_all(json.as_bytes())?;
            drop(file);
            std::fs::rename(&temporary, &path)
        })();
        if temporary.exists() {
            let _ = std::fs::remove_file(&temporary);
        }
        written.map_err(|error| error.to_string())
    }
}

/// .NET's `Path.GetRandomFileName()` shape: eight characters, a dot, three.
fn random_file_name() -> String {
    let text = uuid::Uuid::new_v4().simple().to_string();
    format!("{}.{}", &text[..8], &text[8..11])
}

fn put(state: &mut State, entry: &ToolkitFileEntry) {
    state.this_os_entries.retain(|e| e.id != entry.id);
    state.approvals.remove(&entry.id);
    state.touched_entry_ids.insert(entry.id.clone());
    if file::is_macos_only(entry) {
        state.other_os_ids.insert(entry.id.clone());
        state.all_entry_raw_text.insert(entry.id.clone(), entry_to_json(entry));
        if !state.all_entry_ids.contains(&entry.id) {
            state.all_entry_ids.push(entry.id.clone());
        }
        return;
    }
    state.other_os_ids.remove(&entry.id);
    state.this_os_entries.push(ToolkitFileEntry { approval: None, source: ToolkitEntrySource::User, ..entry.clone() });
}

/// The entries array, each item's raw text and parsed value, when the file reads as a version 1 toolkit.
fn load(path: &Path, state: &mut State) {
    let Ok(bytes) = std::fs::read(path) else {
        // No file is no error; one that cannot be read is.
        if path.exists() {
            state.file_error = Some(strings::get("toolkit.error.unreadable"));
        }
        return;
    };
    // `File.ReadAllText`: the encoding the mark names (a UTF-16 file reads too), UTF-8 without one.
    let text = file::decode_text(&bytes);
    let unreadable = || Some(strings::get("toolkit.error.unreadable"));
    let Ok(root) = serde_json::from_str::<HashMap<String, &RawValue>>(&text) else {
        state.file_error = unreadable();
        return;
    };
    let version = root.get("version").and_then(|raw| serde_json::from_str::<Value>(raw.get()).ok());
    if version.as_ref().and_then(Value::as_i64) != Some(1) {
        state.file_error = unreadable();
        return;
    }
    let Some(entries) = root.get("entries").and_then(|raw| serde_json::from_str::<Vec<&RawValue>>(raw.get()).ok()) else {
        state.file_error = unreadable();
        return;
    };
    for raw in entries {
        // An item the whole-document reader would refuse (C#'s `JsonDocument` throws for the file) makes the file unreadable, so a
        // later save never drops it.
        let Ok(item) = serde_json::from_str::<Value>(raw.get()) else {
            state.file_error = unreadable();
            return;
        };
        let Some(item_id) = item.as_object().and_then(|o| o.get("id")).and_then(Value::as_str) else { continue };
        if item_id.is_empty() {
            continue;
        }
        state.all_entry_ids.push(item_id.to_string());
        state.all_entry_raw_text.insert(item_id.to_string(), raw.get().to_string());
        if is_other_os_item(&item) {
            state.other_os_ids.insert(item_id.to_string());
            continue;
        }
        let Some(entry) = file::parse_entry(&item) else { continue };
        let approval = entry.approval.clone();
        state.this_os_entries.push(ToolkitFileEntry { approval: None, source: ToolkitEntrySource::User, ..entry });
        if let Some(approval) = approval {
            state.approvals.insert(item_id.to_string(), approval);
        }
    }
}

/// brew and repoScript are macOS-only; everything else (plugin, mcp, skill, npm, winget) runs on Windows.
fn is_other_os_item(item: &Value) -> bool {
    let Some(install) = item.get("install").filter(|install| install.is_object()) else { return false };
    match install.get("kind").and_then(Value::as_str) {
        Some("repoScript") => true,
        Some("package") => install.get("manager").and_then(Value::as_str) == Some("brew"),
        _ => false,
    }
}

/// One entry line of the file: canonical form with sorted keys, its approval first.
fn entry_line(entry: &ToolkitFileEntry, approvals: &HashMap<String, ToolkitApproval>) -> String {
    let install = install_json(entry);
    match approvals.get(&entry.id) {
        Some(approval) => {
            let inner = match &approval.resolved_commit {
                None => format!("\"contentHash\":{}", escape(&approval.content_hash)),
                Some(commit) => format!("\"contentHash\":{},\"resolvedCommit\":{}", escape(&approval.content_hash), escape(commit)),
            };
            // Keys sorted: approval < displayName < id < install.
            format!(
                "{{\"approval\":{{{inner}}},\"displayName\":{},\"id\":{},\"install\":{install}}}",
                escape(&entry.display_name),
                escape(&entry.id)
            )
        }
        None => entry_to_json(entry),
    }
}

/// SHA-256 of the canonical JSON for the entry (no approval, sorted keys), lower-case hex: the Mac's `canonicalHash(_:)`.
pub fn canonical_hash(entry: &ToolkitFileEntry) -> String {
    Sha256::digest(build_canonical_json(entry).as_bytes()).iter().map(|byte| format!("{byte:02x}")).collect()
}

/// The canonical JSON: `displayName`, `id`, `install` in that order, compact, strings escaped as `Wire.Json` writes them.
pub fn build_canonical_json(entry: &ToolkitFileEntry) -> String {
    entry_to_json(entry)
}

fn entry_to_json(entry: &ToolkitFileEntry) -> String {
    format!("{{\"displayName\":{},\"id\":{},\"install\":{}}}", escape(&entry.display_name), escape(&entry.id), install_json(entry))
}

fn install_json(entry: &ToolkitFileEntry) -> String {
    match &entry.install {
        // Sorted keys: kind, pluginID, source.
        ToolkitInstallSpec::Plugin { source, plugin_id } => {
            format!("{{\"kind\":\"plugin\",\"pluginID\":{},\"source\":{}}}", escape(plugin_id), escape(source))
        }
        // Sorted keys: args, executable, kind, name.
        ToolkitInstallSpec::Mcp { name, executable, args } => {
            let args = format!("[{}]", args.iter().map(|arg| escape(arg)).collect::<Vec<_>>().join(","));
            format!("{{\"args\":{args},\"executable\":{},\"kind\":\"mcp\",\"name\":{}}}", escape(executable), escape(name))
        }
        // Sorted keys: kind, url.
        ToolkitInstallSpec::Skill { url } => format!("{{\"kind\":\"skill\",\"url\":{}}}", escape(url)),
        // Sorted keys: executable, kind, manager, name (winget has an executable).
        ToolkitInstallSpec::Package { manager, name, executable: Some(executable) } if manager == "winget" => {
            format!("{{\"executable\":{},\"kind\":\"package\",\"manager\":\"winget\",\"name\":{}}}", escape(executable), escape(name))
        }
        // Sorted keys: kind, manager, name (brew, npm).
        ToolkitInstallSpec::Package { manager, name, .. } => {
            format!("{{\"kind\":\"package\",\"manager\":{},\"name\":{}}}", escape(manager), escape(name))
        }
        // Sorted keys: kind, ref, scriptPath, url.
        ToolkitInstallSpec::RepoScript { url, git_ref, script_path } => format!(
            "{{\"kind\":\"repoScript\",\"ref\":{},\"scriptPath\":{},\"url\":{}}}",
            escape(git_ref),
            escape(script_path),
            escape(url)
        ),
    }
}

/// An other-OS entry's raw text without its approval, written again compactly (C# `JsonNode.ToJsonString()`): every number as its
/// text in the file (a `JsonValue` over a `JsonElement` writes its raw text: `1.0` stays `1.0`, a number past 64 bits stays whole).
fn without_approval(raw: &str) -> String {
    let Ok(members) = serde_json::from_str::<Members>(raw) else { return raw.to_string() };
    let mut out = JsonOut::new();
    out.begin_object();
    for (name, item) in members.0.iter().filter(|(name, _)| name != "approval") {
        out.name(name);
        write_raw(item, &mut out);
    }
    out.end_object();
    out.finish()
}

/// An object's members in their order, each value still its raw text.
struct Members(Vec<(String, Box<RawValue>)>);

impl<'de> serde::Deserialize<'de> for Members {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Members, D::Error> {
        struct Visitor;
        impl<'de> serde::de::Visitor<'de> for Visitor {
            type Value = Members;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("a JSON object")
            }
            fn visit_map<A: serde::de::MapAccess<'de>>(self, mut map: A) -> Result<Members, A::Error> {
                let mut members = Vec::new();
                while let Some(member) = map.next_entry::<String, Box<RawValue>>()? {
                    members.push(member);
                }
                Ok(Members(members))
            }
        }
        deserializer.deserialize_map(Visitor)
    }
}

/// One raw value written compactly: strings escaped as the writer escapes them, numbers and literals as their text.
fn write_raw(raw: &RawValue, out: &mut JsonOut) {
    let text = raw.get().trim();
    match text.as_bytes().first() {
        Some(b'{') => match serde_json::from_str::<Members>(text) {
            Ok(members) => {
                out.begin_object();
                for (name, item) in &members.0 {
                    out.name(name);
                    write_raw(item, out);
                }
                out.end_object();
            }
            Err(_) => out.raw(text),
        },
        Some(b'[') => match serde_json::from_str::<Vec<Box<RawValue>>>(text) {
            Ok(items) => {
                out.begin_array();
                for item in &items {
                    write_raw(item, out);
                }
                out.end_array();
            }
            Err(_) => out.raw(text),
        },
        Some(b'"') => match serde_json::from_str::<String>(text) {
            Ok(words) => out.str(&words),
            Err(_) => out.raw(text),
        },
        _ => out.raw(text),
    }
}
