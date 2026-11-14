//! C# `ClaudePluginReader` (and its half of PluginAutoUpdates.cs): the workspace's Claude plugins through the installed Claude CLI's
//! own plugin subcommands and nothing else —
//!
//! ```text
//! claude --version
//! claude plugin list --json --available
//! claude plugin marketplace list --json
//! ```
//!
//! all three in the workspace folder through the shared one-shot runner. None of them changes anything. The two mutations macOS has
//! (`plugin install <id> --scope <scope> --json`, `plugin marketplace update <name>`) assemble their arguments only from values a
//! fresh read just returned. A missing CLI, a CLI too old for the subcommands, a timeout and malformed or oversized output each
//! become a status with its macOS sentence, never an error the screen has to catch.
//!
//! The shared mechanics of both readers live here too ([`Core`]): one read at a time (a second request joins the running one), one
//! operation at a time, the workspace folder check, a run's failure as a status.

use std::collections::{BTreeMap, HashSet};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use mighty_core::model::Workspace;
use mighty_platform::BoxFuture;
use serde_json::Value;
use tokio::sync::watch;

use super::model::{ClaudePluginOperationResult, ClaudePluginSnapshot, PluginReader, status};
use super::operations::PluginAutoUpdateResult;
use super::strings::{auto_update as auto_strings, plugin as strings};
use super::support::{self, MESSAGE_CAP, VERSION_CAP};
use crate::paths;
use crate::runner::{CancellationToken, CliCall, CliRunResult, CliRunner, RunError};

/// A file or folder question the reader asks (`File.Exists`, `Directory.Exists`), so a check can answer it.
pub type Probe = Arc<dyn Fn(&str) -> bool + Send + Sync>;

/// macOS ClaudePluginService: reads 20 s, the version probe min(4, read), an operation 180 s.
pub const READ_TIMEOUT: Duration = Duration::from_secs(20);
pub const VERSION_TIMEOUT: Duration = Duration::from_secs(4);
pub const OPERATION_TIMEOUT: Duration = Duration::from_secs(180);
const INSTALL_SCOPES: [&str; 3] = ["local", "project", "user"];

/// Why a read or an operation stopped: the token (`OperationCanceledException`) or a status with its sentence (`PluginFailure`).
pub(crate) enum Stop {
    Cancelled,
    Failure { status: &'static str, detail: String, output: String },
}

impl Stop {
    pub(crate) fn failure(status: &'static str, detail: String) -> Stop {
        Stop::Failure { status, detail, output: String::new() }
    }

    pub(crate) fn snapshot(self) -> ClaudePluginSnapshot {
        match self {
            Stop::Cancelled => ClaudePluginSnapshot::of(status::CANCELLED, strings::detail_cancelled()),
            Stop::Failure { status, detail, output } => {
                ClaudePluginSnapshot { diagnostic_output: output, ..ClaudePluginSnapshot::of(status, detail) }
            }
        }
    }

    pub(crate) fn operation(self) -> ClaudePluginOperationResult {
        match self {
            Stop::Cancelled => ClaudePluginOperationResult::new(status::CANCELLED, strings::operation_cancelled()),
            Stop::Failure { status: kind, detail, output } => {
                ClaudePluginOperationResult::new(if kind == status::CANCELLED { status::CANCELLED } else { status::FAILED }, detail)
                    .with_output(output)
            }
        }
    }

    pub(crate) fn auto_update(self) -> PluginAutoUpdateResult {
        match self {
            Stop::Cancelled => PluginAutoUpdateResult::cancelled(),
            Stop::Failure { status: kind, detail, .. } => PluginAutoUpdateResult::new(
                if matches!(kind, status::MISSING | status::UNSUPPORTED) { "skipped" } else { "failed" },
                detail,
            ),
        }
    }
}

pub(crate) type Step<T> = Result<T, Stop>;

#[derive(Default)]
struct Gate {
    running: Option<watch::Receiver<Option<Arc<ClaudePluginSnapshot>>>>,
    closed: bool,
    operating: bool,
}

/// What both readers share: the runner, the environment every call gets, the two probes and the gate.
pub(crate) struct Core {
    pub(crate) runner: Arc<dyn CliRunner>,
    pub(crate) environment: BTreeMap<String, String>,
    /// Names the child must not inherit from the app's own environment.
    without: Vec<String>,
    is_executable: Probe,
    directory_exists: Probe,
    gate: Mutex<Gate>,
}

/// Holds the one-operation gate until the operation ends or is dropped.
pub(crate) struct Operating(Arc<Core>);

impl Drop for Operating {
    fn drop(&mut self) {
        self.0.lock().operating = false;
    }
}

impl Core {
    pub(crate) fn new(
        runner: Arc<dyn CliRunner>,
        environment: BTreeMap<String, String>,
        without: Vec<String>,
        is_executable: Probe,
        directory_exists: Probe,
    ) -> Core {
        Core { runner, environment, without, is_executable, directory_exists, gate: Mutex::new(Gate::default()) }
    }

