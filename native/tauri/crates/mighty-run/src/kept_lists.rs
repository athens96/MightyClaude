//! C# `KeptModelLists` and `ModelListNotices` (KeptModelLists.cs; M/MightyCore/KeptModelLists.swift): the last list each CLI gave,
//! by provider and workspace folder, so a read that fails later (offline, signed out, a CLI that changed its flags), or fails at the
//! next launch, shows that list marked as kept instead of the built-in one. [`crate::providers::ProviderCatalog`] keeps it in memory
//! and, with a storage folder, in [`FILE_NAME`] there: bounded, written when a list changes (or its file is [`REWRITE_AGE`] old),
//! readable by this user only, and read tolerantly (a bad file or entry is left out, never an error). Hermes is not here: its model
//! list is the picker its last session left.

use std::path::{Path, PathBuf};
use std::time::Duration;

use mighty_core::json::JsonOut;
use mighty_core::time::DateTimeOffset;
use mighty_core::wire;
use serde::de::{Deserialize, Deserializer, MapAccess, SeqAccess, Visitor};
use serde_json::Value;

use crate::model::{ModelCatalog, ModelOption};
use crate::{os, providers, strings};

/// `KeptModelLists.FileName`.
pub const FILE_NAME: &str = "model-lists.json";
/// `KeptModelLists.MaximumBytes`.
pub const MAXIMUM_BYTES: usize = 2 * 1_048_576;
/// `KeptModelLists.MaximumEntries`.
pub const MAXIMUM_ENTRIES: usize = 24;
/// `KeptModelLists.RewriteAge`: a list read again unchanged is written again only once the file is this old.
pub const REWRITE_AGE: Duration = Duration::from_secs(5 * 60);

/// `KeptModelLists.MaximumModels`: the most rows a list keeps (the largest a reader gives, opencode's default and 1 024 models).
pub fn maximum_models() -> usize {
    1 + providers::listed_model_limit("opencode")
}

/// C# `KeptModelLists.Entry(Provider, Workspace, ListedAt, Detail, Models)`: `workspace` is the folder the list was read in (None for
/// a read outside any), `listed_at` when the CLI gave it (ISO 8601).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    pub provider: String,
    pub workspace: Option<String>,
    pub listed_at: String,
    pub detail: String,
    pub models: Vec<ModelOption>,
}

impl Entry {
    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("provider", Some(&self.provider));
        out.prop_str("workspace", self.workspace.as_deref());
        out.prop_str("listedAt", Some(&self.listed_at));
        out.prop_str("detail", Some(&self.detail));
        out.name("models");
        out.begin_array();
        for model in &self.models {
            model.write(out);
        }
        out.end_array();
        out.end_object();
    }
}

/// C# `KeptModelLists`: newest read first.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct KeptModelLists {
    entries: Vec<Entry>,
}

impl KeptModelLists {
    pub fn new(entries: Vec<Entry>) -> KeptModelLists {
        KeptModelLists { entries }
    }

    /// `Entries`: newest read first.
    pub fn entries(&self) -> &[Entry] {
        &self.entries
    }

    /// `FilePath(directory)`.
    pub fn file_path(directory: Option<&Path>) -> Option<PathBuf> {
        directory.map(|d| d.join(FILE_NAME))
    }

    /// `Load(path)`: the file's lists; a missing, oversized or unreadable file gives none, and an entry that does not check out is
    /// left out.
    pub fn load(path: Option<&Path>) -> KeptModelLists {
        let mut lists = KeptModelLists::default();
        let Some(path) = path else { return lists };
        let Ok(meta) = std::fs::symlink_metadata(path) else { return lists };
        if meta.is_dir() || reparse_point(&meta) || meta.len() > MAXIMUM_BYTES as u64 {
            return lists;
        }
        let Ok(data) = std::fs::read(path) else { return lists };
        if data.len() > MAXIMUM_BYTES {
            return lists;
        }
        // `JsonDocument.Parse`: the whole file is JSON or nothing is read.
        let Ok(Raw(root)) = serde_json::from_slice::<Raw>(&data) else { return lists };
        let RawValue::Object(members) = &root else { return lists };
        let version = last(members, "version").and_then(|v| v.integer());
        let Some(RawValue::Array(rows)) = last(members, "lists") else { return lists };
        if version != Some(1) {
            return lists;
        }
        for row in rows.iter().take(MAXIMUM_ENTRIES * 4) {
            if !matches!(row, RawValue::Object(_)) {
                continue;
            }
            // Read as the Mac's decoder reads an entry: every field it does not take as optional must be there.
            let Some(entry) = read_entry(row) else { continue };
            let Some(valid) = validated(&entry) else { continue };
            if lists.entries.iter().any(|e| e.provider == valid.provider && e.workspace == valid.workspace) {
                continue;
            }
            lists.entries.push(valid);
        }
        // Newest first, as Remember keeps them (a stable order for the same time).
        lists.entries.sort_by_key(|entry| std::cmp::Reverse(date(Some(&entry.listed_at)).map_or(i64::MIN, |d| d.utc_ticks())));
        lists.trim();
        lists
    }

