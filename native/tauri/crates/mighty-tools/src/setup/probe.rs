//! Whether each catalog item is on this PC (C# `SetupProbeResult`, `SetupProber`, SetupProbe.cs:1-134), what a row shows
//! (`SetupRows`), whether the screen opens by itself and what it ticks the first time (`SetupFirstRun`), and whether the runtime
//! disagrees with what the probe just saw (`SetupRuntimeSync`).
//!
//! The prober reads fresh on every call: the PATH it is given plus the folders the official installers use, with PATHEXT (the
//! platform's finder, mighty-platform-windows `setup_probe::executables`, handed in as a [`Finder`]); `--version` with a deadline
//! through the platform's `ProcessHost` (or a [`VersionReader`] the checks give); the Claude plugin registry; the skills folder.
//! Nothing is installed or changed.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use indexmap::IndexMap;
use mighty_platform::{BoxFuture, PrefValue, PrefsStore, ProcessHost};
use mighty_run::model::ProviderRuntime;
use regex::Regex;

use crate::setup::catalog::{SetupCatalog, SetupGroup, SetupItem, SetupPlatform, SetupProbe};
use crate::setup::windows::{set_variable, variable};

/// What a probe found.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SetupProbeState {
    Installed,
    Missing,
    Outdated,
    Unverified,
    NeedsAttention,
}

/// C# `SetupProbeResult(State, Version?, Minimum?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupProbeResult {
    pub state: SetupProbeState,
    pub version: Option<String>,
    pub minimum: Option<String>,
}

impl SetupProbeResult {
    pub fn new(state: SetupProbeState, version: Option<&str>, minimum: Option<&str>) -> SetupProbeResult {
        SetupProbeResult { state, version: version.map(str::to_string), minimum: minimum.map(str::to_string) }
    }

    pub fn missing() -> SetupProbeResult {
        SetupProbeResult::new(SetupProbeState::Missing, None, None)
    }

    pub fn installed(version: Option<&str>) -> SetupProbeResult {
        SetupProbeResult::new(SetupProbeState::Installed, version, None)
    }

    /// Counts as there: installed, or there with its version unread.
    pub fn is_installed(&self) -> bool {
        matches!(self.state, SetupProbeState::Installed | SetupProbeState::Unverified)
    }

    /// The text `--version` printed, when the item is there at all.
    pub fn shown_version(&self) -> Option<&str> {
        match self.state {
            SetupProbeState::Installed | SetupProbeState::Outdated => self.version.as_deref(),
            _ => None,
        }
    }
}

/// Each copy of a program along the environment's PATH and then in the installers' folders, in order, once (C#
/// `SetupProber.Executables`): the platform's `cli_copies`.
pub type Finder = Arc<dyn Fn(&str, &BTreeMap<String, String>) -> Vec<String> + Send + Sync>;

/// `--version`'s output for a program's full path, or None when it did not answer in time or failed.
pub type VersionReader = Arc<dyn Fn(String) -> BoxFuture<'static, Option<String>> + Send + Sync>;

/// Written in a skill's folder by the Mac's setup screen: a folder it placed counts only once its setup finished.
pub const SKILL_INSTALLING_MARKER: &str = ".mighty-setup";
pub const SKILL_DONE_MARKER: &str = ".mighty-setup-ok";
pub const VERSION_TIMEOUT: Duration = Duration::from_secs(4);

/// C# `SetupProber`.
#[derive(Clone)]
pub struct SetupProber {
    environment: BTreeMap<String, String>,
    finder: Finder,
    versions: VersionReader,
}

impl SetupProber {
    /// `environment`: the CLI environment (C# `CliEnvironment.Current()`, or its forced refresh).
    pub fn new(environment: BTreeMap<String, String>, finder: Finder, versions: VersionReader) -> SetupProber {
        SetupProber { environment, finder, versions }
    }

    /// The app's prober: versions read through `processes` in `neutral` (the system folder: no repository and no user-writable
    /// folder a shim's bare node could be planted in).
    pub fn with_processes(
        environment: BTreeMap<String, String>,
        finder: Finder,
        processes: Arc<dyn ProcessHost>,
        neutral: PathBuf,
    ) -> SetupProber {
        let quiet = quiet(&environment);
        let versions: VersionReader = Arc::new(move |executable: String| {
            let (processes, quiet, neutral) = (processes.clone(), quiet.clone(), neutral.clone());
            Box::pin(async move { read_version(processes.as_ref(), &executable, &quiet, &neutral).await })
        });
        SetupProber::new(environment, finder, versions)
    }