    fn lock(&self) -> MutexGuard<'_, Gate> {
        self.gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    pub(crate) fn shutdown(&self) {
        self.lock().closed = true;
    }

    /// One read at a time: a second request while a read runs is answered from that read instead of starting a second CLI. The read
    /// starts at once on a task of its own (call it inside a Tokio runtime).
    pub(crate) fn snapshot(
        self: &Arc<Self>,
        read: impl FnOnce(Arc<Core>) -> BoxFuture<'static, ClaudePluginSnapshot>,
    ) -> BoxFuture<'static, Arc<ClaudePluginSnapshot>> {
        let mut receiver = {
            let mut gate = self.lock();
            if gate.closed {
                let closed = Arc::new(ClaudePluginSnapshot::of(status::CANCELLED, strings::detail_cancelled()));
                return Box::pin(async move { closed });
            }
            match &gate.running {
                Some(running) if running.borrow().is_none() => running.clone(),
                _ => {
                    let (sender, receiver) = watch::channel(None);
                    let work = read(self.clone());
                    tokio::spawn(async move {
                        let snapshot = work.await;
                        sender.send_replace(Some(Arc::new(snapshot)));
                    });
                    gate.running = Some(receiver.clone());
                    receiver
                }
            }
        };
        Box::pin(async move {
            match receiver.wait_for(Option::is_some).await {
                Ok(value) => value.as_ref().cloned().unwrap_or_default(),
                Err(_) => Arc::new(ClaudePluginSnapshot::of(status::FAILED, strings::detail_incomplete())),
            }
        })
    }

    /// One operation at a time: macOS refuses a second one with its sentence instead of queueing it.
    pub(crate) fn begin_operation(self: &Arc<Self>) -> Result<Operating, ClaudePluginOperationResult> {
        let mut gate = self.lock();
        if gate.closed {
            return Err(ClaudePluginOperationResult::new(status::CANCELLED, strings::operation_cancelled()));
        }
        if gate.operating {
            return Err(ClaudePluginOperationResult::new(status::BUSY, strings::operation_busy()));
        }
        gate.operating = true;
        Ok(Operating(self.clone()))
    }

    /// The automatic update's admission: never beside an operation or a running read.
    pub(crate) fn begin_auto_update(self: &Arc<Self>, cancel: &CancellationToken) -> Result<Operating, PluginAutoUpdateResult> {
        let mut gate = self.lock();
        if gate.closed || cancel.is_cancelled() {
            return Err(PluginAutoUpdateResult::cancelled());
        }
        if gate.operating || gate.running.as_ref().is_some_and(|running| running.borrow().is_none()) {
            return Err(PluginAutoUpdateResult::busy());
        }
        gate.operating = true;
        Ok(Operating(self.clone()))
    }

    /// The workspace's folder: rooted, at most 16 KiB, existing; its full path without a trailing separator.
    pub(crate) fn local_directory(&self, workspace_path: &str) -> Step<String> {
        let invalid = || Stop::failure(status::FAILED, strings::detail_invalid_workspace());
        if workspace_path.is_empty()
            || workspace_path.contains('\0')
            || workspace_path.len() > support::PATH_CAP
            || !paths::rooted(workspace_path)
        {
            return Err(invalid());
        }
        let full = paths::full_path(workspace_path).ok_or_else(invalid)?.to_string_lossy().into_owned();
        if !(self.directory_exists)(&full) {
            return Err(Stop::failure(status::FAILED, strings::detail_missing_workspace()));
        }
        Ok(full)
    }

    pub(crate) fn call(&self, executable: &str, arguments: &[&str], cwd: &str, timeout: Duration) -> CliCall {
        CliCall::new(executable, arguments.iter().copied(), timeout)
            .environment(Some(self.environment.clone()))
            .without(self.without.iter().cloned())
            .working_directory(Some(cwd.to_string()))
    }

