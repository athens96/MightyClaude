//! The setup screen's state and decisions as MainWindow.Setup.cs keeps them (필요한 도구, its window and Settings › 구성 요소'
//! list): the catalog read once, the probe results, the picks, the one install running, the probe cadence, the install's start
//! and watch, and what the frame shows (`setupNeeded`, the status bar's badge). The host draws it (phase 2) and runs its timers;
//! everything that touches the machine goes through ports: the probes through [`ProbeContext`] (`ShellEnvironment`, the platform's
//! finder, `ProcessHost`), the folder and markers through `SetupFiles`, the install window through W6-A's `TerminalLauncher`.
//!
//! An install changes the machine: [`Gate`] says whether this run of the app may start one (W6 defaults: only with
//! `--allow-state-write` until W9, never in a smoke).

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use mighty_platform::console::{TerminalLaunch, TerminalLauncher};
use mighty_platform::{ProcessHost, SetupFiles, ShellEnvironment};
use mighty_run::model::ProviderRuntime;
use mighty_run::strings;

use crate::setup::catalog::{SetupCatalog, SetupItem, SetupPlatform};
use crate::setup::files;
use crate::setup::plan;
use crate::setup::probe::{Finder, SetupFirstRun, SetupProbeResult, SetupProber, SetupRowState, SetupRows, SetupRuntimeSync};
use crate::setup::script::{self, SetupScriptLabels, SetupScriptStep};
use crate::setup::windows::set_variable;

/// While an install runs, the run's own items are probed every 5 s; otherwise everything every 30 s.
pub const RUN_PROBE_INTERVAL: Duration = Duration::from_secs(5);
pub const IDLE_PROBE_INTERVAL: Duration = Duration::from_secs(30);
/// How often the watcher looks at the run's markers.
pub const WATCH_INTERVAL: Duration = Duration::from_secs(2);
/// The script has not said it started this long after the window opened: the screen says to look at the terminal.
pub const START_DEADLINE: Duration = Duration::from_secs(20);
/// The screen's size in points: the Mac's 600-wide sheet, tall enough for most of the list.
pub const WINDOW_WIDTH: f64 = 620.0;
pub const WINDOW_HEIGHT: f64 = 700.0;

/// C# `WindowsCommand.NoCurrentDirectorySearch`: npx and claude are .cmd shims, and their bare node is never looked for in the
/// folder a step runs in.
pub const NO_CURRENT_DIRECTORY_SEARCH: &str = "NoDefaultCurrentDirectoryInExePath";

/// Whether this run of the app may change the machine (the host's `--allow-state-write` and smoke flags).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Gate {
    /// `--allow-state-write`: installs run, never-show is stored.
    Allowed,
    /// A smoke run: nothing on the machine is probed without fixed results, and nothing is installed (C#'s `SmokeTest` branches).
    Smoke,
    /// The default until W9: the screen probes and shows, but starts no install and stores nothing outside the app.
    ReadOnly,
}

/// The install the screen started: one script in one window.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupRun {
    pub items: Vec<String>,
    pub started: PathBuf,
    pub done: PathBuf,
    pub opened: Instant,
    /// When the watcher last probed the run's items.
    pub probed: Instant,
    pub finished: bool,
    /// The script has not said it started 20 s after the window opened.
    pub not_started: bool,
}

/// What a refresh asked of the host: nothing, the fixed results (a smoke's), or a probe (with PATH read again when forced).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Refresh {
    Nothing,
    /// A forced refresh asked for while a probe runs: it runs when that one ends.
    Queued,
    Fixed(HashMap<String, SetupProbeResult>),
    Probe {
        force: bool,
        only: Option<HashSet<String>>,
    },
}

/// What a finished refresh asks of the host next.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Refreshed {
    /// An agent CLI appeared or changed: read the runtime and the accounts again (C# `RefreshRuntime`, `accountsCoordinator.RefreshAsync`).
    pub runtime_stale: bool,
    /// A forced refresh was queued meanwhile: run it now.
    pub again: bool,
    /// The welcome's button changed while no workspace is open: draw the frame again.
    pub attention_changed: bool,
}

/// What starting an install did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InstallStart {
    Started,
    /// Nothing happened: an install runs, a smoke, no steps.
    Ignored,
    /// The app may not change the machine in this run ([`Gate::ReadOnly`]).
    NotAllowed,
    /// Refused with the screen's message set (elevated, the script or the window could not be made).
    Refused,
}