    fn home(&self) -> PathBuf {
        match variable(&self.environment, "USERPROFILE").filter(|home| !home.is_empty()) {
            Some(home) => PathBuf::from(home),
            None => {
                let fallback = variable(&self.environment, "HOME").map(str::to_string).or_else(|| std::env::var("USERPROFILE").ok());
                PathBuf::from(fallback.or_else(|| std::env::var("HOME").ok()).unwrap_or_default())
            }
        }
    }

    /// C# `SetupProber.Executables(name, environment)`.
    pub fn executables(&self, name: &str) -> Vec<String> {
        (self.finder)(name, &self.environment)
    }

    /// C# `ProbeAsync`.
    pub async fn probe(&self, probe: &SetupProbe) -> SetupProbeResult {
        match probe {
            SetupProbe::Executable { name, minimum_version } => {
                // Like ProviderCatalog: every copy in order, the first that says its version counts.
                let copies = self.executables(name);
                if copies.is_empty() {
                    return SetupProbeResult::missing();
                }
                let mut version = None;
                for copy in copies {
                    // Hermes's version is its first line (CliDiscovery.VersionText), as the runtime keeps it.
                    if let Some(text) = (self.versions)(copy).await {
                        let line = mighty_run::providers::version_text(name, &text);
                        if !line.is_empty() {
                            version = Some(line);
                            break;
                        }
                    }
                }
                let Some(minimum) = minimum_version else {
                    return SetupProbeResult { state: SetupProbeState::Installed, version, minimum: None };
                };
                match version {
                    None => SetupProbeResult::new(SetupProbeState::Unverified, None, Some(minimum)),
                    Some(version) if meets(&version, minimum) => SetupProbeResult::installed(Some(&version)),
                    Some(version) => SetupProbeResult::new(SetupProbeState::Outdated, Some(&version), Some(minimum)),
                }
            }
            SetupProbe::ClaudePlugin { prefix } => {
                if crate::toolkit::probe::user_scope_plugin_installed(&self.home(), &|id: &str| id.starts_with(prefix.as_str())) {
                    SetupProbeResult::installed(None)
                } else {
                    SetupProbeResult::missing()
                }
            }
            SetupProbe::Skill { name } => {
                if skill_installed(&self.home().join(".claude").join("skills").join(name)) {
                    SetupProbeResult::installed(None)
                } else {
                    SetupProbeResult::missing()
                }
            }
            // The command line tools and app bundles are the Mac's; the Windows catalog never asks for them.
            _ => SetupProbeResult::missing(),
        }
    }

    /// C# `ProbeAllAsync`: every item that has a Windows entry (only those in `only`, when given), one after another, in the
    /// catalog's order.
    pub async fn probe_all(&self, catalog: &SetupCatalog, only: Option<&HashSet<String>>) -> IndexMap<String, SetupProbeResult> {
        let mut results = IndexMap::new();
        for item in &catalog.items {
            if only.is_some_and(|only| !only.contains(&item.id)) {
                continue;
            }
            let Some(entry) = item.entry(SetupPlatform::Windows) else { continue };
            let result = self.probe(&entry.probe).await;
            results.insert(item.id.clone(), result);
        }
        results
    }
}

/// The environment the `--version` read runs with: the CLI environment, colour, the auto-updater and update notices off.
fn quiet(environment: &BTreeMap<String, String>) -> BTreeMap<String, String> {
    let mut quiet = environment.clone();
    for name in ["NO_COLOR", "DISABLE_AUTOUPDATER", "NO_UPDATE_NOTIFIER"] {
        set_variable(&mut quiet, name, "1");
    }
    quiet
}

/// C# `ReadVersionAsync`: `--version`'s output trimmed and at most 160 characters, or None when it did not answer in 4 s, failed or
/// could not start (CliRunner with a 16 KiB output cap).
async fn read_version(
    processes: &dyn ProcessHost,
    executable: &str,
    environment: &BTreeMap<String, String>,
    neutral: &Path,
) -> Option<String> {
    let request = processes.cli_request(Path::new(executable), &["--version".to_string()], neutral, environment).ok()?;
    let ran = crate::process::run(processes, request, None, VERSION_TIMEOUT, 16_384).await.ok()?;
    let text = ran.output.trim();
    (ran.exit_code == 0 && !ran.timed_out && !text.is_empty()).then(|| take_units(text, 160))
}