    /// A read subcommand: a runner error is incomplete, a timeout is a slow CLI (never a short list), a bad exit `failure_detail`.
    pub(crate) async fn read(
        &self,
        executable: &str,
        arguments: &[&str],
        cwd: &str,
        failure_detail: String,
        cancel: &CancellationToken,
    ) -> Step<CliRunResult> {
        let result = match self.runner.run(self.call(executable, arguments, cwd, READ_TIMEOUT), cancel.clone()).await {
            Ok(result) => result,
            Err(RunError::Cancelled) => return Err(Stop::Cancelled),
            Err(RunError::Failed(message)) => {
                return Err(Stop::Failure {
                    status: status::FAILED,
                    detail: strings::detail_incomplete(),
                    output: support::display(Some(&message), MESSAGE_CAP),
                });
            }
        };
        if result.timed_out {
            return Err(Stop::Failure { status: status::FAILED, detail: strings::detail_incomplete(), output: support::output(&result) });
        }
        if result.exit_code != 0 {
            return Err(Stop::Failure { status: status::FAILED, detail: failure_detail, output: support::output(&result) });
        }
        Ok(result)
    }

    /// An operation's run: a runner error is incomplete with its message.
    pub(crate) async fn operate(&self, call: CliCall, cancel: &CancellationToken) -> Result<CliRunResult, ClaudePluginOperationResult> {
        match self.runner.run(call, cancel.clone()).await {
            Ok(result) => Ok(result),
            Err(RunError::Cancelled) => Err(Stop::Cancelled.operation()),
            Err(RunError::Failed(message)) => Err(ClaudePluginOperationResult::new(status::FAILED, strings::detail_incomplete())
                .with_output(support::display(Some(&message), MESSAGE_CAP))),
        }
    }

    /// The first `name` on PATH that answers `--version` with a version (exit 0, in time): `(path, version)`, or why none did.
    pub(crate) async fn command(
        &self,
        name: &str,
        cwd: &str,
        cancel: &CancellationToken,
        accept: impl Fn(&str) -> Step<()>,
        missing: (String, String),
    ) -> Step<(String, String)> {
        let mut present = false;
        for candidate in paths::path_candidates(&self.environment, name) {
            if !(self.is_executable)(&candidate) {
                continue;
            }
            present = true;
            let result = match self.runner.run(self.call(&candidate, &["--version"], cwd, VERSION_TIMEOUT), cancel.clone()).await {
                Ok(result) => result,
                Err(RunError::Cancelled) => return Err(Stop::Cancelled),
                Err(RunError::Failed(_)) => continue,
            };
            if result.timed_out || result.exit_code != 0 {
                continue;
            }
            let version = support::display(Some(&result.output), VERSION_CAP).trim().to_string();
            if version.is_empty() {
                continue;
            }
            accept(&version)?;
            return Ok((candidate, version));
        }
        let (unknown_version, missing_cli) = missing;
        Err(if present { Stop::failure(status::FAILED, unknown_version) } else { Stop::failure(status::MISSING, missing_cli) })
    }
}

pub(crate) fn cancelled(cancel: &CancellationToken) -> Step<()> {
    if cancel.is_cancelled() { Err(Stop::Cancelled) } else { Ok(()) }
}

pub(crate) fn default_probes() -> (Probe, Probe) {
    (Arc::new(|path: &str| std::path::Path::new(path).is_file()), Arc::new(|path: &str| std::path::Path::new(path).is_dir()))
}

/// C# `ClaudePluginReader`.
#[derive(Clone)]
pub struct ClaudePluginReader {
    core: Arc<Core>,
}

impl ClaudePluginReader {
    /// `environment` is the CLI environment (C#: the process's variables) the PATH is read from and every call gets, with the
    /// macOS switches laid over it ([`ClaudePluginReader::environment`]). The runner must keep at least
    /// [`support::MAXIMUM_LISTING_BYTES`] of output: anything larger is refused by the parse rather than read as a short list.
    pub fn new(runner: Arc<dyn CliRunner>, environment: BTreeMap<String, String>) -> ClaudePluginReader {
        let (is_executable, directory_exists) = default_probes();
        Self::with_probes(runner, environment, is_executable, directory_exists)
    }

    pub fn with_probes(
        runner: Arc<dyn CliRunner>,
        environment: BTreeMap<String, String>,
        is_executable: Probe,
        directory_exists: Probe,
    ) -> ClaudePluginReader {
        let core =
            Core::new(runner, Self::environment(environment), vec!["FORCE_AUTOUPDATE_PLUGINS".into()], is_executable, directory_exists);
        ClaudePluginReader { core: Arc::new(core) }
    }