/// What a watcher tick found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WatchTick {
    /// No run to watch.
    Idle,
    /// The script ended (its end marker, or its PowerShell gone): call [`SetupScreen::finish_run`].
    Ended,
    /// Still running; probe these items when given (every 5 s); `redraw` when the not-started hint changed.
    Running { probe: Option<HashSet<String>>, redraw: bool },
}

/// The confirmation's content: the lines that run first, then every step's command exactly as the script has it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Confirmation {
    pub preamble: Vec<String>,
    pub steps: Vec<SetupScriptStep>,
}

/// How the screen probes this PC: the CLI environment (read again from the registry when forced), the platform's finder, and
/// `--version` through `ProcessHost` in a neutral folder (the system folder).
#[derive(Clone)]
pub struct ProbeContext {
    pub environment: Arc<dyn ShellEnvironment>,
    pub finder: Finder,
    pub processes: Arc<dyn ProcessHost>,
    pub neutral: PathBuf,
}

impl ProbeContext {
    /// C#: `force ? await CliEnvironment.RefreshAsync(true) : CliEnvironment.Current()`, then `new SetupProber(environment)`.
    pub async fn prober(&self, force: bool) -> SetupProber {
        let environment = if force { self.environment.resolve(None, true).await.values } else { self.environment.current().values };
        SetupProber::with_processes(environment, self.finder.clone(), self.processes.clone(), self.neutral.clone())
    }
}

/// The screen's state (C# MainWindow.Setup.cs's fields).
#[derive(Debug, Default)]
pub struct SetupScreen {
    catalog: Option<Arc<SetupCatalog>>,
    pub results: HashMap<String, SetupProbeResult>,
    pub selection: HashSet<String>,
    /// The GUI smoke's fixed probe results: nothing on the machine is probed while they are set.
    pub fixed_results: Option<HashMap<String, SetupProbeResult>>,
    pub probing: bool,
    force_queued: bool,
    pub did_preselect: bool,
    /// An install ended behind another window: the status bar's badge says so until the screen opens.
    pub result_waiting: bool,
    needs_attention_shown: bool,
    pub message: Option<String>,
    pub run: Option<SetupRun>,
    pub focus: Option<String>,
    /// How many terminal windows the screen opened: the smoke proves a cancelled confirmation opened none.
    pub terminals_opened: usize,
    pub probed_at: Option<Instant>,
}

impl SetupScreen {
    pub fn new() -> SetupScreen {
        SetupScreen::default()
    }

    /// The catalog, read once. A broken one is said, not hidden.
    pub fn load_catalog(&mut self) -> Option<Arc<SetupCatalog>> {
        if let Some(catalog) = &self.catalog {
            return Some(catalog.clone());
        }
        match SetupCatalog::bundled() {
            Ok(catalog) => {
                self.catalog = Some(catalog.clone());
                Some(catalog)
            }
            Err(error) => {
                self.message = Some(strings::get_with("setup.error.catalog", &[("error", &error.0)]));
                None
            }
        }
    }

    pub fn catalog(&self) -> Option<&SetupCatalog> {
        self.catalog.as_deref()
    }

    pub fn running(&self) -> bool {
        self.run.as_ref().is_some_and(|run| !run.finished)
    }

    /// The items with a Windows entry, in the catalog's order.
    pub fn items(&self) -> Vec<&SetupItem> {
        self.catalog
            .as_deref()
            .map(|c| c.items.iter().filter(|item| item.entry(SetupPlatform::Windows).is_some()).collect())
            .unwrap_or_default()
    }

    pub fn state_of(&self, item: &SetupItem) -> SetupRowState {
        let in_run = self.run.as_ref().is_some_and(|run| run.items.contains(&item.id));
        let finished = self.run.as_ref().is_none_or(|run| run.finished);
        SetupRows::derive(self.results.get(&item.id), in_run, finished)
    }

    /// The selection plus the missing prerequisites it needs, in install order.
    pub fn plan_ids(&self) -> Vec<String> {
        match self.catalog.as_deref() {
            Some(catalog) => {
                let results = &self.results;
                plan::order(&self.selection, catalog, SetupPlatform::Windows, &|id| {
                    results.get(id).is_some_and(SetupProbeResult::is_installed)
                })
            }
            None => vec![],
        }
    }

    /// The plan's items that were not picked themselves (the rows say "added for the selection").
    pub fn added(&self) -> HashSet<String> {
        self.plan_ids().into_iter().filter(|id| !self.selection.contains(id)).collect()
    }

    pub fn toggle(&mut self, id: &str) {
        if self.running() {
            return;
        }
        self.did_preselect = true;
        if !self.selection.insert(id.to_string()) {
            self.selection.remove(id);
        }
    }

