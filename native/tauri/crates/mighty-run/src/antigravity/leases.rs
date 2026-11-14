//! C# `AntigravityRuleLeases` (AntigravityPermissions.cs:877-1265, M/MightyCore/AntigravityPermissions.swift AntigravityRuleLeases): puts
//! rules in agy's settings and takes them out again. One per app: the panes on this computer (and, later, the runs it hosts for other
//! computers) share it, so a rule two of them hold at once stays until the last is done. Every rule it adds for a while is written to its
//! ledger first, and taken out of it only once the settings are seen without it a little after it was removed (a running agy that
//! rewrites its settings could bring it back); a removal that fails (the swap meets a file agy has open) is tried again with growing
//! pauses, then again on the next apply or release and at the next launch ([`AntigravityRuleLeases::remove_leftovers`]). A rule the
//! settings already had is never removed, and what adding had to create (`permissions`, `allow`, the file) goes again once it is empty.
//! Calls are serialized; each reads and writes files and may pause, so callers keep them off the page's thread.

use std::collections::{HashMap, HashSet};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use mighty_core::json;
use mighty_core::wire;
use serde_json::Value;

use super::permissions::{self, AllowGrant, RuleVerdict};
use super::settings::{self, SettingsError};
use crate::lock;
use crate::os;

/// The rules one run holds a count on (the ones the app added); released once (C# `Lease`).
#[derive(Debug, PartialEq, Eq)]
pub struct Lease {
    id: u64,
    pub rules: Vec<String>,
}

/// Why [`AntigravityRuleLeases::apply`] added nothing: quit already released everything (C#'s `OperationCanceledException`), or the
/// settings or the grant were refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ApplyError {
    Cancelled,
    Settings(SettingsError),
}

impl From<SettingsError> for ApplyError {
    fn from(error: SettingsError) -> Self {
        ApplyError::Settings(error)
    }
}

/// How a written file takes the settings' place when they exist: on Windows C#'s `File.Replace(temporary, target, null,
/// ignoreMetadataErrors: true)` (`ReplaceFileW`: the file's ACL, attributes and creation time kept), which the host plugs in; by
/// default the settings' permissions copied and the file renamed over them, as C# does off Windows.
pub type Replace = fn(&Path, &Path) -> std::io::Result<()>;