    /// The switches macOS sets: no auto-update, no telemetry, no background work, no implicit marketplace install, no git credential
    /// prompt. A caller's `FORCE_AUTOUPDATE_PLUGINS` is dropped, so reading the list can never update a plugin behind the user's back.
    pub fn environment(mut supplied: BTreeMap<String, String>) -> BTreeMap<String, String> {
        for (name, value) in [
            ("DISABLE_AUTOUPDATER", "1"),
            ("DISABLE_TELEMETRY", "1"),
            ("DISABLE_ERROR_REPORTING", "1"),
            ("CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL", "1"),
            ("CLAUDE_CODE_DISABLE_BACKGROUND_TASKS", "1"),
            ("CLAUDE_CODE_SKIP_PROMPT_HISTORY", "1"),
            ("GIT_TERMINAL_PROMPT", "0"),
        ] {
            paths::set_variable(&mut supplied, name, value);
        }
        paths::remove_variable(&mut supplied, "FORCE_AUTOUPDATE_PLUGINS");
        supplied
    }

    async fn command(core: &Core, cwd: &str, cancel: &CancellationToken) -> Step<(String, String)> {
        let accept = |version: &str| {
            if support::supported_version(version) {
                Ok(())
            } else {
                Err(Stop::failure(status::UNSUPPORTED, strings::detail_unsupported()))
            }
        };
        core.command("claude", cwd, cancel, accept, (strings::detail_unknown_version(), strings::detail_missing_cli())).await
    }

    /// The two read subcommands and the parse, shared by the list and by the re-read an operation does right before it assembles
    /// its arguments.
    async fn list(core: &Core, executable: &str, cwd: &str, version: &str, cancel: &CancellationToken) -> Step<ClaudePluginSnapshot> {
        let listing =
            core.read(executable, &["plugin", "list", "--json", "--available"], cwd, strings::detail_listing_failed(), cancel).await?;
        let markets =
            core.read(executable, &["plugin", "marketplace", "list", "--json"], cwd, strings::detail_marketplaces_failed(), cancel).await?;
        Ok(support::parse_snapshot(&listing.output, &markets.output, cwd, Some(version)))
    }

    async fn read(core: Arc<Core>, workspace_path: String, cancel: CancellationToken) -> ClaudePluginSnapshot {
        let read = async {
            let cwd = core.local_directory(&workspace_path)?;
            let (executable, version) = Self::command(&core, &cwd, &cancel).await?;
            Self::list(&core, &executable, &cwd, &version, &cancel).await
        };
        read.await.unwrap_or_else(Stop::snapshot)
    }