    /// C# `OpenSetup(focus)`: the badge goes, `focus` is picked when it may be. False when there is nothing to show.
    pub fn open(&mut self, focus: Option<&str>) -> bool {
        if self.load_catalog().is_none() && self.message.is_none() {
            return false;
        }
        self.result_waiting = false;
        self.focus = focus.map(str::to_string);
        if let Some(focus) = focus
            && let Some(item) = self.catalog.as_deref().and_then(|c| c.item(focus))
            && SetupRows::is_selectable(self.state_of(item), item)
        {
            self.selection.insert(focus.to_string());
        }
        true
    }

    /// The screen closed: never-show applied when ticked and allowed (the host stores it, [`SetupFirstRun::set_never_show`]), the
    /// focus forgotten.
    pub fn closed(&mut self) {
        self.focus = None;
    }

    /// The first time the list shows with results: Git when it is missing, and Claude Code when no agent CLI is there.
    pub fn preselect_once(&mut self) {
        if self.did_preselect || self.results.is_empty() {
            return;
        }
        let Some(catalog) = self.catalog.clone() else { return };
        self.did_preselect = true;
        self.selection.extend(SetupFirstRun::preselection(&catalog, &self.results));
    }

    /// The welcome screen's button shows while this holds (the frame's `setupNeeded`).
    pub fn needs_attention(&self) -> bool {
        self.catalog.as_deref().is_some_and(|catalog| SetupFirstRun::needs_attention(catalog, &self.results))
    }

    /// The frame's flags: `(setup badge, setupNeeded)`.
    pub fn shell_flags(&self) -> (bool, bool) {
        (self.result_waiting, self.needs_attention())
    }

    /// C# `RefreshSetup`'s start: what to probe, if anything. `force` reads PATH again; `only` limits the probe to a run's items.
    pub fn begin_refresh(&mut self, force: bool, only: Option<HashSet<String>>, gate: Gate) -> Refresh {
        if self.load_catalog().is_none() {
            return Refresh::Nothing;
        }
        // A smoke run probes nothing of the machine it runs on (다시 확인 included): only its own fixed results.
        if gate == Gate::Smoke && self.fixed_results.is_none() {
            return Refresh::Nothing;
        }
        if self.probing {
            if force {
                self.force_queued = true;
                return Refresh::Queued;
            }
            return Refresh::Nothing;
        }
        self.probing = true;
        match &self.fixed_results {
            Some(fixed) => Refresh::Fixed(fixed.clone()),
            None => Refresh::Probe { force, only },
        }
    }

    /// C# `RefreshSetup`'s end: the results kept, what is now there unpicked, and whether the runtime must be read again.
    /// `workspaces_open`: the frame has a workspace (the welcome's button only matters without one).
    pub fn finish_refresh(
        &mut self,
        results: impl IntoIterator<Item = (String, SetupProbeResult)>,
        runtime: Option<&[ProviderRuntime]>,
        workspaces_open: bool,
        now: Instant,
    ) -> Refreshed {
        self.probing = false;
        self.probed_at = Some(now);
        for (id, result) in results {
            self.results.insert(id, result);
        }
        let results = &self.results;
        self.selection.retain(|id| !results.get(id).is_some_and(SetupProbeResult::is_installed));
        let mut done = Refreshed::default();
        if self.fixed_results.is_none()
            && let (Some(runtime), Some(catalog)) = (runtime, self.catalog.as_deref())
        {
            done.runtime_stale = SetupRuntimeSync::is_stale(runtime, catalog, &self.results);
        }
        let attention = self.needs_attention();
        if attention != self.needs_attention_shown && !workspaces_open {
            done.attention_changed = true;
        }
        self.needs_attention_shown = attention;
        if self.force_queued {
            self.force_queued = false;
            done.again = true;
        }
        done
    }

    /// A probe that failed ends the refresh with nothing read (C#'s `finally`).
    pub fn abort_refresh(&mut self, now: Instant) {
        self.probing = false;
        self.probed_at = Some(now);
    }