/// The first `count` UTF-16 units of `text` (C# `text[..count]`), never splitting a character.
fn take_units(text: &str, count: usize) -> String {
    let mut units = 0;
    let mut out = String::new();
    for c in text.chars() {
        units += c.len_utf16();
        if units > count {
            break;
        }
        out.push(c);
    }
    out
}

/// A skill counts when its SKILL.md is there; a folder the setup screen placed counts only once its setup finished.
pub fn skill_installed(folder: &Path) -> bool {
    if !folder.join("SKILL.md").is_file() {
        return false;
    }
    if !folder.join(SKILL_INSTALLING_MARKER).is_file() {
        return true;
    }
    folder.join(SKILL_DONE_MARKER).is_file()
}

fn version_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new(r"[0-9]+(\.[0-9]+){0,2}").expect("a valid pattern"))
}

fn triple_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new(r"[0-9]+\.[0-9]+\.[0-9]+").expect("a valid pattern"))
}

/// The first `1.2.3` (or `1.2`, `1`) in a version line.
pub fn version_numbers(text: &str) -> Option<Vec<i32>> {
    let found = version_pattern().find(text)?;
    let numbers: Vec<i32> = found.as_str().split('.').filter_map(|part| part.parse::<i32>().ok()).collect();
    (!numbers.is_empty()).then_some(numbers)
}

pub fn is_at_least(found: &[i32], required: &[i32]) -> bool {
    let width = found.len().max(required.len());
    for index in 0..width {
        let a = found.get(index).copied().unwrap_or(0);
        let b = required.get(index).copied().unwrap_or(0);
        if a != b {
            return a > b;
        }
    }
    true
}

/// Whether `version` reaches `minimum`. A prerelease (`2.1.300-beta`) never does, as ProviderCatalog.SupportsMods reads it.
pub fn meets(version: &str, minimum: &str) -> bool {
    if let Some(triple) = triple_pattern().find(version)
        && version.as_bytes().get(triple.end()) == Some(&b'-')
    {
        return false;
    }
    match (version_numbers(version), version_numbers(minimum)) {
        (Some(found), Some(required)) => is_at_least(&found, &required),
        _ => false,
    }
}

/// What a row shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SetupRowState {
    Checking,
    Installed,
    Missing,
    Outdated,
    Attention,
    Installing,
    Failed,
}

impl SetupRowState {
    /// The C# enum's name (`AutomationProperties.ItemStatus`, the smoke reads it).
    pub fn name(self) -> &'static str {
        match self {
            SetupRowState::Checking => "Checking",
            SetupRowState::Installed => "Installed",
            SetupRowState::Missing => "Missing",
            SetupRowState::Outdated => "Outdated",
            SetupRowState::Attention => "Attention",
            SetupRowState::Installing => "Installing",
            SetupRowState::Failed => "Failed",
        }
    }
}

/// C# `SetupRows`.
pub struct SetupRows;

impl SetupRows {
    /// `in_run`: the item is part of the install that is running, or was part of the last one. `run_finished`: that install's
    /// script has ended. An item the probe still does not see once the script ended failed.
    pub fn derive(result: Option<&SetupProbeResult>, in_run: bool, run_finished: bool) -> SetupRowState {
        match result.map(|result| result.state) {
            None => SetupRowState::Checking,
            Some(SetupProbeState::Installed | SetupProbeState::Unverified) => SetupRowState::Installed,
            Some(SetupProbeState::NeedsAttention) => SetupRowState::Attention,
            Some(SetupProbeState::Outdated) => {
                if in_run && !run_finished {
                    SetupRowState::Installing
                } else {
                    SetupRowState::Outdated
                }
            }
            Some(SetupProbeState::Missing) => {
                if !in_run {
                    SetupRowState::Missing
                } else if run_finished {
                    SetupRowState::Failed
                } else {
                    SetupRowState::Installing
                }
            }
        }
    }

    /// Whether the row's box may be ticked: what is missing or failed, and an out-of-date tool that is not an agent CLI (those get the
    /// CLI update instead), or one its own update cannot bring up to date (OpenCode 1 to 2).
    pub fn is_selectable(state: SetupRowState, item: &SetupItem) -> bool {
        item.entry(SetupPlatform::Windows).is_some_and(|entry| entry.install.runs_in_terminal())
            && match state {
                SetupRowState::Missing | SetupRowState::Failed => true,
                SetupRowState::Outdated => item.provider().is_none() || SetupCatalog::REINSTALLED_WHEN_OUTDATED.contains(&item.id.as_str()),
                _ => false,
            }
    }