    /// An install (`plugin_id` and `scope`) or a marketplace refresh (`marketplace`), admitted already.
    async fn run_operation(
        core: Arc<Core>,
        _operating: Operating,
        target: Target,
        workspace_path: String,
        cancel: CancellationToken,
    ) -> ClaudePluginOperationResult {
        let work = async {
            let cwd = core.local_directory(&workspace_path)?;
            let (executable, version) = Self::command(&core, &cwd, &cancel).await?;
            // Re-read the registry and the cached catalog right before the mutation: only a value this snapshot carries becomes
            // an argument.
            let snapshot = Self::list(&core, &executable, &cwd, &version, &cancel).await?;
            if snapshot.status != status::READY {
                return Ok(ClaudePluginOperationResult::new(status::FAILED, snapshot.detail).with_output(snapshot.diagnostic_output));
            }
            cancelled(&cancel)?;
            let arguments: Vec<String> = match &target {
                Target::Install { plugin_id, scope } => {
                    let found = support::plugin_parts(Some(plugin_id)).is_some_and(|(_, marketplace)| {
                        snapshot.available.iter().any(|p| p.id == *plugin_id) && snapshot.marketplaces.iter().any(|m| m.name == marketplace)
                    });
                    if !found {
                        return Ok(ClaudePluginOperationResult::new(status::FAILED, strings::install_not_found()));
                    }
                    let already = snapshot.installed.iter().any(|p| {
                        p.plugin_id == *plugin_id
                            && p.scope == *scope
                            && (scope == "user" || p.project_path.as_deref().is_none_or(|path| support::applies(path, &cwd)))
                    });
                    if already {
                        return Ok(ClaudePluginOperationResult::new(status::SKIPPED, strings::install_skipped()));
                    }
                    // No --yes and no --accept-command: a marketplace-declared command keeps needing the CLI's own consent.
                    ["plugin", "install", plugin_id.as_str(), "--scope", scope.as_str(), "--json"].map(str::to_string).to_vec()
                }
                Target::Refresh { marketplace } => {
                    if !snapshot.marketplaces.iter().any(|m| m.name == *marketplace) {
                        return Ok(ClaudePluginOperationResult::new(status::FAILED, strings::marketplace_not_registered()));
                    }
                    ["plugin", "marketplace", "update", marketplace.as_str()].map(str::to_string).to_vec()
                }
            };
            let call = CliCall::new(&executable, arguments, OPERATION_TIMEOUT)
                .environment(Some(core.environment.clone()))
                .without(core.without.iter().cloned())
                .working_directory(Some(cwd.clone()));
            let result = match core.operate(call, &cancel).await {
                Ok(result) => result,
                Err(refused) => return Ok(refused),
            };
            cancelled(&cancel)?;
            let output = support::output(&result);
            if result.timed_out {
                return Ok(ClaudePluginOperationResult::new(status::FAILED, strings::detail_incomplete()).with_output(output));
            }
            Ok(match &target {
                Target::Install { plugin_id, scope } => installed(&result, plugin_id, scope, output),
                Target::Refresh { .. } if result.exit_code == 0 => {
                    ClaudePluginOperationResult::new(status::SUCCEEDED, strings::marketplace_refresh_succeeded()).with_output(output)
                }
                Target::Refresh { .. } => {
                    ClaudePluginOperationResult::new(status::FAILED, strings::marketplace_refresh_failed()).with_output(output)
                }
            })
        };
        work.await.unwrap_or_else(Stop::operation)
    }

    /// The automatic update (PluginAutoUpdates.cs): every user-scope plugin but mighty-bridge, once each, with
    /// `plugin update <id> --scope user --json` and no consent flag — a marketplace's changed command waits for the user's review in
    /// the plugin window — until `budget` is spent; the rest is postponed.
    pub async fn update_installed(&self, workspace: &Workspace, budget: Duration, cancel: &CancellationToken) -> PluginAutoUpdateResult {
        let _operating = match self.core.begin_auto_update(cancel) {
            Ok(operating) => operating,
            Err(refused) => return refused,
        };
        let core = &self.core;
        let work = async {
            let cwd = core.local_directory(workspace.path())?;
            let (executable, version) = Self::command(core, &cwd, cancel).await?;
            let snapshot = Self::list(core, &executable, &cwd, &version, cancel).await?;
            if snapshot.status != status::READY {
                let kind = if matches!(snapshot.status.as_str(), status::MISSING | status::UNSUPPORTED) { "skipped" } else { "failed" };
                return Ok(PluginAutoUpdateResult::new(kind, snapshot.detail));
            }
            let mut seen = HashSet::new();
            let targets: Vec<String> = snapshot
                .installed
                .iter()
                .filter(|p| p.scope == "user" && p.name != "mighty-bridge" && support::plugin_parts(Some(&p.plugin_id)).is_some())
                .filter(|p| seen.insert(p.plugin_id.clone()))
                .map(|p| p.plugin_id.clone())
                .collect();
            let (mut updated, mut approval, mut failed, mut postponed) = (Vec::new(), Vec::new(), Vec::new(), 0);
            let started = Instant::now();
            for plugin in &targets {
                cancelled(cancel)?;
                if started.elapsed() >= budget {
                    postponed += 1;
                    continue;
                }
                let call = core.call(&executable, &["plugin", "update", plugin, "--scope", "user", "--json"], &cwd, OPERATION_TIMEOUT);
                let result = match core.runner.run(call, cancel.clone()).await {
                    Ok(result) => result,
                    Err(RunError::Cancelled) => return Err(Stop::Cancelled),
                    Err(RunError::Failed(_)) => return Ok(PluginAutoUpdateResult::failure()),
                };
                cancelled(cancel)?;
                match PluginAutoUpdateResult::claude_outcome(&result) {
                    "updated" => updated.push(plugin.clone()),
                    "needsApproval" => approval.push(plugin.clone()),
                    _ => failed.push(plugin.clone()),
                }
            }
            let mut details = vec![if targets.is_empty() {
                auto_strings::none()
            } else {
                auto_strings::count("pluginAutoUpdate.checkedTemplate", targets.len())
            }];
            if !approval.is_empty() {
                details.push(auto_strings::names("pluginAutoUpdate.needsApprovalTemplate", &approval));
            }
            if !failed.is_empty() {
                details.push(auto_strings::names("pluginAutoUpdate.failedTemplate", &failed));
            }
            if postponed > 0 {
                details.push(auto_strings::count("pluginAutoUpdate.postponedTemplate", postponed));
            }
            Ok(PluginAutoUpdateResult {
                status: if failed.is_empty() { "succeeded" } else { "failed" }.into(),
                detail: details.join(" "),
                updated: Some(updated),
                needs_approval: Some(approval),
                failed: Some(failed),
            })
        };
        work.await.unwrap_or_else(Stop::auto_update)
    }
}