    /// `Encoded()`: the file's bytes, the oldest lists left out until it fits [`MAXIMUM_BYTES`].
    pub fn encoded(&self) -> Vec<u8> {
        let mut kept: Vec<&Entry> = self.entries.iter().collect();
        loop {
            let mut out = JsonOut::new();
            out.begin_object();
            out.prop_int("version", 1);
            out.name("lists");
            out.begin_array();
            for entry in &kept {
                entry.write(&mut out);
            }
            out.end_array();
            out.end_object();
            let data = out.finish().into_bytes();
            if data.len() <= MAXIMUM_BYTES || kept.is_empty() {
                return data;
            }
            kept.pop();
        }
    }

    /// `List(provider, workspace)`: the list kept for this workspace, else the newest one this provider gave anywhere.
    pub fn list(&self, provider: &str, workspace: Option<&str>) -> Option<&Entry> {
        self.entries
            .iter()
            .find(|e| e.provider == provider && e.workspace.as_deref() == workspace)
            .or_else(|| self.entries.iter().find(|e| e.provider == provider))
    }

    /// `Remember(catalog, provider, workspace, at, writtenAt)`: keeps `catalog` (a list the CLI just gave) as this provider and
    /// workspace's list, first as the newest read; true when the file should be written: the list changed, or `written_at` (the file's
    /// last write) is [`REWRITE_AGE`] old. A kept or built-in list is never kept.
    pub fn remember(
        &mut self,
        catalog: &ModelCatalog,
        provider: &str,
        workspace: Option<&str>,
        at: DateTimeOffset,
        written_at: Option<DateTimeOffset>,
    ) -> bool {
        if !catalog.is_fresh() {
            return false;
        }
        let candidate = Entry {
            provider: provider.to_string(),
            workspace: workspace.map(str::to_string),
            listed_at: timestamp(at),
            detail: catalog.detail.clone(),
            models: catalog.models.clone(),
        };
        let Some(entry) = validated(&candidate) else { return false };
        let old = self.entries.iter().position(|e| e.provider == provider && e.workspace.as_deref() == workspace);
        let changed = old.is_none_or(|i| self.entries[i].detail != entry.detail || self.entries[i].models != entry.models);
        if let Some(i) = old {
            self.entries.remove(i);
        }
        self.entries.insert(0, entry);
        self.trim();
        let rewrite_ticks = REWRITE_AGE.as_secs() as i64 * 10_000_000;
        changed || written_at.is_none_or(|written| at.utc_ticks() - written.utc_ticks() >= rewrite_ticks)
    }

    /// `Trim()`: at most [`MAXIMUM_ENTRIES`], the oldest reads go.
    fn trim(&mut self) {
        self.entries.truncate(MAXIMUM_ENTRIES);
    }
}

/// `Kept(entry, reason)`: `entry`'s models as the list a read that failed for `reason` answers with.
pub fn kept(entry: &Entry, reason: &str) -> ModelCatalog {
    ModelCatalog {
        source: "cli".into(),
        models: entry.models.clone(),
        detail: entry.detail.clone(),
        read_failure: Some(reason.to_string()),
        listed_at: Some(entry.listed_at.clone()),
    }
}