    /// The whole refresh with the probe run here: `begin_refresh`, the probe (or the fixed results), `finish_refresh`. A queued forced
    /// refresh is the caller's to run when `again` comes back.
    pub async fn refresh(
        screen: &std::sync::Mutex<SetupScreen>,
        context: &ProbeContext,
        force: bool,
        only: Option<HashSet<String>>,
        gate: Gate,
        runtime: Option<&[ProviderRuntime]>,
        workspaces_open: bool,
    ) -> Refreshed {
        let step = lock(screen).begin_refresh(force, only, gate);
        let (results, catalog) = match step {
            Refresh::Nothing | Refresh::Queued => return Refreshed::default(),
            Refresh::Fixed(fixed) => (fixed.into_iter().collect::<Vec<_>>(), None),
            Refresh::Probe { force, only } => (vec![], Some((force, only))),
        };
        let results = match catalog {
            None => results,
            Some((force, only)) => {
                let Some(catalog) = lock(screen).catalog.clone() else { return Refreshed::default() };
                let prober = context.prober(force).await;
                prober.probe_all(&catalog, only.as_ref()).await.into_iter().collect()
            }
        };
        lock(screen).finish_refresh(results, runtime, workspaces_open, Instant::now())
    }

    /// The timed probe while a list shows: due when nothing runs or probes, the last probe is 30 s old and something is still
    /// missing; never in a smoke.
    pub fn poll_due(&self, now: Instant, gate: Gate) -> bool {
        if self.running() || self.probing || gate == Gate::Smoke {
            return false;
        }
        if self.probed_at.is_some_and(|at| now.duration_since(at) < IDLE_PROBE_INTERVAL) {
            return false;
        }
        self.items().iter().any(|item| !self.results.get(&item.id).is_some_and(SetupProbeResult::is_installed))
    }

    /// The one confirmation before anything runs: the lines that run first (the CLI environment's), then every step.
    pub fn confirmation(&self, environment: &BTreeMap<String, String>) -> Option<Confirmation> {
        let catalog = self.catalog.as_deref()?;
        let steps = script::steps(&self.plan_ids(), catalog, &SetupCatalog::title);
        (!steps.is_empty()).then(|| Confirmation { preamble: script::preamble(environment), steps })
    }

    /// An app started as administrator installs nothing from this screen: every step would run elevated. Says so and refuses.
    pub fn refuse_when_elevated(&mut self, files: &dyn SetupFiles) -> bool {
        if !files.is_elevated() {
            return false;
        }
        self.message = Some(strings::get("setup.windows.error.elevated"));
        true
    }

    /// C# `RequestSetupInstall`'s gate before the confirmation: no install while one runs, none elevated, none without steps.
    pub fn may_confirm(&mut self, files: &dyn SetupFiles, environment: &BTreeMap<String, String>) -> Option<Confirmation> {
        if self.running() || self.refuse_when_elevated(files) {
            return None;
        }
        self.confirmation(environment)
    }

    /// C# `StartSetupInstall`: the script written into the app's own setup folder and run in a window of its own. The probe, not the
    /// script's exit code, decides what got installed; the screen stays open and follows along ([`SetupScreen::watch_tick`]).
    #[allow(clippy::too_many_arguments)]
    pub fn start_install(
        &mut self,
        steps: &[SetupScriptStep],
        gate: Gate,
        environment: &BTreeMap<String, String>,
        files: &dyn SetupFiles,
        terminals: &dyn TerminalLauncher,
        profile_directory: Option<&Path>,
        local_app_data: &Path,
        now: Instant,
    ) -> InstallStart {
        if self.running() || gate == Gate::Smoke || steps.is_empty() {
            return InstallStart::Ignored;
        }
        if gate == Gate::ReadOnly {
            return InstallStart::NotAllowed;
        }
        if self.refuse_when_elevated(files) {
            return InstallStart::Refused;
        }
        let folder = files::folder(profile_directory, local_app_data);
        let prepared = (|| -> Result<(PathBuf, (PathBuf, PathBuf)), String> {
            files::prepare_directory(files, &folder)?;
            // Earlier runs' scripts and markers are of no further use.
            files::remove_leftovers(files, &folder);
            let markers = files::markers(&folder);
            let text = script::render(
                steps,
                &SetupScriptLabels::localized(),
                environment,
                &markers.0.to_string_lossy(),
                &markers.1.to_string_lossy(),
            );
            let script = files::write_script(&text, &folder).map_err(|error| error.to_string())?;
            Ok((script, markers))
        })();
        let (script, markers) = match prepared {
            Ok(done) => done,
            Err(error) => {
                self.message = Some(strings::get_with("setup.error.script", &[("error", &error)]));
                return InstallStart::Refused;
            }
        };
        // The app wrote this command itself from its own catalog, after the user confirmed it.
        let argv = script::launch_arguments(&script.to_string_lossy(), files.system_folder().as_deref(), environment);
        let mut variables = environment.clone();
        set_variable(&mut variables, NO_CURRENT_DIRECTORY_SEARCH, "1");
        // The console window the script runs in outlives the app (C# `Process.Start` of `CliAccountTerminal.LaunchPlan`: Windows
        // Terminal by the full path found along the PATH, else the system's console host), the system's PowerShell by its own path.
        let launch = TerminalLaunch {
            resolved: argv.first().cloned(),
            argv,
            directory: Some(folder.clone()),
            title: None,
            environment: variables,
            prefer_terminal_app: true,
        };
        if let Err(error) = terminals.open(launch) {
            self.message = Some(strings::get_with("setup.windows.error.terminal", &[("error", &error.to_string())]));
            return InstallStart::Refused;
        }
        self.terminals_opened += 1;
        self.message = None;
        self.run = Some(SetupRun {
            items: steps.iter().map(|step| step.item_id.clone()).collect(),
            started: markers.0,
            done: markers.1,
            opened: now,
            probed: now,
            finished: false,
            not_started: false,
        });
        InstallStart::Started
    }