    /// The state word's locale key.
    pub fn status_key(state: SetupRowState) -> &'static str {
        match state {
            SetupRowState::Checking => "setup.status.checking",
            SetupRowState::Installed => "setup.status.installed",
            SetupRowState::Missing => "setup.status.missing",
            SetupRowState::Outdated => "setup.status.outdated",
            SetupRowState::Attention => "setup.status.attention",
            SetupRowState::Installing => "setup.status.installing",
            SetupRowState::Failed => "setup.status.failed",
        }
    }
}

/// C# `SetupFirstRun`.
pub struct SetupFirstRun;

impl SetupFirstRun {
    pub const REGISTRY_KEY: &'static str = r"Software\MightyCode";
    pub const NEVER_SHOW_VALUE: &'static str = "setup.neverShow";

    /// True when no agent CLI can run, or a required item (Git) is not ready, unless the user chose never to see it again. Results
    /// still missing count as not known yet.
    pub fn should_present(catalog: &SetupCatalog, results: &HashMap<String, SetupProbeResult>, never_show: bool) -> bool {
        if never_show {
            return false;
        }
        let items: Vec<&SetupItem> = catalog.items.iter().filter(|item| item.entry(SetupPlatform::Windows).is_some()).collect();
        if !items.iter().all(|item| results.contains_key(&item.id)) {
            return false;
        }
        let agents: Vec<&&SetupItem> = items.iter().filter(|item| item.group == SetupGroup::Agent).collect();
        if !agents.is_empty() && !agents.iter().any(|item| results[&item.id].is_installed()) {
            return true;
        }
        items.iter().any(|item| item.required && !results[&item.id].is_installed())
    }

    /// The welcome screen's button shows while this holds (the same rule, without "never show").
    pub fn needs_attention(catalog: &SetupCatalog, results: &HashMap<String, SetupProbeResult>) -> bool {
        Self::should_present(catalog, results, false)
    }

    /// Every required item that is missing, and Claude Code when no agent CLI is there at all.
    pub fn preselection(catalog: &SetupCatalog, results: &HashMap<String, SetupProbeResult>) -> HashSet<String> {
        let mut picked = HashSet::new();
        for item in &catalog.items {
            if item.required
                && results.get(&item.id).map(|r| r.state) == Some(SetupProbeState::Missing)
                && item.entry(SetupPlatform::Windows).is_some_and(|entry| entry.install.runs_in_terminal())
            {
                picked.insert(item.id.clone());
            }
        }
        let agents: Vec<&SetupItem> =
            catalog.items.iter().filter(|item| item.group == SetupGroup::Agent && item.entry(SetupPlatform::Windows).is_some()).collect();
        if !agents.iter().any(|item| results.get(&item.id).is_some_and(SetupProbeResult::is_installed))
            && let Some(first) = agents.first()
            && results.get(&first.id).map(|r| r.state) == Some(SetupProbeState::Missing)
        {
            picked.insert(first.id.clone());
        }
        picked
    }

    /// "다시 보지 않기": a DWORD `setup.neverShow` under `HKCU\Software\MightyCode` (the Mac's defaults key), read through the
    /// platform's `PrefsStore`. Anything but a non-zero integer, or a read the OS refused, is off.
    pub fn never_show(prefs: &dyn PrefsStore) -> bool {
        matches!(prefs.get(Self::NEVER_SHOW_VALUE), Ok(Some(PrefValue::Int(value))) if value != 0)
    }

    /// Stores never-show (DWORD 1). This changes a value the installed C# app shares: the host calls it only with
    /// `--allow-state-write` (W6 defaults), and never in a smoke.
    pub fn set_never_show(prefs: &dyn PrefsStore) -> mighty_platform::Result<()> {
        prefs.set(Self::NEVER_SHOW_VALUE, PrefValue::Int(1))
    }
}

/// C# `SetupRuntimeSync`: whether the app's runtime disagrees with what the probe just saw, so the runtime (and every pane blocked
/// for a missing CLI) must be read again.
pub struct SetupRuntimeSync;

impl SetupRuntimeSync {
    pub fn is_stale(runtime: &[ProviderRuntime], catalog: &SetupCatalog, results: &HashMap<String, SetupProbeResult>) -> bool {
        for item in &catalog.items {
            let (Some(provider), Some(result)) = (item.provider(), results.get(&item.id)) else { continue };
            let known = runtime.iter().find(|p| p.id == provider).and_then(|p| p.version.as_deref());
            if result.shown_version() != known {
                return true;
            }
        }
        false
    }
}