/// `Validated(entry)`: the entry bounded as the file may hold it, or None when it cannot be kept.
pub fn validated(entry: &Entry) -> Option<Entry> {
    if !wire::PROVIDERS.contains(&entry.provider.as_str()) || entry.provider == "hermes" || date(Some(&entry.listed_at)).is_none() {
        return None;
    }
    if let Some(workspace) = &entry.workspace
        && !(path_rooted(workspace) && workspace.encode_utf16().count() <= 4_096 && !workspace.contains('\0'))
    {
        return None;
    }
    let mut seen = std::collections::HashSet::new();
    let models: Vec<ModelOption> = entry
        .models
        .iter()
        .take(maximum_models())
        .filter(|model| wire::model(&model.value) && seen.insert(model.value.clone()))
        .map(|model| ModelOption {
            value: model.value.clone(),
            display_name: wire::clean(&model.display_name, 160),
            description: wire::clean(&model.description, 2_400),
            resolved_model: model.resolved_model.clone().filter(|m| wire::model(m)),
            supports_effort: model.supports_effort,
            supported_effort_levels: model.supported_effort_levels.as_ref().map(|levels| levels.iter().take(16).cloned().collect()),
        })
        .collect();
    if models.is_empty() {
        return None;
    }
    Some(Entry {
        provider: entry.provider.clone(),
        workspace: entry.workspace.clone(),
        listed_at: entry.listed_at.clone(),
        detail: wire::clean(&entry.detail, 2_000),
        models,
    })
}

/// `Timestamp(date)`: ISO 8601 in UTC to the second, as the Mac's ISO8601DateFormatter writes it.
pub fn timestamp(date: DateTimeOffset) -> String {
    date.to_chrono().format("%Y-%m-%dT%H:%M:%SZ").to_string()
}

/// `Date(text)`: `yyyy-MM-dd'T'HH:mm:ssK` or with up to seven digits of a second (`.FFFFFFF`), read as UTC when it names no zone.
pub fn date(text: Option<&str>) -> Option<DateTimeOffset> {
    let text = text.filter(|t| !t.is_empty() && t.encode_utf16().count() <= 64)?;
    let bytes = text.as_bytes();
    let digits = |from: usize, count: usize| -> Option<u32> {
        let part = bytes.get(from..from + count)?;
        part.iter().all(u8::is_ascii_digit).then(|| part.iter().fold(0u32, |n, d| n * 10 + u32::from(d - b'0')))
    };
    let at = |index: usize, expected: u8| bytes.get(index) == Some(&expected);
    if !(at(4, b'-') && at(7, b'-') && at(10, b'T') && at(13, b':') && at(16, b':')) {
        return None;
    }
    let (year, month, day) = (digits(0, 4)?, digits(5, 2)?, digits(8, 2)?);
    let (hour, minute, second) = (digits(11, 2)?, digits(14, 2)?, digits(17, 2)?);
    let mut index = 19;
    let mut ticks_fraction: i64 = 0;
    if at(index, b'.') {
        let start = index + 1;
        let count = bytes[start..].iter().take_while(|b| b.is_ascii_digit()).count();
        if count == 0 || count > 7 {
            return None;
        }
        let value = i64::from(digits(start, count)?);
        ticks_fraction = value * 10_i64.pow((7 - count) as u32);
        index = start + count;
    }
    let offset_minutes: i64 = match &bytes[index..] {
        [] | [b'Z'] => 0,
        [sign @ (b'+' | b'-'), _, _, b':', _, _] => {
            let (hours, minutes) = (i64::from(digits(index + 1, 2)?), i64::from(digits(index + 4, 2)?));
            if hours > 14 || minutes > 59 || hours == 14 && minutes > 0 {
                return None;
            }
            let total = hours * 60 + minutes;
            if *sign == b'-' { -total } else { total }
        }
        _ => return None,
    };
    let date = chrono::NaiveDate::from_ymd_opt(year as i32, month, day)?;
    let time = chrono::NaiveTime::from_hms_opt(hour, minute, second)?;
    let local = chrono::NaiveDateTime::new(date, time).and_utc();
    let utc = local - chrono::Duration::minutes(offset_minutes);
    let ticks = mighty_core::time::ticks_of(utc) + ticks_fraction;
    Some(DateTimeOffset::from_utc_ticks(ticks))
}