    /// One look of C# `WatchSetupRun` (every [`WATCH_INTERVAL`]): ended by its markers or its PowerShell's end; the not-started
    /// hint 20 s after the window opened while the start marker is missing; the run's items probed every 5 s.
    pub fn watch_tick(&mut self, now: Instant, files: &dyn SetupFiles) -> WatchTick {
        let Some(run) = self.run.as_mut().filter(|run| !run.finished) else { return WatchTick::Idle };
        if files::run_ended(files, &run.started, &run.done, None) {
            return WatchTick::Ended;
        }
        let stalled = !files::marker_exists(files, &run.started) && now.duration_since(run.opened) > START_DEADLINE;
        let redraw = run.not_started != stalled;
        run.not_started = stalled;
        let probe = (now.duration_since(run.probed) >= RUN_PROBE_INTERVAL).then(|| {
            run.probed = now;
            run.items.iter().cloned().collect()
        });
        WatchTick::Running { probe, redraw }
    }

    /// C# `FinishSetupRun`'s state change: the run is over (the host then probes everything again, forced, and either brings the
    /// screen back or sets the badge, [`SetupScreen::after_finish`]). False when there was no run to finish.
    pub fn finish_run(&mut self) -> bool {
        match self.run.as_mut() {
            Some(run) if !run.finished => {
                run.finished = true;
                run.not_started = false;
                true
            }
            _ => false,
        }
    }

    /// The hint's button: the script never started, so the run is closed by hand (C# `AbandonSetupRun`).
    pub fn abandon(&mut self) -> bool {
        self.finish_run()
    }

    /// After a finished run's probe: true when the screen should open again (the app is in front and no screen shows), else the
    /// status bar says the install is over.
    pub fn after_finish(&mut self, screen_open: bool, app_in_front: bool) -> bool {
        if screen_open {
            return false;
        }
        if app_in_front {
            return true;
        }
        self.result_waiting = true;
        false
    }

    /// C# `SetupItemForMissingCli`: the setup item for an agent pane's provider when its CLI is missing on this PC, so the blocked
    /// line can offer to install it. A remote pane's CLI is on its host; an old Claude is no missing CLI (it gets the update). `runtime`
    /// is each provider's runtime as last read (C#'s `runtime.Providers`), None before the first read.
    pub fn item_for_missing_cli(
        &mut self,
        kind: &str,
        provider: &str,
        remote: bool,
        runtime: Option<&[ProviderRuntime]>,
    ) -> Option<String> {
        let runtime = runtime?;
        if kind != "claude" || remote || runtime.iter().any(|p| p.id == provider && p.version.is_some()) {
            return None;
        }
        self.load_catalog()?.items.iter().find(|item| item.provider() == Some(provider)).map(|item| item.id.clone())
    }
}

fn lock(screen: &std::sync::Mutex<SetupScreen>) -> std::sync::MutexGuard<'_, SetupScreen> {
    screen.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// The GUI smoke's fixed probe results (C# MainWindow.SetupSmoke.cs): every Windows item missing, Claude Code out of date (2.0.0
/// below 2.1.271), Codex there.
pub fn smoke_fixed_results(catalog: &SetupCatalog) -> HashMap<String, SetupProbeResult> {
    let mut results: HashMap<String, SetupProbeResult> = catalog
        .items
        .iter()
        .filter(|item| item.entry(SetupPlatform::Windows).is_some())
        .map(|item| (item.id.clone(), SetupProbeResult::missing()))
        .collect();
    results.insert(
        "claude".into(),
        SetupProbeResult::new(crate::setup::probe::SetupProbeState::Outdated, Some("2.0.0 (Claude Code)"), Some("2.1.271")),
    );
    results.insert("codex".into(), SetupProbeResult::installed(Some("codex-cli 0.160.0")));
    results
}