/// The default [`Replace`].
pub fn portable_replace(temporary: &Path, target: &Path) -> std::io::Result<()> {
    let permissions = std::fs::metadata(target)?.permissions();
    std::fs::set_permissions(temporary, permissions)?;
    std::fs::rename(temporary, target)
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct LedgerEntry {
    settings: String,
    rule: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct CreatedEntry {
    settings: String,
    part: String,
}

/// C# `Ledger(Version, Entries, Created?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Ledger {
    version: i64,
    entries: Vec<LedgerEntry>,
    created: Option<Vec<CreatedEntry>>,
}

impl Ledger {
    fn is_empty(&self) -> bool {
        self.entries.is_empty() && self.created.as_ref().is_none_or(Vec::is_empty)
    }

    /// `JsonSerializer.Deserialize<Ledger>(bytes, Wire.Json)` with C#'s filter: None when it does not read or has no entries list.
    fn read(bytes: &[u8]) -> Option<Ledger> {
        let value: Value = serde_json::from_slice(bytes).ok()?;
        let o = json::Obj::of(&value, "the ledger").ok()?;
        let version = match o.get("version") {
            None => 0,
            Some(v) => json::as_integer(v)?,
        };
        let pairs = |list: Option<&Value>, second: &str| -> Option<Option<Vec<(String, String)>>> {
            match list {
                None | Some(Value::Null) => Some(None),
                Some(Value::Array(items)) => {
                    let mut kept = vec![];
                    for item in items {
                        if item.is_null() {
                            continue;
                        }
                        let entry = json::Obj::of(item, "an entry").ok()?;
                        let settings = json::opt_string(entry.get("settings")).ok()?;
                        let other = json::opt_string(entry.get(second)).ok()?;
                        if let (Some(settings), Some(other)) = (settings, other) {
                            kept.push((settings, other));
                        }
                    }
                    Some(Some(kept))
                }
                Some(_) => None,
            }
        };
        let entries = pairs(o.get("entries"), "rule")??;
        let created = pairs(o.get("created"), "part")?;
        Some(Ledger {
            version,
            entries: entries.into_iter().map(|(settings, rule)| LedgerEntry { settings, rule }).collect(),
            created: created.map(|list| list.into_iter().map(|(settings, part)| CreatedEntry { settings, part }).collect()),
        })
    }

    /// `JsonSerializer.SerializeToUtf8Bytes(ledger, Wire.Json with WriteIndented)`: two spaces a level, `Environment.NewLine` between
    /// lines, null left out, strings escaped as `Utf8JsonWriter` escapes them.
    fn write(&self) -> Vec<u8> {
        let newline = os::new_line();
        let quoted = |text: &str| {
            let mut out = String::new();
            json::escape_into(&mut out, text);
            out
        };
        let list = |name: &str, rows: Vec<(&str, &str, &str, &str)>| {
            if rows.is_empty() {
                return format!("  \"{name}\": []");
            }
            let items: Vec<String> = rows
                .iter()
                .map(|(k1, v1, k2, v2)| {
                    format!("    {{{newline}      \"{k1}\": {},{newline}      \"{k2}\": {}{newline}    }}", quoted(v1), quoted(v2))
                })
                .collect();
            format!("  \"{name}\": [{newline}{}{newline}  ]", items.join(&format!(",{newline}")))
        };
        let mut members = vec![format!("  \"version\": {}", self.version)];
        members.push(list("entries", self.entries.iter().map(|e| ("settings", e.settings.as_str(), "rule", e.rule.as_str())).collect()));
        if let Some(created) = &self.created {
            members.push(list("created", created.iter().map(|e| ("settings", e.settings.as_str(), "part", e.part.as_str())).collect()));
        }
        format!("{{{newline}{}{newline}}}", members.join(&format!(",{newline}"))).into_bytes()
    }
}

/// What a read of the settings found: their bytes (None when there is no file yet) and their rules.
struct Current {
    data: Option<Vec<u8>>,
    rules: Vec<String>,
}

#[derive(Default)]
struct State {
    counts: HashMap<String, i32>,
    leases: HashSet<u64>,
    /// Rules removed and waiting to be looked for once more.
    awaiting_recheck: HashSet<String>,
    rechecks: Vec<JoinHandle<()>>,
    /// Quit released everything: nothing is added any more.
    closed: bool,
}

struct Inner {
    settings_path: String,
    ledger_path: Option<PathBuf>,
    retry_delays: Vec<Duration>,
    recheck_delay: Duration,
    replace: Replace,
    gate: Mutex<State>,
}

/// C# `AntigravityRuleLeases`: a handle; clones share the same rules.
#[derive(Clone)]
pub struct AntigravityRuleLeases {
    inner: Arc<Inner>,
}

static NEXT_LEASE: AtomicU64 = AtomicU64::new(1);

impl AntigravityRuleLeases {
    /// `DefaultSettingsPath(home)`: `%USERPROFILE%\.gemini\antigravity-cli\settings.json` (on a Mac, `~/.gemini/antigravity-cli/settings.json`).
    pub fn default_settings_path(home: Option<&Path>) -> PathBuf {
        let home = home.map(Path::to_path_buf).or_else(permissions::user_profile).unwrap_or_default();
        home.join(".gemini").join("antigravity-cli").join("settings.json")
    }

    /// `ledger_path`: where the rules added for a while are recorded (the app's data folder); None keeps no record. `retry_delays`: the
    /// pauses before each new try of a failed removal (None: 250 ms, 1 s, 3 s). `recheck_delay`: how long after a removal the settings
    /// are looked at again (None: 5 s).
    pub fn new(
        settings_path: &Path,
        ledger_path: Option<&Path>,
        retry_delays: Option<Vec<Duration>>,
        recheck_delay: Option<Duration>,
    ) -> AntigravityRuleLeases {
        AntigravityRuleLeases::with_replace(settings_path, ledger_path, retry_delays, recheck_delay, portable_replace)
    }

    /// [`AntigravityRuleLeases::new`] with the swap the platform does ([`Replace`]).
    pub fn with_replace(
        settings_path: &Path,
        ledger_path: Option<&Path>,
        retry_delays: Option<Vec<Duration>>,
        recheck_delay: Option<Duration>,
        replace: Replace,
    ) -> AntigravityRuleLeases {
        AntigravityRuleLeases {
            inner: Arc::new(Inner {
                settings_path: settings_path.to_string_lossy().into_owned(),
                ledger_path: ledger_path.map(Path::to_path_buf),
                retry_delays: retry_delays
                    .unwrap_or_else(|| vec![Duration::from_millis(250), Duration::from_secs(1), Duration::from_secs(3)]),
                recheck_delay: recheck_delay.unwrap_or(Duration::from_secs(5)),
                replace,
                gate: Mutex::new(State::default()),
            }),
        }
    }

    /// `SettingsPath`.
    pub fn settings_path(&self) -> &str {
        &self.inner.settings_path
    }

    /// `Held`: the rules the app holds for a while now, with their counts.
    pub fn held(&self) -> HashMap<String, i32> {
        lock(&self.inner.gate).counts.clone()
    }

    /// `Check`: reads the settings the way adding would, and changes nothing.
    pub fn check(&self) -> Result<(), SettingsError> {
        let _gate = lock(&self.inner.gate);
        self.inner.read().map(|_| ())
    }

    /// `Settled`: waits for the looks-again removals started.
    pub fn settled(&self) {
        loop {
            let pending = std::mem::take(&mut lock(&self.inner.gate).rechecks);
            if pending.is_empty() {
                return;
            }
            for handle in pending {
                let _ = handle.join();
            }
        }
    }

    /// `Apply`: puts `grant`'s rules in place. A temporary grant returns the lease to [`AntigravityRuleLeases::release`] when its run ends;
    /// one for good returns None. After quit's [`AntigravityRuleLeases::release_all`] it adds nothing.
    pub fn apply(&self, grant: &AllowGrant) -> Result<Option<Lease>, ApplyError> {
        let inner = &self.inner;
        let mut state = lock(&inner.gate);
        if state.closed {
            return Err(ApplyError::Cancelled);
        }
        if !grant.is_valid() {
            return Err(SettingsError::of("badRule", None, None).into());
        }
        // Never a file rule into (or above) these settings' own folder or this ledger's, wherever they are.
        let mut guarded = permissions::protected_folders();
        guarded.extend(inner.own_folders());
        if grant.rules.iter().any(|rule| permissions::verdict(rule, Some(&guarded), Some(true)) == RuleVerdict::Refused) {
            return Err(SettingsError::of("badRule", None, None).into());
        }
        if !grant.keeps_only_allowed_rules() {
            return Err(SettingsError::of("onceOnly", None, None).into());
        }
        self.retry_pending(&mut state, &[]);
        let current = inner.read()?;
        let existing: HashSet<&String> = current.rules.iter().collect();
        let mut rules: Vec<String> = vec![];
        for rule in &grant.rules {
            if !rules.contains(rule) {
                rules.push(rule.clone());
            }
        }
        if !grant.temporary {
            // Kept for good: a rule the app holds or held for a while is no longer its to remove.
            let missing: Vec<String> = rules.iter().filter(|rule| !existing.contains(rule)).cloned().collect();
            if !missing.is_empty() {
                let (data, _) = inner.changed(&current, &missing)?;
                inner.save(&data)?;
            }
            for rule in &rules {
                state.counts.remove(rule);
                state.awaiting_recheck.remove(rule);
            }
            let _ = inner.forget(&rules);
            return Ok(None);
        }
        // Still in the settings from an earlier hold whose removal is pending: held again.
        let pending: HashSet<String> = inner.ledger_rules().into_iter().collect();
        let (mut incremented, mut reclaimed, mut added) = (vec![], vec![], vec![]);
        for rule in rules {
            if let Some(count) = state.counts.get_mut(&rule) {
                *count += 1;
                incremented.push(rule);
            } else if !existing.contains(&rule) {
                added.push(rule);
            } else if pending.contains(&rule) {
                state.counts.insert(rule.clone(), 1);
                state.awaiting_recheck.remove(&rule);
                reclaimed.push(rule);
            }
        }
        if !added.is_empty() {
            let outcome = inner.changed(&current, &added).and_then(|(data, created)| {
                inner.record(&added, &created)?;
                inner.save(&data)
            });
            if let Err(error) = outcome {
                let _ = inner.forget(&added);
                for rule in &incremented {
                    let count = state.counts.get(rule).copied().unwrap_or(1);
                    state.counts.insert(rule.clone(), count - 1);
                }
                for rule in &reclaimed {
                    state.counts.remove(rule);
                }
                return Err(error.into());
            }
            for rule in &added {
                state.counts.insert(rule.clone(), 1);
            }
        }
        let lease = Lease { id: NEXT_LEASE.fetch_add(1, Ordering::Relaxed), rules: [incremented, reclaimed, added].concat() };
        state.leases.insert(lease.id);
        Ok(Some(lease))
    }

    /// `Release`: the lease's run ended — a rule nobody holds any more leaves the settings.
    pub fn release(&self, lease: &Lease) {
        let mut gone = vec![];
        {
            let mut state = lock(&self.inner.gate);
            if !state.leases.remove(&lease.id) {
                return;
            }
            for rule in &lease.rules {
                let Some(count) = state.counts.get(rule).copied() else { continue };
                if count > 1 {
                    state.counts.insert(rule.clone(), count - 1);
                } else {
                    state.counts.remove(rule);
                    gone.push(rule.clone());
                }
            }
            self.retry_pending(&mut state, &gone);
        }
        self.remove(&gone);
    }

    /// `ReleaseAll`: quit — every rule held for a while leaves now, and nothing is added after.
    pub fn release_all(&self) {
        let gone: Vec<String> = {
            let mut state = lock(&self.inner.gate);
            state.closed = true;
            let mut gone: Vec<String> = state.counts.keys().cloned().collect();
            for rule in self.inner.ledger_rules() {
                if !state.awaiting_recheck.contains(&rule) && !gone.contains(&rule) {
                    gone.push(rule);
                }
            }
            state.counts.clear();
            state.leases.clear();
            gone
        };
        self.remove(&gone);
    }

    /// `RemoveLeftovers`: launch — what a previous run of the app added for a while and could not take out.
    pub fn remove_leftovers(&self) {
        let left: Vec<String> = {
            let state = lock(&self.inner.gate);
            let left: Vec<String> = self
                .inner
                .ledger_rules()
                .into_iter()
                .filter(|rule| !state.counts.contains_key(rule) && !state.awaiting_recheck.contains(rule))
                .collect();
            if left.is_empty() {
                self.inner.prune_if_done(&state);
                return;
            }
            left
        };
        self.remove(&left);
    }

    // Removing

    /// `Remove`: takes `rules` out of the settings, trying again after each of the retry pauses; then looks again after the recheck delay.
    /// A rule held again meanwhile is left in.
    fn remove(&self, rules: &[String]) {
        if rules.is_empty() {
            return;
        }
        if self.remove_with_retries(rules) {
            let mut state = lock(&self.inner.gate);
            let still: Vec<String> = rules.iter().filter(|rule| !state.counts.contains_key(*rule)).cloned().collect();
            self.schedule_recheck(&mut state, still);
        }
    }

    /// `RemoveWithRetries`: each try under the lock, each pause outside it.
    fn remove_with_retries(&self, rules: &[String]) -> bool {
        let mut attempt = 0;
        loop {
            {
                let state = lock(&self.inner.gate);
                let still: Vec<String> = rules.iter().filter(|rule| !state.counts.contains_key(*rule)).cloned().collect();
                if still.is_empty() {
                    return true;
                }
                // C# catches IOException, UnauthorizedAccessException and AntigravitySettingsException here: every failure is one.
                if self.inner.remove_once(&still).is_ok() {
                    return true;
                }
                if attempt >= self.inner.retry_delays.len() {
                    return false;
                }
            }
            std::thread::sleep(self.inner.retry_delays[attempt]);
            attempt += 1;
        }
    }

    /// `ScheduleRecheck`, under the gate.
    fn schedule_recheck(&self, state: &mut State, rules: Vec<String>) {
        if rules.is_empty() {
            return;
        }
        for rule in &rules {
            state.awaiting_recheck.insert(rule.clone());
        }
        let delay = self.inner.recheck_delay;
        let leases = self.clone();
        state.rechecks.push(std::thread::spawn(move || {
            std::thread::sleep(delay);
            leases.recheck(&rules);
        }));
    }

    /// `Recheck`: a little after a removal, a rule agy wrote back is taken out again; then the ledger lets go of it.
    fn recheck(&self, rules: &[String]) {
        let still: Vec<String> = {
            let mut state = lock(&self.inner.gate);
            let ledger: HashSet<String> = self.inner.ledger_rules().into_iter().collect();
            let still: Vec<String> = rules
                .iter()
                .filter(|rule| !state.counts.contains_key(*rule) && state.awaiting_recheck.contains(*rule) && ledger.contains(*rule))
                .cloned()
                .collect();
            let free: Vec<&String> = rules.iter().filter(|rule| !state.counts.contains_key(*rule)).collect();
            for rule in free {
                state.awaiting_recheck.remove(rule);
            }
            if still.is_empty() {
                return;
            }
            still
        };
        if !self.remove_with_retries(&still) {
            return;
        }
        let state = lock(&self.inner.gate);
        let free: Vec<String> = still.into_iter().filter(|rule| !state.counts.contains_key(rule)).collect();
        if self.inner.forget(&free).is_err() {
            return;
        }
        self.inner.prune_if_done(&state);
    }

    /// `RetryPending`, under the gate: one try, without waiting, at what the ledger still holds that nobody holds now (an earlier removal
    /// that failed).
    fn retry_pending(&self, state: &mut State, except: &[String]) {
        let pending: Vec<String> = self
            .inner
            .ledger_rules()
            .into_iter()
            .filter(|rule| !state.counts.contains_key(rule) && !state.awaiting_recheck.contains(rule) && !except.contains(rule))
            .collect();
        if pending.is_empty() {
            return;
        }
        if self.inner.remove_once(&pending).is_err() {
            return;
        }
        self.schedule_recheck(state, pending);
    }
}

fn link_target(path: &Path) -> Option<PathBuf> {
    std::fs::symlink_metadata(path).ok().filter(|m| m.file_type().is_symlink())?;
    permissions::resolved_path(&path.to_string_lossy()).map(PathBuf::from)
}

impl Inner {
    fn settings(&self) -> &Path {
        Path::new(&self.settings_path)
    }

    /// `OwnFolders`: agy's folder these settings are in (`.gemini`, two up from `antigravity-cli\settings.json`) and the ledger's folder,
    /// as written and resolved.
    fn own_folders(&self) -> Vec<String> {
        let full = |path: &Path| std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf());
        let mut folders: Vec<PathBuf> = vec![];
        if let Some(agy) = full(self.settings()).parent().and_then(Path::parent) {
            folders.push(agy.to_path_buf());
        }
        if let Some(ledger) = &self.ledger_path
            && let Some(folder) = full(ledger).parent()
        {
            folders.push(folder.to_path_buf());
        }
        let mut result = vec![];
        for folder in folders {
            let written = folder.to_string_lossy().into_owned();
            let resolved = permissions::resolved_path(&written);
            result.push(written);
            if let Some(resolved) = resolved {
                result.push(resolved);
            }
        }
        result
    }

    fn remove_once(&self, rules: &[String]) -> Result<(), SettingsError> {
        let current = self.read()?;
        let Some(data) = current.data else { return Ok(()) };
        let (bytes, removed) = settings::removing(rules, &data, &self.settings_path)?;
        if !removed.is_empty() {
            self.save(&bytes)?;
        }
        Ok(())
    }

    /// `PruneIfDone`, under the gate: nothing held and nothing left in the ledger, so what adding created goes once empty.
    fn prune_if_done(&self, state: &State) {
        if !state.counts.is_empty() || !self.ledger_rules().is_empty() {
            return;
        }
        let Some(ledger) = self.read_ledger() else { return };
        let created: HashSet<String> =
            ledger.created.iter().flatten().filter(|e| e.settings == self.settings_path).map(|e| e.part.clone()).collect();
        if created.is_empty() {
            return;
        }
        let outcome = (|| -> Result<(), SettingsError> {
            let current = self.read()?;
            if let Some(data) = current.data {
                let (bytes, empty) = settings::pruning(&created, &data, &self.settings_path)?;
                let linked = std::fs::symlink_metadata(self.settings()).is_ok_and(|m| m.file_type().is_symlink());
                if empty && created.contains(settings::CREATED_FILE) && !linked {
                    std::fs::remove_file(self.settings())
                        .map_err(|e| SettingsError::of("writeFailed", Some(&self.settings_path), Some(&e.to_string())))?;
                } else if bytes != data {
                    self.save(&bytes)?;
                }
            }
            let kept: Vec<CreatedEntry> = ledger.created.iter().flatten().filter(|e| e.settings != self.settings_path).cloned().collect();
            self.write_ledger(&Ledger { created: Some(kept), ..ledger.clone() })
                .map_err(|e| SettingsError::of("writeFailed", None, Some(&e.to_string())))
        })();
        let _ = outcome;
    }

    // Files

    /// `Target`: the settings file itself — a symbolic link is followed, so a linked file stays linked.
    fn target(&self) -> PathBuf {
        link_target(self.settings()).unwrap_or_else(|| self.settings().to_path_buf())
    }

    fn read(&self) -> Result<Current, SettingsError> {
        let unreadable = || SettingsError::of("unreadable", Some(&self.settings_path), None);
        // A link to nothing is not replaced by a new file.
        if std::fs::symlink_metadata(self.settings()).is_ok_and(|m| m.file_type().is_symlink()) {
            match link_target(self.settings()) {
                Some(target) if target.exists() => {}
                _ => return Err(unreadable()),
            }
        }
        let target = self.target();
        let folder = target.parent().map(Path::to_path_buf);
        match &folder {
            Some(folder) if folder.is_dir() => {}
            _ => {
                let shown = folder.unwrap_or_else(|| target.clone());
                return Err(SettingsError::of("missingFolder", Some(&shown.to_string_lossy()), None));
            }
        }
        if target.is_dir() {
            return Err(unreadable());
        }
        if !target.is_file() {
            return Ok(Current { data: None, rules: vec![] });
        }
        let data = std::fs::read(&target).map_err(|_| unreadable())?;
        let rules = settings::rules(&data, &self.settings_path)?;
        Ok(Current { data: Some(data), rules })
    }

    /// `Changed`: the settings with `rules` added, and what that created.
    fn changed(&self, current: &Current, rules: &[String]) -> Result<(Vec<u8>, Vec<String>), SettingsError> {
        match &current.data {
            None => Ok((settings::new_file(rules), vec![settings::CREATED_FILE.into()])),
            Some(data) => {
                let (bytes, _, created) = settings::adding(rules, data, &self.settings_path)?;
                Ok((bytes, created))
            }
        }
    }

    /// `Save`: writes beside the file and puts it in the file's place. On Windows the swap keeps the file's ACL, attributes and creation
    /// time; elsewhere its mode is copied. A symbolic link keeps pointing where it did.
    fn save(&self, data: &[u8]) -> Result<(), SettingsError> {
        let target = self.target();
        let folder = target.parent().map(Path::to_path_buf).unwrap_or_default();
        let name = target.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let temporary = folder.join(format!(".{name}.mighty-{}.tmp", wire::new_id()));
        let outcome = (|| -> std::io::Result<()> {
            {
                let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
                file.write_all(data)?;
                file.sync_all()?;
            }
            if target.is_file() {
                (self.replace)(&temporary, &target)
            } else {
                #[cfg(unix)]
                {
                    use std::os::unix::fs::PermissionsExt;
                    std::fs::set_permissions(&temporary, std::fs::Permissions::from_mode(0o644))?;
                }
                std::fs::rename(&temporary, &target)
            }
        })();
        if let Err(error) = outcome {
            let _ = std::fs::remove_file(&temporary);
            return Err(SettingsError::of("writeFailed", Some(&self.settings_path), Some(&error.to_string())));
        }
        Ok(())
    }

    fn read_ledger(&self) -> Option<Ledger> {
        let path = self.ledger_path.as_ref()?;
        if !path.is_file() {
            return None;
        }
        Ledger::read(&std::fs::read(path).ok()?)
    }

    /// `LedgerRules`: the rules the ledger holds for these settings.
    fn ledger_rules(&self) -> Vec<String> {
        let mut rules: Vec<String> = vec![];
        for entry in self.read_ledger().map(|l| l.entries).unwrap_or_default() {
            if entry.settings == self.settings_path && !rules.contains(&entry.rule) {
                rules.push(entry.rule);
            }
        }
        rules
    }

    fn write_ledger(&self, ledger: &Ledger) -> std::io::Result<()> {
        let Some(path) = &self.ledger_path else { return Ok(()) };
        if ledger.is_empty() {
            if path.exists() {
                std::fs::remove_file(path)?;
            }
            return Ok(());
        }
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let temporary = PathBuf::from(format!("{}.{}.tmp", path.display(), wire::new_id().replace('-', "")));
        let outcome = std::fs::write(&temporary, ledger.write()).and_then(|_| std::fs::rename(&temporary, path));
        if outcome.is_err() {
            let _ = std::fs::remove_file(&temporary);
        }
        outcome
    }

    /// `Record`: the rules added for a while and what that created, in the ledger before the settings change.
    fn record(&self, rules: &[String], created: &[String]) -> Result<(), SettingsError> {
        let Some(path) = &self.ledger_path else { return Ok(()) };
        let mut ledger = self.read_ledger().unwrap_or(Ledger { version: 1, entries: vec![], created: None });
        for rule in rules {
            let entry = LedgerEntry { settings: self.settings_path.clone(), rule: rule.clone() };
            if !ledger.entries.contains(&entry) {
                ledger.entries.push(entry);
            }
        }
        let mut parts = ledger.created.take().unwrap_or_default();
        for part in created {
            let entry = CreatedEntry { settings: self.settings_path.clone(), part: part.clone() };
            if !parts.contains(&entry) {
                parts.push(entry);
            }
        }
        ledger.created = (!parts.is_empty()).then_some(parts);
        self.write_ledger(&ledger).map_err(|e| SettingsError::of("writeFailed", Some(&path.to_string_lossy()), Some(&e.to_string())))
    }

    /// `Forget`: the rules no longer recorded.
    fn forget(&self, rules: &[String]) -> std::io::Result<()> {
        if self.ledger_path.is_none() || rules.is_empty() {
            return Ok(());
        }
        let Some(mut ledger) = self.read_ledger() else { return Ok(()) };
        ledger.entries.retain(|e| !(e.settings == self.settings_path && rules.contains(&e.rule)));
        self.write_ledger(&ledger)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_ledger_reads_back_what_it_writes() {
        let ledger = Ledger {
            version: 1,
            entries: vec![LedgerEntry { settings: "C:\\a\\settings.json".into(), rule: "command(echo \"hi\")".into() }],
            created: Some(vec![CreatedEntry { settings: "C:\\a\\settings.json".into(), part: "file".into() }]),
        };
        let written = ledger.write();
        assert_eq!(Ledger::read(&written), Some(ledger.clone()));
        let text = String::from_utf8(written).unwrap();
        let nl = os::new_line();
        assert!(
            text.starts_with(&format!(
                "{{{nl}  \"version\": 1,{nl}  \"entries\": [{nl}    {{{nl}      \"settings\": \"C:\\\\a\\\\settings.json\",{nl}"
            )),
            "{text}"
        );
        assert!(text.contains("\"rule\": \"command(echo \\u0022hi\\u0022)\""), "{text}");
        let empty = Ledger { version: 1, entries: vec![], created: None };
        assert_eq!(String::from_utf8(empty.write()).unwrap(), format!("{{{nl}  \"version\": 1,{nl}  \"entries\": []{nl}}}"));
        assert_eq!(Ledger::read(b"{\"version\":1}"), None, "no entries list");
        assert_eq!(Ledger::read(b"{ broken"), None);
    }
}