/// C# `StateStore.AtomicWriteAsync(path, data)` as `KeepAsync` calls it (no backup): `data` written to a new temporary file beside
/// `path` and flushed to the disk, readable and writable by this user only off Windows, then moved over the file; a refused move
/// (another reader holding the file) is tried again, 40 times 50 ms apart; the temporary file never stays.
pub fn write(path: &Path, data: &[u8]) -> std::io::Result<()> {
    use std::io::Write as _;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let temporary = PathBuf::from(format!("{}.{}.tmp", path.display(), wire::new_id()));
    let result = (|| {
        {
            let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
            file.write_all(data)?;
            file.sync_all()?;
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            std::fs::set_permissions(&temporary, std::fs::Permissions::from_mode(0o600))?;
        }
        let mut attempt = 1;
        loop {
            match std::fs::rename(&temporary, path) {
                Ok(()) => return Ok(()),
                Err(_) if attempt < 40 => {
                    attempt += 1;
                    std::thread::sleep(Duration::from_millis(50));
                }
                Err(error) => return Err(error),
            }
        }
    })();
    if temporary.exists() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

/// `Path.IsPathRooted`: on Windows a leading separator or a drive (`C:`); elsewhere a leading `/`.
fn path_rooted(path: &str) -> bool {
    let bytes = path.as_bytes();
    if os::windows() {
        matches!(bytes.first(), Some(b'\\' | b'/')) || bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':'
    } else {
        bytes.first() == Some(&b'/')
    }
}

/// `FileAttributes.ReparsePoint` of what `symlink_metadata` read: a link or junction is never read as the file.
fn reparse_point(meta: &std::fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt as _;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        meta.file_type().is_symlink()
    }
}

// ── the file as JSON ────────────────────────────────────────────────────────────────────────────────────────────────────

/// A JSON value with every member kept as written, a repeated name included: the root is read as `JsonDocument` reads it (a name
/// written twice: the last), an entry as `RemoteWire.Json` reads it (a name written twice refuses the entry).
#[derive(Debug, Clone)]
enum RawValue {
    Null,
    Bool(bool),
    Number(serde_json::Number),
    String(String),
    Array(Vec<RawValue>),
    Object(Vec<(String, RawValue)>),
}

impl RawValue {
    /// `MetadataJson.Integer`: an integer literal that fits a long.
    fn integer(&self) -> Option<i64> {
        match self {
            RawValue::Number(n) => n.as_i64(),
            _ => None,
        }
    }

    /// The value as serde_json reads it, or None when an object anywhere in it repeats a name.
    fn strict(&self) -> Option<Value> {
        Some(match self {
            RawValue::Null => Value::Null,
            RawValue::Bool(b) => Value::Bool(*b),
            RawValue::Number(n) => Value::Number(n.clone()),
            RawValue::String(s) => Value::String(s.clone()),
            RawValue::Array(items) => Value::Array(items.iter().map(RawValue::strict).collect::<Option<_>>()?),
            RawValue::Object(members) => {
                let mut map = serde_json::Map::new();
                for (name, value) in members {
                    if map.insert(name.clone(), value.strict()?).is_some() {
                        return None;
                    }
                }
                Value::Object(map)
            }
        })
    }
}

/// The last member called `name` (`JsonElement.TryGetProperty`).
fn last<'a>(members: &'a [(String, RawValue)], name: &str) -> Option<&'a RawValue> {
    members.iter().rev().find(|(n, _)| n == name).map(|(_, v)| v)
}

struct Raw(RawValue);

impl<'de> Deserialize<'de> for Raw {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Raw, D::Error> {
        deserializer.deserialize_any(RawVisitor).map(Raw)
    }
}

struct RawVisitor;

impl<'de> Visitor<'de> for RawVisitor {
    type Value = RawValue;

    fn expecting(&self, formatter: &mut std::fmt::Formatter) -> std::fmt::Result {
        formatter.write_str("JSON")
    }
    fn visit_unit<E>(self) -> Result<RawValue, E> {
        Ok(RawValue::Null)
    }
    fn visit_bool<E>(self, value: bool) -> Result<RawValue, E> {
        Ok(RawValue::Bool(value))
    }
    fn visit_i64<E>(self, value: i64) -> Result<RawValue, E> {
        Ok(RawValue::Number(value.into()))
    }
    fn visit_u64<E>(self, value: u64) -> Result<RawValue, E> {
        Ok(RawValue::Number(value.into()))
    }
    fn visit_f64<E: serde::de::Error>(self, value: f64) -> Result<RawValue, E> {
        serde_json::Number::from_f64(value).map(RawValue::Number).ok_or_else(|| E::custom("not a finite number"))
    }
    fn visit_str<E>(self, value: &str) -> Result<RawValue, E> {
        Ok(RawValue::String(value.to_string()))
    }
    fn visit_string<E>(self, value: String) -> Result<RawValue, E> {
        Ok(RawValue::String(value))
    }
    fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<RawValue, A::Error> {
        let mut items = Vec::new();
        while let Some(Raw(item)) = seq.next_element::<Raw>()? {
            items.push(item);
        }
        Ok(RawValue::Array(items))
    }
    fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<RawValue, A::Error> {
        let mut members = Vec::new();
        while let Some((name, Raw(value))) = map.next_entry::<String, Raw>()? {
            members.push((name, value));
        }
        Ok(RawValue::Object(members))
    }
}