enum Target {
    Install { plugin_id: String, scope: String },
    Refresh { marketplace: String },
}

/// The CLI may print a marketplace-declared command before its own result, so only the last nonempty stdout line is read as JSON.
fn installed(result: &CliRunResult, plugin_id: &str, scope: &str, output: String) -> ClaudePluginOperationResult {
    let line = result.output.split('\n').map(str::trim).rfind(|l| !l.is_empty()).unwrap_or("");
    let unconfirmed = || ClaudePluginOperationResult::new(status::FAILED, strings::install_unconfirmed()).with_output(output.clone());
    let Ok(Value::Object(root)) = serde_json::from_str::<Value>(line) else { return unconfirmed() };
    let text = |name: &str| root.get(name).and_then(Value::as_str);
    if text("command") != Some("install") || !matches!(text("outcome"), Some("ok" | "failed")) {
        return unconfirmed();
    }
    if root.contains_key("shownCommand") {
        return ClaudePluginOperationResult::new(status::FAILED, strings::install_command_required()).with_output(output);
    }
    let claimed_id = if root.contains_key("pluginId") { text("pluginId") } else { Some(plugin_id) };
    let claimed_scope = if root.contains_key("scope") { text("scope") } else { Some(scope) };
    if result.exit_code != 0 || text("outcome") != Some("ok") || claimed_id != Some(plugin_id) || claimed_scope != Some(scope) {
        return ClaudePluginOperationResult::new(status::FAILED, strings::install_failed()).with_output(output);
    }
    ClaudePluginOperationResult::new(status::SUCCEEDED, strings::install_succeeded()).with_output(output)
}

fn ready(result: ClaudePluginOperationResult) -> BoxFuture<'static, ClaudePluginOperationResult> {
    Box::pin(async move { result })
}

impl PluginReader for ClaudePluginReader {
    fn snapshot(&self, workspace: &Workspace, cancel: CancellationToken) -> BoxFuture<'static, Arc<ClaudePluginSnapshot>> {
        let path = workspace.path().to_string();
        self.core.snapshot(move |core| Box::pin(Self::read(core, path, cancel)))
    }

    fn shutdown(&self) {
        self.core.shutdown();
    }

    /// `plugin install <id> --scope <scope> --json`, for one plugin the catalog just offered, in one of the three macOS scopes.
    fn install(
        &self,
        plugin_id: &str,
        scope: &str,
        workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult> {
        if support::plugin_parts(Some(plugin_id)).is_none() || !INSTALL_SCOPES.contains(&scope) {
            return ready(ClaudePluginOperationResult::new(status::FAILED, strings::install_bad_id_or_scope()));
        }
        let operating = match self.core.begin_operation() {
            Ok(operating) => operating,
            Err(refused) => return ready(refused),
        };
        let target = Target::Install { plugin_id: plugin_id.into(), scope: scope.into() };
        Box::pin(Self::run_operation(self.core.clone(), operating, target, workspace.path().to_string(), cancel))
    }

    /// `plugin marketplace update <name>`, for one registered marketplace.
    fn refresh_marketplace(
        &self,
        marketplace: &str,
        workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult> {
        if !support::identifier(Some(marketplace)) {
            return ready(ClaudePluginOperationResult::new(status::FAILED, strings::marketplace_bad_name()));
        }
        let operating = match self.core.begin_operation() {
            Ok(operating) => operating,
            Err(refused) => return ready(refused),
        };
        let target = Target::Refresh { marketplace: marketplace.into() };
        Box::pin(Self::run_operation(self.core.clone(), operating, target, workspace.path().to_string(), cancel))
    }
}