/// `row.Deserialize<Entry>(RemoteWire.Json)`: names matched exactly, `provider`, `listedAt`, `detail` and `models` required and not
/// null, each model's `value`, `displayName` and `description` too, no null inside a list, no name repeated; None when it does not
/// read.
fn read_entry(row: &RawValue) -> Option<Entry> {
    let row = row.strict()?;
    let object = row.as_object()?;
    let text = |name: &str| object.get(name).and_then(Value::as_str).map(str::to_string);
    let optional_text = |name: &str| -> Option<Option<String>> {
        match object.get(name) {
            None | Some(Value::Null) => Some(None),
            Some(Value::String(s)) => Some(Some(s.clone())),
            Some(_) => None,
        }
    };
    let models = object.get("models")?.as_array()?.iter().map(read_model).collect::<Option<Vec<_>>>()?;
    Some(Entry {
        provider: text("provider")?,
        workspace: optional_text("workspace")?,
        listed_at: text("listedAt")?,
        detail: text("detail")?,
        models,
    })
}

/// `ModelOption` as `RemoteWire.Json` reads it.
fn read_model(value: &Value) -> Option<ModelOption> {
    let object = value.as_object()?;
    let text = |name: &str| object.get(name).and_then(Value::as_str).map(str::to_string);
    let resolved_model = match object.get("resolvedModel") {
        None | Some(Value::Null) => None,
        Some(Value::String(s)) => Some(s.clone()),
        Some(_) => return None,
    };
    let supports_effort = match object.get("supportsEffort") {
        None | Some(Value::Null) => None,
        Some(Value::Bool(b)) => Some(*b),
        Some(_) => return None,
    };
    let supported_effort_levels = match object.get("supportedEffortLevels") {
        None | Some(Value::Null) => None,
        Some(Value::Array(levels)) => Some(levels.iter().map(|l| l.as_str().map(str::to_string)).collect::<Option<Vec<_>>>()?),
        Some(_) => return None,
    };
    Some(ModelOption {
        value: text("value")?,
        display_name: text("displayName")?,
        description: text("description")?,
        resolved_model,
        supports_effort,
        supported_effort_levels,
    })
}

// ── the menu's line ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ModelListNotice(Line, Help)`: the muted line under a model menu's refresh row, and the failure its help shows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelListNotice {
    pub line: String,
    pub help: String,
}

/// `ModelListNotices.Notice(catalog, now)`: what the model menu says about this list — the built-in list after a read that failed, or
/// the last list the CLI gave when a newer read failed. A list the CLI just gave, or one nothing has read yet, needs no line.
pub fn notice(catalog: &ModelCatalog, now: Option<DateTimeOffset>) -> Option<ModelListNotice> {
    let failure = catalog.read_failure.as_deref().filter(|f| !f.is_empty())?;
    if catalog.source != "cli" {
        return Some(ModelListNotice { line: strings::get("composer.model.listFallback"), help: failure.to_string() });
    }
    let age = match date(catalog.listed_at.as_deref()) {
        Some(listed) => relative_time(listed, now.unwrap_or_else(DateTimeOffset::now)),
        None => "—".to_string(),
    };
    Some(ModelListNotice { line: strings::get_with("composer.model.listKept", &[("age", &age)]), help: failure.to_string() })
}

/// C# `ResumableSessions.RelativeTime` (mighty-history's `resumable::relative_time`, which this crate cannot read): "방금", "5분 전",
/// "3시간 전", "12일 전".
fn relative_time(date: DateTimeOffset, now: DateTimeOffset) -> String {
    let seconds = ((now.utc_ticks() - date.utc_ticks()) as f64 / 10_000_000.0).max(0.0);
    let count = |value: f64| (value as i64).to_string();
    if seconds < 60.0 {
        strings::get("resume.time.now")
    } else if seconds < 3_600.0 {
        strings::get_with("resume.time.minutes", &[("count", &count(seconds / 60.0))])
    } else if seconds < 86_400.0 {
        strings::get_with("resume.time.hours", &[("count", &count(seconds / 3_600.0))])
    } else {
        strings::get_with("resume.time.days", &[("count", &count(seconds / 86_400.0))])
    }
}
